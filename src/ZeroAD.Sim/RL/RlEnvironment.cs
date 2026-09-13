using System;
using System.Collections.Generic;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using ZeroAD.Sim.Simulation;

namespace ZeroAD.Sim.RL;

/// <summary>Headless AlphaStar-style environment. Unique sim source is this process's
/// <see cref="ComponentManager"/>; Python/PyTorch talks to it via in-process calls (P0)
/// or later shared-memory / gRPC facades.</summary>
public sealed class RlEnvironment : IDisposable
{
    private readonly RlConfig _cfg;
    private readonly SimLoopState _loop = new();
    private ComponentManager? _cm;
    private RangeManager? _range;
    private NetTurnManager? _net;
    private RlCatalog _catalog = new();
    private RlObservation _obs = new();
    private bool _done;

    public ComponentManager Sim => _cm ?? throw new ObjectDisposedException(nameof(RlEnvironment));
    public RangeManager Range => _range ?? throw new ObjectDisposedException(nameof(RlEnvironment));
    public NetTurnManager Net => _net ?? throw new ObjectDisposedException(nameof(RlEnvironment));
    public RlCatalog Catalog => _catalog;
    public RlObservation Observation => _obs;
    public bool Done => _done;
    public int WorldMeters => _cfg.WorldMeters;

    public RlEnvironment(RlConfig? config = null) => _cfg = config ?? new RlConfig();

    public RlObservation Reset()
    {
        DisposeWorld();
        _done = false;
        _loop.GateTickAccum = 0f;
        _obs = new RlObservation();
        _catalog = new RlCatalog();

        var cm = new ComponentManager(_cfg.Seed);
        SimSystem.Init(cm);
        var world = Fixed.FromInt(_cfg.WorldMeters);
        var range = new RangeManager(cm, world, world);
        SimSystem.SetRangeManager(range);
        var territory = new TerritoryManager(cm, _cfg.WorldMeters);
        SimSystem.SetTerritoryManager(territory);

        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent());
        cm.AddComponent(p1, new OwnershipComponent { PlayerId = _cfg.AgentPlayerId });
        cm.Players.AddPlayer(_cfg.AgentPlayerId, p1);

        var p2 = cm.CreateEntity();
        cm.AddComponent(p2, new PlayerComponent());
        cm.AddComponent(p2, new OwnershipComponent { PlayerId = _cfg.OpponentPlayerId });
        cm.Players.AddPlayer(_cfg.OpponentPlayerId, p2);

        var expected = new HashSet<uint> { (uint)_cfg.AgentPlayerId, (uint)_cfg.OpponentPlayerId };
        var net = new NetTurnManager(cm, _cfg.CommandDelay, (uint)_cfg.AgentPlayerId,
            NetRole.Standalone, expected);
        SimSystem.SetNet(net);

        SpawnSeer(cm, range, 64, 64, _cfg.AgentPlayerId);
        SpawnSeer(cm, range, _cfg.WorldMeters - 64, _cfg.WorldMeters - 64, _cfg.OpponentPlayerId);

        if (_cfg.PrivilegedVision)
            range.SetLosRevealAll(_cfg.AgentPlayerId, true);
        range.UpdateVisibilityData();

        _cm = cm;
        _range = range;
        _net = net;
        Encode();
        return _obs;
    }

    public RlStepResult Step(RlAction action)
    {
        if (_cm == null || _range == null || _net == null)
            throw new InvalidOperationException("Reset() first.");
        if (_done)
            return new RlStepResult { Observation = _obs, Reward = 0, Done = true };

        if (ActionTranslator.TryTranslate(_obs, action, (uint)_cfg.AgentPlayerId,
                _cfg.WorldMeters, _catalog, out var cmd))
            _net.SubmitAiCommand(cmd);

        int mul = Math.Max(1, _cfg.StepMul);
        for (int i = 0; i < mul; i++)
        {
            SimLoop.Tick(_cm, 0.1f, _loop);
            if (_cfg.TickOpponentAi)
                SimLoop.TickAiBrains(_cm);
            _net.AdvanceTurn();
        }

        Encode();
        int reward = 0;
        var player = _cm.GetPlayerEntity(_cfg.AgentPlayerId);
        bool won = player?.HasWon() == true;
        bool lost = player?.IsDefeated() == true;
        bool timeout = _net.CurrentTurn >= (uint)_cfg.MaxEpisodeTurns;
        if (won) { reward = 1; _done = true; }
        else if (lost) { reward = -1; _done = true; }
        else if (timeout) _done = true;
        _obs.Done = _done;
        _obs.Reward = reward;
        return new RlStepResult { Observation = _obs, Reward = reward, Done = _done };
    }

    public void Dispose() => DisposeWorld();

    private void Encode()
    {
        ObservationEncoder.Encode(_cm!, _range!, _catalog, _cfg.AgentPlayerId,
            _cfg.PrivilegedVision, _net!.CurrentTurn, _obs,
            SimSystem.Pathfinder, SimSystem.Territory);
    }

    private static void SpawnSeer(ComponentManager cm, RangeManager rm, int x, int z, int owner)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new IdentityComponent
        {
            Name = "rl_unit",
            TemplateName = owner == 1 ? "rl/agent_unit" : "rl/enemy_unit",
            IsUnit = true
        });
        cm.AddComponent(e, new VisionComponent());
        cm.QueryInterface<VisionComponent>(e)!.Range = Fixed.FromInt(24);
        cm.AddComponent(e, new UnitAIComponent());
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new AttackComponent());
        cm.AddComponent(e, new HealthComponent { Current = 100, Max = 100 });
        cm.NotifyEntityCreated(e);
        rm.RefreshFromComponents(e);
        var p = new FixedVector2D(Fixed.FromInt(x), Fixed.FromInt(z));
        cm.NotifyPositionChanged(e, p, p);
    }

    private void DisposeWorld()
    {
        _cm = null;
        _range = null;
        _net = null;
    }
}
