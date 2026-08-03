using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Pathfinding;

// Core pathfinding data types — ported from source/simulation2/helpers/Pathfinding.h,
// PathGoal.h, Grid.h. These are the pure data structures shared by all three pathfinders
// (Hierarchical, Long/JPS, Vertex). All math is fixed-point (Fixed / CFixed_15_16) so the
// pathfinder stays deterministic and Godot-free.

// --- Passability bitmask model -------------------------------------------------
// Each navcell stores a ushort; one bit per passability class. Convention (matches the
// original IS_PASSABLE macro): a SET bit means impassable for that class.
//   IS_PASSABLE(cell, classMask) = (cell & classMask) == 0
// Bit 15 (SpecialPassClass) is a runtime scratch bit for excluded circular regions.

/// <summary>Per-navcell passability data: one bit per class, set = impassable.</summary>
public readonly struct NavcellData
{
    public readonly ushort Value;
    public NavcellData(ushort value) => Value = value;
    public static implicit operator ushort(NavcellData d) => d.Value;
    public static implicit operator NavcellData(ushort v) => new(v);
}

/// <summary>Passability-class bitmask. A single bit identifying one class.</summary>
public readonly struct PassClass
{
    public readonly ushort Mask;
    public PassClass(ushort mask) => Mask = mask;
}

// Free functions and constants live in this static class (C# requires a containing type).
public static class PathfindingCore
{
    /// <summary>Maximum number of passability classes (bits in NavcellData).</summary>
    public const int PassClassBits = 16;

    /// <summary>The mask for class at index id: bit id set.</summary>
    public static PassClass PassClassMaskFromIndex(int id) => new((ushort)(1u << id));

    /// <summary>Scratch bit (index 15) reused at runtime to mark temporarily-excluded regions.
    /// LongPathfinder stamps it on cells inside excluded circular areas so JPS routes around them.</summary>
    public static readonly PassClass SpecialPassClass = PassClassMaskFromIndex(PassClassBits - 1);

    /// <summary>True if <paramref name="cell"/> is passable for <paramref name="passClass"/>.</summary>
    public static bool IsPassable(NavcellData cell, PassClass passClass) =>
        (cell.Value & passClass.Mask) == 0;

    /// <summary>Mark a cell impassable for a class (set the bit).</summary>
    public static NavcellData MakeImpassable(NavcellData cell, PassClass passClass) =>
        new((ushort)(cell.Value | passClass.Mask));

    // --- Navcell geometry ------------------------------------------------------
    // The nav grid runs at 1 world unit per navcell (NAVCELL_SIZE = fixed::FromInt(1) in the
    // original). Each 4m terrain tile is a 4x4 navcell block.

    /// <summary>World units per navcell side. Matches NAVCELL_SIZE in Pathfinding.h.</summary>
    public static readonly Fixed NavcellSize = Fixed.FromInt(1);

    /// <summary>Navcells per terrain tile side (TERRAIN_TILE_SIZE / NAVCELL_SIZE = 4/1).</summary>
    public const int NavcellsPerTerrainTile = 4;

    /// <summary>Convert world coordinate to navcell index (floor).</summary>
    public static int WorldToNavcell(Fixed world)
    {
        int v = world.InternalValue;        // 16.16 fixed
        return v >> 16;                     // floor divide by 1.0 (integer part)
    }

    /// <summary>Convert navcell index to world coordinate of its center.</summary>
    public static Fixed NavcellCenterToWorld(int navcell) =>
        Fixed.FromInt(navcell) + Fixed.FromFraction(1, 2);
}

// --- Grid<T> (flat array) — ported from helpers/Grid.h -------------------------
/// <summary>A flat 2D grid of T. Width/Height are u16 (matches the original's map-size limits).</summary>
public sealed class Grid<T>
{
    public readonly int W;
    public readonly int H;
    private readonly T[] _data;

    public Grid(int w, int h)
    {
        W = w; H = h;
        _data = new T[w * h];
    }

