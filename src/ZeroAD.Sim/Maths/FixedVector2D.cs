using System;
using System.Runtime.CompilerServices;

namespace ZeroAD.Sim.Maths;

/// <summary>
/// 2D vector using Fixed components. Direct translation of <c>CFixedVector2D</c>
/// from <c>source/maths/FixedVector2D.h</c>.
/// </summary>
public readonly struct FixedVector2D : IEquatable<FixedVector2D>
{
    public readonly Fixed X;
    public readonly Fixed Y;

    public FixedVector2D(Fixed x, Fixed y)
    {
        X = x;
        Y = y;
    }

    public static FixedVector2D Zero => new(Fixed.Zero, Fixed.Zero);

    public bool IsZero => X.IsZero && Y.IsZero;

    public bool Equals(FixedVector2D other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is FixedVector2D v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y);

    public static bool operator ==(FixedVector2D a, FixedVector2D b) => a.Equals(b);
    public static bool operator !=(FixedVector2D a, FixedVector2D b) => !a.Equals(b);

    public static FixedVector2D operator +(FixedVector2D a, FixedVector2D b) =>
        new(a.X + b.X, a.Y + b.Y);

    public static FixedVector2D operator -(FixedVector2D a, FixedVector2D b) =>
        new(a.X - b.X, a.Y - b.Y);

    public static FixedVector2D operator -(FixedVector2D a) => new(-a.X, -a.Y);

    public static FixedVector2D operator *(FixedVector2D a, int n) => new(a.X * n, a.Y * n);
    public static FixedVector2D operator /(FixedVector2D a, int n) => new(a.X / n, a.Y / n);

    /// <summary>Multiply by a Fixed scalar. Named to make overflow potential explicit.</summary>
    public FixedVector2D Multiply(Fixed n) => new(X.Multiply(n), Y.Multiply(n));

    /// <summary>Length via integer sqrt. Won't overflow if result fits in Fixed range.</summary>
    public Fixed Length()
    {
        ulong xx = SquareU64(X);
        ulong yy = SquareU64(Y);
        ulong d2 = xx + yy;
        uint d = MathInt.Sqrt64(d2);
        return new Fixed((int)d);
    }

    /// <summary>Compare length to a value without sqrting. Returns -1/0/+1.</summary>
    public int CompareLength(Fixed cmp)
    {
        ulong d2 = SquareU64(X) + SquareU64(Y);
        ulong cmpSquared = SquareU64(cmp);
        return d2 < cmpSquared ? -1 : d2 > cmpSquared ? 1 : 0;
    }

    /// <summary>Compare length to another vector's length. Returns -1/0/+1.</summary>
    public int CompareLength(FixedVector2D other)
    {
        ulong d2 = SquareU64(X) + SquareU64(Y);
        ulong od2 = SquareU64(other.X) + SquareU64(other.Y);
        return d2 < od2 ? -1 : d2 > od2 ? 1 : 0;
    }

    /// <summary>Normalize to length ~1. If length is 0, does nothing.</summary>
    public FixedVector2D Normalized()
    {
        if (IsZero)
            return this;
        Fixed l = Length();
        return new FixedVector2D(X / l, Y / l);
    }

    /// <summary>Normalize to length ~n. If length is 0, does nothing.</summary>
    public FixedVector2D Normalized(Fixed n)
    {
        Fixed l = Length();
        if (l.IsZero)
            return this;
        return new FixedVector2D(X.MulDiv(n, l), Y.MulDiv(n, l));
    }

    /// <summary>Dot product. Uses 64-bit intermediate.</summary>
    public Fixed Dot(FixedVector2D v)
    {
        long x = (long)X.InternalValue * (long)v.X.InternalValue;
        long y = (long)Y.InternalValue * (long)v.Y.InternalValue;
        long sum = x + y;
        sum >>= Fixed.FractBits;
        return new Fixed((int)sum);
    }

    /// <summary>Returns -1/0/+1: opposite/perpendicular/same direction.</summary>
    public int RelativeOrientation(FixedVector2D v)
    {
        long x = (long)X.InternalValue * (long)v.X.InternalValue;
        long y = (long)Y.InternalValue * (long)v.Y.InternalValue;
        return x > -y ? 1 : x < -y ? -1 : 0;
    }

    public FixedVector2D Perpendicular() => new(Y, -X);

    /// <summary>Rotate anticlockwise by angle (radians, as Fixed).</summary>
    public FixedVector2D Rotate(Fixed angle)
    {
        Trig.SinCosApprox(angle, out Fixed s, out Fixed c);
        return new FixedVector2D(
            X.Multiply(c) + Y.Multiply(s),
            Y.Multiply(c) - X.Multiply(s));
    }

    /// <summary>Square a Fixed's internal value as unsigned 64-bit.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static ulong SquareU64(Fixed f) =>
        (ulong)((long)f.InternalValue * (long)f.InternalValue);
}
