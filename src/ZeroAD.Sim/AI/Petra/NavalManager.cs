using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>海军管理器（原版 petra/navalManager.js，924 行）。
/// 管理船只（渔船/战船/运输船）、海上贸易保护、运输调度。
/// 本版启用核心闭环(原版 buildNavalStructures + maintainFleet 的简化):
///   - 建码头:海图(HQ.NavalMap)且人口 > popForDock 且无码头无在队计划 →
///     经 Accessibility.TryFindShoreline 选岸线点 → ConstructionPlan(position 元数据)。
///   - 训船:码头建成 → 每码头补足 WantedShipsPerDock 艘(渔船优先——
///     船模板从码头可训列表取首个 Ship 类;原版 getBestShip 的渔/运输区分待运输计划落地)。
/// 运输计划(TransportPlan)仍为骨架——渡海运兵待 attackManager 的跨海进攻接入。</summary>
public sealed class NavalManager
{
    private readonly PetraConfig _config;
    public readonly List<TransportPlan> TransportPlans = new();

    /// <summary>每码头目标船数(原版 wantedFishShips 按鱼资源量动态;定值 2 起步)。</summary>
    private const int WantedShipsPerDock = 2;

    public NavalManager(PetraConfig config) => _config = config;

    /// <summary>事件检查（原版 checkEvents）。结构保留(运输计划接入时用);
    /// 船只集合改为每 think 无态重扫(GetOwnEntitiesByClass),不在此维护。</summary>
    public void CheckEvents(GameState gameState, QueueManager queues, AIEventBuffer events)
    {
    }

    /// <summary>主更新（原版 navalManager.update:checkLevels → maintainFleet →
    /// buildNavalStructures 的顺序在此简并）。</summary>
    public void Update(GameState gameState, QueueManager queues, AIEventBuffer events)
    {
        var docks = gameState.GetOwnEntitiesByClass("Dock").Values().ToList();
        var ships = gameState.GetOwnEntitiesByClass("Ship").Values().ToList();

        // 1. 训船(原版 maintainFleet):有建成码头且船不足 → 补。
        if (docks.Count > 0 && ships.Count < docks.Count * WantedShipsPerDock)
            TrainShip(gameState, queues, docks);

        // 2. 建码头(原版 buildNavalStructures):人口达标 + 无码头 + 无在队。
        if (docks.Count == 0)
            BuildDock(gameState, queues);

        // 3. 运输计划(骨架)。
        foreach (var plan in TransportPlans.ToList())
        {
            plan.Update(gameState);
            if (plan.State == TransportPlan.TransportState.Completed
                || plan.State == TransportPlan.TransportState.Failed)
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
            foreach (var token in trainables.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
            {
                string template = gameState.ResolveTokens(token);
                var tmpl = gameState.GetTemplate(template);
                if (tmpl == null || !tmpl.HasClass("Ship")) continue;
                queues.AddPlan("ships", new TrainingPlan(gameState, template,
                    new Dictionary<string, object> { ["sea"] = 0 }, 1, 1));
                return;   // 每次 think 最多补一艘(队列管理器合并同型)
            }
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

    /// <summary>需要运输时创建 TransportPlan。</summary>
    public TransportPlan? CreateTransport(GameState gameState, uint unit, FixedVector2D destination)
    {
        var plan = new TransportPlan(unit, destination);
        TransportPlans.Add(plan);
        return plan;
    }
}

/// <summary>运输计划（原版 petra/transportPlan.js，753 行）。
/// 跨海运兵：登船 → 航行 → 下船。
/// 骨架版——状态机结构。</summary>
public sealed class TransportPlan
{
    public readonly uint Unit;
    public readonly FixedVector2D Destination;

    public enum TransportState { Boarding, Sailing, Unboarding, Completed, Failed }
    public TransportState State { get; private set; }

    public TransportPlan(uint unit, FixedVector2D destination)
    { Unit = unit; Destination = destination; State = TransportState.Boarding; }

    public void Update(GameState gameState)
    {
        // TODO: 完整状态机（Boarding→Sailing→Unboarding→Completed）
        // 简化版：无船可用时直接 Failed
        State = TransportState.Failed;
    }
}
