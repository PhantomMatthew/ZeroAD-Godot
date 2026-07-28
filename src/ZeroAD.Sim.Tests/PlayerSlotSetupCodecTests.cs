using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using ZeroAD.Sim.Net;

namespace ZeroAD.Sim.Tests;

/// <summary>Wire codec for the MP lobby slot table (Task #10 C1). The slot table is the
/// host→client setup contract that makes per-slot AI deterministic in multiplayer.</summary>
public sealed class PlayerSlotSetupCodecTests
{
    [Fact]
    public void RoundTrips_AllKinds_AndPreservesOrder()
    {
        var slots = new List<PlayerSlotSetup>
        {
            new() { PlayerId = 1, Kind = PlayerSlotKind.Human,  Civ = "athen", Team = -1 },
            new() { PlayerId = 2, Kind = PlayerSlotKind.AI,     Civ = "gaul",  Team = -1 },
            new() { PlayerId = 3, Kind = PlayerSlotKind.Closed, Civ = "athen", Team = -1 },
            new() { PlayerId = 4, Kind = PlayerSlotKind.Human,  Civ = "spart", Team = 0 },
        };

        var (kinds, civs, teams) = PlayerSlotSetupCodec.Pack(slots);
        var decoded = PlayerSlotSetupCodec.Unpack(kinds, civs, teams);

        Assert.Equal(slots.Count, decoded.Count);
        for (int i = 0; i < slots.Count; i++)
        {
            Assert.Equal(slots[i].PlayerId, decoded[i].PlayerId);
            Assert.Equal(slots[i].Kind, decoded[i].Kind);
            Assert.Equal(slots[i].Civ, decoded[i].Civ);
            Assert.Equal(slots[i].Team, decoded[i].Team);
        }
    }

    [Fact]
    public void RoundTrips_Empty()
    {
        var (kinds, civs, teams) = PlayerSlotSetupCodec.Pack(new List<PlayerSlotSetup>());
        Assert.Empty(kinds);
        Assert.Empty(civs);
        Assert.Empty(teams);
        var decoded = PlayerSlotSetupCodec.Unpack(kinds, civs, teams);
        Assert.Empty(decoded);
    }

    [Fact]
    public void RoundTrips_PreservesCivsAndTeams()
    {
        var slots = new List<PlayerSlotSetup>
        {
            new() { PlayerId = 1, Kind = PlayerSlotKind.Human, Civ = "athen", Team = 0 },
            new() { PlayerId = 2, Kind = PlayerSlotKind.AI,    Civ = "spart", Team = 0 },
            new() { PlayerId = 3, Kind = PlayerSlotKind.AI,    Civ = "gaul",  Team = 1 },
        };
        var (kinds, civs, teams) = PlayerSlotSetupCodec.Pack(slots);
        var decoded = PlayerSlotSetupCodec.Unpack(kinds, civs, teams);

        Assert.Equal(new[] { "athen", "spart", "gaul" }, decoded.Select(s => s.Civ).ToArray());
        Assert.Equal(new[] { 0, 0, 1 }, decoded.Select(s => s.Team).ToArray());
    }

    [Fact]
    public void PlayerId_IsImpliedBySlotOrder()
    {
        // Pack ignores PlayerId; Unpack rebuilds it as i+1. A deliberately wrong PlayerId
        // on the input must not survive — the wire is the source of truth.
        var slots = new List<PlayerSlotSetup>
        {
            new() { PlayerId = 99, Kind = PlayerSlotKind.Human },
            new() { PlayerId = 7,  Kind = PlayerSlotKind.AI },
        };
        var (kinds, civs, teams) = PlayerSlotSetupCodec.Pack(slots);
        var decoded = PlayerSlotSetupCodec.Unpack(kinds, civs, teams);

        Assert.Equal(1, decoded[0].PlayerId);
        Assert.Equal(2, decoded[1].PlayerId);
    }

    [Fact]
    public void Rejects_UnequalArrayLengths()
    {
        Assert.Throws<ArgumentException>(() =>
            PlayerSlotSetupCodec.Unpack(new[] { 1, 2 }, new[] { "athen" }, new[] { -1, -1 }));
    }

    [Fact]
    public void Rejects_MoreThanMaxSlots()
    {
        var tooMany = Enumerable.Range(1, PlayerSlotSetupCodec.MaxSlots + 1)
            .Select(i => new PlayerSlotSetup { PlayerId = i, Kind = PlayerSlotKind.Human }).ToList();
        Assert.Throws<ArgumentException>(() => PlayerSlotSetupCodec.Pack(tooMany));

        int n = PlayerSlotSetupCodec.MaxSlots + 1;
        Assert.Throws<ArgumentException>(() =>
            PlayerSlotSetupCodec.Unpack(new int[n], new string[n], new int[n]));
    }
}
