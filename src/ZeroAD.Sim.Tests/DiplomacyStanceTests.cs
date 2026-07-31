using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Net;

namespace ZeroAD.Sim.Tests;

// 外交立场命令(SetStance)+ 单向恶化规则(原版 Diplomacy.js OnDiplomacyChanged)+ Team 字段写入。
// 覆盖组件层(SetStanceToward)与执行器路由(Apply(SetStance))两条路径。
public sealed class DiplomacyStanceTests
{
    private static (ComponentManager cm, EntityId p1, EntityId p2) NewTwoPlayers()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent());
        cm.AddComponent(p1, new DiplomacyComponent());
        cm.Players.AddPlayer(1, p1);
        var p2 = cm.CreateEntity();
        cm.AddComponent(p2, new PlayerComponent());
        cm.AddComponent(p2, new DiplomacyComponent());
        cm.Players.AddPlayer(2, p2);
        return (cm, p1, p2);
    }

    private static DiplomacyComponent Dip(ComponentManager cm, EntityId e) =>
        cm.QueryInterface<DiplomacyComponent>(e)!;

    [Fact]
    public void SetStanceToward_SetsDirectStance()
    {
        var (cm, p1, p2) = NewTwoPlayers();
        Dip(cm, p1).SetStanceToward(selfId: 1, Dip(cm, p2), otherId: 2, DiplomacyComponent.Enemy);
        Assert.Equal(DiplomacyComponent.Enemy, Dip(cm, p1).GetStance(2));
    }

    [Fact]
    public void Worsening_RaisesEnemy_DropsTheirStanceToMatch()
    {
        // 互为盟友;P1 单方面降为敌 → P2 对 P1 也被降到敌(单向恶化)。
        var (cm, p1, p2) = NewTwoPlayers();
        Dip(cm, p1).SetAlly(2);
        Dip(cm, p2).SetAlly(1);
        Dip(cm, p1).SetStanceToward(1, Dip(cm, p2), 2, DiplomacyComponent.Enemy);
        Assert.Equal(DiplomacyComponent.Enemy, Dip(cm, p1).GetStance(2));
        Assert.Equal(DiplomacyComponent.Enemy, Dip(cm, p2).GetStance(1));
    }

    [Fact]
    public void Improvement_DoesNotRaiseTheirStance()
    {
        // 互为敌;P1 单方面升为盟友 → P2 对 P1 仍为敌(只恶化不改善)。
        var (cm, p1, p2) = NewTwoPlayers();
        Dip(cm, p1).SetEnemy(2);
        Dip(cm, p2).SetEnemy(1);
        Dip(cm, p1).SetStanceToward(1, Dip(cm, p2), 2, DiplomacyComponent.Ally);
        Assert.Equal(DiplomacyComponent.Ally, Dip(cm, p1).GetStance(2));
        Assert.Equal(DiplomacyComponent.Enemy, Dip(cm, p2).GetStance(1));
    }

    [Fact]
    public void Executor_SetStance_RoutesAndAppliesWorsening()
    {
        var (cm, p1, p2) = NewTwoPlayers();
        Dip(cm, p1).SetAlly(2);
        Dip(cm, p2).SetAlly(1);
        var exec = new SimCommandExecutor(cm);
        exec.Apply(NetCommand.SetStance(player: 1, targetPlayer: 2, DiplomacyComponent.Enemy));
        Assert.Equal(DiplomacyComponent.Enemy, Dip(cm, p2).GetStance(1));
    }

    [Fact]
    public void IsTeamLocked_False_UntilCeasefirePorted()
    {
        var (cm, p1, _) = NewTwoPlayers();
        Assert.False(Dip(cm, p1).IsTeamLocked());
    }

    [Fact]
    public void SeedDiplomacyFromTeams_WritesTeamField()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        for (int p = 1; p <= 3; p++)
        {
            var ent = cm.CreateEntity();
            cm.AddComponent(ent, new PlayerComponent());
            cm.AddComponent(ent, new DiplomacyComponent());
            cm.Players.AddPlayer(p, ent);
        }
        cm.Players.SeedDiplomacyFromTeams(new System.Collections.Generic.Dictionary<int, int>
        {
            [1] = 0, [2] = 0, [3] = 1,
        });
        Assert.Equal(0, cm.Players.GetPlayerEntity(1)!.Team);
        Assert.Equal(0, cm.Players.GetPlayerEntity(2)!.Team);
        Assert.Equal(1, cm.Players.GetPlayerEntity(3)!.Team);
        // 同队 → 互盟;异队 → 互敌。
        Assert.True(Dip(cm, cm.Players.GetPlayerEntityId(1)!.Value).IsAlly(2));
        Assert.True(Dip(cm, cm.Players.GetPlayerEntityId(1)!.Value).IsEnemy(3));
    }
}
