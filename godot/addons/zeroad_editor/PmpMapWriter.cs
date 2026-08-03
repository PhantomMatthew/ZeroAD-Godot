using System.IO;
using Godot;

namespace ZeroAD.Godot.Editor;

/// <summary>PMP 二进制写入器（镜像 MapWriter::PackTerrain）。
/// 将 MapData 写回 PMP 格式。关键是 tile 的 patch-major repatch
/// （PmpMap.cs 读取时做了 depatch，写入时必须反向操作）。</summary>
[Tool]
public static class PmpMapWriter
{
    private const int PatchSize = 16;
    private const int PmpVersion = 7;
    private const string Magic = "PSMP";

    /// <summary>保存 MapData 到 PMP 文件。</summary>
    public static void Save(string path, MapData data)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(fs);

        int pps = data.PatchesPerSide;
        int tilesPerSide = pps * PatchSize;
        int vertsPerSide = tilesPerSide + 1;

        // Header
        foreach (char c in Magic)
            bw.Write((byte)c);
        bw.Write((uint)PmpVersion);
        bw.Write((uint)pps);

        // Heightmap: (vertsPerSide)² 个 u16，已经是正确格式（world row-major = PMP on-disk）
        // PMP heightmap 存储为 vertex-major（x 外层 z 内层），与 MapData.Heightmap 一致
        for (int i = 0; i < data.Heightmap.Length; i++)
            bw.Write(data.Heightmap[i]);

        // Texture name table
        bw.Write((uint)data.TextureNames.Length);
        foreach (var name in data.TextureNames)
        {
            bw.Write((uint)name.Length);
            var bytes = System.Text.Encoding.UTF8.GetBytes(name);
            bw.Write(bytes);
        }

        // Tiles: repatch from world row-major → patch-major
        // 读取时的 depatch 公式：dst = (pj*16+zi)*tps + (pi*16+xi)
        // 写入时的 repatch 反向：src = (pj*16+zi)*tps + (pi*16+xi)
        for (int pj = 0; pj < pps; pj++)
        {
            for (int pi = 0; pi < pps; pi++)
            {
                for (int zi = 0; zi < PatchSize; zi++)
                {
                    for (int xi = 0; xi < PatchSize; xi++)
                    {
                        int worldIdx = (pj * PatchSize + zi) * tilesPerSide + (pi * PatchSize + xi);
                        ushort tex1 = data.TileTextureIndex[worldIdx];
                        ushort tex2 = 0xFFFF;  // 无第二纹理
                        uint priority = data.TilePriority != null && worldIdx < data.TilePriority.Length
                            ? data.TilePriority[worldIdx] : 0;

                        bw.Write(tex1);
                        bw.Write(tex2);
                        bw.Write(priority);
                    }
                }
            }
        }
    }
}
