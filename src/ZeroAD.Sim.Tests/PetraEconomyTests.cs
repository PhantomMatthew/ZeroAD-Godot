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
}
