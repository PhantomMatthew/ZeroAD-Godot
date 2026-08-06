using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Pathfinding;
using Xunit;

namespace ZeroAD.Sim.Tests;

// Accessibility(terrain-analysis.js 移植):水陆分类、双 flood-fill 区域、regionLinks、
// getTrajectTo。合成 8×8 navcell 网格:陆(左两列)| 水(中四列)| 陆(右两列)。
public sealed class AccessibilityTests
{
    // 掩码约定(IsPassable:bit 未置 = 可通行):陆=0x1,水=0x2。
    private static readonly PassClass LandMask = new(0x1);
    private static readonly PassClass ShipMask = new(0x2);

    private const ushort LandCell = 0x2;        // 陆可通/水受阻 → LAND
    private const ushort WaterCell = 0x1;       // 陆受阻/水可通 → DEEP_WATER
    private const ushort BlockedCell = 0x3;     // 双阻 → IMPASSABLE
    private const ushort ShallowCell = 0x0;     // 双通 → SHALLOW_WATER

    private static Grid<NavcellData> BuildGrid()
    {
        var grid = new Grid<NavcellData>(8, 8);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                ushort v = x < 2 ? LandCell : x < 6 ? WaterCell : LandCell;
                grid.Set(x, y, new NavcellData(v));
            }
        return grid;
    }

    private static Accessibility Build(Grid<NavcellData> grid) =>
        new(grid, LandMask, ShipMask, navcellsPerSide: 8, cellSize: 1);

    [Fact]
    public void Classification_LandAndWaterRegions()
    {
        var acc = Build(BuildGrid());
        Assert.True(acc.LandRegionAt(0, 0) > 1);    // 左陆
        Assert.True(acc.LandRegionAt(7, 7) > 1);    // 右陆
        Assert.True(acc.WaterRegionAt(3, 3) > 1);   // 中水
        Assert.Equal(1, acc.LandRegionAt(3, 3));    // 水格非陆
        Assert.Equal(1, acc.WaterRegionAt(0, 0));   // 陆格非水
    }

    [Fact]
    public void FloodFill_SeparateLandmassesGetSeparateRegions()
    {
        var acc = Build(BuildGrid());
        ushort left = acc.LandRegionAt(0, 0);
        ushort right = acc.LandRegionAt(7, 7);
        Assert.NotEqual(left, right);
        // 同一块陆内任意两点同区域。
        Assert.Equal(left, acc.LandRegionAt(1, 7));
        Assert.Equal("land", acc.GetRegionType(left));
        Assert.Equal("water", acc.GetRegionType(acc.WaterRegionAt(3, 3)));
    }

    [Fact]
    public void GetTrajectTo_CrossesWaterBetweenLandmasses()
    {
        var acc = Build(BuildGrid());
        // 左陆 (0.5,0.5) → 右陆 (7.5,7.5):路径应跨水(陆→水→陆,3 段)。
        var path = acc.GetTrajectTo(0.5f, 0.5f, 7.5f, 7.5f);
        Assert.NotNull(path);
        Assert.Equal(3, path!.Count);
        Assert.Equal("land", acc.GetRegionType(path[0]));
        Assert.Equal("water", acc.GetRegionType(path[1]));
        Assert.Equal("land", acc.GetRegionType(path[2]));
    }

    [Fact]
    public void GetTrajectTo_SameLandmass_SingleRegion()
    {
        var acc = Build(BuildGrid());
        var path = acc.GetTrajectTo(0.5f, 0.5f, 1.5f, 7.5f);
        Assert.NotNull(path);
        Assert.Single(path!);
    }

    [Fact]
    public void GetTrajectTo_ImpassableEndpoint_ReturnsNull()
    {
        var grid = BuildGrid();
        grid.Set(7, 7, new NavcellData(BlockedCell));   // 目标点双阻
        var acc = Build(grid);
        Assert.Null(acc.GetTrajectTo(0.5f, 0.5f, 7.5f, 7.5f));
    }

    [Fact]
    public void GetTrajectTo_WaterStart_WaterPath()
    {
        var acc = Build(BuildGrid());
        // 水中起点 → 水上路径到另一水格:同水域单区域。
        var path = acc.GetTrajectTo(2.5f, 0.5f, 5.5f, 7.5f);
        Assert.NotNull(path);
        Assert.Single(path!);
        Assert.Equal("water", acc.GetRegionType(path![0]));
    }

    [Fact]
    public void Classification_ShallowWater_IsBothPassable()
    {
        var grid = BuildGrid();
        grid.Set(2, 2, new NavcellData(ShallowCell));   // 双通浅滩
        var acc = Build(grid);
        // 浅滩:陆水两侧区域都 > 1(陆域跨不过去——浅滩是独立陆格;
        // 但它自身既可陆又可水)。
        Assert.True(acc.LandRegionAt(2, 2) > 1);
        Assert.True(acc.WaterRegionAt(2, 2) > 1);
    }
}
