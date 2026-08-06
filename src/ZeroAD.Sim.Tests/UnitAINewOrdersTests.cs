using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// UnitAI 补全订单的 FSM 测试:WalkAndFight(攻击移动)/Patrol(往返)/Flee(逃跑)/Guard(护卫)。
/// 对照原版 UnitAI.js 的状态名与转移:WALKINGANDFIGHTING、PATROL.PATROLLING ⇄
/// CHECKINGWAYPOINT、FLEEING、GUARD.ESCORTING ⇄ GUARDING。
/// </summary>
public sealed class UnitAINewOrdersTests
{
    private static EntityId MakeUnit(ComponentManager cm, int player = 1)
    {
        Components.SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new IdentityComponent());
        cm.AddComponent(e, new UnitAIComponent());
        if (player > 0)
            cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        return e;
    }

    private static void SetPos(ComponentManager cm, EntityId e, float x, float z)
    {
        var pos = cm.QueryInterface<PositionComponent>(e)!;
        pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
    }

    private static void TickBoth(ComponentManager cm, EntityId e, float dt = 0.1f)
    {
        cm.QueryInterface<UnitMotion>(e)?.Tick(dt);
        cm.QueryInterface<UnitAIComponent>(e)?.Tick(dt, cm);
    }

    [Fact]
    public void WalkAndFight_EntersWafState_AndCompletesOnArrival()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;

        ai.WalkAndFight(new FixedVector2D(Fixed.FromFloat(8f), Fixed.FromFloat(8f)));
        ai.Tick(0.1f, cm);
        Assert.Equal("INDIVIDUAL.WALKINGANDFIGHTING", ai.FsmStateName);
        Assert.False(ai.IsIdle);

        for (int i = 0; i < 100 && !ai.IsIdle; i++)
            TickBoth(cm, unit);
        Assert.True(ai.IsIdle);
        Assert.Equal("INDIVIDUAL.IDLE", ai.FsmStateName);
    }

    [Fact]
    public void Patrol_PingPongs_BetweenStartAndTarget()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);
        SetPos(cm, unit, 5, 5);
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;

        var target = new FixedVector2D(Fixed.FromFloat(12f), Fixed.FromFloat(5f));
        ai.Patrol(target);
        ai.Tick(0.1f, cm);
        Assert.Equal("INDIVIDUAL.PATROL.PATROLLING", ai.FsmStateName);

        // 走到目标点 → 路点等待
        for (int i = 0; i < 100 && ai.FsmStateName == "INDIVIDUAL.PATROL.PATROLLING"; i++)
            TickBoth(cm, unit);
        Assert.Equal("INDIVIDUAL.PATROL.CHECKINGWAYPOINT", ai.FsmStateName);

        // 等满 PatrolWaitTime(1s)→ 折返(队首换成回起点的巡逻单,状态回 PATROLLING)
        for (int i = 0; i < 30 && ai.FsmStateName == "INDIVIDUAL.PATROL.CHECKINGWAYPOINT"; i++)
            TickBoth(cm, unit);
        Assert.Equal("INDIVIDUAL.PATROL.PATROLLING", ai.FsmStateName);
        // 折返后仍在巡逻链上(队列非空,Patrol 订单循环)
        Assert.False(ai.IsIdle);
        Assert.Equal("Patrol", ai.CurrentOrder?.Type);
    }

    [Fact]
    public void Flee_MovesAwayFromThreat_ThenCompletes()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);
        var threat = MakeUnit(cm, player: 2);
        SetPos(cm, unit, 10, 10);
        SetPos(cm, threat, 14, 10);   // 威胁在 +x 方向 → 应向 −x 逃

        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;
        ai.Flee(threat);
        ai.Tick(0.1f, cm);
        Assert.Equal("INDIVIDUAL.FLEEING", ai.FsmStateName);

        for (int i = 0; i < 100 && !ai.IsIdle; i++)
            TickBoth(cm, unit);
        Assert.True(ai.IsIdle);
        float x = cm.QueryInterface<PositionComponent>(unit)!.Position.X.ToFloat();
        Assert.True(x < 10f, $"fled unit should move away from threat (x={x} < 10)");
    }

    [Fact]
    public void Guard_EscortsWhenFar_GuardsWhenNear_DropsWhenTargetDead()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var guard = MakeUnit(cm, player: 1);
        var ward = MakeUnit(cm, player: 1);
        cm.AddComponent(ward, new HealthComponent { Current = 100, Max = 100 });
        SetPos(cm, guard, 0, 0);
        SetPos(cm, ward, 40, 0);   // 超出护卫半径 12

        var ai = cm.QueryInterface<UnitAIComponent>(guard)!;
        ai.Guard(ward);
        ai.Tick(0.1f, cm);
        Assert.Equal("INDIVIDUAL.GUARD.ESCORTING", ai.FsmStateName);

        // 护卫追近 → 进入 GUARDING
        for (int i = 0; i < 200 && ai.FsmStateName == "INDIVIDUAL.GUARD.ESCORTING"; i++)
            TickBoth(cm, guard);
        Assert.Equal("INDIVIDUAL.GUARD.GUARDING", ai.FsmStateName);

        // 目标死亡 → 护卫订单结束
        cm.QueryInterface<HealthComponent>(ward)!.Current = 0;
        for (int i = 0; i < 30 && !ai.IsIdle; i++)
            TickBoth(cm, guard);
        Assert.True(ai.IsIdle);
    }

    [Fact]
    public void Guard_RejectsEnemyTarget()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var guard = MakeUnit(cm, player: 1);
        var enemy = MakeUnit(cm, player: 2);
        cm.AddComponent(enemy, new HealthComponent { Current = 100, Max = 100 });

        var ai = cm.QueryInterface<UnitAIComponent>(guard)!;
        ai.Guard(enemy);
        ai.Tick(0.1f, cm);
        // 敌对目标 → 订单直接结算(原版 AddGuard 失败 → FinishOrder)
        Assert.True(ai.IsIdle);
    }
}
