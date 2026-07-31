using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>RangeManager ↔ LOS grid integration: entity lifecycle events drive
/// per-player vision counts (spawn/move/destroy/owner-change/SetBounds).</summary>
public class RangeManagerLosTests
{
    private static (ComponentManager cm, RangeManager rm) NewWorld(int sizeMeters = 256)
    {
        var cm = new ComponentManager(42);
        var rm = new RangeManager(cm, Fixed.FromInt(sizeMeters), Fixed.FromInt(sizeMeters));
        return (cm, rm);
    }

    private static EntityId SpawnSeer(ComponentManager cm, RangeManager rm,
        int x, int z, int owner = 1, int range = 16)
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
        var p = new FixedVector2D(Fixed.FromInt(x), Fixed.FromInt(z));
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    [Fact]
    public void SpawnSeer_AddsLosAroundPosition()
    {
        var (cm, rm) = NewWorld();
        SpawnSeer(cm, rm, 100, 100, owner: 1, range: 16);

        Assert.True(rm.Los.IsVisible(1, 25, 25), "center vertex visible");
        Assert.True(rm.Los.IsExplored(1, 25, 25));
        Assert.False(rm.Los.IsVisible(1, 5, 5), "far vertex not visible");
        Assert.False(rm.Los.IsVisible(2, 25, 25), "other player unaffected");
    }

    [Fact]
    public void MoveSeer_UpdatesLos()
    {
        var (cm, rm) = NewWorld();
        var e = SpawnSeer(cm, rm, 100, 100, owner: 1, range: 16);
        Assert.True(rm.Los.IsVisible(1, 25, 25));

        cm.NotifyPositionChanged(e,
            new FixedVector2D(Fixed.FromInt(100), Fixed.FromInt(100)),
            new FixedVector2D(Fixed.FromInt(200), Fixed.FromInt(200)));

        Assert.False(rm.Los.IsVisible(1, 25, 25), "old position lost visibility");
        Assert.True(rm.Los.IsExplored(1, 25, 25), "old position stays explored");
        Assert.True(rm.Los.IsVisible(1, 50, 50), "new position visible");
    }

    [Fact]
    public void DestroySeer_RemovesLos()
    {
        var (cm, rm) = NewWorld();
        var e = SpawnSeer(cm, rm, 100, 100, owner: 1, range: 16);
        Assert.True(rm.Los.IsVisible(1, 25, 25));

        cm.DestroyEntity(e);
        Assert.False(rm.Los.IsVisible(1, 25, 25));
        Assert.True(rm.Los.IsExplored(1, 25, 25));
    }

    [Fact]
    public void OwnerChange_MovesLosToNewOwner()
    {
        var (cm, rm) = NewWorld();
        var e = SpawnSeer(cm, rm, 100, 100, owner: 1, range: 16);
        Assert.True(rm.Los.IsVisible(1, 25, 25));

        cm.NotifyOwnerChanged(e, 1, 2);
        Assert.False(rm.Los.IsVisible(1, 25, 25), "old owner lost circle");
        Assert.True(rm.Los.IsVisible(2, 25, 25), "new owner gained circle");

        // Dropping to no-owner removes the circle entirely.
        cm.NotifyOwnerChanged(e, 2, -1);
        Assert.False(rm.Los.IsVisible(2, 25, 25));
    }

    [Fact]
    public void NoVisionRange_ProducesNoLos()
    {
        var (cm, rm) = NewWorld();
        SpawnSeer(cm, rm, 100, 100, owner: 1, range: 0);
        for (int j = 20; j <= 30; j++)
            for (int i = 20; i <= 30; i++)
                Assert.Equal(0, rm.Los.GetCount(1, i, j));
    }

    [Fact]
    public void SetBounds_RebuildsAndKeepsWorking()
    {
        var (cm, rm) = NewWorld(64); // constructor default covers only 64m
        var old = SpawnSeer(cm, rm, 32, 32, owner: 1, range: 12);

        rm.SetBounds(Fixed.FromInt(768));
        Assert.True(rm.Los.VerticesPerSide > 190);

        // Old entity re-indexed: still seer after the rebuild.
        Assert.True(rm.Los.IsVisible(1, 8, 8));

        // New entity beyond the old 64m limit works now.
        SpawnSeer(cm, rm, 700, 700, owner: 2, range: 16);
        Assert.True(rm.Los.IsVisible(2, 175, 175));

        // Spatial queries still work on the rebuilt index.
        var near = rm.ExecuteQuery(old, Fixed.Zero, Fixed.FromInt(20));
        Assert.DoesNotContain(near, e => e == old);
    }

