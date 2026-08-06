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
        // 1. 易物（有市场时;资源严重失衡才换,节流每 5 回合）
        if (Route != null || gameState.GetOwnStructures().Filter(e => e.HasClass("Market")).HasEntities())
            PerformBarter(gameState);

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

    /// <summary>资源严重失衡时易物(原版 performBarter 简化版):某资源 <100 且另一
    /// 资源 >1000 时换 500。每 5 回合节流。</summary>
    private void PerformBarter(GameState gameState)
    {
        if (gameState.Events.Events.Count % 5 != 0) return;
        var res = gameState.GetResources();
        (int amount, ZeroAD.Sim.Components.ResourceType type)[] stocks =
        {
            (res.Wood, ZeroAD.Sim.Components.ResourceType.Wood),
            (res.Food, ZeroAD.Sim.Components.ResourceType.Food),
            (res.Stone, ZeroAD.Sim.Components.ResourceType.Stone),
            (res.Metal, ZeroAD.Sim.Components.ResourceType.Metal),
        };
        var scarce = stocks.OrderBy(s => s.amount).First();
        var surplus = stocks.OrderByDescending(s => s.amount).First();
        if (scarce.amount < 100 && surplus.amount > 1000 && surplus.type != scarce.type)
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Barter(
                (uint)gameState.PlayerId, surplus.type, scarce.type, 500));
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
    /// 简化版：商队无双市场目标时指派到贸易路线远端市场(SetupTradeRoute 命令)。</summary>
    private void UpdateTrader(GameState gameState, AIEntity trader)
    {
        if (Route == null) return;
        var tc = gameState.Cm.QueryInterface<ZeroAD.Sim.Components.TraderComponent>(
            new ZeroAD.Sim.EntityId(trader.Id));
        if (tc == null || tc.HasBothMarkets()) return;
        // 指派到路线另一端市场(原版 route assignment 简化:统一指向 Market2,源=近端)。
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.SetupTradeRoute(
            (uint)gameState.PlayerId, trader.Id, Route.Market2));
    }

    /// <summary>训练更多商队（原版 trainMoreTraders）。</summary>
    private void TrainMoreTraders(GameState gameState, QueueManager queues)
    {
        if (_traders.Count >= TargetNumTraders) return;
        if (Route == null) return;
        if (queues.GetQueue("trader")?.HasQueuedUnits == true) return;
        queues.AddPlan("trader",
            new TrainingPlan(gameState, "units/{civ}/support_trader"));
    }

    /// <summary>设置贸易品比例（原版 setTradingGoods）。
    /// 简化版：最缺资源设为买入,其余按存量比例卖出(SetTradingGoods 命令)。</summary>
    private void SetTradingGoods(GameState gameState)
    {
        var res = gameState.GetResources();
        int total = res.Wood + res.Food + res.Stone + res.Metal + 1;
        // 原版按 tradeRate 配平;简化:缺的买 100,其余按存量占比卖。
        int wood = 100, food = 100, stone = 100, metal = 100;
        var scarce = new (int amount, int idx)[] { (res.Wood, 0), (res.Food, 1), (res.Stone, 2), (res.Metal, 3) }
            .OrderBy(s => s.amount).First().idx;
        switch (scarce)
        {
            case 0: wood = 0; break;
            case 1: food = 0; break;
            case 2: stone = 0; break;
            case 3: metal = 0; break;
        }
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.SetTradingGoods(
            (uint)gameState.PlayerId, wood, food, stone, metal));
    }

    /// <summary>寻找新市场位置（原版 prospectForNewMarket）。</summary>
    private void ProspectForNewMarket(GameState gameState, QueueManager queues)
    {
        // TODO: 完整选址逻辑（依赖 territory map + obstruction map）
    }

    /// <summary>贸易路线（两个市场）。</summary>
    public sealed record TradeRoute(uint Market1, uint Market2);
}
