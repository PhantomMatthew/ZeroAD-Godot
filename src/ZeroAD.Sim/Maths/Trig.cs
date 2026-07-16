namespace ZeroAD.Sim.Maths;

/// <summary>
/// Fixed-point trigonometric approximations.
/// Direct translation of <c>atan2_approx</c> and <c>sincos_approx</c>
/// from <c>source/maths/Fixed.cpp</c>.
/// </summary>
public static class Trig
{
    // Magic constants from Fixed.cpp:
    // pi/4 << 16 = 51472, 3*pi/4 << 16 = 154415, 2/pi << 16 = 41721
    private static readonly Fixed C1 = new(51472);
    private static readonly Fixed C2 = new(154415);
    private static readonly Fixed C2Pi = new(41721);

    /// <summary>
    /// Approximation of atan2 over fixed-point numbers.
    /// Maximum error is almost 0.08 radians (4.5 degrees).
    /// </summary>
    public static Fixed Atan2Approx(Fixed y, Fixed x)
    {
        Fixed zero = Fixed.Zero;

        if (x.IsZero && y.IsZero)
            return zero;

        Fixed absY = y.Absolute;
        Fixed angle;

        if (x >= zero)
        {
            Fixed r = (x - absY) / (x + absY);
            angle = C1 - C1.Multiply(r);
        }
        else
        {
            Fixed r = (x + absY) / (absY - x);
            angle = C2 - C1.Multiply(r);
        }

        return y < zero ? -angle : angle;
    }

    /// <summary>
    /// Compute sin(a) and cos(a).
    /// Maximum error for -2pi &lt; a &lt; 2pi is almost 0.0005.
    /// Fifth-order approximation from http://www.coranac.com/2009/07/sines/
    /// </summary>
    public static void SinCosApprox(Fixed a, out Fixed sinOut, out Fixed cosOut)
    {
        // Map radians onto range [0, 4)
        Fixed z = a.Multiply(C2Pi) % Fixed.FromInt(4);

        Fixed sz, cz;
        if (z >= Fixed.FromInt(3)) // [3, 4)
        {
            sz = z - Fixed.FromInt(4);
            cz = z - Fixed.FromInt(3);
        }
        else if (z >= Fixed.FromInt(2)) // [2, 3)
        {
            sz = Fixed.FromInt(2) - z;
            cz = z - Fixed.FromInt(3);
        }
        else if (z >= Fixed.FromInt(1)) // [1, 2)
        {
            sz = Fixed.FromInt(2) - z;
            cz = Fixed.FromInt(1) - z;
        }
        else // [0, 1)
        {
            sz = z;
            cz = Fixed.FromInt(1) - z;
        }

        // Fifth-order: sin(x) ≈ x*(pi - x²*(pi*2 - 5 - x²*(pi - 3))) / 2
        Fixed pi = Fixed.Pi;

        Fixed sz2 = sz.Multiply(sz);
        sinOut = sz.Multiply(
            pi - sz2.Multiply(pi * 2 - Fixed.FromInt(5) - sz2.Multiply(pi - Fixed.FromInt(3)))) / 2;

        Fixed cz2 = cz.Multiply(cz);
        cosOut = cz.Multiply(
            pi - cz2.Multiply(pi * 2 - Fixed.FromInt(5) - cz2.Multiply(pi - Fixed.FromInt(3)))) / 2;
    }
}
