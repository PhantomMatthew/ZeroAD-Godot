using System;
using System.Collections.Generic;
using System.IO;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using ZeroAD.Sim.Rmgen;
using ZeroAD.Sim.Simulation;

namespace ZeroAD.Sim.RL;

/// <summary>Headless AlphaStar-style environment. Unique sim source is this process's
/// <see cref="ComponentManager"/>; Python/PyTorch talks to it via in-process calls
/// or the shared-memory / gRPC host.</summary>
public sealed class RlEnvironment : IDisposable
{
    private readonly RlConfig _cfg;
    private readonly SimLoopState _loop = new();
    private readonly SimLoopHooks _hooks = new();
    private ComponentManager? _cm;
    private RangeManager? _range;
    private NetTurnManager? _net;
    private RlCatalog _catalog = new();
    private RlObservation _obs = new();
    private bool _done;
    private int _prevEnemyHp;
    private int _prevStock;

    private readonly List<uint>[] _controlGroups = CreateGroups();
    private int _worldMeters;
    private int _maxTurns;

    public ComponentManager Sim => _cm ?? throw new ObjectDisposedException(nameof(RlEnvironment));
    public RangeManager Range => _range ?? throw new ObjectDisposedException(nameof(RlEnvironment));
    public NetTurnManager Net => _net ?? throw new ObjectDisposedException(nameof(RlEnvironment));
    public RlCatalog Catalog => _catalog;
    public RlObservation Observation => _obs;
    public bool Done => _done;
    public int WorldMeters => _worldMeters > 0 ? _worldMeters : _cfg.WorldMeters;
    /// <summary>True when this episode loaded templates and spawned a real match (encounter, PMP, or rmgen).</summary>
    public bool LoadedRealMatch { get; private set; }
    public string AgentCiv { get; private set; } = "athen";
    public string OpponentCiv { get; private set; } = "athen";

    public RlEnvironment(RlConfig? config = null)
    {
        _cfg = config ?? new RlConfig();
        _worldMeters = _cfg.WorldMeters;
        _maxTurns = Math.Max(1, _cfg.MaxEpisodeTurns);
    }

    public void SetMaxEpisodeTurns(int turns) => _maxTurns = Math.Max(1, turns);

    private static List<uint>[] CreateGroups()
    {
        var g = new List<uint>[RlPacking.ControlGroupCount];
        for (int i = 0; i < g.Length; i++) g[i] = new List<uint>();
        return g;
    }

    public RlObservation Reset()
    {
        DisposeWorld();
        _done = false;
        LoadedRealMatch = false;
        _loop.GateTickAccum = 0f;
        _obs = new RlObservation();
        _catalog = new RlCatalog();
        _maxTurns = Math.Max(1, _cfg.MaxEpisodeTurns);
        foreach (var g in _controlGroups) g.Clear();

        TemplateLoader? templates = null;
        TechCatalog? techs = null;
        RlCatalog? catalog = null;
        bool real = _cfg.UseRealMatch
            && RlContentCache.TryGet(_cfg.DataRoot, out templates, out techs, out catalog);

        string ac;
        string oc;
        AgentCiv = _cfg.AgentCiv;
        OpponentCiv = _cfg.OpponentCiv;
        if (_cfg.RandomizeCivs)
            RlMapApply.PickCivs(_cfg.Seed, out ac, out oc);
        else
        {
            ac = AgentCiv;
            oc = OpponentCiv;
        }
        AgentCiv = ac;
        OpponentCiv = oc;

        string mapName = _cfg.MapName ?? "";
        MapExport? export = null;
        PmpTerrain? pmp = null;
        ScenarioData? scenario = null;
        int worldM = _cfg.WorldMeters;
        string? mods = RlDataRoot.FindModsPublic(_cfg.DataRoot);

        if (real && mapName.Length > 0)
        {
            if (RlMapApply.IsRmgenName(mapName))
            {
                try
                {
                    export = RlMapApply.GenerateRmgen(mapName, _cfg.Seed, _cfg.MapSize,
                        AgentCiv, OpponentCiv, mods);
                }
                catch (Exception)
                {
                    export = null;
                }
                if (export != null)
                    worldM = export.Size * (int)PmpTerrain.TileSize;
            }
            else if (mods != null)
            {
                try
                {
                    if (RlMapApply.TryLoadPmp(mods, mapName, out pmp, out scenario))
                        worldM = pmp.MapSizeMeters;
                }
                catch (Exception)
                {
                    pmp = null;
                    scenario = null;
                }
            }
        }

        _worldMeters = Math.Max(32, worldM);

        var cm = real
            ? new ComponentManager(_cfg.Seed, templates: templates!)
            : new ComponentManager(_cfg.Seed);
        SimSystem.Init(cm);
        var world = Fixed.FromInt(_worldMeters);
        var range = new RangeManager(cm, world, world);
        SimSystem.SetRangeManager(range);
        var territory = new TerritoryManager(cm, _worldMeters);
        SimSystem.SetTerritoryManager(territory);

        if (export != null)
            RlMapApply.ApplyRmgen(cm, export, mods);
        else if (pmp != null)
            RlMapApply.ApplyPmp(cm, pmp, waterMeters: 2f, mods);
        else
            SetupFlatWorld(cm);

        MakePlayer(cm, _cfg.AgentPlayerId, real ? AgentCiv : "athen",
            real ? techs : null);
        MakePlayer(cm, _cfg.OpponentPlayerId, real ? OpponentCiv : "athen",
            real ? techs : null);
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int>
        {
            [_cfg.AgentPlayerId] = 0,
            [_cfg.OpponentPlayerId] = 1
        });
        cm.EndGame.SetVictoryConditions(new[] { "conquest_units" });

