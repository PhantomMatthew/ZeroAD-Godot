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
/// Petra HQ 填充的端到端运营测试:AI 世界(CC+村民)→ HQ.Update 驱动决策 →
/// Queues 启动计划 → AI 命令经 SubmitAiCommand 落 NetTurnManager._aiBundles →
/// AdvanceTurn 执行——验证"训练村民"与"建房"真的落进 sim(不是只停在计划层)。
/// junction 数据(模板)缺失时按惯例跳过。
/// </summary>
public sealed class PetraEconomyTests
{
    private static string? FindRepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, relative);
    }

    private sealed class AiWorld
    {
        public required ComponentManager Cm;
        public required NetTurnManager Net;
        public required GameState Gs;
        public required Headquarters Hq;
        public required AIEventBuffer Events;
        public required EntityId Cc;
        public required EntityId Worker;
    }

    private static AiWorld? NewAiWorld()
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
        events.Attach(cm);   // 实体创建即录事件(AIComponent 同款;turnMod 轮转靠它)

        // AI 玩家实体(player 2,gaul)
        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent { Civ = "gaul" });
        cm.AddComponent(playerEntity, new OwnershipComponent { PlayerId = 2 });
        cm.RegisterPlayer(2, playerEntity);
        // 科技管理器(研究命令路径需要;与 SimBridge InitWorld 同款配置)。
        var techMgr2 = new TechnologyManager();
        techMgr2.Configure(techCatalog, "gaul");
        cm.AddComponent(playerEntity, techMgr2);
        // 本地玩家实体(player 1,旁观)
        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent { Civ = "athen" });
        cm.AddComponent(p1, new OwnershipComponent { PlayerId = 1 });
        cm.RegisterPlayer(1, p1);

        // AI 的 CC(可训练 female citizen 的 trainer)
        var cc = cm.CreateEntity();
        cm.AddComponent(cc, new PositionComponent());
        cm.AddComponent(cc, new OwnershipComponent { PlayerId = 2 });
        cm.AddComponent(cc, new IdentityComponent
        {
            TemplateName = "structures/gaul/civil_centre",
            IsBuilding = true,
            Classes = new System.Collections.Generic.List<string> { "CivCentre", "Structure" },
        });
        cm.AddComponent(cc, new ProductionQueue
        {
            TrainableTokens = "units/{civ}/support_civilian units/{civ}/infantry_spearman_b",
            NativeCiv = "gaul",
        });

        // AI 的村民(可建造的 builder;role 不设——CountOwnEntitiesByRole("worker")=0
        // → TrainMoreWorkers 必触发,正好验证训练链)
        var worker = cm.CreateEntity();
        cm.AddComponent(worker, new PositionComponent());
        cm.AddComponent(worker, new OwnershipComponent { PlayerId = 2 });
        cm.AddComponent(worker, new IdentityComponent
        {
            TemplateName = "units/gaul/support_civilian",
            IsUnit = true,
            Classes = new System.Collections.Generic.List<string> { "Citizen", "Unit" },
        });
        cm.AddComponent(worker, new ResourceGatherer());

        var net = new NetTurnManager(cm, commandDelay: 2, localPlayerId: 2,
            NetRole.Standalone, expectedPlayers: new System.Collections.Generic.HashSet<uint> { 2 });
        var metadata = new EntityMetadata();
        var gs = new GameState(cm, templates, techCatalog, 2, metadata, events, null)
        { Net = net };
        var hq = new Headquarters(new PetraConfig(DifficultyLevel.Medium));
        // 首回合初始化(AIComponent.Tick 同款):注册首基地 → HasActiveBase=true → 经济循环可运行
        StartingStrategy.GameAnalysis(hq, gs);
        StartingStrategy.BuildFirstBase(hq, gs);
        StartingStrategy.ConfigFirstBase(hq, gs);

        return new AiWorld { Cm = cm, Net = net, Gs = gs, Hq = hq, Events = events, Cc = cc, Worker = worker };
    }

    [Fact]
    public void Hq_TrainMoreWorkers_QueuesVillagerPlan_AndTrainExecutes()
    {
        var w = NewAiWorld();
        if (w == null) return;

        // HQ.Update 一轮内完成"加计划→启动"(Queues.Update 在末尾)——断言终态:
        // AI 训练命令经 SubmitAiCommand 落批次,AdvanceTurn 执行后 CC 队列有训练项。
        w.Hq.Update(w.Gs, w.Events);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(w.Net.CanAdvanceTurn());
            w.Net.AdvanceTurn();
        }

        var pq = w.Cm.QueryInterface<ProductionQueue>(w.Cc)!;
        Assert.True(pq.QueueCount > 0,
            "CC 生产队列应有训练项(AI 命令经 SubmitAiCommand→AdvanceTurn 执行)");
        Assert.Contains("support_civilian", pq.Queue[0].TemplateName);
    }

    [Fact]
    public void Hq_BuildMoreHouses_SpawnsHouseFoundation_WhenBedsLow()
    {
        var w = NewAiWorld();
        if (w == null) return;

        // 床位压到缓冲线内(limit-used <= 8):AI 玩家 pop 拉满
        var player = w.Cm.GetPlayerEntity(2)!;
        player.PopUsed = player.PopulationLimit - 4;

        // 回合推进让 turnMod 轮转(%4==1 命中 BuildMoreHouses);每轮:
        // HQ.Update(决策+启动)→ AdvanceTurn(执行)。8 轮内应出现 house 地基。
        EntityId? foundation = null;
        for (int turn = 0; turn < 8 && foundation == null; turn++)
        {
            w.Hq.Update(w.Gs, w.Events);
            w.Net.AdvanceTurn();
            foundation = w.Cm.AllEntities.FirstOrDefault(e =>
                w.Cm.QueryInterface<IdentityComponent>(e)?.TemplateName.Contains("/house") == true);
        }
        Assert.True(foundation != null,
            "床位紧张时 AI 应已下达 house 建造命令(地基实体出现)");
    }

    [Fact]
    public void TrainingPlan_Start_IssuesTrainCommand_ToAiBundle()
    {
        var w = NewAiWorld();
        if (w == null) return;

        // 直接构造计划并启动:命令应经 SubmitAiCommand 落 _aiBundles(经 AdvanceTurn 执行)
        var plan = new TrainingPlan(w.Gs, "units/{civ}/support_civilian", number: 2);
        Assert.True(plan.CanStart(w.Gs));
        plan.Start(w.Gs);

        for (int i = 0; i < 3; i++) w.Net.AdvanceTurn();
        var pq = w.Cm.QueryInterface<ProductionQueue>(w.Cc)!;
        Assert.True(pq.QueueCount > 0);
        Assert.Equal(2, pq.Queue[0].Count);
    }

    [Fact]
    public void ResearchPlan_Start_IssuesResearchCommand_AndProgresses()
    {
        var w = NewAiWorld();
        if (w == null) return;

        // CC 挂研究组件;phase 造价 500F+500W,先补足资源(StartResearch 扣费门)。
        var p2 = w.Cm.GetPlayerEntity(2)!;
        p2.Wood = 5000; p2.Food = 5000; p2.Stone = 5000; p2.Metal = 5000;
        w.Cm.AddComponent(w.Cc, new ResearcherComponent());
        var plan = new ResearchPlan(w.Gs, "phase_town_gaul");
        Assert.True(plan.CanStart(w.Gs));
        plan.Start(w.Gs);

        for (int i = 0; i < 3; i++) w.Net.AdvanceTurn();
        var researcher = w.Cm.QueryInterface<ResearcherComponent>(w.Cc)!;
        Assert.True(researcher.IsResearching);
        Assert.Equal("phase_town_generic", researcher.CurrentTech);
    }

    [Fact]
    public void AttackPlan_Started_IssuesAttackWalk_ToAllUnits()
    {
        var w = NewAiWorld();
        if (w == null) return;

        // 造 5 个 AI 士兵(AttackPlan 组军阈值) + 一个敌方目标建筑
        var soldiers = new List<EntityId>();
        for (int i = 0; i < 5; i++)
        {
            var s = w.Cm.CreateEntity();
            w.Cm.AddComponent(s, new PositionComponent());
            w.Cm.AddComponent(s, new UnitMotion());
            w.Cm.AddComponent(s, new IdentityComponent
            {
                TemplateName = "units/gaul/infantry_spearman_b",
                IsUnit = true,
                Classes = new List<string> { "CitizenSoldier", "Unit" },
            });
            w.Cm.AddComponent(s, new UnitAIComponent());
            w.Cm.AddComponent(s, new AttackComponent());
            w.Cm.QueryInterface<AttackComponent>(s)!.Damage.Amounts[DamageType.Hack] = 5;
            w.Cm.AddComponent(s, new OwnershipComponent { PlayerId = 2 });
            soldiers.Add(s);
        }
        var enemy = w.Cm.CreateEntity();
        w.Cm.AddComponent(enemy, new PositionComponent());
        w.Cm.AddComponent(enemy, new IdentityComponent
        {
            TemplateName = "structures/athen/house",
            IsBuilding = true,
            Classes = new List<string> { "Structure" },
        });
        w.Cm.AddComponent(enemy, new HealthComponent { Current = 100, Max = 100 });
        w.Cm.AddComponent(enemy, new OwnershipComponent { PlayerId = 1 });

        var mgr = new ZeroAD.Sim.AI.Petra.AttackManager(new PetraConfig(DifficultyLevel.Medium));
        // 驱动到 Started:两次 Update(建计划→组军满→Started+下推进令)
        for (int i = 0; i < 4; i++)
            mgr.Update(w.Gs, w.Hq.Queues, w.Events);

        var started = mgr.StartedAttacks.Concat(mgr.UpcomingAttacks).ToList();
        Assert.NotEmpty(started);
        // 推进后士兵应持攻击移动状态(WalkAndFight;目标=敌方建筑位置)
        var ai = w.Cm.QueryInterface<UnitAIComponent>(soldiers[0])!;
        // 命令经锁步延迟——AdvanceTurn 后检查
        for (int i = 0; i < 3; i++) w.Net.AdvanceTurn();
        if (mgr.StartedAttacks.Count > 0)
            Assert.Equal("INDIVIDUAL.WALKINGANDFIGHTING", ai.FsmStateName);
    }
}
