using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>Vision/Range through the modifiers pipeline: tech bonuses grow/shrink the
/// effective range, RangeManager re-covers the LOS circle, no-change reapply is a no-op.</summary>
public sealed class VisionModifierTests
{
    private static (ComponentManager cm, RangeManager rm, EntityId playerEnt) NewWorld()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var rm = new RangeManager(cm, Fixed.FromInt(256), Fixed.FromInt(256));
        SimSystem.SetRangeManager(rm);
        var playerEnt = cm.CreateEntity();
        cm.AddComponent(playerEnt, new PlayerComponent());
        cm.AddComponent(playerEnt, new OwnershipComponent { PlayerId = 1 });
        cm.Players.AddPlayer(1, playerEnt);
        return (cm, rm, playerEnt);
    }

    private static EntityId SpawnSeer(ComponentManager cm, RangeManager rm, int x, int z, int range)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = 1 });
        cm.AddComponent(e, new IdentityComponent { Classes = new List<string> { "Unit", "Soldier" } });
        cm.AddComponent(e, new VisionComponent());
        cm.QueryInterface<VisionComponent>(e)!.Range = Fixed.FromInt(range);
        cm.NotifyEntityCreated(e);
        rm.RefreshFromComponents(e);
        var p = new FixedVector2D(Fixed.FromInt(x), Fixed.FromInt(z));
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    private static Modification Add(string path, float a) => new(path, a, null, null, new List<string>());
    private static Modification Mul(string path, float m) => new(path, null, m, null, new List<string>());

    [Fact]
    public void TechBonus_GrowsRange_LosCoversMore()
    {
        var (cm, rm, playerEnt) = NewWorld();
        SpawnSeer(cm, rm, 100, 100, range: 16);
        // Vertex (32,25) = 28m out from the seer's tile: outside 16m, inside 32m.
        Assert.Equal(0, rm.Los.GetCount(1, 32, 25));

        cm.Modifiers.AddModifiers("tech_vision", new[] { Add("Vision/Range", 16f) }, playerEnt);
        ValueModificationApplier.ReapplyVisionRangeAll(cm, rm);

        Assert.True(rm.Los.GetCount(1, 32, 25) > 0, "modified 32m range covers the vertex");
        Assert.True(rm.Los.IsVisible(1, 32, 25));
    }

    [Fact]
    public void Reapply_NoChange_NoLosChurn()
    {
        var (cm, rm, playerEnt) = NewWorld();
        SpawnSeer(cm, rm, 100, 100, range: 16);
        rm.UpdateVisibilityData();

        var events = new List<Events.VisibilityChangedEvent>();
        cm.Events.VisibilityChanged += e => events.Add(e);

        ValueModificationApplier.ReapplyVisionRangeAll(cm, rm); // no modifier change
        rm.UpdateVisibilityData();

        Assert.Empty(events);
        Assert.True(rm.Los.IsVisible(1, 25, 25), "circle untouched");
        Assert.True(rm.Los.GetCount(1, 25, 25) == 1, "no double-add from a no-op reapply");
    }

    [Fact]
    public void RangeShrinks_VisibilityLost_ExploredStays()
    {
        var (cm, rm, playerEnt) = NewWorld();
        SpawnSeer(cm, rm, 100, 100, range: 16);
        Assert.True(rm.Los.IsVisible(1, 28, 25), "12m vertex visible at range 16");

        cm.Modifiers.AddModifiers("debuff", new[] { Mul("Vision/Range", 0.5f) }, playerEnt);
        ValueModificationApplier.ReapplyVisionRangeAll(cm, rm);

        Assert.False(rm.Los.IsVisible(1, 28, 25), "outside the shrunk 8m range");
        Assert.True(rm.Los.IsExplored(1, 28, 25), "explored never decays");
        Assert.True(rm.Los.IsVisible(1, 26, 25), "4m vertex still visible at range 8");
    }
}
