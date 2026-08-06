using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using Xunit;

namespace ZeroAD.Sim.Tests;

// 编队作战子树(FORMATIONCONTROLLER.COMBAT / MEMBER / WALKINGANDFIGHTING;
// 原版 UnitAI.js 同名树 + FormationAttack.GetRange 聚合)。
public sealed class FormationCombatTests
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
        // 双方互相敌对(攻击合法性门;外交件挂在玩家实体上)。
        var p1e = cm.Players.GetPlayerEntityId(1)!.Value;
        var p2e = cm.Players.GetPlayerEntityId(2)!.Value;
        cm.AddComponent(p1e, new DiplomacyComponent());
        cm.AddComponent(p2e, new DiplomacyComponent());
        cm.QueryInterface<DiplomacyComponent>(p1e)!.SetEnemy(2);
        cm.QueryInterface<DiplomacyComponent>(p2e)!.SetEnemy(1);
        // 全图可见(测试聚焦 FSM 流转,不铺 Vision;LOS 门在别处测)。
        // reveal-all 作用于缓存可见性的重算 → 立即跑一拍 UpdateVisibilityData 生效。
        range.SetLosRevealAll(1, true);
        range.SetLosRevealAll(2, true);
        range.UpdateVisibilityData();
        return cm;
    }

    private static EntityId MakeSoldier(ComponentManager cm, int owner, float x, float z, float range = 3f)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromFloat(x), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromFloat(z));
        cm.AddComponent(e, new UnitMotion());
        var ai = new UnitAIComponent();
        cm.AddComponent(e, ai);
        cm.AddComponent(e, new IdentityComponent { Name = "S", IsUnit = true });
        cm.AddComponent(e, new HealthComponent { Current = 100, Max = 100 });
        cm.AddComponent(e, new AttackComponent { Range = range });
        cm.QueryInterface<AttackComponent>(e)!.Damage.Amounts[DamageType.Hack] = 10;
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var p = new ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z);
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    /// <summary>建编队:控制器 + 两成员(均 p1)。返回 (controller, formation, members)。</summary>
    private static (EntityId Ctrl, FormationComponent Form, EntityId M1, EntityId M2) MakeFormation(
        ComponentManager cm, float x, float z, bool canAttackAsFormation = false)
    {
        var m1 = MakeSoldier(cm, 1, x, z);
        var m2 = MakeSoldier(cm, 1, x + 2, z);

        var ctrl = cm.CreateEntity();
        var cpos = new PositionComponent();
        cm.AddComponent(ctrl, cpos);
        cpos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromFloat(x + 1), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromFloat(z));
        cm.AddComponent(ctrl, new UnitMotion());
        var ai = new UnitAIComponent();
        cm.AddComponent(ctrl, ai);
        ai.InitAsFormationController();
        cm.AddComponent(ctrl, new IdentityComponent { Name = "F" });
        cm.AddComponent(ctrl, new OwnershipComponent { PlayerId = 1 });
        var form = new FormationComponent
        {
            RequiredMemberCount = 2,
            CanAttackAsFormation = canAttackAsFormation,
        };
        cm.AddComponent(ctrl, form);
        cm.NotifyEntityCreated(ctrl);
        cm.NotifyOwnerChanged(ctrl, -1, 1);
        var p = new ZeroAD.Sim.Maths.FixedVector2D(cpos.Position.X, cpos.Position.Z);
        cm.NotifyPositionChanged(ctrl, p, p);

        form.SetMembers(cm, new System.Collections.Generic.List<EntityId> { m1, m2 });
        return (ctrl, form, m1, m2);
    }

    private static UnitAIComponent AI(ComponentManager cm, EntityId e) =>
        cm.QueryInterface<UnitAIComponent>(e)!;

    /// <summary>新实体入索引后重算缓存可见性(reveal-all 经此路径生效到逐实体缓存)。</summary>
    private static void RefreshLos() => SimSystem.Range?.UpdateVisibilityData();

    [Fact]
    public void GetAttackRange_AggregatesMemberRanges_PlusHalfDepth()
    {
        var cm = SetupWorld();
        var (_, form, _, _) = MakeFormation(cm, 0, 0, canAttackAsFormation: false);
        form.Depth = 4f;
        var enemy = MakeSoldier(cm, 2, 50, 0);

        // 不可整体作战:max = 成员最大射程(3) + Depth/2(2) = 5。
        var (min, max) = form.GetAttackRange(cm, enemy);
        Assert.Equal(0f, min);
        Assert.Equal(5f, max);
    }

    [Fact]
    public void GetAttackRange_CanAttackAsFormation_TakesMinOfMemberRanges()
    {
        var cm = SetupWorld();
        var (_, form, m1, m2) = MakeFormation(cm, 0, 0, canAttackAsFormation: true);
        form.Depth = 4f;
        // 一近一远:近战 3,弓箭 20 → 整体作战取最小 max(3)+2 = 5。
        cm.QueryInterface<AttackComponent>(m2)!.Range = 20f;
        var enemy = MakeSoldier(cm, 2, 50, 0);

        var (_, max) = form.GetAttackRange(cm, enemy);
        Assert.Equal(5f, max);
    }

    [Fact]
    public void GetClosestMemberToEntity_PicksNearest()
    {
        var cm = SetupWorld();
        var (_, form, m1, m2) = MakeFormation(cm, 0, 0);
        var probe = MakeSoldier(cm, 2, 10, 0);   // 距 m2(2,0) 比 m1(0,0) 近
        Assert.Equal(m2, form.GetClosestMemberToEntity(cm, probe));
    }

    [Fact]
    public void OrderAttack_OutOfRange_GoesApproaching_ThenMembersEngage()
    {
        var cm = SetupWorld();
        var (ctrl, form, m1, m2) = MakeFormation(cm, 0, 0, canAttackAsFormation: false);
        var enemy = MakeSoldier(cm, 2, 30, 0);   // 30m 外:射程 3+Depth/2 不及
        RefreshLos();

        AI(cm, ctrl).Attack(enemy);
        for (int i = 0; i < 3; i++) AI(cm, ctrl).Tick(0.1f, cm);
        Assert.Equal("FORMATIONCONTROLLER.COMBAT.APPROACHING", AI(cm, ctrl).FsmStateName);

        // 把整队搬到目标旁(控制器+成员),射程内 → 成员开打,控制器转 MEMBER(散开作战)。
        Teleport(cm, ctrl, 28, 0);
        Teleport(cm, m1, 28, 0);
        Teleport(cm, m2, 29, 0);
        for (int i = 0; i < 5; i++) AI(cm, ctrl).Tick(0.1f, cm);

        Assert.Equal("FORMATIONCONTROLLER.MEMBER", AI(cm, ctrl).FsmStateName);
        // 成员收到个体 Attack 订单(目标 = 敌实体)。
        Assert.Equal("Attack", AI(cm, m1).CurrentOrder?.Type);
        Assert.Equal("Attack", AI(cm, m2).CurrentOrder?.Type);
        // 控制器已移出世界(原版 MEMBER.enter MoveOutOfWorld)。
        Assert.False(cm.QueryInterface<PositionComponent>(ctrl)!.InWorld);
    }

    [Fact]
    public void OrderAttack_InRange_CanAttackAsFormation_GoesAttacking()
    {
        var cm = SetupWorld();
        var (ctrl, form, m1, m2) = MakeFormation(cm, 0, 0, canAttackAsFormation: true);
        form.Depth = 4f;
        var enemy = MakeSoldier(cm, 2, 4, 0);   // 4m:射程 3+2=5 内

        AI(cm, ctrl).Attack(enemy);
        for (int i = 0; i < 3; i++) AI(cm, ctrl).Tick(0.1f, cm);

        Assert.Equal("FORMATIONCONTROLLER.COMBAT.ATTACKING", AI(cm, ctrl).FsmStateName);
        // 成员接到攻击订单;控制器仍在世界(整体作战不移出)。
        Assert.Equal("Attack", AI(cm, m1).CurrentOrder?.Type);
        Assert.True(cm.QueryInterface<PositionComponent>(ctrl)!.InWorld);
    }

    [Fact]
    public void MemberState_AllMembersFinished_CompletesOrder_AndReturnsToWorld()
    {
        var cm = SetupWorld();
        var (ctrl, form, m1, m2) = MakeFormation(cm, 0, 0, canAttackAsFormation: false);
        var enemy = MakeSoldier(cm, 2, 4, 0);

        AI(cm, ctrl).Attack(enemy);
        for (int i = 0; i < 3; i++) AI(cm, ctrl).Tick(0.1f, cm);
        Assert.Equal("FORMATIONCONTROLLER.MEMBER", AI(cm, ctrl).FsmStateName);

        // 成员各自打完(FinishOrder 回报 SetFinishedEntity):模拟成员订单完成。
        // 直接调用成员 FinishOrder 不可达(private)——令其目标死亡后由成员 FSM 收工太慢,
        // 改为标记完成(等价于 FinishOrder 内的 SetFinishedEntity 调用)。
        form.SetFinishedEntity(cm, m1);
        form.SetFinishedEntity(cm, m2);
        for (int i = 0; i < 3; i++) AI(cm, ctrl).Tick(0.1f, cm);

        // 订单完成 → 回 IDLE;控制器回到世界。
        Assert.Equal("FORMATIONCONTROLLER.IDLE", AI(cm, ctrl).FsmStateName);
        Assert.True(cm.QueryInterface<PositionComponent>(ctrl)!.InWorld);
    }

    [Fact]
    public void MemberFinishOrder_MarksFinishedInFormation()
    {
        var cm = SetupWorld();
        var (ctrl, form, m1, m2) = MakeFormation(cm, 0, 0, canAttackAsFormation: false);
        var enemy = MakeSoldier(cm, 2, 4, 0);

        AI(cm, ctrl).Attack(enemy);
        for (int i = 0; i < 3; i++) AI(cm, ctrl).Tick(0.1f, cm);
        Assert.Equal("FORMATIONCONTROLLER.MEMBER", AI(cm, ctrl).FsmStateName);

        // 成员 1 的目标死亡 → 成员 FSM 收工(FinishOrder → SetFinishedEntity 回报)。
        cm.QueryInterface<HealthComponent>(enemy)!.Current = 0;
        for (int i = 0; i < 10; i++) AI(cm, m1).Tick(0.1f, cm);

        Assert.Contains(m1, form.FinishedEntities);
        Assert.DoesNotContain(m2, form.FinishedEntities);
        Assert.False(form.AreAllMembersFinished());
    }

    [Fact]
    public void OrderGuard_MembersGuard_AndFormationDisbands()
    {
        var cm = SetupWorld();
        var (ctrl, form, m1, m2) = MakeFormation(cm, 0, 0);
        var protectee = MakeSoldier(cm, 1, 10, 10);

        AI(cm, ctrl).Guard(protectee);
        AI(cm, ctrl).Tick(0.1f, cm);   // 单 tick:Order.Guard 派发即解散,控制器销毁

        // 成员收到 Guard 订单;编队解散(成员链接清除,控制器销毁)。
        Assert.Equal("Guard", AI(cm, m1).CurrentOrder?.Type);
        Assert.Equal("Guard", AI(cm, m2).CurrentOrder?.Type);
        Assert.Null(AI(cm, m1).FormationController);
        Assert.Empty(form.Members);
    }

    [Fact]
    public void CallMemberAttack_EnemyFormationTarget_ResolvesClosestMemberPerMember()
    {
        var cm = SetupWorld();
        // 我方编队(0,0)与敌编队(20,0)/(22,0)。
        var (ctrl, form, m1, m2) = MakeFormation(cm, 0, 0, canAttackAsFormation: false);
        var (enemyCtrl, enemyForm, e1, e2) = MakeEnemyFormation(cm, 20, 0);

        // 原版玩家点的是敌成员(控制器不可选):攻击 e1 → 解析为其控制器 →
        // 成员应各自打"离自己最近的敌成员"。先搬到射程内免走接近流程。
        Teleport(cm, ctrl, 16, 0);
        Teleport(cm, m1, 16, 0);
        Teleport(cm, m2, 17, 0);
        RefreshLos();
        AI(cm, ctrl).Attack(e1);
        for (int i = 0; i < 5; i++) AI(cm, ctrl).Tick(0.1f, cm);

        var t1 = AI(cm, m1).CurrentOrder?.Target;
        var t2 = AI(cm, m2).CurrentOrder?.Target;
        Assert.NotNull(t1);
        Assert.NotNull(t2);
        // 目标必须是敌编队的具体成员(不可打的控制器已被解析掉)。
        Assert.True(t1 == e1 || t1 == e2, $"m1 target should be an enemy member, got {t1}");
        Assert.True(t2 == e1 || t2 == e2, $"m2 target should be an enemy member, got {t2}");
    }

    private static (EntityId, FormationComponent, EntityId, EntityId) MakeEnemyFormation(
        ComponentManager cm, float x, float z)
    {
        var e1 = MakeSoldier(cm, 2, x, z);
        var e2 = MakeSoldier(cm, 2, x + 2, z);
        var ctrl = cm.CreateEntity();
        var cpos = new PositionComponent();
        cm.AddComponent(ctrl, cpos);
        cpos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromFloat(x + 1), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromFloat(z));
        cm.AddComponent(ctrl, new UnitMotion());
        var ai = new UnitAIComponent();
        cm.AddComponent(ctrl, ai);
        ai.InitAsFormationController();
        cm.AddComponent(ctrl, new IdentityComponent { Name = "EF" });
        cm.AddComponent(ctrl, new OwnershipComponent { PlayerId = 2 });
        var form = new FormationComponent { RequiredMemberCount = 2 };
        cm.AddComponent(ctrl, form);
        cm.NotifyEntityCreated(ctrl);
        cm.NotifyOwnerChanged(ctrl, -1, 2);
        var p = new ZeroAD.Sim.Maths.FixedVector2D(cpos.Position.X, cpos.Position.Z);
        cm.NotifyPositionChanged(ctrl, p, p);
        form.SetMembers(cm, new System.Collections.Generic.List<EntityId> { e1, e2 });
        return (ctrl, form, e1, e2);
    }

    private static void Teleport(ComponentManager cm, EntityId e, float x, float z)
    {
        var pos = cm.QueryInterface<PositionComponent>(e)!;
        var old = new ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z);
        pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromFloat(x), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromFloat(z));
        cm.NotifyPositionChanged(e, old,
            new ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z));
    }

    // --- 控制器巡逻 + CallMemberFunction 广播 ---

    [Fact]
    public void ControllerPatrol_Arrival_ThenPingPongBackToStart()
    {
        var cm = SetupWorld();
        var (ctrl, form, m1, m2) = MakeFormation(cm, 0, 0);

        AI(cm, ctrl).Patrol(new ZeroAD.Sim.Maths.FixedVector2D(
            ZeroAD.Sim.Maths.Fixed.FromInt(20), ZeroAD.Sim.Maths.Fixed.Zero));
        for (int i = 0; i < 3; i++) AI(cm, ctrl).Tick(0.1f, cm);
        Assert.Equal("FORMATIONCONTROLLER.PATROL.PATROLLING", AI(cm, ctrl).FsmStateName);

        // 到达路点(搬过去 + 停走)→ CHECKINGWAYPOINT。
        Teleport(cm, ctrl, 20, 0);
        cm.QueryInterface<UnitMotion>(ctrl)!.Stop();
        AI(cm, ctrl).Tick(0.1f, cm);
        Assert.Equal("FORMATIONCONTROLLER.PATROL.CHECKINGWAYPOINT", AI(cm, ctrl).FsmStateName);

        // 停留 1s(PatrolWaitTime)→ FinishOrder + 折返双单;下一拍派发回起点单。
        for (int i = 0; i < 12; i++) AI(cm, ctrl).Tick(0.1f, cm);
        Assert.Equal("FORMATIONCONTROLLER.PATROL.PATROLLING", AI(cm, ctrl).FsmStateName);
        var order = AI(cm, ctrl).CurrentOrder;
        Assert.NotNull(order);
        Assert.Equal("Patrol", order!.Type);
        Assert.Equal(1f, order.Position.X.ToFloat());   // 回起点(控制器锚定处 1,0)
    }

    [Fact]
    public void ControllerGather_InRange_BroadcastsToMembers_AndWaitsMember()
    {
        var cm = SetupWorld();
        var (ctrl, form, m1, m2) = MakeFormation(cm, 0, 0);
        var tree = MakeSoldier(cm, 0, 5, 0);   // 5m 内(广播半径 10m)

        AI(cm, ctrl).Gather(tree);
        for (int i = 0; i < 3; i++) AI(cm, ctrl).Tick(0.1f, cm);

        // 成员收到个体 Gather 订单;控制器转 MEMBER 等待(散开作业)。
        Assert.Equal("FORMATIONCONTROLLER.MEMBER", AI(cm, ctrl).FsmStateName);
        Assert.Equal("Gather", AI(cm, m1).CurrentOrder?.Type);
        Assert.Equal("Gather", AI(cm, m2).CurrentOrder?.Type);
    }

    [Fact]
    public void ControllerGather_OutOfRange_ApproachesFirst()
    {
        var cm = SetupWorld();
        var (ctrl, form, m1, m2) = MakeFormation(cm, 0, 0);
        var tree = MakeSoldier(cm, 0, 50, 0);  // 50m 外

        AI(cm, ctrl).Gather(tree);
        for (int i = 0; i < 3; i++) AI(cm, ctrl).Tick(0.1f, cm);
        Assert.Equal("FORMATIONCONTROLLER.CALLMEMBER.APPROACHING", AI(cm, ctrl).FsmStateName);
        // 未进射程前不广播。
        Assert.NotEqual("Gather", AI(cm, m1).CurrentOrder?.Type);

        // 整队到位 → 广播 + MEMBER。
        Teleport(cm, ctrl, 48, 0);
        for (int i = 0; i < 3; i++) AI(cm, ctrl).Tick(0.1f, cm);
        Assert.Equal("FORMATIONCONTROLLER.MEMBER", AI(cm, ctrl).FsmStateName);
        Assert.Equal("Gather", AI(cm, m1).CurrentOrder?.Type);
    }

    [Fact]
    public void ControllerPack_BroadcastsPackToMembers()
    {
        var cm = SetupWorld();
        var (ctrl, form, m1, m2) = MakeFormation(cm, 0, 0);

        AI(cm, ctrl).Pack();
        for (int i = 0; i < 3; i++) AI(cm, ctrl).Tick(0.1f, cm);

        Assert.Equal("FORMATIONCONTROLLER.MEMBER", AI(cm, ctrl).FsmStateName);
        Assert.Equal("Pack", AI(cm, m1).CurrentOrder?.Type);
        Assert.Equal("Pack", AI(cm, m2).CurrentOrder?.Type);
    }

    [Fact]
    public void ControllerGatherNearPosition_OutOfRange_ApproachesThenBroadcasts()
    {
        var cm = SetupWorld();
        var (ctrl, form, m1, m2) = MakeFormation(cm, 0, 0);
        var point = new ZeroAD.Sim.Maths.FixedVector2D(
            ZeroAD.Sim.Maths.Fixed.FromInt(60), ZeroAD.Sim.Maths.Fixed.Zero);

        AI(cm, ctrl).GatherNearPosition(point);
        for (int i = 0; i < 3; i++) AI(cm, ctrl).Tick(0.1f, cm);
        Assert.Equal("FORMATIONCONTROLLER.CALLMEMBER.APPROACHING", AI(cm, ctrl).FsmStateName);

        Teleport(cm, ctrl, 59, 0);
        for (int i = 0; i < 3; i++) AI(cm, ctrl).Tick(0.1f, cm);
        Assert.Equal("FORMATIONCONTROLLER.MEMBER", AI(cm, ctrl).FsmStateName);
        Assert.Equal("GatherNearPosition", AI(cm, m1).CurrentOrder?.Type);
    }
}
