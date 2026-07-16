using System;

namespace ZeroAD.Sim;

/// <summary>
/// Deterministic PRNG — direct translation of <c>boost::rand48</c> used by
/// <c>CComponentManager::m_RNG</c>. LCG parameters: a=25214903917, c=11, m=2^48.
/// Seeding matches Java's <c>java.util.Random</c>: state = (seed &lt;&lt; 16) | 0x330e.
/// </summary>
public sealed class Rand48
{
    internal const ulong A = 25214903917UL;
    internal const ulong C = 11UL;
    internal const ulong M = 1UL << 48;
    internal const ulong Mask = M - 1;

    internal ulong _state;

    public Rand48(uint seed)
    {
        Seed(seed);
    }

    public void Seed(uint seed)
    {
        _state = ((ulong)seed << 16) | 0x330E;
    }

    /// <summary>Advance one step and return the full 48-bit state (matches boost::rand48::operator()).</summary>
    public ulong Next()
    {
        _state = (A * _state + C) & Mask;
        return _state;
    }

    /// <summary>Uniform [0, 1) double — matches <c>generate_uniform_real(rng, 0, 1)</c>.</summary>
    public double NextDouble()
    {
        while (true)
        {
            double n = (double)Next();
            double d = (double)M;
            double r = n / d;
            if (r < 1.0)
                return r;
        }
    }

    /// <summary>Uniform [min, max) integer via rejection sampling.</summary>
    public int NextInt(int min, int max)
    {
        long range = (long)max - min;
        if (range <= 0)
            return min;
        ulong mask = (ulong)range - 1;
        mask |= mask >> 1;
        mask |= mask >> 2;
        mask |= mask >> 4;
        mask |= mask >> 8;
        mask |= mask >> 16;
        mask |= mask >> 32;
        ulong val;
        do { val = Next() & mask; } while (val >= (ulong)range);
        return min + (int)val;
    }

    /// <summary>Serialize state as decimal string (matches <c>operator&lt;&lt;</c> on boost::rand48).</summary>
    public string Serialize() => _state.ToString();

    /// <summary>Deserialize state from decimal string.</summary>
    public void Deserialize(string str) => _state = ulong.Parse(str);
}
