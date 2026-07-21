using Godot;
using System;
using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Tutorial;

namespace ZeroAD.Godot;

public sealed partial class SimBridge : Node
{
    private ComponentManager _sim = null!;
    private TurnManager _turnManager = null!;
    private double _simAccumulator;
    private const double SimTickRate = 0.1;

    private readonly Dictionary<EntityId, Node3D> _entityNodes = new();
    private readonly Dictionary<EntityId, AnimationPlayer> _animPlayers = new();
    private readonly Dictionary<EntityId, string> _animState = new();
    private readonly Dictionary<EntityId, Vector3> _lastPos = new();
    private EntityId? _playerEntity;
    private ObstructionManager _obstructions = null!;
    private readonly Dictionary<uint, EntityId> _scenarioUidMap = new();
    private readonly List<Node3D> _decorativeNodes = new();

    public SimEventBus Events { get; } = new();
    public TutorialEngine? Tutorial { get; private set; }
    public bool IsTutorialMode { get; private set; }

    public IReadOnlyDictionary<EntityId, Node3D> EntityNodes => _entityNodes;
    public Node3D UnitContainer { get; set; } = null!;
    public TemplateLoader? Templates { get; private set; }

    public ComponentManager Sim => _sim;
    public TurnManager Turns => _turnManager;
    public ObstructionManager Obstructions => _obstructions;

    public void InitWorld()
    {
        InitWorld(null);
    }

