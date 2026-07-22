using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim
{
    /// <summary>Flags describing what an obstruction blocks. Mirrors 0 A.D. <c>ICmpObstructionManager</c>.</summary>
    [Flags]
    public enum ObstructionFlags : byte
    {
        None = 0,
        BlockMovement = 1 << 0,
        BlockFoundation = 1 << 1,
        BlockConstruction = 1 << 2,
        BlockPathfinding = 1 << 3,
        Moving = 1 << 4,    // unit is currently moving (pathfinder hint)
        DefaultBlock = BlockMovement | BlockFoundation | BlockConstruction | BlockPathfinding,
    }

    /// <summary>
    /// A rectangular obstruction in world space: center, two unit axes (u, v), half-extents.
    /// Mirrors <c>ObstructionSquare</c> from <c>ICmpObstructionManager.h</c>. u × v must be +1
    /// (v is u rotated +90°) for <see cref="Geometry.TestSquareSquare"/> to work.
    /// </summary>
    public readonly struct ObstructionSquare
    {
        public readonly Fixed X, Z;
        public readonly FixedVector2D U, V;    // unit axes
        public readonly Fixed Hw, Hh;          // half-width (along U), half-height (along V)
        public ObstructionSquare(Fixed x, Fixed z, FixedVector2D u, FixedVector2D v, Fixed hw, Fixed hh)
        { X = x; Z = z; U = u; V = v; Hw = hw; Hh = hh; }
    }

    /// <summary>Dynamic circle-shaped obstruction (units). Lightweight: units move often.</summary>
    public sealed class UnitShape
    {
        public EntityId Entity;
        public Fixed X, Z;
        public Fixed Clearance;     // radius + small buffer
        public ObstructionFlags Flags;
        public uint Group;          // control group (units in same group don't block each other)
    }

    /// <summary>Static rotated-rectangle obstruction (buildings). u/v encode orientation.</summary>
    public sealed class StaticShape
    {
        public EntityId Entity;
        public Fixed X, Z;
        public FixedVector2D U, V;  // unit axes (rotation encoded)
        public Fixed Hw, Hh;        // half-width along U, half-height along V
        public ObstructionFlags Flags;
        public uint Group, Group2;
    }

    /// <summary>
    /// Strategy predicate for obstruction queries: returns true if the shape should be SKIPPED
    /// (not counted as blocking). Mirrors <c>IObstructionTestFilter</c> subclasses from
    /// <c>ICmpObstructionManager.h</c>. Stateless, so implemented as a delegate for simplicity.
    /// Arguments: (tag, flags, group, group2).
    /// </summary>
    public delegate bool ObstructionShapeFilter(ObstructionTag tag, ObstructionFlags flags, uint group, uint group2);

    /// <summary>Opaque handle returned by AddUnitShape/AddStaticShape. Low bit distinguishes kind.</summary>
    public readonly struct ObstructionTag : IEquatable<ObstructionTag>
    {
        public readonly uint N;
        public ObstructionTag(uint n) { N = n; }
        public bool IsValid => N != 0;
        public bool IsStatic => (N & 1) == 1;
        public bool IsUnit => (N & 1) == 0 && N != 0;
        public uint Index => N >> 1;
        public bool Equals(ObstructionTag other) => N == other.N;
        public override bool Equals(object? obj) => obj is ObstructionTag t && Equals(t);
        public override int GetHashCode() => (int)N;
        public static bool operator ==(ObstructionTag a, ObstructionTag b) => a.N == b.N;
        public static bool operator !=(ObstructionTag a, ObstructionTag b) => a.N != b.N;
    }

    /// <summary>
    /// Spatial hash grid for obstruction queries. Each item (entity id) is copied into every grid
    /// cell its AABB overlaps, so a query just scans the cells in its AABB and dedupes. Ported
    /// from <c>SpatialSubdivision</c> in <c>source/simulation2/helpers/Spatial.h</c>.
    ///
    /// Cells are <c>divisionSize</c> units square; the grid covers [0, maxX] × [0, maxZ].
    /// </summary>
    public sealed class SpatialSubdivision
    {
        private List<uint>[] _cells = Array.Empty<List<uint>>();
        public Fixed DivisionSize { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        public void Reset(Fixed maxX, Fixed maxZ, Fixed divisionSize)
        {
            DivisionSize = divisionSize;
            Width = Math.Max(1, (maxX / divisionSize).ToIntRoundToInfinity());
            Height = Math.Max(1, (maxZ / divisionSize).ToIntRoundToInfinity());
            int n = Width * Height;
            _cells = new List<uint>[n];
            for (int i = 0; i < n; i++) _cells[i] = new List<uint>();
        }

        // Coordinate-to-cell helpers. Points on a boundary count in BOTH adjacent cells, matching
        // the original (RoundToInfinity-1 for low, RoundToNegInfinity for high).
        private int GetI0(Fixed x) => Clamp((x / DivisionSize).ToIntRoundToInfinity() - 1, 0, Width - 1);
        private int GetJ0(Fixed z) => Clamp((z / DivisionSize).ToIntRoundToInfinity() - 1, 0, Height - 1);
        private int GetI1(Fixed x) => Clamp((x / DivisionSize).ToIntRoundToNegInfinity(), 0, Width - 1);
        private int GetJ1(Fixed z) => Clamp((z / DivisionSize).ToIntRoundToNegInfinity(), 0, Height - 1);
        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        private int CellIndex(int i, int j) => i + j * Width;

        /// <summary>Add an item covering AABB [minX,minZ]-[maxX,maxZ]. Item must not already be present in those cells.</summary>
        public void Add(uint item, Fixed minX, Fixed minZ, Fixed maxX, Fixed maxZ)
        {
            int i0 = GetI0(minX), j0 = GetJ0(minZ), i1 = GetI1(maxX), j1 = GetJ1(maxZ);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    _cells[CellIndex(i, j)].Add(item);
        }

        /// <summary>Remove an item from the cells its AABB [minX,minZ]-[maxX,maxZ] covers. Size must match Add.</summary>
        public void Remove(uint item, Fixed minX, Fixed minZ, Fixed maxX, Fixed maxZ)
        {
            int i0 = GetI0(minX), j0 = GetJ0(minZ), i1 = GetI1(maxX), j1 = GetJ1(maxZ);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var list = _cells[CellIndex(i, j)];
                    for (int n = 0; n < list.Count; n++)
                        if (list[n] == item) { list.RemoveAt(n); break; }
                }
        }

        /// <summary>Equivalent to Remove(from) + Add(to), but skips work if the cell range is unchanged.</summary>
        public void Move(uint item, Fixed fromMinX, Fixed fromMinZ, Fixed fromMaxX, Fixed fromMaxZ,
                         Fixed toMinX, Fixed toMinZ, Fixed toMaxX, Fixed toMaxZ)
        {
            if (GetI0(fromMinX) == GetI0(toMinX) && GetJ0(fromMinZ) == GetJ0(toMinZ) &&
                GetI1(fromMaxX) == GetI1(toMaxX) && GetJ1(fromMaxZ) == GetJ1(toMaxZ))
                return;
            Remove(item, fromMinX, fromMinZ, fromMaxX, fromMaxZ);
            Add(item, toMinX, toMinZ, toMaxX, toMaxZ);
        }

        /// <summary>Collect items overlapping the AABB, deduped and sorted. Over-approximates (returns all cells' contents).</summary>
        public void GetInRange(List<uint> output, Fixed minX, Fixed minZ, Fixed maxX, Fixed maxZ)
        {
            output.Clear();
            int i0 = GetI0(minX), j0 = GetJ0(minZ), i1 = GetI1(maxX), j1 = GetJ1(maxZ);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    output.AddRange(_cells[CellIndex(i, j)]);
            output.Sort();
            // Dedupe in place (items copied across cells appear multiple times).
            int w = 0;
            for (int r = 0; r < output.Count; r++)
            {
                if (r > 0 && output[r] == output[r - 1]) continue;
                output[w++] = output[r];
            }
            if (w < output.Count) output.RemoveRange(w, output.Count - w);
        }
    }

    /// <summary>
    /// System-level obstruction store. Tracks all unit (circle) and static (rotated-rectangle)
    /// shapes, indexed by two <see cref="SpatialSubdivision"/>s for fast range/placement queries.
    /// Ported from <c>CCmpObstructionManager</c>. This replaces the legacy bool[,] grid while
    /// keeping its public API (<see cref="IsBlocked"/>, <see cref="WorldToGrid"/>,
    /// <see cref="GridToWorld"/>, <see cref="FindPath"/>) alive via a compatibility passability
    /// grid so <see cref="Components.UnitMotion"/> and <c>Minimap</c> keep working until they're
    /// migrated to the new API.
    /// </summary>
    public sealed class ObstructionManager
    {
        // World bounds.
        private Fixed _boundsX0, _boundsZ0, _boundsX1, _boundsZ1;

        // Shapes keyed by tag. Tag low bit: 0 = unit, 1 = static.
        private readonly Dictionary<uint, UnitShape> _unitShapes = new();
        private readonly Dictionary<uint, StaticShape> _staticShapes = new();
        private uint _nextTagRaw = 2; // 0 invalid, 1 would be static-index-0; start at 2 (unit)

        // Spatial indices (one per shape kind). Cell size 32m, matching the original.
        private readonly SpatialSubdivision _unitSubdivision = new();
        private readonly SpatialSubdivision _staticSubdivision = new();

        // --- Legacy compatibility grid (bool[,] + A*), kept so UnitMotion/Minimap don't break ---
        public int GridSize { get; }
        public float CellSize { get; }
        private readonly bool[,] _blocked;
        // Reused scratch list for queries — never exposed across calls.
        private readonly List<uint> _scratch = new();

        public ObstructionManager(int gridSize = 64, float cellSize = 4.0f)
        {
            GridSize = gridSize;
            CellSize = cellSize;
            _blocked = new bool[gridSize, gridSize];
            // Default world bounds to the legacy grid extent.
            float world = gridSize * cellSize;
            _boundsX0 = Fixed.Zero; _boundsZ0 = Fixed.Zero;
            _boundsX1 = Fixed.FromFloat(world); _boundsZ1 = Fixed.FromFloat(world);
            _unitSubdivision.Reset(_boundsX1, _boundsZ1, Fixed.FromInt(32));
            _staticSubdivision.Reset(_boundsX1, _boundsZ1, Fixed.FromInt(32));
        }

        public void SetBounds(Fixed x0, Fixed z0, Fixed x1, Fixed z1)
        {
            _boundsX0 = x0; _boundsZ0 = z0; _boundsX1 = x1; _boundsZ1 = z1;
            _unitSubdivision.Reset(x1, z1, Fixed.FromInt(32));
            _staticSubdivision.Reset(x1, z1, Fixed.FromInt(32));
            // Re-insert all existing shapes into the resized grid.
            foreach (var kvp in _unitShapes) AddUnitToSubdivision(kvp.Key, kvp.Value);
            foreach (var kvp in _staticShapes) AddStaticToSubdivision(kvp.Key, kvp.Value);
        }

        // --- Shape CRUD ---

        public ObstructionTag AddUnitShape(EntityId entity, Fixed x, Fixed z, Fixed clearance,
            ObstructionFlags flags, uint group)
        {
            uint raw = _nextTagRaw;
            _nextTagRaw += 2; // keep low bit 0 for unit tags
            var shape = new UnitShape { Entity = entity, X = x, Z = z, Clearance = clearance, Flags = flags, Group = group };
            _unitShapes[raw] = shape;
            AddUnitToSubdivision(raw, shape);
            return new ObstructionTag(raw);
        }

        public ObstructionTag AddStaticShape(EntityId entity, Fixed x, Fixed z, FixedVector2D u, FixedVector2D v,
            Fixed hw, Fixed hh, ObstructionFlags flags, uint group, uint group2)
        {
            uint raw = _nextTagRaw | 1; // low bit 1 = static
            _nextTagRaw += 2;
            var shape = new StaticShape { Entity = entity, X = x, Z = z, U = u, V = v, Hw = hw, Hh = hh, Flags = flags, Group = group, Group2 = group2 };
            _staticShapes[raw] = shape;
            AddStaticToSubdivision(raw, shape);
            // Also mark the legacy grid so UnitMotion's A* still routes around it.
            RasterizeStaticToLegacyGrid(shape);
            return new ObstructionTag(raw);
        }

        public void MoveShape(ObstructionTag tag, Fixed x, Fixed z, FixedVector2D u, FixedVector2D v)
        {
            if (tag.IsStatic && _staticShapes.TryGetValue(tag.N, out var ss))
            {
                RemoveStaticFromSubdivision(tag.N, ss);
                ss.X = x; ss.Z = z; ss.U = u; ss.V = v;
                AddStaticToSubdivision(tag.N, ss);
            }
            else if (tag.IsUnit && _unitShapes.TryGetValue(tag.N, out var us))
            {
                RemoveUnitFromSubdivision(tag.N, us);
                us.X = x; us.Z = z;
                AddUnitToSubdivision(tag.N, us);
            }
        }

        public void MoveUnitShape(ObstructionTag tag, Fixed x, Fixed z)
        {
            if (_unitShapes.TryGetValue(tag.N, out var us))
            {
                RemoveUnitFromSubdivision(tag.N, us);
                us.X = x; us.Z = z;
                AddUnitToSubdivision(tag.N, us);
            }
        }

        public void RemoveShape(ObstructionTag tag)
        {
            if (tag.IsStatic && _staticShapes.TryGetValue(tag.N, out var ss))
            {
                RemoveStaticFromSubdivision(tag.N, ss);
                UnrasterizeStaticFromLegacyGrid(ss);
                _staticShapes.Remove(tag.N);
            }
            else if (tag.IsUnit && _unitShapes.TryGetValue(tag.N, out var us))
            {
                RemoveUnitFromSubdivision(tag.N, us);
                _unitShapes.Remove(tag.N);
            }
        }

        public void SetUnitMovingFlag(ObstructionTag tag, bool moving)
        {
            if (_unitShapes.TryGetValue(tag.N, out var us))
                us.Flags = moving ? us.Flags | ObstructionFlags.Moving : us.Flags & ~ObstructionFlags.Moving;
        }

        public ObstructionSquare? GetObstruction(ObstructionTag tag)
        {
            if (tag.IsStatic && _staticShapes.TryGetValue(tag.N, out var ss))
                return new ObstructionSquare(ss.X, ss.Z, ss.U, ss.V, ss.Hw, ss.Hh);
            if (tag.IsUnit && _unitShapes.TryGetValue(tag.N, out var us))
                return new ObstructionSquare(us.X, us.Z, new FixedVector2D(Fixed.FromInt(1), Fixed.Zero),
                    new FixedVector2D(Fixed.Zero, Fixed.FromInt(1)), us.Clearance, us.Clearance);
            return null;
        }

        /// <summary>Snapshot of every static obstruction square (buildings etc.). Used by the
        /// passability-grid builder to stamp obstacles onto the navcell grid.</summary>
        public System.Collections.Generic.List<ObstructionSquare> GetAllStaticObstructions()
        {
            var list = new System.Collections.Generic.List<ObstructionSquare>(_staticShapes.Count);
            foreach (var ss in _staticShapes.Values)
                list.Add(new ObstructionSquare(ss.X, ss.Z, ss.U, ss.V, ss.Hw, ss.Hh));
            return list;
        }

        // --- Subdivision bookkeeping helpers ---

        private void AddUnitToSubdivision(uint raw, UnitShape s)
        {
            Fixed r = s.Clearance;
            _unitSubdivision.Add(raw, s.X - r, s.Z - r, s.X + r, s.Z + r);
        }

        private void RemoveUnitFromSubdivision(uint raw, UnitShape s)
        {
            Fixed r = s.Clearance;
            _unitSubdivision.Remove(raw, s.X - r, s.Z - r, s.X + r, s.Z + r);
        }

        private void AddStaticToSubdivision(uint raw, StaticShape s)
        {
            var bb = Geometry.GetHalfBoundingBox(s.U, s.V, new FixedVector2D(s.Hw, s.Hh));
            _staticSubdivision.Add(raw, s.X - bb.X, s.Z - bb.Y, s.X + bb.X, s.Z + bb.Y);
        }

        private void RemoveStaticFromSubdivision(uint raw, StaticShape s)
        {
            var bb = Geometry.GetHalfBoundingBox(s.U, s.V, new FixedVector2D(s.Hw, s.Hh));
            _staticSubdivision.Remove(raw, s.X - bb.X, s.Z - bb.Y, s.X + bb.X, s.Z + bb.Y);
        }

        // --- Placement tests ---

        /// <summary>
        /// Test whether a unit circle at (x,z) with <paramref name="clearance"/> overlaps any
        /// obstruction that the filter doesn't skip. Returns the colliding shape tags (empty = clear).
        /// Mirrors <c>CCmpObstructionManager::TestUnitShape</c>.
        /// </summary>
        public List<ObstructionTag> TestUnitShape(ObstructionShapeFilter? filter, Fixed x, Fixed z, Fixed clearance)
        {
            var hits = new List<ObstructionTag>();
            // Test against static shapes (OBB vs circle ≈ OBB vs point inflated by clearance).
            _staticSubdivision.GetInRange(_scratch, x - clearance, z - clearance, x + clearance, z + clearance);
            foreach (uint raw in _scratch)
            {
                if (!_staticShapes.TryGetValue(raw, out var ss)) continue;
                var tag = new ObstructionTag(raw | 1);
                if (filter != null && filter(tag, ss.Flags, ss.Group, ss.Group2)) continue;
                // Circle vs OBB: transform circle center into the OBB's local frame, clamp to box, measure distance.
                var rel = new FixedVector2D(x - ss.X, z - ss.Z);
                Fixed du = rel.Dot(ss.U).Absolute;
                Fixed dv = rel.Dot(ss.V).Absolute;
                Fixed extraU = du > ss.Hw ? du - ss.Hw : Fixed.Zero;
                Fixed extraV = dv > ss.Hh ? dv - ss.Hh : Fixed.Zero;
                // Squared distance from box to circle center vs clearance².
                long distSqInternal = (long)extraU.InternalValue * extraU.InternalValue + (long)extraV.InternalValue * extraV.InternalValue;
                long clearanceSqInternal = (long)clearance.InternalValue * clearance.InternalValue;
                if (distSqInternal <= clearanceSqInternal)
                    hits.Add(tag);
            }
            // Test against unit shapes (circle vs circle).
            _unitSubdivision.GetInRange(_scratch, x - clearance, z - clearance, x + clearance, z + clearance);
            foreach (uint raw in _scratch)
            {
                if (!_unitShapes.TryGetValue(raw, out var us)) continue;
                var tag = new ObstructionTag(raw);
                if (filter != null && filter(tag, us.Flags, us.Group, 0)) continue;
                Fixed dx = x - us.X, dz = z - us.Z;
                Fixed combined = clearance + us.Clearance;
                long distSqInternal = (long)dx.InternalValue * dx.InternalValue + (long)dz.InternalValue * dz.InternalValue;
                long combinedSqInternal = (long)combined.InternalValue * combined.InternalValue;
                if (distSqInternal <= combinedSqInternal)
                    hits.Add(tag);
            }
            return hits;
        }

        /// <summary>
        /// Test whether an OBB at (x,z) with axes u/v and half-size (hw,hh) overlaps any obstruction
        /// that the filter doesn't skip. Mirrors <c>CCmpObstructionManager::TestStaticShape</c>.
        /// </summary>
        public List<ObstructionTag> TestStaticShape(ObstructionShapeFilter? filter,
            Fixed x, Fixed z, FixedVector2D u, FixedVector2D v, Fixed hw, Fixed hh)
        {
            var hits = new List<ObstructionTag>();
            var center = new FixedVector2D(x, z);
            var halfSize = new FixedVector2D(hw, hh);
            var bb = Geometry.GetHalfBoundingBox(u, v, halfSize);

            // Static vs static: OBB-OBB SAT.
            _staticSubdivision.GetInRange(_scratch, x - bb.X, z - bb.Y, x + bb.X, z + bb.Y);
            foreach (uint raw in _scratch)
            {
                if (!_staticShapes.TryGetValue(raw, out var ss)) continue;
                var tag = new ObstructionTag(raw | 1);
                if (filter != null && filter(tag, ss.Flags, ss.Group, ss.Group2)) continue;
                var otherCenter = new FixedVector2D(ss.X, ss.Z);
                var otherHalf = new FixedVector2D(ss.Hw, ss.Hh);
                if (Geometry.TestSquareSquare(center, u, v, halfSize, otherCenter, ss.U, ss.V, otherHalf))
                    hits.Add(tag);
            }
            // Static vs unit: OBB contains circle center (inflated by clearance via point-in-box on center).
            _unitSubdivision.GetInRange(_scratch, x - bb.X, z - bb.Y, x + bb.X, z + bb.Y);
            foreach (uint raw in _scratch)
            {
                if (!_unitShapes.TryGetValue(raw, out var us)) continue;
                var tag = new ObstructionTag(raw);
                if (filter != null && filter(tag, us.Flags, us.Group, 0)) continue;
                // Circle (center us.X,us.Z, radius us.Clearance) vs OBB.
                var rel = new FixedVector2D(us.X - x, us.Z - z);
                Fixed du = rel.Dot(u).Absolute;
                Fixed dv = rel.Dot(v).Absolute;
                Fixed extraU = du > hw ? du - hw : Fixed.Zero;
                Fixed extraV = dv > hh ? dv - hh : Fixed.Zero;
                long distSqInternal = (long)extraU.InternalValue * extraU.InternalValue + (long)extraV.InternalValue * extraV.InternalValue;
                long clearanceSqInternal = (long)us.Clearance.InternalValue * us.Clearance.InternalValue;
                if (distSqInternal <= clearanceSqInternal)
                    hits.Add(tag);
            }
            return hits;
        }

        // --- Legacy compatibility API (used by UnitMotion + Minimap until migrated) ---

        public void Clear()
        {
            Array.Clear(_blocked, 0, _blocked.Length);
            _unitShapes.Clear();
            _staticShapes.Clear();
            _unitSubdivision.Reset(_boundsX1, _boundsZ1, Fixed.FromInt(32));
            _staticSubdivision.Reset(_boundsX1, _boundsZ1, Fixed.FromInt(32));
        }

        /// <summary>Mark a circle as blocked on the legacy grid. Kept for SimBridge callers that
        /// haven't been migrated to AddStaticShape yet.</summary>
        public void BlockCircle(float worldX, float worldZ, float radius)
        {
            int cx = WorldToGrid(worldX);
            int cz = WorldToGrid(worldZ);
            int r = Math.Max(1, (int)(radius / CellSize + 0.5f));
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r * r) continue;
                    int gx = cx + dx, gz = cz + dz;
                    if (gx >= 0 && gx < GridSize && gz >= 0 && gz < GridSize)
                        _blocked[gx, gz] = true;
                }
        }

        public void UnblockCircle(float worldX, float worldZ, float radius)
        {
            int cx = WorldToGrid(worldX);
            int cz = WorldToGrid(worldZ);
            int r = Math.Max(1, (int)(radius / CellSize + 0.5f));
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r * r) continue;
                    int gx = cx + dx, gz = cz + dz;
                    if (gx >= 0 && gx < GridSize && gz >= 0 && gz < GridSize)
                        _blocked[gx, gz] = false;
                }
        }

        public bool IsBlocked(int gx, int gz)
        {
            if (gx < 0 || gx >= GridSize || gz < 0 || gz >= GridSize) return true;
            return _blocked[gx, gz];
        }

        public int WorldToGrid(float world) => (int)(world / CellSize);
        public float GridToWorld(int grid) => grid * CellSize + CellSize * 0.5f;

        [Obsolete("Use PathfinderComponent.ComputePath (the M3 hierarchical+A*+vertex pipeline). "
            + "Retained as a fallback for code paths where the new pathfinder isn't initialized.")]
        public List<(int x, int z)> FindPath(int sx, int sz, int ex, int ez)
        {
            if (IsBlocked(ex, ez)) return new List<(int, int)>();
            var open = new LegacyPriorityQueue();
            var cameFrom = new Dictionary<int, int>();
            var gScore = new Dictionary<int, int>();
            var closed = new HashSet<int>();
            int start = LegacyKey(sx, sz), end = LegacyKey(ex, ez);
            gScore[start] = 0;
            open.Enqueue(start, LegacyHeuristic(sx, sz, ex, ez));
            while (open.Count > 0)
            {
                int current = open.Dequeue();
                if (current == end) return LegacyReconstruct(cameFrom, current);
                closed.Add(current);
                int cx = current / GridSize, cz = current % GridSize;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = cx + dx, nz = cz + dz;
                        if (IsBlocked(nx, nz)) continue;
                        if (dx != 0 && dz != 0 && (IsBlocked(cx + dx, cz) || IsBlocked(cx, cz + dz))) continue;
                        int neighbor = LegacyKey(nx, nz);
                        if (closed.Contains(neighbor)) continue;
                        int stepCost = (dx != 0 && dz != 0) ? 14 : 10;
                        int tentativeG = gScore[current] + stepCost;
                        if (!gScore.TryGetValue(neighbor, out int existing) || tentativeG < existing)
                        {
                            cameFrom[neighbor] = current;
                            gScore[neighbor] = tentativeG;
                            open.Enqueue(neighbor, tentativeG + LegacyHeuristic(nx, nz, ex, ez));
                        }
                    }
            }
            return new List<(int, int)>();
        }

        private int LegacyKey(int x, int z) => x * GridSize + z;
        private static int LegacyHeuristic(int sx, int sz, int ex, int ez)
        {
            int dx = Math.Abs(sx - ex), dz = Math.Abs(sz - ez);
            return 10 * (dx + dz) + 4 * Math.Min(dx, dz);
        }
        private List<(int x, int z)> LegacyReconstruct(Dictionary<int, int> cameFrom, int current)
        {
            var path = new List<(int, int)>();
            while (cameFrom.ContainsKey(current))
            {
                path.Add((current / GridSize, current % GridSize));
                current = cameFrom[current];
            }
            path.Reverse();
            return path;
        }

        private void RasterizeStaticToLegacyGrid(StaticShape s)
        {
            // Approximate the OBB by its AABB on the legacy grid (good enough for A* routing).
            var bb = Geometry.GetHalfBoundingBox(s.U, s.V, new FixedVector2D(s.Hw, s.Hh));
            BlockCircle(s.X.ToFloat(), s.Z.ToFloat(), Math.Max(bb.X.ToFloat(), bb.Y.ToFloat()));
        }

        private void UnrasterizeStaticFromLegacyGrid(StaticShape s)
        {
            var bb = Geometry.GetHalfBoundingBox(s.U, s.V, new FixedVector2D(s.Hw, s.Hh));
            UnblockCircle(s.X.ToFloat(), s.Z.ToFloat(), Math.Max(bb.X.ToFloat(), bb.Y.ToFloat()));
        }

        private sealed class LegacyPriorityQueue
        {
            private readonly List<(int item, int priority)> _items = new();
            public int Count => _items.Count;
            public void Enqueue(int item, int priority) => _items.Add((item, priority));
            public int Dequeue()
            {
                int bestIdx = 0;
                for (int i = 1; i < _items.Count; i++)
                    if (_items[i].priority < _items[bestIdx].priority) bestIdx = i;
                int item = _items[bestIdx].item;
                _items.RemoveAt(bestIdx);
                return item;
            }
        }
    }
}
