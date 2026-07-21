using Xunit;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Tests for the OBB geometry helpers ported from source/simulation2/helpers/Geometry.
/// These underpin Obstruction placement tests and Footprint spawn searches, so the
/// PointIsInSquare / TestSquareSquare / GetHalfBoundingBox behavior must be exact.
/// </summary>
public class GeometryTests
{
    // Axis-aligned unit axes (no rotation) — the common case for buildings.
    private static readonly FixedVector2D U = new(Fixed.FromInt(1), Fixed.Zero);
    private static readonly FixedVector2D V = new(Fixed.Zero, Fixed.FromInt(1));

    [Fact]
    public void PointIsInSquare_CenterInside_ReturnsTrue()
    {
        var half = new FixedVector2D(Fixed.FromInt(5), Fixed.FromInt(5));
        Assert.True(Geometry.PointIsInSquare(new FixedVector2D(Fixed.Zero, Fixed.Zero), U, V, half));
    }

    [Fact]
    public void PointIsInSquare_OnEdge_ReturnsTrue()
    {
        // On the boundary is inclusive (<=).
        var half = new FixedVector2D(Fixed.FromInt(5), Fixed.FromInt(5));
        Assert.True(Geometry.PointIsInSquare(new FixedVector2D(Fixed.FromInt(5), Fixed.Zero), U, V, half));
        Assert.True(Geometry.PointIsInSquare(new FixedVector2D(Fixed.Zero, Fixed.FromInt(5)), U, V, half));
    }

    [Fact]
    public void PointIsInSquare_Outside_ReturnsFalse()
    {
        var half = new FixedVector2D(Fixed.FromInt(5), Fixed.FromInt(5));
        Assert.False(Geometry.PointIsInSquare(new FixedVector2D(Fixed.FromInt(6), Fixed.Zero), U, V, half));
        Assert.False(Geometry.PointIsInSquare(new FixedVector2D(Fixed.Zero, Fixed.FromInt(6)), U, V, half));
        // (4,4) is INSIDE a half=5 axis-aligned box (|4|<=5 on both axes), so it must be true.
        Assert.True(Geometry.PointIsInSquare(new FixedVector2D(Fixed.FromInt(4), Fixed.FromInt(4)), U, V, half));
    }

    [Fact]
    public void PointIsInSquare_RotatedSquare_WorksWithAxes()
    {
        // A 45°-rotated square: u and v are the diagonals of the unit square.
        // halfSize (along u, along v) = (sqrt(0.5), sqrt(0.5)) makes a 1×1 diamond.
        Fixed s = Fixed.FromFloat(0.7071f);
        var ru = new FixedVector2D(s, s);       // 45° axis
        var rv = new FixedVector2D(-s, s);      // perpendicular
        var half = new FixedVector2D(s, s);
        // The point (0.5, 0) should be inside this diamond (|proj onto ru| = 0.5*0.7071 ≈ 0.354 <= 0.707).
        Assert.True(Geometry.PointIsInSquare(new FixedVector2D(Fixed.FromFloat(0.5f), Fixed.Zero), ru, rv, half));
        // (1, 0): proj onto ru = 1*0.707 ≈ 0.707, on the boundary (<=), so inside.
        Assert.True(Geometry.PointIsInSquare(new FixedVector2D(Fixed.FromInt(1), Fixed.Zero), ru, rv, half));
        // (2, 0): proj onto ru = 2*0.707 ≈ 1.414 > 0.707, clearly outside.
        Assert.False(Geometry.PointIsInSquare(new FixedVector2D(Fixed.FromInt(2), Fixed.Zero), ru, rv, half));
    }

    [Fact]
    public void GetHalfBoundingBox_AxisAligned_ReturnsHalfSize()
    {
        var half = new FixedVector2D(Fixed.FromInt(3), Fixed.FromInt(4));
        var bb = Geometry.GetHalfBoundingBox(U, V, half);
        Assert.Equal(Fixed.FromInt(3), bb.X);
        Assert.Equal(Fixed.FromInt(4), bb.Y);
    }

