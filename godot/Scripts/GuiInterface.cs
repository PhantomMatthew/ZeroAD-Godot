using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

// GuiInterface — the read-only sim query surface for the presentation layer.
//
// The original 0 A.D. has this as a system component (GuiInterface.js, ~30 ScriptCall methods).
// The C# port keeps it on the presentation side (SimBridge) rather than in the deterministic
// kernel: GUI query semantics don't belong in a headless-replayable kernel, and SimBridge is
// already the sole Godot↔sim seam (per AGENTS.md). This facade consolidates the scattered
// QueryInterface + entity-list iteration that HUD/Minimap/Main used to inline.
//
// DTOs are `record` types (per .claude/rules/csharp) — value-equal snapshots of sim state.

/// <summary>Read-only queries over the sim. Construct with the ComponentManager; all methods
/// return immutable snapshots suitable for HUD/Minimap/AI consumption.</summary>
public sealed class GuiInterface
{
    private readonly ComponentManager _cm;

    public GuiInterface(ComponentManager cm) => _cm = cm;

    /// <summary>Per-entity snapshot aggregating the components the GUI/AI reads for selection
    /// panels, health bars, and click classification. Null fields mean the entity lacks that
    /// component. Mirrors GuiInterface.js GetEntityState.</summary>
    public record EntityState(
        uint Id,
        string Name,
        int OwnerPlayerId,
        int HealthCurrent,
        int HealthMax,
        float HealthFraction,
        float PosX,
        float PosZ,
        bool IsUnit,
        bool IsBuilding,
        bool CanGather,
        bool CanAttack,
        bool IsDropsite,
        int CarryAmount,
        int ResourceAmount,
        string State);   // UnitAI FSM state name, or "" if no UnitAI

    public EntityState? GetEntityState(EntityId entity)
    {
        var id = cm().QueryInterface<IdentityComponent>(entity);
        var own = cm().QueryInterface<OwnershipComponent>(entity);
        var hp = cm().QueryInterface<HealthComponent>(entity);
        var pos = cm().QueryInterface<PositionComponent>(entity);
        var gatherer = cm().QueryInterface<ResourceGatherer>(entity);
        var attack = cm().QueryInterface<AttackComponent>(entity);
        var supply = cm().QueryInterface<ResourceSupply>(entity);
        var dropsite = cm().QueryInterface<ResourceDropsite>(entity);
        var ai = cm().QueryInterface<UnitAIComponent>(entity);

        // An entity with no identity/position is not a meaningful selectable — skip.
        if (id == null && pos == null) return null;

        int hpMax = hp?.Max ?? 0;
        return new EntityState(
            Id: entity.Value,
            Name: id?.Name ?? "Entity",
            OwnerPlayerId: own?.PlayerId ?? -1,
            HealthCurrent: hp?.Current ?? 0,
            HealthMax: hpMax,
            HealthFraction: hpMax > 0 ? (float)(hp?.Current ?? 0) / hpMax : 0f,
            PosX: pos?.Position.X.ToFloat() ?? 0f,
            PosZ: pos?.Position.Z.ToFloat() ?? 0f,
            IsUnit: id?.IsUnit ?? false,
            IsBuilding: id?.IsBuilding ?? false,
            CanGather: gatherer != null,
            CanAttack: attack != null,
            IsDropsite: dropsite != null,
            CarryAmount: gatherer?.CarryAmount ?? 0,
            ResourceAmount: supply?.Amount ?? 0,
            State: ai?.FsmStateName ?? "");
    }

    public List<EntityState> GetMultipleEntityStates(IEnumerable<EntityId> entities)
    {
        var result = new List<EntityState>();
        foreach (var e in entities)
        {
            var st = GetEntityState(e);
            if (st != null) result.Add(st);
        }
        return result;
    }

    /// <summary>Per-player resource/pop snapshot for the resource bar. Mirrors the fields
    /// HUD.cs reads off PlayerComponent every frame.</summary>
    public record PlayerStats(
        int PlayerId,
        int Food,
        int Wood,
        int Stone,
        int Metal,
        int PopUsed,
        int PopulationLimit);

