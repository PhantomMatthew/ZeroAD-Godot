using System;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>Per-cell terrain passability class. The terrain grid is filled by the presentation
/// layer at load time (from the PMP heightmap: above water = Land, below = Water, cliffs = Impassable).
/// Pathfinder and BuildRestrictions consult this to reject water/cliff placement.</summary>
public enum TerrainClass : byte
{
    Land = 0,
    Water = 1,
    Impassable = 2,
}

[Component("Terrain", "Terrain")]
public sealed class TerrainComponent : ComponentBase, IComponentMessageHandler
{
    public int MapSize { get; private set; }
    public float TileSize { get; private set; }

    // Per-tile terrain class grid, [tileX, tileZ]. Null until the presentation layer fills it via
    // SetPassabilityGrid; queries treat null as "all land" so unconfigured maps still work.
    private TerrainClass[,]? _passability;

    protected override void OnInit()
    {
        MapSize = 64;
        TileSize = 4.0f;
    }

    public void Configure(int mapSize, float tileSize)
    {
        MapSize = mapSize;
        TileSize = tileSize;
    }

    /// <summary>Fill the passability grid from a source computed by the presentation layer (which
    /// owns the heightmap). Grid must be [MapSize, MapSize]. Stored by reference (no copy) — caller
    /// must not mutate it afterwards.</summary>
    public void SetPassabilityGrid(TerrainClass[,] grid)
    {
        if (grid.GetLength(0) != MapSize || grid.GetLength(1) != MapSize)
            throw new ArgumentException(
                $"passability grid must be [{MapSize},{MapSize}], got [{grid.GetLength(0)},{grid.GetLength(1)}]");
        _passability = grid;
    }

    public float GetWorldSize() => MapSize * TileSize;

    public bool IsInBounds(FixedVector2D pos)
    {
        float x = pos.X.ToFloat();
        float y = pos.Y.ToFloat();
        return x >= 0 && x < GetWorldSize() && y >= 0 && y < GetWorldSize();
    }

    /// <summary>World coordinate → tile index (clamped to grid).</summary>
    public (int tx, int tz) WorldToTile(Fixed x, Fixed z)
    {
        int tx = (x / Fixed.FromFloat(TileSize)).ToIntRoundToZero();
        int tz = (z / Fixed.FromFloat(TileSize)).ToIntRoundToZero();
        if (tx < 0) tx = 0; if (tx >= MapSize) tx = MapSize - 1;
        if (tz < 0) tz = 0; if (tz >= MapSize) tz = MapSize - 1;
        return (tx, tz);
    }

    /// <summary>True if the tile at (x,z) is passable for a land unit. Treats unconfigured terrain
    /// (null grid) as all-land so the sim still runs before the Godot layer fills it.</summary>
    public bool IsLand(Fixed x, Fixed z)
    {
        if (_passability == null) return true;
        var (tx, tz) = WorldToTile(x, z);
        return _passability[tx, tz] == TerrainClass.Land;
    }

    public TerrainClass GetClass(Fixed x, Fixed z)
    {
        if (_passability == null) return TerrainClass.Land;
        var (tx, tz) = WorldToTile(x, z);
        return _passability[tx, tz];
    }

    /// <summary>
    /// Check whether an OBB footprint (center x,z, half-width hw, half-height hh, axis-aligned
    /// since buildings don't rotate here) fits entirely on Land tiles. Mirrors the terrain half of
    /// <c>CCmpPathfinder::CheckBuildingPlacement</c>. We sample the tile at each corner; if any is
    /// water/impassable the placement fails.
    /// </summary>
    public bool IsFootprintOnLand(Fixed x, Fixed z, Fixed hw, Fixed hh)
    {
        return IsLand(x - hw, z - hh) && IsLand(x + hw, z - hh)
            && IsLand(x - hw, z + hh) && IsLand(x + hw, z + hh);
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("size", MapSize);
        s.NumberFixed("tile", Fixed.FromFloat(TileSize));
    }

    public override void Deserialize(IDeserializer d)
    {
        MapSize = d.NumberI32("size");
        TileSize = d.NumberFixed("tile").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}
