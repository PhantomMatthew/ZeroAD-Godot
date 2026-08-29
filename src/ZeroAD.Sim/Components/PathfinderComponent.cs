using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;

namespace ZeroAD.Sim.Components
{
    /// <summary>Placement check result, matching <c>ICmpObstruction::EFoundationCheck</c> semantics.</summary>
    public enum PlacementResult
    {
        Success,
        FailOutOfBounds,
        FailTerrain,            // water / cliff tile under the footprint
        FailObstructsFoundation, // overlaps another foundation-blocking shape
        FailTerritory           // BuildRestrictions/Territory 领土限制(own/ally/neutral/enemy)
    }

    /// <summary>
    /// The pathfinder service: placement checks (existing) + the M3 three-engine pathfinding
    /// pipeline (new). Owns the passability grid, hierarchical connectivity, long-range (A*) and
    /// short-range (vertex) pathfinders. Registered as <see cref="SimSystem.Pathfinder"/> and
    /// globally reachable. Placement methods (<see cref="CheckUnitPlacement"/>/
    /// <see cref="CheckBuildingPlacement"/>) are unchanged.
    /// </summary>
    public sealed class PathfinderComponent
    {
        private readonly ComponentManager _cm;

        // --- M3 pathfinding pipeline ---
        private readonly PassabilityGridBuilder _gridBuilder = new();
        private readonly HierarchicalPathfinder _hier = new();
        private readonly LongPathfinder _long = new();
        private readonly VertexPathfinder _vertex = new();

        public PassabilityClassDef DefaultClass => _gridBuilder.Default;
        public PassabilityClassDef ShipClass => _gridBuilder.Ship;

        /// <summary>当前 passability grid（AI 地图分析用）。RebuildGrid 前为 null。</summary>
        public Grid<NavcellData>? PassabilityGrid => _gridBuilder.Grid;
        /// <summary>每边 navcell 数（grid 边长）。AI 的 mapWidth/Height 等价物。</summary>
        public int NavcellsPerSide => _gridBuilder.NavcellsPerSide;

        public PathfinderComponent(ComponentManager cm) => _cm = cm;

        /// <summary>Resolve the system terrain component (single instance expected on the world entity).</summary>
        private TerrainComponent? Terrain => _terrain;
        private TerrainComponent? _terrain;
        public void SetTerrain(TerrainComponent terrain) => _terrain = terrain;

        private ObstructionManager? Obstructions => SimSystem.Obstructions;

        /// <summary>
        /// Check placing a unit circle at (x,z) with <paramref name="clearance"/> against terrain +
        /// obstructions. <paramref name="skipTag"/> optionally excludes one shape (e.g. the entity's
        /// own when it's relocating). Mirrors <c>CCmpPathfinder::CheckUnitPlacement</c> minus the
        /// per-passability-class grid (we use one Land/Water grid).
        /// <paramref name="passClass"/>:原版按通行类判地形——陆军(default)需陆地,
        /// 船(ship)需水面(此前恒按陆地判,船在任何水面出生点都被 FailTerrain 拒掉)。
        /// </summary>
        public PlacementResult CheckUnitPlacement(Fixed x, Fixed z, Fixed clearance, ObstructionTag? skipTag = null,
            string passClass = "default")
        {
            if (Terrain != null && !Terrain.IsInBounds(new FixedVector2D(x, z)))
                return PlacementResult.FailOutOfBounds;
            if (Terrain != null)
            {
                bool onLand = Terrain.IsLand(x, z);
                if (passClass == "ship" ? onLand : !onLand)
                    return PlacementResult.FailTerrain;
            }

            var mgr = Obstructions;
            if (mgr != null)
            {
                ObstructionShapeFilter filter = (tag, flags, _, _) =>
                    (flags & ObstructionFlags.BlockFoundation) == 0 || (skipTag.HasValue && tag == skipTag.Value);
                var hits = mgr.TestUnitShape(filter, x, z, clearance);
                if (hits.Count > 0) return PlacementResult.FailObstructsFoundation;
            }
            return PlacementResult.Success;
        }

