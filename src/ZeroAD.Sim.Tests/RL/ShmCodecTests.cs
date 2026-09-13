using System;
using Xunit;
using ZeroAD.Sim.RL;

namespace ZeroAD.Sim.Tests.RL;

public sealed class ShmCodecTests
{
    [Fact]
    public void LayoutSizes_AreStable()
    {
        Assert.Equal(0x5A414452u, ShmLayout.Magic);
        Assert.Equal(1, ShmLayout.Version);
        Assert.Equal(64, ShmLayout.HeaderBytes);
        Assert.Equal(512 * 13 * 4, ShmLayout.EntitiesBytes);
        Assert.Equal(4 * 64 * 64 * 4, ShmLayout.SpatialBytes);
        Assert.Equal(11 * 4, ShmLayout.ScalarsBytes);
        Assert.Equal(ShmLayout.OffAction + 32, ShmLayout.SlotBytes);
    }

    [Fact]
    public void ObservationAndAction_RoundTrip()
    {
        var obs = new RlObservation();
        obs.Turn = 12;
        obs.Reward = -1;
        obs.Done = true;
        obs.SetEntity(0, RlSpec.Ent.Valid, 1);
        obs.SetEntity(0, RlSpec.Ent.EntityId, 42);
        obs.Spatial[3] = 7;
        obs.Scalars[RlSpec.Scal.Wood] = 99;
        obs.FunctionMask[(int)RlFunction.Move] = 1;

        var slot = new byte[ShmLayout.SlotBytes];
        ShmCodec.WriteObservation(slot, obs);
        ShmCodec.WriteAction(slot, new RlAction(RlFunction.Move, 0, -1, 4, 5, 0));

        int turn = BitConverter.ToInt32(slot, ShmLayout.OffTurn);
        int reward = BitConverter.ToInt32(slot, ShmLayout.OffReward);
        int done = BitConverter.ToInt32(slot, ShmLayout.OffDone);
        Assert.Equal(12, turn);
        Assert.Equal(-1, reward);
        Assert.Equal(1, done);
        Assert.Equal(1, BitConverter.ToInt32(slot, ShmLayout.OffEntities));
        Assert.Equal(42, BitConverter.ToInt32(slot, ShmLayout.OffEntities + RlSpec.Ent.EntityId * 4));
        Assert.Equal(7, BitConverter.ToInt32(slot, ShmLayout.OffSpatial + 3 * 4));
        Assert.Equal(99, BitConverter.ToInt32(slot, ShmLayout.OffScalars + RlSpec.Scal.Wood * 4));

        var act = ShmCodec.ReadAction(slot);
        Assert.Equal(RlFunction.Move, act.Function);
        Assert.Equal(0, act.SelectedIndex);
        Assert.Equal(4, act.TargetCellX);
        Assert.Equal(5, act.TargetCellZ);
        Assert.Equal(1, slot[ShmLayout.OffMask + (int)RlFunction.Move]);
    }

    [Fact]
    public void Header_WritesMagicAndSlotCount()
    {
        var file = new byte[ShmLayout.FileBytes(2)];
        ShmCodec.WriteHeader(file, 2, seed: 9, privileged: true);
        Assert.Equal(ShmLayout.Magic, BitConverter.ToUInt32(file, ShmLayout.OffMagic));
        Assert.Equal(2, BitConverter.ToInt32(file, ShmLayout.OffSlots));
        Assert.Equal(1, BitConverter.ToInt32(file, ShmLayout.OffPrivileged));
        Assert.Equal(9, BitConverter.ToInt32(file, ShmLayout.OffSeed));
    }
}
