using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>Fog texture construction: R8 base from the LOS grid + 7-tap binomial blur
/// (soft fog edges for minimap + world shader).</summary>
public sealed class FogTextureBuilderTests
{
    [Fact]
    public void BaseFill_ThreeStates()
    {
        var los = new LosGrid(256);
        los.AddLos(1, Fixed.FromInt(100), Fixed.FromInt(100), Fixed.FromInt(16));
        los.AddLos(1, Fixed.FromInt(200), Fixed.FromInt(200), Fixed.FromInt(12));
        los.RemoveLos(1, Fixed.FromInt(200), Fixed.FromInt(200), Fixed.FromInt(12)); // explored only

        var builder = new FogTextureBuilder();
        byte[] data = builder.BuildBase(los, 1);

        int n = los.VerticesPerSide;
        Assert.Equal(255, data[25 * n + 25]); // visible
        Assert.Equal(128, data[50 * n + 50]); // explored, not visible
        Assert.Equal(0, data[5 * n + 5]);     // unexplored
    }

    [Fact]
    public void Blur_SoftensEdges_PreservesUniformRegions()
    {
        var los = new LosGrid(256);
        // Single visible vertex in the middle of nowhere.
        los.AddLos(1, Fixed.FromInt(128), Fixed.FromInt(128), Fixed.FromDouble(0.5));

        var builder = new FogTextureBuilder();
        int n = los.VerticesPerSide;
        byte[] blurred = builder.BuildBlurred(los, 1);

        int c = 32 * n + 32; // vertex nearest (128,128)
        Assert.True(blurred[c] > 0 && blurred[c] < 255, $"hot pixel spreads: got {blurred[c]}");
        Assert.True(blurred[c + 1] > 0, "neighbour picks up bleed");
        Assert.True(blurred[10 * n + 10] == 0, "far stays black");

        // A fully-visible board is a constant signal — the binomial kernel must leave it flat.
        var los2 = new LosGrid(64);
        los2.AddLos(1, Fixed.FromInt(32), Fixed.FromInt(32), Fixed.FromInt(100));
        byte[] flat = builder.BuildBlurred(los2, 1);
        int n2 = los2.VerticesPerSide;
        Assert.Equal(255, flat[4 * n2 + 4]);
        Assert.Equal(255, flat[8 * n2 + 8]);
    }
}
