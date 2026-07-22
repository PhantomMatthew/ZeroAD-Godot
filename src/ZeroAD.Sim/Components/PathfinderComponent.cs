using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;

namespace ZeroAD.Sim.Components
{
    /// <summary>Placement check result, matching <c>ICmpObstruction::EFoundationCheck</c> semantics.</summary>
    public enum PlacementResult
    {
        Success,
        FailOutOfBounds,
        FailTerrain,            // water / cliff tile under the footprint
        FailObstructsFoundation // overlaps another foundation-blocking shape
    }

    /// <summary>
    /// The pathfinder service: placement checks (existing) + the M3 three-engine pathfinding
    /// pipeline (new). Owns the passability grid, hierarchical connectivity, long-range (A*) and
    /// short-range (vertex) pathfinders. Registered as <see cref="SimSystem.Pathfinder"/> and
    /// globally reachable. Placement methods (<see cref="CheckUnitPlacement"/>/
    /// <see cref="CheckBuildingPlacement"/>) are unchanged.
    /// </summary>
    public sealed class PathfinderComponent
    {
        private readonly ComponentManager _cm;

        // --- M3 pathfinding pipeline ---
        private readonly PassabilityGridBuilder _gridBuilder = new();
        private readonly HierarchicalPathfinder _hier = new();
        private readonly LongPathfinder _long = new();
        private readonly VertexPathfinder _vertex = new();

        public PassabilityClassDef DefaultClass => _gridBuilder.Default;
        public PassabilityClassDef ShipClass => _gridBuilder.Ship;

        public PathfinderComponent(ComponentManager cm) => _cm = cm;

        /// <summary>Resolve the system terrain component (single instance expected on the world entity).</summary>
        private TerrainComponent? Terrain => _terrain;
        private TerrainComponent? _terrain;
        public void SetTerrain(TerrainComponent terrain) => _terrain = terrain;

        private ObstructionManager? Obstructions => SimSystem.Obstructions;

        /// <summary>
        /// Check placing a unit circle at (x,z) with <paramref name="clearance"/> against terrain +
        /// obstructions. <paramref name="skipTag"/> optionally excludes one shape (e.g. the entity's
        /// own when it's relocating). Mirrors <c>CCmpPathfinder::CheckUnitPlacement</c> minus the
        /// per-passability-class grid (we use one Land/Water grid).
        /// </summary>
        public PlacementResult CheckUnitPlacement(Fixed x, Fixed z, Fixed clearance, ObstructionTag? skipTag = null)
        {
            if (Terrain != null && !Terrain.IsInBounds(new FixedVector2D(x, z)))
                return PlacementResult.FailOutOfBounds;
            if (Terrain != null && !Terrain.IsLand(x, z))
                return PlacementResult.FailTerrain;

            var mgr = Obstructions;
            if (mgr != null)
            {
                ObstructionShapeFilter filter = (tag, flags, _, _) =>
                    (flags & ObstructionFlags.BlockFoundation) == 0 || (skipTag.HasValue && tag == skipTag.Value);
                var hits = mgr.TestUnitShape(filter, x, z, clearance);
                if (hits.Count > 0) return PlacementResult.FailObstructsFoundation;
            }
            return PlacementResult.Success;
        }

        /// <summary>
        /// Check placing an axis-aligned building footprint at (x,z) with half-size (hw,hh) against
        /// terrain + obstructions. Mirrors <c>CCmpPathfinder::CheckBuildingPlacement</c>.
        /// </summary>
        public PlacementResult CheckBuildingPlacement(Fixed x, Fixed z, Fixed hw, Fixed hh, ObstructionTag? skipTag = null)
        {
            if (Terrain != null)
            {
                if (!Terrain.IsInBounds(new FixedVector2D(x - hw, z - hh)) ||
                    !Terrain.IsInBounds(new FixedVector2D(x + hw, z + hh)))
                    return PlacementResult.FailOutOfBounds;
                if (!Terrain.IsFootprintOnLand(x, z, hw, hh))
                    return PlacementResult.FailTerrain;
            }

            var mgr = Obstructions;
            if (mgr != null)
            {
                FixedVector2D u = new(Fixed.FromInt(1), Fixed.Zero);
                FixedVector2D v = new(Fixed.Zero, Fixed.FromInt(1));
                ObstructionShapeFilter filter = (tag, flags, _, _) =>
                    (flags & ObstructionFlags.BlockFoundation) == 0 || (skipTag.HasValue && tag == skipTag.Value);
                var hits = mgr.TestStaticShape(filter, x, z, u, v, hw, hh);
                if (hits.Count > 0) return PlacementResult.FailObstructsFoundation;
            }
            return PlacementResult.Success;
        }

        // --- M3 pathfinding ---

