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
	private bool _placeBuildingMode;
	private string _buildTemplate = "";
	private bool _gameStarted;
	private bool _isTutorial;
	private TutorialPanel _tutorialPanel = null!;
	private LoadingOverlay? _loadingOverlay;
	private PauseMenu? _pauseMenu;
	// FPS 叠层(overlay.fps 配置项驱动,原版 Display 类):右上角实时帧率。
	private CanvasLayer? _fpsOverlay;
	private Label? _fpsLabel;
	// 第二梯队菜单面板(Game Speed/Diplomacy/Trade/Match Settings):模态叠层,不暂停 sim。
	private GameSpeedPanel? _gameSpeedPanel;
	private DiplomacyPanel? _diplomacyPanel;
	private TradePanel? _tradePanel;
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
		_lobby.OnStartGameRequested += () => _mp.HostStartGame();
		_lobby.OnSinglePlayer += seed => StartSinglePlayer(seed);
		_lobby.OnTutorialStart += () => StartTutorial();
		// Lobby-state refresh: clients repaint their read-only slot list from the host's table.
		// The host is the source of truth (its rows are editable) and never repaints from events.
		_mp.OnLobbyStateChanged += slots => { if (!_mp.IsHost) _lobby.RefreshSlotDisplay(slots); };

		_camera.SetFocus(new Vector3(128, 0, 128));

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
			GD.PrintErr($"[Gameplay] EXCEPTION in load: {e}");
			GD.PrintErr($"[Gameplay] Stack: {e.StackTrace}");
			throw;
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
		_mp.OnGameStart += (s, pid, slots) => StartMpGameplay(s, pid, slots, isHost: true);
		_lobby.ShowSlotLobby(isHost: true, _mp.Slots);
		_lobby.SetStatus($"Hosting on port {port} — configure slots, then Start.");
	}

	/// <summary>Client connects and waits in the lobby. Its slot is claimed by the host on
	/// connect; the host's slot table broadcasts keep this client's read-only view in sync.
	/// World creation is deferred until the host fires GameStart.</summary>
	private void StartMpClient(string addr, int port)
	{
		_mp.StartClient(addr, port);
		_mp.OnGameStart += (s, pid, slots) => StartMpGameplay(s, pid, slots, isHost: false);
		_lobby.ShowSlotLobby(isHost: false, null);
		_lobby.SetStatus($"Connecting to {addr}:{port} — waiting for host…");
	}

	/// <summary>MP 正式开局(host 点 Start 后双端同走):加载等待页 + 分阶段构建。</summary>
	private void StartMpGameplay(uint seed, uint playerId,
		IReadOnlyList<ZeroAD.Sim.Net.PlayerSlotSetup> slots, bool isHost)
	{
		_loadingOverlay = new LoadingOverlay(MapTitleFromPath(PickSkirmishMapRel()));
		AddChild(_loadingOverlay);
		RunStagedGameplayLoad(seed, playerId, slots, tutorial: false, isMultiplayer: true, isHost: isHost);
	}

	/// <summary>SP/MP 默认地图(镜像 SetupTerrain 的 pmp 回退链),供加载页标题推导。</summary>
	private string? PickSkirmishMapRel()
	{
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
					new() { PlayerId = 1, Kind = ZeroAD.Sim.Net.PlayerSlotKind.Human, Civ = "athen" },
				}
				: new List<ZeroAD.Sim.Net.PlayerSlotSetup>
				{
					new() { PlayerId = 1, Kind = ZeroAD.Sim.Net.PlayerSlotKind.Human, Civ = "athen", Team = -1 },
					new() { PlayerId = 2, Kind = ZeroAD.Sim.Net.PlayerSlotKind.AI,    Civ = "gaul",  Team = -1 },
				});
		_sim.InitWorld(templatesPath, seed, playerId, role, effectiveSlots);
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

		GD.Print(_isTutorial
			? "[Tutorial] Introductory Tutorial started"
			: $"[Tutorial] MS6 Game started: player={playerId}");
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

		// 第二梯队菜单面板(Game Speed/Diplomacy/Trade/Match Settings):模态叠层,挡鼠标不暂停。
		_gameSpeedPanel = new GameSpeedPanel(_sim);
		_diplomacyPanel = new DiplomacyPanel(_sim);
		_tradePanel = new TradePanel(_sim);
		_matchSettingsPanel = new MatchSettingsPanel(_sim);
		AddChild(_gameSpeedPanel);
		AddChild(_diplomacyPanel);
		AddChild(_tradePanel);
		AddChild(_matchSettingsPanel);

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
				float waterHeight = water?.height ?? -999f;
				if (water != null)
				{
					var waterMesh = WaterRenderer.CreateWaterPlane(water.Value.height, water.Value.color, pmp.MapSizeMeters);
					_worldRoot.AddChild(waterMesh);
					GD.Print($"Water: height={water.Value.height:F1}m color={water.Value.color}");
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

	private void FillPassabilityAllLand()
	{
		var terrain = _sim.Terrain;
		if (terrain == null) return;
		int n = terrain.MapSize;
		var grid = new ZeroAD.Sim.Components.TerrainClass[n, n];
		// Default Land (0) is already the zero value, so no need to fill explicitly.
		terrain.SetPassabilityGrid(grid);

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
				// The scenario's <Camera> position is the Atlas editor's last pose — restore
				// it on launch (matches 0 A.D.'s "start where the designer left off"). The
				// look-at (focus) is the player's (P1) civic centre so the base is centered;
				// if no CC is found we fall back to focusing the scenario's camera position.
				float focusX = scenario.CameraX, focusZ = scenario.CameraZ;
				bool foundCc = false;
				foreach (var ent in scenario.Entities)
				{
					if (ent.Player != 1 || !ent.IsSimulationEntity) continue;
					if (ent.Template.Contains("civil_centre") || ent.Template.Contains("civic_centre"))
					{
						focusX = ent.X; focusZ = ent.Z;
						GD.Print($"[Tutorial] focusing P1 civic centre at ({focusX},{focusZ})");
						foundCc = true;
						break;
					}
				}
				float h = TerrainHeightService.Sample(focusX, focusZ);
				_camera.SetFocus(new Vector3(focusX, h, focusZ));
				// Restore the designer's camera pose (yaw/pitch/distance derived from the
				// scenario Camera → focus vector). Skip when the focus fell back to the
				// camera position itself (no CC) — PlaceFromScenarioCamera needs a non-zero
				// delta to derive a meaningful orbit.
				if (foundCc)
				{
					var camPos = new Vector3(scenario.CameraX, scenario.CameraY, scenario.CameraZ);
					_camera.PlaceFromScenarioCamera(camPos);
					GD.Print($"[Tutorial] restored scenario camera pose from {camPos} toward focus ({focusX},{focusZ})");
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
		SetupTerrain();
		bool useRealTemplates = _sim.Templates != null;

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
		_sim.SpawnUnit(380, 380, isSoldier: true);
		_sim.SpawnUnit(385, 385, isSoldier: true);

		// Initial buildings/units were spawned AFTER the map-load RebuildGrid; rebuild once more so
		// pathing accounts for the town centres and any scenario buildings.
		_sim.Pathfinder.RebuildGrid();

		// Fog: sandbox/SP spawns owner-less world-dev entities with no seers, so reveal the map so
		// the dev world isn't shrouded. MP keeps real fog — each human only sees their own vision.
		_sim.Range.SetLosRevealAll((int)playerId, isMultiplayer ? false : true);

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
	}

	// pauseonfocusloss 配置项(原版 PauseOnFocusLoss,仅 SP):窗口失焦自动暂停并显示暂停菜单。
	public override void _Notification(int what)
	{
		if (what != NotificationWMWindowFocusOut) return;
		if (!_gameStarted
			|| _sim.NetTurn.Role != NetRole.Standalone
			|| GetNode<UserConfig>("/root/UserConfig").GetEffective("pauseonfocusloss") != "true"
			|| _pauseMenu is not { Visible: false })
			return;
		OpenPauseMenu();
	}

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
			float ringRadius = isBuilding ? 10f : 2f;

			Color friendlyColor = ownerPlayerId == 1
				? new Color(0.08f, 0.22f, 0.58f)
				: new Color(0.72f, 0.06f, 0.06f);
			Color enemyColor = new Color(0.72f, 0.06f, 0.06f);

			var ring = SelectionRing.Create(ringRadius, friendlyColor, enemyColor,
				isBuilding ? SelectionRing.Shape.Square : SelectionRing.Shape.Circle);
			ring.Position = new Vector3(0, 0.1f, 0);
			node.AddChild(ring);
			_selectionMarkers.Add(ring);

			if (healthMax > 0)
			{
				var bar = SelectionRing.CreateHealthBar(healthFraction);
				bar.Position = new Vector3(0, isBuilding ? 6f : 2.5f, 0);
				node.AddChild(bar);
				_selectionMarkers.Add(bar);
			}
		}

		// Rally-point marker (flag + path line) is cached across frames — see ReconcileRallyMarker.
		ReconcileRallyMarker();
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
			if (key.Keycode == Key.T) TrainVillager(Input.IsKeyPressed(Key.Shift));
			if (key.Keycode == Key.S) TrainSoldier(Input.IsKeyPressed(Key.Shift));
			if (key.Keycode == Key.Escape) { _placeBuildingMode = false; _selectedEntities.Clear(); }
			if (key.Keycode == Key.F5) QuickSave();
			if (key.Keycode == Key.F9) QuickLoad();
		}

		if (@event is InputEventMouseButton mb && mb.Pressed)
		{
			if (mb.ButtonIndex == MouseButton.Left)
			{
				if (_placeBuildingMode) { PlaceBuilding(mb.Position); return; }
				_dragStart = mb.Position;
				_dragSelecting = true;
				_isDragging = false;
			}
			else if (mb.ButtonIndex == MouseButton.Right)
				HandleRightClick(mb.Position, mb.CtrlPressed);
		}

		if (@event is InputEventMouseMotion mm && _dragSelecting && mm.Position.DistanceTo(_dragStart) > 8f)
			_isDragging = true;

		if (@event is InputEventMouseButton mbu && !mbu.Pressed && mbu.ButtonIndex == MouseButton.Left && _dragSelecting)
		{
			_dragSelecting = false;
			if (_isDragging) HandleDragSelect(_dragStart, mbu.Position);
			else HandleLeftClick(mbu.Position);
		}
	}

	private void HandleLeftClick(Vector2 screenPos)
	{
		var worldPos = ScreenToWorld(screenPos);
		if (worldPos == null) return;
		var entities = _sim.GetEntitiesAtPosition(worldPos.Value, 3f);
		_selectedEntities.Clear();
		if (entities.Count > 0) _selectedEntities.Add(entities[0]);

		if (_selectedEntities.Count == 0)
		{
			var nearby = _sim.GetEntitiesAtPosition(worldPos.Value, 30f);
			foreach (var eid in nearby)
			{
				var id = _sim.Sim.QueryInterface<IdentityComponent>(eid);
				var node = _sim.EntityNodes.GetValueOrDefault(eid);
				GD.Print($"[Click] miss at {worldPos.Value:F1} | nearby: {id?.Name ?? "?"} at {node?.Position:F1} dist={node?.Position.DistanceTo(worldPos.Value):F1} isBuilding={id?.IsBuilding}");
			}
			if (nearby.Count == 0)
				GD.Print($"[Click] miss at {worldPos.Value:F1} | NO entities within 30f at all");
		}
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
			// 按资源大类映射光标(原版按 specificType 细分 fish/fruit/meat 等;
			// 我们的 Supply 只有大类,food 统一走 grain)。
			return supply.Type switch
			{
				ResourceType.Wood => "action-gather-tree",
				ResourceType.Stone => "action-gather-rock",
				ResourceType.Metal => "action-gather-ore",
				_ => "action-gather-grain",
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

		bool isResource = false, isEnemy = false, isGarrisonTarget = false;
		foreach (var eid in targets)
		{
			targetEntity = eid;
			isResource = _sim.Sim.QueryInterface<ResourceSupply>(eid) != null;
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
		foreach (var unit in _selectedEntities)
		{
			if (isGarrisonTarget && targetEntity.HasValue
				&& _sim.Sim.QueryInterface<UnitAIComponent>(unit) != null)
			{
				// 右键己方驻军建筑 → 载入(原版 unit_actions garrison;
				// 宿主是否接受由 sim 侧 Garrisonable.CanGarrison 判)。
				_sim.CommandGarrison(unit, targetEntity.Value);
			}
			else if (isResource && targetEntity.HasValue
				&& _sim.Sim.QueryInterface<ResourceGatherer>(unit) != null)
			{
				// 采集者优先采集(鹿=enemy+resource 双身份:村民猎鹿=采集,
				// 女兵有弱攻击也不能去杀食材)。
				_sim.CommandGather(unit, targetEntity.Value);
			}
			else if (isEnemy && targetEntity.HasValue && _sim.Sim.QueryInterface<AttackComponent>(unit) != null)
			{
				// Ctrl+右键 = 捕获(原版 Ctrl+click → attack allowCapture=true);
				// 无捕获能力的单位自动退化普通攻击(GetBestAttackAgainst 选型)。
				_sim.CommandAttack(unit, targetEntity.Value, allowCapture);
			}
			else
			{
				_sim.MoveEntity(unit, worldPos.Value.X, worldPos.Value.Z);
				issuedMove = true;
			}
		}

		// 移动指令的"目标标记"(原版 unit_actions.js 的 move 动作 → DrawTargetMarker →
		// GuiInterface.AddTargetMarker("special/target_marker")):在点击处放红金动画标记,
		// 仅表现层、本地生成、不进网络/存档(命令本身已承载指令)。攻击/采集目标是实体本身,
		// 不画地面标记——与原版一致(只 move / map_flare 进 g_TargetMarker)。
		if (issuedMove)
			SpawnTargetMarker(worldPos.Value);
	}

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

	/// <summary>顶栏 Game Speed 按钮回调:打开倍率面板(本地表现层节奏,不暂停 sim)。</summary>
	public void OpenGameSpeedPanel() => _gameSpeedPanel?.Open();

	/// <summary>顶栏 Diplomacy 按钮回调:打开外交面板(立场/进贡,不暂停 sim)。</summary>
	public void OpenDiplomacyPanel() => _diplomacyPanel?.Open();

	/// <summary>顶栏 Trade 按钮回调:打开贸易面板(易物/贸易品比例,不暂停 sim)。</summary>
	public void OpenTradePanel() => _tradePanel?.Open();

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
		if (player == null) { _placeBuildingMode = false; return; }
		var (wood, stone, metal, food, buildTime) = GetBuildCost(_buildTemplate);
		if (!CanAfford(player, wood, stone, metal, food))
		{
			GD.Print($"Cannot afford {_buildTemplate}: needs {wood}W {stone}S {metal}M {food}F");
			_placeBuildingMode = false;
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
			// Stay in placement mode so the player can try another spot.
			return;
		}

		_ = buildTime; // build time comes from template data at execution; not needed here.
		string fullTemplate = MapBuildTemplateName(_buildTemplate);
		_placeBuildingMode = false;
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null)
			{
				_sim.CommandBuild(eid, fullTemplate, worldPos.Value.X, worldPos.Value.Z);
				break;
			}
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

	private static string MapBuildTemplateName(string name) => name switch
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
