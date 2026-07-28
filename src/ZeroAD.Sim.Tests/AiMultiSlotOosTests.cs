using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Net;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Task #10 MP-AI OOS proof. Two NetTurnManager peers (Host + Client) wired with a synchronous
/// in-memory transport, with an <see cref="AIComponent"/> on a NON-human slot (player 3) of BOTH
/// sides. This is the automated equivalent of the manual two-instance MP smoke:
/// <list type="bullet">
/// <item>The AI's commands ride the local <c>_aiBundles</c> channel on each peer (submitted via
/// <c>SubmitAiCommand</c>), never the network batch — so two peers running the same AIComponent
/// from the same seed generate identical commands and stay hash-equal.</item>
/// <item>The AI slot is excluded from <c>expectedPlayers</c> (Human-only {1,2}). Including it
/// would deadlock the host, which waits for every expected player's batch. The NoDeadlock test
/// pins that guard.</item>
/// </list>
/// Mirrors <c>NetLockstepTests.TwoPeerLockstep</c> (transport) + <c>AiBrainDeterminismTests</c>
/// (the AI world shape). Construction order matters: <c>NetTurnManager</c> must exist before
/// <c>AIComponent.Configure(cm, net)</c>, which must run before <c>AddComponent</c>.
/// </summary>
public sealed class AiMultiSlotOosTests
{
    private const int CommandDelay = 2;
    private const int AiPlayer = 3;

    private sealed class TwoPeerAiLockstep
    {
        public readonly ComponentManager HostCm;
        public readonly ComponentManager ClientCm;
        public readonly NetTurnManager Host;
        public readonly NetTurnManager Client;
        private readonly AIComponent _hostAi;
        private readonly AIComponent _clientAi;

        public TwoPeerAiLockstep(uint seed = 42)
        {
            // HUMAN-ONLY expectedPlayers: AI slot 3 rides the local _aiBundles channel, never
            // the network. This is the Task #10 deadlock guard — the host must not wait for 3.
            var humans = new HashSet<uint> { 1, 2 };

            HostCm = BuildAiSide(seed, humans, NetRole.Host, localPlayerId: 1, out _hostAi, out Host);
            ClientCm = BuildAiSide(seed, humans, NetRole.Client, localPlayerId: 2, out _clientAi, out Client);

            // Synchronous transport, identical wiring to NetLockstepTests.TwoPeerLockstep. The
            // host's own (player 1) batch is self-ingested inside AdvanceTurn; the client ships
            // its (player 2) batch to the host; the host aggregates both into a bundle that both
            // peers receive. AI commands never touch this path.
            Host.OnTurnBundleReady += (turn, cmds) =>
            {
                byte[] data = NetCommand.SerializeBatch(cmds);
                Host.ReceiveTurnBundle(turn, NetCommand.DeserializeBatch(data));
                Client.ReceiveTurnBundle(turn, NetCommand.DeserializeBatch(data));
            };
            Client.OnBatchDue += (turn, cmds) => Host.HostIngestBatch(2, turn, cmds);

            Host.HostBootstrap();
        }

        public void Pump(int turns)
        {
            for (int i = 0; i < turns; i++)
            {
                Assert.True(Host.CanAdvanceTurn(), $"host stalled at turn {Host.CurrentTurn}");
                Assert.True(Client.CanAdvanceTurn(), $"client stalled at turn {Client.CurrentTurn}");
                // Per-turn order mirrors SimBridge: TickAI reacts to this turn's state, then
                // AdvanceTurn drains the outbox + executes bundles AND _aiBundles for this turn.
                _hostAi.Tick();
                _clientAi.Tick();
                Host.AdvanceTurn();
                Client.AdvanceTurn();
            }
        }
    }