        /// <summary>Rebuild the passability grid + hierarchical connectivity + long pathfinder
        /// from the current terrain and obstructions. Call after map load and whenever
        /// obstructions change (P0: full rebuild each time; incremental is P1).</summary>
        public void RebuildGrid()
        {
            if (Terrain == null || Obstructions == null) return;

            int tiles = Terrain.MapSize;
            // Guard against pathological map sizes that would explode the navcell grid. The
            // original caps at 256 tiles/side; if MapSize is unreasonable (e.g. terrain wasn't
            // Configure'd before RebuildGrid, or a PMP parse returned garbage), skip the build
            // rather than allocate gigabytes.
            int navcellsPerSide = tiles * PathfindingCore.NavcellsPerTerrainTile;
            if (tiles <= 0 || tiles > 512 || navcellsPerSide > 2048)
            {
                System.Console.WriteLine($"[Pathfinder] RebuildGrid skipped: tiles={tiles} (navcells/side={navcellsPerSide}, limit 2048)");
                return;
            }

            float ts = Terrain.TileSize;
            // Derive per-tile terrain info from TerrainComponent's land/water grid. Slope/depth
            // detail isn't available yet (the PMP passability is baked into TerrainClass), so we
            // map class → approximate depth: land=0, water=deep, impassable=cliff.
            var terrain = new TerrainTileInfo[tiles, tiles];
            for (int j = 0; j < tiles; j++)
                for (int i = 0; i < tiles; i++)
                {
                    // Sample the terrain class at the tile's centre (world coords).
                    var cls = Terrain.GetClass(
                        Fixed.FromFloat(i * ts + ts * 0.5f),
                        Fixed.FromFloat(j * ts + ts * 0.5f));
                    terrain[i, j] = cls switch
                    {
                        TerrainClass.Land => new TerrainTileInfo(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                        TerrainClass.Water => new TerrainTileInfo(Fixed.FromInt(5), Fixed.Zero, Fixed.Zero),
                        _ => new TerrainTileInfo(Fixed.Zero, Fixed.FromInt(2), Fixed.Zero), // Impassable = steep cliff
                    };
                }

            _gridBuilder.Build(terrain, tiles, Obstructions.GetAllStaticObstructions());
            if (_gridBuilder.Grid != null)
            {
                _hier.Recompute(_gridBuilder.Grid, _gridBuilder.AllClasses);
                _long.Reload(_gridBuilder.Grid);
            }
        }

        /// <summary>Compute a long-range path from a world position to a goal. Returns waypoints
        /// (world-space) or an empty path if no route exists. Uses the default (land) class.</summary>
        public WaypointPath ComputePath(FixedVector2D start, in PathGoal goal)
            => ComputePath(start, goal, _gridBuilder.Default.Mask);

        /// <summary>Compute a long-range path for a specific passability class.</summary>
        public WaypointPath ComputePath(FixedVector2D start, in PathGoal goal, PassClass passClass)
        {
            var empty = new WaypointPath();
            if (_gridBuilder.Grid == null) return empty;
            int si = PathfindingCore.WorldToNavcell(start.X);
            int sj = PathfindingCore.WorldToNavcell(start.Y);
            return _long.ComputePath(_hier, si, sj, goal, passClass);
        }

        /// <summary>Compute a short-range path that routes precisely around nearby obstructions.
        /// Used for local detours / unit avoidance.</summary>
        public WaypointPath ComputeShortPath(FixedVector2D start, in PathGoal goal,
            Fixed clearance, Fixed range, PassClass passClass, bool avoidMovingUnits = false)
        {
            // P0: gather all static obstructions (range-filtering is a refinement; at P0 map
            // sizes the vertex graph stays small). Moving-unit avoidance is a P1 add.
            System.Collections.Generic.List<ObstructionSquare> obstructions =
                Obstructions?.GetAllStaticObstructions()
                ?? new System.Collections.Generic.List<ObstructionSquare>();
            return _vertex.ComputeShortPath(start, goal, clearance, range, obstructions);
        }

        /// <summary>True if a straight line between two world points is unobstructed (no impassable
        /// navcell crossed). Mirrors CCmpPathfinder::CheckMovement.</summary>
        public bool CheckMovement(FixedVector2D from, FixedVector2D to, PassClass passClass)
        {
            if (_gridBuilder.Grid == null) return true;
            int i0 = PathfindingCore.WorldToNavcell(from.X);
            int j0 = PathfindingCore.WorldToNavcell(from.Y);
            int i1 = PathfindingCore.WorldToNavcell(to.X);
            int j1 = PathfindingCore.WorldToNavcell(to.Y);
            return _long.CheckLineMovement(i0, j0, i1, j1, passClass);
        }
    }
}
