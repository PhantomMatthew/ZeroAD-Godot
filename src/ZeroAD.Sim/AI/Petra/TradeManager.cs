using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>贸易管理器（原版 petra/tradeManager.js，732 行）。
/// 管理贸易路线（市场间商队）、易物（barter）、贸易品配置。
/// checkRoutes/checkTrader/updateTrader/prospectForNewMarket 全量移植:
/// 收益驱动(TradeGain 距离平方 × 模板倍率;盟友市场参与;陆/海双通道;陆线穿敌领回避),
/// 市场建/毁/易主/换名事件重启勘探,新市场选址经 HQ.FindMarketLocation,
/// isNewMarketWorth 显著增益门控(动态换路线的核心:更好的市场对出现时切线/增建)。
///
/// 与原版的差异记录:
/// - 商队集合按 Trader 类每 think 重扫(原版按 metadata role==trader 增量维护;
///   我方训练的商队模板自带 Trader 类,覆盖等价)。
/// - DiplomacyChanged 事件:内核 AIEventBuffer 无此类型,外交变化不触发重勘
///   (待事件缓冲补该类型后接入;OwnershipChanged/Create 已覆盖主要触发源)。
/// - activateProspection 原版本同步 buildManager.setBuildable(market/dock);
///   我方无 buildManager 禁建表——仅置勘探旗(勘探失败即自闭,事件再激活)。
/// - 商船顺手捡宝(原版 updateTrader 首段 gatherTreasure,每5回合):未移植。
/// - Resources.GetTradableCodes 门:内核四种资源恒可贸易,视为恒真。</summary>
public sealed class TradeManager
{
    private readonly PetraConfig _config;

    /// <summary>原版 gameState.ai.HQ 反链(FindMarketLocation/NavalManager.RequireTransport/
    /// NavalMap 判定用)。HQ 构造时注入。</summary>
    public Headquarters? Hq;

    // 贸易路线（原版 tradeRoute:当前启用线,两市场均建成;
    // potentialTradeRoute:最佳潜在线,含地基市场——isNewMarketWorth 的比较基准）
    public TradeRoute? Route;
    public TradeRoute? PotentialRoute;
    public int TargetNumTraders;
    public bool RouteProspection = true;

    // 商队实体集合（每 think 重建）
    private List<AIEntity> _traders = new();

    public TradeManager(PetraConfig config)
    {
        _config = config;
        TargetNumTraders = config.Economy.TargetNumTraders;
    }

    /// <summary>原版 minimalGain(tradeManager.js:30 init):海图 3,否则 5。
    /// HQ.FindMarketLocation 的期望收益门也用此值(原版 this.tradeManager.minimalGain)。</summary>
    public int MinimalGain => Hq?.NavalMap == true ? 3 : 5;

