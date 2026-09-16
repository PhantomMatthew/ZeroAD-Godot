using System;
using System.IO;

namespace ZeroAD.Sim.Content;

/// <summary>Headless PMP reader (height + size only). Same on-disk layout as
/// <c>godot/Scripts/PmpMap.cs</c> without Godot types or splat tables.</summary>
public sealed class PmpTerrain
{
    public const uint Magic = 0x504D5350; // "PSMP"
    public const int PatchSize = 16;
    public const float TileSize = 4.0f;
    public const float HeightScale = 1.0f / 92.0f;

    public int PatchesPerSide { get; init; }
    public int VerticesPerSide { get; init; }
    public ushort[] Heightmap { get; init; } = Array.Empty<ushort>();

    public int TilesPerSide => PatchesPerSide * PatchSize;
    public int MapSizeMeters => (VerticesPerSide - 1) * (int)TileSize;

    public float GetHeight(int x, int z)
    {
        int idx = z * VerticesPerSide + x;
        if (idx < 0 || idx >= Heightmap.Length) return 0f;
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
        return h00 + (h10 - h00) * tx + ((h01 + (h11 - h01) * tx) - (h00 + (h10 - h00) * tx)) * tz;
    }

    public static PmpTerrain Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs);
        uint magic = reader.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException($"Invalid PMP magic: 0x{magic:X}");
        reader.ReadUInt32(); // version
        reader.ReadUInt32(); // payload size
        int patchesPerSide = reader.ReadInt32();
        int verticesPerSide = patchesPerSide * PatchSize + 1;
        int heightmapSize = verticesPerSide * verticesPerSide;
        var heightmap = new ushort[heightmapSize];
        byte[] raw = reader.ReadBytes(heightmapSize * 2);
        for (int i = 0; i < heightmapSize; i++)
            heightmap[i] = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));
        return new PmpTerrain
        {
            PatchesPerSide = patchesPerSide,
            VerticesPerSide = verticesPerSide,
            Heightmap = heightmap
        };
    }
}
