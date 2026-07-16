using Xunit;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests.Maths;

public class FixedTests
{
    [Fact]
    public void FromInt_ReturnsCorrectInternalValue()
    {
        Assert.Equal(1 << 16, Fixed.FromInt(1).InternalValue);
        Assert.Equal(5 << 16, Fixed.FromInt(5).InternalValue);
        Assert.Equal(-3 << 16, Fixed.FromInt(-3).InternalValue);
        Assert.Equal(0, Fixed.FromInt(0).InternalValue);
    }

    [Fact]
    public void ToInt_RoundConversions()
    {
        Fixed f = Fixed.FromInt(7);
        Assert.Equal(7, f.ToIntRoundToZero());
        Assert.Equal(7, f.ToIntRoundToNearest());

        Fixed half = Fixed.FromFraction(1, 2);
        Assert.Equal(0, half.ToIntRoundToZero());
        Assert.Equal(1, half.ToIntRoundToNearest()); // ties to infinity
    }

    [Fact]
    public void Addition_Subtraction_Basic()
    {
        Fixed a = Fixed.FromInt(3);
        Fixed b = Fixed.FromInt(5);
        Assert.Equal(Fixed.FromInt(8), a + b);
        Assert.Equal(Fixed.FromInt(-2), a - b);
    }

    [Fact]
    public void Multiply_Uses64BitIntermediate()
    {
        Fixed a = Fixed.FromInt(3);
        Fixed b = Fixed.FromInt(5);
        // 3 * 5 = 15
        Assert.Equal(Fixed.FromInt(15), a.Multiply(b));

        // Large values that would overflow 32-bit if not using 64-bit
        Fixed big = Fixed.FromInt(100);
        Assert.Equal(Fixed.FromInt(10000), big.Multiply(big));
    }

    [Fact]
    public void Division_Basic()
    {
        Fixed a = Fixed.FromInt(10);
        Fixed b = Fixed.FromInt(2);
        Assert.Equal(Fixed.FromInt(5), a / b);
    }

    [Fact]
    public void Division_Fractional()
    {
        Fixed a = Fixed.FromInt(1);
        Fixed b = Fixed.FromInt(3);
        // 1/3 ≈ 0.333... → internal = (1<<16) / 3 = 65536/3 = 21845
        Assert.Equal(21845, (a / b).InternalValue);
    }

    [Fact]
    public void Sqrt_BasicValues()
    {
        Assert.Equal(Fixed.FromInt(0), Fixed.FromInt(0).Sqrt());
        Assert.Equal(Fixed.FromInt(1), Fixed.FromInt(1).Sqrt());
        Assert.Equal(Fixed.FromInt(4), Fixed.FromInt(16).Sqrt());
        Assert.Equal(Fixed.FromInt(3), Fixed.FromInt(9).Sqrt());
    }

    [Fact]
    public void Sqrt_NonPerfectSquare()
    {
        Fixed result = Fixed.FromInt(2).Sqrt();
        // sqrt(2) ≈ 1.41421... → internal = floor(sqrt(2 << 32)) = floor(92681.9...) = 92681
        // Verify: 92681^2 = 8589767761 <= 8589934592 < 8589953124 = 92682^2
        Assert.Equal(92681, result.InternalValue);
    }

    [Fact]
    public void Pi_HasCorrectConstant()
    {
        // pi << 16 = 205887 (from Fixed.cpp line 186)
        Assert.Equal(205887, Fixed.Pi.InternalValue);
    }

    [Fact]
    public void FromString_Integer()
    {
        Assert.Equal(Fixed.FromInt(42), Fixed.FromString("42"));
        Assert.Equal(Fixed.FromInt(-7), Fixed.FromString("-7"));
        Assert.Equal(Fixed.FromInt(0), Fixed.FromString("0"));
    }

    [Fact]
    public void FromString_Decimal()
    {
        Fixed half = Fixed.FromString("0.5");
        Assert.Equal(1 << 15, half.InternalValue); // 0.5 * 65536 = 32768

        Fixed quarter = Fixed.FromString("0.25");
        Assert.Equal(1 << 14, quarter.InternalValue); // 0.25 * 65536 = 16384
    }

    [Fact]
    public void ToString_FromString_Roundtrip()
    {
        string[] values = { "0", "1", "-1", "0.5", "0.25", "3.1416", "-2.7", "10", "0.1", "99.9" };
        foreach (string s in values)
        {
            Fixed f = Fixed.FromString(s);
            string back = f.ToString();
            Fixed reparsed = Fixed.FromString(back);
            Assert.Equal(f.InternalValue, reparsed.InternalValue);
        }
    }

    [Fact]
    public void Atan2Approx_45Degrees()
    {
        // atan2(1, 1) = pi/4 ≈ 0.7854
        Fixed result = Trig.Atan2Approx(Fixed.FromInt(1), Fixed.FromInt(1));
        // pi/4 << 16 = 51472
        Assert.Equal(51472, result.InternalValue);
    }

    [Fact]
    public void Atan2Approx_Origin()
    {
        Assert.Equal(Fixed.Zero, Trig.Atan2Approx(Fixed.Zero, Fixed.Zero));
    }

    [Fact]
    public void SinCosApprox_Zero()
    {
        Trig.SinCosApprox(Fixed.Zero, out Fixed sin, out Fixed cos);
        Assert.Equal(Fixed.FromInt(0), sin);
        Assert.Equal(Fixed.FromInt(1), cos);
    }

    [Fact]
    public void SinCosApprox_HalfPi()
    {
        // sin(pi/2) = 1, cos(pi/2) = 0
        Fixed halfPi = Fixed.Pi / 2;
        Trig.SinCosApprox(halfPi, out Fixed sin, out Fixed cos);
        // sin should be close to 1 (max error ~0.0005)
        Assert.True(sin > Fixed.FromFraction(65530, 65536));
        // cos should be close to 0
        Assert.True(cos.Absolute < Fixed.FromFraction(100, 65536));
    }
}
