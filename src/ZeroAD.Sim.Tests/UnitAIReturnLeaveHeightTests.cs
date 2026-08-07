using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using Xunit;

namespace ZeroAD.Sim.Tests;

// UnitAI 收尾:ReturnResource/DropAtNearestDropSite 订单链、LeaveFormation、Attack 高度差门。
public sealed class UnitAIReturnLeaveHeightTests
{
    private static ComponentManager SetupWorld()
    {
        var cm = new ComponentManager(rngSeed: 1);
        SimSystem.Init(cm);
        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent());
        cm.Players.AddPlayer(1, p1);
        var p2 = cm.CreateEntity();
        cm.AddComponent(p2, new PlayerComponent());
        cm.Players.AddPlayer(2, p2);
        var range = new RangeManager(cm, ZeroAD.Sim.Maths.Fixed.FromInt(256), ZeroAD.Sim.Maths.Fixed.FromInt(256));
        SimSystem.SetRangeManager(range);
        return cm;
    }

    private static EntityId MakeGatherer(ComponentManager cm, int owner, float x, float z, int carry = 8)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromFloat(x), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromFloat(z));
        cm.AddComponent(e, new UnitAIComponent());
        cm.AddComponent(e, new UnitMotion());
        var g = new ResourceGatherer();
        cm.AddComponent(e, g);
        g.CarryAmount = carry;
        g.CarryType = ResourceType.Wood;
        cm.AddComponent(e, new IdentityComponent { TemplateName = "units/athen/support_female_citizen", IsUnit = true });
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var p = new ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z);
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    private static EntityId MakeDropsite(ComponentManager cm, int owner, float x, float z)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromFloat(x), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromFloat(z));
        cm.AddComponent(e, new IdentityComponent { TemplateName = "structures/athen/storehouse", IsBuilding = true });
        cm.AddComponent(e, new ResourceDropsite());
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var p = new ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z);
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    [Fact]
    public void ReturnResource_InRange_DepositsImmediately()
    {
        var cm = SetupWorld();
        var worker = MakeGatherer(cm, 1, 10, 10, carry: 8);
        var ds = MakeDropsite(cm, 1, 11, 10);   // 1m 外(GatherRange 内)
        int wood0 = cm.Players.GetPlayerEntity(1)!.Wood;

        var ai = cm.QueryInterface<UnitAIComponent>(worker)!;
        ai.ReturnResource(ds);
        for (int i = 0; i < 3; i++) ai.Tick(0.1f, cm);

        var g = cm.QueryInterface<ResourceGatherer>(worker)!;
        Assert.Equal(0, g.CarryAmount);
        Assert.Equal(wood0 + 8, cm.Players.GetPlayerEntity(1)!.Wood);
        Assert.True(ai.IsIdle);
    }

    [Fact]
    public void ReturnResource_OutOfRange_ApproachesThenDeposits()
    {
        var cm = SetupWorld();
        var worker = MakeGatherer(cm, 1, 10, 10, carry: 8);
        var ds = MakeDropsite(cm, 1, 30, 10);   // 20m 外

        var ai = cm.QueryInterface<UnitAIComponent>(worker)!;
        ai.ReturnResource(ds);
        for (int i = 0; i < 2; i++) ai.Tick(0.1f, cm);
        Assert.Equal("INDIVIDUAL.RETURNRESOURCE.APPROACHING", ai.FsmStateName);

        // 搬到投放站旁 → 下拍交付收单。
        var pos = cm.QueryInterface<PositionComponent>(worker)!;
        pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromInt(29), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromInt(10));
        for (int i = 0; i < 3; i++) ai.Tick(0.1f, cm);
        Assert.Equal(0, cm.QueryInterface<ResourceGatherer>(worker)!.CarryAmount);
        Assert.True(ai.IsIdle);
    }

    [Fact]
    public void DropAtNearestDropSite_PicksNearest_AndReturns()
    {
        var cm = SetupWorld();
        var worker = MakeGatherer(cm, 1, 10, 10, carry: 8);
        MakeDropsite(cm, 1, 60, 10);            // 远站 50m
        var near = MakeDropsite(cm, 1, 12, 10); // 近站 2m

        var ai = cm.QueryInterface<UnitAIComponent>(worker)!;
        ai.DropAtNearestDropSite();
        for (int i = 0; i < 4; i++) ai.Tick(0.1f, cm);

        var g = cm.QueryInterface<ResourceGatherer>(worker)!;
        Assert.Equal(near, g.TargetDropsite);   // 选了近站并交付
        Assert.Equal(0, g.CarryAmount);
    }

    [Fact]
    public void ReturnResource_EmptyHands_FinishesImmediately()
    {
        var cm = SetupWorld();
        var worker = MakeGatherer(cm, 1, 10, 10, carry: 0);
        var ds = MakeDropsite(cm, 1, 12, 10);
        var ai = cm.QueryInterface<UnitAIComponent>(worker)!;
        ai.ReturnResource(ds);
        ai.Tick(0.1f, cm);
        Assert.True(ai.IsIdle);
    }

    [Fact]
    public void LeaveFormation_RemovesMemberFromController()
    {
        var cm = SetupWorld();
        // 控制器 + 两成员。
        var m1 = MakeGatherer(cm, 1, 0, 0);
        var m2 = MakeGatherer(cm, 1, 2, 0);
        var ctrl = cm.CreateEntity();
        cm.AddComponent(ctrl, new PositionComponent());
        var ai = new UnitAIComponent();
        cm.AddComponent(ctrl, ai);
        ai.InitAsFormationController();
        var form = new FormationComponent { RequiredMemberCount = 2 };
        cm.AddComponent(ctrl, form);
        form.SetMembers(cm, new System.Collections.Generic.List<EntityId> { m1, m2 });
        Assert.Equal(2, form.GetMemberCount());

        cm.QueryInterface<UnitAIComponent>(m1)!.LeaveFormation();
        for (int i = 0; i < 3; i++) cm.QueryInterface<UnitAIComponent>(m1)!.Tick(0.1f, cm);

        Assert.DoesNotContain(m1, form.Members);
        Assert.Null(cm.QueryInterface<UnitAIComponent>(m1)!.FormationController);
    }

    [Fact]
    public void AttackHeightGate_MeleeCannotReachElevatedTarget()
    {
        var cm = SetupWorld();
        var attacker = cm.CreateEntity();
        var ap = new PositionComponent();
        cm.AddComponent(attacker, ap);
        ap.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.Zero);
        var atk = new AttackComponent { Range = 3f };
        cm.AddComponent(attacker, atk);
        atk.Damage.Amounts[DamageType.Hack] = 10;
        cm.AddComponent(attacker, new OwnershipComponent { PlayerId = 1 });

        var target = cm.CreateEntity();
        var tp = new PositionComponent();
        cm.AddComponent(target, tp);
        // 目标在 10m 高的崖上(近战 3m 射程 → 永不可达)。
        tp.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromInt(2), ZeroAD.Sim.Maths.Fixed.FromInt(10), ZeroAD.Sim.Maths.Fixed.Zero);
        cm.AddComponent(target, new HealthComponent { Current = 50, Max = 50 });
        cm.AddComponent(target, new OwnershipComponent { PlayerId = 2 });
        var p1e = cm.Players.GetPlayerEntityId(1)!.Value;
        var p2e = cm.Players.GetPlayerEntityId(2)!.Value;
        cm.AddComponent(p1e, new DiplomacyComponent());
        cm.AddComponent(p2e, new DiplomacyComponent());
        cm.QueryInterface<DiplomacyComponent>(p1e)!.SetEnemy(2);

        Assert.False(atk.AttackTarget(cm, target));   // 高度差 10 > 射程 3 → 拒

        // 远程(射程 30)同目标可打。
        atk.Range = 30f;
        Assert.True(atk.AttackTarget(cm, target));
    }
}
