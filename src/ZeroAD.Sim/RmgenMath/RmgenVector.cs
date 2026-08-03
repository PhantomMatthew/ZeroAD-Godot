using System;

namespace ZeroAD.Sim.RmgenMath;

/// <summary>2D 向量（逐字移植自 globalscripts/vector.js）。全 double。
/// 三角函数用 SafeMath（保证跨平台一致）。</summary>
public struct RmgenVector2D
{
    public double X, Y;

    public RmgenVector2D(double x, double y) { X = x; Y = y; }

    public RmgenVector2D Clone() => new(X, Y);

    // ── Mutating（返回 this 以支持链式调用，与 JS 一致）──
    // 注意：struct 不能返回 this 引用——用 void（调用方直接链式不太方便，但语义一致）。
    // rmgen 实际用法多数是静态方法返回新实例（Vector2D.add/sub/mult），mutating 方法较少。

    public void Add(RmgenVector2D v) { X += v.X; Y += v.Y; }
    public void Sub(RmgenVector2D v) { X -= v.X; Y -= v.Y; }
    public void Mult(double f) { X *= f; Y *= f; }
    public void Div(double f) { X /= f; Y /= f; }

    public void Normalize()
    {
        double mag = Length();
        if (mag != 0) Div(mag);
    }

    public void Rotate(double angle)
    {
        double sin = SafeMath.Sin(angle);
        double cos = SafeMath.Cos(angle);
        double nx = X * cos + Y * sin;
        double ny = -X * sin + Y * cos;
        X = nx; Y = ny;
    }

    public void RotateAround(double angle, RmgenVector2D center)
    {
        Sub(center);
        Rotate(angle);
        Add(center);
    }

    public void Round() { X = SafeMath.Round(X); Y = SafeMath.Round(Y); }
    public void Floor() { X = SafeMath.Floor(X); Y = SafeMath.Floor(Y); }

    // ── 非变更查询 ──

    public RmgenVector2D Perpendicular() => new(-Y, X);
    public double Dot(RmgenVector2D v) => X * v.X + Y * v.Y;
    public double Cross(RmgenVector2D v) => X * v.Y - Y * v.X;
    public double LengthSquared() => Dot(this);
    public double Length() => SafeMath.Sqrt(LengthSquared());

    public int CompareLength(RmgenVector2D v)
        => Math.Sign(LengthSquared() - v.LengthSquared());

    public double DistanceToSquared(RmgenVector2D v)
        => SafeMath.EuclidDistance2DSquared(X, Y, v.X, v.Y);
    public double DistanceTo(RmgenVector2D v)
        => SafeMath.EuclidDistance2D(X, Y, v.X, v.Y);

    public double AngleTo(RmgenVector2D v)
        => SafeMath.Atan2(v.X - X, v.Y - Y);

    // ── 静态方法 ──

    public static RmgenVector2D From3D(RmgenVector3D v) => new(v.X, v.Z);
    public static RmgenVector2D Add(RmgenVector2D a, RmgenVector2D b) => new(a.X + b.X, a.Y + b.Y);
    public static RmgenVector2D Sub(RmgenVector2D a, RmgenVector2D b) => new(a.X - b.X, a.Y - b.Y);
    public static RmgenVector2D Mult(RmgenVector2D v, double f) => new(v.X * f, v.Y * f);
    public static RmgenVector2D Div(RmgenVector2D v, double f) => new(v.X / f, v.Y / f);
    public static bool IsEqualTo(RmgenVector2D a, RmgenVector2D b) => a.X == b.X && a.Y == b.Y;
}

/// <summary>3D 向量（逐字移植自 vector.js）。全 double。</summary>
public struct RmgenVector3D
{
    public double X, Y, Z;

    public RmgenVector3D(double x, double y, double z) { X = x; Y = y; Z = z; }

    public RmgenVector3D Clone() => new(X, Y, Z);

    public void Add(RmgenVector3D v) { X += v.X; Y += v.Y; Z += v.Z; }
    public void Sub(RmgenVector3D v) { X -= v.X; Y -= v.Y; Z -= v.Z; }
    public void Mult(double f) { X *= f; Y *= f; Z *= f; }
    public void Div(double f) { X /= f; Y /= f; Z /= f; }

    public double Dot(RmgenVector3D v) => X * v.X + Y * v.Y + Z * v.Z;
    public double LengthSquared() => Dot(this);
    public double Length() => SafeMath.Sqrt(LengthSquared());
    public double DistanceToSquared(RmgenVector3D v)
        => SafeMath.EuclidDistance3DSquared(X, Y, Z, v.X, v.Y, v.Z);
    public double DistanceTo(RmgenVector3D v) => SafeMath.Sqrt(DistanceToSquared(v));

    public void Normalize()
    {
        double mag = Length();
        if (mag != 0) Div(mag);
    }

    public static RmgenVector3D Add(RmgenVector3D a, RmgenVector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static RmgenVector3D Sub(RmgenVector3D a, RmgenVector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static RmgenVector3D Mult(RmgenVector3D v, double f) => new(v.X * f, v.Y * f, v.Z * f);
}
