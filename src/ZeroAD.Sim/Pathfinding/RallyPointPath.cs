using System;
using System.Collections.Generic;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Pathfinding;

/// <summary>
/// Rally overlay polyline matching <c>CCmpRallyPointRenderer::RecomputeRallyPointPath</c>
/// (render-only: footprint-edge start, long path, snap, linearize, visibility reduce).
/// Open RNS resampling stays in the presentation layer (float spline).
/// </summary>
public static class RallyPointPath
{
    public const string LinePassabilityClass = "default";
    public const int MaxVisibilityLinks = 6;
    public const float MaxHeightDelta = 3f;

    /// <summary>
    /// Ground/water elevation at world (x, z) for the |Δh|&lt;3 visibility gate.
    /// Null skips the height test (flat-world tests).
    /// </summary>
    public delegate float ElevationSample(float worldX, float worldZ);

    /// <summary>
    /// Closest point on the building footprint edge to <paramref name="from"/>.
    /// Mirrors <c>GetClosestsEdgePointFrom</c>.
    /// </summary>
    public static FixedVector2D ClosestEdgePoint(
        PositionComponent pos, FootprintComponent? footprint, FixedVector2D from)
    {
        var center = new FixedVector2D(pos.Position.X, pos.Position.Z);
        if (footprint == null)
            return center;

        if (footprint.Shape == FootprintShape.Circle)
        {
            var dir = from - center;
            if (dir.IsZero)
                return center;
            return center + dir.Normalized().Multiply(footprint.Size0);
        }

        Trig.SinCosApprox(pos.Rotation.Y, out Fixed s, out Fixed c);
        var u = new FixedVector2D(c, -s);
        var v = new FixedVector2D(s, c);
        var half = new FixedVector2D(footprint.Size0 / 2, footprint.Size1 / 2);
        return center + Geometry.NearestPointOnSquare(from - center, u, v, half);
    }

    /// <summary>
    /// One overlay segment in C++ <c>m_Path[index]</c> order (goal-first when a long
    /// path exists; start→goal when the pathfinder returns fewer than two waypoints).
    /// Does not mutate cached <see cref="WaypointPath"/> instances.
    /// </summary>
    public static List<FixedVector2D> ComputeSegment(
        PathfinderComponent pathfinder,
        PositionComponent building,
        FootprintComponent? footprint,
        IReadOnlyList<FixedVector2D> rallyPoints,
        int index,
        ElevationSample? elevation = null)
    {
        var goal = rallyPoints[index];
        FixedVector2D start;
        if (index == 0)
            start = ClosestEdgePoint(building, footprint, goal);
        else
            start = rallyPoints[index - 1];

        var path = pathfinder.ComputePath(start, PathGoal.Point(goal.X, goal.Y),
            pathfinder.GetPassabilityClassMask(LinePassabilityClass));

        if (path.Waypoints.Count < 2)
        {
            return new List<FixedVector2D> { start, goal };
        }

        var coords = new List<FixedVector2D>(path.Waypoints.Count);
        for (int i = 0; i < path.Waypoints.Count; i++)
            coords.Add(new FixedVector2D(path.Waypoints[i].X, path.Waypoints[i].Z));

        // C++ LongPathfinder reconstructs goal→start (index 0 = goal, back ≈ start).
        // Our JPS Reconstruct emits start→goal; reverse so snap/linearize match C++.
        EnsureGoalFirst(coords, goal);

        if (index == 0)
        {
            var nearBuilding = coords[coords.Count - 2];
            var snapped = ClosestEdgePoint(building, footprint, nearBuilding);
            coords[coords.Count - 1] = snapped;
        }
        else
        {
            coords[coords.Count - 1] = rallyPoints[index - 1];
        }

        coords[0] = goal;

        for (int i = coords.Count - 2; i > 0; --i)
            coords[i] = (coords[i] + coords[i - 1]) / 2;

        ReduceSegmentsByVisibility(coords, pathfinder, MaxVisibilityLinks, elevation);
        return coords;
    }

