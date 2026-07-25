using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Tests;

/// <summary>LOS state serialization: full-state round-trip rebuilds counts + explored,
/// and LOS data rides the state hash so lockstep divergence in fog-of-war is caught.</summary>
public sealed class LosSerializationTests
{
    private const int World = 256;

    /// <summary>World with players 1+2, a system entity hosting LosManagerComponent
    /// (explicitly wired to this world's RangeManager), a static p2 seer, and a
    /// retain-in-fog p2 structure. Returns ids so tests can add more.</summary>
    private static (ComponentManager cm, RangeManager rm, EntityId sys, EntityId p2Seer, EntityId p2Fort)
        NewWorld()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var rm = new RangeManager(cm, Fixed.FromInt(World), Fixed.FromInt(World));

        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent());
        cm.Players.AddPlayer(1, p1);
        var p2 = cm.CreateEntity();
        cm.AddComponent(p2, new PlayerComponent());
        cm.Players.AddPlayer(2, p2);

        var sys = cm.CreateEntity();
        var losComp = new LosManagerComponent();
        losComp.Attach(rm);
        cm.AddComponent(sys, losComp);

        var p2Seer = Spawn(cm, rm, 200, 200, owner: 2, range: 16);
        var p2Fort = Spawn(cm, rm, 208, 200, owner: 2, range: 0,
            flags: RangeEntityData.FlagRetainInFog);
        return (cm, rm, sys, p2Seer, p2Fort);
    }

    private static EntityId Spawn(ComponentManager cm, RangeManager rm,
        int x, int z, int owner, int range, byte flags = 0)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));
        if (owner > 0)
            cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        if (range > 0)
        {
            cm.AddComponent(e, new VisionComponent());
            cm.QueryInterface<VisionComponent>(e)!.Range = Fixed.FromInt(range);
        }
        cm.NotifyEntityCreated(e);
        rm.RefreshFromComponents(e);
        if (flags != 0)
            rm.SetEntityFlags(e, flags);
        var p = new FixedVector2D(Fixed.FromInt(x), Fixed.FromInt(z));
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    private static void Move(ComponentManager cm, EntityId e, int fromX, int fromZ, int toX, int toZ)
    {
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromInt(toX), Fixed.Zero, Fixed.FromInt(toZ));
        cm.NotifyPositionChanged(e,
            new FixedVector2D(Fixed.FromInt(fromX), Fixed.FromInt(fromZ)),
            new FixedVector2D(Fixed.FromInt(toX), Fixed.FromInt(toZ)));
    }

    private static byte[] HashLos(ComponentManager cm, EntityId sys)
    {
        var s = new HashSerializer();
        cm.QueryInterface<LosManagerComponent>(sys)!.Serialize(s);
        return s.ComputeHash();
    }

    [Fact]
    public void RoundTrip_PreservesGridStateAndRevealAll()
    {
        var (cmA, rmA, sysA, _, _) = NewWorld();
        Spawn(cmA, rmA, 100, 100, owner: 1, range: 20); // p1 seer
        rmA.SetLosRevealAll(2, true);
        rmA.UpdateVisibilityData();

        var cap = new CapturingSerializer();
        cmA.QueryInterface<LosManagerComponent>(sysA)!.Serialize(cap);

        var (cmB, rmB, sysB, _, _) = NewWorld();
        Spawn(cmB, rmB, 100, 100, owner: 1, range: 20);
        cmB.QueryInterface<LosManagerComponent>(sysB)!.Deserialize(new ReplayingDeserializer(cap));
        rmB.UpdateVisibilityData();

        Assert.Equal(HashLos(cmA, sysA), HashLos(cmB, sysB));
        Assert.True(rmB.GetLosRevealAll(2), "reveal-all mask survives the round trip");
        Assert.False(rmB.GetLosRevealAll(1));
    }

    [Fact]
    public void Rebuild_CountsMatchOriginal()
    {
        var (cmA, rmA, sysA, _, _) = NewWorld();
        Spawn(cmA, rmA, 100, 100, owner: 1, range: 20);
        Spawn(cmA, rmA, 108, 100, owner: 1, range: 20); // overlapping circles → counts of 2
        rmA.UpdateVisibilityData();

        var cap = new CapturingSerializer();
        cmA.QueryInterface<LosManagerComponent>(sysA)!.Serialize(cap);

        var (cmB, rmB, sysB, _, _) = NewWorld();
        Spawn(cmB, rmB, 100, 100, owner: 1, range: 20);
        Spawn(cmB, rmB, 108, 100, owner: 1, range: 20);
        cmB.QueryInterface<LosManagerComponent>(sysB)!.Deserialize(new ReplayingDeserializer(cap));

        int n = rmA.Los.VerticesPerSide;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(rmA.Los.GetCount(1, i, j), rmB.Los.GetCount(1, i, j));
                Assert.Equal(rmA.Los.GetCount(2, i, j), rmB.Los.GetCount(2, i, j));
            }
    }

    [Fact]
    public void Explored_SurvivesRoundTrip()
    {
        var (cmA, rmA, sysA, _, _) = NewWorld();
        var seer = Spawn(cmA, rmA, 60, 60, owner: 1, range: 16);
        Move(cmA, seer, 60, 60, 240, 40); // leaves explored-not-visible residue
        rmA.UpdateVisibilityData();

        var cap = new CapturingSerializer();
        cmA.QueryInterface<LosManagerComponent>(sysA)!.Serialize(cap);

        var (cmB, rmB, sysB, _, _) = NewWorld();
        var seerB = Spawn(cmB, rmB, 60, 60, owner: 1, range: 16);
        Move(cmB, seerB, 60, 60, 240, 40);
        cmB.QueryInterface<LosManagerComponent>(sysB)!.Deserialize(new ReplayingDeserializer(cap));

        Assert.True(rmB.Los.IsExplored(1, 15, 15), "explored residue survives");
        Assert.False(rmB.Los.IsVisible(1, 15, 15), "residue stays fogged, not visible");
        Assert.Equal(rmA.Los.GetPercentExplored(1), rmB.Los.GetPercentExplored(1));
        Assert.Equal(rmA.Los.GetPercentExplored(2), rmB.Los.GetPercentExplored(2));
    }

    [Fact]
    public void UpdateVisibility_AfterLoad_ReproducesCachedVisibilities()
    {
        var (cmA, rmA, sysA, p2SeerA, p2FortA) = NewWorld();
        var seerA = Spawn(cmA, rmA, 180, 190, owner: 1, range: 40); // sees the p2 pair
        rmA.UpdateVisibilityData();

        var cap = new CapturingSerializer();
        cmA.QueryInterface<LosManagerComponent>(sysA)!.Serialize(cap);

        var (cmB, rmB, sysB, p2SeerB, p2FortB) = NewWorld();
        var seerB = Spawn(cmB, rmB, 180, 190, owner: 1, range: 40);
        cmB.QueryInterface<LosManagerComponent>(sysB)!.Deserialize(new ReplayingDeserializer(cap));
        rmB.UpdateVisibilityData(); // fresh world: caches recomputed from HIDDEN

        Assert.Equal(rmA.GetLosVisibility(p2SeerA, 1), rmB.GetLosVisibility(p2SeerB, 1));
        Assert.Equal(rmA.GetLosVisibility(p2FortA, 1), rmB.GetLosVisibility(p2FortB, 1));
        Assert.Equal(rmA.GetLosVisibility(seerA, 1), rmB.GetLosVisibility(seerB, 1));
        Assert.Equal(LosVisibility.Visible, rmB.GetLosVisibility(p2FortB, 1));
    }

    [Fact]
    public void Determinism_1000Turns_MovingSeer()
    {
        var (cmA, rmA, _, _, _) = NewWorld();
        var seerA = Spawn(cmA, rmA, 100, 100, owner: 1, range: 20);
        var (cmB, rmB, _, _, _) = NewWorld();
        var seerB = Spawn(cmB, rmB, 100, 100, owner: 1, range: 20);

        var posA = (x: 100, z: 100);
        var posB = (x: 100, z: 100);
        for (int turn = 0; turn < 1000; turn++)
        {
            if (turn % 7 == 3)
            {
                // Deterministic pseudo-path, identical on both worlds.
                int nx = 32 + turn * 13 % 192;
                int nz = 48 + turn * 29 % 160;
                Move(cmA, seerA, posA.x, posA.z, nx, nz); posA = (nx, nz);
                Move(cmB, seerB, posB.x, posB.z, nx, nz); posB = (nx, nz);
            }
            rmA.UpdateVisibilityData();
            rmB.UpdateVisibilityData();
            Assert.Equal(cmA.ComputeStateHash(), cmB.ComputeStateHash());
        }
        Assert.True(rmA.GetPercentMapExplored(1) > 30, "the wandering seer explored a third of the map");
    }
}
