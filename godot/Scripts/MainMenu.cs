using System;
using System.IO;
using System.Linq;
using Godot;
using ZeroAD.Godot.Options;

namespace ZeroAD.Godot;

// MainMenu — 会话外主页(独立场景,project main_scene)。原版是 gui/page_pregame.xml 的独立页面,
// 这里贴原版"独立场景"模型:本场景做主菜单前置页 → 点 SP/Tutorial/MP 设 GameLaunchConfig →
// ChangeScene 到 session 场景(Main.tscn)→ Main._Ready 读 GameLaunchConfig 决定启动方式。
//
// ZEROAD_AUTOSTART/TUTORIAL 环境变量降级为 dev fallback:仅本页 _Ready 首次读取并**读取后清空**
// (修历史 bug——进程级 env 会在 ChangeScene 回主菜单时重触发,误以为还要自动开局)。
public sealed partial class MainMenu : Control
{
	private GameLaunchConfig _cfg = null!;

	public override void _Ready()
	{
		// 统一日志通道(诊断方案 3):尽早把内核 Diag.Sink 接到 Godot 输出,接住后续所有
		// startup/运行期日志。幂等——重进主菜单再 _Ready 也不重复挂。
		ZeroAD.Godot.Diagnostics.DiagGodot.Install();
		SetAnchorsPreset(LayoutPreset.FullRect);

		// dev 跳过主菜单:ZEROAD_TUTORIAL/AUTOSTART 读一次即清空,设 GameLaunchConfig 后转 session。
		if (TryConsumeAutostartEnv())
			return; // 已 CallDeferred 切场景,本帧不必构建菜单。

		_cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		// 已存设置全量重放(音量/全屏/垂直同步/GUI 缩放等即时生效项;场景相关项此处无 light/env
		// → no-op,进 session 后由 Main 再重放)。菜单上下文 inGame:false(adaptivefps 取 menu 值)。
		var userCfg = GetNode<UserConfig>("/root/UserConfig");
		OptionsApplier.ApplyAll(userCfg, GetTree(), inGame: false);
		// 音量滑杆改动即时生效(菜单音乐实时调)。具名方法 + _ExitTree 退订防悬垂。
		userCfg.ConfigChanged += OnUserConfigChangedAudio;
		BuildUi();
		MaybeRunScreenshotHook();
	}

	private void OnUserConfigChangedAudio(System.Collections.Generic.IReadOnlyList<string> keys)
	{
		if (keys.Any(k => k.StartsWith("sound.", StringComparison.Ordinal)))
			AudioManager.RefreshVolumes(this);
	}

	public override void _ExitTree()
	{
		GetNode<UserConfig>("/root/UserConfig").ConfigChanged -= OnUserConfigChangedAudio;
	}

	// dev 截图钩子:ZEROAD_SHOT=hotkeys/options 自动开对应面板,1.5s 后截屏存 user://shot_<名>.png 退出。
	private void MaybeRunScreenshotHook()
	{
		string shot = OS.GetEnvironment("ZEROAD_SHOT");
		if (string.IsNullOrEmpty(shot)) return;
		switch (shot)
		{
			case "hotkeys": OnHotkeys(); break;
			case "options": OnOptions(); break;
			case "mpclient":
				// dev:连 127.0.0.1:61195 的 client 大厅页(需本机已有 mphost 进程)。
				CallDeferred(nameof(StartMp), false);
				break;
			case "mphost":
				// dev:拉起 MP host 大厅页面截图(真实 ENet host,端口 61195)。
				// 必须延迟到 _Ready 之后(scene 切换中不能动子树);截图由 Main 场景接力。
				CallDeferred(nameof(StartMp), true);
				break;
			case "matches":
				OnSinglePlayer();
				// dev 配合:ZEROAD_MATCH_TAB=1/2 选 Player/Game Type 页签再截。
				if (int.TryParse(OS.GetEnvironment("ZEROAD_MATCH_TAB"), out int tabIdx))
					foreach (var node in GetChildren())
						if (node is MapPickerPanel tabPicker)
						{
							tabPicker.DevSelectTab(tabIdx);
							break;
						}
				// dev 配合:ZEROAD_MATCH_MAPTYPE=0/1/2 预选 Map Type 并展开地图浏览器再截。
				if (int.TryParse(OS.GetEnvironment("ZEROAD_MATCH_MAPTYPE"), out int mtIdx))
					foreach (var node in GetChildren())
						if (node is MapPickerPanel pickerNode)
						{
							pickerNode.DevShowMapType(mtIdx);
							break;
						}
				break;
			default: return;
		}
		ScreenshotAndQuit(shot);
	}

