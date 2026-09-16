using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;

namespace ZeroAD.Sim.Tests;

/// <summary>地基开工挤出(原版 Foundation.Commit + UnitAI.LeaveFoundation):
/// 未提交地基不挡走;重叠单位走开后才 committed 并恢复阻挡。</summary>
public sealed class FoundationPushOutTests
{
    private static (ComponentManager cm, EntityId foundation, EntityId unit) World(
        bool deleteFlag = false, bool unitOnSite = true, int footprint = 8)
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
            Size0 = Fixed.FromInt(footprint),
            Size1 = Fixed.FromInt(footprint),
            Flags = ObstructionFlags.DefaultBlock,
            DisableBlockMovement = true,
            DisableBlockPathfinding = true,
        };
        cm.AddComponent(f, fobs);
        fobs.EnsureRegistered();
        var fd = new FoundationComponent();
        fd.Configure("structures/athen/house", 100f);
        cm.AddComponent(f, fd);

        var u = cm.CreateEntity();
        cm.AddComponent(u, new PositionComponent());
        cm.QueryInterface<PositionComponent>(u)!.Position = unitOnSite
            ? new FixedVector3D(Fixed.FromInt(50), Fixed.Zero, Fixed.FromInt(50))
            : new FixedVector3D(Fixed.FromInt(80), Fixed.Zero, Fixed.FromInt(80));
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
    public void Commit_PushesUnitOut_DoesNotCommitWhileBlocked()
    {
        var (cm, f, u) = World();
        var fd = cm.QueryInterface<FoundationComponent>(f)!;
        Assert.False(fd.Commit(cm));
        Assert.False(fd.Committed);
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;
        var order = ai.CurrentOrder;
        Assert.NotNull(order);
        Assert.Equal("Walk", order!.Type);
        var t = order.Position;
        float dx = t.X.ToFloat() - 50f, dz = t.Y.ToFloat() - 50f;
        Assert.True(dx * dx + dz * dz > 8f * 8f, "walk target must be outside the footprint");
    }

    [Fact]
    public void Commit_SucceedsAfterUnitLeaves()
    {
        var (cm, f, u) = World();
        var fd = cm.QueryInterface<FoundationComponent>(f)!;
        Assert.False(fd.Commit(cm));
        var pos = cm.QueryInterface<PositionComponent>(u)!;
        var old = new FixedVector2D(pos.Position.X, pos.Position.Z);
        pos.Position = new FixedVector3D(Fixed.FromInt(80), Fixed.Zero, Fixed.FromInt(80));
        cm.NotifyPositionChanged(u, old, new FixedVector2D(Fixed.FromInt(80), Fixed.FromInt(80)));
        Assert.True(fd.Commit(cm));
        Assert.True(fd.Committed);
        var flags = cm.QueryInterface<ObstructionComponent>(f)!.EffectiveFlags();
        Assert.True(flags.HasFlag(ObstructionFlags.BlockMovement));
        Assert.True(flags.HasFlag(ObstructionFlags.BlockPathfinding));
    }

    [Fact]
    public void Commit_DeletesDeleteUponConstruction()
    {
        var (cm, f, u) = World(deleteFlag: true);
        var fd = cm.QueryInterface<FoundationComponent>(f)!;
        Assert.True(fd.Commit(cm));
        Assert.Null(cm.QueryInterface<PositionComponent>(u));
        Assert.True(fd.Committed);
    }

    [Fact]
    public void Commit_EnemyUnitStays_AndBlocksCommit()
    {
        var (cm, f, u) = World();
        var own = cm.QueryInterface<OwnershipComponent>(u)!;
        own.PlayerId = 2;
        var fd = cm.QueryInterface<FoundationComponent>(f)!;
        Assert.False(fd.Commit(cm));
        Assert.False(fd.Committed);
        Assert.False(cm.QueryInterface<UnitAIComponent>(u)!.CurrentOrder?.Type == "Walk");
    }

    [Fact]
    public void Commit_UnitAlreadyOutside_Commits()
    {
        var (cm, f, u) = World(unitOnSite: false);
        var fd = cm.QueryInterface<FoundationComponent>(f)!;
        Assert.True(fd.Commit(cm));
        Assert.True(fd.Committed);
        Assert.Null(cm.QueryInterface<UnitAIComponent>(u)!.CurrentOrder);
    }

    [Fact]
    public void Build_FirstTickCommits_Once_WhenClear()
    {
        var (cm, f, _) = World(unitOnSite: false);
        var builder = cm.CreateEntity();
        cm.AddComponent(builder, new PositionComponent());
        var fd = cm.QueryInterface<FoundationComponent>(f)!;
        fd.Build(builder, 1f, 0.1f);
        Assert.True(fd.Committed);
        fd.Build(builder, 1f, 0.1f);
        Assert.True(fd.Committed);
        Assert.True(fd.Progress > 0f);
    }

    [Fact]
    public void Build_DoesNotProgressWhileUnitBlocks()
    {
        var (cm, f, _) = World();
        var builder = cm.CreateEntity();
        cm.AddComponent(builder, new PositionComponent());
        var fd = cm.QueryInterface<FoundationComponent>(f)!;
        fd.Build(builder, 1f, 0.1f);
        Assert.False(fd.Committed);
        Assert.Equal(0f, fd.Progress);
    }

    [Fact]
    public void SpawnFoundation_HasDisabledObstruction_ThenPushOut()
    {
        var templatesDir = RepoPaths.Resolve("binaries/data/mods/public/simulation/templates");
        if (templatesDir == null) return;

        var cm = new ComponentManager(1, templates: new TemplateLoader(templatesDir));
        SimSystem.Init(cm);
        SimSystem.SetObstructionManager(new ObstructionManager(128, 4f));
        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent());
        cm.Players.AddPlayer(1, p1);
        cm.QueryInterface<PlayerComponent>(p1)!.AddResource(ResourceType.Wood, 5000);
        cm.QueryInterface<PlayerComponent>(p1)!.AddResource(ResourceType.Food, 5000);
        cm.QueryInterface<PlayerComponent>(p1)!.AddResource(ResourceType.Stone, 5000);
        cm.QueryInterface<PlayerComponent>(p1)!.AddResource(ResourceType.Metal, 5000);

        var unit = cm.SpawnEntity("units/spart/support_civilian", 30f, 10f, ownerPlayerId: 1);
        var exec = new SimCommandExecutor(cm);
        exec.Apply(new NetCommand(1, NetCommandType.Build, unit.Value,
            fp1: Fixed.FromFloat(30f).InternalValue, fp2: Fixed.FromFloat(10f).InternalValue,
            templateName: "structures/spart/house"));

        EntityId? foundation = null;
        foreach (var e in cm.AllEntities)
        {
            if (cm.QueryInterface<FoundationComponent>(e) != null)
                foundation = e;
        }
        Assert.True(foundation.HasValue);
        var fobs = cm.QueryInterface<ObstructionComponent>(foundation!.Value);
        Assert.NotNull(fobs);
        Assert.True(fobs!.DisableBlockMovement);
        Assert.True(fobs.DisableBlockPathfinding);

        Assert.False(cm.QueryInterface<FoundationComponent>(foundation.Value)!.Committed);
        var fd = cm.QueryInterface<FoundationComponent>(foundation.Value)!;
        Assert.False(fd.Commit(cm));
        var order = cm.QueryInterface<UnitAIComponent>(unit)!.CurrentOrder;
        Assert.Equal("Walk", order?.Type);
    }

    [Fact]
    public void BarracksSizedFoundation_BuilderProgressesFromEdge()
    {
        // 17×17 壳半宽 8.5m;旧工位判定 dist≤8 落在壳内,提交后走不到中心、进度永远 0。
        var (cm, f, u) = World(unitOnSite: false, footprint: 17);
        Assert.True(BuilderProgresses(cm, f, u));
        var p = cm.QueryInterface<PositionComponent>(u)!;
        float dx = p.Position.X.ToFloat() - 50f, dz = p.Position.Z.ToFloat() - 50f;
        Assert.True(dx * dx + dz * dz > 8f * 8f, "builder must work outside the 17×17 hull");
    }

    [Fact]
    public void BarracksSizedFoundation_OnSiteVillagerLeavesThenBuilds()
    {
        var (cm, f, u) = World(unitOnSite: true, footprint: 17);
        Assert.True(BuilderProgresses(cm, f, u));
    }

    private static bool BuilderProgresses(ComponentManager cm, EntityId f, EntityId u)
    {
        cm.AddComponent(u, new BuilderComponent());
        cm.QueryInterface<UnitAIComponent>(u)!.Repair(f);
        var fd = cm.QueryInterface<FoundationComponent>(f)!;
        var motion = cm.QueryInterface<UnitMotion>(u)!;
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;
        var builder = cm.QueryInterface<BuilderComponent>(u)!;
        for (int i = 0; i < 500; i++)
        {
            motion.Tick(0.1f);
            ai.Tick(0.1f, cm);
            builder.Tick(cm);
            if (fd.Progress > 0f) return true;
        }
        return false;
    }

    [Fact]
    public void AdjacentSecondHouse_WorkGoalIsOutsideFirstHouse()
    {
        // 第一座 11×11 在 (50,50) 已提交;第二座在 (63,50);工人在第一座北侧工位。
        // 外接圆工位会落进第一座壳内;矩形边缘工位必须在第一座 AABB 之外。
        var (cm, house1, u) = World(unitOnSite: false, footprint: 11);
        cm.QueryInterface<PositionComponent>(u)!.Position =
            new FixedVector3D(Fixed.FromInt(50), Fixed.Zero, Fixed.FromInt(58));
        cm.QueryInterface<FoundationComponent>(house1)!.Commit(cm);

        var house2 = cm.CreateEntity();
        cm.AddComponent(house2, new PositionComponent());
        cm.QueryInterface<PositionComponent>(house2)!.Position =
            new FixedVector3D(Fixed.FromInt(63), Fixed.Zero, Fixed.FromInt(50));
        cm.AddComponent(house2, new OwnershipComponent { PlayerId = 1 });
        var fobs = new ObstructionComponent
        {
            Type = ObstructionType.Static,
            Size0 = Fixed.FromInt(11),
            Size1 = Fixed.FromInt(11),
            Flags = ObstructionFlags.DefaultBlock,
            DisableBlockMovement = true,
            DisableBlockPathfinding = true,
        };
        cm.AddComponent(house2, fobs);
        fobs.EnsureRegistered();
        var fd = new FoundationComponent();
        cm.AddComponent(house2, fd);
        fd.Configure("structures/athen/house", 30f);

        cm.AddComponent(u, new BuilderComponent());
        Assert.True(BuilderComponent.TryWorkGoal(cm, u, house2, out var goal));
        float gx = goal.X.ToFloat(), gz = goal.Y.ToFloat();
        Assert.False(gx >= 44.5f && gx <= 55.5f && gz >= 44.5f && gz <= 55.5f,
            $"work goal ({gx:0.00},{gz:0.00}) must not sit inside the first house");
        Assert.False(BuilderComponent.InWorkRange(cm, u, house2));
    }
}
