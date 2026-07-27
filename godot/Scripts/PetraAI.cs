using Godot;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

/// <summary>Single-player AI brain (sandbox/dev mode). Lives in the Godot layer for now —
/// Phase 2 will move it into the kernel as a serializable ComponentBase. Determinism in
/// this phase: RNG comes from <see cref="ComponentManager.RNG"/> (seeded + hashed), the
/// think cadence is driven by sim turns (not wall-clock), and every decision routes
/// through <see cref="SimBridge.SubmitCommand"/> → the lockstep queue, exactly like a
/// human player. MP stays gated off until the brain state is serializable (Phase 2).</summary>
public sealed partial class PetraAI : Node
{
    private SimBridge _sim = null!;
    private EntityId _playerEntity;
    private int _aiPlayerId;

    private EconomyManager _economy = null!;
    private BuildManager _build = null!;
    private ResearchManager _research = null!;
    private DefenseManager _defense = null!;
    private AttackManager _attack = null!;

    // Turn-driven think cadence (replaces the old frame-timer). 5 turns ≈ 0.5s @ 10Hz,
    // matching the previous ThinkInterval. Gating on CurrentTurn makes the schedule
    // deterministic: same seed → same turn count → same think ticks across peers/reloads.
    private const int ThinkIntervalTurns = 5;
    private uint _lastThinkTurn = uint.MaxValue;

    // Rebuilt every think from the live entity graph (owner == _aiPlayerId). Kept as the
    // same list instances the managers reference, so manager wiring is unchanged while the
    // AI stays robust to sim reloads and entity death (gone entities simply drop out).
    private readonly List<EntityId> _ownedUnits = new();
    private readonly List<EntityId> _ownedBuildings = new();

    public void Init(SimBridge sim, EntityId playerEntity, int aiPlayerId)
    {
        _sim = sim;
        _playerEntity = playerEntity;
        _aiPlayerId = aiPlayerId;
        _economy = new EconomyManager(sim, (uint)aiPlayerId, _ownedUnits);
        _build = new BuildManager(sim, (uint)aiPlayerId, _ownedBuildings, _ownedUnits);
        _research = new ResearchManager(sim, (uint)aiPlayerId, playerEntity, _ownedBuildings);
        _defense = new DefenseManager(sim, (uint)aiPlayerId, _ownedUnits, _ownedBuildings);
        _attack = new AttackManager(sim, (uint)aiPlayerId, _ownedUnits);
    }

    /// <summary>Per-sim-turn entry point. Invoked from SimBridge's lockstep while-loop via
    /// the <see cref="SimBridge.AiThink"/> hook, once per advanced turn, AFTER TickSimulation
    /// (so the AI sees the freshly computed world state) and BEFORE AdvanceTurn (so its
    /// SubmitCommand calls land in the current turn's outbox, reaching the queue at
    /// currentTurn+commandDelay exactly like a human command). Throttled to once every
    /// <see cref="ThinkIntervalTurns"/> turns.</summary>
    public void TickAI()
    {
        if (_sim == null || _sim.Sim == null) return;

        uint turn = _sim.NetTurn.CurrentTurn;
        if (_lastThinkTurn != uint.MaxValue && turn - _lastThinkTurn < ThinkIntervalTurns) return;
        _lastThinkTurn = turn;

        RebuildOwned();

        var player = _sim.Sim.GetPlayerEntity(_aiPlayerId);
        if (player == null) return;

        var snapshot = new AISnapshot
        {
            Player = player,
            Villagers = _ownedUnits.Where(u => _sim.Sim.QueryInterface<ResourceGatherer>(u) != null).ToList(),
            Soldiers = _ownedUnits.Where(u => _sim.Sim.QueryInterface<AttackComponent>(u) != null).ToList(),
            Buildings = _ownedBuildings.ToList(),
            EnemyUnits = FindEnemyUnits(),
            EnemyBuildings = FindEnemyBuildings(),
        };

        _economy.Update(snapshot);
        _build.Update(snapshot);
        _research.Update(snapshot);
        _defense.Update(snapshot);
        _attack.Update(snapshot);
    }

    /// <summary>Rebuild the owned lists from the kernel entity graph. Dead entities are
    /// already absent from AllEntities, so this doubles as cleanup — no separate
    /// dead-prune pass needed. Deterministic: iteration follows AllEntities's stored order.</summary>
    private void RebuildOwned()
    {
        _ownedUnits.Clear();
        _ownedBuildings.Clear();
        foreach (var entity in _sim.Sim.AllEntities)
        {
            var owner = _sim.Sim.QueryInterface<OwnershipComponent>(entity);
            if (owner == null || owner.PlayerId != _aiPlayerId) continue;

            var identity = _sim.Sim.QueryInterface<IdentityComponent>(entity);
            if (identity == null) continue;
            if (identity.IsBuilding)
                _ownedBuildings.Add(entity);
            else if (identity.IsUnit)
                _ownedUnits.Add(entity);
        }
    }

    private List<EntityId> FindEnemyUnits()
    {
        var result = new List<EntityId>();
        foreach (var entity in _sim.Sim.AllEntities)
        {
            var owner = _sim.Sim.QueryInterface<OwnershipComponent>(entity);
            if (owner == null || owner.PlayerId == _aiPlayerId) continue;

            var identity = _sim.Sim.QueryInterface<IdentityComponent>(entity);
            var attack = _sim.Sim.QueryInterface<AttackComponent>(entity);
            if (identity != null && identity.IsUnit && attack != null)
                result.Add(entity);
        }
        return result;
    }

    private List<EntityId> FindEnemyBuildings()
    {
        var result = new List<EntityId>();
        foreach (var entity in _sim.Sim.AllEntities)
        {
            var owner = _sim.Sim.QueryInterface<OwnershipComponent>(entity);
            if (owner == null || owner.PlayerId == _aiPlayerId) continue;

            var identity = _sim.Sim.QueryInterface<IdentityComponent>(entity);
            if (identity != null && identity.IsBuilding)
                result.Add(entity);
        }
        return result;
    }
}

public sealed class AISnapshot
{
    public PlayerComponent Player = null!;
    public List<EntityId> Villagers = new();
    public List<EntityId> Soldiers = new();
    public List<EntityId> Buildings = new();
    public List<EntityId> EnemyUnits = new();
    public List<EntityId> EnemyBuildings = new();
}
