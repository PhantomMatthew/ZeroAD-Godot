namespace ZeroAD.Sim.RmgenMath;

/// <summary>插值辅助（逐字移植自 globalscripts/interpolation.js）。</summary>
public static class Interpolation
{
    /// <summary>三次插值（Cardinal/Catmull-Rom 样条）。</summary>
    public static double CubicInterpolation(double tension, double x,
        double p0, double p1, double p2, double p3)
    {
        double P = -tension * p0 + (2 - tension) * p1 + (tension - 2) * p2 + tension * p3;
        double Q = 2 * tension * p0 + (tension - 3) * p1 + (3 - 2 * tension) * p2 - tension * p3;
        double R = -tension * p0 + tension * p2;
        double S = p1;

        return ((P * x + Q) * x + R) * x + S;
    }

    /// <summary>双三次插值（4×4 网格内）。tension 固定 0.5。</summary>
    public static double BicubicInterpolation(RmgenVector2D position,
        double p00, double p01, double p02, double p03,
        double p10, double p11, double p12, double p13,
        double p20, double p21, double p22, double p23,
        double p30, double p31, double p32, double p33)
    {
        const double tension = 0.5;
        return CubicInterpolation(
            tension,
            position.X,
            CubicInterpolation(tension, position.Y, p00, p01, p02, p03),
            CubicInterpolation(tension, position.Y, p10, p11, p12, p13),
            CubicInterpolation(tension, position.Y, p20, p21, p22, p23),
            CubicInterpolation(tension, position.Y, p30, p31, p32, p33));
    }
}