	private async void ScreenshotAndQuit(string name)
	{
		await ToSignal(GetTree().CreateTimer(1.5), SceneTreeTimer.SignalName.Timeout);
		var img = GetViewport().GetTexture().GetImage();
		img?.SavePng($"user://shot_{name}.png");
		ZeroAD.Sim.Diag.Log("Main", $"SHOT_SAVED user://shot_{name}.png");
		GetTree().Quit();
	}

	private bool TryConsumeAutostartEnv()
	{
		string tut = OS.GetEnvironment("ZEROAD_TUTORIAL");
		string auto = OS.GetEnvironment("ZEROAD_AUTOSTART");
		string map = OS.GetEnvironment("ZEROAD_MAP");
		if (string.IsNullOrEmpty(tut) && string.IsNullOrEmpty(auto) && string.IsNullOrEmpty(map))
			return false;

		// 清空:避免 Leave 回主菜单时 _Ready 再次读到,重触发自动开局。
		OS.SetEnvironment("ZEROAD_TUTORIAL", "");
		OS.SetEnvironment("ZEROAD_AUTOSTART", "");
		OS.SetEnvironment("ZEROAD_MAP", "");

		var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
		cfg.Reset();
		cfg.Mode = !string.IsNullOrEmpty(tut)
			? GameLaunchConfig.LaunchMode.Tutorial
			: GameLaunchConfig.LaunchMode.SinglePlayer;
		cfg.Seed = 42;
		// dev 选图钩子:ZEROAD_MAP=maps/skirmishes/acropolis_bay_2p.pmp（skirmish 图）
		// 或 random/mainland（随机图）或 maps/scenarios/x.pmp。单独设置即隐含 SP autostart。
		cfg.MapPath = map;

		CallDeferred(nameof(GotoSession));
		return true;
	}

