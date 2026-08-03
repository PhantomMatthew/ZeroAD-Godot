using System;

namespace ZeroAD.Sim.RmgenMath;

/// <summary>Safe, platform-consistent math functions. 逐字移植自 globalscripts/Math.js。
/// 原版替换 Math.cos/sin/atan/atan2/pow/exp/log 为自定义实现（Taylor 展开 + 魔法常数），
/// 专门为跨平台确定性。C# System.Math 用平台 CRT，结果不同——必须用此 SafeMath。
/// 全部用 double。</summary>
public static class SafeMath
{
    public const double PI = Math.PI;
    public const double E = Math.E;
    public const double LOG2E = 1.4426950408889634;

    /// <summary>JS Math.floor 语义（向 -∞ 取整）。C# Math.Floor 一致。</summary>
    public static double Floor(double x) => Math.Floor(x);

    /// <summary>JS Math.ceil 语义（向 +∞ 取整）。C# Math.Ceiling 一致。</summary>
    public static double Ceil(double x) => Math.Ceiling(x);

    /// <summary>JS Math.round 语义：半向 +∞。Math.round(0.5)=1, Math.round(-0.5)=0。
    /// C# Math.Round 默认 banker's rounding——不能直接用。</summary>
    public static double Round(double x) => Math.Floor(x + 0.5);

    /// <summary>JS Math.abs。</summary>
    public static double Abs(double x) => Math.Abs(x);

    /// <summary>JS Math.sqrt（原版未替换，用平台原生 sqrt）。</summary>
    public static double Sqrt(double x) => Math.Sqrt(x);

    /// <summary>JS Math.max。</summary>
    public static double Max(double a, double b) => Math.Max(a, b);

    /// <summary>JS Math.min。</summary>
    public static double Min(double a, double b) => Math.Min(a, b);

    /// <summary>x²（不调用 pow）。</summary>
    public static double Square(double x) => x * x;

    // ── 三角函数（逐字移植 Math.js）──

    /// <summary>cos(a) — 9 阶 Taylor + 常数 0.5000000025619951（使 cos(pi/2)=0）。</summary>
    public static double Cos(double a)
    {
        // 折叠到 [0, π]
        a = (a + PI) % (2 * PI);
        a = Abs((2 * PI + a) % (2 * PI) - PI);

        // b=0 if a<π/2, b=1 if a>π/2
        double b = (a - PI / 2) + Abs(a - PI / 2);
        b /= (b + 1e-30);

        a = b * PI - a;
        double c = 1 - 2 * b;

        return c * (1 - a * a * (0.5000000025619951 - a * a * (1.0 / 24 - a * a * (1.0 / 720 - a * a * (1.0 / 40320 - a * a * (1.0 / 3628800 - a * a / 479001600))))));
    }

    /// <summary>sin(a) = cos(a - π/2)。</summary>
    public static double Sin(double a) => Cos(a - PI / 2);

    /// <summary>atan(a) — 分段 + 常数 1.0000000000390272。返回 [-π/2, π/2]。</summary>
    public static double Atan(double a)
    {
        double tanPiBy6 = 0.5773502691896257;
        double tanPiBy12 = 0.2679491924311227;
        double sign = 1;
        bool inverted = false;
        double tanPiBy6Shift = 0;

        if (a < 0 || double.IsNegativeInfinity(1.0 / a))
        {
            sign = -1;
            a *= -1;
        }

        if (a > 1)
        {
            inverted = true;
            a = 1 / a;
        }

        if (a > tanPiBy12)
        {
            tanPiBy6Shift = PI / 6;
            a = (a - tanPiBy6) / (1 + tanPiBy6 * a);
        }

        double r = a * (1.0000000000390272 - a * a * (1.0 / 3 - a * a * (1.0 / 5 - a * a * (1.0 / 7 - a * a * (1.0 / 9 - a * a * (1.0 / 11 - a * a * (1.0 / 13 - a * a / 15)))))));

        r += tanPiBy6Shift;
        if (inverted)
            r = PI / 2 - r;
        return sign * r;
    }

