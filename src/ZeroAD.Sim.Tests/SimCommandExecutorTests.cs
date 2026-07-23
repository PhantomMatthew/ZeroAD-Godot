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
}
