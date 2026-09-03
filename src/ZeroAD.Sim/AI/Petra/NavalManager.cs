using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>海军管理器（原版 petra/navalManager.js，924 行）。
/// 管理船只（渔船/战船/运输船）、海上贸易保护、运输调度。
/// 本版启用核心闭环(原版 buildNavalStructures + maintainFleet 的简化):
///   - 建码头:海图(HQ.NavalMap)且人口 > popForDock 且无码头无在队计划 →
///     经 Accessibility.TryFindShoreline 选岸线点 → ConstructionPlan(position 元数据)。
///   - 训船:码头建成 → 每码头补足 WantedShipsPerDock 艘(渔船优先——
///     船模板从码头可训列表取首个 Ship 类;原版 getBestShip 的渔/运输区分待运输计划落地)。
/// 运输调度(原版 requireTransport/assignShipsToPlans/splitTransport 的功能端口):
/// 跨海单位 → TransportPlan(同目标陆区+近目的点复用);每 think 给缺船计划分自由
/// 运输船(GarrisonHolder>0 的 Ship);运输船不足时码头优先训运输船。</summary>
public sealed class NavalManager
{
    private readonly PetraConfig _config;
    public readonly List<TransportPlan> TransportPlans = new();

    /// <summary>每码头目标船数(原版 wantedFishShips 按鱼资源量动态;定值 2 起步)。</summary>
    private const int WantedShipsPerDock = 2;

    public NavalManager(PetraConfig config) => _config = config;

    internal int _nextTransportId = 1;
    /// <summary>每海域最低运输船数(原版 minimalTransportShips;attackPlan 跨海进攻上调)。</summary>
    public readonly Dictionary<ushort, int> MinimalTransportShips = new();

    /// <summary>原版 requireTransport:单位跨海运输请求——陆区不同则找/建计划
    /// (同 EndIndex 且目的点 40m 内的 Boarding 计划复用,原版 splitTransport 的
    /// 合流语义),无途经海域(陆区图无路)→ false。</summary>
    public bool RequireTransport(GameState gameState, AIEntity ent, ushort startIndex,
        ushort endIndex, FixedVector2D endPos)
    {
        if (startIndex == endIndex) return false;
        if (ent.Position2D == default) return false;
        var sea = GetSeaBetweenIndices(gameState, startIndex, endIndex);
        if (sea == 0) return false;

        // 复用同目标计划(原版:addUnit 到 matching plan)。
        foreach (var plan in TransportPlans)
        {
            if (plan.State != TransportPlan.TransportState.Boarding) continue;
            if (plan.EndIndex != endIndex) continue;
            float dx = plan.EndPos.X.ToFloat() - endPos.X.ToFloat();
            float dz = plan.EndPos.Y.ToFloat() - endPos.Y.ToFloat();
            if (dx * dx + dz * dz > 40f * 40f) continue;
            return plan.AddUnit(gameState, ent.Id);
        }

        var newPlan = new TransportPlan(_nextTransportId++, startIndex, endIndex, sea, endPos);
        TransportPlans.Add(newPlan);
        return newPlan.AddUnit(gameState, ent.Id);
    }

    /// <summary>两陆区间的海域(原版 HQ.getSeaBetweenIndices:区域路径的第二段)。</summary>
    public static ushort GetSeaBetweenIndices(GameState gameState, ushort start, ushort end)
    {
        var acc = gameState.Accessibility;
        if (acc == null) return 0;
        var traj = acc.GetTrajectToIndex(start, end);
        if (traj == null || traj.Count < 3) return 0;
        return (ushort)traj[1];   // 陆→海→陆:第二段即海
    }

    /// <summary>原版 setMinimalTransportShips:海域最低运输船数登记(只增不减)。</summary>
    public void SetMinimalTransportShips(ushort sea, int number)
    {
        if (number > MinimalTransportShips.GetValueOrDefault(sea))
            MinimalTransportShips[sea] = number;
    }

