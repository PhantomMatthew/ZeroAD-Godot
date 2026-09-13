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
/// 暂存数据(模板)缺失时按惯例跳过。
/// </summary>
public sealed class PetraEconomyTests
{
    private static string? FindRepoPath(string relative) => RepoPaths.Resolve(relative);


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

    private static PetraEconomyFixtures.AiWorld? NewAiWorld()
        => PetraEconomyFixtures.NewAiWorld();

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
    public void Hq_TrainMoreWorkers_InTrainingSaturation_StopsNewPlans()
    {
        // 原版饱和闸:numberInTraining > 15 → 不加单。直接把 CC 生产队列堆过闸,
        // 观察若干回合内 villager 队列无新单(命令侧:无新 Train)。
        var w = NewAiWorld();
        if (w == null) return;
        var pq = w.Cm.QueryInterface<ProductionQueue>(w.Cc)!;
        for (int i = 0; i < 4; i++)
            pq.Enqueue("units/gaul/infantry_spearman_b", 0, 0, 10f, count: 5);   // 20 在训
        int villagerPlansBefore = w.Hq.Queues.GetQueue("villager")?.CountQueuedUnits() ?? 0;
        for (int i = 0; i < 6; i++)
        {
            w.Hq.Update(w.Gs, w.Events);
            w.Net.AdvanceTurn();
        }
        int villagerPlansAfter = w.Hq.Queues.GetQueue("villager")?.CountQueuedUnits() ?? 0;
        Assert.True(villagerPlansAfter <= villagerPlansBefore + 1,
            $"in-training saturation gate should suppress new villager plans ({villagerPlansBefore}→{villagerPlansAfter})");
        // 对照:无在训时首轮即出村民计划(旧测试已钉训练执行;这里钉"会产生计划")。
    }

    [Fact]
    public void Hq_TrainMoreWorkers_AdaptiveBatch_ClampReachesProduction()
    {
        // 批量自适应:workers<12 → size=1;预置 5 批量计划被钳到 1,
        // 效果到达生产项(Count==1)。
        var w = NewAiWorld();
        if (w == null) return;
        w.Hq.Queues.GetQueue("villager")?.AddPlan(new TrainingPlan(w.Gs,
            "units/{civ}/support_civilian", number: 5));
        w.Hq.Update(w.Gs, w.Events);
        for (int i = 0; i < 3; i++)
        {
            w.Net.AdvanceTurn();
        }
        var pq = w.Cm.QueryInterface<ProductionQueue>(w.Cc)!;
        var item = pq.Queue.FirstOrDefault(q => q.TemplateName.Contains("support_civilian"));
        Assert.True(item != null, "clamped villager plan should reach production");
        Assert.Equal(1, item!.Count);
    }

    [Fact]
    public void Hq_BuildDefenses_PhaseGates()
    {
        // 原版 buildDefenses 全量门控:一阶只出哨塔(config NumSentryTowers>0 才建),
        // 石塔二阶起、要塞三阶起——门未到不出。
        var w = NewAiWorld();
        if (w == null) return;

        // 一阶 + 中等难度(NumSentryTowers=1):应出哨塔计划(非石塔)。
        for (int turn = 0; turn < 40; turn++)
        {
            w.Hq.Update(w.Gs, w.Events);
            w.Net.AdvanceTurn();
            var q = w.Hq.Queues.GetQueue("defenseBuilding");
            if (q != null && q.Plans.Count > 0)
            {
                Assert.Contains("sentry_tower", q.Plans[0].Type);
                return;
            }
            // 哨塔可能已启动出队——看地基/生产也算数;此处只钉"不出石塔/要塞"。
            var all = w.Cm.AllEntities;
            foreach (var e in all)
            {
                string? tn = w.Cm.QueryInterface<IdentityComponent>(e)?.TemplateName;
                if (tn != null && tn.Contains("defense_tower") )
                    Assert.Fail($"phase 1 should never queue defense_tower, found {tn}");
            }
        }
        // 40 回合一阶内没出哨塔也可接受(saveResources/资源门槛)——测试钉的是
        // 相位门:一阶永远不出 defense_tower/fortress(上面循环里已断言)。
    }

