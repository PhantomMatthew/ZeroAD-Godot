using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>StatisticsTracker — per-player match statistics collector.
/// 镜像原版 StatisticsTracker.js（526 行）。挂载到每个玩家实体，订阅 SimEventBus 的事件
/// 更新 25 类计数器。参与序列化（确定性：进 OOS 哈希 + 存档），UI 在 GameEndedEvent 时读取。
///
/// 类分桶对齐原版：单位按 Infantry/Worker/Cavalry/Champion/Hero/Siege/Ship/Trader；
/// 建筑按 House/Economic/Outpost/Military/Fortress/CivCentre/Wonder。价值按 CostComponent 求和。
///
/// 周期快照：每 30 秒把 GetStatistics() 压入 sequences（同原版 UpdateSequenceInterval），
/// 供 UI 绘制时间线图表。Tick 由 EntityAssembler/ComponentManager 在回合边界调用。</summary>
[Component("StatisticsTracker", "StatisticsTracker")]
public sealed class StatisticsTrackerComponent : ComponentBase
{
    // ── 计数器（对齐原版字段集）──

    // 单位（按类分桶；"total" 键存合计）
    public Dictionary<string, int> UnitsTrained = new();
    public Dictionary<string, int> UnitsLost = new();
    public Dictionary<string, int> EnemyUnitsKilled = new();
    public Dictionary<string, int> UnitsCaptured = new();
    public int UnitsLostValue, EnemyUnitsKilledValue, UnitsCapturedValue;

    // 建筑
    public Dictionary<string, int> BuildingsConstructed = new();
    public Dictionary<string, int> BuildingsLost = new();
    public Dictionary<string, int> EnemyBuildingsDestroyed = new();
    public Dictionary<string, int> BuildingsCaptured = new();
    public int BuildingsLostValue, EnemyBuildingsDestroyedValue, BuildingsCapturedValue;

    // 资源（按 ResourceType.ToString() 分桶：wood/food/stone/metal）
    public Dictionary<string, int> ResourcesGathered = new();
    public Dictionary<string, int> ResourcesUsed = new();
    public Dictionary<string, int> ResourcesSold = new();
    public Dictionary<string, int> ResourcesBought = new();

    // 经济/外交标量
    public int TributesSent, TributesReceived;
    public int TradeIncome;
    public int TreasuresCollected;   // 子系统未完整，恒 0（占位）
    public int LootCollected;        // 同上

    // 地图百分比（0-100）
    public float PercentMapExplored, PercentMapControlled, PeakPercentMapControlled;

    // 时间序列（30 秒快照）
    public List<float> SequenceTimes = new();
    public List<StatisticsSnapshot> Sequences = new();

    private ComponentManager? _cm;
    private float _snapshotTimer;
    private const float SnapshotInterval = 30f;  // 秒（原版 UpdateSequenceInterval = 30000ms）

    // ── 生命周期 ──

    /// <summary>注入 cm 并订阅事件。由 EntityAssembler（新游戏）或 prepareComponent（冷加载）调用。
    /// 同 AIComponent.Configure / LosManagerComponent.Attach 模式。</summary>
    public void Attach(ComponentManager cm)
    {
        _cm = cm;
        var ev = cm.Events;
        ev.TrainingFinished += OnTrainingFinished;
        ev.StructureBuilt += OnStructureBuilt;
        ev.OwnershipChanged += OnOwnershipChanged;
        ev.EntityKilled += OnEntityKilled;
        ev.ResourceGathered += OnResourceGathered;
        ev.ResourceSpent += OnResourceSpent;
        ev.TradeIncome += OnTradeIncome;
        ev.Tribute += OnTribute;
    }

    public void Detach()
    {
        if (_cm == null) return;
        var ev = _cm.Events;
        ev.TrainingFinished -= OnTrainingFinished;
        ev.StructureBuilt -= OnStructureBuilt;
        ev.OwnershipChanged -= OnOwnershipChanged;
        ev.EntityKilled -= OnEntityKilled;
        ev.ResourceGathered -= OnResourceGathered;
        ev.ResourceSpent -= OnResourceSpent;
        ev.TradeIncome -= OnTradeIncome;
        ev.Tribute -= OnTribute;
        _cm = null;
    }

    protected override void OnDeinit() => Detach();

    // ── 周期快照 ──

