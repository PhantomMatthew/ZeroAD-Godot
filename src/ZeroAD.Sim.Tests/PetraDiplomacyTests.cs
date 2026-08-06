using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using ZeroAD.Sim.AI;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.AI.Petra;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Net;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Petra 外交/胜利管理器:贡品输送、LMS 背叛、奇迹建造、弑君护主。
/// junction 数据(模板)缺失时按惯例跳过。
/// </summary>
public sealed class PetraDiplomacyTests
{
    private static string? FindRepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, relative);
    }

    private sealed class DipWorld
    {
        public required ComponentManager Cm;
        public required NetTurnManager Net;
        public required GameState Gs;
        public required AIEventBuffer Events;
    }

    /// <summary>AI(2) + 盟友(3) 世界;两玩家互相同盟。</summary>
    private static DipWorld? NewDipWorld()
    {
        var templatesRoot = FindRepoPath("binaries/data/mods/public/simulation/templates");
        var techRoot = FindRepoPath("binaries/data/mods/public/simulation/data/technologies");
        if (templatesRoot == null || techRoot == null) return null;

        var templates = new TemplateLoader(templatesRoot);
        templates.LoadAllTemplates();
        var techCatalog = TechnologyLoader.LoadAll(techRoot);

        var cm = new ComponentManager(rngSeed: 42, templates: templates);
        SimSystem.Init(cm);
        var events = new AIEventBuffer();
        events.Attach(cm);
        var range = new RangeManager(cm, global::ZeroAD.Sim.Maths.Fixed.FromInt(256), global::ZeroAD.Sim.Maths.Fixed.FromInt(256));
        SimSystem.SetRangeManager(range);

        foreach (var pid in new[] { 2, 3 })
        {
            var pe = cm.CreateEntity();
            cm.AddComponent(pe, new PlayerComponent { Civ = "gaul" });
            cm.AddComponent(pe, new OwnershipComponent { PlayerId = pid });
            cm.AddComponent(pe, new DiplomacyComponent());
            cm.RegisterPlayer(pid, pe);
        }
        var d2 = cm.QueryInterface<DiplomacyComponent>(cm.Players.GetPlayerEntityId(2)!.Value)!;
        var d3 = cm.QueryInterface<DiplomacyComponent>(cm.Players.GetPlayerEntityId(3)!.Value)!;
        d2.SetAlly(3);
        d3.SetAlly(2);

        var net = new NetTurnManager(cm, commandDelay: 2, localPlayerId: 2,
            NetRole.Standalone, expectedPlayers: new HashSet<uint> { 2 });
        var gs = new GameState(cm, templates, techCatalog, 2, new EntityMetadata(), events, null)
        { Net = net };
        return new DipWorld { Cm = cm, Net = net, Gs = gs, Events = events };
    }

    private static EntityId MakeUnit(ComponentManager cm, int owner, float x, float z)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new global::ZeroAD.Sim.Maths.FixedVector3D(
            global::ZeroAD.Sim.Maths.Fixed.FromFloat(x), global::ZeroAD.Sim.Maths.Fixed.Zero, global::ZeroAD.Sim.Maths.Fixed.FromFloat(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new IdentityComponent { TemplateName = "units/gaul/support_civilian", IsUnit = true });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var p = new global::ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z);
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    [Fact]
    public void Tributes_SurplusFlowstoNeedyAlly()
    {
        var w = NewDipWorld();
        if (w == null) return;
        // 我方富(木 5000)、盟友穷(木 50 < 20%);各有实体让 donor 判定成立。
        w.Cm.GetPlayerEntity(2)!.Wood = 5000;
        var ally = w.Cm.GetPlayerEntity(3)!;
        ally.Wood = 50;
        MakeUnit(w.Cm, 2, 10, 10);
        MakeUnit(w.Cm, 2, 12, 10);
        MakeUnit(w.Cm, 3, 50, 50);

        var dm = new DiplomacyManager(new PetraConfig(DifficultyLevel.Medium));
        dm.NextTributeTurn = 0;   // 立即触发
        int allyWoodBefore = ally.Wood;
        dm.Update(w.Gs, w.Events);
        for (int i = 0; i < 4; i++) w.Net.AdvanceTurn();   // 命令落地

        // 常规口径:floor(0.3×5000 − 50) = 1450 木输送。
        Assert.True(ally.Wood > allyWoodBefore,
            $"expected tribute to ally, wood {allyWoodBefore} → {ally.Wood}");
    }

    [Fact]
    public void Tributes_NoSurplus_NoTribute()
    {
        var w = NewDipWorld();
        if (w == null) return;
        w.Cm.GetPlayerEntity(2)!.Wood = 150;   // < 200 盈余门槛
        var ally = w.Cm.GetPlayerEntity(3)!;
        ally.Wood = 10;
        MakeUnit(w.Cm, 2, 10, 10);
        MakeUnit(w.Cm, 3, 50, 50);

        var dm = new DiplomacyManager(new PetraConfig(DifficultyLevel.Medium));
        dm.NextTributeTurn = 0;
        dm.Update(w.Gs, w.Events);
        for (int i = 0; i < 4; i++) w.Net.AdvanceTurn();

        Assert.Equal(10, ally.Wood);
    }

    [Fact]
    public void LastManStanding_TwoAlliesLeft_Betrays()
    {
        var w = NewDipWorld();
        if (w == null) return;
        w.Cm.EndGame.AlliedVictory = false;   // LMS 模式
        MakeUnit(w.Cm, 2, 10, 10);
        MakeUnit(w.Cm, 3, 50, 50);

        var dm = new DiplomacyManager(new PetraConfig(DifficultyLevel.Medium));
        dm.NextTributeTurn = uint.MaxValue;   // 本测试不看贡品
        dm.Update(w.Gs, w.Events);
        Assert.True(dm.WaitingToBetray);

        // 推过背叛缓冲(100 回合)。
        for (int i = 0; i < 105; i++) w.Net.AdvanceTurn();
        dm.Update(w.Gs, w.Events);
        for (int i = 0; i < 4; i++) w.Net.AdvanceTurn();

        var d2 = w.Cm.QueryInterface<DiplomacyComponent>(
            w.Cm.Players.GetPlayerEntityId(2)!.Value)!;
        Assert.True(d2.IsEnemy(3), "LMS 只剩两家盟友应反目成敌");
    }

    [Fact]
    public void VictoryManager_WonderCondition_QueuesWonder()
    {
        var w = NewDipWorld();
        if (w == null) return;
        w.Cm.EndGame.SetVictoryConditions(new[] { "conquest", "wonder" });
        MakeUnit(w.Cm, 2, 10, 10);

        var vm = new VictoryManager(new PetraConfig(DifficultyLevel.Medium));
        var queues = new QueueManager(new PetraConfig(DifficultyLevel.Medium));
        // 对齐 playedTurn%10 门:回合 0 通过。
        vm.Update(w.Gs, w.Events, queues);

        var q = queues.GetQueue("wonder");
        Assert.NotNull(q);
        Assert.True(q!.HasQueuedUnits);
        Assert.EndsWith("/wonder", q.Plans[0].Type);
    }

    [Fact]
    public void VictoryManager_NoSpecialCondition_NoWonder()
    {
        var w = NewDipWorld();
        if (w == null) return;
        // 默认征服(无 wonder)→ 不建奇迹。
        var vm = new VictoryManager(new PetraConfig(DifficultyLevel.Medium));
        var queues = new QueueManager(new PetraConfig(DifficultyLevel.Medium));
        vm.Update(w.Gs, w.Events, queues);

        var q = queues.GetQueue("wonder");
        Assert.True(q == null || !q.HasQueuedUnits);
    }
}
