using System.Collections.Generic;
using ZeroAD.Sim.Components;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Task #12 — one serialization round-trip per upgraded P1 stub. Each test sets non-default
/// fields (incl. lists / nested structs), serializes via <see cref="CapturingSerializer"/>,
/// deserializes into a fresh instance via <see cref="ReplayingDeserializer"/>, then re-serializes
/// and asserts the two field streams are byte-for-byte identical. Plus pinned spot-checks on the
/// fields the P0 stub got wrong — most importantly the <see cref="TurretHolderComponent"/> occupant
/// data-loss bug (P0 wrote Count then re-read default EntityIds).
/// </summary>
public sealed class P1StubsRoundTripTests
{
    [Fact]
    public void Heal_RoundTrip()
    {
        var c = new HealComponent { HealAmount = 12, Range = 25f, Rate = 3f, Target = new EntityId(7) };
        c.HealableClasses.Add("Support");
        c.HealableClasses.Add("Cavalry");
        c.UnhealableClasses.Add("Champion");

        var round = RoundTrip(c);

        Assert.Equal(7u, round.Target!.Value.Value);
        Assert.Equal(new[] { "Support", "Cavalry" }, round.HealableClasses);
        Assert.Equal(new[] { "Champion" }, round.UnhealableClasses);
    }

    [Fact]
    public void Trader_RoundTrip()
    {
        var c = new TraderComponent
        {
            FirstMarket = new EntityId(2),
            SecondMarket = new EntityId(3),
            Index = 1,
            GoodsType = ResourceType.Metal,
            TraderGain = 7,
            Market1Gain = 3,
            Market2Gain = 4,
        };

        var round = RoundTrip(c);

        Assert.Equal(2u, round.FirstMarket!.Value.Value);
        Assert.Equal(3u, round.SecondMarket!.Value.Value);
        Assert.Equal(1, round.Index);
        Assert.Equal(7, round.TraderGain);
        Assert.Equal(3, round.Market1Gain);
        Assert.Equal(4, round.Market2Gain);
    }

    [Fact]
    public void Pack_RoundTrip_IncludesPackingFlag()
    {
        var c = new PackComponent { Packed = true, Packing = true, PackTime = 8f, ElapsedTime = 2.5f };

        var round = RoundTrip(c);

        Assert.True(round.Packed);
        Assert.True(round.Packing);   // the field the P0 stub was missing
        Assert.Equal(2.5f, round.ElapsedTime);
    }

    [Fact]
    public void Garrisonable_RoundTrip()
    {
        var c = new GarrisonableComponent { Holder = new EntityId(42) };
        var round = RoundTrip(c);
        Assert.Equal(42u, round.Holder!.Value.Value);

        // Null holder (ungarrisoned) round-trips too.
        var none = RoundTrip(new GarrisonableComponent());
        Assert.Null(none.Holder);
    }

    [Fact]
    public void Turretable_RoundTrip_IncludesEjectableAndPointName()
    {
        var c = new TurretableComponent { Holder = new EntityId(9), Ejectable = false, TurretPointName = "tower-top" };

        var round = RoundTrip(c);

        Assert.Equal(9u, round.Holder!.Value.Value);
        Assert.False(round.Ejectable);
        Assert.Equal("tower-top", round.TurretPointName);
    }

    [Fact]
    public void TurretHolder_RoundTrip_PreservesOccupants()
    {
        // The P0 bug: only Capacity + Count were serialized; Deserialize re-added default
        // EntityIds, silently dropping every occupant. This pins the fix.
        var c = new TurretHolderComponent();
        c.TurretPoints.Add(new TurretHolderComponent.TurretPoint { Name = "archer-left", Entity = new EntityId(11), Ejectable = true });
        c.TurretPoints.Add(new TurretHolderComponent.TurretPoint { Name = "archer-right", Entity = null, Ejectable = false });

        var round = RoundTrip(c);

        Assert.Equal(2, round.TurretPoints.Count);
        Assert.Equal("archer-left", round.TurretPoints[0].Name);
        Assert.Equal(11u, round.TurretPoints[0].Entity!.Value.Value);
        Assert.True(round.TurretPoints[0].Ejectable);
        Assert.Equal("archer-right", round.TurretPoints[1].Name);
        Assert.Null(round.TurretPoints[1].Entity);          // empty point stays empty
        Assert.False(round.TurretPoints[1].Ejectable);
    }

    [Fact]
    public void TreasureCollector_RoundTrip_IncludesMaxDistance()
    {
        var c = new TreasureCollectorComponent { MaxDistance = 12.5f, Treasure = new EntityId(5) };
        var round = RoundTrip(c);
        Assert.Equal(12.5f, round.MaxDistance);
        Assert.Equal(5u, round.Treasure!.Value.Value);
    }

    [Fact]
    public void Formation_RoundTrip_IncludesAddedFields()
    {
        var c = new FormationComponent
        {
            Shape = "line",
            MaxRowsUsed = 4,
            Width = 12f,
            Depth = 6f,
            FormationSeparation = 2.5f,
        };
        c.MaxColumnsUsed.Add(8);
        c.Members.Add(new EntityId(10));
        c.Members.Add(new EntityId(11));
        c.FinishedEntities.Add(new EntityId(10));
        c.TwinFormations.Add(new EntityId(20));
        c.SortingClasses.Add("Cavalry");
        c.SortingClasses.Add("Infantry");

        var round = RoundTrip(c);

        Assert.Equal("line", round.Shape);
        Assert.Equal(4, round.MaxRowsUsed);
        Assert.Equal(new[] { 8 }, round.MaxColumnsUsed);
        Assert.Equal(12f, round.Width);
        Assert.Equal(6f, round.Depth);
        Assert.Equal(2.5f, round.FormationSeparation);
        Assert.Equal(new[] { 10u, 11u }, Ids(round.Members));
        Assert.Equal(new[] { 10u }, Ids(round.FinishedEntities));
        Assert.Equal(new[] { 20u }, Ids(round.TwinFormations));
        Assert.Equal(new[] { "Cavalry", "Infantry" }, round.SortingClasses);
    }

    // --- harness ---

    private static T RoundTrip<T>(T original) where T : ComponentBase, new()
    {
        var s1 = new CapturingSerializer();
        original.Serialize(s1);

        var restored = new T();
        restored.Deserialize(new ReplayingDeserializer(s1));

        // Re-serialize and demand an identical field stream — catches any field written but not
        // restored (or restored into the wrong slot).
        var s2 = new CapturingSerializer();
        restored.Serialize(s2);
        Assert.Equal(s1.Fields, s2.Fields);
        return restored;
    }

    private static uint[] Ids(List<EntityId> list)
    {
        var arr = new uint[list.Count];
        for (int i = 0; i < list.Count; i++) arr[i] = list[i].Value;
        return arr;
    }
}
