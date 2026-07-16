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
        reader.ReadBytes(tileCount * 8);

        return new PmpMap
        {
            Version = version,
            PatchesPerSide = patchesPerSide,
            VerticesPerSide = verticesPerSide,
            Heightmap = heightmap,
            TextureNames = textureNames,
        };
    }

    private static string ReadPmpString(BinaryReader reader)
    {
        int len = reader.ReadInt32();
        byte[] bytes = reader.ReadBytes(len);
        return System.Text.Encoding.ASCII.GetString(bytes);
    }
}
