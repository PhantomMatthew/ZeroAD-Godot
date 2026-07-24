using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using ZeroAD.Sim.Tutorial;

namespace ZeroAD.Godot;

public sealed partial class SimBridge : Node
{
    private ComponentManager _sim = null!;
    private NetTurnManager _netTurn = null!;
    private double _simAccumulator;
    private const double SimTickRate = 0.1;

    private readonly Dictionary<EntityId, Node3D> _entityNodes = new();
    private readonly Dictionary<EntityId, AnimationPlayer> _animPlayers = new();
    private readonly Dictionary<EntityId, string> _animState = new();
    private readonly Dictionary<EntityId, Vector3> _lastPos = new();
    private EntityId? _playerEntity;
    private ObstructionManager _obstructions = null!;
    private RangeManager _range = null!;
    private PathfinderComponent _pathfinder = null!;
    private TerrainComponent _terrain = null!;
    private EntityId _terrainEntity;
    private readonly Dictionary<uint, EntityId> _scenarioUidMap = new();
    private readonly List<Node3D> _decorativeNodes = new();

    /// <summary>
    /// The single shared sim event bus. Delegates to <see cref="ComponentManager.Events"/> so
    /// sim-side raises (EntityCreated, TrainingFinished, ...) and SimBridge-side raises
    /// (PlayerCommand, OwnershipChanged on death, ...) hit the same subscribers. TutorialEngine
    /// and HUD subscribe through this same reference.
    /// </summary>
    public SimEventBus Events => _sim?.Events ?? _fallbackEvents;
    private readonly SimEventBus _fallbackEvents = new();

    public TutorialEngine? Tutorial { get; private set; }
    public bool IsTutorialMode { get; private set; }

    public IReadOnlyDictionary<EntityId, Node3D> EntityNodes => _entityNodes;
    public Node3D UnitContainer { get; set; } = null!;
    public TemplateLoader? Templates { get; private set; }

    public ComponentManager Sim => _sim;

    /// <summary>The lockstep turn manager. In single-player it is Standalone (local
    /// batches aggregate synchronously, so the barrier never blocks); in multiplayer
    /// the MultiplayerController feeds it batches/bundles via the transport.</summary>
    public NetTurnManager NetTurn => _netTurn;
    public uint LocalPlayerId { get; private set; } = 1;

    /// <summary>Read-only query facade for HUD/Minimap/AI. Consolidates the scattered
    /// QueryInterface + entity-list iteration that previously lived inline in the GUI.</summary>
    public GuiInterface Gui { get; private set; } = null!;
    public ObstructionManager Obstructions => _obstructions;
    public TerrainComponent Terrain => _terrain;
    public PathfinderComponent Pathfinder => _pathfinder;
    public RangeManager Range => _range;

    public void InitWorld()
    {
        InitWorld(null);
    }

    /// <param name="seed">RNG seed — must match across peers (host assigns it in MP).</param>
    /// <param name="localPlayerId">This peer's game player id (host=1, clients assigned by host).</param>
    /// <param name="role">Standalone for SP; Host/Client for MP. Governs turn-barrier behaviour.</param>
    /// <param name="playerCount">Number of player slots to create. Host + each client own one.</param>
    public void InitWorld(string? templatesPath, uint seed = 42, uint localPlayerId = 1,
        NetRole role = NetRole.Standalone, int playerCount = 1, string civ = "athen")
    {
        var registry = new ComponentRegistry();
        registry.AutoRegister(typeof(PositionComponent).Assembly);

        // Wire templates + events into the sim so SpawnEntity / EnqueueTraining can run headless.
        TemplateLoader? templates = null;
        TechCatalog? techCatalog = null;
        if (templatesPath != null && System.IO.Directory.Exists(templatesPath))
        {
            templates = new TemplateLoader(templatesPath);
            GD.Print($"Loaded templates from: {templatesPath}");
            int count = 0;
            foreach (var kvp in templates.Cache) count++;
            if (count == 0) templates.LoadAllTemplates();
            GD.Print($"Template cache: {templates.Cache.Count} entries");

            // 科技 JSON 与模板同根(simulation/templates → simulation/data/technologies)
            var techDir = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(templatesPath, "..", "data", "technologies"));
            techCatalog = TechnologyLoader.LoadAll(techDir);
            GD.Print($"Technologies: {techCatalog.Technologies.Count} (+{techCatalog.Pairs.Count} pairs)");
        }

        _sim = new ComponentManager(seed, registry, templates);
        SimSystem.Init(_sim);
        Templates = templates;
        LocalPlayerId = localPlayerId;

        // Subscribe so the sim can ask us (the presentation layer) to build visuals whenever it
        // spawns an entity. This is the only Godot→sim coupling direction for spawn.
        _sim.Events.EntityCreated += OnEntityCreated;

        int gridSize = 64;
        float cellSize = 4.0f;
        _obstructions = new ObstructionManager(gridSize, cellSize);
        SimSystem.SetObstructionManager(_obstructions);

        // System services: RangeManager (spatial queries), Pathfinder (placement checks),
        // Terrain (passability grid filled from the heightmap by SetupTerrain). All sim-side,
        // no Godot dependency. The world size matches the obstruction grid for now.
        float worldSize = gridSize * cellSize;
        _terrain = new TerrainComponent();
        _terrain.Configure(gridSize, cellSize);
        _pathfinder = new PathfinderComponent(_sim);
        _pathfinder.SetTerrain(_terrain);
        _range = new RangeManager(_sim, Fixed.FromFloat(worldSize), Fixed.FromFloat(worldSize));
        SimSystem.SetRangeManager(_range);
        SimSystem.SetPathfinder(_pathfinder);
        SimSystem.SetWaterManager(_sim.Water);
        Gui = new GuiInterface(_sim);

        // A system entity to host the TerrainComponent so components can QueryInterface it.
        _terrainEntity = _sim.CreateEntity();
        _sim.AddComponent(_terrainEntity, _terrain);

        for (int pid = 1; pid <= playerCount; pid++)
        {
            var playerEntity = _sim.CreateEntity();
            _sim.AddComponent(playerEntity, new PlayerComponent { Civ = civ });
            var techMgr = new TechnologyManager();
            _sim.AddComponent(playerEntity, techMgr);
            _sim.AddComponent(playerEntity, new OwnershipComponent { PlayerId = pid });
            _sim.AddComponent(playerEntity, new EntityLimitsComponent());
            _sim.RegisterPlayer(pid, playerEntity);
            if (techCatalog != null)
            {
                techMgr.Configure(techCatalog, civ);
                // 开局即满足的 autoResearch 科技(phase_village、civ 加成)免费落地
                techMgr.UpdateAutoResearch(_sim);
            }
            if (pid == (int)localPlayerId)
                _playerEntity = playerEntity;
        }

