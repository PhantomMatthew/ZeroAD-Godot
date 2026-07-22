using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;
using Xunit;

namespace ZeroAD.Sim.Tests.Pathfinding;

// Tests for the pathfinding core data types (Pathfinding/Core.cs + PriorityQueue.cs).
// These are the shared primitives the three pathfinders build on; correctness here is
// load-bearing for every path computed downstream.
public sealed class PathfindingCoreTests
{
    [Fact]
    public void NavcellData_IsPassable_UsesSetBitMeansImpassable()
    {
        var land = PathfindingCore.PassClassMaskFromIndex(0);   // default = bit 0
        var water = PathfindingCore.PassClassMaskFromIndex(1);  // ship = bit 1

        var cell = new NavcellData(0);
        Assert.True(PathfindingCore.IsPassable(cell, land));
        Assert.True(PathfindingCore.IsPassable(cell, water));

        // Mark impassable for land only: ship still passable.
        cell = PathfindingCore.MakeImpassable(cell, land);
        Assert.False(PathfindingCore.IsPassable(cell, land));
        Assert.True(PathfindingCore.IsPassable(cell, water));

        // Water tile: impassable for land (deep), passable for ship.
        cell = new NavcellData(0);
        cell = PathfindingCore.MakeImpassable(cell, land);
        Assert.False(PathfindingCore.IsPassable(cell, land));
        Assert.True(PathfindingCore.IsPassable(cell, water));
    }

    [Fact]
    public void WorldToNavcell_FloorsAndInverse()
    {
        // 1 world unit per navcell; navcell N center is at N + 0.5.
        Assert.Equal(0, PathfindingCore.WorldToNavcell(Fixed.FromFloat(0.4f)));
        Assert.Equal(0, PathfindingCore.WorldToNavcell(Fixed.FromFloat(0.9f)));
        Assert.Equal(3, PathfindingCore.WorldToNavcell(Fixed.FromFloat(3.2f)));
        Assert.Equal(Fixed.FromFloat(0.5f), PathfindingCore.NavcellCenterToWorld(0));
        Assert.Equal(Fixed.FromFloat(3.5f), PathfindingCore.NavcellCenterToWorld(3));
    }

    [Fact]
    public void Grid_GetSetRoundtrip()
    {
        var g = new Grid<int>(4, 3);
        g.Set(2, 1, 42);
        Assert.Equal(42, g.Get(2, 1));
        Assert.Equal(0, g.Get(0, 0));   // default
    }

    [Fact]
    public void SparseGrid_UnwrittenReadsAsDefault_OnlySetStored()
    {
        var g = new SparseGrid<string>(100, 100);
        Assert.Null(g.Get(50, 50));     // never written → default
        Assert.False(g.IsSet(50, 50));

        g.Set(50, 50, "x");
        Assert.True(g.IsSet(50, 50));
        Assert.Equal("x", g.Get(50, 50));
        Assert.Null(g.Get(51, 50));     // neighbour still default

        g.Clear();
        Assert.False(g.IsSet(50, 50));
    }

    [Fact]
    public void PathCost_DiagonalMoreExpensiveThanOrthogonal_NoOverflow()
    {
        var orth = new PathCost(1, 0);      // 1 orthogonal step
        var diag = new PathCost(0, 1);      // 1 diagonal step
        Assert.True(diag > orth);           // diagonal costs more (×sqrt2)

        // A long path (45K steps) must not overflow uint (matches the original's claim).
        var longPath = new PathCost(45000, 0);
        Assert.True(longPath.ToInt64() > 0);
    }

    [Fact]
    public void PriorityQueueHeap_PopsInRankOrder()
    {
        var pq = new PriorityQueueHeap(maxId: 100);
        pq.Push(5, 50);
        pq.Push(1, 10);
        pq.Push(3, 30);
        pq.Push(2, 20);

        Assert.Equal(1, pq.Pop());  // lowest rank first
        Assert.Equal(2, pq.Pop());
        Assert.Equal(3, pq.Pop());
        Assert.Equal(5, pq.Pop());
        Assert.True(pq.IsEmpty);
    }

    [Fact]
    public void PriorityQueueHeap_PromoteDecreasesRank()
    {
        var pq = new PriorityQueueHeap(maxId: 100);
        pq.Push(1, 100);
        pq.Push(2, 50);
        pq.Push(3, 75);

        // Item 1 has rank 100; promote it to 10 → should pop first now.
        pq.Promote(1, 10);
        Assert.Equal(1, pq.Pop());
        Assert.Equal(2, pq.Pop());
        Assert.Equal(3, pq.Pop());
    }

    [Fact]
    public void PriorityQueueHeap_DuplicatePushActsAsPromote()
    {
        var pq = new PriorityQueueHeap(maxId: 100);
        pq.Push(1, 50);
        bool inserted = pq.Push(1, 20);  // already present, better rank

        Assert.False(inserted);            // not a new insertion
        Assert.Equal(1, pq.Pop());         // still there, with improved rank
    }

    [Fact]
    public void PathGoal_PointContainsOnlyExactCell()
    {
        var goal = PathGoal.Point(Fixed.FromFloat(3.5f), Fixed.FromFloat(3.5f));
        Assert.True(goal.NavcellContainsGoal(Fixed.FromFloat(3.5f), Fixed.FromFloat(3.5f)));
        Assert.False(goal.NavcellContainsGoal(Fixed.FromFloat(4.5f), Fixed.FromFloat(3.5f)));
    }

    [Fact]
    public void PathGoal_CircleContainsPointsWithinRadius()
    {
        var goal = PathGoal.Circle(Fixed.FromFloat(0f), Fixed.FromFloat(0f), Fixed.FromFloat(5f));
        Assert.True(goal.NavcellContainsGoal(Fixed.FromFloat(0f), Fixed.FromFloat(0f)));
        Assert.True(goal.NavcellContainsGoal(Fixed.FromFloat(4f), Fixed.FromFloat(0f)));
        Assert.False(goal.NavcellContainsGoal(Fixed.FromFloat(6f), Fixed.FromFloat(0f)));
    }

    [Fact]
    public void WaypointPath_ConsumesFromBackInReverseOrder()
    {
        // Push goal first, then intermediates — consumers pop the back (earliest next step).
        var path = new WaypointPath();
        path.Push(new Waypoint(Fixed.FromInt(0), Fixed.FromInt(0)));   // goal (pushed first → last consumed)
        path.Push(new Waypoint(Fixed.FromInt(5), Fixed.FromInt(5)));   // mid

        var first = path.Next()!.Value;
        Assert.Equal(5, first.X.ToIntRoundToZero());
        var second = path.Next()!.Value;
        Assert.Equal(0, second.X.ToIntRoundToZero());
        Assert.Null(path.Next());
    }
}
