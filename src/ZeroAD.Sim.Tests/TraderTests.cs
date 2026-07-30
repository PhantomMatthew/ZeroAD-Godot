using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

// TraderComponent + MarketComponent — ports of Trader.js / Market.js (+ globalscripts/Trade.js).
// A trader shuttles between two markets; each arrival (PerformTrade) pays the COMPLETED leg
// (goods picked at the previous departure) and computes the next leg's gain from the squared
// distance between markets. Mirage switching + waypoints/route are not ported (noted in code).
public sealed class TraderTests
{
    private static EntityId MakeTrader(ComponentManager cm, int player = 1, float x = 0f, float z = 0f,
        string classes = "Organic Support")
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new UnitMotion());
        var id = new IdentityComponent();
        cm.AddComponent(e, id);
        id.Classes.AddRange(classes.Split(' '));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        var trader = new TraderComponent { GainMultiplier = 0.75f };
        cm.AddComponent(e, trader);
        return e;
    }

    private static EntityId MakeMarket(ComponentManager cm, int player, float x, float z,
        string tradeType = "land", float bonus = 0.2f)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        var market = new MarketComponent { InternationalBonus = bonus };
        cm.AddComponent(e, market);
        market.TradeTypes.Add(tradeType);
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        return e;
    }

    private static PlayerComponent AddPlayer(ComponentManager cm, int playerId)
    {
        var pe = cm.CreateEntity();
        var pc = new PlayerComponent();
        cm.AddComponent(pe, pc);
        pc.Wood = 0; pc.Food = 0; pc.Stone = 0; pc.Metal = 0;   // OnInit 重置后赋值
        cm.Players.AddPlayer(playerId, pe);
        return pc;
    }

    [Fact]
    public void SetTargetMarket_BuildsRouteIncrementally()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var traderE = MakeTrader(cm);
        var trader = cm.QueryInterface<TraderComponent>(traderE)!;
        var a = MakeMarket(cm, 1, 0f, 0f);
        var b = MakeMarket(cm, 1, 100f, 0f);

        Assert.True(trader.SetTargetMarket(cm, a));
        Assert.Equal(a, trader.FirstMarket);
        Assert.Equal(0, trader.Index);
        Assert.False(trader.HasBothMarkets());
        Assert.Contains(traderE, cm.QueryInterface<MarketComponent>(a)!.Traders);

        Assert.False(trader.SetTargetMarket(cm, a));            // 同一市场 → 拒
        Assert.True(trader.SetTargetMarket(cm, b));
        Assert.True(trader.HasBothMarkets());
        Assert.False(trader.HasGain);                           // 路由变更丢弃携带货物

        // 双市场已满 → 全部丢弃,c 作第一市场重开。
        var c = MakeMarket(cm, 1, 50f, 50f);
        Assert.True(trader.SetTargetMarket(cm, c));
        Assert.Equal(c, trader.FirstMarket);
        Assert.Null(trader.SecondMarket);
        Assert.DoesNotContain(traderE, cm.QueryInterface<MarketComponent>(a)!.Traders);
    }

    [Fact]
    public void RemoveTargetMarket_OnlyWhenSingleMarket()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var traderE = MakeTrader(cm);
        var trader = cm.QueryInterface<TraderComponent>(traderE)!;
        var a = MakeMarket(cm, 1, 0f, 0f);
        var b = MakeMarket(cm, 1, 100f, 0f);

        trader.SetTargetMarket(cm, a);
        Assert.True(trader.RemoveTargetMarket(cm, a));
        Assert.Null(trader.FirstMarket);
        Assert.Equal(-1, trader.Index);

        trader.SetTargetMarket(cm, a);
        trader.SetTargetMarket(cm, b);
        Assert.False(trader.RemoveTargetMarket(cm, a));         // 双市场不可移除
    }

    [Fact]
    public void CalculateGain_DistanceSquared_Formula()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var traderE = MakeTrader(cm);
        var trader = cm.QueryInterface<TraderComponent>(traderE)!;
        var a = MakeMarket(cm, 1, 0f, 0f);
        var b = MakeMarket(cm, 1, 100f, 0f);                    // d=100m, d²=10000
        trader.SetTargetMarket(cm, a);
        trader.SetTargetMarket(cm, b);

        // 无地形件 → mapSize 默认 64:normalize=√(1024/64)/TG(10000,64),0.75×4=3(100m 基准)。
        var next = trader.PerformTrade(cm, a);
        Assert.Equal(b, next);
        Assert.True(trader.HasGain);
        Assert.Equal(3, trader.TraderGain);
        Assert.Equal(0, trader.Market1Gain);                    // 同主市场 → 无国际加成
        Assert.Equal(0, trader.Market2Gain);
    }

    [Fact]
    public void PerformTrade_PaysCompletedLeg_OnNextArrival()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var player = AddPlayer(cm, 1);
        var traderE = MakeTrader(cm);
        var trader = cm.QueryInterface<TraderComponent>(traderE)!;
        var a = MakeMarket(cm, 1, 0f, 0f);
        var b = MakeMarket(cm, 1, 100f, 0f);
        trader.SetTargetMarket(cm, a);
        trader.SetTargetMarket(cm, b);

        // 首次 PerformTrade:只取货(选品 + 定价),不结算。
        Assert.Equal(b, trader.PerformTrade(cm, a));
        int total = player.Food + player.Wood + player.Stone + player.Metal;
        Assert.Equal(0, total);

        // 到达 b 再 PerformTrade:结算 a→b 这一程。
        Assert.Equal(a, trader.PerformTrade(cm, b));
        total = player.Food + player.Wood + player.Stone + player.Metal;
        Assert.Equal(3, total);                                 // traderGain=3 进某一种资源
    }

    [Fact]
    public void PerformTrade_InconsistentMarket_Null()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var traderE = MakeTrader(cm);
        var trader = cm.QueryInterface<TraderComponent>(traderE)!;
        var a = MakeMarket(cm, 1, 0f, 0f);
        var b = MakeMarket(cm, 1, 100f, 0f);
        trader.SetTargetMarket(cm, a);
        trader.SetTargetMarket(cm, b);

        // Index=0 → 当前市场应为 a;从 b 结算 = 不一致。
        Assert.Null(trader.PerformTrade(cm, b));
        Assert.False(trader.HasGain);
    }

    [Fact]
    public void PerformTrade_InternationalRoute_PaysBothMarketOwners()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var p1 = AddPlayer(cm, 1);
        var p2 = AddPlayer(cm, 2);
        // 互盟(同队)→ CanTrade 不拦(仅敌对照搬拒)。
        cm.Players.SeedDiplomacyFromTeams(new System.Collections.Generic.Dictionary<int, int> { [1] = 0, [2] = 0 });
        var traderE = MakeTrader(cm, player: 1);
        var trader = cm.QueryInterface<TraderComponent>(traderE)!;
        var a = MakeMarket(cm, 1, 0f, 0f, bonus: 0.2f);
        var b = MakeMarket(cm, 2, 100f, 0f, bonus: 0.4f);
        trader.SetTargetMarket(cm, a);
        trader.SetTargetMarket(cm, b);

        trader.PerformTrade(cm, a);                             // 取货
        Assert.True(trader.HasGain);
        Assert.Equal(3, trader.TraderGain);
        Assert.Equal(1, trader.Market1Gain);                    // round(3×0.2)
        Assert.Equal(1, trader.Market2Gain);                    // round(3×0.4)

        trader.PerformTrade(cm, b);                             // 结算 a→b
        int p1Total = p1.Food + p1.Wood + p1.Stone + p1.Metal;
        int p2Total = p2.Food + p2.Wood + p2.Stone + p2.Metal;
        Assert.Equal(3 + 1, p1Total);                           // traderGain + market1Gain
        Assert.Equal(1, p2Total);                               // market2Gain
    }

    [Fact]
    public void CanTrade_Matrix()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        AddPlayer(cm, 2);                                       // 无外交 → 默认敌
        var traderE = MakeTrader(cm, classes: "Organic Support");
        var trader = cm.QueryInterface<TraderComponent>(traderE)!;
        var land = MakeMarket(cm, 1, 0f, 0f, "land");
        var naval = MakeMarket(cm, 1, 0f, 0f, "naval");
        var enemy = MakeMarket(cm, 2, 0f, 0f, "land");
        var notMarket = MakeTrader(cm, player: 1, x: 9f);

        Assert.True(trader.CanTrade(cm, land));
        Assert.False(trader.CanTrade(cm, naval));               // Organic 不能走 naval
        Assert.False(trader.CanTrade(cm, enemy));               // 敌方市场拒
        Assert.False(trader.CanTrade(cm, notMarket));           // 无 Market 件

        var f = MakeMarket(cm, 1, 0f, 0f, "land");
        cm.AddComponent(f, new FoundationComponent());
        Assert.False(trader.CanTrade(cm, f));                   // 地基市场拒

        var shipE = MakeTrader(cm, classes: "Ship");
        var shipTrader = cm.QueryInterface<TraderComponent>(shipE)!;
        Assert.True(shipTrader.CanTrade(cm, naval));
        Assert.False(shipTrader.CanTrade(cm, land));
    }

    [Fact]
    public void RoundTrip_PreservesRouteAndGoods()
    {
        var trader = new TraderComponent
        {
            FirstMarket = new EntityId(5),
            SecondMarket = new EntityId(8),
            Index = 1,
            GoodsType = ResourceType.Stone,
            TraderGain = 7,
            Market1Gain = 2,
            Market2Gain = 3,
            HasGain = true,
            GainMultiplier = 0.9f,
        };
        var ms = new System.IO.MemoryStream();
        trader.Serialize(new Serialization.BinarySerializer(new System.IO.BinaryWriter(ms)));
        ms.Position = 0;
        var back = new TraderComponent();
        back.Deserialize(new Serialization.BinaryDeserializer(new System.IO.BinaryReader(ms)));

        Assert.Equal(new EntityId(5), back.FirstMarket);
        Assert.Equal(new EntityId(8), back.SecondMarket);
        Assert.Equal(1, back.Index);
        Assert.Equal(ResourceType.Stone, back.GoodsType);
        Assert.Equal(7, back.TraderGain);
        Assert.Equal(2, back.Market1Gain);
        Assert.Equal(3, back.Market2Gain);
        Assert.True(back.HasGain);
        Assert.Equal(0.9f, back.GainMultiplier, 3);
    }

    // --- UnitAI 集成:完整穿梭往返结算 ---

    [Fact]
    public void UnitAI_TradeOrder_ShuttlesAndPaysOut()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var player = AddPlayer(cm, 1);
        var traderE = MakeTrader(cm, x: 0f, z: 0f);
        var trader = cm.QueryInterface<TraderComponent>(traderE)!;
        var a = MakeMarket(cm, 1, 0f, 0f);
        var b = MakeMarket(cm, 1, 60f, 0f);                     // 60m:每程 gain=1(30m 会 round 到 0)
        trader.SetTargetMarket(cm, a);
        trader.SetTargetMarket(cm, b);
        var ai = new UnitAIComponent();
        cm.AddComponent(traderE, ai);

        ai.Trade(null);                                         // back-to-work:目标取当前 index 市场
        ai.Tick(0.1f, cm);                                      // 派发 Order.Trade
        Assert.StartsWith("INDIVIDUAL.TRADE", ai.FsmStateName);

        for (int i = 0; i < 400; i++)
        {
            cm.QueryInterface<UnitMotion>(traderE)?.Tick(0.1f);
            ai.Tick(0.1f, cm);
        }

        int total = player.Food + player.Wood + player.Stone + player.Metal;
        Assert.True(total > 0, $"expected trade income; state={ai.FsmStateName} idx={trader.Index}");
        Assert.False(trader.HasGain == false && trader.Index == -1);
    }

    [Fact]
    public void UnitAI_TradeRejected_WithoutRoute_NoFsmCrash()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var traderE = MakeTrader(cm);                           // 未建立双市场路由 → 拒收
        var ai = new UnitAIComponent();
        cm.AddComponent(traderE, ai);

        ai.Trade(null);
        ai.Tick(0.1f, cm);
        ai.Tick(0.1f, cm);

        Assert.EndsWith("IDLE", ai.FsmStateName);
    }
}
