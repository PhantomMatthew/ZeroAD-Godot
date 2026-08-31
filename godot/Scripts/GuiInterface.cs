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

    // ── 外交(原版 GuiInterface.js GetSimulationState 的 players 段 + DiplomacyDialog.js 查询面)──

    /// <summary>立场(值与 DiplomacyComponent.Ally/Neutral/Enemy 对齐,面板可安全 (int) 转换
    /// 喂 CommandSetStance)。</summary>
    public enum Stance
    {
        Enemy = DiplomacyComponent.Enemy,     // -1
        Neutral = DiplomacyComponent.Neutral, // 0
        Ally = DiplomacyComponent.Ally,       // 1
    }

    /// <summary>外交表一行(DiplomacyDialog 每玩家行所需全部只读字段)。
    /// Tributeable = 本地余额能否负担该资源 100(500 = Shift;原版按 100 逐资源禁用)。</summary>
    public record DiplomacyRow(
        int PlayerId, string Civ, int Team,
        bool IsSelf, bool IsActive, bool IsDefeated, bool HasWon,
        Stance TheirStance,    // 对方对本地的立场(只读展示)
        Stance OurStance,      // 本地对其的立场(A/N/E 钮当前档)
        bool TeamLocked,
        IReadOnlyDictionary<ResourceType, bool> Tributeable);

    /// <summary>外交页整体快照:行表 + 本地资源(状态行)+ 本地活跃态(进贡禁用条件之一)。</summary>
    public record DiplomacyState(
        IReadOnlyList<DiplomacyRow> Rows,
        bool HasLocalPlayer,
        bool LocalActive,
        int LocalWood, int LocalFood, int LocalStone, int LocalMetal);

    public DiplomacyState GetDiplomacyState(int localPlayerId)
    {
        var localEnt = _cm.GetPlayerEntityId(localPlayerId);
        var localDip = localEnt.HasValue ? _cm.QueryInterface<DiplomacyComponent>(localEnt.Value) : null;
        var local = _cm.GetPlayerEntity(localPlayerId);

        var rows = new List<DiplomacyRow>();
        foreach (int pid in _cm.Players.GetNonGaiaPlayerIds())
        {
            var other = _cm.GetPlayerEntity(pid);
            if (other == null) continue;
            var otherEnt = _cm.GetPlayerEntityId(pid);
            var otherDip = otherEnt.HasValue ? _cm.QueryInterface<DiplomacyComponent>(otherEnt.Value) : null;
            rows.Add(new DiplomacyRow(
                PlayerId: pid,
                Civ: other.Civ,
                Team: other.Team,
                IsSelf: pid == localPlayerId,
                IsActive: other.IsActive(),
                IsDefeated: other.IsDefeated(),
                HasWon: other.HasWon(),
                TheirStance: (Stance)(otherDip?.GetStance(localPlayerId) ?? DiplomacyComponent.Neutral),
                OurStance: (Stance)(localDip?.GetStance(pid) ?? DiplomacyComponent.Neutral),
                TeamLocked: localDip?.IsTeamLocked() ?? false,
                Tributeable: new Dictionary<ResourceType, bool>
                {
                    [ResourceType.Food] = local?.CanAfford(ResourceType.Food, 100) ?? false,
                    [ResourceType.Wood] = local?.CanAfford(ResourceType.Wood, 100) ?? false,
                    [ResourceType.Stone] = local?.CanAfford(ResourceType.Stone, 100) ?? false,
                    [ResourceType.Metal] = local?.CanAfford(ResourceType.Metal, 100) ?? false,
                }));
        }
        return new DiplomacyState(rows, local != null, local?.IsActive() ?? false,
            local?.Wood ?? 0, local?.Food ?? 0, local?.Stone ?? 0, local?.Metal ?? 0);
    }

    // ── 玩家花名册(原版 GetSimulationState 的 players 段;Match Settings 页只读摘要)──

    /// <summary>玩家花名册一行(名字色由面板自取;人口/状态为运行时值)。</summary>
    public record PlayerRosterRow(
        int PlayerId, string Civ, int Team,
        bool IsActive, bool IsDefeated, bool HasWon,
        int PopUsed, int PopulationLimit);

    public List<PlayerRosterRow> GetPlayerRoster()
    {
        var rows = new List<PlayerRosterRow>();
        foreach (int pid in _cm.Players.GetNonGaiaPlayerIds())
        {
            var p = _cm.GetPlayerEntity(pid);
            if (p == null) continue;
            rows.Add(new PlayerRosterRow(pid, p.Civ, p.Team,
                p.IsActive(), p.IsDefeated(), p.HasWon(), p.PopUsed, p.PopulationLimit));
        }
        return rows;
    }

    // ── 易物(原版 Barter.js 的 GUI 查询面:可易物性 + 价签估算)──

    /// <summary>易物报价快照:本地有无市场 + 按当前漂移价的估算换得量(100/500 两档)。</summary>
    public record BarterQuote(bool CanBarter, int Gain100, int Gain500);

    public BarterQuote GetBarterQuote(int playerId, ResourceType sell, ResourceType buy)
    {
        var local = _cm.GetPlayerEntity(playerId);
        bool canBarter = local != null && local.CanBarter(_cm, playerId);
        int Gain(int amount) => (int)System.Math.Round(
            (double)BarterSystem.SellPrice(sell) / BarterSystem.BuyPrice(buy) * amount);
        return new BarterQuote(canBarter, Gain(100), Gain(500));
    }

    private ComponentManager cm() => _cm;
}
