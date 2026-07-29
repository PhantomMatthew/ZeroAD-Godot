using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// P1 component stubs — compilable shims so UnitAI's full state machine compiles and the core
// gameplay loop (Walk/Gather/Attack/Repair) runs. Behavior is deferred (P1 / MS5), but the
// SERIALIZABLE fields are now aligned with the original JS components at
// binaries/data/mods/public/simulation/components/, so state round-trips save/load and OOS hash
// faithfully once behaviour lands. The 5 methods UnitAI calls (Garrison/StartHealing/
// SetFirstMarket/Pack/Unpack) keep their signatures; the others have no external references.
//
// Each is marked [Component] so it auto-registers; Serialize feeds the HashSerializer, so the
// fields below automatically participate in the OOS hash.

// --- Heal (Heal.js) — used by UnitAI's HEAL state + heal-range queries. ---
[Component("Heal", "Heal")]
public sealed class HealComponent : ComponentBase, IComponentMessageHandler
{
    public int HealAmount = 5;     // template Heal/HP (HP restored per tick)
    public float Range = 15f;      // template Heal/Range
    public float Rate = 1f;        // template Heal/Rate (interval, seconds, between ticks)
    // Template Heal/HealableClasses + Heal/UnhealableClasses — restrict valid heal targets.
    public readonly List<string> HealableClasses = new();
    public readonly List<string> UnhealableClasses = new();
    public EntityId? Target;        // runtime: entity currently being healed

    public void StartHealing(EntityId target) { Target = target; }   // P1 stub
    public void StopHealing() { Target = null; }                     // P1 stub

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("hp", HealAmount);
        s.NumberFixed("range", Maths.Fixed.FromFloat(Range));
        s.NumberFixed("rate", Maths.Fixed.FromFloat(Rate));
        s.NumberI32("healable_n", HealableClasses.Count);
        foreach (var cls in HealableClasses) s.StringASCII("healable", cls);
        s.NumberI32("unhealable_n", UnhealableClasses.Count);
        foreach (var cls in UnhealableClasses) s.StringASCII("unhealable", cls);
        s.NumberU32("target", Target?.Value ?? 0);
    }

    public override void Deserialize(IDeserializer d)
    {
        HealAmount = d.NumberI32("hp");
        Range = d.NumberFixed("range").ToFloat();
        Rate = d.NumberFixed("rate").ToFloat();
        HealableClasses.Clear();
        int hn = d.NumberI32("healable_n");
        for (int i = 0; i < hn; i++) HealableClasses.Add(d.StringASCII("healable"));
        UnhealableClasses.Clear();
        int un = d.NumberI32("unhealable_n");
        for (int i = 0; i < un; i++) UnhealableClasses.Add(d.StringASCII("unhealable"));
        uint t = d.NumberU32("target");
        Target = t != 0 ? new EntityId(t) : null;
    }

    public void HandleMessage(IMessage message) { }
}

// --- Trader (Trader.js) — used by UnitAI's TRADE state. ---
[Component("Trader", "Trader")]
public sealed class TraderComponent : ComponentBase, IComponentMessageHandler
{
    public EntityId? FirstMarket;   // this.markets[0]
    public EntityId? SecondMarket;  // this.markets[1]
    public int Index = -1;          // this.index — current target in the 2-market route (-1 = none)
    // this.goods = { type, amount: { traderGain, market1Gain, market2Gain } }. Split flat.
    public ResourceType GoodsType = ResourceType.Metal;
    public int TraderGain;
    public int Market1Gain;
    public int Market2Gain;

    public void SetFirstMarket(EntityId market) { FirstMarket = market; }
    public void SetSecondMarket(EntityId market) { SecondMarket = market; }
    public void GainTradeGold() { TraderGain += 10; }   // P1 stub

    public override void Serialize(ISerializer s)
    {
        s.NumberU32("m1", FirstMarket?.Value ?? 0);
        s.NumberU32("m2", SecondMarket?.Value ?? 0);
        s.NumberI32("index", Index);
        s.NumberI32("goodsType", (int)GoodsType);
        s.NumberI32("traderGain", TraderGain);
        s.NumberI32("market1Gain", Market1Gain);
        s.NumberI32("market2Gain", Market2Gain);
    }