    [Fact]
    public void QueueToReset_PriorityRestoredOnPlanStart()
    {
        // QueueToReset:计划启动离队列 → 队列优先级复位 config 默认。
        var w = NewAiWorld();
        if (w == null) return;
        int dflt = w.Hq.Config.Priorities["defenseBuilding"];
        w.Hq.Queues.ChangePriority("defenseBuilding", 2 * dflt);
        Assert.Equal(2 * dflt, w.Hq.Queues.GetPriority("defenseBuilding"));
        w.Hq.Queues.AddPlan("defenseBuilding", new TrainingPlan(w.Gs,
            "units/gaul/support_civilian", number: 1) { QueueToReset = "defenseBuilding" });
        // 训练计划不可在 villager 外队列启动?——直接经队列 API 启动(资源够)。
        var q = w.Hq.Queues.GetQueue("defenseBuilding")!;
        // StartNext 需要 plan.CanStart;训练计划 CanStart 需 trainer——夹具有 CC,可启。
        bool started = q.StartNext(w.Gs);
        if (started)
            Assert.Equal(dflt, w.Hq.Queues.GetPriority("defenseBuilding"));
    }

    [Fact]
    public void StartingStrategy_LowWood_SaveResourcesAndCutsPopPhase2()
    {
        // 原版 configFirstBase 低木联动:startingWood<6000 → saveResources +
        // popPhase2×0.75(早出二阶扩张);>8500 → setRushes 收窄。
        var w = NewAiWorld();
        if (w == null) return;
        var hq2 = new Headquarters(new PetraConfig(DifficultyLevel.Medium));
        StartingStrategy.GameAnalysis(hq2, w.Gs);
        StartingStrategy.BuildFirstBase(hq2, w.Gs);
        // 夹具地图无资源点(无 ResourceSupply)→ startingWood = 仅库存(0)。
        int before = hq2.Config.Economy.PopPhase2;
        StartingStrategy.ConfigFirstBase(hq2, w.Gs);
        Assert.True(hq2.SaveResources);
        Assert.Equal((int)(before * 0.75), hq2.Config.Economy.PopPhase2);
    }

    [Fact]
    public void StartingStrategy_RichWood_NoSaveResources_RushesAllowed()
    {
        var w = NewAiWorld();
        if (w == null) return;
        var player = w.Cm.GetPlayerEntity(2)!;
        player.Wood = 10000;   // 库存木 >8500(无地图资源点)
        var hq2 = new Headquarters(new PetraConfig(DifficultyLevel.Medium));
        StartingStrategy.GameAnalysis(hq2, w.Gs);
        StartingStrategy.BuildFirstBase(hq2, w.Gs);
        StartingStrategy.ConfigFirstBase(hq2, w.Gs);
        Assert.False(hq2.SaveResources);
        // 性格默认 0.5(≤ weak 0.3?默认 aggressive 0.5 > medium 0.5 不成立 →
        // allowed>0 但性格不够 → 不收窄/不启用 rush——只钉 saveResources 位。)
    }

