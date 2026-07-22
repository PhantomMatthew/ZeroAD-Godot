using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;
using Xunit;

namespace ZeroAD.Sim.Tests.Pathfinding;

// Tests for the VertexPathfinder (visibility-graph A*). Verifies straight-line routing when
// clear, and corner-routing around an obstruction.
public sealed class VertexPathfinderTests
{
    private static ObstructionSquare Box(Fixed cx, Fixed cz, Fixed hw, Fixed hh) => new(
        cx, cz,
        new FixedVector2D(Fixed.FromInt(1), Fixed.Zero),
        new FixedVector2D(Fixed.Zero, Fixed.FromInt(1)),
        hw, hh);

    [Fact]
    public void NoObstruction_BeeLinesStraightToGoal()
    {
        var pf = new VertexPathfinder();
        var start = new FixedVector2D(Fixed.Zero, Fixed.Zero);
        var goal = PathGoal.Point(Fixed.FromInt(10), Fixed.FromInt(10));

        var path = pf.ComputeShortPath(start, goal,
            clearance: Fixed.FromFraction(1, 2),
            range: Fixed.FromInt(20),
            obstructions: System.Array.Empty<ObstructionSquare>());

        // Single waypoint: the goal.
        Assert.Single(path.Waypoints);
        var wp = path.Peek();
        Assert.Equal(10, wp.X.ToIntRoundToZero());
        Assert.Equal(10, wp.Z.ToIntRoundToZero());
    }

    [Fact]
    public void ObstructionDirectlyAhead_RoutesAroundItsCorner()
    {
        // A box blocking the straight line from (0,0) to (10,0); the path must detour around
        // one of its corners rather than pass through it.
        var pf = new VertexPathfinder();
        var start = new FixedVector2D(Fixed.Zero, Fixed.Zero);
        var goal = PathGoal.Point(Fixed.FromInt(10), Fixed.Zero);
        // Box centred at (5,0), half-extents 2 → spans x[3,7], z[-2,2], right on the path.
        var obstruction = Box(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(2), Fixed.FromInt(2));

        var path = pf.ComputeShortPath(start, goal,
            clearance: Fixed.FromFraction(1, 2),
            range: Fixed.FromInt(20),
            new[] { obstruction });

        Assert.False(path.IsEmpty);
        // The path must NOT contain a waypoint inside the box's interior (x∈(3,7), z∈(-2,2)).
        foreach (var wp in path.Waypoints)
        {
            bool insideBox = wp.X > Fixed.FromInt(3) && wp.X < Fixed.FromInt(7)
                          && wp.Z > Fixed.FromInt(-2) && wp.Z < Fixed.FromInt(2);
            Assert.False(insideBox, $"waypoint ({wp.X.ToFloat()},{wp.Z.ToFloat()}) inside the obstruction");
        }
        // And it must reach the goal.
        var last = path.Waypoints[0];   // goal is pushed last → index 0 after Push order
        // Waypoints stored start→goal; goal is last in the list.
        var goalWp = path.Waypoints[path.Waypoints.Count - 1];
        Assert.Equal(10, goalWp.X.ToIntRoundToZero());
    }

    [Fact]
    public void GoalAlreadyReachableStraight_NoDetour()
    {
        // Obstruction off to the side, not blocking the path → straight beeline.
        var pf = new VertexPathfinder();
        var start = new FixedVector2D(Fixed.Zero, Fixed.Zero);
        var goal = PathGoal.Point(Fixed.FromInt(10), Fixed.Zero);
        var obstruction = Box(Fixed.FromInt(5), Fixed.FromInt(20), Fixed.FromInt(2), Fixed.FromInt(2));

        var path = pf.ComputeShortPath(start, goal,
            clearance: Fixed.FromFraction(1, 2),
            range: Fixed.FromInt(30),
            new[] { obstruction });

        // Straight beeline → single waypoint (the goal).
        Assert.Single(path.Waypoints);
    }

    [Fact]
    public void NarrowGapBetweenTwoObstructions_PassesThrough()
    {
        // Two boxes with a gap between them; start and goal on opposite sides, aligned with gap.
        var pf = new VertexPathfinder();
        var start = new FixedVector2D(Fixed.Zero, Fixed.Zero);
        var goal = PathGoal.Point(Fixed.FromInt(10), Fixed.Zero);
        // Box A above the line, Box B below, leaving a gap at z≈0.
        var a = Box(Fixed.FromInt(5), Fixed.FromInt(4), Fixed.FromInt(2), Fixed.FromInt(2));  // z∈[2,6]
        var b = Box(Fixed.FromInt(5), Fixed.FromInt(-4), Fixed.FromInt(2), Fixed.FromInt(2)); // z∈[-6,-2]

        var path = pf.ComputeShortPath(start, goal,
            clearance: Fixed.FromFraction(1, 2),
            range: Fixed.FromInt(20),
            new[] { a, b });

        Assert.False(path.IsEmpty);
        // The path should reach the goal.
        var goalWp = path.Waypoints[path.Waypoints.Count - 1];
        Assert.Equal(10, goalWp.X.ToIntRoundToZero());
    }
}
