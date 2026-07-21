using System;

namespace ZeroAD.Sim.Maths;

/// <summary>
/// 2D oriented-bounding-box (OBB) geometry helpers, ported from
/// <c>source/simulation2/helpers/Geometry.h/.cpp</c>. All math is fixed-point so results are
/// cross-platform deterministic — these underpin Obstruction placement tests and Footprint
/// spawn-point searches.
///
/// A square is defined by center <c>c</c>, two unit axes <c>u</c>/<c>v</c> (u × v = +1, i.e. v is
/// u rotated +90°), and <c>halfSize</c> = (half-width along u, half-height along v).
/// </summary>
public static class Geometry
{
    /// <summary>
    /// True if <paramref name="point"/> lies inside the square (centered at origin) defined by
    /// axes <paramref name="u"/>/<paramref name="v"/> and <paramref name="halfSize"/>.
    /// Ported from <c>Geometry.h:PointIsInSquare</c>: project onto each axis and compare.
    /// </summary>
    public static bool PointIsInSquare(FixedVector2D point, FixedVector2D u, FixedVector2D v, FixedVector2D halfSize)
        => point.Dot(u).Absolute <= halfSize.X && point.Dot(v).Absolute <= halfSize.Y;

    /// <summary>
    /// Returns (bx, by) such that every point inside the rotated rectangle has coordinates
    /// (x, y) with -bx &lt;= x &lt;= bx, -by &lt;= y &lt;= by — i.e. the half-extents of the
    /// rectangle's axis-aligned bounding box. Ported from <c>Geometry.cpp:GetHalfBoundingBox</c>.
    /// Used to widen a query AABB before a precise OBB test.
    /// </summary>
    public static FixedVector2D GetHalfBoundingBox(FixedVector2D u, FixedVector2D v, FixedVector2D halfSize)
        => new(
            u.X.Multiply(halfSize.X).Absolute + v.X.Multiply(halfSize.Y).Absolute,
            u.Y.Multiply(halfSize.X).Absolute + v.Y.Multiply(halfSize.Y).Absolute);

    /// <summary>
    /// Separating-Axis-Test helper for <see cref="TestSquareSquare"/>, ported 1:1 from
    /// <c>Geometry.cpp:SquareSAT</c>.
    ///
    /// <paramref name="a"/> is a corner of square 0, expressed relative to square 1's center.
    /// <paramref name="axis"/> is an edge direction of square 0. <paramref name="u1"/>/
    /// <paramref name="v1"/>/<paramref name="halfSize1"/> describe square 1. The edge normal is
    /// <c>p = axis.Perpendicular()</c>; square 1's 4 corners relative to <c>a</c> are
    /// <c>±u1*hw ± v1*hh - a</c>. If all 4 corners lie strictly on the same side of the line
    /// through <c>a</c> along <c>p</c> (all <c>RelativeOrientation(p) &gt; 0</c>), then this edge
    /// is a separating axis → squares don't overlap → return false. Otherwise return true
    /// (this axis doesn't separate them).
    /// </summary>
    private static bool SquareSAT(FixedVector2D a, FixedVector2D axis, FixedVector2D u1, FixedVector2D v1, FixedVector2D halfSize1)
    {
        Fixed hw = halfSize1.X;
        Fixed hh = halfSize1.Y;

        FixedVector2D p = axis.Perpendicular();
        // Check each of square 1's 4 corners (in its local frame, offset by -a) against the
        // edge normal p. If any corner is on the separating side (RelativeOrientation <= 0),
        // this axis is NOT a separating axis.
        if (p.RelativeOrientation(u1.Multiply(hw) + v1.Multiply(hh) - a) <= 0)
            return true;
        if (p.RelativeOrientation(u1.Multiply(hw) - v1.Multiply(hh) - a) <= 0)
            return true;
        if (p.RelativeOrientation(-u1.Multiply(hw) - v1.Multiply(hh) - a) <= 0)
            return true;
        if (p.RelativeOrientation(-u1.Multiply(hw) + v1.Multiply(hh) - a) <= 0)
            return true;

        return false;
    }