    public PlayerStats? GetPlayerStats(int playerId)
    {
        var p = cm().GetPlayerEntity(playerId);
        if (p == null) return null;
        return new PlayerStats(
            PlayerId: playerId,
            Food: p.Food,
            Wood: p.Wood,
            Stone: p.Stone,
            Metal: p.Metal,
            PopUsed: p.PopUsed,
            PopulationLimit: p.PopulationLimit);
    }

    /// <summary>Count of each resource type currently being gathered by a player's units.
    /// Replaces the inline EntityNodes iteration in HUD._Process.</summary>
    public Dictionary<ResourceType, int> GetGathererCounts(int playerId)
    {
        var counts = new Dictionary<ResourceType, int>
        {
            [ResourceType.Wood] = 0,
            [ResourceType.Food] = 0,
            [ResourceType.Stone] = 0,
            [ResourceType.Metal] = 0,
        };
        foreach (var e in cm().AllEntities)
        {
            var own = cm().QueryInterface<OwnershipComponent>(e);
            if (own == null || own.PlayerId != playerId) continue;
            var g = cm().QueryInterface<ResourceGatherer>(e);
            if (g == null) continue;
            if (g.State == ResourceGatherer.GatherState.Gathering ||
                g.State == ResourceGatherer.GatherState.MovingToResource ||
                g.State == ResourceGatherer.GatherState.MovingToDropsite)
            {
                counts[g.CarryType]++;
            }
        }
        return counts;
    }

    /// <summary>All entities owned by a player. Delegates to RangeManager when available.</summary>
    public List<EntityId> GetPlayerEntities(int playerId)
    {
        var range = SimSystem.Range;
        if (range != null)
            return new List<EntityId>(range.GetEntitiesByPlayer(playerId));

        var result = new List<EntityId>();
        foreach (var e in cm().AllEntities)
        {
            var own = cm().QueryInterface<OwnershipComponent>(e);
            if (own != null && own.PlayerId == playerId) result.Add(e);
        }
        return result;
    }

    /// <summary>All non-gaia (player id >= 1) entities on the map.</summary>
    public List<EntityId> GetNonGaiaEntities()
    {
        var result = new List<EntityId>();
        foreach (var e in cm().AllEntities)
        {
            var own = cm().QueryInterface<OwnershipComponent>(e);
            if (own != null && own.PlayerId > 0) result.Add(e);
        }
        return result;
    }

    /// <summary>玩家贸易单位计数(原版 GuiInterface.js GetTraderNumber)。
    /// land/ship 按 IdentityComponent.HasClass("Ship") 分桶;"trading"=同时设了两个市场
    /// (TraderComponent.HasBothMarkets)。garrisoned-trade(商船驻军)本轮不计(延后),恒 0。</summary>
    public record TraderNumber(int LandTotal, int LandTrading, int LandGarrisoned, int ShipTotal, int ShipTrading);

    public TraderNumber GetTraderNumber(int playerId)
    {
        int landTotal = 0, landTrading = 0, shipTotal = 0, shipTrading = 0;
        foreach (var e in cm().AllEntities)
        {
            var own = cm().QueryInterface<OwnershipComponent>(e);
            if (own == null || own.PlayerId != playerId) continue;
            var trader = cm().QueryInterface<TraderComponent>(e);
            if (trader == null) continue;
            var id = cm().QueryInterface<IdentityComponent>(e);
            bool ship = id != null && id.HasClass("Ship");
            bool trading = trader.HasBothMarkets();
            if (ship) { shipTotal++; if (trading) shipTrading++; }
            else { landTotal++; if (trading) landTrading++; }
        }
        return new TraderNumber(landTotal, landTrading, 0, shipTotal, shipTrading);
    }

    /// <summary>玩家贸易品比例(4 资源百分比,和=100)。转发 PlayerComponent.GetTradingGoods。</summary>
    public Dictionary<ResourceType, int> GetTradingGoods(int playerId)
    {
        var p = cm().GetPlayerEntity(playerId);
        return p != null ? p.GetTradingGoods() : new Dictionary<ResourceType, int>();
    }

    private ComponentManager cm() => _cm;
}