    /// <summary>主更新（原版 tradeManager.js:685-712 update 逐字）。</summary>
    public void Update(GameState gameState, AIEventBuffer events, QueueManager queues)
    {
        // 1. 易物（有市场时;资源严重失衡才换,节流每 5 回合）
        if (Route != null || gameState.GetOwnStructures().Filter(e => e.HasClass("Market")).HasEntities())
            PerformBarter(gameState);

        if (_config.Difficulty <= DifficultyLevel.VeryEasy) return;

        // 2. 市场/外交变动（原版:checkEvents 返回 true → 逐商队 checkTrader + checkRoutes）
        if (CheckEvents(gameState, events))
        {
            RebuildTraders(gameState);
            foreach (var trader in _traders)
                CheckTrader(gameState, trader);
            CheckRoutes(gameState);
        }

        // 3. 有路线时：更新商队 + 训练新商队 + 重设贸易品
        // （回合门控用 playedTurn = Net.CurrentTurn;Net 缺失的测试环境回落事件计数）
        uint turn = gameState.Net?.CurrentTurn ?? (uint)gameState.Events.Events.Count;
        if (Route != null)
        {
            RebuildTraders(gameState);
            foreach (var trader in _traders)
                UpdateTrader(gameState, trader);

            if (turn % 5 == 0)
                TrainMoreTraders(gameState, queues);
            if (turn % 60 == 0)
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

    /// <summary>检查事件（原版 checkEvents 逐字:市场换名/销毁/建成/易主）。
    /// 返回 true = 需复核商队路线并重选路线。</summary>
    private bool CheckEvents(GameState gameState, AIEventBuffer events)
    {
        // 市场换名(晋升/变身/地基落成换号):商队路线元数据同步换 id(原版 EntityRenamed 段)。
        foreach (var ev in events.Events)
        {
            if (ev.Type != AIEventType.EntityRenamed) continue;
            var ent = gameState.GetEntityById((uint)ev.IntParam);
            if (ent == null || !ent.HasClass("Trade")) continue;
            foreach (var trader in _traders)
            {
                if (!TryGetRouteMeta(gameState, trader.Id, out uint src, out uint tgt)) continue;
                if (src == ev.Entity) src = (uint)ev.IntParam;
                else if (tgt == ev.Entity) tgt = (uint)ev.IntParam;
                else continue;
                SetRouteMeta(gameState, trader.Id, src, tgt);
            }
        }

        // 路线/潜在路线的市场被毁 → 重启勘探(原版 Destroy 段)。
        foreach (var ev in events.Events)
        {
            if (ev.Type != AIEventType.Destroy) continue;
            if ((Route != null && (ev.Entity == Route.Source || ev.Entity == Route.Target))
                || (PotentialRoute != null
                    && (ev.Entity == PotentialRoute.Source || ev.Entity == PotentialRoute.Target)))
            {
                ActivateProspection();
                return true;
            }
        }

        // 新市场建成/出现(我方或盟友;地基不算) → 重启勘探(原版 Create 段;
        // 另纳 ConstructionFinished——内核地基完工的确定性信号,语义等价于原版
        // "落成换号后的新实体 Create")。
        foreach (var ev in events.Events)
        {
            if (ev.Type != AIEventType.Create && ev.Type != AIEventType.ConstructionFinished) continue;
            var ent = gameState.GetEntityById(ev.Entity);
            if (ent == null || ent.IsFoundation || !ent.HasClass("Trade")
                || !gameState.IsPlayerAlly(ent.Owner))
                continue;
            ActivateProspection();
            return true;
        }

        // 市场易主(含被占领;新旧主均非盟友的换手不算) → 重启勘探(原版 OwnershipChanged 段)。
        foreach (var ev in events.Events)
        {
            if (ev.Type != AIEventType.OwnershipChanged) continue;
            if (!gameState.IsPlayerAlly(ev.IntParam) && !gameState.IsPlayerAlly(ev.IntParam2))
                continue;
            var ent = gameState.GetEntityById(ev.Entity);
            if (ent == null || ent.IsFoundation || !ent.HasClass("Trade")) continue;
            ActivateProspection();
            return true;
        }

        return false;
    }

    /// <summary>原版 activateProspection:置勘探旗(+原版此处 setBuildable(market/dock),
    /// 我方无 buildManager 禁建表——仅置旗,见类注释)。</summary>
    private void ActivateProspection()
    {
        RouteProspection = true;
    }

    /// <summary>重建商队集合(原版按 metadata role==trader;我方按 Trader 类,见类注释)。</summary>
    private void RebuildTraders(GameState gameState)
    {
        _traders = gameState.GetOwnUnits().Filter(e => e.HasClass("Trader")).ToList();
    }

    /// <summary>检查/创建贸易路线（原版 checkRoutes 441-582 逐字移植）。
    /// 市场池 = 我方 Trade 建筑 × 排他盟友 Trade 实体(无盟友市场时我方自成对);
    /// 市场对须同陆区(陆线不穿敌领)或同海域;收益 = round(模板倍率 ×
    /// TradeGain(距离², mapSize)),低于 minimalGain 弃;建成市场对出最佳 candidate
    /// (= Route),含地基市场对出最佳 potential(= PotentialRoute)。
    /// 给 accessIndex(商队可达区)时返回该区最佳线(水区配海线/陆区配陆线,
    /// 陆区无配时回落最佳陆线 bestLand);否则返回 null 并以字段承载结果。</summary>
    private TradeRoute? CheckRoutes(GameState gameState, ushort? accessIndex = null)
    {
        var market1 = gameState.GetOwnStructures().Filter(e => e.HasClass("Trade")).ToList();
        var market2 = gameState.GetAllyEntities().Filter(e => e.HasClass("Trade")).ToList();
        if (market1.Count + market2.Count < 2)  // 市场不足,等建(原版同)
        {
            Route = null;
            PotentialRoute = null;
            return null;
        }

        bool onlyOurs = market2.Count == 0;
        if (onlyOurs)
            market2 = market1;
        TradeRoute? candidate = null, potential = null, bestIndex = null, bestLand = null;

        double mapSize = gameState.MapSize;
        var gains = gameState.GetTraderTemplatesGains();

        foreach (var m1 in market1)
        {
            if (m1.Position2D == default) continue;
            ushort access1 = EntityExtend.GetLandAccess(gameState, m1);
            ushort? sea1 = m1.HasClass("Naval") ? EntityExtend.GetSeaAccess(gameState, m1) : null;
            foreach (var m2 in market2)
            {
                if (onlyOurs && m1.Id >= m2.Id) continue;
                if (m2.Position2D == default) continue;
                ushort access2 = EntityExtend.GetLandAccess(gameState, m2);
                ushort? sea2 = m2.HasClass("Naval") ? EntityExtend.GetSeaAccess(gameState, m2) : null;
                ushort? land = access1 == access2 ? access1 : null;
                ushort? sea = sea1.HasValue && sea1 == sea2 ? sea1 : null;
                if (land == null && sea == null) continue;
                if (land != null && EntityExtend.IsLineInsideEnemyTerritory(
                        gameState, m1.Position2D, m2.Position2D))
                    continue;
                float gainMultiplier;
                if (land != null && gains.Land != null)
                    gainMultiplier = gains.Land.Value;
                else if (sea != null && gains.Naval != null)
                    gainMultiplier = gains.Naval.Value;
                else
                    continue;
                // JS Math.round = 远离零取整(收益恒正;C# 默认银行家舍入会偏)。
                int gain = (int)System.Math.Round(gainMultiplier * MarketComponent.TradeGain(
                    AIUtils3.SquareDistanceMeters(m1.Position2D, m2.Position2D), mapSize),
                    System.MidpointRounding.AwayFromZero);
                if (gain < MinimalGain) continue;

                if (!m1.IsFoundation && !m2.IsFoundation)
                {
                    if (accessIndex.HasValue && gameState.Accessibility != null)
                    {
                        string regionType = gameState.Accessibility.GetRegionType(accessIndex.Value);
                        if (regionType == "water" && sea == accessIndex)
                        {
                            if (bestIndex == null || gain >= bestIndex.Gain)
                                bestIndex = new TradeRoute(m1.Id, m2.Id, gain, land, sea);
                        }
                        else if (regionType == "land" && land == accessIndex)
                        {
                            if (bestIndex == null || gain >= bestIndex.Gain)
                                bestIndex = new TradeRoute(m1.Id, m2.Id, gain, land, sea);
                        }
                        else if (regionType == "land")
                        {
                            if (bestLand == null || gain >= bestLand.Gain)
                                bestLand = new TradeRoute(m1.Id, m2.Id, gain, land, sea);
                        }
                    }
                    if (candidate == null || gain >= candidate.Gain)
                        candidate = new TradeRoute(m1.Id, m2.Id, gain, land, sea);
                }
                if (potential == null || gain >= potential.Gain)
                    potential = new TradeRoute(m1.Id, m2.Id, gain, land, sea);
            }
        }

        PotentialRoute = potential is { Gain: >= 1 } ? potential : null;

        if (candidate is not { Gain: >= 1 })
        {
            Route = null;   // 原版:"no better trade route possible"
            return null;
        }
        Route = candidate;   // 收益最优线——市场对变化后此线即动态切换点

        if (accessIndex.HasValue && gameState.Accessibility != null)
        {
            if (bestIndex is { Gain: > 0 }) return bestIndex;
            if (gameState.Accessibility.GetRegionType(accessIndex.Value) == "land"
                && bestLand is { Gain: > 0 })
                return bestLand;
            return null;
        }
        return Route;
    }

    /// <summary>市场建/毁后复核商队路线（原版 checkTrader 585-619 逐字）:
    /// 当前路线与新最优线两端市场不完全重合 → 清路线元数据(下轮 updateTrader 重派);
    /// 无线可派的陆商队 → 回最近同陆区基地(原版 getBestBase+moveToRange),
    /// 无基地可归 → 原地停。</summary>
    private void CheckTrader(GameState gameState, AIEntity trader)
    {
        if (!TryGetRouteMeta(gameState, trader.Id, out uint src, out uint tgt)) return;

        if (trader.Position2D == default)
        {
            // 驻军中——出营时再议(原版同)。
            ClearRouteMeta(gameState, trader.Id);
            return;
        }

        ushort access = trader.HasClass("Ship")
            ? EntityExtend.GetSeaAccess(gameState, trader)
            : EntityExtend.GetLandAccess(gameState, trader);
        var possibleRoute = CheckRoutes(gameState, access);
        if (possibleRoute == null
            || possibleRoute.Source != src && possibleRoute.Source != tgt
            || possibleRoute.Target != src && possibleRoute.Target != tgt)
        {
            ClearRouteMeta(gameState, trader.Id);   // updateTrader 将重派
            if (possibleRoute == null && !trader.HasClass("Ship"))
            {
                var anchor = NearestBaseAnchor(gameState, trader, access);
                if (anchor != null)
                {
                    gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Move(
                        (uint)gameState.PlayerId, trader.Id,
                        anchor.Position2D.X, anchor.Position2D.Y));
                    return;
                }
            }
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Stop(
                (uint)gameState.PlayerId, trader.Id));
        }
    }

