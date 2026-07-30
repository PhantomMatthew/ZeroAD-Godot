using System;
using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// Trader + Market — ports of Trader.js / Market.js / globalscripts/Trade.js.
// A trader shuttles between two markets. Each arrival calls PerformTrade, which pays the
// COMPLETED leg (resources of the goods type picked at the previous departure) and prices the
// next leg from the squared distance between the markets. International routes (different
// market owners) additionally pay each market owner their InternationalBonus share.
//
// 不移植(记录):市场 mirage 切换(MarketMirage/SwitchMarket)、UnitAI 航线 waypoints
// (naval route)、GUI 侧的 CalculateTraderGain(traderTemplate) 分支、StatisticsTracker。
// GarrisonGainMultiplier 舰载商人加成已随 GarrisonHolder(#14)启用(Trader.js:40-63)。

/// <summary>一程贸易的收益(Market.CalculateTraderGain 结果;国际线才有 market 分成)。</summary>
public readonly struct TradeGainResult
{
    public readonly int TraderGain;
    public readonly int Market1Gain;
    public readonly int Market2Gain;
    public TradeGainResult(int traderGain, int market1Gain, int market2Gain)
    {
        TraderGain = traderGain;
        Market1Gain = market1Gain;
        Market2Gain = market2Gain;
    }
}

[Component("Market", "Market")]
public sealed class MarketComponent : ComponentBase, IComponentMessageHandler
{
    // --- P0 货货交易(barter)字段(原 ExtraComponents.cs 的 MarketComponent,已合并于此;
    // 序列化保持原有前 4 字段以兼容既有布局;原版对应 Barter.js,此处暂留) ---
    public int WoodBuyPrice = 100;
    public int FoodBuyPrice = 100;
    public int WoodSellPrice = 70;
    public int FoodSellPrice = 70;

    public void BarterWood(PlayerComponent player, bool sell)
    {
        if (sell)
        {
            if (player.Wood < 100) return;
            player.Wood -= 100;
            player.Metal += WoodSellPrice;
        }
        else
        {
            if (player.Metal < WoodBuyPrice) return;
            player.Metal -= WoodBuyPrice;
            player.Wood += 100;
        }
    }

    public void BarterFood(PlayerComponent player, bool sell)
    {
        if (sell)
        {
            if (player.Food < 100) return;
            player.Food -= 100;
            player.Metal += FoodSellPrice;
        }
        else
        {
            if (player.Metal < FoodBuyPrice) return;
            player.Metal -= FoodBuyPrice;
            player.Food += 100;
        }
    }

    /// <summary>template Market/TradeType:"land" / "naval"(可两者)。</summary>
    public readonly List<string> TradeTypes = new();
    /// <summary>template Market/InternationalBonus:不同主市场间贸易的分成比例。</summary>
    public float InternationalBonus = 0.2f;
    /// <summary>runtime:路由经过本市场的 trader 集合(对应原版 this.traders)。</summary>
    public readonly HashSet<EntityId> Traders = new();

    public bool HasType(string type) => TradeTypes.Contains(type);
    public void AddTrader(EntityId trader) => Traders.Add(trader);
    public void RemoveTrader(EntityId trader) => Traders.Remove(trader);

    // --- globalscripts/Trade.js 公式(双精度数学,.NET 两端确定) ---
    public static double TradeGain(double distanceSquared, double mapSize) =>
        distanceSquared / (1.0 + 0.25 * Math.Sqrt(distanceSquared) / mapSize);

    public static double TradeGainNormalization(double mapSize) =>
        Math.Sqrt(1024.0 / mapSize) / TradeGain(10000.0, mapSize);