        var expectedPlayers = Enumerable.Range(1, playerCount).Select(i => (uint)i).ToHashSet();
        _netTurn = new NetTurnManager(_sim, commandDelay: 2, localPlayerId, role, expectedPlayers);
    }

    public void StartTutorial()
    {
        IsTutorialMode = true;
        Tutorial = IntroductoryTutorial.Create(_sim, Events);
    }

    public ScenarioData? LoadTutorialScenario(string dataRoot)
    {
        string? xmlPath = ScenarioLoader.FindScenarioPath(dataRoot, "maps/tutorials/introductory_tutorial");
        if (xmlPath == null)
        {
            GD.PrintErr("Tutorial scenario XML not found");
            return null;
        }

        var scenario = ScenarioLoader.Load(xmlPath);
        ApplyScenarioPlayers(scenario);
        SpawnScenarioEntities(scenario);
        GD.Print($"Loaded tutorial scenario: {scenario.Entities.Count} entities ({scenario.Name})");
        return scenario;
    }

    private void ApplyScenarioPlayers(ScenarioData scenario)
    {
        foreach (var pd in scenario.Players)
        {
            if (pd.PlayerId == 1 && _playerEntity.HasValue)
            {
                var player = _sim.QueryInterface<PlayerComponent>(_playerEntity.Value);
                if (player != null)
                {
                    player.Wood = pd.Wood;
                    player.Food = pd.Food;
                    player.Stone = pd.Stone;
                    player.Metal = pd.Metal;
                    player.PopulationLimit = 20;
                }
            }
            else if (pd.PlayerId == 2)
            {
                var enemy = _sim.CreateEntity();
                _sim.AddComponent(enemy, new PlayerComponent
                {
                    Wood = pd.Wood,
                    Food = pd.Food,
                    Stone = pd.Stone,
                    Metal = pd.Metal,
                    PopulationLimit = 20
                });
                _sim.AddComponent(enemy, new OwnershipComponent { PlayerId = pd.PlayerId });
            }
        }
    }

    private void SpawnScenarioEntities(ScenarioData scenario)
    {
        _scenarioUidMap.Clear();
        foreach (var child in _decorativeNodes)
            child.QueueFree();
        _decorativeNodes.Clear();

        foreach (var def in scenario.Entities)
        {
            try
            {
                if (def.IsSimulationEntity)
                {
                    var eid = SpawnScenarioEntity(def);
                    if (def.Uid != 0)
                        _scenarioUidMap[def.Uid] = eid;
                }
                else if (def.IsActor)
                {
                    SpawnDecorativeActor(def);
                }
            }
            catch (System.Exception ex)
            {
                ZeroAD.Godot.Actors.ActorDiagnostics.Fallback(def.Template, $"spawn-exception:{ex.GetType().Name}:{ex.Message}");
                GD.PushWarning($"SimBridge: spawn failed for '{def.Template}': {ex.Message}");
            }
        }

        ZeroAD.Godot.Actors.ActorDiagnostics.DumpSummary();
    }

    private EntityId SpawnScenarioEntity(ScenarioEntityDef def)
    {
        _lastSpawnedTemplate = def.Template;
        _lastPlayerColor = GetPlayerColor(def.Player);
        TemplateStats? stats = null;
        if (Templates != null)
        {
            try { stats = Templates.ExtractStats(def.Template); }
            catch { }
        }

        bool isBuilding = def.Template.StartsWith("structures/", StringComparison.Ordinal);
        bool isGaia = def.Template.StartsWith("gaia/", StringComparison.Ordinal);
        bool isUnit = def.Template.StartsWith("units/", StringComparison.Ordinal);

        if (isBuilding)
            return SpawnScenarioBuilding(def, stats);
        if (isGaia)
            return SpawnScenarioGaia(def, stats);
        if (isUnit)
            return SpawnScenarioUnit(def, stats);

        return SpawnUnit(def.X, def.Z, stats: stats);
    }

    private EntityId SpawnScenarioBuilding(ScenarioEntityDef def, TemplateStats? stats)
    {
        var entity = _sim.CreateEntity();
        _sim.AddComponent(entity, new PositionComponent());
        _sim.AddComponent(entity, new ResourceDropsite());
        _sim.AddComponent(entity, new ProductionQueue());
        _sim.AddComponent(entity, new ResearcherComponent());
        _sim.AddComponent(entity, new RallyPointComponent());

        if (def.Template.Contains("field", StringComparison.OrdinalIgnoreCase))
        {
            var fieldSupply = new ResourceSupply();
            fieldSupply.SetTypeString("food.grain");
            fieldSupply.Amount = 100;
            fieldSupply.MaxAmount = 100;
            _sim.AddComponent(entity, fieldSupply);
        }

        var identity = new IdentityComponent
        {
            Name = stats?.Name ?? def.Template,
            TemplateName = def.Template,
            IsUnit = false,
            IsBuilding = true,
            Classes = stats?.GetClassList() ?? new List<string> { "Building" }
        };
        _sim.AddComponent(entity, identity);
        _sim.AddComponent(entity, new HealthComponent { Current = stats?.MaxHealth ?? 500, Max = stats?.MaxHealth ?? 500 });

        // Population-providing buildings (House etc.) carry their bonus as data so pop-limit
        // accounting is data-driven via RecomputePlayerPopBonus rather than hardcoded per-template.
        if (stats != null && stats.PopulationBonus > 0)
            _sim.AddComponent(entity, new PopulationComponent { Bonus = stats.PopulationBonus });

        if (def.Player > 0)
            _sim.AddComponent(entity, new OwnershipComponent { PlayerId = def.Player });

        var pos = _sim.QueryInterface<PositionComponent>(entity);
        if (pos != null)
            pos.Position = new FixedVector3D(Fixed.FromFloat(def.X), Fixed.Zero, Fixed.FromFloat(def.Z));

        // Building footprint + obstruction + build restrictions, all template-driven. This
        // replaces the legacy hardcoded BlockCircle(x,z,8f) which gave every building an 8m
        // radius regardless of actual size and was never cleared on death.
        float fpSize = stats?.FootprintSize0.ToFloat() is { } fp && fp > 0 ? fp : 12f;
        float obSize0 = stats?.ObstructionSize0.ToFloat() is { } ob0 && ob0 > 0 ? ob0 : fpSize;
        float obSize1 = stats?.ObstructionSize1.ToFloat() is { } ob1 && ob1 > 0 ? ob1 : fpSize;
        _sim.AddComponent(entity, new FootprintComponent
        {
            Shape = stats?.FootprintShape == "circle" ? FootprintShape.Circle : FootprintShape.Square,
            Size0 = Fixed.FromFloat(fpSize),
            Size1 = Fixed.FromFloat(stats?.FootprintSize1.ToFloat() is { } fp1 && fp1 > 0 ? fp1 : fpSize),
        });
        var obstruction = new ObstructionComponent
        {
            Type = ObstructionType.Static,
            Size0 = Fixed.FromFloat(obSize0),
            Size1 = Fixed.FromFloat(obSize1),
            Flags = ObstructionFlags.DefaultBlock,
        };
        _sim.AddComponent(entity, obstruction);
        _sim.AddComponent(entity, new BuildRestrictionsComponent
        {
            PlacementType = BuildPlacementType.Land,
            Category = stats?.Category ?? "Building",
        });
        obstruction.EnsureRegistered();

        CreateVisualFor(entity, GetPlayerColor(def.Player), Math.Max(fpSize * 0.5f, 4f), isBuilding: true);
        return entity;
    }

    private EntityId SpawnScenarioUnit(ScenarioEntityDef def, TemplateStats? stats)
    {
        bool isVillager = stats?.CanGather == true && stats.AttackDamage == 0;
        bool isSoldier = stats?.AttackDamage > 0 || stats?.GetClassList().Contains("CitizenSoldier") == true;
        var entity = SpawnUnit(def.X, def.Z, isVillager, isSoldier, stats);

        var identity = _sim.QueryInterface<IdentityComponent>(entity);
        if (identity != null)
        {
            identity.TemplateName = def.Template;
            identity.Classes = stats?.GetClassList() ?? identity.Classes;
        }

        if (def.Player > 0)
            _sim.AddComponent(entity, new OwnershipComponent { PlayerId = def.Player });

        return entity;
    }

    private EntityId SpawnScenarioGaia(ScenarioEntityDef def, TemplateStats? stats)
    {
        var entity = _sim.CreateEntity();
        _sim.AddComponent(entity, new PositionComponent());

        if (stats != null && stats.ResourceAmount > 0)
        {
            var supply = new ResourceSupply
            {
                Amount = stats.ResourceAmount,
                MaxAmount = stats.ResourceAmount,
                Type = stats.ResourceType
            };
            if (!string.IsNullOrEmpty(stats.ResourceTypeString))
                supply.SetTypeString(stats.ResourceTypeString);
            else if (def.Template.Contains("fruit", StringComparison.OrdinalIgnoreCase) ||
                     def.Template.Contains("berry", StringComparison.OrdinalIgnoreCase))
                supply.SetTypeString("food.fruit");
            else if (def.Template.Contains("tree", StringComparison.OrdinalIgnoreCase))
                supply.SetTypeString("wood.tree");
            _sim.AddComponent(entity, supply);
        }

        var identity = new IdentityComponent
        {
            Name = stats?.Name ?? def.Template,
            TemplateName = def.Template,
            IsUnit = false,
            IsBuilding = false,
            Classes = stats?.GetClassList() ?? new List<string>()
        };
        _sim.AddComponent(entity, identity);
        _sim.AddComponent(entity, new HealthComponent { Current = 9999, Max = 9999 });

        var pos = _sim.QueryInterface<PositionComponent>(entity);
        if (pos != null)
            pos.Position = new FixedVector3D(Fixed.FromFloat(def.X), Fixed.Zero, Fixed.FromFloat(def.Z));

        bool isTree = def.Template.Contains("tree", StringComparison.OrdinalIgnoreCase) ||
                      def.Template.Contains("bush", StringComparison.OrdinalIgnoreCase) ||
                      def.Template.Contains("fruit", StringComparison.OrdinalIgnoreCase) ||
                      def.Template.Contains("berry", StringComparison.OrdinalIgnoreCase);
        CreateVisualFor(entity,
            isTree ? new Color(0.1f, 0.5f, 0.1f) : new Color(0.5f, 0.5f, 0.3f),
            isTree ? 2.5f : 1.5f);
        return entity;
    }

    private void SpawnDecorativeActor(ScenarioEntityDef def)
    {
        bool isTree = def.Template.Contains("tree", StringComparison.OrdinalIgnoreCase);
        var color = isTree ? new Color(0.15f, 0.45f, 0.12f) : new Color(0.35f, 0.55f, 0.2f);

        Node3D? node = ModelLibrary.InstantiateForTemplate(def.Template, def.X, def.Z, color);
        if (node != null)
            node.Rotation = new Vector3(0, def.OrientationY, 0);
        else
            node = MakeFallbackBox(def, color);

        UnitContainer.AddChild(node);
        _decorativeNodes.Add(node);
    }

    private static MeshInstance3D MakeFallbackBox(ScenarioEntityDef def, Color color)
    {
        var box = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.5f, 2f, 1.5f) } };
        box.MaterialOverride = new StandardMaterial3D { AlbedoColor = color };
        float h = TerrainHeightService.Sample(def.X, def.Z);
        box.Position = new Vector3(def.X, h, def.Z);
        box.Rotation = new Vector3(0, def.OrientationY, 0);
        return box;
    }

    private bool _stallLogged;

    public override void _Process(double delta)
    {
        if (_sim == null) return;

        _simAccumulator += delta;
        while (_simAccumulator >= SimTickRate)
        {
            // Turn barrier: in lockstep the sim advances only when the bundle for the
            // upcoming turn has arrived (always true in standalone — local bundles are
            // produced synchronously). While stalled, rendering continues; only the
            // sim pauses.
            if (!_netTurn.CanAdvanceTurn())
            {
                if (!_stallLogged)
                {
                    GD.Print($"[Lockstep] waiting for turn {_netTurn.CurrentTurn} bundle");
                    _stallLogged = true;
                }
                break;
            }
            _stallLogged = false;
            _simAccumulator -= SimTickRate;
            TickSimulation((float)SimTickRate);
            _netTurn.AdvanceTurn();
        }
        SyncVisuals();
    }

    private void TickSimulation(float dt)
    {
        RemoveDeadEntities();
        TickUnitMotions(dt);
        TickUnitAI(dt);
        TickGatherers(dt);
        TickAttackers(dt);
        TickBuilders(dt);
        TickProductionQueues(dt);
        TickFoundations(dt);
        TickResearch(dt);
        // Settle any damage whose delay elapsed this turn, then advance the delay clock.
        _sim.DelayedDamage.TickPending(_sim);
        _sim.DelayedDamage.AdvanceTurn();
        // Conquest victory check — runs after dead entities are removed so the RangeManager
        // index reflects the current survivors.
        _sim.TickVictory();
    }

    private void RemoveDeadEntities()
    {
        foreach (var entity in GetAllEntitiesSnapshot())
        {
            var health = _sim.QueryInterface<HealthComponent>(entity);
            if (health != null && health.IsDead)
            {
                var owner = _sim.QueryInterface<OwnershipComponent>(entity);
                int fromPlayer = owner?.PlayerId ?? -1;
                Events.RaiseOwnershipChanged(new OwnershipChangedEvent
                {
                    Entity = entity,
                    From = fromPlayer,
                    To = -1
                });

                if (_entityNodes.TryGetValue(entity, out var node))
                {
                    node.QueueFree();
                    _entityNodes.Remove(entity);
                    _animPlayers.Remove(entity);
                    _animState.Remove(entity);
                    _lastPos.Remove(entity);
                }
                // Pop accounting: dying means the entity leaves its owner. Mirrors how Player.js
                // reacts to MT_OwnershipChanged (To = INVALID_PLAYER).
                _sim.ApplyOwnershipPopChange(entity, fromPlayer, -1);
                _sim.DestroyEntity(entity);
                _entityCacheDirty = true;
            }
        }
    }

    private void TickUnitMotions(float dt)
    {
        foreach (var entity in GetAllEntitiesSnapshot())
        {
            var motion = _sim.QueryInterface<UnitMotion>(entity);
            motion?.Tick(dt);
        }
    }

    private void TickUnitAI(float dt)
    {
        foreach (var entity in GetAllEntitiesSnapshot())
        {
            var ai = _sim.QueryInterface<UnitAIComponent>(entity);
            ai?.Tick(dt, _sim);
        }
    }

    private void TickGatherers(float dt)
    {
        foreach (var entity in GetAllEntitiesSnapshot())
        {
            var gatherer = _sim.QueryInterface<ResourceGatherer>(entity);
            if (gatherer == null || gatherer.State == ResourceGatherer.GatherState.Idle) continue;

            var motion = _sim.QueryInterface<UnitMotion>(entity);

            switch (gatherer.State)
            {
                case ResourceGatherer.GatherState.MovingToResource:
                    if (motion != null && !motion.HasMoveTarget)
                        gatherer.State = ResourceGatherer.GatherState.Gathering;
                    break;

                case ResourceGatherer.GatherState.Gathering:
                    if (gatherer.TargetSupply is { } supplyId)
                    {
                        var supply = _sim.QueryInterface<ResourceSupply>(supplyId);
                        if (supply != null && !supply.IsEmpty)
                        {
                            int gathered = supply.Take((int)(gatherer.GatherRate * dt));
                            gatherer.CarryAmount += gathered;
                            gatherer.CarryType = supply.Type;

                            if (gatherer.CarryAmount >= 10 || supply.IsEmpty)
                            {
                                gatherer.CarryAmount = System.Math.Clamp(gatherer.CarryAmount, 0, 10);
                                var dropsite = FindNearestDropsite(entity);
                                if (dropsite.HasValue && motion != null)
                                {
                                    var dpos = _sim.QueryInterface<PositionComponent>(dropsite.Value);
                                    if (dpos != null)
                                    {
                                        motion.MoveToPoint(new FixedVector2D(dpos.Position.X, dpos.Position.Z));
                                        gatherer.TargetDropsite = dropsite;
                                        gatherer.State = ResourceGatherer.GatherState.MovingToDropsite;
                                    }
                                }
                            }
                        }
                        else
                        {
                            FindAndGatherNewResource(entity, gatherer.CarryType);
                        }
                    }
                    break;

                case ResourceGatherer.GatherState.MovingToDropsite:
                    if (motion != null && !motion.HasMoveTarget && gatherer.TargetDropsite.HasValue)
                    {
                        var player = GetPlayer();
                        if (player != null)
                            player.AddResource(gatherer.CarryType, gatherer.CarryAmount);
                        gatherer.CarryAmount = 0;

                        FindAndGatherNewResource(entity, gatherer.CarryType);
                    }
                    break;
            }
        }
    }

    private void FindAndGatherNewResource(EntityId entity, ResourceType type)
    {
        var motion = _sim.QueryInterface<UnitMotion>(entity);
        var newSupply = FindNearestResource(entity, type);
        if (newSupply.HasValue && motion != null)
            GatherResource(entity, newSupply.Value, motion);
        else
        {
            var g = _sim.QueryInterface<ResourceGatherer>(entity);
            if (g != null) g.State = ResourceGatherer.GatherState.Idle;
        }
    }

    private void TickAttackers(float dt)
    {
        foreach (var entity in GetAllEntitiesSnapshot())
        {
            var attack = _sim.QueryInterface<AttackComponent>(entity);
            attack?.Tick(dt, _sim);
        }
    }

    private void TickBuilders(float dt)
    {
        foreach (var entity in GetAllEntitiesSnapshot())
        {
            var builder = _sim.QueryInterface<BuilderComponent>(entity);
            builder?.Tick(_sim);
        }
    }

    private void TickFoundations(float dt)
    {
        var completed = new List<EntityId>();
        foreach (var entity in GetAllEntitiesSnapshot())
        {
            var foundation = _sim.QueryInterface<FoundationComponent>(entity);
            if (foundation == null) continue;

            if (!foundation.IsBuilt)
            {
                if (_entityNodes.TryGetValue(entity, out var node))
                {
                    var mat = new StandardMaterial3D();
                    float alpha = 0.3f + 0.7f * foundation.BuildFraction;
                    mat.AlbedoColor = new Color(0.6f, 0.5f, 0.4f, alpha);
                    mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    if (node is MeshInstance3D mi && mi.Mesh is BoxMesh bm)
                        bm.Material = mat;
                }
                continue;
            }

            completed.Add(entity);
        }

        foreach (var entity in completed)
        {
            var foundation = _sim.QueryInterface<FoundationComponent>(entity)!;
            var pos = _sim.QueryInterface<PositionComponent>(entity);
            var identity = _sim.QueryInterface<IdentityComponent>(entity);
            // Prefer the full template name carried by IdentityComponent (the kernel
            // SimCommandExecutor stores it there for player-placed foundations). Fall back to
            // ResultTemplate mapped through the UI-name table for legacy/scenario foundations
            // that still store a short display name.
            string fullTemplate = !string.IsNullOrEmpty(identity?.TemplateName)
                ? identity!.TemplateName
                : MapBuildNameToTemplate(foundation.ResultTemplate);
            float x = pos?.Position.X.ToFloat() ?? 0;
            float z = pos?.Position.Z.ToFloat() ?? 0;
            var owner = _sim.QueryInterface<OwnershipComponent>(entity);

            if (_entityNodes.TryGetValue(entity, out var oldNode))
            {
                oldNode.QueueFree();
                _entityNodes.Remove(entity);
            }
            _sim.DestroyEntity(entity);

            TemplateStats? stats = null;
            try { stats = Templates?.ExtractStats(fullTemplate); } catch { }
            var built = SpawnScenarioBuilding(new ScenarioEntityDef
            {
                Template = fullTemplate,
                X = x,
                Z = z,
                Player = owner?.PlayerId ?? 1
            }, stats);

            Events.RaiseStructureBuilt(new StructureBuiltEvent
            {
                Building = built,
                TemplateName = fullTemplate
            });

            // Pop bonus is data-driven now: PopulationComponent on the building (added in
            // SpawnScenarioBuilding from template stats) feeds PlayerComponent.PopBonuses via
            // RecomputePlayerPopBonus. This replaces the hardcoded "if house, +10" rule.
            if (owner != null)
                _sim.RecomputePlayerPopBonus(owner.PlayerId);

            // The completed building registered a static obstruction (via SpawnScenarioBuilding →
            // ObstructionComponent.EnsureRegistered). Rebuild the pathfinder's navcell grid so the
            // new obstacle blocks pathing. (P0: full rebuild; incremental region update is P1.)
            _pathfinder.RebuildGrid();

            AutoAssignIdleBuilders(x, z);
        }
    }

    private void AutoAssignIdleBuilders(float bx, float bz)
    {
        EntityId? nearest = null;
        float nearestDist = 30f * 30f;
        foreach (var e in GetAllEntitiesSnapshot())
        {
            var supply = _sim.QueryInterface<ResourceSupply>(e);
            if (supply == null || supply.Amount <= 0) continue;
            var pos = _sim.QueryInterface<PositionComponent>(e);
            if (pos == null) continue;
            float dx = pos.Position.X.ToFloat() - bx;
            float dz = pos.Position.Z.ToFloat() - bz;
            float d2 = dx * dx + dz * dz;
            if (d2 < nearestDist)
            {
                nearestDist = d2;
                nearest = e;
            }
        }
        if (nearest == null) return;

        foreach (var e in GetAllEntitiesSnapshot())
        {
            var builder = _sim.QueryInterface<BuilderComponent>(e);
            if (builder == null || builder.Target != null) continue;
            var gatherer = _sim.QueryInterface<ResourceGatherer>(e);
            if (gatherer == null) continue;
            var motion = _sim.QueryInterface<UnitMotion>(e);
            if (motion == null || motion.HasMoveTarget) continue;
            GatherResource(e, nearest.Value, motion);
        }
    }

    private static string MapBuildNameToTemplate(string name) => name switch
    {
        "House" => "structures/spart/house",
        "Storehouse" => "structures/spart/storehouse",
        "Farmstead" => "structures/spart/farmstead",
        "Field" => "structures/spart/field",
        "Barracks" => "structures/spart/barracks",
        "Outpost" => "structures/spart/outpost",
        "Tower" => "structures/spart/defense_tower",
        "Forge" => "structures/spart/forge",
        "Market" => "structures/spart/market",
        "Temple" => "structures/spart/temple",
        "Arsenal" => "structures/spart/arsenal",
        _ => $"structures/spart/{name.ToLowerInvariant()}"
    };

    private void TickResearch(float dt)
    {
        var techMgr = _playerEntity.HasValue
            ? _sim.QueryInterface<TechnologyManager>(_playerEntity.Value)
            : null;
        if (techMgr == null) return;

        foreach (var entity in GetAllEntitiesSnapshot())
        {
            var researcher = _sim.QueryInterface<ResearcherComponent>(entity);
            if (researcher == null) continue;
            string? prev = researcher.CurrentTech;
            var completed = researcher.Tick(dt, techMgr, _sim);
            if (completed != null)
            {
                // 修改值已在 ApplyResearch 内落地;手动研究可能解锁新的 autoResearch 科技
                techMgr.UpdateAutoResearch(_sim);
                // TODO(Task 4): ValueModificationApplier.RescaleHealth(_sim, _playerEntity.Value)
                Events.RaiseResearchFinished(new ResearchFinishedEvent
                {
                    ResearcherEntity = entity,
                    Tech = completed
                });
            }
        }
    }

    private void TickProductionQueues(float dt)
    {
        // Training spawn, cost charging, pop/entity-limit accounting, and rally-point assignment
        // all live in the sim now (ProductionQueue.Tick + EnqueueTraining + ComponentManager).
        // We just drive the tick; visuals are built when EntityCreated fires from SpawnEntity.
        foreach (var entity in GetAllEntitiesSnapshot())
        {
            var queue = _sim.QueryInterface<ProductionQueue>(entity);
            queue?.Tick(dt, _sim);
        }
    }

    /// <summary>
    /// Build a Godot visual for a sim-spawned entity (training output). Driven by the sim's
    /// EntityCreated event so the train→spawn loop is fully sim-owned and replayable, with the
    /// presentation layer reacting to events rather than orchestrating spawn. Mirrors how
    /// CreateVisualFor is called from the legacy SimBridge.Spawn* paths.
    /// </summary>
    private void OnEntityCreated(EntityCreatedEvent e)
    {
        // If this entity already has a visual (e.g. created via the legacy SimBridge.Spawn* paths
        // which call CreateVisualFor directly), don't double up. The sim SpawnEntity path is the
        // only one that goes through this event for freshly-assembled entities.
        if (_entityNodes.ContainsKey(e.Entity)) return;

        var owner = _sim.QueryInterface<OwnershipComponent>(e.Entity);
        int playerId = owner?.PlayerId ?? e.OwnerPlayerId;
        Color color = GetPlayerColor(playerId);

        // Foundations (placed via SimCommandExecutor.ApplyBuild in the kernel) get a ghost
        // preview; everything else uses the unit-size heuristic.
        bool isFoundation = _sim.QueryInterface<FoundationComponent>(e.Entity) != null;
        if (isFoundation)
        {
            CreateVisualFor(e.Entity, new Color(0.6f, 0.5f, 0.4f, 0.3f), 6f,
                isBuilding: true, isGhost: true, templateName: e.TemplateName);
        }
        else
        {
            CreateVisualFor(e.Entity, color, 1.5f, templateName: e.TemplateName);
        }

        // Charge pop on ownership assignment. The sim owns the accounting rule so it stays
        // deterministic; we call the helper from here because ownership for sim-spawned units is
        // applied inside ComponentManager.SpawnEntity before this event fires.
        _sim.ApplyOwnershipPopChange(e.Entity, -1, playerId);
    }

    // --- Entity spawning ---

    public EntityId SpawnFromTemplate(string templateName, float x, float z)
    {
        _lastSpawnedTemplate = templateName;
        TemplateStats? stats = null;
        if (Templates != null)
        {
            try { stats = Templates.ExtractStats(templateName); } catch { }
        }
        return SpawnUnit(x, z,
            isVillager: stats?.CanGather == true,
            isSoldier: stats?.AttackDamage > 0,
            stats: stats);
    }

    public EntityId SpawnUnit(float x, float z, bool isVillager = false, bool isSoldier = false, TemplateStats? stats = null)
    {
        var entity = _sim.CreateEntity();
        _sim.AddComponent(entity, new PositionComponent());
        _sim.AddComponent(entity, new UnitMotion());
        _sim.AddComponent(entity, new UnitAIComponent());

        string name = stats?.Name ?? (isSoldier ? "Soldier" : isVillager ? "Villager" : "Unit");
        int maxHp = stats?.MaxHealth ?? (isSoldier ? 80 : 50);
        _sim.AddComponent(entity, new HealthComponent { Current = maxHp, Max = maxHp });
        var identity = new IdentityComponent
        {
            Name = name,
            TemplateName = stats?.TemplateName ?? "",
            IsUnit = true,
            Classes = stats?.GetClassList() ?? new List<string>()
        };
        if (isSoldier && !identity.HasClass("CitizenSoldier"))
            identity.Classes.Add("CitizenSoldier");
        _sim.AddComponent(entity, identity);

        var motion = _sim.QueryInterface<UnitMotion>(entity);
        if (motion != null && stats != null)
            motion.Speed = Fixed.FromFloat(stats.WalkSpeed);

        if (isVillager || stats?.CanGather == true)
        {
            _sim.AddComponent(entity, new ResourceGatherer());
            _sim.AddComponent(entity, new BuilderComponent());
        }

        if (isSoldier || (stats != null && stats.AttackDamage > 0))
        {
            var dmg = new DamageBlock();
            if (stats != null)
            {
                if (stats.AttackHack > 0) dmg.Amounts[DamageType.Hack] = stats.AttackHack;
                if (stats.AttackPierce > 0) dmg.Amounts[DamageType.Pierce] = stats.AttackPierce;
                if (stats.AttackCrush > 0) dmg.Amounts[DamageType.Crush] = stats.AttackCrush;
                dmg.Capture = stats.AttackCapture;
            }
            else
            {
                dmg.Amounts[DamageType.Hack] = 20;
            }
            var atk = new AttackComponent
            {
                Damage = dmg,
                Range = stats?.AttackRange ?? 3.0f,
                Rate = stats?.AttackRate ?? 1.0f
            };
            _sim.AddComponent(entity, atk);

            // Resistance (mirror EntityAssembler so sim-trained units also resist damage).
            if (stats != null &&
                (stats.ResistanceHack != 0 || stats.ResistancePierce != 0 ||
                 stats.ResistanceCrush != 0 || stats.ResistanceCapture != 0))
            {
                var res = new ResistanceComponent();
                if (stats.ResistanceHack != 0) res.Resistances[DamageType.Hack] = stats.ResistanceHack;
                if (stats.ResistancePierce != 0) res.Resistances[DamageType.Pierce] = stats.ResistancePierce;
                if (stats.ResistanceCrush != 0) res.Resistances[DamageType.Crush] = stats.ResistanceCrush;
                res.CaptureResistance = stats.ResistanceCapture;
                _sim.AddComponent(entity, res);
            }
        }

        var pos = _sim.QueryInterface<PositionComponent>(entity);
        if (pos != null)
            pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));

        Color color = _lastPlayerColor;
        CreateVisualFor(entity, color, 1.5f);
        return entity;
    }

    public EntityId SpawnTree(float x, float z)
    {
        _lastSpawnedTemplate = "gaia/tree/oak";
        var entity = _sim.CreateEntity();
        _sim.AddComponent(entity, new PositionComponent());
        _sim.AddComponent(entity, new ResourceSupply { Type = ResourceType.Wood, Amount = 200, MaxAmount = 200 });
        _sim.AddComponent(entity, new IdentityComponent { Name = "Tree", IsUnit = false });
        _sim.AddComponent(entity, new HealthComponent { Current = 9999, Max = 9999 });

        var pos = _sim.QueryInterface<PositionComponent>(entity);
        if (pos != null)
            pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));

        CreateVisualFor(entity, new Color(0.1f, 0.5f, 0.1f), 2.5f);
        return entity;
    }

    public EntityId SpawnBuilding(float x, float z, string name = "Town Center")
    {
        _lastSpawnedTemplate = "structures/athen/civil_centre";
        var entity = _sim.CreateEntity();
        _sim.AddComponent(entity, new PositionComponent());
        _sim.AddComponent(entity, new ResourceDropsite());
        _sim.AddComponent(entity, new ProductionQueue());
        _sim.AddComponent(entity, new IdentityComponent { Name = name, IsBuilding = true });
        _sim.AddComponent(entity, new HealthComponent { Current = 500, Max = 500 });

        var pos = _sim.QueryInterface<PositionComponent>(entity);
        if (pos != null)
            pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));

        _obstructions.BlockCircle(x, z, 8f);
        CreateVisualFor(entity, new Color(0.6f, 0.5f, 0.4f), 8f, isBuilding: true);
        return entity;
    }

    // --- Commands (ALL player commands funnel into the lockstep queue; in standalone
    // they execute COMMAND_DELAY turns later, exactly as in multiplayer — one code path,
    // no SP/MP divergence. Presentation-only validation stays in Main.) ---

    public void SubmitCommand(NetCommand cmd) => _netTurn.SubmitLocalCommand(cmd);

    /// <summary>
    /// Synchronous foundation spawn + build order for the single-player AI scripts ONLY.
    /// The AI is non-deterministic and disabled in multiplayer (design doc §9), so it does
    /// not participate in lockstep and may touch the sim directly. Returns the foundation
    /// entity so the AI can track it for "don't build a second barracks" checks. Player
    /// commands must NEVER use this — they go through <see cref="CommandBuild"/>.
    /// </summary>
    public EntityId SpawnFoundationDirect(float x, float z, string name, float buildTime)
    {
        string fullTemplate = MapBuildNameToTemplate(name);
        var entity = _sim.CreateEntity();
        _sim.AddComponent(entity, new PositionComponent());
        _sim.AddComponent(entity, new FoundationComponent());
        TemplateStats? stats = null;
        try { stats = Templates?.ExtractStats(fullTemplate); } catch { }
        _sim.AddComponent(entity, new IdentityComponent
        {
            Name = name + " (building)",
            TemplateName = fullTemplate,
            IsBuilding = true,
            IsUnit = false,
            Classes = stats?.GetClassList() ?? new List<string> { name }
        });
        _sim.AddComponent(entity, new HealthComponent { Current = 200, Max = 200 });
        _sim.AddComponent(entity, new OwnershipComponent { PlayerId = 1 });
        _sim.QueryInterface<FoundationComponent>(entity)?.Configure(fullTemplate, buildTime);
        var pos = _sim.QueryInterface<PositionComponent>(entity);
        if (pos != null)
            pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        CreateVisualFor(entity, new Color(0.6f, 0.5f, 0.4f, 0.3f), 6f, isBuilding: true, isGhost: true);
        return entity;
    }

    /// <summary>
    /// Order a builder to repair a specific foundation, synchronously. AI-only counterpart
    /// to <see cref="SpawnFoundationDirect"/>: the AI drives the sim directly outside lockstep.
    /// Player builds must use <see cref="CommandBuild"/> so they route through the turn queue.
    /// </summary>
    public void OrderRepairDirect(EntityId builder, EntityId foundation)
    {
        var ai = _sim.QueryInterface<UnitAIComponent>(builder);
        if (ai != null)
            ai.Repair(foundation);
        else
            _sim.QueryInterface<BuilderComponent>(builder)?.Build(foundation);
        Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "repair", Target = foundation });
    }

    public void MoveEntity(EntityId entity, float x, float z) =>
        SubmitCommand(NetCommand.Move(LocalPlayerId, entity.Value,
            Fixed.FromFloat(x), Fixed.FromFloat(z)));

    public void CommandGather(EntityId unit, EntityId target) =>
        SubmitCommand(NetCommand.Gather(LocalPlayerId, unit.Value, target.Value));

    public void CommandAttack(EntityId attacker, EntityId target) =>
        SubmitCommand(NetCommand.Attack(LocalPlayerId, attacker.Value, target.Value));

    /// <summary>Issue a build order: cost charge + foundation spawn happen in the sim
    /// at the execution turn (SimCommandExecutor). `template` is the FULL template name.</summary>
    public void CommandBuild(EntityId builder, string template, float x, float z) =>
        SubmitCommand(NetCommand.Build(LocalPlayerId, builder.Value, template,
            Fixed.FromFloat(x), Fixed.FromFloat(z)));

    public void CommandSetRallyPoint(EntityId building, EntityId? target) =>
        SubmitCommand(NetCommand.SetRallyPoint(LocalPlayerId, building.Value, target?.Value ?? 0));

    public void CommandResearch(EntityId building, string techName) =>
        SubmitCommand(NetCommand.Research(LocalPlayerId, building.Value, techName));

    public void CommandTrain(EntityId building, string template, int count = 1, bool batch = false) =>
        SubmitCommand(NetCommand.Train(LocalPlayerId, building.Value, template, batch ? 5 : count));

    public void CommandTrain(EntityId building) =>
        CommandTrain(building, "units/spart/support_civilian");

    public void CommandTrainSoldier(EntityId building)
    {
        CommandTrain(building, "units/spart/infantry_spearman_b");
    }

    public PlayerComponent? GetPlayer() =>
        _playerEntity.HasValue ? _sim.QueryInterface<PlayerComponent>(_playerEntity.Value) : null;

    // --- Helpers ---

    private void GatherResource(EntityId unit, EntityId supplyEntity, UnitMotion motion)
    {
        var gatherer = _sim.QueryInterface<ResourceGatherer>(unit);
        var supply = _sim.QueryInterface<ResourceSupply>(supplyEntity);
        var supplyPos = _sim.QueryInterface<PositionComponent>(supplyEntity);
        if (gatherer == null || supply == null || supplyPos == null) return;

        gatherer.TargetSupply = supplyEntity;
        gatherer.CarryType = supply.Type;
        gatherer.State = ResourceGatherer.GatherState.MovingToResource;
        motion.MoveToPoint(new FixedVector2D(supplyPos.Position.X, supplyPos.Position.Z));
    }

    private EntityId? FindNearestDropsite(EntityId from)
    {
        var fromPos = _sim.QueryInterface<PositionComponent>(from);
        if (fromPos == null) return null;
        return FindNearest(from, e => _sim.QueryInterface<ResourceDropsite>(e) != null, fromPos);
    }

    private EntityId? FindNearestResource(EntityId from, ResourceType type)
    {
        var fromPos = _sim.QueryInterface<PositionComponent>(from);
        if (fromPos == null) return null;
        return FindNearest(from, e =>
        {
            var s = _sim.QueryInterface<ResourceSupply>(e);
            return s != null && !s.IsEmpty && s.Type == type;
        }, fromPos);
    }

    public EntityId? FindNearestEntity(EntityId from, Func<EntityId, bool> predicate)
    {
        var fromPos = _sim.QueryInterface<PositionComponent>(from);
        if (fromPos == null) return null;
        return FindNearest(from, predicate, fromPos);
    }

    private EntityId? FindNearest(EntityId from, Func<EntityId, bool> predicate, PositionComponent fromPos)
    {
        float bestDist = float.MaxValue;
        EntityId? best = null;

        foreach (var entity in GetAllEntitiesSnapshot())
        {
            if (entity == from) continue;
            if (!predicate(entity)) continue;
            var pos = _sim.QueryInterface<PositionComponent>(entity);
            if (pos == null) continue;


            float dx = pos.Position.X.ToFloat() - fromPos.Position.X.ToFloat();
            float dz = pos.Position.Z.ToFloat() - fromPos.Position.Z.ToFloat();
            float dist = dx * dx + dz * dz;
            if (dist < bestDist) { bestDist = dist; best = entity; }
        }
        return best;
    }

    private readonly List<EntityId> _entityCache = new();
    private bool _entityCacheDirty = true;

    internal void MarkEntityCacheDirty() => _entityCacheDirty = true;

    private List<EntityId> GetAllEntitiesSnapshot()
    {
        // Single source of truth: the sim's entity list. Previously this iterated _entityNodes
        // (the visual map), which meant a sim entity whose visual failed to build became a ghost
        // — Tick loops never saw it again. Iterating _sim.AllEntities fixes that and keeps the
        // visual map purely a render concern.
        if (_entityCacheDirty)
        {
            _entityCache.Clear();
            if (_sim != null)
                _entityCache.AddRange(_sim.AllEntities);
            _entityCacheDirty = false;
        }
        return _entityCache;
    }

    private static readonly Color[] PlayerColors = new Color[]
    {
        new(0.5f, 0.5f, 0.5f),     // gaia/neutral
        new(0.08f, 0.22f, 0.58f),  // P1: blue
        new(0.72f, 0.06f, 0.06f),  // P2: red
        new(0.12f, 0.55f, 0.14f),  // P3: green
        new(0.85f, 0.68f, 0.10f),  // P4: yellow
        new(0.52f, 0.14f, 0.68f),  // P5: purple
        new(0.10f, 0.62f, 0.70f),  // P6: cyan
        new(0.86f, 0.48f, 0.14f),  // P7: orange
        new(0.20f, 0.20f, 0.22f),  // P8: dark gray
    };

    private static Color GetPlayerColor(int playerId) =>
        playerId >= 0 && playerId < PlayerColors.Length ? PlayerColors[playerId] : new Color(0.6f, 0.5f, 0.4f);

    private string _lastSpawnedTemplate = "";
    private Color _lastPlayerColor = new(0.6f, 0.5f, 0.4f);

    private void CreateVisualFor(EntityId entity, Color color, float size, bool isBuilding = false, bool isGhost = false, string? templateName = null)
    {
        var identity = _sim.QueryInterface<IdentityComponent>(entity);
        string name = identity?.Name ?? "";
        string template = templateName
            ?? (!string.IsNullOrEmpty(identity?.TemplateName) ? identity.TemplateName : null)
            ?? _lastSpawnedTemplate
            ?? name;
        Node3D? visual = null;

        if (!isGhost)
        {
            visual = ModelLibrary.InstantiateForTemplate(template, 0, 0, color);
        }

        if (visual == null)
        {
            if (isGhost)
                visual = EntityMeshFactory.CreateFoundation(color, 0.3f);
            else if (name.Contains("tree") || name.Contains("Tree") || template.Contains("tree"))
                visual = EntityMeshFactory.CreateTree();
            else if (isBuilding)
                visual = EntityMeshFactory.CreateBuilding(color, name);
            else
            {
                var attack = _sim.QueryInterface<AttackComponent>(entity);
                visual = attack != null
                    ? EntityMeshFactory.CreateSoldier(color)
                    : EntityMeshFactory.CreateVillager(color);
            }
        }

        var pos = _sim.QueryInterface<PositionComponent>(entity);
        if (pos != null)
        {
            float vx = pos.Position.X.ToFloat();
            float vz = pos.Position.Z.ToFloat();
            visual.Position = new Vector3(vx, TerrainHeightService.Sample(vx, vz), vz);
        }

        visual.SetMeta("entityId", (int)entity.Value);
        UnitContainer.AddChild(visual);
        _entityNodes[entity] = visual;
        _entityCacheDirty = true;

        var animPlayer = ModelLibrary.FindAnimationPlayer(visual);
        if (animPlayer != null)
        {
            _animPlayers[entity] = animPlayer;
            _animState[entity] = "idle";
            if (pos != null)
                _lastPos[entity] = new Vector3(pos.Position.X.ToFloat(), 0, pos.Position.Z.ToFloat());
        }
    }

    private void SyncVisuals()
    {
        foreach (var kvp in _entityNodes)
        {
            var pos = _sim.QueryInterface<PositionComponent>(kvp.Key);
            if (pos == null) continue;
            var node = kvp.Value;

            var newPos = new Vector3(
                pos.Position.X.ToFloat(),
                TerrainHeightService.Sample(pos.Position.X.ToFloat(), pos.Position.Z.ToFloat()),
                pos.Position.Z.ToFloat());

            node.Position = newPos;

            if (_animPlayers.TryGetValue(kvp.Key, out var player))
                UpdateUnitAnimation(kvp.Key, node, player, newPos);
        }
    }

    private void UpdateUnitAnimation(EntityId entity, Node3D node, AnimationPlayer player, Vector3 newPos)
    {
        Vector3 last = _lastPos.TryGetValue(entity, out var lp) ? lp : newPos;
        Vector3 delta = newPos - last;
        float distSq = delta.LengthSquared();
        _lastPos[entity] = newPos;

        bool moving = distSq > 0.0001f;

        if (moving)
        {
            float yaw = Mathf.Atan2(delta.X, delta.Z);
            node.Rotation = new Vector3(0, yaw, 0);
        }

        string want = moving ? "walk" : "idle";
        if (!_animState.TryGetValue(entity, out var cur) || cur != want)
        {
            string clip = ModelLibrary.ResolveClip(player, want);
            if (clip != "")
            {
                player.Play(clip);
                _animState[entity] = want;
            }
        }
    }

    public List<EntityId> GetEntitiesAtPosition(Vector3 worldPos, float radius = 3f)
    {
        var result = new List<EntityId>();
        foreach (var kvp in _entityNodes)
        {
            var p = kvp.Value.Position;
            float dx = p.X - worldPos.X;
            float dz = p.Z - worldPos.Z;
            float distXZ = Mathf.Sqrt(dx * dx + dz * dz);

            // Pick click-radius by entity kind. The node's Position is the foot/origin point,
            // and gaia (trees, rocks) visually occupy a large footprint above it — so a click on
            // the canopy lands well away from the trunk and needs a wide tolerance. Units stay
            // tight so you can click precisely in a crowd. Buildings stay wide.
            float r = radius; // unit default
            var identity = _sim.QueryInterface<IdentityComponent>(kvp.Key);
            if (identity != null)
            {
                if (identity.IsBuilding) r = 15f;
                else if (!identity.IsUnit) r = 8f; // gaia: trees, rocks, resources
                else r = 5f; // units: generous click tolerance (node.Position is the foot; clicks
                             // land on the body/canopy, so a tight 3m radius misses often)
            }

            if (distXZ < r)
                result.Add(kvp.Key);
        }

        result.Sort((a, b) =>
        {
            var pa = _entityNodes[a].Position;
            var pb = _entityNodes[b].Position;
            float da = (pa.X - worldPos.X) * (pa.X - worldPos.X) + (pa.Z - worldPos.Z) * (pa.Z - worldPos.Z);
            float db = (pb.X - worldPos.X) * (pb.X - worldPos.X) + (pb.Z - worldPos.Z) * (pb.Z - worldPos.Z);
            return da.CompareTo(db);
        });
        return result;
    }

    public List<EntityId> GetEntitiesInBounds(Vector3 center, Vector3 extents)
    {
        var result = new List<EntityId>();
        foreach (var kvp in _entityNodes)
        {
            var p = kvp.Value.Position;
            if (System.Math.Abs(p.X - center.X) < extents.X &&
                System.Math.Abs(p.Z - center.Z) < extents.Z)
                result.Add(kvp.Key);
        }
        return result;
    }
}
