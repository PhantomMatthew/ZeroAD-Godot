using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// P1 component stubs — minimal, compilable shims so UnitAI's full state machine compiles and
// the core gameplay loop (Walk/Gather/Attack/Repair) runs. Each carries the fields/methods
// UnitAI touches, with placeholder logic. Full behaviour is P1 (MS5) work.
//
// These mirror the original JS components at binaries/data/mods/public/simulation/components/.
// Each is marked [Component] so it auto-registers and serializes (state stays deterministic).

// --- Heal (Heal.js) — used by UnitAI's HEAL state + heal-range queries. ---
[Component("Heal", "Heal")]
public sealed class HealComponent : ComponentBase, IComponentMessageHandler
{
    public int HealAmount = 5;     // P1: read from template Heal/HP
    public float Range = 15f;
    public float Rate = 1f;
    public float Cooldown;
    public EntityId? Target;

    public void StartHealing(EntityId target) { Target = target; }   // P1 stub
    public void StopHealing() { Target = null; }                     // P1 stub

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("hp", HealAmount);
        s.NumberFixed("range", Maths.Fixed.FromFloat(Range));
        s.NumberFixed("rate", Maths.Fixed.FromFloat(Rate));
        s.NumberU32("target", Target?.Value ?? 0);
    }

    public override void Deserialize(IDeserializer d)
    {
        HealAmount = d.NumberI32("hp");
        Range = d.NumberFixed("range").ToFloat();
        Rate = d.NumberFixed("rate").ToFloat();
        uint t = d.NumberU32("target");
        Target = t != 0 ? new EntityId(t) : null;
    }

    public void HandleMessage(IMessage message) { }
}

// --- Trader (Trader.js) — used by UnitAI's TRADE state. ---
[Component("Trader", "Trader")]
public sealed class TraderComponent : ComponentBase, IComponentMessageHandler
{
    public EntityId? FirstMarket;
    public EntityId? SecondMarket;
    public ResourceType Goods = ResourceType.Metal;
    public int Gain;                 // P1: computed from market distance

    public void SetFirstMarket(EntityId market) { FirstMarket = market; }
    public void SetSecondMarket(EntityId market) { SecondMarket = market; }
    public void GainTradeGold() { Gain += 10; }   // P1 stub

    public override void Serialize(ISerializer s)
    {
        s.NumberU32("m1", FirstMarket?.Value ?? 0);
        s.NumberU32("m2", SecondMarket?.Value ?? 0);
        s.NumberI32("goods", (int)Goods);
        s.NumberI32("gain", Gain);
    }

    public override void Deserialize(IDeserializer d)
    {
        uint m1 = d.NumberU32("m1"); FirstMarket = m1 != 0 ? new EntityId(m1) : null;
        uint m2 = d.NumberU32("m2"); SecondMarket = m2 != 0 ? new EntityId(m2) : null;
        Goods = (ResourceType)d.NumberI32("goods");
        Gain = d.NumberI32("gain");
    }

    public void HandleMessage(IMessage message) { }
}

// --- Pack (Pack.js) — used by UnitAI's PACKING/UNPACKING states. ---
[Component("Pack", "Pack")]
public sealed class PackComponent : ComponentBase, IComponentMessageHandler
{
    public bool Packed;              // true = sieged/transport form; false = unpacked/active
    public float PackTime = 5f;      // seconds to pack/unpack
    public float Progress;           // 0..PackTime

    public void Pack() { }           // P1 stub — start packing
    public void Unpack() { }         // P1 stub — start unpacking

    public override void Serialize(ISerializer s)
    {
        s.Bool("packed", Packed);
        s.NumberFixed("time", Maths.Fixed.FromFloat(PackTime));
        s.NumberFixed("progress", Maths.Fixed.FromFloat(Progress));
    }

