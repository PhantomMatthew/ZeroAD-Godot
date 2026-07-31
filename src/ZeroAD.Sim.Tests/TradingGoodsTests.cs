using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Net;

namespace ZeroAD.Sim.Tests;

// 贸易品比例命令(SetTradingGoods):4 资源百分比,和=100,负值/和≠100 拒收。
// 覆盖 PlayerComponent.Get/SetTradingGoods 与执行器路由(Apply(SetTradingGoods))。
public sealed class TradingGoodsTests
{
    private static PlayerComponent NewPlayer()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PlayerComponent());
        cm.Players.AddPlayer(1, e);
        return cm.Players.GetPlayerEntity(1)!;
    }

    [Fact]
    public void Default_Is_25_Each()
    {
        var p = NewPlayer();
        var g = p.GetTradingGoods();
        Assert.Equal(25, g[ResourceType.Food]);
        Assert.Equal(25, g[ResourceType.Wood]);
        Assert.Equal(25, g[ResourceType.Stone]);
        Assert.Equal(25, g[ResourceType.Metal]);
    }

    [Fact]
    public void Set_Valid_Sum100_Updates()
    {
        var p = NewPlayer();
        p.SetTradingGoods(new Dictionary<ResourceType, int>
        {
            [ResourceType.Wood] = 50,
            [ResourceType.Food] = 50,
            [ResourceType.Stone] = 0,
            [ResourceType.Metal] = 0,
        });
        var g = p.GetTradingGoods();
        Assert.Equal(50, g[ResourceType.Wood]);
        Assert.Equal(50, g[ResourceType.Food]);
        Assert.Equal(0, g[ResourceType.Stone]);
        Assert.Equal(0, g[ResourceType.Metal]);
    }

    [Fact]
    public void Set_SumNot100_Rejected()
    {
        var p = NewPlayer();
        p.SetTradingGoods(new Dictionary<ResourceType, int>
        {
            [ResourceType.Wood] = 50,
            [ResourceType.Food] = 30, // sum 80
            [ResourceType.Stone] = 0,
            [ResourceType.Metal] = 0,
        });
        // 默认值不变(25/25/25/25)
        Assert.Equal(25, p.GetTradingGoods()[ResourceType.Wood]);
    }

    [Fact]
    public void Set_Negative_Rejected()
    {
        var p = NewPlayer();
        p.SetTradingGoods(new Dictionary<ResourceType, int>
        {
            [ResourceType.Wood] = 150,
            [ResourceType.Food] = -50,
            [ResourceType.Stone] = 0,
            [ResourceType.Metal] = 0,
        });
        Assert.Equal(25, p.GetTradingGoods()[ResourceType.Wood]);
    }

    [Fact]
    public void Executor_SetTradingGoods_RoutesAndApplies()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PlayerComponent());
        cm.Players.AddPlayer(1, e);
        var p = cm.Players.GetPlayerEntity(1)!;
        // 编码:wood%=70, food%=30, stone%=0, metal%=0(sum 100)。
        var exec = new SimCommandExecutor(cm);
        exec.Apply(NetCommand.SetTradingGoods(player: 1, wood: 70, food: 30, stone: 0, metal: 0));
        var g = p.GetTradingGoods();
        Assert.Equal(70, g[ResourceType.Wood]);
        Assert.Equal(30, g[ResourceType.Food]);
    }

    [Fact]
    public void Executor_SetTradingGoods_SumNot100_DropsSilently()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PlayerComponent());
        cm.Players.AddPlayer(1, e);
        var p = cm.Players.GetPlayerEntity(1)!;
        var exec = new SimCommandExecutor(cm);
        exec.Apply(NetCommand.SetTradingGoods(player: 1, wood: 10, food: 10, stone: 10, metal: 10)); // sum 40
        Assert.Equal(25, p.GetTradingGoods()[ResourceType.Wood]); // 默认未变
    }
}
