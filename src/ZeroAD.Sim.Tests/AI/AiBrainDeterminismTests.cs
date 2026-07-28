using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using ZeroAD.Sim.Serialization;
using Xunit;

namespace ZeroAD.Sim.Tests.AI;

// Phase 2 "AI 迁入内核" determinism contract. AIComponent is a serializable ComponentBase in
// the kernel; in MP both peers run identical kernel state, so the brain must run identically
// and emit identical commands through the LOCAL SubmitAiCommand channel (never the network
// outbox). If any of these fail, AI would cause OOS or lose state across save/load.
//
// (1) two identical AI worlds stay hash-equal across 100 turns incl. RNG-drawing build
//     decisions — the MP OOS smoke harness;
// (2) SubmitAiCommand never leaks into the network batch (OnBatchDue);
// (3) SubmitAiCommand executes at exactly commandDelay, no earlier;
// (4) AIComponent counters survive serialize → deserialize (idempotent re-serialize);
// (5) AIComponent hash is stable across the round-trip (catches an un-restored field).
public sealed class AiBrainDeterminismTests
{
    private const int CommandDelay = 2;

    /// <summary>Builds a minimal player-2 AI world: a rich player entity (carries its own
    /// OwnershipComponent so AIComponent.Tick can derive playerId), a Civil Centre, and one
    /// villager carrying ResourceGatherer + BuilderComponent. Resources are set AFTER
    /// AddComponent because PlayerComponent.OnInit resets them. No templates are loaded —
    /// ApplyBuild's RegisterForLos is null-guarded, so build commands spawn foundations
    /// deterministically without LFS data. The villager reaches BuildManager.TryBuild, the
    /// one Petra code path that draws from ComponentManager.RNG (the determinism risk).</summary>
    private static (ComponentManager cm, NetTurnManager net, EntityId player, AIComponent ai)
        BuildAiWorld(uint seed)
    {
        var cm = new ComponentManager(seed);
        SimSystem.Init(cm);

        var player = cm.CreateEntity();
        cm.AddComponent(player, new PlayerComponent());
        var pc = cm.QueryInterface<PlayerComponent>(player)!;
        pc.Wood = 1000; pc.Food = 1000; pc.Stone = 500; pc.Metal = 500; pc.PopBonuses = 50;
        cm.Players.AddPlayer(2, player);
        // AIComponent.Tick reads THIS entity's OwnershipComponent as the single playerId source.
        cm.AddComponent(player, new OwnershipComponent { PlayerId = 2 });

        var tc = cm.CreateEntity();
        cm.AddComponent(tc, new PositionComponent());
        cm.AddComponent(tc, new IdentityComponent { Name = "Civil Centre", IsBuilding = true });
        cm.AddComponent(tc, new OwnershipComponent { PlayerId = 2 });

        var villager = cm.CreateEntity();
        cm.AddComponent(villager, new PositionComponent());
        cm.AddComponent(villager, new IdentityComponent { IsUnit = true });
        cm.AddComponent(villager, new OwnershipComponent { PlayerId = 2 });
        cm.AddComponent(villager, new ResourceGatherer());   // → lands in snapshot.Villagers
        cm.AddComponent(villager, new BuilderComponent());   // → BuildManager.TryBuild can pick it

        var net = new NetTurnManager(cm, CommandDelay, localPlayerId: 1,
            NetRole.Standalone, new HashSet<uint> { 1, 2 });

        var ai = new AIComponent();
        ai.Configure(cm, net);          // AuraComponent.Configure pattern: before AddComponent.
        cm.AddComponent(player, ai);
        return (cm, net, player, ai);
    }

    private static void PumpTurn(AIComponent ai, NetTurnManager net)
    {
        // Mirrors SimBridge's per-turn order: TickAI (AI reacts to this turn's state) then
        // AdvanceTurn (drains outbox + executes bundles AND _aiBundles for this turn).
        ai.Tick();
        net.AdvanceTurn();
    }

    [Fact]
    public void TwoInstances_ProduceIdenticalHashes()
    {
        // Two peers built from the same seed must hash equally every turn — the MP OOS contract.
        // 100 turns = thinks at turns 0,5,...,95 (20 thinks) → BuildManager fires twice (think
        // 10 & 20), each drawing ComponentManager.RNG twice. Same seed → same RNG draws → same
        // _aiBundles → same foundations spawned → same hashes.
        var (hostCm, hostNet, _, hostAi) = BuildAiWorld(42);
        var (clientCm, clientNet, _, clientAi) = BuildAiWorld(42);

        for (int t = 0; t < 100; t++)
        {
            PumpTurn(hostAi, hostNet);
            PumpTurn(clientAi, clientNet);
            Assert.Equal(hostCm.ComputeStateHash(), clientCm.ComputeStateHash());
        }
    }

