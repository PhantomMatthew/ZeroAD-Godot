using Xunit;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests.Maths;

public class VectorTests
{
    [Fact]
    public void Sqrt64_PerfectSquares()
    {
        Assert.Equal(0u, MathInt.Sqrt64(0));
        Assert.Equal(1u, MathInt.Sqrt64(1));
        Assert.Equal(2u, MathInt.Sqrt64(4));
        Assert.Equal(3u, MathInt.Sqrt64(9));
        Assert.Equal(4u, MathInt.Sqrt64(16));
        Assert.Equal(100u, MathInt.Sqrt64(10000));
        Assert.Equal(1000u, MathInt.Sqrt64(1000000));
    }

    [Fact]
    public void Sqrt64_LargeValue()
    {
        // (2^31 - 1) → sqrt = 46340
        Assert.Equal(46340u, MathInt.Sqrt64(2147483647));
        // 2^32 → sqrt = 65536
        Assert.Equal(65536u, MathInt.Sqrt64(1UL << 32));
    }

    [Fact]
    public void Sqrt64_NonPerfectSquare_FloorCorrect()
    {
        // sqrt(2) floor = 1
        Assert.Equal(1u, MathInt.Sqrt64(2));
        // sqrt(3) floor = 1
        Assert.Equal(1u, MathInt.Sqrt64(3));
        // sqrt(5) floor = 2
        Assert.Equal(2u, MathInt.Sqrt64(5));
        // sqrt(15) floor = 3
        Assert.Equal(3u, MathInt.Sqrt64(15));
    }

    [Fact]
    public void Vector2D_Length_UnitVectors()
    {
        var v = new FixedVector2D(Fixed.FromInt(3), Fixed.FromInt(4));
        Assert.Equal(Fixed.FromInt(5), v.Length());
    }

    [Fact]
    public void Vector2D_Dot()
    {
        var a = new FixedVector2D(Fixed.FromInt(1), Fixed.FromInt(0));
        var b = new FixedVector2D(Fixed.FromInt(0), Fixed.FromInt(1));
        Assert.Equal(Fixed.Zero, a.Dot(b));

        var c = new FixedVector2D(Fixed.FromInt(2), Fixed.FromInt(3));
        var d = new FixedVector2D(Fixed.FromInt(4), Fixed.FromInt(5));
        // 2*4 + 3*5 = 23
        Assert.Equal(Fixed.FromInt(23), c.Dot(d));
    }

    [Fact]
    public void Vector2D_Normalize_UnitLength()
    {
        var v = new FixedVector2D(Fixed.FromInt(3), Fixed.FromInt(4));
        Fixed n = v.Normalized().Length();
        // Normalizing (3,4)/5 gives (39321, 52428) internally;
        // its length computes to 65535 (i.e. 0.99998), not exactly 65536.
        Assert.Equal(65535, n.InternalValue);
    }

    [Fact]
    public void Vector2D_Perpendicular()
    {
        var v = new FixedVector2D(Fixed.FromInt(1), Fixed.FromInt(2));
        var p = v.Perpendicular();
        Assert.Equal(new FixedVector2D(Fixed.FromInt(2), -Fixed.FromInt(1)), p);
    }

    [Fact]
    public void Vector2D_Rotate_90Degrees()
    {
        var v = new FixedVector2D(Fixed.FromInt(1), Fixed.Zero);
        Fixed halfPi = Fixed.Pi / 2;
        var rotated = v.Rotate(halfPi);
        // Engine uses screen-space Y-down, so "anticlockwise" gives (cos, -sin).
        // cos(pi/2)≈3/65536, sin(pi/2)≈65535/65536 via fifth-order approx.
        Assert.Equal(3, rotated.X.InternalValue);
        Assert.Equal(-65535, rotated.Y.InternalValue);
    }

    [Fact]
    public void Vector2D_CompareLength()
    {
        var v = new FixedVector2D(Fixed.FromInt(3), Fixed.FromInt(4)); // length=5
        Assert.Equal(-1, v.CompareLength(Fixed.FromInt(10)));
        Assert.Equal(1, v.CompareLength(Fixed.FromInt(3)));
        Assert.Equal(0, v.CompareLength(Fixed.FromInt(5)));
    }

    [Fact]
    public void Vector3D_Length()
    {
        var v = new FixedVector3D(Fixed.FromInt(1), Fixed.FromInt(2), Fixed.FromInt(2));
        // sqrt(1+4+4) = sqrt(9) = 3
        Assert.Equal(Fixed.FromInt(3), v.Length());
    }

    [Fact]
    public void Vector3D_Dot()
    {
        var a = new FixedVector3D(Fixed.FromInt(1), Fixed.FromInt(2), Fixed.FromInt(3));
        var b = new FixedVector3D(Fixed.FromInt(4), Fixed.FromInt(5), Fixed.FromInt(6));
        // 1*4 + 2*5 + 3*6 = 32
        Assert.Equal(Fixed.FromInt(32), a.Dot(b));
    }

    [Fact]
    public void Vector3D_Cross()
    {
        var a = new FixedVector3D(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero);
        var b = new FixedVector3D(Fixed.Zero, Fixed.FromInt(1), Fixed.Zero);
        // (1,0,0) × (0,1,0) = (0,0,1)
        var cross = a.Cross(b);
        Assert.Equal(Fixed.Zero, cross.X);
        Assert.Equal(Fixed.Zero, cross.Y);
        Assert.Equal(Fixed.FromInt(1), cross.Z);
    }
}
