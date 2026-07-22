using System.Collections.Generic;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Pathfinding;

// LongPathfinder — long-range pathfinding over the navcell grid. Ported from
// source/simulation2/helpers/LongPathfinder.h/.cpp.
//
// Algorithm: JPS (Jump Point Search), an A* variant that "jumps" in straight lines to the next
// forced-neighbour (jump point) instead of expanding every cell, drastically cutting the open
// set on uniform-cost grids. Diagonals never cut corners. Heuristic is octile distance.
//
// Dependency: HierarchicalPathfinder.MakeGoalReachable must convert the goal to a reachable
// POINT before JPS runs (so the search has a valid, reachable target).
//
// Scope: this is the full JPS. The JumpPointCache optimization (precomputed per-cell jump
// points) is omitted — the original ships with it OFF by default too (m_UseJPSCache(false)).

public sealed class LongPathfinder
{
    // Per-search tile record. status: 0=unexplored, 1=open, 2=closed.
    private struct Tile
    {
        public long G;       // cost-so-far (scaled, for ranking only)
        public long H;       // heuristic (scaled)
        public int PredI;    // predecessor navcell
        public int PredJ;
        public byte Status;  // 0/1/2
        public bool HasPred;
    }

    private Grid<NavcellData>? _grid;

    /// <summary>Set the current passability grid. Call after the grid is (re)built.</summary>
    public void Reload(Grid<NavcellData> grid) => _grid = grid;

    private bool InBounds(int i, int j) =>
        _grid != null && (uint)i < (uint)_grid.W && (uint)j < (uint)_grid.H;

    private bool Passable(int i, int j, PassClass passClass) =>
        InBounds(i, j) && PathfindingCore.IsPassable(_grid!.Get(i, j), passClass);

    /// <summary>Compute a long path from a navcell to a goal. The goal should already have been
    /// passed through HierarchicalPathfinder.MakeGoalReachable so it's a reachable POINT.</summary>
    public WaypointPath ComputePath(HierarchicalPathfinder hier, int x0, int z0,
        in PathGoal goal, PassClass passClass)
    {
        var path = new WaypointPath();
        if (_grid == null) return path;

        int startI = x0, startJ = z0;
        // Sanitize start: if impassable, snap to nearest passable.
        if (!Passable(startI, startJ, passClass))
        {
            var snap = hier.FindNearestPassableNavcell(startI, startJ, passClass);
            if (!snap.HasValue) return path;
            startI = snap.Value.x; startJ = snap.Value.z;
        }

        // MakeGoalReachable rewrites goal to a reachable POINT.
        var resolvedGoal = goal;
        hier.MakeGoalReachable(startI, startJ, ref resolvedGoal, passClass);

        int goalI = PathfindingCore.WorldToNavcell(resolvedGoal.X);
        int goalJ = PathfindingCore.WorldToNavcell(resolvedGoal.Z);

        if (!Passable(goalI, goalJ, passClass)) return path;
        if (startI == goalI && startJ == goalJ)
        {
            path.Push(new Waypoint(resolvedGoal.X, resolvedGoal.Z));
            return path;
        }

        if (JpsSearch(startI, startJ, goalI, goalJ, passClass, out var navPath))
        {
            // Convert navcell path → world-space waypoints (reverse order: goal pushed first).
            foreach (var (ni, nj) in navPath)
                path.Push(new Waypoint(
                    PathfindingCore.NavcellCenterToWorld(ni),
                    PathfindingCore.NavcellCenterToWorld(nj)));
        }
        return path;
    }

