using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// Resistance — damage reduction. Ported from
// binaries/data/mods/public/simulation/components/Resistance.js.
//
// Holds per-type resistance values (Hack/Pierce/Crush) and capture resistance. The damage
// formula lives in DamageBlock.WithResistanceApplied (mirrors AttackHelper.GetTotalAttackEffects):
//   finalDamage = raw * 0.9^resistance
// This component is the data source that DelayedDamage consults before handing damage to Health.
//
// State: Invulnerable (god-mode flag), per-type resistance map, capture resistance, and a set
// of currently-attacking entities (so we can notify them if the target becomes invalid). The
// original also distinguishes Entity vs Foundation resistance forms; P0 collapses to one form.

[Component("Resistance", "Resistance")]
public sealed class ResistanceComponent : ComponentBase, IComponentMessageHandler
{
    public bool Invulnerable;

    /// <summary>Per-type resistance values. Missing types are treated as 0 (no reduction).</summary>
    public Dictionary<DamageType, int> Resistances = new();

    /// <summary>Capture resistance (same 0.9^n formula as physical).</summary>
    public int CaptureResistance;

    /// <summary>Entities currently attacking this one. Tracked so Attack can be told to stop
    /// if the target dies or changes owner. Not serialized for hashing (regenerated on attack).</summary>
    public HashSet<EntityId> Attackers = new();

    protected override void OnInit()
    {
        // Defaults on field initializers (empty/zero) so EntityAssembler's object-initializer
        // assignments survive AddComponent.
    }

    public bool IsInvulnerable() => Invulnerable;

    public void AddAttacker(EntityId attacker) => Attackers.Add(attacker);
    public void RemoveAttacker(EntityId attacker) => Attackers.Remove(attacker);

    public int GetResistance(DamageType type) =>
        Resistances.TryGetValue(type, out var v) ? v : 0;

    /// <summary>The full resistance map (for GUI display / debugging).</summary>
    public IReadOnlyDictionary<DamageType, int> GetAllResistances() => Resistances;

    public override void Serialize(ISerializer s)
    {
        s.Bool("invuln", Invulnerable);
        // Fixed-type order (Hack,Pierce,Crush) for deterministic hashing.
        s.NumberI32("r_hack", GetResistance(DamageType.Hack));
        s.NumberI32("r_pierce", GetResistance(DamageType.Pierce));
        s.NumberI32("r_crush", GetResistance(DamageType.Crush));
        s.NumberI32("r_capture", CaptureResistance);
    }

    public override void Deserialize(IDeserializer d)
    {
        Invulnerable = d.Bool("invuln");
        Resistances[DamageType.Hack] = d.NumberI32("r_hack");
        Resistances[DamageType.Pierce] = d.NumberI32("r_pierce");
        Resistances[DamageType.Crush] = d.NumberI32("r_crush");
        CaptureResistance = d.NumberI32("r_capture");
    }

    public void HandleMessage(IMessage message) { }
}
