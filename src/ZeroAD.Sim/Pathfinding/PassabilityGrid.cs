using System.Collections.Generic;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Pathfinding;

// PassabilityGridBuilder — produces the navcell passability grid from terrain info +
// obstruction shapes. Ported from source/simulation2/helpers/Rasterize.cpp (ExpandImpassableCells)
// and CCmpPathfinder::UpdateGrid.
//
// 通行类注册表驱动(pathfinder.xml;见 PathfinderConfig):
//   单位寻路类 default/large/ship/ship-small(Obstructions=pathfinding,印 BlockPathfinding)
//   建筑放置类 building-land/building-shore(Obstructions=foundation,印 BlockFoundation)
//   AI 类 unrestricted/*-terrain-only(Obstructions=none,只按地形规则)
//
// Pipeline:
//   1. Rasterize terrain: for each 4m terrain tile, classify its 4x4 navcell block by the
//      tile's depth/slope/shore-distance. A navcell is impassable for a class if the tile
//      fails that class's terrain rules.
//   2. Stamp obstructions: for each class, mark navcells covered by static obstruction shapes
//      carrying the kind-matching flag impassable.
//   3. Expand by clearance: dilate the impassable region by each class's clearance radius
//      (ExpandImpassableCells) so units keep distance from walls/buildings.

/// <summary>Builds and owns the navcell passability grid + the passability-class registry.</summary>
public sealed class PassabilityGridBuilder
{
    private readonly PathfinderConfig _config;

    /// <summary>The default (land) passability class.</summary>
    public PassabilityClassDef Default => _config.ByName("default")!;
    /// <summary>The ship (water) passability class.</summary>
    public PassabilityClassDef Ship => _config.ByName("ship")!;

    /// <summary>The current passability grid (navcell → bitmask). Null until Build.</summary>
    public Grid<NavcellData>? Grid { get; private set; }

    /// <summary>纯地形基线(地形栅格化 + 地形侧 clearance 膨胀,无 obstruction)。
    /// 增量补丁从这里恢复脏格再重戳(上游 m_TerrainOnlyGrid 同款,CCmpPathfinder.cpp:576-580)。
    /// 语义等价说明:旧管线是 dilate(地形∪障碍),新管线是 dilate(地形)∪dilate(障碍)
    /// ——8 连通环形膨胀(Chebyshev dilation)对并集可分配,逐位相同,测试钉死。</summary>
    public Grid<NavcellData>? TerrainOnly { get; private set; }

    /// <summary>全类最大 clearance(navcell,Chebyshev 半径;脏区相交测试的外扩量)。</summary>
    public int MaxClearanceNavcells { get; private set; }

    /// <summary>Navcells per side (square map). 0 until Build.</summary>
    public int NavcellsPerSide { get; private set; }

    public PassabilityGridBuilder(PathfinderConfig? config = null)
    {
        _config = config ?? PathfinderConfig.Default();
    }

    /// <summary>All defined classes (for hierarchical/long pathfinder recompute).</summary>
    public IEnumerable<PassabilityClassDef> AllClasses => _config.Classes;

    /// <summary>图外缘不可通行带的宽度(地块;原版 MapEdgeTiles.h MAP_EDGE_TILES=3)。
    /// 与渲染侧 CLOSTexture 阴影模糊半径挂钩,勿随意改。</summary>
    public const int MapEdgeTiles = 3;

    /// <summary>圆形图外缘(原版 ICmpObstructionManager.SetPassabilityCircular):
    /// true = 图外缘按圆印(直径 = 图宽 − 2×边带),false = 四边方带。
    /// 由地图设置(CircularMap)经 PathfinderComponent.RebuildGrid 传入。</summary>
    public bool PassabilityCircular { get; set; }

    /// <summary>单位寻路类(default/large/ship/ship-small)——寻路连通性只对它们建。</summary>
    public IEnumerable<PassabilityClassDef> UnitClasses => _config.UnitPathClasses();

    public PassabilityClassDef GetClass(PassClass mask) =>
        _config.Classes.Count > 0 && mask.Mask != 0
            ? _config.Classes[System.Math.Min(
                System.Numerics.BitOperations.Log2(mask.Mask), _config.Classes.Count - 1)]
            : Default;

