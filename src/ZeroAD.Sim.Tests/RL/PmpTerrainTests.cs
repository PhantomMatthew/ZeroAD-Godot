using System;
using System.IO;
using Xunit;
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim.Tests.RL;

public sealed class PmpTerrainTests
{
    [Fact]
    public void Load_MinimalOnePatch_ReadsHeightAndSize()
    {
        string path = Path.Combine(Path.GetTempPath(), "zeroad-rl-min.pmp");
        int patches = 1;
        int verts = patches * PmpTerrain.PatchSize + 1;
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(PmpTerrain.Magic);
            w.Write(7u);
            w.Write(0u);
            w.Write(patches);
            for (int i = 0; i < verts * verts; i++)
                w.Write((ushort)(92 * 4)); // 4 m
        }
        try
        {
            var pmp = PmpTerrain.Load(path);
            Assert.Equal(16, pmp.TilesPerSide);
            Assert.Equal(64, pmp.MapSizeMeters);
            Assert.Equal(4f, pmp.GetHeight(0, 0), 2);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
