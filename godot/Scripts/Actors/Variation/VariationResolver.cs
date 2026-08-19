using System;
using System.Collections.Generic;

namespace ZeroAD.Godot.Actors.Variation;

using ZeroAD.Godot.Actors.Parsing;

/// <summary>
/// Port of 0 A.D.'s ObjectBase::CalculateVariationKey: each &lt;group&gt; picks one variant
/// by name-priority sets first, then frequency-weighted RNG when no name matches.
/// </summary>
public static class VariationResolver
{
    public sealed record Selections(IReadOnlyList<IReadOnlySet<string>> PrioritySets);

    public static readonly Selections IdleOnly = new(
        new[]
        {
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "idle" }
        });

    /// <summary>建造中（原版 Foundation.js Commit → SelectAnimation("scaffold")）：
    /// scaffold 变体优先，无 scaffold 的组回退 idle，再回退加权随机。</summary>
    public static readonly Selections Scaffold = new(
        new[]
        {
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scaffold" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "idle" },
        });

    public static IReadOnlyList<int> Resolve(ActorDoc doc, int seed, Selections selections)
    {
        var chosen = new int[doc.Groups.Count];
        for (int gi = 0; gi < doc.Groups.Count; gi++)
        {
            var variants = doc.Groups[gi].Variants;
            if (variants.Count == 0)
            {
                chosen[gi] = -1;
                continue;
            }
            if (variants.Count == 1)
            {
                chosen[gi] = 0;
                continue;
            }

            bool matched = false;
            foreach (var set in selections.PrioritySets)
            {
                for (int vi = 0; vi < variants.Count; vi++)
                {
                    if (!string.IsNullOrEmpty(variants[vi].Name) && set.Contains(variants[vi].Name))
                    {
                        chosen[gi] = vi;
                        matched = true;
                        break;
                    }
                }
                if (matched) break;
            }
            if (matched) continue;

            // Frequency-weighted RNG. All-zero frequencies collapse to uniform.
            int groupSeed = HashCode.Combine(seed, gi, doc.Path);
            var rng = new Random(groupSeed);
            chosen[gi] = PickWeighted(variants, rng);
        }
        return chosen;
    }

    private static int PickWeighted(IReadOnlyList<ActorVariant> variants, Random rng)
    {
        long total = 0;
        bool anyNonZero = false;
        foreach (var v in variants)
        {
            int f = v.Frequency > 0 ? v.Frequency : 0;
            if (f > 0) anyNonZero = true;
            total += f;
        }
        if (!anyNonZero)
        {
            // All-zero → uniform over all variants.
            return rng.Next(variants.Count);
        }
        long roll = (long)(rng.NextDouble() * total);
        long acc = 0;
        for (int i = 0; i < variants.Count; i++)
        {
            acc += variants[i].Frequency > 0 ? variants[i].Frequency : 0;
            if (roll < acc) return i;
        }
        return variants.Count - 1;
    }
}
