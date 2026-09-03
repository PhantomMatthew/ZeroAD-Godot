using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>地基开工挤出(原版 Foundation.Commit + UnitAI.LeaveFoundation)测试。</summary>
public sealed class FoundationPushOutTests
{
    private static (ComponentManager cm, EntityId foundation, EntityId unit) World(
        bool deleteFlag = false)
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        SimSystem.SetObstructionManager(new ObstructionManager());
        SimSystem.SetRangeManager(new RangeManager(cm, Fixed.FromInt(256), Fixed.FromInt(256)));

        var f = cm.CreateEntity();
        cm.AddComponent(f, new PositionComponent());
        cm.QueryInterface<PositionComponent>(f)!.Position =
            new FixedVector3D(Fixed.FromInt(50), Fixed.Zero, Fixed.FromInt(50));
        cm.AddComponent(f, new OwnershipComponent { PlayerId = 1 });
        cm.NotifyEntityCreated(f);
        var fobs = new ObstructionComponent
        {
            Type = ObstructionType.Static,
            Size0 = Fixed.FromInt(8),
            Size1 = Fixed.FromInt(8),
        };
        cm.AddComponent(f, fobs);
        fobs.EnsureRegistered();
        var fd = new FoundationComponent();
        fd.Configure("structures/athen/house", 100f);
        cm.AddComponent(f, fd);

        var u = cm.CreateEntity();
        cm.AddComponent(u, new PositionComponent());
        cm.QueryInterface<PositionComponent>(u)!.Position =
            new FixedVector3D(Fixed.FromInt(50), Fixed.Zero, Fixed.FromInt(50));   // 站在地基正中
        cm.AddComponent(u, new OwnershipComponent { PlayerId = 1 });
        cm.AddComponent(u, new UnitMotion());
        cm.AddComponent(u, new UnitAIComponent());
        cm.NotifyEntityCreated(u);
        var uobs = new ObstructionComponent
        {
            Type = ObstructionType.Unit,
            Size0 = Fixed.FromInt(1),
            Flags = ObstructionFlags.DefaultBlock
                | (deleteFlag ? ObstructionFlags.DeleteUponConstruction : 0),
        };
        cm.AddComponent(u, uobs);
        uobs.EnsureRegistered();
        return (cm, f, u);
    }

    [Fact]
    public void Commit_PushesUnitOut()
    {
        var (cm, f, u) = World();
        var fd = cm.QueryInterface<FoundationComponent>(f)!;
        fd.Commit(cm);
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;
        var order = ai.CurrentOrder;
        Assert.NotNull(order);
        Assert.Equal("Walk", order!.Type);
        // 目标在地基外(中心 50,50;半对角 ~5.66 + 4 = ~9.7m 外)。
        var t = order.Position;
        float dx = t.X.ToFloat() - 50f, dz = t.Y.ToFloat() - 50f;
        Assert.True(dx * dx + dz * dz > 8f * 8f, "walk target must be outside the footprint");
    }

    [Fact]
    public void Commit_DeletesDeleteUponConstruction()
    {
        var (cm, f, u) = World(deleteFlag: true);
        var fd = cm.QueryInterface<FoundationComponent>(f)!;
        fd.Commit(cm);
        Assert.Null(cm.QueryInterface<PositionComponent>(u));   // 实体已销毁
    }

    [Fact]
    public void Commit_EnemyUnitStays()
    {
        var (cm, f, u) = World();
        var own = cm.QueryInterface<OwnershipComponent>(u)!;
        own.PlayerId = 2;   // 敌方(无互盟)
        var fd = cm.QueryInterface<FoundationComponent>(f)!;
        fd.Commit(cm);
        Assert.False(cm.QueryInterface<UnitAIComponent>(u)!.CurrentOrder?.Type == "Walk");
    }

    [Fact]
    public void Commit_UnitAlreadyOutside_Stays()
    {
        var (cm, f, u) = World();
        cm.QueryInterface<PositionComponent>(u)!.Position =
            new FixedVector3D(Fixed.FromInt(80), Fixed.Zero, Fixed.FromInt(80));
        var fd = cm.QueryInterface<FoundationComponent>(f)!;
        fd.Commit(cm);
        Assert.Null(cm.QueryInterface<UnitAIComponent>(u)!.CurrentOrder);
    }

    [Fact]
    public void Build_FirstTickCommits_Once()
    {
        var (cm, f, u) = World();
        var builder = cm.CreateEntity();
        cm.AddComponent(builder, new PositionComponent());
        var fd = cm.QueryInterface<FoundationComponent>(f)!;
        fd.Build(builder, 1f, 0.1f);
        Assert.True(fd.Committed);
        fd.Build(builder, 1f, 0.1f);   // 幂等
        Assert.True(fd.Committed);
    }
}