    /// <summary>每回合调用（dt = SimTickRate ≈ 0.1s）。到 30s 压一次快照。</summary>
    public void Tick(float dt)
    {
        _snapshotTimer += dt;
        if (_snapshotTimer >= SnapshotInterval)
        {
            _snapshotTimer = 0;
            SequenceTimes.Add(_snapshotTimer == 0 ? SequenceTimes.Count * SnapshotInterval : 0);
            Sequences.Add(GetStatistics());
        }
    }

    // ── 事件处理 ──

    private void OnTrainingFinished(TrainingFinishedEvent e)
    {
        int owner = OwnerOf(e.TrainerEntity);
        if (owner != PlayerId) return;
        var stats = _cm!.Templates?.ExtractStats(e.UnitTemplate);
        string cls = stats != null ? PrimaryUnitClass(stats.GetClassList()) : "total";
        Inc(UnitsTrained, cls);
        Inc(UnitsTrained, "total");
    }

    private void OnStructureBuilt(StructureBuiltEvent e)
    {
        int owner = OwnerOf(e.Building);
        if (owner != PlayerId) return;
        var id = _cm!.QueryInterface<IdentityComponent>(e.Building);
        string cls = id != null ? PrimaryBuildingClass(id) : "total";
        Inc(BuildingsConstructed, cls);
        Inc(BuildingsConstructed, "total");
    }

    private void OnOwnershipChanged(OwnershipChangedEvent e)
    {
        // 占领：From≠-1 && To≠-1（双方计数器）
        if (e.From < 0 || e.To < 0) return;
        var id = _cm!.QueryInterface<IdentityComponent>(e.Entity);
        int value = EntityValue(e.Entity);
        if (e.To == PlayerId)
        {
            // 我方获得该实体
            if (id?.IsBuilding == true)
            { Inc(BuildingsCaptured, PrimaryBuildingClass(id)); Inc(BuildingsCaptured, "total"); BuildingsCapturedValue += value; }
            else
            { Inc(UnitsCaptured, PrimaryUnitClass(id?.Classes ?? new())); Inc(UnitsCaptured, "total"); UnitsCapturedValue += value; }
        }
    }

    private void OnEntityKilled(EntityKilledEvent e)
    {
        int killer = OwnerOf(e.Killer);
        int victim = OwnerOf(e.Victim);
        int value = EntityValue(e.Victim);
        var id = _cm!.QueryInterface<IdentityComponent>(e.Victim);

        // killer 的 enemyUnitsKilled / enemyBuildingsDestroyed
        if (killer == PlayerId && victim != PlayerId)
        {
            if (id?.IsBuilding == true)
            { Inc(EnemyBuildingsDestroyed, PrimaryBuildingClass(id)); Inc(EnemyBuildingsDestroyed, "total"); EnemyBuildingsDestroyedValue += value; }
            else
            { Inc(EnemyUnitsKilled, PrimaryUnitClass(id?.Classes ?? new())); Inc(EnemyUnitsKilled, "total"); EnemyUnitsKilledValue += value; }
        }
        // victim 的 unitsLost / buildingsLost
        if (victim == PlayerId)
        {
            if (id?.IsBuilding == true)
            { Inc(BuildingsLost, PrimaryBuildingClass(id)); Inc(BuildingsLost, "total"); BuildingsLostValue += value; }
            else
            { Inc(UnitsLost, PrimaryUnitClass(id?.Classes ?? new())); Inc(UnitsLost, "total"); UnitsLostValue += value; }
        }
    }

    private void OnResourceGathered(ResourceGatheredEvent e)
    {
        if (e.PlayerId != PlayerId) return;
        Inc(ResourcesGathered, e.Type.ToString().ToLowerInvariant(), e.Amount);
        Inc(ResourcesGathered, "total", e.Amount);
    }

    private void OnResourceSpent(ResourceSpentEvent e)
    {
        if (e.PlayerId != PlayerId) return;
        Inc(ResourcesUsed, e.Type.ToString().ToLowerInvariant(), e.Amount);
        Inc(ResourcesUsed, "total", e.Amount);
    }

    private void OnTradeIncome(TradeIncomeEvent e)
    {
        if (e.PlayerId != PlayerId) return;
        TradeIncome += e.Amount;
    }

    private void OnTribute(TributeEvent e)
    {
        if (e.FromPlayerId == PlayerId) TributesSent += e.Amount;
        if (e.ToPlayerId == PlayerId) TributesReceived += e.Amount;
    }

