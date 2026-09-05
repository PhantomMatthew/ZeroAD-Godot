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

    /// <summary>停战状态(原版 GetSimState().ceasefireActive/TimeRemaining;
    /// 外交面板倒计时读此)。</summary>
    public (bool Active, float RemainingSeconds) GetCeasefireState()
    {
        var endGame = _cm.EndGame;
        return (endGame.CeasefireActive, endGame.CeasefireRemaining);
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
        // 玩家乘数接线(原版 prices.buy/sell × multiplier)。
        float multSell = local?.GetBarterMultiplierSell(sell.ToString().ToLowerInvariant()) ?? 1f;
        float multBuy = local?.GetBarterMultiplierBuy(buy.ToString().ToLowerInvariant()) ?? 1f;
        int Gain(int amount) => (int)System.Math.Round(
            (double)BarterSystem.SellPrice(sell, multSell) / BarterSystem.BuyPrice(buy, multBuy) * amount);
        return new BarterQuote(canBarter, Gain(100), Gain(500));
    }

    // ── 小地图/选择集批量快照(原版 GetEntitiesWithInterface 批查;桥扩面)──

    /// <summary>小地图点(值类型;批量快照的数组元素,避免每帧每实体 record 分配)。</summary>
    public readonly struct MinimapDot
    {
        public readonly uint Id;
        public readonly float X, Z;
        public readonly int OwnerPlayerId;
        public readonly bool IsUnit, IsBuilding;
        public readonly float HealthFraction;
        public readonly string Name;
        public MinimapDot(uint id, float x, float z, int owner, bool isUnit, bool isBuilding,
            float healthFraction, string name)
        {
            Id = id; X = x; Z = z; OwnerPlayerId = owner; IsUnit = isUnit; IsBuilding = isBuilding;
            HealthFraction = healthFraction; Name = name;
        }
    }

    private readonly List<MinimapDot> _minimapCache = new();
    private uint _minimapCacheTurn = uint.MaxValue;

    /// <summary>全部在世实体的展示快照(按回合缓存:sim 状态只在回合推进变;
    /// 同一回合内多次读取零重算。迷雾可见性由调用方按 RangeManager 另行过滤)。
    /// 替代 Minimap 每帧 × 每实体的 GetEntityState 逐调用分配。</summary>
    public IReadOnlyList<MinimapDot> GetMinimapEntities()
    {
        // 回合同代:有 NetTurnManager 按 CurrentTurn;无(纯测试/无锁步环境)每次都重建。
        if (SimSystem.Net is not { } net)
        {
            _minimapCacheTurn = uint.MaxValue;
        }
        else if (net.CurrentTurn == _minimapCacheTurn)
            return _minimapCache;
        else
            _minimapCacheTurn = net.CurrentTurn;
        _minimapCache.Clear();
        foreach (var e in _cm.AllEntities)
        {
            var pos = _cm.QueryInterface<PositionComponent>(e);
            if (pos == null || !pos.InWorld) continue;
            var own = _cm.QueryInterface<OwnershipComponent>(e);
            var id = _cm.QueryInterface<IdentityComponent>(e);
            var hp = _cm.QueryInterface<HealthComponent>(e);
            _minimapCache.Add(new MinimapDot(
                e.Value,
                pos.Position.X.ToFloat(), pos.Position.Z.ToFloat(),
                own?.PlayerId ?? 0,
                id?.IsUnit ?? false, id?.IsBuilding ?? false,
                hp != null && hp.Max > 0 ? (float)hp.Current / hp.Max : 1f,
                id?.Name ?? ""));
        }
        return _minimapCache;
    }

    /// <summary>玩家首个 CC 的世界坐标(小地图玩家标记用;无 → null)。
    /// 替代 Minimap.DrawPlayerMarker 的每帧全表扫描。</summary>
    public (float X, float Z)? GetCivilCentrePosition(int playerId)
    {
        foreach (var e in _cm.AllEntities)
        {
            var own = _cm.QueryInterface<OwnershipComponent>(e);
            if (own == null || own.PlayerId != playerId) continue;
            var id = _cm.QueryInterface<IdentityComponent>(e);
            if (id == null || !id.IsBuilding) continue;
            if (!id.TemplateName.Contains("civil_centre") && !id.TemplateName.Contains("civic_centre"))
                continue;
            var pos = _cm.QueryInterface<PositionComponent>(e);
            if (pos == null || !pos.InWorld) continue;
            return (pos.Position.X.ToFloat(), pos.Position.Z.ToFloat());
        }
        return null;
    }

    /// <summary>选择集能力摘要(HUD 命令面板重建的多趟扫描并为一趟;字段即各面板的
    /// 显示条件;模板数据读取(ExtractStats)不在此——那是数据层非 sim 态)。</summary>
    public record SelectionCapabilities(
        bool HasOwnEntity, bool HasOwnUnit,
        bool AnyBuilder, bool AnyProducer, bool HasArsenal,
        bool AnyGarrisonable, bool AnyCanPack, bool AnyCanUnpack,
        IReadOnlyList<string> ResearcherTemplates,
        EntityId? UpgradableId, string UpgradableTemplate,
        EntityId? GateId, bool GateLocked,
        EntityId? ProducerId, EntityId? BuilderId);

    public SelectionCapabilities GetSelectionCapabilities(
        IReadOnlyCollection<EntityId> selected, int localPlayerId)
    {
        bool hasOwnEntity = false, hasOwnUnit = false, anyBuilder = false, anyProducer = false,
            hasArsenal = false, anyGarrisonable = false, anyCanPack = false, anyCanUnpack = false;
        var researcherTemplates = new List<string>();
        EntityId? upgradable = null; string upgradableTemplate = "";
        EntityId? gate = null; bool gateLocked = false;
        EntityId? producer = null, builder = null;

        foreach (var eid in selected)
        {
            bool own = _cm.QueryInterface<OwnershipComponent>(eid)?.PlayerId == localPlayerId;
            if (own)
            {
                hasOwnEntity = true;
                if (_cm.QueryInterface<UnitAIComponent>(eid) != null) hasOwnUnit = true;
            }
            if (_cm.QueryInterface<BuilderComponent>(eid) != null)
            {
                anyBuilder = true;
                builder ??= eid;
            }
            if (_cm.QueryInterface<ProductionQueue>(eid) != null)
            {
                anyProducer = true;
                producer ??= eid;
            }
            var identity = _cm.QueryInterface<IdentityComponent>(eid);
            if (identity != null)
            {
                if (_cm.QueryInterface<ResearcherComponent>(eid) != null)
                    researcherTemplates.Add(identity.TemplateName);
                if (identity.TemplateName.Contains("arsenal")) hasArsenal = true;
                if (own && upgradable == null)
                {
                    var st = _cm.Templates?.ExtractStats(identity.TemplateName);
                    if (st != null && st.UpgradeToTemplate.Length > 0)
                    {
                        upgradable = eid;
                        upgradableTemplate = identity.TemplateName;
                    }
                }
            }
            if (_cm.QueryInterface<GarrisonableComponent>(eid) != null) anyGarrisonable = true;
            var pack = _cm.QueryInterface<PackComponent>(eid);
            if (pack != null)
            {
                if (pack.CanPack()) anyCanPack = true;
                if (pack.CanUnpack()) anyCanUnpack = true;
            }
            if (own && gate == null && _cm.QueryInterface<GateComponent>(eid) is { } g)
            {
                gate = eid;
                gateLocked = g.Locked;
            }
        }
        return new SelectionCapabilities(
            hasOwnEntity, hasOwnUnit, anyBuilder, anyProducer, hasArsenal,
            anyGarrisonable, anyCanPack, anyCanUnpack, researcherTemplates,
            upgradable, upgradableTemplate, gate, gateLocked, producer, builder);
    }

    // ── 热路径聚合快照(桥扩面第二波:HUD 单选详情/选择圈/光标/研究条/站姿/编队组/集结点)──

    /// <summary>单选详情面板快照(原版 GetEntityState 的详情段:HUD.FillSingleDetails
    /// 此前每帧 9 趟 QueryInterface,现一趟聚合)。名字/头像仍走 HUD 的模板缓存
    /// (数据层);本 DTO 只载 sim 态。CapturePoints 为按玩家序的 float 快照(≤9)。</summary>
    public record SelectionDetails(
        uint Id, string TemplateName, bool IsBuilding, string IdentityName,
        string Rank,                              // Elite/Advanced/Basic/""(无军衔)
        int Xp, int XpNext,                       // XpNext<=0 → 隐藏经验条
        bool HasHealth, int HealthCurrent, int HealthMax,
        bool HasCapturable, float MaxCapturePoints, float[] CapturePoints,
        bool HasSupply, string SupplyType, int SupplyAmount, int SupplyMaxAmount,
        int CarryAmount, string CarryType,        // 已 ToLowerInvariant
        bool HasAttack, int AttackPhysical,
        bool HasResistance, int ResistHack, int ResistPierce, int ResistCrush,
        int OwnerPlayerId, string OwnerCiv);

    public SelectionDetails? GetSelectionDetails(EntityId entity)
    {
        var id = _cm.QueryInterface<IdentityComponent>(entity);
        var pos = _cm.QueryInterface<PositionComponent>(entity);
        if (id == null && pos == null) return null;

        var hp = _cm.QueryInterface<HealthComponent>(entity);
        var promotion = _cm.QueryInterface<PromotionComponent>(entity);
        var capturable = _cm.QueryInterface<CapturableComponent>(entity);
        var supply = _cm.QueryInterface<ResourceSupply>(entity);
        var gatherer = _cm.QueryInterface<ResourceGatherer>(entity);
        var attack = _cm.QueryInterface<AttackComponent>(entity);
        var resistance = _cm.QueryInterface<ResistanceComponent>(entity);
        var owner = _cm.QueryInterface<OwnershipComponent>(entity);

        string rank = id != null
            ? id.HasClass("Elite") ? "Elite"
            : id.HasClass("Advanced") ? "Advanced"
            : id.HasClass("Basic") ? "Basic" : "" : "";

        float maxCp = capturable?.MaxCapturePoints.ToFloat() ?? 0f;
        float[] cps = [];
        if (capturable != null && maxCp > 0f)
        {
            int n = System.Math.Min(capturable.CapturePoints.Length, 17);
            cps = new float[n];
            for (int p = 0; p < n; p++) cps[p] = capturable.CapturePoints[p].ToFloat();
        }

        int pid = owner?.PlayerId ?? 0;
        return new SelectionDetails(
            entity.Value, id?.TemplateName ?? "", id?.IsBuilding ?? false, id?.Name ?? "",
            rank,
            promotion?.XP ?? 0, promotion?.XpNext ?? 0,
            hp != null && hp.Max > 0 && hp.Current > 0, hp?.Current ?? 0, hp?.Max ?? 0,
            capturable != null && maxCp > 0f, maxCp, cps,
            supply != null && supply.MaxAmount > 0,
            supply?.Type.ToString() ?? "", supply?.Amount ?? 0, supply?.MaxAmount ?? 0,
            gatherer?.CarryAmount ?? 0,
            gatherer != null ? gatherer.CarryType.ToString().ToLowerInvariant() : "",
            attack != null, attack?.Damage.TotalPhysical ?? 0,
            resistance != null,
            resistance?.Resistances.GetValueOrDefault(DamageType.Hack) ?? 0,
            resistance?.Resistances.GetValueOrDefault(DamageType.Pierce) ?? 0,
            resistance?.Resistances.GetValueOrDefault(DamageType.Crush) ?? 0,
            pid, _cm.GetPlayerEntity(pid)?.Civ ?? "");
    }

    /// <summary>选择圈/状态条快照(原版 GetEntitiesWithStatusBars 段:Main 选择圈重建
    /// 与 hover 条此前 EntityState 之外另查 4 组件,现一趟)。FootprintHalfX/Z 为
    /// 建筑选择圈半宽深(圆形时 HalfX=半径);无 Footprint 件回退 10(调用方语义)。</summary>
    public record MarkerState(
        bool IsBuilding, int OwnerPlayerId, int HealthMax, float HealthFraction,
        int ResourceAmount, int ResourceMaxAmount,
        bool FootprintCircle, float FootprintHalfX, float FootprintHalfZ,
        bool HasRangeOverlay, float Range,
        float MaxCapturePoints, float[] CapturePoints);

    public MarkerState? GetMarkerState(EntityId entity)
    {
        var id = _cm.QueryInterface<IdentityComponent>(entity);
        var pos = _cm.QueryInterface<PositionComponent>(entity);
        if (id == null && pos == null) return null;
        var own = _cm.QueryInterface<OwnershipComponent>(entity);
        var hp = _cm.QueryInterface<HealthComponent>(entity);
        var supply = _cm.QueryInterface<ResourceSupply>(entity);
        var fp = _cm.QueryInterface<FootprintComponent>(entity);
        var attack = _cm.QueryInterface<AttackComponent>(entity);
        var capturable = _cm.QueryInterface<CapturableComponent>(entity);

        float maxCp = capturable?.MaxCapturePoints.ToFloat() ?? 0f;
        float[] cps = [];
        if (capturable != null && maxCp > 0f)
        {
            int n = System.Math.Min(capturable.CapturePoints.Length, 17);
            cps = new float[n];
            for (int p = 0; p < n; p++) cps[p] = capturable.CapturePoints[p].ToFloat();
        }

        return new MarkerState(
            id?.IsBuilding ?? false, own?.PlayerId ?? -1,
            hp?.Max ?? 0, hp != null && hp.Max > 0 ? (float)hp.Current / hp.Max : 0f,
            supply?.Amount ?? 0, supply?.MaxAmount ?? 0,
            fp?.Shape == FootprintShape.Circle,
            fp != null ? fp.Size0.ToFloat() * 0.5f : 10f,
            fp != null ? fp.Size1.ToFloat() * 0.5f : 10f,
            attack is { HasRangeOverlay: true }, attack?.Range ?? 0f,
            maxCp, cps);
    }

    /// <summary>选中集动作能力(原版 actionCheck 的选中侧:攻击/采集/驻防三光标资格,
    /// 一趟扫描替代 DetermineHoverCursor 的每帧 5×N 查询)。Garrison 资格 =
    /// Garrisonable + UnitAI(与调用方原判定一致)。</summary>
    public readonly record struct ActionCaps(bool CanAttack, bool CanGather, bool CanGarrison);

    public ActionCaps GetSelectedActionCaps(IReadOnlyCollection<EntityId> selected, int localPlayerId)
    {
        bool canAttack = false, canGather = false, canGarrison = false;
        foreach (var eid in selected)
        {
            if (_cm.QueryInterface<OwnershipComponent>(eid)?.PlayerId != localPlayerId) continue;
            if (_cm.QueryInterface<AttackComponent>(eid) != null) canAttack = true;
            if (_cm.QueryInterface<ResourceGatherer>(eid) != null) canGather = true;
            if (!canGarrison
                && _cm.QueryInterface<GarrisonableComponent>(eid) != null
                && _cm.QueryInterface<UnitAIComponent>(eid) != null)
                canGarrison = true;
            if (canAttack && canGather && canGarrison) break;
        }
        return new ActionCaps(canAttack, canGather, canGarrison);
    }

    /// <summary>在研科技快照(原版 GetStartedResearch:首个己方在研建筑)。
    /// 无在研 → null;TotalTime 取科技定义 ResearchTime(≤0 回退 1 防除零)。</summary>
    public record StartedResearch(
        string Tech, float Progress, float TotalTime, string GenericName, string Icon);

    public StartedResearch? GetStartedResearch(int playerId)
    {
        var playerEnt = _cm.GetPlayerEntityId(playerId);
        var tm = playerEnt.HasValue
            ? _cm.QueryInterface<TechnologyManager>(playerEnt.Value) : null;
        foreach (var eid in _cm.AllEntities)
        {
            var own = _cm.QueryInterface<OwnershipComponent>(eid);
            if (own == null || own.PlayerId != playerId) continue;
            var r = _cm.QueryInterface<ResearcherComponent>(eid);
            if (r == null || !r.IsResearching || r.CurrentTech == null) continue;
            var def = tm?.GetDefinition(r.CurrentTech);
            return new StartedResearch(
                r.CurrentTech, r.Progress,
                def != null && def.ResearchTime > 0 ? def.ResearchTime : 1f,
                def?.GenericName ?? r.CurrentTech, def?.Icon ?? "");
        }
        return null;
    }

    /// <summary>首个选中有站姿的己方单位的当前站姿(原版 IsStanceSelected 的单值版;
    /// 按钮高亮用)。无 → null。</summary>
    public string? GetFirstStance(IEnumerable<EntityId> selected, int localPlayerId)
    {
        foreach (var eid in selected)
        {
            if (_cm.QueryInterface<OwnershipComponent>(eid)?.PlayerId != localPlayerId) continue;
            if (_cm.QueryInterface<UnitAIComponent>(eid) is { } ai) return ai.Stance;
        }
        return null;
    }

    /// <summary>存活计数(编队组图标条:Identity+Position 俱在 = 活;替代 Main
    /// 每帧每成员 2 趟查询)。</summary>
    public int CountAlive(IEnumerable<EntityId> entities)
    {
        int n = 0;
        foreach (var e in entities)
            if (_cm.QueryInterface<IdentityComponent>(e) != null
                && _cm.QueryInterface<PositionComponent>(e) != null)
                n++;
        return n;
    }

    /// <summary>首个带非空集结点队列的选中建筑(原版 DisplayRallyPoint 的查询侧;
    /// 渲染/缓存键仍在 Main)。Civ 取自模板路径 structures/{civ}/...;无 → null。</summary>
    public record RallyQueue(EntityId Building, IReadOnlyList<ZeroAD.Sim.Maths.FixedVector2D> Points,
        string Civ, int OwnerPlayerId);

    public RallyQueue? GetFirstRallyQueue(IReadOnlyCollection<EntityId> selected, int localPlayerId)
    {
        foreach (var eid in selected)
        {
            var rally = _cm.QueryInterface<RallyPointComponent>(eid);
            if (rally == null) continue;
            var pts = rally.GetPositions(_cm, localPlayerId);
            if (pts.Count == 0) continue;
            string civ = "athen";
            var id = _cm.QueryInterface<IdentityComponent>(eid);
            if (id != null)
            {
                var parts = id.TemplateName.Split('/');
                if (parts.Length >= 2 && parts[0] == "structures") civ = parts[1];
            }
            return new RallyQueue(eid, pts, civ,
                _cm.QueryInterface<OwnershipComponent>(eid)?.PlayerId ?? -1);
        }
        return null;
    }

    /// <summary>实体世界位置(相机跟随用;无 Position/不在世界(驻军等)→ null)。
    /// 消除 RTSCamera 对 SimSystem.Sim 全局单例的直读。</summary>
    public (float X, float Y, float Z)? GetWorldPosition(EntityId entity)
    {
        var pos = _cm.QueryInterface<PositionComponent>(entity);
        if (pos == null || !pos.InWorld) return null;
        return (pos.Position.X.ToFloat(), pos.Position.Y.ToFloat(), pos.Position.Z.ToFloat());
    }

    // ── 环境音邻近查询(表现层音景;上游 audio/ambient/building 数据存在但
    // 未接线——Ambient.js 只播单轨 dayscape。此查询是我们多轨叠加的增补,记录在案)──

    /// <summary>焦点周边建筑音景强度(port/farm/trade,0..1;取最近匹配建筑的
    /// 距离线性衰减,45m 外为 0)。任意属主(可听即可);无匹配 → 全 0。</summary>
    public (float Port, float Farm, float Trade) GetAmbientBuildingLevels(float x, float z)
    {
        float port = 0f, farm = 0f, trade = 0f;
        const float radius2 = 45f * 45f;
        foreach (var e in _cm.AllEntities)
        {
            var id = _cm.QueryInterface<IdentityComponent>(e);
            if (id == null || !id.IsBuilding) continue;
            int kind = id.HasClass("Dock") ? 1
                : id.HasClass("Farmstead") || id.HasClass("Field") ? 2
                : id.HasClass("Market") ? 3 : 0;
            if (kind == 0) continue;
            var pos = _cm.QueryInterface<PositionComponent>(e);
            if (pos == null || !pos.InWorld) continue;
            float dx = pos.Position.X.ToFloat() - x, dz = pos.Position.Z.ToFloat() - z;
            float level = 1f - (dx * dx + dz * dz) / radius2;
            if (level <= 0f) continue;
            if (kind == 1) port = System.Math.Max(port, level);
            else if (kind == 2) farm = System.Math.Max(farm, level);
            else trade = System.Math.Max(trade, level);
        }
        return (port, farm, trade);
    }

    private ComponentManager cm() => _cm;
}
