using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using Xunit;

namespace ZeroAD.Sim.Tests;

// Tests for NetTurnManager command routing. Verifies that the Move/Gather/Attack commands
// route through UnitAI when present (so lockstep agrees with single-player SimBridge) and
// fall back to direct leaf-component manipulation for legacy entities without UnitAI.
//
// This is the determinism-critical seam: if NetTurnManager diverged from SimBridge in how it
// applies a command, the two clients would OOS. The previous bug (hardcoded "villager" in
// NetTurnManager vs SimBridge's real template) is exactly this class of divergence.
public sealed class NetCommandRoutingTests
{
    private static EntityId MakeUnitWithAI(ComponentManager cm, int player = 1)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new UnitAIComponent());
        cm.AddComponent(e, new IdentityComponent());
        if (player > 0)
            cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        return e;
    }

    private static EntityId MakeLegacyUnit(ComponentManager cm)
    {
        // No UnitAI — exercises the fallback path (direct leaf-component manipulation).
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new IdentityComponent());
        return e;
    }

    private static void RunOneTurn(NetTurnManager tm)
    {
        var players = new HashSet<uint> { 1 };
        // commandDelay=1 means a submitted command sits in slot 1; it only executes once it
        // rotates down to slot 0. Each AdvanceTurn shifts slots, so two advances flush the
        // command through. (IsTurnReady is irrelevant here — we force-advance for the test.)
        tm.AdvanceTurn(players);
        tm.AdvanceTurn(players);
    }

    [Fact]
    public void Move_RoutesThroughUnitAI_WhenPresent()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var tm = new NetTurnManager(cm, commandDelay: 1, localPlayerId: 1);
        var unit = MakeUnitWithAI(cm);
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;

        Assert.True(ai.IsIdle);

        tm.SubmitLocalCommand(NetCommand.Move(player: 1, unit.Value,
            Fixed.FromFloat(5f), Fixed.FromFloat(5f)));
        RunOneTurn(tm);
        // The command executed; UnitAI now has the order queued. Tick once to dispatch it
        // (Order handlers run on Tick, which owns the ComponentManager).
        ai.Tick(0.1f, cm);

        Assert.False(ai.IsIdle);
        Assert.StartsWith("INDIVIDUAL", ai.FsmStateName);
    }

    [Fact]
    public void Move_FallsBackToDirectUnitMotion_ForLegacyEntity()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var tm = new NetTurnManager(cm, commandDelay: 1, localPlayerId: 1);
        var unit = MakeLegacyUnit(cm);
        var motion = cm.QueryInterface<UnitMotion>(unit)!;

        Assert.False(motion.HasMoveTarget);

        tm.SubmitLocalCommand(NetCommand.Move(player: 1, unit.Value,
            Fixed.FromFloat(8f), Fixed.FromFloat(8f)));
        RunOneTurn(tm);

        // The fallback path drives UnitMotion directly.
        Assert.True(motion.HasMoveTarget);
    }

    [Fact]
    public void Attack_RoutesThroughUnitAI_WhenPresent()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var tm = new NetTurnManager(cm, commandDelay: 1, localPlayerId: 1);

        var attacker = MakeUnitWithAI(cm);
        cm.AddComponent(attacker, new AttackComponent { Damage = new DamageBlock(DamageType.Hack, 10) });
        var target = MakeUnitWithAI(cm, player: 2);
        cm.AddComponent(target, new HealthComponent());

        var ai = cm.QueryInterface<UnitAIComponent>(attacker)!;
        tm.SubmitLocalCommand(NetCommand.Attack(player: 1, attacker.Value, target.Value));
        RunOneTurn(tm);
        ai.Tick(0.1f, cm);

        Assert.StartsWith("INDIVIDUAL.COMBAT", ai.FsmStateName);
    }

    [Fact]
    public void Attack_FallsBackToDirectAttackComponent_ForLegacyEntity()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var tm = new NetTurnManager(cm, commandDelay: 1, localPlayerId: 1);

        var attacker = MakeLegacyUnit(cm);
        cm.AddComponent(attacker, new AttackComponent { Damage = new DamageBlock(DamageType.Hack, 10) });
        var target = cm.CreateEntity();
        cm.AddComponent(target, new HealthComponent());

        tm.SubmitLocalCommand(NetCommand.Attack(player: 1, attacker.Value, target.Value));
        RunOneTurn(tm);

        var attack = cm.QueryInterface<AttackComponent>(attacker)!;
        Assert.Equal(target, attack.Target);
    }
}
