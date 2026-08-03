using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot.Editor;

/// <summary>地图数据 Resource（编辑器内持有，可序列化为 .res 或附加到场景节点 metadata）。
/// 包含 PMP 地形数据 + 实体列表 + 玩家设置。</summary>
[Tool]
[GlobalClass]
public partial class MapData : Resource
{
    /// <summary>Patches per side（PMP 的 map size 字段，通常 8/16/32/64）。</summary>
    [Export] public int PatchesPerSide;

    /// <summary>Heightmap：(patchesPerSide*16+1)² 个 ushort 值。编码 = (height+20)*92。</summary>
    public ushort[] Heightmap = System.Array.Empty<ushort>();

    /// <summary>地形纹理名表（PMP texture name table）。</summary>
    public string[] TextureNames = System.Array.Empty<string>();

    /// <summary>Tile 纹理索引：patchesPerSide*16 的方阵，每个 tile 的纹理 ID。</summary>
    public ushort[] TileTextureIndex = System.Array.Empty<ushort>();

    /// <summary>Tile 优先级（PMP STileDesc.m_Priority）。</summary>
    public uint[] TilePriority = System.Array.Empty<uint>();

    /// <summary>实体列表（编辑器内放置的实体）。</summary>
    public List<MapEntityData> Entities = new();

    /// <summary>玩家设置列表。</summary>
    public List<MapPlayerData> Players = new();

    /// <summary>地图名/描述。</summary>
    [Export] public string MapName = "";
    [Export] public string Description = "";
}

/// <summary>实体数据（场景树 ↔ PMP/XML 转换中间态）。</summary>
public sealed class MapEntityData
{
    public int Uid;
    public string Template = "";
    public int PlayerID;
    public float X, Y, Z;  // 世界坐标
    public float Angle;
}

/// <summary>玩家设置数据。</summary>
public sealed class MapPlayerData
{
    public string Civ = "athen";
    public int Team = -1;
    public int Food = 300, Wood = 300, Stone = 200, Metal = 100;
    public string Color = "";
}