    [Fact]
    public void SubmitAiCommand_NeverAppearsInNetworkBatch_ButStillExecutes()
    {
        // The network batch (OnBatchDue) carries ONLY human SubmitLocalCommand output. AI
        // commands must travel the local _aiBundles channel — otherwise Host+Client would each
        // emit the AI's commands into their own outbox and every command would execute twice.
        var cm = new ComponentManager(7);
        SimSystem.Init(cm);
        var net = new NetTurnManager(cm, CommandDelay, localPlayerId: 1,
            NetRole.Standalone, new HashSet<uint> { 1, 2 });

        var unit = cm.CreateEntity();
        cm.AddComponent(unit, new PositionComponent());
        cm.AddComponent(unit, new UnitMotion());
        cm.AddComponent(unit, new UnitAIComponent());
        cm.AddComponent(unit, new IdentityComponent());
        cm.AddComponent(unit, new OwnershipComponent { PlayerId = 1 });

        var batches = new List<NetCommand[]>();
        net.OnBatchDue += (_, cmds) => batches.Add(cmds);

        net.SubmitAiCommand(NetCommand.Move(1, unit.Value,
            Fixed.FromFloat(10f), Fixed.FromFloat(10f)));

        for (int t = 0; t < CommandDelay + 1; t++)
            net.AdvanceTurn();

        // (1) No network batch ever carried the AI command (all batches empty).
        Assert.All(batches, b => Assert.Empty(b));
        // (2) Yet the command executed via _aiBundles → the unit received the order.
        Assert.False(cm.QueryInterface<UnitAIComponent>(unit)!.IsIdle);
    }

    [Fact]
    public void SubmitAiCommand_ExecutesAtExactlyCommandDelay()
    {
        // Mirrors NetLockstepTests.NoExecution_BeforeScheduledTurn for the AI channel.
        var cm = new ComponentManager(7);
        SimSystem.Init(cm);
        var net = new NetTurnManager(cm, CommandDelay, localPlayerId: 1,
            NetRole.Standalone, new HashSet<uint> { 1, 2 });

        var unit = cm.CreateEntity();
        cm.AddComponent(unit, new PositionComponent());
        cm.AddComponent(unit, new UnitMotion());
        cm.AddComponent(unit, new UnitAIComponent());
        cm.AddComponent(unit, new IdentityComponent());
        cm.AddComponent(unit, new OwnershipComponent { PlayerId = 1 });
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;

        net.SubmitAiCommand(NetCommand.Move(1, unit.Value,
            Fixed.FromFloat(5f), Fixed.FromFloat(5f)));
        // execTurn = currentTurn(0) + commandDelay(2) = 2. The command is consumed by the
        // AdvanceTurn that ENTERS with currentTurn=2 (the 3rd call: call 1 processes turn 0,
        // call 2 processes turn 1, call 3 processes turn 2). Symmetric with SubmitLocalCommand.

        net.AdvanceTurn();   // processes turn 0
        net.AdvanceTurn();   // processes turn 1 — commands for turns < commandDelay never execute
        Assert.True(ai.IsIdle);

        net.AdvanceTurn();   // processes turn 2 = commandDelay → _aiBundles[2] executes
        Assert.False(ai.IsIdle);
    }

    [Fact]
    public void SerializeRoundTrip_PreservesCounters()
    {
        // After pumping, AIComponent holds non-default state: lastThinkTurn, four think
        // counters, targetVillagers. Serialize → Deserialize → Serialize must be idempotent.
        var (_, _, _, ai) = BuildAiWorld(42);
        var net = new NetTurnManager(new ComponentManager(1), CommandDelay, 1,
            NetRole.Standalone, new HashSet<uint> { 1, 2 });
        // 70 turns → thinks at 0,5,…,65 (14 thinks): BuildManager fires once (think 10), so
        // counters land on non-trivial, non-reset values worth round-tripping.
        for (int t = 0; t < 70; t++)
            PumpTurn(ai, net);

        var s1 = new CapturingSerializer();
        ai.Serialize(s1);

        var ai2 = new AIComponent();
        ai2.Configure(new ComponentManager(1), net);
        ai2.Deserialize(new ReplayingDeserializer(s1));

        var s2 = new CapturingSerializer();
        ai2.Serialize(s2);

        Assert.Equal(s1.Fields, s2.Fields);
    }

    [Fact]
    public void HashStable_AcrossSerializeRoundTrip()
    {
        // A field AIComponent.Serialize writes but Deserialize fails to restore would make the
        // post-round-trip hash differ from the pre-round-trip hash. Catches exactly that.
        var (_, _, _, ai) = BuildAiWorld(42);
        var net = new NetTurnManager(new ComponentManager(1), CommandDelay, 1,
            NetRole.Standalone, new HashSet<uint> { 1, 2 });
        for (int t = 0; t < 70; t++)
            PumpTurn(ai, net);

        byte[] HashOf(AIComponent c)
        {
            var s = new HashSerializer();
            c.Serialize(s);
            return s.ComputeHash();
        }

        byte[] hashBefore = HashOf(ai);

        var cap = new CapturingSerializer();
        ai.Serialize(cap);
        var ai2 = new AIComponent();
        ai2.Configure(new ComponentManager(1), net);
        ai2.Deserialize(new ReplayingDeserializer(cap));

        Assert.Equal(hashBefore, HashOf(ai2));
    }
}
