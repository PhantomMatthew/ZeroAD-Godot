using System;
using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using Xunit;

namespace ZeroAD.Sim.Tests;

public sealed class SimCommandExecutorTests
{
    private const string TemplatesRoot = "../../../binaries/data/mods/public/simulation/templates";
    private const string TechDir = "../../../binaries/data/mods/public/simulation/data/technologies";

    /// <summary>从测试程序集位置向上找仓库标记目录(相对 ../../../ 依赖 CWD,会静默解析失败)。</summary>
    private static string? FindRepoPath(string relative)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : System.IO.Path.Combine(dir.FullName, relative);
    }

    private static Content.TemplateLoader? TryLoadTemplates() =>
        System.IO.Directory.Exists(TemplatesRoot) ? new Content.TemplateLoader(TemplatesRoot) : null;

    private static EntityId MakeUnitWithAI(ComponentManager cm, int player = 1)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new UnitAIComponent());
        cm.AddComponent(e, new IdentityComponent());
        if (player > 0)
            cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        return e;
    }

    [Fact]
    public void Gather_RaisesPlayerCommandEvent()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var unit = MakeUnitWithAI(cm);
        var tree = cm.CreateEntity();
        cm.AddComponent(tree, new PositionComponent());
        cm.AddComponent(tree, new ResourceSupply());

        PlayerCommandEvent? raised = null;
        cm.Events.PlayerCommand += e => raised = e;

        executor.Apply(NetCommand.Gather(1, unit.Value, tree.Value));

        Assert.NotNull(raised);
        Assert.Equal("gather", raised!.Type);
        Assert.Equal(tree, raised.Target);
    }

    [Fact]
    public void Attack_RaisesPlayerCommandEvent()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var attacker = MakeUnitWithAI(cm);
        cm.AddComponent(attacker, new AttackComponent { Damage = new DamageBlock(DamageType.Hack, 10) });
        var target = MakeUnitWithAI(cm, player: 2);
        cm.AddComponent(target, new HealthComponent());

        PlayerCommandEvent? raised = null;
        cm.Events.PlayerCommand += e => raised = e;

        executor.Apply(NetCommand.Attack(1, attacker.Value, target.Value));

        Assert.NotNull(raised);
        Assert.Equal("attack", raised!.Type);
    }

    [Fact]
    public void Train_EnqueuesExactCount()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return; // LFS data missing — skip like ProductionQueueTests
        var cm = new ComponentManager(42, templates: templates);
        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent { Wood = 1000, Food = 1000, Stone = 1000, Metal = 1000, PopBonuses = 50 });
        cm.RegisterPlayer(1, playerEntity);
        var building = cm.CreateEntity();
        cm.AddComponent(building, new PositionComponent());
        cm.AddComponent(building, new ProductionQueue());
        cm.AddComponent(building, new OwnershipComponent { PlayerId = 1 });

        var executor = new SimCommandExecutor(cm);
        executor.Apply(NetCommand.Train(1, building.Value, "units/spart/support_civilian", count: 5));

        var queue = cm.QueryInterface<ProductionQueue>(building)!;
        Assert.Single(queue.Queue);
        Assert.Equal(5, queue.Queue[0].Count);
    }

    private static ComponentManager BuildWorldWithRichPlayer(uint seed = 42)
    {
        var templates = TryLoadTemplates();
        var cm = new ComponentManager(seed, templates: templates);
        SimSystem.Init(cm);
        var playerEntity = cm.CreateEntity();
        var player = new PlayerComponent();
        cm.AddComponent(playerEntity, player);
        // AddComponent 触发 OnInit 重置默认值 → 资源在挂载后设置
        player.Wood = 1000; player.Food = 1000; player.Stone = 1000; player.Metal = 1000; player.PopBonuses = 50;
        var techMgr = new TechnologyManager();
        cm.AddComponent(playerEntity, techMgr);
        // 数据驱动科技:配置真实 JSON 目录(LFS 缺失时 catalog 为空,研究类测试自然跳过)
        var techDir = FindRepoPath("binaries/data/mods/public/simulation/data/technologies");
        if (techDir != null)
            techMgr.Configure(Content.TechnologyLoader.LoadAll(techDir), "athen");
        cm.RegisterPlayer(1, playerEntity);
        return cm;
    }

    [Fact]
    public void Build_ChargesCostOnce_AndSpawnsFoundationOwnedByCommander()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;
        var cm = BuildWorldWithRichPlayer();
        var builder = MakeUnitWithAI(cm, player: 1);
        var executor = new SimCommandExecutor(cm);

        const string template = "structures/spart/house";
        int woodBefore = cm.GetPlayerEntity(1)!.Wood;

        executor.Apply(NetCommand.Build(1, builder.Value, template,
            Fixed.FromFloat(30f), Fixed.FromFloat(30f), Fixed.FromFloat(MathF.PI * 3f / 4f)));

        var player = cm.GetPlayerEntity(1)!;
        var stats = templates.ExtractStats(template);
        Assert.Equal(woodBefore - stats.WoodCost, player.Wood);
        var f = Assert.Single(cm.AllEntities, e => cm.QueryInterface<FoundationComponent>(e) != null);
        Assert.Equal(1, cm.QueryInterface<OwnershipComponent>(f)!.PlayerId);
        // The foundation carries the FULL template so the completion path (Task 7) can
        // rebuild the building without re-mapping a display name.
        Assert.Equal(template, cm.QueryInterface<FoundationComponent>(f)!.ResultTemplate);
    }

    [Fact]
    public void Build_Refused_WhenUnaffordable()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return; // without template cost data this test can't assert
        var cm = BuildWorldWithRichPlayer();
        cm.GetPlayerEntity(1)!.Wood = 0;
        cm.GetPlayerEntity(1)!.Stone = 0;
        cm.GetPlayerEntity(1)!.Metal = 0;
        cm.GetPlayerEntity(1)!.Food = 0;
        var builder = MakeUnitWithAI(cm);
        var executor = new SimCommandExecutor(cm);
        int entitiesBefore = cm.AllEntities.Count;

        executor.Apply(NetCommand.Build(1, builder.Value, "structures/spart/house",
            Fixed.FromFloat(30f), Fixed.FromFloat(30f), Fixed.FromFloat(MathF.PI * 3f / 4f)));

        Assert.Equal(entitiesBefore, cm.AllEntities.Count);
    }

    [Fact]
    public void Research_StartsExactlyOnce()
    {
        var cm = BuildWorldWithRichPlayer();
        // phase_town 前置(entity 形态:5 个 Village 类建筑)+ 范围索引(计数数据源)。
        SimSystem.SetRangeManager(new RangeManager(cm,
            ZeroAD.Sim.Maths.Fixed.FromInt(256), ZeroAD.Sim.Maths.Fixed.FromInt(256)));
        for (int i = 0; i < 5; i++)
        {
            var house = cm.CreateEntity();
            var hp = new PositionComponent();
            cm.AddComponent(house, hp);
            hp.Position = new ZeroAD.Sim.Maths.FixedVector3D(
                ZeroAD.Sim.Maths.Fixed.FromInt(20 + i * 8), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromInt(50));
            cm.AddComponent(house, new OwnershipComponent { PlayerId = 1 });
            cm.AddComponent(house, new IdentityComponent
            {
                TemplateName = "structures/athen/house",
                IsBuilding = true,
                Classes = new System.Collections.Generic.List<string> { "Village", "Structure" },
            });
            cm.NotifyEntityCreated(house);
            cm.NotifyOwnerChanged(house, -1, 1);
            var hp2 = new ZeroAD.Sim.Maths.FixedVector2D(hp.Position.X, hp.Position.Z);
            cm.NotifyPositionChanged(house, hp2, hp2);
        }
        var building = cm.CreateEntity();
        cm.AddComponent(building, new ResearcherComponent());
        cm.AddComponent(building, new OwnershipComponent { PlayerId = 1 });
        var executor = new SimCommandExecutor(cm);

        ResearchQueuedEvent? raised = null;
        cm.Events.ResearchQueued += e => raised = e;

        executor.Apply(NetCommand.Research(1, building.Value, "phase_town_generic"));

        Assert.NotNull(raised);
        Assert.Equal("phase_town_generic", raised!.TechnologyTemplate);
        Assert.True(cm.QueryInterface<ResearcherComponent>(building)!.IsResearching);
    }

    [Fact]
    public void SetRallyPoint_SetsPositionFromTargetEntity()
    {
        var cm = new ComponentManager(1);
        SimSystem.Init(cm);
        var building = cm.CreateEntity();
        cm.AddComponent(building, new RallyPointComponent());
        var target = cm.CreateEntity();
        cm.AddComponent(target, new PositionComponent());
        cm.QueryInterface<PositionComponent>(target)!.Position =
            new FixedVector3D(Fixed.FromFloat(11f), Fixed.Zero, Fixed.FromFloat(22f));
        var executor = new SimCommandExecutor(cm);

        executor.Apply(NetCommand.SetRallyPoint(1, building.Value, target.Value));

        var rally = cm.QueryInterface<RallyPointComponent>(building)!;
        Assert.Equal(Fixed.FromFloat(11f), rally.Position.X);
        Assert.Equal(Fixed.FromFloat(22f), rally.Position.Y);
    }

    [Fact]
    public void SetRallyPointPosition_SetsGroundPosition()
    {
        var cm = new ComponentManager(1);
        SimSystem.Init(cm);
        var building = cm.CreateEntity();
        cm.AddComponent(building, new RallyPointComponent());
        var executor = new SimCommandExecutor(cm);

        // Ground rally (right-click empty ground): FixedParam1/2 carry world x/z; IntParam1=0.
        executor.Apply(NetCommand.SetRallyPointPosition(1, building.Value,
            Fixed.FromFloat(33f), Fixed.FromFloat(44f)));

        var rally = cm.QueryInterface<RallyPointComponent>(building)!;
        Assert.Equal(Fixed.FromFloat(33f), rally.Position.X);
        Assert.Equal(Fixed.FromFloat(44f), rally.Position.Y);
    }

    [Fact]
    public void Garrison_IssuesGarrisonOrder()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var unit = MakeUnitWithAI(cm, player: 1);
        cm.AddComponent(unit, new GarrisonableComponent { Size = 1 });
        var holder = MakeGarrisonHolder(cm, player: 1);

        executor.Apply(NetCommand.Garrison(1, unit.Value, holder.Value));

        Assert.Equal("Garrison", cm.QueryInterface<UnitAIComponent>(unit)!.CurrentOrder?.Type);
    }

    [Fact]
    public void Garrison_RejectsForeignUnit()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var foreign = MakeUnitWithAI(cm, player: 2);
        cm.AddComponent(foreign, new GarrisonableComponent { Size = 1 });
        var holder = MakeGarrisonHolder(cm, player: 2);

        executor.Apply(NetCommand.Garrison(1, foreign.Value, holder.Value));

        Assert.True(cm.QueryInterface<UnitAIComponent>(foreign)!.IsIdle);
    }

    [Fact]
    public void Ungarrison_All_EjectsOccupants()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var unit = MakeUnitWithAI(cm, player: 1);
        cm.QueryInterface<IdentityComponent>(unit)!.Classes.Add("Infantry");
        cm.AddComponent(unit, new GarrisonableComponent { Size = 1 });
        var holder = MakeGarrisonHolder(cm, player: 1);
        Assert.True(cm.QueryInterface<GarrisonableComponent>(unit)!.Garrison(cm, holder));
        Assert.Single(cm.QueryInterface<GarrisonHolderComponent>(holder)!.Entities);

        executor.Apply(NetCommand.Ungarrison(1, holder.Value, -1));

        Assert.Empty(cm.QueryInterface<GarrisonHolderComponent>(holder)!.Entities);
    }

    [Fact]
    public void Ungarrison_RejectsForeignHolder()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var unit = MakeUnitWithAI(cm, player: 2);
        cm.QueryInterface<IdentityComponent>(unit)!.Classes.Add("Infantry");
        cm.AddComponent(unit, new GarrisonableComponent { Size = 1 });
        var holder = MakeGarrisonHolder(cm, player: 2);
        Assert.True(cm.QueryInterface<GarrisonableComponent>(unit)!.Garrison(cm, holder));

        executor.Apply(NetCommand.Ungarrison(1, holder.Value, -1));   // player 1 tries

        Assert.Single(cm.QueryInterface<GarrisonHolderComponent>(holder)!.Entities);
    }

    private static EntityId MakeGarrisonHolder(ComponentManager cm, int player)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        var id = new IdentityComponent();
        cm.AddComponent(e, id);
        id.Classes.Add("Structure");
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        var gh = new GarrisonHolderComponent { Max = 10 };
        cm.AddComponent(e, gh);
        gh.AllowedClasses.Add("Infantry");
        return e;
    }

    [Fact]
    public void SetUnitStance_SetsOwnUnitStance()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var unit = MakeUnitWithAI(cm, player: 1);

        executor.Apply(NetCommand.SetUnitStance(1, unit.Value, "defensive"));

        Assert.Equal("defensive", cm.QueryInterface<UnitAIComponent>(unit)!.Stance);
    }

    [Fact]
    public void SetUnitStance_RejectsForeignUnit()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var foreign = MakeUnitWithAI(cm, player: 2);

        executor.Apply(NetCommand.SetUnitStance(1, foreign.Value, "passive"));

        Assert.Equal("aggressive", cm.QueryInterface<UnitAIComponent>(foreign)!.Stance);
    }

    [Fact]
    public void SetUnitStance_RejectsUnknownStanceName()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var unit = MakeUnitWithAI(cm, player: 1);

        executor.Apply(NetCommand.SetUnitStance(1, unit.Value, "bogus"));

        Assert.Equal("aggressive", cm.QueryInterface<UnitAIComponent>(unit)!.Stance);
    }

    [Fact]
    public void Stop_ClearsUnitAIOrderQueue()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var unit = MakeUnitWithAI(cm);
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;
        ai.Walk(new FixedVector2D(Fixed.FromInt(50), Fixed.FromInt(50)));
        Assert.False(ai.IsIdle);

        executor.Apply(NetCommand.Stop(1, unit.Value));

        Assert.True(ai.IsIdle);
    }

    [Fact]
    public void Delete_DestroysOwnEntity()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var unit = MakeUnitWithAI(cm, player: 1);

        executor.Apply(NetCommand.Delete(1, unit.Value));

        Assert.DoesNotContain(unit, cm.AllEntities);
    }

    [Fact]
    public void Delete_RejectsForeignEntity()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var foreign = MakeUnitWithAI(cm, player: 2);

        executor.Apply(NetCommand.Delete(1, foreign.Value));

        Assert.Contains(foreign, cm.AllEntities);
    }

    [Fact]
    public void CancelProduction_RefundsAndDequeues()
    {
        var cm = new ComponentManager(1);
        SimSystem.Init(cm);
        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent());
        cm.RegisterPlayer(1, playerEntity);
        // OnInit resets resources to the skirmish defaults — set post-AddComponent.
        cm.QueryInterface<PlayerComponent>(playerEntity)!.Wood = 100;
        var trainer = cm.CreateEntity();
        cm.AddComponent(trainer, new PositionComponent());
        cm.AddComponent(trainer, new ProductionQueue());
        cm.AddComponent(trainer, new OwnershipComponent { PlayerId = 1 });
        cm.QueryInterface<ProductionQueue>(trainer)!
            .Enqueue("dummy", woodCost: 50, foodCost: 0, buildTime: 10f, count: 2);
        var executor = new SimCommandExecutor(cm);

        executor.Apply(NetCommand.CancelProduction(1, trainer.Value, 0));

        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;
        Assert.Equal(0, queue.QueueCount);
        Assert.Equal(200, cm.QueryInterface<PlayerComponent>(playerEntity)!.Wood);
    }

    // --- SetupTradeRoute / CancelSetupTradeRoute(原版 setup-trade-route 命令:
    // 双市场齐 → 推 Trade 订单,商队自动往返;否则走向首市场待命)---

    private static EntityId MakeTrader(ComponentManager cm, float x, float z)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new UnitMotion());
        var id = new IdentityComponent();
        cm.AddComponent(e, id);
        id.Classes.AddRange("Organic Support".Split(' '));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = 1 });
        cm.AddComponent(e, new TraderComponent { GainMultiplier = 0.75f });
        cm.AddComponent(e, new UnitAIComponent());
        return e;
    }

    private static EntityId MakeMarket(ComponentManager cm, float x, float z)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        var market = new MarketComponent();
        cm.AddComponent(e, market);
        market.TradeTypes.Add("land");
        cm.AddComponent(e, new OwnershipComponent { PlayerId = 1 });
        return e;
    }

    private static PlayerComponent AddPlayer(ComponentManager cm)
    {
        var pe = cm.CreateEntity();
        var pc = new PlayerComponent();
        cm.AddComponent(pe, pc);
        pc.Wood = 0; pc.Food = 0; pc.Stone = 0; pc.Metal = 0;
        cm.Players.AddPlayer(1, pe);
        return pc;
    }

    [Fact]
    public void SetupTradeRoute_BothMarkets_TraderShuttlesAutomatically()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        var player = AddPlayer(cm);
        var trader = MakeTrader(cm, 0f, 0f);
        var a = MakeMarket(cm, 0f, 0f);
        var b = MakeMarket(cm, 60f, 0f);   // 60m:每程 gain=1(同 TraderTests 基准)
        var tc = cm.QueryInterface<TraderComponent>(trader)!;
        var ai = cm.QueryInterface<UnitAIComponent>(trader)!;

        // 首市场:路由登记 + 走向首市场待命(原版 WalkToTarget(firstMarket))。
        executor.Apply(NetCommand.SetupTradeRoute(1, trader.Value, a.Value));
        Assert.Equal(a, tc.FirstMarket);
        Assert.False(tc.HasBothMarkets());
        Assert.Equal("Walk", ai.CurrentOrder?.Type);

        // 第二市场:双市场齐 → 推 Trade 订单(目标 = 首市场,force:false)。
        executor.Apply(NetCommand.SetupTradeRoute(1, trader.Value, b.Value));
        Assert.True(tc.HasBothMarkets());
        Assert.Equal("Trade", ai.CurrentOrder?.Type);
        Assert.Equal(a, ai.CurrentOrder!.Target);
        Assert.False(ai.CurrentOrder.Force);

        // 端到端:商队自动往返两市场并产生贸易收入。
        for (int i = 0; i < 400; i++)
        {
            cm.QueryInterface<UnitMotion>(trader)?.Tick(0.1f);
            ai.Tick(0.1f, cm);
        }
        int total = player.Food + player.Wood + player.Stone + player.Metal;
        Assert.True(total > 0, $"expected trade income; state={ai.FsmStateName} idx={tc.Index}");
    }

    [Fact]
    public void SetupTradeRoute_WithSource_EstablishesRouteInOneCommand()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        AddPlayer(cm);
        var trader = MakeTrader(cm, 0f, 0f);
        var a = MakeMarket(cm, 0f, 0f);
        var b = MakeMarket(cm, 60f, 0f);
        var tc = cm.QueryInterface<TraderComponent>(trader)!;

        // source 参数(原版 cmd.source):一条命令建双市场路由 → 直接推 Trade。
        executor.Apply(NetCommand.SetupTradeRoute(1, trader.Value, b.Value, sourceMarketId: a.Value));
        Assert.Equal(a, tc.FirstMarket);
        Assert.Equal(b, tc.SecondMarket);
        Assert.Equal("Trade", cm.QueryInterface<UnitAIComponent>(trader)!.CurrentOrder?.Type);
    }

    [Fact]
    public void CancelSetupTradeRoute_RemovesPendingFirstMarket_OnlyWhenSingle()
    {
        var cm = new ComponentManager(1);
        var executor = new SimCommandExecutor(cm);
        AddPlayer(cm);
        var trader = MakeTrader(cm, 0f, 0f);
        var a = MakeMarket(cm, 0f, 0f);
        var b = MakeMarket(cm, 60f, 0f);
        var tc = cm.QueryInterface<TraderComponent>(trader)!;

        executor.Apply(NetCommand.SetupTradeRoute(1, trader.Value, a.Value));
        Assert.Equal(a, tc.FirstMarket);

        // 单市场 → 摘除(原版 RemoveTargetMarket)。
        executor.Apply(NetCommand.CancelSetupTradeRoute(1, trader.Value, a.Value));
        Assert.Null(tc.FirstMarket);
        Assert.Equal(-1, tc.Index);

        // 双市场 → 拒绝摘除(原版:仅待定首市场可撤)。
        executor.Apply(NetCommand.SetupTradeRoute(1, trader.Value, a.Value));
        executor.Apply(NetCommand.SetupTradeRoute(1, trader.Value, b.Value));
        Assert.True(tc.HasBothMarkets());
        executor.Apply(NetCommand.CancelSetupTradeRoute(1, trader.Value, a.Value));
        Assert.True(tc.HasBothMarkets());
    }
}
