using Xunit;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.Tests;

/// <summary>TerritoryBoundaryCalculator(上游轮廓追踪)单元测试:
/// 单区闭环、双区背靠背、内沿(飞地洞)环、blink 判别位、processed 不重复。</summary>
public sealed class TerritoryBoundaryTests
{
    private static byte[] Grid(int w, int h, params (int X, int Z, byte V)[] cells)
    {
        var g = new byte[w * h];
        foreach (var (x, z, v) in cells) g[z * w + x] = v;
        return g;
    }

    [Fact]
    public void SingleCellRegion_OneClosedLoop()
    {
        var g = Grid(4, 4, (1, 1, 1));
        var bs = TerritoryBoundaryCalculator.ComputeBoundaries(g, 4, 8);
        Assert.Single(bs);
        Assert.Equal(1, bs[0].Owner);
        Assert.False(bs[0].Blinking);
        // 单格轮廓 = 4 角点。
        Assert.Equal(4, bs[0].Points.Count);
    }

    [Fact]
    public void BlockRegion_LoopCoversPerimeter()
    {
        // 2×2 块在 (1,1)-(2,2):轮廓应含 8 个点(角+边中点)。
        var g = Grid(6, 6,
            (1, 1, 1), (2, 1, 1), (1, 2, 1), (2, 2, 1));
        var bs = TerritoryBoundaryCalculator.ComputeBoundaries(g, 6, 8);
        Assert.Single(bs);
        Assert.True(bs[0].Points.Count >= 8);
        // 追踪点列走边中点(edgeOffsets 半格偏移)——角部各切去半格斜角:
        // 面积 = 256 − 4×(4×4/2) = 224(上游同款,edgeOffsets 逐字)。
        double area = PolygonArea(bs[0].Points);
        Assert.Equal(224, area, 1);
    }

    [Fact]
    public void TwoOwners_BackToBackBoundaries()
    {
        // 左半 P1 右半 P2(4 列):两环(owner 各异),x=16m 界两侧。
        var g = new byte[4 * 4];
        for (int z = 0; z < 4; z++)
            for (int x = 0; x < 4; x++)
                g[z * 4 + x] = (byte)(x < 2 ? 1 : 2);
        var bs = TerritoryBoundaryCalculator.ComputeBoundaries(g, 4, 8);
        Assert.Equal(2, bs.Count);
        Assert.Contains(bs, b => b.Owner == 1);
        Assert.Contains(bs, b => b.Owner == 2);
    }

    [Fact]
    public void Enclave_InnerEdgeTracedAsSeparateLoop()
    {
        // P1 大块 4×4 含 P2 飞地(2,2)→ 3 环:P1 外环 + P1 内沿环 + P2 外环。
        var g = new byte[5 * 5];
        for (int z = 0; z < 5; z++)
            for (int x = 0; x < 5; x++)
                g[z * 5 + x] = 1;
        g[2 * 5 + 2] = 2;
        var bs = TerritoryBoundaryCalculator.ComputeBoundaries(g, 5, 8);
        Assert.Equal(3, bs.Count);
        Assert.Equal(2, bs.FindAll(b => b.Owner == 1).Count);   // 外环 + 内沿
        Assert.Single(bs.FindAll(b => b.Owner == 2));
    }

    [Fact]
    public void BlinkBit_SeparatesBoundaryDiscriminator()
    {
        // 同主异 blink 的两格 → 两条环(判别位含 blink)。
        var g = Grid(4, 4,
            (1, 1, 1), (2, 1, (byte)(1 | TerritoryBoundaryCalculator.BlinkingMask)));
        var bs = TerritoryBoundaryCalculator.ComputeBoundaries(g, 4, 8);
        Assert.Equal(2, bs.Count);
        Assert.Single(bs.FindAll(b => b.Blinking));
        Assert.Single(bs.FindAll(b => !b.Blinking));
    }

    private static double PolygonArea(System.Collections.Generic.List<(float X, float Z)> pts)
    {
        double a = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % pts.Count];
            a += (double)p.X * q.Z - (double)q.X * p.Z;
        }
        return System.Math.Abs(a) / 2;
    }
}