    // ── 公开 API ──

    private int PlayerId => _cm?.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;

    /// <summary>当前所有计数器的深拷贝快照（UI / 时间序列用）。</summary>
    public StatisticsSnapshot GetStatistics() => new()
    {
        UnitsTrained = Clone(UnitsTrained),
        UnitsLost = Clone(UnitsLost),
        EnemyUnitsKilled = Clone(EnemyUnitsKilled),
        UnitsCaptured = Clone(UnitsCaptured),
        UnitsLostValue = UnitsLostValue,
        EnemyUnitsKilledValue = EnemyUnitsKilledValue,
        UnitsCapturedValue = UnitsCapturedValue,
        BuildingsConstructed = Clone(BuildingsConstructed),
        BuildingsLost = Clone(BuildingsLost),
        EnemyBuildingsDestroyed = Clone(EnemyBuildingsDestroyed),
        BuildingsCaptured = Clone(BuildingsCaptured),
        BuildingsLostValue = BuildingsLostValue,
        EnemyBuildingsDestroyedValue = EnemyBuildingsDestroyedValue,
        BuildingsCapturedValue = BuildingsCapturedValue,
        ResourcesGathered = Clone(ResourcesGathered),
        ResourcesUsed = Clone(ResourcesUsed),
        TributesSent = TributesSent,
        TributesReceived = TributesReceived,
        TradeIncome = TradeIncome,
        TreasuresCollected = TreasuresCollected,
        LootCollected = LootCollected,
        PercentMapExplored = PercentMapExplored,
        PercentMapControlled = PercentMapControlled,
        PeakPercentMapControlled = PeakPercentMapControlled,
    };

    /// <summary>分数（对齐原版 counters.js 公式）。</summary>
    public (int total, int economy, int military, int exploration) GetScore()
    {
        int gathered = Sum(ResourcesGathered);
        int economy = (gathered + TradeIncome) / 10;
        int military = (EnemyUnitsKilledValue + UnitsCapturedValue + EnemyBuildingsDestroyedValue + BuildingsCapturedValue) / 10;
        int exploration = (int)(PercentMapExplored * 10);
        return (economy + military + exploration, economy, military, exploration);
    }

    // ── 序列化（参与 OOS 哈希 + 存档）──

    public override void Serialize(ISerializer s)
    {
        SerializeDict(s, "ut", UnitsTrained);
        SerializeDict(s, "ul", UnitsLost);
        SerializeDict(s, "euk", EnemyUnitsKilled);
        SerializeDict(s, "uc", UnitsCaptured);
        s.NumberI32("ulv", UnitsLostValue);
        s.NumberI32("eukv", EnemyUnitsKilledValue);
        s.NumberI32("ucv", UnitsCapturedValue);
        SerializeDict(s, "bc", BuildingsConstructed);
        SerializeDict(s, "bl", BuildingsLost);
        SerializeDict(s, "ebd", EnemyBuildingsDestroyed);
        SerializeDict(s, "bca", BuildingsCaptured);
        s.NumberI32("blv", BuildingsLostValue);
        s.NumberI32("ebdv", EnemyBuildingsDestroyedValue);
        s.NumberI32("bcav", BuildingsCapturedValue);
        SerializeDict(s, "rg", ResourcesGathered);
        SerializeDict(s, "ru", ResourcesUsed);
        s.NumberI32("ts", TributesSent);
        s.NumberI32("tr", TributesReceived);
        s.NumberI32("ti", TradeIncome);
        s.NumberFixed("pme", Fixed.FromFloat(PercentMapExplored));
        s.NumberFixed("pmc", Fixed.FromFloat(PercentMapControlled));
        s.NumberFixed("ppmc", Fixed.FromFloat(PeakPercentMapControlled));
    }