    public override void Deserialize(IDeserializer d)
    {
        uint m1 = d.NumberU32("m1"); FirstMarket = m1 != 0 ? new EntityId(m1) : null;
        uint m2 = d.NumberU32("m2"); SecondMarket = m2 != 0 ? new EntityId(m2) : null;
        Index = d.NumberI32("index");
        GoodsType = (ResourceType)d.NumberI32("goodsType");
        TraderGain = d.NumberI32("traderGain");
        Market1Gain = d.NumberI32("market1Gain");
        Market2Gain = d.NumberI32("market2Gain");
    }

    public void HandleMessage(IMessage message) { }
}

// --- Pack (Pack.js) — used by UnitAI's PACKING/UNPACKING states. ---
[Component("Pack", "Pack")]
public sealed class PackComponent : ComponentBase, IComponentMessageHandler
{
    public bool Packed;              // true = sieged/transport form; false = unpacked/active
    public bool Packing;             // true while a pack/unpack is in progress (this.packing)
    public float PackTime = 5f;      // template Pack/Time — seconds to pack/unpack
    public float ElapsedTime;        // this.elapsedTime — progress 0..PackTime

    public void Pack() { Packing = true; }     // P1 stub — start packing
    public void Unpack() { Packing = true; }   // P1 stub — start unpacking

    public override void Serialize(ISerializer s)
    {
        s.Bool("packed", Packed);
        s.Bool("packing", Packing);
        s.NumberFixed("time", Maths.Fixed.FromFloat(PackTime));
        s.NumberFixed("elapsed", Maths.Fixed.FromFloat(ElapsedTime));
    }

    public override void Deserialize(IDeserializer d)
    {
        Packed = d.Bool("packed");
        Packing = d.Bool("packing");
        PackTime = d.NumberFixed("time").ToFloat();
        ElapsedTime = d.NumberFixed("elapsed").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}

// --- Garrisonable (Garrisonable.js) — used by UnitAI's GARRISON state. ---
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

// --- Turretable (Turretable.js) — a unit that can occupy a turret point on a holder. ---
[Component("Turretable", "Turretable")]
public sealed class TurretableComponent : ComponentBase, IComponentMessageHandler
{
    public EntityId? Holder;                  // the holder entity this unit is turreted on
    public bool Ejectable = true;             // template: whether the holder can auto-eject this unit
    public string TurretPointName = "";       // this.turretPoint — named slot on the holder ("", or point name)

    public bool Occupy(EntityId holder) { Holder = holder; return true; }                          // P1 stub
    public bool Occupy(EntityId holder, string pointName) { TurretPointName = pointName; return Occupy(holder); }
    public void Leave() { Holder = null; TurretPointName = ""; }                                   // P1 stub

    public override void Serialize(ISerializer s)
    {
        s.NumberU32("holder", Holder?.Value ?? 0);
        s.Bool("ejectable", Ejectable);
        s.StringASCII("turretPoint", TurretPointName);
    }

    public override void Deserialize(IDeserializer d)
    {
        uint h = d.NumberU32("holder");
        Holder = h != 0 ? new EntityId(h) : null;
        Ejectable = d.Bool("ejectable");
        TurretPointName = d.StringASCII("turretPoint");
    }

    public void HandleMessage(IMessage message) { }
}

// --- TurretHolder (TurretHolder.js) — holds occupant entities on named turret points.
// FIX: the P0 stub serialized only Capacity + Count, then Deserialized default EntityIds,
// silently losing every occupant. Modeled on the original: a list of named points, each
// carrying its occupant (or empty) + ejectable flag, all round-tripped. ---
[Component("TurretHolder", "TurretHolder")]
public sealed class TurretHolderComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>One named slot on the holder. <see cref="Entity"/> is the occupant, or null when empty.</summary>
    public sealed class TurretPoint
    {
        public string Name = "";
        public EntityId? Entity;
        public bool Ejectable = true;
    }

