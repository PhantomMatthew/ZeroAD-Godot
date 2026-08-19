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
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

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
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        ProfReachTicks += t1 - t0;

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
        ProfSearchTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
        return path;
    }

    /// <summary>性能探针:可达性处理 vs JPS 求解耗时(ticks)。</summary>
    public static long ProfReachTicks, ProfSearchTicks;

    // The search: JPS(Jump Point Search)。原版 plain A* 在 2752² 开放网格上长路径
    // 扩展 ~1-3M cell(Gather 长单实测 ~220ms/次);JPS 只展开跳跃点,开放区 10-100x。
    // 禁角规则同 plain 版:对角移动要求两正交邻均可通。
    private bool JpsSearch(int startI, int startJ, int goalI, int goalJ,
        PassClass passClass, out List<(int i, int j)> navPath)
    {
        navPath = new List<(int, int)>();
        int w = _grid!.W, h = _grid.H;
        var tiles = new SparseGrid<Tile>(w, h);
        // 复用堆实例:此前每次寻路都 new PriorityQueueHeap(w*h)——分配 7.6M 项
        // int 数组(30MB)再 Fill(-1) 一次,2752² 大地图上 ~20ms/次寻路 ×150 次/回合
        // = 帧率归零级内存抖动。position 表常驻(30MB 可接受),Clear 只清已用项。
        var pq = GetQueue(w * h);
        _jpsGoalI = goalI; _jpsGoalJ = goalJ; _jpsPass = passClass;

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

            foreach (var (di, dj) in NeighborDirs(tiles, ci, cj, curTile))
            {
                var jp = Jump(ci, cj, di, dj);
                if (jp.HasValue)
                    RelaxTo(tiles, pq, ci, cj, jp.Value.i, jp.Value.j, di != 0 && dj != 0, goalI, goalJ,
                        StepCount(ci, cj, jp.Value.i, jp.Value.j));
            }
        }
        return false;
    }

    private int _jpsGoalI, _jpsGoalJ;
    private PassClass _jpsPass;

    /// <summary>JPS 跳跃:沿 (di,dj) 直行,遇跳跃点(目标/含被迫邻点)返回之,撞墙返回 null。
    /// 迭代实现(递归版在 2752² 开阔区单次搜索 300 万次调用,调用开销即 ~20ms 主因;
    /// 扫描序列与返回值与递归版逐一对位,输出路径不变)。</summary>
    private (int i, int j)? Jump(int ci, int cj, int di, int dj)
    {
        int ni = ci, nj = cj;
        while (true)
        {
            ni += di; nj += dj;
            if (!Passable(ni, nj, _jpsPass)) return null;
            if (ni == _jpsGoalI && nj == _jpsGoalJ) return (ni, nj);

            if (di != 0 && dj != 0)
            {
                // 对角:被迫邻点检查(两侧开阔但邻侧受阻)
                if ((Passable(ni - di, nj + dj, _jpsPass) && !Passable(ni - di, nj, _jpsPass))
                    || (Passable(ni + di, nj - dj, _jpsPass) && !Passable(ni, nj - dj, _jpsPass)))
                    return (ni, nj);
                // 两正交分量任一有跳跃点 → 本点即跳跃点(每步在不同行/列扫描,无法去重)。
                if (Jump(ni, nj, di, 0).HasValue || Jump(ni, nj, 0, dj).HasValue)
                    return (ni, nj);
            }
            else if (di != 0)
            {
                if ((Passable(ni + di, nj + 1, _jpsPass) && !Passable(ni, nj + 1, _jpsPass))
                    || (Passable(ni + di, nj - 1, _jpsPass) && !Passable(ni, nj - 1, _jpsPass)))
                    return (ni, nj);
            }
            else
            {
                if ((Passable(ni + 1, nj + dj, _jpsPass) && !Passable(ni + 1, nj, _jpsPass))
                    || (Passable(ni - 1, nj + dj, _jpsPass) && !Passable(ni - 1, nj, _jpsPass)))
                    return (ni, nj);
            }
        }
    }

    private static readonly (int di, int dj)[] _allDirs =
        { (-1,-1), (0,-1), (1,-1), (-1,0), (1,0), (-1,1), (0,1), (1,1) };

    /// <summary>JPS 邻点方向剪枝:按来向保留自然邻 + 被迫邻(无来向=起点,全 8 向)。</summary>
    private IEnumerable<(int di, int dj)> NeighborDirs(SparseGrid<Tile> tiles, int ci, int cj, Tile cur)
    {
        if (!cur.HasPred)
        {
            foreach (var d in _allDirs) yield return d;
            yield break;
        }
        int di = System.Math.Sign(ci - cur.PredI), dj = System.Math.Sign(cj - cur.PredJ);
        if (di != 0 && dj != 0)
        {
            // 对角来向:正交两向 + 同对角
            yield return (di, 0);
            yield return (0, dj);
            yield return (di, dj);
            // 被迫邻:正交受阻侧的斜向
            if (!Passable(ci - di, cj, _jpsPass) && Passable(ci - di, cj + dj, _jpsPass))
                yield return (-di, dj);
            if (!Passable(ci, cj - dj, _jpsPass) && Passable(ci + di, cj - dj, _jpsPass))
                yield return (di, -dj);
        }
        else if (di != 0)
        {
            yield return (di, 0);
            if (!Passable(ci, cj + 1, _jpsPass) && Passable(ci + di, cj + 1, _jpsPass))
                yield return (di, 1);
            if (!Passable(ci, cj - 1, _jpsPass) && Passable(ci + di, cj - 1, _jpsPass))
                yield return (di, -1);
        }
        else
        {
            yield return (0, dj);
            if (!Passable(ci + 1, cj, _jpsPass) && Passable(ci + 1, cj + dj, _jpsPass))
                yield return (1, dj);
            if (!Passable(ci - 1, cj, _jpsPass) && Passable(ci - 1, cj + dj, _jpsPass))
                yield return (-1, dj);
        }
    }

    private static int StepCount(int x0, int y0, int x1, int y1) =>
        System.Math.Max(System.Math.Abs(x1 - x0), System.Math.Abs(y1 - y0));

    // Relax 一个跳跃点:G 按跳步数×单位步进(斜步 diag 价)。
    private void RelaxTo(SparseGrid<Tile> tiles, PriorityQueueHeap pq,
        int fromI, int fromJ, int ni, int nj, bool diagonal, int goalI, int goalJ, int steps)
    {
        long stepCost = (diagonal ? DiagScaled() : OrthoScaled()) * steps;
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

    // 跨寻路复用的堆(position 查找表随地图尺寸常驻,免去每次寻路 30MB 分配)。
    private PriorityQueueHeap? _queue;
    private PriorityQueueHeap GetQueue(int maxId)
    {
        if (_queue == null || _queue.Capacity < maxId)
            _queue = new PriorityQueueHeap(maxId);
        else
            _queue.Clear();
        return _queue;
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
