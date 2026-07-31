using System.Linq;
using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Tests for the post-sink ProductionQueue: EnqueueTraining (cost/limit/pop validation),
/// Tick driving sim-side spawn via ComponentManager.SpawnEntity, and the Count loop fix.
/// </summary>
public class ProductionQueueTests
{
    /// <summary>
    /// Path to the real 0 A.D. templates shipped in this repo. Tests that need real cost/build-time
    /// data load from here; if the data tree isn't present (LFS not pulled), those tests skip.
    /// </summary>
    private const string TemplatesRoot = "../../../binaries/data/mods/public/simulation/templates";

    private static TemplateLoader? TryLoadTemplates()
    {
        if (!System.IO.Directory.Exists(TemplatesRoot)) return null;
        return new TemplateLoader(TemplatesRoot);
    }

    private static ComponentManager BuildWorldWithPlayer(out EntityId trainer, out EntityId playerEntity)
    {
        var cm = new ComponentManager(42);

        playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent { Wood = 1000, Food = 1000, Stone = 1000, Metal = 1000, PopBonuses = 50 });
        cm.RegisterPlayer(1, playerEntity);

        trainer = cm.CreateEntity();
        cm.AddComponent(trainer, new PositionComponent());
        cm.AddComponent(trainer, new ProductionQueue());
        cm.AddComponent(trainer, new OwnershipComponent { PlayerId = 1 });
        return cm;
    }

    [Fact]
    public void Tick_SpawnsAllCountUnits()
    {
        // Regression for the "batch trains 5 but only spawns 1" bug. Uses the simple Enqueue
        // path so it doesn't depend on template data being present.
        var cm = BuildWorldWithPlayer(out var trainer, out _);

        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;
        queue.Enqueue("dummy_unit", woodCost: 0, foodCost: 0, buildTime: 1.0f, count: 5);

        int entitiesBefore = cm.AllEntities.Count;
        int createdEvents = 0;
        cm.Events.EntityCreated += _ => createdEvents++;

        // Tick past the build time in one go.
        queue.Tick(1.5f, cm);

        // 5 fresh units (one per Count) plus the original trainer. EntityCreated fires per spawn.
        Assert.Equal(5, cm.AllEntities.Count - entitiesBefore);
        Assert.Equal(5, createdEvents);
    }

    [Fact]
    public void Tick_RaisesTrainingFinished()
    {
        var cm = BuildWorldWithPlayer(out var trainer, out _);
        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;
        queue.Enqueue("dummy", 0, 0, 1.0f, count: 1);

        TrainingFinishedEvent? received = null;
        cm.Events.TrainingFinished += e => received = e;

        queue.Tick(1.5f, cm);
        Assert.NotNull(received);
        Assert.Equal(trainer, received!.TrainerEntity);
    }

    [Fact]
    public void Tick_AppliesRallyPoint()
    {
        // Spawned units are sent toward the trainer's rally point via a real UnitAI Walk
        // order, NOT a raw UnitMotion MoveToPoint. Raw MoveToPoint sets the motion goal
        // but leaves the FSM in IDLE, so the unit glides to the rally with no walk
        // animation (ResolveAnimationState keys off the FSM state). The Walk order
        // transitions the FSM to WALKING, which drives the walk clip.
        var cm = BuildWorldWithPlayer(out var trainer, out _);
        cm.AddComponent(trainer, new RallyPointComponent());
        var rally = cm.QueryInterface<RallyPointComponent>(trainer)!;
        rally.Set(new FixedVector2D(Fixed.FromInt(100), Fixed.FromInt(100)));

        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;
        queue.Enqueue("dummy", 0, 0, 1.0f, count: 1);

        queue.Tick(1.5f, cm);

        // The spawned unit is the newest entity. Production.Tick queued a Walk order;
        // dispatch it by ticking the unit's UnitAI (mirrors what a sim turn does).
        var spawned = cm.AllEntities.Last(e => e != trainer && e != cm.GetPlayerEntityId(1)!.Value);
        var ai = cm.QueryInterface<UnitAIComponent>(spawned);
        Assert.NotNull(ai);
        ai!.Tick(0.1f, cm);

        var motion = cm.QueryInterface<UnitMotion>(spawned);
        Assert.NotNull(motion);
        Assert.True(motion!.HasMoveTarget, "the Walk order must set a motion target toward the rally");
        Assert.Contains("WALKING", ai.FsmStateName);
    }

    [Fact]
    public void EnqueueTraining_ReturnsFalseWithoutTemplates()
    {
        // Without a template loader, EnqueueTraining can't read costs and must refuse rather
        // than spawn a free unit. This guards the sim against silent zero-cost training in
        // headless/test setups.
        var cm = BuildWorldWithPlayer(out var trainer, out _);
        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;

        Assert.False(queue.EnqueueTraining("anything", count: 1, cm));
        Assert.Equal(0, queue.QueueCount);
    }

    [Fact]
    public void EnqueueTraining_ReturnsFalseWithoutOwnership()
    {
        // A trainer with no owner can't bill anyone.
        var cm = new ComponentManager(42);
        var trainer = cm.CreateEntity();
        cm.AddComponent(trainer, new PositionComponent());
        cm.AddComponent(trainer, new ProductionQueue());
        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;

        Assert.False(queue.EnqueueTraining("anything", 1, cm));
    }

    [Fact]
    public void EnqueueTraining_DeductsTemplateCost()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return; // skip when 0 A.D. data isn't present

        var cm = BuildWorldWithPlayer(out var trainer, out var playerEntity);
        cm.Templates = templates;
        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;
        var player = cm.QueryInterface<PlayerComponent>(playerEntity)!;
        int woodBefore = player.Wood;
        int foodBefore = player.Food;

        // support_female_citizen is the cheapest, well-known villager template.
        bool ok = queue.EnqueueTraining("units/athen/support_female_citizen", count: 1, cm);
        Assert.True(ok, "training a villager the player can afford should succeed");
        Assert.True(player.Wood < woodBefore || player.Food < foodBefore,
            "training must deduct at least one resource from the template cost");
    }

    [Fact]
    public void EnqueueTraining_BlockedByPopLimit()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var cm = BuildWorldWithPlayer(out var trainer, out var playerEntity);
        cm.Templates = templates;
        // Starve pop: only 1 headroom.
        var player = cm.QueryInterface<PlayerComponent>(playerEntity)!;
        player.PopBonuses = 1;
        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;

        // Batching 5 units when only 1 pop fits must be rejected wholesale (no partial charge).
        bool ok = queue.EnqueueTraining("units/athen/support_female_citizen", count: 5, cm);
        Assert.False(ok);
        Assert.Equal(0, queue.QueueCount);
    }

    [Fact]
    public void EnqueueTraining_RaisesQueuedEvent()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var cm = BuildWorldWithPlayer(out var trainer, out _);
        cm.Templates = templates;
        var queue = cm.QueryInterface<ProductionQueue>(trainer)!;

        TrainingQueuedEvent? ev = null;
        cm.Events.TrainingQueued += e => ev = e;

        queue.EnqueueTraining("units/athen/support_female_citizen", count: 1, cm);
        Assert.NotNull(ev);
        Assert.Equal("units/athen/support_female_citizen", ev!.UnitTemplate);
    }
}