    public override void Deserialize(IDeserializer d)
    {
        Packed = d.Bool("packed");
        PackTime = d.NumberFixed("time").ToFloat();
        Progress = d.NumberFixed("progress").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}

// --- Garrisonable (the counterpart to GarrisonHolder) — used by UnitAI's GARRISON state. ---
[Component("Garrisonable", "Garrisonable")]
public sealed class GarrisonableComponent : ComponentBase, IComponentMessageHandler
{
    public EntityId? Holder;         // the building/unit currently holding this entity

    public bool Garrison(EntityId holder) { Holder = holder; return true; }   // P1 stub
    public bool Ungarrison() { Holder = null; return true; }                  // P1 stub

    public override void Serialize(ISerializer s) =>
        s.NumberU32("holder", Holder?.Value ?? 0);

    public override void Deserialize(IDeserializer d)
    {
        uint h = d.NumberU32("holder");
        Holder = h != 0 ? new EntityId(h) : null;
    }

    public void HandleMessage(IMessage message) { }
}

// --- TurretHolder / Turretable (TurretHolder.js) — used by UnitAI's PICKUP/Turret order. ---
[Component("Turretable", "Turretable")]
public sealed class TurretableComponent : ComponentBase, IComponentMessageHandler
{
    public EntityId? Holder;
    public bool Occupy(EntityId holder) { Holder = holder; return true; }  // P1 stub
    public void Leave() { Holder = null; }                                 // P1 stub

    public override void Serialize(ISerializer s) =>
        s.NumberU32("holder", Holder?.Value ?? 0);

    public override void Deserialize(IDeserializer d)
    {
        uint h = d.NumberU32("holder");
        Holder = h != 0 ? new EntityId(h) : null;
    }

    public void HandleMessage(IMessage message) { }
}

[Component("TurretHolder", "TurretHolder")]
public sealed class TurretHolderComponent : ComponentBase, IComponentMessageHandler
{
    public int Capacity = 5;                                         // P1 stub
    public readonly List<EntityId> Turreted = new();

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("cap", Capacity);
        s.NumberI32("count", Turreted.Count);
    }

    public override void Deserialize(IDeserializer d)
    {
        Capacity = d.NumberI32("cap");
        int count = d.NumberI32("count");
        Turreted.Clear();
        for (int i = 0; i < count; i++) Turreted.Add(default);
    }

    public void HandleMessage(IMessage message) { }
}

// --- TreasureCollector (TreasureCollector.js) — used by UnitAI's COLLECTTREASURE state. ---
[Component("TreasureCollector", "TreasureCollector")]
public sealed class TreasureCollectorComponent : ComponentBase, IComponentMessageHandler
{
    public EntityId? Treasure;
    public void Collect(EntityId treasure) { Treasure = treasure; }  // P1 stub

    public override void Serialize(ISerializer s) =>
        s.NumberU32("treasure", Treasure?.Value ?? 0);

    public override void Deserialize(IDeserializer d)
    {
        uint t = d.NumberU32("treasure");
        Treasure = t != 0 ? new EntityId(t) : null;
    }

    public void HandleMessage(IMessage message) { }
}

// --- Formation (Formation.js) — used by UnitAI's FORMATIONCONTROLLER/FORMATIONMEMBER states. ---
[Component("Formation", "Formation")]
public sealed class FormationComponent : ComponentBase, IComponentMessageHandler
{
    public readonly List<EntityId> Members = new();
    public string Shape = "square";   // P1: "square"/"triangle"/"line"/...

    public void AddMember(EntityId member) { Members.Add(member); }      // P1 stub
    public void RemoveMember(EntityId member) { Members.Remove(member); } // P1 stub
    public void SetFormationShape(string shape) { Shape = shape; }       // P1 stub

    public override void Serialize(ISerializer s)
    {
        s.StringASCII("shape", Shape);
        s.NumberI32("count", Members.Count);
        foreach (var m in Members) s.NumberU32("m", m.Value);
    }

    public override void Deserialize(IDeserializer d)
    {
        Shape = d.StringASCII("shape");
        int count = d.NumberI32("count");
        Members.Clear();
        for (int i = 0; i < count; i++) Members.Add(new EntityId(d.NumberU32("m")));
    }

    public void HandleMessage(IMessage message) { }
}