    /// <summary>最近同陆区基地的 anchor(原版 getBestBase 简化:bases 表内同
    /// accessIndex 且有活 anchor 的最近者;无 → null)。</summary>
    private AIEntity? NearestBaseAnchor(GameState gameState, AIEntity ent, ushort access)
    {
        if (Hq == null) return null;
        AIEntity? best = null;
        float bestDist = float.MaxValue;
        foreach (var b in Hq.BasesManager.Bases)
        {
            if (b.AnchorId == null || (b.AccessIndex ?? 0) != access) continue;
            var anchor = gameState.GetEntityById(b.AnchorId.Value);
            if (anchor == null || anchor.Position2D == default) continue;
            float d = AIUtils3.SquareDistanceMeters(anchor.Position2D, ent.Position2D);
            if (d < bestDist) { bestDist = d; best = anchor; }
        }
        return best;
    }

    /// <summary>更新单个商队（原版 updateTrader 127-180 逐字;捡宝段未移植,见类注释）。
    /// 空闲商队按其可达区重查最佳线(checkRoutes(access)),指派近端为源、远端为目标的
    /// SetupTradeRoute;陆商队与路线陆区不符 → 请求海军运输(原版 requireTransport)。</summary>
    private void UpdateTrader(GameState gameState, AIEntity trader)
    {
        if (Route == null) return;
        if (!trader.IsIdle || trader.Position2D == default) return;
        if (gameState.Metadata.GetObject(trader.Id, "transport") != null) return;

        ushort access = trader.HasClass("Ship")
            ? EntityExtend.GetSeaAccess(gameState, trader)
            : EntityExtend.GetLandAccess(gameState, trader);
        var route = CheckRoutes(gameState, access);
        if (route == null) return;
        var routeSource = gameState.GetEntityById(route.Source);
        var routeTarget = gameState.GetEntityById(route.Target);
        if (routeSource == null || routeTarget == null) return;

        bool nearerSource = true;
        if (AIUtils3.SquareDistanceMeters(routeTarget.Position2D, trader.Position2D)
            < AIUtils3.SquareDistanceMeters(routeSource.Position2D, trader.Position2D))
            nearerSource = false;

        if (!trader.HasClass("Ship") && route.Land != access)
        {
            // 陆商队不在路线陆区 → 请求摆渡(原版 navalManager.requireTransport)。
            if (Hq != null && route.Land.HasValue)
                Hq.NavalManager.RequireTransport(gameState, trader, access, route.Land.Value,
                    nearerSource ? routeSource.Position2D : routeTarget.Position2D);
            return;
        }

        // 近端为源:原版 ent.tradeRoute(远, 近) / ent.tradeRoute(近, 远)。
        gameState.SubmitCommand(nearerSource
            ? ZeroAD.Sim.Net.NetCommand.SetupTradeRoute(
                (uint)gameState.PlayerId, trader.Id, route.Target, route.Source)
            : ZeroAD.Sim.Net.NetCommand.SetupTradeRoute(
                (uint)gameState.PlayerId, trader.Id, route.Source, route.Target));
        SetRouteMeta(gameState, trader.Id, route.Source, route.Target);
    }