    /// <summary>One peer's AI world. Mirrors <c>AiBrainDeterminismTests.BuildAiWorld</c> but:
    /// (a) registers HUMAN player entities for the network slots (1, 2) that stay silent;
    /// (b) the AI is on slot 3; (c) the NetRole + localPlayerId + expectedPlayers are parameterized
    /// so the same builder constructs both peers identically from the same seed.</summary>
    private static ComponentManager BuildAiSide(uint seed, HashSet<uint> expectedPlayers,
        NetRole role, uint localPlayerId, out AIComponent ai, out NetTurnManager net)
    {
        var cm = new ComponentManager(seed);
        SimSystem.Init(cm);

        // Human slots (1, 2): they hold the network slots but never submit commands in these
        // tests. Registered so PlayerManager has the full table diplomacy/hash expect.
        for (uint p = 1; p <= 2; p++)
        {
            var ent = cm.CreateEntity();
            cm.AddComponent(ent, new PlayerComponent());
            cm.AddComponent(ent, new OwnershipComponent { PlayerId = (int)p });
            cm.Players.AddPlayer((int)p, ent);
        }

        // AI slot (3): rich, mirrors BuildAiWorld so the brain can issue build decisions that
        // draw ComponentManager.RNG — the determinism risk. Resources set after AddComponent of
        // PlayerComponent.OnInit would reset them, so set them on the queried instance.
        var player = cm.CreateEntity();
        cm.AddComponent(player, new PlayerComponent());
        var pc = cm.QueryInterface<PlayerComponent>(player)!;
        pc.Wood = 1000; pc.Food = 1000; pc.Stone = 500; pc.Metal = 500; pc.PopBonuses = 50;
        cm.Players.AddPlayer(AiPlayer, player);
        // AIComponent.Tick reads THIS entity's OwnershipComponent as the single playerId source.
        cm.AddComponent(player, new OwnershipComponent { PlayerId = AiPlayer });

        var tc = cm.CreateEntity();
        cm.AddComponent(tc, new PositionComponent());
        cm.AddComponent(tc, new IdentityComponent { Name = "Civil Centre", IsBuilding = true });
        cm.AddComponent(tc, new OwnershipComponent { PlayerId = AiPlayer });

        var villager = cm.CreateEntity();
        cm.AddComponent(villager, new PositionComponent());
        cm.AddComponent(villager, new IdentityComponent { IsUnit = true });
        cm.AddComponent(villager, new OwnershipComponent { PlayerId = AiPlayer });
        cm.AddComponent(villager, new ResourceGatherer());   // → lands in snapshot.Villagers
        cm.AddComponent(villager, new BuilderComponent());   // → BuildManager.TryBuild can pick it

        net = new NetTurnManager(cm, CommandDelay, localPlayerId, role, expectedPlayers);

        ai = new AIComponent();
        ai.Configure(cm, net);          // AuraComponent.Configure pattern: before AddComponent.
        cm.AddComponent(player, ai);
        return cm;
    }

    [Fact]
    public void TwoPeers_WithAiSlot_ProduceIdenticalHashes_For100Turns()
    {
        // The MP OOS contract for per-slot AI: two peers, same seed, AI on a non-human slot.
        // The AI thinks every 5 turns (0,5,…,95 = 20 thinks) and its commands execute locally
        // via _aiBundles on each peer at commandDelay. Same seed → same RNG draws → same commands
        // → same foundations → hash-equal every turn. This is the automated #10 OOS proof.
        var net = new TwoPeerAiLockstep(seed: 42);
        for (int t = 0; t < 100; t++)
        {
            net.Pump(1);
            Assert.Equal(net.HostCm.ComputeStateHash(), net.ClientCm.ComputeStateHash());
        }
    }

    [Fact]
    public void TwoPeers_WithAiSlot_NoDeadlock_WhenHumansSilent()
    {
        // The Task #10 deadlock guard: AI slot 3 is excluded from expectedPlayers, so the host
        // (which waits for every expected player's batch) never blocks on slot 3 — even though
        // no human ever submits a command and the AI's commands ride the local channel only.
        // Pump asserts CanAdvanceTurn each turn; a deadlock would surface as a stall assertion.
        var net = new TwoPeerAiLockstep(seed: 42);
        net.Pump(50);
        Assert.Equal(net.HostCm.ComputeStateHash(), net.ClientCm.ComputeStateHash());
    }
}
