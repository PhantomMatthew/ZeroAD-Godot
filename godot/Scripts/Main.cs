using Godot;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Godot.Actors;
using ZeroAD.Godot.Options;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using ZeroAD.Sim.Pathfinding;

namespace ZeroAD.Godot;

public sealed partial class Main : Node3D
{
	private RTSCamera _camera = null!;
	private SimBridge _sim = null!;
	private Node3D _units = null!;
	private Node3D _worldRoot = null!;
	private Node3D _shadowRoot = null!;
	private DirectionalLight3D _light = null!;
	private global::Godot.Environment _env = null!;
	private HUD _hud = null!;
	private ChatPanel _chatPanel = null!;
	private LobbyUI _lobby = null!;
	private MultiplayerController _mp = null!;

	private readonly HashSet<EntityId> _selectedEntities = new();
	private bool _dragSelecting;
	private Vector2 _dragStart;
	private bool _isDragging;
	private CanvasLayer? _bandBoxLayer;
	private BandBoxRect? _bandBox;

	/// <summary>框选矩形(原版 bandbox:拖拽时屏幕空间半透明白框)。</summary>
	private sealed partial class BandBoxRect : Control
	{
		public Rect2 Rect;

		public override void _Draw()
		{
			if (Rect.Size == Vector2.Zero) return;
			DrawRect(Rect, new Color(1f, 1f, 1f, 0.08f));
			DrawRect(Rect, new Color(1f, 1f, 1f, 0.9f), filled: false, width: 1.5f);
		}
	}

	private void EnsureBandBox()
	{
		if (_bandBox != null) return;
		_bandBoxLayer = new CanvasLayer { Layer = 40 };
		_bandBox = new BandBoxRect { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
		_bandBox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_bandBoxLayer.AddChild(_bandBox);
		AddChild(_bandBoxLayer);
	}
	private bool _placeBuildingMode;
	// 放置预览状态(对齐原版 placement.js 的 placementSupport:position + angle)。
	// 默认朝向 3π/4(原版 PlacementSupport.DEFAULT_ANGLE),[/]键 ±π/12 旋转(15°/步),
	// 鼠标按住拖拽超阈值后朝向光标方向(input.js:786)。
	private float _placeAngle = Mathf.Pi * 0.75f;
	private Node3D? _placeGhost;             // 跟随鼠标的半透明预览节点
	private Vector2 _placeMouseDown = new(-1, -1);  // 左键按下屏幕坐标(拖拽旋转基准)
	private Vector3? _placeAnchorWorld;      // 按下点的世界坐标(atan2 基准)
	private string? _commandTargetMode;   // 命令键目标模式:"garrison"/"repair"/"guard" —— 下次左键选目标
	/// <summary>进入命令目标模式(原版 unit_actions 按钮 → 光标选目标)。Escape/再次点击后清除。</summary>
	public void EnterCommandTargetMode(string mode)
	{
		_commandTargetMode = mode;
		_placeBuildingMode = false;
	}
	private string _buildTemplate = "";
	private bool _gameStarted;
	/// <summary>BeginGameplayInit 的生效槽位表(rmgen 玩家 civ 列表用;教程/冷加载为 null)。</summary>
	private IReadOnlyList<ZeroAD.Sim.Net.PlayerSlotSetup>? _worldSlots;
	private bool _isTutorial;
	private TutorialPanel _tutorialPanel = null!;
	private LoadingOverlay? _loadingOverlay;
	private PauseMenu? _pauseMenu;
	// FPS 叠层(overlay.fps 配置项驱动,原版 Display 类):右上角实时帧率。
	private CanvasLayer? _fpsOverlay;
	private Label? _fpsLabel;
	// 第二梯队菜单面板(Diplomacy/Trade/Match Settings):模态叠层,不暂停 sim。
	// (Game Speed 已改为顶栏时间按钮下方的非模态弹出条,见 HUD.BuildGameSpeedPopover,
	// 对齐原版 GameSpeedControl 下拉位置。)
	private DiplomacyPanel? _diplomacyPanel;
	private TradePanel? _tradePanel;
	private StructreePanel? _structreePanel;
	private MatchSettingsPanel? _matchSettingsPanel;

	public IReadOnlySet<EntityId> SelectedEntities => _selectedEntities;
	public bool IsTutorial => _isTutorial;
	public SimBridge Sim => _sim;
	public void SetCameraFocus(Vector3 pos) => _camera.SetFocus(pos);
	public Vector3? GetCameraFocus() => _camera?.Focus;
	public float GetCameraYaw() => _camera?.Yaw ?? 0f;

	public override void _Ready()
	{
		_camera = new RTSCamera();
		AddChild(_camera);

		var light = new DirectionalLight3D();
		light.Rotation = new Vector3(-0.7f, 0.5f, 0);
		light.LightEnergy = 1.2f;
		AddChild(light);
		_light = light;

		var sky = new WorldEnvironment();
		var env = new global::Godot.Environment();
		// 背景模式显式设为 Color:默认 BG_SKY 无 sky 资源时行为依渲染器而异
		// (Compatibility 渲白、Forward+ 渲深灰),设为 Color 后两个渲染器都出目标蓝。
		env.BackgroundMode = global::Godot.Environment.BGMode.Color;
		env.BackgroundColor = new Color(0.45f, 0.65f, 0.9f);
		env.FogEnabled = true;
		env.FogLightColor = new Color(0.5f, 0.7f, 0.95f);
		env.FogDensity = 0.001f;
		sky.Environment = env;
		AddChild(sky);
		_env = env;
		// Options 图形项(light/env)的作用目标注册给映射层;ChangeScene 时 _ExitTree 注销。
		OptionsApplier.RegisterSceneNodes(light, sky);

		// 视觉镜像根:C++ pyrogenesis 的世界是左手系惯例(+z=北、+x=东),相机基向量
		// 带 −s 翻转(CCamera::LookAlong)把画面掰回"上北下南左西右东";Godot 标准相机
		// 做不到同画面(屏幕映射左手性),故把所有世界视觉挂到 Scale.z=−1 的根下整体镜像
		// (Position.z=WorldSize 使视觉 z = WorldSize − sim z 保持 0..WorldSize 正区间)。
		// 子节点局部坐标=sim 坐标不变(单位位置同步/朝向/标记全不用改),Godot 自动处理
		// 负 scale 实例的正反面剔除。两个边界点:相机对焦(RTSCamera 内部换算)与
		// 屏幕拾取(ScreenToWorld 返回 sim 坐标)。
		_worldRoot = new Node3D { Name = "WorldMirror", Scale = new Vector3(1f, 1f, -1f) };
		AddChild(_worldRoot);

		_units = new Node3D { Name = "Units" };
		_worldRoot.AddChild(_units);

		// 阴影代理容器(正规空间,与 _worldRoot 平级):负 scale 根的深度 pass 不投影,
		// ShadowsOnly 代理在镜像世界外重建投影(见 ShadowProxyManager 注释的 S 相消数学)。
		_shadowRoot = new Node3D { Name = "ShadowProxies" };
		AddChild(_shadowRoot);

		_sim = new SimBridge { UnitContainer = _units, ShadowRoot = _shadowRoot };
		AddChild(_sim);

		_mp = new MultiplayerController { Name = "Multiplayer" };
		AddChild(_mp);

		_lobby = new LobbyUI();
		AddChild(_lobby);

		_lobby.OnHostStart += (port, seed) => StartMpHost(port, seed);
		_lobby.OnClientConnect += (addr, port) => StartMpClient(addr, port);
		// Lobby slot editing (host only): each edit re-broadcasts the slot table to clients.
		_lobby.OnSlotEdit += (id, kind, civ, team) => _mp.HostSetSlot(id, kind, civ, team);
		_lobby.OnMapEdit += map => _mp.HostSetMap(map);
		_mp.OnMapChanged += map => _lobby.SetMapDisplay(map);
		_lobby.OnStartGameRequested += () => _mp.HostStartGame();
		_lobby.OnSinglePlayer += seed => StartSinglePlayer(seed);
		_lobby.OnTutorialStart += () => StartTutorial();
		// Lobby-state refresh: clients repaint their read-only slot list from the host's table.
		// The host is the source of truth (its rows are editable) and never repaints from events.
		_mp.OnLobbyStateChanged += slots => { if (!_mp.IsHost) _lobby.RefreshSlotDisplay(slots); };
		// Start 拒绝(有 Human 槽未被认领)——原因显示到大厅状态行,否则按钮看似没反应。
		_mp.OnStartRefused += msg => _lobby.SetStatus(msg);
		// MP 面板 Cancel/Close:关 peer + 回主菜单(原仅关面板,用户困在无菜单的 session 场景)。
		_lobby.OnCancelRequested += () =>
		{
			_mp.Shutdown();
			GetNode<GameLaunchConfig>("/root/GameLaunchConfig").Reset();
			GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
		};

		_camera.SetFocus(new Vector3(128, 0, 128));

		// 音频初始化(数据根 = binaries/data/mods/public;null 静默)。音乐播放列表在
		// 开局时启动(BeginGameplayScenario 末尾 peace 列表)。
		AudioManager.Init(this, FindDataRoot());

		// 启动模式由 MainMenu 写入 GameLaunchConfig(进程级 env 仅 dev fallback,已由 MainMenu
		// 首次读取后清空——修 ChangeScene 回主菜单重触发自动开局的 bug)。SP/Tutorial 直接开局;
		// Load 冷加载存档;Multiplayer/Lobby 显大厅 LobbyUI(不自动开局,等用户 Host/Join)。
		// 先全量重放已存设置:音量/显示即时生效项 + 本会话场景图形项(light/env 已注册)。
		OptionsApplier.ApplyAll(GetNode<UserConfig>("/root/UserConfig"), GetTree(), inGame: true);
		// 恢复用户热键重绑到 InputMap（session-only，每次启动须重放）。
		HotkeyApplier.ApplyAll(GetNode<UserConfig>("/root/UserConfig"));

		var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		switch (cfg.Mode)
		{
			case GameLaunchConfig.LaunchMode.SinglePlayer:
				CallDeferred(nameof(AutoStart));
				break;
			case GameLaunchConfig.LaunchMode.Tutorial:
				CallDeferred(nameof(AutoTutorial));
				break;
			case GameLaunchConfig.LaunchMode.Load:
				CallDeferred(nameof(AutoLoad));
				break;
			case GameLaunchConfig.LaunchMode.Replay:
				CallDeferred(nameof(AutoReplay));
				break;
			case GameLaunchConfig.LaunchMode.Multiplayer:
				CallDeferred(nameof(AutoMp));
				break;
		}
	}

	private void AutoStart() => StartSinglePlayer(GetNode<GameLaunchConfig>("/root/GameLaunchConfig").Seed);
	private void AutoTutorial() => StartTutorial();

	/// <summary>MP 入口(MainMenu 子菜单 Host New Game / Connect by IP):直显连接表单,
	/// 不再显 LobbyUI 遗留旧菜单(那是 MainMenu.tscn 存在前的假主菜单)。</summary>
	private void AutoMp()
	{
		var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		_lobby.EnterMpDirect(cfg.MpHost);
	}

	private void StartTutorial()
	{
		// 加载等待页(对齐原版 page_loading:顶部进度条 + 中央提示卡)。分阶段驱动:
		// BeginGameplay 拆成 Init/Session/Scenario 三段,段间 await 一帧让进度条重绘
		// (原 0.15s Timer 只保证首帧绘制,无法反映真实阶段进度)。
		_loadingOverlay = new LoadingOverlay("Introductory Tutorial");
		AddChild(_loadingOverlay);
		RunStagedGameplayLoad(42, 1, null, tutorial: true, isMultiplayer: false, isHost: false);
	}

	/// <summary>分阶段加载并驱动等待页进度条(0.05→0.5→0.65→1.0)。SP/Tutorial/MP 开局共用。</summary>
	private async void RunStagedGameplayLoad(uint seed, uint playerId,
		IReadOnlyList<ZeroAD.Sim.Net.PlayerSlotSetup>? slots,
		bool tutorial, bool isMultiplayer, bool isHost)
	{
		try
		{
			_loadingOverlay!.SetProgress(0.05f);
			// 两帧:确保等待页(含提示图)完整呈交后再开始阻塞式加载。
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

			var effectiveSlots = BeginGameplayInit(seed, playerId, slots, tutorial, isMultiplayer, isHost);
			_loadingOverlay.SetProgress(0.5f);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

			BeginGameplaySession(playerId);
			_loadingOverlay.SetProgress(0.65f);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

			BeginGameplayScenario(playerId, effectiveSlots, isMultiplayer);
			_loadingOverlay.SetProgress(1f);
		}
		catch (System.Exception e)
		{
			// 加载失败不再 rethrow:async void 里抛异常只会留一个无地形的"天蓝空世界",
			// 用户看到的像卡死而不是崩溃。改为报错 + 回主菜单(同 ColdLoad 失败路径)。
			GD.PrintErr($"[Gameplay] EXCEPTION in load: {e}");
			GD.PrintErr($"[Gameplay] Stack: {e.StackTrace}");
			GetNode<GameLaunchConfig>("/root/GameLaunchConfig").Reset();
			GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
		}
		finally
		{
			_loadingOverlay?.QueueFree();
			_loadingOverlay = null;
		}
	}

	private void StartSinglePlayer(uint seed)
	{
		// SP 同样走加载等待页(page_loading:进度条 + 提示卡),标题取所选地图名。
		_loadingOverlay = new LoadingOverlay(MapTitleFromPath(PickSkirmishMapRel()));
		AddChild(_loadingOverlay);
		RunStagedGameplayLoad(seed, 1, null, tutorial: false, isMultiplayer: false, isHost: false);
	}

	/// <summary>Host enters the lobby (transport up, slot table editable). The game does NOT start
	/// here — the host configures AI/civ/team slots, then clicks Start → HostStartGame → GameStart.
	/// OnGameStart carries the frozen slot table so host + client build identical worlds.</summary>
	private void StartMpHost(int port, uint seed)
	{
		_mp.StartHost(port, seed);
		_mp.OnGameStart += (s, pid, slots, map) => StartMpGameplay(s, pid, slots, isHost: true, map);
		_lobby.ShowSlotLobby(isHost: true, _mp.Slots, LobbyMapCatalog(), "");
		_lobby.SetStatus($"Hosting on port {port} — configure slots, then Start.");
	}

	/// <summary>Client connects and waits in the lobby. Its slot is claimed by the host on
	/// connect; the host's slot table broadcasts keep this client's read-only view in sync.
	/// World creation is deferred until the host fires GameStart.</summary>
	private void StartMpClient(string addr, int port)
	{
		_mp.StartClient(addr, port);
		_mp.OnGameStart += (s, pid, slots, map) => StartMpGameplay(s, pid, slots, isHost: false, map);
		_lobby.ShowSlotLobby(isHost: false, null, LobbyMapCatalog(), "");
		_lobby.SetStatus($"Connecting to {addr}:{port} — waiting for host…");
	}

	/// <summary>大厅选图目录(scenario/skirmish 走数据根;random 由 MapRegistry 提供,始终可用)。</summary>
	private List<MapEntry> LobbyMapCatalog()
	{
		string? dataRoot = FindDataRoot();
		return MapCatalog.Scan(dataRoot);
	}

	/// <summary>MP 正式开局(host 点 Start 后双端同走):加载等待页 + 分阶段构建。
	/// map 由 host 大厅冻结并经 GameStart 下发,与 seed 一起写回 cfg 供 SetupTerrain/rmgen 用。</summary>
	private void StartMpGameplay(uint seed, uint playerId,
		IReadOnlyList<ZeroAD.Sim.Net.PlayerSlotSetup> slots, bool isHost, string map)
	{
		var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		cfg.MapPath = map;
		cfg.Seed = seed;   // rmgen 种子必须与 host 一致(cfg.Seed 的菜单值对 MP 无意义)
		_loadingOverlay = new LoadingOverlay(MapTitleFromPath(string.IsNullOrEmpty(map) ? PickSkirmishMapRel() : map));
		AddChild(_loadingOverlay);
		RunStagedGameplayLoad(seed, playerId, slots, tutorial: false, isMultiplayer: true, isHost: isHost);
	}

	/// <summary>SP/MP 默认地图(镜像 SetupTerrain 的 pmp 回退链),供加载页标题推导。
	/// GameLaunchConfig.MapPath 已选图(ZEROAD_MAP / 未来选图 UI)时优先返回所选。</summary>
	private string? PickSkirmishMapRel()
	{
		var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		if (!string.IsNullOrEmpty(cfg.MapPath)) return cfg.MapPath;
		if (FindDataPath("maps/scenarios/arcadia.pmp") != null) return "maps/scenarios/arcadia.pmp";
		if (FindDataPath("maps/scenarios/laconia_01.pmp") != null) return "maps/scenarios/laconia_01.pmp";
		return null;
	}

	/// <summary>地图文件名 → 加载页标题(原版取地图 display name;文件名推导为近似)。</summary>
	private static string MapTitleFromPath(string? rel)
	{
		if (rel == null) return "Single Player";
		string name = System.IO.Path.GetFileNameWithoutExtension(rel).Replace('_', ' ').Trim();
		return name.Length == 0 ? "Single Player" : char.ToUpperInvariant(name[0]) + name[1..];
	}

	/// <summary>阶段 1(重:模板解析+世界构建)。guard + InitWorld + MP 接线;返回生效槽位表。
	/// 拆段是为加载等待页:阶段间 await 一帧让进度条重绘(见 StartTutorial)。</summary>
	private IReadOnlyList<ZeroAD.Sim.Net.PlayerSlotSetup> BeginGameplayInit(uint seed, uint playerId,
		IReadOnlyList<ZeroAD.Sim.Net.PlayerSlotSetup>? slots, bool tutorial, bool isMultiplayer, bool isHost)
	{
		if (_gameStarted) throw new System.InvalidOperationException("BeginGameplayInit called twice");
		_gameStarted = true;
		_isTutorial = tutorial;
		_lobby.Hide();
		GD.Print($"[Tutorial] BeginGameplay start: tutorial={tutorial}");

		string? templatesPath = FindTemplatesPath();
		GD.Print($"[Tutorial] templatesPath={templatesPath ?? "null"}");

		// One InitWorld path for SP/MP/tutorial: seed + player slots + role all flow in
		// here. In MP the host assigned the seed + the frozen slot table over GameStart, so
		// every peer constructs the same world and the same NetTurnManager.
		var role = isMultiplayer
			? (isHost ? ZeroAD.Sim.Net.NetRole.Host : ZeroAD.Sim.Net.NetRole.Client)
			: ZeroAD.Sim.Net.NetRole.Standalone;
		// Effective slot table: MP passes the host's frozen table verbatim. SP/sandbox
		// default to a 1v1 Human-vs-AI table (slot 1 = this player, slot 2 = AI opponent);
		// tutorial is single-player (one Human slot, no AI). Slot count is data-driven now.
		IReadOnlyList<ZeroAD.Sim.Net.PlayerSlotSetup> effectiveSlots = slots
				?? (tutorial
					? new List<ZeroAD.Sim.Net.PlayerSlotSetup>
					{
						// 教程地图(introductory_tutorial)PlayerData[1].Civ=spart——
						// 槽位文明须与地图一致(TechnologyManager 文明键在世界构建时定型)。
						new() { PlayerId = 1, Kind = ZeroAD.Sim.Net.PlayerSlotKind.Human, Civ = "spart" },
					}
				: new List<ZeroAD.Sim.Net.PlayerSlotSetup>
				{
					new() { PlayerId = 1, Kind = ZeroAD.Sim.Net.PlayerSlotKind.Human, Civ = "athen", Team = -1 },
					new() { PlayerId = 2, Kind = ZeroAD.Sim.Net.PlayerSlotKind.AI,    Civ = "gaul",  Team = -1 },
				});
		_sim.InitWorld(templatesPath, seed, playerId, role, effectiveSlots);
		_worldSlots = effectiveSlots;   // rmgen 玩家 civ 列表(SetupRmgenTerrain)等用
		GD.Print("[Tutorial] InitWorld done");

		if (isMultiplayer)
		{
			// Wire the transport to the freshly built NetTurnManager. The host bootstraps
			// its empty leading turns so play can start immediately.
			_mp.AttachTurnManager(_sim.NetTurn);
			_mp.OnOOS += OnOOSDetected;
			GD.Print("[MP] AttachTurnManager done");
		}
		return effectiveSlots;
	}

	/// <summary>阶段 2(轻:会话 UI 装配)。</summary>
	private void BeginGameplaySession(uint playerId)
	{
		BuildSessionUi(playerId);
		if (_isTutorial)
			WireTutorialPanel();
		WireMirageSwapBack();
	}

	/// <summary>阶段 3(重:地形+实体生成)。</summary>
	private void BeginGameplayScenario(uint playerId, IReadOnlyList<ZeroAD.Sim.Net.PlayerSlotSetup> effectiveSlots,
		bool isMultiplayer = false)
	{
		if (_isTutorial)
		{
			GD.Print("[Tutorial] calling SetupTutorialWorld...");
			try
			{
				SetupTutorialWorld();
			}
			catch (System.Exception ex)
			{
				GD.PrintErr($"[Tutorial] SetupTutorialWorld FAILED: {ex}");
				GD.PrintErr($"[Tutorial] Stack: {ex.StackTrace}");
				// Don't rethrow — let the game continue without the tutorial scenario rather
				// than crash. The player can still see terrain and the panel.
			}
			GD.Print("[Tutorial] SetupTutorialWorld done");
		}
		else
			SetupGameWorld(playerId, effectiveSlots, isMultiplayer);

		// 世界已完整:放行回合推进(SimBridge._Process 闸门)。分阶段加载在 Init 与本阶段
		// 之间让帧,此间回合必须冻结,否则 TickVictory 在空世界判全员 0 实体→进场即 Defeat。
		_sim.StartRecording();  // 自动录像：开局后立即开始录制（回放模式不录，见 AutoReplay）
		_sim.SimulationRunning = true;
		AudioManager.StartPlaylist("peace");   // 局内音乐(原版 PEACE 列表 shuffle)
		AudioManager.StartAmbient("ambient/dayscape/day_temperate.xml", this);   // 环境音景循环

		// dev 自检钩子:ZEROAD_AUTOBUILD=1 时开局 ~8s 后自动下令建一栋住宅
		// (建造动画/地基渐显的无人值守验证;正常游戏不触发)。
		if (System.Environment.GetEnvironmentVariable("ZEROAD_AUTOBUILD") == "1")
			AutobuildDeferred();

		// dev 截图钩子:ZEROAD_SHOT_SESSION=<秒> 开局 N 秒后视口截图存
		// user://session_shot.png(不退出;窗口无需前台,后台可截)。
		if (int.TryParse(System.Environment.GetEnvironmentVariable("ZEROAD_SHOT_SESSION"), out int shotSec))
			SessionShotDeferred(shotSec);

		GD.Print(_isTutorial
			? "[Tutorial] Introductory Tutorial started"
			: $"[Tutorial] MS6 Game started: player={playerId}");
	}

	/// <summary>dev 钩子:N 秒后视口截图(可多次:用 ZEROAD_SHOT_SESSION 逗号秒数)。</summary>
	private async void SessionShotDeferred(int seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
		var img = GetViewport().GetTexture().GetImage();
		string p = $"user://session_shot_{seconds}s.png";
		img.SavePng(p);
		GD.Print($"[Shot] saved {p}");
	}

	/// <summary>dev 钩子:找本地玩家的 CC + 一个工人,在 CC 旁下个住宅建造令。</summary>
	private async void AutobuildDeferred()
	{
		await ToSignal(GetTree().CreateTimer(8.0), SceneTreeTimer.SignalName.Timeout);
		int lp = (int)_sim.LocalPlayerId;
		Vector3? ccPos = null;
		string civ = "spart";
		ZeroAD.Sim.EntityId builder = default;
		bool foundBuilder = false;
		foreach (var e in _sim.Sim.AllEntities)
		{
			var own = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.OwnershipComponent>(e);
			if (own == null || own.PlayerId != lp) continue;
			var id = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.IdentityComponent>(e);
			if (id == null) continue;
			if (ccPos == null && id.TemplateName.Contains("/civil_centre"))
			{
				civ = id.TemplateName.Split('/')[1];
				var p = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.PositionComponent>(e);
				if (p != null) ccPos = new Vector3(p.Position.X.ToFloat(), 0, p.Position.Z.ToFloat());
			}
			if (!foundBuilder && _sim.Sim.QueryInterface<ZeroAD.Sim.Components.BuilderComponent>(e) != null)
			{
				builder = e; foundBuilder = true;
			}
		}
		if (ccPos == null || !foundBuilder)
		{
			GD.PrintErr("[Autobuild] no CC or builder found");
			return;
		}
		string house = $"structures/{civ}/house";
		// 逐偏移尝试(放置校验不合法会被执行端拒绝,换下一个)。
		foreach (var (ox, oz) in new[] { (18f, 0f), (0f, 18f), (-18f, 0f), (0f, -18f), (18f, 18f), (-18f, -18f) })
		{
			// 默认朝向 3π/4(原版 placement.js DEFAULT_ANGLE;自动建造不旋转)。
			_sim.CommandBuild(builder, house, ccPos.Value.X + ox, ccPos.Value.Z + oz, Mathf.Pi * 0.75f);
			// 命令经锁步延迟两回合生效,稍等再数地基。
			await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
			bool spawned = false;
			foreach (var e in _sim.Sim.AllEntities)
				if (_sim.Sim.QueryInterface<ZeroAD.Sim.Components.FoundationComponent>(e) != null) { spawned = true; break; }
			if (spawned)
			{
				GD.Print($"[Autobuild] ordered {house} at +({ox},{oz}) — watch the rise");
				// 视口自证:镜头对准工地,按进度连拍存 user://autobuild_t*.png。
				float h = TerrainHeightService.Sample(ccPos.Value.X + ox, ccPos.Value.Z + oz);
				_camera.SetFocus(new Vector3(ccPos.Value.X + ox, h, ccPos.Value.Z + oz));
				for (int shot = 0; shot < 4; shot++)
				{
					await ToSignal(GetTree().CreateTimer(5.0), SceneTreeTimer.SignalName.Timeout);
					var img = GetViewport().GetTexture().GetImage();
					string shotPath = $"user://autobuild_t{shot * 5 + 14}s.png";
					img.SavePng(shotPath);
					GD.Print($"[Autobuild] shot saved: {shotPath}");
				}
				return;
			}
		}
		GD.PrintErr("[Autobuild] all placements rejected");
	}

	/// <summary>Build the in-session UI chrome (HUD, game-over overlay, pause menu, tier-2
	/// panels) exactly once. Shared by BeginGameplay (fresh game) and ColdLoad (LoadGame):
	/// both run after InitWorld so the sim exists, and both need identical wiring.</summary>
	private void BuildSessionUi(uint playerId)
	{
		if (_hud != null) return;
		_hud = new HUD(_sim, this);
		AddChild(_hud);

		// 软件光标(macOS 上 Input.SetCustomMouseCursor 在用户环境静默无效,改自绘:
		// 顶层 CanvasLayer 贴图逐帧跟随鼠标。仅动作态(attack/gather/...)启用精灵+
		// 隐藏 OS 光标;默认态 = OS 箭头(原版如此,默认/move 无纹理)。_ExitTree 恢复可见。
		_cursorLayer = new CanvasLayer { Layer = 127 };
		_cursorSprite = new TextureRect
		{
			MouseFilter = Control.MouseFilterEnum.Ignore,
			StretchMode = TextureRect.StretchModeEnum.Keep,
			Visible = false,
		};
		_cursorLayer.AddChild(_cursorSprite);
		AddChild(_cursorLayer);

		// 建造拒绝 toast(原版红字提示):执行端拒绝是锁步两回合后异步发生,
		// 只能走事件回传;过滤只显本地玩家的拒绝。_ExitTree 退订。
		_sim.Sim.Events.PlayerCommand += OnPlayerCommandEvent;
		// Game-over overlay: subscribes to the sim's win/loss events and shows the
		// Victory/Defeat panel when the match ends.
		var gameOver = new GameOverOverlay(_sim, localPlayerId: (int)playerId);
		AddChild(gameOver);

		// 聊天面板（左上角消息日志 + Enter 打开输入框）。MP 时经 _mp 广播；SP 本地回显。
		_chatPanel = new ChatPanel(_sim, _mp, playerId);
		AddChild(_chatPanel);
		// MP 收到聊天 → 转发到 SimEventBus（ChatPanel 统一订阅）。
		_mp.OnChatReceived += OnMpChatReceived;
		// 游戏事件 → 系统聊天消息（"Player N was defeated"）。
		_sim.Sim.Events.PlayerDefeated += OnPlayerDefeatedChat;

		// 音频钩子:训练完成警报(单位模板 trained 组)+ 胜/败 jingle(原版 music.js
		// VICTORY/DEFEAT 单曲)。均为表现层;具名方法以便 _ExitTree 退订。
		_sessionPlayerId = (int)playerId;
		_sim.Sim.Events.TrainingFinished += OnTrainingFinishedSound;
		_sim.Sim.Events.PlayerWon += OnPlayerWonSound;
		_sim.Sim.Events.PlayerDefeated += OnPlayerDefeatedSound;
		// 武器音效(发射时刻,近战/远程按事件分流)+ 战斗计时(切 BATTLE 音乐用)。
		_sim.Sim.Events.AttackLaunched += OnAttackLaunchedSound;
		// 遇袭警报(原版 alert_panel):己方实体被命中 → 警报图标闪烁,点击跳相机。
		_sim.Sim.Events.AttackLanded += OnAttackAlert;
		// 数据驱动触发器消息(ShowMessage 动作)→ HUD toast。
		_sim.TriggerMessage += OnTriggerMessage;
		// 停战开始/结束(原版 CeasefireManager 的倒计时/开打通知)。
		_sim.Sim.Events.CeasefireStarted += OnCeasefireStarted;
		_sim.Sim.Events.CeasefireEnded += OnCeasefireEnded;

		// Pause menu (Menu 按钮 → 暂停叠层):冻结 sim + 存档/读档/离开。事件解耦同 LobbyUI:
		// 存档/读档复用 QuickSave/QuickLoad(含视觉重建),离开回主菜单。
		_pauseMenu = new PauseMenu(_sim);
		var pm = _pauseMenu;
		pm.OnSave += () => pm.SetStatus(QuickSave() != null ? "Saved." : "Save failed.");
		pm.OnLoad += () =>
		{
			var t = QuickLoad();
			pm.SetStatus(t == null ? "No save / load failed." : $"Loaded turn {t}.");
		};
		pm.OnLeave += () => GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
		AddChild(pm);

		// 第二梯队菜单面板(Diplomacy/Trade/Match Settings):模态叠层,挡鼠标不暂停。
		_diplomacyPanel = new DiplomacyPanel(_sim);
		_tradePanel = new TradePanel(_sim);
		_matchSettingsPanel = new MatchSettingsPanel(_sim);
		AddChild(_diplomacyPanel);
		AddChild(_tradePanel);
		AddChild(_matchSettingsPanel);
		// 科技树(原版顶栏民族徽标 → page_structree):面板自载模板/科技数据,
		// 打开时预选本地玩家文明。
		_structreePanel = new StructreePanel();
		AddChild(_structreePanel);

		// FPS 叠层:overlay.fps 改动经 UserConfig.ConfigChanged 即时显隐(Options 页改动不落盘也生效)。
		_fpsOverlay = new CanvasLayer { Layer = 45, Visible = false };
		_fpsLabel = new Label
		{
			AnchorsPreset = (int)Control.LayoutPreset.TopRight,
			OffsetLeft = -110,
			OffsetTop = 8,
			Theme = UITheme.GetTheme(),
		};
		_fpsLabel.AddThemeFontSizeOverride("font_size", 14);
		_fpsOverlay.AddChild(_fpsLabel);
		AddChild(_fpsOverlay);
		UpdateFpsOverlayVisibility();
		GetNode<UserConfig>("/root/UserConfig").ConfigChanged += OnUserConfigChanged;
	}

	private void OnUserConfigChanged(IReadOnlyList<string> keys)
	{
		if (keys.Contains("overlay.fps"))
			UpdateFpsOverlayVisibility();
		// 音量滑杆即时生效(原版 options 的 gain 项实时应用到 SoundManager)
		if (keys.Any(k => k.StartsWith("sound.", System.StringComparison.Ordinal)))
			AudioManager.RefreshVolumes(this);
	}

	private void UpdateFpsOverlayVisibility()
	{
		if (_fpsOverlay != null)
			_fpsOverlay.Visible =
				GetNode<UserConfig>("/root/UserConfig").GetEffective("overlay.fps") == "true";
	}

	/// <summary>Tutorial panel wiring, shared by BeginGameplay and ColdLoad.</summary>
	private void WireTutorialPanel()
	{
		_tutorialPanel = new TutorialPanel();
		AddChild(_tutorialPanel);
		_tutorialPanel.OnReadyPressed += () => _sim.Tutorial?.OnReadyPressed();
		_tutorialPanel.OnQuitPressed += QuitTutorial;
		_sim.Events.TutorialMessage += OnTutorialMessage;
	}

	/// <summary>Fog-of-war: a selected mirage swaps back to the real entity when it returns
	/// to sight (MT_EntityRenamed semantics), so orders/GUI keep targeting the real one.
	/// Shared by BeginGameplay and ColdLoad.</summary>
	private void WireMirageSwapBack()
	{
		_sim.Events.MirageSwapBack += e =>
		{
			if (e.Player == (int)_sim.LocalPlayerId && _selectedEntities.Remove(e.Mirage))
				_selectedEntities.Add(e.Parent);
		};
	}

	/// <summary>LoadGame entry: GameLaunchConfig.Mode=Load + LoadSlot set by the LoadGame
	/// browser. Reads the save header, then cold-loads behind a loading overlay (the world
	/// rebuild + deserialize is heavy synchronous work — same overlay pattern as StartTutorial).</summary>
	private void AutoLoad()
	{
		var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		var meta = SaveGameManager.ReadHeader(cfg.LoadSlot);
		if (meta == null || meta.MapPath == null)
		{
			// No such save / incompatible version / generated-terrain save (no map to rebuild).
			GD.PrintErr($"[LoadGame] cannot cold-load slot '{cfg.LoadSlot}': " +
				(meta == null ? "missing or incompatible save" : "generated terrain has no map path"));
			cfg.Reset();
			GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
			return;
		}

		_loadingOverlay = new LoadingOverlay(meta.Description);
		AddChild(_loadingOverlay);
		_loadingOverlay.SetProgress(0.05f);
		RunColdLoadStages(meta, cfg);
	}

	/// <summary>回放入口：打开录像 → 读 header → 加载初始状态 → 安装 ReplayDriver → 播放。
	/// 镜像 AutoLoad 的三段式（AutoReplay → RunReplayStages → ReplayPlay），但不录制、不 StartRecording。</summary>
	private void AutoReplay()
	{
		var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		var reader = ReplayFileManager.Open(cfg.ReplaySlot);
		if (reader == null)
		{
			GD.PrintErr($"[Replay] cannot open slot '{cfg.ReplaySlot}'");
			cfg.Reset();
			GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
			return;
		}
		_loadingOverlay = new LoadingOverlay(reader.Meta.Description);
		AddChild(_loadingOverlay);
		_loadingOverlay.SetProgress(0.05f);
		RunReplayStages(reader, cfg);
	}

	private async void RunReplayStages(ZeroAD.Sim.Net.ReplayReader reader, GameLaunchConfig cfg)
	{
		try
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			_loadingOverlay!.SetProgress(0.3f);
			ReplayPlay(reader);
			_loadingOverlay.SetProgress(1f);
		}
		catch (System.Exception e)
		{
			GD.PrintErr($"[Replay] playback init failed: {e}");
			cfg.Reset();
			GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
		}
		finally
		{
			_loadingOverlay?.QueueFree();
			_loadingOverlay = null;
		}
	}