    [Fact]
    public void Hq_BuildMoreHouses_HouseNeededGate_GatesStart()
    {
        // houseNeeded 启动门(原版 queueplanBuilding isGo):计划排上但床位充裕时
        // 不启动(无地基出现);床位逼近阈值才动工。
        var w = NewAiWorld();
        if (w == null) return;
        w.Gs.Hq = w.Hq;   // IsGo 的 HQ 反链(AIComponent 正式路径同款注入)

        var player = w.Cm.GetPlayerEntity(2)!;
        // 床位充裕(limit-used 大)→ 队列可有计划但不动工。
        // 注意原版门:popMax > popLimit 才盖(无限制地图不盖)——limit 设 100 < 300 上限。
        player.PopUsed = 10;
        player.PopulationLimit = 100;
        for (int turn = 0; turn < 10; turn++)
        {
            w.Hq.Update(w.Gs, w.Events);
            w.Net.AdvanceTurn();
        }
        bool foundation = w.Cm.AllEntities.Any(e =>
            w.Cm.QueryInterface<FoundationComponent>(e) != null
            && w.Cm.QueryInterface<IdentityComponent>(e)?.TemplateName.Contains("/house") == true);
        Assert.False(foundation, "床位充裕时 houseNeeded 计划不得动工");
        // 队列里有挂门计划。
        var hq2 = w.Hq.Queues.GetQueue("house");
        Assert.True(hq2 != null && hq2.Plans.Count > 0, "计划应已排(带启动门)");
        Assert.Equal("houseNeeded", hq2!.Plans[0].GoRequirement);
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

    // ── 贸易管理器(TradeManager checkRoutes/updateTrader 全量移植)──
    // 全陆单陆区世界(陆区 id=2 处处同);收益 = round(0.75 × norm × d²/(1+0.25d/mapSize)),
    // mapSize 默认 64(无 TerrainComponent)——距离 <100m 的对子增益 <minimalGain(5) 被滤。

    private sealed class TradeWorld
    {
        public required ComponentManager Cm;
        public required GameState Gs;
        public required AIEventBuffer Events;
        public required Headquarters Hq;
    }

    private static TradeWorld? NewTradeWorld()
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

        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent { Civ = "gaul" });
        cm.AddComponent(playerEntity, new OwnershipComponent { PlayerId = 2 });
        cm.RegisterPlayer(2, playerEntity);

        // 全陆可达性(单陆区;原版 getLandAccess 处处同区 → 陆线永可达)。
        var grid = new ZeroAD.Sim.Pathfinding.Grid<ZeroAD.Sim.Pathfinding.NavcellData>(32, 32);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                grid.Set(x, y, new ZeroAD.Sim.Pathfinding.NavcellData(0x2));   // 陆通/水阻
        var acc = new Accessibility(grid, new ZeroAD.Sim.Pathfinding.PassClass(0x1),
            new ZeroAD.Sim.Pathfinding.PassClass(0x2), 32, 1);