    [Fact]
    public void NonSeer_Move_AllocatesNoCounts()
    {
        var (cm, rm) = NewWorld();
        var e = SpawnSeer(cm, rm, 60, 60, owner: 1, range: 0);
        cm.NotifyPositionChanged(e,
            new FixedVector2D(Fixed.FromInt(60), Fixed.FromInt(60)),
            new FixedVector2D(Fixed.FromInt(120), Fixed.FromInt(120)));

        // No count grid should exist for player 1 at all.
        Assert.Equal(0, rm.Los.GetCount(1, 15, 15));
        Assert.Equal(0, rm.Los.GetCount(1, 30, 30));
        Assert.False(rm.Los.IsVisible(1, 30, 30));
    }

    [Fact]
    public void Spawn_WithoutPositionNotify_StillEvaluatedForAllPlayers()
    {
        var (cm, rm) = NewWorld();
        var p2 = cm.CreateEntity();
        cm.AddComponent(p2, new PlayerComponent());
        cm.Players.AddPlayer(2, p2);
        SpawnSeer(cm, rm, 100, 100, owner: 2, range: 20);
        rm.UpdateVisibilityData(); // settle: dirty bits cleared

        // Production spawn path: components + NotifyEntityCreated only, no position message.
        var u = cm.CreateEntity();
        cm.AddComponent(u, new PositionComponent());
        cm.QueryInterface<PositionComponent>(u)!.Position =
            new FixedVector3D(Fixed.FromInt(100), Fixed.Zero, Fixed.FromInt(100));
        cm.AddComponent(u, new OwnershipComponent { PlayerId = 1 });
        cm.NotifyEntityCreated(u);
        rm.UpdateVisibilityData();

        Assert.Equal(LosVisibility.Visible, rm.GetLosVisibility(u, 2));
    }

    [Fact]
    public void AssembleUnit_AttachesVisionFromTemplate()
    {
        // Template stats with a vision range must produce a Fixed-range VisionComponent.
        var cm = new ComponentManager(42);
        var stats = new Content.TemplateStats { VisionRange = 24 };
        var e = cm.CreateEntity();
        EntityAssembler.AssembleUnit(cm, e, "units/test_seer", stats, 50, 50);
        var vis = cm.QueryInterface<VisionComponent>(e);
        Assert.NotNull(vis);
        Assert.Equal(Fixed.FromInt(24), vis!.Range);
    }

    [Fact]
    public void LosVersion_BumpsOnRecompute_NotOnEarlyOut()
    {
        // LosVersion is the render-side change signal FogWorldRenderer gates its per-frame
        // texture rebuild on (mirrors TerritoryManager.Version). It must bump exactly when
        // UpdateVisibilityData does real work (something moved/placed or the LOS grid changed)
        // and stay flat on the idle early-out, so the fog rebuilds only on turns that changed
        // visibility — not every render frame.
        var (cm, rm) = NewWorld();
        var e = SpawnSeer(cm, rm, 100, 100, owner: 1, range: 16);
        int v0 = rm.LosVersion;

        // Spawn dirtied moved/placed + the LOS grid (SetBounds set the dirty mask) → recompute → bump.
        rm.UpdateVisibilityData();
        int v1 = rm.LosVersion;
        Assert.True(v1 > v0, "version must bump when visibility is recomputed");

        // Idle pass: nothing moved/placed/requested, dirty mask clear → early-out → no bump.
        rm.UpdateVisibilityData();
        Assert.Equal(v1, rm.LosVersion);

        // A fresh move re-dirties → the next pass bumps again.
        cm.NotifyPositionChanged(e,
            new FixedVector2D(Fixed.FromInt(100), Fixed.FromInt(100)),
            new FixedVector2D(Fixed.FromInt(200), Fixed.FromInt(200)));
        rm.UpdateVisibilityData();
        Assert.True(rm.LosVersion > v1, "version must bump again after a move");
    }
}
