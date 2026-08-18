using System.Collections.Generic;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Pathfinding;

// HierarchicalPathfinder — connectivity-only pathfinding. Ported from
// source/simulation2/helpers/HierarchicalPathfinder.h/.cpp.
//
// Divides the nav grid into CHUNK_SIZE×CHUNK_SIZE chunks. Within each chunk, flood-fills
// 4-connected regions of passable navcells (per passability class). Regions connect across
// chunk borders where passable border navcells touch. Global regions (connected components of
// the region graph) make "is A reachable from B?" an O(1) lookup.
//
// This does NOT produce waypoint paths. Its job: (1) decide goal reachability fast,
// (2) snap an unreachable goal to the nearest reachable navcell (MakeGoalReachable) so
// LongPathfinder's JPS has a valid target.
//
// Per the confirmed scope, this is the full hierarchical structure but synchronously rebuilt
// (no incremental Update/dirtiness — that's a P1 optimization; Recompute is cheap enough at
// P0 map sizes).

public sealed class HierarchicalPathfinder
{
    public const int ChunkSize = 96;

    /// <summary>Identifies a region: which chunk + which local region id within it.
    /// r=0 means impassable.</summary>
    public readonly struct RegionId
    {
        public readonly int Ci;   // chunk index X
        public readonly int Cj;   // chunk index Z
        public readonly ushort R; // local region id within the chunk (0 = impassable)
        public RegionId(int ci, int cj, ushort r) { Ci = ci; Cj = cj; R = r; }
        public bool IsValid => R != 0;
    }

    // Per-class state. Region grid: navcell → local region id (0 = impassable).
    private sealed class ClassState
    {
        public readonly int ChunksW;
        public readonly int ChunksH;
        // Per-chunk local region grids: ChunkRegions[ci * ChunksH + cj] = ushort[CHUNK_SIZE, CHUNK_SIZE].
        // (Flat outer array of 2D arrays; outer is 1D indexed by chunk, inner is the 2D region map.)
        public readonly ushort[][,] ChunkRegions;
        // Region → global region id (0 = impassable / not assigned).
        public Dictionary<long, uint> GlobalRegions = new();
        // Edge graph: region → set of adjacent regions.
        public Dictionary<long, HashSet<long>> Edges = new();

        public ClassState(int chunksW, int chunksH)
        {
            ChunksW = chunksW;
            ChunksH = chunksH;
            ChunkRegions = new ushort[chunksW * chunksH][,];
        }

        public ushort[,] GetChunk(int ci, int cj) =>
            ChunkRegions[ci * ChunksH + cj]!;
        public void SetChunk(int ci, int cj, ushort[,] regions) =>
            ChunkRegions[ci * ChunksH + cj] = regions;
    }

    private readonly Dictionary<ushort, ClassState> _states = new();
    private int _navW;
    private int _navH;

    /// <summary>Full rebuild from a passability grid. Call after the grid changes (P0: every
    /// change; P1 can switch to incremental Update).</summary>
    public void Recompute(Grid<NavcellData> grid, IEnumerable<PassabilityClassDef> classes)
    {
        _states.Clear();
        _navW = grid.W;
        _navH = grid.H;
        foreach (var cls in classes)
        {
            var st = BuildClassState(grid, cls);
            if (st != null) _states[cls.Mask.Mask] = st;
        }
    }

    private static ClassState BuildClassState(Grid<NavcellData> grid, PassabilityClassDef cls)
    {
        int navW = grid.W, navH = grid.H;
        int chunksW = (navW + ChunkSize - 1) / ChunkSize;
        int chunksH = (navH + ChunkSize - 1) / ChunkSize;
        var st = new ClassState(chunksW, chunksH);

        // 1. Flood-fill regions within each chunk (4-connected). Region id 0 = impassable.
        for (int cj = 0; cj < chunksH; cj++)
            for (int ci = 0; ci < chunksW; ci++)
            {
                st.SetChunk(ci, cj, FloodChunkRegions(grid, cls.Mask, ci, cj, navW, navH));
            }

        // 2. Build the region-edge graph: regions connect across chunk borders where passable
        // border navcells are 4-adjacent.
        BuildEdges(grid, cls.Mask, st, navW, navH);

        // 3. Flood-fill global regions (connected components of the edge graph).
        BuildGlobalRegions(st);

        return st;
    }