        var expected = new HashSet<uint> { (uint)_cfg.AgentPlayerId, (uint)_cfg.OpponentPlayerId };
        var net = new NetTurnManager(cm, _cfg.CommandDelay, (uint)_cfg.AgentPlayerId,
            NetRole.Standalone, expected);
        SimSystem.SetNet(net);

        _hooks.SpawnBuilding = (tmpl, x, z, owner, _) => cm.SpawnEntity(tmpl, x, z, owner);

        if (real)
        {
            _catalog = catalog!;
            if (export != null)
                RlMapApply.SpawnRmgenEntities(cm, templates!, export);
            else if (scenario != null)
            {
                string? civsRoot = SkirmishReplacer.CivsRootFromTemplatesRoot(
                    Path.Combine(mods ?? "", "simulation", "templates"));
                RlMapApply.SpawnScenario(cm, templates!, scenario, AgentCiv, OpponentCiv, civsRoot);
            }
            else
                SpawnEncounter(cm, templates!);
            LoadedRealMatch = true;
            if (_cfg.TickOpponentAi)
                AttachPetra(cm, net, templates!, techs!);
        }
        else
        {
            SpawnSeer(cm, range, 64, 64, _cfg.AgentPlayerId);
            SpawnSeer(cm, range, _worldMeters - 64, _worldMeters - 64, _cfg.OpponentPlayerId);
        }

        if (_cfg.PrivilegedVision)
            range.SetLosRevealAll(_cfg.AgentPlayerId, true);
        range.UpdateVisibilityData();

