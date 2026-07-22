using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim;

// WaterManager — authoritative sim-side water surface. Ported from
// source/simulation2/components/CCmpWaterManager.cpp (there is no JS layer).
//
// The original holds a single global water height (entity_position_t m_WaterHeight);
// GetWaterLevel(x,z) ignores its coordinates. The C# rewrite had no sim-side water at
// all — Godot baked a TerrainClass.Water passability grid from the PMP height at load
// time. This class introduces the sim-side height query so pathfinding/passability can
// derive water tiles dynamically and naval movement can read depth later (M3/P1).
//
// P0 scope: just the global height + Set/Get + serialization. Hooking it into
// TerrainComponent's passability recompute is deferred (the baked grid is still used).

/// <summary>Global water surface height. Single value, not per-tile (matches the original).</summary>
public sealed class WaterManager
{
    /// <summary>Water surface height in world units. Below this, terrain is submerged.</summary>
    public Fixed WaterHeight { get; private set; } = Fixed.Zero;

    /// <summary>True once a height has been loaded from the map (otherwise water is absent).</summary>
    public bool HasWater { get; private set; }

    /// <summary>Set the global water height. Mirrors CCmpWaterManager::SetWaterLevel.</summary>
    public void SetWaterLevel(Fixed height)
    {
        WaterHeight = height;
        HasWater = true;
    }

    /// <summary>Get the water level at a position. Coordinates are ignored (global height),
    /// matching the original GetWaterLevel(x,z).</summary>
    public Fixed GetWaterLevel(Fixed x, Fixed z) => WaterHeight;

    public void Serialize(ISerializer s)
    {
        s.Bool("has", HasWater);
        s.NumberFixed("height", WaterHeight);
    }

    public void Deserialize(IDeserializer d)
    {
        HasWater = d.Bool("has");
        WaterHeight = d.NumberFixed("height");
    }
}