    // Flood-fill 4-connected passable navcells within one chunk into regions (union-find).
    // Returns the local region id grid for the chunk (0 = impassable).
    private static ushort[,] FloodChunkRegions(Grid<NavcellData> grid, PassClass mask,
        int ci, int cj, int navW, int navH)
    {
        var regions = new ushort[ChunkSize, ChunkSize];
        var parent = new List<ushort> { 0 };   // index 0 unused (impassable)
        int x0 = ci * ChunkSize, z0 = cj * ChunkSize;

        // First pass: assign a new region id to each passable navcell.
        ushort nextId = 1;
        for (int dz = 0; dz < ChunkSize; dz++)
            for (int dx = 0; dx < ChunkSize; dx++)
            {
                int nx = x0 + dx, nz = z0 + dz;
                if (nx >= navW || nz >= navH) { regions[dx, dz] = 0; continue; }
                if (!PathfindingCore.IsPassable(grid.Get(nx, nz), mask)) { regions[dx, dz] = 0; continue; }
                regions[dx, dz] = nextId;
                parent.Add(nextId);
                nextId++;
            }

        // Union-find helpers.
        ushort Find(ushort a)
        {
            while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
            return a;
        }
        void Union(ushort a, ushort b)
        {
            ushort ra = Find(a), rb = Find(b);
            if (ra != rb) parent[rb] = ra;
        }

        // Second pass: union 4-adjacent passable cells (within this chunk).
        for (int dz = 0; dz < ChunkSize; dz++)
            for (int dx = 0; dx < ChunkSize; dx++)
            {
                ushort here = regions[dx, dz];
                if (here == 0) continue;
                if (dx + 1 < ChunkSize && regions[dx + 1, dz] != 0) Union(here, regions[dx + 1, dz]);
                if (dz + 1 < ChunkSize && regions[dx, dz + 1] != 0) Union(here, regions[dx, dz + 1]);
            }

        // Third pass: compress to canonical ids, remap to a dense 1..N range.
        var remap = new Dictionary<ushort, ushort>();
        ushort canonical = 0;
        for (int dz = 0; dz < ChunkSize; dz++)
            for (int dx = 0; dx < ChunkSize; dx++)
            {
                if (regions[dx, dz] == 0) continue;
                ushort root = Find(regions[dx, dz]);
                if (!remap.TryGetValue(root, out ushort mapped))
                {
                    canonical++;
                    mapped = canonical;
                    remap[root] = mapped;
                }
                regions[dx, dz] = mapped;
            }
        return regions;
    }

    // Build the region-edge graph by scanning chunk borders.
    private static void BuildEdges(Grid<NavcellData> grid, PassClass mask, ClassState st, int navW, int navH)
    {
        for (int cj = 0; cj < st.ChunksH; cj++)
            for (int ci = 0; ci < st.ChunksW; ci++)
            {
                var chunk = st.GetChunk(ci, cj);
                // East border (between this chunk and ci+1).
                if (ci + 1 < st.ChunksW)
                {
                    var eastChunk = st.GetChunk(ci + 1, cj);
                    for (int dz = 0; dz < ChunkSize; dz++)
                    {
                        int localX = ChunkSize - 1;
                        ushort r1 = chunk[localX, dz];
                        ushort r2 = eastChunk[0, dz];
                        if (r1 != 0 && r2 != 0)
                            AddEdge(st, RegionKey(ci, cj, r1), RegionKey(ci + 1, cj, r2));
                    }
                }
                // South border (between this chunk and cj+1).
                if (cj + 1 < st.ChunksH)
                {
                    var southChunk = st.GetChunk(ci, cj + 1);
                    for (int dx = 0; dx < ChunkSize; dx++)
                    {
                        int localZ = ChunkSize - 1;
                        ushort r1 = chunk[dx, localZ];
                        ushort r2 = southChunk[dx, 0];
                        if (r1 != 0 && r2 != 0)
                            AddEdge(st, RegionKey(ci, cj, r1), RegionKey(ci, cj + 1, r2));
                    }
                }
            }
    }