    /// <summary>每 think:给缺船/舱位不足的 Boarding 计划分自由运输船
    /// (原版 assignShipsToPlans;自由 = Ship 类 + 有 GarrisonHolder + 无 transporter 元数据)。</summary>
    private void AssignShipsToPlans(GameState gameState)
    {
        var needPlans = TransportPlans
            .Where(p => p.State == TransportPlan.TransportState.Boarding
                && p.CountFreeSlots(gameState) < p.Units.Count)
            .ToList();
        if (needPlans.Count == 0) return;
        foreach (var ship in gameState.GetOwnEntitiesByClass("Ship").Values()
            .OrderBy(e => e.Id))
        {
            if (gameState.Metadata.GetObject(ship.Id, "transporter") != null) continue;
            if (ship.Template.GarrisonCapacity <= 0) continue;
            var plan = needPlans[0];
            plan.AssignShip(gameState, ship.Id);
            if (plan.CountFreeSlots(gameState) >= plan.Units.Count)
                needPlans.RemoveAt(0);
            if (needPlans.Count == 0) return;
        }
    }

    // ── 序列化(原版 navalManager 运输段)──
    public void Serialize(Serialization.ISerializer s)
    {
        s.NumberI32("nextPlan", _nextTransportId);
        s.NumberI32("minShips", MinimalTransportShips.Count);
        foreach (var kv in MinimalTransportShips.OrderBy(kv => kv.Key))
        {
            s.NumberI32("sea", kv.Key);
            s.NumberI32("num", kv.Value);
        }
        s.NumberI32("plans", TransportPlans.Count);
        foreach (var plan in TransportPlans.OrderBy(p => p.ID))
            plan.Serialize(s);
    }

    public void Deserialize(Serialization.IDeserializer d)
    {
        _nextTransportId = d.NumberI32("nextPlan");
        int minShips = d.NumberI32("minShips");
        for (int i = 0; i < minShips; i++)
            MinimalTransportShips[(ushort)d.NumberI32("sea")] = d.NumberI32("num");
        int plans = d.NumberI32("plans");
        for (int i = 0; i < plans; i++)
            TransportPlans.Add(TransportPlan.Deserialize(d));
    }

    /// <summary>运输船缺口(原版 wantedTransportShips:各海域最低数 vs 实有)。</summary>
    public int TransportShipShortage(GameState gameState)
    {
        int want = MinimalTransportShips.Count > 0 ? MinimalTransportShips.Values.Sum() : 0;
        if (want == 0) return 0;
        int have = gameState.GetOwnEntitiesByClass("Ship").Values()
            .Count(e => e.Template.GarrisonCapacity > 0);
        return Math.Max(0, want - have);
    }

    /// <summary>事件检查（原版 checkEvents）。结构保留(运输计划接入时用);
    /// 船只集合改为每 think 无态重扫(GetOwnEntitiesByClass),不在此维护。</summary>
    public void CheckEvents(GameState gameState, QueueManager queues, AIEventBuffer events)
    {
    }

    /// <summary>主更新（原版 navalManager.update:checkLevels → maintainFleet →
    /// buildNavalStructures 的顺序在此简并）。</summary>
    /// <summary>HQ 反链(消费 attackManager.AttackPlansEncounteredWater 用;
    /// 原版 navalManager.HQ)。HQ 构造注入。</summary>
    public Func<Headquarters?>? HqResolver;

    public void Update(GameState gameState, QueueManager queues, AIEventBuffer events)
    {
        // 海图换面(原版 attackPlansEncounteredWater 的应有消费端):陆攻隔水失败
        // 且确有海外敌 → 途经海域最低运输船数 +1(累加;驱动训练/摆渡)。
        var hq = HqResolver?.Invoke();
        if (hq != null && hq.AttackManager.AttackPlansEncounteredWater)
        {
            hq.AttackManager.AttackPlansEncounteredWater = false;
            var pf = SimSystem.Pathfinder;
            var myPos = gameState.GetOwnStructures().Values()
                .FirstOrDefault(e => e.Position2D != default);
            if (pf != null && myPos != null)
            {
                uint myRegion = pf.GetLandRegion(myPos.Position2D.X, myPos.Position2D.Y);
                foreach (var enemy in gameState.GetEnemies())
                {
                    var enemyPos = gameState.GetStructures().Values()
                        .Where(e => e.Owner == enemy && e.Position2D != default)
                        .OrderBy(e => e.Id).FirstOrDefault();
                    if (enemyPos == null) continue;
                    uint tgtRegion = pf.GetLandRegion(
                        enemyPos.Position2D.X, enemyPos.Position2D.Y);
                    if (tgtRegion == 0 || tgtRegion == myRegion) continue;
                    ushort sea = GetSeaBetweenIndices(gameState, (ushort)myRegion, (ushort)tgtRegion);
                    if (sea == 0) continue;
                    // 该海域当前值 +1(原版 minimalTransportShips 只增不减)。
                    SetMinimalTransportShips(sea,
                        MinimalTransportShips.GetValueOrDefault(sea) + 1);
                }
            }
        }

        var docks = gameState.GetOwnEntitiesByClass("Dock").Values().ToList();
        var ships = gameState.GetOwnEntitiesByClass("Ship").Values().ToList();

        // 1. 训船(原版 maintainFleet):有建成码头且船不足 → 补。
        if (docks.Count > 0 && ships.Count < docks.Count * WantedShipsPerDock)
            TrainShip(gameState, queues, docks);

        // 2. 建码头(原版 buildNavalStructures):人口达标 + 无码头 + 无在队。
        if (docks.Count == 0)
            BuildDock(gameState, queues);

        // 3. 运输计划:分船 → 推进 → 收尾(Completed/Failed/Canceled 移除)。
        AssignShipsToPlans(gameState);
        foreach (var plan in TransportPlans.ToList())
        {
            plan.Update(gameState);
            if (plan.State != TransportPlan.TransportState.Boarding
                && plan.State != TransportPlan.TransportState.Sailing)
                TransportPlans.Remove(plan);
        }
    }