    public T Get(int i, int j)
    {
#if DEBUG
        if ((uint)i >= (uint)W || (uint)j >= (uint)H)
            throw new IndexOutOfRangeException($"Grid.Get({i},{j}) out of {W}x{H}");
#endif
        return _data[j * W + i];
    }

    public void Set(int i, int j, T value)
    {
#if DEBUG
        if ((uint)i >= (uint)W || (uint)j >= (uint)H)
            throw new IndexOutOfRangeException($"Grid.Set({i},{j}) out of {W}x{H}");
#endif
        _data[j * W + i] = value;
    }

    /// <summary>True if any cell in the axis-aligned rectangle has a truthy value.
    /// Port of Grid::any_set_in_square (used by dirtiness checks).</summary>
    public bool AnySetInSquare(int i0, int j0, int i1, int j1)
    {
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                if (i < 0 || j < 0 || i >= W || j >= H) continue;
                if (_data[j * W + i] is bool b && b) return true;
            }
        return false;
    }

    /// <summary>按线性索引访问原始数据（AI 的 passabilityMap.data[i] 等价物）。
    /// 行主序：index = j * W + i。AI 地图分析（TerrainAnalysis/Accessibility）需此访问。</summary>
    public ref readonly T this[int linearIndex] => ref _data[linearIndex];

    /// <summary>原始数据数组的只读引用（供 AI 批量扫描，避免逐格 Get 开销）。</summary>
    public ReadOnlySpan<T> AsSpan() => _data.AsSpan();
}

// --- SparseGrid<T> — ported from Grid.h:304 -----------------------------------
// Same interface as Grid<T> but lazily populated: only written cells are stored, unwritten
// cells read as default(T). Used as the per-search A* scratch grid (most searches touch few
// cells, so allocating/clearing a full W*H array is wasteful). The original used 16x16 chunk
// buckets; this C# port uses a dictionary keyed by the flat index for simplicity.

/// <summary>A lazily-populated grid. Unwritten cells read as default(T); only Set cells are
/// stored. Avoids the O(W*H) allocation/clear a dense scratch grid would need.</summary>
public sealed class SparseGrid<T>
{
    public readonly int W;
    public readonly int H;
    private readonly Dictionary<int, T> _cells = new();

    public SparseGrid(int w, int h) { W = w; H = h; }

    public T Get(int i, int j) =>
        _cells.TryGetValue(j * W + i, out var v) ? v : default!;

    public void Set(int i, int j, T value) => _cells[j * W + i] = value;

    public bool IsSet(int i, int j) => _cells.ContainsKey(j * W + i);

    public void Clear() => _cells.Clear();
}

// --- PathCost — integer-packed path cost (ported from Pathfinding.h) ------------
// Packs horizontal/vertical cost and diagonal cost into a single uint so A* comparisons
// are exact (no float drift). diag = hv * sqrt(2) ≈ hv * 92682 / 65536.
// "Maximum path length before overflow is about 45K steps" (original comment).

public readonly struct PathCost : IComparable<PathCost>
{
    // data = hv * 65536 + diag * 92682  (encodes both move kinds; 2^16 * sqrt(2) ≈ 92681.9).
    public readonly uint Data;

    private PathCost(uint data) => Data = data;

    /// <summary>Cost of an orthogonal step (hv) and a diagonal step (diag), each in navcell units.</summary>
    public PathCost(int hvSteps, int diagSteps)
    {
        // 65536 = 2^16; 92682 ≈ 2^16 * sqrt(2).
        Data = (uint)(hvSteps * 65536 + diagSteps * 92682);
    }

    public static PathCost operator +(PathCost a, PathCost b) => new(a.Data + b.Data);
    public int CompareTo(PathCost other) => Data.CompareTo(other.Data);
    public static bool operator <(PathCost a, PathCost b) => a.Data < b.Data;
    public static bool operator >(PathCost a, PathCost b) => a.Data > b.Data;
    public static bool operator <=(PathCost a, PathCost b) => a.Data <= b.Data;
    public static bool operator >=(PathCost a, PathCost b) => a.Data >= b.Data;

    /// <summary>Crude integer estimate (navcell steps * 65536 scale) for heuristic use.</summary>
    public long ToInt64() => Data;
}

