using System;
using System.Collections.Generic;

namespace ZeroAD.Sim.Net;

/// <summary>
/// Parallel-array pack/unpack for the lobby slot table, Godot-RPC friendly
/// (<c>int[]</c> marshals as PackedInt32Array, <c>string[]</c> as PackedStringArray — both
/// first-class Variant types in Godot 4). <see cref="PlayerSlotSetup.PlayerId"/> is implied
/// by slot order (slot i → PlayerId i+1), so it is not carried on the wire. Mirrors the
/// deterministic framing style of <c>NetCommand.SerializeBatch</c>/<c>DeserializeBatch</c>:
/// no reflection, fixed layout.
/// </summary>
public static class PlayerSlotSetupCodec
{
    /// <summary>Maximum lobby slots (host + up to 3 clients = 4, matching the ENet peer cap).</summary>
    public const int MaxSlots = 4;

    /// <summary>Pack the slot table into three parallel arrays. PlayerId is NOT carried.</summary>
    /// <summary>v2: 追加 AI 难度/性格平行数组(线格式扩展——存档 v13/录像 v3 随升)。</summary>
    public static (int[] kinds, string[] civs, int[] teams, int[] difficulties, string[] behaviors)
        PackFull(IReadOnlyList<PlayerSlotSetup> slots)
    {
        var (kinds, civs, teams) = Pack(slots);
        var diffs = new int[slots.Count];
        var behs = new string[slots.Count];
        for (int i = 0; i < slots.Count; i++)
        {
            diffs[i] = slots[i].AIDifficulty;
            behs[i] = slots[i].AIBehavior;
        }
        return (kinds, civs, teams, diffs, behs);
    }

    public static List<PlayerSlotSetup> UnpackFull(int[] kinds, string[] civs, int[] teams,
        int[] difficulties, string[] behaviors)
    {
        var slots = Unpack(kinds, civs, teams);
        for (int i = 0; i < slots.Count && i < difficulties.Length; i++)
            slots[i] = slots[i] with
            {
                AIDifficulty = difficulties[i],
                AIBehavior = i < behaviors.Length ? behaviors[i] : "",
            };
        return slots;
    }

    public static (int[] kinds, string[] civs, int[] teams) Pack(IReadOnlyList<PlayerSlotSetup> slots)
    {
        if (slots.Count > MaxSlots)
            throw new ArgumentException(
                $"Too many slots: {slots.Count} > MaxSlots ({MaxSlots}).", nameof(slots));

        var kinds = new int[slots.Count];
        var civs = new string[slots.Count];
        var teams = new int[slots.Count];
        for (int i = 0; i < slots.Count; i++)
        {
            kinds[i] = (int)slots[i].Kind;
            civs[i] = slots[i].Civ;
            teams[i] = slots[i].Team;
        }
        return (kinds, civs, teams);
    }

    /// <summary>Unpack parallel arrays back into a slot table. Reconstructs PlayerId = i+1.</summary>
    public static List<PlayerSlotSetup> Unpack(int[] kinds, string[] civs, int[] teams)
    {
        if (kinds.Length != civs.Length || civs.Length != teams.Length)
            throw new ArgumentException("Parallel array length mismatch in slot table.");
        if (kinds.Length > MaxSlots)
            throw new ArgumentException(
                $"Too many slots: {kinds.Length} > MaxSlots ({MaxSlots}).");

        var slots = new List<PlayerSlotSetup>(kinds.Length);
        for (int i = 0; i < kinds.Length; i++)
        {
            slots.Add(new PlayerSlotSetup
            {
                PlayerId = i + 1,
                Kind = (PlayerSlotKind)kinds[i],
                Civ = civs[i],
                Team = teams[i],
            });
        }
        return slots;
    }
}