    public PassabilityClassDef? GetClassByName(string name) => _config.ByName(name);
    public PassClass MaskOf(string name) => _config.MaskOf(name);

    /// <summary>Build the passability grid from terrain tile info + obstruction shapes.</summary>
    /// <param name="terrain">Per-tile (4m) depth/slope/shore info, indexed [tileX, tileZ].</param>
    /// <param name="terrainTilesPerSide">Terrain tiles per map side.</param>
    /// <param name="obstructions">Static obstruction squares to stamp (buildings etc.).</param>
    public void Build(TerrainTileInfo[,] terrain, int terrainTilesPerSide,
        IEnumerable<ObstructionSquare> obstructions)
    {
        int nav = terrainTilesPerSide * PathfindingCore.NavcellsPerTerrainTile;
        NavcellsPerSide = nav;
        var grid = new Grid<NavcellData>(nav, nav);

        // --- 1. Rasterize terrain: classify each navcell by its parent terrain tile. ---
        // Every navcell in a 4m tile inherits that tile's depth/slope/shore (the original does
        // the same — terrain is sampled at tile granularity).
        for (int tileZ = 0; tileZ < terrainTilesPerSide; tileZ++)
            for (int tileX = 0; tileX < terrainTilesPerSide; tileX++)
            {
                var info = terrain[tileX, tileZ];
                // 每类判定一次,把失败类的位印到该 tile 的 4×4 navcell 块。
                for (int dz = 0; dz < PathfindingCore.NavcellsPerTerrainTile; dz++)
                    for (int dx = 0; dx < PathfindingCore.NavcellsPerTerrainTile; dx++)
                    {
                        int ni = tileX * PathfindingCore.NavcellsPerTerrainTile + dx;
                        int nj = tileZ * PathfindingCore.NavcellsPerTerrainTile + dz;
                        var cell = grid.Get(ni, nj);
                        foreach (var cls in _config.Classes)
                            if (!cls.TerrainIsPassable(in info))
                                cell = PathfindingCore.MakeImpassable(cell, cls.Mask);
                        grid.Set(ni, nj, cell);
                    }
            }

        // --- 1.5 图外缘印戳(上游 CCmpPathfinder::UpdateGrid 的 off-world passability):
        // 全类不可通行(所有 mask 位),方形图印四条边带,圆形图按半径印圆。
        // 在 clearance 膨胀之前印——膨胀会把边带外扩,与上游一致(上游同序:
        // 先 off-world 再 ExpandImpassableCells)。 ---
        StampOffWorldEdge(grid);

        // --- 2. 地形侧 clearance 膨胀(先于障碍印戳——两格模型,见 TerrainOnly 注释)。 ---
        foreach (var cls in _config.Classes)
            ExpandImpassable(grid, cls);

        int maxClear = 0;
        foreach (var cls in _config.Classes)
            maxClear = System.Math.Max(maxClear, cls.Clearance.ToIntRoundToInfinity());
        MaxClearanceNavcells = maxClear;
        TerrainOnly = grid.Clone();

        // --- 3. Stamp static obstructions per class(印戳自带 clearance 扩展 =
        // 该形状的 Chebyshev 膨胀,与旧"先印后整体膨胀"逐位等价)。 ---
        foreach (var ob in obstructions)
            foreach (var cls in _config.Classes)
                StampObstruction(grid, ob, cls);

        Grid = grid;
    }

