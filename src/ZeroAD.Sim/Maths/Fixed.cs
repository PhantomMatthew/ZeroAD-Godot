using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ZeroAD.Sim.Maths;

/// <summary>
/// Fixed-point number: 1-bit sign, 15-bit integer, 16-bit fraction (Q15.16).
/// Direct C# translation of <c>CFixed_15_16</c> from <c>source/maths/Fixed.h</c>.
/// All arithmetic is deterministic across platforms (pure integer math).
/// </summary>
public readonly struct Fixed : IEquatable<Fixed>, IComparable<Fixed>
{
    // fract_bits = 16, fract_pow2 = 65536
    internal const int FractBits = 16;
    internal const int FractPow2 = 1 << FractBits; // 65536

    internal readonly int _value;

    internal Fixed(int value)
    {
        _value = value;
    }

    // --- Factory methods ---

    public static Fixed Zero => new(0);
    public static Fixed Epsilon => new(1);
    public static Fixed One => new(FractPow2);
    /// <summary>最大有限值(0x7FFFFFFF ≈ 32767.99998;C++ 同款钳制界)。</summary>
    public static Fixed MaxValue => new(int.MaxValue);

    /// <summary>pi &lt;&lt; 16 = 205887</summary>
    public static Fixed Pi => new(205887);

    /// <summary>整数底整数幂(精确,64 位中间;溢出钳到 MaxValue)。替代 MathF.Pow 的
    /// 整数指数场景。指数为负时返回倒数幂(除法亦确定)。</summary>
    public static Fixed Pow(int @base, int exponent)
    {
        if (exponent < 0)
        {
            Fixed p = Pow(@base, -exponent);
            return p.IsZero ? MaxValue : One / p;
        }
        long acc = 1L << FractBits;   // 1.0 in fixed
        long b = @base;
        int e = exponent;
        while (e > 0)
        {
            if ((e & 1) == 1)
            {
                acc *= b;
                if (acc > int.MaxValue) return MaxValue;
            }
            e >>= 1;
            if (e > 0)
            {
                b *= b;
                if (b > int.MaxValue) return MaxValue;
            }
        }
        return new Fixed((int)acc);
    }

    /// <summary>多工匠建造时间惩罚的确定实现(原版 Repairable.js:
    /// <c>num &lt; 2 ? 1 : Math.pow(num, 0.7) / num</c>)。num 是小整数(工匠数),
    /// 0.7 = 7/10 的结果按 Fixed 预计算成查表——全程查表零浮点,跨平台逐位一致
    /// (Math.Pow 属 libm,各平台低位可能不同,是 OOS 源)。表覆盖 2..64,超出钳到端点。</summary>
    public static Fixed BuilderTimeMultiplier(int num)
    {
        if (num < 2) return One;
        int idx = num > BuilderPowTable.Length + 1 ? BuilderPowTable.Length + 1 : num;
        return new Fixed(BuilderPowTable[idx - 2]) / idx;
    }

    // num^0.7 << 16, num = 2..64(建表:Math.Pow 一次性离线计算后取整,
    // 运行期只做查表——表值本身成为规范常数,不再依赖任何平台 libm)。
    private static readonly int[] BuilderPowTable =
    {
        106464, 141405, 172951, 202190, 229713, 255887, 280959, 305105,
        328458, 351119, 373170, 394676, 415690, 436258, 456419, 476205,
        495645, 514763, 533582, 552120, 570395, 588423, 606217, 623789,
        641152, 658316, 675290, 692084, 708704, 725159, 741455, 757600,
        773598, 789455, 805178, 820769, 836235, 851579, 866806, 881919,
        896921, 911817, 926610, 941301, 955895, 970395, 984802, 999119,
        1013349, 1027493, 1041555, 1055536, 1069438, 1083263, 1097012, 1110689,
        1124293, 1137827, 1151293, 1164691, 1178024, 1191292, 1204498,
    };

    public static Fixed FromInt(int n) => new(n << FractBits);

    public static Fixed FromFraction(int n, int d) =>
        new((int)((uint)n << FractBits) / d);

    public static Fixed FromFloat(float n)
    {
        if (!float.IsFinite(n))
            return Zero;
        float scaled = n * FractPow2;
        return new Fixed(RoundAwayFromZero(scaled));
    }

    public static Fixed FromDouble(double n)
    {
        if (!double.IsFinite(n))
            return Zero;
        double scaled = n * FractPow2;
        return new Fixed(RoundAwayFromZero(scaled));
    }

    // --- Conversions ---

    public int InternalValue => _value;

    public Fixed WithInternalValue(int v) => new(v);

    public float ToFloat() => (float)_value / (float)FractPow2;
    public double ToDouble() => (double)_value / (double)FractPow2;

    public int ToIntRoundToZero() =>
        _value > 0 ? _value >> FractBits : (_value + FractPow2 - 1) >> FractBits;

    public int ToIntRoundToInfinity() => (_value + FractPow2 - 1) >> FractBits;

    public int ToIntRoundToNegInfinity() => _value >> FractBits;

    public int ToIntRoundToNearest() => (_value + FractPow2 / 2) >> FractBits;

    public bool IsZero => _value == 0;

    // --- Arithmetic operators ---

    public static Fixed operator +(Fixed a, Fixed b) => new(a._value + b._value);
    public static Fixed operator -(Fixed a, Fixed b) => new(a._value - b._value);
    public static Fixed operator -(Fixed a) => new(-a._value);

    public static Fixed operator >>(Fixed a, int n) => new(a._value >> n);
    public static Fixed operator <<(Fixed a, int n) => new(a._value << n);

    /// <summary>Divide by a Fixed. Uses 64-bit intermediate to prevent overflow.</summary>
    public static Fixed operator /(Fixed a, Fixed b)
    {
        long t = (long)a._value << FractBits;
        long result = t / (long)b._value;
        return new((int)result);
    }

    /// <summary>Multiply by an integer.</summary>
    public static Fixed operator *(Fixed a, int n) => new(a._value * n);

    /// <summary>Divide by an integer.</summary>
    public static Fixed operator /(Fixed a, int n) => new(a._value / n);

    /// <summary>Mod by a Fixed. Result has same sign as divisor (like C++ version).</summary>
    public static Fixed operator %(Fixed a, Fixed b)
    {
        int t = a._value % b._value;
        if (b._value > 0 && t < 0)
            t += b._value;
        else if (b._value < 0 && t > 0)
            t += b._value;
        return new(t);
    }

    // --- Comparison ---

    public static bool operator ==(Fixed a, Fixed b) => a._value == b._value;
    public static bool operator !=(Fixed a, Fixed b) => a._value != b._value;
    public static bool operator <(Fixed a, Fixed b) => a._value < b._value;
    public static bool operator >(Fixed a, Fixed b) => a._value > b._value;
    public static bool operator <=(Fixed a, Fixed b) => a._value <= b._value;
    public static bool operator >=(Fixed a, Fixed b) => a._value >= b._value;

    public bool Equals(Fixed other) => _value == other._value;
    public int CompareTo(Fixed other) => _value.CompareTo(other._value);
    public override bool Equals(object? obj) => obj is Fixed f && _value == f._value;
    public override int GetHashCode() => _value;

    // --- Fixed-specific operations ---

    public Fixed Absolute => new(Math.Abs(_value));

    /// <summary>
    /// Multiply by a Fixed. Uses 64-bit intermediate. Named (not operator*) to
    /// match C++ convention of making overflow potential explicit.
    /// </summary>
    public Fixed Multiply(Fixed n)
    {
        long t = (long)_value * (long)n._value;
        t >>= FractBits;
        return new((int)t);
    }

    public Fixed Square() => Multiply(this);

    /// <summary>Compute this*m/d. Won't overflow if result fits in Fixed range.</summary>
    public Fixed MulDiv(Fixed m, Fixed d)
    {
        long t = (long)_value * (long)m._value / (long)d._value;
        return new((int)t);
    }

    /// <summary>Multiply by integer, clamping to min/max instead of overflowing.</summary>
    public Fixed MultiplyClamp(int n)
    {
        long t = (long)_value * n;
        t = Math.Max(long.MinValue, Math.Min(long.MaxValue, t));
        return new((int)t);
    }

    public Fixed Sqrt()
    {
        if (_value <= 0)
            return Zero;
        uint s = MathInt.Sqrt64((ulong)((long)_value << FractBits));
        return new((int)s);
    }

    // --- String parsing/formatting (exact translation of Fixed.cpp) ---

    public static Fixed FromString(string s)
    {
        if (string.IsNullOrEmpty(s))
            return Zero;

        bool neg = false;
        Fixed r = Zero;
        int i = 0;

        if (s[0] == '+')
        {
            i++;
        }
        else if (s[0] == '-')
        {
            i++;
            neg = true;
        }

        while (i < s.Length)
        {
            if (s[i] >= '0' && s[i] <= '9')
            {
                r = r * 10;
                r += FromInt(s[i] - '0');
                i++;
            }
            else if (s[i] == '.')
            {
                i++;
                uint frac = 0;
                uint div = 1;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9')
                {
                    frac *= 10;
                    frac += (uint)(s[i] - '0');
                    div *= 10;
                    i++;
                    if (div >= 100000)
                        break;
                }
                r += new Fixed((int)((ulong)frac << FractBits) / (int)div);
                break;
            }
            else
            {
                break;
            }
        }

        return neg ? -r : r;
    }

    /// <summary>Shortest string such that FromString will parse back to this value.</summary>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder(16);
        uint posvalue = (uint)Math.Abs(_value);
        if (_value < 0)
            sb.Append('-');

        sb.Append(posvalue >> FractBits);

        uint fraction = posvalue & ((1u << FractBits) - 1);
        if (fraction != 0)
        {
            sb.Append('.');
            uint frac = 0;
            uint div = 1;

            while (true)
            {
                frac *= 10;
                div *= 10;

                uint digit = (uint)(((ulong)fraction * div) >> FractBits) - frac;
                frac += digit;

                if (((ulong)frac << FractBits) / div == fraction)
                {
                    sb.Append(digit);
                    break;
                }

                if (digit <= 8 && ((ulong)(frac + 1) << FractBits) / div == fraction)
                {
                    sb.Append(digit + 1);
                    break;
                }

                sb.Append(digit);
            }
        }

        return sb.ToString();
    }

    // --- Helpers ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundAwayFromZero(float value) =>
        value >= 0 ? (int)(value + 0.5f) : (int)(value - 0.5f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundAwayFromZero(double value) =>
        value >= 0 ? (int)(value + 0.5) : (int)(value - 0.5);
}
