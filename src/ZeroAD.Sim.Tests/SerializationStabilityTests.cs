using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;
using Xunit;

namespace ZeroAD.Sim.Tests;

// Hash-stability / OOS tests for the new P0-A components.
//
// The core determinism invariant: two simulations built from identical inputs must produce
// identical state hashes every turn (NetTurnManager checks this every 20 turns). Any
// component whose Serialize writes values in a non-deterministic order (e.g. an unsorted
// dictionary) silently breaks this. These tests build identical worlds twice and assert the
// hashes match — and that advancing both in lockstep keeps them matched.
public sealed class SerializationStabilityTests
{
    [Fact]
    public void ComputeStateHash_IsStableAcrossIdenticalWorlds()
    {
        byte[] hash1 = BuildWorldAndHash();
        byte[] hash2 = BuildWorldAndHash();
        Assert.Equal(hash1, hash2);
    }

    private static byte[] BuildWorldAndHash()
    {
        var cm = new ComponentManager(rngSeed: 42);
        SimSystem.Init(cm);

        // A player + a combat pair with resistance, exercising the new components.
        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent());
        cm.Players.AddPlayer(1, playerEntity);

        var attacker = cm.CreateEntity();
        cm.AddComponent(attacker, new PositionComponent());
        cm.AddComponent(attacker, new UnitMotion());
        cm.AddComponent(attacker, new UnitAIComponent());
        cm.AddComponent(attacker, new AttackComponent
        {
            Damage = new DamageBlock(DamageType.Hack, 30),
            Range = 3f,
            Rate = 1f
        });
        cm.AddComponent(attacker, new OwnershipComponent { PlayerId = 1 });

        var target = cm.CreateEntity();
        cm.AddComponent(target, new PositionComponent());
        cm.AddComponent(target, new HealthComponent());
        var res = new ResistanceComponent();
        res.Resistances[DamageType.Hack] = 2;
        res.Resistances[DamageType.Pierce] = 1;
        cm.AddComponent(target, res);

        return cm.ComputeStateHash();
    }

    [Fact]
    public void ComputeStateHash_StaysStableAfterTickProgression()
    {
        // Two identical worlds, advanced in lockstep, must hash equally at every turn.
        var (cm1, attacker1, target1) = BuildCombatWorld();
        var (cm2, attacker2, target2) = BuildCombatWorld();

        for (int turn = 0; turn < 10; turn++)
        {
            cm1.QueryInterface<UnitMotion>(attacker1)?.Tick(0.1f);
            cm1.QueryInterface<UnitAIComponent>(attacker1)?.Tick(0.1f, cm1);
            cm1.QueryInterface<AttackComponent>(attacker1)?.Tick(0.1f, cm1);
            cm1.DelayedDamage.TickPending(cm1);
            cm1.DelayedDamage.AdvanceTurn();

            cm2.QueryInterface<UnitMotion>(attacker2)?.Tick(0.1f);
            cm2.QueryInterface<UnitAIComponent>(attacker2)?.Tick(0.1f, cm2);
            cm2.QueryInterface<AttackComponent>(attacker2)?.Tick(0.1f, cm2);
            cm2.DelayedDamage.TickPending(cm2);
            cm2.DelayedDamage.AdvanceTurn();

            Assert.Equal(cm1.ComputeStateHash(), cm2.ComputeStateHash());
        }
    }

    private static (ComponentManager, EntityId attacker, EntityId target) BuildCombatWorld()
    {
        var cm = new ComponentManager(rngSeed: 7);
        SimSystem.Init(cm);

        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent());
        cm.Players.AddPlayer(1, playerEntity);

        var attacker = cm.CreateEntity();
        cm.AddComponent(attacker, new PositionComponent());
        cm.AddComponent(attacker, new UnitMotion());
        cm.AddComponent(attacker, new UnitAIComponent());
        cm.AddComponent(attacker, new AttackComponent
        {
            Damage = new DamageBlock(DamageType.Hack, 25),
            Range = 3f,
            Rate = 2f
        });
        cm.AddComponent(attacker, new OwnershipComponent { PlayerId = 1 });

        var target = cm.CreateEntity();
        var tpos = cm.QueryInterface<PositionComponent>(target) ?? new PositionComponent();
        if (cm.QueryInterface<PositionComponent>(target) == null)
            cm.AddComponent(target, tpos);
        tpos.Position = new FixedVector3D(Fixed.FromFloat(2f), Fixed.Zero, Fixed.Zero);
        cm.AddComponent(target, new HealthComponent());
        cm.AddComponent(target, new ResistanceComponent());

        return (cm, attacker, target);
    }

    [Fact]
    public void Resistance_SerializeProducesStableHash()
    {
        // A ResistanceComponent with multiple resistance types must hash stably regardless of
        // insertion order (the Serialize writes in fixed DamageType order, not dict order).
        byte[] h1 = HashResistance(insertHackFirst: true);
        byte[] h2 = HashResistance(insertHackFirst: false);
        Assert.Equal(h1, h2);
    }

    private static byte[] HashResistance(bool insertHackFirst)
    {
        var res = new ResistanceComponent();
        if (insertHackFirst)
        {
            res.Resistances[DamageType.Hack] = 3;
            res.Resistances[DamageType.Crush] = 1;
        }
        else
        {
            res.Resistances[DamageType.Crush] = 1;
            res.Resistances[DamageType.Hack] = 3;
        }
        var s = new HashSerializer();
        res.Serialize(s);
        return s.ComputeHash();
    }

    [Fact]
    public void WaterManager_SerializeProducesStableHash()
    {
        var w1 = new WaterManager();
        w1.SetWaterLevel(Fixed.FromFloat(4.2f));
        var s1 = new HashSerializer();
        w1.Serialize(s1);

        var w2 = new WaterManager();
        w2.SetWaterLevel(Fixed.FromFloat(4.2f));
        var s2 = new HashSerializer();
        w2.Serialize(s2);

        Assert.Equal(s1.ComputeHash(), s2.ComputeHash());
    }

    [Fact]
    public void DamageBlock_SerializeStableRegardlessOfInsertionOrder()
    {
        var d1 = new DamageBlock();
        d1.Amounts[DamageType.Hack] = 10;
        d1.Amounts[DamageType.Pierce] = 20;
        d1.Amounts[DamageType.Crush] = 5;
        d1.Capture = Fixed.FromInt(3);

        var d2 = new DamageBlock();
        d2.Amounts[DamageType.Crush] = 5;
        d2.Amounts[DamageType.Hack] = 10;
        d2.Amounts[DamageType.Pierce] = 20;
        d2.Capture = Fixed.FromInt(3);

        var s1 = new HashSerializer(); d1.Serialize(s1, "dmg");
        var s2 = new HashSerializer(); d2.Serialize(s2, "dmg");
        Assert.Equal(s1.ComputeHash(), s2.ComputeHash());
    }
}
