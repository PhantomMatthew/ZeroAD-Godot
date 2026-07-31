using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Net;

namespace ZeroAD.Sim.Tests;

// 易物系统(BarterSystem,去价漂移):静态价 truePrice±ConstantDifference(110/90),
// 换算量=round(sellPrice/buyPrice*amount),amount∈{100,500},须有市场。
// 覆盖 BarterSystem 直调与执行器路由(Apply(Barter))。
public sealed class BarterTests
{
    private static (ComponentManager cm, PlayerComponent p1) NewPlayerWithMarket()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var e1 = cm.CreateEntity();
        cm.AddComponent(e1, new PlayerComponent());
        cm.Players.AddPlayer(1, e1);
        var market = cm.CreateEntity();
        cm.AddComponent(market, new MarketComponent());
        cm.AddComponent(market, new OwnershipComponent { PlayerId = 1 });
        return (cm, cm.Players.GetPlayerEntity(1)!);
    }

    [Fact]
    public void StaticPrices_Are_TruePrice_PlusMinus_ConstantDifference()
    {
        Assert.Equal(110, BarterSystem.BuyPrice(ResourceType.Wood));
        Assert.Equal(90, BarterSystem.SellPrice(ResourceType.Wood));
        Assert.Equal(110, BarterSystem.BuyPrice(ResourceType.Food));
        Assert.Equal(90, BarterSystem.SellPrice(ResourceType.Food));
    }

    [Fact]
    public void Exchange_SellsWood_BuysFood()
    {
        // round(90/110*100) = round(81.81) = 82
        var (cm, p1) = NewPlayerWithMarket();
        p1.Wood = 1000; p1.Food = 0;
        BarterSystem.ExchangeResources(cm, p1, playerId: 1, ResourceType.Wood, ResourceType.Food, 100);
        Assert.Equal(900, p1.Wood);
        Assert.Equal(82, p1.Food);
    }

    [Fact]
    public void Exchange_MassBarter_500()
    {
        // round(90/110*500) = round(409.09) = 409
        var (cm, p1) = NewPlayerWithMarket();
        p1.Wood = 1000; p1.Food = 0;
        BarterSystem.ExchangeResources(cm, p1, 1, ResourceType.Wood, ResourceType.Food, 500);
        Assert.Equal(500, p1.Wood);
        Assert.Equal(409, p1.Food);
    }

    [Fact]
    public void Exchange_InvalidAmount_NoOp()
    {
        var (cm, p1) = NewPlayerWithMarket();
        p1.Wood = 1000; p1.Food = 0;
        BarterSystem.ExchangeResources(cm, p1, 1, ResourceType.Wood, ResourceType.Food, 300);
        Assert.Equal(1000, p1.Wood);
        Assert.Equal(0, p1.Food);
    }

    [Fact]
    public void Exchange_SameResource_NoOp()
    {
        var (cm, p1) = NewPlayerWithMarket();
        p1.Wood = 1000;
        BarterSystem.ExchangeResources(cm, p1, 1, ResourceType.Wood, ResourceType.Wood, 100);
        Assert.Equal(1000, p1.Wood);
    }

    [Fact]
    public void Exchange_NoMarket_NoOp()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var e1 = cm.CreateEntity();
        cm.AddComponent(e1, new PlayerComponent());
        cm.Players.AddPlayer(1, e1);
        var p1 = cm.Players.GetPlayerEntity(1)!;
        p1.Wood = 1000; p1.Food = 0;
        BarterSystem.ExchangeResources(cm, p1, 1, ResourceType.Wood, ResourceType.Food, 100);
        Assert.Equal(1000, p1.Wood);
        Assert.Equal(0, p1.Food);
    }

    [Fact]
    public void Executor_Barter_RoutesAndExchanges()
    {
        var (cm, p1) = NewPlayerWithMarket();
        p1.Wood = 1000; p1.Food = 0;
        var exec = new SimCommandExecutor(cm);
        exec.Apply(NetCommand.Barter(player: 1, ResourceType.Wood, ResourceType.Food, 100));
        Assert.Equal(900, p1.Wood);
        Assert.Equal(82, p1.Food);
    }
}
