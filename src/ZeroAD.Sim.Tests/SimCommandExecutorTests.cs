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
            Fixed.FromFloat(30f), Fixed.FromFloat(30f)));

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
            Fixed.FromFloat(30f), Fixed.FromFloat(30f)));

        Assert.Equal(entitiesBefore, cm.AllEntities.Count);
    }

    [Fact]
    public void Research_StartsExactlyOnce()
    {
        var cm = BuildWorldWithRichPlayer();
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
}