    /// <summary>atan2(y, x) — 象限逻辑 + 1/x === -Infinity 检测。返回 [-π, π]。</summary>
    public static double Atan2(double y, double x)
    {
        double ux = Abs(x);
        double uy = Abs(y);
        double r;

        if (uy == 0)
            r = 0;
        else if (double.IsPositiveInfinity(uy))
        {
            if (double.IsPositiveInfinity(ux))
                r = PI / 4;
            else
                r = PI / 2;
        }
        else
        {
            if (double.IsPositiveInfinity(ux))
                r = 0;
            else
                r = Atan(uy / ux);
        }

        if (x < 0 || double.IsNegativeInfinity(1.0 / x))
            return (y < 0 || double.IsNegativeInfinity(1.0 / y)) ? -PI + r : PI - r;
        return (y < 0 || double.IsNegativeInfinity(1.0 / y)) ? -r : r;
    }

    // ── 幂/指数/对数（逐字移植）──

    /// <summary>pow(x, y) — 整数指数走 intPow，否则 exp(y*log(x))。</summary>
    public static double Pow(double x, double y)
    {
        if (Round(y) == y)
        {
            if (y >= 0)
                return IntPow(x, (long)y);
            return 1 / IntPow(x, (long)(-y));
        }
        return Exp(y * Log(x));
    }

    /// <summary>exp(x) — 整数/小数分离 + 反向 Taylor。</summary>
    public static double Exp(double x)
    {
        double iPart;
        if (x < 0)
            iPart = 1 / IntPow(E, -(long)Floor(x));
        else
            iPart = IntPow(E, (long)Floor(x));

        if (x == Floor(x))
            return iPart;

        x -= Floor(x);

        double dPart = 1;
        for (int i = 22; i > 0; i--)
            dPart = 1 + x * dPart / i;

        return iPart * dPart;
    }

    /// <summary>log(x) — 50 位二进制对数算法，转自然对数。</summary>
    public static double Log(double x)
    {
        if (!(x >= 0))
            return double.NaN;
        if (x == 0)
            return double.NegativeInfinity;
        if (double.IsPositiveInfinity(x))
            return x;

        int precisionBits = 50;
        int log = 0;
        double i;
        if (x >= 1)
        {
            for (i = 1; i <= x; i *= 2)
                log++;
            log--;
            i /= 2;
        }
        else
        {
            for (i = 1; i > x; i /= 2)
                log--;
        }

        double y = x / i;
        if (y <= 1)
            return log / LOG2E;

        int m = 0;
        double add = 1;
        while (true)
        {
            while (m <= precisionBits && y < 4)
            {
                m++;
                y *= y;
                add /= 2;
            }
            if (m > precisionBits)
                break;
            log += (int)add;
            y /= 2;
        }

        return log / LOG2E;
    }

    /// <summary>正整数次幂（二进制展开）。</summary>
    private static double IntPow(double x, long y)
    {
        if (double.IsInfinity(Abs(y)))
        {
            if (Abs(x) == 1)
                return double.NaN;
            if (Abs(x) < 1 && y > 0 || Abs(x) > 1 && y < 0)
                return 0;
            return double.PositiveInfinity;
        }

        var powers = new System.Collections.Generic.List<double> { x };
        var binary = new System.Collections.Generic.List<long> { 1 };
        int idx = 0;
        for (long e = 2; e <= y; e *= 2)
        {
            powers.Add(powers[idx] * powers[idx]);
            binary.Add(e);
            idx++;
        }

        double result = 1;
        int l = binary.Count;
        while (y > 0)
        {
            l--;
            if (binary[l] <= y)
            {
                result *= powers[l];
                y -= binary[l];
            }
        }
        return result;
    }

    // ── 距离辅助 ──

    public static double EuclidDistance2DSquared(double x1, double y1, double x2, double y2)
        => Square(x2 - x1) + Square(y2 - y1);

    public static double EuclidDistance2D(double x1, double y1, double x2, double y2)
        => Sqrt(EuclidDistance2DSquared(x1, y1, x2, y2));

    public static double EuclidDistance3DSquared(double x1, double y1, double z1, double x2, double y2, double z2)
        => Square(x2 - x1) + Square(y2 - y1) + Square(z2 - z1);

    public static double EuclidDistance3D(double x1, double y1, double z1, double x2, double y2, double z2)
        => Sqrt(EuclidDistance3DSquared(x1, y1, z1, x2, y2, z2));

    /// <summary>clamp(value, min, max)。</summary>
    public static double Clamp(double value, double min, double max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