    /// <summary>
    /// Concatenate every rally-flag segment in travel order (building/previous flag → next flag)
    /// so a single ribbon can be drawn. C++ draws each <c>m_Path[i]</c> as its own overlay;
    /// joining after reversing goal-first segments yields the same geometry without a fold.
    /// </summary>
    public static List<FixedVector2D> ComputeTravelPolyline(
        PathfinderComponent pathfinder,
        PositionComponent building,
        FootprintComponent? footprint,
        IReadOnlyList<FixedVector2D> rallyPoints,
        ElevationSample? elevation = null)
    {
        var result = new List<FixedVector2D>();
        if (rallyPoints.Count == 0)
            return result;

        for (int i = 0; i < rallyPoints.Count; i++)
        {
            var seg = ComputeSegment(pathfinder, building, footprint, rallyPoints, i, elevation);
            ToTravelOrder(seg, SegmentStart(building, footprint, rallyPoints, i), rallyPoints[i]);
            AppendJoining(result, seg);
        }

        return result;
    }

    internal static void ReduceSegmentsByVisibility(
        List<FixedVector2D> coords,
        PathfinderComponent pathfinder,
        int maxSegmentLinks,
        ElevationSample? elevation)
    {
        if (coords.Count < 3)
            return;

        var pass = pathfinder.GetPassabilityClassMask(LinePassabilityClass);
        var newCoords = new List<FixedVector2D>();
        newCoords.Add(coords[0]);

        int baseNodeIdx = 0;
        int curNodeIdx = 1;

        while (curNodeIdx < coords.Count)
        {
            bool curNodeVisible = pathfinder.CheckMovement(coords[baseNodeIdx], coords[curNodeIdx], pass);

            if (elevation != null)
            {
                float baseY = elevation(coords[baseNodeIdx].X.ToFloat(), coords[baseNodeIdx].Y.ToFloat());
                float curY = elevation(coords[curNodeIdx].X.ToFloat(), coords[curNodeIdx].Y.ToFloat());
                curNodeVisible = curNodeVisible && MathF.Abs(curY - baseY) < MaxHeightDelta;
            }

            if (maxSegmentLinks > 0)
                curNodeVisible = curNodeVisible && (curNodeIdx - baseNodeIdx) <= maxSegmentLinks;

            if (!curNodeVisible)
            {
                if (curNodeIdx > baseNodeIdx + 1)
                    baseNodeIdx = curNodeIdx - 1;
                else
                {
                    baseNodeIdx = curNodeIdx;
                    ++curNodeIdx;
                }

                newCoords.Add(coords[baseNodeIdx]);
            }

            ++curNodeIdx;
        }

        newCoords.Add(coords[coords.Count - 1]);
        coords.Clear();
        coords.AddRange(newCoords);
    }

    private static FixedVector2D SegmentStart(
        PositionComponent building,
        FootprintComponent? footprint,
        IReadOnlyList<FixedVector2D> rallyPoints,
        int index)
    {
        if (index == 0)
            return ClosestEdgePoint(building, footprint, rallyPoints[0]);
        return rallyPoints[index - 1];
    }

    private static void EnsureGoalFirst(List<FixedVector2D> coords, FixedVector2D goal)
    {
        if (coords.Count < 2)
            return;
        if ((coords[0] - goal).CompareLength(coords[coords.Count - 1] - goal) > 0)
            coords.Reverse();
    }

    /// <summary>
    /// Long paths are goal-first; the empty-path fallback is already start→goal.
    /// Reverse so a ribbon can run building → rally without doubling back.
    /// </summary>
    public static void ToTravelOrder(List<FixedVector2D> pts, FixedVector2D start, FixedVector2D goal)
    {
        if (pts.Count < 2)
            return;
        int d0s = (pts[0] - start).CompareLength(pts[0] - goal);
        if (d0s > 0)
            pts.Reverse();
    }

    private static void AppendJoining(List<FixedVector2D> dest, List<FixedVector2D> seg)
    {
        int i0 = 0;
        if (dest.Count > 0 && seg.Count > 0 && dest[dest.Count - 1] == seg[0])
            i0 = 1;
        for (int i = i0; i < seg.Count; i++)
            dest.Add(seg[i]);
    }
}