// --- Waypoint / path result (ported from Pathfinding.h) ------------------------

public readonly struct Waypoint
{
    public readonly Fixed X;
    public readonly Fixed Z;
    public Waypoint(Fixed x, Fixed z) { X = x; Z = z; }
}

/// <summary>A list of waypoints. Order matches the original WaypointPath: EARLIEST waypoint
/// at the BACK (consumers pop from the back). Constructed reversed by the pathfinders.</summary>
public sealed class WaypointPath
{
    public readonly List<Waypoint> Waypoints = new();

    public bool IsEmpty => Waypoints.Count == 0;

    /// <summary>Next waypoint to walk toward (back of the list), or null if exhausted.</summary>
    public Waypoint? Next()
    {
        if (Waypoints.Count == 0) return null;
        var w = Waypoints[Waypoints.Count - 1];
        Waypoints.RemoveAt(Waypoints.Count - 1);
        return w;
    }

    public Waypoint Peek() => Waypoints[Waypoints.Count - 1];

    /// <summary>Push a waypoint; the first pushed becomes the LAST to be consumed (goal first).</summary>
    public void Push(Waypoint w) => Waypoints.Add(w);
}

// --- PathGoal — ported from helpers/PathGoal.h ---------------------------------
// A goal is one of five shapes. LongPathfinder requires MakeGoalReachable to have converted
// it to POINT before JPS runs, but VertexPathfinder handles arbitrary shapes directly.

public readonly struct PathGoal
{
    public enum Kind { Point, Circle, InvertedCircle, Square, InvertedSquare }

    public readonly Kind Type;
    public readonly Fixed X;
    public readonly Fixed Z;
    public readonly Fixed Hw;   // half-width (Square)
    public readonly Fixed Hh;   // half-height (Square)
    public readonly FixedVector2D U;  // Square orientation
    public readonly FixedVector2D V;
    public readonly Fixed MaxDist;    // waypoint spacing hint (LongPathfinder)

    private PathGoal(Kind type, Fixed x, Fixed z, Fixed hw, Fixed hh,
        FixedVector2D u, FixedVector2D v, Fixed maxDist)
    {
        Type = type; X = x; Z = z; Hw = hw; Hh = hh; U = u; V = v; MaxDist = maxDist;
    }

    public static PathGoal Point(Fixed x, Fixed z) =>
        new(Kind.Point, x, z, Fixed.Zero, Fixed.Zero, default, default, Fixed.Zero);

    public static PathGoal Circle(Fixed x, Fixed z, Fixed radius) =>
        new(Kind.Circle, x, z, radius, Fixed.Zero, default, default, Fixed.Zero);

    public static PathGoal InvertedCircle(Fixed x, Fixed z, Fixed radius) =>
        new(Kind.InvertedCircle, x, z, radius, Fixed.Zero, default, default, Fixed.Zero);

    /// <summary>Distance² from a point to the goal centre (fixed-point squared distance).</summary>
    public Fixed DistanceToPoint(Fixed px, Fixed pz)
    {
        Fixed dx = px - X;
        Fixed dz = pz - Z;
        return dx.Square() + dz.Square();
    }

    /// <summary>True if a navcell (by its centre world coords) contains/satisfies the goal.</summary>
    public bool NavcellContainsGoal(Fixed cx, Fixed cz)
    {
        switch (Type)
        {
            case Kind.Point:
                return cx == X && cz == Z;
            case Kind.Circle:
            case Kind.InvertedCircle:
                {
                    // Inside-circle: dist² <= r². Inverted: dist² >= r².
                    Fixed dx = cx - X, dz = cz - Z;
                    Fixed d2 = dx.Square() + dz.Square();
                    bool inside = d2 <= Hw.Square();   // Hw holds radius for circle kinds
                    return Type == Kind.Circle ? inside : !inside;
                }
            case Kind.Square:
            case Kind.InvertedSquare:
                {
                    // Axis-aligned box test (this port doesn't rotate goal squares).
                    Fixed dx = Fixed.FromInt(1); // abs via conditional
                    dx = cx - X; dx = dx < Fixed.Zero ? -dx : dx;
                    Fixed dz = cz - Z; dz = dz < Fixed.Zero ? -dz : dz;
                    bool inside = dx <= Hw && dz <= Hh;
                    return Type == Kind.Square ? inside : !inside;
                }
            default:
                return false;
        }
    }

    /// <summary>Nearest point on the goal shape to a given position. Used by VertexPathfinder's
    /// virtual goal vertex.</summary>
    public (Fixed X, Fixed Z) NearestPoint(Fixed px, Fixed pz)
    {
        switch (Type)
        {
            case Kind.Point:
                return (X, Z);
            case Kind.Circle:
                {
                    Fixed dx = px - X, dz = pz - Z;
                    // Normalize direction (approx) and scale by radius.
                    Fixed d2 = dx.Square() + dz.Square();
                    if (d2 <= Hw.Square()) return (px, pz); // already inside
                    // dir = (dx,dz)/|d| * radius. Use Fixed division.
                    Fixed dist = Fixed.FromInt(1).WithInternalValue((int)MathInt.Sqrt64(
                        (ulong)((long)d2.InternalValue >= 0 ? (long)d2.InternalValue : -(long)d2.InternalValue)));
                    if (dist == Fixed.Zero) return (X, Z);
                    Fixed rx = X + dx * Hw.InternalValue / dist.InternalValue;
                    Fixed rz = Z + dz * Hw.InternalValue / dist.InternalValue;
                    return (rx, rz);
                }
            default:
                // Clamp to box for square/inverted-square; fall back to centre.
                {
                    Fixed cx = px < X - Hw ? X - Hw : (px > X + Hw ? X + Hw : px);
                    Fixed cz = pz < Z - Hh ? Z - Hh : (pz > Z + Hh ? Z + Hh : pz);
                    return (cx, cz);
                }
        }
    }
}

