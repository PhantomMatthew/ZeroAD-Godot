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
        Assert.Equal(4, ShmLayout.Version);
        Assert.Equal(64, ShmLayout.HeaderBytes);
        Assert.Equal(512 * 20 * 4, ShmLayout.EntitiesBytes);
        Assert.Equal(5 * 64 * 64 * 4, ShmLayout.SpatialBytes);
        Assert.Equal(11 * 4, ShmLayout.ScalarsBytes);
        Assert.Equal(32, ShmLayout.MaskBytes);
        Assert.Equal(512 * 4, ShmLayout.EntityMaskBytes);
        Assert.Equal(512 * 3 * 16 * 4, ShmLayout.CatalogBytes);
        Assert.Equal(128, ShmLayout.ActionBytes);
        Assert.Equal(8, RlSpec.MaxSelected);
        Assert.Equal(4, RlSpec.LayoutVersion);
        Assert.Equal(ShmLayout.OffCatalog, ShmLayout.OffEntityMask + ShmLayout.EntityMaskBytes);
        Assert.Equal(ShmLayout.OffAction, ShmLayout.OffCatalog + ShmLayout.CatalogBytes);
        Assert.Equal(ShmLayout.OffAction + 128, ShmLayout.SlotBytes);
        Assert.Equal(RlSpec.FunctionCount, Enum.GetValues<RlFunction>().Length);
        Assert.True(RlSpec.FunctionCount <= ShmLayout.MaskBytes);
        Assert.Equal(32, RlSpec.FunctionCount);
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
        obs.EntityMask[0] = 1u << (int)RlFunction.Move;
        obs.SetCatalog(1, RlSpec.Cat.Train, 0, 1234);
        obs.Spatial[RlSpec.Spat.Resource * RlSpec.SpatialSize * RlSpec.SpatialSize] = 50;

        var slot = new byte[ShmLayout.SlotBytes];
        ShmCodec.WriteObservation(slot, obs);
        ShmCodec.WriteAction(slot, new RlAction(RlFunction.Move, 0, -1, 4, 5, 0, 1, 2));

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
        Assert.Equal(1u << (int)RlFunction.Move, BitConverter.ToUInt32(slot, ShmLayout.OffEntityMask));
        Assert.Equal(1234, BitConverter.ToInt32(slot,
            ShmLayout.OffCatalog + RlObservation.CatalogIndex(1, RlSpec.Cat.Train, 0) * 4));
        Assert.Equal(50, BitConverter.ToInt32(slot, ShmLayout.OffSpatial
            + RlSpec.Spat.Resource * RlSpec.SpatialSize * RlSpec.SpatialSize * 4));

        var act = ShmCodec.ReadAction(slot);
        Assert.Equal(RlFunction.Move, act.Function);
        Assert.Equal(0, act.SelectedIndex);
        Assert.Equal(1, act.Selected1);
        Assert.Equal(2, act.Selected2);
        Assert.Equal(4, act.TargetCellX);
        Assert.Equal(5, act.TargetCellZ);
        Assert.Equal(1, slot[ShmLayout.OffMask + (int)RlFunction.Move]);
    }

    [Fact]
    public void ReadAction_AcceptsGuard()
    {
        var slot = new byte[ShmLayout.SlotBytes];
        ShmCodec.WriteAction(slot, new RlAction(RlFunction.Guard, 1, 2, 3, 4, 5));
        var act = ShmCodec.ReadAction(slot);
        Assert.Equal(RlFunction.Guard, act.Function);
        Assert.Equal(1, act.SelectedIndex);
        Assert.Equal(2, act.TargetEntityIndex);
    }

    [Fact]
    public void OpponentAction_UsesSecondBlock()
    {
        var slot = new byte[ShmLayout.SlotBytes];
        ShmCodec.WriteAction(slot, new RlAction(RlFunction.Attack, 3, 4, 1, 2, 0));
        ShmCodec.WriteOpponentAction(slot, new RlAction(RlFunction.Move, 10, 11, 6, 7, 8));
        var agent = ShmCodec.ReadAction(slot);
        var opp = ShmCodec.ReadOpponentAction(slot);
        Assert.Equal(RlFunction.Attack, agent.Function);
        Assert.Equal(3, agent.SelectedIndex);
        Assert.Equal(RlFunction.Move, opp.Function);
        Assert.Equal(10, opp.SelectedIndex);
        Assert.Equal(11, opp.TargetEntityIndex);
        Assert.Equal(6, opp.TargetCellX);
        Assert.Equal(7, opp.TargetCellZ);
        Assert.Equal(8, opp.CatalogId);
        Assert.Equal(RlFunction.NoOp, ShmCodec.ReadOpponentAction(new byte[ShmLayout.SlotBytes]).Function);
    }

    [Fact]
    public void Header_WritesMagicAndSlotCount()
    {
        var file = new byte[ShmLayout.FileBytes(2)];
        ShmCodec.WriteHeader(file, 2, seed: 9, privileged: true);
        Assert.Equal(ShmLayout.Magic, BitConverter.ToUInt32(file, ShmLayout.OffMagic));
        Assert.Equal(2, BitConverter.ToInt32(file, ShmLayout.OffSlots));
        Assert.Equal(4, BitConverter.ToInt32(file, ShmLayout.OffVersion));
        Assert.Equal(1, BitConverter.ToInt32(file, ShmLayout.OffPrivileged));
        Assert.Equal(9, BitConverter.ToInt32(file, ShmLayout.OffSeed));
        Assert.Equal(32, ShmLayout.OffMaxTurns);
    }

    [Fact]
    public void ExpandCivTokens_ReplacesPlaceholdersAndDropsUnresolved()
    {
        var names = RlCatalog.ExpandCivTokens(
            "units/{civ}/infantry_spearman_b units/{native}/support_civilian leftover/{x}",
            "athen", "spart");
        Assert.Equal(2, names.Count);
        Assert.Equal("units/athen/infantry_spearman_b", names[0]);
        Assert.Equal("units/spart/support_civilian", names[1]);
    }
}