        _cm = cm;
        _range = range;
        _net = net;
        SimSystem.Bind(cm);
        Encode();
        SnapshotShaping();
        return _obs;
    }

    public RlStepResult Step(RlAction action, RlAction? opponentAction = null)
    {
        if (_cm == null || _range == null || _net == null)
            throw new InvalidOperationException("Reset() first.");
        if (_done)
            return new RlStepResult { Observation = _obs, Reward = 0, Done = true };

        SimSystem.Bind(_cm);
        Submit(_obs, action, (uint)_cfg.AgentPlayerId);
        if (opponentAction is { } opp && opp.Function != RlFunction.NoOp)
            Submit(_obs, opp, (uint)_cfg.OpponentPlayerId);

        int mul = Math.Max(1, _cfg.StepMul);
        for (int i = 0; i < mul; i++)
        {
            SimLoop.Tick(_cm, 0.1f, _loop, hooks: _hooks);
            if (_cfg.TickOpponentAi)
                SimLoop.TickAiBrains(_cm);
            _net.AdvanceTurn();
        }

        Encode();
        int enemyHp = SumUnitHp(_cfg.OpponentPlayerId);
        int stock = Stockpile();
        int shape = 0;
        if (_prevEnemyHp - enemyHp >= 50) shape += 1;
        if (stock - _prevStock >= 100) shape += 1;

        int reward = 0;
        var player = _cm.GetPlayerEntity(_cfg.AgentPlayerId);
        bool won = player?.HasWon() == true;
        bool lost = player?.IsDefeated() == true;
        bool timeout = _net.CurrentTurn >= (uint)_maxTurns;
        if (won) { reward = 1; _done = true; }
        else if (lost) { reward = -1; _done = true; }
        else if (timeout) _done = true;
        else reward = shape;
        _prevEnemyHp = enemyHp;
        _prevStock = stock;
        _obs.Done = _done;
        _obs.Reward = reward;
        return new RlStepResult { Observation = _obs, Reward = reward, Done = _done };
    }

    public void Dispose() => DisposeWorld();

    private void Submit(RlObservation obs, RlAction action, uint player)
    {
        var resolved = ActionTranslator.WithMapCells(obs, action);
        if (TryControlGroup(obs, resolved, player)) return;

        var expanded = new List<NetCommand>();
        if (ActionTranslator.TryExpand(obs, resolved, player, _worldMeters, _catalog, expanded))
        {
            foreach (var c in expanded)
                _net!.SubmitAiCommand(c);
            return;
        }

        if (ActionTranslator.FansOut(resolved.Function))
        {
            bool submitted = false;
            for (int i = 0; i < RlSpec.MaxSelected; i++)
            {
                int row = resolved.SelectedAt(i);
                if (ActionTranslator.EntityAt(obs, row) == 0) continue;
                if (!ActionTranslator.TryTranslate(obs, resolved.WithSelected(row), player,
                        _worldMeters, _catalog, out var fan))
                    continue;
                _net!.SubmitAiCommand(fan);
                submitted = true;
            }
            if (submitted) return;
        }
        if (ActionTranslator.TryTranslate(obs, resolved, player, _worldMeters, _catalog, out var cmd))
            _net!.SubmitAiCommand(cmd);
    }

    private bool TryControlGroup(RlObservation obs, RlAction action, uint player)
    {
        if (action.Function != RlFunction.Formation) return false;
        if (RlPacking.IsControlAssign(action.CatalogId, out int assign))
        {
            var g = _controlGroups[assign];
            g.Clear();
            for (int i = 0; i < RlSpec.MaxSelected; i++)
            {
                uint id = ActionTranslator.EntityAt(obs, action.SelectedAt(i));
                if (id != 0) g.Add(id);
            }
            return true;
        }
        if (!RlPacking.IsControlRecall(action.CatalogId, out int recall)) return false;
        var members = _controlGroups[recall];
        if (members.Count == 0) return false;
        _net!.SubmitAiCommand(NetCommand.FormationCmd(player, "box", members));
        return true;
    }

    private void Encode()
    {
        ObservationEncoder.Encode(_cm!, _range!, _catalog, _cfg.AgentPlayerId,
            _cfg.PrivilegedVision, _net!.CurrentTurn, _obs,
            _cm!.Pathfinder ?? SimSystem.Pathfinder,
            _cm.Territory ?? SimSystem.Territory);
    }

    private void SnapshotShaping()
    {
        _prevEnemyHp = SumUnitHp(_cfg.OpponentPlayerId);
        _prevStock = Stockpile();
    }

    private int SumUnitHp(int playerId)
    {
        if (_cm == null) return 0;
        int sum = 0;
        foreach (var e in _cm.AllEntities)
        {
            var own = _cm.QueryInterface<OwnershipComponent>(e);
            if (own == null || own.PlayerId != playerId) continue;
            var id = _cm.QueryInterface<IdentityComponent>(e);
            if (id == null || !id.IsUnit) continue;
            var hp = _cm.QueryInterface<HealthComponent>(e);
            if (hp != null) sum += hp.Current;
        }
        return sum;
    }

    private int Stockpile()
    {
        var p = _cm?.GetPlayerEntity(_cfg.AgentPlayerId);
        if (p == null) return 0;
        return p.Wood + p.Food + p.Stone + p.Metal;
    }

    private void SetupFlatWorld(ComponentManager cm)
    {
        const int tileSize = 4;
        int tiles = Math.Max(8, _worldMeters / tileSize);
        var terrain = new TerrainComponent();
        terrain.Configure(tiles, tileSize);
        var grid = new TerrainClass[tiles, tiles];
        for (int i = 0; i < tiles; i++)
            for (int j = 0; j < tiles; j++)
                grid[i, j] = TerrainClass.Land;
        terrain.SetPassabilityGrid(grid);
        SimSystem.SetTerrainComponent(terrain);
        SimSystem.SetObstructionManager(new ObstructionManager(tiles, tileSize));

        var pf = new PathfinderComponent(cm);
        string? modsPublic = RlDataRoot.FindModsPublic(_cfg.DataRoot);
        string? modsParent = modsPublic != null ? Directory.GetParent(modsPublic)?.FullName : null;
        pf.SetPassabilityConfig(modsParent);
        pf.SetTerrain(terrain);
        pf.RebuildGrid();
        SimSystem.SetPathfinder(pf);
    }

    private static EntityId MakePlayer(ComponentManager cm, int playerId, string civ,
        TechCatalog? techs)
    {
        var p = cm.CreateEntity();
        cm.AddComponent(p, new PlayerComponent { Civ = civ });
        cm.AddComponent(p, new OwnershipComponent { PlayerId = playerId });
        cm.AddComponent(p, new DiplomacyComponent());
        if (techs != null)
        {
            var tm = new TechnologyManager();
            tm.Configure(techs, civ);
            cm.AddComponent(p, tm);
            tm.ApplyResearch("phase_village", cm);
        }
        cm.Players.AddPlayer(playerId, p);
        return p;
    }

    private void SpawnEncounter(ComponentManager cm, TemplateLoader templates)
    {
        int w = _worldMeters;
        float z = w * 0.5f;
        SpawnSide(cm, templates, AgentCiv, _cfg.AgentPlayerId, 48f, z, +1f);
        SpawnSide(cm, templates, OpponentCiv, _cfg.OpponentPlayerId, w - 48f, z, -1f);
    }

    private void SpawnSide(ComponentManager cm, TemplateLoader templates, string civ,
        int owner, float baseX, float baseZ, float towardCenter)
    {
        TrySpawn(cm, templates, $"structures/{civ}/civil_centre", baseX, baseZ, owner);

        string soldier = $"units/{civ}/infantry_spearman_b";
        int nSol = Math.Max(0, _cfg.SoldiersPerSide);
        for (int i = 0; i < nSol; i++)
        {
            float ox = towardCenter * (10f + i * 3f);
            float oz = (i - (nSol - 1) * 0.5f) * 4f;
            TrySpawn(cm, templates, soldier, baseX + ox, baseZ + oz, owner);
        }

        string villager = $"units/{civ}/support_civilian";
        int nVil = Math.Max(0, _cfg.VillagersPerSide);
        for (int i = 0; i < nVil; i++)
        {
            float ox = towardCenter * 6f;
            float oz = (i - (nVil - 1) * 0.5f) * 4f;
            TrySpawn(cm, templates, villager, baseX + ox, baseZ + oz, owner);
        }

        TrySpawn(cm, templates, "gaia/tree/aleppo_pine", baseX - towardCenter * 14f, baseZ - 10f, 0);
        TrySpawn(cm, templates, "gaia/tree/aleppo_pine", baseX - towardCenter * 14f, baseZ + 10f, 0);
        TrySpawn(cm, templates, "gaia/fruit/berry_01", baseX - towardCenter * 10f, baseZ, 0);
    }

    private static void TrySpawn(ComponentManager cm, TemplateLoader templates,
        string template, float x, float z, int owner)
    {
        if (!templates.TemplateExists(template)) return;
        cm.SpawnEntity(template, x, z, owner > 0 ? owner : -1);
    }

    private void AttachPetra(ComponentManager cm, NetTurnManager net,
        TemplateLoader templates, TechCatalog techs)
    {
        var opp = cm.GetPlayerEntityId(_cfg.OpponentPlayerId);
        if (opp == null) return;
        var ai = new AIComponent();
        ai.Configure(cm, net, _cfg.PetraDifficulty);
        ai.ConfigureSharedState(new SharedState(templates, techs));
        cm.AddComponent(opp.Value, ai);
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
        LoadedRealMatch = false;
    }
}
