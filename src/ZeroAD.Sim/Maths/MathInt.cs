using System.Runtime.CompilerServices;

namespace ZeroAD.Sim.Maths;

/// <summary>
/// Deterministic integer math operations. No floating-point used anywhere.
/// </summary>
public static class MathInt
{
    /// <summary>
    /// 64-bit integer square root.
    /// Returns r such that r^2 &lt;= n &lt; (r+1)^2, for the complete ulong range.
    /// Translation of <c>isqrt64</c> from <c>source/maths/Sqrt.cpp</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Sqrt64(ulong n)
    {
        if (n == 0)
            return 0;

        // Use the hardware uint sqrt when available (.NET 7+ Math.Sqrt returns double
        // which loses precision for large values, so we use a pure integer algorithm).
        // Bit-by-bit method: deterministic, no platform dependency.
        ulong op = n;
        ulong res = 0;
        ulong one = 1UL << 62;

        // "one" starts at the highest power of four <= the argument
        while (one > op)
            one >>= 2;

        while (one != 0)
        {
            if (op >= res + one)
            {
                op -= res + one;
                res += one << 1;
            }
            res >>= 1;
            one >>= 2;
        }

        return (uint)res;
    }
}
