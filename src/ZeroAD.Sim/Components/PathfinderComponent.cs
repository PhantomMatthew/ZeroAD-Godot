using System;
using ZeroAD.Sim.Maths;

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
    /// Minimal pathfinder/placement service. The full hierarchical + vertex pathfinder (M3) isn't
    /// ported yet; this component only provides placement checks that <see cref="BuildRestrictionsComponent"/>
    /// and <see cref="FootprintComponent"/> need to validate where units/buildings can go:
    ///   1. Is the footprint inside the map bounds?
    ///   2. Is the terrain under it passable (land, not water/cliff)?
    ///   3. Does it overlap any foundation-blocking obstruction?
    ///
    /// Pathfinding for unit movement still uses <see cref="ObstructionManager"/>'s legacy A* grid
    /// until the proper pathfinder lands; this class does NOT replace that.
    /// </summary>
    public sealed class PathfinderComponent
    {
        private readonly ComponentManager _cm;

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
    }
}
