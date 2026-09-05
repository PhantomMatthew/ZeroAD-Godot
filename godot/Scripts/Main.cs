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
	/// <summary>过场动画管理器(原版 graphics/CinemaManager;地图脚本
	/// PushPathToQueue 按名播放,播完广播事件到触发器)。</summary>
	private CinemaManager? _cinema;
	public CinemaManager? Cinema => _cinema;
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
	private readonly List<EntityId> _toRemove = new();   // UpdateSelectionMarkers 阵亡清理复用
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

	// ── 城墙拖放(原版 placement.js 墙模式):build 按钮选中墙组模板 →
	// 按下锚定起点,拖动实时拼链预览,松开全链下单;单点(未拖过阈值)= 单座塔楼。──
	private bool _wallDragMode;
	private bool _wallDragging;
	private Vector3 _wallStart;
	private WallPlacer.WallSetData? _wallSet;
	private readonly List<Node3D> _wallGhosts = new();
	private string _wallGhostSignature = "";
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
	// 加载失败/回退的用户可见报告:修"进不去图只能猜"——异常弹窗给地图名+错误,
	// 静默回退(错图换图/沙盒基地)收进 _loadWarnings,加载完成后一次弹出。
	private string _loadingMapDesc = "";
	private readonly System.Collections.Generic.List<string> _loadWarnings = new();
	private int _rmgenSpawnFailures;
	private PauseMenu? _pauseMenu;
	// FPS 叠层(overlay.fps 配置项驱动,原版 Display 类):右上角实时帧率。
	private CanvasLayer? _fpsOverlay;
	private Label? _fpsLabel;
	// 开发者覆盖层(F8 切换,诊断方案 4):左上角回合/FPS/实体数/选中数/状态 hash。
	private CanvasLayer? _devOverlay;
	private Label? _devLabel;
	// 第二梯队菜单面板(Diplomacy/Trade/Match Settings):模态叠层,不暂停 sim。
	// (Game Speed 已改为顶栏时间按钮下方的非模态弹出条,见 HUD.BuildGameSpeedPopover,
	// 对齐原版 GameSpeedControl 下拉位置。)
	private DiplomacyPanel? _diplomacyPanel;
	private TradePanel? _tradePanel;
	private StructreePanel? _structreePanel;
	private ViewerPanel? _viewerPanel;
	private DiagPanel? _diagPanel;   // F11 诊断日志面板(诊断方案 3)
	private MatchSettingsPanel? _matchSettingsPanel;

	public IReadOnlySet<EntityId> SelectedEntities => _selectedEntities;

	/// <summary>跟随选中单位(原版 camera.follow 热键 setCameraFollow):
	/// 选中首单位 → 相机平滑跟随;任何滚轮/移动输入打断(原版同款)。</summary>
	public void FollowSelectedUnit()
	{
		if (_camera == null || _selectedEntities.Count == 0) return;
		_camera.FollowTarget = _selectedEntities.First();
	}
	public bool IsTutorial => _isTutorial;
	public SimBridge Sim => _sim;
	public void SetCameraFocus(Vector3 pos) => _camera.SetFocus(pos);
	public Vector3? GetCameraFocus() => _camera?.Focus;
	public float GetCameraYaw() => _camera?.Yaw ?? 0f;

	public override void _Ready()
	{
		// 直进 Main.tscn 的 dev 路径(CLI 场景参数/autotest)也要接住日志——
		// Install 幂等,MainMenu 已装则此处空转。
		ZeroAD.Godot.Diagnostics.DiagGodot.Install();
		_camera = new RTSCamera();
		AddChild(_camera);
		// 过场动画管理器(原版 CinemaManager:相机路径队列播放,
		// 播完广播 OnCinemaPathEnded/OnCinemaQueueEnded)。
		_cinema = new CinemaManager(_camera);
		AddChild(_cinema);
		// 过场事件 → 触发器(原版 MT_CinemaPathEnded/MT_CinemaQueueEnded 广播;
		// 触发器脚本的"播完一径/队列空即推进剧情"经此驱动)。
		_cinema.PathEnded += name => _sim?.Sim.Triggers.CallEvent(_sim.Sim, "OnCinemaPathEnded", name);
		_cinema.QueueEnded += () => _sim?.Sim.Triggers.CallEvent(_sim.Sim, "OnCinemaQueueEnded", null);

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
		// 大气雾开关尊重用户选项(Graphics → Fog):此前硬编码 true 会在建世界时
		// 把用户关掉的雾又改回去(选项里关了但没效果)。
		env.FogEnabled = OptionsApplier.GetBool("fog", true);
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
		// STUN 探测完成 → 大厅状态行补公网地址(供好友直连;原版 host 注册同款)。
		_mp.OnStunResolved += () =>
		{
			if (_mp.ExternalAddress != null)
				_lobby.SetStatus($"Hosting on port — public: {_mp.ExternalAddress} (share for direct join)");
		};
		_lobby.OnClientConnect += (addr, port, observer) => StartMpClient(addr, port, observer);
		// Lobby slot editing (host only): each edit re-broadcasts the slot table to clients.
		_lobby.OnSlotEdit += (id, kind, civ, team) => _mp.HostSetSlot(id, kind, civ, team);
		// 全参数版(AI 难度/性格;原版 gamesetup_mp 的 aiDifficulties/aiBehaviors)。
		_lobby.OnSlotEditFull += (id, kind, civ, team, diff, behavior) =>
			_mp.HostSetSlot(id, kind, civ, team, diff, behavior);
		_lobby.OnMapEdit += map => _mp.HostSetMap(map);
		_mp.OnMapChanged += map => _lobby.SetMapDisplay(map);
		_lobby.OnStartGameRequested += () => _mp.HostStartGame();
		// gamesetup 选项(host 编辑 → 广播;客户端收到广播 → 只读刷新)。
		_lobby.OnOptionsEdit += o => _mp.HostSetOptions(o);
		_mp.OnLobbyOptionsChanged += o => { if (!_mp.IsHost) _lobby.RefreshOptions(o); };
		// 槽位认领显示(Peer N / 锁定):大厅行查询指向 controller。
		_lobby._peerLookup = id => _mp.IsSlotClaimedByPeer(id);
		_lobby._peerNameLookup = id => _mp.PeerIdOfSlot(id);
		// 大厅聊天(gamesetup_mp 聊天栏):发送 → 网络;收到 → 追加行。
		_lobby.OnChatSend += text => _mp.SendChat((int)_mp.LocalPlayerId, text);
		_mp.OnChatReceived += (pid, text) => _lobby.AppendChat(pid, text);
		// 局中掉线 → AI 接管(全端同点挂载,锁步一致;MP 大厅见 MultiplayerController)。
		_mp.OnPlayerAiTakeover += pid =>
		{
			if (!_gameStarted) return;
			_sim.AttachAi(pid);
			_hud?.ShowToast(Localization.Tr("Player") + $" {pid} " +
				Localization.Tr("has disconnected — AI takes over."));
		};
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
		// 开局时启动(BeginGameplayScenario 末尾 peace 列表)。3D 宿主注册(世界事件
		// 位置衰减;菜单场景不注册——Init 每次重进重放)。
		AudioManager.Init(this, FindDataRoot());
		AudioManager.Init3D(this);

		// 启动模式由 MainMenu 写入 GameLaunchConfig(进程级 env 仅 dev fallback,已由 MainMenu
		// 首次读取后清空——修 ChangeScene 回主菜单重触发自动开局的 bug)。SP/Tutorial 直接开局;
		// Load 冷加载存档;Multiplayer/Lobby 显大厅 LobbyUI(不自动开局,等用户 Host/Join)。
		// 先全量重放已存设置:音量/显示即时生效项 + 本会话场景图形项(light/env 已注册)。
		OptionsApplier.ApplyAll(GetNode<UserConfig>("/root/UserConfig"), GetTree(), inGame: true);
		// 恢复用户热键重绑到 InputMap（session-only，每次启动须重放）。
		HotkeyApplier.ApplyAll(GetNode<UserConfig>("/root/UserConfig"));

		var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		// dev 诊断入口(ZEROAD_AUTOTEST=mprmgen):自动 host random/botswanan_haven 并
		// 在加载后 dump 相机 focus——"MP rmgen 视角跳左下角"定位用,不进正常流程。
		if (System.Environment.GetEnvironmentVariable("ZEROAD_AUTOTEST") == "mprmgen")
		{
			CallDeferred(nameof(AutoTestMpRmgen));
			return;
		}
		switch (cfg.Mode)
		{
			// Lobby = 未配置裸跑 session(编辑器直开 Main.tscn 等):弹回真主菜单
			// (LobbyUI 的假主菜单已随收敛移除,MainMenu.tscn 是唯一主菜单)。
			case GameLaunchConfig.LaunchMode.Lobby:
				CallDeferred(nameof(BounceToMainMenu));
				break;
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

	/// <summary>ZEROAD_AUTOTEST=mprmgen 诊断:自动 host random/botswanan_haven,加载后
	/// dump 相机 focus/本地玩家实体数/地形尺寸,再注入滚轮事件复现"缩放跳角"。
	/// 结论写 Diag(Err 级,文件日志 user://logs/zeroad.log 与 stderr 双出)。</summary>
	private async void AutoTestMpRmgen()
	{
		StartMpHost(61234, 42);
		_mp.HostSetMap("random/botswanan_haven");
		_mp.HostStartGame();
		for (int i = 0; i < 600 && !_gameStarted; i++)
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		// 加载是分段 async:gameStarted 在最早的 Init 段即置位,多等一会儿让 Scenario 段跑完。
		await ToSignal(GetTree().CreateTimer(5.0), SceneTreeTimer.SignalName.Timeout);
		DumpAutotestFocus("after-load");
		await ToSignal(GetTree().CreateTimer(2.0), SceneTreeTimer.SignalName.Timeout);
		DumpAutotestFocus("after-2s-idle");
		var wheelDown = new InputEventMouseButton
		{
			ButtonIndex = MouseButton.WheelDown,
			Pressed = true,
			Position = GetViewport().GetMousePosition(),
		};
		Input.ParseInputEvent(wheelDown);
		await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
		DumpAutotestFocus("after-wheeldown");
		// 右键点击屏幕中央(模拟点雾外/任意地面)。
		var mp = GetViewport().GetVisibleRect().Size / 2;
		Input.ParseInputEvent(new InputEventMouseButton
			{ ButtonIndex = MouseButton.Right, Pressed = true, Position = mp });
		Input.ParseInputEvent(new InputEventMouseButton
			{ ButtonIndex = MouseButton.Right, Pressed = false, Position = mp });
		await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
		DumpAutotestFocus("after-rightclick");
		ZeroAD.Sim.Diag.Err("AUTOTEST", "done — quitting");
		GetTree().Quit();
	}

	private void DumpAutotestFocus(string tag)
	{
		var f = GetCameraFocus();
		int owned = _sim.Range.GetEntitiesByPlayer((int)_sim.LocalPlayerId).Count;
		ZeroAD.Sim.Diag.Err("AUTOTEST", $"{tag}: focus=({f?.X:F1},{f?.Y:F1},{f?.Z:F1}) " +
			$"localPlayer={_sim.LocalPlayerId} owned={owned} " +
			$"terrain={_sim.Terrain.MapSize}t×{_sim.Terrain.TileSize}m " +
			$"worldRootZ={_worldRoot.Position.Z:F1} worldSize={TerrainHeightService.WorldSize:F1}");
	}

	private void BounceToMainMenu() => GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");

	/// <summary>MP 入口(MainMenu 子菜单 Host New Game / Connect by IP):直显连接表单,
	/// 不再显 LobbyUI 遗留旧菜单(那是 MainMenu.tscn 存在前的假主菜单)。</summary>
	private void AutoMp()
	{
		var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		// CLI autostart MP 分支(-autostart-host/-autostart-client=IP):跳过连接表单直 host/join
		// (原版 autostart_host.js/autostart_client.js;端口缺省走 UserConfig multiplayerhosting.port)。
		if (cfg.MpAutoTarget.Length > 0)
		{
			var userCfg = GetNode<UserConfig>("/root/UserConfig");
			int port = cfg.MpAutoPort > 0 ? cfg.MpAutoPort
				: int.TryParse(userCfg.GetEffective("multiplayerhosting.port"), out int p) ? p : 25565;
			if (cfg.MpHost)
				StartMpHost(port, cfg.Seed);
			else
				StartMpClient(cfg.MpAutoTarget, port);
			return;
		}
		// dev:ZEROAD_SHOT=mphost/mpclient — 跳过连接表单直进大厅页并截图退出。
		if (System.Environment.GetEnvironmentVariable("ZEROAD_SHOT") == "mphost")
		{
			StartMpHost(61195, 42);
			MpLobbyShotDeferred();
			return;
		}
		if (System.Environment.GetEnvironmentVariable("ZEROAD_SHOT") == "mpclient")
		{
			StartMpClient("127.0.0.1", 61195);
			MpLobbyShotDeferred();
			return;
		}
		_lobby.EnterMpDirect(cfg.MpHost);
	}

	private void StartTutorial()
	{
		// 加载等待页(对齐原版 page_loading:顶部进度条 + 中央提示卡)。分阶段驱动:
		// BeginGameplay 拆成 Init/Session/Scenario 三段,段间 await 一帧让进度条重绘
		// (原 0.15s Timer 只保证首帧绘制,无法反映真实阶段进度)。
		var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		bool walkthrough = cfg.MapPath.Contains("starting_economy_walkthrough",
			System.StringComparison.Ordinal);
		_loadingOverlay = new LoadingOverlay(walkthrough
			? "Starting Economy Walkthrough" : "Introductory Tutorial");
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
			_loadWarnings.Clear();
			_loadingMapDesc = "";
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
			ShowLoadWarnings();
		}
		catch (System.Exception e)
		{
			// 加载失败不再 rethrow:async void 里抛异常只会留一个无地形的"天蓝空世界",
			// 用户看到的像卡死而不是崩溃。报错落日志 + 弹窗给出地图名与错误摘要,
			// 用户确认后才回主菜单(此前静默弹回,"进不去图"完全无法诊断)。
			ZeroAD.Sim.Diag.Err("Gameplay", $"EXCEPTION in load: {e}");
			ZeroAD.Sim.Diag.Err("Gameplay", $"Stack: {e.StackTrace}");
			ShowLoadErrorAndReturnToMenu(
				$"Failed to load map '{_loadingMapDesc}'.\n\n{e.GetType().Name}: {e.Message.Split('\n')[0]}");
		}
		finally
		{
			_loadingOverlay?.QueueFree();
			_loadingOverlay = null;
		}
	}

	/// <summary>加载致命错误:弹窗展示错误摘要,用户确认后重置配置并回主菜单。
	/// 三个加载入口(SP/MP staged、ColdLoad、Replay)共用——此前全是静默弹回菜单。</summary>
	private void ShowLoadErrorAndReturnToMenu(string message)
	{
		var dlg = new AcceptDialog
		{
			Title = "Map Load Error",
			DialogText = message + "\n\n(Returning to main menu.)",
			Exclusive = false,
		};
		AddChild(dlg);
		dlg.Confirmed += ReturnToMenuAfterLoadError;
		dlg.CloseRequested += ReturnToMenuAfterLoadError;
		dlg.PopupCentered();
	}

	private void ReturnToMenuAfterLoadError()
	{
		GetNode<GameLaunchConfig>("/root/GameLaunchConfig").Reset();
		GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
	}

	/// <summary>加载完成后的非致命警告(错图换图/沙盒基地/部分实体失败)一次弹完;
	/// 无警告不弹。弹窗不阻塞——对局已开始,用户看完即关。</summary>
	private void ShowLoadWarnings()
	{
		if (_loadWarnings.Count == 0) return;
		var dlg = new AcceptDialog
		{
			Title = "Map Load Warnings",
			DialogText = string.Join("\n", _loadWarnings),
			Exclusive = false,
		};
		AddChild(dlg);
		dlg.PopupCentered();
	}

	private void StartSinglePlayer(uint seed)
	{
		// SP 同样走加载等待页(page_loading:进度条 + 提示卡),标题取所选地图名。
		string spRel = PickSkirmishMapRel();
		_loadingOverlay = new LoadingOverlay(MapTitleFromPath(spRel), IsRandomMap(spRel));
		AddChild(_loadingOverlay);
		// 选图面板的槽位表(可能为 null = 旧默认 1v1);本地玩家 id = Human 槽的 id。
		var slots = GetNode<GameLaunchConfig>("/root/GameLaunchConfig").Slots;
		uint localId = 1;
		if (slots != null)
			foreach (var s in slots)
				if (s.Kind == ZeroAD.Sim.Net.PlayerSlotKind.Human) { localId = (uint)s.PlayerId; break; }
		RunStagedGameplayLoad(seed, localId, slots, tutorial: false, isMultiplayer: false, isHost: false);
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
	private void StartMpClient(string addr, int port, bool observer = false)
	{
		_mp.StartClient(addr, port, observer);
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
		// gamesetup_mp 选项(host 冻结并经大厅广播;双端各自写入,ApplyMatchOptions 落地)
		var o = _mp.LobbyOptions;
		cfg.MapSize = o.MapSize;
		cfg.BiomeId = o.BiomeId;
		cfg.PlayerPlacement = o.PlayerPlacement;
		cfg.StartingResources = o.StartingResources;
		cfg.PopulationCap = o.PopulationCap;
		cfg.GameSpeed = o.GameSpeed;
		cfg.CeasefireMinutes = o.CeasefireMinutes;
		cfg.Nomad = o.Nomad;
		cfg.Treasures = o.Treasures;
		cfg.ExploredMap = o.ExploredMap;
		cfg.RevealedMap = o.RevealedMap;
		cfg.AlliedView = o.AlliedView;
		cfg.LockedTeams = o.LockedTeams;
		cfg.Cheats = o.Cheats;
		cfg.VictoryConditions = new System.Collections.Generic.List<string>(o.VictoryConditions);
		// 观战者(localPlayerId==0):全图揭示(原版 observer 视野),命令面全关
		// (SimBridge 侧拦截:LocalPlayerId 0 不下发任何玩家命令)。
		if (playerId == 0)
			cfg.RevealedMap = true;
		string mpRel = string.IsNullOrEmpty(map) ? PickSkirmishMapRel() : map;
		_loadingOverlay = new LoadingOverlay(MapTitleFromPath(mpRel), IsRandomMap(mpRel));
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

	/// <summary>random/ 前缀 = rmgen 地图(原版加载页标题据此换 "Generating …")。</summary>
	private static bool IsRandomMap(string? rel) =>
		rel != null && rel.StartsWith("random/", System.StringComparison.Ordinal);

	/// <summary>阶段 1(重:模板解析+世界构建)。guard + InitWorld + MP 接线;返回生效槽位表。
	/// 拆段是为加载等待页:阶段间 await 一帧让进度条重绘(见 StartTutorial)。</summary>
	private IReadOnlyList<ZeroAD.Sim.Net.PlayerSlotSetup> BeginGameplayInit(uint seed, uint playerId,
		IReadOnlyList<ZeroAD.Sim.Net.PlayerSlotSetup>? slots, bool tutorial, bool isMultiplayer, bool isHost)
	{
		if (_gameStarted) throw new System.InvalidOperationException("BeginGameplayInit called twice");
		_gameStarted = true;
		_isTutorial = tutorial;
		_lobby.Hide();
		ZeroAD.Sim.Diag.Log("Tutorial", $"BeginGameplay start: tutorial={tutorial}");

		string? templatesPath = FindTemplatesPath();
		ZeroAD.Sim.Diag.Log("Tutorial", $"templatesPath={templatesPath ?? "null"}");

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
				new() { PlayerId = 1, Kind = ZeroAD.Sim.Net.PlayerSlotKind.Human,
					Civ = System.Environment.GetEnvironmentVariable("ZEROAD_CIV") is { Length: > 0 } c1 ? c1 : "athen",
					Team = -1 },
				new() { PlayerId = 2, Kind = ZeroAD.Sim.Net.PlayerSlotKind.AI,
					Civ = System.Environment.GetEnvironmentVariable("ZEROAD_CIV2") is { Length: > 0 } c2 ? c2 : "gaul",
					Team = -1 },
			});
		// "random" 文明统一在开局前解析(SP 选图/MP host 已在上游侧解析;这里兜底 dev 环境变量
		// ZEROAD_CIV=random 的路径)——sim/skirmish 替换只见真文明代码。
		if (effectiveSlots.Any(s => s.Civ == "random"))
			effectiveSlots = CivRandom.Resolve(effectiveSlots);
		_sim.InitWorld(templatesPath, seed, playerId, role, effectiveSlots);
		_worldSlots = effectiveSlots;   // rmgen 玩家 civ 列表(SetupRmgenTerrain)等用
		_sim.EnableTemplateHotReload();   // 开发期模板热载(debug+单机;内部自门)
		ZeroAD.Sim.Diag.Log("Tutorial", "InitWorld done");

		if (isMultiplayer)
		{
			// Wire the transport to the freshly built NetTurnManager. The host bootstraps
			// its empty leading turns so play can start immediately.
			_mp.AttachTurnManager(_sim.NetTurn);
			_mp.OnOOS += OnOOSDetected;
			ZeroAD.Sim.Diag.Log("MP", "AttachTurnManager done");
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
			ZeroAD.Sim.Diag.Log("Tutorial", "calling SetupTutorialWorld...");
			try
			{
				SetupTutorialWorld();
			}
			catch (System.Exception ex)
			{
				ZeroAD.Sim.Diag.Err("Tutorial", $"SetupTutorialWorld FAILED: {ex}");
				ZeroAD.Sim.Diag.Err("Tutorial", $"Stack: {ex.StackTrace}");
				// Don't rethrow — let the game continue without the tutorial scenario rather
				// than crash. The player can still see terrain and the panel.
			}
			ZeroAD.Sim.Diag.Log("Tutorial", "SetupTutorialWorld done");
		}
		else
			SetupGameWorld(playerId, effectiveSlots, isMultiplayer);

		// 停战(原版 gamesetup 的 Ceasefire 下拉):>0 分钟 → 全体非 gaia 互置中立,
		// 倒计时结束恢复外交;scenario 图的地图自带值优先(ApplyVictoryConditions 已启动,
		// 重复调用仅重置计时,语义一致)。
		var launchCfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		if (launchCfg.CeasefireMinutes > 0)
		{
			_sim.Sim.EndGame.CeasefireDuration = launchCfg.CeasefireMinutes * 60f;
			_sim.Sim.EndGame.StartCeasefire(_sim.Sim);
		}

		// gamesetup 其余选项(对齐原版 gamesettings 应用点):
		// StartingResources——四项资源同值覆盖(原版 helpers/Player.js:settings.StartingResources
		// 逐项改写);PopulationCap——PlayerComponent.MaxPopCap;GameSpeed——sim 倍率;
		// RevealedMap/ExploredMap——LOS;AlliedView——盟友共享视野总开关;胜利条件集合。
		ApplyMatchOptions(launchCfg);

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

		// dev 自检钩子:ZEROAD_AUTOTRAIN=1 时开局 ~6s 起对 CC 连下两批 5 个村民
		// (批量训练链路验证:队列叠加/批次完成/人口增长)。
		if (System.Environment.GetEnvironmentVariable("ZEROAD_AUTOTRAIN") == "1")
			AutotrainDeferred();

		// dev 截图钩子:ZEROAD_SHOT_SESSION=<秒[,秒...]> 开局 N 秒后视口截图存
		// user://session_shot_<N>s.png(不退出;窗口无需前台,后台可截)。
		foreach (var part in (System.Environment.GetEnvironmentVariable("ZEROAD_SHOT_SESSION") ?? "")
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			if (int.TryParse(part, out int shotSec))
				SessionShotDeferred(shotSec);

		// dev:MP 大厅页截图接力(MainMenu 的 ZEROAD_SHOT=mphost 只负责拉起 host;
		// 页面就绪后由本场景截图存 user://shot_mphost.png 并退出)。
		if (System.Environment.GetEnvironmentVariable("ZEROAD_SHOT") == "mphost")
			MpLobbyShotDeferred();

		// dev 镜头钩子:ZEROAD_SHOT_FOUNDATION=1 时轮询直到出现地基实体,
		// 把镜头对准它并拉近(配合 ZEROAD_SHOT_SESSION 验收建造视觉)。
		if (System.Environment.GetEnvironmentVariable("ZEROAD_SHOT_FOUNDATION") == "1")
			FocusFoundationDeferred();

		// dev 镜头钩子:ZEROAD_SHOT_DIST=<米> 设镜头距离(俯瞰验收地图全貌)。
		if (float.TryParse(System.Environment.GetEnvironmentVariable("ZEROAD_SHOT_DIST"), out float shotDist))
		{
			var cam = _camera;
			ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout)
				.OnCompleted(() => cam.SetDistance(shotDist));
		}

		// dev 镜头钩子:ZEROAD_SHOT_CENTER=x,z 镜头聚焦指定 sim 坐标(如 384,384 图心)。
		string shotCenter = System.Environment.GetEnvironmentVariable("ZEROAD_SHOT_CENTER") ?? "";
		var scParts = shotCenter.Split(',');
		if (scParts.Length == 2 && float.TryParse(scParts[0], out float scx) && float.TryParse(scParts[1], out float scz))
		{
			var cam = _camera;
			ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout)
				.OnCompleted(() => cam.SetFocus(new Vector3(scx, 0, scz)));
		}

		// dev 钩子:ZEROAD_EXPLORED=1 全图已探索(迷雾仍在;原版 gamesetup 的 Explored Map
		// 选项等价)——验证探索后静态资源/水面的显隐(棕榈排查用)。
		if (System.Environment.GetEnvironmentVariable("ZEROAD_EXPLORED") == "1")
			for (int p = 1; p <= ZeroAD.Sim.Components.LosGrid.MaxPlayers; p++)
				_sim.Range.Los.ExploreAll(p);

		// 开局按选项恢复 free camera(Graphics → Free Camera;上次 F3 开过则沿用)。
		if (OptionsApplier.GetBool("dev.freecamera", false))
			_camera.FreeFlyEnabled = true;

		// dev 钩子:ZEROAD_SELECT_CC=1 开局自动选中本地玩家的 CC(触发生产面板构建,
		// 配合 HUD-DIAG 验证时代过滤)。ZEROAD_SELECT_TOWER=1 选中/生成己方哨塔
		// (验证 upgrade_panel 图标)。
		if (System.Environment.GetEnvironmentVariable("ZEROAD_SELECT_TOWER") == "1")
		{
			EntityId tower = default;
			foreach (var e in _sim.Sim.AllEntities)
			{
				var id = _sim.Sim.QueryInterface<IdentityComponent>(e);
				var own = _sim.Sim.QueryInterface<OwnershipComponent>(e);
				if (id != null && own != null && own.PlayerId == (int)_sim.LocalPlayerId
					&& id.TemplateName.Contains("sentry_tower"))
				{
					tower = e;
					break;
				}
			}
			if (tower == default && _camera?.Focus is Vector3 f)
			{
				tower = _sim.SpawnFromTemplate("structures/athen/sentry_tower", f.X + 20, f.Z + 10);
				_sim.AssignOwner(tower, (int)_sim.LocalPlayerId);
			}
			if (tower != default)
				SelectOnly(new[] { tower });
		}
		if (System.Environment.GetEnvironmentVariable("ZEROAD_SELECT_CC") == "1")
		{
			if (int.TryParse(System.Environment.GetEnvironmentVariable("ZEROAD_BUILD_VILLAGE"), out int nHouses))
			{
				string civ = _sim.GetPlayer()?.Civ ?? "athen";
				for (int i = 0; i < nHouses; i++)
				{
					var h = _sim.SpawnFromTemplate($"structures/{civ}/house", 530 + i * 12, 130 + i * 8);
					_sim.AssignOwner(h, (int)_sim.LocalPlayerId);
				}
			}
			foreach (var e in _sim.Sim.AllEntities)
			{
				var id = _sim.Sim.QueryInterface<IdentityComponent>(e);
				var own = _sim.Sim.QueryInterface<OwnershipComponent>(e);
				if (id != null && own != null && own.PlayerId == (int)_sim.LocalPlayerId
					&& id.TemplateName.Contains("civil_centre"))
				{
					SelectOnly(new[] { e });
					break;
				}
			}
		}

		// dev 钩子:ZEROAD_FLORA_DUMP=<秒> 后逐模板打印合批 flora 的(总数/可见数)。
		if (int.TryParse(System.Environment.GetEnvironmentVariable("ZEROAD_FLORA_DUMP"), out int floraDumpSec))
		{
			var floraSim = _sim;
			ToSignal(GetTree().CreateTimer(floraDumpSec), SceneTreeTimer.SignalName.Timeout)
				.OnCompleted(() =>
				{
					foreach (var (tpl, total, vis) in floraSim.FloraStats())
						ZeroAD.Sim.Diag.Log("FloraDump", $"{tpl}: total={total} visible={vis}");
					foreach (var s in floraSim.FloraSampleBases(3))
						ZeroAD.Sim.Diag.Log("FloraDump", s);
					foreach (var s in floraSim.FloraReportLive())
						ZeroAD.Sim.Diag.Log("FloraDump", s);
					// 用户白圈区域(P1 西南废墟)逐实体精确诊断。
					foreach (var s in floraSim.FloraSampleVariantsInRect(440, 170, 540, 270))
						ZeroAD.Sim.Diag.Log("FloraDump", s);
					// sim 侧同区域实体的真实 LosVisibility(P1)。
					foreach (var s in floraSim.FloraLosInRect(440, 170, 540, 270))
						ZeroAD.Sim.Diag.Log("FloraDump", s);
					// sim 侧高度探针:绿洲心(512,512)/P1 基地(549,160)/图角(100,900)。
					var terr = ZeroAD.Sim.Components.SimSystem.Terrain;
					ZeroAD.Sim.Diag.Log("FloraDump",
						$"simH(512,512)={ZeroAD.Sim.Components.SimSystem.TerrainHeight(ZeroAD.Sim.Maths.Fixed.FromFloat(512), ZeroAD.Sim.Maths.Fixed.FromFloat(512)).ToFloat():F2} " +
						$"simH(549,160)={ZeroAD.Sim.Components.SimSystem.TerrainHeight(ZeroAD.Sim.Maths.Fixed.FromFloat(549), ZeroAD.Sim.Maths.Fixed.FromFloat(160)).ToFloat():F2} " +
						$"simH(100,900)={ZeroAD.Sim.Components.SimSystem.TerrainHeight(ZeroAD.Sim.Maths.Fixed.FromFloat(100), ZeroAD.Sim.Maths.Fixed.FromFloat(900)).ToFloat():F2} " +
						$"terrMapSize={terr?.MapSize} tileSize={terr?.TileSize}");
				});
		}

		ZeroAD.Sim.Diag.Log("Tutorial", _isTutorial
			? "Introductory Tutorial started"
			: $"MS6 Game started: player={playerId}");
	}

	/// <summary>dev:MP 大厅就绪后截图退出(与 MainMenu 的 mphost 钩子配套)。</summary>
	private async void MpLobbyShotDeferred()
	{
		// dev 配合:ZEROAD_MATCH_TAB=0/1/2 切大厅页签再截(等大厅页建完)。
		if (int.TryParse(System.Environment.GetEnvironmentVariable("ZEROAD_MATCH_TAB"), out int mtTab))
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			_lobby.DevSelectTab(mtTab);
		}
		// dev 配合:ZEROAD_MATCH_MAPTYPE=0/1/2 预选大厅 Map Type 再截(等大厅页建完)。
		if (int.TryParse(System.Environment.GetEnvironmentVariable("ZEROAD_MATCH_MAPTYPE"), out int mtIdx))
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			_lobby.DevShowMapType(mtIdx);
		}
		await ToSignal(GetTree().CreateTimer(2.5), SceneTreeTimer.SignalName.Timeout);
		var img = GetViewport().GetTexture().GetImage();
		img?.SavePng("user://shot_mphost.png");
		ZeroAD.Sim.Diag.Log("Shot", "saved user://shot_mphost.png");
		// ZEROAD_MP_STAY=1 时驻留(双端联测:host 不能死)。
		if (System.Environment.GetEnvironmentVariable("ZEROAD_MP_STAY") != "1")
			GetTree().Quit();
	}

	/// <summary>dev 钩子:N 秒后视口截图(可多次:用 ZEROAD_SHOT_SESSION 逗号秒数)。</summary>
	private async void SessionShotDeferred(int seconds)
	{
		await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
		var img = GetViewport().GetTexture().GetImage();
		string p = $"user://session_shot_{seconds}s.png";
		img.SavePng(p);
		ZeroAD.Sim.Diag.Log("Shot", $"saved {p}");
	}

	/// <summary>读 maps/random/{name}.json 的 settings.CircularMap（缺失/读取失败 → true，
	/// 上游绝大多数随机图为圆形可玩区）。</summary>
	private static bool ReadRandomMapCircular(string? dataRoot, string mapName)
	{
		if (dataRoot == null) return true;
		try
		{
			string path = System.IO.Path.Combine(dataRoot, "maps", "random", mapName + ".json");
			if (!System.IO.File.Exists(path)) return true;
			using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
			if (doc.RootElement.TryGetProperty("settings", out var settings) &&
				settings.TryGetProperty("CircularMap", out var cm) &&
				cm.ValueKind == System.Text.Json.JsonValueKind.False)
				return false;
		}
		catch { }
		return true;
	}

	/// <summary>应用 gamesetup 选项（世界建成后、放行回合前调用）。</summary>
	private void ApplyMatchOptions(GameLaunchConfig cfg)
	{
		// 胜利条件(EndGameManager;空列表 = 默认征服)
		if (cfg.VictoryConditions.Count > 0)
			_sim.Sim.EndGame.SetVictoryConditions(cfg.VictoryConditions);

		// 游戏速度倍率(SimBridge 累加器;1.0 默认)
		if (cfg.GameSpeed > 0)
			_sim.SpeedMultiplier = cfg.GameSpeed;

		// 盟友视野共享总开关(原版 Allied View 默认开)
		ZeroAD.Sim.Components.RangeManager.AlliedVisionEnabled = cfg.AlliedView;

		foreach (var ent in _sim.Sim.AllEntities)
		{
			var pc = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.PlayerComponent>(ent);
			if (pc == null) continue;
			// 起始资源(原版 settings.StartingResources:四项同值覆盖)
			if (cfg.StartingResources > 0)
			{
				pc.Wood = cfg.StartingResources;
				pc.Food = cfg.StartingResources;
				pc.Stone = cfg.StartingResources;
				pc.Metal = cfg.StartingResources;
			}
			// 人口上限
			if (cfg.PopulationCap > 0)
				pc.MaxPopCap = cfg.PopulationCap;
		}

		// 迷雾:RevealedMap 全图可见;ExploredMap 全图已探索(迷雾仍在)
		if (cfg.RevealedMap || cfg.ExploredMap)
		{
			for (int p = 1; p <= ZeroAD.Sim.Components.LosGrid.MaxPlayers; p++)
			{
				if (cfg.RevealedMap)
					_sim.Range.SetLosRevealAll(p, true);
				else
					_sim.Range.Los.ExploreAll(p);
			}
		}
	}

	/// <summary>dev 钩子:轮询直到 sim 里出现地基实体,镜头对准(持续跟随到完工,
	/// 覆盖建造全程的截图机位)。</summary>
	private async void FocusFoundationDeferred()
	{
		for (int i = 0; i < 240; i++)
		{
			foreach (var e in _sim.Sim.AllEntities)
			{
				var f = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.FoundationComponent>(e);
				if (f == null || f.IsBuilt) continue;
				var p = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.PositionComponent>(e);
				if (p == null) continue;
				_camera.SetFocus(new Vector3(p.Position.X.ToFloat(), 0, p.Position.Z.ToFloat()));
				_camera.SetDistance(40f);
				goto found;
			}
			await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
		}
	found: ;
	}

	/// <summary>dev 钩子:对本地 CC 连下两批 5 个单位,随后报队列/人口状态。</summary>
	private async void AutotrainDeferred()
	{
		await ToSignal(GetTree().CreateTimer(6.0), SceneTreeTimer.SignalName.Timeout);
		int lp = (int)_sim.LocalPlayerId;
		ZeroAD.Sim.EntityId cc = default;
		string civ = "athen";
		bool found = false;
		foreach (var e in _sim.Sim.AllEntities)
		{
			var own = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.OwnershipComponent>(e);
			if (own == null || own.PlayerId != lp) continue;
			var id = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.IdentityComponent>(e);
			if (id == null || !id.TemplateName.Contains("/civil_centre")) continue;
			civ = id.TemplateName.Split('/')[1];
			cc = e; found = true; break;
		}
		if (!found)
		{
			ZeroAD.Sim.Diag.Err("Autotrain", "no CC found");
			return;
		}
		string unit = $"units/{civ}/support_civilian";
		// 先塞资源排除经济拒绝,纯验证批量队列机制(dev 钩子专用)。
		var p0 = _sim.Sim.Players.GetPlayerEntity(lp);
		if (p0 != null) { p0.Food = 5000; p0.Wood = 5000; p0.Stone = 5000; p0.Metal = 5000; }
		_sim.CommandTrain(cc, unit, batch: true);
		_sim.CommandTrain(cc, unit, batch: true);
		ZeroAD.Sim.Diag.Log("Autotrain", $"queued 2×5 {unit}");

		for (int i = 0; i < 6; i++)
		{
			await ToSignal(GetTree().CreateTimer(5.0), SceneTreeTimer.SignalName.Timeout);
			var queue = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.ProductionQueue>(cc);
			var player = _sim.Sim.Players.GetPlayerEntity(lp);
			ZeroAD.Sim.Diag.Log("Autotrain",
				$"t+{(i + 1) * 5 + 6}s queue={queue?.QueueCount ?? -1} progress={queue?.Progress ?? -1:F1} " +
				$"popUsed={player?.PopUsed ?? -1} popLimit={player?.PopulationLimit ?? -1} food={player?.Food ?? -1}");
		}
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
			ZeroAD.Sim.Diag.Err("Autobuild", "no CC or builder found");
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
				ZeroAD.Sim.Diag.Log("Autobuild", $"ordered {house} at +({ox},{oz}) — watch the rise");
				// 视口自证:镜头对准工地,按进度连拍存 user://autobuild_t*.png。
				float h = TerrainHeightService.Sample(ccPos.Value.X + ox, ccPos.Value.Z + oz);
				_camera.SetFocus(new Vector3(ccPos.Value.X + ox, h, ccPos.Value.Z + oz));
				for (int shot = 0; shot < 4; shot++)
				{
					await ToSignal(GetTree().CreateTimer(5.0), SceneTreeTimer.SignalName.Timeout);
					var img = GetViewport().GetTexture().GetImage();
					string shotPath = $"user://autobuild_t{shot * 5 + 14}s.png";
					img.SavePng(shotPath);
					ZeroAD.Sim.Diag.Log("Autobuild", $"shot saved: {shotPath}");
				}
				return;
			}
		}
		ZeroAD.Sim.Diag.Err("Autobuild", "all placements rejected");
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
		_pauseMenu = new PauseMenu(_sim, _mp);
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
		// 模板查看器(原版 selection_panels showTemplateDetails → page_viewer):
		// 选中实体/生产图标右键 → 完整信息面板。
		_viewerPanel = new ViewerPanel();
		AddChild(_viewerPanel);
		// 诊断日志面板(F11):tag 勾选静音 + 最近日志。非模态,不暂停 sim。
		_diagPanel = new DiagPanel();
		AddChild(_diagPanel);

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

		// 开发者覆盖层(F8):半透明底,左上角多行调试信息。默认隐藏。
		_devOverlay = new CanvasLayer { Layer = 46, Visible = false };
		var devBg = new PanelContainer { AnchorsPreset = (int)Control.LayoutPreset.TopLeft, OffsetLeft = 8, OffsetTop = 8 };
		var sb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.6f), ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 };
		devBg.AddThemeStyleboxOverride("panel", sb);
		_devLabel = new Label { Theme = UITheme.GetTheme() };
		_devLabel.AddThemeFontSizeOverride("font_size", 13);
		devBg.AddChild(_devLabel);
		_devOverlay.AddChild(devBg);
		AddChild(_devOverlay);
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
			ZeroAD.Sim.Diag.Err("LoadGame", $"cannot cold-load slot '{cfg.LoadSlot}': " +
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
			ZeroAD.Sim.Diag.Err("Replay", $"cannot open slot '{cfg.ReplaySlot}'");
			ShowLoadErrorAndReturnToMenu($"Cannot open replay slot '{cfg.ReplaySlot}'.");
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
			ZeroAD.Sim.Diag.Err("Replay", $"playback init failed: {e}");
			ShowLoadErrorAndReturnToMenu(
				$"Failed to start replay '{reader.Meta.Description}'.\n\n{e.GetType().Name}: {e.Message.Split('\n')[0]}");
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
		ZeroAD.Sim.Diag.Log("Replay", $"started '{meta.Description}' (commandDelay {meta.CommandDelay})");
	}

	/// <summary>冷加载分阶段驱动(同 RunTutorialLoadStages:段间 await 一帧让进度条重绘)。</summary>
	private async void RunColdLoadStages(SaveMeta meta, GameLaunchConfig cfg)
	{
		try
		{
			_loadWarnings.Clear();
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			_loadingOverlay!.SetProgress(0.3f);
			ColdLoad(meta);
			_loadingOverlay.SetProgress(1f);
			ShowLoadWarnings();
		}
		catch (System.Exception e)
		{
			ZeroAD.Sim.Diag.Err("LoadGame", $"cold-load failed: {e}");
			ShowLoadErrorAndReturnToMenu(
				$"Failed to load save '{meta.Description}'.\n\n{e.GetType().Name}: {e.Message.Split('\n')[0]}");
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
		ZeroAD.Sim.Diag.Log("LoadGame", $"cold-loaded '{meta.Slot}' (turn {turn}, map {meta.MapPath})");
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
				ZeroAD.Sim.Diag.Log("Main", $"Found templates at: {dir}");
				return dir;
			}
		}
		ZeroAD.Sim.Diag.Err("Main", "FindTemplatesPath: templates dir not found under binaries/data/mods/public/simulation/templates");
		return null;
	}

	private void SetupTerrain(string? pmpRelPath = null)
	{
		_loadingMapDesc = pmpRelPath ?? "(auto terrain)";
		// 随机地图：路径以 "random/" 开头 → 走 rmgen 生成
		if (pmpRelPath != null && pmpRelPath.StartsWith("random/"))
		{
			string mapName = pmpRelPath.Substring("random/".Length);
			_loadingMapDesc = pmpRelPath;
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
				var (terrainNode, overlayMesh) = TerrainRenderer.CreateFromHeightmap(pmp);
				// 地形顶点已预翻转为世界坐标(TerrainRenderer 注释):挂场景根(无负 scale),
				// 两个渲染器都走原生光照/受影;阴影直接自投,无需镜像代理。
				AddChild(terrainNode);
				_worldRoot.Position = new Vector3(0f, 0f, pmp.MapSizeMeters);
				// 雾/领土 overlay:独立整图 mesh 的透明 MIX 层(+3cm 防 z-fighting)——地形本体
				// 现在是按 patch 分块的多个 StandardMaterial3D(受影),没有单一 mesh 可复用。
				var fogOverlay = new MeshInstance3D
				{
					Mesh = overlayMesh,
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
				ZeroAD.Sim.Diag.Log("Main", $"Loaded PMP terrain: {pmpPath} ({pmp.PatchesPerSide} patches, {pmp.MapSizeMeters}m, height at spawn: {h:F1}m)");

				string? xmlPath = pmpPath.Replace(".pmp", ".xml");
				// 地图 Environment 光照(太阳方向/色 + 环境光 + 雾色,公式对齐 CLightEnv);
				// 镜像世界后太阳必须随之镜像,否则面向相机的坡面整体背光发暗。
				(MapEnvironment.LoadFromXml(xmlPath) ?? MapEnvironment.Default).Apply(_light, _env, _camera);
				// 过场路径注册(原版 MapReader::ReadPaths:地图 <Paths> 段
				// → CinemaManager.AddPath;触发器脚本按名 PushPathToQueue 播放)。
				_cinema?.LoadFromMapXml(xmlPath);
				// HQ 上采样(MSAA 3D 2x/4x,原版 HQ 选项;Viewport 属性)。
				MapEnvironment.ApplyViewport(GetViewport());
				var water = WaterRenderer.LoadWaterFromXml(xmlPath);
				float waterHeight = water?.Height ?? -999f;
				if (water != null)
				{
					// 水面只画洼地(需地形采样;TerrainHeightService 已在上方 Set)。
					WaterRenderer.TerrainHeight = TerrainHeightService.Sample;
					var waterMesh = WaterRenderer.CreateWaterPlane(water, pmp.MapSizeMeters);
					_worldRoot.AddChild(waterMesh);
					ZeroAD.Sim.Diag.Log("Main", $"Water: height={water.Height:F1}m color={water.Color}");
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
				ZeroAD.Sim.Diag.Err("Main", $"PMP load failed: {e.Message}, falling back to generated terrain");
				_loadWarnings.Add($"Map file '{_loadingMapDesc}' failed to load " +
					$"({e.Message.Split('\n')[0]}) — using generated terrain instead.");
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
		MapEnvironment.Default.Apply(_light, _env, _camera);
		_camera.SetFocus(new Vector3(130, 0, 122));
		// Generated terrain has no water by default; mark everything land so placement still works.
		// 先同步 sim 侧地形尺寸:生成图 128 tiles=512m,缺省 64 tiles=256m ——不配则
		// 256m 外放置 FailOutOfBounds、寻路网格也只有 1/4(rmgen 路径同款坑的姊妹分支)。
		_sim.Terrain?.Configure(map.VerticesPerSide - 1, map.TileSize);
		FillPassabilityAllLand();
		ZeroAD.Sim.Diag.Log("Main", "Using generated terrain (no PMP found)");
	}

	/// <summary>Build a [MapSize,MapSize] passability grid from the PMP heightmap + water level and
	/// <summary>随机地图生成（rmgen C#）。调 MapRegistry.Generate → MapExport → PmpMap → TerrainRenderer。
	/// 接入 SetupTerrain 的 "random/" 路径前缀分支。</summary>
	private void SetupRmgenTerrain(string mapName)
	{
		ZeroAD.Sim.Diag.Log("Main", $"Generating random map: {mapName}");
		var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		uint seed = cfg.Seed;
		// gamesetup Map Size 下拉(原版默认 Normal 256);cfg.MapSize=0 = 未显式设置(ZEROAD_MAP 等旁路)
		int mapSize = cfg.MapSize > 0 ? cfg.MapSize : 256;
		string? dataRoot = FindDataRoot();

		var rng = new ZeroAD.Sim.RmgenMath.RmgenRng(seed);
		var settings = new ZeroAD.Sim.Rmgen.Common.MapSettings
		{
			Size = mapSize,
			Seed = seed,
			// 图形状读 maps/random/{name}.json 的 settings.CircularMap(上游 79/84 为圆形;
			// 此前硬编码 false → 圆图变方图,边角可玩区/布置全变)。
			CircularMap = ReadRandomMapCircular(dataRoot, mapName),
			DataRoot = dataRoot,   // biome JSON(rmbiome/generic/*.json)经 junction 读取
			// gamesetup 选项:Nomad/PlayerPlacement(biome 在 BiomeLoader 处经 BiomeData 下发)
			Nomad = cfg.Nomad,
			PlayerPlacement = cfg.PlayerPlacement.Length > 0 ? cfg.PlayerPlacement : "circle",
		};
		// gamesetup Biome 下拉:非 random 时预解析(覆盖图内随机自选,同上游 gamesetup biome 语义)
		if (cfg.BiomeId.Length > 0 && cfg.BiomeId != "random")
			settings.BiomeData = ZeroAD.Sim.Rmgen.Common.BiomeLoader.Load(
				settings.DataRoot, cfg.BiomeId, rng);
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
			ZeroAD.Sim.Diag.Err("Main", $"Unknown random map type: {mapName}, falling back to arcadia");
			_loadWarnings.Add($"Unknown random map '{mapName}' — loaded a fallback map instead.");
			SetupTerrain(null);
			return;
		}

		// MapExport → PmpMap 适配(共享实现,封装 VerticesPerSide/TileTex2 两个坑)
		var pmp = PmpMap.FromExport(export);

		// 地形渲染（复用 PMP 路径）
		var (terrainNode, overlayMesh) = TerrainRenderer.CreateFromHeightmap(pmp);
		AddChild(terrainNode);
		_worldRoot.Position = new Vector3(0f, 0f, pmp.MapSizeMeters);

		var fogOverlay = new MeshInstance3D
		{
			Mesh = overlayMesh,
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

		// 地图环境(rmgen environment.js 的 setSkySet/setSun*/setFog*/setPP* 结果):
		// 天光/雾/后处理 + 水面。此前随机图恒用 MapEnvironment.Default,各图专属氛围全丢。
		MapEnvironment.FromRmgen(export.Environment).Apply(_light, _env, _camera);
		MapEnvironment.ApplyViewport(GetViewport());

		var rmgenWater = WaterRenderer.FromRmgen(export.Environment);
		WaterRenderer.TerrainHeight = TerrainHeightService.Sample;
		_worldRoot.AddChild(WaterRenderer.CreateWaterPlane(rmgenWater, pmp.MapSizeMeters));
		_sim.Sim.Water.SetWaterLevel(ZeroAD.Sim.Maths.Fixed.FromFloat(rmgenWater.Height));

		// 可通行性(rmgen 陆水:超过水面高度=Land,否则 Water)+ 顶点高度网格。
		// 水位取地图环境的 setWaterHeight(未设定则 SEA_LEVEL=20m ——
		// rmgen 内部水面高度 0 + SEA_LEVEL 偏移)。
		FillPassabilityAllLand(pmp, rmgenWater.Height);

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
			catch (System.Exception ex) { ZeroAD.Sim.Diag.Warn("Main", $"rmgen entity spawn failed: {ent.TemplateName}: {ex.Message}"); _rmgenSpawnFailures++; }
		}

		if (_rmgenSpawnFailures > 0)
		{
			_loadWarnings.Add($"{_rmgenSpawnFailures} entit(y/ies) failed to spawn on '{mapName}' " +
				"(see log for details).");
			_rmgenSpawnFailures = 0;
		}

		_sim.MapPath = $"random/{mapName}";
		// 地图脚本(_triggers.js 移植件):触发点已注册完毕,安装并跑 OnInit。
		_sim.InitMapScript(mapName);
		ZeroAD.Sim.Diag.Log("Main", $"rmgen terrain ready: {mapName} ({export.Size}×{export.Size}, {export.Entities.Count} entities)");
	}

	/// hand it to the sim-side TerrainComponent. Tiles at/below water are Water, the rest Land.
	/// Also reconfigures TerrainComponent + ObstructionManager bounds to the real map size — they
	/// default to 256m (64 tiles) but real maps are larger (tutorial = 768m), and without this the
	/// placement checks wrongly flag everything in-bounds as FailOutOfBounds.</summary>
	private void FillPassabilityFromPmp(PmpMap pmp, float waterHeight)
	{
		var terrain = _sim.Terrain;
		if (terrain == null) return;
		// 通行类水深/岸线规则的真实水位(原版 CTerrain water level;pathfinder.xml 的
		// ship MinWaterDepth / building-shore MaxShoreDistance 依赖)。
		terrain.SetWaterLevel(ZeroAD.Sim.Maths.Fixed.FromFloat(waterHeight));

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
				if (groundH <= waterHeight)
				{
					grid[tx, tz] = ZeroAD.Sim.Components.TerrainClass.Water;
					continue;
				}
				// 坡度(原版 CTerrain::GetSlopeFixed):4 个角最高 − 最低,除以 4m tile 边长。
				// > MaxTerrainSlope(1.0,即 45°)标 Impassable——悬崖/陡坡不该能走。此前只判
				// 水/陆,从不算坡度 → 单位能从山上走过。
				float h00 = pmp.GetHeight(tx, tz);
				float h10 = pmp.GetHeight(tx + 1, tz);
				float h01 = pmp.GetHeight(tx, tz + 1);
				float h11 = pmp.GetHeight(tx + 1, tz + 1);
				float hi = Mathf.Max(Mathf.Max(h00, h10), Mathf.Max(h01, h11));
				float lo = Mathf.Min(Mathf.Min(h00, h10), Mathf.Min(h01, h11));
				float slope = (hi - lo) / terrain.TileSize;
				grid[tx, tz] = slope > 1.0f
					? ZeroAD.Sim.Components.TerrainClass.Impassable
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

	private void FillPassabilityAllLand(PmpMap? pmp = null, float waterHeight = -999f)
	{
		_sim.Terrain?.SetWaterLevel(ZeroAD.Sim.Maths.Fixed.FromFloat(waterHeight));
		var terrain = _sim.Terrain;
		if (terrain == null) return;
		// rmgen 图也要先把 TerrainComponent 配成真实尺寸:缺省 64×4=256m,而 rmgen 图
		// 通常 192×4=768m。不配置的话 passability/障碍/LOS/领土网格全按 256m 建——
		// 超出的区域放置全 FailOutOfBounds、永久黑雾、寻路网格也只有 1/9。
		// (PMP 路径在 FillPassabilityFromPmp 里 Configure,这里之前漏了。)
		if (pmp != null && (terrain.MapSize != pmp.TilesPerSide || terrain.TileSize != PmpMap.TileSize))
			terrain.Configure(pmp.TilesPerSide, PmpMap.TileSize);
		int n = terrain.MapSize;
		var grid = new ZeroAD.Sim.Components.TerrainClass[n, n];
		// Default Land (0) is already the zero value, so no need to fill explicitly.
		// 有高度图时:水(低于水面)+ 悬崖(坡度>1.0=45°)标对应类别。rmgen 水面在米制
		// SEA_LEVEL=20m(heightmap 编码 currentHeight+20;原版 alpine_lakes.js 水 tile
		// 内部高度 -5 → 米制 15 < 20)。
		if (pmp != null)
		{
			for (int tz = 0; tz < n; tz++)
				for (int tx = 0; tx < n; tx++)
				{
					float wx = (tx + 0.5f) * terrain.TileSize;
					float wz = (tz + 0.5f) * terrain.TileSize;
					if (pmp.GetHeightWorld(wx, wz) < waterHeight)
					{
						grid[tx, tz] = ZeroAD.Sim.Components.TerrainClass.Water;
						continue;
					}
					float h00 = pmp.GetHeight(tx, tz);
					float h10 = pmp.GetHeight(tx + 1, tz);
					float h01 = pmp.GetHeight(tx, tz + 1);
					float h11 = pmp.GetHeight(tx + 1, tz + 1);
					float hi = Mathf.Max(Mathf.Max(h00, h10), Mathf.Max(h01, h11));
					float lo = Mathf.Min(Mathf.Min(h00, h10), Mathf.Min(h01, h11));
					if ((hi - lo) / terrain.TileSize > 1.0f)
						grid[tx, tz] = ZeroAD.Sim.Components.TerrainClass.Impassable;
				}
		}
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
		// 领土网格同尺寸(PMP 路径同款调用;缺了它领土判定/显示也按 256m)。
		_sim.Territory.SetBounds((int)worldM);
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
		// 教程图按 GameLaunchConfig.MapPath(战役 eco_walkthrough 走这;
		// 空 = 主菜单 Tutorial 钮 → introductory)。
		var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		string mapRel = !string.IsNullOrEmpty(cfg.MapPath) && cfg.MapPath.Contains("tutorials/")
			? cfg.MapPath : "maps/tutorials/introductory_tutorial.pmp";
		ZeroAD.Sim.Diag.Log("Tutorial", $"SetupTutorialWorld: loading terrain ({mapRel})...");
		SetupTerrain(mapRel);
		ZeroAD.Sim.Diag.Log("Tutorial", "terrain loaded");

		string? dataRoot = FindDataRoot();
		ZeroAD.Sim.Diag.Log("Tutorial", $"dataRoot={dataRoot ?? "null"}");
		if (dataRoot != null)
		{
			ZeroAD.Sim.Diag.Log("Tutorial", "loading scenario...");
			var scenario = _sim.LoadTutorialScenario(dataRoot,
				mapRel.Contains("starting_economy_walkthrough") ? "starting_economy_walkthrough"
					: "introductory_tutorial");
			if (scenario != null)
			{
				ZeroAD.Sim.Diag.Log("Tutorial", $"scenario loaded: {scenario.Entities.Count} entities, camera=({scenario.CameraX},{scenario.CameraZ})");
				// 开局视角 = 场景作者机位(Position + Rotation + Declination,原版 GameView
				// 语义);无 Camera 元素时回退聚焦 P1 市政厅。
				if (scenario.HasCamera)
				{
					var camPos = new Vector3(scenario.CameraX, scenario.CameraY, scenario.CameraZ);
					_camera.PlaceFromScenarioCamera(camPos, scenario.CameraRotation, scenario.CameraDeclination);
					ZeroAD.Sim.Diag.Log("Tutorial", $"restored scenario camera pose {camPos} rot={scenario.CameraRotation:F2} decl={scenario.CameraDeclination:F2}");
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
							ZeroAD.Sim.Diag.Log("Tutorial", $"focusing P1 civic centre at ({focusX},{focusZ})");
							break;
						}
					}
					float h = TerrainHeightService.Sample(focusX, focusZ);
					_camera.SetFocus(new Vector3(focusX, h, focusZ));
				}
			}
			else
			{
				ZeroAD.Sim.Diag.Err("Tutorial", "LoadTutorialScenario returned null!");
			}
		}
		else
		{
			ZeroAD.Sim.Diag.Err("Tutorial", "FindDataRoot returned null — scenario cannot load");
		}

		ZeroAD.Sim.Diag.Log("Tutorial", "StartTutorial...");
		_sim.StartTutorial(mapRel);
		ZeroAD.Sim.Diag.Log("Tutorial", "showing panel...");
		_tutorialPanel.ShowTutorial();
		ZeroAD.Sim.Diag.Log("Tutorial", "SetupTutorialWorld complete");
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
					// 队伍外交播种(同教程路径:同队互盟,否则敌对)。
					// 槽位表的队伍选择优先于地图 PlayerData(选图面板里改的队要生效);
					// 槽位缺的玩家回退地图值。
					var teams = new Dictionary<int, int>();
					foreach (var pd in scenario.Players)
						teams[pd.PlayerId] = pd.Team;
					foreach (var slot in slots)
						teams[slot.PlayerId] = slot.Team;
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
			// dev 自动地形入口(无图)不算回退,不警告。
			if (_loadingMapDesc != "(auto terrain)")
				_loadWarnings.Add($"Map '{_loadingMapDesc}' provided no playable entities — " +
					"spawned sandbox corner bases instead.");
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
			{
				// 槽位难度/性格(gamesetup+大厅下拉;原版 playerAI.difficulty/behavior);
				// autostart -autostart-aidiff 为 dev 兜底。
				var launchCfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
				int diff = slot.AIDifficulty >= 0 ? slot.AIDifficulty
					: launchCfg.AiDifficulties.TryGetValue(slot.PlayerId, out int d)
						? d : ZeroAD.Sim.AI.Petra.DifficultyLevel.Medium;
				_sim.AttachAi(slot.PlayerId, diff,
					slot.AIBehavior.Length > 0 ? slot.AIBehavior : "random");
			}
		}

		// Ownerless neutral soldiers — mid-map (768m world → centre ~384) so they overlap no base.
		// (moved into the !spawnedFromMap branch above — skirmish maps author their own entities)

		// Initial buildings/units were spawned AFTER the map-load RebuildGrid; rebuild once more so
		// pathing accounts for the town centres and any scenario buildings.
		_sim.Pathfinder.RebuildGrid();
		// AI 水陆区域图(Accessibility)随网格定型重建(Petra 海军/码头选址的前置)。
		_sim.RefreshAiAccessibility();

		if (spawnedFromMap || isRandomMap)
		{
			// 地图自带出生点(skirmish 实体 / rmgen 基地):取景本地玩家首个所属实体
			// (其 CC,同 ColdLoad),而非沙盒固定角落。地图局走真实战争迷雾——
			// 此前的 SP 全图 reveal-all(沙盒时代遗留)让所有 sim 实体强制 Visible,
			// 穿透黑色地形雾(用户截图:黑雾中仍有树/建筑可见)。
			FocusCameraOnLocalPlayer();
			return;
		}

		// Fog: sandbox spawns owner-less world-dev entities with no seers, so reveal the map so
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

		// 开发者覆盖层(F8):回合/FPS/实体数/选中数/状态 hash(每 60 tick 算一次,太贵不每帧算)。
		if (_devOverlay?.Visible == true && _devLabel != null)
			UpdateDevOverlay();

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
		// _sim.Sim(SimSystem)在 MP 大厅阶段尚为 null(未开局),同理守卫。
		if (_sim?.Sim != null)
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
	{
		// 原版训练完成音位置化(训练建筑处)。
		var pos = _sim.Sim.QueryInterface<PositionComponent>(e.TrainerEntity);
		if (pos != null)
			AudioManager.PlayUnitEventAt(_sim.Templates, e.UnitTemplate, "trained",
				new Vector3(pos.Position.X.ToFloat(), pos.Position.Y.ToFloat(),
					pos.Position.Z.ToFloat()));
		else
			AudioManager.PlayUnitEvent(_sim.Templates, e.UnitTemplate, "trained");
	}

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
		{
			// 原版战斗音位置化(CSoundManager 世界事件):按攻击者位 3D 衰减。
			var pos = _sim.Sim.QueryInterface<PositionComponent>(e.Attacker);
			if (pos != null)
				AudioManager.PlayUnitEventAt(_sim.Templates, id.TemplateName,
					e.IsRanged ? "attack_ranged" : "attack_melee",
					new Vector3(pos.Position.X.ToFloat(), pos.Position.Y.ToFloat(),
						pos.Position.Z.ToFloat()));
			else
				AudioManager.PlayUnitEvent(_sim.Templates, id.TemplateName,
					e.IsRanged ? "attack_ranged" : "attack_melee");
		}
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
		ZeroAD.Sim.Diag.Log("Main", $"DEBUG_CAPTURE wrote {dir}/frame.png + entities.txt");

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
			ZeroAD.Sim.Diag.Log("SaveLoadTest", "round-trip OK");
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
		ZeroAD.Sim.Diag.Err("Main", $"OOS: {msg}\nState dumped:\n  {txt}\n  {bin}");
	}

	private void UpdateSelectionMarkers()
	{
		try
		{
			UpdateSelectionMarkersInner();
		}
		catch (System.Exception ex)
		{
			// 防护:选择圈重建中任何异常(如引用了已销毁的 node)不应冻结整个渲染循环。
			// 记录日志后继续,下一帧重试。
			ZeroAD.Sim.Diag.Err("Main", $"UpdateSelectionMarkers threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
		}
	}

	private void UpdateSelectionMarkersInner()
	{
		// 阵亡/销毁实体的选择圈残留:它们的 node 已 QueueFree,但 _selectionMarkers 仍存引用,
		// 下一帧对死对象调 QueueFree 会抛异常,中断整个选择圈重建 → 阵亡后所有选择框消失。
		// 先清 _selectionMarkers 里的死引用,再清 _selectedEntities 里 node 已不存在的实体
		// (阵亡/销毁),保证只对有活 node 的实体画圈。
		foreach (var m in _selectionMarkers)
			if (GodotObject.IsInstanceValid(m)) m.QueueFree();
		_selectionMarkers.Clear();

		// 阵亡/销毁的实体:从选中集合移除(node 不在 EntityNodes 里 = 已销毁),避免引用死节点。
		// 用临时集合避免遍历时改 _selectedEntities。
		if (_selectedEntities.Count > 0)
		{
			_toRemove.Clear();
			foreach (var eid in _selectedEntities)
				if (!_sim.EntityNodes.ContainsKey(eid) || !GodotObject.IsInstanceValid(_sim.EntityNodes[eid]))
					_toRemove.Add(eid);
			foreach (var eid in _toRemove)
				_selectedEntities.Remove(eid);
		}

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

		UpdateHoverMarker();
	}

	/// <summary>Hover 高亮(原版 selection.js onMouseMove:SetHighlight + SetStatusBars):
	/// 鼠标指向的实体显示白色微光圈 + 状态条——有 Health 画血条(动物/建筑),
	/// 否则有 ResourceSupply 画资源条(树木/矿石的剩余量,C++ 的 supply 条)。</summary>
	private Node3D? _hoverMarker;
	private EntityId _hoverEnt;

	private void UpdateHoverMarker()
	{
		var worldPos = ScreenToWorld(GetViewport().GetMousePosition());
		if (worldPos == null) return;
		var targets = _sim.GetEntitiesAtPosition(worldPos.Value, 3f);
		var ent = targets.Count > 0 ? targets[0] : default;

		if (ent == _hoverEnt) return;
		if (_hoverMarker != null && GodotObject.IsInstanceValid(_hoverMarker))
			_hoverMarker.QueueFree();
		if (_hoverExtra != null && GodotObject.IsInstanceValid(_hoverExtra))
			_hoverExtra.QueueFree();
		_hoverMarker = null;
		_hoverExtra = null;
		_hoverEnt = ent;
		if (ent == default) return;

		var node = _sim.EntityNodes.GetValueOrDefault(ent);
		var st = _sim.Gui.GetEntityState(ent);
		if (node == null || st == null) return;

		// 微光圈(gaia/敌方/己方都用原版高亮白 0.5 透明度,不区分敌我)
		var ring = SelectionRing.Create(1.6f, new Color(1f, 1f, 1f, 0.5f), new Color(1f, 1f, 1f, 0.5f));
		node.AddChild(ring);
		_hoverMarker = ring;

		// 状态条:血条(有 Health)或资源条(树木/矿)
		if (st.HealthMax > 0 || st.ResourceAmount > 0)
		{
			float frac = st.HealthMax > 0
				? st.HealthFraction
				: st.ResourceAmount / (float)System.Math.Max(1, MaxSupplyOf(ent));
			var bar = st.HealthMax > 0
				? SelectionRing.CreateHealthBar(frac)
				: SelectionRing.CreateCaptureBar(new List<(float, Color)> { (frac, new Color(0.2f, 0.75f, 0.25f)) });
			bar.Position = new Vector3(0, BarTopHeight(node), 0);
			node.AddChild(bar);
			_hoverExtra = bar;
		}
	}

	private Node3D? _hoverExtra;

	private int MaxSupplyOf(EntityId ent)
	{
		var supply = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.ResourceSupply>(ent);
		return supply?.MaxAmount ?? 1;
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
			// F:跟随选中单位(原版 camera.follow 热键)。
			if (key.Keycode == Key.F) FollowSelectedUnit();
			if (key.Keycode == Key.F5) QuickSave();
			if (key.Keycode == Key.F9) QuickLoad();
			// pause 热键(原版 MenuButtons.js:226 Pause hotkey):Pause/Break 键直接切暂停,
			// 不开菜单叠层(顶栏暂停按钮已移除,对齐上游;Menu 按钮仍可开 PauseMenu)。
			if (key.Keycode == Key.Pause) TogglePause();
			// F12:dump 选中实体的全部组件 + 关键字段(诊断用)。输出到控制台 + user://debug/
			// 的 entity_dump.txt。复用 ComponentManager.DumpEntity(TextDumpSerializer 同通路),
			// 一眼看出实体挂了哪些组件、字段值对不对——定位"为什么不显示/不攻击"类问题,
			// 免去临时加 [DIAG] 打印再删的循环。
			if (key.Keycode == Key.F12 && _selectedEntities.Count > 0)
				DumpSelectedEntity();
			// F6:dump 鼠标指向的实体(含敌方,不受 owner 过滤)——诊断敌方建筑材质/阵营色用。
			if (key.Keycode == Key.F6)
				DumpEntityAtCursor();
			// F11:诊断日志面板(tag 勾选静音 + 最近日志;诊断方案 3)。
			if (key.Keycode == Key.F11)
			{
				if (_diagPanel != null && _diagPanel.Visible) _diagPanel.Close();
				else _diagPanel?.Open();
			}
			// F8:开发者覆盖层(回合/FPS/实体数/选中数/状态 hash;诊断方案 4)。
			if (key.Keycode == Key.F8 && _devOverlay != null)
				_devOverlay.Visible = !_devOverlay.Visible;
			// F7:全开视野调试(观战/看敌方基地建筑不用冒死靠近)。切换本地玩家的 reveal-all。
			if (key.Keycode == Key.F7)
				ToggleRevealAll();
			// F10:在相机焦点刷一个本文明骑兵(调试骑手挂点/动画用——随机图开局不带骑兵,
			// 没有这个键就得训练半天才能看到骑手)。
			if (key.Keycode == Key.F10)
				DebugSpawnCavalry();
			// F3:自由飞行相机(free view;F9 被 QuickLoad 占用)——排查场景里不该有
			// 的东西(遮挡/漂浮/错位);默认按选项 dev.freecamera(持久化)。
			if (key.Keycode == Key.F3)
				ToggleFreeFly();
		}

		if (@event is InputEventMouseButton mb && mb.Pressed)
		{
			if (mb.ButtonIndex == MouseButton.Left)
			{
				if (_placeBuildingMode)
				{
					// 原版 input.js INPUT_BUILDING_CLICK:按下记录起点,松开时若未拖过阈值
					// 则按当前角度放置;拖过阈值则改为朝向光标(自由旋转)。
					// 墙模式:起点 = 拼链首塔锚点。
					_placeMouseDown = mb.Position;
					_placeAnchorWorld = ScreenToWorld(mb.Position);
					if (_wallDragMode && _placeAnchorWorld != null)
					{
						_wallStart = _placeAnchorWorld.Value;
						_wallDragging = false;
						_wallGhostSignature = "";
					}
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
				if (_wallDragMode)
				{
					// 墙模式:拖动 = 更新拼链预览(非旋转)。
					_wallDragging = true;
					UpdateWallPreview(cur.Value);
				}
				else
				{
					var anchor = _placeAnchorWorld.Value;
					// 原版 vector.js:413 atan2(dx, dz);Godot 与原版同 Y-up、angle 0 朝 +Z。
					_placeAngle = Mathf.Atan2(cur.Value.X - anchor.X, cur.Value.Z - anchor.Z);
				}
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
			if (_wallDragMode)
			{
				if (_wallDragging) PlaceWall(mbuPlace.Position);
				else PlaceWallSingleTower();   // 未拖动单点 = 单座塔楼(原版同)
				_wallDragging = false;
			}
			else
			{
				PlaceBuilding(mbuPlace.Position);
			}
		}
	}

	/// <summary>墙预览:按 起点→当前光标 重算拼链(WallPlacer),件序列签名不变不重建。</summary>
	private void UpdateWallPreview(Vector3 simPos)
	{
		if (_wallSet == null || _placeAnchorWorld == null) return;
		var pieces = WallPlacer.Compute(_wallSet,
			new Vector2(_wallStart.X, _wallStart.Z), new Vector2(simPos.X, simPos.Z));
        // 签名:件数 + 各件模板/坐标(0.5m 粒度),不变不重建(每 mouse-motion 都触发)。
        var sig = new System.Text.StringBuilder();
        foreach (var p in pieces)
            sig.Append(p.Template).Append('@').Append((int)(p.X * 2)).Append(',').Append((int)(p.Z * 2)).Append(';');
        if (sig.ToString() == _wallGhostSignature) return;
        _wallGhostSignature = sig.ToString();

        foreach (var g in _wallGhosts) g.QueueFree();
        _wallGhosts.Clear();
        Color color = SimBridge.GetPlayerColor((int)_sim.LocalPlayerId);
        foreach (var p in pieces)
        {
            var node = ModelLibrary.InstantiateForTemplate(p.Template, p.X, p.Z, color);
            if (node == null) continue;
            // ghost 挂 Main(非 _worldRoot),z 手动镜像,与单件 ghost 同套约定。
            node.Position = new Vector3(p.X, TerrainHeightService.Sample(p.X, p.Z),
                TerrainHeightService.MirrorZ(p.Z));
            node.Rotation = new Vector3(0, p.Angle, 0);
            SetGhostTransparency(node, 0.5f);
            AddChild(node);
            _wallGhosts.Add(node);
        }
	}

	/// <summary>松开下单:每个部件一条 Build 命令(首件强制,其余排队——建造者沿链施工)。
    /// 费用按件在执行端各自收取(与原版一致:每件是独立地基)。</summary>
	private void PlaceWall(Vector2 screenPos)
	{
		if (_wallSet == null || _placeAnchorWorld == null) { ExitBuildMode(); return; }
		var endPos = ScreenToWorld(screenPos);
		if (endPos == null) { ExitBuildMode(); return; }
		var pieces = WallPlacer.Compute(_wallSet,
			new Vector2(_wallStart.X, _wallStart.Z), new Vector2(endPos.Value.X, endPos.Value.Z));
		if (pieces.Count == 0) { ExitBuildMode(); return; }

		// 找首个己方建造者(与 PlaceBuilding 同款;建造队列经锁步命令,各端一致)。
		EntityId builder = default;
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null) { builder = eid; break; }
		if (builder.Equals(default)) { ExitBuildMode(); return; }

		foreach (var p in pieces)
			_sim.CommandBuild(builder, p.Template, p.X, p.Z, p.Angle);
		ExitBuildMode();
	}

	/// <summary>单点放置:单座塔楼(原版墙模式未拖动单击的语义)。</summary>
	private void PlaceWallSingleTower()
	{
		if (_wallSet == null || _placeAnchorWorld == null) { ExitBuildMode(); return; }
		EntityId builder = default;
		foreach (var eid in _selectedEntities)
			if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null) { builder = eid; break; }
		if (builder.Equals(default)) { ExitBuildMode(); return; }
		var a = _placeAnchorWorld.Value;
		_sim.CommandBuild(builder, _wallSet.Tower, a.X, a.Z, _placeAngle);
		ExitBuildMode();
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
		// RTS 约定:只选己方(同 HandleLeftClick/HandleDragSelect)。双击选同类不跨阵营。
		int owner = _sim.Sim.QueryInterface<OwnershipComponent>(hit)?.PlayerId ?? -1;
		if (owner != (int)_sim.LocalPlayerId) return;

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
		// RTS 约定:左键选中己方单位 或 gaia(树/石头/果子,owner≤0,选中显示资源信息+
		// 选择圈但不可操作)。敌方单位不能选中(只能右键指定目标)。
		foreach (var eid in entities)
		{
			var own = _sim.Sim.QueryInterface<OwnershipComponent>(eid);
			int ownerId = own?.PlayerId ?? 0;   // 无 Ownership = gaia
			if (ownerId == (int)_sim.LocalPlayerId || ownerId <= 0)
			{
				_selectedEntities.Add(eid);
				break;
			}
		}

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
			if (identity == null || !identity.IsUnit) continue;
			// RTS 约定:只选中己方单位(同 HandleLeftClick)。
			var own = _sim.Sim.QueryInterface<OwnershipComponent>(eid);
			if (own != null && own.PlayerId == (int)_sim.LocalPlayerId)
				_selectedEntities.Add(eid);
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
				// 集结点指令类型化(原版 input.js getActionInfo 同款分派):
				//   资源实体 → gather(带 resourceType);己方建筑地基/受损 → repair;
				//   可驻军己/盟建筑 → garrison;敌实体 → attack;空地面 → walk。
				// Shift = 追加到队列尾(原版 Shift+点击多点排队);无 Shift 重设单点。
				bool append = Input.IsPhysicalKeyPressed(Key.Shift);
				float wx = worldPos.Value.X, wz = worldPos.Value.Z;
				if (targetEntity.HasValue)
				{
					var tEnt = targetEntity.Value;
					var supply = _sim.Sim.QueryInterface<ResourceSupply>(tEnt);
					var tOwner = _sim.Sim.QueryInterface<OwnershipComponent>(tEnt);
					var tHealth = _sim.Sim.QueryInterface<HealthComponent>(tEnt);
					var tFoundation = _sim.Sim.QueryInterface<FoundationComponent>(tEnt);
					var tGarrison = _sim.Sim.QueryInterface<GarrisonHolderComponent>(tEnt);
					if (supply != null)
					{
						string resType = supply.SpecificType ?? "";
						_sim.CommandSetRallyPointFull(only, tEnt, wx, wz, "gather", resType, append);
						return;
					}
					bool hostile = tOwner != null && _sim.Sim.Players.IsEnemy((int)_sim.LocalPlayerId, tOwner.PlayerId);
					if (!hostile && (tFoundation != null || tHealth is { Current: > 0 } && tHealth.Current < tHealth.Max))
					{
						_sim.CommandSetRallyPointFull(only, tEnt, wx, wz, "repair", "", append);
						return;
					}
					if (!hostile && tGarrison != null)
					{
						_sim.CommandSetRallyPointFull(only, tEnt, wx, wz, "garrison", "", append);
						return;
					}
					if (hostile)
					{
						// 防御建筑(BuildingAI)+ 敌目标 → 手动集火(原版 Commands.js:
						// 建筑选中点敌 = focus fire,不走集结);其余建筑 → 集结点
						// attack 指令(出厂单位打它)。
						if (_sim.Sim.QueryInterface<BuildingAIComponent>(only) != null)
						{
							// F+右键 = focus-fire 排队命令(原版 unit_actions focus-fire
							// 热键修饰;Shift 追加队尾),无修饰 = 立即集火(两者并存,WS3 裁定)。
							if (Input.IsPhysicalKeyPressed(Key.F))
								_sim.CommandFocusFire(only, tEnt, queued: append);
							else
								_sim.CommandAttack(only, tEnt);
						}
						else
							_sim.CommandSetRallyPointFull(only, tEnt, wx, wz, "attack", "", append);
						return;
					}
				}
				_sim.CommandSetRallyPointFull(only, null, wx, wz, "walk", "", append);
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
			ZeroAD.Sim.Diag.Log("Main", $"Cannot afford {template}: needs {wood}W {stone}S {metal}M {food}F");
			return;
		}
		_placeBuildingMode = true;
		_buildTemplate = template;
		// 重置朝向为 GUI 默认 3π/4(原版 placement.js Reset→SetDefaultAngle)。
		_placeAngle = Mathf.Pi * 0.75f;
		// 墙组模板 → 拖放连段模式(无单件 ghost;按下锚定后才出预览)。
		var wstats = _sim.Templates?.ExtractStats(MapBuildTemplateName(template));
		if (wstats is { IsWallSet: true })
		{
			_wallSet = BuildWallSetData(wstats);
			_wallDragMode = _wallSet != null;
			if (!_wallDragMode) ZeroAD.Sim.Diag.Warn("Main", $"wallset {template} 部件数据缺失");
			return;
		}
		CreatePlaceGhost();
	}

	/// <summary>墙组模板 → 拼链数据(部件模板 + 各段链长 + 塔楼重叠度;含 {civ} 已解析)。</summary>
	private WallPlacer.WallSetData? BuildWallSetData(TemplateStats ws)
	{
		if (_sim.Templates == null) return null;
		float LenOf(string tmpl)
		{
			if (tmpl.Length == 0) return 0f;
			try { return _sim.Templates.ExtractStats(tmpl).WallPieceLength; }
			catch { return 0f; }
		}
		float tower = LenOf(ws.WallSetTower), lng = LenOf(ws.WallSetLong),
			med = LenOf(ws.WallSetMedium), sht = LenOf(ws.WallSetShort);
		if (tower <= 0f || lng <= 0f || med <= 0f || sht <= 0f) return null;
		return new WallPlacer.WallSetData(ws.WallSetTower, ws.WallSetGate, ws.WallSetLong,
			ws.WallSetMedium, ws.WallSetShort, tower, lng, med, sht,
			ws.WallSetMinTowerOverlap, ws.WallSetMaxTowerOverlap);
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
		_wallDragMode = false;
		_wallDragging = false;
		foreach (var g in _wallGhosts) g.QueueFree();
		_wallGhosts.Clear();
		_wallGhostSignature = "";
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

	/// <summary>Delete:销毁选中的己方实体(原版 delete-entities;归属在执行端再校验)。
	/// 不可删实体(英雄棺椁/须先猎杀/占领点未过半)按原版 execute 端过滤跳过。</summary>
	public void DeleteSelectedEntities()
	{
		foreach (var eid in _selectedEntities)
			if (IsOwn(eid) && GetUndeletableReason(eid) == null)
				_sim.CommandDelete(eid);
	}

	/// <summary>原版 unit_actions.js isUndeletable:返回不可删理由(null = 可删)。
	/// 三道门槛——须先猎杀的资源(动物)/占领点未过半/模板 Undeletable(英雄棺椁等)。
	/// controlsAll 作弊未移植,恒不豁免。</summary>
	public string? GetUndeletableReason(EntityId eid)
	{
		var supply = _sim.Sim.QueryInterface<ResourceSupply>(eid);
		if (supply != null && supply.KillBeforeGather)
			return "The entity has to be killed before it can be gathered from";
		var capturable = _sim.Sim.QueryInterface<CapturableComponent>(eid);
		if (capturable != null)
		{
			float cp = capturable.CapturePoints[(int)_sim.LocalPlayerId].ToFloat();
			float maxCp = capturable.MaxCapturePoints.ToFloat();
			if (maxCp > 0 && cp < maxCp / 2)
				return "You cannot destroy this entity as you own less than half the capture points";
		}
		if (_sim.Sim.QueryInterface<IdentityComponent>(eid) is { Undeletable: true })
			return "This entity is undeletable";
		return null;
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

	/// <summary>模板查看器(原版 page_viewer):按模板名打开完整信息面板;
	/// civ 缺省取本地玩家。</summary>
	public void OpenViewerPanel(string templateName, string civ = "")
	{
		if (_viewerPanel == null) return;
		if (civ.Length == 0) civ = _sim.GetPlayer()?.Civ ?? "athen";
		_viewerPanel.OpenFor(templateName, civ);
	}

	/// <summary>开发者覆盖层内容(F8):回合/FPS/实体数/选中数/状态 hash。hash 每 60 tick 算
	/// 一次(全状态 MD5 太贵,不每帧算)。</summary>
	private int _devOverlayTick;
	private string _devOverlayHash = "-";
	private void UpdateDevOverlay()
	{
		_devOverlayTick++;
		if (_devOverlayTick % 60 == 0)
		{
			try
			{
				var h = _sim.Sim.ComputeStateHash();
				_devOverlayHash = System.Convert.ToHexString(h)[..8].ToLowerInvariant();
			}
			catch { _devOverlayHash = "err"; }
		}
		int entityCount = 0;
		foreach (var _ in _sim.Sim.AllEntities) entityCount++;
		_devLabel!.Text =
			$"turn {_sim.NetTurn.CurrentTurn}  fps {Engine.GetFramesPerSecond():0}\n" +
			$"entities {entityCount}  selected {_selectedEntities.Count}\n" +
			$"hash {_devOverlayHash}  speed {_sim.SpeedMultiplier:0.##}x" +
			(_sim.Paused ? "  [PAUSED]" : "");
	}

	private bool _revealAll;

	/// <summary>F7:全开视野调试(观战/看敌方基地建筑不用冒死靠近)。切换本地玩家 reveal-all;
	/// 再按一次恢复真实迷雾。仅调试/观战用——不应对外暴露为作弊。</summary>
	private void ToggleRevealAll()
	{
		_revealAll = !_revealAll;
		_sim.Range.SetLosRevealAll((int)_sim.LocalPlayerId, _revealAll);
		ZeroAD.Sim.Diag.Log("Main", $"reveal-all {(_revealAll ? "ON" : "OFF")} (player {_sim.LocalPlayerId})");
	}

	/// <summary>F9 调试:自由飞行相机(free view)——排查场景里不该有的东西
	/// (遮挡/漂浮/错位)。默认按选项 dev.freecamera;再按 F9 或 RTS 操作切回。</summary>
	private void ToggleFreeFly()
	{
		bool on = !_camera.FreeFlyEnabled;
		_camera.FreeFlyEnabled = on;
		GetNode<UserConfig>("/root/UserConfig").SetUserValue("dev.freecamera", on ? "true" : "false");
		ZeroAD.Sim.Diag.Log("Dev", on
			? "Free camera ON: WASD 平移/QE 升降/Shift 加速/滚轮调速;F9 或 RTS 操作切回"
			: "Free camera OFF: 回 RTS 视角");
	}

	/// <summary>F10 调试:在相机焦点(sim 坐标)给本地玩家刷一个本文明骑兵剑士。</summary>
	private void DebugSpawnCavalry()
	{
		if (!_gameStarted || _camera?.Focus is not Vector3 focus) return;
		string civ = _sim.GetPlayer()?.Civ ?? "athen";
		var eid = _sim.SpawnFromTemplate($"units/{civ}/cavalry_swordsman_b", focus.X, focus.Z);
		_sim.AssignOwner(eid, (int)_sim.LocalPlayerId);
		ZeroAD.Sim.Diag.Log("Main", $"debug-spawn cavalry ({civ}) at ({focus.X:0.#},{focus.Z:0.#})");
	}

	/// <summary>F12:dump 选中(首个)实体的全部组件到控制台 + user://debug/entity_dump.txt。
	/// 复用 ComponentManager.DumpEntity(与 SerializeFullState 同一逐组件序列化通路)。
	/// 先打印几行关键组件摘要(AttackComponent/PositionComponent 等最常排查的),再写全量
	/// dump 文件。定位"为什么射程圈不显示/为什么送回房屋"类问题:选中实体按 F12 一眼看出
	/// 缺哪个组件、字段值对不对。</summary>
	/// <summary>F6:dump 鼠标指向的实体(含敌方,不受选中 owner 过滤)。诊断敌方建筑
	/// 材质/阵营色:F7 全开视野 → 鼠标移到雅典建筑 → F6 看 playerColor 对不对。</summary>
	private void DumpEntityAtCursor()
	{
		var vp = GetViewport();
		if (vp == null) return;
		var worldPos = ScreenToWorld(vp.GetMousePosition());
		if (worldPos == null) return;
		var entities = _sim.GetEntitiesAtPosition(worldPos.Value, 4f);
		if (entities.Count == 0) { ZeroAD.Sim.Diag.Log("Diag", "F6: no entity at cursor"); return; }
		var eid = entities[0];
		var ident = _sim.Sim.QueryInterface<IdentityComponent>(eid);
		var own = _sim.Sim.QueryInterface<OwnershipComponent>(eid);
		ZeroAD.Sim.Diag.Log("Diag", $"cursor entity {eid} tmpl={ident?.TemplateName ?? "?"} owner={own?.PlayerId.ToString() ?? "?"}");
		DumpEntityMaterials(eid);
	}

	private void DumpSelectedEntity()
	{
		// 选首个选中实体(多选时只 dump 一个;需要的话可循环)
		EntityId eid = _selectedEntities.First();
		var ident = _sim.Sim.QueryInterface<IdentityComponent>(eid);
		var pos = _sim.Sim.QueryInterface<PositionComponent>(eid);
		var atk = _sim.Sim.QueryInterface<AttackComponent>(eid);
		var ai = _sim.Sim.QueryInterface<UnitAIComponent>(eid);
		var own = _sim.Sim.QueryInterface<OwnershipComponent>(eid);
		ZeroAD.Sim.Diag.Log("Diag", $"entity {eid} tmpl={ident?.TemplateName ?? ident?.Name ?? "?"}");
		ZeroAD.Sim.Diag.Log("Diag", $"owner={own?.PlayerId.ToString() ?? "NULL"} pos={pos?.Position.ToString() ?? "NULL"}");
		ZeroAD.Sim.Diag.Log("Diag", $"attack={(atk != null ? $"OK range={atk.Range} rangeOverlay={atk.HasRangeOverlay}" : "NULL")}");
		ZeroAD.Sim.Diag.Log("Diag", $"fsm={ai?.FsmStateName ?? "no-UnitAI"}");
		// 全量 dump 到文件(逐组件 name=value,Fixed 以 hex 显示便于 diff)
		string dump = _sim.Sim.DumpEntity(eid);
		string dir = ProjectSettings.GlobalizePath("user://debug");
		System.IO.Directory.CreateDirectory(dir);
		string path = System.IO.Path.Combine(dir, "entity_dump.txt");
		System.IO.File.WriteAllText(path, $"turn={_sim.NetTurn.CurrentTurn} {dump}");
		ZeroAD.Sim.Diag.Log("Diag", $"full dump → {path}");
		DumpEntityMaterials(eid);
	}

	/// <summary>dump 实体的渲染材质状态(player color 诊断):每个 MeshInstance3D 的
	/// MaterialOverride 类型 + playerColor uniform 值。定位"阵营色缺失"类问题——
	/// P1 蓝 P2 不红时,看材质的 playerColor 是不是对的。</summary>
	private void DumpEntityMaterials(EntityId eid)
	{
		if (!_sim.EntityNodes.TryGetValue(eid, out var node) || node == null)
		{
			ZeroAD.Sim.Diag.Log("Diag", "materials: no node");
			return;
		}
		DumpMaterialsRecursive(node, 0);
		// 节点树总览:prop 是否挂载(雅典 CC 该有 7 个 prop 子树)。
		DumpNodeTree(node, 0);
	}

	private static void DumpNodeTree(Node node, int depth)
	{
		string indent = new string(' ', depth * 2);
		string kind = node is MeshInstance3D ? "MESH" : node is BoneAttachment3D ? "BONE" : "node";
		ZeroAD.Sim.Diag.Log("Diag", $"{indent}{kind} {node.Name} children={node.GetChildCount()}");
		foreach (var child in node.GetChildren())
			DumpNodeTree(child, depth + 1);
	}

	private static void DumpMaterialsRecursive(Node node, int depth)
	{
		if (node is MeshInstance3D mi)
		{
			// 读该 mesh 的 LayerContext meta(决定材质名),解析出实际材质名。
			string? matName = null;
			Node? cur = node;
			while (cur != null)
			{
				if (cur is Node3D n3 && n3.HasMeta("actorPath"))
				{
					var mp = n3.HasMeta("meshGlbPath") ? (string?)n3.GetMeta("meshGlbPath") : null;
					var info = ZeroAD.Godot.Actors.Composition.ActorLayerInfoCache.Get((string)n3.GetMeta("actorPath"), mp);
					matName = info.Material;
					break;
				}
				cur = cur.GetParent();
			}
			var mo = mi.MaterialOverride;
			string desc;
			if (mo is ShaderMaterial sm)
			{
				var pc = sm.GetShaderParameter("playerColor");
				desc = $"ShaderMaterial playerColor={pc}";
			}
			else if (mo is StandardMaterial3D std)
				desc = $"StandardMaterial albedo={std.AlbedoColor}";
			else
				desc = mo == null ? "no-override" : mo.GetType().Name;
			ZeroAD.Sim.Diag.Log("Diag", $"  mesh[{mi.Name}] material={matName ?? "?"}: {desc}");
		}
		foreach (var child in node.GetChildren())
			DumpMaterialsRecursive(child, depth + 1);
	}

	/// <summary>F5 快存 / 暂停菜单 Save。返回存档路径(null=失败),供暂停菜单回灌状态。</summary>
	private string? QuickSave()
	{
		var path = SaveGameManager.Save(_sim);
		if (path != null)
			ZeroAD.Sim.Diag.Log("QuickSave", $"saved to {path}");
		return path;
	}

	/// <summary>F9 快读 / 暂停菜单 Load。返回加载到的回合号(null=无存档或失败)。</summary>
	private uint? QuickLoad()
	{
		if (!SaveGameManager.Exists())
		{
			ZeroAD.Sim.Diag.Err("QuickLoad", "no save file found");
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
		ZeroAD.Sim.Diag.Log("QuickLoad", $"loaded turn {turn}, visuals rebuilt");
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
			ZeroAD.Sim.Diag.Log("Main", $"Cannot afford {_buildTemplate}: needs {wood}W {stone}S {metal}M {food}F");
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
			ZeroAD.Sim.Diag.Log("Main", $"Cannot place {_buildTemplate} at ({worldPos.Value.X:F1},{worldPos.Value.Z:F1}): {pr}");
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
