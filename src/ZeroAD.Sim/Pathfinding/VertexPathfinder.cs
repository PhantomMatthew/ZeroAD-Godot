using System.Collections.Generic;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Pathfinding;

// VertexPathfinder — short-range local avoidance via a visibility graph. Ported from
// source/simulation2/helpers/VertexPathfinder.h/.cpp.
//
// Algorithm: "points of visibility". Collect the corners of obstruction shapes within a
// bounded search box, build a graph where two vertices connect iff the straight segment
// between them clears every obstruction edge, then A* from start to goal over that graph.
// This is what lets a unit route precisely around a building's corner rather than through it.
//
// Scope (P0): a correct, simpler variant than the original's quadrant-pruning + AA-edge
// bucketing. It collects axis-aligned obstruction corners (treating buildings as AABBs,
// matching the LongPathfinder's grid approximation), builds the full visibility graph, and
// A*-searches. Sufficient for short-range detours; the original's optimizations are P1.

public sealed class VertexPathfinder
{
    // The amount each obstruction is expanded by clearance, plus a small delta so units don't
    // graze walls. Matches EDGE_EXPAND_DELTA (1/16) in the original.
    private static readonly Fixed EdgeExpandDelta = Fixed.FromFraction(1, 16);

    /// <summary>Compute a short path from start to goal, routing around obstructions within
    /// <paramref name="range"/> world units of the start.</summary>
    /// <param name="obstructions">Static obstruction squares in the area (caller pre-filters by range).</param>
    public WaypointPath ComputeShortPath(
        FixedVector2D start, in PathGoal goal, Fixed clearance, Fixed range,
        IEnumerable<ObstructionSquare> obstructions)
    {
        var path = new WaypointPath();
        var (goalX, goalZ) = goal.NearestPoint(start.X, start.Y);

        // If the straight line start→goal is unobstructed, take it directly.
        var expanded = ExpandObstructions(obstructions, clearance);
        if (expanded.Count == 0 || IsVisible(start.X, start.Y, goalX, goalZ, expanded))
        {
            path.Push(new Waypoint(goalX, goalZ));
            return path;
        }

        // Collect visibility vertices: start, goal, and every obstruction corner.
        var verts = new List<(Fixed X, Fixed Z)>();
        verts.Add((start.X, start.Y));
        verts.Add((goalX, goalZ));
        var edges = new List<(Fixed X0, Fixed Z0, Fixed X1, Fixed Z1)>();
        foreach (var ob in expanded)
        {
            var (x0, z0, x1, z1) = ob;
            // Four corner vertices.
            verts.Add((x0, z0));
            verts.Add((x1, z0));
            verts.Add((x0, z1));
            verts.Add((x1, z1));
            // Four blocking edges (the box outline).
            edges.Add((x0, z0, x1, z0));
            edges.Add((x1, z0, x1, z1));
            edges.Add((x1, z1, x0, z1));
            edges.Add((x0, z1, x0, z0));
        }

        int n = verts.Count;
        int startIdx = 0, goalIdx = 1;

        // A* over the visibility graph.
        var gScore = new long[n];
        var cameFrom = new int[n];
        var closed = new bool[n];
        for (int i = 0; i < n; i++) { gScore[i] = long.MaxValue; cameFrom[i] = -1; }
        gScore[startIdx] = 0;

        var pq = new PriorityQueueHeap(n);
        pq.Push(startIdx, Heuristic(verts[startIdx], verts[goalIdx]));

        while (!pq.IsEmpty)
        {
            int cur = pq.Pop();
            if (closed[cur]) continue;
            closed[cur] = true;
            if (cur == goalIdx) break;

            for (int nb = 0; nb < n; nb++)
            {
                if (nb == cur || closed[nb]) continue;
                if (!IsVisible(verts[cur].X, verts[cur].Z, verts[nb].X, verts[nb].Z, expanded))
                    continue;
                long stepDist = Heuristic(verts[cur], verts[nb]);
                long tentative = gScore[cur] + stepDist;
                if (tentative < gScore[nb])
                {
                    gScore[nb] = tentative;
                    cameFrom[nb] = cur;
                    pq.Push(nb, tentative + Heuristic(verts[nb], verts[goalIdx]));
                }
            }
        }

        // Reconstruct goal→start, then reverse into the path (start→goal), pushing goal-first.
        if (cameFrom[goalIdx] == -1 && goalIdx != startIdx)
        {
            // No path found through the visibility graph — fall back to a straight beeline.
            path.Push(new Waypoint(goalX, goalZ));
            return path;
        }

        var seq = new List<int>();
        int node = goalIdx;
        while (node != -1) { seq.Add(node); node = cameFrom[node]; }
        seq.Reverse();   // start → goal
        for (int k = 1; k < seq.Count; k++)   // skip start vertex itself
            path.Push(new Waypoint(verts[seq[k]].X, verts[seq[k]].Z));
        return path;
    }