    /// <summary>
    /// OBB-vs-OBB intersection via the Separating Axis Theorem. Two squares overlap iff no
    /// separating axis exists among their 4 edge normals (2 per square). Ported from
    /// <c>Geometry.cpp:TestSquareSquare</c>. <paramref name="c0"/>/<paramref name="c1"/> are
    /// world-space centers; <paramref name="relativePos"/> = c1 - c0 (passed in by callers that
    /// already compute it).
    /// </summary>
    public static bool TestSquareSquare(
        FixedVector2D c0, FixedVector2D u0, FixedVector2D v0, FixedVector2D halfSize0,
        FixedVector2D c1, FixedVector2D u1, FixedVector2D v1, FixedVector2D halfSize1)
    {
        // Two opposite corners of each square, in world space.
        FixedVector2D corner0a = c0 + u0.Multiply(halfSize0.X) + v0.Multiply(halfSize0.Y);
        FixedVector2D corner0b = c0 - u0.Multiply(halfSize0.X) - v0.Multiply(halfSize0.Y);
        FixedVector2D corner1a = c1 + u1.Multiply(halfSize1.X) + v1.Multiply(halfSize1.Y);
        FixedVector2D corner1b = c1 - u1.Multiply(halfSize1.X) - v1.Multiply(halfSize1.Y);

        // SAT: test each square's 4 edge-normal axes (2 per square, each tested from both sides
        // via the two corners). If any axis separates them, they don't overlap.
        // Axes from square 0 (test square 1's corners against them):
        if (!SquareSAT(corner0a - c1, -u0, u1, v1, halfSize1)) return false;
        if (!SquareSAT(corner0a - c1,  v0, u1, v1, halfSize1)) return false;
        if (!SquareSAT(corner0b - c1,  u0, u1, v1, halfSize1)) return false;
        if (!SquareSAT(corner0b - c1, -v0, u1, v1, halfSize1)) return false;
        // Axes from square 1 (test square 0's corners against them):
        if (!SquareSAT(corner1a - c0, -u1, u0, v0, halfSize0)) return false;
        if (!SquareSAT(corner1a - c0,  v1, u0, v0, halfSize0)) return false;
        if (!SquareSAT(corner1b - c0,  u1, u0, v0, halfSize0)) return false;
        if (!SquareSAT(corner1b - c0, -v1, u0, v0, halfSize0)) return false;
        return true;
    }

    /// <summary>
    /// Returns the world-space coordinates of the <paramref name="index"/>-th point walking
    /// the perimeter of a rectangle (expanded by <paramref name="dr"/> rings), starting from
    /// <paramref name="center"/> with axes <paramref name="u"/>/<paramref name="v"/>. Used by
    /// Footprint.PickSpawnPoint to enumerate candidate spawn positions around a building's edge.
    ///
    /// This is a simplified port of <c>Geometry::GetPerimeterCoordinates</c>: it walks the
    /// rectangle perimeter at ring offset <paramref name="dr"/>, dividing each edge into
    /// <paramref name="perEdge"/> segments. <paramref name="index"/> wraps around the full
    /// perimeter (2*(perEdgeX + perEdgeZ) points).
    /// </summary>
    public static FixedVector2D GetPerimeterPoint(
        FixedVector2D center, FixedVector2D u, FixedVector2D v,
        Fixed halfWidth, Fixed halfHeight, int perEdgeX, int perEdgeZ, int dr, int index)
    {
        // Effective half-size at ring offset dr (each ring steps outward by the gap between
        // perimeter points; caller controls that gap via perEdgeX/perEdgeZ + dr scaling).
        Fixed hw = halfWidth + Fixed.FromInt(dr);
        Fixed hh = halfHeight + Fixed.FromInt(dr);

        int perim = 2 * (perEdgeX + perEdgeZ);
        int i = ((index % perim) + perim) % perim; // wrap to [0, perim)

        // Walk the 4 edges starting from corner (-hw along u, -hh along v), going +u first.
        // Edge layout (in local u,v space):
        //   bottom (v=-hh, u from -hw..+hw): perEdgeX segments
        //   right  (u=+hw, v from -hh..+hh): perEdgeZ segments
        //   top    (v=+hh, u from +hw..-hw): perEdgeX segments
        //   left   (u=-hw, v from +hh..-hh): perEdgeZ segments
        Fixed du, dv;
        if (i < perEdgeX)
        {
            // bottom edge: u from -hw to +hw
            Fixed t = perEdgeX == 0 ? Fixed.Zero : Fixed.FromInt(i) / Fixed.FromInt(perEdgeX);
            du = -hw + (hw * 2).Multiply(t);
            dv = -hh;
        }
        else if (i < perEdgeX + perEdgeZ)
        {
            int j = i - perEdgeX;
            Fixed t = perEdgeZ == 0 ? Fixed.Zero : Fixed.FromInt(j) / Fixed.FromInt(perEdgeZ);
            du = hw;
            dv = -hh + (hh * 2).Multiply(t);
        }
        else if (i < 2 * perEdgeX + perEdgeZ)
        {
            int j = i - perEdgeX - perEdgeZ;
            Fixed t = perEdgeX == 0 ? Fixed.Zero : Fixed.FromInt(j) / Fixed.FromInt(perEdgeX);
            du = hw - (hw * 2).Multiply(t);
            dv = hh;
        }
        else
        {
            int j = i - 2 * perEdgeX - perEdgeZ;
            Fixed t = perEdgeZ == 0 ? Fixed.Zero : Fixed.FromInt(j) / Fixed.FromInt(perEdgeZ);
            du = -hw;
            dv = hh - (hh * 2).Multiply(t);
        }

        // Transform local (du,dv) back to world space: center + u*du + v*dv
        return center + u.Multiply(du) + v.Multiply(dv);
    }
}
