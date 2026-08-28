using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using Xunit;

namespace ZeroAD.Sim.Tests;

// Tests for the multi-type damage pipeline:
//   DamageBlock.WithResistanceApplied (the 0.9^resistance formula)
//   DelayedDamage scheduling + settlement through Resistance → Health
//   Health.TakeDamage(DamageBlock)
public sealed class DamageTests
{
    [Fact]
    public void Resistance_ZeroResistance_FullDamage()
    {
        var raw = new DamageBlock(DamageType.Hack, 100);
        var resisted = raw.WithResistanceApplied(new Dictionary<DamageType, int>(), captureResistance: 0);
        Assert.Equal(100, resisted.Get(DamageType.Hack));
    }

    [Fact]
    public void Resistance_EachPointReducesByTenPercent()
    {
        var raw = new DamageBlock(DamageType.Hack, 100);
        // resistance 1 → 90%, resistance 2 → 81%.
        var r1 = raw.WithResistanceApplied(
            new Dictionary<DamageType, int> { [DamageType.Hack] = 1 }, captureResistance: 0);
        Assert.Equal(90, r1.Get(DamageType.Hack));

        var r2 = raw.WithResistanceApplied(
            new Dictionary<DamageType, int> { [DamageType.Hack] = 2 }, captureResistance: 0);
        Assert.Equal(81, r2.Get(DamageType.Hack));
    }

    [Fact]
    public void Resistance_AppliedPerTypeIndependently()
    {
        var raw = new DamageBlock();
        raw.Amounts[DamageType.Hack] = 100;
        raw.Amounts[DamageType.Pierce] = 100;
        raw.Amounts[DamageType.Crush] = 100;

        // Only Hack is resisted; Pierce/Crush pass through.
        var resisted = raw.WithResistanceApplied(
            new Dictionary<DamageType, int> { [DamageType.Hack] = 3 }, captureResistance: 0);

        Assert.Equal(73, resisted.Get(DamageType.Hack));  // 0.9^3 ≈ 0.729 → 73
        Assert.Equal(100, resisted.Get(DamageType.Pierce));
        Assert.Equal(100, resisted.Get(DamageType.Crush));
    }

    [Fact]
    public void Health_TakeDamageBlock_SumsAcrossTypes()
    {
        var health = new HealthComponent { Current = 200, Max = 200 };
        var dmg = new DamageBlock();
        dmg.Amounts[DamageType.Hack] = 30;
        dmg.Amounts[DamageType.Pierce] = 20;

        health.TakeDamage(dmg);
        Assert.Equal(150, health.Current); // 200 - (30+20)
    }

    [Fact]
    public void DelayedDamage_AppliesResistanceBeforeHealth()
    {
        var cm = new ComponentManager(rngSeed: 1);

        var attacker = cm.CreateEntity();
        var target = cm.CreateEntity();
        // AddComponent runs OnInit which sets Current/Max to 100; override afterward so the
        // test starts from a known HP (the clobber-on-Init convention means object-initializer
        // values on Health don't survive — same constraint EntityAssembler already lives with).
        cm.AddComponent(target, new HealthComponent());
        cm.QueryInterface<HealthComponent>(target)!.Current = 200;
        cm.QueryInterface<HealthComponent>(target)!.Max = 200;
        // Target resists Hack by 2 (81% damage kept).
        var res = new ResistanceComponent();
        res.Resistances[DamageType.Hack] = 2;
        cm.AddComponent(target, res);

        var raw = new DamageBlock(DamageType.Hack, 100);
        DelayedDamage.ScheduleHit(cm, attacker, target, raw, delaySeconds: 0f);

        // No delay → settles on next TickPending (same turn).
        cm.DelayedDamage.TickPending(cm);

        // 100 * 0.81 = 81 damage → 200 - 81 = 119.
        var health = cm.QueryInterface<HealthComponent>(target)!;
        Assert.Equal(119, health.Current);
    }

    [Fact]
    public void DelayedDamage_DelayedHitSettlesAfterAdvanceTurn()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var attacker = cm.CreateEntity();
        var target = cm.CreateEntity();
        cm.AddComponent(target, new HealthComponent { Current = 100, Max = 100 });

        DelayedDamage.ScheduleHit(cm, attacker, target, new DamageBlock(DamageType.Hack, 40), delaySeconds: 0.2f);

        // 0.0s: not yet.
        cm.DelayedDamage.TickPending(cm);
        Assert.Equal(100, cm.QueryInterface<HealthComponent>(target)!.Current);
        cm.DelayedDamage.AdvanceTurn();

        // 0.1s: not yet.
        cm.DelayedDamage.TickPending(cm);
        Assert.Equal(100, cm.QueryInterface<HealthComponent>(target)!.Current);
        cm.DelayedDamage.AdvanceTurn();

        // 0.2s: settles now.
        cm.DelayedDamage.TickPending(cm);
        Assert.Equal(60, cm.QueryInterface<HealthComponent>(target)!.Current);
    }

    [Fact]
    public void DelayedDamage_InvulnerableTarget_TakesNoDamage()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var attacker = cm.CreateEntity();
        var target = cm.CreateEntity();
        cm.AddComponent(target, new HealthComponent { Current = 100, Max = 100 });
        cm.AddComponent(target, new ResistanceComponent { Invulnerable = true });

        DelayedDamage.ScheduleHit(cm, attacker, target, new DamageBlock(DamageType.Hack, 999), delaySeconds: 0f);
        cm.DelayedDamage.TickPending(cm);

        Assert.Equal(100, cm.QueryInterface<HealthComponent>(target)!.Current);
    }
}
