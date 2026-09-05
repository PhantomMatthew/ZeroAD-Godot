using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;
using Xunit;

namespace ZeroAD.Sim.Tests.Pathfinding;

// Tests for the passability grid builder. Verifies terrain classification (land/water →
// default/ship classes), obstruction stamping, and clearance expansion.
public sealed class PassabilityGridTests
{
    private static TerrainTileInfo Land() => new(Fixed.Zero, Fixed.Zero, Fixed.Zero);          // depth 0, slope 0
    private static TerrainTileInfo DeepWater() => new(Fixed.FromInt(5), Fixed.Zero, Fixed.Zero); // depth 5
    private static TerrainTileInfo ShallowWater() => new(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero); // depth 1 (shallow)

    [Fact]
    public void LandTile_PassableForDefault_ImpassableForShip()
    {
        var terrain = new TerrainTileInfo[,] { { Land() } };
        var builder = new PassabilityGridBuilder();
        builder.Build(terrain, terrainTilesPerSide: 1, obstructions: System.Array.Empty<ObstructionSquare>());

        var grid = builder.Grid!;
        // 1 tile → 4x4 navcells, all land.
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++)
            {
                Assert.True(PathfindingCore.IsPassable(grid.Get(i, j), builder.Default.Mask),
                    $"default should pass land at ({i},{j})");
                Assert.False(PathfindingCore.IsPassable(grid.Get(i, j), builder.Ship.Mask),
                    $"ship should NOT pass land at ({i},{j})");
            }
    }

    [Fact]
    public void DeepWaterTile_ImpassableForDefault_PassableForShip()
    {
        var terrain = new TerrainTileInfo[,] { { DeepWater() } };
        var builder = new PassabilityGridBuilder();
        builder.Build(terrain, terrainTilesPerSide: 1, obstructions: System.Array.Empty<ObstructionSquare>());

        var grid = builder.Grid!;
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++)
            {
                Assert.False(PathfindingCore.IsPassable(grid.Get(i, j), builder.Default.Mask));
                Assert.True(PathfindingCore.IsPassable(grid.Get(i, j), builder.Ship.Mask));
            }
    }

    [Fact]
    public void ShallowWater_DeptExactly1_PassableForShip()
    {
        // MinWaterDepth for ship is 1; depth==1 should be passable (inclusive).
        var terrain = new TerrainTileInfo[,] { { ShallowWater() } };
        var builder = new PassabilityGridBuilder();
        builder.Build(terrain, terrainTilesPerSide: 1, obstructions: System.Array.Empty<ObstructionSquare>());

        Assert.True(PathfindingCore.IsPassable(builder.Grid!.Get(0, 0), builder.Ship.Mask));
    }

    [Fact]
    public void ObstructionStamp_MarksBuildingCellsImpassable()
    {
        // A 3x3 terrain (12x12 navcells), all land. Place a building at navcell centre (6,6)
        // with half-extent ~2 (covers ~5x5 navcells). Ship doesn't matter here.
        var terrain = new TerrainTileInfo[3, 3];
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
                terrain[i, j] = Land();

        var building = new ObstructionSquare(
            x: Fixed.FromFloat(6.5f), z: Fixed.FromFloat(6.5f),
            u: new FixedVector2D(Fixed.FromInt(1), Fixed.Zero),
            v: new FixedVector2D(Fixed.Zero, Fixed.FromInt(1)),
            hw: Fixed.FromInt(2), hh: Fixed.FromInt(2));

        var builder = new PassabilityGridBuilder();
        builder.Build(terrain, terrainTilesPerSide: 3, new[] { building });

        var grid = builder.Grid!;
        // The building centre navcell must be impassable for default.
        Assert.False(PathfindingCore.IsPassable(grid.Get(6, 6), builder.Default.Mask));
        // A far corner navcell must still be passable.
        Assert.True(PathfindingCore.IsPassable(grid.Get(0, 0), builder.Default.Mask));
    }

    [Fact]
    public void ClearanceExpansion_BlocksCellsAdjacentToObstruction()
    {
        // default clearance is 0.8 → ~1 navcell of expansion. A stamp at centre should make a
        // ring of cells around it also impassable.
        var terrain = new TerrainTileInfo[3, 3];
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
                terrain[i, j] = Land();

        var building = new ObstructionSquare(
            Fixed.FromFloat(6.5f), Fixed.FromFloat(6.5f),
            new FixedVector2D(Fixed.FromInt(1), Fixed.Zero),
            new FixedVector2D(Fixed.Zero, Fixed.FromInt(1)),
            Fixed.FromInt(1), Fixed.FromInt(1));

        var builder = new PassabilityGridBuilder();
        builder.Build(terrain, terrainTilesPerSide: 3, new[] { building });

        var grid = builder.Grid!;
        // The cell immediately adjacent (diagonal) to the obstruction's stamped area should be
        // blocked by the 1-navcell expansion. (6,6) is stamped; (5,5) is one step away.
        Assert.False(PathfindingCore.IsPassable(grid.Get(6, 6), builder.Default.Mask));
        Assert.False(PathfindingCore.IsPassable(grid.Get(5, 5), builder.Default.Mask));
    }

    [Fact]
    public void GridSize_IsTerrainTilesTimesNavcellsPerTile()
    {
        var terrain = new TerrainTileInfo[2, 2];
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
                terrain[i, j] = Land();

        var builder = new PassabilityGridBuilder();
        builder.Build(terrain, terrainTilesPerSide: 2, System.Array.Empty<ObstructionSquare>());

        // 2 tiles × 4 navcells/tile = 8 navcells per side.
        Assert.Equal(8, builder.NavcellsPerSide);
        Assert.Equal(8, builder.Grid!.W);
        Assert.Equal(8, builder.Grid!.H);
    }

    private static TerrainTileInfo[,] AllLand(int tiles)
    {
        var terrain = new TerrainTileInfo[tiles, tiles];
        for (int j = 0; j < tiles; j++)
            for (int i = 0; i < tiles; i++)
                terrain[i, j] = Land();
        return terrain;
    }

    [Fact]
    public void OffWorldEdge_SquareMap_BorderBandImpassableForAllClasses()
    {
        // 8 tiles = 32 navcells;边带 = 3 tiles × 4 = 12 navcells(上游 MAP_EDGE_TILES=3)。
        var builder = new PassabilityGridBuilder();   // PassabilityCircular 缺省 false = 方带
        builder.Build(AllLand(8), terrainTilesPerSide: 8, System.Array.Empty<ObstructionSquare>());
        var grid = builder.Grid!;
        int edge = PassabilityGridBuilder.MapEdgeTiles * PathfindingCore.NavcellsPerTerrainTile;

        // 左边带全格不可行(default 与 ship 同印——edgeMask 是全类 OR)。
        for (int j = 0; j < 32; j++)
            for (int i = 0; i < edge; i++)
            {
                Assert.False(PathfindingCore.IsPassable(grid.Get(i, j), builder.Default.Mask),
                    $"left band ({i},{j}) should be impassable for default");
                Assert.False(PathfindingCore.IsPassable(grid.Get(i, j), builder.Ship.Mask),
                    $"left band ({i},{j}) should be impassable for ship");
            }
        // 上边带 + 下边带(下带从 h-edge+1 起,逐字上游)。
        for (int i = edge; i < 32 - edge + 1; i++)
        {
            Assert.False(PathfindingCore.IsPassable(grid.Get(i, 0), builder.Default.Mask));
            Assert.False(PathfindingCore.IsPassable(grid.Get(i, 31), builder.Default.Mask));
        }
        // 内侧中心可行。
        Assert.True(PathfindingCore.IsPassable(grid.Get(16, 16), builder.Default.Mask));
        // 纯地形基线也含边带(增量补丁恢复时外缘不丢)。
        Assert.False(PathfindingCore.IsPassable(builder.TerrainOnly!.Get(0, 0), builder.Default.Mask));
    }

    [Fact]
    public void OffWorldEdge_CircularMap_CornersCutDeeperThanEdges()
    {
        var builder = new PassabilityGridBuilder { PassabilityCircular = true };
        builder.Build(AllLand(8), terrainTilesPerSide: 8, System.Array.Empty<ObstructionSquare>());
        var grid = builder.Grid!;
        int w = 32, edge = PassabilityGridBuilder.MapEdgeTiles * PathfindingCore.NavcellsPerTerrainTile;

        // 上游判式:dist2 ≥ (w−2e)·(h−2e) 即不可行(格心双倍坐标)。
        static bool ExpectedImpassable(int i, int j, int w, int h, int e)
        {
            long di = 2L * i + 1 - w, dj = 2L * j + 1 - h;
            return di * di + dj * dj >= (long)(w - 2 * e) * (h - 2 * e);
        }
        // 印戳在 clearance 膨胀之前(上游同序)——期望集 = 印戳的 Chebyshev 膨胀
        // (default clearance 0.8 → 1 navcell,8 连通环形,与 ExpandImpassable 同式)。
        int clear = builder.Default.Clearance.ToIntRoundToInfinity();
        bool[,] stamped = new bool[w, w];
        for (int j = 0; j < w; j++)
            for (int i = 0; i < w; i++)
                stamped[i, j] = ExpectedImpassable(i, j, w, w, edge);
        for (int j = 0; j < w; j++)
            for (int i = 0; i < w; i++)
            {
                bool expected = false;
                for (int dj = -clear; dj <= clear && !expected; dj++)
                    for (int di = -clear; di <= clear && !expected; di++)
                    {
                        int ni = i + di, nj = j + dj;
                        if ((uint)ni < (uint)w && (uint)nj < (uint)w && stamped[ni, nj])
                            expected = true;
                    }
                Assert.Equal(!expected,
                    PathfindingCore.IsPassable(grid.Get(i, j), builder.Default.Mask));
            }

        // 角部比边中带切得更深:近角 (13,13) 不可行,而边中 (16,13) 可行。
        Assert.False(PathfindingCore.IsPassable(grid.Get(13, 13), builder.Default.Mask));
        Assert.True(PathfindingCore.IsPassable(grid.Get(16, 13), builder.Default.Mask));
    }

    [Fact]
    public void OffWorldEdge_TinyGrid_Skipped()
    {
        // 小图(≤2×边带)无内侧区域,印戳会全图封死——guard 跳过(测试图常用尺寸)。
        var builder = new PassabilityGridBuilder();
        builder.Build(AllLand(3), terrainTilesPerSide: 3, System.Array.Empty<ObstructionSquare>());
        Assert.True(PathfindingCore.IsPassable(builder.Grid!.Get(0, 0), builder.Default.Mask));
    }
}
