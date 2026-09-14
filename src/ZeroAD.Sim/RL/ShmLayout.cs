using System;
using System.Buffers.Binary;

namespace ZeroAD.Sim.RL;

/// <summary>File layout: 64-byte global header + N slots.
/// Slot offsets are relative to the start of that slot.</summary>
public static class ShmLayout
{
    public const uint Magic = RlSpec.LayoutMagic;
    public const int Version = RlSpec.LayoutVersion;

    public const int CmdIdle = 0;
    public const int CmdReset = 1;
    public const int CmdStep = 2;
    public const int CmdQuit = 3;

    public const int HeaderBytes = 64;
    public const int OffMagic = 0;
    public const int OffVersion = 4;
    public const int OffSlots = 8;
    public const int OffCmd = 12;
    public const int OffSeqIn = 16;
    public const int OffSeqOut = 20;
    public const int OffPrivileged = 24;
    public const int OffSeed = 28;

    public const int EntitiesBytes = RlSpec.MaxEntities * RlSpec.EntityFeat * sizeof(int);
    public const int SpatialBytes = RlSpec.SpatialChannels * RlSpec.SpatialSize * RlSpec.SpatialSize * sizeof(int);
    public const int ScalarsBytes = RlSpec.ScalarCount * sizeof(int);
    public const int MaskBytes = 16;
    public const int ActionBytes = 32;

    public const int OffTurn = 0;
    public const int OffReward = 4;
    public const int OffDone = 8;
    public const int OffEntities = 16;
    public const int OffSpatial = OffEntities + EntitiesBytes;
    public const int OffScalars = OffSpatial + SpatialBytes;
    public const int OffMask = OffScalars + ScalarsBytes;
    public const int OffAction = OffMask + MaskBytes;
    public const int SlotBytes = OffAction + ActionBytes;

    public const int ActFunction = 0;
    public const int ActSelected = 4;
    public const int ActTarget = 8;
    public const int ActCellX = 12;
    public const int ActCellZ = 16;
    public const int ActCatalog = 20;
    /// <summary>Packed opponent action in the unused tail of <see cref="ActionBytes"/>
    /// (layout v1, no version bump). Function int32 + selected/target int16.
    /// Cells are inferred from the entity table. Zero function = no opponent command.</summary>
    public const int ActOppFunction = 24;
    public const int ActOppSelected = 28;
    public const int ActOppTarget = 30;

    public static int FileBytes(int slots) => HeaderBytes + Math.Max(1, slots) * SlotBytes;
    public static int SlotOffset(int slot) => HeaderBytes + slot * SlotBytes;
}

public static class ShmCodec
{
    public static void WriteHeader(Span<byte> file, int slots, int seed, bool privileged)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(file.Slice(ShmLayout.OffMagic), ShmLayout.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(file.Slice(ShmLayout.OffVersion), ShmLayout.Version);
        BinaryPrimitives.WriteInt32LittleEndian(file.Slice(ShmLayout.OffSlots), slots);
        BinaryPrimitives.WriteInt32LittleEndian(file.Slice(ShmLayout.OffCmd), ShmLayout.CmdIdle);
        BinaryPrimitives.WriteInt32LittleEndian(file.Slice(ShmLayout.OffSeqIn), 0);
        BinaryPrimitives.WriteInt32LittleEndian(file.Slice(ShmLayout.OffSeqOut), 0);
        BinaryPrimitives.WriteInt32LittleEndian(file.Slice(ShmLayout.OffPrivileged), privileged ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(file.Slice(ShmLayout.OffSeed), seed);
    }

    public static void WriteObservation(Span<byte> slot, RlObservation obs)
    {
        BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(ShmLayout.OffTurn), (int)obs.Turn);
        BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(ShmLayout.OffReward), obs.Reward);
        BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(ShmLayout.OffDone), obs.Done ? 1 : 0);
        CopyInts(obs.Entities, slot.Slice(ShmLayout.OffEntities, ShmLayout.EntitiesBytes));
        CopyInts(obs.Spatial, slot.Slice(ShmLayout.OffSpatial, ShmLayout.SpatialBytes));
        CopyInts(obs.Scalars, slot.Slice(ShmLayout.OffScalars, ShmLayout.ScalarsBytes));
        var mask = slot.Slice(ShmLayout.OffMask, ShmLayout.MaskBytes);
        mask.Clear();
        int n = Math.Min(obs.FunctionMask.Length, mask.Length);
        obs.FunctionMask.AsSpan(0, n).CopyTo(mask);
    }

    public static RlAction ReadAction(ReadOnlySpan<byte> slot)
    {
        var a = slot.Slice(ShmLayout.OffAction);
        int fn = BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActFunction));
        if (fn < 0 || fn >= RlSpec.FunctionCount) fn = 0;
        return new RlAction(
            (RlFunction)fn,
            BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActSelected)),
            BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActTarget)),
            BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActCellX)),
            BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActCellZ)),
            BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActCatalog)));
    }

    /// <summary>Opponent command packed in action bytes 24–31. NoOp when function is 0.</summary>
    public static RlAction ReadOpponentAction(ReadOnlySpan<byte> slot)
    {
        var a = slot.Slice(ShmLayout.OffAction);
        int fn = BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActOppFunction));
        if (fn <= 0 || fn >= RlSpec.FunctionCount) return RlAction.NoOp();
        short sel = BinaryPrimitives.ReadInt16LittleEndian(a.Slice(ShmLayout.ActOppSelected));
        short tgt = BinaryPrimitives.ReadInt16LittleEndian(a.Slice(ShmLayout.ActOppTarget));
        return new RlAction((RlFunction)fn, sel, tgt, -1, -1, 0);
    }

    public static void WriteAction(Span<byte> slot, RlAction action)
    {
        var a = slot.Slice(ShmLayout.OffAction);
        BinaryPrimitives.WriteInt32LittleEndian(a.Slice(ShmLayout.ActFunction), (int)action.Function);
        BinaryPrimitives.WriteInt32LittleEndian(a.Slice(ShmLayout.ActSelected), action.SelectedIndex);
        BinaryPrimitives.WriteInt32LittleEndian(a.Slice(ShmLayout.ActTarget), action.TargetEntityIndex);
        BinaryPrimitives.WriteInt32LittleEndian(a.Slice(ShmLayout.ActCellX), action.TargetCellX);
        BinaryPrimitives.WriteInt32LittleEndian(a.Slice(ShmLayout.ActCellZ), action.TargetCellZ);
        BinaryPrimitives.WriteInt32LittleEndian(a.Slice(ShmLayout.ActCatalog), action.CatalogId);
    }

    public static void WriteOpponentAction(Span<byte> slot, RlAction action)
    {
        var a = slot.Slice(ShmLayout.OffAction);
        BinaryPrimitives.WriteInt32LittleEndian(a.Slice(ShmLayout.ActOppFunction), (int)action.Function);
        BinaryPrimitives.WriteInt16LittleEndian(a.Slice(ShmLayout.ActOppSelected),
            (short)Math.Clamp(action.SelectedIndex, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(a.Slice(ShmLayout.ActOppTarget),
            (short)Math.Clamp(action.TargetEntityIndex, short.MinValue, short.MaxValue));
    }

    private static void CopyInts(int[] src, Span<byte> dest)
    {
        for (int i = 0; i < src.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(i * 4), src[i]);
    }
}
