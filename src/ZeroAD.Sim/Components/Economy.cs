using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>
/// Cost data for an entity (resources + population + build time). Pure data; read by
/// training (EnqueueTraining) for upfront payment and by pop/entity-limit accounting on
/// ownership change. Mirrors <c>Cost.js</c> minus the ModifiersManager hooks (tech/aura
/// value modifications are not yet ported — costs are the raw template values).
/// </summary>
[Component("Cost", "Cost")]
public sealed class CostComponent : ComponentBase, IComponentMessageHandler
{
    // Defaults live on the field initializers (not OnInit) so callers using object-initializer
    // syntax — e.g. `new CostComponent { PopulationCost = 2 }` in EntityAssembler/EnqueueTraining
    // — keep their values. OnInit runs inside AddComponent AFTER construction and would clobber
    // them otherwise. This matches the convention used by ProductionQueue (no OnInit).
    public int WoodCost;
    public int FoodCost;
    public int StoneCost;
    public int MetalCost;
    public int PopulationCost = 1;
    public float BuildTime = 5f;

    protected override void OnInit() { }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("wood", WoodCost);
        s.NumberI32("food", FoodCost);
        s.NumberI32("stone", StoneCost);
        s.NumberI32("metal", MetalCost);
        s.NumberI32("pop", PopulationCost);
        s.NumberFixed("time", Maths.Fixed.FromFloat(BuildTime));
    }

    public override void Deserialize(IDeserializer d)
    {
        WoodCost = d.NumberI32("wood");
        FoodCost = d.NumberI32("food");
        StoneCost = d.NumberI32("stone");
        MetalCost = d.NumberI32("metal");
        PopulationCost = d.NumberI32("pop");
        BuildTime = d.NumberFixed("time").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}

/// <summary>
/// Population bonus granted by an entity (House +10, etc.). Pure data; the player's
/// <see cref="PlayerComponent.PopBonuses"/> aggregates these. Mirrors <c>Population.js</c>,
/// which is per-entity pop-bonus source rather than the counter itself (the counter lives
/// on PlayerComponent, mirroring how Player.js aggregates popUsed/popBonuses).
/// </summary>
[Component("Population", "Population")]
public sealed class PopulationComponent : ComponentBase, IComponentMessageHandler
{
    // Default on field initializer (not OnInit) so EntityAssembler's
    // `new PopulationComponent { Bonus = 10 }` keeps its value. See CostComponent for rationale.
    public int Bonus;

    protected override void OnInit() { }

    public override void Serialize(ISerializer s) => s.NumberI32("bonus", Bonus);
    public override void Deserialize(IDeserializer d) => Bonus = d.NumberI32("bonus");

    public void HandleMessage(IMessage message) { }
}

/// <summary>
/// Training category tag (Civilian/Hero/WarDog/...) used by <see cref="EntityLimitsComponent"/>
/// to match trained units against per-category caps. Mirrors <c>TrainingRestrictions.js</c>
/// (which has Serialize = null and no state beyond the template-derived category).
/// </summary>
[Component("TrainingRestrictions", "TrainingRestrictions")]
public sealed class TrainingRestrictionsComponent : ComponentBase, IComponentMessageHandler
{
    public string Category = "";

    protected override void OnInit() => Category = "";

    public override void Serialize(ISerializer s) => s.StringASCII("cat", Category);
    public override void Deserialize(IDeserializer d) => Category = d.StringASCII("cat");

    public void HandleMessage(IMessage message) { }
}

/// <summary>
/// Per-player category caps (e.g. at most 1 Hero, 5 WarDog). Attached to the player entity.
/// Mirrors <c>EntityLimits.js</c>: <c>Limits</c> is the cap table, <c>Counts</c> is the live
/// count keyed by TrainingRestrictions category. AllowedToTrain gates training before resources
/// are spent; ChangeCount is called on ownership changes. Dictionary serialization is sorted
/// by key for deterministic cross-platform hashing.
/// </summary>
[Component("EntityLimits", "EntityLimits")]
public sealed class EntityLimitsComponent : ComponentBase, IComponentMessageHandler
{
    public Dictionary<string, int> Limits = new();
    public Dictionary<string, int> Counts = new();

    protected override void OnInit() { }

    /// <summary>True if training <paramref name="count"/> units of <paramref name="category"/> stays within cap.</summary>
    public bool AllowedToTrain(string category, int count)
    {
        if (string.IsNullOrEmpty(category)) return true;
        if (!Limits.TryGetValue(category, out int limit)) return true;
        int current = Counts.TryGetValue(category, out int c) ? c : 0;
        return current + count <= limit;
    }

    public void ChangeCount(string category, int delta)
    {
        if (string.IsNullOrEmpty(category)) return;
        Counts.TryGetValue(category, out int current);
        Counts[category] = current + delta;
    }

    public override void Serialize(ISerializer s)
    {
        // Deterministic order: sort by key before writing so the OOS hash is stable across
        // platforms/runtimes with different Dictionary enumeration orders.
        var limitKeys = Limits.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        s.NumberI32("limitCount", limitKeys.Count);
        foreach (var k in limitKeys)
        {
            s.StringASCII("lk", k);
            s.NumberI32("lv", Limits[k]);
        }
        var countKeys = Counts.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        s.NumberI32("countCount", countKeys.Count);
        foreach (var k in countKeys)
        {
            s.StringASCII("ck", k);
            s.NumberI32("cv", Counts[k]);
        }
    }

    public override void Deserialize(IDeserializer d)
    {
        Limits.Clear();
        int lc = d.NumberI32("limitCount");
        for (int i = 0; i < lc; i++)
            Limits[d.StringASCII("lk")] = d.NumberI32("lv");
        Counts.Clear();
        int cc = d.NumberI32("countCount");
        for (int i = 0; i < cc; i++)
            Counts[d.StringASCII("ck")] = d.NumberI32("cv");
    }

    public void HandleMessage(IMessage message) { }
}