    /// <summary>训练更多商队（原版 trainMoreTraders 简化版:路线存在 + 队列无在排即补）。
    /// 原版另区分陆/海商队配额与在训计数——记录在案,待 naval 商队接入后补齐。</summary>
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

    /// <summary>寻找新市场位置（原版 prospectForNewMarket 621-671 逐字移植):
    /// 队列已有 Trade 类计划/不可建/无任何市场 → 返;重查路线后经
    /// HQ.FindMarketLocation 评估新市场的期望收益;无位 → 停勘探(原版此处
    /// 对已有市场者 setUnbuildable,我方以停勘探等价,事件再激活);
    /// 收益不显著优于现潜在线(isNewMarketWorth) → 不建;首条路线时
    /// economicBuilding 优先级 ×2(计划离队列经 QueueToReset 复位)。</summary>
    private void ProspectForNewMarket(GameState gameState, QueueManager queues)
    {
        if (queues.GetQueue("economicBuilding")?.HasQueuedUnitsWithClass(gameState, "Trade") == true
            || queues.GetQueue("dock")?.HasQueuedUnitsWithClass(gameState, "Trade") == true)
            return;
        if (Hq == null) return;
        if (!Hq.CanBuild(gameState, "structures/{civ}/market")) return;
        if (!gameState.GetOwnStructures().Filter(e => e.HasClass("Trade")).HasEntities()
            && !gameState.GetAllyEntities().Filter(e => e.HasClass("Trade")).HasEntities())
            return;
        var template = gameState.GetTemplate(gameState.ApplyCiv("structures/{civ}/market"));
        if (template == null) return;

        CheckRoutes(gameState);
        var marketPos = Hq.FindMarketLocation(gameState, template);
        if (marketPos == null || marketPos.Value.Gain == 0)
        {
            // 无位置:原版按"已有市场"分流(setUnbuildable / 停勘探)——
            // 我方统一停勘探(市场事件经 ActivateProspection 再激活,见类注释)。
            RouteProspection = false;
            return;
        }
        RouteProspection = false;
        if (!IsNewMarketWorth(marketPos.Value.Gain))
            return;   // 有位但收益不显著优于现路线

        if (Route == null)
            queues.ChangePriority("economicBuilding", 2 * _config.Priorities["economicBuilding"]);
        // 选址不在此固化——ConstructionPlan.Start 的 Market 分支在启动时
        // 重走 HQ.FindMarketLocation(原版 queueplanBuilding 同款,"启动时再算")。
        var plan = new ConstructionPlan(gameState, "structures/{civ}/market");
        if (Route == null)
            plan.QueueToReset = "economicBuilding";
        queues.AddPlan("economicBuilding", plan);
    }