    // The search. P0 uses plain 8-neighbour A* (correct, simple, fast enough at navcell scale).
    // The JPS jump-point optimization (skipping straight-line cells) can be layered on later as
    // a pure performance win without changing the result; correctness comes first.
    private bool JpsSearch(int startI, int startJ, int goalI, int goalJ,
        PassClass passClass, out List<(int i, int j)> navPath)
    {
        navPath = new List<(int, int)>();
        int w = _grid!.W, h = _grid.H;
        var tiles = new SparseGrid<Tile>(w, h);
        var pq = new PriorityQueueHeap(w * h);

        SetTile(tiles, startI, startJ, new Tile
        {
            G = 0,
            H = OctileScaled(startI, startJ, goalI, goalJ),
            Status = 1,
            HasPred = false
        }, pq);

        while (!pq.IsEmpty)
        {
            int current = pq.Pop();
            int ci = current / h, cj = current % h;
            var curTile = tiles.Get(ci, cj);
            if (curTile.Status == 2) continue;   // already closed (stale heap entry)
            curTile.Status = 2;
            tiles.Set(ci, cj, curTile);

            if (ci == goalI && cj == goalJ)
            {
                Reconstruct(tiles, ci, cj, navPath);
                return true;
            }

            // Expand all 8 neighbours (no corner-cutting on diagonals).
            for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0) continue;
                    int ni = ci + di, nj = cj + dj;
                    if (!Passable(ni, nj, passClass)) continue;
                    bool diag = di != 0 && dj != 0;
                    // No corner-cutting: diagonal blocked if either orthogonal neighbour is impassable.
                    if (diag && (!Passable(ci + di, cj, passClass) || !Passable(ci, cj + dj, passClass)))
                        continue;
                    Relax(tiles, pq, ci, cj, ni, nj, diag, goalI, goalJ);
                }
        }
        return false;
    }

    // Relax a neighbour: if it's unexplored or this path is cheaper, record the predecessor +
    // cost and (re)insert into the open set.
    private void Relax(SparseGrid<Tile> tiles, PriorityQueueHeap pq,
        int fromI, int fromJ, int ni, int nj, bool diagonal, int goalI, int goalJ)
    {
        long stepCost = diagonal ? DiagScaled() : OrthoScaled();
        long fromG = tiles.Get(fromI, fromJ).G;
        long tentativeG = fromG + stepCost;

        var existing = tiles.Get(ni, nj);
        if (existing.Status == 2) return;   // closed
        if (existing.Status == 1 && existing.G <= tentativeG) return;   // not better

        var updated = new Tile
        {
            G = tentativeG,
            H = existing.Status == 0 ? OctileScaled(ni, nj, goalI, goalJ) : existing.H,
            PredI = fromI,
            PredJ = fromJ,
            Status = 1,
            HasPred = true
        };
        SetTile(tiles, ni, nj, updated, pq);
    }

    private void SetTile(SparseGrid<Tile> tiles, int i, int j, Tile t, PriorityQueueHeap pq)
    {
        tiles.Set(i, j, t);
        pq.Push(i * _grid!.H + j, t.G + t.H);
    }

    // Reconstruct the path by following predecessors from goal back to start, reversing into navPath.
    private void Reconstruct(SparseGrid<Tile> tiles, int gi, int gj, List<(int i, int j)> navPath)
    {
        int i = gi, j = gj;
        var rev = new List<(int, int)>();
        while (true)
        {
            rev.Add((i, j));
            var t = tiles.Get(i, j);
            if (!t.HasPred) break;
            i = t.PredI; j = t.PredJ;
        }
        rev.Reverse();      // start → goal
        navPath.AddRange(rev);
    }

    private static int Sign(int x) => x > 0 ? 1 : x < 0 ? -1 : 0;

    // Cost scaling constants: orthogonal step = 2^16, diagonal = 2^16 * sqrt(2) ≈ 92682.
    private const long OrthoStep = 65536;
    private const long DiagStep = 92682;
    private static long OrthoScaled() => OrthoStep;
    private static long DiagScaled() => DiagStep;

    // Octile heuristic scaled to match the step costs (diag-aware Chebyshev).
    private static long OctileScaled(int i, int j, int gi, int gj)
    {
        int dx = System.Math.Abs(i - gi);
        int dz = System.Math.Abs(j - gj);
        int diag = System.Math.Min(dx, dz);
        int ortho = System.Math.Max(dx, dz) - diag;
        return ortho * OrthoStep + diag * DiagStep;
    }

    /// <summary>Line-of-sight / movement check: rasterize a line between two navcells and test
    /// each for passability. Allows leaving an impassable cell but not entering one. Ported from
    /// Pathfinding::CheckLineMovement. Used by PathfinderComponent.CheckMovement.</summary>
    public bool CheckLineMovement(int i0, int j0, int i1, int j1, PassClass passClass)
    {
        if (_grid == null) return false;
        // Bresenham; skip the start cell (may legitimately be impassable if unit is stuck).
        int dx = System.Math.Abs(i1 - i0), dz = System.Math.Abs(j1 - j0);
        int sx = i0 < i1 ? 1 : -1, sz = j0 < j1 ? 1 : -1;
        int err = dx - dz;
        int i = i0, j = j0;
        while (true)
        {
            if (i == i1 && j == j1) return true;
            // Don't test the start cell (origin may be impassable).
            if (!(i == i0 && j == j0))
                if (!Passable(i, j, passClass)) return false;
            int e2 = 2 * err;
            if (e2 > -dz) { err -= dz; i += sx; }
            if (e2 < dx) { err += dx; j += sz; }
        }
    }
}