    /// <summary>Port of Market.js CalculateTraderGain(secondMarket, _, trader)。
    /// Null = 任一市场无主/无位置/第二市场非市场(原版"市场被毁"路径)。</summary>
    public TradeGainResult? CalculateTraderGain(ComponentManager cm, EntityId secondMarket, EntityId trader)
    {
        var market2 = cm.QueryInterface<MarketComponent>(secondMarket);
        if (market2 == null)
            return null;
        var own1 = cm.QueryInterface<OwnershipComponent>(Entity);
        var own2 = cm.QueryInterface<OwnershipComponent>(secondMarket);
        if (own1 == null || own2 == null)
            return null;
        var pos1 = cm.QueryInterface<PositionComponent>(Entity);
        var pos2 = cm.QueryInterface<PositionComponent>(secondMarket);
        if (pos1 == null || pos2 == null)
            return null;

        double mapSize = GetMapSize(cm);
        var traderCmp = cm.QueryInterface<TraderComponent>(trader);
        if (traderCmp == null)
            return null;
        // gainMultiplier = 地图归一化 × 贸易商模板倍率(修正值管线,对齐原版两处 Apply*)。
        double gainMultiplier = TradeGainNormalization(mapSize)
            * cm.Modifiers.ApplyPrefix("Trader/GainMultiplier", traderCmp.GainMultiplier, trader);

        double dx = pos1.Position.X.ToFloat() - pos2.Position.X.ToFloat();
        double dz = pos1.Position.Z.ToFloat() - pos2.Position.Z.ToFloat();
        double d2 = dx * dx + dz * dz;
        // 原版注释:用欧氏直线距离而非寻路距离,"看起来更公平";收益随距离平方增长。
        int traderGain = (int)Math.Round(gainMultiplier * TradeGain(d2, mapSize));

        int market1Gain = 0, market2Gain = 0;
        if (own1.PlayerId != own2.PlayerId)
        {
            float bonus1 = cm.Modifiers.ApplyPrefix("Market/InternationalBonus", InternationalBonus, Entity);
            float bonus2 = cm.Modifiers.ApplyPrefix("Market/InternationalBonus", market2.InternationalBonus, secondMarket);
            market1Gain = (int)Math.Round(traderGain * bonus1);
            market2Gain = (int)Math.Round(traderGain * bonus2);
        }
        return new TradeGainResult(traderGain, market1Gain, market2Gain);
    }

    /// <summary>地图边长(米):取世界里的 TerrainComponent;无 → 64(内核测试默认)。</summary>
    internal static double GetMapSize(ComponentManager cm)
    {
        foreach (var e in cm.AllEntities)
        {
            var terrain = cm.QueryInterface<TerrainComponent>(e);
            if (terrain != null)
                return terrain.MapSize;
        }
        return 64;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("wbuy", WoodBuyPrice);
        s.NumberI32("fbuy", FoodBuyPrice);
        s.NumberI32("wsell", WoodSellPrice);
        s.NumberI32("fsell", FoodSellPrice);
        s.NumberI32("types_n", TradeTypes.Count);
        foreach (var t in TradeTypes) s.StringASCII("type", t);
        s.NumberFixed("intlBonus", Maths.Fixed.FromFloat(InternationalBonus));
        s.NumberI32("traders_n", Traders.Count);
        foreach (var t in Traders) s.NumberU32("trader", t.Value);
    }

    public override void Deserialize(IDeserializer d)
    {
        WoodBuyPrice = d.NumberI32("wbuy");
        FoodBuyPrice = d.NumberI32("fbuy");
        WoodSellPrice = d.NumberI32("wsell");
        FoodSellPrice = d.NumberI32("fsell");
        TradeTypes.Clear();
        int tn = d.NumberI32("types_n");
        for (int i = 0; i < tn; i++) TradeTypes.Add(d.StringASCII("type"));
        InternationalBonus = d.NumberFixed("intlBonus").ToFloat();
        Traders.Clear();
        int n = d.NumberI32("traders_n");
        for (int i = 0; i < n; i++) Traders.Add(new EntityId(d.NumberU32("trader")));
    }

    public void HandleMessage(IMessage message) { }
}

