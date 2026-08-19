using System;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen
{
    /// <summary>Noise2D（逐字移植 rmgen/Noise.js）——Perlin 梯度噪声。
    /// 构造器按 freq² 个 randomAngle() 抽数生成梯度网格（RNG 消耗顺序关键）。
    /// get 返回 [0,1]。</summary>
    public sealed class Noise2D
    {
        private readonly int _freq;
        private readonly RmgenVector2D[][] _grads;

        public Noise2D(RmgenRng rng, double freq)
        {
            _freq = (int)Math.Floor(freq);
            _grads = new RmgenVector2D[_freq][];
            for (int i = 0; i < _freq; ++i)
            {
                _grads[i] = new RmgenVector2D[_freq];
                for (int j = 0; j < _freq; ++j)
                {
                    double a = rng.RandomAngle();
                    _grads[i][j] = new RmgenVector2D(SafeMath.Cos(a), SafeMath.Sin(a));
                }
            }
        }

        private static double EaseCurve(double t) => t * t * t * (t * (t * 6 - 15) + 10);

        private static int ModPos(double num, int m)
        {
            double p = num % m;
            if (p < 0)
                p += m;
            return (int)p;
        }

        public double Get(double x, double y)
        {
            x *= _freq;
            y *= _freq;

            int ix = ModPos(Math.Floor(x), _freq);
            int iy = ModPos(Math.Floor(y), _freq);

            // 上游即 x - ix（回绕后的索引），不是小数部分——原样保留
            double fx = x - ix;
            double fy = y - iy;

            int ix1 = (ix + 1) % _freq;
            int iy1 = (iy + 1) % _freq;

            double s = _grads[ix][iy].Dot(new RmgenVector2D(fx, fy));
            double t = _grads[ix1][iy].Dot(new RmgenVector2D(fx - 1, fy));
            double u = _grads[ix][iy1].Dot(new RmgenVector2D(fx, fy - 1));
            double v = _grads[ix1][iy1].Dot(new RmgenVector2D(fx - 1, fy - 1));

            double ex = EaseCurve(fx);
            double ey = EaseCurve(fy);
            double a = s + ex * (t - s);
            double b = u + ex * (v - u);
            return (a + ey * (b - a)) * 0.5 + 0.5;
        }
    }
}