    public override void Deserialize(IDeserializer d)
    {
        UnitsTrained = DeserializeDict(d, "ut");
        UnitsLost = DeserializeDict(d, "ul");
        EnemyUnitsKilled = DeserializeDict(d, "euk");
        UnitsCaptured = DeserializeDict(d, "uc");
        UnitsLostValue = d.NumberI32("ulv");
        EnemyUnitsKilledValue = d.NumberI32("eukv");
        UnitsCapturedValue = d.NumberI32("ucv");
        BuildingsConstructed = DeserializeDict(d, "bc");
        BuildingsLost = DeserializeDict(d, "bl");
        EnemyBuildingsDestroyed = DeserializeDict(d, "ebd");
        BuildingsCaptured = DeserializeDict(d, "bca");
        BuildingsLostValue = d.NumberI32("blv");
        EnemyBuildingsDestroyedValue = d.NumberI32("ebdv");
        BuildingsCapturedValue = d.NumberI32("bcav");
        ResourcesGathered = DeserializeDict(d, "rg");
        ResourcesUsed = DeserializeDict(d, "ru");
        TributesSent = d.NumberI32("ts");
        TributesReceived = d.NumberI32("tr");
        TradeIncome = d.NumberI32("ti");
        PercentMapExplored = d.NumberFixed("pme").ToFloat();
        PercentMapControlled = d.NumberFixed("pmc").ToFloat();
        PeakPercentMapControlled = d.NumberFixed("ppmc").ToFloat();
    }

    // ── 辅助 ──

    private int OwnerOf(EntityId e) => _cm?.QueryInterface<OwnershipComponent>(e)?.PlayerId ?? -1;

    private int EntityValue(EntityId e)
    {
        var cost = _cm?.QueryInterface<CostComponent>(e);
        if (cost == null) return 0;
        return cost.WoodCost + cost.FoodCost + cost.StoneCost + cost.MetalCost;
    }

    private static void Inc(Dictionary<string, int> dict, string key, int by = 1)
        => dict[key] = dict.TryGetValue(key, out var v) ? v + by : by;

    private static int Sum(Dictionary<string, int> dict) => dict.Values.Sum();

    private static Dictionary<string, int> Clone(Dictionary<string, int> src)
        => new(src, StringComparer.Ordinal);

    /// <summary>单位主类桶：按原版 UnitClasses 优先级取第一个匹配的类。</summary>
    private static string PrimaryUnitClass(IReadOnlyList<string> classes)
    {
        string[] order = { "Infantry", "Worker", "Cavalry", "Champion", "Hero", "Siege", "Ship", "Trader" };
        foreach (var c in order)
            if (classes.Contains(c)) return c;
        return "total";
    }

    /// <summary>建筑主类桶：按原版 BuildingClasses 优先级。</summary>
    private static string PrimaryBuildingClass(IdentityComponent id)
    {
        string[] order = { "House", "Economic", "Outpost", "Military", "Fortress", "CivCentre", "Wonder" };
        foreach (var c in order)
            if (id.HasClass(c)) return c;
        return "total";
    }

    private static void SerializeDict(ISerializer s, string name, Dictionary<string, int> dict)
    {
        s.NumberU32(name + "_n", (uint)dict.Count);
        foreach (var kvp in dict.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            s.StringASCII(name + "_k", kvp.Key);
            s.NumberI32(name + "_v", kvp.Value);
        }
    }

    private static Dictionary<string, int> DeserializeDict(IDeserializer d, string name)
    {
        uint n = d.NumberU32(name + "_n");
        var dict = new Dictionary<string, int>((int)n, StringComparer.Ordinal);
        for (uint i = 0; i < n; i++)
        {
            string k = d.StringASCII(name + "_k");
            int v = d.NumberI32(name + "_v");
            dict[k] = v;
        }
        return dict;
    }
}

/// <summary>统计快照（不可变 POCO）。GetStatistics() 返回此类型；时间序列存它的列表。</summary>
public sealed class StatisticsSnapshot
{
    public Dictionary<string, int> UnitsTrained = new();
    public Dictionary<string, int> UnitsLost = new();
    public Dictionary<string, int> EnemyUnitsKilled = new();
    public Dictionary<string, int> UnitsCaptured = new();
    public int UnitsLostValue, EnemyUnitsKilledValue, UnitsCapturedValue;
    public Dictionary<string, int> BuildingsConstructed = new();
    public Dictionary<string, int> BuildingsLost = new();
    public Dictionary<string, int> EnemyBuildingsDestroyed = new();
    public Dictionary<string, int> BuildingsCaptured = new();
    public int BuildingsLostValue, EnemyBuildingsDestroyedValue, BuildingsCapturedValue;
    public Dictionary<string, int> ResourcesGathered = new();
    public Dictionary<string, int> ResourcesUsed = new();
    public int TributesSent, TributesReceived, TradeIncome, TreasuresCollected, LootCollected;
    public float PercentMapExplored, PercentMapControlled, PeakPercentMapControlled;
}
