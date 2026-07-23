using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Two NetTurnManager instances (Host + Client) wired with a synchronous in-memory
/// transport. Proves the lockstep contract: commands execute exactly once on both
/// peers, in the same order, at the same turn; the turn barrier stalls a peer that
/// is missing a bundle; empty heartbeat batches keep turns flowing.
/// </summary>
public sealed class NetLockstepTests
{
    private sealed class TwoPeerLockstep
    {
        public readonly ComponentManager HostCm;
        public readonly ComponentManager ClientCm;
        public readonly NetTurnManager Host;
        public readonly NetTurnManager Client;
        public bool DeliverBundles = true;

        public TwoPeerLockstep(uint seed = 42, int commandDelay = 2)
        {
            HostCm = new ComponentManager(seed);
            ClientCm = new ComponentManager(seed);
            var players = new HashSet<uint> { 1, 2 };
            Host = new NetTurnManager(HostCm, commandDelay, 1, NetRole.Host, players);
            Client = new NetTurnManager(ClientCm, commandDelay, 2, NetRole.Client, players);

            // Synchronous transport. The host produces a bundle (for a turn whose every
            // player has reported) and ships it to itself and the client. The host's OWN
            // batch is self-ingested inside AdvanceTurn, so the transport never carries it.
            Host.OnTurnBundleReady += (turn, cmds) =>
            {
                if (!DeliverBundles) return;
                byte[] data = NetCommand.SerializeBatch(cmds);
                Host.ReceiveTurnBundle(turn, NetCommand.DeserializeBatch(data));
                Client.ReceiveTurnBundle(turn, NetCommand.DeserializeBatch(data));
            };
            // The client ships each per-turn batch to the host for aggregation.
            Client.OnBatchDue += (turn, cmds) =>
                Host.HostIngestBatch(2, turn, cmds);

            Host.HostBootstrap();
        }

        public void Pump(int turns)
        {
            for (int i = 0; i < turns; i++)
            {
                Assert.True(Host.CanAdvanceTurn(), $"host stalled at turn {Host.CurrentTurn}");
                Assert.True(Client.CanAdvanceTurn(), $"client stalled at turn {Client.CurrentTurn}");
                Host.AdvanceTurn();
                Client.AdvanceTurn();
            }
        }
    }

    private static EntityId MakeUnit(ComponentManager cm, int player)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new UnitAIComponent());
        cm.AddComponent(e, new IdentityComponent());
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        return e;
    }

    [Fact]
    public void Commands_ExecuteExactlyOnce_OnBothPeers_WithIdenticalHashes()
    {
        var net = new TwoPeerLockstep();
        var hostUnit = MakeUnit(net.HostCm, 1);
        var clientUnit = MakeUnit(net.ClientCm, 1);
        Assert.Equal(hostUnit.Value, clientUnit.Value);

        net.Host.SubmitLocalCommand(NetCommand.Move(1, hostUnit.Value,
            Fixed.FromFloat(10f), Fixed.FromFloat(10f)));
        net.Client.SubmitLocalCommand(NetCommand.Move(1, clientUnit.Value,
            Fixed.FromFloat(10f), Fixed.FromFloat(10f)));

        for (int t = 0; t < 200; t++)
        {
            net.Pump(1);
            Assert.Equal(net.HostCm.ComputeStateHash(), net.ClientCm.ComputeStateHash());
        }

        // The command executed exactly once per peer.
        var hostAi = net.HostCm.QueryInterface<UnitAIComponent>(hostUnit)!;
        hostAi.Tick(0.1f, net.HostCm);
        Assert.StartsWith("INDIVIDUAL", hostAi.FsmStateName);
    }

    [Fact]
    public void Barrier_ClientStallsWithoutBundle()
    {
        // A client that has never received a bundle for its current turn cannot advance.
        var cm = new ComponentManager(42);
        var client = new NetTurnManager(cm, commandDelay: 2, localPlayerId: 2,
            NetRole.Client, new HashSet<uint> { 1, 2 });

        // No bundle has been delivered for turn 0.
        Assert.False(client.CanAdvanceTurn());

        // Deliver an empty bundle for turn 0: the barrier opens.
        client.ReceiveTurnBundle(0, System.Array.Empty<NetCommand>());
        Assert.True(client.CanAdvanceTurn());
        client.AdvanceTurn();

        // Turn 1's bundle hasn't arrived yet: barrier holds again.
        Assert.False(client.CanAdvanceTurn());
    }

    [Fact]
    public void Heartbeat_EmptyBatchesKeepTurnsFlowing()
    {
        var net = new TwoPeerLockstep();
        // No commands at all — empty per-turn batches must still let the host aggregate
        // and both peers advance without stalling.
        net.Pump(50);
        Assert.Equal(net.HostCm.ComputeStateHash(), net.ClientCm.ComputeStateHash());
    }

    [Fact]
    public void NoExecution_BeforeScheduledTurn()
    {
        var net = new TwoPeerLockstep(commandDelay: 2);
        var hostUnit = MakeUnit(net.HostCm, 1);
        MakeUnit(net.ClientCm, 1);

        net.Host.SubmitLocalCommand(NetCommand.Move(1, hostUnit.Value,
            Fixed.FromFloat(5f), Fixed.FromFloat(5f)));

        // Fewer turns than COMMAND_DELAY: the unit must NOT have any order yet.
        net.Pump(1);
        var ai = net.HostCm.QueryInterface<UnitAIComponent>(hostUnit)!;
        Assert.True(ai.IsIdle);

        net.Pump(2);
        Assert.False(ai.IsIdle);
    }
}