    // Expand each obstruction to its AABB (x0,z0,x1,z1) grown by clearance + delta.
    private static List<(Fixed X0, Fixed Z0, Fixed X1, Fixed Z1)> ExpandObstructions(
        IEnumerable<ObstructionSquare> obstructions, Fixed clearance)
    {
        var list = new List<(Fixed, Fixed, Fixed, Fixed)>();
        Fixed grow = clearance + EdgeExpandDelta;
        foreach (var ob in obstructions)
        {
            // AABB half-extents of the oriented box.
            var bb = Geometry.GetHalfBoundingBox(ob.U, ob.V, new FixedVector2D(ob.Hw, ob.Hh));
            list.Add((ob.X - bb.X - grow, ob.Z - bb.Y - grow,
                      ob.X + bb.X + grow, ob.Z + bb.Y + grow));
        }
        return list;
    }

    // Is the segment from (x0,z0) to (x1,z1) clear of all obstruction boxes? A vertex exactly
    // on an obstruction corner is allowed (that's a graph node); the segment must not pass
    // through the interior of any box.
    private static bool IsVisible(Fixed x0, Fixed z0, Fixed x1, Fixed z1,
        List<(Fixed X0, Fixed Z0, Fixed X1, Fixed Z1)> boxes)
    {
        foreach (var b in boxes)
        {
            if (SegmentIntersectsBox(x0, z0, x1, z1, b))
                return false;
        }
        return true;
    }

    // Liang-Barsky clipped-line test: does the segment intersect the box's interior?
    private static bool SegmentIntersectsBox(Fixed x0, Fixed z0, Fixed x1, Fixed z1,
        (Fixed X0, Fixed Z0, Fixed X1, Fixed Z1) b)
    {
        Fixed dx = x1 - x0;
        Fixed dz = z1 - z0;
        Fixed tMin = Fixed.Zero;
        Fixed tMax = Fixed.FromInt(1);

        // Clip against the four box edges (slab method).
        if (!ClipAxis(x0.InternalValue, dx.InternalValue, b.X0.InternalValue, b.X1.InternalValue, ref tMin, ref tMax)) return false;
        if (!ClipAxis(z0.InternalValue, dz.InternalValue, b.Z0.InternalValue, b.Z1.InternalValue, ref tMin, ref tMax)) return false;

        // If the segment passes through the box interior (tMin < tMax and the overlap is real),
        // it's blocked. But a segment that merely touches a corner (tMin==tMax) is allowed —
        // that's a visibility-graph vertex. Require a non-degenerate overlap.
        return tMin < tMax && tMax > Fixed.Zero && tMin < Fixed.FromInt(1);
    }

    // Clip one axis of a segment against [boxMin, boxMax]. Returns false if the segment is
    // entirely outside this slab. Updates tMin/tMax (as Fixed, working on raw internal ints).
    private static bool ClipAxis(long p, long d, long boxMin, long boxMax,
        ref Fixed tMin, ref Fixed tMax)
    {
        if (d == 0)
        {
            // Parallel to this slab: must be inside the slab.
            return p >= boxMin && p <= boxMax;
        }
        long scale = 1 << 16;   // Fixed is 16.16; work in raw ints for the division.
        // t = (boundary - p) / d, in fixed-point units.
        long t1 = ((boxMin - p) * scale) / d;
        long t2 = ((boxMax - p) * scale) / d;
        if (t1 > t2) { long tmp = t1; t1 = t2; t2 = tmp; }
        Fixed ft1 = Fixed.Zero.WithInternalValue((int)t1);
        Fixed ft2 = Fixed.Zero.WithInternalValue((int)t2);
        if (ft1 > tMin) tMin = ft1;
        if (ft2 < tMax) tMax = ft2;
        return tMin <= tMax;
    }

    // Squared-distance heuristic (fixed-point), scaled to int for the priority queue.
    private static long Heuristic((Fixed X, Fixed Z) a, (Fixed X, Fixed Z) b)
    {
        Fixed dx = a.X - b.X;
        Fixed dz = a.Z - b.Z;
        long d2 = (long)dx.Square().InternalValue + (long)dz.Square().InternalValue;
        return d2 < 0 ? 0 : d2;
    }
}
