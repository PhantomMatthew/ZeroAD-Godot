using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Tests;

/// <summary>Alliance shared LOS (Pathway B of the original's VisionSharing): a seer's
/// vision circle is also written into each mutual ally's LOS grid, so allies see what
/// allies see. Backed by DiplomacyComponent stances + PlayerManager.GetMutualAllies.</summary>
public sealed class VisionSharingTests
{
    private const int World = 256;

    /// <summary>World with N player entities each carrying PlayerComponent + DiplomacyComponent,
    /// a system entity hosting LosManagerComponent wired to this world's RangeManager.</summary>
    private static (ComponentManager cm, RangeManager rm, EntityId sys) NewWorld(int players = 2)
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var rm = new RangeManager(cm, Fixed.FromInt(World), Fixed.FromInt(World));
        for (int p = 1; p <= players; p++)
        {
            var ent = cm.CreateEntity();
            cm.AddComponent(ent, new PlayerComponent());
            cm.AddComponent(ent, new DiplomacyComponent());
            cm.Players.AddPlayer(p, ent);
        }
        var sys = cm.CreateEntity();
        var losComp = new LosManagerComponent();
        losComp.Attach(rm);
        cm.AddComponent(sys, losComp);
        return (cm, rm, sys);
    }

    private static void MakeAllies(ComponentManager cm, int a, int b)
    {
        var ea = cm.Players.GetPlayerEntityId(a)!.Value;
        var eb = cm.Players.GetPlayerEntityId(b)!.Value;
        cm.QueryInterface<DiplomacyComponent>(ea)!.SetAlly(b);
        cm.QueryInterface<DiplomacyComponent>(eb)!.SetAlly(a);
    }

    private static EntityId Spawn(ComponentManager cm, RangeManager rm,
        int x, int z, int owner, int range)
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

    [Fact]
    public void AlliedPlayers_ShareVision()
    {
        var (cm, rm, _) = NewWorld(players: 3);
        MakeAllies(cm, 1, 2); // player 3 stays neutral to both
        Spawn(cm, rm, 100, 100, owner: 1, range: 16);

        // Player 2 (ally) sees player 1's vision; player 3 (non-ally) does not.
        Assert.True(rm.Los.IsVisible(1, 25, 25), "owner sees own seer");
        Assert.True(rm.Los.IsVisible(2, 25, 25), "ally shares the seer's vision");
        Assert.False(rm.Los.IsVisible(3, 25, 25), "non-ally does not share");
    }

    [Fact]
    public void NonAllied_NoShare()
    {
        var (cm, rm, _) = NewWorld(players: 2);
        // No alliance set: default stance is neutral → no mutual allies → no sharing.
        Spawn(cm, rm, 100, 100, owner: 1, range: 16);

        Assert.True(rm.Los.IsVisible(1, 25, 25));
        Assert.False(rm.Los.IsVisible(2, 25, 25), "neutral player does not share vision");
    }

    [Fact]
    public void MutualRequired_OneWayAllyDoesNotShare()
    {
        var (cm, rm, _) = NewWorld(players: 2);
        // One-way: player 1 considers 2 an ally, but 2 does not reciprocate.
        var e1 = cm.Players.GetPlayerEntityId(1)!.Value;
        cm.QueryInterface<DiplomacyComponent>(e1)!.SetAlly(2);
        Spawn(cm, rm, 100, 100, owner: 1, range: 16);

        Assert.Empty(cm.Players.GetMutualAllies(1));
        Assert.False(rm.Los.IsVisible(2, 25, 25), "one-way alliance does not share vision");
    }

    [Fact]
    public void DestroySeer_RemovesSharedVisionFromAlly()
    {
        var (cm, rm, _) = NewWorld(players: 2);
        MakeAllies(cm, 1, 2);
        var seer = Spawn(cm, rm, 100, 100, owner: 1, range: 16);
        Assert.True(rm.Los.IsVisible(2, 25, 25));

        cm.DestroyEntity(seer);
        Assert.False(rm.Los.IsVisible(2, 25, 25), "ally loses shared vision when seer dies");
        Assert.True(rm.Los.IsExplored(2, 25, 25), "explored residue remains");
    }

    [Fact]
    public void DiplomacyFromTeam_SeedsMutualAllies()
    {
        var (cm, _, _) = NewWorld(players: 3);
        // Same team for 1+2, different team for 3.
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { { 1, 0 }, { 2, 0 }, { 3, 1 } });

        Assert.Equal(new List<int> { 2 }, cm.Players.GetMutualAllies(1));
        Assert.Equal(new List<int> { 1 }, cm.Players.GetMutualAllies(2));
        Assert.Empty(cm.Players.GetMutualAllies(3));
    }

    [Fact]
    public void SerializeRoundTrip_VisionSharedPreserved()
    {
        var (cmA, rmA, sysA) = NewWorld(players: 2);
        MakeAllies(cmA, 1, 2);
        Spawn(cmA, rmA, 100, 100, owner: 1, range: 16);
        rmA.UpdateVisibilityData();

        var cap = new CapturingSerializer();
        cmA.QueryInterface<LosManagerComponent>(sysA)!.Serialize(cap);

        var (cmB, rmB, sysB) = NewWorld(players: 2);
        MakeAllies(cmB, 1, 2);
        Spawn(cmB, rmB, 100, 100, owner: 1, range: 16);
        cmB.QueryInterface<LosManagerComponent>(sysB)!.Deserialize(new ReplayingDeserializer(cap));
        rmB.UpdateVisibilityData();

        // After load the seer circle is rebuilt (counts re-added) and must re-expand to the ally.
        Assert.True(rmB.Los.IsVisible(2, 25, 25), "shared vision survives the round trip");

        var hashA = new HashSerializer();
        cmA.QueryInterface<LosManagerComponent>(sysA)!.Serialize(hashA);
        var hashB = new HashSerializer();
        cmB.QueryInterface<LosManagerComponent>(sysB)!.Serialize(hashB);
        Assert.Equal(hashA.ComputeHash(), hashB.ComputeHash());
    }

    [Fact]
    public void Determinism_TwoInstancesSameHash()
    {
        static (ComponentManager, RangeManager, EntityId, EntityId) Build()
        {
            var (cm, rm, sys) = NewWorld(players: 2);
            MakeAllies(cm, 1, 2);
            var seer = Spawn(cm, rm, 100, 100, owner: 1, range: 20);
            return (cm, rm, sys, seer);
        }

        var (cmA, rmA, _, seerA) = Build();
        var (cmB, rmB, _, seerB) = Build();
        var pos = (x: 100, z: 100);

        for (int turn = 0; turn < 200; turn++)
        {
            if (turn % 7 == 3)
            {
                int nx = 32 + turn * 13 % 192;
                int nz = 48 + turn * 29 % 160;
                Move(cmA, seerA, pos.x, pos.z, nx, nz);
                Move(cmB, seerB, pos.x, pos.z, nx, nz);
                pos = (nx, nz);
            }
            rmA.UpdateVisibilityData();
            rmB.UpdateVisibilityData();
            Assert.Equal(cmA.ComputeStateHash(), cmB.ComputeStateHash());
        }
    }
}