// --- Terrain tile info fed into passability rasterization ----------------------
/// <summary>Per-terrain-tile (4m) data used to classify navcell passability. Filled by the
/// presentation layer from the PMP heightmap + WaterManager.</summary>
public readonly struct TerrainTileInfo
{
    public readonly Fixed WaterDepth;   // metres below water surface (positive = submerged)
    public readonly Fixed Slope;        // max slope at this tile
    public readonly Fixed ShoreDist;    // distance to shore (0 = deep inland/water)
    public TerrainTileInfo(Fixed depth, Fixed slope, Fixed shore) =>
        (WaterDepth, Slope, ShoreDist) = (depth, slope, shore);
}

// --- Passability class definition (from pathfinder.xml) -----------------------
/// <summary>One passability class's rules. Mirrors PathfinderPassability in the original.</summary>
public sealed class PassabilityClassDef
{
    public required string Name;
    public PassClass Mask;
    /// <summary>Min water depth to be passable (ships). Negative = no min (land class).</summary>
    public Fixed MinWaterDepth = Fixed.FromInt(-1);
    /// <summary>Max water depth to be passable (land units). Large = no max (ship class).</summary>
    public Fixed MaxWaterDepth = Fixed.FromInt(int.MaxValue >> 16);
    public Fixed MaxTerrainSlope = Fixed.FromInt(int.MaxValue >> 16);
    /// <summary>Min clearance from static obstructions, in world units.</summary>
    public Fixed Clearance = Fixed.Zero;
    /// <summary>Whether static obstruction shapes stamp this class's impassable bit.</summary>
    public bool StampObstructions = true;

    /// <summary>True if a terrain tile satisfies this class's depth/slope rules.</summary>
    public bool TerrainIsPassable(in TerrainTileInfo tile)
    {
        if (tile.WaterDepth < MinWaterDepth) return false;
        if (tile.WaterDepth > MaxWaterDepth) return false;
        if (tile.Slope > MaxTerrainSlope) return false;
        return true;
    }
}
