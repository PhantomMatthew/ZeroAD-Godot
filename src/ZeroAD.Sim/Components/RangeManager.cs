using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Components
{
    /// <summary>
    /// Compact per-entity record kept by <see cref="RangeManager"/> for range queries. Mirrors
    /// <c>EntityData</c> from <c>CCmpRangeManager.cpp</c> — only the fields needed when LOS is off
    /// (everything visible): XZ position, owner, obstruction size (so queries can account for it).
    /// </summary>
    public struct RangeEntityData
    {
        public Fixed X, Z;
        public int Owner;       // -1 = no owner, 0 = gaia, 1+ = players
        public int Size;        // obstruction radius as int (internal Fixed units), for accountForSize
        public bool InWorld;    // has a PositionComponent and is placed
    }

    /// <summary>
    /// Spatial hash grid tuned for range queries: each item is stored in the single cell its center
    /// falls into (NOT copied across an AABB, unlike <see cref="SpatialSubdivision"/>), and items
    /// larger than a cell go into a global "oversized" list. Queries scan the cells in their AABB
    /// plus the oversized list, then the caller does precise distance filtering. Ported from
    /// <c>FastSpatialSubdivision</c> in <c>source/simulation2/helpers/Spatial.h</c>.
    ///
    /// This trades precision for cheap add/remove (vital: units move every tick, so the index is
    /// mutated far more than it's queried).
    /// </summary>
    public sealed class FastSpatialSubdivision
    {
        private const int SubdivisionSize = 20; // meters, matching the original constant

        private readonly List<EntityId>[] _cells;
        private readonly List<EntityId> _oversized = new();
        private readonly int _width;
        private readonly int _height;

        public FastSpatialSubdivision(Fixed maxX, Fixed maxZ)
        {
            _width = Math.Max(1, (maxX / Fixed.FromInt(SubdivisionSize)).ToIntRoundToInfinity());
            _height = Math.Max(1, (maxZ / Fixed.FromInt(SubdivisionSize)).ToIntRoundToInfinity());
            _cells = new List<EntityId>[_width * _height];
            for (int i = 0; i < _cells.Length; i++) _cells[i] = new List<EntityId>();
        }

        private int CellX(Fixed x) => Math.Clamp((x / Fixed.FromInt(SubdivisionSize)).ToIntRoundToZero(), 0, _width - 1);
        private int CellZ(Fixed z) => Math.Clamp((z / Fixed.FromInt(SubdivisionSize)).ToIntRoundToZero(), 0, _height - 1);

        public void Add(EntityId item, Fixed x, Fixed z, Fixed size)
        {
            // Oversized (larger than one cell) go to the global list so a query always finds them.
            if (size >= Fixed.FromInt(SubdivisionSize))
            {
                _oversized.Add(item);
                return;
            }
            _cells[CellX(x) + CellZ(z) * _width].Add(item);
        }

        public void Remove(EntityId item, Fixed x, Fixed z, Fixed size)
        {
            if (size >= Fixed.FromInt(SubdivisionSize))
            {
                _oversized.Remove(item);
                return;
            }
            var list = _cells[CellX(x) + CellZ(z) * _width];
            list.Remove(item);
        }

        public void Move(EntityId item, Fixed fromX, Fixed fromZ, Fixed toX, Fixed toZ, Fixed size)
        {
            if (size >= Fixed.FromInt(SubdivisionSize)) return; // oversized stays in the global list
            int c0 = CellX(fromX) + CellZ(fromZ) * _width;
            int c1 = CellX(toX) + CellZ(toZ) * _width;
            if (c0 == c1) return;
            _cells[c0].Remove(item);
            _cells[c1].Add(item);
        }

        /// <summary>Collect candidate entities whose centers fall in the AABB [minX,minZ]-[maxX,maxZ],
        /// plus all oversized entities. Over-approximates — caller does precise distance test.</summary>
        public void Collect(List<EntityId> output, Fixed minX, Fixed minZ, Fixed maxX, Fixed maxZ)
        {
            output.Clear();
            int ix0 = Math.Clamp((minX / Fixed.FromInt(SubdivisionSize)).ToIntRoundToInfinity() - 1, 0, _width - 1);
            int iz0 = Math.Clamp((minZ / Fixed.FromInt(SubdivisionSize)).ToIntRoundToInfinity() - 1, 0, _height - 1);
            int ix1 = Math.Clamp((maxX / Fixed.FromInt(SubdivisionSize)).ToIntRoundToNegInfinity() + 1, 0, _width - 1);
            int iz1 = Math.Clamp((maxZ / Fixed.FromInt(SubdivisionSize)).ToIntRoundToNegInfinity() + 1, 0, _height - 1);
            for (int z = iz0; z <= iz1; z++)
                for (int x = ix0; x <= ix1; x++)
                    output.AddRange(_cells[x + z * _width]);
            output.AddRange(_oversized);
        }
    }

    /// <summary>
    /// System-level range/visibility service. Mirrors <c>CCmpRangeManager</c> with the LOS (fog-of-war)
    /// subsystem stripped: every entity is visible to every player, so queries are pure spatial lookups.
    ///
    /// Maintains a <see cref="FastSpatialSubdivision"/> indexed by entity center, fed by subscribing
    /// to the ComponentManager's position/creation/destruction/ownership notifications. Consumers
    /// (AI, combat targeting, gathering, build-distance checks) call <see cref="ExecuteQuery"/> /
    /// <see cref="GetEntitiesByPlayer"/> instead of the legacy linear scans in SimBridge.
    /// </summary>
    public sealed class RangeManager
    {
        private readonly ComponentManager _cm;
        private readonly Dictionary<EntityId, RangeEntityData> _data = new();
        private readonly FastSpatialSubdivision _subdivision;
        // Reused scratch buffer — never exposed across calls.
        private readonly List<EntityId> _scratch = new();

        public RangeManager(ComponentManager cm, Fixed maxX, Fixed maxZ)
        {
            _cm = cm;
            _subdivision = new FastSpatialSubdivision(maxX, maxZ);
            cm.EntityCreated += OnEntityCreated;
            cm.EntityDestroyed += OnEntityDestroyed;
            cm.PositionChanged += OnPositionChanged;
            cm.OwnerChanged += OnOwnerChanged;
        }

        private void OnEntityCreated(EntityId entity)
        {
            if (_data.ContainsKey(entity)) return;
            _data[entity] = new RangeEntityData { Owner = -1, InWorld = false };
            // Resolve initial owner + position if the entity already has those components.
            RefreshFromComponents(entity);
        }

        private void OnEntityDestroyed(EntityId entity)
        {
            if (!_data.TryGetValue(entity, out var d)) return;
            if (d.InWorld)
            {
                var size = Fixed.FromInt(0).WithInternalValue(d.Size);
                _subdivision.Remove(entity, d.X, d.Z, size);
            }
            _data.Remove(entity);
        }

        private void OnPositionChanged(EntityId entity, FixedVector2D from, FixedVector2D to)
        {
            if (!_data.TryGetValue(entity, out var d)) return;
            var size = Fixed.FromInt(0).WithInternalValue(d.Size);
            if (!d.InWorld)
            {
                d.X = to.X; d.Z = to.Y; d.InWorld = true;
                _subdivision.Add(entity, d.X, d.Z, size);
            }
            else
            {
                _subdivision.Move(entity, d.X, d.Z, to.X, to.Y, size);
                d.X = to.X; d.Z = to.Y;
            }
            _data[entity] = d;
        }

        private void OnOwnerChanged(EntityId entity, int from, int to)
        {
            if (_data.TryGetValue(entity, out var d))
            {
                d.Owner = to;
                _data[entity] = d;
            }
        }

        /// <summary>Re-read owner + obstruction size from the live components. Call after assembling
        /// an entity (e.g. when OwnershipComponent/ObstructionComponent are added post-creation).</summary>
        public void RefreshFromComponents(EntityId entity)
        {
            if (!_data.TryGetValue(entity, out var d)) return;
            var own = _cm.QueryInterface<OwnershipComponent>(entity);
            d.Owner = own?.PlayerId ?? -1;
            var obs = _cm.QueryInterface<ObstructionComponent>(entity);
            d.Size = obs?.GetSize().InternalValue ?? 0;
            var pos = _cm.QueryInterface<PositionComponent>(entity);
            if (pos != null && !d.InWorld)
            {
                d.X = pos.Position.X; d.Z = pos.Position.Z; d.InWorld = true;
                _subdivision.Add(entity, d.X, d.Z, Fixed.Zero.WithInternalValue(d.Size));
            }
            _data[entity] = d;
        }

        /// <summary>
        /// Return entities within [minRange, maxRange] of <paramref name="source"/> that pass
        /// <paramref name="predicate"/>. Mirrors <c>CCmpRangeManager::ExecuteQuery</c> minus the
        /// owner/interface masks (expressed via the predicate here for flexibility).
        /// </summary>
        public List<EntityId> ExecuteQuery(EntityId source, Fixed minRange, Fixed maxRange,
            Func<EntityId, bool>? predicate = null)
        {
            var result = new List<EntityId>();
            if (!_data.TryGetValue(source, out var src)) return result;
            var srcPos = new FixedVector2D(src.X, src.Z);

            // AABB pre-filter from the spatial index, then precise circular distance test.
            Fixed r = maxRange;
            _subdivision.Collect(_scratch, src.X - r, src.Z - r, src.X + r, src.Z + r);
            foreach (var eid in _scratch)
            {
                if (eid == source) continue;
                if (!_data.TryGetValue(eid, out var d) || !d.InWorld) continue;
                var rel = new FixedVector2D(d.X - src.X, d.Z - src.Z);
                int cmp = rel.CompareLength(maxRange);
                if (cmp > 0) continue;                      // beyond maxRange
                if (minRange > Fixed.Zero && rel.CompareLength(minRange) < 0) continue; // inside minRange
                if (predicate != null && !predicate(eid)) continue;
                result.Add(eid);
            }
            // Stable, deterministic order: sort by entity id so results don't depend on hash order.
            result.Sort((a, b) => a.Value.CompareTo(b.Value));
            return result;
        }

        /// <summary>Return all entities owned by <paramref name="playerId"/> (0 = gaia).</summary>
        public List<EntityId> GetEntitiesByPlayer(int playerId)
        {
            var result = new List<EntityId>();
            foreach (var kvp in _data)
                if (kvp.Value.Owner == playerId && kvp.Value.InWorld)
                    result.Add(kvp.Key);
            result.Sort((a, b) => a.Value.CompareTo(b.Value));
            return result;
        }

        /// <summary>Return all non-gaia, owned entities (players 1+).</summary>
        public List<EntityId> GetNonGaiaEntities()
        {
            var result = new List<EntityId>();
            foreach (var kvp in _data)
                if (kvp.Value.Owner > 0 && kvp.Value.InWorld)
                    result.Add(kvp.Key);
            result.Sort((a, b) => a.Value.CompareTo(b.Value));
            return result;
        }
    }
}