	private void BuildUi()
	{
		// 原版 page_pregame 背景:启动随机一套多层视差图(gui/pregame/backgrounds 端口,
		// 见 PregameBackground);binaries 缺失时回退渐变底。
		string? binDir = FindBinariesDir();
		_binDir = binDir;
		// 音频:初始化 + 主菜单音乐列表(原版 music.js MENU:Honor_Bound 等 shuffle)。
		AudioManager.Init(this, binDir == null ? null : Path.Combine(binDir, "data", "mods", "public"));
		AudioManager.StartPlaylist("menu");
		var parallax = new PregameBackground();
		if (parallax.Init(binDir))
		{
			AddChild(parallax);
		}
		else
		{
			AddChild(new TextureRect
			{
				Texture = MakeBackgroundGradient(),
				AnchorsPreset = (int)LayoutPreset.FullRect,
				MouseFilter = Control.MouseFilterEnum.Ignore,
				ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			});
		}

		// 原版 submenu(menupanel.xml:60 0 300 0%,hidden):与主面板同位同宽,初始藏在
		// 主面板**下方**(先 AddChild → 被主面板盖住);点击顶层组后向右滑出至 300..540
		// (MainMenuItemHandler.onTick:left/right += offset,MenuSpeed 1.2px/ms ≈ 0.2s)。
		_submenuPanel = new Panel
		{
			Visible = false,
			AnchorLeft = 0f, AnchorRight = 0f, AnchorTop = 0f, AnchorBottom = 0f,
			OffsetLeft = 60, OffsetRight = 300,
		};
		var subBg = new StyleBoxFlat
		{
			BgColor = new Color(0.10f, 0.09f, 0.07f, 1.0f),
			BorderColor = new Color(0.90f, 0.75f, 0.31f),
			BorderWidthRight = 2,
		};
		_submenuPanel.AddThemeStyleboxOverride("panel", subBg);
		// 原版 submenuButtons(0 4 100%-4 100%-4)。
		_subVbox = new VBoxContainer
		{
			AnchorLeft = 0f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 1f,
			OffsetLeft = 0, OffsetRight = -4, OffsetTop = 4, OffsetBottom = -4,
		};
		_subVbox.AddThemeConstantOverride("separation", ButtonSep);
		_submenuPanel.AddChild(_subVbox);
		AddChild(_submenuPanel);

		// 对齐原版 pregame/menupanel.xml:主菜单是**左侧竖条面板**(size 60 -2 300 100%+2,
		// 宽 240 通高、上下各溢出 2px),非居中对话框。锚点布局,gui.scale 任意值位置不变。
		var panel = new Panel
		{
			AnchorLeft = 0f, AnchorRight = 0f, AnchorTop = 0f, AnchorBottom = 1f,
			OffsetLeft = 60, OffsetRight = 300, OffsetTop = -2, OffsetBottom = 2,
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		var bg = new StyleBoxFlat
		{
			BgColor = new Color(0.10f, 0.09f, 0.07f, 1.0f),
			// 右缘金边不由 StyleBox 画——见下方 MainMenuPanelRightBorderTop/Bottom 两段,
			// 子菜单展开时需在展开区间断开。
		};
		panel.AddThemeStyleboxOverride("panel", bg);
		AddChild(panel);

		// 原版 productLogo(ProjectInformation.xml:面板内 50%±110, y 10..110,
		// sprite 0ADLogo = pregame/shell/logo/0ad_logo.png)。缺失时回退文字标题。
		string logoPath = binDir == null ? "" : Path.Combine(binDir,
			"data", "mods", "public", "art", "textures", "ui", "pregame", "shell", "logo", "0ad_logo.png");
		var logoImg = binDir == null ? null : Image.LoadFromFile(logoPath);
		if (logoImg != null)
		{
			panel.AddChild(new TextureRect
			{
				Texture = ImageTexture.CreateFromImage(logoImg),
				ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
				StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
				AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0f, AnchorBottom = 0f,
				OffsetLeft = -110, OffsetRight = 110, OffsetTop = 10, OffsetBottom = 110,
				MouseFilter = Control.MouseFilterEnum.Ignore,
			});
		}
		else
		{
			var title = new Label
			{
				Text = "0 A.D.",
				HorizontalAlignment = HorizontalAlignment.Center,
				Theme = UITheme.GetTheme(),
				AnchorLeft = 0f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 0f,
				OffsetTop = 40, OffsetBottom = 110,
			};
			title.AddThemeFontSizeOverride("font_size", 34);
			panel.AddChild(title);
		}

		// 原版 mainMenuButtons(面板内 8 146 100%-8 346):按钮列起始于 y=146,左右留 8px。
		// 顶层按 MainMenuItems.js 分组:Learn to Play / Single-player / Multiplayer /
		// Settings / Quit(Structure Tree/Game Lobby/Editor/Credits 等未移植项跳过)。
		// 带子项的组点击后在按钮下方展开子面板(对齐原版 submenu 机制;滑出动画留 backlog)。
		var vbox = new VBoxContainer
		{
			AnchorLeft = 0f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 0f,
			OffsetLeft = 8, OffsetRight = -8, OffsetTop = ButtonTop0,
		};
		vbox.AddThemeConstantOverride("separation", ButtonSep);
		panel.AddChild(vbox);
		_mainVbox = vbox;

		var entries = new MenuEntry[]
		{
			new("Learn to Play", null, new MenuEntry[]
			{
				new("Manual", OnManual),
				new("Tutorial", OnTutorial),
				new("Structure Tree", OnStructree),
				new("Civilization Overview", OnCivInfo),
			}),
			new("Single-player", null, new MenuEntry[]
			{
				new("Matches", OnSinglePlayer),
				new("Load Game", OnLoadGame),
				new("Replays", OnReplay),
			}),
			new("Multiplayer", null, new MenuEntry[]
			{
				new("Host New Game", OnMpHost),
				new("Connect by IP", OnMpJoin),
				new("Lobby", OnLobby),
			}),
			new("Settings", null, new MenuEntry[]
			{
				new("Options", OnOptions),
				new("Hotkeys", OnHotkeys),
				// 原版 MainMenuItems.js:Settings 第三项即 Language(page_locale.xml)
				new("Language", OnLanguage),
			}),
			new("Quit", () => GetTree().Quit()),
		};
		_entries = entries;
		for (int i = 0; i < entries.Length; i++)
		{
			var entry = entries[i];
			int index = i;
			AddButton(vbox, entry.Caption, () => OnEntryPressed(entry, index));
		}

		// 原版 ProjectInformation 底部信息框(面板内 8 100%-368 100%-8 100%-94,
		// TranslucentPanelThinBorder + 白色 sans-14 描述)。community 按钮留 backlog。
		var infoBox = new PanelContainer
		{
			AnchorLeft = 0f, AnchorRight = 1f, AnchorTop = 1f, AnchorBottom = 1f,
			OffsetLeft = 8, OffsetRight = -8, OffsetTop = -368, OffsetBottom = -94,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		var infoBg = new StyleBoxFlat
		{
			BgColor = new Color(0f, 0f, 0f, 0.45f),
			BorderColor = new Color(1f, 1f, 1f, 0.25f),
			BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
		};
		infoBg.SetContentMarginAll(8);
		infoBox.AddThemeStyleboxOverride("panel", infoBg);
		panel.AddChild(infoBox);

		var infoLbl = new Label
		{
			Text = "0 A.D. Godot Rewrite\n\nNotice: This game is under development and many features have not been added yet.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		infoLbl.AddThemeFontSizeOverride("font_size", 14);
		infoLbl.AddThemeColorOverride("font_color", Colors.White);
		infoBox.AddChild(infoLbl);

		// 原版 MainMenuPanelRightBorderTop/Bottom:右缘 2px 金边(230 190 80)分两段,
		// 子菜单展开时在展开区间断开(Top 止于子菜单顶+Margin,Bottom 起于子菜单底)。
		_borderTop = MakeBorderStrip();
		panel.AddChild(_borderTop);
		_borderBottom = MakeBorderStrip();
		_borderBottom.Visible = false;
		panel.AddChild(_borderBottom);

		// 原版 MainMenuPanel 精灵的另外三条 2px 金边:顶(2 0 100%-2 2)、
		// 底(2 100%-2 100%-2 100%)、左(0 0 2 100%)——此前只有右缘,左缘金线缺失。
		var topStrip = new ColorRect
		{
			Color = GoldBorder,
			AnchorLeft = 0f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 0f,
			OffsetLeft = 2, OffsetRight = -2, OffsetTop = 0, OffsetBottom = 2,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		panel.AddChild(topStrip);
		var bottomStrip = new ColorRect
		{
			Color = GoldBorder,
			AnchorLeft = 0f, AnchorRight = 1f, AnchorTop = 1f, AnchorBottom = 1f,
			OffsetLeft = 2, OffsetRight = -2, OffsetTop = -2, OffsetBottom = 0,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		panel.AddChild(bottomStrip);
		var leftStrip = new ColorRect
		{
			Color = GoldBorder,
			AnchorLeft = 0f, AnchorRight = 0f, AnchorTop = 0f, AnchorBottom = 1f,
			OffsetLeft = 0, OffsetRight = 2, OffsetTop = 0, OffsetBottom = 0,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		panel.AddChild(leftStrip);
	}

	private static readonly Color GoldBorder = new(0.90f, 0.75f, 0.31f);

	private static ColorRect MakeBorderStrip() => new()
	{
		Color = GoldBorder,
		AnchorLeft = 1f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 1f,
		OffsetLeft = -2, OffsetRight = 0,
		MouseFilter = Control.MouseFilterEnum.Ignore,
	};

	// 原版 MainMenuItemHandler:ButtonHeight=28,Margin=4;mainMenuButtons 起于 y=146。
	private const int ButtonTop0 = 146, ButtonH = 28, ButtonSep = 4;

	private sealed record MenuEntry(string Caption, Action? OnPress, MenuEntry[]? Submenu = null);

	private Panel _submenuPanel = null!;
	private VBoxContainer _subVbox = null!;
	private VBoxContainer _mainVbox = null!;
	private ColorRect _borderTop = null!;
	private ColorRect _borderBottom = null!;
	private Tween? _slideTween;
	private MenuEntry? _openEntry;
	private MenuEntry[] _entries = System.Array.Empty<MenuEntry>();
	private string? _binDir;

	/// <summary>顶层按钮:无子项直接执行;有子项则子面板从主面板下方向右滑出(60..300 →
	/// 300..540,0.2s 线性,对齐 onTick 的 MenuSpeed 1.2px/ms),再点同一组收起(对齐 pressButton)。</summary>
	private void OnEntryPressed(MenuEntry entry, int index)
	{
		if (entry.Submenu == null || entry.Submenu.Length == 0)
		{
			CloseSubmenu();
			entry.OnPress?.Invoke();
			return;
		}
		if (_openEntry == entry)
		{
			CloseSubmenu();
			return;
		}
		_openEntry = entry;

		foreach (var child in _subVbox.GetChildren())
			child.QueueFree();
		foreach (var sub in entry.Submenu)
			AddButton(_subVbox, sub.Caption, () =>
			{
				CloseSubmenu();
				sub.OnPress?.Invoke();
			});

		// 竖向:顶 = 被点按钮顶 - Margin(4),高 = (28+4)×count(对齐 openSubmenu)。
		// 主面板顶缘全局 y=-2,vbox 起于面板内 146 → 被点按钮顶全局 = -2+146+index×32。
		float top = -2 + ButtonTop0 + index * (ButtonH + ButtonSep) - 4;
		_submenuPanel.OffsetTop = top;
		_submenuPanel.OffsetBottom = top + (ButtonH + ButtonSep) * entry.Submenu.Length;
		_submenuPanel.OffsetLeft = 60;
		_submenuPanel.OffsetRight = 300;
		_submenuPanel.Visible = true;

		_slideTween?.Kill();
		_slideTween = CreateTween().SetTrans(Tween.TransitionType.Linear);
		_slideTween.TweenProperty(_submenuPanel, "offset_left", 300f, 0.2);
		_slideTween.Parallel().TweenProperty(_submenuPanel, "offset_right", 540f, 0.2);

		// 右金边断开(面板局部坐标 = 全局 + 2):Top 止于子菜单顶+Margin,Bottom 起于子菜单底。
		_borderTop.AnchorBottom = 0f;
		_borderTop.OffsetBottom = top + 2 + 4;
		_borderBottom.AnchorTop = 0f;
		_borderBottom.OffsetTop = _submenuPanel.OffsetBottom + 2;
		_borderBottom.Visible = true;
	}

	private void CloseSubmenu()
	{
		_openEntry = null;
		_slideTween?.Kill();
		_submenuPanel.Visible = false;
		// 金边恢复通高(对齐 closeSubmenu 的 border 复位)。
		_borderTop.AnchorBottom = 1f;
		_borderTop.OffsetBottom = 0;
		_borderBottom.Visible = false;
	}

	/// <summary>binaries/ 目录定位(与 LoadingOverlay.FindBinariesDir 同款 ../、../../ 回退)。</summary>
	private static string? FindBinariesDir()
	{
		string projRoot = ProjectSettings.GlobalizePath("res://");
		foreach (var candidate in new[]
		{
			Path.GetFullPath(Path.Combine(projRoot, "..", "binaries")),
			Path.GetFullPath(Path.Combine(projRoot, "..", "..", "binaries")),
		})
		{
			if (Directory.Exists(candidate)) return candidate;
		}
		return null;
	}

	private void OnSinglePlayer()
	{
		// 选图面板(对齐原版 gamesetup 地图浏览器):SP 不再硬编码 arcadia——
		// random(MapRegistry)/skirmish/scenario 全目录可选,种子仅对 random 生效。
		string? dataRoot = _binDir == null ? null : Path.Combine(_binDir, "data", "mods", "public");
		var picker = new MapPickerPanel(MapCatalog.Scan(dataRoot), dataRoot);
		picker.OnStart += (map, seed, slots) =>
		{
			_cfg.Reset();
			_cfg.Mode = GameLaunchConfig.LaunchMode.SinglePlayer;
			_cfg.MapPath = map.RelPath;
			_cfg.Seed = seed;
			// "random" 文明在此解析成真文明(原版 pickRandomItems 的 GUI 侧抽签)——
			// sim/skirmish 替换永远见真文明代码,占位 general 兜底才不会指向
			// structures/random/* 这类不存在的模板(缺了这步 CC/起始单位会全部消失)。
			_cfg.Slots = CivRandom.Resolve(slots);
			picker.WriteOptions(_cfg);   // gamesetup 全部选项(尺寸/biome/资源/人口/速度/停战/胜利条件…)
			GotoSession();
		};
		picker.OnCancelled += () => picker.QueueFree();
		AddChild(picker);
	}
	private void OnTutorial() => Start(GameLaunchConfig.LaunchMode.Tutorial);

	// 原版 Multiplayer 子菜单:Host New Game / Connect by IP(gamesetup_mp 入口)。
	private void OnMpHost() => StartMp(host: true);
	private void OnMpJoin() => StartMp(host: false);

	private void OnLobby()
	{
		// 原版 Multiplayer → Lobby 经 prelobby 三页分流(entrance → login/register
		// → 大厅);此前直开大厅,跳过登录/注册分流。
		Lobby.PrelobbyPanel.OpenPage(Lobby.PrelobbyPanel.Page.Entrance);
	}

	private void StartMp(bool host)
	{
		_cfg.Reset();
		_cfg.Mode = GameLaunchConfig.LaunchMode.Multiplayer;
		_cfg.MpHost = host;
		GotoSession();
	}

	private void Start(GameLaunchConfig.LaunchMode mode)
	{
		_cfg.Reset();
		_cfg.Mode = mode;
		_cfg.Seed = 42;
		GotoSession();
	}

	private void GotoSession() => GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");

	private void OnManual()
	{
		var manual = new ManualPanel();
		AddChild(manual);
		manual.Open();
	}

	private void OnStructree()
	{
		var panel = new StructreePanel();
		AddChild(panel);
		panel.Open();
	}

	private void OnCivInfo()
	{
		var panel = new CivInfoPanel();
		AddChild(panel);
		panel.Open();
	}

	private void OnLoadGame()
	{
		var panel = new LoadGamePanel();
		AddChild(panel);
		panel.Open();
	}

	private void OnReplay()
	{
		var panel = new ReplayPanel();
		AddChild(panel);
		panel.Open();
	}

	private void OnOptions()
	{
		// 记录打开时 locale:面板关闭后若变了,重建主菜单让新语言立即全量生效
		// (面板开着时不能重建——会把面板本身也 QueueFree)。
		_localeAtOptionsOpen = Localization.CurrentLocale;
		var panel = new OptionsPanel();
		AddChild(panel);
		panel.TreeExited += OnOptionsClosed;
		panel.Open();
	}

	private string? _localeAtOptionsOpen;

	private void OnOptionsClosed()
	{
		if (_localeAtOptionsOpen == Localization.CurrentLocale) return;
		foreach (var child in GetChildren())
			child.QueueFree();
		CallDeferred(nameof(BuildUi));
	}

	private void OnHotkeys()
	{
		var panel = new HotkeysPanel();
		AddChild(panel);
		panel.Open();
	}

	/// <summary>Settings → Language(原版 page_locale.xml 入口)。面板关闭后若 locale
	/// 变了,重建主菜单全量应用(与 OnOptionsClosed 同机制)。</summary>
	private void OnLanguage()
	{
		_localeAtOptionsOpen = Localization.CurrentLocale;
		var panel = new LocalePanel();
		AddChild(panel);
		panel.TreeExited += OnOptionsClosed;
		panel.Open();
	}

	private void AddButton(Control parent, string label, Action onPressed,
		bool disabled = false, string tip = "")
	{
		var btn = new Button
		{
			// 菜单文本经 gettext 表翻译(msgid = 英文原文;locale=en 时 Tr 原样返回)
			Text = Localization.Tr(label),
			Theme = UITheme.GetTheme(),
			CustomMinimumSize = new Vector2(0, ButtonH),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			Disabled = disabled,
			TooltipText = tip,
		};
		// StoneButtonFancy 贴图样式(按钮高 28、白字描边 14,对齐 common/styles.xml)。
		StoneButtonStyle.Apply(btn, _binDir);
		btn.Pressed += () => AudioManager.PlayUi("ui_button_click");   // 原版 styles.xml sound_pressed
		btn.Pressed += onPressed;
		parent.AddChild(btn);
	}

	// 深色顶→更深底渐变,近似原版主菜单暗调。原版真实背景贴图留 backlog。
	private static GradientTexture2D MakeBackgroundGradient()
	{
		var grad = new Gradient();
		grad.SetColor(0, new Color(0.10f, 0.09f, 0.07f));
		grad.SetColor(1, new Color(0.03f, 0.03f, 0.02f));
		return new GradientTexture2D
		{
			Gradient = grad,
			FillTo = new Vector2(0, 1),
			Width = 2,
			Height = 256,
		};
	}
}
