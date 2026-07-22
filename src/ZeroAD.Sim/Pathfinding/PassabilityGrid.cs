using System.Collections.Generic;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Pathfinding;

// PassabilityGridBuilder — produces the navcell passability grid from terrain info +
// obstruction shapes. Ported from source/simulation2/helpers/Rasterize.cpp (ExpandImpassableCells)
// and CCmpPathfinder::UpdateGrid.
//
// Two passability classes (per the confirmed M3 scope):
//   default (index 0): land units — MaxWaterDepth=2, MaxTerrainSlope=1.0, Clearance=0.8
//   ship    (index 1): ships     — MinWaterDepth=1, Clearance=10
//
// Pipeline:
//   1. Rasterize terrain: for each 4m terrain tile, classify its 4x4 navcell block by the
//      tile's depth/slope. A navcell is impassable for a class if the tile fails that class's
//      terrain rules.
//   2. Stamp obstructions: for each class that stamps, mark navcells covered by static
//      obstruction shapes impassable.
//   3. Expand by clearance: dilate the impassable region by each class's clearance radius
//      (ExpandImpassableCells) so units keep distance from walls/buildings.

/// <summary>Builds and owns the navcell passability grid + the passability-class registry.</summary>
public sealed class PassabilityGridBuilder
{
    /// <summary>The default (land) passability class.</summary>
    public readonly PassabilityClassDef Default;
    /// <summary>The ship (water) passability class.</summary>
    public readonly PassabilityClassDef Ship;

    /// <summary>The current passability grid (navcell → bitmask). Null until Build.</summary>
    public Grid<NavcellData>? Grid { get; private set; }

    /// <summary>Navcells per side (square map). 0 until Build.</summary>
    public int NavcellsPerSide { get; private set; }

    public PassabilityGridBuilder()
    {
        Default = new PassabilityClassDef
        {
            Name = "default",
            Mask = PathfindingCore.PassClassMaskFromIndex(0),
            MaxWaterDepth = Fixed.FromInt(2),
            MaxTerrainSlope = Fixed.FromInt(1),
            Clearance = Fixed.FromFraction(4, 5),     // 0.8
            StampObstructions = true,
        };
        Ship = new PassabilityClassDef
        {
            Name = "ship",
            Mask = PathfindingCore.PassClassMaskFromIndex(1),
            MinWaterDepth = Fixed.FromInt(1),
            Clearance = Fixed.FromInt(10),
            StampObstructions = true,
        };
    }

    /// <summary>All defined classes (for hierarchical/long pathfinder recompute).</summary>
    public IEnumerable<PassabilityClassDef> AllClasses
    {
        get { yield return Default; yield return Ship; }
    }

    public PassabilityClassDef GetClass(PassClass mask) =>
        mask.Mask == Default.Mask.Mask ? Default :
        mask.Mask == Ship.Mask.Mask ? Ship :
        Default;

    /// <summary>Build the passability grid from terrain tile info + obstruction shapes.</summary>
    /// <param name="terrain">Per-tile (4m) depth/slope/shore info, indexed [tileX, tileZ].</param>
    /// <param name="terrainTilesPerSide">Terrain tiles per map side.</param>
    /// <param name="obstructions">Static obstruction squares to stamp (buildings etc.).</param>
    public void Build(TerrainTileInfo[,] terrain, int terrainTilesPerSide,
        IEnumerable<ObstructionSquare> obstructions)
    {
        int nav = terrainTilesPerSide * PathfindingCore.NavcellsPerTerrainTile;
        NavcellsPerSide = nav;
        var grid = new Grid<NavcellData>(nav, nav);

        // --- 1. Rasterize terrain: classify each navcell by its parent terrain tile. ---
        // Every navcell in a 4m tile inherits that tile's depth/slope (the original does the
        // same — terrain is sampled at tile granularity).
        for (int tileZ = 0; tileZ < terrainTilesPerSide; tileZ++)
            for (int tileX = 0; tileX < terrainTilesPerSide; tileX++)
            {
                var info = terrain[tileX, tileZ];
                bool passDefault = Default.TerrainIsPassable(in info);
                bool passShip = Ship.TerrainIsPassable(in info);

                for (int dz = 0; dz < PathfindingCore.NavcellsPerTerrainTile; dz++)
                    for (int dx = 0; dx < PathfindingCore.NavcellsPerTerrainTile; dx++)
                    {
                        int ni = tileX * PathfindingCore.NavcellsPerTerrainTile + dx;
                        int nj = tileZ * PathfindingCore.NavcellsPerTerrainTile + dz;
                        var cell = grid.Get(ni, nj);
                        if (!passDefault) cell = PathfindingCore.MakeImpassable(cell, Default.Mask);
                        if (!passShip) cell = PathfindingCore.MakeImpassable(cell, Ship.Mask);
                        grid.Set(ni, nj, cell);
                    }
            }

        // --- 2. Stamp static obstructions for classes that stamp. ---
        foreach (var ob in obstructions)
        {
            StampObstruction(grid, ob, Default);
            StampObstruction(grid, ob, Ship);
        }

        // --- 3. Expand by clearance (dilate impassable region outward). ---
        // Done per-class after all stamps so one class's clearance doesn't bleed into another's
        // pre-expansion state.
        ExpandImpassable(grid, Default);
        ExpandImpassable(grid, Ship);

        Grid = grid;
    }