    /// <summary>新市场是否值得建（原版 isNewMarketWorth 673-683 逐字）:
    /// 低于 minimalGain 不值;不显著优于现潜在线(< 2 倍且 < +20)不值。</summary>
    private bool IsNewMarketWorth(int expectedGain)
    {
        if (expectedGain < MinimalGain) return false;
        if (PotentialRoute != null && expectedGain < 2 * PotentialRoute.Gain
            && expectedGain < PotentialRoute.Gain + 20)
            return false;
        return true;
    }

    // ── 商队路线元数据(route-source/route-target 两 int;原版存 route 对象,
    // 我方 EntityMetadata 序列化只支持标量 → 拆键存,读档保真)──

    private static void SetRouteMeta(GameState gameState, uint traderId, uint source, uint target)
    {
        gameState.Metadata.Set(traderId, "route-source", (int)source);
        gameState.Metadata.Set(traderId, "route-target", (int)target);
    }

    private static void ClearRouteMeta(GameState gameState, uint traderId)
    {
        gameState.Metadata.Remove(traderId, "route-source");
        gameState.Metadata.Remove(traderId, "route-target");
    }

    private static bool TryGetRouteMeta(GameState gameState, uint traderId,
        out uint source, out uint target)
    {
        source = target = 0;
        if (gameState.Metadata.GetObject(traderId, "route-source") is not int s || s <= 0)
            return false;
        if (gameState.Metadata.GetObject(traderId, "route-target") is not int t || t <= 0)
            return false;
        source = (uint)s;
        target = (uint)t;
        return true;
    }

    /// <summary>贸易路线（原版 {source, target, gain, land, sea}:
    /// 两端市场 + 预期收益 + 陆区/海域(0 = 无此通道)）。</summary>
    public sealed record TradeRoute(uint Source, uint Target, int Gain, ushort? Land, ushort? Sea);
}
