using System.Collections.Generic;

namespace ZeroAD.Sim.RmgenMath;

/// <summary>RNG 辅助函数（逐字移植自 globalscripts/random.js）。
/// 每个 RNG 函数恰好消费一次 NextDouble()（确定性约束——不能多消费/少消费）。
/// 用 SafeMath 替代 JS Math（注意 randomNormal2D 里的 sqrt/log 都是 SafeMath 版）。</summary>
public sealed class RmgenRng
{
    private readonly Rand48 _rng;

    public RmgenRng(uint seed)
    {
        _rng = new Rand48(seed);
    }

    /// <summary>[0, 1) 的均匀分布 double。等价于 JS 替换后的 Math.random()。
    /// Rand48.NextDouble() = state / 2^48，拒绝 ==1.0。逐字移植 generate_uniform_real。</summary>
    public double Random() => _rng.NextDouble();

    /// <summary>randFloat(min, max) = min + Math.random() * (max - min)。</summary>
    public double RandFloat(double min, double max)
        => min + Random() * (max - min);

    /// <summary>randIntInclusive(min, max) = Math.floor(min + Math.random() * (max + 1 - min))。</summary>
    public int RandIntInclusive(double min, double max)
        => (int)SafeMath.Floor(min + Random() * (max + 1 - min));

    /// <summary>randIntExclusive(min, max) = Math.floor(min + Math.random() * (max - min))。</summary>
    public int RandIntExclusive(double min, double max)
        => (int)SafeMath.Floor(min + Random() * (max - min));

    /// <summary>pickRandom(source) = source[floor(source.length * Math.random())]。</summary>
    public T PickRandom<T>(IReadOnlyList<T> source)
        => source.Count == 0 ? default! : source[(int)SafeMath.Floor(source.Count * Random())];

    /// <summary>randBool(p) = Math.random() &lt; p。</summary>
    public bool RandBool(double p = 0.5) => Random() < p;

    /// <summary>randomAngle() = randFloat(0, 2*PI)。</summary>
    public double RandomAngle() => RandFloat(0, 2 * SafeMath.PI);

    /// <summary>randomNormal2D() — 极坐标 Box-Muller + 拒绝采样。
    /// 每次尝试消费恰好 2 个 Random() draw；拒绝时重新消费。
    /// 返回 (a*s, b*s)。必须用 SafeMath.Sqrt / SafeMath.Log。</summary>
    public (double x, double y) RandomNormal2D()
    {
        double s, a, b;
        do
        {
            a = 2 * Random() - 1;
            b = 2 * Random() - 1;
            s = a * a + b * b;
        } while (s >= 1 || s == 0);
        s = SafeMath.Sqrt(-2 * SafeMath.Log(s) / s);
        return (a * s, b * s);
    }
}
