using System;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>
/// System-entity component that carries the per-player LOS (fog-of-war) state through
/// full-state serialization, so fog data rides the state hash (OOS detection catches
/// visibility divergence) and a future save/load restores explored/visible bits.
///
/// Only the reveal-all mask + <see cref="LosGrid"/> state words are serialized; the
/// per-vertex seer counts are derivable, so <see cref="Deserialize"/> rebuilds them by
/// re-adding every live seer via <see cref="RangeManager.RebuildLosAfterLoad"/> (mirrors
/// the original, which recomputes counts on load rather than serializing them).
///
/// Must be <see cref="Attach"/>ed to the world's RangeManager before use — SimBridge
/// wires it at InitWorld on the terrain system entity. An explicit reference (not the
/// SimSystem static) keeps interleaved multi-world hashing in tests race-free.
/// </summary>
[Component("LosManager", "LOS manager state")]
public sealed class LosManagerComponent : ComponentBase
{
    private RangeManager? _rm;

    public void Attach(RangeManager rm) => _rm = rm;

    private RangeManager Rm => _rm
        ?? throw new InvalidOperationException(
            "LosManagerComponent must be Attach()ed to a RangeManager before (de)serialization");

    public override void Serialize(ISerializer s)
    {
        uint revealMask = 0;
        for (int p = 1; p <= LosGrid.MaxPlayers; p++)
            if (Rm.GetLosRevealAll(p))
                revealMask |= 1u << (p - 1);
        s.NumberU32("reveal", revealMask);
        Rm.Los.Serialize(s);
    }

    public override void Deserialize(IDeserializer d)
    {
        uint revealMask = d.NumberU32("reveal");
        Rm.Los.Deserialize(d); // zeroes counts; state words restored
        Rm.RebuildLosAfterLoad(revealMask);
    }
}