    public readonly List<TurretPoint> TurretPoints = new();
    public int Capacity => TurretPoints.Count;

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("count", TurretPoints.Count);
        foreach (var p in TurretPoints)
        {
            s.StringASCII("name", p.Name);
            s.NumberU32("entity", p.Entity?.Value ?? 0);
            s.Bool("ejectable", p.Ejectable);
        }
    }

    public override void Deserialize(IDeserializer d)
    {
        int count = d.NumberI32("count");
        TurretPoints.Clear();
        for (int i = 0; i < count; i++)
        {
            var p = new TurretPoint();
            p.Name = d.StringASCII("name");
            uint e = d.NumberU32("entity");
            p.Entity = e != 0 ? new EntityId(e) : null;
            p.Ejectable = d.Bool("ejectable");
            TurretPoints.Add(p);
        }
    }

    public void HandleMessage(IMessage message) { }
}

// --- TreasureCollector (TreasureCollector.js) — used by UnitAI's COLLECTTREASURE state. ---
[Component("TreasureCollector", "TreasureCollector")]
public sealed class TreasureCollectorComponent : ComponentBase, IComponentMessageHandler
{
    public float MaxDistance = 5f;   // template TreasureCollector/MaxDistance — collection radius
    public EntityId? Treasure;
    public void Collect(EntityId treasure) { Treasure = treasure; }  // P1 stub

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("maxDistance", Maths.Fixed.FromFloat(MaxDistance));
        s.NumberU32("treasure", Treasure?.Value ?? 0);
    }

    public override void Deserialize(IDeserializer d)
    {
        MaxDistance = d.NumberFixed("maxDistance").ToFloat();
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
    public string Shape = "square";                       // this.formationShape — square/triangle/line/...
    public readonly List<EntityId> FinishedEntities = new(); // members that reached the formation target
    public readonly List<EntityId> TwinFormations = new();   // nearby same-template formations (merge candidates)
    public int MaxRowsUsed;
    public int MaxColumnsUsed;
    public float Width;
    public float Depth;
    public float FormationSeparation;                     // template Formation/FormationSeparation
    public readonly List<string> SortingClasses = new();  // template Formation/SortingClasses
    // Deferred (recomputed each move in the original, not serialized): memberPositions, offsets,
    // currentPosition, lastOrderVariant.

    public void AddMember(EntityId member) { Members.Add(member); }      // P1 stub
    public void RemoveMember(EntityId member) { Members.Remove(member); } // P1 stub
    public void SetFormationShape(string shape) { Shape = shape; }       // P1 stub

    public override void Serialize(ISerializer s)
    {
        s.StringASCII("shape", Shape);
        s.NumberI32("maxRows", MaxRowsUsed);
        s.NumberI32("maxCols", MaxColumnsUsed);
        s.NumberFixed("width", Maths.Fixed.FromFloat(Width));
        s.NumberFixed("depth", Maths.Fixed.FromFloat(Depth));
        s.NumberFixed("separation", Maths.Fixed.FromFloat(FormationSeparation));
        SerializeEntityList(s, "members", Members);
        SerializeEntityList(s, "finished", FinishedEntities);
        SerializeEntityList(s, "twins", TwinFormations);
        s.NumberI32("sorting_n", SortingClasses.Count);
        foreach (var cls in SortingClasses) s.StringASCII("sorting", cls);
    }

    public override void Deserialize(IDeserializer d)
    {
        Shape = d.StringASCII("shape");
        MaxRowsUsed = d.NumberI32("maxRows");
        MaxColumnsUsed = d.NumberI32("maxCols");
        Width = d.NumberFixed("width").ToFloat();
        Depth = d.NumberFixed("depth").ToFloat();
        FormationSeparation = d.NumberFixed("separation").ToFloat();
        DeserializeEntityList(d, "members", Members);
        DeserializeEntityList(d, "finished", FinishedEntities);
        DeserializeEntityList(d, "twins", TwinFormations);
        SortingClasses.Clear();
        int sn = d.NumberI32("sorting_n");
        for (int i = 0; i < sn; i++) SortingClasses.Add(d.StringASCII("sorting"));
    }

    private static void SerializeEntityList(ISerializer s, string prefix, List<EntityId> list)
    {
        s.NumberI32(prefix + "_n", list.Count);
        foreach (var e in list) s.NumberU32(prefix, e.Value);
    }

    private static void DeserializeEntityList(IDeserializer d, string prefix, List<EntityId> list)
    {
        list.Clear();
        int n = d.NumberI32(prefix + "_n");
        for (int i = 0; i < n; i++) list.Add(new EntityId(d.NumberU32(prefix)));
    }

    public void HandleMessage(IMessage message) { }
}
