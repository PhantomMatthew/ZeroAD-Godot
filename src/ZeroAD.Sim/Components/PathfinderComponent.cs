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
        /// <summary>通行类注册表(pathfinder.xml 数据驱动;缺数据根用内建默认,
        /// 与上游逐值一致)。SetPassabilityConfig 由 SimBridge 在建世界时注入数据根路径。</summary>
        private PathfinderConfig _config = PathfinderConfig.Default();
        private PassabilityGridBuilder _gridBuilder = new();
        private readonly HierarchicalPathfinder _hier = new();
        private readonly LongPathfinder _long = new();
        private readonly VertexPathfinder _vertex = new();

        public PassabilityClassDef DefaultClass => _gridBuilder.Default;
        public PassabilityClassDef ShipClass => _gridBuilder.Ship;

        /// <summary>注入 pathfinder.xml 数据根(mods 目录);下次 RebuildGrid 生效。
        /// 原版 CCmpPathfinder 从 VFS 读 simulation/data/pathfinder.xml;此处走文件系统。</summary>
        public void SetPassabilityConfig(string? dataModsDir)
        {
            _config = PathfinderConfig.Load(dataModsDir);
            _gridBuilder = new PassabilityGridBuilder(_config);
        }

        /// <summary>世界坐标 → 陆地区域 id(分层寻路的 global region;0 = 未建网格/
        /// 不可达)。编队分簇/可达性分组用(原版 getLandAccess 的内核侧等价)。</summary>
        public uint GetLandRegion(Fixed x, Fixed z)
        {
            if (_gridBuilder.Grid == null) return 0;
            int nx = Pathfinding.PathfindingCore.WorldToNavcell(x);
            int nz = Pathfinding.PathfindingCore.WorldToNavcell(z);
            if (nx < 0 || nz < 0 || nx >= _gridBuilder.Grid.W || nz >= _gridBuilder.Grid.H)
                return 0;
            return _hier.GetGlobalRegion(nx, nz, DefaultClass.Mask);
        }

        /// <summary>原版 getPassabilityClassMask:类名 → 位掩码(未知名 → default)。</summary>
        public Pathfinding.PassClass GetPassabilityClassMask(string name) => _config.MaskOf(name);
        /// <summary>类名 → 定义;未知 → null。</summary>
        public PassabilityClassDef? GetClassByName(string name) => _config.ByName(name);

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
            uint allowedGroup = 0, string passClass = "building-land")
        {
            if (Terrain != null)
            {
                if (!Terrain.IsInBounds(new FixedVector2D(x - hw, z - hh)) ||
                    !Terrain.IsInBounds(new FixedVector2D(x + hw, z + hh)))
                    return PlacementResult.FailOutOfBounds;
                // 通行类地形规则(原版 CCmpPathfinder::CheckBuildingPlacement 按类查 navcell
                // 位图):building-land=离地 4m+且无水,building-shore=离水 8m 内(码头)等。
                // 网格未建(测试环境)回退旧 IsFootprintOnLand。
                if (_gridBuilder.Grid != null)
                {
                    var cls = _config.ByName(passClass) ?? _config.ByName("building-land")!;
                    if (!FootprintPassableOnGrid(x, z, hw, hh, cls.Mask))
                        return PlacementResult.FailTerrain;
                }
                else if (passClass != "building-shore" && !Terrain.IsFootprintOnLand(x, z, hw, hh))
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

        /// <summary>footprint 四角( + 中心)所在 navcell 对该类全可通行(原版按类查位图)。
        /// 采样点取四角内缩 0.5m + 中心,足抵小 footprint 的 navcell 粒度误差。</summary>
        private bool FootprintPassableOnGrid(Fixed x, Fixed z, Fixed hw, Fixed hh,
            Pathfinding.PassClass mask)
        {
            var grid = _gridBuilder.Grid!;
            Fixed inset = Fixed.FromFraction(1, 2);
            var pts = new[]
            {
                (x - hw + inset, z - hh + inset), (x + hw - inset, z - hh + inset),
                (x - hw + inset, z + hh - inset), (x + hw - inset, z + hh - inset),
                (x, z),
            };
            foreach (var (px, pz) in pts)
            {
                int ni = PathfindingCore.WorldToNavcell(px);
                int nj = PathfindingCore.WorldToNavcell(pz);
                if (ni < 0 || nj < 0 || ni >= grid.W || nj >= grid.H) return false;
                if (!PathfindingCore.IsPassable(grid.Get(ni, nj), mask)) return false;
            }
            return true;
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
            Obstructions.SetPathfinderMargin(_gridBuilder.MaxClearanceNavcells);
            Obstructions.ClearPathfinderDirtiness();   // 全量重建吞掉累计脏区
            _pathGen++;            // 寻路缓存世代++(旧条目永不命中)
            _pathCache.Clear();
            if (_gridBuilder.Grid != null)
            {
                try
                {
                    // 连通性只对单位寻路类建(原版全类;建筑/AI 类不参与寻路查询,
                    // 跳过省 5/9 的洪泛开销,寻路结果逐位等价)。
                    _hier.Recompute(_gridBuilder.Grid, _gridBuilder.UnitClasses);
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

        /// <summary>地形 tile → TerrainTileInfo 采样(全量 Rebuild 与增量刷新共用)。
        /// 有水位 + 高度网格时算真实值(原版 CTerrain 同款):
        ///   水深 = max(0, 水位 − 四角均值高);坡度 = (四角最高−最低)/tile 边长;
        ///   岸线距离 = 水陆 BFS 距离变换(米)。
        /// 无水位数据回退旧近似(land=0/water=深 5/impassable=坡度 2)。</summary>
        private TerrainTileInfo[,] BuildTerrainTiles(int tiles)
        {
            var terrain = new TerrainTileInfo[tiles, tiles];
            float ts = Terrain!.TileSize;
            bool real = Terrain.HasWaterLevel;
            Fixed water = Terrain.WaterLevel;
            for (int j = 0; j < tiles; j++)
                for (int i = 0; i < tiles; i++)
                {
                    if (!real)
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
                        continue;
                    }
                    // 四角采样(原版 CTerrain::GetSlopeFixed 同款数据源)。
                    Fixed h00 = Terrain.GetHeight(Fixed.FromFloat(i * ts), Fixed.FromFloat(j * ts));
                    Fixed h10 = Terrain.GetHeight(Fixed.FromFloat((i + 1) * ts), Fixed.FromFloat(j * ts));
                    Fixed h01 = Terrain.GetHeight(Fixed.FromFloat(i * ts), Fixed.FromFloat((j + 1) * ts));
                    Fixed h11 = Terrain.GetHeight(Fixed.FromFloat((i + 1) * ts), Fixed.FromFloat((j + 1) * ts));
                    Fixed hi = h00; if (h10 > hi) hi = h10; if (h01 > hi) hi = h01; if (h11 > hi) hi = h11;
                    Fixed lo = h00; if (h10 < lo) lo = h10; if (h01 < lo) lo = h01; if (h11 < lo) lo = h11;
                    Fixed slope = (hi - lo) / Fixed.FromFloat(ts);
                    Fixed avg = (h00 + h10 + h01 + h11) / 4;
                    Fixed depth = water > avg ? water - avg : Fixed.Zero;
                    // 悬崖 passability 类仍优先(坡度网格是表现层从真实高度图推的,含悬崖标记)。
                    if (Terrain.GetClass(Fixed.FromFloat(i * ts + ts * 0.5f),
                            Fixed.FromFloat(j * ts + ts * 0.5f)) == TerrainClass.Impassable
                        && slope < Fixed.FromInt(2))
                        slope = Fixed.FromInt(2);
                    terrain[i, j] = new TerrainTileInfo(depth, slope, Fixed.Zero);
                }
            if (real)
                ComputeShoreDistances(terrain, tiles, ts);
            return terrain;
        }

        /// <summary>岸线距离变换:多源 BFS 从水格向外扩,陆格记到最近水格的米数
        /// (building-land MinShoreDistance=4 / building-shore MaxShoreDistance=8 的数据源)。
        /// 水格 shore=0;纯陆图(无水)全部 = 巨大值(满足 MinShoreDistance)。</summary>
        private static void ComputeShoreDistances(TerrainTileInfo[,] terrain, int tiles, float ts)
        {
            var dist = new int[tiles, tiles];
            var queue = new System.Collections.Generic.Queue<(int x, int z)>();
            for (int j = 0; j < tiles; j++)
                for (int i = 0; i < tiles; i++)
                    if (terrain[i, j].WaterDepth > Fixed.Zero)
                    {
                        dist[i, j] = 0;
                        queue.Enqueue((i, j));
                        terrain[i, j] = new TerrainTileInfo(
                            terrain[i, j].WaterDepth, terrain[i, j].Slope, Fixed.Zero);
                    }
                    else dist[i, j] = int.MaxValue;
            if (queue.Count == 0)
            {
                for (int j = 0; j < tiles; j++)
                    for (int i = 0; i < tiles; i++)
                        terrain[i, j] = new TerrainTileInfo(terrain[i, j].WaterDepth,
                            terrain[i, j].Slope, Fixed.FromInt(1 << 20));
                return;
            }
            while (queue.Count > 0)
            {
                var (x, z) = queue.Dequeue();
                for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        if (di == 0 && dj == 0) continue;
                        int ni = x + di, nj = z + dj;
                        if (ni < 0 || nj < 0 || ni >= tiles || nj >= tiles) continue;
                        int nd = dist[x, z] + 1;
                        if (nd < dist[ni, nj])
                        {
                            dist[ni, nj] = nd;
                            queue.Enqueue((ni, nj));
                        }
                    }
            }
            for (int j = 0; j < tiles; j++)
                for (int i = 0; i < tiles; i++)
                    if (dist[i, j] != int.MaxValue)
                        terrain[i, j] = new TerrainTileInfo(terrain[i, j].WaterDepth,
                            terrain[i, j].Slope, Fixed.FromFloat(dist[i, j] * ts));
        }

        /// <summary>增量更新(上游 CCmpPathfinder::UpdateGrid 的非全量路径):
        /// ObstructionManager 累计的脏 navcell 矩形 → 逐矩形从地形基线恢复 +
        /// 重戳相交形状 → 分层寻路按脏 chunk 局部重连 → 长程缓存失效。
        /// 零脏区零开销;网格未建回落全量。每回合末由 SimBridge 调一次
        /// (上游 Simulation2.cpp:613 同款位置——回合内网格对寻路只读)。</summary>
        public void UpdateGrid()
        {
            var mgr = Obstructions;
            if (mgr == null || !mgr.HasPathfinderDirtiness) return;
            var dirty = mgr.TakePathfinderDirtiness();
            if (_gridBuilder.Grid == null || _gridBuilder.TerrainOnly == null)
            {
                RebuildGrid();   // 未初始化:全量
                return;
            }

            var obstructions = mgr.GetAllStaticObstructions();
            foreach (var (i0, j0, i1, j1) in dirty)
                _gridBuilder.PatchRect(i0, j0, i1, j1, obstructions);
            _pathGen++;            // 寻路缓存世代++(旧条目永不命中)
            _pathCache.Clear();
            try
            {
                // 连通性只对单位寻路类建(同 RebuildGrid 注释)。
                _hier.Update(_gridBuilder.Grid, dirty, _gridBuilder.UnitClasses);
                _long.Reload(_gridBuilder.Grid);
            }
            catch (System.Exception ex)
            {
                Diag.Warn("Pathfinder", $"incremental grid update failed: {ex.Message}; falling back to full rebuild");
                RebuildGrid();
            }
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