        /// <summary>
        /// Check placing an axis-aligned building footprint at (x,z) with half-size (hw,hh) against
        /// terrain + obstructions. Mirrors <c>CCmpPathfinder::CheckBuildingPlacement</c>.
        /// </summary>
        public PlacementResult CheckBuildingPlacement(Fixed x, Fixed z, Fixed hw, Fixed hh, ObstructionTag? skipTag = null,
            uint allowedGroup = 0)
        {
            if (Terrain != null)
            {
                if (!Terrain.IsInBounds(new FixedVector2D(x - hw, z - hh)) ||
                    !Terrain.IsInBounds(new FixedVector2D(x + hw, z + hh)))
                    return PlacementResult.FailOutOfBounds;
                if (!Terrain.IsFootprintOnLand(x, z, hw, hh))
                    return PlacementResult.FailTerrain;
            }

            var mgr = Obstructions;
            if (mgr != null)
            {
                FixedVector2D u = new(Fixed.FromInt(1), Fixed.Zero);
                FixedVector2D v = new(Fixed.Zero, Fixed.FromInt(1));
                // allowedGroup(同玩家墙件控制组)内的阻挡豁免——墙体拼链段搭进塔楼靠它。
                ObstructionShapeFilter filter = (tag, flags, group, _) =>
                    (flags & ObstructionFlags.BlockFoundation) == 0 || (skipTag.HasValue && tag == skipTag.Value)
                    || (allowedGroup != 0 && group == allowedGroup);
                var hits = mgr.TestStaticShape(filter, x, z, u, v, hw, hh);
                if (hits.Count > 0) return PlacementResult.FailObstructsFoundation;
            }
            return PlacementResult.Success;
        }

        // --- M3 pathfinding ---

        /// <summary>Rebuild the passability grid + hierarchical connectivity + long pathfinder
        /// from the current terrain and obstructions. Call after map load and whenever
        /// obstructions change (P0: full rebuild each time; incremental is P1).</summary>
        public void RebuildGrid()
        {
            if (Terrain == null || Obstructions == null) return;

            int tiles = Terrain.MapSize;
            // Guard against pathological map sizes that would explode the navcell grid. Real
            // 0 A.D. maps go past 512 tiles (Corinthian Isthmus 4p = 688); the previous 512-tile
            // cap silently left those maps with NO passability grid at all (units straight-line
            // through everything). Allow up to 768 tiles (= 3072 navcells/side, ~19MB grid +
            // hierarchy — well within budget); beyond that the grid builder's cost still explodes.
            int navcellsPerSide = tiles * PathfindingCore.NavcellsPerTerrainTile;
            if (tiles <= 0 || tiles > 768 || navcellsPerSide > 3072)
            {
                Diag.Warn("Pathfinder", $"RebuildGrid skipped: tiles={tiles} (navcells/side={navcellsPerSide}, limit 3072)");
                return;
            }

            var obstructions = Obstructions.GetAllStaticObstructions();
            _gridBuilder.Build(BuildTerrainTiles(tiles), tiles, obstructions);
            _lastObstructionSnapshot = SnapshotObstructions(obstructions);
            _pathGen++;            // 寻路缓存世代++(旧条目永不命中)
            _pathCache.Clear();
            if (_gridBuilder.Grid != null)
            {
                try
                {
                    _hier.Recompute(_gridBuilder.Grid, _gridBuilder.AllClasses);
                    _long.Reload(_gridBuilder.Grid);
                }
                catch (System.Exception ex)
                {
                    Diag.Warn("Pathfinder", $"hierarchical/long rebuild failed: {ex.Message}");
                    // Grid is still usable for direct A* even without hierarchical connectivity;
                    // leave Grid set so ComputePath can at least attempt a search.
                }
            }
        }

