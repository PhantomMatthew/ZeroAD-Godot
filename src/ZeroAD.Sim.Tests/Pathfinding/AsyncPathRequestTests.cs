using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;
using Xunit;

namespace ZeroAD.Sim.Tests.Pathfinding;

/// <summary>异步路径请求(UnitMotion 异步架构)契约测试:
/// 预算内即答 / 超预算入队次回合投递 / ticket 过期丢弃 / 无驱动全同步。</summary>
public sealed class AsyncPathRequestTests
{
    private static TerrainTileInfo[,] FlatTerrain(int tiles)
    {
        var t = new TerrainTileInfo[tiles, tiles];
        for (int j = 0; j < tiles; j++)
            for (int i = 0; i < tiles; i++)
                t[i, j] = new TerrainTileInfo(Fixed.Zero, Fixed.Zero, Fixed.Zero);
        return t;
    }

    private static PathfinderComponent MakePathfinder(ComponentManager cm, int tiles = 16)
    {
        var pf = new PathfinderComponent(cm) { AsyncPathDriver = true };
        pf.RebuildGridFromTiles(FlatTerrain(tiles), tiles, System.Array.Empty<ObstructionSquare>());
        return pf;
    }

    [Fact]
    public void WithinBudget_ImmediateAnswer()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var pf = MakePathfinder(cm);
        var goal = PathGoal.Point(Fixed.FromInt(50), Fixed.FromInt(50));
        bool immediate = pf.RequestLongPath(new EntityId(7),
            new FixedVector2D(Fixed.FromInt(5), Fixed.FromInt(5)), goal,
            pf.GetPassabilityClassMask("default"), out var path, out _);
        Assert.True(immediate);
        Assert.NotEmpty(path.Waypoints);
        Assert.Equal(0, pf.PendingPathRequests);
    }

    [Fact]
    public void BeyondBudget_EnqueuesAndDeliversNextTurn()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var pf = MakePathfinder(cm);
        var pc = pf.GetPassabilityClassMask("default");

        // 烧光预算(每请求不同起点,避开 memo 缓存)。
        for (int i = 0; i < pf.MaxSameTurnPaths; i++)
        {
            var g = PathGoal.Point(Fixed.FromInt(50), Fixed.FromInt(50));
            Assert.True(pf.RequestLongPath(new EntityId((uint)(100 + i)),
                new FixedVector2D(Fixed.FromInt(1 + i), Fixed.FromInt(1)), g, pc,
                out _, out _));
        }
        // 下一个:入队(不同起点避缓存)。
        var goal = PathGoal.Point(Fixed.FromInt(55), Fixed.FromInt(55));
        bool immediate = pf.RequestLongPath(new EntityId(999),
            new FixedVector2D(Fixed.FromInt(30), Fixed.FromInt(30)), goal, pc,
            out _, out uint ticket);
        Assert.False(immediate);
        Assert.NotEqual(0u, ticket);
        Assert.Equal(1, pf.PendingPathRequests);

        // 回合末启动后台求解;次回合收割投递。
        pf.StartAsyncPathComputation();
        // 投递目标是 UnitMotion——本测试无实体组件,只验汇缴清空队列不抛。
        int delivered = pf.HarvestPathResults();
        Assert.Equal(1, delivered);
        Assert.Equal(0, pf.PendingPathRequests);
        // 收割后预算复位:下一请求又即答。
        Assert.True(pf.RequestLongPath(new EntityId(1000),
            new FixedVector2D(Fixed.FromInt(40), Fixed.FromInt(40)), goal, pc, out _, out _));
    }

    [Fact]
    public void UnitMotion_PendingPath_KeepsOldWaypoints_AndAdoptsOnDelivery()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var pf = MakePathfinder(cm);
        SimSystem.SetPathfinder(pf);
        pf.MaxSameTurnPaths = 0;   // 全部走异步

        var ent = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(ent, pos);
        pos.Position = new FixedVector3D(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(5));
        var motion = new UnitMotion();
        cm.AddComponent(ent, motion);

        // 第一条:旧路标为空 → pending + 直线暂行(不站桩)。
        motion.MoveToPoint(new FixedVector2D(Fixed.FromInt(50), Fixed.FromInt(50)));
        Assert.True(motion.HasMoveTarget);

        float x0 = pos.Position.X.ToFloat();
        for (int i = 0; i < 10; i++) motion.Tick(0.1f);
        Assert.True(pos.Position.X.ToFloat() > x0, "pending 期间应沿暂行直线移动");

        // 回合末启动 + 收割 → 安装真路径(开放平原 = 同向,主要看组件活着且目标未丢)。
        pf.StartAsyncPathComputation();
        pf.HarvestPathResults();
        Assert.True(motion.HasMoveTarget);
        for (int i = 0; i < 600 && motion.HasMoveTarget; i++) motion.Tick(0.1f);
        Assert.False(motion.HasMoveTarget);   // 到达
        Assert.True(System.Math.Abs(pos.Position.X.ToFloat() - 50f) < 2f);
    }

    [Fact]
    public void UnitMotion_StaleTicket_Discarded()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var pf = MakePathfinder(cm);
        SimSystem.SetPathfinder(pf);

        var ent = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(ent, pos);
        pos.Position = new FixedVector3D(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(5));
        var motion = new UnitMotion();
        cm.AddComponent(ent, motion);

        // 直接投递未知 ticket → 丢弃(不抛)。
        motion.OnPathResult(12345, new WaypointPath());
        Assert.False(motion.HasMoveTarget);
    }

    [Fact]
    public void NoDriver_AlwaysSynchronous()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var pf = new PathfinderComponent(cm);   // AsyncPathDriver = false
        pf.RebuildGridFromTiles(FlatTerrain(16), 16, System.Array.Empty<ObstructionSquare>());
        // 预算 0 也同步即答(无驱动 = 旧行为)。
        pf.MaxSameTurnPaths = 0;
        var goal = PathGoal.Point(Fixed.FromInt(50), Fixed.FromInt(50));
        Assert.True(pf.RequestLongPath(new EntityId(1),
            new FixedVector2D(Fixed.FromInt(5), Fixed.FromInt(5)), goal,
            pf.GetPassabilityClassMask("default"), out var path, out _));
        Assert.NotEmpty(path.Waypoints);
    }
}