    /// <summary>训船(原版 getBestShip + maintainFleet):从首个码头的可训列表取
    /// 首个 Ship 类模板,入 ships 队列。无船模板(内陆文明/模板缺失)→ 静默跳过。</summary>
    private void TrainShip(GameState gameState, QueueManager queues, List<AIEntity> docks)
    {
        foreach (var dock in docks.OrderBy(d => d.Id))
        {
            var trainables = dock.Template.TrainableEntities;
            if (string.IsNullOrEmpty(trainables)) continue;
            // 模板 tokens 可含换行/缩进(XML 原文排版)→ 按全部空白符分词。
            // 运输船短缺时优先可载客的船(原版 getBestShip 的 goal 区分;
            // GarrisonCapacity>0 = 运输能力判定)。
            bool preferTransport = TransportShipShortage(gameState) > 0;
            string? firstShip = null, firstTransport = null;
            foreach (var token in trainables.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
            {
                string template = gameState.ResolveTokens(token);
                var tmpl = gameState.GetTemplate(template);
                if (tmpl == null || !tmpl.HasClass("Ship")) continue;
                firstShip ??= template;
                if (tmpl.GarrisonCapacity > 0) { firstTransport = template; break; }
            }
            string? chosen = preferTransport && firstTransport != null ? firstTransport
                : firstTransport ?? firstShip;
            if (chosen == null) continue;
            queues.AddPlan("ships", new TrainingPlan(gameState, chosen,
                new Dictionary<string, object> { ["sea"] = 0 }, 1, 1));
            return;   // 每次 think 最多补一艘(队列管理器合并同型)
        }
    }

    /// <summary>建码头(原版 buildNavalStructures 的 dock 段):人口 > popForDock、
    /// 无码头无在队计划、岸线点可达 → ConstructionPlan(position 元数据)。
    /// 岸线点 = 距最近 CC 最近的"陆格 4 邻接水域"格。</summary>
    private void BuildDock(GameState gameState, QueueManager queues)
    {
        if (gameState.GetPopulation() < _config.Economy.PopForDock) return;
        if (queues.GetQueue("dock")?.HasQueuedUnits == true) return;
        var acc = gameState.Accessibility;
        if (acc == null) return;

        // 参考点 = 最近的 CC(无 CC 用首个建筑)。
        var structures = gameState.GetOwnStructures().Values().ToList();
        if (structures.Count == 0) return;
        var anchor = structures
            .Where(s => s.HasClass("CivCentre"))
            .Cast<AIEntity?>()
            .FirstOrDefault() ?? structures[0];

        float ax = anchor.Position2D.X.ToFloat();
        float az = anchor.Position2D.Y.ToFloat();
        if (!acc.TryFindShoreline(ax, az, out float sx, out float sz)) return;

        var metadata = new Dictionary<string, object>
        {
            ["position"] = new FixedVector2D(Fixed.FromFloat(sx), Fixed.FromFloat(sz)),
        };
        queues.AddPlan("dock", new ConstructionPlan(gameState, "structures/{civ}/dock", metadata));
    }

}