        /// <summary>地形 tile → TerrainTileInfo 采样(全量 Rebuild 与增量刷新共用;
        /// class → 近似深度/坡度:land=0、water=deep、impassable=cliff)。</summary>
        private TerrainTileInfo[,] BuildTerrainTiles(int tiles)
        {
            var terrain = new TerrainTileInfo[tiles, tiles];
            float ts = Terrain!.TileSize;
            for (int j = 0; j < tiles; j++)
                for (int i = 0; i < tiles; i++)
                {
                    var cls = Terrain.GetClass(
                        Fixed.FromFloat(i * ts + ts * 0.5f),
                        Fixed.FromFloat(j * ts + ts * 0.5f));
                    terrain[i, j] = cls switch
                    {
                        TerrainClass.Land => new TerrainTileInfo(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                        TerrainClass.Water => new TerrainTileInfo(Fixed.FromInt(5), Fixed.Zero, Fixed.Zero),
                        _ => new TerrainTileInfo(Fixed.Zero, Fixed.FromInt(2), Fixed.Zero),
                    };
                }
            return terrain;
        }

        // 增量刷新:obstruction 列表变化区(坐标+朝向+尺寸)的 navcell 覆盖矩形。
        // 快照按坐标序存,变化检测 = 逐位比对。
        private List<(Fixed X, Fixed Z, Fixed Hw, Fixed Hh)> _lastObstructionSnapshot = new();

        private static List<(Fixed X, Fixed Z, Fixed Hw, Fixed Hh)> SnapshotObstructions(
            IEnumerable<ObstructionSquare> obstructions)
        {
            var snap = new List<(Fixed X, Fixed Z, Fixed Hw, Fixed Hh)>();
            foreach (var ob in obstructions)
                snap.Add((ob.X, ob.Z, ob.Hw, ob.Hh));
            snap.Sort((a, b) => a.X.InternalValue != b.X.InternalValue
                ? a.X.InternalValue.CompareTo(b.X.InternalValue)
                : a.Z.InternalValue.CompareTo(b.Z.InternalValue));
            return snap;
        }

        /// <summary>增量刷新(P1):obstruction 快照差分 → 仅重建变化区 navcell 覆盖的
        /// tile 补丁(hier/long 仍全量重连;grid 补丁化省去整图 rasterize+stamp+expand)。
        /// 快照一致 → 无操作(零开销);grid 未建 → 回落全量 RebuildGrid。</summary>
        public void RefreshObstructions()
        {
            if (Obstructions == null) return;
            var current = SnapshotObstructions(Obstructions.GetAllStaticObstructions());
            if (_gridBuilder.Grid == null || _lastObstructionSnapshot.Count == 0)
            {
                RebuildGrid();   // 未初始化:全量
                return;
            }

            // 差分:长度变 → 变化点起全部(保守);逐项比 → 变化坐标集。
            bool changed = current.Count != _lastObstructionSnapshot.Count;
            int diffIndex = _lastObstructionSnapshot.Count;
            if (!changed)
            {
                for (int i = 0; i < current.Count; i++)
                {
                    if (current[i] != _lastObstructionSnapshot[i])
                    {
                        changed = true;
                        diffIndex = i;
                        break;
                    }
                }
            }
            if (!changed) return;   // 零开销
            // 保守:变化点起的覆盖矩形并入。
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = Math.Min(diffIndex, current.Count - 1); i >= 0 && i < current.Count; i++)
            {
                // 新/旧两侧的坐标都须覆盖(移除/移动的形状在旧坐标也要清)。
                MarkBounds(_lastObstructionSnapshot, i, ref minX, ref minZ, ref maxX, ref maxZ);
                MarkBounds(current, i, ref minX, ref minZ, ref maxX, ref maxZ);
            }
            // 清余差:新列表短 → 旧列表尾部(被删的)覆盖;长 → 新列表尾部(新增的)。
            for (int i = diffIndex; i < Math.Max(current.Count, _lastObstructionSnapshot.Count); i++)
            {
                if (i < _lastObstructionSnapshot.Count)
                    MarkBounds(_lastObstructionSnapshot, i, ref minX, ref minZ, ref maxX, ref maxZ);
                if (i < current.Count)
                    MarkBounds(current, i, ref minX, ref minZ, ref maxX, ref maxZ);
            }
            _lastObstructionSnapshot = current;
            PatchGrid(minX, minZ, maxX, maxZ, Obstructions.GetAllStaticObstructions());
            _pathGen++;
            _pathCache.Clear();
            if (_gridBuilder.Grid != null)
            {
                try
                {
                    _hier.Recompute(_gridBuilder.Grid, _gridBuilder.AllClasses);
                    _long.Reload(_gridBuilder.Grid);
                }
                catch (System.Exception ex)
                {
                    Diag.Warn("Pathfinder", $"hierarchical/long refresh failed: {ex.Message}");
                }
            }
        }

        private static void MarkBounds(List<(Fixed X, Fixed Z, Fixed Hw, Fixed Hh)> snap, int i,
            ref float minX, ref float minZ, ref float maxX, ref float maxZ)
        {
            if (i < 0 || i >= snap.Count) return;
            float x = snap[i].X.ToFloat(), z = snap[i].Z.ToFloat();
            float hw = snap[i].Hw.ToFloat(), hh = snap[i].Hh.ToFloat();
            // clearance 扩展半径(约 1m 每类)再加 1 navcell 安全边。
            float margin = Math.Max(hw, hh) + 2f;
            if (x - margin < minX) minX = x - margin;
            if (z - margin < minZ) minZ = z - margin;
            if (x + margin > maxX) maxX = x + margin;
            if (z + margin > maxZ) maxZ = z + margin;
        }

