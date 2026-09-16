using System.Collections.Generic;
using Godot;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;

namespace ZeroAD.Godot;

/// <summary>
/// Presentation half of <c>CCmpRallyPointRenderer</c>: open-path RNS (closed territorial
/// interpolator must not wrap last→first) and terrain-sampled ribbon points.
/// </summary>
public static class RallyPointLineBuilder
{
    public const int SegmentSamples = 4;
    public const float OverlayLift = 0.15f;

    /// <summary>
    /// Building → last rally flag, height-sampled. Matches C++ overlay geometry
    /// (per-segment path + open RNS); one ribbon instead of N overlay meshes.
    /// </summary>
    public static List<Vector3> Build(
        PathfinderComponent pathfinder,
        PositionComponent building,
        FootprintComponent? footprint,
        IReadOnlyList<FixedVector2D> rallyPoints,
        RallyPointPath.ElevationSample elevation)
    {
        var pts3 = new List<Vector3>();
        if (rallyPoints.Count == 0)
            return pts3;

        for (int i = 0; i < rallyPoints.Count; i++)
        {
            var seg = RallyPointPath.ComputeSegment(
                pathfinder, building, footprint, rallyPoints, i, elevation);
            var xz = InterpolateOpenRNS(seg, 0f, SegmentSamples);
            ReverseIfGoalFirst(xz, SegmentStart(building, footprint, rallyPoints, i), rallyPoints[i]);
            foreach (var p in xz)
            {
                if (pts3.Count > 0)
                {
                    var last = pts3[pts3.Count - 1];
                    if (Mathf.IsEqualApprox(last.X, p.X) && Mathf.IsEqualApprox(last.Z, p.Y))
                        continue;
                }
                float y = elevation(p.X, p.Y) + OverlayLift;
                pts3.Add(new Vector3(p.X, y, p.Y));
            }
        }

        return pts3;
    }

    /// <summary>
    /// Open-path <c>SimRender::InterpolatePointsRNS(closed=false, offset, samples)</c>.
    /// Artificial p0/p3 extensions; last sample is EvaluateSpline(1). Not the closed-ring
    /// interpolator used for territory borders.
    /// </summary>
    public static List<Vector2> InterpolateOpenRNS(
        IReadOnlyList<FixedVector2D> control, float offset = 0f, int segmentSamples = SegmentSamples)
    {
        int n = control.Count;
        var result = new List<Vector2>();
        if (n < 2 || segmentSamples <= 0)
        {
            for (int i = 0; i < n; i++)
                result.Add(new Vector2(control[i].X.ToFloat(), control[i].Y.ToFloat()));
            return result;
        }

        var points = new Vector2[n];
        for (int i = 0; i < n; i++)
            points[i] = new Vector2(control[i].X.ToFloat(), control[i].Y.ToFloat());

        Vector2 a0 = default, a1 = default, a2 = default, a3 = default;
        int imax = n - 1;
        for (int i = 0; i < imax; i++)
        {
            Vector2 p1 = points[i];
            Vector2 p2 = points[i + 1];
            Vector2 p0 = i == 0
                ? points[0] + (points[0] - points[1])
                : points[i - 1];
            Vector2 p3 = i == n - 2
                ? points[n - 1] + (points[n - 1] - points[n - 2])
                : points[i + 2];

            float l1 = (p2 - p1).Length();
            Vector2 s0 = Norm(p1 - p0);
            Vector2 s1 = Norm(p2 - p1);
            Vector2 s2 = Norm(p3 - p2);
            Vector2 v1 = Norm(s0 + s1) * l1;
            Vector2 v2 = Norm(s1 + s2) * l1;
            a0 = p1 * 2 + p2 * -2 + v1 + v2;
            a1 = p1 * -3 + p2 * 3 + v1 * -2 + v2 * -1;
            a2 = v1;
            a3 = p1;

            for (int sample = 0; sample < segmentSamples; sample++)
                result.Add(EvaluateSpline(sample / (float)segmentSamples, a0, a1, a2, a3, offset));
        }

        result.Add(EvaluateSpline(1f, a0, a1, a2, a3, offset));
        return result;
    }

    private static FixedVector2D SegmentStart(
        PositionComponent building,
        FootprintComponent? footprint,
        IReadOnlyList<FixedVector2D> rallyPoints,
        int index)
    {
        if (index == 0)
            return RallyPointPath.ClosestEdgePoint(building, footprint, rallyPoints[0]);
        return rallyPoints[index - 1];
    }

    private static void ReverseIfGoalFirst(List<Vector2> xz, FixedVector2D start, FixedVector2D goal)
    {
        if (xz.Count < 2)
            return;
        var s = new Vector2(start.X.ToFloat(), start.Y.ToFloat());
        var g = new Vector2(goal.X.ToFloat(), goal.Y.ToFloat());
        if (xz[0].DistanceSquaredTo(g) < xz[0].DistanceSquaredTo(s))
            xz.Reverse();
    }

    private static Vector2 Norm(Vector2 v) =>
        v.LengthSquared() > 1e-12f ? v.Normalized() : Vector2.Zero;

    private static Vector2 EvaluateSpline(
        float t, Vector2 a0, Vector2 a1, Vector2 a2, Vector2 a3, float offset)
    {
        var p = a0 * (t * t * t) + a1 * (t * t) + a2 * t + a3;
        if (offset == 0f)
            return p;
        var dp = Norm(a0 * (3 * t * t) + a1 * (2 * t) + a2);
        return p + new Vector2(dp.Y * -offset, dp.X * offset);
    }
}
