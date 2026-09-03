using System.IO;
using Xunit;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Tests;

/// <summary>序列化类型覆盖(§4):U64/I64/Float/Double 的二进制往返 + 哈希位级一致性。</summary>
public sealed class SerializerTypeCoverageTests
{
    [Fact]
    public void BinaryRoundTrip_U64_I64_Float_Double()
    {
        using var ms = new MemoryStream();
        var w = new BinarySerializer(new BinaryWriter(ms));
        w.NumberU64("u64", 0xFEDCBA9876543210UL);
        w.NumberI64("i64", -0x1234567890ABL);
        w.NumberFloat("f", 3.5f);
        w.NumberDouble("d", -2.25e-10);
        w.NumberU32("u32", 42u);   // 混合序干扰
        w.NumberFloat("f2", float.NaN);
        w.NumberDouble("d2", double.PositiveInfinity);

        ms.Position = 0;
        var r = new BinaryDeserializer(new BinaryReader(ms));
        Assert.Equal(0xFEDCBA9876543210UL, r.NumberU64("u64"));
        Assert.Equal(-0x1234567890ABL, r.NumberI64("i64"));
        Assert.Equal(3.5f, r.NumberFloat("f"));
        Assert.Equal(-2.25e-10, r.NumberDouble("d"));
        Assert.Equal(42u, r.NumberU32("u32"));
        Assert.True(float.IsNaN(r.NumberFloat("f2")));
        Assert.Equal(double.PositiveInfinity, r.NumberDouble("d2"));
    }

    [Fact]
    public void HashSerializer_FloatBits_AreDeterministic()
    {
        // 位级进 MD5:NaN/非正规数/负零都按位哈希(跨平台一致;不同位型 → 不同哈希)。
        static byte[] Hash(double v)
        {
            var h = new HashSerializer();
            h.NumberDouble("d", v);
            return h.ComputeHash();
        }
        Assert.Equal(Hash(1.5), Hash(1.5));
        Assert.NotEqual(Hash(1.5), Hash(-1.5));
        Assert.NotEqual(Hash(0.0), Hash(-0.0));   // 位型不同 → 哈希不同(正确:位级语义)
    }
}
