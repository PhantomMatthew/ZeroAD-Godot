using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>LOS 离世外环(原版 CCmpRangeManager::LosIsOffWorld):外圈 MapEdgeTiles(3)
/// 顶点环(方形)/收缩半径外(圆形)永不探索/可见——SoD 纹理在图缘渐黑,圈外区域
/// 不再显示烘焙地形。序列化格式不变,round-trip 语义一致。</summary>
public sealed class LosGridOffWorldTests
{
    private const int World = 256;   // 65 顶点/边
    private const int N = World / LosGrid.TileSize + 1;

    [Fact]
    public void Square_EdgeRing_NeverExploredBySeer()
    {
        var los = new LosGrid(World);
        // 角落的 seer 视野盖到图缘:环上顶点不计数(永不探索),环内正常。
        los.AddLos(1, Fixed.Zero, Fixed.Zero, Fixed.FromInt(40));
        Assert.False(los.IsExplored(1, 0, 0));
        Assert.False(los.IsExplored(1, 2, 2));
        Assert.False(los.IsVisible(1, 0, 1));
        Assert.True(los.IsExplored(1, 5, 5));
        Assert.True(los.IsVisible(1, 5, 5));
    }

    [Fact]
    public void Square_ExploreAll_LeavesRingUnexplored()
    {
        var los = new LosGrid(World);
        los.ExploreAll(1);
        Assert.False(los.IsExplored(1, 0, 0));
        Assert.False(los.IsExplored(1, N - 1, N - 1));
        Assert.False(los.IsExplored(1, 1, 30));
        Assert.True(los.IsExplored(1, 3, 3));
        // 分母只含在世顶点:ExploreAll 后恰为 100%。
        Assert.Equal(100, los.GetPercentExplored(1));
    }

    [Fact]
    public void Circular_CornersAlwaysOffWorld()
    {
        var los = new LosGrid(World, circular: true);
        // 大视野 seer 也点不亮角部;图心正常。
        los.AddLos(1, Fixed.Zero, Fixed.Zero, Fixed.FromInt(120));
        Assert.False(los.IsExplored(1, 0, 0));
        Assert.False(los.IsExplored(1, N - 1, 0));
        los.AddLos(1, Fixed.FromInt(128), Fixed.FromInt(128), Fixed.FromInt(20));
        Assert.True(los.IsExplored(1, N / 2, N / 2));
    }

    [Fact]
    public void Circular_ExploreAll_RespectsCircle()
    {
        var los = new LosGrid(World, circular: true);
        los.ExploreAll(1);
        Assert.False(los.IsExplored(1, 10, 10));   // dist2=2×22²=968 ≥ r²=30² → 在世外
        Assert.True(los.IsExplored(1, N / 2, N / 2));
        Assert.Equal(100, los.GetPercentExplored(1));
    }

    [Fact]
    public void Circular_FlagFlip_RecomputesPercentBase()
    {
        var los = new LosGrid(World);
        los.Circular = true;   // 模拟地图设置晚到(场景加载在 SetBounds 之后)
        los.ExploreAll(1);
        Assert.False(los.IsExplored(1, 0, 0));
        Assert.Equal(100, los.GetPercentExplored(1));
    }

    [Fact]
    public void RoundTrip_PreservesOffWorldState()
    {
        var losA = new LosGrid(World, circular: true);
        losA.AddLos(1, Fixed.FromInt(128), Fixed.FromInt(128), Fixed.FromInt(24));
        var cap = new CapturingSerializer();
        losA.Serialize(cap);

        var losB = new LosGrid(World, circular: true);
        losB.Deserialize(new ReplayingDeserializer(cap));
        Assert.Equal(losA.IsExplored(1, 0, 0), losB.IsExplored(1, 0, 0));
        Assert.Equal(losA.IsExplored(1, N / 2, N / 2), losB.IsExplored(1, N / 2, N / 2));
        Assert.Equal(losA.GetPercentExplored(1), losB.GetPercentExplored(1));
    }
}
