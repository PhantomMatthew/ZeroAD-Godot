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

    /// <summary>给玩家补 N 座 Village 类建筑(phase 科技 entity 前置的满足件);
    /// 带 RangeManager 注册(CountClassStructures 从范围索引数)。</summary>
    private static void AddVillageHouses(ComponentManager cm, int owner, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var e = cm.CreateEntity();
            var pos = new PositionComponent();
            cm.AddComponent(e, pos);
            pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
                ZeroAD.Sim.Maths.Fixed.FromInt(30 + i * 8), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromInt(60));
            cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
            cm.AddComponent(e, new IdentityComponent
            {
                TemplateName = "structures/gaul/house",
                IsBuilding = true,
                Classes = new System.Collections.Generic.List<string> { "Village", "Structure" },
            });
            cm.NotifyEntityCreated(e);
            cm.NotifyOwnerChanged(e, -1, owner);
            var p = new ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z);
            cm.NotifyPositionChanged(e, p, p);
        }
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
        // RangeManager:entity 前置(phase 科技需 N 个 Village 建筑)从范围索引计数。
        SimSystem.SetRangeManager(new RangeManager(cm,
            ZeroAD.Sim.Maths.Fixed.FromInt(256), ZeroAD.Sim.Maths.Fixed.FromInt(256)));
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
        // phase_town 前置(entity 形态):需 5 个 Village 类建筑——补 5 座民房。
        AddVillageHouses(w.Cm, 2, 5);
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

        // 造 5 个 AI 士兵(真类名:步兵近战公民兵——编组槽匹配要真类) + 敌方 CC 目标
        var soldiers = new List<EntityId>();
        for (int i = 0; i < 5; i++)
        {
            var s = w.Cm.CreateEntity();
            // 真实位置((0,0)==default 会被判"无位置"——IsValidTarget/可分配都拒)。
            var pos = new PositionComponent();
            w.Cm.AddComponent(s, pos);
            pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
                ZeroAD.Sim.Maths.Fixed.FromInt(100 + i * 4), ZeroAD.Sim.Maths.Fixed.Zero,
                ZeroAD.Sim.Maths.Fixed.FromInt(100));
            w.Cm.NotifyEntityCreated(s);
            w.Cm.NotifyOwnerChanged(s, -1, 2);
            w.Cm.NotifyPositionChanged(s,
                new ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z),
                new ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z));
            w.Cm.AddComponent(s, new UnitMotion());
            w.Cm.AddComponent(s, new IdentityComponent
            {
                TemplateName = "units/gaul/infantry_spearman_b",
                IsUnit = true,
                Classes = new List<string> { "CitizenSoldier", "Unit", "Infantry", "Melee" },
            });
            w.Cm.AddComponent(s, new UnitAIComponent());
            w.Cm.AddComponent(s, new AttackComponent());
            w.Cm.QueryInterface<AttackComponent>(s)!.Damage.Amounts[DamageType.Hack] = 5;
            w.Cm.AddComponent(s, new OwnershipComponent { PlayerId = 2 });
            soldiers.Add(s);
        }
        var enemy = w.Cm.CreateEntity();
        var epos = new PositionComponent();
        w.Cm.AddComponent(enemy, epos);
        epos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromInt(200), ZeroAD.Sim.Maths.Fixed.Zero,
            ZeroAD.Sim.Maths.Fixed.FromInt(120));
        w.Cm.NotifyEntityCreated(enemy);
        w.Cm.NotifyOwnerChanged(enemy, -1, 1);
        w.Cm.NotifyPositionChanged(enemy,
            new ZeroAD.Sim.Maths.FixedVector2D(epos.Position.X, epos.Position.Z),
            new ZeroAD.Sim.Maths.FixedVector2D(epos.Position.X, epos.Position.Z));
        w.Cm.AddComponent(enemy, new IdentityComponent
        {
            TemplateName = "structures/athen/civil_centre",
            IsBuilding = true,
            Classes = new List<string> { "Structure", "CivCentre" },
        });
        w.Cm.AddComponent(enemy, new HealthComponent { Current = 100, Max = 100 });
        w.Cm.AddComponent(enemy, new OwnershipComponent { PlayerId = 1 });

        // 原版发起门控:兵营 ≥1 且(到 Town 代或在研 Town)——补兵营 + 全阶段研发
        // (fixture 已有活基地,不满足"无基地可扩"兜底)。
        var rax = w.Cm.CreateEntity();
        w.Cm.AddComponent(rax, new PositionComponent());
        w.Cm.AddComponent(rax, new OwnershipComponent { PlayerId = 2 });
        w.Cm.AddComponent(rax, new IdentityComponent
        {
            TemplateName = "structures/gaul/barracks",
            IsBuilding = true,
            Classes = new List<string> { "Structure", "Barracks" },
        });
        var tm = w.Cm.QueryInterface<TechnologyManager>(w.Cm.GetPlayerEntityId(2)!.Value);
        foreach (var ph in w.Gs.Phases) tm!.ApplyResearch(ph, w.Cm);

        var mgr = new ZeroAD.Sim.AI.Petra.AttackManager(new PetraConfig(DifficultyLevel.Medium));
        mgr.Hq = w.Hq;
        // 筹备轮转:4 次 Update 后应有计划在筹备。
        for (int i = 0; i < 4; i++)
            mgr.Update(w.Gs, w.Hq.Queues, w.Events);

        var preparing = mgr.StartedAttacks.SelectMany(kv => kv.Value)
            .Concat(mgr.UpcomingAttacks.SelectMany(kv => kv.Value)).ToList();
        Assert.True(preparing.Count > 0,
            $"no plan: upcoming={string.Join(',', mgr.UpcomingAttacks.Select(kv => kv.Key + ':' + kv.Value.Count))}");

        // 计划 Started → AttackWalk 下发:直接把 5 个士兵登记进计划并启动
        // (筹备期全量条件—编组/集结/路径—由 updatePreparation 测;此测只锁推进语义)。
        var plan = preparing[0];
        foreach (var s in soldiers)
        {
            plan.UnitCollection.Add(s.Value);
            w.Gs.Metadata.Set(s.Value, "plan", plan.Name);
        }
        Assert.True(plan.ChooseTarget(w.Gs, mgr));
        Assert.True(plan.StartAttack(w.Gs));

        // 命令经锁步延迟——AdvanceTurn 执行;UnitAI 订单分发由 Tick 驱动(UnitAITests 同款)。
        for (int i = 0; i < 3; i++) w.Net.AdvanceTurn();
        var ai = w.Cm.QueryInterface<UnitAIComponent>(soldiers[0])!;
        ai.Tick(0.1f, w.Cm);
        Assert.Equal("INDIVIDUAL.WALKINGANDFIGHTING", ai.FsmStateName);
    }
}
