using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>贸易管理器（原版 petra/tradeManager.js，732 行）。
/// 管理贸易路线（市场间商队）、易物（barter）、贸易品配置。
/// 骨架版——核心 update 结构移植，市场配对/checkTrader/trainMoreTraders 标 TODO。</summary>
public sealed class TradeManager
{
    private readonly PetraConfig _config;

    // 贸易路线（两个市场 entity id）
    public TradeRoute? Route;
    public int TargetNumTraders;
    public bool RouteProspection = true;

    // 商队实体集合（每 think 重建）
    private List<AIEntity> _traders = new();

    public TradeManager(PetraConfig config)
    {
        _config = config;
        TargetNumTraders = config.Economy.TargetNumTraders;
    }

    /// <summary>主更新（原版 tradeManager.js:687-714）。</summary>
    public void Update(GameState gameState, AIEventBuffer events, QueueManager queues)
    {
        // 1. 易物（有市场时）
        // TODO: PerformBarter（资源严重失衡时交换）

        if (_config.Difficulty <= DifficultyLevel.VeryEasy) return;

        // 2. 检查市场变动（建造/销毁）
        bool marketChanged = CheckEvents(gameState, events);
        if (marketChanged)
        {
            RebuildTraders(gameState);
            CheckRoutes(gameState);
        }

        // 3. 有路线时：更新商队 + 训练新商队
        if (Route != null)
        {
            RebuildTraders(gameState);
            foreach (var trader in _traders)
                UpdateTrader(gameState, trader);

            // 每5回合检查训练
            if (gameState.Events.Events.Count % 5 == 0)
                TrainMoreTraders(gameState, queues);

            // 每60回合重设贸易品
            if (gameState.Events.Events.Count % 60 == 0)
                SetTradingGoods(gameState);
        }

        // 4. 寻找新市场位置
        if (RouteProspection)
            ProspectForNewMarket(gameState, queues);
    }

    /// <summary>检查事件（市场建造/销毁）。返回 true = 市场变动。</summary>
    private bool CheckEvents(GameState gameState, AIEventBuffer events)
    {
        bool changed = false;
        foreach (var ev in events.Events)
        {
            if (ev.Type == AIEventType.ConstructionFinished || ev.Type == AIEventType.Destroy)
            {
                var ent = gameState.GetEntityById(ev.Entity);
                if (ent != null && ent.HasClass("Market"))
                    changed = true;
            }
        }
        return changed;
    }

    /// <summary>重建商队集合。</summary>
    private void RebuildTraders(GameState gameState)
    {
        _traders = gameState.GetOwnUnits().Filter(e => e.HasClass("Trader")).ToList();
    }

    /// <summary>检查/创建贸易路线（原版 checkRoutes）。
    /// 简化版：找两个最远的市场作路线。</summary>
    private void CheckRoutes(GameState gameState)
    {
        var markets = gameState.GetOwnStructures().Filter(e => e.HasClass("Market")).ToList();
        if (markets.Count < 2)
        {
            Route = null;
            return;
        }
        // 找最远的一对市场
        uint? m1 = null, m2 = null;
        long maxDist = 0;
        for (int i = 0; i < markets.Count; i++)
        {
            for (int j = i + 1; j < markets.Count; j++)
            {
                long dist = AIUtils3.SquareDistance(markets[i].Position2D, markets[j].Position2D);
                if (dist > maxDist) { maxDist = dist; m1 = markets[i].Id; m2 = markets[j].Id; }
            }
        }
        if (m1.HasValue && m2.HasValue)
            Route = new TradeRoute(m1.Value, m2.Value);
    }

    /// <summary>更新单个商队（原版 updateTrader）。
    /// 简化版：检查是否在贸易路线上。</summary>
    private void UpdateTrader(GameState gameState, AIEntity trader)
    {
        if (Route == null) return;
        // TODO: 下达 setup-trade-route 命令（需 NetCommand）
    }

    /// <summary>训练更多商队（原版 trainMoreTraders）。</summary>
    private void TrainMoreTraders(GameState gameState, QueueManager queues)
    {
        if (_traders.Count >= TargetNumTraders) return;
        if (Route == null) return;
        // TODO: 入队 TrainingPlan 商队单位
    }

    /// <summary>设置贸易品比例（原版 setTradingGoods）。
    /// 简化版：按当前资源量选最少的那种。</summary>
    private void SetTradingGoods(GameState gameState)
    {
        var res = gameState.GetResources();
        // TODO: 通过 PlayerComponent.SetTradingGoods 设置
    }

    /// <summary>寻找新市场位置（原版 prospectForNewMarket）。</summary>
    private void ProspectForNewMarket(GameState gameState, QueueManager queues)
    {
        // TODO: 完整选址逻辑（依赖 territory map + obstruction map）
    }

    /// <summary>贸易路线（两个市场）。</summary>
    public sealed record TradeRoute(uint Market1, uint Market2);
}
