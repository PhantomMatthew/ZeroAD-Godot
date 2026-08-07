using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZeroAD.Godot;

/// <summary>
/// Reader for 0 A.D. PMP (Pyrogenesis Map Persistence) binary terrain format.
/// Format: magic "PSMP" + version(u32) + patchesPerSide(u32) +
///         heightmap[(patches*16+1)^2](u16) + textureCount(u32) + textureNames[] +
///         tiles[patches^2 * 16^2](STileDesc: u16 tex1Index + u16 tex2Index + u32 priority).
/// </summary>
public sealed class PmpMap
{
    public const uint Magic = 0x504D5350; // "PSMP"
    public const int PatchSize = 16;
    public const float TileSize = 4.0f;
    // source/graphics/Terrain.h:44 — HEIGHT_UNITS_PER_METRE = 92
    public const float HeightScale = 1.0f / 92.0f;

    public uint Version { get; init; }
    public int PatchesPerSide { get; init; }
    public int VerticesPerSide { get; init; }
    public ushort[] Heightmap { get; init; } = Array.Empty<ushort>();
    public List<string> TextureNames { get; init; } = new();

    /// <summary>Per-tile texture blend descriptors (STileDesc): base texture index,
    /// blend texture index (0xFFFF = none), and author priority (higher splats later).
    /// Index = z * TilesPerSide + x.</summary>
    public ushort[] TileTex1 { get; init; } = Array.Empty<ushort>();
    public ushort[] TileTex2 { get; init; } = Array.Empty<ushort>();
    public uint[] TilePriority { get; init; } = Array.Empty<uint>();
    public const ushort NoTexture = 0xFFFF;

    public int TilesPerSide => PatchesPerSide * PatchSize;
    public float MapSizeMeters => (VerticesPerSide - 1) * TileSize;

    public float GetHeight(int x, int z)
    {
        int idx = z * VerticesPerSide + x;
        if (idx < 0 || idx >= Heightmap.Length)
            return 0f;
        return Heightmap[idx] * HeightScale;
    }

    public float GetHeightWorld(float worldX, float worldZ)
    {
        float fx = worldX / TileSize;
        float fz = worldZ / TileSize;
        int x0 = (int)fx, z0 = (int)fz;
        float tx = fx - x0, tz = fz - z0;
        float h00 = GetHeight(x0, z0);
        float h10 = GetHeight(x0 + 1, z0);
        float h01 = GetHeight(x0, z0 + 1);
        float h11 = GetHeight(x0 + 1, z0 + 1);
        float h0 = h00 + (h10 - h00) * tx;
        float h1 = h01 + (h11 - h01) * tx;
        return h0 + (h1 - h0) * tz;
    }

    public static PmpMap Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs);
        return Load(reader);
    }

    /// <summary>MapExport → PmpMap 共享适配器(rmgen 生成图 → 地形渲染/编辑器预览共用;
    /// 此前 Main.cs 与 zeroad_editor 插件各有一份内联拷贝且插件版漏赋 VerticesPerSide,
    /// TerrainRenderer 必抛 InvalidDataException)。
    /// 两个坑都在此封装:VerticesPerSide = Size+1(Height 恰为 (Size+1)²,漏赋默认 0 →
    /// 0 顶点空 mesh);TileTex2 显式填满 NoTexture(rmgen 单层贴图无 blend 第二层,
    /// 空数组会让 SplatBaker 越界)。TilePriority 带上 rmgen 的逐 tile 优先级
    /// (地形混合的叠放顺序;长度不符时 SplatBaker 自动按全 0 处理)。</summary>
    public static PmpMap FromExport(ZeroAD.Sim.Rmgen.MapExport export)
    {
        return new PmpMap
        {
            Version = 7,
            PatchesPerSide = export.Size / PatchSize,
            VerticesPerSide = export.Size + 1,
            Heightmap = export.Height,
            TextureNames = new List<string>(export.TextureNames),
            TileTex1 = export.TileIndex,
            TileTex2 = Enumerable.Repeat(NoTexture, export.TileIndex.Length).ToArray(),
            TilePriority = export.TilePriority != null && export.TilePriority.Length == export.TileIndex.Length
                ? Array.ConvertAll(export.TilePriority, v => (uint)v)
                : Array.Empty<uint>(),
        };
    }

    public static PmpMap Load(BinaryReader reader)
    {
        uint magic = reader.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException($"Invalid PMP magic: 0x{magic:X} (expected 0x{Magic:X})");

        uint version = reader.ReadUInt32();
        reader.ReadUInt32(); // payload data_size (FileIo.cpp FileHeader), not needed for sequential read

        int patchesPerSide = reader.ReadInt32();
        int verticesPerSide = patchesPerSide * PatchSize + 1;

        int heightmapSize = verticesPerSide * verticesPerSide;
        var heightmap = new ushort[heightmapSize];
        byte[] raw = reader.ReadBytes(heightmapSize * 2);
        for (int i = 0; i < heightmapSize; i++)
            heightmap[i] = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));

        int textureCount = reader.ReadInt32();
        var textureNames = new List<string>(textureCount);
        for (int i = 0; i < textureCount; i++)
            textureNames.Add(ReadPmpString(reader));

        int tilesPerSide = patchesPerSide * PatchSize;
        int tileCount = tilesPerSide * tilesPerSide;
        // STileDesc: u16 tex1 + u16 tex2 + u32 priority, little-endian, per tile.
        // PMP stores tiles PATCH-MAJOR (patches in row-major order, 16x16 tiles
        // within each patch) — reading them flat scrambles every 16-tile band
        // (measured: vertical texture coherence 0.61 vs 0.94 depatched).
        // Depatch into world row-major (z*TilesPerSide+x) here so consumers
        // never see the on-disk layout.
        byte[] tileRaw = reader.ReadBytes(tileCount * 8);
        var tileTex1 = new ushort[tileCount];
        var tileTex2 = new ushort[tileCount];
        var tilePriority = new uint[tileCount];
        for (int pj = 0; pj < patchesPerSide; pj++)
        {
            for (int pi = 0; pi < patchesPerSide; pi++)
            {
                for (int zi = 0; zi < PatchSize; zi++)
                {
                    for (int xi = 0; xi < PatchSize; xi++)
                    {
                        int src = ((pj * patchesPerSide + pi) * PatchSize + zi) * PatchSize + xi;
                        int dst = (pj * PatchSize + zi) * tilesPerSide + (pi * PatchSize + xi);
                        int o = src * 8;
                        tileTex1[dst] = (ushort)(tileRaw[o] | (tileRaw[o + 1] << 8));
                        tileTex2[dst] = (ushort)(tileRaw[o + 2] | (tileRaw[o + 3] << 8));
                        tilePriority[dst] = (uint)(tileRaw[o + 4] | (tileRaw[o + 5] << 8)
                            | (tileRaw[o + 6] << 16) | (tileRaw[o + 7] << 24));
                    }
                }
            }
        }

        return new PmpMap
        {
            Version = version,
            PatchesPerSide = patchesPerSide,
            VerticesPerSide = verticesPerSide,
            Heightmap = heightmap,
            TextureNames = textureNames,
            TileTex1 = tileTex1,
            TileTex2 = tileTex2,
            TilePriority = tilePriority,
        };
    }

    private static string ReadPmpString(BinaryReader reader)
    {
        int len = reader.ReadInt32();
        byte[] bytes = reader.ReadBytes(len);
        return System.Text.Encoding.ASCII.GetString(bytes);
    }
}
