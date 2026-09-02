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
	private BattleDecals? _decals;           // 战场贴花（击杀血斑,原版 blood_*.xml 的 decal 语义）
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

	/// <summary>触发器 ShowMessage 动作出口(数据驱动触发器 → HUD toast)。</summary>
	public event System.Action<string>? TriggerMessage;

	/// <summary>触发器效果出口实现:消息转事件(订阅在 Main → HUD),
	/// 生成走 SpawnFromTemplate。散布为黄金角圆周——确定性(无 RNG),lockstep 各端一致。</summary>
	private sealed class BridgeTriggerSink : ZeroAD.Sim.Triggers.ITriggerSink
	{
		private readonly SimBridge _bridge;
		public BridgeTriggerSink(SimBridge bridge) => _bridge = bridge;

		public void ShowMessage(string text) => _bridge.TriggerMessage?.Invoke(text);

		public IReadOnlyList<EntityId> SpawnEntities(string template, int playerId, float x, float z, int count, float spread)
		{
			var spawned = new List<EntityId>(count);
			for (int i = 0; i < count; i++)
			{
				float ox = 0f, oz = 0f;
				if (spread > 0f && count > 1)
				{
					float angle = i * 2.399963f;   // 黄金角(弧度)
					float r = spread * (float)System.Math.Sqrt((double)i / count);
					ox = r * (float)System.Math.Cos(angle);
					oz = r * (float)System.Math.Sin(angle);
				}
				spawned.Add(_bridge.SpawnFromTemplate(template, x + ox, z + oz, playerId));
			}
			return spawned;
		}
	}
	/// <summary>InitWorld 传入的模板根路径(simulation/templates)——SkirmishReplacer 由它
	/// 推导 civs 数据目录(../data/civs)。</summary>
	private string? _templatesPath;
	private TechCatalog? _techCatalog;
	/// <summary>Petra 共享状态(模板/科技目录 + Accessibility 水陆区域图;每图一份,
	/// 各 AI 玩家共用)。InitWorld 末构建;AttachAi 注入 AIComponent——此前从未接线,
	/// 导致 HQ 主循环在实战恒不跑(只有旧版兜底管理器在跑)。</summary>
	private ZeroAD.Sim.AI.CommonApi.SharedState? _sharedState;

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
			ZeroAD.Sim.Diag.Log("Sim", $"Loaded templates from: {templatesPath}");
			int count = 0;
			foreach (var kvp in templates.Cache) count++;
			if (count == 0) templates.LoadAllTemplates();
			ZeroAD.Sim.Diag.Log("Sim", $"Template cache: {templates.Cache.Count} entries");

			// 科技 JSON 与模板同根(simulation/templates → simulation/data/technologies)
			var techDir = System.IO.Path.GetFullPath(
				System.IO.Path.Combine(templatesPath, "..", "data", "technologies"));
			techCatalog = TechnologyLoader.LoadAll(techDir);
			ZeroAD.Sim.Diag.Log("Sim", $"Technologies: {techCatalog.Technologies.Count} (+{techCatalog.Pairs.Count} pairs)");

			// 光环 JSON 同根(simulation/data/auras)。MVP 仅收 range/global/player 三型。
			var auraDir = System.IO.Path.GetFullPath(
				System.IO.Path.Combine(templatesPath, "..", "data", "auras"));
			auraCatalog = AuraLoader.LoadAll(auraDir);
			ZeroAD.Sim.Diag.Log("Sim", $"Auras: {auraCatalog.Auras.Count} entries (range/global/player only)");
		}

		_sim = new ComponentManager(seed, registry, templates);
		if (auraCatalog != null) _sim.Auras = auraCatalog;
		SimSystem.Init(_sim);
		_sim.Triggers.Sink = new BridgeTriggerSink(this);
		// 触发器事件总线接 sim 事件(原版 Trigger 组件订阅 sim 消息的等价):
		// OwnershipChanged/StructureBuilt/TrainingFinished/ResearchFinished/
		// TreasureCollected → TriggerSystem.CallEvent 投递到事件触发器。
		_sim.Triggers.Attach(_sim);
		Templates = templates;
		_templatesPath = templatesPath;
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
		_decals ??= new BattleDecals();
		if (_decals.GetParent() == null) AddChild(_decals);

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
		// pathfinder.xml 通行类注册表(数据驱动):templatesPath =
		// …/mods/public/simulation/templates → 三级上级即 mods 根;缺失 → 内建默认。
		if (templatesPath != null)
			_pathfinder.SetPassabilityConfig(System.IO.Path.GetFullPath(
				System.IO.Path.Combine(templatesPath, "..", "..", "..")));
		SimSystem.SetTerrainComponent(_terrain);   // 高度网格/Attack 高度差用
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
		// 易物价差全局状态的存档骑缝(BarterSystem 漂移表 → 状态哈希/存档)。
		_sim.AddComponent(_terrainEntity, new BarterStateComponent());

		foreach (var slot in slots)
		{
			if (slot.Kind == PlayerSlotKind.Closed) continue;
			int pid = slot.PlayerId;
			var playerEntity = _sim.CreateEntity();
			_sim.AddComponent(playerEntity, new PlayerComponent { Civ = slot.Civ });
			_sim.AddComponent(playerEntity, new DiplomacyComponent());
			// 受击警报 + 战区跟踪(原版挂 template_player 的 AttackDetection/BattleDetection)。
			_sim.AddComponent(playerEntity, new AttackDetectionComponent());
			_sim.AddComponent(playerEntity, new BattleDetectionComponent());
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

		// Petra 共享状态:模板+科技目录就绪即建;Accessibility 在地图网格定型后
		// 由 RefreshAiAccessibility 补齐(Main 在末次 RebuildGrid 后调)。
		_techCatalog = techCatalog;
		_sharedState = templates != null && techCatalog != null
			? new ZeroAD.Sim.AI.CommonApi.SharedState(templates, techCatalog)
			: null;
		SimSystem.SetNet(_netTurn);   // 地图脚本/内核侧命令通道(gaia 狼群攻击等)
	}

	/// <summary>安装地图脚本(原版 _triggers.js 的 C# 移植件;Main 在地图加载完成后
	/// 按图名调用)。OnInit 即执行(原版 OnInitGame 时机);Tick 随 TriggerSystem 逐回合。</summary>
	public void InitMapScript(string mapName)
	{
		if (_sim == null) return;
		ZeroAD.Sim.Triggers.IMapScriptBehavior? script = mapName switch
		{
			"polar_sea" => new ZeroAD.Sim.Triggers.PolarSeaScript(),
			"elephantine" => new ZeroAD.Sim.Triggers.ElephantineScript(),
			"survivalofthefittest" => new ZeroAD.Sim.Triggers.SurvivalOfTheFittestScript(),
			"flood" => new ZeroAD.Sim.Triggers.FloodScript(),
			"extinct_volcano" => new ZeroAD.Sim.Triggers.ExtinctVolcanoScript(),
			"danubius" => new ZeroAD.Sim.Triggers.DanubiusScript(),
			"jebel_barkal" => new ZeroAD.Sim.Triggers.JebelBarkalScript(),
			_ => null,
		};
		_sim.Triggers.MapScript = script;
		if (script != null)
		{
			script.OnInit(_sim);
			ZeroAD.Sim.Diag.Log("Map", $"trigger script installed: {mapName}");
		}
	}

	/// <summary>地图加载/障碍定型后重建 AI 水陆可达性区域图(原版 Accessibility
	/// 每图构建一次)。Main 在末次 Pathfinder.RebuildGrid 之后调用。</summary>
	public void RefreshAiAccessibility()
	{
		if (_sharedState != null && _pathfinder.PassabilityGrid != null)
			_sharedState.BuildAccessibility(_pathfinder);
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
			_recorder = new ReplayRecorder(writer, _netTurn, path, () => _sim.ComputeStateHash());
		}
		catch (System.Exception ex)
		{
			ZeroAD.Sim.Diag.Err("Replay", $"start recording failed: {ex.Message}");
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
			ZeroAD.Sim.Diag.Err("Sim", "Tutorial scenario XML not found");
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
		ZeroAD.Sim.Diag.Log("Sim", $"Loaded tutorial scenario: {scenario.Entities.Count} entities ({scenario.Name})");
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
					// 文明随地图 XML(原版 scenario 行为;此前漏设——单位按 spart
					// 模板生成但玩家文明停在默认 athen,徽标/科技树全错)。
					if (pd.Civ.Length > 0) player.Civ = pd.Civ;
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
					PopulationLimit = 20,
					Civ = pd.Civ.Length > 0 ? pd.Civ : "athen",
				});
				_sim.AddComponent(enemy, new OwnershipComponent { PlayerId = pd.PlayerId });
				_sim.AddComponent(enemy, new DiplomacyComponent());
				// 受击警报 + 战区跟踪(与 P1 玩家实体同款)。
				_sim.AddComponent(enemy, new AttackDetectionComponent());
				_sim.AddComponent(enemy, new BattleDetectionComponent());
				// 注册玩家实体到 PlayerManager(原版 player registration):此前漏了 →
				// GetNonGaiaPlayerIds 不含 P2 → RangeManager.UpdateVisibilityData 的评估
				// 循环从不评估任何实体对 P2 的可见性 → P2 视野圆加了但 P1 对 P2 的缓存
				// 可见性永远 Hidden → P2 不攻击。
				_sim.RegisterPlayer(pd.PlayerId, enemy);
			}
		}
	}

	/// <summary>加载地图 XML 实体并生成（scenario/skirmish 通用）。XML 存在且含 sim 实体
	/// 时:skirmish 占位先经 SkirmishReplacer 替换,全部实体走 scenario 生成路径,
	/// XML PlayerData 的 civ 覆盖玩家文明(原版 scenario 行为:地图作者定文明),
	/// 返回 ScenarioData 供调用方做外交播种;否则返回 null(走调用方默认生成)。</summary>
	public ScenarioData? LoadMapScenario(string dataRoot, string mapRelPathNoExt)
	{
		string? xmlPath = ScenarioLoader.FindScenarioPath(dataRoot, mapRelPathNoExt);
		if (xmlPath == null) return null;
		var scenario = ScenarioLoader.Load(xmlPath);
		if (!scenario.Entities.Any(e => e.IsSimulationEntity)) return null;
		ApplyScenarioCivs(scenario);
		ApplyVictoryConditions(scenario);
		SpawnScenarioEntities(scenario);
		// 弑君模式(原版 maps/scripts/Regicide.js 的数据驱动移植):实体落地后,
		// 按玩家文明随机选英雄生成在其最佳建筑旁(CivCentre > Structure > Ship)。
		if (_sim?.EndGame.HasCondition("regicide") == true)
			SpawnRegicideHeroes();
		ZeroAD.Sim.Diag.Log("Map", $"loaded map entities: {scenario.Entities.Count} ({scenario.Name})");
		return scenario;
	}

	/// <summary>为每位非 gaia 玩家生成弑君英雄(原版 InitRegicideGame +
	/// SpawnRegicideHero)。英雄模板 = units/ 下 Hero 类且 Civ 匹配;随机选取走
	/// cm.RNG(锁步确定);出生点取玩家 CivCentre > 其他建筑 > Ship 的首个,
	/// 位置经 Footprint.PickSpawnPoint(无 footprint 回退 +X 偏移)。</summary>
	private void SpawnRegicideHeroes()
	{
		if (_sim == null || Templates == null) return;
		var range = _range;
		foreach (int pid in _sim.Players.GetNonGaiaPlayerIds())
		{
			var player = _sim.GetPlayerEntity(pid);
			if (player == null) continue;

			// 该文明的英雄模板(确定性:模板名排序后 RNG 抽取)。
			var heroes = new List<string>();
			foreach (var kvp in Templates.Cache)
			{
				if (!kvp.Key.StartsWith("units/", StringComparison.Ordinal)) continue;
				var identity = kvp.Value.GetChild("Identity");
				if (!identity.IsOk) continue;
				string civ = identity.GetChild("Civ").ToString();
				if (civ != player.Civ) continue;
				string classes = identity.GetChild("Classes").ToString() + " "
					+ identity.GetChild("VisibleClasses").ToString();
				if (!classes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("Hero")) continue;
				heroes.Add(kvp.Key);
			}
			if (heroes.Count == 0) continue;
			heroes.Sort(StringComparer.Ordinal);
			string heroTemplate = heroes[(int)(_sim.RNG.NextDouble() * heroes.Count) % heroes.Count];

			// 出生点:CivCentre > Structure > Ship(原版 spawnPreferences);
			// 同偏好按实体 id 小者优先(确定性)。
			EntityId? best = null; int bestPref = -1;
			foreach (var ent in range.GetEntitiesByPlayer(pid))
			{
				var id = _sim.QueryInterface<IdentityComponent>(ent);
				if (id == null) continue;
				int pref = id.HasClass("CivCentre") ? 3
					: id.IsBuilding ? 2
					: id.HasClass("Ship") ? 1 : 0;
				if (pref > bestPref || (pref == bestPref && best.HasValue && ent.Value < best.Value.Value))
				{ best = ent; bestPref = pref; }
			}
			if (best == null) continue;   // 无任何实体(原版:取偏好序首实体,任意实体皆可)

			var anchorPos = _sim.QueryInterface<PositionComponent>(best.Value);
			if (anchorPos == null) continue;
			float hx = anchorPos.Position.X.ToFloat() + 3f;
			float hz = anchorPos.Position.Z.ToFloat();
			var footprint = _sim.QueryInterface<FootprintComponent>(best.Value);
			if (footprint != null)
			{
				var spawn = footprint.PickSpawnPoint(ZeroAD.Sim.Maths.Fixed.FromFloat(1f));
				hx = spawn.X.ToFloat();
				hz = spawn.Z.ToFloat();
			}
			var hero = SpawnFromTemplate(heroTemplate, hx, hz, pid);
			_sim.EndGame.RegicideHeroes[pid] = hero;
			ZeroAD.Sim.Diag.Log("Map", $"regicide hero for player {pid}: {heroTemplate}");
		}
	}

	/// <summary>地图 ScriptSettings 的胜利条件注入 EndGameManager(原版 InitGame.js →
	/// EndGameManager.InitGame 读取 GameTypeSettings)。空列表 = 默认征服,时长分钟→秒已在
	/// ScenarioLoader 转换。</summary>
	private void ApplyVictoryConditions(ScenarioData scenario)
	{
		var endGame = _sim?.EndGame;
		if (endGame == null) return;
		if (scenario.VictoryConditions.Count > 0)
			endGame.SetVictoryConditions(scenario.VictoryConditions);
		endGame.WonderVictoryDuration = scenario.WonderVictoryDuration;
		endGame.RelicVictoryDuration = scenario.RelicVictoryDuration;
		endGame.CeasefireDuration = scenario.CeasefireDuration;
		// 同盟共胜 = LockTeams || !LastManStanding(原版 Setup.js)。
		endGame.AlliedVictory = scenario.LockTeams || !scenario.LastManStanding;
		endGame.RegicideGarrison = scenario.RegicideGarrison;
		if (scenario.VictoryConditions.Count > 0)
			ZeroAD.Sim.Diag.Log("Map", $"victory conditions: {string.Join(",", scenario.VictoryConditions)}");
		// 停战设置(原版 Setup.js:if (settings.Ceasefire) StartCeasefire)——
		// 期间全体非 gaia 互置中立,到期恢复外交。
		if (scenario.CeasefireDuration > 0 && _sim != null)
		{
			endGame.StartCeasefire(_sim);
			ZeroAD.Sim.Diag.Log("Map", $"ceasefire: {scenario.CeasefireDuration}s");
		}
	}

	/// <summary>XML PlayerData 的 civ 写入玩家实体(原版 scenario:文明由地图定义,
	/// gamesetup 下拉对 scenario 图实为展示)。空 civ 不动(skirmish 图 PlayerData 无 civ,
	/// 用槽位表)。</summary>
	private void ApplyScenarioCivs(ScenarioData scenario)
	{
		foreach (var pd in scenario.Players)
		{
			if (string.IsNullOrEmpty(pd.Civ) || pd.PlayerId <= 0) continue;
			var player = _sim?.GetPlayerEntity(pd.PlayerId);
			if (player != null) player.Civ = pd.Civ;
		}
	}

	/// <summary>加载 skirmish 地图的 XML 实体并生成（占位模板经 SkirmishReplacer 按槽位文明
	/// 替换后走正常 scenario 生成路径）。仅当地图 XML 存在且含 skirmish/ 占位实体时返回 true——
	/// 普通 scenario 地图（实体全是确定模板）不走路径，保持既有沙盒生成不变。
	/// mapRelPathNoExt 例 "maps/skirmishes/acropolis_bay_2p"。</summary>
	public bool LoadSkirmishScenario(string dataRoot, string mapRelPathNoExt)
		=> LoadMapScenario(dataRoot, mapRelPathNoExt) != null;

	/// <summary>skirmish/ 占位实体的文明替换——原版 InitGame.js 广播 MT_SkirmishReplace 的移植：
	/// 世界构建完成、首回合开始前，对全部 skirmish/ 占位实体按属主文明改写模板名（查 civ JSON
	/// SkirmishReplacements 表 → 占位模板 general 兜底 → 都无则销毁）。civ 解析：玩家实体
	/// PlayerComponent.Civ（槽位表注入）优先，场景 XML PlayerData.Civ 兜底；gaia(0) → 销毁；
	/// 查不到文明 → 保留占位（原版 if (!civ) return）。</summary>
	private void ApplySkirmishReplacements(ScenarioData scenario)
	{
		if (Templates == null) return;
		if (!scenario.Entities.Any(e => e.Template.StartsWith("skirmish/", StringComparison.Ordinal)))
			return;

		var replacer = new SkirmishReplacer(Templates,
			SkirmishReplacer.CivsRootFromTemplatesRoot(_templatesPath));
		var (replaced, destroyed) = replacer.Apply(scenario.Entities, pid =>
		{
			if (pid == 0) return "gaia";   // gaia 属主的占位 → 销毁（原版告警地图作者错误）
			string? civ = _sim?.GetPlayerEntity(pid)?.Civ;
			if (string.IsNullOrEmpty(civ))
				civ = scenario.Players.FirstOrDefault(p => p.PlayerId == pid)?.Civ;
			return string.IsNullOrEmpty(civ) ? null : civ;
		});
		ZeroAD.Sim.Diag.Log("Skirmish", $"civ-replaced {replaced} placeholder entities, destroyed {destroyed} " +
				 $"(no mapping for owner civ)");
	}

	private void SpawnScenarioEntities(ScenarioData scenario)
	{
		_scenarioUidMap.Clear();
		foreach (var child in _decorativeNodes)
			child.QueueFree();
		_decorativeNodes.Clear();

		ApplySkirmishReplacements(scenario);

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
				ZeroAD.Sim.Diag.Warn("Sim", $"SimBridge: spawn failed for '{def.Template}': {ex.Message}");
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
		// 投放点只有带 <ResourceDropsite> 的建筑(CC/storehouse/farmstead/dock)该挂;
		// 此前无条件给所有建筑挂,房屋也被当投放点 → 村民把资源送回房屋。按 IsDropsite
		// 过滤,对齐 EntityAssembler 装配路径(SimBridge.cs:1825)。
		if (stats?.IsDropsite == true)
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
			Undeletable = stats?.Undeletable == true,
			Classes = stats?.GetClassList() ?? new List<string> { "Building" }
		};
		_sim.AddComponent(entity, identity);
		_sim.AddComponent(entity, new HealthComponent
		{ Current = stats?.MaxHealth ?? 500, Max = stats?.MaxHealth ?? 500,
			RegenRate = stats?.HealthRegenRate ?? 0f, IdleRegenRate = stats?.HealthIdleRegenRate ?? 0f });

		// Population-providing buildings (House etc.) carry their bonus as data so pop-limit
		// accounting is data-driven via RecomputePlayerPopBonus rather than hardcoded per-template.
		if (stats != null && stats.PopulationBonus > 0)
			_sim.AddComponent(entity, new PopulationComponent { Bonus = stats.PopulationBonus });

		if (def.Player > 0)
		{
			_sim.AddComponent(entity, new OwnershipComponent { PlayerId = def.Player });
			_range?.RefreshFromComponents(entity);
		}

		var pos = _sim.QueryInterface<PositionComponent>(entity);
		if (pos != null)
		{
			var sp = new FixedVector3D(Fixed.FromFloat(def.X), SimSystem.TerrainHeight(Fixed.FromFloat(def.X), Fixed.FromFloat(def.Z)), Fixed.FromFloat(def.Z));
			pos.Position = sp;
			_sim.NotifyPositionChanged(entity,
				new FixedVector2D(Fixed.Zero, Fixed.Zero),
				new FixedVector2D(sp.X, sp.Z));
		}

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
		// 墙体(Wall 类):控制组 = 玩家墙组——同玩家墙件互不阻挡(拼链段搭进塔楼;
		// 对齐原版 control group 语义),Placement 校验同组豁免(执行端同款)。
		if (stats != null && stats.GetClassList().Contains("Wall") && def.Player > 0)
			obstruction.ControlGroup = ObstructionComponent.PlayerWallGroup(def.Player);
		_sim.AddComponent(entity, obstruction);
		_sim.AddComponent(entity, new BuildRestrictionsComponent
		{
			PlacementType = BuildPlacementType.Land,
			Category = stats?.Category ?? "Building",
			Territory = stats?.BuildRestrictionsTerritory ?? "",
		});
		obstruction.EnsureRegistered();
		// 城门(原版 Gate.js):GateComponent + 默认未锁(可通行 → 阻挡失活)。
		if (stats != null && stats.HasGate)
		{
			_sim.AddComponent(entity, new GateComponent());
			obstruction.SetActive(false);
		}

		// 攻击组件(CC/箭塔/防御塔等有 Attack 的建筑):此前 SpawnScenarioBuilding 不装
		// AttackComponent → QueryInterface<AttackComponent> 返 null → 选中射程圈分支
		// (Main.cs:1876)不进,CC 攻击范围圈永不显示,且建筑也不能反击/防御。
		// 对齐 SpawnUnit 的装配(line 1863)+ EntityAssembler.cs:113。
		if (stats != null && (stats.AttackDamage > 0 || stats.AttackCaptureStrength > Fixed.Zero))
		{
			var bldgAtkDmg = new DamageBlock();
			if (stats.AttackHack > 0) bldgAtkDmg.Amounts[DamageType.Hack] = stats.AttackHack;
			if (stats.AttackPierce > 0) bldgAtkDmg.Amounts[DamageType.Pierce] = stats.AttackPierce;
			if (stats.AttackCrush > 0) bldgAtkDmg.Amounts[DamageType.Crush] = stats.AttackCrush;
			var bldgAtk = new AttackComponent
			{
				Damage = bldgAtkDmg,
				Range = stats.AttackRange > 0 ? stats.AttackRange : 3.0f,
				Rate = stats.AttackRate > 0 ? stats.AttackRate : 1.0f,
				IsRanged = stats.AttackIsRanged,
				HasRangeOverlay = stats.HasRangeOverlay,
				CaptureStrength = stats.AttackCaptureStrength,
				CaptureRange = stats.AttackCaptureRange,
				CaptureRate = stats.AttackCaptureRate,
				CaptureRestrictedClasses = stats.AttackCaptureRestrictedClasses,
				PreferredClasses = stats.AttackPreferredClasses,
				PhysicalRestrictedClasses = stats.AttackPhysicalRestrictedClasses,
			};
			_sim.AddComponent(entity, bldgAtk);
		}

		// BuildingAI(原版:防御塔/CC 自动放箭):有攻击件 + 模板带 BuildingAI 段才装。
		if (stats != null && stats.HasBuildingAI
			&& _sim.QueryInterface<AttackComponent>(entity) != null)
		{
			_sim.AddComponent(entity, new BuildingAIComponent
			{
				DefaultArrowCount = stats.DefaultArrowCount,
				MaxArrowCount = stats.MaxArrowCount,
				GarrisonArrowMultiplier = stats.GarrisonArrowMultiplier,
				GarrisonArrowClasses = stats.GarrisonArrowClasses,
			});
		}

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
		// templateName 必须下传——SpawnUnit 的视觉兜底是"无模板=士兵模型",
		// 不传则村民/支援单位全部渲染成 spearman(skirmish 起始单位全是兵)。
		var entity = SpawnUnit(def.X, def.Z, isVillager, isSoldier, stats, templateName: def.Template);

		var identity = _sim.QueryInterface<IdentityComponent>(entity);
		if (identity != null)
		{
			identity.TemplateName = def.Template;
			identity.Classes = stats?.GetClassList() ?? identity.Classes;
		}

		if (def.Player > 0)
		{
			_sim.AddComponent(entity, new OwnershipComponent { PlayerId = def.Player });
			_range?.RefreshFromComponents(entity);
		}

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
		// gaia 动物(fauna + 有 Health)走移动装配(游荡/逃跑/反击;死后转尸体)。
		// 树/石/矿仍走下方静态路径。
		if (stats is { HasHealth: true, ResourceAmount: > 0 }
			&& def.Template.StartsWith("gaia/fauna", StringComparison.OrdinalIgnoreCase))
		{
			var faun = SpawnUnit(def.X, def.Z,
				isVillager: false,
				isSoldier: stats.AttackDamage > 0,
				stats: stats,
				isStructure: false,
				isResource: true,
				isFauna: true,
				templateName: def.Template);
			if (_entityNodes.TryGetValue(faun, out var faunNode) && def.OrientationY != 0f)
				faunNode.Rotation = new Vector3(0, def.OrientationY, 0);
			EntityAssembler.RegisterForLos(_sim, faun, def.Template, stats);
			return faun;
		}

		var entity = _sim.CreateEntity();
		_sim.AddComponent(entity, new PositionComponent());

		if (stats != null && stats.ResourceAmount > 0)
		{
			var supply = new ResourceSupply
			{
				Amount = stats.ResourceAmount,
				MaxAmount = stats.ResourceAmount,
				Type = stats.ResourceType,
				KillBeforeGather = stats.KillBeforeGather,
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
			Undeletable = stats?.Undeletable == true,
			Classes = stats?.GetClassList() ?? new List<string>()
		};
		_sim.AddComponent(entity, identity);
		// 原版数据:树木/岩石无 Health(不可攻击),fauna 有(可猎)。9999 硬编码让树
		// 也有了血条 → 悬停树出剑/可攻击树,与原版相悖。只给模板真声明 <Health> 的装。
		if (stats != null && stats.HasHealth)
			_sim.AddComponent(entity, new HealthComponent
			{ Current = stats.MaxHealth, Max = stats.MaxHealth,
				RegenRate = stats.HealthRegenRate, IdleRegenRate = stats.HealthIdleRegenRate });

		var pos = _sim.QueryInterface<PositionComponent>(entity);
		if (pos != null)
		{
			var sp = new FixedVector3D(Fixed.FromFloat(def.X), SimSystem.TerrainHeight(Fixed.FromFloat(def.X), Fixed.FromFloat(def.Z)), Fixed.FromFloat(def.Z));
			pos.Position = sp;
			_sim.NotifyPositionChanged(entity,
				new FixedVector2D(Fixed.Zero, Fixed.Zero),
				new FixedVector2D(sp.X, sp.Z));
		}

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
		SpawnDecorative(def.Template, def.X, def.Z, def.OrientationY);
	}

	/// <summary>纯视觉装饰物(actor|/rmgen 装饰实体):不进 sim,只摆 actor 节点。
	/// template 为去掉 actor| 前缀后的 actor 模板名(经 ModelLibrary 解析)。</summary>
	public void SpawnDecorative(string template, float x, float z, float yaw = 0f)
	{
		bool isTree = template.Contains("tree", StringComparison.OrdinalIgnoreCase);
		var color = isTree ? new Color(0.15f, 0.45f, 0.12f) : new Color(0.35f, 0.55f, 0.2f);

		Node3D? node = ModelLibrary.InstantiateForTemplate(template, x, z, color);
		if (node != null)
			node.Rotation = new Vector3(0, yaw, 0);
		else
			node = MakeFallbackBox(template, x, z, color);

		UnitContainer.AddChild(node);
		_decorativeNodes.Add(node);
	}

	private static MeshInstance3D MakeFallbackBox(ScenarioEntityDef def, Color color)
		=> MakeFallbackBox(def.Template, def.X, def.Z, color, def.OrientationY);

	private static MeshInstance3D MakeFallbackBox(string template, float x, float z, Color color, float yaw = 0f)
	{
		var box = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.5f, 2f, 1.5f) } };
		box.MaterialOverride = new StandardMaterial3D { AlbedoColor = color };
		float h = TerrainHeightService.Sample(x, z);
		box.Position = new Vector3(x, h, z);
		box.Rotation = new Vector3(0, yaw, 0);
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
		// 击杀贴地血斑(原版 blood_*.xml 的 decal 语义:命中迸溅在池里,
		// 残留血斑在本系统——45s 消融后回收)。
		if (isKill) _decals?.Spawn(target.Position);
		// 攻城/建筑命中贴花(原版 eyecandy/impact_decal 的 decal 语义:
		// 建筑被命中/被毁时落弹坑贴花,90s 消融;比血斑大、消融更久)。
		var targetIdentity = _sim?.QueryInterface<ZeroAD.Sim.Components.IdentityComponent>(e.Target);
		if (targetIdentity != null && targetIdentity.IsBuilding)
			_decals?.SpawnImpact(target.Position);
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
		// Death spiral 防护:每帧最多跑 5 个 sim tick。当单个 tick 耗时 > SimTickRate(如
		// 89 个单位同时战斗 + 寻路),accumulator 会持续增长,while 循环永远追不上 →
		// 画面冻死。封顶后接受 sim 减速(画面慢但能动)而非冻死。
		int ticksThisFrame = 0;
		while (_simAccumulator >= SimTickRate && ticksThisFrame < 5)
		{
			// Turn barrier: in lockstep the sim advances only when the bundle for the
			// upcoming turn has arrived (always true in standalone — local bundles are
			// produced synchronously). While stalled, rendering continues; only the
			// sim pauses.
			if (!_netTurn.CanAdvanceTurn())
			{
				if (!_stallLogged)
				{
					ZeroAD.Sim.Diag.Log("Lockstep", $"waiting for turn {_netTurn.CurrentTurn} bundle");
					_stallLogged = true;
				}
				break;
			}
			_stallLogged = false;
			_simAccumulator -= SimTickRate;
			// TEMP-DIAG(corinthian 卡死定位):首个 tick 前后打点,定位卡在哪一段。
			var _tickSw = System.Diagnostics.Stopwatch.StartNew();
			if (_netTurn.CurrentTurn < 30) ZeroAD.Sim.Diag.Log("Diag", $"turn{_netTurn.CurrentTurn} TickSimulation start entities={_sim.AllEntities.Count}");
			TickSimulation((float)SimTickRate);
			long tSimMs = _tickSw.ElapsedMilliseconds;
			if (_netTurn.CurrentTurn < 30) ZeroAD.Sim.Diag.Log("Diag", $"turn{_netTurn.CurrentTurn} TickSimulation done; TickAI start");
			// AI 大脑内核驻留(Phase 2):遍历 AllEntities 推进 AIComponent.Tick。AI 是对世界的
			// "反应"而非世界推进的一部分,故独立于 TickSimulation;Tick 内经 SubmitAiCommand 入
			// currentTurn+commandDelay 本地通道,与人手同路径同延迟,各端确定性同生成。
			TickAI();
			long tAiMs = _tickSw.ElapsedMilliseconds - tSimMs;
			_profSimMs += tSimMs; _profAiMs += tAiMs;
			if (_netTurn.CurrentTurn < 30) ZeroAD.Sim.Diag.Log("Diag", $"turn{_netTurn.CurrentTurn} TickAI done");
			_netTurn.AdvanceTurn();
			ticksThisFrame++;
			_profTicks++;
		}
		// 达到 tick 上限但 accumulator 仍有余量:丢弃剩余,避免下帧爆发(death spiral 防护)。
		if (ticksThisFrame >= 5)
			_simAccumulator = 0;
		// SyncVisuals 只在 sim 推进过的帧跑:7400 实体全量 QueryInterface+采样+插值记录
		// 是纯 tick 数据,60fps 帧帧跑纯属浪费(Corinthian 4fps 的主要成本之一);
		// 渲染帧的位置平滑由下方插值器单独负责,与同步解耦。
		var _prof = System.Diagnostics.Stopwatch.StartNew();
		long tSync = 0, tInterp = 0, tShadow = 0;
		if (ticksThisFrame > 0 || _forceFirstSync)
		{
			_forceFirstSync = false;
			SyncVisuals();
			tSync = _prof.ElapsedMilliseconds;
		}
		// 渲染插值:用 tick 余数作 alpha,在两次 tick 之间平滑单位位置(消除 10Hz 瞬移)。
		_interpolator.SetAlpha((float)(_simAccumulator / SimTickRate));
		_interpolator.ApplyRenderPositions();
		tInterp = _prof.ElapsedMilliseconds - tSync;
		// 阴影代理跟拍(插值之后,影子与平滑后的视觉同帧;迷雾隐藏单位经 SyncFrom 关 Visible 不漏影)。
		foreach (var kvp in _shadowProxies)
			if (kvp.Key.IsInsideTree())
				ShadowProxyManager.SyncFrom(kvp.Value, kvp.Key);
		tShadow = _prof.ElapsedMilliseconds - tSync - tInterp;
		// TEMP-PROF:每秒聚合打印各段耗时,定位 4fps 大头。
		_profSync += tSync; _profInterp += tInterp; _profShadow += tShadow; _profFrames++;
		if (_profTimer.ElapsedMilliseconds >= 1000 && _profFrames > 0)
		{
			var top = string.Join(" ", _phaseMs.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key}={kv.Value}ms"));
			var uai = $"uai(disp={ZeroAD.Sim.Components.UnitAIComponent.ProfDispatch / 10000} scan={ZeroAD.Sim.Components.UnitAIComponent.ProfScan / 10000} fsm={ZeroAD.Sim.Components.UnitAIComponent.ProfFsm / 10000} calls={ZeroAD.Sim.Components.UnitAIComponent.ProfCalls})";
			var orders = string.Join(" ", ZeroAD.Sim.Components.UnitAIComponent.ProfOrderMs
				.OrderByDescending(kv => kv.Value).Take(4)
				.Select(kv => $"{kv.Key}={kv.Value / 10000}ms/{ZeroAD.Sim.Components.UnitAIComponent.ProfOrderCount.GetValueOrDefault(kv.Key)}x"));
			var pf = $"pf(hit={ZeroAD.Sim.Components.PathfinderComponent.ProfHits} miss={ZeroAD.Sim.Components.PathfinderComponent.ProfMisses} cost={ZeroAD.Sim.Components.PathfinderComponent.ProfTicks / 1000000}ms reach={ZeroAD.Sim.Pathfinding.LongPathfinder.ProfReachTicks / 1000000}ms search={ZeroAD.Sim.Pathfinding.LongPathfinder.ProfSearchTicks / 1000000}ms)";
			var hq = $"hq(ev={ZeroAD.Sim.AI.Petra.Headquarters.ProfEvents} econ={ZeroAD.Sim.AI.Petra.Headquarters.ProfEcon} exp={ZeroAD.Sim.AI.Petra.Headquarters.ProfExpansion} bld={ZeroAD.Sim.AI.Petra.Headquarters.ProfBuild} bases={ZeroAD.Sim.AI.Petra.Headquarters.ProfBases} atk={ZeroAD.Sim.AI.Petra.Headquarters.ProfAttack} def={ZeroAD.Sim.AI.Petra.Headquarters.ProfDefense} q={ZeroAD.Sim.AI.Petra.Headquarters.ProfQueues})";
			ZeroAD.Sim.Diag.Log("Prof", $"frames={_profFrames} ticks={_profTicks} sim={_profSimMs}ms ai={_profAiMs}ms sync={_profSync}ms interp={_profInterp}ms shadow={_profShadow}ms | {top} | {uai} | {orders} | {pf} | {hq}");
			_profSync = _profInterp = _profShadow = _profFrames = 0;
			_profSimMs = _profAiMs = _profTicks = 0;
			ZeroAD.Godot.SkeletalAnim.ManualAnimator.FrameCostMs = 0;
			ZeroAD.Sim.Components.UnitAIComponent.ProfDispatch = ZeroAD.Sim.Components.UnitAIComponent.ProfScan =
				ZeroAD.Sim.Components.UnitAIComponent.ProfFsm = ZeroAD.Sim.Components.UnitAIComponent.ProfCalls = 0;
			ZeroAD.Sim.Components.UnitAIComponent.ProfOrderMs.Clear();
			ZeroAD.Sim.Components.UnitAIComponent.ProfOrderCount.Clear();
			ZeroAD.Sim.Components.PathfinderComponent.ProfHits = ZeroAD.Sim.Components.PathfinderComponent.ProfMisses =
				ZeroAD.Sim.Components.PathfinderComponent.ProfTicks = 0;
			ZeroAD.Sim.Pathfinding.LongPathfinder.ProfReachTicks = ZeroAD.Sim.Pathfinding.LongPathfinder.ProfSearchTicks = 0;
			ZeroAD.Sim.AI.Petra.Headquarters.ProfEvents = ZeroAD.Sim.AI.Petra.Headquarters.ProfEcon =
				ZeroAD.Sim.AI.Petra.Headquarters.ProfExpansion = ZeroAD.Sim.AI.Petra.Headquarters.ProfBuild =
				ZeroAD.Sim.AI.Petra.Headquarters.ProfBases = ZeroAD.Sim.AI.Petra.Headquarters.ProfAttack =
				ZeroAD.Sim.AI.Petra.Headquarters.ProfDefense = ZeroAD.Sim.AI.Petra.Headquarters.ProfQueues = 0;
			_phaseMs.Clear();
			_profTimer.Restart();
		}
	}

	private bool _forceFirstSync = true;
	private long _profSync, _profInterp, _profShadow;
	private int _profFrames;
	private long _profSimMs, _profAiMs;
	private int _profTicks;
	private readonly System.Diagnostics.Stopwatch _profTimer = System.Diagnostics.Stopwatch.StartNew();

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
		T("dead", () => RemoveDeadEntities());
		T("motions", () => TickUnitMotions(dt));
		// Unit pushing (ports CCmpUnitMotionManager::Move/Push): after every unit has stepped,
		// push overlapping pairs apart so rallied/converging units spread into a visible cluster
		// instead of stacking on one point (which made only one render). Pure sim, lockstep-safe.
		T("separation", () => UnitSeparation.Separate(_sim, Fixed.FromFloat(dt)));
		// 视野重算在 TickUnitAI 之前:单位移动后立即可见性更新,扫描时看到最新结果。
		// 此前 UpdateVisibilityData 在 tick 末尾跑 → 扫描用上一帧的可见性 → 攻击有 1 tick 延迟。
		// 末尾保留第二次调用(拾取驻军/炮塔的位置变更)。
		T("los1", () => _range.UpdateVisibilityData());
		T("unitai", () => TickUnitAI(dt));
		// gather 旧驱动(TickGatherers)已退役:采集周期由 UnitAI 的 GATHER FSM 子树驱动
		// (内核自洽、无头测试同路);双驱动曾对同一 supply 重复结算。
		T("attack", () => TickAttackers(dt));
		T("buildingai", () => TickBuildingAI(dt));
		T("build", () => TickBuilders(dt));
		T("prod", () => TickProductionQueues(dt));
		T("found", () => TickFoundations(dt));
		T("research", () => TickResearch(dt));
		// 光环:每 tick 应用/移除(range diff + global/player reqTech 门控)。放 TickResearch 后、
		// ReapplyVisionScopeAll 前,使 vision aura 的修正值本轮即被 LOS 重算吃到。
		T("auras", () => TickAuras(dt));
		// 领土衰减(对齐原版 TerritoryDecay/Capturable 的 1s 定时器,本处每回合 0.1s×rate):
		// 先刷新 decaying/blink 状态(读本周期的领土网格),再让 Capturable 抽干/恢复 CP。
		// 放 UpdateVisibilityData 前:翻面触发的 OwnerChanged 本周期即被 LOS 重算吃到。
		T("territory", () => TickTerritoryDecay(dt));
		// 驻军持有者:BuffHeal 每秒回血(原版 1s HealTimeout)+ EjectHealth 低血逐出。
		// 放 UpdateVisibilityData 前:逐出回世界的单位本周期即被 LOS 重算吃到。
		T("garrison", () => TickGarrisonHolders(dt));
		// 炮塔跟拍(原版 Position.SetTurretParent 的引擎联动):在点单位锁到持有者
		// 位置+旋转偏移。放 UpdateVisibilityData 前:随行位移本周期即被 LOS 重算吃到。
		T("turrets", () => TickTurrets(dt));
		// 资源涓流(原版 ResourceTrickle 定时器的回合制近似:奇观/牲口棚等按间隔发资源)。
		T("trickle", () => TickResourceTrickles(dt));
		T("closure", () => TickGameplayClosure(dt));
		// 状态效果(原版 StatusEffectsReceiver 定时器):周期伤害/捕获经 DelayedDamage
		// 本回合结算(排在其 TickPending 前),时限到撤修饰。
		T("status", () => TickStatusEffects(dt));
		// Vision range through the modifiers pipeline: tech/aura changes re-cover seer
		// circles in the LOS grid. Runs every turn (after research completes) so all
		// players' ranges stay fresh without a research-completion hook per player.
		T("visionrange", () => ValueModificationApplier.ReapplyVisionRangeAll(_sim, _range));
		// Settle any damage whose delay elapsed this turn, then advance the delay clock.
		T("damage", () => { _sim.DelayedDamage.TickPending(_sim); _sim.DelayedDamage.AdvanceTurn(); });
		// Conquest victory check — runs after dead entities are removed so the RangeManager
		// index reflects the current survivors.
		T("victory", () => _sim.TickVictory());
		// goal Delay 计时器(原版 goal Delay 语义;SimBridge 每回合驱动)。
		T("tutorial", () => Tutorial?.Tick(dt));
		// Fog-of-war: recompute per-player visibility for whatever changed this turn
		// (moved/placed/destroyed seers, ownership flips). Fires VisibilityChanged, which
		// drives Fogging/Mirage bookkeeping and presentation-layer show/hide. Cheap no-op
		// when nothing moved.
		T("los2", () => _range.UpdateVisibilityData());
	}

	// TEMP-PROF:逐阶段计时(tick 内哪个阶段吃掉秒级时间)。
	private readonly Dictionary<string, long> _phaseMs = new();
	private readonly System.Diagnostics.Stopwatch _phaseSw = new();
	private void T(string name, System.Action fn)
	{
		_phaseSw.Restart();
		// 单阶段异常不得截断整个 tick(此前 T("unitai") 抛 FSM 异常 → 其后的
		// foundations/attack 等全不跑,完工地基永远不换建筑)。记录并继续。
		try { fn(); }
		catch (System.Exception ex) { ZeroAD.Sim.Diag.Err("Sim", $"tick phase {name} failed: {ex.Message}"); }
		_phaseMs[name] = _phaseMs.GetValueOrDefault(name) + _phaseSw.ElapsedMilliseconds;
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
		_floraBatch?.Clear();   // 合批 MultiMesh 一并清(实体经下方 CreateVisualFor 重入批)
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

		ZeroAD.Sim.Diag.Log("RebuildAllVisuals", $"recreated {_entityNodes.Count} visual nodes");
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
		ZeroAD.Sim.Diag.Log("RebuildSpatialIndexesAfterLoad", $"re-registered obstructions + repopulated range index");
	}

	private void OnSimEntityDestroyed(EntityId entity)
	{
		// 死亡音效(原版 Sound.js:实体销毁时播模板 death 组;模板查询须在节点释放前)。
		var identity = _sim?.QueryInterface<IdentityComponent>(entity);
		if (identity != null && !string.IsNullOrEmpty(identity.TemplateName))
			AudioManager.PlayUnitEvent(Templates, identity.TemplateName, "death");
		if (_entityNodes.TryGetValue(entity, out var node))
		{
			node.QueueFree();
			_entityNodes.Remove(entity);
		}
		_floraBatch?.Remove(entity);   // 合批实体:回收 MultiMesh 槽位(无锚点时 no-op)
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
			if (health == null || !health.IsDead) continue;
			// 尸体已转换的不再处理(每 tick 全表扫描,IsDead 恒真)。
			if (_sim.QueryInterface<CorpseComponent>(entity) != null) continue;
			// gaia 动物(killBeforeGather 无主资源):死亡不销毁,转尸体供采集(原版行为)。
			var deadSupply = _sim.QueryInterface<ResourceSupply>(entity);
			var deadOwner = _sim.QueryInterface<OwnershipComponent>(entity);
			if (deadSupply != null && deadSupply.KillBeforeGather && deadOwner == null)
			{
				ConvertToCorpse(entity);
				continue;
			}
			{                var owner = deadOwner;
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
				// 死亡自爆(DeathDamage.js:OnDied → CauseDeathDamage):销毁前结算,
				// 否则位置/阻挡已拆,溅射找不到源。
				_sim.QueryInterface<DeathDamageComponent>(entity)?.CauseDeathDamage(_sim);
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

	/// <summary>动物死亡 → 尸体(原版:killBeforeGather 的 gaia 死亡不销毁,转尸体供采集)。
	/// 挂 CorpseComponent(死亡清扫/tick 停摆标记),停 UnitAI 与动画;实体保留
	/// Position/Identity/ResourceSupply——采完肉(Amount=0)由既有枯竭路径销毁。</summary>
	private void ConvertToCorpse(EntityId entity)
	{
		_sim.AddComponent(entity, new CorpseComponent());
		_sim.QueryInterface<UnitAIComponent>(entity)?.OnCorpseConverted(_sim);
		var identity = _sim.QueryInterface<IdentityComponent>(entity);
		if (identity != null) identity.IsUnit = false;
		// 视觉:播 death 动画并定格(从 _animators 摘除,走位动画循环不再驱动它)。
		if (_animators.Remove(entity, out var anim))
		{
			if (anim.HasState("death")) anim.Play("death");
			// 播完定格(表现层计时器;循环播放会反复倒下)。节点销毁则跳过。
			var animRef = anim;
			GetTree().CreateTimer(1.2).Timeout += () =>
			{
				if (GodotObject.IsInstanceValid(animRef)) animRef.SetProcess(false);
			};
		}
		_animState.Remove(entity);
		_lastPos.Remove(entity);
		_interpolator.Remove(entity);
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

	private void TickAttackers(float dt)
	{
		foreach (var entity in GetAllEntitiesSnapshot())
		{
			var attack = _sim.QueryInterface<AttackComponent>(entity);
			attack?.Tick(dt, _sim);
		}
	}

	/// <summary>建筑自动防御驱动(原版 BuildingAI 的 Timer 周期;1s 节流索敌在内)。</summary>
	private void TickBuildingAI(float dt)
	{
		foreach (var entity in GetAllEntitiesSnapshot())
		{
			var bai = _sim.QueryInterface<BuildingAIComponent>(entity);
			bai?.Tick(dt, _sim);
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

	private void TickResourceTrickles(float dt)
	{
		foreach (var entity in GetAllEntitiesSnapshot())
		{
			var trickle = _sim.QueryInterface<ResourceTrickleComponent>(entity);
			trickle?.Tick(_sim, dt);
		}
	}

	/// <summary>P0 补齐件 tick:Upkeep 扣费 / AutoBuildable 自建 / AlertRaiser 时基 /
	/// AttackDetection 抑制表过期 / BattleDetection 战区衰减。</summary>
	private void TickGameplayClosure(float dt)
	{
		foreach (var entity in GetAllEntitiesSnapshot())
		{
			_sim.QueryInterface<UpkeepComponent>(entity)?.Tick(_sim, dt);
			_sim.QueryInterface<AutoBuildableComponent>(entity)?.Tick(_sim, dt);
			_sim.QueryInterface<AlertRaiserComponent>(entity)?.Tick(dt);
			_sim.QueryInterface<AttackDetectionComponent>(entity)?.Tick(dt);
			_sim.QueryInterface<BattleDetectionComponent>(entity)?.Tick(dt);
			// Health 再生(原版 Health.js RegenTimer:建筑 5 HP/s 自愈等)。
			_sim.QueryInterface<HealthComponent>(entity)?.TickRegen(_sim, dt);
		}
		// 易物价差回落(原版 Barter.ProgressTimeout:每 5s 向 0 收敛)。
		BarterSystem.TickRestore(dt);
	}

	private void TickStatusEffects(float dt)
	{
		foreach (var entity in GetAllEntitiesSnapshot())
		{
			var receiver = _sim.QueryInterface<StatusEffectsReceiverComponent>(entity);
			receiver?.Tick(_sim, dt);
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
					if (node.HasMeta("previewNode"))
					{
						// 建造预览:真实建筑随进度从地下升起(原版
						// GetConstructionProgressOffset = (progress-1)×模型高)。
						var preview = (Node3D)node.GetMeta("previewNode");
						float h = (float)node.GetMeta("previewHeight").AsDouble();
						float f = Mathf.Clamp(foundation.BuildFraction, 0f, 1f);
						preview.Position = new Vector3(0, -h * (1f - f), 0);

						// 工人进场(原版 Commit)→ 显示脚手架。
						if (foundation.NumBuilders > 0 && node.HasMeta("scaffoldNode"))
						{
							var scaffold = (Node3D)node.GetMeta("scaffoldNode");
							if (!scaffold.Visible)
							{
								scaffold.Visible = true;
								ZeroAD.Sim.Diag.Log("Fnd",
									$"scaffold shown: entity={entity.Value} frac={foundation.BuildFraction:F2}");
							}
						}
					}
					else if (node is MeshInstance3D mi && mi.Mesh is BoxMesh bm)
					{
						// 幽灵盒兜底:透明度渐升(旧行为)。
						var mat = new StandardMaterial3D();
						float alpha = 0.3f + 0.7f * foundation.BuildFraction;
						mat.AlbedoColor = new Color(0.6f, 0.5f, 0.4f, alpha);
						mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
						bm.Material = mat;
					}

					// 建造进度条(原版地基血条随建造涨;整百分点变化才重建网格)。
					if (_foundationBars.TryGetValue(entity, out var bar))
					{
						int pct = (int)(100f * foundation.BuildFraction);
						if (!bar.HasMeta("pct") || bar.GetMeta("pct").AsInt32() != pct)
						{
							var parent = bar.GetParent();
							var barPos = bar.Position;
							bar.QueueFree();
							var nb = SelectionRing.CreateHealthBar(pct / 100f);
							nb.Position = barPos;
							nb.SetMeta("pct", pct);
							parent.AddChild(nb);
							_foundationBars[entity] = nb;
						}
					}
				}
				continue;
			}

			_foundationBars.Remove(entity);   // 完工:条目清除(条随节点释放)
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
			// 完工继承 foundation 朝向(原版 Transform.js:57-58 把 rot.y 拷给新实体)。
			// 此前 OrientationY 留默认 0,完工建筑总是朝北,丢失玩家放置角度。
			float yaw = pos?.Rotation.Y.ToFloat() ?? 0f;
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
				Player = owner?.PlayerId ?? 1,
				OrientationY = yaw
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
			// ObstructionComponent.EnsureRegistered). Refresh the pathfinder's navcell grid:
			// snapshot diff → only the changed region patches (P1; zero-cost when nothing moved,
			// per-tick safe). Fall back to full RebuildGrid if the grid isn't built yet.
			_pathfinder.RefreshObstructions();

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
			// 队列里还有活(如墙链的下一段)不算空闲——别拽走(此前完工瞬间
			// builder.Target 刚好为空,被自动派去采集,queued 修复单全被顶掉)。
			var ai = _sim.QueryInterface<UnitAIComponent>(e);
			if (ai?.CurrentOrder != null) continue;
			// 走 UnitAI 订单(GATHER FSM 子树;旧直接设状态不经 FSM 已废弃)。
			ai?.Gather(nearest.Value);
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

		// Foundations (placed via SimCommandExecutor.ApplyBuild in the kernel):真实建筑
		// 模型沉地,随进度升起(原版建造动画)+ 头顶血条;模型缺失回退幽灵盒。
		bool isFoundation = _sim.QueryInterface<FoundationComponent>(e.Entity) != null;
		if (isFoundation)
		{
			CreateFoundationVisual(e.Entity, e.TemplateName, playerId);
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

	// 建造中地基的头顶血条(随完成释放;键=地基实体)。
	private readonly Dictionary<EntityId, MeshInstance3D> _foundationBars = new();

	/// <summary>地基视觉（对齐原版建造三段式）：
	/// ① FoundationActor（fndn_XxY：泥地贴花 + 矮石板 + 木桩砖堆）放地面不动；
	/// ② 工人进场（NumBuilders>0，原版 Foundation.js Commit → SelectAnimation("scaffold")）
	///    显示脚手架 prop（scaffold 变体，初始隐藏）；
	/// ③ 真实建筑作建造预览沉在地下，随进度升起（原版 CCmpPosition::
	///    GetConstructionProgressOffset = (progress-1)×模型高）。
	/// meta：previewNode/previewHeight/baseY/scaffoldNode，TickFoundations 读取。
	/// 模型缺失 → 旧幽灵盒兜底。建造尘粒子上游为粒子特效，本端无粒子渲染器，跳过。</summary>
	private void CreateFoundationVisual(EntityId entity, string template, int playerId)
	{
		Color color = GetPlayerColor(playerId);
		// 原版地基显示专门的 FoundationActor(矮地基+四角木桩,如 fndn_5x8.xml),不是
		// 完整建筑。优先取 VisualActor/FoundationActor;取不到才回退完整模板。
		string foundationActor = template;
		try
		{
			var node = _sim.Templates?.LoadTemplate(template);
			if (node?.GetChild("VisualActor")?.GetChild("FoundationActor")?.Value is { Length: > 0 } fa)
				foundationActor = fa;
		}
		catch { /* 取不到就回退完整模板,行为同前 */ }
		var visual = ModelLibrary.InstantiateForTemplate(foundationActor, 0, 0, color,
			Actors.Variation.VariationResolver.Scaffold);
		if (visual == null)
		{
			// 无模型(演员缺失/未知模板)→ 旧幽灵盒路径,表现不变。
			CreateVisualFor(entity, new Color(0.6f, 0.5f, 0.4f, 0.3f), 6f,
				isBuilding: true, isGhost: true, templateName: foundationActor);
			return;
		}

		var pos = _sim.QueryInterface<PositionComponent>(entity);
		float vx = pos?.Position.X.ToFloat() ?? 0;
		float vz = pos?.Position.Z.ToFloat() ?? 0;
		float baseY = TerrainHeightService.Sample(vx, vz);
		visual.Position = new Vector3(vx, baseY, vz);

		// 脚手架 prop 初始隐藏（未开工);工人进场后 TickFoundations 显示。
		var scaffold = FindChildByActorSuffix(visual, "construction/scaffold.xml");
		if (scaffold != null)
		{
			scaffold.Visible = false;
			visual.SetMeta("scaffoldNode", scaffold);
		}

		// 建造预览:真实建筑模型沉入地下(baseY - 模型高),随进度升起。
		if (!foundationActor.Equals(template, StringComparison.Ordinal))
		{
			var preview = ModelLibrary.InstantiateForTemplate(template, 0, 0, color);
			if (preview != null)
			{
				float h = ComputeLocalAabb(preview, Transform3D.Identity, null)?.Size.Y ?? 0f;
				if (h > 0.01f)
				{
					preview.Position = new Vector3(0, -h, 0);
					visual.AddChild(preview);
					visual.SetMeta("previewNode", preview);
					visual.SetMeta("previewHeight", h);
					visual.SetMeta("baseY", baseY);
				}
				else
					preview.QueueFree();
			}
		}

		// 头顶血条(原版地基建造中显示血量;TickFoundations 每 tick 刷新)。固定悬于
		// 地基模型上方 3m(原版地基矮平,无需按模型高算)。
		var bar = SelectionRing.CreateHealthBar(1f);
		bar.Position = new Vector3(0, 3f, 0);
		visual.AddChild(bar);
		_foundationBars[entity] = bar;

		visual.SetMeta("entityId", (int)entity.Value);
		UnitContainer.AddChild(visual);
		_entityNodes[entity] = visual;
		_entityCacheDirty = true;
	}

	/// <summary>按 actor 路径后缀找组合实例里的 prop 子树（LayerMeta.ActorPath 标记）。</summary>
	private static Node3D? FindChildByActorSuffix(Node node, string actorPathSuffix)
	{
		if (node is Node3D n3 && n3.HasMeta(Actors.Composition.LayerMeta.ActorPath) &&
			((string)n3.GetMeta(Actors.Composition.LayerMeta.ActorPath))
				.EndsWith(actorPathSuffix, StringComparison.Ordinal))
			return n3;
		foreach (var child in node.GetChildren())
		{
			var hit = FindChildByActorSuffix(child, actorPathSuffix);
			if (hit != null) return hit;
		}
		return null;
	}

	/// <summary>整树网格 AABB 并集(节点未入树也能算:沿局部 Transform 累积)。
	/// 地基升起行程用——必须覆盖组合场景的全部网格(主体+道具)。</summary>
	private static Aabb? ComputeLocalAabb(Node node, Transform3D xf, Aabb? acc)
	{
		var local = node is Node3D n3 ? xf * n3.Transform : xf;
		if (node is MeshInstance3D mi && mi.Mesh != null)
		{
			var box = local * mi.Mesh.GetAabb();
			acc = acc?.Merge(box) ?? box;
		}
		foreach (var child in node.GetChildren())
			acc = ComputeLocalAabb(child, local, acc);
		return acc;
	}

	/// <summary>首个 MeshInstance3D 子节点(深度优先;建筑模型多为容器节点)。</summary>
	private static MeshInstance3D? FindFirstMesh(Node node)
	{
		foreach (var child in node.GetChildren())
		{
			if (child is MeshInstance3D mi) return mi;
			var found = FindFirstMesh(child);
			if (found != null) return found;
		}
		return null;
	}

	public EntityId SpawnFromTemplate(string templateName, float x, float z, int playerId = 0)
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
		// gaia 动物(fauna + 有 Health):移动单位——带 UnitMotion/UnitAI(游荡/逃跑/反击),
		// 同时保留 ResourceSupply(死后转尸体供采集)。树/石无 Health,仍走静态资源。
		bool isFauna = isResource && stats != null && stats.HasHealth
			&& templateName.StartsWith("gaia/fauna", StringComparison.OrdinalIgnoreCase);

		var eid = SpawnUnit(x, z,
			isVillager: stats?.CanGather == true && !isFauna,
			isSoldier: stats?.AttackDamage > 0,
			stats: stats,
			isStructure: isStructure,
			isResource: isResource,
			isFauna: isFauna,
			templateName: templateName);

		// 属主(rmgen 玩家基地等;上游 ParseEntities:currEnt.playerID → Ownership)。
		// 与 SpawnScenarioUnit 同款:所有权落定后重注册 LOS/fogging(此前 ownerless 注册过
		// 一次,幂等),否则 MP 真雾下该实体永不入属主索引。
		if (playerId > 0)
		{
			_sim.AddComponent(eid, new OwnershipComponent { PlayerId = playerId });
			EntityAssembler.RegisterForLos(_sim, eid, templateName, stats);
		}
		return eid;
	}


	public EntityId SpawnUnit(float x, float z, bool isVillager = false, bool isSoldier = false,
		TemplateStats? stats = null, bool isStructure = false, bool isResource = false,
		bool isFauna = false, string? templateName = null)
	{
		var entity = _sim.CreateEntity();
		_sim.AddComponent(entity, new PositionComponent());
		// Motion + unit AI belong only to mobile units. Structures (Town Centre) and resources
		// (trees) are static — giving them UnitMotion made them move on right-click, and IsUnit
		// made them drag-selectable as if they were troops.动物(isFauna)是例外:有资源
		// 但可移动(游荡/逃跑/反击,死后才变静态尸体)。
		bool isMobile = !isStructure && (!isResource || isFauna);
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
			Undeletable = stats?.Undeletable == true,
			Classes = stats?.GetClassList() ?? new List<string>()
		};
		if (isSoldier && !identity.HasClass("CitizenSoldier"))
			identity.Classes.Add("CitizenSoldier");
		_sim.AddComponent(entity, identity);

		var motion = _sim.QueryInterface<UnitMotion>(entity);
		if (motion != null && stats != null)
			motion.Speed = Fixed.FromFloat(stats.WalkSpeed);

		// Resource node (tree/stone/metal): gatherable supply, nothing else.
		// 动物(isFauna)也带 supply(KillBeforeGather=true——须先猎杀,死后采尸体)。
		if (isResource)
		{
			int amt = stats?.ResourceAmount > 0 ? stats.ResourceAmount : 100;
			var supply = new ResourceSupply
			{
				Type = stats?.ResourceType ?? ResourceType.Wood,
				Amount = amt,
				MaxAmount = amt,
				KillBeforeGather = stats?.KillBeforeGather == true,
			};
			_sim.AddComponent(entity, supply);
			// SpecificType("food.meat"→meat)在 AddComponent 之后设——决定采集光标
			// (action-gather-meat 而非兜底的 tree)。漏设会让所有资源都显示伐木图标。
			if (!string.IsNullOrEmpty(stats?.ResourceTypeString))
				supply.SetTypeString(stats.ResourceTypeString);
		}

		// 动物行为接线(原版 template_unit_fauna):模板 stance(skittish 逃/passive-defensive
		// 反击/aggressive 主动)+ 游荡/进食/逃跑参数;视野(狼的索敌半径)按模板 Vision。
		if (isFauna)
		{
			var faAi = _sim.QueryInterface<UnitAIComponent>(entity);
			if (faAi != null && stats != null)
			{
				if (stats.RoamDistance > 0f) faAi.RoamDistance = stats.RoamDistance;
				if (stats.RoamTimeMax > 0f) { faAi.RoamTimeMin = stats.RoamTimeMin; faAi.RoamTimeMax = stats.RoamTimeMax; }
				if (stats.FeedTimeMax > 0f) { faAi.FeedTimeMin = stats.FeedTimeMin; faAi.FeedTimeMax = stats.FeedTimeMax; }
				if (stats.FleeDistance > 0f) faAi.FleeDistance = stats.FleeDistance;
				if (stats.DefaultStance.Length > 0) faAi.SetStance(stats.DefaultStance, _sim);
			}
			if (stats != null && stats.VisionRange > 0)
			{
				_sim.AddComponent(entity, new VisionComponent());
				_sim.QueryInterface<VisionComponent>(entity)!.Range = Fixed.FromInt(stats.VisionRange);
			}
		}

		// Structures train units and accept dropsite deliveries per their template (Civil Centre
		// has both). Without these a real-template TC couldn't train — the fallback SpawnBuilding
		// hardcodes them.
		if (isStructure && stats != null)
		{
			// Footprint(原版 selectable 选择框/集结落点/驻防出入位置的数据源;
			// 与 SpawnScenarioBuilding 同款——rmgen/sandbox 起始建筑此前没有,
			// 选择框只能回退固定值,偏小且与 C++ 不一致)。
			float fpSize = stats.FootprintSize0.ToFloat() is { } fp && fp > 0 ? fp : 12f;
			_sim.AddComponent(entity, new FootprintComponent
			{
				Shape = stats.FootprintShape == "circle" ? FootprintShape.Circle : FootprintShape.Square,
				Size0 = Fixed.FromFloat(fpSize),
				Size1 = Fixed.FromFloat(stats.FootprintSize1.ToFloat() is { } fp1 && fp1 > 0 ? fp1 : fpSize),
			});

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
				// Guard(原版 template_structure 基模板自带):建筑可被护卫(锚点/伤船)。
				_sim.AddComponent(entity, new GuardComponent());
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
				Rate = stats?.AttackRate ?? 1.0f,
				IsRanged = stats?.AttackIsRanged ?? false,
				// 选中射程圈开关(对齐 EntityAssembler.cs:119):CC/箭塔模板有
				// Attack/Ranged/RangeOverlay → 选中时画防御半径圈。此前漏设 → 永远 false
				// → CC 选中不显示射程圈。
				HasRangeOverlay = stats?.HasRangeOverlay ?? false
			};
			_sim.AddComponent(entity, atk);
			// Capture 攻击类型(对齐 EntityAssembler):AddComponent 后赋值。
			if (stats != null)
			{
				atk.CaptureStrength = stats.AttackCaptureStrength;
				atk.CaptureRange = stats.AttackCaptureRange;
				atk.CaptureRate = stats.AttackCaptureRate;
				atk.CaptureRestrictedClasses = stats.AttackCaptureRestrictedClasses;				atk.PreferredClasses = stats.AttackPreferredClasses;
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

		// BuildingAI(起始建筑走 SpawnUnit 路径;与 SpawnScenarioBuilding 同款装配——
		// 须在攻击件之后,组件依赖 AttackComponent)。
		if (stats != null && stats.HasBuildingAI
			&& _sim.QueryInterface<AttackComponent>(entity) != null)
		{
			_sim.AddComponent(entity, new BuildingAIComponent
			{
				DefaultArrowCount = stats.DefaultArrowCount,
				MaxArrowCount = stats.MaxArrowCount,
				GarrisonArrowMultiplier = stats.GarrisonArrowMultiplier,
				GarrisonArrowClasses = stats.GarrisonArrowClasses,
			});
		}

		var pos = _sim.QueryInterface<PositionComponent>(entity);
		if (pos != null)
		{
			// 从 (0,0)(OnEntityCreated 时的初始值)移到真实坐标,通知 RangeManager 更新
			// spatial subdivision + RangeEntityData + LOS。此前直接字段赋值不通知 →
			// subdivision 里实体永远在 (0,0) → ExecuteQuery 查不到 → 不攻击。
			var spawnPos = new FixedVector3D(Fixed.FromFloat(x), SimSystem.TerrainHeight(Fixed.FromFloat(x), Fixed.FromFloat(z)), Fixed.FromFloat(z));
			pos.Position = spawnPos;
			_sim.NotifyPositionChanged(entity,
				new FixedVector2D(Fixed.Zero, Fixed.Zero),
				new FixedVector2D(spawnPos.X, spawnPos.Z));
		}

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
		{
			var sp = new FixedVector3D(Fixed.FromFloat(x), SimSystem.TerrainHeight(Fixed.FromFloat(x), Fixed.FromFloat(z)), Fixed.FromFloat(z));
			pos.Position = sp;
			_sim.NotifyPositionChanged(entity,
				new FixedVector2D(Fixed.Zero, Fixed.Zero),
				new FixedVector2D(sp.X, sp.Z));
		}

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
		{
			var sp = new FixedVector3D(Fixed.FromFloat(x), SimSystem.TerrainHeight(Fixed.FromFloat(x), Fixed.FromFloat(z)), Fixed.FromFloat(z));
			pos.Position = sp;
			_sim.NotifyPositionChanged(entity,
				new FixedVector2D(Fixed.Zero, Fixed.Zero),
				new FixedVector2D(sp.X, sp.Z));
		}

		_obstructions.BlockCircle(x, z, 8f);
		CreateVisualFor(entity, new Color(0.6f, 0.5f, 0.4f), 8f, isBuilding: true);
		EntityAssembler.RegisterForLos(_sim, entity, _lastSpawnedTemplate, stats: null);
		return entity;
	}

	/// <summary>把 AI 大脑挂到指定玩家实体上(Phase 2 内核驻留)。Main.cs 在 InitWorld 后调用:
	/// 解析玩家实体 → new AIComponent → Configure(_sim, _netTurn)(AddComponent 前注入,与
	/// AuraComponent.Configure 同模式)→ AddComponent。TickAI 每回合推进;save/load 由
	/// SaveGameManager.Load 的 prepareComponent 重注入 Configure。</summary>
	public void AttachAi(int playerId, int difficulty = ZeroAD.Sim.AI.Petra.DifficultyLevel.Medium)
	{
		var playerEntity = _sim.GetPlayerEntityId(playerId);
		if (playerEntity == null) return;
		var ai = new AIComponent();
		ai.Configure(_sim, _netTurn, difficulty);
		if (_sharedState != null)
			ai.ConfigureSharedState(_sharedState);   // Petra HQ 主循环的激活钥匙
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

	/// <summary>攻击移动到坐标(原版 Ctrl+点击;UnitAI WalkAndFight 订单)。</summary>
	public void CommandAttackWalk(EntityId unit, float x, float z) =>
		SubmitCommand(NetCommand.AttackWalk(LocalPlayerId, unit.Value,
			Fixed.FromFloat(x), Fixed.FromFloat(z)));

	/// <summary>巡逻到坐标(原版 P+点击;起点=下单位置,自动往返)。</summary>
	public void CommandPatrol(EntityId unit, float x, float z) =>
		SubmitCommand(NetCommand.Patrol(LocalPlayerId, unit.Value,
			Fixed.FromFloat(x), Fixed.FromFloat(z)));

	/// <summary>护卫友方单位(原版 Guard 订单)。</summary>
	public void CommandGuard(EntityId guard, EntityId target) =>
		SubmitCommand(NetCommand.Guard(LocalPlayerId, guard.Value, target.Value));

	/// <summary>编队成员脱队(原版 RemoveFromFormation;部分选中个体命令前用)。
	/// 经 Formation 命令的 "remove" 负载,锁步安全。</summary>
	public void CommandFormationRemove(IReadOnlyList<EntityId> members)
	{
		if (members.Count == 0) return;
		SubmitCommand(NetCommand.FormationCmd(LocalPlayerId, "remove",
			members.Select(m => m.Value).ToList()));
	}

	/// <summary>修复建筑(原版 repair 命令:builder 修复/续建地基)。</summary>
	public void CommandRepair(EntityId builder, EntityId target) =>
		SubmitCommand(NetCommand.Repair(LocalPlayerId, builder.Value, target.Value));

	/// <summary>攻城器打包/解包(原版 pack/unpack 命令)。</summary>
	public void CommandPack(EntityId unit, bool unpack) =>
		SubmitCommand(NetCommand.Pack(LocalPlayerId, unit.Value, unpack));

	/// <summary>建筑升级(原版 upgrade 命令:哨塔→防御塔等;拆旧+原位放目标地基续建)。</summary>
	public void CommandUpgrade(EntityId building, EntityId? builder) =>
		SubmitCommand(NetCommand.Upgrade(LocalPlayerId, building.Value, builder?.Value ?? 0));

	/// <summary>城门锁切换(原版 gate 面板的 lock/unlock;阻挡活性+寻路网格联动)。</summary>
	public void CommandToggleGate(EntityId gate, bool locked) =>
		SubmitCommand(NetCommand.Gate(LocalPlayerId, gate.Value, locked));

	/// <summary>编队命令(原版 formation 面板):shape=null 解散,否则按阵型创建控制器。
	/// 实体列表经 TemplateName 载荷进锁步(原版 cmd entities 数组的 C# 形)。</summary>
	public void CommandFormation(IReadOnlyList<EntityId> entities, string shape)
	{
		var ids = new List<uint>();
		foreach (var e in entities) ids.Add(e.Value);
		SubmitCommand(NetCommand.FormationCmd(LocalPlayerId, shape, ids));
	}

	/// <summary>Issue a build order: cost charge + foundation spawn happen in the sim
	/// at the execution turn (SimCommandExecutor). `template` is the FULL template name.
	/// `angle` = yaw 弧度(原版 cmd.angle;默认 3π/4 = 135°,对齐 placement.js DEFAULT_ANGLE)。</summary>
	public void CommandBuild(EntityId builder, string template, float x, float z, float angle) =>
		SubmitCommand(NetCommand.Build(LocalPlayerId, builder.Value, template,
			Fixed.FromFloat(x), Fixed.FromFloat(z), Fixed.FromFloat(angle)));

	public void CommandSetRallyPoint(EntityId building, EntityId? target) =>
		SubmitCommand(NetCommand.SetRallyPoint(LocalPlayerId, building.Value, target?.Value ?? 0));

	/// <summary>集结点全量版(原版 input.js 的 rally 指令类型化 + Shift 追加):
	/// commandType ∈ walk/gather/repair/garrison/attack/patrol/trade/collect-treasure;
	/// target 实体优先(其位置随指令入队),否则用地面坐标;append=true 追加到队列尾。</summary>
	public void CommandSetRallyPointFull(EntityId building, EntityId? target, float x, float z,
		string commandType, string resourceType = "", bool append = false) =>
		SubmitCommand(NetCommand.SetRallyPointFull(LocalPlayerId, building.Value,
			target?.Value ?? 0, ZeroAD.Sim.Maths.Fixed.FromFloat(x),
			ZeroAD.Sim.Maths.Fixed.FromFloat(z), commandType, resourceType, append));

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
		new(0.5f, 0.5f, 0.5f, 1f),     // gaia/neutral
		new(0.08f, 0.22f, 0.58f, 1f),  // P1: blue
		new(0.72f, 0.06f, 0.06f, 1f),  // P2: red
		new(0.12f, 0.55f, 0.14f, 1f),  // P3: green
		new(0.85f, 0.68f, 0.10f, 1f),  // P4: yellow
		new(0.52f, 0.14f, 0.68f, 1f),  // P5: purple
		new(0.10f, 0.62f, 0.70f, 1f),  // P6: cyan
		new(0.86f, 0.48f, 0.14f, 1f),  // P7: orange
		new(0.20f, 0.20f, 0.22f, 1f),  // P8: dark gray
	};

	public static Color GetPlayerColor(int playerId) =>
		playerId >= 0 && playerId < PlayerColors.Length ? PlayerColors[playerId] : new Color(0.6f, 0.5f, 0.4f);

	private string _lastSpawnedTemplate = "";
	private Color _lastPlayerColor = new(0.6f, 0.5f, 0.4f);

	// 静态 gaia 资源合批(FloraBatcher 类注释有完整设计);锚点容器与 Units 平级。
	private FloraBatcher? _floraBatch;
	private Node3D? _floraAnchors;

	private void EnsureFloraBatcher()
	{
		if (_floraBatch != null) return;
		var worldRoot = UnitContainer.GetParent<Node3D>();
		_floraBatch = new FloraBatcher(worldRoot);
		_floraAnchors = new Node3D { Name = "FloraAnchors" };
		worldRoot.AddChild(_floraAnchors);
	}

	/// <summary>合批 flora 逐模板 (总数, 可见数)——dev 诊断(ZEROAD_FLORA_DUMP)。</summary>
	public System.Collections.Generic.IEnumerable<(string Template, int Total, int Visible)> FloraStats()
		=> _floraBatch?.Stats() ?? System.Linq.Enumerable.Empty<(string, int, int)>();

	/// <summary>合批 flora 逐模板前 count 个实体的世界坐标采样——dev 诊断。</summary>
	public System.Collections.Generic.IEnumerable<string> FloraSampleBases(int count)
		=> _floraBatch?.SampleBases(count) ?? System.Linq.Enumerable.Empty<string>();

	/// <summary>合批 flora 实时实例状态(非零缩放/零缩放计数)——dev 诊断。</summary>
	public System.Collections.Generic.IEnumerable<string> FloraReportLive()
		=> _floraBatch?.ReportLive() ?? System.Linq.Enumerable.Empty<string>();

	/// <summary>合批 flora 逐部件(变体×网格)容量/顶点/活实例——dev 诊断。</summary>
	public System.Collections.Generic.IEnumerable<string> FloraReportParts()
		=> _floraBatch?.ReportParts() ?? System.Linq.Enumerable.Empty<string>();

	/// <summary>合批 flora 矩形内实体(变体, 实时变换)对照——dev 诊断。</summary>
	public System.Collections.Generic.IEnumerable<string> FloraSampleVariantsInRect(
		float minX, float minZ, float maxX, float maxZ)
		=> _floraBatch?.SampleVariantsInRect(minX, minZ, maxX, maxZ) ?? System.Linq.Enumerable.Empty<string>();

	/// <summary>矩形内 gaia 实体的 sim LosVisibility(P1)——诊断雾隐恢复链路。</summary>
	public System.Collections.Generic.IEnumerable<string> FloraLosInRect(
		float minX, float minZ, float maxX, float maxZ)
	{
		foreach (var (eid, node) in _entityNodes)
		{
			var id = _sim.QueryInterface<IdentityComponent>(eid);
			if (id == null) continue;
			if (!id.TemplateName.StartsWith("gaia/tree", System.StringComparison.Ordinal)) continue;
			var pos = _sim.QueryInterface<PositionComponent>(eid);
			if (pos == null) continue;
			float x = pos.Position.X.ToFloat(), z = pos.Position.Z.ToFloat();
			if (x < minX || x > maxX || z < minZ || z > maxZ) continue;
			var vis = _range.GetLosVisibility(eid, 1);
			yield return $"{id.TemplateName} eid={eid.Value} pos=({x:F0},{z:F0}) los={vis}";
		}
	}

	private void CreateVisualFor(EntityId entity, Color color, float size, bool isBuilding = false, bool isGhost = false, string? templateName = null)
	{
		var identity = _sim.QueryInterface<IdentityComponent>(entity);
		string name = identity?.Name ?? "";
		string template = templateName
			?? (!string.IsNullOrEmpty(identity?.TemplateName) ? identity.TemplateName : null)
			?? _lastSpawnedTemplate
			?? name;
		Node3D? visual = null;

		// 静态 gaia 资源(树/石/矿;无 Health = 非动物)走 MultiMesh 合批:网格并入
		// 按(模板×变体)分桶的 MultiMeshInstance3D,实体只留无网格锚点(选择圈/诊断仍按
		// EntityNodes 工作;锚点不进 UnitContainer → 不产阴影代理)。mirage 不合批——
		// 雾中变暗是逐节点材质处理,量小。
		// 例外:scenario/skirmish 地图(XML 实体)走逐节点路径——合批在这些图上
		// 存在未查明的实例丢失(Gold Oasis 棕榈),逐节点路径已验证稳定;rmgen 继续合批。
		bool useFloraBatch = string.IsNullOrEmpty(MapPath) || MapPath.StartsWith("random/", StringComparison.Ordinal);
		if (!isGhost && useFloraBatch && System.Environment.GetEnvironmentVariable("ZEROAD_NO_FLORA_BATCH") == null
			&& _sim.QueryInterface<MirageComponent>(entity) == null
			&& Templates != null && template.StartsWith("gaia/", System.StringComparison.Ordinal))
		{
			TemplateStats? st = null;
			try { st = Templates.ExtractStats(template); } catch { }
			if (st is { HasHealth: false })
			{
				var pos0 = _sim.QueryInterface<PositionComponent>(entity);
				if (pos0 != null)
				{
					float wx = pos0.Position.X.ToFloat();
					float wz = pos0.Position.Z.ToFloat();
					var worldPos = new Vector3(wx, TerrainHeightService.Sample(wx, wz), wz);
					// 朝向用实体 id 哈希(锚点旋转由生成路径后设,与网格解耦;树木无需地图 yaw)。
					float yaw = entity.Value * 2.399963f;  // 黄金角,确定性的伪随机朝向
					EnsureFloraBatcher();
					if (_floraBatch!.Add(entity, template, worldPos, yaw))
					{
						var anchor = new Node3D { Position = worldPos };
						anchor.SetMeta("entityId", (int)entity.Value);
						_floraAnchors!.AddChild(anchor);
						_entityNodes[entity] = anchor;
						_entityCacheDirty = true;
						return;
					}
				}
			}
		}

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
			// 合批实体(树/石/矿)位置永不变,跳过 tick 级位置同步。
			if (_floraBatch != null && _floraBatch.Contains(kvp.Key)) continue;
			var pos = _sim.QueryInterface<PositionComponent>(kvp.Key);
			if (pos == null) continue;
			var node = kvp.Value;

			// 地基:Y 由 TickFoundations 按进度逐 tick 升起,位置同步不得用地形高度
			// 覆盖(否则建筑永远满高显示,"一下子出现")。riseHeight meta 是地基标记。
			float py = node.HasMeta("riseHeight")
				? node.Position.Y
				: TerrainHeightService.Sample(pos.Position.X.ToFloat(), pos.Position.Z.ToFloat());
			var newPos = new Vector3(
				pos.Position.X.ToFloat(),
				py,
				pos.Position.Z.ToFloat());

			// 推给插值器记录 prev/curr(新单位/传送 snap 内置);渲染帧在 _Process 末尾
			// 按 alpha 插值写入 node.Position,而非每 tick 直接 snap(那会造成 10Hz 瞬移)。
			_interpolator.RecordTick(kvp.Key, node, newPos);

			if (_animators.TryGetValue(kvp.Key, out var animator))
				UpdateUnitAnimation(kvp.Key, node, animator, newPos);
			else
			{
				// 建筑朝向同步:sim 的 Rotation.Y(原版 SetYRotation)推到 Node3D。
				// 严格隔离单位——单位走上面 UpdateUnitAnimation 的 travel-delta 朝向,
				// 若在此覆盖会把单位 yaw 钳回 0。建筑无 animator,只在此设朝向。
				// Rotation.Y==0 时不写,避免覆盖场景建筑已设好的 OrientationY(spawn 时套的)。
				float yaw = pos.Rotation.Y.ToFloat();
				if (yaw != 0f)
					node.Rotation = new Vector3(0, yaw, 0);
			}
		}
	}

	// Last applied per-player visibility per entity — visuals update only on transitions.
	private readonly Dictionary<EntityId, LosVisibility> _lastVis = new();

	/// <summary>Fog-of-war on the presentation layer: HIDDEN entities lose their node,
	/// FOGGED ones (structures/mirages standing in explored fog) stay but ghosted.
	/// Applied only on transitions (per-player visibility changes are rare per frame).
	/// 装饰植被(actor| 纯视觉,不进 sim 无 LOS 实体)在此按 LOS 网格显隐:未探索
	/// tile 的节点 Visible=false——否则它们穿透黑色迷雾浮在地形上(C++ 的 decoratives
	/// 同样受 LOS 门控)。</summary>
	private void SyncVisibility()
	{
		int lp = (int)LocalPlayerId;
		// 装饰植被:按网格 explored 位显隐(无实体,查 _range.Los.IsExplored)。
		if (_decorativeNodes.Count > 0)
		{
			var los = _range.Los;
			for (int i = 0; i < _decorativeNodes.Count; i++)
			{
				var d = _decorativeNodes[i];
				if (!GodotObject.IsInstanceValid(d)) continue;
				// 节点挂在镜像根(UnitContainer)下,local Position 即 sim 坐标
				//(z 镜像由父节点 Scale.z=−1 在世界变换时施加,local 无需再换算)。
				var p = d.Position;
				var (vi, vj) = los.WorldToVertex(
					ZeroAD.Sim.Maths.Fixed.FromFloat(p.X),
					ZeroAD.Sim.Maths.Fixed.FromFloat(p.Z));
				bool explored = los.IsExplored(lp, vi, vj);
				if (d.Visible != explored) d.Visible = explored;
			}
		}
		foreach (var kvp in _entityNodes)
		{
			var vis = _range.GetLosVisibility(kvp.Key, lp);
			if (_lastVis.TryGetValue(kvp.Key, out var old) && old == vis) continue;
			_lastVis[kvp.Key] = vis;
			// 合批实体(树/石/矿):雾隐 = 实例零缩放(锚点无网格,ApplyVisibility 无效)。
			if (_floraBatch != null && _floraBatch.Contains(kvp.Key))
				_floraBatch.SetVisible(kvp.Key, vis != LosVisibility.Hidden);
			else
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
			// Fall back to Walk/Idle when the unit lacks the exact state clip, so a
			// gather/attack state never freezes a unit that has no matching clip.
			if (!animator.HasState(want))
				want = ResolveAnimationState(entity).Contains("Walk") ? "Walk" : "Idle";
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
	/// Maps the UnitAI FSM state to an animation state name. 变体数据的命名大小写混合:
	/// 移动/闲置大写("Idle"/"Walk"/"Run"),行为小写(gather_*/attack_*/promotion),
	/// 建造大写("Build")——HasState 是精确键匹配,写错大小写 = 永远不播。
	/// FSM state names are hierarchical paths produced by Fsm.cs
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
			return "Build";   // 动画变体名大写(variants/biped/build.xml name="Build")
		if (fsm.Contains("COMBAT.ATTACKING"))
			return "attack_melee";

		// Walking states: simple move (WALKING), approaching a target (*.APPROACHING),
		// or returning resources (GATHER.RETURNINGRESOURCE).
		if (fsm.EndsWith(".WALKING", StringComparison.Ordinal)
			|| fsm.Contains("APPROACHING")
			|| fsm.Contains("RETURNINGRESOURCE"))
			return "Walk";

		return "Idle";
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