[Component("Trader", "Trader")]
public sealed class TraderComponent : ComponentBase, IComponentMessageHandler
{
    public EntityId? FirstMarket;   // this.markets[0]
    public EntityId? SecondMarket;  // this.markets[1]
    public int Index = -1;          // this.index — current target market (-1 = none)
    // this.goods = { type, amount: { traderGain, market1Gain, market2Gain } }.
    public ResourceType GoodsType = ResourceType.Metal;
    public int TraderGain;
    public int Market1Gain;
    public int Market2Gain;
    public bool HasGain;            // goods.amount != null(本程已定价)
    public float GainMultiplier = 0.75f;     // template Trader/GainMultiplier
    public float GarrisonGainMultiplier;     // template 可选;0 = 无舰载商人加成(原版 undefined)

    public bool HasBothMarkets() => FirstMarket.HasValue && SecondMarket.HasValue;

    public EntityId? GetCurrentMarket() => Index switch
    {
        0 => FirstMarket,
        1 => SecondMarket,
        _ => null,
    };

    public bool HasMarket(EntityId market) => FirstMarket == market || SecondMarket == market;

    /// <summary>Port of Trader.js CanTrade:Market 件 + 非地基 + 陆/船类型匹配 + 不敌对。</summary>
    public bool CanTrade(ComponentManager cm, EntityId target)
    {
        var market = cm.QueryInterface<MarketComponent>(target);
        if (market == null)
            return false;
        if (cm.QueryInterface<FoundationComponent>(target) != null)
            return false;

        var identity = cm.QueryInterface<IdentityComponent>(Entity);
        bool organicLand = identity?.HasClass("Organic") == true && market.HasType("land");
        bool shipNaval = identity?.HasClass("Ship") == true && market.HasType("naval");
        if (!organicLand && !shipNaval)
            return false;

        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        var targetOwn = cm.QueryInterface<OwnershipComponent>(target);
        if (own == null || targetOwn == null)
            return false;
        // 原版:trader 主的外交 IsEnemy(目标主)→ 拒(盟友/中立/自己均可)。
        return own.PlayerId == targetOwn.PlayerId
            || !cm.Players.IsEnemy(own.PlayerId, targetOwn.PlayerId);
    }

    /// <summary>Port of Trader.js SetTargetMarket(target, source)。路由变更丢弃携带货物。</summary>
    public bool SetTargetMarket(ComponentManager cm, EntityId target, EntityId? source = null)
    {
        var targetMarket = cm.QueryInterface<MarketComponent>(target);
        if (targetMarket == null)
            return false;

        if (source is { } src)
        {
            // 一次性建立双市场路由。
            var srcMarket = cm.QueryInterface<MarketComponent>(src);
            if (srcMarket == null)
                return false;
            ClearMarkets(cm);
            FirstMarket = src;
            srcMarket.AddTrader(Entity);
        }

        if (FirstMarket.HasValue && SecondMarket.HasValue)
        {
            // 双市场已满 → 全部丢弃,target 作第一市场重开。
            ClearMarkets(cm);
            Index = 0;
            FirstMarket = target;
            targetMarket.AddTrader(Entity);
        }
        else if (FirstMarket is { } first)
        {
            // 仅一市场且 target 不同 → 作第二市场(原版此处算了次增益又丢弃,死代码,略)。
            if (target == first)
                return false;
            Index = 0;
            SecondMarket = target;
            targetMarket.AddTrader(Entity);
        }
        else
        {
            Index = 0;
            FirstMarket = target;
            targetMarket.AddTrader(Entity);
        }
        HasGain = false;   // 原版:市场变更 → goods.amount = null
        return true;
    }

    /// <summary>Port of RemoveTargetMarket:仅当只有一个市场且匹配时可移除。</summary>
    public bool RemoveTargetMarket(ComponentManager cm, EntityId target)
    {
        if (SecondMarket.HasValue || FirstMarket != target)
            return false;
        var market = cm.QueryInterface<MarketComponent>(target);
        if (market == null)
            return false;
        market.RemoveTrader(Entity);
        Index = -1;
        FirstMarket = null;
        return true;
    }

