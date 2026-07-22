using System;
using System.Collections.Generic;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim;

// PlayerManager — the player registry. Ported from
// binaries/data/mods/public/simulation/components/PlayerManager.js (system component).
//
// In the original, PlayerManager lives on SYSTEM_ENTITY and holds an array of player
// entity ids indexed by player id (index 0 = gaia). The C# rewrite already had this
// logic living on ComponentManager (_playerEntities + RegisterPlayer/GetPlayerEntity/
// ApplyOwnershipPopChange/RecomputePlayerPopBonus). This class formalises it as a
// standalone manager so the rule ownership is explicit and testable, while
// ComponentManager keeps thin forwarding methods for callers that already use them.
//
// Player id 0 is reserved for gaia (the neutral world owner). Player ids >= 1 are
// human/AI players. INVALID_PLAYER (-1) means "no owner".

/// <summary>Singleton player registry: player id → player entity id, plus the
/// pop/entity-limit accounting rules that react to ownership changes.</summary>
public sealed class PlayerManager
{
    // player id → player entity id. Index 0 is gaia. Mirrors PlayerManager.js playerEntities[].
    private readonly Dictionary<int, EntityId> _playerEntities = new();
    private readonly ComponentManager _cm;

    public PlayerManager(ComponentManager cm) => _cm = cm;

    /// <summary>Register a player entity under its player ID. Call once per player at world setup.</summary>
    public void AddPlayer(int playerId, EntityId entity) => _playerEntities[playerId] = entity;

    public EntityId? GetPlayerEntityId(int playerId) =>
        _playerEntities.TryGetValue(playerId, out var eid) ? eid : null;

    /// <summary>Resolve a player's PlayerComponent by player ID, or null if unregistered.</summary>
    public PlayerComponent? GetPlayerEntity(int playerId)
    {
        if (!_playerEntities.TryGetValue(playerId, out var eid)) return null;
        return _cm.QueryInterface<PlayerComponent>(eid);
    }

    /// <summary>All registered player ids (including gaia at 0).</summary>
    public IEnumerable<int> GetAllPlayerIds() => _playerEntities.Keys;

    /// <summary>Non-gaia player ids (id >= 1).</summary>
    public IEnumerable<int> GetNonGaiaPlayerIds()
    {
        foreach (var id in _playerEntities.Keys)
            if (id > 0) yield return id;
    }

    public int GetNumPlayers() => _playerEntities.Count;

    /// <summary>
    /// Adjust pop usage for a player when an entity's ownership changes. Mirrors how
    /// Player.js reacts to MT_OwnershipChanged (To = INVALID_PLAYER means death/loss).
    /// Pop is charged by CostComponent.PopulationCost.
    /// </summary>
    public void ApplyOwnershipPopChange(EntityId entity, int oldOwner, int newOwner)
    {
        var cost = _cm.QueryInterface<CostComponent>(entity);
        if (cost == null || cost.PopulationCost == 0) return;

        if (oldOwner > 0)
        {
            var p = GetPlayerEntity(oldOwner);
            if (p != null) p.PopUsed = Math.Max(0, p.PopUsed - cost.PopulationCost);
        }
        if (newOwner > 0)
        {
            var p = GetPlayerEntity(newOwner);
            if (p != null) p.PopUsed += cost.PopulationCost;
        }
    }

    /// <summary>
    /// Aggregate a player's PopulationComponent bonuses (House +10, etc.) into
    /// PlayerComponent.PopBonuses. Scans the player's owned entities — cheap enough for
    /// the handful of buildings a player has.
    /// </summary>
    public void RecomputePlayerPopBonus(int playerId)
    {
        var player = GetPlayerEntity(playerId);
        if (player == null) return;
        int total = 0;
        foreach (var entity in _cm.AllEntities)
        {
            var own = _cm.QueryInterface<OwnershipComponent>(entity);
            if (own == null || own.PlayerId != playerId) continue;
            var pop = _cm.QueryInterface<PopulationComponent>(entity);
            if (pop != null) total += pop.Bonus;
        }
        player.PopBonuses = total;
    }

    // --- Serialization (for OOS hashing / save-reload) ---
    // Players are serialized as (id, entityValue) pairs sorted by id for determinism.
    public void Serialize(ISerializer s)
    {
        var sorted = new SortedSet<int>(_playerEntities.Keys);
        s.NumberI32("playerCount", sorted.Count);
        foreach (var pid in sorted)
        {
            s.NumberI32("pid", pid);
            s.NumberU32("entity", _playerEntities[pid].Value);
        }
    }

    public void Deserialize(IDeserializer d)
    {
        _playerEntities.Clear();
        int count = d.NumberI32("playerCount");
        for (int i = 0; i < count; i++)
        {
            int pid = d.NumberI32("pid");
            uint entity = d.NumberU32("entity");
            _playerEntities[pid] = new EntityId(entity);
        }
    }
}
