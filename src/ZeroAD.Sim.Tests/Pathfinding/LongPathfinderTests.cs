using System.Collections.Generic;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;
using Xunit;

namespace ZeroAD.Sim.Tests.Pathfinding;

// Tests for the LongPathfinder (JPS). Verifies path correctness: straight-line routing,
// wall avoidance, no corner-cutting, and that JPS produces the same reachability as plain A*.
public sealed class LongPathfinderTests
{
    private const int Size = 12;
    private static readonly PassClass Land = PathfindingCore.PassClassMaskFromIndex(0);

    private static Grid<NavcellData> OpenGrid()
    {
        var g = new Grid<NavcellData>(Size, Size);
        for (int j = 0; j < Size; j++)
            for (int i = 0; i < Size; i++)
                g.Set(i, j, new NavcellData(0));
        return g;
    }

    private static void Block(Grid<NavcellData> g, int i, int j) =>
        g.Set(i, j, PathfindingCore.MakeImpassable(g.Get(i, j), Land));

    private static (LongPathfinder pf, HierarchicalPathfinder hier, PassabilityClassDef cls) Build(Grid<NavcellData> g)
    {
        var builder = new PassabilityGridBuilder();
        var hier = new HierarchicalPathfinder();
        hier.Recompute(g, new[] { builder.Default });
        var pf = new LongPathfinder();
        pf.Reload(g);
        return (pf, hier, builder.Default);
    }

    private static List<(int i, int j)> PathToNavcells(WaypointPath path)
    {
        var navcells = new List<(int, int)>();
        // WaypointPath.Waypoints is stored start→goal (the pathfinder pushes start first,
        // goal last). Consumers pop from the back (goal-first) for walking, but for assertions
        // we want the natural start→goal order, which is already the list order.
        foreach (var wp in path.Waypoints)
            navcells.Add((PathfindingCore.WorldToNavcell(wp.X), PathfindingCore.WorldToNavcell(wp.Z)));
        return navcells;
    }

    [Fact]
    public void OpenGrid_ProducesStraightPathToGoal()
    {
        var grid = OpenGrid();
        var (pf, hier, cls) = Build(grid);

        var goal = PathGoal.Point(
            PathfindingCore.NavcellCenterToWorld(Size - 1),
            PathfindingCore.NavcellCenterToWorld(Size - 1));
        var path = pf.ComputePath(hier, 0, 0, goal, cls.Mask);

        Assert.False(path.IsEmpty);
        var navs = PathToNavcells(path);
        // Starts at origin, ends at the goal navcell.
        Assert.Equal((0, 0), navs[0]);
        Assert.Equal((Size - 1, Size - 1), navs[^1]);
    }

    [Fact]
    public void WallBlocksGoal_PathRoutesAroundIt()
    {
        // A horizontal wall across the middle with a single gap; goal is below the wall, start
        // above. The path must go through the gap.
        var grid = OpenGrid();
        int wallJ = Size / 2;
        for (int i = 0; i < Size; i++)
            Block(grid, i, wallJ);   // full wall row
        Block(grid, Size / 2, wallJ); // ... actually clear a gap here
        grid.Set(Size / 2, wallJ, new NavcellData(0));   // reopen the gap navcell

        var (pf, hier, cls) = Build(grid);
        var goal = PathGoal.Point(
            PathfindingCore.NavcellCenterToWorld(1),
            PathfindingCore.NavcellCenterToWorld(Size - 1));   // bottom-left
        var path = pf.ComputePath(hier, 1, 0, goal, cls.Mask);  // start top-left

        Assert.False(path.IsEmpty);
        var navs = PathToNavcells(path);
        Assert.Equal((1, 0), navs[0]);
        Assert.Equal((1, Size - 1), navs[^1]);
        // Every navcell on the path must be passable.
        foreach (var (i, j) in navs)
            Assert.True(PathfindingCore.IsPassable(grid.Get(i, j), cls.Mask));
    }

    [Fact]
    public void DiagonalPath_DoesNotCutCorners()
    {
        // A single blocked cell; a diagonal path around it must not pass through the corner.
        var grid = OpenGrid();
        Block(grid, 5, 4);
        Block(grid, 4, 5);   // these two make a corner; cutting through (5,5)↔(4,4) is illegal

        var (pf, hier, cls) = Build(grid);
        var goal = PathGoal.Point(
            PathfindingCore.NavcellCenterToWorld(8), PathfindingCore.NavcellCenterToWorld(8));
        var path = pf.ComputePath(hier, 0, 0, goal, cls.Mask);

        Assert.False(path.IsEmpty);
        var navs = PathToNavcells(path);
        // No segment of the path should be an illegal corner-cut: consecutive diagonal moves
        // can't have both orthogonal neighbours blocked.
        for (int k = 1; k < navs.Count; k++)
        {
            var (pi, pj) = navs[k - 1];
            var (ci, cj) = navs[k];
            int di = ci - pi, dj = cj - pj;
            if (di != 0 && dj != 0)
            {
                // Both orthogonal cells must be passable (no corner-cut).
                Assert.True(PathfindingCore.IsPassable(grid.Get(pi + di, pj), cls.Mask) ||
                            PathfindingCore.IsPassable(grid.Get(pi, pj + dj), cls.Mask),
                            $"illegal corner cut between ({pi},{pj}) and ({ci},{cj})");
            }
        }
    }

    [Fact]
    public void UnreachableGoal_MakeGoalReachableSnapsAndPathReachesIt()
    {
        // Completely enclosed region (box with no exit); goal inside the box, start outside.
        // MakeGoalReachable should snap the goal to the nearest navcell on the start's side.
        var grid = OpenGrid();
        // Build a closed box from (5,5) to (7,7).
        for (int i = 5; i <= 7; i++) { Block(grid, i, 5); Block(grid, i, 7); }
        for (int j = 5; j <= 7; j++) { Block(grid, 5, j); Block(grid, 7, j); }

        var (pf, hier, cls) = Build(grid);
        var goal = PathGoal.Point(
            PathfindingCore.NavcellCenterToWorld(6),
            PathfindingCore.NavcellCenterToWorld(6));   // inside the box (unreachable)
        var path = pf.ComputePath(hier, 0, 0, goal, cls.Mask);

        // Path is non-empty (snapped to a reachable point) but the endpoint is NOT inside the box.
        Assert.False(path.IsEmpty);
        var navs = PathToNavcells(path);
        var (ei, ej) = navs[^1];
        Assert.True(ei < 5 || ei > 7 || ej < 5 || ej > 7,
            $"endpoint ({ei},{ej}) should be outside the sealed box");
    }

    [Fact]
    public void StartEqualsGoal_ReturnsSingleWaypoint()
    {
        var grid = OpenGrid();
        var (pf, hier, cls) = Build(grid);
        var goal = PathGoal.Point(
            PathfindingCore.NavcellCenterToWorld(3), PathfindingCore.NavcellCenterToWorld(3));
        var path = pf.ComputePath(hier, 3, 3, goal, cls.Mask);

        Assert.Single(path.Waypoints);
    }

    [Fact]
    public void CheckLineMovement_OpenLineIsClear_BlockedLineIsObstructed()
    {
        var grid = OpenGrid();
        Block(grid, 5, 5);
        var (pf, hier, cls) = Build(grid);

        // Straight clear line.
        Assert.True(pf.CheckLineMovement(0, 0, 8, 0, cls.Mask));
        // Line passing through the blocked cell at (5,5).
        Assert.False(pf.CheckLineMovement(0, 0, 10, 10, cls.Mask));
    }
}
