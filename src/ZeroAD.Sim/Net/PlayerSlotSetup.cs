namespace ZeroAD.Sim.Net;

/// <summary>
/// Kind of a lobby slot. Mirrors 0 A.D. gamesetup's PlayerSlot assignment. This is a
/// setup-time contract only — the runtime signal for an AI is <see cref="Components.AIComponent"/>
/// presence on the player entity. <see cref="Human"/> drives world construction differently
/// from <see cref="AI"/>: Human slots enter <c>NetTurnManager._expectedPlayers</c> (they submit
/// network batches); AI slots get an AIComponent (local <c>_aiBundles</c> channel, no network slot);
/// Closed slots are not instantiated at all.
/// </summary>
public enum PlayerSlotKind : byte
{
    /// <summary>No entity registered; skipped by InitWorld.</summary>
    Closed = 0,
    /// <summary>Networked peer; goes into NetTurnManager._expectedPlayers.</summary>
    Human = 1,
    /// <summary>Local-only AIComponent; never in _expectedPlayers.</summary>
    AI = 2,
}

/// <summary>
/// One row of the host-authoritative lobby slot table. Setup-time contract — NOT a
/// <see cref="Components.ComponentBase"/>. Mirrors <c>Content/ScenarioPlayerData</c>
/// (PlayerId/Civ/Team) plus <see cref="Kind"/>. PlayerId is 1-based and matches slot order
/// (slot i → PlayerId i+1); the wire codec (<see cref="PlayerSlotSetupCodec"/>) implies it
/// from position rather than transmitting it.
/// </summary>
public sealed record PlayerSlotSetup
{
    /// <summary>1-based player id; matches PlayerManager registration.</summary>
    public int PlayerId { get; init; }

    /// <summary>Slot role decided by the host in the lobby.</summary>
    public PlayerSlotKind Kind { get; init; } = PlayerSlotKind.Closed;

    /// <summary>Civilization code (e.g. "athen", "spart", "gaul").</summary>
    public string Civ { get; init; } = "athen";

    /// <summary>Team id: -1 = no team (FFA), 0+ = allied team (mutual allies via diplomacy seeding).</summary>
    public int Team { get; init; } = -1;

    /// <summary>AI 难度(原版 gamesetup AIDifficulty:0 Sandbox…5 VeryHard;
    /// 仅 Kind==AI 有意义;-1 = 未设 → 挂接时回落 Medium)。</summary>
    public int AIDifficulty { get; init; } = -1;
    /// <summary>AI 性格(原版 AIBehavior:aggressive/balanced/defensive/random;
    /// "" = random)。</summary>
    public string AIBehavior { get; init; } = "";
}