    public void InitWorld(string? templatesPath)
    {
        uint seed = 42;
        var registry = new ComponentRegistry();
        registry.AutoRegister(typeof(PositionComponent).Assembly);
        _sim = new ComponentManager(seed, registry);
        _turnManager = new TurnManager(_sim, commandDelay: 0);
        SimSystem.Init(_sim);

        int gridSize = 64;
        float cellSize = 4.0f;
        _obstructions = new ObstructionManager(gridSize, cellSize);
        UnitMotion.SetObstructionManager(_obstructions);

        if (templatesPath != null && System.IO.Directory.Exists(templatesPath))
        {
            Templates = new TemplateLoader(templatesPath);
            GD.Print($"Loaded templates from: {templatesPath}");
            int count = 0;
            foreach (var kvp in Templates.Cache) count++;
            if (count == 0) Templates.LoadAllTemplates();
            GD.Print($"Template cache: {Templates.Cache.Count} entries");
        }

        _playerEntity = _sim.CreateEntity();
        _sim.AddComponent(_playerEntity.Value, new PlayerComponent());
        _sim.AddComponent(_playerEntity.Value, new TechnologyManager { });
        _sim.AddComponent(_playerEntity.Value, new OwnershipComponent { PlayerId = 1 });
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

        if (def.Player > 0)
            _sim.AddComponent(entity, new OwnershipComponent { PlayerId = def.Player });

        var pos = _sim.QueryInterface<PositionComponent>(entity);
        if (pos != null)
            pos.Position = new FixedVector3D(Fixed.FromFloat(def.X), Fixed.Zero, Fixed.FromFloat(def.Z));

        _obstructions.BlockCircle(def.X, def.Z, 8f);
        CreateVisualFor(entity, GetPlayerColor(def.Player), 8f, isBuilding: true);
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

    public override void _Process(double delta)
    {
        if (_sim == null) return;

        _simAccumulator += delta;
        while (_simAccumulator >= SimTickRate)
        {
            _simAccumulator -= SimTickRate;
            TickSimulation((float)SimTickRate);
            _turnManager.AdvanceTurn();
        }
        SyncVisuals();
    }

    private void TickSimulation(float dt)
    {
        RemoveDeadEntities();
        TickUnitMotions(dt);
        TickGatherers(dt);
        TickAttackers(dt);
        TickBuilders(dt);
        TickProductionQueues(dt);
        TickFoundations(dt);
        TickResearch(dt);
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
                    _entityCacheDirty = true;
                }
                _sim.DestroyEntity(entity);
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
            string template = foundation.ResultTemplate;
            float x = pos?.Position.X.ToFloat() ?? 0;
            float z = pos?.Position.Z.ToFloat() ?? 0;
            var owner = _sim.QueryInterface<OwnershipComponent>(entity);

            if (_entityNodes.TryGetValue(entity, out var oldNode))
            {
                oldNode.QueueFree();
                _entityNodes.Remove(entity);
            }
            _sim.DestroyEntity(entity);

            string fullTemplate = MapBuildNameToTemplate(template);
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
                TemplateName = template
            });

            if (fullTemplate.Contains("house", StringComparison.OrdinalIgnoreCase))
            {
                var houseOwner = GetPlayer();
                if (houseOwner != null) houseOwner.PopulationLimit += 10;
            }

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
            var completed = researcher.Tick(dt, techMgr);
            if (completed != null)
            {
                var player = GetPlayer();
                if (player != null && techMgr.Available.TryGetValue(completed, out var tech))
                {
                    if (tech.Effects.TryGetValue("pop_limit", out float delta))
                        player.PopulationLimit += (int)delta;
                }
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
        foreach (var entity in GetAllEntitiesSnapshot())
        {
            var queue = _sim.QueryInterface<ProductionQueue>(entity);
            if (queue == null) continue;

            var completed = queue.Tick(dt);
            if (completed != null)
            {
                var pos = _sim.QueryInterface<PositionComponent>(entity);
                if (pos != null)
                {
                    float x = pos.Position.X.ToFloat() + 10;
                    float z = pos.Position.Z.ToFloat() + 10;
                    var owner = _sim.QueryInterface<OwnershipComponent>(entity);
                    var spawned = SpawnFromTemplate(completed.TemplateName, x, z);
                    if (owner != null)
                        _sim.AddComponent(spawned, new OwnershipComponent { PlayerId = owner.PlayerId });

                    var rally = _sim.QueryInterface<RallyPointComponent>(entity);
                    if (rally != null && !rally.Position.IsZero)
                    {
                        var motion = _sim.QueryInterface<UnitMotion>(spawned);
                        motion?.MoveToPoint(new FixedVector2D(rally.Position.X, rally.Position.Y));
                    }
                }

                var player = GetPlayer();
                if (player != null) player.Population++;

                Events.RaiseTrainingFinished(new TrainingFinishedEvent
                {
                    TrainerEntity = entity,
                    UnitTemplate = completed.TemplateName
                });
            }
        }
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
            var atk = new AttackComponent
            {
                Damage = stats?.AttackDamage ?? 20,
                Range = stats?.AttackRange ?? 3.0f,
                Rate = stats?.AttackRate ?? 1.0f
            };
            _sim.AddComponent(entity, atk);
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

    public EntityId SpawnFoundation(float x, float z, string name, float buildTime)
    {
        var entity = _sim.CreateEntity();
        _sim.AddComponent(entity, new PositionComponent());
        _sim.AddComponent(entity, new FoundationComponent());
        string fullTemplate = MapBuildNameToTemplate(name);
        TemplateStats? stats = null;
        try { stats = Templates?.ExtractStats(fullTemplate); } catch { }
        var identity = new IdentityComponent
        {
            Name = name + " (building)",
            TemplateName = fullTemplate,
            IsBuilding = true,
            IsUnit = false,
            Classes = stats?.GetClassList() ?? new List<string> { name }
        };
        _sim.AddComponent(entity, identity);
        _sim.AddComponent(entity, new HealthComponent { Current = 200, Max = 200 });
        _sim.AddComponent(entity, new OwnershipComponent { PlayerId = 1 });

        var foundation = _sim.QueryInterface<FoundationComponent>(entity);
        foundation?.Configure(name, buildTime);

        var pos = _sim.QueryInterface<PositionComponent>(entity);
        if (pos != null)
            pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));

        CreateVisualFor(entity, new Color(0.6f, 0.5f, 0.4f, 0.3f), 6f, isBuilding: true, isGhost: true);
        return entity;
    }

    // --- Commands ---

    public void MoveEntity(EntityId entity, float x, float z)
    {
        var motion = _sim.QueryInterface<UnitMotion>(entity);
        motion?.MoveToPoint(new FixedVector2D(Fixed.FromFloat(x), Fixed.FromFloat(z)));
    }

    public void CommandGather(EntityId unit, EntityId target)
    {
        var motion = _sim.QueryInterface<UnitMotion>(unit);
        if (motion != null) GatherResource(unit, target, motion);
        Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "gather", Target = target });
    }

    public void CommandAttack(EntityId attacker, EntityId target)
    {
        var attack = _sim.QueryInterface<AttackComponent>(attacker);
        attack?.AttackTarget(target);
        Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "attack", Target = target });
    }

    public void CommandBuild(EntityId builder, EntityId foundation)
    {
        var b = _sim.QueryInterface<BuilderComponent>(builder);
        b?.Build(foundation);
        Events.RaisePlayerCommand(new PlayerCommandEvent { Type = "repair", Target = foundation });
    }

    public void CommandSetRallyPoint(EntityId building, EntityId? target, string command, string specific)
    {
        var rally = _sim.QueryInterface<RallyPointComponent>(building);
        if (target.HasValue)
        {
            var pos = _sim.QueryInterface<PositionComponent>(target.Value);
            if (pos != null && rally != null)
                rally.Set(new FixedVector2D(pos.Position.X, pos.Position.Z));
        }
        Events.RaisePlayerCommand(new PlayerCommandEvent
        {
            Type = "set-rallypoint",
            Target = target,
            Data = new Dictionary<string, object>
            {
                ["command"] = command,
                ["specific"] = specific
            }
        });
    }

    public void CommandResearch(EntityId building, string techName)
    {
        var researcher = _sim.QueryInterface<ResearcherComponent>(building);
        var techMgr = _playerEntity.HasValue ? _sim.QueryInterface<TechnologyManager>(_playerEntity.Value) : null;
        var player = GetPlayer();
        if (researcher == null || techMgr == null || player == null) return;
        if (!researcher.StartResearch(techName, techMgr, player)) return;
        Events.RaiseResearchQueued(new ResearchQueuedEvent
        {
            ResearcherEntity = building,
            TechnologyTemplate = techName
        });
    }

    public void CommandTrain(EntityId building, string template, int count = 1, bool batch = false)
    {
        int actualCount = batch ? 5 : count;
        var queue = _sim.QueryInterface<ProductionQueue>(building);
        var player = GetPlayer();
        if (queue == null || player == null) return;

        int wood = 50, food = 50, metal = 0;
        if (template.Contains("spearman")) { food = 80; metal = 20; }
        if (template.Contains("javelineer")) { food = 70; wood = 30; }
        if (template.Contains("support_civilian")) { food = 50; wood = 0; }
        if (template.Contains("siege_ram")) { wood = 200; food = 0; metal = 50; }

        int totalFood = food * actualCount;
        int totalWood = wood * actualCount;
        if (!player.CanAfford(totalWood, totalFood) && player.Food < totalFood) return;
        player.Spend(totalWood, totalFood);
        player.Metal -= metal * actualCount;
        queue.Enqueue(template, totalWood, totalFood, 5.0f, actualCount);

        Events.RaiseTrainingQueued(new TrainingQueuedEvent
        {
            TrainerEntity = building,
            UnitTemplate = template,
            Count = actualCount
        });
    }

    public void CommandTrain(EntityId building)
    {
        CommandTrain(building, "units/spart/support_civilian");
    }

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
        if (_entityCacheDirty)
        {
            _entityCache.Clear();
            _entityCache.AddRange(_entityNodes.Keys);
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

            float r = radius;
            var identity = _sim.QueryInterface<IdentityComponent>(kvp.Key);
            if (identity != null && identity.IsBuilding)
                r = 15f;

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