    /// <summary>Port of RemoveMarket(市场被毁/外交变化):从路由摘除,字段前移对齐 splice。</summary>
    public void RemoveMarket(ComponentManager cm, EntityId market)
    {
        if (FirstMarket == market)
        {
            cm.QueryInterface<MarketComponent>(market)?.RemoveTrader(Entity);
            FirstMarket = SecondMarket;
            SecondMarket = null;
            Index = FirstMarket.HasValue ? 0 : -1;
        }
        else if (SecondMarket == market)
        {
            cm.QueryInterface<MarketComponent>(market)?.RemoveTrader(Entity);
            SecondMarket = null;
            if (Index > 0) Index = 0;
        }
        HasGain = false;
    }

    /// <summary>Port of StopTrading:清空路由并从各市场注销。</summary>
    public void StopTrading(ComponentManager cm)
    {
        ClearMarkets(cm);
        HasGain = false;
    }

    private void ClearMarkets(ComponentManager cm)
    {
        if (FirstMarket is { } m1)
            cm.QueryInterface<MarketComponent>(m1)?.RemoveTrader(Entity);
        if (SecondMarket is { } m2)
            cm.QueryInterface<MarketComponent>(m2)?.RemoveTrader(Entity);
        FirstMarket = null;
        SecondMarket = null;
        Index = -1;
    }

    /// <summary>Port of Trader.js PerformTrade:结算上一程、选下一程货物、定价下一程。
    /// 返回下一目标市场;null = 市场不一致/无主(对应 INVALID_ENTITY)。</summary>
    public EntityId? PerformTrade(ComponentManager cm, EntityId currentMarket)
    {
        var previousMarket = GetCurrentMarket();
        if (previousMarket != currentMarket)
        {
            HasGain = false;
            return null;
        }

        int count = SecondMarket.HasValue ? 2 : FirstMarket.HasValue ? 1 : 0;
        if (count == 0)
            return null;
        Index = (Index + 1) % count;
        var nextMarket = GetCurrentMarket()!.Value;

        // 结算刚走完的一程(货物在上次出发时已选定)。
        if (HasGain && TraderGain > 0)
            GenerateResources(cm, previousMarket!.Value, nextMarket);

        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        if (own == null || own.PlayerId < 1)
            return null;
        var player = cm.GetPlayerEntity(own.PlayerId);
        if (player == null)
            return null;

        GoodsType = player.GetNextTradingGoods(cm);
        CalculateGainInto(cm, currentMarket, nextMarket);
        return nextMarket;
    }

    /// <summary>Port of GenerateResources:trader 主得 traderGain,两市场主各得国际分成。</summary>
    private void GenerateResources(ComponentManager cm, EntityId currentMarket, EntityId nextMarket)
    {
        AddResources(cm, Entity, TraderGain);
        if (Market1Gain > 0)
            AddResources(cm, currentMarket, Market1Gain);
        if (Market2Gain > 0)
            AddResources(cm, nextMarket, Market2Gain);
    }

    private void AddResources(ComponentManager cm, EntityId ent, int gain)
    {
        var own = cm.QueryInterface<OwnershipComponent>(ent);
        if (own == null || own.PlayerId < 1)
            return;
        cm.GetPlayerEntity(own.PlayerId)?.AddResource(GoodsType, gain);
    }

