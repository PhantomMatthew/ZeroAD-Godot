using System.Collections.Generic;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;
using Xunit;

namespace ZeroAD.Sim.Tests.Pathfinding;

// Tests for the hierarchical pathfinder's connectivity model: chunk/region/global-region
// flood-fill, cross-border edges, and MakeGoalReachable snapping.
public sealed class HierarchicalPathfinderTests
{
    private const int NavSize = 20;   // small grid (< 1 chunk of 96) so all regions are local.

    // Build a grid where all navcells are passable for the default class.
    private static Grid<NavcellData> OpenGrid()
    {
        var grid = new Grid<NavcellData>(NavSize, NavSize);
        for (int j = 0; j < NavSize; j++)
            for (int i = 0; i < NavSize; i++)
                grid.Set(i, j, new NavcellData(0));
        return grid;
    }

    private static (HierarchicalPathfinder hier, PassabilityClassDef cls) Build(params Grid<NavcellData>[] gridHolder)
    {
        var grid = gridHolder.Length > 0 ? gridHolder[0] : OpenGrid();
        var builder = new PassabilityGridBuilder();
        var cls = builder.Default;
        var hier = new HierarchicalPathfinder();
        hier.Recompute(grid, new[] { cls });
        return (hier, cls);
    }

    [Fact]
    public void OpenGrid_AllNavcellsSameGlobalRegion()
    {
        var (hier, cls) = Build();
        uint g00 = hier.GetGlobalRegion(0, 0, cls.Mask);
        uint gNN = hier.GetGlobalRegion(NavSize - 1, NavSize - 1, cls.Mask);
        Assert.NotEqual(0u, g00);
        Assert.Equal(g00, gNN);   // all mutually reachable
    }

    [Fact]
    public void WallSplittingGrid_CreatesSeparateGlobalRegions()
    {
        // Build a grid with a full impassable wall down the middle (column NavSize/2), so the
        // left and right halves can't connect → different global regions.
        var grid = OpenGrid();
        int wallX = NavSize / 2;
        for (int j = 0; j < NavSize; j++)
            grid.Set(wallX, j, PathfindingCore.MakeImpassable(grid.Get(wallX, j), PathfindingCore.PassClassMaskFromIndex(0)));

        var (hier, cls) = Build(grid);
        uint left = hier.GetGlobalRegion(0, 0, cls.Mask);
        uint right = hier.GetGlobalRegion(NavSize - 1, NavSize - 1, cls.Mask);
        Assert.NotEqual(0u, left);
        Assert.NotEqual(0u, right);
        Assert.NotEqual(left, right);   // wall separates them
    }

    [Fact]
    public void WallWithGap_KeepsBothHalvesConnected()
    {
        // Same wall but leave a one-navcell gap → still connected (same global region).
        var grid = OpenGrid();
        int wallX = NavSize / 2;
        for (int j = 0; j < NavSize; j++)
        {
            if (j == NavSize / 2) continue;   // gap
            grid.Set(wallX, j, PathfindingCore.MakeImpassable(grid.Get(wallX, j), PathfindingCore.PassClassMaskFromIndex(0)));
        }

        var (hier, cls) = Build(grid);
        uint left = hier.GetGlobalRegion(0, 0, cls.Mask);
        uint right = hier.GetGlobalRegion(NavSize - 1, NavSize - 1, cls.Mask);
        Assert.Equal(left, right);   // gap keeps them connected
    }

    [Fact]
    public void ImpassableNavcell_HasRegionZero()
    {
        var grid = OpenGrid();
        grid.Set(5, 5, PathfindingCore.MakeImpassable(grid.Get(5, 5), PathfindingCore.PassClassMaskFromIndex(0)));
        var (hier, cls) = Build(grid);
        Assert.False(hier.Get(5, 5, cls.Mask).IsValid);
        Assert.Equal(0u, hier.GetGlobalRegion(5, 5, cls.Mask));
    }

    [Fact]
    public void MakeGoalReachable_ReachableGoal_LeftUnchanged()
    {
        var grid = OpenGrid();
        var (hier, cls) = Build(grid);
        var goal = PathGoal.Point(Fixed.FromFloat(5.5f), Fixed.FromFloat(5.5f));
        bool reachable = hier.MakeGoalReachable(startX: 0, startZ: 0, ref goal, cls.Mask);
        Assert.True(reachable);   // open grid → everything reachable
        // Goal unchanged: still the original point.
        Assert.Equal(Fixed.FromFloat(5.5f), goal.X);
    }

    [Fact]
    public void MakeGoalReachable_UnreachableGoal_SnapsToNearestReachable()
    {
        // Wall down the middle; goal is on the far (unreachable) side. MakeGoalReachable should
        // snap it to the nearest navcell on the start's side.
        var grid = OpenGrid();
        int wallX = NavSize / 2;
        for (int j = 0; j < NavSize; j++)
            grid.Set(wallX, j, PathfindingCore.MakeImpassable(grid.Get(wallX, j), PathfindingCore.PassClassMaskFromIndex(0)));

        var (hier, cls) = Build(grid);
        // Goal on the right side (start is on the left).
        var goal = PathGoal.Point(Fixed.FromFloat(NavSize - 0.5f), Fixed.FromFloat(NavSize - 0.5f));
        bool reachable = hier.MakeGoalReachable(startX: 0, startZ: 0, ref goal, cls.Mask);

        Assert.False(reachable);   // was unreachable, got snapped
        // Snapped goal must now be on the start's side (x < wallX) and reachable.
        int snappedNavX = PathfindingCore.WorldToNavcell(goal.X);
        Assert.True(snappedNavX < wallX, $"snapped goal {snappedNavX} should be on left side of wall at {wallX}");
        // The snapped goal is now reachable from the start.
        Assert.True(hier.MakeGoalReachable(0, 0, ref goal, cls.Mask));
    }

    [Fact]
    public void FindNearestPassableNavcell_SnapsImpassableStart()
    {
        var grid = OpenGrid();
        grid.Set(5, 5, PathfindingCore.MakeImpassable(grid.Get(5, 5), PathfindingCore.PassClassMaskFromIndex(0)));
        var (hier, cls) = Build(grid);
        var nearest = hier.FindNearestPassableNavcell(5, 5, cls.Mask);
        Assert.True(nearest.HasValue);
        // Nearest passable is an adjacent cell (4 or 8-connected ring 1).
        Assert.True(hier.Get(nearest!.Value.x, nearest.Value.z, cls.Mask).IsValid);
    }
}