    // Stamp an axis-aligned approximation of an obstruction square onto the grid for one class.
    // P0 uses the AABB (half-bounding-box) of the oriented box; precise rotated rasterization
    // is a P1 refinement (matches the original's Rasterize which does per-edge, but AABB is a
    // close approximation for the small unit/building footprints at 1m navcell resolution).
    private static void StampObstruction(Grid<NavcellData> grid, ObstructionSquare ob, PassabilityClassDef cls)
    {
        if (!cls.StampObstructions) return;
        // AABB half-extents from the oriented box.
        var bb = Geometry.GetHalfBoundingBox(ob.U, ob.V, new FixedVector2D(ob.Hw, ob.Hh));
        int x0 = PathfindingCore.WorldToNavcell(ob.X - bb.X);
        int z0 = PathfindingCore.WorldToNavcell(ob.Z - bb.Y);
        int x1 = PathfindingCore.WorldToNavcell(ob.X + bb.X);
        int z1 = PathfindingCore.WorldToNavcell(ob.Z + bb.Y);
        for (int nj = z0; nj <= z1; nj++)
            for (int ni = x0; ni <= x1; ni++)
            {
                if (ni < 0 || nj < 0 || ni >= grid.W || nj >= grid.H) continue;
                var cell = grid.Get(ni, nj);
                grid.Set(ni, nj, PathfindingCore.MakeImpassable(cell, cls.Mask));
            }
    }

    // Expand impassable cells outward by the class's clearance radius, in navcells. This is the
    // fixed-point equivalent of ExpandImpassableCells in Rasterize.cpp: any passable cell within
    // `clearance` world-units of an impassable cell becomes impassable too.
    //
    // Implementation: multi-source BFS from all impassable cells, stopping at clearance. Using a
    // ring expansion (iterate distance = 0..clearanceNavcells) keeps it O(clearance² × stamps).
    private static void ExpandImpassable(Grid<NavcellData> grid, PassabilityClassDef cls)
    {
        int clearanceNav = cls.Clearance.ToIntRoundToInfinity();
        if (clearanceNav <= 0) return;

        // Snapshots of which cells are impassable-for-this-class at each ring distance, expanded
        // outward. Start with the current impassable set.
        bool[,] impassable = new bool[grid.W, grid.H];
        for (int j = 0; j < grid.H; j++)
            for (int i = 0; i < grid.W; i++)
                impassable[i, j] = !PathfindingCore.IsPassable(grid.Get(i, j), cls.Mask);

        // For each ring distance, mark any passable cell adjacent (8-connected) to an impassable
        // cell as impassable. Repeat clearanceNav times.
        for (int ring = 0; ring < clearanceNav; ring++)
        {
            bool[,] newlyBlocked = new bool[grid.W, grid.H];
            for (int j = 0; j < grid.H; j++)
                for (int i = 0; i < grid.W; i++)
                {
                    if (impassable[i, j]) continue;
                    // Check 8-neighbours; if any impassable, this cell gets blocked this ring.
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            if (di == 0 && dj == 0) continue;
                            int ni = i + di, nj = j + dj;
                            if (ni < 0 || nj < 0 || ni >= grid.W || nj >= grid.H) continue;
                            if (impassable[ni, nj]) { newlyBlocked[i, j] = true; break; }
                        }
                }
            // Commit this ring into the impassable set.
            bool any = false;
            for (int j = 0; j < grid.H; j++)
                for (int i = 0; i < grid.W; i++)
                    if (newlyBlocked[i, j]) { impassable[i, j] = true; any = true; }
            if (!any) break; // reached steady state early
        }

        // Write the expanded set back into the grid for this class's bit only.
        for (int j = 0; j < grid.H; j++)
            for (int i = 0; i < grid.W; i++)
                if (impassable[i, j])
                {
                    var cell = grid.Get(i, j);
                    grid.Set(i, j, PathfindingCore.MakeImpassable(cell, cls.Mask));
                }
    }
}
