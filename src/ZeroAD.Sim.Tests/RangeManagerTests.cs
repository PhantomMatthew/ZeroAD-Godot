using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Tests for RangeManager: spatial-index-driven range queries and per-player entity lists.
/// These replace the O(N) linear scans that SimBridge.FindNearest used to do.
/// </summary>
public class RangeManagerTests
{
    private static (ComponentManager cm, RangeManager rm) NewWorld(int size = 64)
    {
        var cm = new ComponentManager(42);
        var rm = new RangeManager(cm, Fixed.FromInt(size), Fixed.FromInt(size));
        return (cm, rm);
    }

    private static EntityId SpawnAt(ComponentManager cm, RangeManager rm, int x, int z, int owner = -1)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        var pos = cm.QueryInterface<PositionComponent>(e)!;
        pos.Position = new FixedVector3D(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));
        if (owner > 0)
            cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        // Notify RangeManager the entity exists + is placed. (EntityCreated fires from AddComponent
        // of the first component; position is set above so RefreshFromComponents picks it up.)
        cm.NotifyEntityCreated(e);
        rm.RefreshFromComponents(e);
        var pos2 = new FixedVector2D(Fixed.FromInt(x), Fixed.FromInt(z));
        cm.NotifyPositionChanged(e, pos2, pos2);
        return e;
    }

    [Fact]
    public void ExecuteQuery_ReturnsEntitiesInRange()
    {
        var (cm, rm) = NewWorld();
        var src = SpawnAt(cm, rm, 10, 10);
        var near = SpawnAt(cm, rm, 12, 10);     // 2 units away
        var far = SpawnAt(cm, rm, 50, 50);       // well outside

        var result = rm.ExecuteQuery(src, Fixed.Zero, Fixed.FromInt(5));
        Assert.Contains(near, result);
        Assert.DoesNotContain(far, result);
        Assert.DoesNotContain(src, result);     // source excludes itself
    }

    [Fact]
    public void ExecuteQuery_RespectsMinRange()
    {
        var (cm, rm) = NewWorld();
        var src = SpawnAt(cm, rm, 10, 10);
        var close = SpawnAt(cm, rm, 11, 10);     // 1 unit away
        var mid = SpawnAt(cm, rm, 15, 10);       // 5 units away

        // minRange=3: close (dist 1) is excluded, mid (dist 5) included within maxRange=10.
        var result = rm.ExecuteQuery(src, Fixed.FromInt(3), Fixed.FromInt(10));
        Assert.DoesNotContain(close, result);
        Assert.Contains(mid, result);
    }

    [Fact]
    public void ExecuteQuery_AppliesPredicate()
    {
        var (cm, rm) = NewWorld();
        var src = SpawnAt(cm, rm, 10, 10, owner: 1);
        var ally = SpawnAt(cm, rm, 12, 10, owner: 1);
        var enemy = SpawnAt(cm, rm, 11, 10, owner: 2);

        // Only return enemies (owner != source's owner).
        var result = rm.ExecuteQuery(src, Fixed.Zero, Fixed.FromInt(10),
            predicate: eid => cm.QueryInterface<OwnershipComponent>(eid)?.PlayerId != 1);
        Assert.Contains(enemy, result);
        Assert.DoesNotContain(ally, result);
    }

    [Fact]
    public void GetEntitiesByPlayer_FiltersByOwner()
    {
        var (cm, rm) = NewWorld();
        SpawnAt(cm, rm, 10, 10, owner: 1);
        SpawnAt(cm, rm, 20, 20, owner: 1);
        SpawnAt(cm, rm, 30, 30, owner: 2);
        SpawnAt(cm, rm, 40, 40);                  // no owner

        Assert.Equal(2, rm.GetEntitiesByPlayer(1).Count);
        Assert.Single(rm.GetEntitiesByPlayer(2));
        Assert.Empty(rm.GetEntitiesByPlayer(3));
    }

    [Fact]
    public void GetNonGaiaEntities_ExcludesUnowned()
    {
        var (cm, rm) = NewWorld();
        SpawnAt(cm, rm, 10, 10, owner: 1);
        SpawnAt(cm, rm, 20, 20, owner: 2);
        SpawnAt(cm, rm, 30, 30);                  // no owner (gaia-ish)

        var nonGaia = rm.GetNonGaiaEntities();
        Assert.Equal(2, nonGaia.Count);
    }

    [Fact]
    public void PositionChanged_UpdatesQueryResults()
    {
        var (cm, rm) = NewWorld();
        var src = SpawnAt(cm, rm, 10, 10);
        var mover = SpawnAt(cm, rm, 50, 50);      // starts far

        Assert.Empty(rm.ExecuteQuery(src, Fixed.Zero, Fixed.FromInt(5)));

        // Move the entity close to the source.
        var oldPos = new FixedVector2D(Fixed.FromInt(50), Fixed.FromInt(50));
        var newPos = new FixedVector2D(Fixed.FromInt(12), Fixed.FromInt(10));
        cm.NotifyPositionChanged(mover, oldPos, newPos);

        Assert.Contains(mover, rm.ExecuteQuery(src, Fixed.Zero, Fixed.FromInt(5)));
    }

    [Fact]
    public void EntityDestroyed_RemovedFromIndex()
    {
        var (cm, rm) = NewWorld();
        var src = SpawnAt(cm, rm, 10, 10);
        var other = SpawnAt(cm, rm, 12, 10);

        Assert.Contains(other, rm.ExecuteQuery(src, Fixed.Zero, Fixed.FromInt(5)));

        cm.NotifyEntityDestroyed(other);
        cm.DestroyEntity(other);

        Assert.DoesNotContain(other, rm.ExecuteQuery(src, Fixed.Zero, Fixed.FromInt(5)));
    }

    [Fact]
    public void ExecuteQuery_DeterministicOrder()
    {
        // Same entities in any insertion order must return sorted by entity id.
        var (cm, rm) = NewWorld();
        var src = SpawnAt(cm, rm, 10, 10);
        // Spawn in non-sorted order of ids (they get sequential ids, so spawn reverse-distance).
        var a = SpawnAt(cm, rm, 11, 10);
        var b = SpawnAt(cm, rm, 12, 10);
        var c = SpawnAt(cm, rm, 13, 10);

        var result = rm.ExecuteQuery(src, Fixed.Zero, Fixed.FromInt(10));
        // Should be sorted ascending by entity id.
        for (int i = 1; i < result.Count; i++)
            Assert.True(result[i - 1].Value < result[i].Value);
    }
}
