using System;
using System.Runtime.CompilerServices;

namespace ZeroAD.Sim.Maths;

/// <summary>
/// 3D vector using Fixed components. Direct translation of <c>CFixedVector3D</c>
/// from <c>source/maths/FixedVector3D.h</c>.
/// </summary>
public readonly struct FixedVector3D : IEquatable<FixedVector3D>
{
    public readonly Fixed X;
    public readonly Fixed Y;
    public readonly Fixed Z;

    public FixedVector3D(Fixed x, Fixed y, Fixed z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public bool Equals(FixedVector3D other) => X == other.X && Y == other.Y && Z == other.Z;
    public override bool Equals(object? obj) => obj is FixedVector3D v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public static bool operator ==(FixedVector3D a, FixedVector3D b) => a.Equals(b);
    public static bool operator !=(FixedVector3D a, FixedVector3D b) => !a.Equals(b);

    public static FixedVector3D operator +(FixedVector3D a, FixedVector3D b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static FixedVector3D operator -(FixedVector3D a, FixedVector3D b) =>
        new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static FixedVector3D operator -(FixedVector3D a) => new(-a.X, -a.Y, -a.Z);

    /// <summary>Length via integer sqrt. Uses 64-bit intermediates.</summary>
    public Fixed Length()
    {
        ulong xx = SquareU64(X);
        ulong yy = SquareU64(Y);
        ulong zz = SquareU64(Z);
        ulong t = xx + yy;
        ulong d2 = t + zz;
        uint d = MathInt.Sqrt64(d2);
        return new Fixed((int)d);
    }

    /// <summary>Normalize to length ~1. If length is 0, does nothing.</summary>
    public FixedVector3D Normalized()
    {
        Fixed l = Length();
        if (l.IsZero)
            return this;
        return new FixedVector3D(X / l, Y / l, Z / l);
    }

    /// <summary>Normalize to length ~n. If length is 0, does nothing.</summary>
    public FixedVector3D Normalized(Fixed n)
    {
        Fixed l = Length();
        if (l.IsZero)
            return this;
        return new FixedVector3D(X.MulDiv(n, l), Y.MulDiv(n, l), Z.MulDiv(n, l));
    }

    /// <summary>Cross product. Uses 64-bit intermediates.</summary>
    public FixedVector3D Cross(FixedVector3D v)
    {
        long y_vz = (long)Y.InternalValue * (long)v.Z.InternalValue;
        long z_vy = (long)Z.InternalValue * (long)v.Y.InternalValue;
        long x = (y_vz - z_vy) >> Fixed.FractBits;

        long z_vx = (long)Z.InternalValue * (long)v.X.InternalValue;
        long x_vz = (long)X.InternalValue * (long)v.Z.InternalValue;
        long y = (z_vx - x_vz) >> Fixed.FractBits;

        long x_vy = (long)X.InternalValue * (long)v.Y.InternalValue;
        long y_vx = (long)Y.InternalValue * (long)v.X.InternalValue;
        long z = (x_vy - y_vx) >> Fixed.FractBits;

        return new FixedVector3D(
            new Fixed((int)x),
            new Fixed((int)y),
            new Fixed((int)z));
    }

    /// <summary>Dot product. Uses 64-bit intermediates.</summary>
    public Fixed Dot(FixedVector3D v)
    {
        long x = (long)X.InternalValue * (long)v.X.InternalValue;
        long y = (long)Y.InternalValue * (long)v.Y.InternalValue;
        long z = (long)Z.InternalValue * (long)v.Z.InternalValue;
        long sum = x + y + z;
        sum >>= Fixed.FractBits;
        return new Fixed((int)sum);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static ulong SquareU64(Fixed f) =>
        (ulong)((long)f.InternalValue * (long)f.InternalValue);
}
