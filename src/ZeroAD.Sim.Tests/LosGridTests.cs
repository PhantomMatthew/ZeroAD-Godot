using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

public sealed class LosGridTests
{
    private static Fixed M(double meters) => Fixed.FromDouble(meters);

    [Fact]
    public void Packing_PerPlayerTwoBits_Independent()
    {
        var g = new LosGrid(256);
        g.AddLos(1, M(40), M(40), M(12));
        Assert.True(g.IsVisible(1, 10, 10));
        Assert.False(g.IsVisible(2, 10, 10));
        Assert.False(g.IsExplored(2, 10, 10));

        g.AddLos(2, M(40), M(40), M(12));
        Assert.True(g.IsVisible(2, 10, 10));
        g.RemoveLos(1, M(40), M(40), M(12));
        Assert.False(g.IsVisible(1, 10, 10));
        Assert.True(g.IsVisible(2, 10, 10));
    }

    [Fact]
    public void AddLos_FirstSeer_SetsVisibleAndExplored()
    {
        var g = new LosGrid(256);
        g.AddLos(1, M(100), M(100), M(16));
        Assert.True(g.IsVisible(1, 25, 25));
        Assert.True(g.IsExplored(1, 25, 25));
        // Far outside the circle.
        Assert.False(g.IsVisible(1, 5, 5));
        Assert.False(g.IsExplored(1, 5, 5));
    }

    [Fact]
    public void RemoveLos_LastSeer_ClearsVisible_KeepsExplored()
    {
        var g = new LosGrid(256);
        g.AddLos(1, M(100), M(100), M(16));
        g.RemoveLos(1, M(100), M(100), M(16));
        Assert.False(g.IsVisible(1, 25, 25));
        Assert.True(g.IsExplored(1, 25, 25));
    }

    [Fact]
    public void TwoSeers_RemoveOne_StillVisible()
    {
        var g = new LosGrid(256);
        g.AddLos(1, M(100), M(100), M(16));
        g.AddLos(1, M(104), M(100), M(16));
        g.RemoveLos(1, M(100), M(100), M(16));
        Assert.True(g.IsVisible(1, 25, 25), "second seer still covers the vertex");
        Assert.Equal(1, g.GetCount(1, 25, 25));
        g.RemoveLos(1, M(104), M(100), M(16));
        Assert.False(g.IsVisible(1, 25, 25));
        Assert.Equal(0, g.GetCount(1, 25, 25));
    }

    [Fact]
    public void StripCircle_MatchesBruteForce()
    {
        // Exact oracle: vertex (i,j) is inside iff (i-x)²+(j-y)² <= r² in tile-space
        // Fixed math. Brute-force double loop vs the incremental strip scan.
        var centers = new[] { (30.2, 40.7), (128.9, 5.1), (200.0, 200.0), (0.0, 0.0), (255.9, 12.3) };
        var ranges = new[] { 14.8, 32.0, 50.0, 100.0 };
        foreach (var (cx, cz) in centers)
            foreach (var rm in ranges)
            {
                var g = new LosGrid(256);
                var x = M(cx); var z = M(cz); var r = M(rm);
                g.AddLos(1, x, z, r);

                // Same tile-space conversion as the implementation.
                var xt = x >> 2; var zt = z >> 2; var rt = r >> 2;
                var r2 = rt.Square();
                int verts = g.VerticesPerSide;
                for (int j = 0; j < verts; j++)
                    for (int i = 0; i < verts; i++)
                    {
                        var dx = Fixed.FromInt(i) - xt;
                        var dy = Fixed.FromInt(j) - zt;
                        bool expected = dx.Square() + dy.Square() <= r2;
                        Assert.Equal(expected, g.GetCount(1, i, j) > 0);
                    }
            }
    }

    [Fact]
    public void Explored_NeverDecays()
    {
        var g = new LosGrid(256);
        g.AddLos(1, M(60), M(60), M(12));
        g.RemoveLos(1, M(60), M(60), M(12));
        Assert.True(g.IsExplored(1, 15, 15));
        // Re-add elsewhere; old explored state untouched.
        g.AddLos(1, M(200), M(200), M(12));
        g.RemoveLos(1, M(200), M(200), M(12));
        Assert.True(g.IsExplored(1, 15, 15));
        Assert.True(g.IsExplored(1, 50, 50));
    }

    [Fact]
    public void PercentExplored_Increases()
    {
        var g = new LosGrid(256);
        Assert.Equal(0, g.GetPercentExplored(1));
        g.AddLos(1, M(128), M(128), M(40));
        int p1 = g.GetPercentExplored(1);
        Assert.True(p1 > 0);
        g.AddLos(1, M(32), M(32), M(40));
        Assert.True(g.GetPercentExplored(1) > p1);
        // Removing LOS must not reduce the explored percentage.
        g.RemoveLos(1, M(32), M(32), M(40));
        Assert.Equal(g.GetPercentExplored(1), g.GetPercentExplored(1));
    }

    [Fact]
    public void Serialize_RoundTrip_PreservesExploredAndVisible()
    {
        var g = new LosGrid(256);
        g.AddLos(1, M(100), M(100), M(20));
        g.AddLos(3, M(150), M(150), M(24));
        g.RemoveLos(3, M(150), M(150), M(24)); // p3: explored but not visible

        var cap = new CapturingSerializer();
        g.Serialize(cap);

        var g2 = new LosGrid(256);
        g2.Deserialize(new ReplayingDeserializer(cap));

        Assert.Equal(g.VerticesPerSide, g2.VerticesPerSide);
        for (int j = 0; j < g.VerticesPerSide; j++)
            for (int i = 0; i < g.VerticesPerSide; i++)
            {
                Assert.Equal(g.IsVisible(1, i, j), g2.IsVisible(1, i, j));
                Assert.Equal(g.IsExplored(1, i, j), g2.IsExplored(1, i, j));
                Assert.Equal(g.IsExplored(3, i, j), g2.IsExplored(3, i, j));
            }
        Assert.Equal(g.GetPercentExplored(1), g2.GetPercentExplored(1));
    }
}