    /// <summary>增量补丁(上游 CCmpPathfinder::UpdateGrid 的非 globallyDirty 路径):
    /// 脏区 navcell 矩形先回滚地形基线,再重戳与其(按最大 clearance 外扩)相交的
    /// 全部静态形状。只 OR 不清——恢复由基线拷贝负责。</summary>
    public void PatchRect(int i0, int j0, int i1, int j1,
        IEnumerable<ObstructionSquare> obstructions)
    {
        var grid = Grid;
        var terrainOnly = TerrainOnly;
        if (grid == null || terrainOnly == null) return;
        i0 = System.Math.Max(0, i0); j0 = System.Math.Max(0, j0);
        i1 = System.Math.Min(grid.W - 1, i1); j1 = System.Math.Min(grid.H - 1, j1);
        if (i0 > i1 || j0 > j1) return;

        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
                grid.Set(i, j, terrainOnly.Get(i, j));

        int margin = MaxClearanceNavcells;
        foreach (var ob in obstructions)
        {
            var bb = Geometry.GetHalfBoundingBox(ob.U, ob.V, new FixedVector2D(ob.Hw, ob.Hh));
            int x0 = PathfindingCore.WorldToNavcell(ob.X - bb.X) - margin;
            int z0 = PathfindingCore.WorldToNavcell(ob.Z - bb.Y) - margin;
            int x1 = PathfindingCore.WorldToNavcell(ob.X + bb.X) + margin;
            int z1 = PathfindingCore.WorldToNavcell(ob.Z + bb.Y) + margin;
            if (x1 < i0 || x0 > i1 || z1 < j0 || z0 > j1) continue;
            foreach (var cls in _config.Classes)
                StampObstruction(grid, ob, cls);
        }
    }