    [Fact]
    public void GetHalfBoundingBox_Rotated45_ReturnsExpandedBox()
    {
        // 45° rotation: AABB half-extent = (hw*|cos| + hh*|sin|, hw*|sin| + hh*|cos|).
        // For hw=hh=5, cos=sin≈0.707: (5*0.707+5*0.707) ≈ 7.07 on each axis.
        Fixed s = Fixed.FromFloat(0.7071f);
        var ru = new FixedVector2D(s, s);
        var rv = new FixedVector2D(-s, s);
        var half = new FixedVector2D(Fixed.FromInt(5), Fixed.FromInt(5));
        var bb = Geometry.GetHalfBoundingBox(ru, rv, half);
        // Both axes ≈ 7.07; allow small fixed-point slack.
        Assert.True(bb.X > Fixed.FromFloat(7.0f) && bb.X < Fixed.FromFloat(7.2f));
        Assert.True(bb.Y > Fixed.FromFloat(7.0f) && bb.Y < Fixed.FromFloat(7.2f));
    }

    [Fact]
    public void TestSquareSquare_OverlappingAxisAligned_ReturnsTrue()
    {
        var half = new FixedVector2D(Fixed.FromInt(5), Fixed.FromInt(5));
        var c0 = new FixedVector2D(Fixed.Zero, Fixed.Zero);
        var c1 = new FixedVector2D(Fixed.FromInt(3), Fixed.Zero); // 3 apart, each half=5 → overlap
        Assert.True(Geometry.TestSquareSquare(c0, U, V, half, c1, U, V, half));
    }

    [Fact]
    public void TestSquareSquare_SeparatedAxisAligned_ReturnsFalse()
    {
        var half = new FixedVector2D(Fixed.FromInt(5), Fixed.FromInt(5));
        var c0 = new FixedVector2D(Fixed.Zero, Fixed.Zero);
        var c1 = new FixedVector2D(Fixed.FromInt(20), Fixed.Zero); // 20 apart, sum of halves=10 → separated
        Assert.False(Geometry.TestSquareSquare(c0, U, V, half, c1, U, V, half));
    }

    [Fact]
    public void TestSquareSquare_TouchingEdge_ReturnsTrue()
    {
        // Squares touching exactly at the edge (distance == sum of halves) are considered overlapping
        // by SAT (<= comparisons), matching 0 A.D. behavior where touching blocks placement.
        var half = new FixedVector2D(Fixed.FromInt(5), Fixed.FromInt(5));
        var c0 = new FixedVector2D(Fixed.Zero, Fixed.Zero);
        var c1 = new FixedVector2D(Fixed.FromInt(10), Fixed.Zero); // 10 apart, sum of halves=10 → touching
        Assert.True(Geometry.TestSquareSquare(c0, U, V, half, c1, U, V, half));
    }

    [Fact]
    public void TestSquareSquare_Identical_ReturnsTrue()
    {
        var half = new FixedVector2D(Fixed.FromInt(5), Fixed.FromInt(5));
        var c = new FixedVector2D(Fixed.FromInt(7), Fixed.FromInt(3));
        Assert.True(Geometry.TestSquareSquare(c, U, V, half, c, U, V, half));
    }

    [Fact]
    public void TestSquareSquare_RotatedVsAxisAligned_CorrectCollision()
    {
        // A long thin rotated bar vs an axis-aligned square: SAT must catch the rotation.
        var c0 = new FixedVector2D(Fixed.Zero, Fixed.Zero);
        // Bar: 10 long (along u), 1 thin (along v), rotated 45°.
        Fixed s = Fixed.FromFloat(0.7071f);
        var barU = new FixedVector2D(s, s);
        var barV = new FixedVector2D(-s, s);
        var barHalf = new FixedVector2D(Fixed.FromInt(5), Fixed.Zero);
        // Square at (3, 3): in the bar's path once rotated.
        var c1 = new FixedVector2D(Fixed.FromInt(3), Fixed.FromInt(3));
        var sqHalf = new FixedVector2D(Fixed.FromInt(1), Fixed.FromInt(1));
        Assert.True(Geometry.TestSquareSquare(c0, barU, barV, barHalf, c1, U, V, sqHalf));
    }
}
