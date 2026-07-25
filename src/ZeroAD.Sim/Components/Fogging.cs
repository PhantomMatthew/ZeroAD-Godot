using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>
/// Per-entity fog-of-war memory, ported from Fogging.js. Tracks, per player, whether the
/// entity has ever been seen (seen), and whether a mirage currently replaces it (miraged).
/// Task 3 ships the data + predicates consumed by RangeManager.ComputeLosVisibility;
/// Task 5 adds the lifecycle behavior (OnVisibilityChanged → LoadMirage, swap-back).
/// Masks are per-player bits (players 1..16) serialized as u32.
/// </summary>
[Component("Fogging", "Fogging")]
public sealed class FoggingComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>Activated once the entity has a real owner (mirrors Fogging.js OnOwnershipChanged).</summary>
    public bool Activated;
    public uint SeenMask;
    public uint MiragedMask;
    /// <summary>player → mirage entity standing in for this one.</summary>
    public EntityId?[] MirageOf = new EntityId?[LosGrid.MaxPlayers + 1];

    public bool WasSeen(int player) => (SeenMask & Bit(player)) != 0;
    public bool IsMiraged(int player) => (MiragedMask & Bit(player)) != 0;

    internal static uint Bit(int player) => 1u << (player - 1);

    /// <summary>Visibility transition hook, called by RangeManager.EvaluateVisibility in a
    /// deterministic order. Task 5 implements the lifecycle: VISIBLE → mark seen + clear
    /// mirage; FOGGED → LoadMirage (spawn frozen stand-in).</summary>
    public void OnVisibilityChanged(int player, LosVisibility vis, ComponentManager cm)
    {
        // Task 5: lifecycle behavior. Skeleton intentionally does nothing.
    }

    protected override void OnInit()
    {
        Activated = false;
        SeenMask = 0;
        MiragedMask = 0;
        MirageOf = new EntityId?[LosGrid.MaxPlayers + 1];
    }

    public override void Serialize(ISerializer s)
    {
        s.Bool("act", Activated);
        s.NumberU32("seen", SeenMask);
        s.NumberU32("mird", MiragedMask);
        for (int p = 1; p <= LosGrid.MaxPlayers; p++)
            s.NumberU32("mid", MirageOf[p]?.Value ?? 0);
    }

    public override void Deserialize(IDeserializer d)
    {
        Activated = d.Bool("act");
        SeenMask = d.NumberU32("seen");
        MiragedMask = d.NumberU32("mird");
        for (int p = 1; p <= LosGrid.MaxPlayers; p++)
        {
            uint v = d.NumberU32("mid");
            MirageOf[p] = v == 0 ? null : new EntityId(v);
        }
    }

    public void HandleMessage(IMessage message) { }
}

/// <summary>
/// Marks an entity as a mirage: a frozen stand-in for <see cref="Parent"/> in one player's
/// fog, ported from Mirage.js. Holds last-seen data for GUI queries. Task 5 adds swap-back
/// and self-destruct behavior; the visibility interlock lives in
/// RangeManager.ComputeLosVisibility (mirage is HIDDEN while its tile is visible).
/// </summary>
[Component("Mirage", "Mirage")]
public sealed class MirageComponent : ComponentBase, IComponentMessageHandler
{
    public EntityId Parent;
    public int Player;

    // Last-seen data for GUI queries (health bars, resource amounts).
    public int FrozenHealthCurrent;
    public int FrozenHealthMax;
    public int FrozenResourceAmount = -1; // -1 = not a resource

    protected override void OnInit()
    {
        Parent = default;
        Player = 0;
        FrozenHealthCurrent = 0;
        FrozenHealthMax = 0;
        FrozenResourceAmount = -1;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberU32("parent", Parent.Value);
        s.NumberI32("player", Player);
        s.NumberI32("fhc", FrozenHealthCurrent);
        s.NumberI32("fhm", FrozenHealthMax);
        s.NumberI32("fra", FrozenResourceAmount);
    }

    public override void Deserialize(IDeserializer d)
    {
        Parent = new EntityId(d.NumberU32("parent"));
        Player = d.NumberI32("player");
        FrozenHealthCurrent = d.NumberI32("fhc");
        FrozenHealthMax = d.NumberI32("fhm");
        FrozenResourceAmount = d.NumberI32("fra");
    }

    public void HandleMessage(IMessage message) { }
}
