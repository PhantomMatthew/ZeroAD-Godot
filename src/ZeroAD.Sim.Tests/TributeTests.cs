using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Net;

namespace ZeroAD.Sim.Tests;

// 进贡命令(Tribute):扣源资源、加目的资源;校验双方 active、amount>0、余额足。
// 覆盖 PlayerComponent.TributeResource 与执行器路由(Apply(Tribute))。
public sealed class TributeTests
{
    private static (ComponentManager cm, PlayerComponent p1, PlayerComponent p2) NewTwoPlayers()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var e1 = cm.CreateEntity();
        cm.AddComponent(e1, new PlayerComponent());
        cm.Players.AddPlayer(1, e1);
        var e2 = cm.CreateEntity();
        cm.AddComponent(e2, new PlayerComponent());
        cm.Players.AddPlayer(2, e2);
        var p1 = cm.Players.GetPlayerEntity(1)!;
        var p2 = cm.Players.GetPlayerEntity(2)!;
        return (cm, p1, p2);
    }

    [Fact]
    public void Tribute_MovesResources_SourceToDest()
    {
        var (_, p1, p2) = NewTwoPlayers();
        p1.Wood = 500; p2.Wood = 0;
        Assert.True(p1.TributeResource(p2, ResourceType.Wood, 100));
        Assert.Equal(400, p1.Wood);
        Assert.Equal(100, p2.Wood);
    }

    [Fact]
    public void Tribute_InsufficientFunds_Rejected()
    {
        var (_, p1, p2) = NewTwoPlayers();
        p1.Food = 50; p2.Food = 0;
        Assert.False(p1.TributeResource(p2, ResourceType.Food, 100));
        Assert.Equal(50, p1.Food);
        Assert.Equal(0, p2.Food);
    }

    [Fact]
    public void Tribute_InactiveDest_Rejected()
    {
        var (_, p1, p2) = NewTwoPlayers();
        p1.Metal = 500;
        p2.SetDefeated();
        Assert.False(p1.TributeResource(p2, ResourceType.Metal, 100));
        Assert.Equal(500, p1.Metal); // 未扣
    }

    [Fact]
    public void Tribute_NonPositiveAmount_Rejected()
    {
        var (_, p1, p2) = NewTwoPlayers();
        p1.Stone = 500;
        Assert.False(p1.TributeResource(p2, ResourceType.Stone, 0));
        Assert.False(p1.TributeResource(p2, ResourceType.Stone, -10));
        Assert.Equal(500, p1.Stone);
    }

    [Fact]
    public void Executor_Tribute_RoutesAndExchanges()
    {
        var (cm, p1, p2) = NewTwoPlayers();
        p1.Wood = 1000; p2.Wood = 0;
        var exec = new SimCommandExecutor(cm);
        exec.Apply(NetCommand.Tribute(player: 1, destPlayer: 2, ResourceType.Wood, 500));
        Assert.Equal(500, p1.Wood);
        Assert.Equal(500, p2.Wood);
    }
}
