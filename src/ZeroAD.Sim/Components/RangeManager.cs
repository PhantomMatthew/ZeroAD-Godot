using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Components
{
    /// <summary>Per-entity visibility for one player. Values mirror the original's
    /// LosVisibility enum (HIDDEN/FOGGED/VISIBLE) and fit the 2-bit per-player cache.</summary>
    public enum LosVisibility : byte { Hidden = 0, Fogged = 1, Visible = 2 }

    /// <summary>
    /// Compact per-entity record kept by <see cref="RangeManager"/>. Mirrors
    /// <c>EntityData</c> from <c>CCmpRangeManager.cpp</c> (24 bytes there): XZ position, owner,
    /// obstruction size, vision range, per-player 2-bit visibility cache, and flags.
    /// </summary>
    public struct RangeEntityData
    {
        public Fixed X, Z;
        public int Owner;       // -1 = no owner, 0 = gaia, 1+ = players
        public int Size;        // obstruction radius as int (internal Fixed units), for accountForSize
        public bool InWorld;    // has a PositionComponent and is placed
        public Fixed VisionRange;   // 0 = not a seer
        public uint Visibilities;   // per-player 2-bit LosVisibility cache (Task 3)
        public byte Flags;          // bit0 RetainInFog, bit1 IsMirage (Task 5)
        public bool LosAdded;       // vision circle currently counted in the LOS grid

        public const byte FlagRetainInFog = 1;
        public const byte FlagIsMirage = 2;
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
        private FastSpatialSubdivision _subdivision;
        // Reused scratch buffer — never exposed across calls.
        private readonly List<EntityId> _scratch = new();

        /// <summary>Per-player LOS grids. Rebuilt by <see cref="SetBounds"/> on map load.</summary>
        public LosGrid Los { get; private set; }

        // --- Visibility (fog-of-war) state ---
        // Players whose LOS grid changed this turn (bit p-1 set): all entities re-evaluated.
        private uint _playerLosDirtyMask;
        // Entities that moved or were placed this turn: re-evaluated for every player.
        private readonly HashSet<EntityId> _movedOrPlacedEntities = new();
        // Explicit re-evaluation requests (e.g. freshly spawned mirages).
        private readonly HashSet<EntityId> _requestedVisibilityUpdates = new();
        private readonly bool[] _revealAll = new bool[LosGrid.MaxPlayers + 1];

        public RangeManager(ComponentManager cm, Fixed maxX, Fixed maxZ)
        {
            _cm = cm;
            _subdivision = new FastSpatialSubdivision(maxX, maxZ);
            Los = new LosGrid(maxX.ToIntRoundToNearest());
            cm.EntityCreated += OnEntityCreated;
            cm.EntityDestroyed += OnEntityDestroyed;
            cm.PositionChanged += OnPositionChanged;
            cm.OwnerChanged += OnOwnerChanged;
        }

        /// <summary>Resize the world after loading a real map (the constructor default is 256m,
        /// but e.g. the tutorial map is 768m). Rebuilds the spatial index and LOS grid from the
        /// current entity data, in sorted entity order for determinism.</summary>
        public void SetBounds(Fixed worldMeters)
        {
            _subdivision = new FastSpatialSubdivision(worldMeters, worldMeters);
            Los = new LosGrid(worldMeters.ToIntRoundToNearest());
            _playerLosDirtyMask = 0xFFFF; // everything re-evaluated against the fresh grid
            var keys = new List<EntityId>(_data.Keys);
            keys.Sort((a, b) => a.Value.CompareTo(b.Value));
            foreach (var eid in keys)
            {
                var d = _data[eid];
                d.LosAdded = false;
                _data[eid] = d;
                if (!d.InWorld) continue;
                _subdivision.Add(eid, d.X, d.Z, Fixed.Zero.WithInternalValue(d.Size));
                SyncLos(eid, d);
            }
        }

        // --- LOS bookkeeping ---

        private static uint DirtyBit(int player) => 1u << (player - 1);

        /// <summary>After a full-state load (LosGrid.Deserialize restored the state words
        /// and zeroed the counts): re-apply the reveal-all mask, re-add every live seer's
        /// circle in sorted order (deterministic count rebuild), and mark all players dirty
        /// so the next UpdateVisibilityData recomputes every cached visibility.</summary>
        public void RebuildLosAfterLoad(uint revealAllMask)
        {
            for (int p = 1; p <= LosGrid.MaxPlayers; p++)
                _revealAll[p] = (revealAllMask & DirtyBit(p)) != 0;
            Los.RebuildCountsClear();
            _playerLosDirtyMask = 0xFFFF;
            _movedOrPlacedEntities.Clear();
            _requestedVisibilityUpdates.Clear();
            var keys = new List<EntityId>(_data.Keys);
            keys.Sort((a, b) => a.Value.CompareTo(b.Value));
            foreach (var eid in keys)
            {
                var d = _data[eid];
                d.LosAdded = false;
                _data[eid] = d;
                SyncLos(eid, d);
            }
        }

        /// <summary>Bring the LOS grid in line with the entity's desired seer state:
        /// counted iff in-world, owned by a real player, and has a vision range.</summary>
        private void SyncLos(EntityId entity, RangeEntityData d)
        {
            bool want = d.InWorld && d.Owner > 0 && d.VisionRange > Fixed.Zero;
            if (want && !d.LosAdded)
            {
                Los.AddLos(d.Owner, d.X, d.Z, d.VisionRange);
                d.LosAdded = true;
                _playerLosDirtyMask |= DirtyBit(d.Owner);
            }
            else if (!want && d.LosAdded)
            {
                Los.RemoveLos(d.Owner, d.X, d.Z, d.VisionRange);
                d.LosAdded = false;
                _playerLosDirtyMask |= DirtyBit(d.Owner);
            }
            _data[entity] = d;
        }

        /// <summary>Effective vision range changed (tech/aura via the modifiers pipeline):
        /// re-cover with the new range. Mirrors MT_VisionRangeChanged → LosRemove(old)+LosAdd(new).
        /// No-op when the range didn't actually change, so callers can re-apply freely.</summary>
        public void OnVisionRangeChanged(EntityId entity, Fixed newRange)
        {
            if (!_data.TryGetValue(entity, out var d)) return;
            if (d.VisionRange == newRange) return;
            if (d.LosAdded)
            {
                Los.RemoveLos(d.Owner, d.X, d.Z, d.VisionRange);
                _playerLosDirtyMask |= DirtyBit(d.Owner);
                d.LosAdded = false;
            }
            d.VisionRange = newRange;
            _data[entity] = d;
            SyncLos(entity, d);
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
            // Fogging cleanup first (mirrors MT_OwnershipChanged to=-1 on death): hidden
            // mirages die with the parent, fogged ones are orphaned. Needs _data intact.
            _cm.QueryInterface<FoggingComponent>(entity)?.OnOwnershipChanged(d.Owner, -1, _cm, this);
            if (d.LosAdded)
            {
                Los.RemoveLos(d.Owner, d.X, d.Z, d.VisionRange);
                _playerLosDirtyMask |= DirtyBit(d.Owner);
            }
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
                _movedOrPlacedEntities.Add(entity);
                SyncLos(entity, d);
                return;
            }
            _subdivision.Move(entity, d.X, d.Z, to.X, to.Y, size);
            if (d.LosAdded)
            {
                Los.MoveLos(d.Owner, d.X, d.Z, to.X, to.Y, d.VisionRange);
                _playerLosDirtyMask |= DirtyBit(d.Owner);
            }
            d.X = to.X; d.Z = to.Y;
            _data[entity] = d;
            _movedOrPlacedEntities.Add(entity);
        }

        private void OnOwnerChanged(EntityId entity, int from, int to)
        {
            if (!_data.TryGetValue(entity, out var d)) return;
            // Fogging activation/cleanup hook (mirrors Fogging.js OnOwnershipChanged).
            _cm.QueryInterface<FoggingComponent>(entity)?.OnOwnershipChanged(from, to, _cm, this);
            // OwnerChanged while counted under a stale owner: drop that owner's circle
            // first (the new owner's circle is added by SyncLos below if still a valid
            // seer). Uses d.Owner — the owner the circle was actually counted under —
            // not `from`, so a Refresh that already updated the owner can't double-add.
            if (d.LosAdded && d.Owner != to)
            {
                if (d.Owner > 0)
                {
                    Los.RemoveLos(d.Owner, d.X, d.Z, d.VisionRange);
                    _playerLosDirtyMask |= DirtyBit(d.Owner);
                }
                d.LosAdded = false;
            }
            d.Owner = to;
            _data[entity] = d;
            _movedOrPlacedEntities.Add(entity); // ownership affects every player's chain
            SyncLos(entity, d);
        }

        /// <summary>Re-read owner + obstruction size + vision range from the live components.
        /// Call after assembling an entity (e.g. when OwnershipComponent/ObstructionComponent
        /// are added post-creation).</summary>
        public void RefreshFromComponents(EntityId entity)
        {
            if (!_data.TryGetValue(entity, out var d)) return;
            var own = _cm.QueryInterface<OwnershipComponent>(entity);
            d.Owner = own?.PlayerId ?? -1;
            var obs = _cm.QueryInterface<ObstructionComponent>(entity);
            d.Size = obs?.GetSize().InternalValue ?? 0;
            var vis = _cm.QueryInterface<VisionComponent>(entity);
            d.VisionRange = vis == null
                ? Fixed.Zero
                : ValueModificationApplier.EffectiveVisionRange(_cm, entity, vis);
            // Fog-of-war flags from components (mirrors m_EntityData flag fill in the
            // original, which reads them off ICmpVisibility / ICmpMirage).
            var visib = _cm.QueryInterface<VisibilityComponent>(entity);
            if (visib?.RetainInFog == true)
                d.Flags |= RangeEntityData.FlagRetainInFog;
            else
                d.Flags &= unchecked((byte)~RangeEntityData.FlagRetainInFog);
            if (_cm.QueryInterface<MirageComponent>(entity) != null)
                d.Flags |= RangeEntityData.FlagIsMirage;
            var pos = _cm.QueryInterface<PositionComponent>(entity);
            if (pos != null && !d.InWorld)
            {
                d.X = pos.Position.X; d.Z = pos.Position.Z; d.InWorld = true;
                _subdivision.Add(entity, d.X, d.Z, Fixed.Zero.WithInternalValue(d.Size));
            }
            _data[entity] = d;
            SyncLos(entity, d);
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

        // --- Per-turn visibility (ported from CCmpRangeManager's UpdateVisibilityData) ---

        /// <summary>Set RetainInFog / IsMirage flags on a tracked entity (assembly-time).</summary>
        public void SetEntityFlags(EntityId entity, byte flags)
        {
            if (!_data.TryGetValue(entity, out var d)) return;
            d.Flags = flags;
            _data[entity] = d;
        }

        /// <summary>Cached per-player visibility of an entity (HIDDEN when untracked).</summary>
        public LosVisibility GetLosVisibility(EntityId ent, int player) =>
            _data.TryGetValue(ent, out var d) ? GetCachedVis(d.Visibilities, player) : LosVisibility.Hidden;

        /// <summary>Visibility of an arbitrary world position (grid lookup only, no entity logic).
        /// Mirrors ICmpRangeManager::GetLosVisibilityPosition.</summary>
        public LosVisibility GetLosVisibilityPosition(Fixed x, Fixed z, int player)
        {
            var (i, j) = Los.WorldToVertex(x, z);
            if (_revealAll[player] || Los.IsVisible(player, i, j)) return LosVisibility.Visible;
            return Los.IsExplored(player, i, j) ? LosVisibility.Fogged : LosVisibility.Hidden;
        }

        /// <summary>Reveal the whole map for a player (debug/spectator). Mirrors SetLosRevealAll.</summary>
        public void SetLosRevealAll(int player, bool enabled)
        {
            if (player < 1 || player > LosGrid.MaxPlayers || _revealAll[player] == enabled) return;
            _revealAll[player] = enabled;
            _playerLosDirtyMask |= DirtyBit(player);
        }

        public bool GetLosRevealAll(int player) => _revealAll[player];

        public int GetPercentMapExplored(int player) => Los.GetPercentExplored(player);

        /// <summary>Force re-evaluation of an entity next turn (mirage spawns use this).</summary>
        public void RequestVisibilityUpdate(EntityId ent) => _requestedVisibilityUpdates.Add(ent);

        /// <summary>Per-turn visibility pass. Re-evaluates entities whose inputs changed
        /// (players with LOS grid changes → all entities; moved/placed/requested entities →
        /// all players), fires VisibilityChangedEvent on transitions, and notifies Fogging.
        /// Standing game with no movement costs nothing. Called once per sim turn by SimBridge.</summary>
        public void UpdateVisibilityData()
        {
            if (_playerLosDirtyMask == 0 && _movedOrPlacedEntities.Count == 0
                && _requestedVisibilityUpdates.Count == 0)
                return;

            uint dirtyMask = _playerLosDirtyMask;
            _playerLosDirtyMask = 0;

            if (dirtyMask != 0)
            {
                var ents = new List<EntityId>(_data.Keys);
                ents.Sort((a, b) => a.Value.CompareTo(b.Value));
                for (int p = 1; p <= LosGrid.MaxPlayers; p++)
                {
                    if ((dirtyMask & DirtyBit(p)) == 0) continue;
                    foreach (var e in ents)
                        EvaluateVisibility(e, p);
                }
            }

            if (_movedOrPlacedEntities.Count > 0 || _requestedVisibilityUpdates.Count > 0)
            {
                var set = new HashSet<EntityId>(_movedOrPlacedEntities);
                set.UnionWith(_requestedVisibilityUpdates);
                _movedOrPlacedEntities.Clear();
                _requestedVisibilityUpdates.Clear();
                var ents = new List<EntityId>(set);
                ents.Sort((a, b) => a.Value.CompareTo(b.Value));

                var players = new List<int>(_cm.Players.GetNonGaiaPlayerIds());
                players.Sort();
                foreach (var e in ents)
                    foreach (var p in players)
                        EvaluateVisibility(e, p);
            }
        }

        private void EvaluateVisibility(EntityId e, int player)
        {
            if (!_data.TryGetValue(e, out var d)) return;
            var newVis = ComputeLosVisibility(e, d, player);
            var oldVis = GetCachedVis(d.Visibilities, player);
            if (newVis == oldVis) return;
            d.Visibilities = SetCachedVis(d.Visibilities, player, newVis);
            _data[e] = d;
            _cm.Events.RaiseVisibilityChanged(new Events.VisibilityChangedEvent
            {
                Player = player,
                Entity = e,
                Old = oldVis,
                New = newVis
            });
            // Fogging/Mirage lifecycle hooks (an entity carries at most one of the two).
            _cm.QueryInterface<FoggingComponent>(e)?.OnVisibilityChanged(player, newVis, _cm, this);
            _cm.QueryInterface<MirageComponent>(e)?.OnVisibilityChanged(player, newVis, _cm);
        }

        /// <summary>The visibility decision chain, faithfully ported from
        /// CCmpRangeManager::ComputeLosVisibility (lines 1653-1748).</summary>
        private LosVisibility ComputeLosVisibility(EntityId ent, RangeEntityData d, int player)
        {
            // Not placed in the world: never visible.
            if (!d.InWorld) return LosVisibility.Hidden;

            bool isMirage = (d.Flags & RangeEntityData.FlagIsMirage) != 0;
            var mirage = isMirage ? _cm.QueryInterface<MirageComponent>(ent) : null;
            // Mirage entities, whatever the situation, are visible for one specific player.
            if (isMirage && mirage != null && mirage.Player != player)
                return LosVisibility.Hidden;

            var (i, j) = Los.WorldToVertex(d.X, d.Z);

            // Reveal-all: everything real visible, all mirages useless.
            if (_revealAll[player])
                return isMirage ? LosVisibility.Hidden : LosVisibility.Visible;

            if (Los.IsVisible(player, i, j))
                return isMirage ? LosVisibility.Hidden : LosVisibility.Visible;

            if (!Los.IsExplored(player, i, j)) return LosVisibility.Hidden;

            // Explored-but-fogged: only retain-in-fog entities linger.
            if ((d.Flags & RangeEntityData.FlagRetainInFog) == 0) return LosVisibility.Hidden;

            if (isMirage) return LosVisibility.Fogged;

            if (d.Owner < 0) return LosVisibility.Fogged;

            var fogging = _cm.QueryInterface<FoggingComponent>(ent);
            if (d.Owner == player)
                return fogging == null || !fogging.IsMiraged(player)
                    ? LosVisibility.Fogged
                    : LosVisibility.Hidden;

            // Enemy entity in fog: hidden when never scouted or currently mirage-replaced.
            if (fogging != null && fogging.Activated && (!fogging.WasSeen(player) || fogging.IsMiraged(player)))
                return LosVisibility.Hidden;

            return LosVisibility.Fogged;
        }

        private static LosVisibility GetCachedVis(uint vis, int player) =>
            (LosVisibility)(vis >> 2 * (player - 1) & 3);

        private static uint SetCachedVis(uint vis, int player, LosVisibility v)
        {
            int s = 2 * (player - 1);
            return vis & ~(3u << s) | ((uint)v << s);
        }
    }
}
