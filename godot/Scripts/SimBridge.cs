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
    private ReplayRecorder? _recorder;       // 自动录像：非 null 时每回合录制命令批
    private ReplayDriver? _replayDriver;     // 回放播放：非 null 时每帧注入预录制命令
    private ProjectilePool? _projectiles;    // 飞行投射物池（ranged 攻击的箭矢）
    private ImpactEffectPool? _impacts;      // 命中特效池（血雾/扬尘）
    private double _simAccumulator;
    private const double SimTickRate = 0.1;

    private readonly Dictionary<EntityId, Node3D> _entityNodes = new();
    private readonly Dictionary<EntityId, SkeletalAnim.ManualAnimator> _animators = new();
    private readonly Dictionary<EntityId, string> _animState = new();
    private readonly Dictionary<EntityId, Vector3> _lastPos = new();
    // 表现层位置插值器:消除 10Hz sim tick 的单位瞬移(见 SyncVisuals / _Process)。
    private readonly VisualInterpolator _interpolator = new();
    private EntityId? _playerEntity;
    private ObstructionManager _obstructions = null!;
    private RangeManager _range = null!;
    private TerritoryManager _territory = null!;
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

    /// <summary>对局骨架(冷加载重建契约,存档头 v6 内嵌):本局地图 rel 路径——SetupTerrain
    /// 选定后写入;null = 生成地形,无法冷加载。</summary>
    public string? MapPath { get; set; }

    /// <summary>对局骨架(冷加载重建契约):本局冻结槽位表——InitWorld slot-table overload 写入。
    /// 存档头 v6 内嵌,冷加载 InitWorld 用它重建同构世界。</summary>
    public IReadOnlyList<PlayerSlotSetup> Slots { get; private set; } = System.Array.Empty<PlayerSlotSetup>();

    public IReadOnlyDictionary<EntityId, Node3D> EntityNodes => _entityNodes;
    public Node3D UnitContainer { get; set; } = null!;

    /// <summary>阴影代理容器(正规空间,Main 创建,勿挂 _worldRoot)。见 ShadowProxyManager:
    /// 负 scale 镜像根下的视觉不投影,每个单位视觉在此有一份 ShadowsOnly 代理。</summary>
    public Node3D? ShadowRoot { get; set; }
    private readonly Dictionary<Node3D, Node3D> _shadowProxies = new();
    public TemplateLoader? Templates { get; private set; }

    public ComponentManager Sim => _sim;

    /// <summary>The lockstep turn manager. In single-player it is Standalone (local
    /// batches aggregate synchronously, so the barrier never blocks); in multiplayer
    /// the MultiplayerController feeds it batches/bundles via the transport.</summary>
    public NetTurnManager NetTurn => _netTurn;
    public uint LocalPlayerId { get; private set; } = 1;

    /// <summary>暂停标志(表现层门控)。置 true 后 _Process 直接返回:既不累加 delta
    /// 也跳过 SyncVisuals/插值——sim 状态冻结,且恢复时无补帧爆发(SP 向;MP 暂停不在内,
    /// 叠层可开但锁步屏障仍驱动 AdvanceTurn)。由 PauseMenu.Open/Close 翻转。</summary>
    public bool Paused;

    /// <summary>游戏速度倍率(原版 Engine.SetSimRate,本地表现层字段)。1=正常。
    /// 在 _Process 累加 delta 时相乘 → 每 tick 数学不变、仅节拍快慢。MP 下会失步
    /// (各端渲染自定步速)——与 Paused 同性质,MP 速度协商列 backlog。</summary>
    public double SpeedMultiplier = 1.0;

    /// <summary>Read-only query facade for HUD/Minimap/AI. Consolidates the scattered
    /// QueryInterface + entity-list iteration that previously lived inline in the GUI.</summary>
    public GuiInterface Gui { get; private set; } = null!;
    public ObstructionManager Obstructions => _obstructions;
    public TerrainComponent Terrain => _terrain;
    public PathfinderComponent Pathfinder => _pathfinder;
    public FogWorldRenderer FogWorld => _fogWorld;
    private FogWorldRenderer _fogWorld = null!;
    public TerritoryWorldRenderer TerritoryWorld => _territoryWorld;
    private TerritoryWorldRenderer _territoryWorld = null!;
    public RangeManager Range => _range;
    public TerritoryManager Territory => _territory;

    public void InitWorld()
    {
        InitWorld(null);
    }

    /// <param name="seed">RNG seed — must match across peers (host assigns it in MP).</param>
    /// <param name="localPlayerId">This peer's game player id (host=1, clients assigned by host).</param>
    /// <param name="role">Standalone for SP; Host/Client for MP. Governs turn-barrier behaviour.</param>
    /// <param name="playerCount">Number of player slots to create. Host + each client own one.</param>
    /// <remarks>Back-compat shim — maps the count + civ onto N Human slots and delegates to
    /// the slot-table overload. SP/tutorial/sandbox still call this; MP passes the host's
    /// frozen slot table directly.</remarks>
    public void InitWorld(string? templatesPath, uint seed = 42, uint localPlayerId = 1,
        NetRole role = NetRole.Standalone, int playerCount = 1, string civ = "athen")
        => InitWorld(templatesPath, seed, localPlayerId, role,
            Enumerable.Range(1, playerCount)
                .Select(i => new PlayerSlotSetup { PlayerId = i, Kind = PlayerSlotKind.Human, Civ = civ })
                .ToList());

    /// <summary>
    /// Slot-driven InitWorld (Task #10): the host→client setup contract. Every peer feeds in
    /// the same frozen slot table + the same seed, so they build identical worlds. Human slots
    /// enter <c>NetTurnManager._expectedPlayers</c> (they ship network batches); AI slots get an
    /// AIComponent attached later in SetupGameWorld and ride the local <c>_aiBundles</c> channel;
    /// Closed slots are not instantiated at all. This is the only overload that should grow new
    /// world-construction logic.
    /// </summary>
    public void InitWorld(string? templatesPath, uint seed, uint localPlayerId, NetRole role,
        IReadOnlyList<PlayerSlotSetup> slots)
    {
        var registry = new ComponentRegistry();
        registry.AutoRegister(typeof(PositionComponent).Assembly);

        // Wire templates + events into the sim so SpawnEntity / EnqueueTraining can run headless.
        TemplateLoader? templates = null;
        TechCatalog? techCatalog = null;
        AuraCatalog? auraCatalog = null;
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

            // 光环 JSON 同根(simulation/data/auras)。MVP 仅收 range/global/player 三型。
            var auraDir = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(templatesPath, "..", "data", "auras"));
            auraCatalog = AuraLoader.LoadAll(auraDir);
            GD.Print($"Auras: {auraCatalog.Auras.Count} entries (range/global/player only)");
        }

        _sim = new ComponentManager(seed, registry, templates);
        if (auraCatalog != null) _sim.Auras = auraCatalog;
        SimSystem.Init(_sim);
        Templates = templates;
        LocalPlayerId = localPlayerId;
        Slots = slots;   // 存档头 v6 契约:冷加载用同一份槽位表重建世界

        // Subscribe so the sim can ask us (the presentation layer) to build visuals whenever it
        // spawns an entity. This is the only Godot→sim coupling direction for spawn.
        _sim.Events.EntityCreated += OnEntityCreated;
        // Kernel-side destruction (mirage self-destruct/cleanup, RemoveDeadEntities) → drop the
        // Godot node + cached state. Without this, kernel-destroyed entities leak nodes.
        _sim.EntityDestroyed += OnSimEntityDestroyed;
        // 战斗观感：攻击发射 → 飞行投射物（ranged）；命中 → 血雾/扬尘（AttackLandedEvent 原零订阅，现接入）。
        _sim.Events.AttackLaunched += OnAttackLaunched;
        _sim.Events.AttackLanded += OnAttackLanded;

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
        _territory = new TerritoryManager(_sim, (int)worldSize);
        SimSystem.SetTerritoryManager(_territory);
        SimSystem.SetPathfinder(_pathfinder);
        SimSystem.SetWaterManager(_sim.Water);
        Gui = new GuiInterface(_sim);
        _fogWorld = new FogWorldRenderer(this);
        _territoryWorld = new TerritoryWorldRenderer(this);

        // A system entity to host the TerrainComponent so components can QueryInterface it.
        _terrainEntity = _sim.CreateEntity();
        _sim.AddComponent(_terrainEntity, _terrain);
        // LOS state rides full-state serialization + the lockstep hash via this component.
        var losComp = new LosManagerComponent();
        losComp.Attach(_range);
        _sim.AddComponent(_terrainEntity, losComp);

        foreach (var slot in slots)
        {
            if (slot.Kind == PlayerSlotKind.Closed) continue;
            int pid = slot.PlayerId;
            var playerEntity = _sim.CreateEntity();
            _sim.AddComponent(playerEntity, new PlayerComponent { Civ = slot.Civ });
            _sim.AddComponent(playerEntity, new DiplomacyComponent());
            var techMgr = new TechnologyManager();
            _sim.AddComponent(playerEntity, techMgr);
            _sim.AddComponent(playerEntity, new OwnershipComponent { PlayerId = pid });
            _sim.AddComponent(playerEntity, new EntityLimitsComponent());
            var stats = new StatisticsTrackerComponent();
            _sim.AddComponent(playerEntity, stats);
            stats.Attach(_sim);   // 订阅 SimEventBus（同 TechnologyManager.Configure 模式）
            _sim.RegisterPlayer(pid, playerEntity);
            if (techCatalog != null)
            {
                techMgr.Configure(techCatalog, slot.Civ);
                // 开局即满足的 autoResearch 科技(phase_village、civ 加成)免费落地
                techMgr.UpdateAutoResearch(_sim);
            }
            if (pid == (int)localPlayerId)
                _playerEntity = playerEntity;
        }

        // Seed diplomacy from the slot table's team assignments. Closed slots are excluded; a
        // team of -1 (the default) means FFA — players on -1 each form their own singleton team
        // and are mutual enemies (SeedDiplomacyFromTeams only allies on team >= 0 equality).
        // Slot-table teams derive from the lobby (Task #10) or default to FFA in SP/sandbox.
        // Scenarios carrying Team data re-seed after loading players.
        var teamMap = slots
            .Where(s => s.Kind != PlayerSlotKind.Closed)
            .ToDictionary(s => s.PlayerId, s => s.Team);
        _sim.Players.SeedDiplomacyFromTeams(teamMap);

        // CRITICAL deadlock guard: only HUMAN slots submit network batches, so only they enter
        // _expectedPlayers. AI commands ride the local _aiBundles channel and never reach the
        // network — including an AI player id here would block the host forever waiting for a
        // batch that never arrives. (NetTurnManager.HostIngestBatch waits for all expected.)
        var expectedPlayers = slots
            .Where(s => s.Kind == PlayerSlotKind.Human)
            .Select(s => (uint)s.PlayerId)
            .ToHashSet();
        _netTurn = new NetTurnManager(_sim, commandDelay: 2, localPlayerId, role, expectedPlayers);
    }

    /// <summary>开始自动录像。InitWorld 后、SimulationRunning=true 前调用。
    /// 自动录制所有对局（Standalone/Host）。Client 不录（命令不完整）。</summary>
    public void StartRecording()
    {
        if (_netTurn.Role == NetRole.Client) return;  // Client 视角命令不完整，不录
        try
        {
            string dir = ProjectSettings.GlobalizePath("user://replays/");
            System.IO.Directory.CreateDirectory(dir);
            string stamp = System.DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
            string map = System.IO.Path.GetFileNameWithoutExtension(MapPath ?? "match");
            string path = System.IO.Path.Combine(dir, $"{stamp}_{map}.zreplay");
            string engineVersion = "0.29.0";  // 与存档一致：版本号在 header 记录，便于将来兼容
            var meta = new ReplayMeta(
                MapPath ?? string.Empty,
                IsTutorialMode ? "tutorial" : (_netTurn.Role != NetRole.Standalone ? "multiplayer" : "singleplayer"),
                IsTutorialMode, LocalPlayerId, _netTurn.Role, _netTurn.CommandDelay,
                Slots, System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                $"Match {stamp}", engineVersion);
            var fs = new System.IO.FileStream(path, System.IO.FileMode.Create);
            var writer = ReplayFile.BeginRecording(fs, meta, _sim);
            _recorder = new ReplayRecorder(writer, _netTurn, path);
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[Replay] start recording failed: {ex.Message}");
        }
    }

    /// <summary>安装回放驱动器（Main.AutoReplay 在冷加载初始状态后调用）。</summary>
    public void StartReplay(ReplayReader reader)
        => _replayDriver = new ReplayDriver(this, reader);

    /// <summary>录制器/驱动器是否活跃（用于 Main 判断当前是否回放模式）。</summary>
    public bool IsReplayMode => _replayDriver != null;

    /// <summary>回放总回合数（ReplayControls 显示用）。非回放模式返回 0。</summary>
    public uint ReplayTotalTurns => _replayDriver?.TotalTurns ?? 0;

    /// <summary>结束录制（胜利/失败/退出时）。幂等。</summary>
    public void FinalizeRecording(string description = "")
    {
        _recorder?.Finalize(description);
        _recorder = null;
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
        // Re-seed diplomacy from the scenario's Team assignments (covers the enemy player
        // created in ApplyScenarioPlayers): same-team → mutual ally, else enemy. No-op for
        // a 1v1 tutorial with no Team data.
        var teams = new Dictionary<int, int>();
        foreach (var pd in scenario.Players)
            teams[pd.PlayerId] = pd.Team;
        _sim.Players.SeedDiplomacyFromTeams(teams);
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
                _sim.AddComponent(enemy, new DiplomacyComponent());
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
        _sim.AddComponent(entity, new ProductionQueue
        {
            TrainableTokens = stats?.TrainableEntities ?? "",
            NativeCiv = stats?.Civ ?? "",
        });
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
            Territory = stats?.BuildRestrictionsTerritory ?? "",
        });
        obstruction.EnsureRegistered();

        // Fog-of-war registration: Vision/Fogging/Visibility from the template + entry
        // into the RangeManager index (without this the entity is permanently HIDDEN).
        EntityAssembler.RegisterForLos(_sim, entity, def.Template, stats);

        CreateVisualFor(entity, GetPlayerColor(def.Player), Math.Max(fpSize * 0.5f, 4f), isBuilding: true);
        // Apply the scenario's authored yaw (Atlas stores <Orientation y="rad"/> per entity).
        // SyncVisuals only updates Position, never Rotation, so this persists for the life of
        // the entity. Matches C++ CmpPosition::SetYRotation at scenario load.
        if (_entityNodes.TryGetValue(entity, out var bldgNode) && def.OrientationY != 0f)
            bldgNode.Rotation = new Vector3(0, def.OrientationY, 0);
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

        // Re-register now that ownership is set: activates fogging and indexes the entity
        // under its owner (the in-SpawnUnit call ran ownerless). Idempotent.
        EntityAssembler.RegisterForLos(_sim, entity, def.Template, stats);

        // Authored yaw — overruled the first time the unit walks (UpdateUnitAnimation
        // yaws to travel direction), but until then the unit should face as placed.
        if (_entityNodes.TryGetValue(entity, out var unitNode) && def.OrientationY != 0f)
            unitNode.Rotation = new Vector3(0, def.OrientationY, 0);

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
            _sim.AddComponent(entity, supply);
            // SetTypeString AFTER AddComponent — OnInit resets SpecificType/GenericType
            // to defaults ("tree"/"wood"), so configuring before attach is overwritten.
            if (!string.IsNullOrEmpty(stats.ResourceTypeString))
                supply.SetTypeString(stats.ResourceTypeString);
            else if (def.Template.Contains("fruit", StringComparison.OrdinalIgnoreCase) ||
                     def.Template.Contains("berry", StringComparison.OrdinalIgnoreCase))
                supply.SetTypeString("food.fruit");
            else if (def.Template.Contains("tree", StringComparison.OrdinalIgnoreCase))
                supply.SetTypeString("wood.tree");
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
        // 原版数据:树木/岩石无 Health(不可攻击),fauna 有(可猎)。9999 硬编码让树
        // 也有了血条 → 悬停树出剑/可攻击树,与原版相悖。只给模板真声明 <Health> 的装。
        if (stats != null && stats.HasHealth)
            _sim.AddComponent(entity, new HealthComponent { Current = stats.MaxHealth, Max = stats.MaxHealth });

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
        EntityAssembler.RegisterForLos(_sim, entity, def.Template, stats);
        if (_entityNodes.TryGetValue(entity, out var gaiaNode) && def.OrientationY != 0f)
            gaiaNode.Rotation = new Vector3(0, def.OrientationY, 0);
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

    /// <summary>世界构建完成后才为 true(BeginGameplayScenario/ColdLoad 末尾置位)。
    /// 分阶段加载在 InitWorld 与实体生成之间让帧,若此间推进回合,TickVictory 会在
    /// 空世界里判所有玩家 0 实体→进场即 Defeat。此闸门保证回合推进与世界完整同生。</summary>
    public bool SimulationRunning { get; set; }

    public override void _ExitTree()
    {
        // 统一收尾录制：场景切回主菜单时 SimBridge 被销毁，无论从哪条路径退出
        // （胜利/暂停离开/场景切换）都会触发，保证录像文件完整落盘。幂等。
        FinalizeRecording();
        // 退订战斗观感事件（防 sim 被回收后仍回调已销毁的池）。
        if (_sim != null)
        {
            _sim.Events.AttackLaunched -= OnAttackLaunched;
            _sim.Events.AttackLanded -= OnAttackLanded;
        }
    }

    // ── 战斗观感 handler ──

    private void OnAttackLaunched(ZeroAD.Sim.Events.AttackLaunchedEvent e)
    {
        // 仅 ranged 攻击生成飞行投射物（melee 只在命中时播特效）。
        if (!e.IsRanged || _projectiles == null) return;
        if (!_entityNodes.TryGetValue(e.Attacker, out var from) || !_entityNodes.TryGetValue(e.Target, out var to))
            return;
        // 抬高发射点（从单位腰部而非脚底发射），目标点稍高于地面。
        _projectiles.Spawn(from.Position + Vector3.Up * 1.2f, to.Position + Vector3.Up * 0.8f);
    }

    private void OnAttackLanded(ZeroAD.Sim.Events.AttackLandedEvent e)
    {
        if (_impacts == null) return;
        if (!_entityNodes.TryGetValue(e.Target, out var target)) return;
        // 物理伤害 >0 才有受击特效（捕获命中无视觉反馈，对齐原版 MT_Attacked 接收序）。
        if (e.DamageDealt <= 0) return;
        // 判断是否击杀：查目标的 HealthComponent。
        bool isKill = false;
        var health = _sim?.QueryInterface<ZeroAD.Sim.Components.HealthComponent>(e.Target);
        if (health != null) isKill = health.IsDead;
        _impacts.Spawn(target.Position + Vector3.Up * 0.8f, isKill);
    }

    public override void _Ready()
    {
        // 阴影代理生命周期跟随单位视觉进/出树(EnsureVisual/装饰物/RebuildAllVisuals 全路径
        // 都经 UnitContainer.AddChild,信号一处拦截覆盖全部生成点)。
        if (UnitContainer != null)
        {
            UnitContainer.ChildEnteredTree += OnUnitVisualEntered;
            UnitContainer.ChildExitingTree += OnUnitVisualExiting;
        }
        // 战斗观感池：飞行投射物 + 命中特效（纯视觉，不进 sim 序列化/OOS 哈希）。
        _projectiles = new ProjectilePool();
        _impacts = new ImpactEffectPool();
        AddChild(_projectiles);
        AddChild(_impacts);
    }

    private void OnUnitVisualEntered(Node node)
    {
        if (ShadowRoot == null || node is not Node3D n3) return;
        var proxy = ShadowProxyManager.CreateProxyRoot(n3);
        ShadowRoot.AddChild(proxy);
        _shadowProxies[n3] = proxy;
        ShadowProxyManager.SyncFrom(proxy, n3);
    }

    private void OnUnitVisualExiting(Node node)
    {
        if (node is not Node3D n3) return;
        if (_shadowProxies.Remove(n3, out var proxy) && GodotObject.IsInstanceValid(proxy))
            proxy.QueueFree();
    }

    public override void _Process(double delta)
    {
        if (_sim == null) return;
        _replayDriver?.Pump();  // 回放模式：注入当前回合预录制命令（播完则停止）。正常游戏为 null，零开销。
        if (!SimulationRunning) return;  // 加载中:世界未完整,冻结回合推进(渲染照常)
        if (Paused) return;   // 状态冻结;早于累加 delta 以避免恢复时补帧爆发

        _simAccumulator += delta * SpeedMultiplier;
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
            // AI 大脑内核驻留(Phase 2):遍历 AllEntities 推进 AIComponent.Tick。AI 是对世界的
            // "反应"而非世界推进的一部分,故独立于 TickSimulation;Tick 内经 SubmitAiCommand 入
            // currentTurn+commandDelay 本地通道,与人手同路径同延迟,各端确定性同生成。
            TickAI();
            _netTurn.AdvanceTurn();
        }
        SyncVisuals();
        // 渲染插值:用 tick 余数作 alpha,在两次 tick 之间平滑单位位置(消除 10Hz 瞬移)。
        _interpolator.SetAlpha((float)(_simAccumulator / SimTickRate));
        _interpolator.ApplyRenderPositions();
        // 阴影代理跟拍(插值之后,影子与平滑后的视觉同帧;迷雾隐藏单位经 SyncFrom 关 Visible 不漏影)。
        foreach (var kvp in _shadowProxies)
            if (kvp.Key.IsInsideTree())
                ShadowProxyManager.SyncFrom(kvp.Value, kvp.Key);
    }

    /// <summary>推进所有 AI 大脑(Phase 2 内核驻留)。遍历 AllEntities,对挂 AIComponent 的玩家
    /// 实体调 Tick:回合计流 + 从 OwnershipComponent 派生 playerId + 5 manager 决策 →
    /// SubmitAiCommand(本地 AI 通道,永不进网络 outbox)。各端确定性同跑同生成,故 AI 无需网络槽。</summary>
    private void TickAI()
    {
        foreach (var entity in _sim.AllEntities)
        {
            var ai = _sim.QueryInterface<AIComponent>(entity);
            if (ai != null) ai.Tick();
        }
    }

    /// <summary>推进所有光环(对齐 TickResearch)。遍历 AllEntities,对挂 AuraComponent 的
    /// 实体调 Tick:range 型 ExecuteQuery+diff,global/player 型玩家实体+reqTech 门控。
    /// 派生态每 tick 重建,无累积。</summary>
    private void TickAuras(float dt)
    {
        var catalog = _sim.Auras;
        if (catalog == null || catalog.Auras.Count == 0) return;
        foreach (var entity in _sim.AllEntities)
        {
            var aura = _sim.QueryInterface<AuraComponent>(entity);
            if (aura != null) aura.Tick(_sim, _range, catalog);
        }
    }

    /// <summary>领土衰减闭环(原版 TerritoryDecay.js 事件驱动 → 本移植每回合刷新,回合边界
    /// 取值一致):1) 每个 TerritoryDecayComponent 重算 decaying + 邻主表 + blink 覆盖;
    /// 2) 每个 CapturableComponent TimerTick(decay 抽干分给邻主/gaia + regen 恢复)。
    /// 原地主翻面在 Capturable 内走 NotifyOwnerChanged,与各端同序 → 确定性。</summary>
    private void TickTerritoryDecay(float dt)
    {
        var fixedDt = Fixed.FromFloat(dt);
        foreach (var entity in _sim.AllEntities)
        {
            var decay = _sim.QueryInterface<TerritoryDecayComponent>(entity);
            if (decay != null) decay.Refresh(_sim, _territory);
            var capturable = _sim.QueryInterface<CapturableComponent>(entity);
            if (capturable != null) capturable.TimerTick(_sim, fixedDt);
        }
    }

    private void TickGarrisonHolders(float dt)
    {
        foreach (var entity in _sim.AllEntities)
            _sim.QueryInterface<GarrisonHolderComponent>(entity)?.Tick(dt, _sim);
    }

    private void TickTurrets(float dt)
    {
        foreach (var entity in _sim.AllEntities)
            _sim.QueryInterface<TurretableComponent>(entity)?.UpdatePosition(_sim);
    }

    private void TickSimulation(float dt)
    {
        RemoveDeadEntities();
        TickUnitMotions(dt);
        // Unit pushing (ports CCmpUnitMotionManager::Move/Push): after every unit has stepped,
        // push overlapping pairs apart so rallied/converging units spread into a visible cluster
        // instead of stacking on one point (which made only one render). Pure sim, lockstep-safe.
        UnitSeparation.Separate(_sim, Fixed.FromFloat(dt));
        TickUnitAI(dt);
        TickGatherers(dt);
        TickAttackers(dt);
        TickBuilders(dt);
        TickProductionQueues(dt);
        TickFoundations(dt);
        TickResearch(dt);
        // 光环:每 tick 应用/移除(range diff + global/player reqTech 门控)。放 TickResearch 后、
        // ReapplyVisionScopeAll 前,使 vision aura 的修正值本轮即被 LOS 重算吃到。
        TickAuras(dt);
        // 领土衰减(对齐原版 TerritoryDecay/Capturable 的 1s 定时器,本处每回合 0.1s×rate):
        // 先刷新 decaying/blink 状态(读本周期的领土网格),再让 Capturable 抽干/恢复 CP。
        // 放 UpdateVisibilityData 前:翻面触发的 OwnerChanged 本周期即被 LOS 重算吃到。
        TickTerritoryDecay(dt);
        // 驻军持有者:BuffHeal 每秒回血(原版 1s HealTimeout)+ EjectHealth 低血逐出。
        // 放 UpdateVisibilityData 前:逐出回世界的单位本周期即被 LOS 重算吃到。
        TickGarrisonHolders(dt);
        // 炮塔跟拍(原版 Position.SetTurretParent 的引擎联动):在点单位锁到持有者
        // 位置+旋转偏移。放 UpdateVisibilityData 前:随行位移本周期即被 LOS 重算吃到。
        TickTurrets(dt);
        // Vision range through the modifiers pipeline: tech/aura changes re-cover seer
        // circles in the LOS grid. Runs every turn (after research completes) so all
        // players' ranges stay fresh without a research-completion hook per player.
        ValueModificationApplier.ReapplyVisionRangeAll(_sim, _range);
        // Settle any damage whose delay elapsed this turn, then advance the delay clock.
        _sim.DelayedDamage.TickPending(_sim);
        _sim.DelayedDamage.AdvanceTurn();
        // Conquest victory check — runs after dead entities are removed so the RangeManager
        // index reflects the current survivors.
        _sim.TickVictory();
        // Fog-of-war: recompute per-player visibility for whatever changed this turn
        // (moved/placed/destroyed seers, ownership flips). Fires VisibilityChanged, which
        // drives Fogging/Mirage bookkeeping and presentation-layer show/hide. Cheap no-op
        // when nothing moved.
        _range.UpdateVisibilityData();
    }

    /// <summary>Destroys every visual node and recreates one for each entity in the
    /// restored simulation. Called after <see cref="SaveGameManager.Load"/>: the
    /// old visual nodes referenced entities that were cleared + recreated by
    /// DeserializeSaveGame (same IDs, new component instances), so every node must
    /// be rebuilt from scratch. Also re-registers LOS/fog state.</summary>
    public void RebuildAllVisuals()
    {
        // Tear down existing nodes.
        foreach (var node in _entityNodes.Values)
            node.QueueFree();
        _entityNodes.Clear();
        _animators.Clear();
        _animState.Clear();
        _lastPos.Clear();
        _lastVis.Clear();
        _interpolator.Clear();
        _entityCacheDirty = true;

        // Recreate visuals for every entity in the restored sim.
        foreach (var entity in GetAllEntitiesSnapshot())
        {
            var identity = _sim.QueryInterface<IdentityComponent>(entity);
            string template = identity?.TemplateName ?? "";
            var owner = _sim.QueryInterface<OwnershipComponent>(entity);
            var color = GetPlayerColor(owner?.PlayerId ?? 0);

            bool isBuilding = identity?.IsBuilding == true;
            var health = _sim.QueryInterface<HealthComponent>(entity);
            float size = isBuilding ? 4f : 1.5f;

            CreateVisualFor(entity, color, size, isBuilding, templateName: template);

            // Re-register for LOS so the entity isn't permanently hidden.
            EntityAssembler.RegisterForLos(_sim, entity, template, null);
        }

        GD.Print($"[RebuildAllVisuals] recreated {_entityNodes.Count} visual nodes");
    }

    /// <summary>Cold-load rebuild of the two system spatial indexes that
    /// <c>DeserializeSaveGame</c> does NOT refill (it bypasses EntityCreated): the
    /// ObstructionManager shapes and the RangeManager entity index + LOS counts. The player
    /// registry round-trips in the save payload itself (ComponentManager v6), so it needs no
    /// rebuild here. Call AFTER SetBounds sized the indexes to the real map and BEFORE
    /// RebuildAllVisuals (whose RegisterForLos notifications need RangeManager._data populated).
    /// Idempotent — safe to call alongside a fresh spawn's own registrations.</summary>
    public void RebuildSpatialIndexesAfterLoad()
    {
        foreach (var e in GetAllEntitiesSnapshot())
            _sim.QueryInterface<ObstructionComponent>(e)?.EnsureRegistered();
        _range.Repopulate(GetAllEntitiesSnapshot());
        _range.UpdateVisibilityData();
        GD.Print($"[RebuildSpatialIndexesAfterLoad] re-registered obstructions + repopulated range index");
    }

    private void OnSimEntityDestroyed(EntityId entity)
    {
        if (_entityNodes.TryGetValue(entity, out var node))
        {
            node.QueueFree();
            _entityNodes.Remove(entity);
        }
        _animators.Remove(entity);
        _animState.Remove(entity);
        _lastPos.Remove(entity);
        _lastVis.Remove(entity);
        _interpolator.Remove(entity);
    }

    private void RemoveDeadEntities()
    {
        foreach (var entity in GetAllEntitiesSnapshot())
        {
            var health = _sim.QueryInterface<HealthComponent>(entity);
            if (health != null && health.IsDead)
            {                var owner = _sim.QueryInterface<OwnershipComponent>(entity);
                int fromPlayer = owner?.PlayerId ?? -1;
                Events.RaiseOwnershipChanged(new OwnershipChangedEvent
                {
                    Entity = entity,
                    From = fromPlayer,
                    To = -1
                });

                // Node cleanup happens in OnSimEntityDestroyed (fired by DestroyEntity below).
                // Pop accounting: dying means the entity leaves its owner. Mirrors how Player.js
                // reacts to MT_OwnershipChanged (To = INVALID_PLAYER).
                _sim.ApplyOwnershipPopChange(entity, fromPlayer, -1);
                // 驻军持有者被毁兜底:逐出可逐类别,其余随主同灭(原版 EjectOrKill;
                // EjectHealth 阈值内的通常已被 Tick 提前逐出)。
                _sim.QueryInterface<GarrisonHolderComponent>(entity)?.EjectOrKillAll(_sim);
                // 炮塔持有者在点单位强制下塔(原版 TurretHolder OnOwnershipChanged →
                // EjectOrKill);塔上单位死亡则让出点位(原版 Turretable.OnOwnershipChanged)。
                _sim.QueryInterface<TurretHolderComponent>(entity)?.EjectOrKillAll(_sim);
                var turretable = _sim.QueryInterface<TurretableComponent>(entity);
                if (turretable is { Holder: not null })
                    turretable.LeaveTurret(_sim, forced: true);
                // 编队成员死亡:从所属编队移除(低于 RequiredMemberCount 时编队解散,
                // 原版同;成员位释放,Offsets 作废待下次重排)。
                var memberAi = _sim.QueryInterface<UnitAIComponent>(entity);
                if (memberAi?.FormationController is { } formationCtrl)
                    _sim.QueryInterface<FormationComponent>(formationCtrl)
                        ?.RemoveMembers(_sim, new List<EntityId> { entity });
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
                            int gathered = supply.Take((int)(gatherer.EffectiveRate(_sim, supply.Type) * dt));
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

    /// <summary>Debug helper for ZEROAD_CAPTURE=gather: order the first civilian to
    /// gather the nearest tree, so captures land inside the GATHERING state
    /// (verifies chop animation + axe prop switching).</summary>
    public void DebugOrderFirstCivilianGatherNearest()
    {
        EntityId? civ = null;
        PositionComponent? civPos = null;
        foreach (var e in GetAllEntitiesSnapshot())
        {
            var ident = _sim.QueryInterface<IdentityComponent>(e);
            if (ident?.TemplateName?.Contains("support_civilian") != true) continue;
            if (_sim.QueryInterface<ResourceGatherer>(e) == null) continue;
            var p = _sim.QueryInterface<PositionComponent>(e);
            if (p == null) continue;
            civ = e; civPos = p;
            break;
        }
        if (civ == null || civPos == null) return;

        EntityId? tree = null;
        float best = float.MaxValue;
        foreach (var e in GetAllEntitiesSnapshot())
        {
            var supply = _sim.QueryInterface<ResourceSupply>(e);
            if (supply == null || supply.Amount <= 0 || supply.SpecificType != "tree") continue;
            var pos = _sim.QueryInterface<PositionComponent>(e);
            if (pos == null) continue;
            float dx = pos.Position.X.ToFloat() - civPos.Position.X.ToFloat();
            float dz = pos.Position.Z.ToFloat() - civPos.Position.Z.ToFloat();
            float d2 = dx * dx + dz * dz;
            if (d2 < best) { best = d2; tree = e; }
        }
        if (tree == null) return;

        // Go through the real command path (lockstep queue → SimCommandExecutor →
        // UnitAI FSM order) — calling GatherResource directly sets the target but
        // leaves the FSM in IDLE, so the gather states/animations never trigger.
        CommandGather(civ.Value, tree.Value);
    }

    public static string MapBuildNameToTemplate(string name) => name switch
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
                // 血量类科技改变 Health/Max → 该玩家全部实体按比例缩放(原版 Health.js 同款)
                if (_playerEntity.HasValue)
                {
                    ValueModificationApplier.RescaleHealth(_sim, _playerEntity.Value);
                    // Capturable/CapturePoints 科技(如 ship_capture_resistance ×1.4)→ CP 数组按比例缩放
                    ValueModificationApplier.RescaleMaxCapturePoints(_sim, _playerEntity.Value);
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

        // special/* 虚拟实体(编队控制器等):无视觉不渲染,不占人口(无 CostComponent)。
        if (e.TemplateName.StartsWith("special/", StringComparison.Ordinal)) return;

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

        // Dispatch by template kind so static entities aren't assembled as movable units.
        // SpawnUnit otherwise adds UnitMotion + IsUnit=true unconditionally, which made Town
        // Centres and trees selectable-and-movable (right-click moved them). 0 A.D. namespaces
        // templates as structures/ (buildings), gaia/ (resources), units/ (mobile); a non-zero
        // ResourceAmount marks gatherable gaia (trees/stone/metal) vs. decor/animals.
        bool isStructure = templateName.StartsWith("structures/", StringComparison.OrdinalIgnoreCase);
        bool isResource = (stats?.ResourceAmount ?? 0) > 0;

        return SpawnUnit(x, z,
            isVillager: stats?.CanGather == true,
            isSoldier: stats?.AttackDamage > 0,
            stats: stats,
            isStructure: isStructure,
            isResource: isResource,
            templateName: templateName);
    }

    public EntityId SpawnUnit(float x, float z, bool isVillager = false, bool isSoldier = false,
        TemplateStats? stats = null, bool isStructure = false, bool isResource = false,
        string? templateName = null)
    {
        var entity = _sim.CreateEntity();
        _sim.AddComponent(entity, new PositionComponent());
        // Motion + unit AI belong only to mobile units. Structures (Town Centre) and resources
        // (trees) are static — giving them UnitMotion made them move on right-click, and IsUnit
        // made them drag-selectable as if they were troops.
        bool isMobile = !isStructure && !isResource;
        if (isMobile)
        {
            _sim.AddComponent(entity, new UnitMotion());
            _sim.AddComponent(entity, new UnitAIComponent());
        }

        string name = stats?.Name ?? (isSoldier ? "Soldier" : isVillager ? "Villager" : "Unit");
        // 血条只在模板真声明 <Health> 时装配(原版:树木/岩石无 Health=不可攻击,
        // gaia 动物有 Health=可猎)。MaxHealth 默认 100 不代表模板有血条,须看 HasHealth;
        // stats 缺失的旧路径(无模板调试生成)保持原样加血。
        if (stats == null || stats.HasHealth)
        {
            int maxHp = stats?.MaxHealth ?? (isSoldier ? 80 : 50);
            _sim.AddComponent(entity, new HealthComponent { Current = maxHp, Max = maxHp });
        }
        var identity = new IdentityComponent
        {
            Name = name,
            TemplateName = stats?.TemplateName ?? "",
            IsUnit = isMobile,
            IsBuilding = isStructure,
            Classes = stats?.GetClassList() ?? new List<string>()
        };
        if (isSoldier && !identity.HasClass("CitizenSoldier"))
            identity.Classes.Add("CitizenSoldier");
        _sim.AddComponent(entity, identity);

        var motion = _sim.QueryInterface<UnitMotion>(entity);
        if (motion != null && stats != null)
            motion.Speed = Fixed.FromFloat(stats.WalkSpeed);

        // Resource node (tree/stone/metal): gatherable supply, nothing else.
        if (isResource)
        {
            int amt = stats?.ResourceAmount > 0 ? stats.ResourceAmount : 100;
            _sim.AddComponent(entity, new ResourceSupply
            {
                Type = stats?.ResourceType ?? ResourceType.Wood,
                Amount = amt,
                MaxAmount = amt
            });
        }

        // Structures train units and accept dropsite deliveries per their template (Civil Centre
        // has both). Without these a real-template TC couldn't train — the fallback SpawnBuilding
        // hardcodes them.
        if (isStructure && stats != null)
        {
            if (stats.CanTrain)
            {
                // 训练列表数据驱动(原版 Trainer/Entities):tokens+原生文明随组件装配,
                // {civ} 按属主实时解析——雅典 CC 出雅典兵,被占领后出占领者的兵。
                _sim.AddComponent(entity, new ProductionQueue
                {
                    TrainableTokens = stats.TrainableEntities,
                    NativeCiv = stats.Civ,
                });
                // 集结点(原版每个生产建筑都有 RallyPointRenderer):此前只有地基完工路径
                // (SpawnScenarioBuilding)装了,起始 CC 永远设不了集结点。
                _sim.AddComponent(entity, new RallyPointComponent());
            }
            if (stats.IsDropsite)
                _sim.AddComponent(entity, new ResourceDropsite());
            // 人口加成数据驱动(顶层 <Population><Bonus>:CC +20/房子 +5)。起始 CC 走
            // 本路径——缺它则 RecomputePlayerPopBonus(覆写语义)在首个房子完工时把
            // CC 的 20 抹掉,人口帽反降。镜像 SpawnScenarioBuilding 的装配。
            if (stats.PopulationBonus > 0)
                _sim.AddComponent(entity, new PopulationComponent { Bonus = stats.PopulationBonus });
        }

        if (isVillager || stats?.CanGather == true)
        {
            _sim.AddComponent(entity, new ResourceGatherer());
            _sim.AddComponent(entity, new BuilderComponent());
        }

        // Garrisonable(镜像 EntityAssembler:可驻防单位;缺它 Order.Garrison 一律拒收,
        // 开局单位曾因此无法驻防、驻军光标门也失效)。
        if (stats != null && stats.GarrisonableSize > 0)
            _sim.AddComponent(entity, new GarrisonableComponent { Size = stats.GarrisonableSize });

        if (isSoldier || (stats != null && (stats.AttackDamage > 0
            || stats.AttackCaptureStrength > Fixed.Zero)))
        {
            var dmg = new DamageBlock();
            if (stats != null)
            {
                if (stats.AttackHack > 0) dmg.Amounts[DamageType.Hack] = stats.AttackHack;
                if (stats.AttackPierce > 0) dmg.Amounts[DamageType.Pierce] = stats.AttackPierce;
                if (stats.AttackCrush > 0) dmg.Amounts[DamageType.Crush] = stats.AttackCrush;
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
            // Capture 攻击类型(对齐 EntityAssembler):AddComponent 后赋值。
            if (stats != null)
            {
                atk.CaptureStrength = stats.AttackCaptureStrength;
                atk.CaptureRange = stats.AttackCaptureRange;
                atk.CaptureRate = stats.AttackCaptureRate;
                atk.CaptureRestrictedClasses = stats.AttackCaptureRestrictedClasses;
                atk.PreferredClasses = stats.AttackPreferredClasses;
                atk.PhysicalRestrictedClasses = stats.AttackPhysicalRestrictedClasses;
            }

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
        float visualSize = isStructure ? 8f : isResource ? 2.5f : 1.5f;
        // Resolve the visual from the authoritative template name (SpawnFromTemplate passes the
        // real one; stats.TemplateName 自 ExtractStats 回填后亦为真名,视觉解析仍以此显式
        // 参数为准)。Don't fall back to the shared _lastSpawnedTemplate — SpawnBuilding
        // contaminates it with civil_centre, which made stats-less units render as Town
        // Centres. Direct callers with no template (AI villagers, sandbox soldiers) get a
        // role-appropriate default instead.
        string visualTemplate = templateName ?? string.Empty;
        if (visualTemplate.Length == 0 && !isStructure && !isResource)
        {
            visualTemplate = isVillager ? "units/athen/support_female_citizen"
                          : isSoldier ? "units/athen/infantry_spearman_b"
                          : string.Empty;
        }
        CreateVisualFor(entity, color, visualSize, isBuilding: isStructure,
            templateName: visualTemplate.Length > 0 ? visualTemplate : null);
        // Ownerless at this point (scenario callers re-register after assigning an owner);
        // still indexes the entity so fog visibility applies to it.
        EntityAssembler.RegisterForLos(_sim, entity, stats?.TemplateName ?? "", stats);
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
        // No template stats on this fallback path: indexing only, no fog components.
        EntityAssembler.RegisterForLos(_sim, entity, _lastSpawnedTemplate, stats: null);
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
        EntityAssembler.RegisterForLos(_sim, entity, _lastSpawnedTemplate, stats: null);
        return entity;
    }

    /// <summary>把 AI 大脑挂到指定玩家实体上(Phase 2 内核驻留)。Main.cs 在 InitWorld 后调用:
    /// 解析玩家实体 → new AIComponent → Configure(_sim, _netTurn)(AddComponent 前注入,与
    /// AuraComponent.Configure 同模式)→ AddComponent。TickAI 每回合推进;save/load 由
    /// SaveGameManager.Load 的 prepareComponent 重注入 Configure。</summary>
    public void AttachAi(int playerId)
    {
        var playerEntity = _sim.GetPlayerEntityId(playerId);
        if (playerEntity == null) return;
        var ai = new AIComponent();
        ai.Configure(_sim, _netTurn);
        _sim.AddComponent(playerEntity.Value, ai);
    }

    // --- Commands (ALL player commands funnel into the lockstep queue; in standalone
    // they execute COMMAND_DELAY turns later, exactly as in multiplayer — one code path,
    // no SP/MP divergence. Presentation-only validation stays in Main.) ---

    public void SubmitCommand(NetCommand cmd) => _netTurn.SubmitLocalCommand(cmd);

    /// <summary>Idempotently assign an entity's owning player. Sandbox spawn paths
    /// (SpawnUnit/SpawnBuilding) create ownerless entities; this attaches the OwnershipComponent
    /// the AI owned-list scan and SimCommandExecutor routing rely on, AND re-syncs the
    /// RangeManager index so the entity counts for conquest. SpawnUnit/SpawnBuilding add Position
    /// + Ownership post-creation, but neither fires the events RangeManager._data relies on
    /// (PositionComponent.Position is a plain field; AddComponent doesn't NotifyOwnerChanged), so
    /// the auto-fired OnEntityCreated leaves _data at {Owner=-1, InWorld=false}. Without this
    /// refresh, GetEntitiesByPlayer returns empty → TickVictory flags the player defeated at the
    /// first tick. RefreshFromComponents is the documented post-assembly re-read (mirrors the
    /// mirage registration path in EntityAssembler).</summary>
    public void AssignOwner(EntityId entity, int playerId)
    {
        if (_sim.QueryInterface<OwnershipComponent>(entity) == null)
        {
            _sim.AddComponent(entity, new OwnershipComponent { PlayerId = playerId });
            _range.RefreshFromComponents(entity);
        }
    }

    public void MoveEntity(EntityId entity, float x, float z) =>
        SubmitCommand(NetCommand.Move(LocalPlayerId, entity.Value,
            Fixed.FromFloat(x), Fixed.FromFloat(z)));

    public void CommandGather(EntityId unit, EntityId target) =>
        SubmitCommand(NetCommand.Gather(LocalPlayerId, unit.Value, target.Value));

    public void CommandAttack(EntityId attacker, EntityId target, bool allowCapture = false) =>
        SubmitCommand(NetCommand.Attack(LocalPlayerId, attacker.Value, target.Value, allowCapture));

    /// <summary>Issue a build order: cost charge + foundation spawn happen in the sim
    /// at the execution turn (SimCommandExecutor). `template` is the FULL template name.</summary>
    public void CommandBuild(EntityId builder, string template, float x, float z) =>
        SubmitCommand(NetCommand.Build(LocalPlayerId, builder.Value, template,
            Fixed.FromFloat(x), Fixed.FromFloat(z)));

    public void CommandSetRallyPoint(EntityId building, EntityId? target) =>
        SubmitCommand(NetCommand.SetRallyPoint(LocalPlayerId, building.Value, target?.Value ?? 0));

    /// <summary>Set a ground rally point (right-click empty ground on a production
    /// building). x/z are world coords; mirrors <see cref="CommandBuild"/>'s float→Fixed
    /// conversion (对齐原版集合点语义).</summary>
    public void CommandSetRallyPointPosition(EntityId building, float x, float z) =>
        SubmitCommand(NetCommand.SetRallyPointPosition(LocalPlayerId, building.Value,
            Fixed.FromFloat(x), Fixed.FromFloat(z)));

    public void CommandResearch(EntityId building, string techName) =>
        SubmitCommand(NetCommand.Research(LocalPlayerId, building.Value, techName));

    /// <summary>停止单位全部订单回 IDLE(原版 "stop")。</summary>
    public void CommandStop(EntityId unit) =>
        SubmitCommand(NetCommand.Stop(LocalPlayerId, unit.Value));

    /// <summary>删除己方选中实体(执行端校验归属;原版 delete-entities)。</summary>
    public void CommandDelete(EntityId entity) =>
        SubmitCommand(NetCommand.Delete(LocalPlayerId, entity.Value));

    /// <summary>取消训练队列第 index 项并全额退资源(原版 stop-production)。</summary>
    public void CommandCancelProduction(EntityId building, int queueIndex) =>
        SubmitCommand(NetCommand.CancelProduction(LocalPlayerId, building.Value, queueIndex));

    /// <summary>改站姿(原版 stance 命令;violent/aggressive/defensive/passive/standground)。</summary>
    public void CommandSetUnitStance(EntityId unit, string stance) =>
        SubmitCommand(NetCommand.SetUnitStance(LocalPlayerId, unit.Value, stance));

    /// <summary>载入驻军(原版 garrison 命令:单位走近宿主建筑后入住)。</summary>
    public void CommandGarrison(EntityId unit, EntityId holder) =>
        SubmitCommand(NetCommand.Garrison(LocalPlayerId, unit.Value, holder.Value));

    /// <summary>卸载驻军(unitId=-1 = 全部,原版 unload-all-by-owner)。</summary>
    public void CommandUngarrison(EntityId holder, int unitId = -1) =>
        SubmitCommand(NetCommand.Ungarrison(LocalPlayerId, holder.Value, unitId));

    public void CommandTrain(EntityId building, string template, int count = 1, bool batch = false) =>
        SubmitCommand(NetCommand.Train(LocalPlayerId, building.Value, template, batch ? 5 : count));

    /// <summary>从建筑可训练列表选首选项(support=true → 首个含 "support_" 的项,否则首个
    /// 非 support 项;原版 GUI 列表首项语义)。列表为空返回 null。</summary>
    private string? FirstTrainable(EntityId building, bool support)
    {
        var queue = _sim.QueryInterface<ProductionQueue>(building);
        if (queue == null) return null;
        foreach (var t in queue.GetTrainableEntities(_sim))
        {
            bool isSupport = t.Contains("support_");
            if (isSupport == support) return t;
        }
        return null;
    }

    public void CommandTrain(EntityId building) =>
        CommandTrain(building, FirstTrainable(building, support: true) ?? "units/spart/support_civilian");

    // ── 第二梯队菜单面板:外交/贸易命令包装(玩家级,无 entity) ────────────────

    /// <summary>外交立场(原版 cmd type:"diplomacy")。stance 取 DiplomacyComponent 常量。</summary>
    public void CommandSetStance(int targetPlayer, int stance) =>
        SubmitCommand(NetCommand.SetStance(LocalPlayerId, targetPlayer, stance));

    /// <summary>进贡(原版 cmd type:"tribute",单资源/次)。</summary>
    public void CommandTribute(int destPlayer, ResourceType type, int amount) =>
        SubmitCommand(NetCommand.Tribute(LocalPlayerId, destPlayer, type, amount));

    /// <summary>贸易品比例(原版 cmd type:"set-trading-goods",4 资源百分比和=100)。</summary>
    public void CommandSetTradingGoods(int wood, int food, int stone, int metal) =>
        SubmitCommand(NetCommand.SetTradingGoods(LocalPlayerId, wood, food, stone, metal));

    /// <summary>易物(原版 cmd type:"barter",amount∈{100,500})。</summary>
    public void CommandBarter(ResourceType sell, ResourceType buy, int amount) =>
        SubmitCommand(NetCommand.Barter(LocalPlayerId, sell, buy, amount));

    /// <summary>本地玩家认输(原版 Menu "Resign"):置 Defeated + 触发 PlayerDefeated 事件
    /// (GameOverOverlay 已订阅 → 显失败屏)。SP 本地直改;MP 需广播一致置败,列 backlog。</summary>
    public void ResignLocalPlayer()
    {
        int lp = (int)LocalPlayerId;
        var player = _sim?.Players.GetPlayerEntity(lp);
        if (player == null) return;
        if (player.SetDefeated())
            _sim.Events.RaisePlayerDefeated(new PlayerDefeatedEvent { PlayerId = lp, Reason = "Resigned." });
    }

    public void CommandTrainSoldier(EntityId building)
    {
        CommandTrain(building, FirstTrainable(building, support: false) ?? "units/spart/infantry_spearman_b");
    }

    /// <summary>编队行走(原版 Commands.js 编队创建流:过滤可编队成员 → 生成
    /// special/formations/{shape} 控制器 → SetMembers → 控制器 Walk)。成员不足
    /// RequiredMemberCount 或模板缺失时退化为逐个普通行走(原版默认 NULL_FORMATION
    /// 不成队,编队只能显式选择)。未接锁步:NetCommand 无实体列表参数,MP 编队
    /// 指令随 GUI 编队选择器一起做;SP 直接执行(命令点确定,控制器 spawn/布阵
    /// 全在内核确定性路径上)。</summary>
    public void CommandFormationWalk(IReadOnlyList<EntityId> entities, float x, float z, string shape = "box")
    {
        // 原版编队按玩家分组:取首个合格成员的属主,仅纳入同主成员。
        int owner = -1;
        var members = new List<EntityId>();
        foreach (var e in entities)
        {
            var ai = _sim.QueryInterface<UnitAIComponent>(e);
            if (ai == null || ai.IsGarrisoned || ai.IsTurret
                || ai.FormationController != null || ai.IsFormationController)
                continue;
            int eOwner = _sim.QueryInterface<OwnershipComponent>(e)?.PlayerId ?? -1;
            if (owner < 0) owner = eOwner;
            if (eOwner != owner) continue;
            members.Add(e);
        }
        string template = "special/formations/" + shape;
        TemplateStats? stats = null;
        try { stats = Templates?.ExtractStats(template); } catch { }
        int required = stats?.FormationRequiredMemberCount ?? int.MaxValue;
        if (stats == null || members.Count < required)
        {
            foreach (var m in members)
                MoveEntity(m, x, z);
            return;
        }
        // 控制器生成于成员质心(SetMembers 的 MoveToMembersCenter 同样会归位)。
        float ax = 0, az = 0;
        foreach (var m in members)
        {
            var p = _sim.QueryInterface<PositionComponent>(m);
            if (p == null) continue;
            ax += p.Position.X.ToFloat();
            az += p.Position.Z.ToFloat();
        }
        ax /= members.Count;
        az /= members.Count;
        var controller = _sim.SpawnEntity(template, ax, az, owner);
        var formation = _sim.QueryInterface<FormationComponent>(controller);
        if (formation == null)
        {
            // 模板未解析出编队件(不应发生)——保险退化:逐个行走。
            foreach (var m in members)
                MoveEntity(m, x, z);
            return;
        }
        formation.SetMembers(_sim, members);
        _sim.QueryInterface<UnitAIComponent>(controller)
            ?.Walk(new FixedVector2D(Fixed.FromFloat(x), Fixed.FromFloat(z)));
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

    public static Color GetPlayerColor(int playerId) =>
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

        var animator = ModelLibrary.FindManualAnimator(visual);
        if (animator != null)
        {
            _animators[entity] = animator;
            _animState[entity] = "idle";
            if (pos != null)
                _lastPos[entity] = new Vector3(pos.Position.X.ToFloat(), 0, pos.Position.Z.ToFloat());
        }
    }

    private void SyncVisuals()
    {
        SyncVisibility();
        _fogWorld.Update();
        _territoryWorld.Update();
        foreach (var kvp in _entityNodes)
        {
            var pos = _sim.QueryInterface<PositionComponent>(kvp.Key);
            if (pos == null) continue;
            var node = kvp.Value;

            var newPos = new Vector3(
                pos.Position.X.ToFloat(),
                TerrainHeightService.Sample(pos.Position.X.ToFloat(), pos.Position.Z.ToFloat()),
                pos.Position.Z.ToFloat());

            // 推给插值器记录 prev/curr(新单位/传送 snap 内置);渲染帧在 _Process 末尾
            // 按 alpha 插值写入 node.Position,而非每 tick 直接 snap(那会造成 10Hz 瞬移)。
            _interpolator.RecordTick(kvp.Key, node, newPos);

            if (_animators.TryGetValue(kvp.Key, out var animator))
                UpdateUnitAnimation(kvp.Key, node, animator, newPos);
        }
    }

    // Last applied per-player visibility per entity — visuals update only on transitions.
    private readonly Dictionary<EntityId, LosVisibility> _lastVis = new();

    /// <summary>Fog-of-war on the presentation layer: HIDDEN entities lose their node,
    /// FOGGED ones (structures/mirages standing in explored fog) stay but ghosted.
    /// Applied only on transitions (per-player visibility changes are rare per frame).</summary>
    private void SyncVisibility()
    {
        int lp = (int)LocalPlayerId;
        foreach (var kvp in _entityNodes)
        {
            var vis = _range.GetLosVisibility(kvp.Key, lp);
            if (_lastVis.TryGetValue(kvp.Key, out var old) && old == vis) continue;
            _lastVis[kvp.Key] = vis;
            ApplyVisibility(kvp.Value, vis);
        }
    }

    private static void ApplyVisibility(Node3D node, LosVisibility vis)
    {
        node.Visible = vis != LosVisibility.Hidden;
        // FOGGED = frozen last-seen state: darken every mesh's material via a surface
        // override (cleared back to null when the entity returns to sight).
        SetFoggedVisualRecursive(node, vis == LosVisibility.Fogged);
    }

    private static readonly Color FoggedTint = new(0.42f, 0.46f, 0.55f);

    private static void SetFoggedVisualRecursive(Node node, bool fogged)
    {
        if (node is MeshInstance3D mi)
        {
            int surfaces = mi.Mesh?.GetSurfaceCount() ?? 0;
            for (int i = 0; i < surfaces; i++)
            {
                if (!fogged)
                {
                    mi.SetSurfaceOverrideMaterial(i, null);
                    continue;
                }
                if (mi.GetActiveMaterial(i) is StandardMaterial3D active)
                {
                    var dimmed = (StandardMaterial3D)active.Duplicate();
                    // AlbedoColor multiplies the texture — a gray-blue tint reads as
                    // "in the fog" without touching the shared source materials.
                    var c = active.AlbedoColor;
                    dimmed.AlbedoColor = new Color(c.R * FoggedTint.R, c.G * FoggedTint.G,
                        c.B * FoggedTint.B, c.A);
                    mi.SetSurfaceOverrideMaterial(i, dimmed);
                }
            }
        }
        foreach (var child in node.GetChildren())
            SetFoggedVisualRecursive(child, fogged);
    }

    private void UpdateUnitAnimation(EntityId entity, Node3D node,
        SkeletalAnim.ManualAnimator animator, Vector3 newPos)
    {
        // Facing: rotate the visual to match travel direction on the frame the sim
        // actually moved us (sim ticks at 10 Hz, so the delta is 0 between ticks —
        // we only yaw on the nonzero step, which is correct).
        Vector3 last = _lastPos.TryGetValue(entity, out var lp) ? lp : newPos;
        Vector3 delta = newPos - last;
        _lastPos[entity] = newPos;
        if (delta.LengthSquared() > 0.0001f)
            node.Rotation = new Vector3(0, Mathf.Atan2(delta.X, delta.Z), 0);

        // Animation state is FSM-driven (not position-delta). Position deltas flip-flop
        // at 10 Hz vs 60 fps render and would stutter walk/idle; the FSM state is stable
        // between ticks. Mirrors the original VisualActor picking the variant by UnitAI
        // state name.
        string want = ResolveAnimationState(entity);
        if (!_animState.TryGetValue(entity, out var cur) || cur != want)
        {
            // Fall back to walk/idle when the unit lacks the exact state clip, so a
            // gather/attack state never freezes a unit that has no matching clip.
            if (!animator.HasState(want))
                want = ResolveAnimationState(entity).Contains("walk") ? "walk" : "idle";
            if (animator.HasState(want))
            {
                // SetAnimationState (not animator.Play) so per-state props switch too
                // (axe appears while chopping, shield hidden, restored on walk/idle).
                Actors.Composition.ActorComposer.SetAnimationState(node, want);
                _animState[entity] = want;
            }
        }
    }

    /// <summary>
    /// Maps the UnitAI FSM state to an animation state name (idle/walk/gather_*/build/
    /// attack_melee). FSM state names are hierarchical paths produced by Fsm.cs
    /// (e.g. "INDIVIDUAL.GATHER.GATHERING"), so we match by substring, not prefix.
    /// </summary>
    private string ResolveAnimationState(EntityId entity)
    {
        var ai = _sim.QueryInterface<UnitAIComponent>(entity);
        string fsm = ai?.FsmStateName ?? "";

        if (fsm.Contains("GATHER.GATHERING"))
        {
            var gatherer = _sim.QueryInterface<ResourceGatherer>(entity);
            var supply = gatherer?.TargetSupply is EntityId s
                ? _sim.QueryInterface<ResourceSupply>(s) : null;
            string? specific = supply?.SpecificType;
            return string.IsNullOrEmpty(specific) ? "gather_tree" : "gather_" + specific;
        }
        if (fsm.Contains("REPAIR.REPAIRING"))
            return "build";
        if (fsm.Contains("COMBAT.ATTACKING"))
            return "attack_melee";

        // Walking states: simple move (WALKING), approaching a target (*.APPROACHING),
        // or returning resources (GATHER.RETURNINGRESOURCE).
        if (fsm.EndsWith(".WALKING", StringComparison.Ordinal)
            || fsm.Contains("APPROACHING")
            || fsm.Contains("RETURNINGRESOURCE"))
            return "walk";

        return "idle";
    }

    public List<EntityId> GetEntitiesAtPosition(Vector3 worldPos, float radius = 3f)
    {
        var result = new List<EntityId>();
        int lp = (int)LocalPlayerId;
        foreach (var kvp in _entityNodes)
        {
            // Fog-of-war: hidden entities can't be clicked (fogged stand-ins stay
            // selectable, matching the original's mirage selection).
            if (_range.GetLosVisibility(kvp.Key, lp) == LosVisibility.Hidden) continue;
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
