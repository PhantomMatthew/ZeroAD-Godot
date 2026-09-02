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
    // 顶点高度网格 [MapSize+1, MapSize+1](PMP heightmap 逐点,米;定点)。
    // 供 Attack 高度差/单位 Y 贴地;null = 平地(查询返 0,行为同旧)。
    private Fixed[,]? _heights;

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

    /// <summary>填入顶点高度网格([MapSize+1, MapSize+1],米)。与 passability 同约:
    /// 引用存储不拷贝,调用方不得再改。</summary>
    public void SetHeightGrid(Fixed[,] grid)
    {
        if (grid.GetLength(0) != MapSize + 1 || grid.GetLength(1) != MapSize + 1)
            throw new ArgumentException(
                $"height grid must be [{MapSize + 1},{MapSize + 1}], got [{grid.GetLength(0)},{grid.GetLength(1)}]");
        _heights = grid;
    }

    /// <summary>水平面高度(米;地面低于它即水域)。原版 CTerrain 的水位——通行类水深规则
    /// (ship MinWaterDepth / building-land MaxWaterDepth=0)与岸线距离的真实数据源;此前
    /// TerrainClass.Water 二分丢了深度。由建图侧(PMP/rmgen 都知道水面高)在 RebuildGrid 前设置;
    /// 未设置时水深判定回落到 passability 网格的 Water 类(深 5m 近似,旧行为)。</summary>
    public Fixed WaterLevel { get; private set; } = Fixed.Zero;
    public bool HasWaterLevel { get; private set; }
    public void SetWaterLevel(Fixed waterLevel) { WaterLevel = waterLevel; HasWaterLevel = true; }

    /// <summary>世界坐标处地形高度(双线性插值,定点;无网格 → 0)。</summary>
    public Fixed GetHeight(Fixed x, Fixed z)
    {
        if (_heights == null) return Fixed.Zero;
        float fx = x.ToFloat() / TileSize;
        float fz = z.ToFloat() / TileSize;
        int x0 = (int)System.MathF.Floor(fx);
        int z0 = (int)System.MathF.Floor(fz);
        if (x0 < 0) x0 = 0; if (x0 >= MapSize) x0 = MapSize - 1;
        if (z0 < 0) z0 = 0; if (z0 >= MapSize) z0 = MapSize - 1;
        float tx = fx - x0, tz = fz - z0;
        if (tx < 0) tx = 0; if (tx > 1) tx = 1;
        if (tz < 0) tz = 0; if (tz > 1) tz = 1;
        // 双线性(全程浮点→定点一次换算;同一数据各端同值,确定性成立)。
        float h00 = _heights[x0, z0].ToFloat();
        float h10 = _heights[x0 + 1, z0].ToFloat();
        float h01 = _heights[x0, z0 + 1].ToFloat();
        float h11 = _heights[x0 + 1, z0 + 1].ToFloat();
        float h0 = h00 + (h10 - h00) * tx;
        float h1 = h01 + (h11 - h01) * tx;
        return Fixed.FromFloat(h0 + (h1 - h0) * tz);
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
