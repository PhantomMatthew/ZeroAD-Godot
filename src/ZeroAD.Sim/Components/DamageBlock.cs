using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// Damage model — multi-type damage + capture, matching the original 0 A.D. Attack.js /
// AttackHelper.js / Resistance.js pipeline.
//
// The original splits damage into per-type values (Hack/Pierce/Crush) each resisted
// independently, plus a separate Capture effect. AttackHelper.GetTotalAttackEffects applies
// resistance per type via  finalDamage = raw * 0.9^resistanceValue  (each point of resistance
// = 10% reduction). Capture scales by 0.1 + 0.9 * (hp/maxHp) so wounded targets are easier
// to capture.
//
// This file holds the shared value types (DamageType enum, DamageBlock) so Attack,
// Resistance, Health, and DelayedDamage all speak the same shape.

/// <summary>Damage type codes from the original AttackEffects schema (globalscripts/AttackEffects.js).</summary>
public enum DamageType : byte
{
    Hack = 0,
    Pierce = 1,
    Crush = 2,
}

/// <summary>
/// A bundle of damage to apply: per-type physical amounts plus optional capture. This is the
/// unit that flows through Attack → DelayedDamage → Resistance → Health. Immutable in spirit;
/// mutate via <see cref="WithResistanceApplied"/> to produce the post-resistance version.
/// </summary>
public sealed class DamageBlock
{
    /// <summary>Per-type raw damage. Missing types are treated as 0.</summary>
    public Dictionary<DamageType, int> Amounts = new();

    /// <summary>Capture points (separate damage channel for structure conversion).
    /// Fixed:模板值是小数(infantry 2.5 / cavalry 1.75)。</summary>
    public Maths.Fixed Capture;

    public DamageBlock() { }

    /// <summary>Convenience: single-type block (melee units often deal one damage type).</summary>
    public DamageBlock(DamageType type, int amount)
    {
        Amounts[type] = amount;
    }

    public int Get(DamageType t) => Amounts.TryGetValue(t, out var v) ? v : 0;

    /// <summary>Total physical damage (sum across types). Used for HUD/summary display.</summary>
    public int TotalPhysical => Get(DamageType.Hack) + Get(DamageType.Pierce) + Get(DamageType.Crush);

    public bool IsEmpty => TotalPhysical == 0 && Capture <= Maths.Fixed.Zero;

    /// <summary>
    /// Return a new block with each type reduced by resistance. Mirrors
    /// AttackHelper.GetTotalAttackEffects: finalDamage = raw * 0.9^resistance.
    /// Capture is reduced by the capture resistance, then caller applies the hp-ratio scale.
    /// </summary>
    public DamageBlock WithResistanceApplied(IReadOnlyDictionary<DamageType, int> resistance, int captureResistance)
    {
        var result = new DamageBlock { Capture = ApplyResistanceFixed(Capture, captureResistance) };
        foreach (var (type, raw) in Amounts)
        {
            int r = resistance.TryGetValue(type, out var rv) ? rv : 0;
            result.Amounts[type] = ApplyResistance(raw, r);
        }
        return result;
    }

    // 0.9^resistance as integer math. We precompute a lookup for the common resistance range
    // (0..20 covers all realistic values; beyond that it's effectively immune). Negative
    // resistance (vulnerability) inverts the multiplier. Kept in fixed-point-free integer form
    // here to stay simple and deterministic; the 0.9 base is approximated by integer rounding.
    private static readonly int[] s_resistMultiplierPercent =
    {
        // index = resistance value; entry = percent of original damage kept (0.9^index * 100, rounded)
        100, 90, 81, 73, 66, 59, 53, 48, 43, 39, 35, 32, 28, 26, 23, 21, 19, 17, 15, 14, 13
    };

    internal static int ApplyResistance(int raw, int resistance)
    {
        if (raw == 0) return 0;
        return raw * ResistancePercent(resistance) / 100;
    }

    /// <summary>Capture 通道的 Fixed 变体:同一张 0.9^r 整数查表,确定性整点数学。</summary>
    internal static Maths.Fixed ApplyResistanceFixed(Maths.Fixed raw, int resistance)
    {
        if (raw <= Maths.Fixed.Zero) return Maths.Fixed.Zero;
        return raw * ResistancePercent(resistance) / 100;
    }

    private static int ResistancePercent(int resistance) => resistance switch
    {
        >= 0 and <= 20 => s_resistMultiplierPercent[resistance],
        > 20 => 0,                 // heavily resisted → negligible
        _ => 100 + (-resistance) * 10 // negative = vulnerability: +10% per point
    };

    // --- Serialization (for OOS hashing) ---
    // Write types in enum order (Hack,Pierce,Crush) for deterministic hashing.
    public void Serialize(ISerializer s, string prefix)
    {
        s.NumberI32(prefix + "_hack", Get(DamageType.Hack));
        s.NumberI32(prefix + "_pierce", Get(DamageType.Pierce));
        s.NumberI32(prefix + "_crush", Get(DamageType.Crush));
        s.NumberFixed(prefix + "_capture", Capture);
    }

    public static DamageBlock Deserialize(IDeserializer d, string prefix)
    {
        // 读取顺序必须与 Serialize 写入顺序逐位一致(hack/pierce/crush/capture)——
        // BinaryDeserializer 是位置流,对象初始化器会先跑 capture 造成整体错位。
        var block = new DamageBlock();
        block.Amounts[DamageType.Hack] = d.NumberI32(prefix + "_hack");
        block.Amounts[DamageType.Pierce] = d.NumberI32(prefix + "_pierce");
        block.Amounts[DamageType.Crush] = d.NumberI32(prefix + "_crush");
        block.Capture = d.NumberFixed(prefix + "_capture");
        return block;
    }
}