    /// <summary>图外缘印戳(上游 UpdateGrid 的 off-world passability 段,CCmpPathfinder.cpp:700-744)。
    /// 边带 = MapEdgeTiles 地块 × 4 navcell;edgeMask = 全部类位的 OR(任何类都不可出图)。
    /// 方形图:四条边带(上游右侧/下侧从 w-edgeSize+1 起印,逐字保留——覆盖略不对称是原版行为)。
    /// 圆形图:以格心到图心距离判定,dist2 ≥ (w−2·edge)·(h−2·edge) 印不可行
    /// (上游注释:比 LOS 圆略紧,防单位走进边缘阴影区)。</summary>
    private void StampOffWorldEdge(Grid<NavcellData> grid)
    {
        int edgeSize = MapEdgeTiles * PathfindingCore.NavcellsPerTerrainTile;
        if (grid.W <= 2 * edgeSize || grid.H <= 2 * edgeSize) return;   // 小测试图无内侧,不印

        NavcellData edgeMask = new((ushort)0);
        foreach (var cls in _config.Classes)
            edgeMask = new NavcellData((ushort)(edgeMask.Value | cls.Mask.Mask));
        if (edgeMask.Value == 0) return;

        int w = grid.W, h = grid.H;
        if (PassabilityCircular)
        {
            long threshold = (long)(w - 2 * edgeSize) * (h - 2 * edgeSize);
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    // 双倍坐标表达半格精度(上游同式)。
                    long di = 2L * i + 1 - w, dj = 2L * j + 1 - h;
                    if (di * di + dj * dj >= threshold)
                        grid.Set(i, j, new NavcellData((ushort)(grid.Get(i, j).Value | edgeMask.Value)));
                }
        }
        else
        {
            for (int j = 0; j < h; j++)
                for (int i = 0; i < edgeSize; i++)
                    grid.Set(i, j, new NavcellData((ushort)(grid.Get(i, j).Value | edgeMask.Value)));
            for (int j = 0; j < h; j++)
                for (int i = w - edgeSize + 1; i < w; i++)
                    grid.Set(i, j, new NavcellData((ushort)(grid.Get(i, j).Value | edgeMask.Value)));
            for (int j = 0; j < edgeSize; j++)
                for (int i = edgeSize; i < w - edgeSize + 1; i++)
                    grid.Set(i, j, new NavcellData((ushort)(grid.Get(i, j).Value | edgeMask.Value)));
            for (int j = h - edgeSize + 1; j < h; j++)
                for (int i = edgeSize; i < w - edgeSize + 1; i++)
                    grid.Set(i, j, new NavcellData((ushort)(grid.Get(i, j).Value | edgeMask.Value)));
        }
    }

    // Stamp an axis-aligned approximation of an obstruction square onto the grid for one class.
    // P0 uses the AABB (half-bounding-box) of the oriented box; precise rotated rasterization
    // is a P1 refinement (matches the original's Rasterize which does per-edge, but AABB is a
    // close approximation for the small unit/building footprints at 1m navcell resolution).
    private static void StampObstruction(Grid<NavcellData> grid, ObstructionSquare ob, PassabilityClassDef cls)
    {
        var flag = cls.Obstructions == ObstructionKind.Pathfinding ? ObstructionFlags.BlockPathfinding
            : cls.Obstructions == ObstructionKind.Foundation ? ObstructionFlags.BlockFoundation
            : ObstructionFlags.None;
        if (flag == ObstructionFlags.None) return;
        if ((ob.Flags & flag) == 0) return;
        // AABB half-extents from the oriented box;按该类 clearance 外扩
        // (= 形状集合的 Chebyshev 膨胀,等价旧管线的事后 ExpandImpassable)。
        int expand = cls.Clearance.ToIntRoundToInfinity();
        var bb = Geometry.GetHalfBoundingBox(ob.U, ob.V, new FixedVector2D(ob.Hw, ob.Hh));
        int x0 = PathfindingCore.WorldToNavcell(ob.X - bb.X) - expand;
        int z0 = PathfindingCore.WorldToNavcell(ob.Z - bb.Y) - expand;
        int x1 = PathfindingCore.WorldToNavcell(ob.X + bb.X) + expand;
        int z1 = PathfindingCore.WorldToNavcell(ob.Z + bb.Y) + expand;
        for (int nj = z0; nj <= z1; nj++)
            for (int ni = x0; ni <= x1; ni++)
            {
                if (ni < 0 || nj < 0 || ni >= grid.W || nj >= grid.H) continue;
                var cell = grid.Get(ni, nj);
                grid.Set(ni, nj, PathfindingCore.MakeImpassable(cell, cls.Mask));
            }
    }

    // Expand impassable cells outward by the class's clearance radius, in navcells. This is the
    // fixed-point equivalent of ExpandImpassableCells in Rasterize.cpp: any passable cell within
    // `clearance` world-units of an impassable cell becomes impassable too.
    //
    // Implementation: multi-source BFS from all impassable cells, stopping at clearance. Using a
    // ring expansion (iterate distance = 0..clearanceNavcells) keeps it O(clearance² × stamps).
    private static void ExpandImpassable(Grid<NavcellData> grid, PassabilityClassDef cls)
    {
        int clearanceNav = cls.Clearance.ToIntRoundToInfinity();
        if (clearanceNav <= 0) return;

        // Snapshots of which cells are impassable-for-this-class at each ring distance, expanded
        // outward. Start with the current impassable set.
        bool[,] impassable = new bool[grid.W, grid.H];
        for (int j = 0; j < grid.H; j++)
            for (int i = 0; i < grid.W; i++)
                impassable[i, j] = !PathfindingCore.IsPassable(grid.Get(i, j), cls.Mask);

        // For each ring distance, mark any passable cell adjacent (8-connected) to an impassable
        // cell as impassable. Repeat clearanceNav times.
        for (int ring = 0; ring < clearanceNav; ring++)
        {
            bool[,] newlyBlocked = new bool[grid.W, grid.H];
            for (int j = 0; j < grid.H; j++)
                for (int i = 0; i < grid.W; i++)
                {
                    if (impassable[i, j]) continue;
                    // Check 8-neighbours; if any impassable, this cell gets blocked this ring.
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            if (di == 0 && dj == 0) continue;
                            int ni = i + di, nj = j + dj;
                            if (ni < 0 || nj < 0 || ni >= grid.W || nj >= grid.H) continue;
                            if (impassable[ni, nj]) { newlyBlocked[i, j] = true; break; }
                        }
                }
            // Commit this ring into the impassable set.
            bool any = false;
            for (int j = 0; j < grid.H; j++)
                for (int i = 0; i < grid.W; i++)
                    if (newlyBlocked[i, j]) { impassable[i, j] = true; any = true; }
            if (!any) break; // reached steady state early
        }

        // Write the expanded set back into the grid for this class's bit only.
        for (int j = 0; j < grid.H; j++)
            for (int i = 0; i < grid.W; i++)
                if (impassable[i, j])
                {
                    var cell = grid.Get(i, j);
                    grid.Set(i, j, PathfindingCore.MakeImpassable(cell, cls.Mask));
                }
    }
}
