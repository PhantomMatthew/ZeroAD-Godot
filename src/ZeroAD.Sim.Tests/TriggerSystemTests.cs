using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Triggers;
using Xunit;

namespace ZeroAD.Sim.Tests;

// 数据驱动触发器系统(Trigger.js 移植框架):条件求值、动作执行、Once/Enable/Disable 语义。
public sealed class TriggerSystemTests
{
    private sealed class RecordingSink : ITriggerSink
    {
        public readonly List<string> Messages = new();
        public readonly List<(string Template, int PlayerId, float X, float Z, int Count, float Spread)> Spawns = new();
        public void ShowMessage(string text) => Messages.Add(text);
        public IReadOnlyList<EntityId> SpawnEntities(string template, int playerId, float x, float z, int count, float spread)
        {
            Spawns.Add((template, playerId, x, z, count, spread));
            return System.Array.Empty<EntityId>();
        }
    }

    private static readonly ZeroAD.Sim.Maths.Fixed Dt = ZeroAD.Sim.Maths.Fixed.FromFloat(0.1f);

    private static ComponentManager SetupWorld()
    {
        var cm = new ComponentManager(rngSeed: 1);
        SimSystem.Init(cm);
        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent());
        cm.Players.AddPlayer(1, p1);
        var p2 = cm.CreateEntity();
        cm.AddComponent(p2, new PlayerComponent());
        cm.Players.AddPlayer(2, p2);
        var range = new RangeManager(cm, ZeroAD.Sim.Maths.Fixed.FromInt(256), ZeroAD.Sim.Maths.Fixed.FromInt(256));
        SimSystem.SetRangeManager(range);
        return cm;
    }

    private static EntityId MakeUnit(ComponentManager cm, int owner, float x, float z, params string[] classes)
    {
        var e = cm.CreateEntity();
        var posComp = new PositionComponent();
        cm.AddComponent(e, posComp);
        var id = new IdentityComponent { Name = "U", IsUnit = true };
        id.Classes.AddRange(classes);
        cm.AddComponent(e, id);
        cm.AddComponent(e, new HealthComponent { Current = 100, Max = 100 });
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var fx = ZeroAD.Sim.Maths.Fixed.FromFloat(x);
        var fz = ZeroAD.Sim.Maths.Fixed.FromFloat(z);
        posComp.Position = new ZeroAD.Sim.Maths.FixedVector3D(fx, ZeroAD.Sim.Maths.Fixed.Zero, fz);
        var pos = new ZeroAD.Sim.Maths.FixedVector2D(fx, fz);
        cm.NotifyPositionChanged(e, pos, pos);
        return e;
    }

    private static TriggerCondition Cond(string type, params (string K, string V)[] ps)
    {
        var c = new TriggerCondition { Type = type };
        foreach (var (k, v) in ps) c.Params[k] = v;
        return c;
    }

    private static TriggerAction Act(string type, params (string K, string V)[] ps)
    {
        var a = new TriggerAction { Type = type };
        foreach (var (k, v) in ps) a.Params[k] = v;
        return a;
    }

    [Fact]
    public void TimeElapsed_ShowMessage_FiresOnce()
    {
        var cm = SetupWorld();
        var sink = new RecordingSink();
        var ts = new TriggerSystem { Sink = sink };
        ts.Add(new TriggerDefinition
        {
            Name = "welcome", Once = true,
            Conditions = { Cond("TimeElapsed", ("Seconds", "1")) },
            Actions = { Act("ShowMessage", ("Text", "hello")) }
        });

        for (int i = 0; i < 5; i++) ts.Tick(cm, Dt);   // 0.5s < 1s
        Assert.Empty(sink.Messages);

        for (int i = 0; i < 10; i++) ts.Tick(cm, Dt);  // 累计 1.5s > 1s → 触发
        Assert.Single(sink.Messages);
        Assert.Equal("hello", sink.Messages[0]);

        for (int i = 0; i < 20; i++) ts.Tick(cm, Dt);  // Once:不再触发
        Assert.Single(sink.Messages);
    }

    [Fact]
    public void RepeatingTrigger_FiresEveryTickWhileConditionHolds()
    {
        var cm = SetupWorld();
        var sink = new RecordingSink();
        var ts = new TriggerSystem { Sink = sink };
        ts.Add(new TriggerDefinition
        {
            Name = "pulse", Once = false,
            Conditions = { Cond("TimeElapsed", ("Seconds", "0.2")) },
            Actions = { Act("ShowMessage", ("Text", "tick")) }
        });

        int fired = 0;
        for (int i = 0; i < 5; i++) fired += ts.Tick(cm, Dt);
        Assert.True(fired >= 3, $"repeating trigger should fire most ticks, got {fired}");
    }

    [Fact]
    public void DisabledTrigger_DoesNotFire_UntilEnabledByAction()
    {
        var cm = SetupWorld();
        var sink = new RecordingSink();
        var ts = new TriggerSystem { Sink = sink };
        ts.Add(new TriggerDefinition
        {
            Name = "gate", Once = true,
            Conditions = { Cond("TimeElapsed", ("Seconds", "0.5")) },
            Actions = { Act("EnableTrigger", ("Name", "reveal")) }
        });
        ts.Add(new TriggerDefinition
        {
            Name = "reveal", Enabled = false, Once = true,
            Conditions = { Cond("TimeElapsed", ("Seconds", "0")) },
            Actions = { Act("ShowMessage", ("Text", "revealed")) }
        });

        for (int i = 0; i < 3; i++) ts.Tick(cm, Dt);   // 0.3s:gate 未到时,reveal 禁用
        Assert.Empty(sink.Messages);

        for (int i = 0; i < 5; i++) ts.Tick(cm, Dt);   // gate 触发 → 启用 reveal → 下回合触发
        Assert.Single(sink.Messages);
        Assert.Equal("revealed", sink.Messages[0]);
    }

    [Fact]
    public void PlayerDefeatedCondition_VictoryPlayerAction_EndsMatch()
    {
        var cm = SetupWorld();
        var ts = new TriggerSystem();
        ts.Add(new TriggerDefinition
        {
            Name = "assassin_win", Once = true,
            Conditions = { Cond("PlayerDefeated", ("PlayerId", "2")) },
            Actions = { Act("VictoryPlayer", ("PlayerId", "1")) }
        });

        ts.Tick(cm, Dt);
        Assert.False(cm.Players.GetPlayerEntity(1)!.HasWon());

        cm.Players.GetPlayerEntity(2)!.SetDefeated();
        ts.Tick(cm, Dt);
        Assert.True(cm.Players.GetPlayerEntity(1)!.HasWon());
    }

    [Fact]
    public void AreaContainsEntities_RespectsRadiusAndClassAndPlayer()
    {
        var cm = SetupWorld();
        MakeUnit(cm, 1, 10, 10, "Spear");      // 在区域内
        MakeUnit(cm, 1, 200, 200, "Spear");    // 区域外
        MakeUnit(cm, 2, 10, 12, "Spear");      // 区域内但别的玩家

        var sink = new RecordingSink();
        var ts = new TriggerSystem { Sink = sink };
        // 玩家 1 的 Spear 在 (10,10) 半径 20 内 ≥ 2 才触发——实际只有 1 → 不触发
        ts.Add(new TriggerDefinition
        {
            Name = "strict", Once = true,
            Conditions = { Cond("AreaContainsEntities",
                ("X", "10"), ("Z", "10"), ("Radius", "20"),
                ("PlayerId", "1"), ("Class", "Spear"), ("MinCount", "2")) },
            Actions = { Act("ShowMessage", ("Text", "no")) }
        });
        // 不限玩家:区域内(含玩家2)≥ 2 → 触发
        ts.Add(new TriggerDefinition
        {
            Name = "loose", Once = true,
            Conditions = { Cond("AreaContainsEntities",
                ("X", "10"), ("Z", "10"), ("Radius", "20"), ("MinCount", "2")) },
            Actions = { Act("ShowMessage", ("Text", "yes")) }
        });

        ts.Tick(cm, Dt);
        Assert.Single(sink.Messages);
        Assert.Equal("yes", sink.Messages[0]);
    }

    [Fact]
    public void EntityCountAtMost_FiresWhenPlayerBelowThreshold()
    {
        var cm = SetupWorld();
        MakeUnit(cm, 1, 0, 0);
        MakeUnit(cm, 1, 5, 5);

        var sink = new RecordingSink();
        var ts = new TriggerSystem { Sink = sink };
        ts.Add(new TriggerDefinition
        {
            Name = "lowpop", Once = true,
            Conditions = { Cond("EntityCountAtMost", ("PlayerId", "1"), ("Count", "1")) },
            Actions = { Act("ShowMessage", ("Text", "low")) }
        });

        ts.Tick(cm, Dt);
        Assert.Empty(sink.Messages);   // 2 个实体 > 1

        // 杀一个 → 1 ≤ 1 → 触发
        var e = MakeUnit(cm, 2, 0, 0); // 顺手验证别家实体不计
        var first = cm.QueryInterface<HealthComponent>(e); // 占位防误删
        Assert.NotNull(first);
        foreach (var ent in SimSystem.Range!.GetEntitiesByPlayer(1))
        {
            cm.NotifyOwnerChanged(ent, 1, -1);
            cm.DestroyEntity(ent);
            break;
        }
        ts.Tick(cm, Dt);
        Assert.Single(sink.Messages);
    }

    [Fact]
    public void SpawnEntitiesAction_RoutedToSink()
    {
        var cm = SetupWorld();
        var sink = new RecordingSink();
        var ts = new TriggerSystem { Sink = sink };
        ts.Add(new TriggerDefinition
        {
            Name = "reinforce", Once = true,
            Conditions = { Cond("TimeElapsed", ("Seconds", "0")) },
            Actions = { Act("SpawnEntities",
                ("Template", "units/athen/infantry_spearman_b"), ("PlayerId", "1"),
                ("X", "50"), ("Z", "60"), ("Count", "3"), ("Spread", "4")) }
        });

        ts.Tick(cm, Dt);
        Assert.Single(sink.Spawns);
        var s = sink.Spawns[0];
        Assert.Equal("units/athen/infantry_spearman_b", s.Template);
        Assert.Equal(1, s.PlayerId);
        Assert.Equal(3, s.Count);
        Assert.Equal(50f, s.X);
        Assert.Equal(60f, s.Z);
        Assert.Equal(4f, s.Spread);
    }

    [Fact]
    public void UnknownCondition_NeverFires()
    {
        var cm = SetupWorld();
        var sink = new RecordingSink();
        var ts = new TriggerSystem { Sink = sink };
        ts.Add(new TriggerDefinition
        {
            Name = "bogus",
            Conditions = { Cond("OnPlayerCommand") },
            Actions = { Act("ShowMessage", ("Text", "x")) }
        });
        for (int i = 0; i < 10; i++) ts.Tick(cm, Dt);
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public void CampaignScenario_ApplyTriggers_ConvertsTimerMessages()
    {
        var cm = SetupWorld();
        var sink = new RecordingSink();
        var ts = new TriggerSystem { Sink = sink };
        var scenario = Content.CampaignScenario.CreateTutorial();
        scenario.ApplyTriggers(ts);

        Assert.Equal(3, ts.Triggers.Count);
        for (int i = 0; i < 25; i++) ts.Tick(cm, Dt);  // 2.5s → 第一条(2s)到
        Assert.Single(sink.Messages);
        Assert.Contains("select", sink.Messages[0]);
    }

    private sealed class NoEndgameScript : IMapScriptBehavior
    {
        public void OnInit(ComponentManager cm) { }
        public void Tick(ComponentManager cm, ZeroAD.Sim.Maths.Fixed dt) { }
    }

    private sealed class EndgameScript : IMapScriptBehavior, ICampaignGameEndData
    {
        public void OnInit(ComponentManager cm) { }
        public void Tick(ComponentManager cm, ZeroAD.Sim.Maths.Fixed dt) { }
        public IReadOnlyDictionary<string, string> OnCampaignGameEnd(ComponentManager cm) =>
            new Dictionary<string, string> { ["relics"] = "3", ["bonus"] = "fast" };
    }

    [Fact]
    public void GetCampaignGameEndData_NoScriptOrNoHook_ReturnsEmpty()
    {
        var cm = SetupWorld();
        var ts = new TriggerSystem();
        Assert.Empty(ts.GetCampaignGameEndData(cm));   // 无地图脚本
        ts.MapScript = new NoEndgameScript();           // 脚本未挂钩子 → 原版 {}
        Assert.Empty(ts.GetCampaignGameEndData(cm));
    }

    [Fact]
    public void GetCampaignGameEndData_HookedScript_ReturnsCustomData()
    {
        var cm = SetupWorld();
        var ts = new TriggerSystem { MapScript = new EndgameScript() };
        var data = ts.GetCampaignGameEndData(cm);
        Assert.Equal(2, data.Count);
        Assert.Equal("3", data["relics"]);
        Assert.Equal("fast", data["bonus"]);
    }
}