	/// <summary>镜像 ColdLoad：重建世界骨架 → 反序列化初始状态 → 安装播放驱动器 → 放行回合。</summary>
	private void ReplayPlay(ZeroAD.Sim.Net.ReplayReader reader)
	{
		var meta = reader.Meta;
		_gameStarted = true;
		_isTutorial = meta.Tutorial;
		_lobby.Hide();

		string? templatesPath = FindTemplatesPath();
		_sim.InitWorld(templatesPath, seed: 0, meta.LocalPlayerId, NetRole.Standalone, meta.Slots);

		BuildSessionUi(meta.LocalPlayerId);
		WireMirageSwapBack();

		if (!string.IsNullOrEmpty(meta.MapPath))
			SetupTerrain(meta.MapPath);

		// 反序列化初始状态（从录像 payload，与 SaveGameManager.Load 同路径）。
		using var ms = new System.IO.MemoryStream(reader.InitialStatePayload);
		using var br = new System.IO.BinaryReader(ms);
		_sim.Sim.DeserializeSaveGame(new ZeroAD.Sim.Serialization.BinaryDeserializer(br), comp =>
		{
			if (comp is ZeroAD.Sim.Components.LosManagerComponent los)
				los.Attach(_sim.Range);
			if (comp is ZeroAD.Sim.Components.AIComponent ai)
				ai.Configure(_sim.Sim, _sim.NetTurn);
			if (comp is ZeroAD.Sim.Components.StatisticsTrackerComponent st)
				st.Attach(_sim.Sim);
		});

		_sim.RebuildSpatialIndexesAfterLoad();
		_sim.RebuildAllVisuals();
		FocusCameraOnLocalPlayer();

		// 安装回放驱动器（每帧注入预录制命令）+ 控制条。
		_sim.StartReplay(reader);
		var controls = new ReplayControls(_sim);
		controls.OnExit += () =>
		{
			_sim.SimulationRunning = false;
			GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
		};
		AddChild(controls);

		_sim.SimulationRunning = true;
		GD.Print($"[Replay] started '{meta.Description}' (commandDelay {meta.CommandDelay})");
	}

