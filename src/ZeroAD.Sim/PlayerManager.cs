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
    /// Players mutually allied with <paramref name="player"/> (A.IsAlly(B) AND B.IsAlly(A)),
    /// in ascending id order for determinism. Mirrors Diplomacy.js GetMutualAllies — the
    /// precondition for alliance shared LOS (Pathway B of VisionSharing). Returns empty when
    /// this player or any candidate lacks a DiplomacyComponent (e.g. pre-seeding or old saves).
    /// </summary>
    public List<int> GetMutualAllies(int player)
    {
        var result = new List<int>();
        if (!_playerEntities.TryGetValue(player, out var selfEntity)) return result;
        var selfDip = _cm.QueryInterface<DiplomacyComponent>(selfEntity);
        if (selfDip == null) return result;
        foreach (var other in GetNonGaiaPlayerIds())
        {
            if (other == player) continue;
            if (!selfDip.IsAlly(other)) continue;
            if (!_playerEntities.TryGetValue(other, out var otherEntity)) continue;
            var otherDip = _cm.QueryInterface<DiplomacyComponent>(otherEntity);
            if (otherDip != null && otherDip.IsAlly(player)) result.Add(other);
        }
        result.Sort();
        return result;
    }

    /// <summary>
    /// Combat-facing enemy test, mirroring Player.js IsEnemy's combat semantics:
    /// self → false; gaia (0) → true (the original's <c>IsEnemy(0) == true</c>); an
    /// unregistered self-player or a missing <see cref="DiplomacyComponent"/> (legacy
    /// worlds/tests) → true, matching the original's default stance of enemy-before-setup;
    /// otherwise the seeded <see cref="DiplomacyComponent"/> stance decides.
    /// Neutral stance = non-belligerent: IsEnemy is false, so neutral players can neither
    /// be attack-ordered nor auto-acquired by the AI (中立语义).
    /// </summary>
    public bool IsEnemy(int selfPlayer, int otherPlayer)
    {
        if (otherPlayer == selfPlayer) return false;
        if (otherPlayer <= 0) return true; // gaia / invalid owner is hostile to all
        if (!_playerEntities.TryGetValue(selfPlayer, out var selfEntity)) return true;
        var dip = _cm.QueryInterface<DiplomacyComponent>(selfEntity);
        if (dip == null) return true;
        return dip.IsEnemy(otherPlayer);
    }

    /// <summary>
    /// Seed every player's <see cref="DiplomacyComponent"/> from team assignments: same
    /// team (id &gt;= 0) → mutual ally; otherwise → mutual enemy. Idempotent — overwrites
    /// prior stances. Call once at world setup after all player entities are registered.
    /// </summary>
    public void SeedDiplomacyFromTeams(IReadOnlyDictionary<int, int> teamByPlayer)
    {
        var ids = new List<int>(GetNonGaiaPlayerIds());
        ids.Sort();
        foreach (var a in ids)
        {
            if (!_playerEntities.TryGetValue(a, out var aEntity)) continue;
            var dipA = _cm.QueryInterface<DiplomacyComponent>(aEntity);
            if (dipA == null) continue;
            int ta = teamByPlayer.TryGetValue(a, out var va) ? va : -1;
            // 同步写运行时 Team 字段(外交面板"Team"列显示用;原版 Player.js team)。
            var pa = _cm.QueryInterface<PlayerComponent>(aEntity);
            if (pa != null) pa.Team = ta;
            foreach (var b in ids)
            {
                if (b == a) continue;
                if (!_playerEntities.TryGetValue(b, out var bEntity)) continue;
                var dipB = _cm.QueryInterface<DiplomacyComponent>(bEntity);
                if (dipB == null) continue;
                int tb = teamByPlayer.TryGetValue(b, out var vb) ? vb : -1;
                if (ta >= 0 && ta == tb)
                {
                    dipA.SetAlly(b);
                    dipB.SetAlly(a);
                }
                else
                {
                    dipA.SetEnemy(b);
                    dipB.SetEnemy(a);
                }
            }
        }
    }

    /// <summary>
    /// Adjust pop usage for a player when an entity's ownership changes. Mirrors how
    /// Player.js reacts to MT_OwnershipChanged (To = INVALID_PLAYER means death/loss).
    /// Pop is charged by CostComponent.PopulationCost.
    /// </summary>
    public void ApplyOwnershipPopChange(EntityId entity, int oldOwner, int newOwner)
    {
        var pop = _cm.QueryInterface<PopulationComponent>(entity);
        var ownership = _cm.QueryInterface<OwnershipComponent>(entity);
        // 通知语义=应用语义:归属变更先落到组件,下面的重算读当前归属才正确
        // (调用方本就会改/已改归属时此处幂等)。实体已销毁(组件拆除)时
        // AllEntities 已不含它,重算天然排除——此时无法判断它是否带人口加成,保守重算。
        if (ownership != null && newOwner != oldOwner)
            ownership.PlayerId = newOwner;
        if (pop != null || ownership == null)
        {
            if (oldOwner > 0) RecomputePlayerPopBonus(oldOwner);
            if (newOwner > 0 && newOwner != oldOwner) RecomputePlayerPopBonus(newOwner);
        }

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
            // 每栋建筑的加成过修正值管线(科技如 "Population/Bonus" add/multiply)
            if (pop != null)
                total += (int)System.MathF.Round(
                    _cm.Modifiers.Apply("Population/Bonus", pop.Bonus, entity),
                    System.MidpointRounding.AwayFromZero);
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