    private static void AddEdge(ClassState st, long a, long b)
    {
        if (!st.Edges.TryGetValue(a, out var setA)) { setA = new HashSet<long>(); st.Edges[a] = setA; }
        setA.Add(b);
        if (!st.Edges.TryGetValue(b, out var setB)) { setB = new HashSet<long>(); st.Edges[b] = setB; }
        setB.Add(a);
    }

    // Pack a region id into a long key for graph lookups.
    private static long RegionKey(int ci, int cj, ushort r) =>
        ((long)ci << 40) | ((long)cj << 20) | (uint)r;

    private static long RegionKey(RegionId rid) => RegionKey(rid.Ci, rid.Cj, rid.R);

    // Flood-fill connected components of the region-edge graph → global region ids.
    // Iterates ALL regions (not just those with edges) so isolated single regions get a global
    // id too — otherwise a region with no neighbours would read as global 0 (impassable).
    private static void BuildGlobalRegions(ClassState st)
    {
        uint nextGlobal = 1;
        var visited = new HashSet<long>();

        // Collect every region id that appears in any chunk's region grid.
        var allRegions = new HashSet<long>();
        for (int cj = 0; cj < st.ChunksH; cj++)
            for (int ci = 0; ci < st.ChunksW; ci++)
            {
                var chunk = st.GetChunk(ci, cj);
                for (int dz = 0; dz < ChunkSize; dz++)
                    for (int dx = 0; dx < ChunkSize; dx++)
                    {
                        ushort r = chunk[dx, dz];
                        if (r != 0) allRegions.Add(RegionKey(ci, cj, r));
                    }
            }

        var queue = new Queue<long>();
        foreach (long start in allRegions)
        {
            if (visited.Contains(start)) continue;
            uint gid = nextGlobal++;
            visited.Add(start);
            queue.Enqueue(start);
            st.GlobalRegions[start] = gid;
            while (queue.Count > 0)
            {
                long cur = queue.Dequeue();
                if (st.Edges.TryGetValue(cur, out var neighbors))
                {
                    foreach (long nb in neighbors)
                    {
                        if (visited.Add(nb))
                        {
                            st.GlobalRegions[nb] = gid;
                            queue.Enqueue(nb);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Resolve the region id of a navcell for a class.</summary>
    public RegionId Get(int navX, int navZ, PassClass passClass)
    {
        if (!_states.TryGetValue(passClass.Mask, out var st)) return default;
        int ci = navX / ChunkSize, cj = navZ / ChunkSize;
        int dx = navX - ci * ChunkSize, dz = navZ - cj * ChunkSize;
        if (ci < 0 || cj < 0 || ci >= st.ChunksW || cj >= st.ChunksH) return default;
        ushort r = st.GetChunk(ci, cj)[dx, dz];
        return new RegionId(ci, cj, r);
    }

    /// <summary>The global region id of a navcell. Two navcells with the same global id are
    /// mutually reachable. 0 = impassable.</summary>
    public uint GetGlobalRegion(int navX, int navZ, PassClass passClass)
    {
        var rid = Get(navX, navZ, passClass);
        if (!rid.IsValid) return 0;
        if (!_states.TryGetValue(passClass.Mask, out var st)) return 0;
        return st.GlobalRegions.TryGetValue(RegionKey(rid), out var gid) ? gid : 0;
    }

    /// <summary>True if a goal is reachable from a start navcell.</summary>
    public bool IsGoalReachable(int startX, int startZ, in PathGoal goal, PassClass passClass)
    {
        uint startGlobal = GetGlobalRegion(startX, startZ, passClass);
        if (startGlobal == 0) return false;
        // Point 目标(绝对主流:MoveToTargetEdge 出的全是 Point)直接查自身 region——
        // 旧实现无条件全图 7.6M navcell 扫描 ×region 查表(~250ms/次寻路),2752²
        // 大地图上每单位每订单一次,是大地图寻路慢的最大单一原因。
        if (goal.Type == PathGoal.Kind.Point)
        {
            return GetGlobalRegion(
                PathfindingCore.WorldToNavcell(goal.X),
                PathfindingCore.WorldToNavcell(goal.Z), passClass) == startGlobal;
        }
        // 形状目标:只扫形状的包围盒内 navcell,任一与 start 同 global region 即可达。
        // (Circle/Square 的中心±半径范围;Inverted 形态按同样盒近似——比原版全图扫描保守,
        // 但 Inverse 目标在实践中不用。)
        int gx = PathfindingCore.WorldToNavcell(goal.X);
        int gz = PathfindingCore.WorldToNavcell(goal.Z);
        int rad = System.Math.Max(
            PathfindingCore.WorldToNavcell(goal.Hw),
            PathfindingCore.WorldToNavcell(goal.Hh)) + 1;
        for (int j = System.Math.Max(0, gz - rad); j <= System.Math.Min(_navH - 1, gz + rad); j++)
            for (int i = System.Math.Max(0, gx - rad); i <= System.Math.Min(_navW - 1, gx + rad); i++)
            {
                if (GetGlobalRegion(i, j, passClass) != startGlobal) continue;
                if (goal.NavcellContainsGoal(PathfindingCore.NavcellCenterToWorld(i),
                                             PathfindingCore.NavcellCenterToWorld(j)))
                    return true;
            }
        return false;
    }

    /// <summary>Rewrite an unreachable goal to the nearest passable navcell reachable from the
    /// start. Returns true if the goal was already reachable (goal unchanged); false if the goal
    /// was snapped. Mirrors HierarchicalPathfinder::MakeGoalReachable.</summary>
    public bool MakeGoalReachable(int startX, int startZ, ref PathGoal goal, PassClass passClass)
    {
        if (IsGoalReachable(startX, startZ, goal, passClass)) return true;

        uint startGlobal = GetGlobalRegion(startX, startZ, passClass);
        // Find the nearest navcell that is in the start's global region (BFS outward from start).
        if (startGlobal == 0)
        {
            // Start itself is impassable — snap to nearest passable first.
            var nearest = FindNearestPassableNavcell(startX, startZ, passClass);
            if (!nearest.HasValue) return false;
            startGlobal = GetGlobalRegion(nearest.Value.x, nearest.Value.z, passClass);
            startX = nearest.Value.x; startZ = nearest.Value.z;
            if (startGlobal == 0) return false;
        }

        // Spiral/BFS outward from the goal centre to find the nearest navcell in startGlobal.
        var (gx, gz) = (PathfindingCore.WorldToNavcell(goal.X), PathfindingCore.WorldToNavcell(goal.Z));
        var found = FindNearestNavcellInGlobalRegion(gx, gz, startGlobal, passClass);
        if (!found.HasValue) return false;
        // Rewrite goal to a POINT at that navcell's centre.
        goal = PathGoal.Point(
            PathfindingCore.NavcellCenterToWorld(found.Value.x),
            PathfindingCore.NavcellCenterToWorld(found.Value.z));
        return false;
    }

    /// <summary>Find the nearest passable navcell to a position (spiral search). Mirrors
    /// FindNearestPassableNavcell.</summary>
    public (int x, int z)? FindNearestPassableNavcell(int x, int z, PassClass passClass)
    {
        if (x < 0) x = 0; if (x >= _navW) x = _navW - 1;
        if (z < 0) z = 0; if (z >= _navH) z = _navH - 1;
        if (!_states.TryGetValue(passClass.Mask, out var st)) return null;
        if (Get(x, z, passClass).IsValid) return (x, z);
        // Expanding ring search.
        for (int ring = 1; ring < System.Math.Max(_navW, _navH); ring++)
        {
            for (int dz = -ring; dz <= ring; dz++)
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (System.Math.Abs(dx) != ring && System.Math.Abs(dz) != ring) continue;
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= _navW || nz >= _navH) continue;
                    if (Get(nx, nz, passClass).IsValid) return (nx, nz);
                }
        }
        return null;
    }

    private (int x, int z)? FindNearestNavcellInGlobalRegion(int x, int z, uint targetGlobal, PassClass passClass)
    {
        if (x < 0) x = 0; if (x >= _navW) x = _navW - 1;
        if (z < 0) z = 0; if (z >= _navH) z = _navH - 1;
        for (int ring = 0; ring < System.Math.Max(_navW, _navH); ring++)
        {
            for (int dz = -ring; dz <= ring; dz++)
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (System.Math.Abs(dx) != ring && System.Math.Abs(dz) != ring) continue;
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= _navW || nz >= _navH) continue;
                    if (GetGlobalRegion(nx, nz, passClass) == targetGlobal) return (nx, nz);
                }
        }
        return null;
    }
}