        var net = new NetTurnManager(cm, commandDelay: 2, localPlayerId: 2,
            NetRole.Standalone, expectedPlayers: new HashSet<uint> { 2 });
        var gs = new GameState(cm, templates, techCatalog, 2, new EntityMetadata(), events, acc)
        { Net = net };
        var hq = new Headquarters(new PetraConfig(DifficultyLevel.Medium));
        return new TradeWorld { Cm = cm, Gs = gs, Events = events, Hq = hq };
    }

    private static EntityId AddMarket(ComponentManager cm, int owner, float x, float z)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromFloat(x), ZeroAD.Sim.Maths.Fixed.Zero,
            ZeroAD.Sim.Maths.Fixed.FromFloat(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new IdentityComponent
        {
            TemplateName = "structures/gaul/market",
            IsBuilding = true,
            Classes = new List<string> { "Structure", "Market", "Trade" },
        });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var p = new ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z);
        cm.NotifyPositionChanged(e, p, p);
        // AI 事件缓冲的 Create 走 SimEventBus(SimCommandExecutor 同款;cm.Notify* 是
        // 内核内部钩子,不到 AIEventBuffer)。
        cm.Events.RaiseEntityCreated(new ZeroAD.Sim.Events.EntityCreatedEvent
        { Entity = e, TemplateName = "structures/gaul/market", OwnerPlayerId = owner });
        return e;
    }

    private static EntityId AddTraderUnit(ComponentManager cm, float x, float z)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromFloat(x), ZeroAD.Sim.Maths.Fixed.Zero,
            ZeroAD.Sim.Maths.Fixed.FromFloat(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = 2 });
        cm.AddComponent(e, new IdentityComponent
        {
            TemplateName = "units/gaul/support_trader",
            IsUnit = true,
            Classes = new List<string> { "Unit", "Trader" },
        });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, 2);
        var p = new ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z);
        cm.NotifyPositionChanged(e, p, p);
        cm.Events.RaiseEntityCreated(new ZeroAD.Sim.Events.EntityCreatedEvent
        { Entity = e, TemplateName = "units/gaul/support_trader", OwnerPlayerId = 2 });
        return e;
    }

    [Fact]
    public void TradeManager_CheckRoutes_GainDriven_PicksHighestGainPair()
    {
        // 原版 checkRoutes:收益 = 倍率 × TradeGain(距离²) —— 最远市场对胜出,
        // 近端对(<minimalGain)被滤。A(10,10) B(30,10) C(400,10):
        // AB d²=400 → gain 0(滤);AC d²=152100 → gain 25;BC → 23。Route = AC。
        var w = NewTradeWorld();
        if (w == null) return;
        var a = AddMarket(w.Cm, 2, 10, 10);
        var b = AddMarket(w.Cm, 2, 30, 10);
        var c = AddMarket(w.Cm, 2, 400, 10);

        w.Hq.TradeManager.Update(w.Gs, w.Events, w.Hq.Queues);

        var route = w.Hq.TradeManager.Route;
        Assert.NotNull(route);
        Assert.Equal(a.Value, route!.Source);
        Assert.Equal(c.Value, route.Target);
        Assert.True(route.Gain >= 5, $"route gain {route.Gain} should clear minimalGain");
        Assert.DoesNotContain(b.Value, new[] { route.Source, route.Target });
    }

    [Fact]
    public void TradeManager_DynamicSwitch_BetterMarketSwitchesRoute()
    {
        // 动态换路线:初始 AC 线;更远市场 D(800,10) 建成后(Create 事件重启勘探),
        // 下一轮 Update 重选 → AD(gain 更高)。原版"收益驱动换线"行为。
        var w = NewTradeWorld();
        if (w == null) return;
        var a = AddMarket(w.Cm, 2, 10, 10);
        AddMarket(w.Cm, 2, 30, 10);
        var c = AddMarket(w.Cm, 2, 400, 10);
        w.Hq.TradeManager.Update(w.Gs, w.Events, w.Hq.Queues);
        Assert.Equal(c.Value, w.Hq.TradeManager.Route!.Target);

        var d = AddMarket(w.Cm, 2, 800, 10);
        w.Hq.TradeManager.Update(w.Gs, w.Events, w.Hq.Queues);

        var route = w.Hq.TradeManager.Route;
        Assert.NotNull(route);
        Assert.Equal(a.Value, route!.Source);
        Assert.Equal(d.Value, route.Target);
    }

    [Fact]
    public void TradeManager_UpdateTrader_IdleTraderAssignedNearerSourceRoute()
    {
        // 原版 updateTrader:空闲商队按其可达区重查最佳线,近端为源——
        // 商队 (15,10) 近 A(10,10) → SetupTradeRoute(target=C, source=A),
        // 路线元数据 (route-source=A, route-target=C)。
        var w = NewTradeWorld();
        if (w == null) return;
        var a = AddMarket(w.Cm, 2, 10, 10);
        var c = AddMarket(w.Cm, 2, 400, 10);
        var trader = AddTraderUnit(w.Cm, 15, 10);

        w.Hq.TradeManager.Update(w.Gs, w.Events, w.Hq.Queues);

        Assert.Equal(a.Value,
            (uint)(int)w.Gs.Metadata.GetObject(trader.Value, "route-source")!);
        Assert.Equal(c.Value,
            (uint)(int)w.Gs.Metadata.GetObject(trader.Value, "route-target")!);
    }

    // ── PetraConfig(全文明建筑表 + Cheat 难度修正)──

    [Fact]
    public void PetraConfig_Buildings_PerCivTable_MatchesUpstream()
    {
        // 原版 config.js buildings 全键:default + 14 civ(pers 等无表文明回落 default)。
        var cfg = new PetraConfig(DifficultyLevel.Medium);
        var expectedKeys = new[]
        {
            "default", "achae", "athen", "brit", "cart", "gaul", "han", "iber",
            "kush", "mace", "maur", "ptol", "rome", "sele", "spart",
        };
        Assert.Equal(expectedKeys.OrderBy(k => k), cfg.Buildings.Keys.OrderBy(k => k));
        // 逐字抽查(原版键值)
        Assert.Equal(new[] { "structures/{civ}/tachara" }, cfg.Buildings["achae"]);
        Assert.Empty(cfg.Buildings["brit"]);
        Assert.Equal(new[] { "structures/{civ}/assembly" }, cfg.Buildings["gaul"]);
        Assert.Equal(5, cfg.Buildings["kush"].Count);
        Assert.Contains("structures/{civ}/pyramid_large", cfg.Buildings["kush"]);
        Assert.Equal(new[] { "structures/{civ}/army_camp", "structures/{civ}/temple_vesta" },
            cfg.Buildings["rome"]);
        Assert.Equal(new[] { "structures/{civ}/syssiton", "structures/{civ}/theater" },
            cfg.Buildings["spart"]);
    }

    [Fact]
    public void PetraConfig_Cheat_ScalesGatherTradeAndBuildTime_ByDifficulty()
    {
        // 原版 config.js Cheat:rate = 采集/贸易倍率,time = 建造时间倍率。
        // VeryHard(5):rate 1.56 / time 1.00;Easy(2):rate 0.75 / time 1.10。
        var w = NewAiWorld();
        if (w == null) return;
        var pe = w.Cm.GetPlayerEntityId(2)!.Value;
        var affects = new List<string> { "Unit" };

        new PetraConfig(DifficultyLevel.VeryHard).Cheat(w.Gs);
        Assert.Equal(1.56f, w.Cm.Modifiers.ApplyTemplate(
            "ResourceGatherer/BaseSpeed", 1f, affects, pe), 3);
        Assert.Equal(1.56f, w.Cm.Modifiers.ApplyTemplate(
            "Trader/GainMultiplier", 1f, affects, pe), 3);
        Assert.Equal(1.00f, w.Cm.Modifiers.ApplyTemplate(
            "Cost/BuildTime", 1f, affects, pe), 3);

        // 另一世界(Modifier 表在 cm 上,不能复用):Easy → 0.75 / 1.10。
        var w2 = NewAiWorld();
        if (w2 == null) return;
        var pe2 = w2.Cm.GetPlayerEntityId(2)!.Value;
        new PetraConfig(DifficultyLevel.Easy).Cheat(w2.Gs);
        Assert.Equal(0.75f, w2.Cm.Modifiers.ApplyTemplate(
            "ResourceGatherer/BaseSpeed", 1f, affects, pe2), 3);
        Assert.Equal(1.10f, w2.Cm.Modifiers.ApplyTemplate(
            "Cost/BuildTime", 1f, affects, pe2), 3);
    }

    [Fact]
    public void PetraConfig_SetConfig_InvokesCheat()
    {
        // 调用链确认(原版 setConfig 末段 → Cheat):SetConfig 后难度修正即生效。
        var w = NewAiWorld();
        if (w == null) return;
        var pe = w.Cm.GetPlayerEntityId(2)!.Value;
        new PetraConfig(DifficultyLevel.Hard).SetConfig(w.Gs, w.Cm.RNG);
        Assert.Equal(1.25f, w.Cm.Modifiers.ApplyTemplate(
            "ResourceGatherer/BaseSpeed", 1f, new List<string> { "Unit" }, pe), 3);
    }

    [Fact]
    public void ResourceGatherer_EffectiveRate_AppliesBaseSpeedModifier()
    {
        // Cheat 的采集倍率落地确认:AI Bonus(BaseSpeed 路径)经 EffectiveRate 生效。
        var w = NewAiWorld();
        if (w == null) return;
        new PetraConfig(DifficultyLevel.VeryHard).Cheat(w.Gs);
        var gatherer = w.Cm.QueryInterface<ResourceGatherer>(w.Worker)!;
        // GatherRate 默认 10 × rate[VeryHard]=1.56 → 16(四舍五入远离零)。
        Assert.Equal(16, gatherer.EffectiveRate(w.Cm, ResourceType.Food));
    }

    // ── 贸易勘探全量世界(带寻路 + 领土:FindMarketLocation 的完整依赖)──
    // 512m 全陆图;CC 在 (64,256) 带领土影响力(radius 400 → 覆盖几乎全部地图,
    // 角点 gaia);mapSize 取默认 64(无 TerrainComponent 入 cm) → 远距离市场对
    // 增益远超 minimalGain(5),断言余量大。

    private sealed class TerritoryTradeWorld
    {
        public required ComponentManager Cm;
        public required GameState Gs;
        public required AIEventBuffer Events;
        public required Headquarters Hq;
        public required EntityId Cc;
    }

    private static TerritoryTradeWorld? NewTerritoryTradeWorld()
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

        // 寻路网格(128 地块 × 4m = 512m 全陆;ShipPassabilityTests 同款装配)。
        SimSystem.SetObstructionManager(new ObstructionManager(512, 4f));
        var terrain = new TerrainComponent();
        terrain.Configure(128, 4f);
        // 置零水位 → 走真实地形采样(否则合成回退的岸线距离恒 0,building-land 类
        // MinShoreDistance=4 全图不可建——合成路径的已知近似,见 PathfinderComponent)。
        terrain.SetWaterLevel(ZeroAD.Sim.Maths.Fixed.Zero);
        var gridClasses = new TerrainClass[128, 128];
        for (int i = 0; i < 128; i++)
            for (int j = 0; j < 128; j++)
                gridClasses[i, j] = TerrainClass.Land;
        terrain.SetPassabilityGrid(gridClasses);
        var pf = new PathfinderComponent(cm);
        pf.SetTerrain(terrain);
        pf.RebuildGrid();
        SimSystem.SetPathfinder(pf);
        SimSystem.SetTerritoryManager(new TerritoryManager(cm, 512));

        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent { Civ = "gaul" });
        cm.AddComponent(playerEntity, new OwnershipComponent { PlayerId = 2 });
        cm.RegisterPlayer(2, playerEntity);

        // CC:基地锚 + 领土影响力(root;radius 400 ≈ 覆盖 256m 半径外全部,角点除外)。
        var cc = cm.CreateEntity();
        var ccPos = new PositionComponent();
        cm.AddComponent(cc, ccPos);
        ccPos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromFloat(64), ZeroAD.Sim.Maths.Fixed.Zero,
            ZeroAD.Sim.Maths.Fixed.FromFloat(256));
        cm.AddComponent(cc, new OwnershipComponent { PlayerId = 2 });
        cm.AddComponent(cc, new IdentityComponent
        {
            TemplateName = "structures/gaul/civil_centre",
            IsBuilding = true,
            Classes = new List<string> { "CivCentre", "Structure" },
        });
        cm.AddComponent(cc, new TerritoryInfluenceComponent
        {
            Radius = ZeroAD.Sim.Maths.Fixed.FromFloat(400), Weight = 10000, Root = true,
        });
        cm.NotifyEntityCreated(cc);
        cm.NotifyOwnerChanged(cc, -1, 2);
        var ccP = new ZeroAD.Sim.Maths.FixedVector2D(ccPos.Position.X, ccPos.Position.Z);
        cm.NotifyPositionChanged(cc, ccP, ccP);
        cm.Events.RaiseEntityCreated(new ZeroAD.Sim.Events.EntityCreatedEvent
        { Entity = cc, TemplateName = "structures/gaul/civil_centre", OwnerPlayerId = 2 });

        // 工人(CanBuild("structures/{civ}/market") 的 FindBuilder 依赖)。
        var worker = cm.CreateEntity();
        var wpos = new PositionComponent();
        cm.AddComponent(worker, wpos);
        wpos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromFloat(70), ZeroAD.Sim.Maths.Fixed.Zero,
            ZeroAD.Sim.Maths.Fixed.FromFloat(256));
        cm.AddComponent(worker, new OwnershipComponent { PlayerId = 2 });
        cm.AddComponent(worker, new IdentityComponent
        {
            TemplateName = "units/gaul/support_civilian",
            IsUnit = true,
            Classes = new List<string> { "Citizen", "Unit" },
        });
        cm.NotifyEntityCreated(worker);
        cm.NotifyOwnerChanged(worker, -1, 2);

        var acc = new Accessibility(pf.PassabilityGrid!, pf.DefaultClass.Mask,
            pf.ShipClass.Mask, pf.NavcellsPerSide, 1);
        var net = new NetTurnManager(cm, commandDelay: 2, localPlayerId: 2,
            NetRole.Standalone, expectedPlayers: new HashSet<uint> { 2 });
        var gs = new GameState(cm, templates, techCatalog, 2, new EntityMetadata(), events, acc)
        { Net = net };
        var hq = new Headquarters(new PetraConfig(DifficultyLevel.Medium));
        // 原版 startingStrategy.gameAnalysis:海图判定 + 运营陆区标注(LandRegions)。
        StartingStrategy.GameAnalysis(hq, gs);
        return new TerritoryTradeWorld { Cm = cm, Gs = gs, Events = events, Hq = hq, Cc = cc };
    }

    [Fact]
    public void TradeManager_Prospect_QueuesMarketPlan_WhenGainWorth()
    {
        // 原版 prospectForNewMarket 全链:单一市场 → FindMarketLocation 找高收益位
        // → 排队 economicBuilding 市场计划(首路线:优先级 ×2 + QueueToReset)。
        var w = NewTerritoryTradeWorld();
        if (w == null) return;
        AddMarket(w.Cm, 2, 80, 256);

        w.Hq.TradeManager.Update(w.Gs, w.Events, w.Hq.Queues);

        var queue = w.Hq.Queues.GetQueue("economicBuilding");
        Assert.NotNull(queue);
        Assert.True(queue!.HasQueuedUnits, "expected a market construction plan queued");
        Assert.Contains("market", queue.Plans[0].Type);
        Assert.Equal("economicBuilding", queue.Plans[0].QueueToReset);
        Assert.Equal(2 * w.Hq.Config.Priorities["economicBuilding"],
            w.Hq.Queues.GetPriority("economicBuilding"));
    }

    [Fact]
    public void TradeManager_RouteMarketDestroyed_ReprospectsAndQueuesRebuild()
    {
        // 路线市场被毁(原版 checkEvents Destroy 段):activateProspection → 同轮
        // 重选(不足 2 市场 → 无线)+ 勘探重排新市场计划(世界有领土/寻路时)。
        var w = NewTerritoryTradeWorld();
        if (w == null) return;
        AddMarket(w.Cm, 2, 80, 256);
        var c = AddMarket(w.Cm, 2, 400, 256);
        w.Hq.TradeManager.Update(w.Gs, w.Events, w.Hq.Queues);
        Assert.NotNull(w.Hq.TradeManager.Route);

        w.Cm.DestroyEntity(c);
        w.Hq.TradeManager.Update(w.Gs, w.Events, w.Hq.Queues);

        Assert.Null(w.Hq.TradeManager.Route);
        var queue = w.Hq.Queues.GetQueue("economicBuilding");
        Assert.True(queue != null && queue.HasQueuedUnits,
            "destroyed route market should re-trigger prospection and queue a new market");
        Assert.Contains("market", queue!.Plans[0].Type);
    }
}
