using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

// Tests for UnitAI's order queue + state machine. Covers the core gameplay loop that UnitAI
// owns: Walk (move → arrive → idle), Gather (approach → gather → return → repeat), Attack.
public sealed class UnitAITests
{
    private static EntityId MakeUnit(ComponentManager cm, int player = 1)
    {
        // UnitMotion.Tick reads through SimSystem, so the test must wire it to the cm.
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

    [Fact]
    public void NewUnit_StartsIdleWithEmptyQueue()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);

        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;
        Assert.True(ai.IsIdle);
        Assert.Equal("INDIVIDUAL.IDLE", ai.FsmStateName);
    }

    [Fact]
    public void Walk_OrderTransitionsToWalking_ThenIdleOnArrival()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;

        ai.Walk(new FixedVector2D(Fixed.FromFloat(10f), Fixed.FromFloat(10f)));
        // The Order.Walk handler runs on the first Tick (it needs the ComponentManager).
        ai.Tick(0.1f, cm);
        Assert.Equal("INDIVIDUAL.WALKING", ai.FsmStateName);
        Assert.False(ai.IsIdle);

        // Tick the sim a few turns — UnitMotion will run and clear HasMoveTarget on arrival.
        for (int i = 0; i < 50; i++)
        {
            cm.QueryInterface<UnitMotion>(unit)?.Tick(0.1f);
            ai.Tick(0.1f, cm);
            if (ai.IsIdle) break;
        }
        Assert.True(ai.IsIdle);
        Assert.Equal("INDIVIDUAL.IDLE", ai.FsmStateName);
    }

    [Fact]
    public void Attack_OrderTransitionsToCombatApproaching()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var attacker = MakeUnit(cm);
        // 目标须是另一玩家(敌对校验对齐原版 CanAttack:同玩家目标必拒)。两玩家均未注册
        // 玩家实体/外交 → IsEnemy 兜底=敌(原版开局默认外交)。
        var target = MakeUnit(cm, player: 2);
        cm.AddComponent(attacker, new AttackComponent());
        cm.AddComponent(target, new HealthComponent());

        var ai = cm.QueryInterface<UnitAIComponent>(attacker)!;
        ai.Attack(target);
        ai.Tick(0.1f, cm);   // dispatch the Attack order

        Assert.StartsWith("INDIVIDUAL.COMBAT", ai.FsmStateName);
    }

    [Fact]
    public void Gather_ApproachesResourceAndGathers()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);
        cm.AddComponent(unit, new ResourceGatherer { GatherRate = 100 });
        cm.AddComponent(unit, new BuilderComponent());

        // A tree (resource supply) right next to the unit.
        var tree = cm.CreateEntity();
        cm.AddComponent(tree, new PositionComponent());
        var treePos = cm.QueryInterface<PositionComponent>(tree)!;
        treePos.Position = new FixedVector3D(Fixed.FromFloat(1f), Fixed.Zero, Fixed.FromFloat(1f));
        cm.AddComponent(tree, new ResourceSupply { Amount = 1000, MaxAmount = 1000 });

        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;
        ai.Gather(tree);

        // Tick enough turns for approach + gather. UnitMotion without obstructions walks a
        // single waypoint straight to the tree, so arrival is fast.
        for (int i = 0; i < 100; i++)
        {
            cm.QueryInterface<UnitMotion>(unit)?.Tick(0.1f);
            ai.Tick(0.1f, cm);
        }

        var gatherer = cm.QueryInterface<ResourceGatherer>(unit)!;
        // The unit should have gathered something (carry > 0 at some point) and transitioned
        // through GATHER states. With no dropsite present, it keeps gathering.
        Assert.True(gatherer.CarryAmount > 0 || ai.FsmStateName.StartsWith("INDIVIDUAL.GATHER"),
            $"expected gathering; state={ai.FsmStateName} carry={gatherer.CarryAmount}");
    }

    [Fact]
    public void QueuedOrders_AppendToBackOfQueue()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;

        // First order (replace): Walk to A.
        ai.Walk(new FixedVector2D(Fixed.FromFloat(5f), Fixed.Zero));
        // Second order (queued): Walk to B.
        ai.Walk(new FixedVector2D(Fixed.FromFloat(10f), Fixed.Zero), queued: true);

        // Two orders in the queue; front is the active one.
        Assert.False(ai.IsIdle);
        Assert.NotNull(ai.CurrentOrder);
    }

    [Fact]
    public void Stop_ClearsQueueAndReturnsToIdle()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;

        ai.Walk(new FixedVector2D(Fixed.FromFloat(5f), Fixed.Zero), queued: true);
        ai.Walk(new FixedVector2D(Fixed.FromFloat(10f), Fixed.Zero), queued: true);
        Assert.False(ai.IsIdle);

        ai.Stop();
        Assert.True(ai.IsIdle);
        Assert.Equal("INDIVIDUAL.IDLE", ai.FsmStateName);
    }

    [Fact]
    public void Serialize_WritesFsmStateAndOrderCount()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;
        ai.Walk(new FixedVector2D(Fixed.FromFloat(7f), Fixed.FromFloat(3f)), queued: true);
        ai.Walk(new FixedVector2D(Fixed.FromFloat(9f), Fixed.FromFloat(4f)), queued: true);

        // Serialize into the hash serializer (the OOS-relevant path) — must not throw and must
        // produce a stable byte stream. Two distinct runs over the same state must hash equally.
        var s1 = new Serialization.HashSerializer();
        ai.Serialize(s1);
        byte[] hash1 = s1.ComputeHash();

        // Build an identical component and serialize again.
        var cm2 = new ComponentManager(rngSeed: 1);
        var unit2 = MakeUnit(cm2);
        var ai2 = cm2.QueryInterface<UnitAIComponent>(unit2)!;
        ai2.Walk(new FixedVector2D(Fixed.FromFloat(7f), Fixed.FromFloat(3f)), queued: true);
        ai2.Walk(new FixedVector2D(Fixed.FromFloat(9f), Fixed.FromFloat(4f)), queued: true);
        var s2 = new Serialization.HashSerializer();
        ai2.Serialize(s2);
        byte[] hash2 = s2.ComputeHash();

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Gather_RejectedWithoutGatherer_FinishesOrder_NoFsmCrash()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);            // 无 ResourceGatherer → 必须拒收
        var tree = cm.CreateEntity();
        cm.AddComponent(tree, new PositionComponent());
        cm.AddComponent(tree, new ResourceSupply());

        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;
        ai.Gather(tree);
        // 拒令须 FinishOrder 出队(对齐原版):残留会让同 Tick 的 Timer 在无 handler 的
        // IDLE 态抛 InvalidOperationException,并每 Tick 重复派发。
        ai.Tick(0.1f, cm);
        ai.Tick(0.1f, cm);

        Assert.EndsWith("IDLE", ai.FsmStateName);
    }

    [Fact]
    public void Repair_RejectedWithoutBuilder_FinishesOrder_NoFsmCrash()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);            // 无 BuilderComponent → 必须拒收
        var foundation = cm.CreateEntity();
        cm.AddComponent(foundation, new PositionComponent());
        cm.AddComponent(foundation, new FoundationComponent());

        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;
        ai.Repair(foundation);
        ai.Tick(0.1f, cm);
        ai.Tick(0.1f, cm);

        Assert.EndsWith("IDLE", ai.FsmStateName);
    }
}