    /// <summary>Port of CalculateGain:市场定价后,若模板带 GarrisonGainMultiplier 且自身有
    /// GarrisonHolder(商船),舱内每有一个 Trader 件,三项收益 ×(1+mult×n) 分别取整
    /// (Trader.js:40-63;JS Math.round = 远离零)。</summary>
    private void CalculateGainInto(ComponentManager cm, EntityId m1, EntityId m2)
    {
        var market1 = cm.QueryInterface<MarketComponent>(m1);
        var gain = market1?.CalculateTraderGain(cm, m2, Entity);
        if (gain == null)
        {
            HasGain = false;   // 原版:一方市场被毁
            return;
        }
        TraderGain = gain.Value.TraderGain;
        Market1Gain = gain.Value.Market1Gain;
        Market2Gain = gain.Value.Market2Gain;

        if (GarrisonGainMultiplier > 0f)
        {
            var holder = cm.QueryInterface<GarrisonHolderComponent>(Entity);
            if (holder != null)
            {
                int traders = 0;
                foreach (var e in holder.Entities)
                    if (cm.QueryInterface<TraderComponent>(e) != null)
                        traders++;
                if (traders > 0)
                {
                    float mult = 1 + GarrisonGainMultiplier * traders;
                    TraderGain = (int)Math.Round(mult * TraderGain, MidpointRounding.AwayFromZero);
                    Market1Gain = (int)Math.Round(mult * Market1Gain, MidpointRounding.AwayFromZero);
                    Market2Gain = (int)Math.Round(mult * Market2Gain, MidpointRounding.AwayFromZero);
                }
            }
        }
        HasGain = true;
    }

    /// <summary>Port of Trader.js GetRange:1 + 自身障碍半径 × 1.5。</summary>
    public float GetTradeRange(ComponentManager cm)
    {
        float max = 1f;
        var obs = cm.QueryInterface<ObstructionComponent>(Entity);
        if (obs != null)
            max += obs.GetSize().ToFloat() * 1.5f;
        return max;
    }

    /// <summary>到市场的交易射程内判定(edge-to-edge,同 Heal/TreasureCollector 语义)。</summary>
    public bool IsInTradeRange(ComponentManager cm, EntityId market)
    {
        var a = cm.QueryInterface<PositionComponent>(Entity);
        var b = cm.QueryInterface<PositionComponent>(market);
        if (a == null || b == null)
            return false;
        var dx = a.Position.X - b.Position.X;
        var dz = a.Position.Z - b.Position.Z;
        long d2 = (long)dx.InternalValue * dx.InternalValue
                + (long)dz.InternalValue * dz.InternalValue;
        var eff = Maths.Fixed.FromFloat(GetTradeRange(cm));
        var obs = cm.QueryInterface<ObstructionComponent>(market);
        if (obs != null)
            eff += obs.GetSize();
        long r2 = (long)eff.InternalValue * eff.InternalValue;
        return d2 <= r2;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberU32("m1", FirstMarket?.Value ?? 0);
        s.NumberU32("m2", SecondMarket?.Value ?? 0);
        s.NumberI32("index", Index);
        s.NumberI32("goodsType", (int)GoodsType);
        s.NumberI32("traderGain", TraderGain);
        s.NumberI32("market1Gain", Market1Gain);
        s.NumberI32("market2Gain", Market2Gain);
        s.Bool("hasGain", HasGain);
        s.NumberFixed("gainMult", Maths.Fixed.FromFloat(GainMultiplier));
        s.NumberFixed("garrisonGainMult", Maths.Fixed.FromFloat(GarrisonGainMultiplier));
    }

    public override void Deserialize(IDeserializer d)
    {
        uint m1 = d.NumberU32("m1"); FirstMarket = m1 != 0 ? new EntityId(m1) : null;
        uint m2 = d.NumberU32("m2"); SecondMarket = m2 != 0 ? new EntityId(m2) : null;
        Index = d.NumberI32("index");
        GoodsType = (ResourceType)d.NumberI32("goodsType");
        TraderGain = d.NumberI32("traderGain");
        Market1Gain = d.NumberI32("market1Gain");
        Market2Gain = d.NumberI32("market2Gain");
        HasGain = d.Bool("hasGain");
        GainMultiplier = d.NumberFixed("gainMult").ToFloat();
        GarrisonGainMultiplier = d.NumberFixed("garrisonGainMult").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}
