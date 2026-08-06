using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Stance system tests (g_Stances port): the five GUI stances drive idle auto-acquire
/// (StanceIdleScan), attacked-response (OnAttacked via DelayedDamage), chase limits and
/// the defensive held-position anchor. Setup mirrors LosVisibilityTests: a real
/// RangeManager so LOS visibility gates run for real.
/// </summary>
public sealed class StanceTests
{
    private static (ComponentManager cm, RangeManager rm) NewWorld()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var rm = new RangeManager(cm, Fixed.FromInt(256), Fixed.FromInt(256));
        SimSystem.SetRangeManager(rm);
        // No DiplomacyComponent on the players: IsEnemy's legacy default treats
        // different players as enemies (mirrors CombatEnemySemanticsTests).
        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent());
        cm.Players.AddPlayer(1, p1);
        var p2 = cm.CreateEntity();
        cm.AddComponent(p2, new PlayerComponent());
        cm.Players.AddPlayer(2, p2);
        return (cm, rm);
    }

    private static EntityId SpawnSoldier(ComponentManager cm, RangeManager rm,
        int x, int z, int owner, int visionRange = 20, float attackRange = 4f)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new UnitAIComponent());
        cm.AddComponent(e, new IdentityComponent());
        cm.AddComponent(e, new HealthComponent { Current = 100, Max = 100 });
        cm.AddComponent(e, new AttackComponent { Damage = new DamageBlock(DamageType.Hack, 5), Range = attackRange });
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new VisionComponent());
        cm.QueryInterface<VisionComponent>(e)!.Range = Fixed.FromInt(visionRange);
        cm.NotifyEntityCreated(e);
        rm.RefreshFromComponents(e);
        var p = new FixedVector2D(Fixed.FromInt(x), Fixed.FromInt(z));
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    /// <summary>A civilian: no AttackComponent — flee-capable but cannot fight back.</summary>
    private static EntityId SpawnCivilian(ComponentManager cm, RangeManager rm, int x, int z, int owner)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new UnitAIComponent());
        cm.AddComponent(e, new IdentityComponent());
        cm.AddComponent(e, new HealthComponent { Current = 50, Max = 50 });
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new VisionComponent());
        cm.NotifyEntityCreated(e);
        rm.RefreshFromComponents(e);
        var p = new FixedVector2D(Fixed.FromInt(x), Fixed.FromInt(z));
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    private static void Move(ComponentManager cm, RangeManager rm, EntityId e, int fromX, int fromZ, int toX, int toZ)
    {
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromInt(toX), Fixed.Zero, Fixed.FromInt(toZ));
        cm.NotifyPositionChanged(e,
            new FixedVector2D(Fixed.FromInt(fromX), Fixed.FromInt(fromZ)),
            new FixedVector2D(Fixed.FromInt(toX), Fixed.FromInt(toZ)));
        rm.UpdateVisibilityData();
    }

    [Fact]
    public void SetStance_AcceptsKnown_RejectsUnknown()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1);
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;

        Assert.True(ai.SetStance("defensive", cm));
        Assert.Equal("defensive", ai.Stance);
        Assert.False(ai.SetStance("bogus", cm));
        Assert.Equal("defensive", ai.Stance);
    }

    [Fact]
    public void IdleScan_AggressiveUnit_AutoAttacksVisibleEnemy()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1);
        SpawnSoldier(cm, rm, 20, 10, owner: 2);   // inside vision (20), LOS visible
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;

        ai.Tick(1.0f, cm);   // one scan interval

        Assert.Equal("Attack", ai.CurrentOrder?.Type);
        Assert.Contains("COMBAT", ai.FsmStateName);
        Assert.False(ai.CurrentOrder!.Force);   // stance-acquired, not player-forced
    }

    [Fact]
    public void IdleScan_PassiveUnit_IgnoresVisibleEnemy()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1);
        SpawnSoldier(cm, rm, 20, 10, owner: 2);
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;
        ai.SetStance("passive", cm);

        ai.Tick(1.0f, cm);
        ai.Tick(1.0f, cm);

        Assert.True(ai.IsIdle);
    }

    [Fact]
    public void IdleScan_ExcludesGaiaOwnedEntities()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1);
        // Gaia animal: Health + owner 0. IsEnemy(player, 0) is true in combat semantics,
        // so this guards the explicit gaia exclusion in FindVisibleEnemies (no auto-hunting).
        var deer = cm.CreateEntity();
        cm.AddComponent(deer, new PositionComponent());
        cm.QueryInterface<PositionComponent>(deer)!.Position =
            new FixedVector3D(Fixed.FromInt(15), Fixed.Zero, Fixed.FromInt(10));
        cm.AddComponent(deer, new HealthComponent { Current = 20, Max = 20 });
        cm.AddComponent(deer, new OwnershipComponent { PlayerId = 0 });
        cm.NotifyEntityCreated(deer);
        rm.RefreshFromComponents(deer);
        cm.NotifyPositionChanged(deer, new FixedVector2D(Fixed.FromInt(15), Fixed.FromInt(10)),
            new FixedVector2D(Fixed.FromInt(15), Fixed.FromInt(10)));
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;

        ai.Tick(1.0f, cm);

        Assert.True(ai.IsIdle);
    }

    [Fact]
    public void OnAttacked_Aggressive_RetaliatesAgainstVisibleAttacker()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1);
        var attacker = SpawnSoldier(cm, rm, 16, 10, owner: 2);
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;

        ai.OnAttacked(attacker, cm);

        Assert.Equal("Attack", ai.CurrentOrder?.Type);
        Assert.Equal(attacker, ai.CurrentOrder!.Target);
    }

    [Fact]
    public void OnAttacked_Aggressive_DoesNotInterruptForcedOrder()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1);
        var attacker = SpawnSoldier(cm, rm, 16, 10, owner: 2);
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;
        ai.Walk(new FixedVector2D(Fixed.FromInt(100), Fixed.FromInt(100)));   // player-forced
        ai.Tick(0.1f, cm);   // dispatch the walk

        ai.OnAttacked(attacker, cm);

        Assert.Equal("Walk", ai.CurrentOrder?.Type);
    }

    [Fact]
    public void OnAttacked_Violent_RetaliatesDespiteForcedOrderAndInvisibility()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1);
        var far = SpawnSoldier(cm, rm, 200, 200, owner: 2);   // outside vision → not LOS-visible
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;
        ai.SetStance("violent", cm);
        ai.Walk(new FixedVector2D(Fixed.FromInt(50), Fixed.FromInt(50)));
        ai.Tick(0.1f, cm);

        ai.OnAttacked(far, cm);

        // targetAttackersAlways: responds to forced-order interruption AND invisible attackers.
        Assert.Equal("Attack", ai.CurrentOrder?.Type);
        Assert.Equal(far, ai.CurrentOrder!.Target);
    }

    [Fact]
    public void OnAttacked_Aggressive_IgnoresInvisibleAttacker()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1);
        var far = SpawnSoldier(cm, rm, 200, 200, owner: 2);
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;   // default aggressive

        ai.OnAttacked(far, cm);

        Assert.True(ai.IsIdle);
    }

    [Fact]
    public void OnAttacked_Passive_FleesAwayFromAttacker()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnCivilian(cm, rm, 50, 50, owner: 1);
        var attacker = SpawnSoldier(cm, rm, 45, 50, owner: 2);   // to the left
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;
        ai.SetStance("passive", cm);

        ai.OnAttacked(attacker, cm);

        // 真 Flee 订单(FLEEING 状态):Target=威胁,Force=false;奔跑目的地方向在
        // 订单派发时算(背离攻击者)。
        Assert.Equal("Flee", ai.CurrentOrder?.Type);
        Assert.False(ai.CurrentOrder!.Force);
        Assert.Equal(attacker, ai.CurrentOrder!.Target);

        // 派发 + 奔跑:单位须远离攻击者(攻击者 x=45,单位 x=50 → 逃向 +x)。
        float x0 = cm.QueryInterface<PositionComponent>(u)!.Position.X.ToFloat();
        for (int i = 0; i < 100 && !ai.IsIdle; i++)
        {
            cm.QueryInterface<UnitMotion>(u)?.Tick(0.1f);
            ai.Tick(0.1f, cm);
        }
        float x1 = cm.QueryInterface<PositionComponent>(u)!.Position.X.ToFloat();
        Assert.True(x1 > x0, $"fled unit should move away from attacker (x {x0} → {x1})");
    }

    [Fact]
    public void Standground_NeverChases_OutOfRangeOrderAborts()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1, attackRange: 4f);
        var attacker = SpawnSoldier(cm, rm, 18, 10, owner: 2);   // visible but out of attack range
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;
        ai.SetStance("standground", cm);

        ai.OnAttacked(attacker, cm);
        ai.Tick(0.1f, cm);   // dispatch → COMBAT.APPROACHING
        ai.Tick(0.1f, cm);   // Timer → standground gate aborts

        Assert.True(ai.IsIdle);
    }

    [Fact]
    public void Standground_AttacksInRangeTarget()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1, attackRange: 5f);
        var attacker = SpawnSoldier(cm, rm, 13, 10, owner: 2);   // within attack range
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;
        ai.SetStance("standground", cm);

        ai.OnAttacked(attacker, cm);
        ai.Tick(0.1f, cm);   // dispatch
        ai.Tick(0.1f, cm);   // in range → ATTACKING, no abort

        Assert.Equal("Attack", ai.CurrentOrder?.Type);
        Assert.Contains("COMBAT", ai.FsmStateName);
    }

    [Fact]
    public void Defensive_IgnoresAttackerBeyondHoldZone()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1, attackRange: 4f);
        var far = SpawnSoldier(cm, rm, 19, 10, owner: 2);   // visible (vision 20) but 9m from anchor
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;
        ai.SetStance("defensive", cm);   // anchors held position at (10,10)

        ai.OnAttacked(far, cm);

        Assert.True(ai.IsIdle);
    }

    [Fact]
    public void Defensive_AttacksInsideHoldZone_ThenReturnsToAnchor()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1, attackRange: 6f);
        var attacker = SpawnSoldier(cm, rm, 14, 10, owner: 2);   // 4m from anchor ≤ 6 range
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;
        ai.SetStance("defensive", cm);

        ai.OnAttacked(attacker, cm);
        Assert.Equal("Attack", ai.CurrentOrder?.Type);
    }

    [Fact]
    public void Defensive_WalksBackToAnchorWhenDrifted()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1);
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;
        ai.SetStance("defensive", cm);   // anchors held position at (10,10)

        // Drift away (separation/pushing in a real game; teleport here).
        Move(cm, rm, u, 10, 10, 60, 10);
        ai.Tick(1.0f, cm);   // scan: no enemy, 50m from anchor > 10m → walk-back

        Assert.Equal("Walk", ai.CurrentOrder?.Type);
        Assert.False(ai.CurrentOrder!.Force);
        Assert.Equal(10f, ai.CurrentOrder!.Position.X.ToFloat(), 1);
    }

    [Fact]
    public void ChaseAbort_Aggressive_StopsWhenTargetLeavesVision()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1, attackRange: 4f);
        var enemy = SpawnSoldier(cm, rm, 18, 10, owner: 2);   // visible, out of range → APPROACHING
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;
        ai.Tick(1.0f, cm);   // auto-acquire
        Assert.Equal("Attack", ai.CurrentOrder?.Type);

        Move(cm, rm, enemy, 18, 10, 200, 200);   // vanish from LOS
        ai.Tick(0.1f, cm);   // APPROACHING Timer → LOS gate aborts the chase

        Assert.True(ai.IsIdle);
    }

    [Fact]
    public void ChaseAbort_Violent_KeepsChasingBeyondVision()
    {
        var (cm, rm) = NewWorld();
        var u = SpawnSoldier(cm, rm, 10, 10, owner: 1, attackRange: 4f);
        var enemy = SpawnSoldier(cm, rm, 18, 10, owner: 2);
        rm.UpdateVisibilityData();
        var ai = cm.QueryInterface<UnitAIComponent>(u)!;
        ai.SetStance("violent", cm);
        ai.Tick(1.0f, cm);
        Assert.Equal("Attack", ai.CurrentOrder?.Type);

        Move(cm, rm, enemy, 18, 10, 200, 200);
        ai.Tick(0.1f, cm);

        Assert.Equal("Attack", ai.CurrentOrder?.Type);
    }
}
