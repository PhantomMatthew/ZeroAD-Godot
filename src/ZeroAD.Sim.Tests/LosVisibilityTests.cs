using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>Per-turn visibility: ComputeLosVisibility chain, dirty tracking,
/// VisibilityChangedEvent, reveal-all, position queries.</summary>
public class LosVisibilityTests
{
    private static (ComponentManager cm, RangeManager rm) NewWorld(int sizeMeters = 256)
    {
        var cm = new ComponentManager(42);
        var rm = new RangeManager(cm, Fixed.FromInt(sizeMeters), Fixed.FromInt(sizeMeters));
        // Two registered players (player entities are minimal).
        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent());
        cm.Players.AddPlayer(1, p1);
        var p2 = cm.CreateEntity();
        cm.AddComponent(p2, new PlayerComponent());
        cm.Players.AddPlayer(2, p2);
        return (cm, rm);
    }

    private static EntityId Spawn(ComponentManager cm, RangeManager rm,
        int x, int z, int owner = 1, int range = 0, byte flags = 0)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));
        if (owner > 0)
            cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        if (range > 0)
        {
            cm.AddComponent(e, new VisionComponent());
            cm.QueryInterface<VisionComponent>(e)!.Range = Fixed.FromInt(range);
        }
        cm.NotifyEntityCreated(e);
        rm.RefreshFromComponents(e);
        if (flags != 0)
            rm.SetEntityFlags(e, flags);
        var p = new FixedVector2D(Fixed.FromInt(x), Fixed.FromInt(z));
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    private static void Move(ComponentManager cm, EntityId e, int fromX, int fromZ, int toX, int toZ)
    {
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromInt(toX), Fixed.Zero, Fixed.FromInt(toZ));
        cm.NotifyPositionChanged(e,
            new FixedVector2D(Fixed.FromInt(fromX), Fixed.FromInt(fromZ)),
            new FixedVector2D(Fixed.FromInt(toX), Fixed.FromInt(toZ)));
    }

    [Fact]
    public void NotInWorld_Hidden()
    {
        var (cm, rm) = NewWorld();
        var e = cm.CreateEntity();
        cm.NotifyEntityCreated(e); // tracked, but no position → not in world
        rm.UpdateVisibilityData();
        Assert.Equal(LosVisibility.Hidden, rm.GetLosVisibility(e, 1));
    }

    [Fact]
    public void InOwnLos_Visible()
    {
        var (cm, rm) = NewWorld();
        Spawn(cm, rm, 100, 100, owner: 1, range: 20);          // seer
        var friend = Spawn(cm, rm, 104, 100, owner: 1);        // inside the circle
        rm.UpdateVisibilityData();
        Assert.Equal(LosVisibility.Visible, rm.GetLosVisibility(friend, 1));
    }

    [Fact]
    public void Unexplored_EnemyHidden()
    {
        var (cm, rm) = NewWorld();
        Spawn(cm, rm, 100, 100, owner: 1, range: 16);
        var enemy = Spawn(cm, rm, 240, 240, owner: 2);         // far outside
        rm.UpdateVisibilityData();
        Assert.Equal(LosVisibility.Hidden, rm.GetLosVisibility(enemy, 1));
    }

    [Fact]
    public void ExploredFog_UnitHidden_RetainInFogEntityFogged()
    {
        var (cm, rm) = NewWorld();
        var seer = Spawn(cm, rm, 100, 100, owner: 1, range: 20);
        var enemyUnit = Spawn(cm, rm, 104, 100, owner: 2);
        var enemyBld = Spawn(cm, rm, 108, 100, owner: 2,
            flags: RangeEntityData.FlagRetainInFog);
        rm.UpdateVisibilityData();
        Assert.Equal(LosVisibility.Visible, rm.GetLosVisibility(enemyUnit, 1));
        Assert.Equal(LosVisibility.Visible, rm.GetLosVisibility(enemyBld, 1));

        // Seer leaves: area stays explored but is no longer visible.
        Move(cm, seer, 100, 100, 240, 240);
        rm.UpdateVisibilityData();
        Assert.True(LosVisibility.Hidden == rm.GetLosVisibility(enemyUnit, 1),
            "no RetainInFog → hidden in fog");
        Assert.True(LosVisibility.Fogged == rm.GetLosVisibility(enemyBld, 1),
            "RetainInFog → fogged");
    }

    [Fact]
    public void RevealAll_MakesRealVisible_MirageHidden()
    {
        var (cm, rm) = NewWorld();
        var enemy = Spawn(cm, rm, 240, 240, owner: 2);
        var mirage = Spawn(cm, rm, 240, 240, owner: 2,
            flags: RangeEntityData.FlagIsMirage | RangeEntityData.FlagRetainInFog);
        cm.AddComponent(mirage, new MirageComponent());
        var mc = cm.QueryInterface<MirageComponent>(mirage)!;
        mc.Parent = enemy; mc.Player = 1;

        rm.SetLosRevealAll(1, true);
        rm.UpdateVisibilityData();

        Assert.Equal(LosVisibility.Visible, rm.GetLosVisibility(enemy, 1));
        Assert.True(LosVisibility.Hidden == rm.GetLosVisibility(mirage, 1),
            "reveal-all makes mirages useless");
    }

    [Fact]
    public void VisibilityChangedEvent_FiresOnce_WithOldNew()
    {
        var (cm, rm) = NewWorld();
        var seer = Spawn(cm, rm, 60, 60, owner: 1, range: 16);
        var enemy = Spawn(cm, rm, 200, 200, owner: 2);
        rm.UpdateVisibilityData(); // settle: hidden

        var events = new List<VisibilityChangedEvent>();
        cm.Events.VisibilityChanged += e => events.Add(e);

        Move(cm, seer, 60, 60, 192, 192); // enemy enters the circle
        rm.UpdateVisibilityData();

        var ev = Assert.Single(events, e => e.Entity == enemy && e.Player == 1);
        Assert.Equal(LosVisibility.Hidden, ev.Old);
        Assert.Equal(LosVisibility.Visible, ev.New);

        // Second pass with no changes: no further events.
        rm.UpdateVisibilityData();
        Assert.Single(events, e => e.Entity == enemy && e.Player == 1);
    }

    [Fact]
    public void GetLosVisibilityPosition_ThreeStates()
    {
        var (cm, rm) = NewWorld();
        var seer = Spawn(cm, rm, 100, 100, owner: 1, range: 16);
        rm.UpdateVisibilityData();
        Assert.Equal(LosVisibility.Visible,
            rm.GetLosVisibilityPosition(Fixed.FromInt(100), Fixed.FromInt(100), 1));
        Assert.Equal(LosVisibility.Hidden,
            rm.GetLosVisibilityPosition(Fixed.FromInt(240), Fixed.FromInt(240), 1));

        Move(cm, seer, 100, 100, 240, 240);
        rm.UpdateVisibilityData();
        Assert.True(LosVisibility.Fogged ==
            rm.GetLosVisibilityPosition(Fixed.FromInt(100), Fixed.FromInt(100), 1),
            "explored but no longer visible");
    }

    [Fact]
    public void Events_OrderedByEntityId()
    {
        var (cm, rm) = NewWorld();
        var seer = Spawn(cm, rm, 60, 60, owner: 1, range: 24);
        var e1 = Spawn(cm, rm, 200, 200, owner: 2);
        var e2 = Spawn(cm, rm, 204, 200, owner: 2);
        Assert.True(e1.Value < e2.Value);
        rm.UpdateVisibilityData(); // settle hidden

        var events = new List<VisibilityChangedEvent>();
        cm.Events.VisibilityChanged += e => events.Add(e);

        Move(cm, seer, 60, 60, 196, 196);
        rm.UpdateVisibilityData();

        var reveals = events.FindAll(e => e.New == LosVisibility.Visible && e.Player == 1);
        Assert.Equal(2, reveals.Count);
        Assert.True(reveals[0].Entity.Value < reveals[1].Entity.Value,
            "visibility events fire in entity-id order");
    }
}