	/// <summary>冷加载分阶段驱动(同 RunTutorialLoadStages:段间 await 一帧让进度条重绘)。</summary>
	private async void RunColdLoadStages(SaveMeta meta, GameLaunchConfig cfg)
	{
		try
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			_loadingOverlay!.SetProgress(0.3f);
			ColdLoad(meta);
			_loadingOverlay.SetProgress(1f);
		}
		catch (System.Exception e)
		{
			GD.PrintErr($"[LoadGame] cold-load failed: {e}");
			cfg.Reset();
			GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
		}
		finally
		{
			_loadingOverlay?.QueueFree();
			_loadingOverlay = null;
		}
	}

	/// <summary>Cold (cross-scene) load: rebuild the match skeleton from the save header, then
	/// overlay the saved component state and rebuild the derived indexes DeserializeSaveGame
	/// doesn't refill. Mirrors BeginGameplay's world/UI construction but SKIPS the fresh-game
	/// spawn (SpawnStartingBase/tree clusters/AI attach/neutral soldiers/reveal-all) — all of
	/// that is already in the save. Always resumes standalone (MP rejoin is backlog).</summary>
	private void ColdLoad(SaveMeta meta)
	{
		_gameStarted = true;
		_isTutorial = meta.Tutorial;
		_lobby.Hide();

		// Rebuild the world from the saved slot table (seed 0 — the RNG state is restored from
		// the save payload, so the construction seed is irrelevant here).
		string? templatesPath = FindTemplatesPath();
		_sim.InitWorld(templatesPath, seed: 0, meta.LocalPlayerId, NetRole.Standalone, meta.Slots);

		BuildSessionUi(meta.LocalPlayerId);
		if (_isTutorial)
			WireTutorialPanel();
		WireMirageSwapBack();

		// Terrain + spatial-index bounds + passability + pathfinder grid, sized to the real map.
		SetupTerrain(meta.MapPath);

		// Overlay the saved component state, re-injecting the runtime managers each component
		// needs before deserialization (same prepareComponent as QuickLoad). The player registry
		// round-trips inside the payload itself.
		var turn = SaveGameManager.Load(_sim, meta.Slot, prepareComponent: comp =>
		{
			if (comp is ZeroAD.Sim.Components.LosManagerComponent los)
				los.Attach(_sim.Range);
			if (comp is ZeroAD.Sim.Components.AIComponent ai)
				ai.Configure(_sim.Sim, _sim.NetTurn);
			if (comp is ZeroAD.Sim.Components.StatisticsTrackerComponent st)
				st.Attach(_sim.Sim);
		});
		if (turn == null)
			throw new System.InvalidOperationException($"save payload failed to load: {meta.Slot}");

		// Rebuild the two spatial indexes DeserializeSaveGame bypasses (obstructions + range/LOS),
		// THEN rebuild visuals (whose RegisterForLos needs the range index populated).
		_sim.RebuildSpatialIndexesAfterLoad();
		_sim.RebuildAllVisuals();

		// No saved camera in v1: frame the local player's first owned entity (its base).
		FocusCameraOnLocalPlayer();
		// 世界已完整(组件+索引+视觉全部重建):放行回合推进(同 BeginGameplayScenario 闸门)。
		_sim.SimulationRunning = true;
		GD.Print($"[LoadGame] cold-loaded '{meta.Slot}' (turn {turn}, map {meta.MapPath})");
	}

	/// <summary>Cold-load camera: focus the local player's first owned in-world entity
	/// (its town centre / starting units) instead of the map centre.</summary>
	private void FocusCameraOnLocalPlayer()
	{
		foreach (var e in _sim.Range.GetEntitiesByPlayer((int)_sim.LocalPlayerId))
		{
			var pos = _sim.Sim.QueryInterface<PositionComponent>(e);
			if (pos == null) continue;
			SetCameraFocus(new Vector3(
				pos.Position.X.ToFloat(), pos.Position.Y.ToFloat(), pos.Position.Z.ToFloat()));
			return;
		}
	}

	private void OnTutorialMessage(TutorialNotification notification)
	{
		_tutorialPanel.UpdateTutorial(
			notification.Instructions,
			notification.Warning,
			notification.ReadyButton,
			notification.Leave);
	}

	private void QuitTutorial()
	{
		// 退教程回主菜单。不再 ReloadCurrentScene:那会重读 GameLaunchConfig.Mode=Tutorial 又开局。
		GetNode<GameLaunchConfig>("/root/GameLaunchConfig").Reset();
		GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
	}

	private string? FindTemplatesPath()
	{
		string projRoot = ProjectSettings.GlobalizePath("res://");
		var candidates = new[]
		{
			System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "binaries", "data", "mods", "public", "simulation", "templates")),
			System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "..", "binaries", "data", "mods", "public", "simulation", "templates")),
		};
		foreach (string dir in candidates)
		{
			if (System.IO.Directory.Exists(dir))
			{
				GD.Print($"Found templates at: {dir}");
				return dir;
			}
		}
		GD.PrintErr("FindTemplatesPath: templates dir not found under binaries/data/mods/public/simulation/templates");
		return null;
	}

	private void SetupTerrain(string? pmpRelPath = null)
	{
		// 随机地图：路径以 "random/" 开头 → 走 rmgen 生成
		if (pmpRelPath != null && pmpRelPath.StartsWith("random/"))
		{
			string mapName = pmpRelPath.Substring("random/".Length);
			SetupRmgenTerrain(mapName);
			return;
		}

		// Track the rel path actually used so a save can rebuild this terrain on cold-load
		// (embedded in the v6 save header; generated-terrain saves leave MapPath=null).
		string? mapRel = pmpRelPath;
		string? pmpPath = pmpRelPath != null ? FindDataPath(pmpRelPath) : null;
		if (pmpPath == null)
		{
			if ((pmpPath = FindDataPath("maps/scenarios/arcadia.pmp")) != null) mapRel = "maps/scenarios/arcadia.pmp";
			else if ((pmpPath = FindDataPath("maps/scenarios/laconia_01.pmp")) != null) mapRel = "maps/scenarios/laconia_01.pmp";
		}

		if (pmpPath != null)
		{
			try
			{
				var pmp = PmpMap.Load(pmpPath);
				var terrainNode = TerrainRenderer.CreateFromHeightmap(pmp);
				// 地形顶点已预翻转为世界坐标(TerrainRenderer 注释):挂场景根(无负 scale),
				// 两个渲染器都走原生光照/受影;阴影直接自投,无需镜像代理。
				AddChild(terrainNode);
				_worldRoot.Position = new Vector3(0f, 0f, pmp.MapSizeMeters);
				// 雾/领土 overlay:同网格透明 MIX 层(+3cm 防 z-fighting)。地形本体已是
				// 烘焙 StandardMaterial3D(受影);雾变暗=朝黑 alpha(=乘法),领土=玩家色边界。
				var fogOverlay = new MeshInstance3D
				{
					Mesh = terrainNode.Mesh,
					Position = new Vector3(0f, 0.03f, 0f),
					CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
					Name = "TerrainFogOverlay",
					MaterialOverride = new ShaderMaterial
					{
						Shader = GD.Load<Shader>("res://Shaders/fog_territory_overlay.gdshader"),
					},
				};
				AddChild(fogOverlay);
				_sim.FogWorld.Attach(fogOverlay, pmp.MapSizeMeters);
				_sim.TerritoryWorld.Attach(fogOverlay, pmp.MapSizeMeters);
				TerrainHeightService.Set(pmp.GetHeightWorld, pmp.MapSizeMeters);
				float h = pmp.GetHeightWorld(130, 122);
				_camera.SetFocus(new Vector3(130, h, 122));
				GD.Print($"Loaded PMP terrain: {pmpPath} ({pmp.PatchesPerSide} patches, {pmp.MapSizeMeters}m, height at spawn: {h:F1}m)");

				string? xmlPath = pmpPath.Replace(".pmp", ".xml");
				// 地图 Environment 光照(太阳方向/色 + 环境光 + 雾色,公式对齐 CLightEnv);
				// 镜像世界后太阳必须随之镜像,否则面向相机的坡面整体背光发暗。
				(MapEnvironment.LoadFromXml(xmlPath) ?? MapEnvironment.Default).Apply(_light, _env);
				var water = WaterRenderer.LoadWaterFromXml(xmlPath);
				float waterHeight = water?.Height ?? -999f;
				if (water != null)
				{
					var waterMesh = WaterRenderer.CreateWaterPlane(water, pmp.MapSizeMeters);
					_worldRoot.AddChild(waterMesh);
					GD.Print($"Water: height={water.Height:F1}m color={water.Color}");
				}

				// Record the authoritative sim-side water height (matches CCmpWaterManager).
				// The passability grid below is still baked from it for now; a future pass will
				// derive tiles dynamically from (terrainHeight, waterHeight).
				if (water != null)
					_sim.Sim.Water.SetWaterLevel(ZeroAD.Sim.Maths.Fixed.FromFloat(waterHeight));

				// Fill the sim-side passability grid from the heightmap: any tile whose terrain
				// height is at/below the water level is Water, everything else is Land. This drives
				// BuildRestrictions (can't build on water) and Footprint spawn placement.
				FillPassabilityFromPmp(pmp, waterHeight);
				_sim.MapPath = mapRel; // 冷加载重建本地形的契约字段(存档头 v6)

				return;
			}
			catch (System.Exception e)
			{
				GD.PrintErr($"PMP load failed: {e.Message}, falling back to generated terrain");
			}
		}

		var map = MapGenerator.GenerateContinents(8, 42);
		// No fog attach here: the generated mesh emits no UVs and uses vertex-color albedo,
		// which the fog shader can't sample — fog stays a PMP-terrain feature for now.
		// 顶点已预翻转世界坐标(MapGenerator 注释):挂场景根,无负 scale,阴影直接自投。
		var genMesh = MapGenerator.CreateMeshFromGenerated(map);
		AddChild(genMesh);
		float genWorldSize = (map.VerticesPerSide - 1) * map.TileSize;
		_worldRoot.Position = new Vector3(0f, 0f, genWorldSize);
		TerrainHeightService.Set((x, z) =>
		{
			int gx = (int)(x / map.TileSize);
			int gz = (int)(z / map.TileSize);
			return map.GetHeight(gx, gz);
		}, genWorldSize);
		MapEnvironment.Default.Apply(_light, _env);
		_camera.SetFocus(new Vector3(130, 0, 122));
		// Generated terrain has no water by default; mark everything land so placement still works.
		FillPassabilityAllLand();
		GD.Print("Using generated terrain (no PMP found)");
	}

	/// <summary>Build a [MapSize,MapSize] passability grid from the PMP heightmap + water level and
	/// <summary>随机地图生成（rmgen C#）。调 MapRegistry.Generate → MapExport → PmpMap → TerrainRenderer。
	/// 接入 SetupTerrain 的 "random/" 路径前缀分支。</summary>
	private void SetupRmgenTerrain(string mapName)
	{
		GD.Print($"[Main] Generating random map: {mapName}");
		var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		uint seed = cfg.Seed;
		int mapSize = 192;

		var rng = new ZeroAD.Sim.RmgenMath.RmgenRng(seed);
		var settings = new ZeroAD.Sim.Rmgen.Common.MapSettings
		{
			Size = mapSize,
			Seed = seed,
			CircularMap = false,
			DataRoot = FindDataRoot(),   // biome JSON(rmbiome/generic/*.json)经 junction 读取
		};
		// 玩家 civ 列表:gaia + 冻结槽位表(MP 双端同表 → 同图;SP 来自选图面板/默认 1v1)。
		// 原硬编码 gaia/athen/spart 会让 gaul 玩家的出生基地按 spart 生成。
		settings.PlayerData.Add(new ZeroAD.Sim.Rmgen.Common.PlayerData { Civ = "gaia" });
		if (_worldSlots != null)
		{
			foreach (var slot in _worldSlots.OrderBy(s => s.PlayerId))
			{
				if (slot.Kind == ZeroAD.Sim.Net.PlayerSlotKind.Closed) continue;
				settings.PlayerData.Add(new ZeroAD.Sim.Rmgen.Common.PlayerData { Civ = slot.Civ });
			}
		}
		if (settings.PlayerData.Count == 1)   // 无槽位信息(教程/冷加载):沿用旧默认
		{
			settings.PlayerData.Add(new ZeroAD.Sim.Rmgen.Common.PlayerData { Civ = "athen" });
			settings.PlayerData.Add(new ZeroAD.Sim.Rmgen.Common.PlayerData { Civ = "spart" });
		}

		var export = ZeroAD.Sim.Rmgen.Maps.MapRegistry.Generate(mapName, rng, settings);
		if (export == null)
		{
			GD.PrintErr($"[Main] Unknown random map type: {mapName}, falling back to arcadia");
			SetupTerrain(null);
			return;
		}

		// MapExport → PmpMap 适配(共享实现,封装 VerticesPerSide/TileTex2 两个坑)
		var pmp = PmpMap.FromExport(export);

		// 地形渲染（复用 PMP 路径）
		var terrainNode = TerrainRenderer.CreateFromHeightmap(pmp);
		AddChild(terrainNode);
		_worldRoot.Position = new Vector3(0f, 0f, pmp.MapSizeMeters);

		var fogOverlay = new MeshInstance3D
		{
			Mesh = terrainNode.Mesh,
			Position = new Vector3(0f, 0.03f, 0f),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Name = "TerrainFogOverlay",
			MaterialOverride = new ShaderMaterial
			{
				Shader = GD.Load<Shader>("res://Shaders/fog_territory_overlay.gdshader"),
			},
		};
		AddChild(fogOverlay);
		_sim.FogWorld.Attach(fogOverlay, pmp.MapSizeMeters);
		_sim.TerritoryWorld.Attach(fogOverlay, pmp.MapSizeMeters);
		TerrainHeightService.Set(pmp.GetHeightWorld, pmp.MapSizeMeters);

		// 可通行性(rmgen 陆水:超过水面高度=Land,否则 Water)+ 顶点高度网格。
		FillPassabilityAllLand(pmp);

		// 放置实体（从 MapExport.Entities）。rmgen 实体坐标单位是 TILES——上游
		// MapReader::ParseEntities ×TERRAIN_TILE_SIZE 转米;不乘 4 会把全部实体挤进
		// 西南角 192×192m（地图实际 768m）,点击全落空。
		foreach (var ent in export.Entities)
		{
			float x = (float)ent.Position.X * PmpMap.TileSize;
			float z = (float)ent.Position.Y * PmpMap.TileSize;
			float yaw = (float)ent.Orientation;
			try
			{
				if (ent.TemplateName.StartsWith("actor|", System.StringComparison.Ordinal))
				{
					// 装饰物(actor| 前缀):纯视觉不进 sim,同 scenario actor| 路径。
					_sim.SpawnDecorative(ent.TemplateName.Substring("actor|".Length), x, z, yaw);
					continue;
				}
				if (ent.TemplateName.StartsWith("trigger/trigger_point_", System.StringComparison.Ordinal))
				{
					// 触发点(trigger_point_X):注册进触发系统(地图脚本的生成/区域锚点),
					// 不生成实体(原版 TriggerPoint 实体只作位置注册)。
					string tref = ent.TemplateName.Substring("trigger/trigger_point_".Length);
					_sim.Sim.Triggers.RegisterTriggerPoint(tref,
						new ZeroAD.Sim.Maths.FixedVector2D(
							ZeroAD.Sim.Maths.Fixed.FromFloat(x), ZeroAD.Sim.Maths.Fixed.FromFloat(z)));
					continue;
				}
				// 属主随 rmgen PlayerID(上游 ParseEntities 同款)——玩家基地/起始单位归属。
				var eid = _sim.SpawnFromTemplate(ent.TemplateName, x, z, ent.PlayerID);
				if (_sim.EntityNodes.TryGetValue(eid, out var node) && yaw != 0f)
					node.Rotation = new Vector3(0, yaw, 0);
			}
			catch (System.Exception ex) { GD.PushWarning($"[Main] rmgen entity spawn failed: {ent.TemplateName}: {ex.Message}"); }
		}

		_sim.MapPath = $"random/{mapName}";
		// 地图脚本(_triggers.js 移植件):触发点已注册完毕,安装并跑 OnInit。
		_sim.InitMapScript(mapName);
		GD.Print($"[Main] rmgen terrain ready: {mapName} ({export.Size}×{export.Size}, {export.Entities.Count} entities)");
	}

	/// hand it to the sim-side TerrainComponent. Tiles at/below water are Water, the rest Land.
	/// Also reconfigures TerrainComponent + ObstructionManager bounds to the real map size — they
	/// default to 256m (64 tiles) but real maps are larger (tutorial = 768m), and without this the
	/// placement checks wrongly flag everything in-bounds as FailOutOfBounds.</summary>
	private void FillPassabilityFromPmp(PmpMap pmp, float waterHeight)
	{
		var terrain = _sim.Terrain;
		if (terrain == null) return;

		// Reconfigure terrain dimensions to the actual map, then size the grid to match.
		int tilesPerSide = pmp.TilesPerSide;
		terrain.Configure(tilesPerSide, PmpMap.TileSize);
		var grid = new ZeroAD.Sim.Components.TerrainClass[tilesPerSide, tilesPerSide];
		for (int tz = 0; tz < tilesPerSide; tz++)
			for (int tx = 0; tx < tilesPerSide; tx++)
			{
				float wx = (tx + 0.5f) * terrain.TileSize;
				float wz = (tz + 0.5f) * terrain.TileSize;
				float groundH = pmp.GetHeightWorld(wx, wz);
				grid[tx, tz] = groundH <= waterHeight
					? ZeroAD.Sim.Components.TerrainClass.Water
					: ZeroAD.Sim.Components.TerrainClass.Land;
			}
		terrain.SetPassabilityGrid(grid);

		// 顶点高度网格(PMP heightmap 逐点;Attack 高度差/单位 Y 贴地的数据源)。
		var heights = new ZeroAD.Sim.Maths.Fixed[tilesPerSide + 1, tilesPerSide + 1];
		for (int tz = 0; tz <= tilesPerSide; tz++)
			for (int tx = 0; tx <= tilesPerSide; tx++)
				heights[tx, tz] = ZeroAD.Sim.Maths.Fixed.FromFloat(pmp.GetHeight(tx, tz));
		terrain.SetHeightGrid(heights);

		// Match the obstruction + range spatial-index world bounds to the real map so queries
		// don't clamp to the old 256m limit. SetBounds re-indexes existing shapes.
			float worldM = pmp.MapSizeMeters;
			var f0 = ZeroAD.Sim.Maths.Fixed.Zero;
			var f1 = ZeroAD.Sim.Maths.Fixed.FromFloat(worldM);
			_sim.Obstructions.SetBounds(f0, f0, f1, f1);
			// The fog-of-war vertex grid must cover the real map too (same bounds as the
			// spatial index — one LosGrid vertex per 4m).
			_sim.Range.SetBounds(f1);
			_sim.Territory.SetBounds((int)worldM);

			// Build the M3 pathfinding pipeline (passability grid → hierarchical connectivity →
			// A*) now that terrain + obstructions reflect the real map.
			_sim.Pathfinder.RebuildGrid();
		}

	private void FillPassabilityAllLand(PmpMap? pmp = null)
	{
		var terrain = _sim.Terrain;
		if (terrain == null) return;
		int n = terrain.MapSize;
		var grid = new ZeroAD.Sim.Components.TerrainClass[n, n];
		// Default Land (0) is already the zero value, so no need to fill explicitly.
		terrain.SetPassabilityGrid(grid);

		// rmgen 适配 PMP 带高度图 → 填顶点高度网格(Attack 高度差/Y 贴地)。
		if (pmp != null)
		{
			var heights = new ZeroAD.Sim.Maths.Fixed[n + 1, n + 1];
			for (int tz = 0; tz <= n; tz++)
				for (int tx = 0; tx <= n; tx++)
					heights[tx, tz] = ZeroAD.Sim.Maths.Fixed.FromFloat(pmp.GetHeight(tx, tz));
			terrain.SetHeightGrid(heights);
		}

		// Match the obstruction bounds to the generated map, then build the pathfinding grid
		// (the PMP path does the same in FillPassabilityFromPmp). Without this, the pathfinder's
		// grid stays null and ComputePath returns empty paths — units would only ever move in
		// straight lines, ignoring terrain and obstructions.
		float worldM = n * terrain.TileSize;
		var f0 = ZeroAD.Sim.Maths.Fixed.Zero;
		var f1 = ZeroAD.Sim.Maths.Fixed.FromFloat(worldM);
		_sim.Obstructions.SetBounds(f0, f0, f1, f1);
		_sim.Range.SetBounds(f1);
		_sim.Pathfinder.RebuildGrid();
	}

	private string? FindDataPath(string relPath)
	{
		string projRoot = ProjectSettings.GlobalizePath("res://");
		var candidates = new[]
		{
			System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "binaries", "data", "mods", "public", relPath)),
			System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "..", "binaries", "data", "mods", "public", relPath)),
		};
		foreach (var p in candidates)
			if (System.IO.File.Exists(p))
				return p;
		return null;
	}

	private string? FindDataRoot()
	{
		string projRoot = ProjectSettings.GlobalizePath("res://");
		var candidates = new[]
		{
			System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "binaries", "data", "mods", "public")),
			System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "..", "binaries", "data", "mods", "public")),
		};
		foreach (var p in candidates)
			if (System.IO.Directory.Exists(p))
				return p;
		return null;
	}

	private void SetupTutorialWorld()
	{
		GD.Print("[Tutorial] SetupTutorialWorld: loading terrain...");
		SetupTerrain("maps/tutorials/introductory_tutorial.pmp");
		GD.Print("[Tutorial] terrain loaded");

		string? dataRoot = FindDataRoot();
		GD.Print($"[Tutorial] dataRoot={dataRoot ?? "null"}");
		if (dataRoot != null)
		{
			GD.Print("[Tutorial] loading scenario...");
			var scenario = _sim.LoadTutorialScenario(dataRoot);
			if (scenario != null)
			{
				GD.Print($"[Tutorial] scenario loaded: {scenario.Entities.Count} entities, camera=({scenario.CameraX},{scenario.CameraZ})");
				// 开局视角 = 场景作者机位(Position + Rotation + Declination,原版 GameView
				// 语义);无 Camera 元素时回退聚焦 P1 市政厅。
				if (scenario.HasCamera)
				{
					var camPos = new Vector3(scenario.CameraX, scenario.CameraY, scenario.CameraZ);
					_camera.PlaceFromScenarioCamera(camPos, scenario.CameraRotation, scenario.CameraDeclination);
					GD.Print($"[Tutorial] restored scenario camera pose {camPos} rot={scenario.CameraRotation:F2} decl={scenario.CameraDeclination:F2}");
				}
				else
				{
					float focusX = scenario.CameraX, focusZ = scenario.CameraZ;
					foreach (var ent in scenario.Entities)
					{
						if (ent.Player != 1 || !ent.IsSimulationEntity) continue;
						if (ent.Template.Contains("civil_centre") || ent.Template.Contains("civic_centre"))
						{
							focusX = ent.X; focusZ = ent.Z;
							GD.Print($"[Tutorial] focusing P1 civic centre at ({focusX},{focusZ})");
							break;
						}
					}
					float h = TerrainHeightService.Sample(focusX, focusZ);
					_camera.SetFocus(new Vector3(focusX, h, focusZ));
				}
			}
			else
			{
				GD.PrintErr("[Tutorial] LoadTutorialScenario returned null!");
			}
		}
		else
		{
			GD.PrintErr("[Tutorial] FindDataRoot returned null — scenario cannot load");
		}

		GD.Print("[Tutorial] StartTutorial...");
		_sim.StartTutorial();
		GD.Print("[Tutorial] showing panel...");
		_tutorialPanel.ShowTutorial();
		GD.Print("[Tutorial] SetupTutorialWorld complete");
	}

	/// <summary>Deterministic corner start positions, shared by every peer (Task #10). P1/P2 are
	/// Arcadia's authored starts (NE/SW); P3/P4 are the symmetric NW/SE corners so 3–4 player
	/// games still spread out. Same table on both peers → same world from the same seed.</summary>
	private static readonly (float x, float z)[] StartPositions =
	{
		(604f, 637f),  // P1 — Arcadia NE (authored)
		(104f, 147f),  // P2 — Arcadia SW (authored)
		(104f, 637f),  // P3 — symmetric NW
		(604f, 147f),  // P4 — symmetric SE
	};

	/// <summary>One player's starting base: civil centre + 3 villagers + 2 spearmen + 1 cavalry,
	/// laid out relative to the corner. Unified on real templates when available (deterministic
	/// across peers since the template cache is identical); falls back to fake-template dev
	/// spawns otherwise. Owner is stamped so the owned-list scan + SimCommandExecutor routing
	/// (which key off OwnershipComponent) find these units.</summary>
	private void SpawnStartingBase(int playerId, string civ, float x, float z)
	{
		bool useRealTemplates = _sim.Templates != null;
		if (useRealTemplates)
		{
			_sim.AssignOwner(_sim.SpawnFromTemplate($"structures/{civ}/civil_centre", x, z), playerId);
			_sim.AssignOwner(_sim.SpawnFromTemplate($"units/{civ}/support_female_citizen", x + 8, z - 6), playerId);
			_sim.AssignOwner(_sim.SpawnFromTemplate($"units/{civ}/support_female_citizen", x + 12, z - 6), playerId);
			_sim.AssignOwner(_sim.SpawnFromTemplate($"units/{civ}/support_female_citizen", x + 16, z - 6), playerId);
			_sim.AssignOwner(_sim.SpawnFromTemplate($"units/{civ}/infantry_spearman_b", x + 8, z - 12), playerId);
			_sim.AssignOwner(_sim.SpawnFromTemplate($"units/{civ}/infantry_spearman_b", x + 12, z - 12), playerId);
			_sim.AssignOwner(_sim.SpawnFromTemplate($"units/{civ}/cavalry_swordsman_b", x + 16, z - 12), playerId);
		}
		else
		{
			_sim.AssignOwner(_sim.SpawnBuilding(x, z, "Town Center"), playerId);
			for (int i = 0; i < 5; i++)
				_sim.AssignOwner(_sim.SpawnUnit(x + 8 + i * 4, z - 6, isVillager: true), playerId);
			for (int i = 0; i < 3; i++)
				_sim.AssignOwner(_sim.SpawnUnit(x + 8 + i * 4, z - 12, isSoldier: true), playerId);
		}
	}

	/// <summary>18-tree cluster around a base. The AI's EconomyManager FindNearest-scans for the
	/// nearest wood; without local wood a base's villagers idle (read as "no animation" since an
	/// idle villager only runs the subtle idle loop — it needs a gather target to walk/chop).</summary>
	private void SpawnTreeCluster(float cx, float cz, bool useRealTemplates)
	{
		for (int i = 0; i < 18; i++)
		{
			float angle = i * 0.4f;
			float dist = 30 + (i % 3) * 8;
			if (useRealTemplates)
				_sim.SpawnFromTemplate("gaia/tree/oak", cx + Mathf.Cos(angle) * dist, cz + Mathf.Sin(angle) * dist);
			else
				_sim.SpawnTree(cx + Mathf.Cos(angle) * dist, cz + Mathf.Sin(angle) * dist);
		}
	}

	private void SetupGameWorld(uint playerId, IReadOnlyList<ZeroAD.Sim.Net.PlayerSlotSetup> slots, bool isMultiplayer)
	{
		// 选图来源:SP = GameLaunchConfig.MapPath(选图面板/ZEROAD_MAP);MP = host 大厅冻结、
		// GameStart 下发并写回 cfg(StartMpGameplay)——双端同图,皆确定性(pmp 同数据,
		// random 同 seed 同槽位表),不构成 OOS 源。
		var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		string? mapPath = string.IsNullOrEmpty(cfg.MapPath) ? null : cfg.MapPath;
		SetupTerrain(mapPath);
		bool useRealTemplates = _sim.Templates != null;

		// XML 实体地图(scenario/skirmish 通用):XML 含 sim 实体 → 全部实体由地图提供
		// (skirmish 占位经 SkirmishReplacer 按槽位文明替换;scenario 作者实体原样生成,
		// XML civ 覆盖玩家文明),跳过沙盒出生基地/树簇/中立兵。
		// random 地图:rmgen 自带玩家基地(CC+起始单位)与全部资源,同样跳过沙盒生成,
		// 否则玩家会拿到双基地( rmgen 环形位 + 沙盒固定角落 )。
		// 无 XML 实体 / 默认图走既有沙盒生成,行为不变。
		bool spawnedFromMap = false;
		bool isRandomMap = mapPath != null && mapPath.StartsWith("random/", System.StringComparison.Ordinal);
		if (mapPath != null && !isRandomMap
			&& FindDataPath(mapPath) != null)   // pmp 缺失时 SetupTerrain 已回退默认图,不能再配 skirmish XML
		{
			string? dataRoot = FindDataRoot();
			if (dataRoot != null)
			{
				string relNoExt = mapPath.EndsWith(".pmp", System.StringComparison.OrdinalIgnoreCase)
					? mapPath[..^4] : mapPath;
				var scenario = _sim.LoadMapScenario(dataRoot, relNoExt);
				if (scenario != null)
				{
					spawnedFromMap = true;
					// 队伍外交播种(同教程路径:同队互盟,否则敌对)
					var teams = new Dictionary<int, int>();
					foreach (var pd in scenario.Players)
						teams[pd.PlayerId] = pd.Team;
					_sim.Sim.Players.SeedDiplomacyFromTeams(teams);
					// 场景作者机位恢复(Arcadia 等 scenarios/* 的开局视角;此前从不恢复,
					// 开局停在 SetupTerrain 的硬编码焦点 (130,122)——用户报视角错)。
					if (scenario.HasCamera)
						_camera.PlaceFromScenarioCamera(
							new Vector3(scenario.CameraX, scenario.CameraY, scenario.CameraZ),
							scenario.CameraRotation, scenario.CameraDeclination);
				}
			}
		}

		if (!spawnedFromMap && !isRandomMap)
		{
			// Every non-Closed slot gets a starting base + local tree cluster in its deterministic
			// corner. This also fixes the old MP bug where slot 2 was an inert shell with no base or
			// units: now every player — human or AI — starts with a TC, villagers, and soldiers.
			foreach (var slot in slots)
			{
				if (slot.Kind == ZeroAD.Sim.Net.PlayerSlotKind.Closed) continue;
				int idx = slot.PlayerId - 1;
				if ((uint)idx >= StartPositions.Length) continue;
				var (bx, bz) = StartPositions[idx];
				SpawnStartingBase(slot.PlayerId, slot.Civ, bx, bz);
				SpawnTreeCluster(bx, bz, useRealTemplates);
			}

			// Ownerless neutral soldiers — mid-map (768m world → centre ~384) so they overlap no base.
			_sim.SpawnUnit(380, 380, isSoldier: true);
			_sim.SpawnUnit(385, 385, isSoldier: true);
		}

		// AI brains are kernel-resident (Phase 2): AIComponent on the player entity → brain state
		// enters the OOS hash + save streams; TickAI advances it each lockstep turn; commands ride
		// SubmitAiCommand's local _aiBundles channel (each peer generates them identically, never
		// the network). The old `if (!isMultiplayer)` gate is gone — AI slots work in MP and SP.
		foreach (var slot in slots)
		{
			if (slot.Kind == ZeroAD.Sim.Net.PlayerSlotKind.AI)
				_sim.AttachAi(slot.PlayerId);
		}

		// Ownerless neutral soldiers — mid-map (768m world → centre ~384) so they overlap no base.
		// (moved into the !spawnedFromMap branch above — skirmish maps author their own entities)

		// Initial buildings/units were spawned AFTER the map-load RebuildGrid; rebuild once more so
		// pathing accounts for the town centres and any scenario buildings.
		_sim.Pathfinder.RebuildGrid();
		// AI 水陆区域图(Accessibility)随网格定型重建(Petra 海军/码头选址的前置)。
		_sim.RefreshAiAccessibility();

		// Fog: sandbox/SP spawns owner-less world-dev entities with no seers, so reveal the map so
		// the dev world isn't shrouded. MP keeps real fog — each human only sees their own vision.
		_sim.Range.SetLosRevealAll((int)playerId, isMultiplayer ? false : true);

		if (spawnedFromMap || isRandomMap)
		{
			// 地图自带出生点(skirmish 实体 / rmgen 基地):取景本地玩家首个所属实体
			// (其 CC,同 ColdLoad),而非沙盒固定角落。
			FocusCameraOnLocalPlayer();
			return;
		}

		// Frame the player's starting town centre so the game opens on their base. StartPositions
		// is 0-indexed by player id - 1; clamp guards against an out-of-range local player id.
		var (fx, fz) = StartPositions[Math.Clamp((int)playerId - 1, 0, StartPositions.Length - 1)];
		_camera.SetFocus(new Vector3(fx, 0, fz));
	}

	private readonly List<Node3D> _selectionMarkers = new();

	// Rally-point marker (flag + path line). Cached across frames: the actor instantiate +
	// pathfind are too costly to rebuild every frame, so we only rebuild when the selected
	// building, rally position, or civ changes. Separate from the per-frame _selectionMarkers.
	private Node3D? _rallyMarker;
	private (uint buildingId, int rallyXi, int rallyZi, string civ)? _rallyMarkerKey;

	public override void _Process(double delta)
	{
		if (!_gameStarted) return;

		UpdateSelectionMarkers();
		UpdateActionCursor();
		UpdateCursorSpritePosition();

		// FPS 叠层(可见时才读帧率,Engine.GetFramesPerSecond 是上一帧实测值)。
		if (_fpsOverlay?.Visible == true && _fpsLabel != null)
			_fpsLabel.Text = $"{Engine.GetFramesPerSecond():0} FPS";

		// Turn advancement is driven by SimBridge._Process, which honours the lockstep
		// barrier (it only advances when the next turn's bundle has arrived). Nothing to
		// force here.

		TryDebugCapture();
		UpdateBattleMusic(delta);
		UpdatePlaceGhost();
	}

	/// <summary>放置预览 ghost 跟随鼠标 + 套当前 _placeAngle(原版 placement preview 每帧更新)。</summary>
	private void UpdatePlaceGhost()
	{
		if (!_placeBuildingMode || _placeGhost == null) return;
		var vp = GetViewport();
		if (vp == null) return;
		var worldPos = ScreenToWorld(vp.GetMousePosition());
		if (worldPos == null) return;
		// vis 空间:world z 经镜像(_worldRoot)。建筑节点挂在 _worldRoot 下,直接套 vis 坐标。
		float vz = TerrainHeightService.MirrorZ(worldPos.Value.Z);
		_placeGhost.Position = new Vector3(worldPos.Value.X,
			TerrainHeightService.Sample(worldPos.Value.X, worldPos.Value.Z), vz);
		_placeGhost.Rotation = new Vector3(0, _placeAngle, 0);
	}

	// pauseonfocusloss 配置项(原版 PauseOnFocusLoss,仅 SP):窗口失焦自动暂停并显示暂停菜单。
	public override void _Notification(int what)
	{
		if (what != NotificationWMWindowFocusOut) return;
		if (!_gameStarted
			|| _sim.NetTurn.Role != NetRole.Standalone
			|| GetNode<UserConfig>("/root/UserConfig").GetEffective("pauseonfocusloss") != "true"
			|| _pauseMenu is not { Visible: false }
			|| AnyModalPanelOpen())   // 模态面板(科技树等)的 Popup 会触发失焦——不打扰
			return;
		OpenPauseMenu();
	}

	/// <summary>任一模态面板开着?(失焦自动暂停的豁免:面板的下拉 Popup 会抢焦,
	/// 不应因此弹暂停菜单盖住面板)。速度弹出条非模态但含下拉,同样豁免。</summary>
	private bool AnyModalPanelOpen()
		=> _structreePanel is { Visible: true }
		|| (_hud != null && _hud.GameSpeedPopoverOpen)
		|| _diplomacyPanel is { Visible: true }
		|| _tradePanel is { Visible: true }
		|| _matchSettingsPanel is { Visible: true };

	public override void _ExitTree()
	{
		// 静态注册的场景节点随场景销毁——注销防悬垂(下个 Main._Ready 重新注册)。
		OptionsApplier.RegisterSceneNodes(null, null);
		// ConfigChanged 订阅挂的是本节点方法,UserConfig 是 autoload 长存——退订防死引用。
		GetNode<UserConfig>("/root/UserConfig").ConfigChanged -= OnUserConfigChanged;
		// 软件光标:恢复 OS 光标可见(主菜单/桌面需要)。
		Input.MouseMode = Input.MouseModeEnum.Visible;
		// 建造拒绝事件订阅(BuildSessionUi 挂)——退订防死引用。
		if (_sim != null)
		{
			_sim.Sim.Events.PlayerCommand -= OnPlayerCommandEvent;
			_sim.Sim.Events.PlayerDefeated -= OnPlayerDefeatedChat;
			_sim.Sim.Events.TrainingFinished -= OnTrainingFinishedSound;
			_sim.Sim.Events.PlayerWon -= OnPlayerWonSound;
			_sim.Sim.Events.PlayerDefeated -= OnPlayerDefeatedSound;
			_sim.Sim.Events.AttackLaunched -= OnAttackLaunchedSound;
			_sim.Sim.Events.AttackLanded -= OnAttackAlert;
			_sim.TriggerMessage -= OnTriggerMessage;
			_sim.Sim.Events.CeasefireStarted -= OnCeasefireStarted;
			_sim.Sim.Events.CeasefireEnded -= OnCeasefireEnded;
		}
		if (_mp != null)
			_mp.OnChatReceived -= OnMpChatReceived;
	}

	/// <summary>MP 收到聊天 → 转发到 SimEventBus（ChatPanel 统一订阅展示）。</summary>
	private void OnMpChatReceived(int playerId, string text)
		=> _sim.Events.RaiseChatMessage(new ZeroAD.Sim.Events.ChatMessageEvent
		{ Kind = ZeroAD.Sim.Events.ChatMessageEvent.KindType.Message, SenderPlayerId = playerId, Text = text });

	/// <summary>玩家被击败 → 系统聊天消息（"Player N was defeated"）。</summary>
	private void OnPlayerDefeatedChat(PlayerDefeatedEvent e)
		=> _sim.Events.RaiseChatMessage(new ZeroAD.Sim.Events.ChatMessageEvent
		{ Kind = ZeroAD.Sim.Events.ChatMessageEvent.KindType.System, Text = $"Player {e.PlayerId} was defeated: {e.Reason}" });

	// ── 音频钩子(具名方法,_ExitTree 退订防悬垂)──

	private int _sessionPlayerId = 1;

	private void OnTrainingFinishedSound(ZeroAD.Sim.Events.TrainingFinishedEvent e)
		=> AudioManager.PlayUnitEvent(_sim.Templates, e.UnitTemplate, "trained");

	private void OnPlayerWonSound(ZeroAD.Sim.Events.PlayerWonEvent e)
	{
		if (e.PlayerId == _sessionPlayerId) AudioManager.PlayJingle(AudioManager.VictoryTrack);
	}

	private void OnPlayerDefeatedSound(ZeroAD.Sim.Events.PlayerDefeatedEvent e)
	{
		if (e.PlayerId == _sessionPlayerId) AudioManager.PlayJingle(AudioManager.DefeatTrack);
	}

	/// <summary>武器音效(发射时刻,模板 attack_melee/attack_ranged 组)+ 战斗计时
	/// (驱动 BATTLE/PEACE 音乐切换,原版 music.js battle state 语义:10s 无战斗回 PEACE)。</summary>
	private double _lastCombatSec = -100;
	private double _musicCheckAccum;

	private void OnAttackLaunchedSound(ZeroAD.Sim.Events.AttackLaunchedEvent e)
	{
		var id = _sim.Sim.QueryInterface<IdentityComponent>(e.Attacker);
		if (id != null && !string.IsNullOrEmpty(id.TemplateName))
			AudioManager.PlayUnitEvent(_sim.Templates, id.TemplateName,
				e.IsRanged ? "attack_ranged" : "attack_melee");
		_lastCombatSec = Time.GetTicksMsec() / 1000.0;
	}

	/// <summary>遇袭警报:己方实体被命中(AttackLanded 含 Target)→ 记录位置,
	/// HUD 警报图标开始闪烁(点击跳转后清除)。原版 alert_panel 的 v1。</summary>
	private void OnAttackAlert(ZeroAD.Sim.Events.AttackLandedEvent e)
	{
		var owner = _sim.Sim.QueryInterface<OwnershipComponent>(e.Target);
		if (owner == null || owner.PlayerId != _sessionPlayerId) return;
		var pos = _sim.Sim.QueryInterface<PositionComponent>(e.Target);
		if (pos == null) return;
		_hud?.SetAlert(pos.Position.X.ToFloat(), pos.Position.Z.ToFloat());
	}

	/// <summary>触发器 ShowMessage → HUD toast(经本地化表,缺译回退原文)。</summary>
	private void OnTriggerMessage(string text) => _hud?.ShowToast(Localization.Tr(text));

	/// <summary>停战开始(原版 AddTimeNotification "You can attack in %(time)s")。</summary>
	private void OnCeasefireStarted(ZeroAD.Sim.Events.CeasefireStartedEvent e)
	{
		int total = (int)e.RemainingSeconds;
		_hud?.ShowToast(Localization.Tr("Ceasefire — you can attack in %(time)s")
			.Replace("%(time)s", $"{total / 60}:{total % 60:00}"));
	}

	/// <summary>停战结束(原版 "You can attack now!")。</summary>
	private void OnCeasefireEnded(ZeroAD.Sim.Events.CeasefireEndedEvent e)
		=> _hud?.ShowToast(Localization.Tr("You can attack now!"));

	private void UpdateBattleMusic(double delta)
	{
		_musicCheckAccum += delta;
		if (_musicCheckAccum < 1.0) return;   // 1s 节流
		_musicCheckAccum = 0;
		bool inBattle = Time.GetTicksMsec() / 1000.0 - _lastCombatSec < 10.0;
		AudioManager.SetBattleMode(inBattle);
	}

	/// <summary>选中/命令语音:取首个选中实体的模板事件(select/order_*;无该事件的
	/// 模板静默)。</summary>
	private void PlaySelectionSound(string eventName)
	{
		if (_selectedEntities.Count == 0) return;
		var first = System.Linq.Enumerable.First(_selectedEntities);
		var id = _sim.Sim.QueryInterface<IdentityComponent>(first);
		if (id == null || string.IsNullOrEmpty(id.TemplateName)) return;
		AudioManager.PlayUnitEvent(_sim.Templates, id.TemplateName, eventName);
	}

	/// <summary>建造拒绝 toast(执行端 PlayerCommandEvent "build-rejected" → 顶部红字;
	/// 只显本地玩家的拒绝)。</summary>
	private void OnPlayerCommandEvent(PlayerCommandEvent e)
	{
		if (e.Type != "build-rejected" && e.Type != "train-rejected") return;
		if (e.Data.TryGetValue("player", out var p) && p is int pid && pid != (int)_sim.LocalPlayerId)
			return;
		string reason = e.Data.TryGetValue("reason", out var r) ? r?.ToString() ?? "" : "";
		if (e.Type == "train-rejected")
		{
			_hud?.ShowToast(reason switch
			{
				"cannot-afford" => "Not enough resources.",
				"pop-limit" => "Population limit reached — build more houses.",
				"entity-limit" => "Training limit reached for this unit.",
				_ => "Cannot train that unit.",
			});
			return;
		}
		_hud?.ShowToast(reason switch
		{
			"cannot-afford" => "Not enough resources.",
			"invalid-placement" => "Cannot place building here.",
			"territory" => "Must be built in connected territory.",
			_ => "Cannot build there.",
		});
	}

	// --- Debug capture (ZEROAD_CAPTURE=1|gather): screenshot + per-entity diagnostics ---
	private int _captureFrames;
	private bool _captureDone;
	private Camera3D? _debugCam;
	private void TryDebugCapture()
	{
		string mode = System.Environment.GetEnvironmentVariable("ZEROAD_CAPTURE") ?? "";
		if (string.IsNullOrEmpty(mode) || _captureDone) return;
		bool gather = mode == "gather";
		bool wide = mode == "wide"; // RTS default camera view — for terrain comparisons
		bool train = mode == "train"; // train a spearman at the CC, verify trained-unit visuals
		_captureFrames++;

		// gather mode: frame 60 orders the first civilian to chop the nearest tree,
		// so the capture lands inside GATHERING (axe prop + chop animation visible).
		if (gather && _captureFrames == 60)
			_sim.DebugOrderFirstCivilianGatherNearest();

		// train mode: frame 60 queues a spearman + a civilian at the first visible
		// civil centre through the real command path.
		if (train && _captureFrames == 60)
		{
			foreach (var kvp in _sim.EntityNodes)
			{
				var ident = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.IdentityComponent>(kvp.Key);
				if (ident?.TemplateName?.Contains("civil_centre") != true) continue;
				int lp = (int)_sim.LocalPlayerId;
				if (_sim.Range.GetLosVisibility(kvp.Key, lp) == ZeroAD.Sim.Components.LosVisibility.Hidden) continue;
				_sim.CommandTrainSoldier(kvp.Key);
				_sim.CommandTrain(kvp.Key);
				break;
			}
		}

		// Mode "1": fixed frames (camera at 175, capture at 180). Mode "wide": RTS
		// camera as-is, capture at 185. Mode "gather": wait until any civilian
		// actually reaches GATHERING (walk time varies with tree distance), spawn
		// the camera that frame, capture the next; hard cap at frame 3000.
		// Mode "train": wait until a trained spearman exists and is visible
		// (training takes ~15s sim), then frame it like the gather camera.
		bool spawnCam;
		bool captureNow;
		if (!gather && !train)
		{
			spawnCam = !wide && _captureFrames == 175 && _debugCam == null;
			captureNow = _captureFrames == (wide ? 600 : 180);
		}
		else
		{
			spawnCam = false;
			if (_debugCam == null && _captureFrames >= (train ? 600 : 900))
			{
				bool ready = false;
				foreach (var kvp in _sim.EntityNodes)
				{
					if (train)
					{
						var ident = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.IdentityComponent>(kvp.Key);
						if (ident?.TemplateName?.Contains("infantry_spearman") != true) continue;
						int lp = (int)_sim.LocalPlayerId;
						if (_sim.Range.GetLosVisibility(kvp.Key, lp) == ZeroAD.Sim.Components.LosVisibility.Hidden) continue;
						ready = true; break;
					}
					var g = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.UnitAIComponent>(kvp.Key)?.FsmStateName ?? "";
					// Trigger on APPROACHING (mid-walk) OR GATHERING so we can capture both the
					// walk cycle and the gather cycle from a single capture session.
					if (g.Contains("GATHER.APPROACHING") || g.Contains("GATHER.GATHERING")) { ready = true; break; }
				}
				spawnCam = ready || _captureFrames >= 3000;
			}
			captureNow = _debugCam != null; // the frame after the camera spawned
		}

		// Camera spawn: dedicated debug Camera3D on a visible civilian (RTSCamera._Process
		// fights manual position sets, so we add a separate current camera we control).
		// "wide" instead mounts a high overview above the player's civil centre
		// (RTS camera focus is unreliable in captures) for terrain comparisons.
		if (wide && _debugCam == null && _captureFrames == 175)
		{
			foreach (var kvp in _sim.EntityNodes)
			{
				var ident = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.IdentityComponent>(kvp.Key);
				if (ident?.TemplateName?.Contains("civil_centre") != true) continue;
				int lp = (int)_sim.LocalPlayerId;
				if (_sim.Range.GetLosVisibility(kvp.Key, lp) == ZeroAD.Sim.Components.LosVisibility.Hidden) continue;
				var p = kvp.Value.GlobalPosition;
				_debugCam = new Camera3D();
				AddChild(_debugCam);
				_debugCam.GlobalPosition = p + new Vector3(80f, 160f, 140f);
				_debugCam.LookAt(p, Vector3.Up);
				_debugCam.Current = true;
				break;
			}
		}
		if (spawnCam && _debugCam == null)
		{
			Node3D? firstCiv = null;
			foreach (var kvp in _sim.EntityNodes)
			{
				var ident = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.IdentityComponent>(kvp.Key);
				// train mode frames the trained spearman; other modes frame civilians
				// (ZEROAD_CAPTURE_TARGET overrides the template substring).
				string want = train ? "infantry_spearman"
					: System.Environment.GetEnvironmentVariable("ZEROAD_CAPTURE_TARGET") ?? "support_civilian";
				if (ident?.TemplateName?.Contains(want) != true) continue;
				int lp = (int)_sim.LocalPlayerId;
				if (_sim.Range.GetLosVisibility(kvp.Key, lp) == ZeroAD.Sim.Components.LosVisibility.Hidden) continue;
				firstCiv ??= kvp.Value;
				// In gather mode prefer the civilian that is actually gathering.
				var fsm = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.UnitAIComponent>(kvp.Key)?.FsmStateName ?? "";
				if (gather && !fsm.Contains("GATHER")) continue;
				var p = kvp.Value.GlobalPosition;
				_debugCam = new Camera3D();
				AddChild(_debugCam);
				_debugCam.GlobalPosition = new Vector3(p.X + 4f, p.Y + 3.5f, p.Z + 4f);
				_debugCam.LookAt(p + new Vector3(0, 1f, 0), Vector3.Up);
				_debugCam.Current = true;
				break;
			}
		}

		if (!captureNow) return;
		_captureDone = true;

		string dir = "/tmp/zeroad_debug";
		System.IO.Directory.CreateDirectory(dir);
		// Headless (RasterizerSceneDummy) has no real viewport texture — skip the PNG.
		if (DisplayServer.GetName() != "headless")
			GetViewport().GetTexture().GetImage().SavePng($"{dir}/frame.png");

		var sb = new System.Text.StringBuilder();
		sb.AppendLine($"frame={_captureFrames} entities={_sim.EntityNodes.Count} turn={_sim.NetTurn.CurrentTurn}");
		sb.AppendLine($"camera_pos={_camera.GlobalPosition:F1} camera_focus={_camera.Focus:F1}");
		sb.AppendLine($"debugcam={(_debugCam != null ? _debugCam.GlobalPosition.ToString("F1") : "null")} current={GetViewport().GetCamera3D()?.Name ?? "none"}");
		foreach (var kvp in _sim.EntityNodes)
		{
			var ident = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.IdentityComponent>(kvp.Key);
			string tmpl = ident?.TemplateName ?? ident?.Name ?? "?";
			var fsm = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.UnitAIComponent>(kvp.Key)?.FsmStateName ?? "";
			var gatherer = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.ResourceGatherer>(kvp.Key);
			string gtarget = gatherer?.TargetSupply is EntityId gs ? $" gtarget={gs.Value}" : "";
			var node = kvp.Value;
			var anim = ModelLibrary.FindManualAnimator(node);
			var props = ZeroAD.Godot.Actors.Composition.StatePropSwitcher.Find(node);
			var mesh = _findFirstMesh(node);
			int lp = (int)_sim.LocalPlayerId;
			var vis = _sim.Range.GetLosVisibility(kvp.Key, lp);
			sb.AppendLine($"eid={kvp.Key.Value} tmpl={tmpl} fsm={fsm} pos={node.GlobalPosition:F1}{gtarget} " +
				$"vis={vis} mesh={(mesh != null ? mesh.Name : "none")} " +
				$"anim={(anim != null ? anim.Summary : "none")} clips={(anim != null ? anim.StatesCsv : "")} " +
				$"props={(props != null ? props.Summary : "-")}");
		}
		System.IO.File.WriteAllText($"{dir}/entities.txt", sb.ToString());
		GD.Print($"DEBUG_CAPTURE wrote {dir}/frame.png + entities.txt");

		// Save/load round-trip smoke test (capture mode only).
		if (SaveGameManager.Save(_sim) != null)
		{
			SaveGameManager.Load(_sim, prepareComponent: comp =>
			{
				if (comp is ZeroAD.Sim.Components.LosManagerComponent los)
					los.Attach(_sim.Range);
				if (comp is ZeroAD.Sim.Components.AIComponent ai)
					ai.Configure(_sim.Sim, _sim.NetTurn);
			});
			GD.Print("[SaveLoadTest] round-trip OK");
		}
	}

	private static MeshInstance3D? _findFirstMesh(Node n)
	{
		if (n is MeshInstance3D m) return m;
		foreach (var c in n.GetChildren())
		{
			var r = _findFirstMesh(c);
			if (r != null) return r;
		}
		return null;
	}

	/// <summary>
	/// OOS handler: write a binary + text state dump so the two peers' dumps can be
	/// diffed to locate the divergence. Triggered via the host's broadcast once it
	/// detects a state-hash mismatch.
	/// </summary>
	private void OnOOSDetected(string msg)
	{
		string dir = ProjectSettings.GlobalizePath("user://oos");
		var (bin, txt) = ZeroAD.Sim.Serialization.StateDump.WriteAll(
			_sim.Sim, dir, _sim.NetTurn.CurrentTurn, _sim.LocalPlayerId);
		GD.PrintErr($"OOS: {msg}\nState dumped:\n  {txt}\n  {bin}");
	}

	private void UpdateSelectionMarkers()
	{
		foreach (var m in _selectionMarkers)
			m.QueueFree();
		_selectionMarkers.Clear();

		foreach (var eid in _selectedEntities)
		{
			var node = _sim.EntityNodes.GetValueOrDefault(eid);
			if (node == null) continue;
			// Read identity/owner/health through the GuiInterface facade.
			var st = _sim.Gui.GetEntityState(eid);
			bool isBuilding = st?.IsBuilding ?? false;
			int ownerPlayerId = st?.OwnerPlayerId ?? -1;
			int healthMax = st?.HealthMax ?? 0;
			float healthFraction = st?.HealthFraction ?? 0f;

			// 选择框颜色(原版:属主玩家色;gaia = 白)。此前非 P1 一律红——gaia 动物/树也红。
			Color friendlyColor = ownerPlayerId <= 0
				? Colors.White
				: SimBridge.GetPlayerColor(ownerPlayerId);
			Color enemyColor = SimBridge.GetPlayerColor(ownerPlayerId);

			// 建筑选择框 = footprint 精确形状/尺寸(原版 SelectionShape=<Footprint/>):
			// 方形 → 半宽/半深矩形;圆形 → 半径圆环(如 tholos 圆形神庙)。
			// 无 footprint 组件才回退 10。
			MeshInstance3D ring;
			if (isBuilding)
			{
				var fp = _sim.Sim.QueryInterface<FootprintComponent>(eid);
				if (fp != null && fp.Shape == FootprintShape.Circle)
				{
					ring = SelectionRing.Create(fp.Size0.ToFloat(), friendlyColor, enemyColor,
						SelectionRing.Shape.Circle);
				}
				else
				{
					float halfX = fp != null ? fp.Size0.ToFloat() * 0.5f : 10f;
					float halfZ = fp != null ? fp.Size1.ToFloat() * 0.5f : 10f;
					ring = SelectionRing.CreateRect(halfX, halfZ, friendlyColor);
				}
			}
			else
			{
				ring = SelectionRing.Create(2f, friendlyColor, enemyColor,
					SelectionRing.Shape.Circle);
			}
			ring.Position = new Vector3(0, 0.1f, 0);
			node.AddChild(ring);
			_selectionMarkers.Add(ring);

			// 攻击射程圈(原版 RangeOverlay:模板 Attack/Ranged/RangeOverlay 存在时,
			// 选中即显示——CC/箭塔的防御半径;近战无此元素不显示)。颜色 = 属主玩家色
			// (对齐 CCmpRangeOverlayRenderer::UpdateColor → cmpPlayer->GetDisplayedColor),
			// 此前硬编码白色与原版不符。
			var attack = _sim.Sim.QueryInterface<AttackComponent>(eid);
			if (attack is { HasRangeOverlay: true })
			{
				var posC = _sim.Sim.QueryInterface<PositionComponent>(eid);
				if (posC != null)
				{
					var ringColor = SimBridge.GetPlayerColor(ownerPlayerId);
					ringColor.A = 0.75f;
					var rangeRing = SelectionRing.CreateRangeRing(attack.Range,
						posC.Position.X.ToFloat(), posC.Position.Z.ToFloat(),
						ringColor);
					node.AddChild(rangeRing);
					_selectionMarkers.Add(rangeRing);
				}
			}

			if (healthMax > 0)
			{
				// 头顶高度 = 模型 AABB 顶 + 0.3(原版状态条悬于实体顶;固定 2.5/6 对
                // 高塔矮兵都不对)。缓存进 meta,重复选择不重算。
				float topY = BarTopHeight(node);
				var bar = SelectionRing.CreateHealthBar(healthFraction);
				bar.Position = new Vector3(0, topY, 0);
				node.AddChild(bar);
				_selectionMarkers.Add(bar);

				// 占领条(蓝条,血条上方;原版可占领建筑的双条):各玩家 CP 占比分段。
				var capturable = _sim.Sim.QueryInterface<CapturableComponent>(eid);
				float maxCp = capturable?.MaxCapturePoints.ToFloat() ?? 0f;
				if (capturable != null && maxCp > 0f)
				{
					var segs = new List<(float, Color)>();
					int n = System.Math.Min(capturable.CapturePoints.Length, 9);
					for (int p = 0; p < n; p++)
					{
						float cp = capturable.CapturePoints[p].ToFloat();
						if (cp > 0f)
							segs.Add((cp / maxCp, SimBridge.GetPlayerColor(p)));
					}
					var capBar = SelectionRing.CreateCaptureBar(segs);
					capBar.Position = new Vector3(0, topY + 0.45f, 0);
					node.AddChild(capBar);
					_selectionMarkers.Add(capBar);
				}
			}
		}

		// Rally-point marker (flag + path line) is cached across frames — see ReconcileRallyMarker.
		ReconcileRallyMarker();
	}

	/// <summary>实体头顶条高度(原版状态条悬于模型顶):取首个网格 AABB 顶 + 0.3;
	/// 无网格回退按建筑/单位 6/2.2。结果缓存进节点 meta(模型不换不重用算)。</summary>
	private static float BarTopHeight(Node3D node)
	{
		if (node.HasMeta("barTopY"))
			return (float)node.GetMeta("barTopY").AsDouble();
		float top = 0f;
		var meshNode = FindFirstMeshNode(node);
		if (meshNode?.Mesh != null)
		{
			var aabb = meshNode.Mesh.GetAabb();
			top = aabb.End.Y;
		}
		if (top < 0.5f)
			top = 2.2f;   // 无网格兜底(演员缺失的占位)
		float result = top + 0.3f;
		node.SetMeta("barTopY", result);
		return result;
	}

	private static MeshInstance3D? FindFirstMeshNode(Node node)
	{
		foreach (var child in node.GetChildren())
		{
			if (child is MeshInstance3D mi && mi.Mesh != null) return mi;
			var found = FindFirstMeshNode(child);
			if (found != null) return found;
		}
		return null;
	}

	/// <summary>Reconcile the cached rally marker (_rallyMarker) with the current selection:
	/// find the first selected production building carrying a non-zero rally, and rebuild the
	/// flag + path line only when the building/rally/civ changes (对齐原版 0 A.D.). Tearing
	/// down when nothing qualifies keeps pathfinding off the hot path — ComputePath runs once
	/// per rally change, not per frame.</summary>
	private void ReconcileRallyMarker()
	{
		// v1: the first selected building with a set rally point drives the marker.
		EntityId? rallyBuilding = null;
		FixedVector2D rallyPos = default;
		string civ = "athen";
		foreach (var eid in _selectedEntities)
		{
			var rally = _sim.Sim.QueryInterface<RallyPointComponent>(eid);
			if (rally == null || rally.Position.IsZero) continue;
			rallyBuilding = eid;
			rallyPos = rally.Position;
			// Civ from the building template path: structures/{civ}/...
			var id = _sim.Sim.QueryInterface<IdentityComponent>(eid);
			if (id != null)
			{
				var parts = id.TemplateName.Split('/');
				if (parts.Length >= 2 && parts[0] == "structures") civ = parts[1];
			}
			break;
		}

		if (rallyBuilding == null)
		{
			ClearRallyMarker();
			return;
		}

		// Fixed.InternalValue is stable across frames → exact key without float drift.
		var key = (rallyBuilding.Value.Value, rallyPos.X.InternalValue, rallyPos.Y.InternalValue, civ);
		if (_rallyMarkerKey == key) return;
		ClearRallyMarker();

		float rallyX = rallyPos.X.ToFloat();
		float rallyZ = rallyPos.Y.ToFloat();
		float rallyGroundY = TerrainHeightService.Sample(rallyX, rallyZ);

		// Owner colour mirrors the selection-ring friendly/enemy split.
		var own = _sim.Sim.QueryInterface<OwnershipComponent>(rallyBuilding.Value);
		int ownerPlayerId = own?.PlayerId ?? -1;
		Color ownerColor = ownerPlayerId == 1
			? new Color(0.08f, 0.22f, 0.58f) : new Color(0.72f, 0.06f, 0.06f);

		var container = new Node3D();
		_sim.UnitContainer.AddChild(container);
		_rallyMarker = container;
		_rallyMarkerKey = key;

		// Flag: the real per-civ waypoint_flag actor; procedural fallback if it won't load.
		int seed = (int)(rallyBuilding.Value.Value * 2654435761u);   // stable per-building hash
		var flagActor = ActorLoader.Instance.Instantiate(
			$"props/special/common/{civ}_waypoint_flag.xml", seed, ownerColor);
		Node3D flag;
		if (flagActor != null)
		{
			flag = flagActor;
			flag.Position = new Vector3(rallyX, rallyGroundY, rallyZ);
		}
		else
		{
			flag = SelectionRing.CreateRallyFlag(ownerColor);
			flag.Position = new Vector3(rallyX, rallyGroundY + 0.1f, rallyZ);
		}
		container.AddChild(flag);

		// Path line: pathfind building → rally (read-only; mirrors CCmpRallyPointRenderer),
		// then lay a textured ground ribbon along the waypoints.
		var bpos = _sim.Sim.QueryInterface<PositionComponent>(rallyBuilding.Value);
		if (bpos != null)
		{
			var start = new FixedVector2D(bpos.Position.X, bpos.Position.Z);
			var path = _sim.Pathfinder.ComputePath(start, PathGoal.Point(rallyPos.X, rallyPos.Y));
			if (!path.IsEmpty)
			{
				// Waypoints are stored start→goal (index 0 = start; UnitMotion consumes front→back likewise);
				// iterate front→back for travel order, capped with the exact building/rally endpoints.
				// Reversing this draws the path backward so the cap segments cross over → a straight
				// diagonal + the curve (two visible lines instead of one)
				var pts = new List<Vector3>
				{
					new(start.X.ToFloat(),
						TerrainHeightService.Sample(start.X.ToFloat(), start.Y.ToFloat()) + 0.15f,
						start.Y.ToFloat())
				};
				for (int i = 0; i < path.Waypoints.Count; i++)
				{
					var w = path.Waypoints[i];
					pts.Add(new Vector3(w.X.ToFloat(),
						TerrainHeightService.Sample(w.X.ToFloat(), w.Z.ToFloat()) + 0.15f,
						w.Z.ToFloat()));
				}
				pts.Add(new Vector3(rallyX, rallyGroundY + 0.15f, rallyZ));
				container.AddChild(SelectionRing.CreateRallyLine(pts));
			}
		}
	}

	private void ClearRallyMarker()
	{
		if (_rallyMarker != null)
		{
			_rallyMarker.QueueFree();
			_rallyMarker = null;
		}
		_rallyMarkerKey = null;
	}

	// _UnhandledInput (not _Input) so that clicks absorbed by the HUD's Control nodes —
	// e.g. pressing a training button — don't also fall through to HandleLeftClick and wipe
	// the current selection. GUI-consumed events never reach here; only raw 3D-scene clicks do.
	public override void _UnhandledInput(InputEvent @event)
	{
		if (!_gameStarted) return;
		// 暂停时屏蔽游戏输入(热键 B/T/S/右键/选区);叠层按钮走 GUI 事件不受影响。
		if (_sim.Paused) return;

		if (@event is InputEventKey key && key.Pressed)
		{
			// Enter 打开聊天输入框（输入框聚焦时 GUI 消费事件，不会到此分支）。
			if (key.Keycode == Key.Enter) _chatPanel.OpenInput();
			if (key.Keycode == Key.H && _isTutorial) _tutorialPanel.Toggle();
			if (key.Keycode == Key.B) EnterBuildMode("House");
			// 编队组(原版 control groups):Ctrl+数字=编入,数字=选中(存留过滤死亡实体)。
			if (key.Keycode >= Key.Key0 && key.Keycode <= Key.Key9)
			{
				int g = (int)(key.Keycode - Key.Key0);
				if (key.CtrlPressed) AssignControlGroup(g);
				else SelectControlGroup(g);
			}
			if (key.Keycode == Key.T) TrainVillager(Input.IsKeyPressed(Key.Shift));
			if (key.Keycode == Key.S) TrainSoldier(Input.IsKeyPressed(Key.Shift));
			if (key.Keycode == Key.Escape) { ExitBuildMode(); _commandTargetMode = null; _selectedEntities.Clear(); }
			// 放置旋转:[ / ] 每次 ±π/12(15°,原版 input.js:1238 rotation_step)。
			// 仅在放置模式响应,避免和别的 [/] 绑定冲突。
			if (_placeBuildingMode)
			{
				if (key.Keycode == Key.Bracketleft) _placeAngle -= Mathf.Pi / 12f;
				else if (key.Keycode == Key.Bracketright) _placeAngle += Mathf.Pi / 12f;
			}
			if (key.Keycode == Key.F5) QuickSave();
			if (key.Keycode == Key.F9) QuickLoad();
			// pause 热键(原版 MenuButtons.js:226 Pause hotkey):Pause/Break 键直接切暂停,
			// 不开菜单叠层(顶栏暂停按钮已移除,对齐上游;Menu 按钮仍可开 PauseMenu)。
			if (key.Keycode == Key.Pause) TogglePause();
		}

		if (@event is InputEventMouseButton mb && mb.Pressed)
		{
			if (mb.ButtonIndex == MouseButton.Left)
			{
				if (_placeBuildingMode)
				{
					// 原版 input.js INPUT_BUILDING_CLICK:按下记录起点,松开时若未拖过阈值
					// 则按当前角度放置;拖过阈值则改为朝向光标(自由旋转)。
					_placeMouseDown = mb.Position;
					_placeAnchorWorld = ScreenToWorld(mb.Position);
					return;
				}
				if (_commandTargetMode != null) { HandleCommandTargetClick(mb.Position); return; }
				_pendingDoubleClick = mb.DoubleClick;   // 双击标记在按下帧;释放时消费
				_dragStart = mb.Position;
				_dragSelecting = true;
				_isDragging = false;
				EnsureBandBox();
				_bandBox!.Rect = new Rect2();
				_bandBox.Visible = false;
			}
			else if (mb.ButtonIndex == MouseButton.Right)
				HandleRightClick(mb.Position, mb.CtrlPressed);
		}

		if (@event is InputEventMouseMotion mm && _dragSelecting && mm.Position.DistanceTo(_dragStart) > 8f)
		{
			_isDragging = true;
			// 框选矩形(原版 bandbox):实时跟随鼠标
			_bandBox!.Rect = new Rect2(_dragStart, mm.Position - _dragStart).Abs();
			_bandBox.Visible = true;
			_bandBox.QueueRedraw();
		}

		// 放置模式拖拽自由旋转(原版 input.js:786):按住左键拖超阈值后,朝向 = 锚点→光标方向。
		if (@event is InputEventMouseMotion pmm && _placeBuildingMode
			&& _placeMouseDown.X >= 0 && _placeAnchorWorld != null
			&& pmm.Position.DistanceTo(_placeMouseDown) > 8f)
		{
			var cur = ScreenToWorld(pmm.Position);
			if (cur != null)
			{
				var anchor = _placeAnchorWorld.Value;
				// 原版 vector.js:413 atan2(dx, dz);Godot 与原版同 Y-up、angle 0 朝 +Z。
				_placeAngle = Mathf.Atan2(cur.Value.X - anchor.X, cur.Value.Z - anchor.Z);
			}
		}

		if (@event is InputEventMouseButton mbu && !mbu.Pressed && mbu.ButtonIndex == MouseButton.Left && _dragSelecting)
		{
			_dragSelecting = false;
			if (_bandBox != null) _bandBox.Visible = false;
			if (_isDragging) HandleDragSelect(_dragStart, mbu.Position);
			else if (_pendingDoubleClick) HandleDoubleClick(mbu.Position);
			else HandleLeftClick(mbu.Position);
		}

		// 放置模式:左键松开时确认放置(原版 input.js mousebuttonup → tryPlaceBuilding)。
		// 放在按下之后、松开之时,让拖拽自由旋转有机会先更新 _placeAngle。
		if (@event is InputEventMouseButton mbuPlace && !mbuPlace.Pressed
			&& mbuPlace.ButtonIndex == MouseButton.Left && _placeBuildingMode
			&& _placeMouseDown.X >= 0)
		{
			_placeMouseDown = new Vector2(-1, -1);
			PlaceBuilding(mbuPlace.Position);
		}
	}

	/// <summary>命令目标模式下的左键点击(原版 unit_actions 按钮 → 光标选目标):
	/// garrison=己方驻军建筑 / repair=己方受损建筑或地基 / guard=己方单位;
	/// 执行后清模式(原版同:一次性目标选择)。</summary>
	private void HandleCommandTargetClick(Vector2 screenPos)
	{
		string mode = _commandTargetMode!;
		_commandTargetMode = null;
		var worldPos = ScreenToWorld(screenPos);
		if (worldPos == null) return;
		var targets = _sim.GetEntitiesAtPosition(worldPos.Value, 5f);
		EntityId? target = targets.Count > 0 ? targets[0] : null;

		switch (mode)
		{
			case "garrison":
			{
				if (target == null) return;
				var holder = _sim.Sim.QueryInterface<GarrisonHolderComponent>(target.Value);
				var owner = _sim.Sim.QueryInterface<OwnershipComponent>(target.Value);
				if (holder == null || owner == null || owner.PlayerId != (int)_sim.LocalPlayerId) return;
				foreach (var unit in _selectedEntities)
					if (_sim.Sim.QueryInterface<UnitAIComponent>(unit) != null)
						_sim.CommandGarrison(unit, target.Value);
				break;
			}
			case "repair":
			{
				if (target == null || !IsOwn(target.Value)) return;
				// 编队路由:控制器 Repair 广播给有 Builder 的成员(无件成员自行拒收)。
				foreach (var unit in ExpandFormationOrderTargets())
					if (_sim.Sim.QueryInterface<BuilderComponent>(unit) != null || IsFormationController(unit))
						_sim.CommandRepair(unit, target.Value);
				break;
			}
			case "guard":
			{
				if (target == null || !IsOwn(target.Value)) return;
				if (_sim.Sim.QueryInterface<UnitAIComponent>(target.Value) == null) return;
				// 编队路由:整队全选 → 控制器 Guard(成员广播 Guard + 解散,原版同)。
				foreach (var unit in ExpandFormationOrderTargets())
					if (unit != target.Value && _sim.Sim.QueryInterface<UnitAIComponent>(unit) != null)
						_sim.CommandGuard(unit, target.Value);
				break;
			}
			case "patrol":
			{
				// 编队路由:整队全选 → 控制器巡逻(整队往返,成员随队)。
				foreach (var unit in ExpandFormationOrderTargets())
					if (_sim.Sim.QueryInterface<UnitAIComponent>(unit) != null)
						_sim.CommandPatrol(unit, worldPos.Value.X, worldPos.Value.Z);
				break;
			}
		}
	}

	private bool _pendingDoubleClick;

	/// <summary>双击同类全选(原版 input.js 双击语义):屏幕内所有与目标同模板、
	/// 同属主的实体入列。屏幕范围 = 相机视锥。</summary>
	private void HandleDoubleClick(Vector2 screenPos)
	{
		var worldPos = ScreenToWorld(screenPos);
		if (worldPos == null) return;
		var targets = _sim.GetEntitiesAtPosition(worldPos.Value, 3f);
		if (targets.Count == 0) return;
		var hit = targets[0];
		var identity = _sim.Sim.QueryInterface<IdentityComponent>(hit);
		if (identity == null || identity.TemplateName.Length == 0) return;
		int owner = _sim.Sim.QueryInterface<OwnershipComponent>(hit)?.PlayerId ?? -1;

		var camera = GetViewport().GetCamera3D();
		_selectedEntities.Clear();
		foreach (var (eid, node) in _sim.EntityNodes)
		{
			var id = _sim.Sim.QueryInterface<IdentityComponent>(eid);
			if (id == null || id.TemplateName != identity.TemplateName) continue;
			var own = _sim.Sim.QueryInterface<OwnershipComponent>(eid);
			if ((own?.PlayerId ?? -1) != owner) continue;
			if (camera != null && !camera.IsPositionInFrustum(node.GlobalPosition)) continue;
			_selectedEntities.Add(eid);
		}
		if (_selectedEntities.Count > 0)
		{
			UpdateSelectionMarkers();
			PlaySelectionSound("select");
		}
	}

	private void HandleLeftClick(Vector2 screenPos)
	{
		var worldPos = ScreenToWorld(screenPos);
		if (worldPos == null) return;
		var entities = _sim.GetEntitiesAtPosition(worldPos.Value, 3f);
		_selectedEntities.Clear();
		if (entities.Count > 0) _selectedEntities.Add(entities[0]);

		// 选中语音(原版 Sound.js select 事件:单位语音/资源/建筑选择声)
		if (_selectedEntities.Count > 0)
			PlaySelectionSound("select");
	}

	private void HandleDragSelect(Vector2 start, Vector2 end)
	{
		var sw = ScreenToWorld(start); var ew = ScreenToWorld(end);
		if (sw == null || ew == null) return;
		var center = (sw.Value + ew.Value) / 2;
		var extents = new Vector3(Mathf.Abs(ew.Value.X - sw.Value.X) / 2, 50, Mathf.Abs(ew.Value.Z - sw.Value.Z) / 2);
		_selectedEntities.Clear();
		foreach (var eid in _sim.GetEntitiesInBounds(center, extents))
		{
			var identity = _sim.Sim.QueryInterface<IdentityComponent>(eid);
			if (identity != null && identity.IsUnit) _selectedEntities.Add(eid);
		}
		// 框选语音同点选(原版:选中即播 select 组)
		if (_selectedEntities.Count > 0)
			PlaySelectionSound("select");
	}

	// ── 动作光标(原版 input.js updateCursorAndTooltip 的 v1 子集)─────────────────
	// 选中己方单位时按 hover 目标切 attack/gather/garrison/capture 光标;move 无专属
	// 光标(原版如此:地面 = 默认箭头)。纹理 = 原版 art/textures/cursors,热点全 1,1。
	private string _cursorState = "";
	private readonly Dictionary<string, Texture2D> _cursorCache = new();
	private CanvasLayer? _cursorLayer;
	private TextureRect? _cursorSprite;

	private void UpdateActionCursor()
	{
		if (GetTree().Paused)
		{
			SetActionCursor("");
			return;
		}
		SetActionCursor(DetermineHoverCursor(GetViewport().GetMousePosition()));
	}

	private void SetActionCursor(string name)
	{
		if (name == _cursorState) return;
		_cursorState = name;
		if (_cursorSprite == null) return;
		if (name.Length == 0)
		{
			// 原版默认态 = OS 箭头(art/textures/cursors 无默认/move 纹理,input.js
			// 仅在动作态 SetCursor)。软件精灵藏起,OS 光标还回。
			_cursorSprite.Visible = false;
			Input.MouseMode = Input.MouseModeEnum.Visible;
			return;
		}
		if (!_cursorCache.TryGetValue(name, out var tex) || tex == null)
		{
			tex = GD.Load<Texture2D>($"res://assets/ui/cursors/{name}.png");
			_cursorCache[name] = tex;
		}
		if (tex == null) return; // 贴图缺失时保持上一光标,不闪没
		_cursorSprite.Texture = tex;
		_cursorSprite.Visible = true;
		Input.MouseMode = Input.MouseModeEnum.Hidden;
	}

	/// <summary>软件光标逐帧跟随(热点对齐原版 .txt:全 1,1)。</summary>
	private void UpdateCursorSpritePosition()
	{
		if (_cursorSprite != null)
			_cursorSprite.Position = GetViewport().GetMousePosition() - new Vector2(1, 1);
	}

	private string DetermineHoverCursor(Vector2 mousePos)
	{
		if (_selectedEntities.Count == 0) return "";
		// 选中集中各能力只要有一个具备即显示对应光标(原版 actionCheck 同理)。
		bool canAttack = false, canGather = false, canGarrison = false;
		foreach (var eid in _selectedEntities)
		{
			if (!IsOwn(eid)) continue;
			if (_sim.Sim.QueryInterface<AttackComponent>(eid) != null) canAttack = true;
			if (_sim.Sim.QueryInterface<ResourceGatherer>(eid) != null) canGather = true;
			if (_sim.Sim.QueryInterface<GarrisonableComponent>(eid) != null
				&& _sim.Sim.QueryInterface<UnitAIComponent>(eid) != null) canGarrison = true;
		}
		if (!canAttack && !canGather && !canGarrison) return "";

		var worldPos = ScreenToWorld(mousePos);
		if (worldPos == null) return "";
		var targets = _sim.GetEntitiesAtPosition(worldPos.Value, 3f);
		if (targets.Count == 0) return "";
		var e = targets[0];
		int lp = (int)_sim.LocalPlayerId;
		var owner = _sim.Sim.QueryInterface<OwnershipComponent>(e);
		// gaia 实体(鹿/狼等)无 OwnershipComponent,按玩家 0 处理——IsEnemy(lp,0) 恒 true,
		// 有 Health 的 gaia 动物对士兵显示剑(原版可猎);树木无 Health(原版数据)不显示。

		// 采集者在资源目标上优先采集光标(鹿对村民=猎取;对齐 HandleRightClick 分流)。
		if (canGather && _sim.Sim.QueryInterface<ResourceSupply>(e) is { } supply)
		{
			// 按 specificType 细分(原版 cursors/action-gather-{fruit,fish,meat,...}.png);
			// 大类兜底(旧数据无 specificType 时回退)。
			return supply.SpecificType switch
			{
				"tree" => "action-gather-tree",
				"rock" => "action-gather-rock",
				"ore" => "action-gather-ore",
				"fruit" => "action-gather-fruit",
				"fish" => "action-gather-fish",
				"meat" => "action-gather-meat",
				"milk" => "action-gather-milk",
				"rice" => "action-gather-rice",
				"ruins" => "action-gather-ruins",
				"grain" => "action-gather-grain",
				_ => supply.Type switch
				{
					ResourceType.Wood => "action-gather-tree",
					ResourceType.Stone => "action-gather-rock",
					ResourceType.Metal => "action-gather-ore",
					_ => "action-gather-grain",
				}
			};
		}
		if (canAttack
			&& _sim.Sim.Players.IsEnemy(lp, owner?.PlayerId ?? 0)
			&& (_sim.Sim.QueryInterface<HealthComponent>(e) != null
				|| _sim.Sim.QueryInterface<CapturableComponent>(e) != null))
		{
			// Ctrl = 捕获修饰(与右键 HandleRightClick 的 allowCapture 一致)。
			return Input.IsKeyPressed(Key.Ctrl) ? "action-capture" : "action-attack";
		}
		if (canGarrison && owner != null && owner.PlayerId == lp
			&& _sim.Sim.QueryInterface<GarrisonHolderComponent>(e) != null)
			return "action-garrison";
		return "";
	}

	private void HandleRightClick(Vector2 screenPos, bool allowCapture)
	{
		var worldPos = ScreenToWorld(screenPos);
		if (worldPos == null) return;

		var targets = _sim.GetEntitiesAtPosition(worldPos.Value, 3f);
		EntityId? targetEntity = targets.Count > 0 ? targets[0] : null;

		// Rally point: building selected, right-click target
		if (_selectedEntities.Count == 1)
		{
			var only = _selectedEntities.First();
			var rally = _sim.Sim.QueryInterface<RallyPointComponent>(only);
			if (rally != null)
			{
				// 命中资源实体 → 集结到该资源(采集集结);其余一律地面集结。
				// 关键:点在建筑自身 15m 拾取半径内时 targetEntity=建筑自己(无 supply),
				// 旧逻辑落空不设集结——门口点集结点永远失败。
				if (targetEntity.HasValue
					&& _sim.Sim.QueryInterface<ResourceSupply>(targetEntity.Value) != null)
					_sim.CommandSetRallyPoint(only, targetEntity);
				else
					_sim.CommandSetRallyPointPosition(only, worldPos.Value.X, worldPos.Value.Z);
				return;
			}
		}

		if (_selectedEntities.Count == 0) return;

		bool isResource = false, isEnemy = false, isGarrisonTarget = false, isFoundation = false;
		foreach (var eid in targets)
		{
			targetEntity = eid;
			isResource = _sim.Sim.QueryInterface<ResourceSupply>(eid) != null;
			// 不完工地基(原版 repair 动作):右键己方地基 → 建造工去帮建。此前无此分支,
			// 右键地基只走 Move,建造工走过去就站住、不建造。
			var foundationCmp = _sim.Sim.QueryInterface<FoundationComponent>(eid);
			isFoundation = foundationCmp != null && !foundationCmp.IsBuilt;
			var owner = _sim.Sim.QueryInterface<OwnershipComponent>(eid);
			// 敌对判定走内核外交(对齐原版):敌军建筑/单位/gaia 野兽一视同仁——
			// gaia 实体无 OwnershipComponent,按玩家 0 处理(IsEnemy 恒 true)。
			// 须可攻击(Health 或 Capturable),树木等资源实体不算敌(原版树木无 Health)。
			isEnemy = _sim.Sim.Players.IsEnemy((int)_sim.LocalPlayerId, owner?.PlayerId ?? 0)
				&& (_sim.Sim.QueryInterface<HealthComponent>(eid) != null
					|| _sim.Sim.QueryInterface<CapturableComponent>(eid) != null);
			// 驻军目标(原版 garrison 动作):己方有驻军位的建筑;与敌对互斥。
			isGarrisonTarget = !isEnemy && owner != null
				&& owner.PlayerId == (int)_sim.LocalPlayerId
				&& _sim.Sim.QueryInterface<GarrisonHolderComponent>(eid) != null;
			break;
		}

		bool issuedMove = false;
		string? orderSound = null;   // 原版:命令语音每点击播一次(首个命中分支)
		// 编队命令路由(原版 Commands.js GetFormationUnitAIs 的选择侧):整队全选 →
		// 一条命令发给控制器(FORMATIONCONTROLLER 树:整体走位/编队作战);部分选中 →
		// 成员先脱队(remove 命令,锁步安全)再个体命令。
		var orderTargets = ExpandFormationOrderTargets();
		foreach (var unit in orderTargets)
		{
			if (isGarrisonTarget && targetEntity.HasValue
				&& _sim.Sim.QueryInterface<UnitAIComponent>(unit) != null
				&& !IsFormationController(unit))
			{
				// 右键己方驻军建筑 → 载入(原版 unit_actions garrison;
				// 宿主是否接受由 sim 侧 Garrisonable.CanGarrison 判)。
				_sim.CommandGarrison(unit, targetEntity.Value);
				orderSound ??= "order_garrison";
			}
			else if (isFoundation && targetEntity.HasValue
				&& _sim.Sim.QueryInterface<BuilderComponent>(unit) != null)
			{
				// 右键不完工地基 → 建造工去帮建(原版 repair 动作)。此前无此分支,
				// 右键地基走 Move,建造工走到就站住不建造。多个建造工可同时帮建同一地基。
				_sim.CommandRepair(unit, targetEntity.Value);
				orderSound ??= "order_repair";
			}
			else if (isResource && targetEntity.HasValue
				&& (_sim.Sim.QueryInterface<ResourceGatherer>(unit) != null || IsFormationController(unit)))
			{
				// 采集者优先采集(鹿=enemy+resource 双身份:村民猎鹿=采集,
				// 女兵有弱攻击也不能去杀食材)。
				_sim.CommandGather(unit, targetEntity.Value);
				orderSound ??= "order_gather";
			}
			else if (isEnemy && targetEntity.HasValue
				&& (_sim.Sim.QueryInterface<AttackComponent>(unit) != null || IsFormationController(unit)))
			{
				// Ctrl+右键 = 捕获(原版 Ctrl+click → attack allowCapture=true);
				// 无捕获能力的单位自动退化普通攻击(GetBestAttackAgainst 选型)。
				_sim.CommandAttack(unit, targetEntity.Value, allowCapture);
				orderSound ??= "order_attack";
			}
			else
			{
				// 地面点击修饰键(原版 default.cfg:attackmove=Ctrl / patrol=P):
				// Ctrl+点地 = 攻击移动(沿途遇敌自动交战);P+点地 = 巡逻(往返)。
				if (allowCapture && _sim.Sim.QueryInterface<UnitAIComponent>(unit) != null)
				{
					_sim.CommandAttackWalk(unit, worldPos.Value.X, worldPos.Value.Z);
					orderSound ??= "order_attack_move";
				}
				else if (Input.IsKeyPressed(Key.P) && _sim.Sim.QueryInterface<UnitAIComponent>(unit) != null)
				{
					_sim.CommandPatrol(unit, worldPos.Value.X, worldPos.Value.Z);
					orderSound ??= "order_walk";
				}
				else
				{
					_sim.MoveEntity(unit, worldPos.Value.X, worldPos.Value.Z);
					orderSound ??= "order_walk";
				}
				issuedMove = true;
			}
		}
		if (orderSound != null)
			PlaySelectionSound(orderSound);

		// 移动指令的"目标标记"(原版 unit_actions.js 的 move 动作 → DrawTargetMarker →
		// GuiInterface.AddTargetMarker("special/target_marker")):在点击处放红金动画标记,
		// 仅表现层、本地生成、不进网络/存档(命令本身已承载指令)。攻击/采集目标是实体本身,
		// 不画地面标记——与原版一致(只 move / map_flare 进 g_TargetMarker)。
		if (issuedMove)
			SpawnTargetMarker(worldPos.Value);
	}

	/// <summary>编队命令路由(原版 Commands.js GetFormationUnitAIs 的选择侧近似):
	/// 整队全选 → 返回控制器(一条命令,走 FORMATIONCONTROLLER 树);部分选中 →
	/// 发 remove 脱队命令(锁步安全)后按个体返回;非编队成员原样通过。
	/// 顺序确定:无编队者先行(选择序),编队组按控制器 id 升序。</summary>
	private List<EntityId> ExpandFormationOrderTargets()
	{
		var result = new List<EntityId>();
		var byController = new Dictionary<EntityId, List<EntityId>>();
		foreach (var unit in _selectedEntities)
		{
			var ai = _sim.Sim.QueryInterface<UnitAIComponent>(unit);
			if (ai != null && ai.FormationController is { } fc && !ai.IsFormationController)
			{
				if (!byController.TryGetValue(fc, out var l))
					byController[fc] = l = new List<EntityId>();
				l.Add(unit);
			}
			else
			{
				result.Add(unit);
			}
		}
		foreach (var kv in byController.OrderBy(k => k.Key.Value))
		{
			var formation = _sim.Sim.QueryInterface<FormationComponent>(kv.Key);
			if (formation != null && kv.Value.Count == formation.GetMemberCount())
			{
				result.Add(kv.Key);   // 整队全选 → 控制器单令
			}
			else
			{
				// 部分选中 → 脱队个体令(原版单选 RemoveFromFormation;
				// 多选 regroup 未移植,近似为脱队)。
				_sim.CommandFormationRemove(kv.Value);
				result.AddRange(kv.Value);
			}
		}
		return result;
	}

	/// <summary>实体是否为编队控制器(虚拟实体;命令路由时按整队对待)。</summary>
	private bool IsFormationController(EntityId ent) =>
		_sim.Sim.QueryInterface<UnitAIComponent>(ent)?.IsFormationController == true;

	/// <summary>Spawn the 0 A.D. move-order target marker (<c>special/target_marker</c>) at the
	/// clicked ground point — the red-and-gold animated standard that confirms "units ordered here".
	/// Ports <c>GuiInterface.AddTargetMarker</c> + the template's <c>&lt;Decay&gt;</c> (DelayTime 0.5s
	/// then a rapid sink). Visual-only local feedback: not networked, not serialized.</summary>
	private void SpawnTargetMarker(Vector3 worldPos)
	{
		// Texture is red-and-gold (no player-colour channel), so teamColor is cosmetic here;
		// pass the local player's colour anyway for consistency with other owned markers.
		int lp = (int)_sim.LocalPlayerId;
		Color color = lp == 1 ? new Color(0.08f, 0.22f, 0.58f) : new Color(0.72f, 0.06f, 0.06f);

		int seed = (int)(worldPos.X * 100f) ^ (int)(worldPos.Z * 100f);
		var marker = ActorLoader.Instance.Instantiate("special/target_marker.xml", seed, color);
		if (marker == null) return;
		marker.Position = new Vector3(worldPos.X, TerrainHeightService.Sample(worldPos.X, worldPos.Z), worldPos.Z);
		_sim.UnitContainer.AddChild(marker);

		// Decay: 0.5s delay, then sink out (SinkRate huge in the original). Free the node after.
		GetTree().CreateTimer(0.5f).Timeout += () =>
		{
			if (!GodotObject.IsInstanceValid(marker)) return;
			var sink = CreateTween();
			sink.TweenProperty(marker, "position:y", marker.Position.Y - 1.5f, 0.2f);
			sink.TweenCallback(Callable.From(() => marker.QueueFree()));
		};
	}

	/// <summary>屏幕点 → sim 世界坐标。相机/射线活在视觉空间(世界经 _worldRoot 镜像),
	/// 步进时按 sim 坐标采样高度(visZ → simZ = WorldSize − visZ),返回点同样换回 sim,
	/// 让所有调用方(下令/放建筑/标记)只面对 sim 坐标。</summary>
	private Vector3? ScreenToWorld(Vector2 screenPos)
	{
		var from = _camera.ProjectRayOrigin(screenPos);
		var dir = _camera.ProjectRayNormal(screenPos);
		if (dir.Y >= 0) return null;

		// Raymarch against heightmap: coarse steps, then bisect refine.
		float t = 0f;
		const float maxDist = 1000f;
		const float step = 2f;
		float prevT = 0f;
		while (t < maxDist)
		{
			var p = from + dir * t;
			if (p.Y <= TerrainHeightService.Sample(p.X, TerrainHeightService.MirrorZ(p.Z)))
			{
				float lo = prevT, hi = t;
				for (int i = 0; i < 8; i++)
				{
					float mid = (lo + hi) * 0.5f;
					var m = from + dir * mid;
					if (m.Y <= TerrainHeightService.Sample(m.X, TerrainHeightService.MirrorZ(m.Z))) hi = mid;
					else lo = mid;
				}
				var hit = from + dir * hi;
				return new Vector3(hit.X, hit.Y, TerrainHeightService.MirrorZ(hit.Z));
			}
			prevT = t;
			t += step;
		}
		return null;
	}

	public void EnterBuildMode(string template)
	{
		bool hasBuilder = false;
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null) { hasBuilder = true; break; }
		if (!hasBuilder) return;
		var player = _sim.GetPlayer();
		if (player == null) return;
		var (wood, stone, metal, food, _) = GetBuildCost(template);
		if (!CanAfford(player, wood, stone, metal, food))
		{
			GD.Print($"Cannot afford {template}: needs {wood}W {stone}S {metal}M {food}F");
			return;
		}
		_placeBuildingMode = true;
		_buildTemplate = template;
		// 重置朝向为 GUI 默认 3π/4(原版 placement.js Reset→SetDefaultAngle)。
		_placeAngle = Mathf.Pi * 0.75f;
		CreatePlaceGhost();
	}

	/// <summary>建造放置预览 ghost(原版 placement.js 的 SetEntityPreview 等价):半透明建筑
	/// 节点跟随鼠标 + 套当前 _placeAngle。失败(无 actor)时静默——ghost 为 null 时
	/// _Process 跳过位置更新,放置仍可进行(只是看不到预览)。</summary>
	private void CreatePlaceGhost()
	{
		FreePlaceGhost();
		string fullTemplate = MapBuildTemplateName(_buildTemplate ?? "");
		if (string.IsNullOrEmpty(fullTemplate)) return;
		Color color = SimBridge.GetPlayerColor((int)_sim.LocalPlayerId);
		// 复用建筑视觉装配(ModelLibrary.InstantiateForTemplate),与完工建筑同一套 actor。
		_placeGhost = ModelLibrary.InstantiateForTemplate(fullTemplate, 0, 0, color);
		if (_placeGhost == null) return;
		AddChild(_placeGhost);
		// 半透明:递归设所有 MeshInstance3D 的透明度(原版 ghost 亦是半透)。
		SetGhostTransparency(_placeGhost, 0.5f);
	}

	private static void SetGhostTransparency(Node node, float alpha)
	{
		if (node is MeshInstance3D mi)
		{
			// 克隆材质避免共享覆盖完工建筑(Instance:每节点独立材质覆盖)。
			var mat = mi.MaterialOverride?.Duplicate() as Material;
			if (mat is StandardMaterial3D sm)
			{
				sm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
				sm.AlbedoColor = new Color(sm.AlbedoColor.R, sm.AlbedoColor.G, sm.AlbedoColor.B, alpha);
				mi.MaterialOverride = sm;
			}
		}
		foreach (var child in node.GetChildren())
			SetGhostTransparency(child, alpha);
	}

	private void FreePlaceGhost()
	{
		if (_placeGhost != null)
		{
			_placeGhost.QueueFree();
			_placeGhost = null;
		}
		_placeAnchorWorld = null;
		_placeMouseDown = new Vector2(-1, -1);
	}

	/// <summary>退出放置模式并清理预览(放置完成/取消/Esc/负担不起时调)。</summary>
	private void ExitBuildMode()
	{
		_placeBuildingMode = false;
		FreePlaceGhost();
	}

	public void TrainVillager(bool batch = false) => TrainFirstMatching(batch, support: true);

	public void TrainSoldier(bool batch = false) => TrainFirstMatching(batch, support: false);

	/// <summary>训练选中首个生产建筑可训练列表中首个 support/非-support 项(热键
	/// T=村民/S=士兵;数据驱动,文明正确——原版热键亦按训练面板项语义)。</summary>
	public void TrainFirstMatching(bool batch, bool support)
	{
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<ProductionQueue>(eid) is { } queue)
			{
				foreach (var t in queue.GetTrainableEntities(_sim.Sim))
				{
					if (t.Contains("support_") != support) continue;
					_sim.CommandTrain(eid, t, batch: batch);
					return;
				}
				break;
			}
	}

	/// <summary>选中的实体是否归本地玩家(指令栏 Stop/Delete 等按钮的可见性判据;
	/// 执行端另有归属校验兜底)。</summary>
	public bool IsOwn(EntityId eid) =>
		_sim.Sim.QueryInterface<OwnershipComponent>(eid)?.PlayerId == (int)_sim.LocalPlayerId;

	/// <summary>编队命令(原版 formation 面板按钮):shape=null 解散所选成员的控制器,
	/// 否则按阵型创建控制器(成员过滤/RequiredMemberCount 判定在执行器内核侧)。</summary>
	public void FormSelectedUnits(string shape)
	{
		if (_selectedEntities.Count == 0) return;
		_sim.CommandFormation(_selectedEntities.ToList(), shape);
	}

	/// <summary>暂停切换(原版 PauseControl:冻结/解冻 sim,不开菜单叠层)。</summary>
	public void TogglePause() => _sim.Paused = !_sim.Paused;

	/// <summary>游戏速度档位(原版 GameSpeeds 9 档;顶栏 +/- 步进)。</summary>
	private static readonly double[] GameSpeedSteps = { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 5.0, 10.0, 20.0 };

	/// <summary>顶栏速度 +/-(原版 GameSpeedControl 的步进按钮):取最近档 ±1。</summary>
	public void AdjustGameSpeed(int direction)
	{
		double cur = _sim.SpeedMultiplier;
		int best = 2;
		double bestDiff = double.MaxValue;
		for (int i = 0; i < GameSpeedSteps.Length; i++)
		{
			double d = System.Math.Abs(GameSpeedSteps[i] - cur);
			if (d < bestDiff) { bestDiff = d; best = i; }
		}
		int next = System.Math.Clamp(best + direction, 0, GameSpeedSteps.Length - 1);
		_sim.SpeedMultiplier = GameSpeedSteps[next];
		_hud?.ShowToast($"Game speed: {GameSpeedSteps[next]}×");
	}

	/// <summary>相机聚焦世界坐标(警报跳转/空闲村民等共用)。</summary>
	public void FocusWorldPosition(float x, float z)
		=> _camera.SetFocus(new Vector3(x, TerrainHeightService.Sample(x, z), z));

	/// <summary>空闲村民循环(原版 MiniMapIdleWorkerButton):实体 id 升序取下一个
	/// 空闲采集者(无订单+有采集组件+非驻防),聚焦相机并选中。</summary>
	private int _idleWorkerIndex = -1;

	public void CycleIdleWorker()
	{
		var idle = new List<(EntityId e, float x, float z)>();
		foreach (var eid in _sim.Sim.AllEntities)
		{
			var own = _sim.Sim.QueryInterface<OwnershipComponent>(eid);
			if (own == null || own.PlayerId != (int)_sim.LocalPlayerId) continue;
			var gatherer = _sim.Sim.QueryInterface<ResourceGatherer>(eid);
			var ai = _sim.Sim.QueryInterface<UnitAIComponent>(eid);
			if (gatherer == null || ai == null || !ai.IsIdle || ai.IsGarrisoned) continue;
			var pos = _sim.Sim.QueryInterface<PositionComponent>(eid);
			if (pos == null) continue;
			idle.Add((eid, pos.Position.X.ToFloat(), pos.Position.Z.ToFloat()));
		}
		if (idle.Count == 0)
		{
			_hud?.ShowToast("No idle workers");
			return;
		}
		idle.Sort((a, b) => a.e.Value.CompareTo(b.e.Value));
		_idleWorkerIndex = (_idleWorkerIndex + 1) % idle.Count;
		var (e, x, z) = idle[_idleWorkerIndex];
		_selectedEntities.Clear();
		_selectedEntities.Add(e);
		_camera.SetFocus(new Vector3(x, TerrainHeightService.Sample(x, z), z));
	}

	/// <summary>打包/解包所选攻城器(原版 pack_panel 按钮;PackComponent 自校验)。</summary>
	public void PackSelectedUnits(bool unpack)
	{
		foreach (var unit in _selectedEntities)
			if (_sim.Sim.QueryInterface<PackComponent>(unit) != null)
				_sim.CommandPack(unit, unpack);
	}

	// ── 编队组(原版 control groups,纯表现层)──

	private readonly Dictionary<int, List<EntityId>> _controlGroups = new();

	private void AssignControlGroup(int group)
	{
		_controlGroups[group] = _selectedEntities.ToList();
		_hud?.ShowToast($"Group {group} assigned ({_selectedEntities.Count})");
	}

	private void SelectControlGroup(int group)
	{
		if (!_controlGroups.TryGetValue(group, out var members) || members.Count == 0) return;
		// 死亡实体过滤(身份件缺失/实体不在世界=已毁;原版图省事存活滤)。
		var alive = members.Where(e =>
			_sim.Sim.QueryInterface<IdentityComponent>(e) != null
			&& _sim.Sim.QueryInterface<PositionComponent>(e) != null).ToList();
		_controlGroups[group] = alive;
		if (alive.Count == 0) return;
		_selectedEntities.Clear();
		foreach (var e in alive) _selectedEntities.Add(e);
		// 聚焦组内首个(原版双击才跳相机,单击组=选中——此实现单击即选中+聚焦首个)。
		var pos = _sim.Sim.QueryInterface<PositionComponent>(alive[0]);
		if (pos != null)
			_camera.SetFocus(new Vector3(pos.Position.X.ToFloat(), 0, pos.Position.Z.ToFloat()));
	}

	/// <summary>编队组概览(图标条用):(组号, 存活成员数),升序;空组不列。</summary>
	public List<(int group, int alive)> GetControlGroupInfo()
	{
		var result = new List<(int, int)>();
		foreach (var (g, members) in _controlGroups.OrderBy(kv => kv.Key))
		{
			int alive = members.Count(e =>
				_sim.Sim.QueryInterface<IdentityComponent>(e) != null
				&& _sim.Sim.QueryInterface<PositionComponent>(e) != null);
			if (alive > 0) result.Add((g, alive));
		}
		return result;
	}

	/// <summary>选中编队组(图标条点击与数字热键同路)。</summary>
	public void SelectControlGroupPublic(int group) => SelectControlGroup(group);

	/// <summary>多选网格点击(原版 unitSelectionButton):选中给定实体组。</summary>
	public void SelectOnly(IEnumerable<EntityId> entities)
	{
		_selectedEntities.Clear();
		foreach (var e in entities) _selectedEntities.Add(e);
		UpdateSelectionMarkers();
		if (_selectedEntities.Count > 0)
			PlaySelectionSound("select");
	}

	/// <summary>建筑升级命令(原版 upgrade 按钮:哨塔→防御塔等)。</summary>
	public void CommandUpgrade(EntityId building, EntityId? builder)
		=> _sim.CommandUpgrade(building, builder);

	/// <summary>城门锁切换(原版 gate 面板按钮)。</summary>
	public void CommandToggleGate(EntityId gate, bool locked)
		=> _sim.CommandToggleGate(gate, locked);

	/// <summary>易物命令(原版 barter;服务端 BarterSystem 校验汇率/town 门槛)。</summary>
	public void CommandBarter(ZeroAD.Sim.Components.ResourceType sell, ZeroAD.Sim.Components.ResourceType buy, int amount)
		=> _sim.CommandBarter(sell, buy, amount);

	/// <summary>改站姿:选中且有 UnitAI 的己方单位全部切到指定站姿(原版 stance 命令
	/// 对全部选中单位生效;站姿行为语义见 UnitAIComponent.s_stances)。</summary>
	public void SetSelectedUnitStance(string stance)
	{
		foreach (var eid in _selectedEntities)
			if (IsOwn(eid) && _sim.Sim.QueryInterface<UnitAIComponent>(eid) != null)
				_sim.CommandSetUnitStance(eid, stance);
	}

	/// <summary>首个选中有站姿单位的当前站姿(按钮高亮用;无则 null)。</summary>
	public string? GetFirstSelectedStance()
	{
		foreach (var eid in _selectedEntities)
			if (IsOwn(eid) && _sim.Sim.QueryInterface<UnitAIComponent>(eid) is { } ai)
				return ai.Stance;
		return null;
	}

	/// <summary>卸载单个驻军(原版 unload;仅己方建筑)。</summary>
	public void UnloadGarrison(EntityId holder, EntityId unit)
	{
		if (IsOwn(holder))
			_sim.CommandUngarrison(holder, (int)unit.Value);
	}

	/// <summary>卸载全部驻军(原版 unload-all-by-owner;仅己方建筑)。</summary>
	public void UnloadAllGarrison(EntityId holder)
	{
		if (IsOwn(holder))
			_sim.CommandUngarrison(holder, -1);
	}

	/// <summary>Stop:选中且有 UnitAI 的己方单位全部停止订单回 IDLE(原版 stop 命令,
	/// 对所有选中单位生效,不只第一个)。</summary>
	public void StopSelectedUnits()
	{
		foreach (var eid in _selectedEntities)
			if (IsOwn(eid) && _sim.Sim.QueryInterface<UnitAIComponent>(eid) != null)
				_sim.CommandStop(eid);
	}

	/// <summary>Delete:销毁选中的己方实体(原版 delete-entities;归属在执行端再校验)。</summary>
	public void DeleteSelectedEntities()
	{
		foreach (var eid in _selectedEntities)
			if (IsOwn(eid))
				_sim.CommandDelete(eid);
	}

	/// <summary>取消生产:选中建筑里第一个有 ProductionQueue 的,取消其队列第 index 项
	/// (HUD 训练队列槽点击的入口)。</summary>
	public void CancelProductionAt(int index)
	{
		foreach (var eid in _selectedEntities)
			if (IsOwn(eid) && _sim.Sim.QueryInterface<ProductionQueue>(eid) != null)
			{
				_sim.CommandCancelProduction(eid, index);
				break;
			}
	}

	public void TrainUnit(string template, bool batch = false)
	{
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<ProductionQueue>(eid) != null)
			{
				_sim.CommandTrain(eid, template, batch: batch);
				break;
			}
	}

	public void ResearchTech(string tech)
	{
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<ResearcherComponent>(eid) != null)
			{
				_sim.CommandResearch(eid, tech);
				break;
			}
	}

	/// <summary>顶栏 Menu 按钮回调:打开暂停菜单(冻结 sim)。HUD 持 _main 引用直接调。</summary>
	public void OpenPauseMenu() => _pauseMenu?.Open();

	/// <summary>顶栏 Diplomacy 按钮回调:打开外交面板(立场/进贡,不暂停 sim)。</summary>
	public void OpenDiplomacyPanel() => _diplomacyPanel?.Open();

	/// <summary>顶栏 Trade 按钮回调:打开贸易面板(易物/贸易品比例,不暂停 sim)。</summary>
	public void OpenTradePanel() => _tradePanel?.Open();

	/// <summary>顶栏民族徽标回调:打开科技树,预选本地玩家文明(原版 CivIcon.onPress)。</summary>
	public void OpenStructreePanel()
	{
		if (_structreePanel == null) return;
		string civ = _sim.GetPlayer()?.Civ ?? "athen";
		_structreePanel.SetCiv(civ);
		_structreePanel.Open();
	}

	/// <summary>顶栏 Settings 按钮回调:打开对局设置摘要面板(只读,不暂停 sim)。</summary>
	public void OpenMatchSettingsPanel() => _matchSettingsPanel?.Open();

	/// <summary>F5 快存 / 暂停菜单 Save。返回存档路径(null=失败),供暂停菜单回灌状态。</summary>
	private string? QuickSave()
	{
		var path = SaveGameManager.Save(_sim);
		if (path != null)
			GD.Print($"[QuickSave] saved to {path}");
		return path;
	}

	/// <summary>F9 快读 / 暂停菜单 Load。返回加载到的回合号(null=无存档或失败)。</summary>
	private uint? QuickLoad()
	{
		if (!SaveGameManager.Exists())
		{
			GD.PrintErr("[QuickLoad] no save file found");
			return null;
		}
		var turn = SaveGameManager.Load(_sim, prepareComponent: comp =>
		{
			// LosManagerComponent needs Attach(rangeManager) before deserialization.
			if (comp is ZeroAD.Sim.Components.LosManagerComponent los)
				los.Attach(_sim.Range);
			// AIComponent needs Configure(cm, net) before deserialization(与 AuraComponent 同模式:
			// manager 由 Configure 构造,Deserialize 还原计数器)。漏此 → 首 Tick NullRef。
			if (comp is ZeroAD.Sim.Components.AIComponent ai)
				ai.Configure(_sim.Sim, _sim.NetTurn);
			if (comp is ZeroAD.Sim.Components.StatisticsTrackerComponent st)
				st.Attach(_sim.Sim);
		});
		if (turn == null) return null;

		// Rebuild the entire visual layer: the old entity nodes are stale (they
		// reference entities that were cleared + recreated by DeserializeSaveGame).
		// Destroy every visual node, then recreate one for each loaded entity.
		_sim.RebuildAllVisuals();
		GD.Print($"[QuickLoad] loaded turn {turn}, visuals rebuilt");
		return turn;
	}

	private void PlaceBuilding(Vector2 screenPos)
	{
		var worldPos = ScreenToWorld(screenPos);
		if (worldPos == null) return;
		var player = _sim.GetPlayer();
		if (player == null) { ExitBuildMode(); return; }
		var (wood, stone, metal, food, buildTime) = GetBuildCost(_buildTemplate);
		if (!CanAfford(player, wood, stone, metal, food))
		{
			GD.Print($"Cannot afford {_buildTemplate}: needs {wood}W {stone}S {metal}M {food}F");
			ExitBuildMode();
			return;
		}

		// Placement validation is a presentation-only courtesy pre-filter (reject obviously
		// bad clicks without charging). The authoritative check — and resource charging and
		// foundation spawn — happens in the sim at the execution turn via SimCommandExecutor,
		// identically on every peer, so MP never desyncs on build.
		float halfSize = 3f;
		var stats = _sim.Templates?.ExtractStats(MapBuildTemplateName(_buildTemplate));
		if (stats != null)
		{
			float ob = Mathf.Max(stats.ObstructionSize0.ToFloat(), stats.ObstructionSize1.ToFloat());
			if (ob > 0) halfSize = ob * 0.5f;
		}
		var pr = _sim.Pathfinder.CheckBuildingPlacement(
			ZeroAD.Sim.Maths.Fixed.FromFloat(worldPos.Value.X),
			ZeroAD.Sim.Maths.Fixed.FromFloat(worldPos.Value.Z),
			ZeroAD.Sim.Maths.Fixed.FromFloat(halfSize),
			ZeroAD.Sim.Maths.Fixed.FromFloat(halfSize));
		if (pr != ZeroAD.Sim.Components.PlacementResult.Success)
		{
			GD.Print($"Cannot place {_buildTemplate} at ({worldPos.Value.X:F1},{worldPos.Value.Z:F1}): {pr}");
			_hud?.ShowToast("Cannot place building here.");
			_placeMouseDown = new Vector2(-1, -1);  // 清按下标记,允许下次重新拖拽
			// Stay in placement mode so the player can try another spot.
			return;
		}

		_ = buildTime; // build time comes from template data at execution; not needed here.
		string fullTemplate = MapBuildTemplateName(_buildTemplate);
		float angle = _placeAngle;
		ExitBuildMode();
		// 多建造者:每个选中的建造工都发 CommandBuild(同位置同模板)。SimCommandExecutor
		// 在同 turn 内去重——只 spawn 一个地基、扣一次费,其余 builder 改派去帮建同一地基
		// (对齐原版 construct 命令带 entities 数组的语义)。此前 break 只派第一个建造工。
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null)
				_sim.CommandBuild(eid, fullTemplate, worldPos.Value.X, worldPos.Value.Z, angle);
	}

	private (int wood, int stone, int metal, int food, float buildTime) GetBuildCost(string name)
	{
		TemplateStats? stats = null;
		try { stats = _sim.Templates?.ExtractStats(MapBuildTemplateName(name)); } catch { }
		if (stats != null && (stats.WoodCost > 0 || stats.StoneCost > 0 || stats.MetalCost > 0 || stats.FoodCost > 0))
			return (stats.WoodCost, stats.StoneCost, stats.MetalCost, stats.FoodCost,
				stats.BuildTime > 0f ? stats.BuildTime : 8.0f);
		var c = FallbackBuildCost(name);
		return (c.wood, c.stone, c.metal, c.food, 8.0f);
	}

	private static bool CanAfford(PlayerComponent player, int wood, int stone, int metal, int food) =>
		player.Wood >= wood && player.Stone >= stone && player.Metal >= metal && player.Food >= food;

	private static string MapBuildTemplateName(string name)
	{
		// 完整模板名直接透传(数据驱动建造面板给的就是 structures/{civ}/x);
		// 短名是热键 B 的老路径(教程默认 spart)。
		if (name.StartsWith("structures/", System.StringComparison.Ordinal)) return name;
		return name switch
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
	}

	private static (int wood, int stone, int metal, int food) FallbackBuildCost(string name) => name switch
	{
		"House" => (30, 0, 0, 0),
		"Storehouse" => (80, 0, 0, 0),
		"Farmstead" => (80, 0, 0, 0),
		"Field" => (60, 0, 0, 0),
		"Barracks" => (100, 0, 0, 0),
		"Outpost" => (80, 20, 0, 0),
		"Tower" => (100, 50, 0, 0),
		"Forge" => (120, 0, 30, 0),
		"Market" => (100, 0, 0, 0),
		"Temple" => (150, 50, 0, 0),
		"Arsenal" => (150, 0, 50, 0),
		_ => (50, 0, 0, 0)
	};
}