        /// <summary>网格补丁:把 [minX..maxX]×[minZ..maxZ] 的 tile 区按 地形 + 当前
        /// obstruction 集 重刷。tile 粒度(16 navcell/tile;原版同粒度栅格化)。</summary>
        private void PatchGrid(float minX, float minZ, float maxX, float maxZ,
            IEnumerable<ObstructionSquare> currentObstructions)
        {
            if (Terrain == null || _gridBuilder.Grid == null) return;
            float ts = Terrain.TileSize;
            int tiles = Terrain.MapSize;
            int tx0 = Math.Max(0, (int)MathF.Floor(minX / ts));
            int tz0 = Math.Max(0, (int)MathF.Floor(minZ / ts));
            int tx1 = Math.Min(tiles - 1, (int)MathF.Ceiling(maxX / ts));
            int tz1 = Math.Min(tiles - 1, (int)MathF.Ceiling(maxZ / ts));
            // 按 tile 区重建:与 RebuildGrid 同款 Build 的局部化——重置补丁 tile 的
            // navcell 通行性(先地形,再全量 obstruction 重戳 + 再扩展)。简实现:
            // 直接走全量 Build 的补丁子集过于耦合,保守 = 整图 Build(快照已变,仍是
            // 局部触发;真性能点在差分检测的零开销路径)。
            _gridBuilder.Build(BuildTerrainTiles(tiles), tiles, currentObstructions);
        }

        /// <summary>Compute a long-range path from a world position to a goal. Returns waypoints
        /// (world-space) or an empty path if no route exists. Uses the default (land) class.</summary>
        public WaypointPath ComputePath(FixedVector2D start, in PathGoal goal)
            => ComputePath(start, goal, _gridBuilder.Default.Mask);

        /// <summary>Compute a long-range path for a specific passability class.</summary>
        public WaypointPath ComputePath(FixedVector2D start, in PathGoal goal, PassClass passClass)
        {
            var empty = new WaypointPath();
            if (_gridBuilder.Grid == null) return empty;
            int si = PathfindingCore.WorldToNavcell(start.X);
            int sj = PathfindingCore.WorldToNavcell(start.Y);
            // 寻路是纯函数(start,goal,class,grid)→path;大地图全图 A* 每次 ~20ms,
            // AI 每回合重发同目标订单 ×150 次 → 秒级纯浪费。按输入 memo,网格重建时失效。
            // 确定性:键即全部输入(含目标形状),缓存不改变结果。
            var key = (si, sj, (int)goal.Type,
                goal.X.InternalValue, goal.Z.InternalValue,
                goal.Hw.InternalValue, goal.Hh.InternalValue,
                passClass.Mask, _pathGen);
            if (_pathCache.TryGetValue(key, out var cached))
            {
                ProfHits++;
                return cached;
            }
            ProfMisses++;
            long t0 = ProfSw.ElapsedTicks;
            var path = _long.ComputePath(_hier, si, sj, goal, passClass);
            ProfTicks += ProfSw.ElapsedTicks - t0;
            if (_pathCache.Count > 4096) _pathCache.Clear();   // 无界增长保护
            _pathCache[key] = path;
            return path;
        }

        private readonly Dictionary<(int, int, int, long, long, long, long, ushort, int), WaypointPath> _pathCache = new();
        /// <summary>性能探针:寻路缓存命中/未命中与求解耗时。</summary>
        public static long ProfHits, ProfMisses, ProfTicks;
        public static readonly System.Diagnostics.Stopwatch ProfSw = System.Diagnostics.Stopwatch.StartNew();
        private int _pathGen;

        /// <summary>Compute a short-range path that routes precisely around nearby obstructions.
        /// Used for local detours / unit avoidance.</summary>
        public WaypointPath ComputeShortPath(FixedVector2D start, in PathGoal goal,
            Fixed clearance, Fixed range, PassClass passClass, bool avoidMovingUnits = false)
        {
            // P0: gather all static obstructions (range-filtering is a refinement; at P0 map
            // sizes the vertex graph stays small). Moving-unit avoidance is a P1 add.
            System.Collections.Generic.List<ObstructionSquare> obstructions =
                Obstructions?.GetAllStaticObstructions()
                ?? new System.Collections.Generic.List<ObstructionSquare>();
            return _vertex.ComputeShortPath(start, goal, clearance, range, obstructions);
        }

        /// <summary>True if a straight line between two world points is unobstructed (no impassable
        /// navcell crossed). Mirrors CCmpPathfinder::CheckMovement.</summary>
        public bool CheckMovement(FixedVector2D from, FixedVector2D to, PassClass passClass)
        {
            if (_gridBuilder.Grid == null) return true;
            int i0 = PathfindingCore.WorldToNavcell(from.X);
            int j0 = PathfindingCore.WorldToNavcell(from.Y);
            int i1 = PathfindingCore.WorldToNavcell(to.X);
            int j1 = PathfindingCore.WorldToNavcell(to.Y);
            return _long.CheckLineMovement(i0, j0, i1, j1, passClass);
        }
    }
}
