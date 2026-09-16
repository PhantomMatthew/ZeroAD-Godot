using System;
using System.Buffers.Binary;

namespace ZeroAD.Sim.RL;

/// <summary>File layout v4: 64-byte global header + N slots.
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
    /// <summary>Optional per-reset episode cap (0 = use host --max-turns).</summary>
    public const int OffMaxTurns = 32;

    public const int EntitiesBytes = RlSpec.MaxEntities * RlSpec.EntityFeat * sizeof(int);
    public const int SpatialBytes = RlSpec.SpatialChannels * RlSpec.SpatialSize * RlSpec.SpatialSize * sizeof(int);
    public const int ScalarsBytes = RlSpec.ScalarCount * sizeof(int);
    public const int MaskBytes = 32;
    public const int EntityMaskBytes = RlSpec.MaxEntities * sizeof(uint);
    public const int CatalogBytes = RlSpec.CatalogLength * sizeof(int);
    public const int ActionBlockBytes = 64;
    public const int ActionBytes = ActionBlockBytes * 2;

    public const int OffTurn = 0;
    public const int OffReward = 4;
    public const int OffDone = 8;
    public const int OffEntities = 16;
    public const int OffSpatial = OffEntities + EntitiesBytes;
    public const int OffScalars = OffSpatial + SpatialBytes;
    public const int OffMask = OffScalars + ScalarsBytes;
    public const int OffEntityMask = OffMask + MaskBytes;
    public const int OffCatalog = OffEntityMask + EntityMaskBytes;
    public const int OffAction = OffCatalog + CatalogBytes;
    public const int SlotBytes = OffAction + ActionBytes;

    public const int ActFunction = 0;
    public const int ActTarget = 4;
    public const int ActCellX = 8;
    public const int ActCellZ = 12;
    public const int ActCatalog = 16;
    public const int ActSelected0 = 20;
    public const int ActOppBase = ActionBlockBytes;

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
        var em = slot.Slice(ShmLayout.OffEntityMask, ShmLayout.EntityMaskBytes);
        for (int i = 0; i < obs.EntityMask.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(em.Slice(i * 4), obs.EntityMask[i]);
        CopyInts(obs.Catalog, slot.Slice(ShmLayout.OffCatalog, ShmLayout.CatalogBytes));
    }

    public static RlAction ReadAction(ReadOnlySpan<byte> slot) =>
        ReadActionBlock(slot.Slice(ShmLayout.OffAction, ShmLayout.ActionBlockBytes));

    public static RlAction ReadOpponentAction(ReadOnlySpan<byte> slot) =>
        ReadActionBlock(slot.Slice(ShmLayout.OffAction + ShmLayout.ActOppBase, ShmLayout.ActionBlockBytes));

    public static void WriteAction(Span<byte> slot, RlAction action) =>
        WriteActionBlock(slot.Slice(ShmLayout.OffAction, ShmLayout.ActionBlockBytes), action);

    public static void WriteOpponentAction(Span<byte> slot, RlAction action) =>
        WriteActionBlock(slot.Slice(ShmLayout.OffAction + ShmLayout.ActOppBase, ShmLayout.ActionBlockBytes), action);

    private static RlAction ReadActionBlock(ReadOnlySpan<byte> a)
    {
        int fn = BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActFunction));
        if (fn < 0 || fn >= RlSpec.FunctionCount) fn = 0;
        int s0 = BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActSelected0));
        int s1 = BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActSelected0 + 4));
        int s2 = BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActSelected0 + 8));
        int s3 = BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActSelected0 + 12));
        int s4 = BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActSelected0 + 16));
        int s5 = BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActSelected0 + 20));
        int s6 = BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActSelected0 + 24));
        int s7 = BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActSelected0 + 28));
        return new RlAction(
            (RlFunction)fn,
            s0,
            BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActTarget)),
            BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActCellX)),
            BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActCellZ)),
            BinaryPrimitives.ReadInt32LittleEndian(a.Slice(ShmLayout.ActCatalog)),
            s1, s2, s3, s4, s5, s6, s7);
    }

    private static void WriteActionBlock(Span<byte> a, RlAction action)
    {
        a.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(a.Slice(ShmLayout.ActFunction), (int)action.Function);
        BinaryPrimitives.WriteInt32LittleEndian(a.Slice(ShmLayout.ActTarget), action.TargetEntityIndex);
        BinaryPrimitives.WriteInt32LittleEndian(a.Slice(ShmLayout.ActCellX), action.TargetCellX);
        BinaryPrimitives.WriteInt32LittleEndian(a.Slice(ShmLayout.ActCellZ), action.TargetCellZ);
        BinaryPrimitives.WriteInt32LittleEndian(a.Slice(ShmLayout.ActCatalog), action.CatalogId);
        for (int i = 0; i < RlSpec.MaxSelected; i++)
            BinaryPrimitives.WriteInt32LittleEndian(a.Slice(ShmLayout.ActSelected0 + i * 4), action.SelectedAt(i));
    }

    private static void CopyInts(int[] src, Span<byte> dest)
    {
        for (int i = 0; i < src.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(i * 4), src[i]);
    }
}
