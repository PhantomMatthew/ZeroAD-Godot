using Godot;
using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

public sealed partial class HUD : CanvasLayer
{
    private readonly SimBridge _sim;
    private readonly Main _main;

    private TextureRect _topBar = null!;
    private TextureButton _civEmblemBtn = null!;
    private readonly List<ResourceCounter> _resourceCounters = new();
    private Minimap _minimap = null!;
    private Panel _bottomBar = null!;

    private TextureRect _selIcon = null!;
    private Label _selName = null!;
    private ProgressBar _selHealth = null!;
    private Label _selHealthText = null!;
    private CaptureBar _selCapture = null!;
    private Label _selGarrison = null!;
    private HBoxContainer _garrisonRow = null!;
    private string _garrisonSignature = "";
    private Label _stanceLabel = null!;
    private HBoxContainer _stanceRow = null!;
    private HBoxContainer _formationRow = null!;
    private string _formationSignature = "";
    private Button _alertBtn = null!;
    private float _alertX, _alertZ, _alertElapsed;
    private double _alertFlash;
    private HBoxContainer _groupRow = null!;
    private string _groupSignature = "";
    private HBoxContainer _researchPanel = null!;
    private TextureRect _researchIcon = null!;
    private Label _researchLabel = null!;
    private ProgressBar _researchBar = null!;
    private string _researchTech = "";
    private readonly System.Collections.Generic.Dictionary<string, Button> _stanceButtons = new();
    private const int QueueSlotCount = 16;    // 队列条槽数(原版 unitQueuePanel repeat 16)
    private HBoxContainer _queueRow = null!;
    private readonly QueueSlot[] _queueSlots = new QueueSlot[QueueSlotCount];
    private HFlowContainer _commandBox = null!;

    private static readonly string[] _resNames = { "food", "wood", "stone", "metal" };
    private static readonly string[] _resIcons = { "resources/food.png", "resources/wood.png", "resources/stone.png", "resources/metal.png" };

    public HUD(SimBridge sim, Main main) { _sim = sim; _main = main; }

    public override void _Ready()
    {
        SetupTopBar();
        SetupBottomPanel();
        SetupToast();
    }

    private Label _toast = null!;
    private int _toastSeq;

    /// <summary>居中置顶的一行提示(原版红字错误提示的移植:建造拒绝等原因回显)。
    /// 3s 自动隐;连发时序号失效旧计时器,只保留最后一次。</summary>
    /// <summary>民族徽标(原版 CivIcon.rebuild):按本地玩家文明取
    /// session/portraits/emblems/emblem_{name}.png。</summary>
    private void RefreshCivEmblem()
    {
        string civ = _sim.GetPlayer()?.Civ ?? "athen";
        string emblemName = CivEmblemNames.GetValueOrDefault(civ, "hellenes");
        var tex = LoadTex($"session/portraits/emblems/emblem_{emblemName}.png");
        if (tex != null)
            _civEmblemBtn.TextureNormal = tex;
    }

    /// <summary>文明代码 → 徽标文件名(原版 civData.Emblem 的命名约定)。</summary>
    private static readonly Dictionary<string, string> CivEmblemNames = new(System.StringComparer.Ordinal)
    {
        ["athen"] = "athenians", ["spart"] = "spartans", ["gaul"] = "celts",
        ["brit"] = "britons", ["rome"] = "romans", ["cart"] = "carthaginians",
        ["kart"] = "carthaginians",
        ["ptol"] = "ptolemies", ["sele"] = "seleucids", ["kush"] = "kushites",
        ["maur"] = "mauryas", ["iber"] = "iberians", ["pers"] = "achaemenids",
        ["achae"] = "achaemenids", ["germ"] = "germ", ["han"] = "han",
        ["theb"] = "thebans", ["mace"] = "macedonians",
    };

    private void SetupToast()
    {
        _toast = new Label { Text = "", Visible = false };
        _toast.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _toast.OffsetTop = 60;
        _toast.OffsetLeft = -300;
        _toast.OffsetRight = 300;
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.AddThemeFontSizeOverride("font_size", 16);
        _toast.AddThemeColorOverride("font_color", new Color(1f, 0.45f, 0.35f));
        _toast.AddThemeColorOverride("font_outline_color", Colors.Black);
        _toast.AddThemeConstantOverride("outline_size", 4);
        AddChild(_toast);
    }

    public void ShowToast(string text)
    {
        _toast.Text = text;
        _toast.Visible = true;
        int seq = ++_toastSeq;
        GetTree().CreateTimer(3.0).Timeout += () =>
        {
            if (seq == _toastSeq) _toast.Visible = false;
        };
    }

    private void SetupTopBar()
    {
        _topBar = new TextureRect
        {
            Texture = LoadTex("ribbon_bg.png") ?? LoadTex("top_bar.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Tile,
        };
        _topBar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _topBar.OffsetBottom = 36;
        AddChild(_topBar);

        var hbox = new HBoxContainer();
        hbox.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        hbox.OffsetLeft = 4; hbox.OffsetTop = 0;
        hbox.OffsetBottom = 36;
        hbox.AddThemeConstantOverride("separation", 2);
        _topBar.AddChild(hbox);

        for (int i = 0; i < _resNames.Length; i++)
        {
            var counter = CreateResourceCounter(_resIcons[i]);
            _resourceCounters.Add(counter);
            hbox.AddChild(counter.Root);
        }

        var popCounter = CreateResourceCounter("resources/population.png");
        _resourceCounters.Add(popCounter);
        hbox.AddChild(popCounter.Root);

        // 研究进度条(原版 session_objects research progress):顶栏中部,
        // 任一己方建筑在研时显示 科技图标+名+进度条;完成/无在研隐藏。
        _researchPanel = new HBoxContainer();
        _researchPanel.AnchorLeft = 0.5f; _researchPanel.AnchorRight = 0.5f;
        _researchPanel.AnchorTop = 0f; _researchPanel.AnchorBottom = 0f;
        _researchPanel.OffsetLeft = -330; _researchPanel.OffsetRight = -60;
        _researchPanel.OffsetTop = 4; _researchPanel.OffsetBottom = 34;
        _researchPanel.AddThemeConstantOverride("separation", 6);
        _researchPanel.Visible = false;
        _researchIcon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(26, 26),
        };
        _researchPanel.AddChild(_researchIcon);
        _researchLabel = new Label { VerticalAlignment = VerticalAlignment.Center };
        _researchLabel.AddThemeFontSizeOverride("font_size", 12);
        _researchLabel.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.82f));
        _researchLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _researchLabel.AddThemeConstantOverride("outline_size", 2);
        _researchPanel.AddChild(_researchLabel);
        _researchBar = new ProgressBar
        {
            MinValue = 0, MaxValue = 100,
            CustomMinimumSize = new Vector2(90, 12),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            ShowPercentage = false,
        };
        _researchPanel.AddChild(_researchBar);
        _topBar.AddChild(_researchPanel);

        // 民族徽标钮(原版 top_panel/CivIcon.xml):顶栏正中的圆形文明徽标
        // (size 50%±48, y −26..70——跨栏下探),点击开科技树(structree)。
        _civEmblemBtn = new TextureButton
        {
            TooltipText = "View Structure Tree",
            StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
        };
        _civEmblemBtn.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _civEmblemBtn.OffsetLeft = -48; _civEmblemBtn.OffsetRight = 48;
        _civEmblemBtn.OffsetTop = -26; _civEmblemBtn.OffsetBottom = 70;
        _civEmblemBtn.GrowHorizontal = Control.GrowDirection.Both;
        _civEmblemBtn.Pressed += () => _main.OpenStructreePanel();
        _topBar.AddChild(_civEmblemBtn);
        RefreshCivEmblem();

        // 对齐 C++ TopPanel 右侧(top_panel/MenuButton.xml + IconButtons/*):
        // 从左到右 GameSpeed(100%−284) / Diplomacy / Trade / MatchSettings(28×28 图标,
        // 间距 2) / **Menu 在最右**(100%−164..100%-8,156×28 文字按钮,StoneButtonFancy)。
        // 原版顶栏无暂停按钮——暂停是 Menu 下拉里的项(PauseMenu 的 Resume)+ pause 热键;
        // 此前在此塞了个 ❚❚ 按钮并把盒宽从 276 拓到 322,与上游布局不符,已移除。
        var menuBox = new HBoxContainer();
        menuBox.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        menuBox.OffsetLeft = -284; menuBox.OffsetTop = 4;
        menuBox.OffsetRight = -8; menuBox.OffsetBottom = 32;
        menuBox.AddThemeConstantOverride("separation", 2);
        _topBar.AddChild(menuBox);

        AddMenuButton(menuBox, "time_small", "Game Speed", () => ToggleGameSpeedPopover());
        AddMenuButton(menuBox, "diplomacy", "Diplomacy", () => _main.OpenDiplomacyPanel());
        AddMenuButton(menuBox, "economics", "Trade", () => _main.OpenTradePanel());
        AddMenuButton(menuBox, "match-settings", "Settings", () => _main.OpenMatchSettingsPanel());

        // 速度控制弹出条(原版 GameSpeedControl.xml:gameSpeed 下拉位于顶栏下方
        // 100%-390 40 100%-230 65,由时间按钮开合,默认隐藏)。顶栏不内联 +/-——
        // 步进键与档位下拉都收进这个弹出条。
        BuildGameSpeedPopover();

        var menuBtn = new Button
        {
            Text = "Menu",
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(156, 28),
            TooltipText = "Menu",
        };
        StoneButtonStyle.Apply(menuBtn, FindBinariesDir());
        menuBtn.Pressed += () => _main.OpenPauseMenu();
        menuBox.AddChild(menuBtn);
    }

    // ── 游戏速度控制(原版 GameSpeedControl:顶栏时间按钮 → 开合下方控制条)──

    private PanelContainer _speedPopover = null!;
    private Label _speedLabel = null!;
    private OptionButton _speedOptions = null!;
    private bool _speedSyncing;

    /// <summary>速度弹出条开着?(Main 失焦自动暂停豁免用:弹出条的下拉 Popup 会抢焦,
    /// 与模态面板同款问题)。</summary>
    public bool GameSpeedPopoverOpen => _speedPopover.Visible;

    // 原版 GameSpeedControl 的 9 档(与 Main.AdjustGameSpeed 的步进表同集)。
    private static readonly (double rate, string label)[] SpeedSteps =
    {
        (0.5, "0.5×"), (0.75, "0.75×"), (1.0, "Normal"), (1.25, "1.25×"),
        (1.5, "1.5×"), (2.0, "2×"), (5.0, "Fast (5×)"),
        (10.0, "Very Fast (10×)"), (20.0, "Extremely Fast (20×)"),
    };

    /// <summary>速度控制条(原版 gameSpeed 下拉位于顶栏正下方:100%-390 40 100%-230 65,
    /// 默认隐藏,时间按钮开合)。内容 = 步进排(− 当前倍率 +)+ 9 档下拉,双向同步。</summary>
    private void BuildGameSpeedPopover()
    {
        _speedPopover = new PanelContainer { Visible = false, Theme = UITheme.GetTheme() };
        _speedPopover.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _speedPopover.OffsetLeft = -390; _speedPopover.OffsetTop = 40;
        _speedPopover.OffsetRight = -170; _speedPopover.OffsetBottom = 100;
        AddChild(_speedPopover);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);
        _speedPopover.AddChild(vbox);

        var row = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        row.AddThemeConstantOverride("separation", 4);
        vbox.AddChild(row);

        var slowerBtn = new Button
        {
            Text = "−",
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(30, 26),
            TooltipText = "Slower",
        };
        StoneButtonStyle.Apply(slowerBtn, FindBinariesDir());
        slowerBtn.Pressed += () => { _main.AdjustGameSpeed(-1); SyncSpeedControls(); };
        row.AddChild(slowerBtn);

        _speedLabel = new Label
        {
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(72, 26),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.AddChild(_speedLabel);

        var fasterBtn = new Button
        {
            Text = "+",
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(30, 26),
            TooltipText = "Faster",
        };
        StoneButtonStyle.Apply(fasterBtn, FindBinariesDir());
        fasterBtn.Pressed += () => { _main.AdjustGameSpeed(+1); SyncSpeedControls(); };
        row.AddChild(fasterBtn);

        _speedOptions = new OptionButton
        {
            Theme = UITheme.GetTheme(),
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            TooltipText = "Choose game speed",
        };
        foreach (var (_, label) in SpeedSteps)
            _speedOptions.AddItem(label);
        _speedOptions.ItemSelected += (idx) =>
        {
            if (_speedSyncing || idx < 0 || idx >= SpeedSteps.Length) return;
            _sim.SpeedMultiplier = SpeedSteps[(int)idx].rate;
            SyncSpeedControls();
        };
        vbox.AddChild(_speedOptions);

        SyncSpeedControls();
    }

    private void ToggleGameSpeedPopover()
    {
        _speedPopover.Visible = !_speedPopover.Visible;
        if (_speedPopover.Visible) SyncSpeedControls();
    }

    /// <summary>当前倍率 → 步进排文本 + 下拉选中项(取最近档,原版 rebuild 同逻辑)。</summary>
    private void SyncSpeedControls()
    {
        double cur = _sim.SpeedMultiplier;
        _speedLabel.Text = $"{cur:0.##}×";
        int best = 2;   // 默认 Normal
        double bestDiff = double.MaxValue;
        for (int i = 0; i < SpeedSteps.Length; i++)
        {
            double d = System.Math.Abs(SpeedSteps[i].rate - cur);
            if (d < bestDiff) { bestDiff = d; best = i; }
        }
        _speedSyncing = true;
        _speedOptions.Selected = best;
        _speedSyncing = false;
    }

    /// <summary>binaries/ 目录定位(与 MainMenu.FindBinariesDir 同款 ../、../../ 回退)。</summary>
    private static string? FindBinariesDir()
    {
        string projRoot = ProjectSettings.GlobalizePath("res://");
        foreach (var candidate in new[]
        {
            System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "binaries")),
            System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, "..", "..", "binaries")),
        })
        {
            if (System.IO.Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private void AddMenuButton(HBoxContainer parent, string icon, string tooltip, System.Action onPressed)
    {
        var btn = new Button
        {
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(28, 28),
            TooltipText = tooltip,
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            VerticalIconAlignment = VerticalAlignment.Center,
        };
        var tex = LoadIcon(icon);
        if (tex != null) btn.Icon = tex;
        btn.Pressed += onPressed;
        parent.AddChild(btn);
    }

    private ResourceCounter CreateResourceCounter(string iconPath)
    {
        var root = new Control { CustomMinimumSize = new Vector2(73, 36) };

        var icon = new TextureRect
        {
            Texture = LoadTex(iconPath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            CustomMinimumSize = new Vector2(36, 36),
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        icon.OffsetLeft = 2; icon.OffsetTop = 0;
        icon.OffsetRight = 38; icon.OffsetBottom = 36;
        root.AddChild(icon);

        var count = new Label
        {
            Text = "0",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        count.OffsetLeft = 33; count.OffsetTop = 2;
        count.OffsetRight = 73; count.OffsetBottom = 22;
        count.AddThemeFontSizeOverride("font_size", 14);
        count.AddThemeColorOverride("font_color", Colors.White);
        count.AddThemeColorOverride("font_outline_color", Colors.Black);
        count.AddThemeConstantOverride("outline_size", 3);
        root.AddChild(count);

        var stats = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        stats.OffsetLeft = 33; stats.OffsetTop = 18;
        stats.OffsetRight = 73; stats.OffsetBottom = 36;
        stats.AddThemeFontSizeOverride("font_size", 11);
        stats.AddThemeColorOverride("font_color", new Color(0.78f, 0.78f, 0.78f));
        stats.AddThemeColorOverride("font_outline_color", Colors.Black);
        stats.AddThemeConstantOverride("outline_size", 2);
        root.AddChild(stats);

        return new ResourceCounter(root, count, stats);
    }

    private void SetupBottomPanel()
    {
        // 原版 session.xml 底栏:总宽 1024 整体居中(50%±512),四个区固定坐标——
        // minimap(0,0)200×204 / supplemental(196,38)206×166 / selection(398,0)228×204 /
        // commands(622,31)402×173(短区底部对齐)。此前 BottomWide 全宽展开,与 C++ 不符。
        _bottomBar = new Panel();
        _bottomBar.AnchorLeft = 0.5f; _bottomBar.AnchorRight = 0.5f;
        _bottomBar.AnchorTop = 1f; _bottomBar.AnchorBottom = 1f;
        _bottomBar.OffsetLeft = -512; _bottomBar.OffsetRight = 512;
        _bottomBar.OffsetTop = -204; _bottomBar.OffsetBottom = 0;
        // 透明底(各区自带贴图+边框,原版同)。
        _bottomBar.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) });

        SetupMinimapZone(_bottomBar, new Vector2(0, 0), new Vector2(200, 204));
        SetupSupplementalZone(_bottomBar, new Vector2(196, 204 - 166), new Vector2(206, 166));
        SetupSelectionZone(_bottomBar, new Vector2(398, 0), new Vector2(228, 204));
        SetupCommandZone(_bottomBar, new Vector2(622, 204 - 173), new Vector2(402, 173));
        // 队列条(原版 unitQueuePanel:size="4 -56 100% 0"——第四面板上方 56px 横条,
        // 上缘探出底栏顶):生产图标+剩余时间 + 16 槽(40×40 带进度遮罩)。
        SetupQueueStrip(_bottomBar, new Vector2(622 + 4, 204 - 173 - 56), new Vector2(398, 56));

        AddChild(_bottomBar);
    }

    private Control _queueStrip = null!;
    private Label _queueTime = null!;

    private void SetupQueueStrip(Control parent, Vector2 pos, Vector2 size)
    {
        _queueStrip = new Control { Position = pos, Size = size, Visible = false };

        // 生产图标(左,52×54)+ 剩余时间文本(原版 queueTimeRemaining)。
        var prodIcon = new TextureRect
        {
            Texture = LoadIcon("production"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Position = new Vector2(-4, 0),
            Size = new Vector2(52, 54),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _queueStrip.AddChild(prodIcon);
        _queueTime = new Label
        {
            Text = "",
            Position = new Vector2(-4, 36),
            Size = new Vector2(52, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _queueTime.AddThemeFontSizeOverride("font_size", 11);
        _queueTime.AddThemeColorOverride("font_color", Colors.White);
        _queueTime.AddThemeColorOverride("font_outline_color", Colors.Black);
        _queueTime.AddThemeConstantOverride("outline_size", 2);
        _queueStrip.AddChild(_queueTime);

        // 16 槽(原版 repeat 16,40×40;点击取消=全额退款)。
        _queueRow = new HBoxContainer { Position = new Vector2(52, 6) };
        _queueRow.AddThemeConstantOverride("separation", 2);
        for (int i = 0; i < _queueSlots.Length; i++)
        {
            _queueSlots[i] = new QueueSlot
            {
                CustomMinimumSize = new Vector2(40, 40),
                Visible = false,
                SlotIndex = i,
                TooltipText = "Click to cancel (full refund)",
            };
            _queueSlots[i].Clicked += idx => _main.CancelProductionAt(idx);
            _queueRow.AddChild(_queueSlots[i]);
        }
        _queueStrip.AddChild(_queueRow);

        parent.AddChild(_queueStrip);
    }

    /// <summary>hud_panels.png 的区底图(原版 sprite 的 real_texture_placement 裁剪)。
    /// inset=4(sprite size="4 4 100%-4 100%-4" 语义:边框内缩 4px)。</summary>
    private static void AddPanelBackground(Control zone, Rect2 region)
    {
        var hudPanels = LoadTex("hud_panels.png");
        if (hudPanels == null) return;
        var bg = new TextureRect
        {
            Texture = new AtlasTexture { Atlas = hudPanels, Region = region },
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        bg.OffsetLeft = 4; bg.OffsetTop = 4; bg.OffsetRight = -4; bg.OffsetBottom = -4;
        zone.AddChild(bg);
    }

    /// <summary>Wraps a zone Control with a C++-style border frame: 4 edge lines
    /// (line_horiz/line_vert) + 4 corner pieces, drawn as children of the zone.
    /// Mirrors the C++ sprites.xml pattern used by supplementalDetailsPanel and
    /// unitCommandsPanel.</summary>
    private static void AddBorderFrame(Control zone)
    {
        var horiz = LoadTex("session/line_horiz.png");
        var vert = LoadTex("session/line_vert.png");
        var ctl = LoadTex("session/corner_tl.png");
        var ctr = LoadTex("session/corner_tr.png");
        var cbl = LoadTex("session/corner_bl.png");
        var cbr = LoadTex("session/corner_br.png");
        const int bw = 4; // border width (matches texture_size in C++ sprites)

        if (horiz != null)
        {
            var top = new TextureRect { Texture = horiz, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Tile };
            top.SetAnchorsPreset(Control.LayoutPreset.TopWide); top.OffsetBottom = bw;
            top.MouseFilter = Control.MouseFilterEnum.Ignore; zone.AddChild(top);

            var bot = new TextureRect { Texture = horiz, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Tile };
            bot.SetAnchorsPreset(Control.LayoutPreset.BottomWide); bot.OffsetTop = -bw;
            bot.MouseFilter = Control.MouseFilterEnum.Ignore; zone.AddChild(bot);
        }
        if (vert != null)
        {
            var left = new TextureRect { Texture = vert, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Tile };
            left.SetAnchorsPreset(Control.LayoutPreset.LeftWide); left.OffsetRight = bw;
            left.MouseFilter = Control.MouseFilterEnum.Ignore; zone.AddChild(left);

            var right = new TextureRect { Texture = vert, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Tile };
            right.SetAnchorsPreset(Control.LayoutPreset.RightWide); right.OffsetLeft = -bw;
            right.MouseFilter = Control.MouseFilterEnum.Ignore; zone.AddChild(right);
        }
        void AddCorner(Texture2D? tex, float left, float top)
        {
            if (tex == null) return;
            var c = new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize };
            c.OffsetLeft = left; c.OffsetTop = top; c.OffsetRight = left + bw; c.OffsetBottom = top + bw;
            c.MouseFilter = Control.MouseFilterEnum.Ignore; zone.AddChild(c);
        }
        AddCorner(ctl, 0, 0);
        AddCorner(ctr, -bw, 0);
        AddCorner(cbl, 0, -bw);
        AddCorner(cbr, -bw, -bw);
    }

    private void SetupMinimapZone(Control parent, Vector2 pos, Vector2 size)
    {
        var frame = new Control { Position = pos, Size = size };
        AddBorderFrame(frame);
        var ring = new TextureRect
        {
            Texture = LoadTex("minimap_circle_modern.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        ring.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        ring.MouseFilter = Control.MouseFilterEnum.Ignore;
        frame.AddChild(ring);

        _minimap = new Minimap(_sim, _main)
        {
            Position = new Vector2(0, 0),
            Size = new Vector2(200, 200),
        };
        frame.AddChild(_minimap);

        // 空闲村民按钮(原版 MiniMapIdleWorkerButton:小地图区角落;点击循环聚焦
        // 下一个空闲采集者并选中)。
        var idleBtn = new Button
        {
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(30, 30),
            TooltipText = "Find idle worker",
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            VerticalIconAlignment = VerticalAlignment.Center,
        };
        var idleTex = LoadIcon("back-to-work");
        if (idleTex != null) idleBtn.Icon = idleTex;
        idleBtn.Pressed += () => _main.CycleIdleWorker();
        idleBtn.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        frame.AddChild(idleBtn);

        parent.AddChild(frame);
    }

    /// <summary>Supplemental panel (C++ "selection_panels_left"): stance buttons,
    /// garrison count, and formation placeholder.</summary>
    private void SetupSupplementalZone(Control parent, Vector2 pos, Vector2 size)
    {
        var panel = new Control { Position = pos, Size = size };
        // 原版 supplementalDetailsPanel 底图:hud_panels.png 裁 (314,98)-(512,256)=198×158
        AddPanelBackground(panel, new Rect2(314, 98, 198, 158));
        AddBorderFrame(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);   // 此前缺失:右/下负偏移在无锚点时矩形为负
        vbox.OffsetLeft = 8; vbox.OffsetTop = 8;
        vbox.OffsetRight = -8; vbox.OffsetBottom = -8;
        panel.AddChild(vbox);

        // 编队组图标条(原版 PanelEntityManager 的紧凑版):已编入的组 0-9 小图标,
        // 数字+成员数;点击选中该组(与 Ctrl+数字编入/数字选中热键同路)。
        _groupRow = new HBoxContainer();
        _groupRow.AddThemeConstantOverride("separation", 2);
        vbox.AddChild(_groupRow);

        // Stance row: 5 stance icons (violent/aggressive/defensive/passive/standground).
        // 接 sim:点击经 NetCommand.SetUnitStance 改全部选中己方单位;当前站姿高亮。
        _stanceLabel = new Label { Text = "Stance" };
        _stanceLabel.AddThemeFontSizeOverride("font_size", 11);
        _stanceLabel.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.82f));
        _stanceLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _stanceLabel.AddThemeConstantOverride("outline_size", 2);
        vbox.AddChild(_stanceLabel);

        _stanceRow = new HBoxContainer();
        _stanceRow.AddThemeConstantOverride("separation", 2);
        vbox.AddChild(_stanceRow);

        // 阵型行(原版 formation_panel):选中 ≥2 同主可编队单位时按模板
        // UnitAI/Formations 显示可用阵型;选中编队控制器时只显 null(解散)。
        // 点击经 NetCommand.FormationCmd 锁步创建/解散。
        _formationRow = new HBoxContainer();
        _formationRow.AddThemeConstantOverride("separation", 2);
        _formationRow.Visible = false;
        vbox.AddChild(_formationRow);
        foreach (var stance in UnitAIComponent.SelectableStances)
        {
            var btn = new Button
            {
                Theme = UITheme.GetTheme(),
                CustomMinimumSize = new Vector2(28, 28),
                TooltipText = stance,
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                VerticalIconAlignment = VerticalAlignment.Center,
            };
            ApplySessionIconButtonStyle(btn);
            var tex = LoadIcon($"stances/{stance}");
            if (tex != null) btn.Icon = tex;
            string captured = stance;
            btn.Pressed += () =>
            {
                _main.SetSelectedUnitStance(captured);
                RefreshStanceHighlight();
            };
            _stanceButtons[stance] = btn;
            _stanceRow.AddChild(btn);
        }

        // Garrison indicator (count label) + occupant portrait row (click = unload one,
        // trailing button = unload all). Mirrors the original garrison selection panel.
        _selGarrison = new Label { Text = "" };
        _selGarrison.AddThemeFontSizeOverride("font_size", 12);
        _selGarrison.AddThemeColorOverride("font_color", new Color(0.85f, 0.80f, 0.65f));
        _selGarrison.AddThemeColorOverride("font_outline_color", Colors.Black);
        _selGarrison.AddThemeConstantOverride("outline_size", 2);
        vbox.AddChild(_selGarrison);

        _garrisonRow = new HBoxContainer();
        _garrisonRow.AddThemeConstantOverride("separation", 2);
        _garrisonRow.Visible = false;
        vbox.AddChild(_garrisonRow);

        // 遇袭警报按钮(原版 alert_panel v1):己方实体被命中后闪现(10s 有效),
        // 点击跳相机到被袭位置并清除。
        _alertBtn = new Button
        {
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(30, 30),
            TooltipText = "Under attack! Click to jump",
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            VerticalIconAlignment = VerticalAlignment.Center,
            Visible = false,
        };
        ApplySessionIconButtonStyle(_alertBtn);
        var alertTex = LoadIcon("die");
        if (alertTex != null) _alertBtn.Icon = alertTex;
        _alertBtn.Modulate = new Color(1f, 0.35f, 0.3f);
        _alertBtn.Pressed += () =>
        {
            _main.FocusWorldPosition(_alertX, _alertZ);
            _alertBtn.Visible = false;
        };
        vbox.AddChild(_alertBtn);

        parent.AddChild(panel);
    }

    private void SetupSelectionZone(Control parent, Vector2 pos, Vector2 size)
    {
        var panel = new Control { Position = pos, Size = size };

        // C++ selectionDetailsPanel uses session/hud_panels.png with
        // real_texture_placement="0 0 220 192" — only the top-left 220×192 region
        // of the 512×256 texture. AtlasTexture crops to that region so the panel
        // shows the same carved-stone background as the original.
        var hudPanels = LoadTex("hud_panels.png");
        if (hudPanels != null)
        {
            var atlas = new AtlasTexture
            {
                Atlas = hudPanels,
                Region = new Rect2(0, 0, 220, 192),
            };
            var bg = new TextureRect
            {
                Texture = atlas,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
            };
            bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            bg.MouseFilter = Control.MouseFilterEnum.Ignore;
            panel.AddChild(bg);
        }
        AddBorderFrame(panel);

        // ── 单选区(原版 detailsAreaSingle:4 4 100%-4 100%-44;底 40px 让位命令条)──
        _singleArea = new Control
        {
            Position = new Vector2(4, 4),
            Size = new Vector2(size.X - 8, size.Y - 48),
        };
        panel.AddChild(_singleArea);

        // 大头像 96×96(左上,原版 iconBorder 框)。
        _selIcon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Position = new Vector2(0, 0),
            Size = new Vector2(96, 96),
        };
        _singleArea.AddChild(_selIcon);

        // 军衔图标(原版 rankIcon,头像左上 4,4 20×20;无军衔隐藏)。
        _rankIcon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Position = new Vector2(4, 4),
            Size = new Vector2(20, 20),
            Visible = false,
        };
        _singleArea.AddChild(_rankIcon);

        // 经验竖条(原版 experience:头像左边 2,2 宽 6;仅可晋升单位)。
        _xpBar = new ProgressBar
        {
            MinValue = 0, MaxValue = 100,
            Position = new Vector2(2, 2),
            Size = new Vector2(6, 92),
            FillMode = (int)ProgressBar.FillModeEnum.BottomToTop,
            ShowPercentage = false,
            Visible = false,
        };
        _xpBar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0.1f, 0.1f, 0.1f, 0.8f) });
        _xpBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color(0.2f, 0.5f, 0.9f) });
        _singleArea.AddChild(_xpBar);

        // 右侧栏(x100 起,宽 116):capture/health/resource 三段(文本行 14px + 条 7px)。
        const float bx = 100, bw = 116;
        _captureLabel = MakeStatLabel("Capture", false, bx, 2, bw);
        _captureStats = MakeStatLabel("", true, bx, 2, bw);
        _selCapture = new CaptureBar
        {
            Position = new Vector2(bx, 16),
            Size = new Vector2(bw, 7),
            Visible = false,
            TooltipText = "",
        };
        _singleArea.AddChild(_selCapture);

        _selHealthText = MakeStatLabel("", true, bx, 26, bw);
        _selHealth = new ProgressBar
        {
            MinValue = 0, MaxValue = 100, Value = 100,
            Position = new Vector2(bx, 40),
            Size = new Vector2(bw, 7),
            ShowPercentage = false,
        };
        _selHealth.AddThemeStyleboxOverride("background", new StyleBoxFlat
        {
            BgColor = new Color(0.5f, 0, 0, 0.8f),
            BorderColor = new Color(0, 0, 0, 0.5f),
        });
        _selHealth.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color(0.1f, 0.7f, 0.1f) });
        _singleArea.AddChild(_selHealth);

        _resLabel = MakeStatLabel("", false, bx, 50, bw);
        _resStats = MakeStatLabel("", true, bx, 50, bw);
        _resBar = new ProgressBar
        {
            MinValue = 0, MaxValue = 100, Value = 100,
            Position = new Vector2(bx, 64),
            Size = new Vector2(bw, 7),
            ShowPercentage = false,
            Visible = false,
        };
        _resBar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0.15f, 0.12f, 0.08f, 0.8f) });
        _resBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color(0.75f, 0.65f, 0.3f) });
        _singleArea.AddChild(_resBar);

        // 底条(y74..100,右栏内):攻防图标(左)+ 携带量(右)。
        _attackIcon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Position = new Vector2(bx, 72),
            Size = new Vector2(28, 28),
            Texture = LoadIcon("stances/defensive"),
            TooltipText = "Attack and Resistance",
        };
        _singleArea.AddChild(_attackIcon);
        _carryText = new Label
        {
            Text = "",
            Position = new Vector2(bx + 32, 76),
            Size = new Vector2(bw - 60, 20),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _carryText.AddThemeFontSizeOverride("font_size", 12);
        _carryText.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.82f));
        _singleArea.AddChild(_carryText);
        _carryIcon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Position = new Vector2(bx + bw - 26, 72),
            Size = new Vector2(26, 26),
            Visible = false,
        };
        _singleArea.AddChild(_carryIcon);

        // 名称区(原版 statsArea 顶部):主名(通用名)居中 + 次名(专名)居中。
        _selName = MakeNameLabel(102, 13);
        _selName2 = MakeNameLabel(120, 11);

        // 玩家带(原版 civ 徽标带):玩家色底 + 文明徽标 + 玩家名。
        _playerBand = new ColorRect
        {
            Position = new Vector2(0, 136),
            Size = new Vector2(size.X - 8, 18),
            Color = new Color(0.3f, 0.3f, 0.3f, 0.55f),
        };
        _singleArea.AddChild(_playerBand);
        _playerCivEmblem = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Position = new Vector2((size.X - 8) / 2 - 60, 137),
            Size = new Vector2(120, 16),
        };
        _singleArea.AddChild(_playerCivEmblem);
        _playerLabel = new Label
        {
            Text = "",
            Position = new Vector2(0, 136),
            Size = new Vector2(size.X - 8, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _playerLabel.AddThemeFontSizeOverride("font_size", 11);
        _playerLabel.AddThemeColorOverride("font_color", Colors.White);
        _playerLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _playerLabel.AddThemeConstantOverride("outline_size", 2);
        _singleArea.AddChild(_playerLabel);

        // ── 多选区(原版 detailsAreaMultiple:单位图标网格 + 右侧计数/竖条)──
        _multiArea = new Control
        {
            Position = new Vector2(6, 6),
            Size = new Vector2(size.X - 12, size.Y - 50),
            Visible = false,
        };
        panel.AddChild(_multiArea);
        _multiGrid = new HFlowContainer
        {
            Position = new Vector2(0, 0),
            Size = new Vector2(160, size.Y - 50),
        };
        _multiGrid.AddThemeConstantOverride("h_separation", 2);
        _multiGrid.AddThemeConstantOverride("v_separation", 2);
        _multiArea.AddChild(_multiGrid);
        _multiCount = new Label
        {
            Text = "",
            Position = new Vector2(164, 4),
            Size = new Vector2(48, 28),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _multiCount.AddThemeFontSizeOverride("font_size", 15);
        _multiCount.AddThemeColorOverride("font_color", Colors.White);
        _multiCount.AddThemeColorOverride("font_outline_color", Colors.Black);
        _multiCount.AddThemeConstantOverride("outline_size", 2);
        _multiArea.AddChild(_multiCount);
        _multiHealth = new ProgressBar
        {
            MinValue = 0, MaxValue = 100,
            Position = new Vector2(176, 36),
            Size = new Vector2(10, size.Y - 100),
            FillMode = (int)ProgressBar.FillModeEnum.BottomToTop,
            ShowPercentage = false,
        };
        _multiHealth.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0.5f, 0, 0, 0.8f) });
        _multiHealth.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color(0.1f, 0.7f, 0.1f) });
        _multiArea.AddChild(_multiHealth);

        // ── 单位命令条(原版 unitCommandPanel:0 100%-40 100% 100%-4)──
        _unitActionRow = new HBoxContainer
        {
            Position = new Vector2(6, size.Y - 38),
            Size = new Vector2(size.X - 12, 36),
        };
        _unitActionRow.AddThemeConstantOverride("separation", 4);
        panel.AddChild(_unitActionRow);

        parent.AddChild(panel);
    }

    // 选择详情区控件(原版 single/multiple details area 成员)。
    private Control _singleArea = null!;
    private Control _multiArea = null!;
    private TextureRect _rankIcon = null!;
    private ProgressBar _xpBar = null!;
    private Label _captureLabel = null!;
    private Label _captureStats = null!;
    private Label _resLabel = null!;
    private Label _resStats = null!;
    private ProgressBar _resBar = null!;
    private TextureRect _attackIcon = null!;
    private Label _carryText = null!;
    private TextureRect _carryIcon = null!;
    private Label _selName2 = null!;
    private ColorRect _playerBand = null!;
    private TextureRect _playerCivEmblem = null!;
    private Label _playerLabel = null!;
    private HFlowContainer _multiGrid = null!;
    private Label _multiCount = null!;
    private ProgressBar _multiHealth = null!;
    private HBoxContainer _unitActionRow = null!;

    private Label MakeStatLabel(string text, bool right, float x, float y, float w)
    {
        var lbl = new Label
        {
            Text = text,
            Position = new Vector2(x, y),
            Size = new Vector2(w, 14),
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        lbl.AddThemeFontSizeOverride("font_size", 11);
        lbl.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.82f));
        lbl.AddThemeColorOverride("font_outline_color", Colors.Black);
        lbl.AddThemeConstantOverride("outline_size", 2);
        _singleArea.AddChild(lbl);
        return lbl;
    }

    private Label MakeNameLabel(float y, int fontSize)
    {
        var lbl = new Label
        {
            Text = "",
            Position = new Vector2(0, y),
            Size = new Vector2(212, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        lbl.AddThemeFontSizeOverride("font_size", fontSize);
        lbl.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.82f));
        lbl.AddThemeColorOverride("font_outline_color", Colors.Black);
        lbl.AddThemeConstantOverride("outline_size", 2);
        _singleArea.AddChild(lbl);
        return lbl;
    }


    private void SetupCommandZone(Control parent, Vector2 pos, Vector2 size)
    {
        var panel = new Control { Position = pos, Size = size };
        // 原版 unitCommandsPanel 底图:hud_panels.png 裁 (75,64)-(469,222)=394×158
        AddPanelBackground(panel, new Rect2(75, 64, 394, 158));
        AddBorderFrame(panel);

        var scroll = new ScrollContainer();
        scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scroll.OffsetLeft = 8; scroll.OffsetTop = 8;
        scroll.OffsetRight = -8; scroll.OffsetBottom = -8;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        panel.AddChild(scroll);

        _commandBox = new HFlowContainer();
        _commandBox.AddThemeConstantOverride("h_separation", 6);
        _commandBox.AddThemeConstantOverride("v_separation", 6);
        _commandBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_commandBox);

        parent.AddChild(panel);

        RebuildCommands();
    }

    /// <summary>单位命令行(第三面板底条;原版 selection_panels_middle/unit_commands.xml):
    /// 6 个 32×32 命令钮——Delete(任意己方实体)/Stop(己方单位)/Garrison(可驻防)/
    /// Repair(建造者)/Guard/Patrol。由 RebuildCommands 随选择变化驱动。</summary>
    private void RebuildUnitActions(bool hasOwnUnit, bool hasOwnEntity)
    {
        foreach (var child in _unitActionRow.GetChildren())
            child.QueueFree();
        _unitActionRow.Visible = hasOwnEntity;

        // 与右面板同套布尔量,但按原版顺序:delete 在 stop 前。
        // 图标对齐原版 unit_actions.js:delete=kill_small.png(骷髅),stop=stop.png(手掌)。
        if (hasOwnEntity)
        {
            // 原版 isUndeletable 逐实体判定:有可删实体 → 亮骷髅可用;全部不可删 →
            // 灰骷髅禁用,tooltip 显示去重后的理由(原版同款文案)。
            var reasons = new List<string>();
            bool anyDeletable = false;
            foreach (var eid in _main.SelectedEntities)
            {
                if (!_main.IsOwn(eid)) continue;
                var reason = _main.GetUndeletableReason(eid);
                if (reason == null) anyDeletable = true;
                else if (!reasons.Contains(reason)) reasons.Add(reason);
            }
            AddUnitActionButton(
                LoadIcon(anyDeletable ? "kill_small" : "kill_small_disabled"),
                anyDeletable ? "Self-Destruct\nDestroy the selected entities."
                             : string.Join("\n", reasons),
                () => _main.DeleteSelectedEntities(),
                enabled: anyDeletable);
        }
        if (!hasOwnUnit) return;
        AddUnitActionButton(LoadIcon("stop"), "Stop", () => _main.StopSelectedUnits());

        bool anyGarrisonable = false, anyBuilder = false;
        foreach (var eid in _main.SelectedEntities)
        {
            if (_sim.Sim.QueryInterface<GarrisonableComponent>(eid) != null) anyGarrisonable = true;
            if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null) anyBuilder = true;
        }
        if (anyGarrisonable)
            AddUnitActionButton(LoadIcon("garrison"), "Garrison", () => _main.EnterCommandTargetMode("garrison"));
        if (anyBuilder)
            AddUnitActionButton(LoadIcon("repair"), "Repair", () => _main.EnterCommandTargetMode("repair"));
        AddUnitActionButton(LoadIcon("add-guard"), "Guard", () => _main.EnterCommandTargetMode("guard"));
        AddUnitActionButton(LoadIcon("patrol"), "Patrol", () => _main.EnterCommandTargetMode("patrol"));
    }

    /// <summary>32×32 命令钮(原版 unitCommandButton 尺寸;图标缺失时显示名保底)。
    /// enabled=false → 禁用态(原版 kill_small_disabled 灰骷髅场景;深底换灰阶样式)。</summary>
    private void AddUnitActionButton(Texture2D? tex, string name, System.Action onPressed, bool enabled = true)
    {
        var btn = new Button
        {
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(32, 32),
            TooltipText = name,
            Disabled = !enabled,
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            VerticalIconAlignment = VerticalAlignment.Center,
        };
        ApplySessionIconButtonStyle(btn);
        if (tex != null) btn.Icon = tex;
        else btn.Text = name;
        btn.Pressed += () => onPressed();
        _unitActionRow.AddChild(btn);
    }

    private void RebuildCommands()
    {        foreach (var child in _commandBox.GetChildren())
            child.QueueFree();

        bool hasBuilder = false, hasProducer = false;
        bool hasArsenal = false, hasOwnUnit = false, hasOwnEntity = false;
        var researcherTemplates = new HashSet<string>();
        foreach (var eid in _main.SelectedEntities)
        {
            if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null) hasBuilder = true;
            if (_sim.Sim.QueryInterface<ProductionQueue>(eid) != null) hasProducer = true;
            if (_main.IsOwn(eid))
            {
                hasOwnEntity = true;
                if (_sim.Sim.QueryInterface<UnitAIComponent>(eid) != null) hasOwnUnit = true;
            }
            var identity = _sim.Sim.QueryInterface<IdentityComponent>(eid);
            if (identity is null) continue;
            if (_sim.Sim.QueryInterface<ResearcherComponent>(eid) != null)
                researcherTemplates.Add(identity.TemplateName);
            if (identity.TemplateName.Contains("arsenal")) hasArsenal = true;
        }

        // 单位命令行(stop/delete/garrison/repair/guard/patrol)在第三面板
        // (选择详情底条,原版 selection_panels_middle/unit_commands.xml)——见
        // RebuildUnitActions;本面板(右)只留生产类:打包/升级/门/训练/建造/研究。
        RebuildUnitActions(hasOwnUnit, hasOwnEntity);

        // 打包栏(原版 pack_panel,右面板):选中含可打包/解包攻城器时显示对应按钮。
        if (hasOwnUnit)
        {
            bool anyCanPack = false, anyCanUnpack = false;
            foreach (var eid in _main.SelectedEntities)
            {
                var pack = _sim.Sim.QueryInterface<PackComponent>(eid);
                if (pack == null) continue;
                if (pack.CanPack()) anyCanPack = true;
                if (pack.CanUnpack()) anyCanUnpack = true;
            }
            if (anyCanPack)
                AddCmdButton(LoadIcon("pack"), "Pack", () => _main.PackSelectedUnits(false));
            if (anyCanUnpack)
                AddCmdButton(LoadIcon("unpack"), "Unpack", () => _main.PackSelectedUnits(true));
        }

        // 升级栏(原版 upgrade_panel):选中己方"有升级路径"建筑(哨塔→防御塔等)
        // 且选中列表含建造者时显示;点击扣费升级(内核拆旧+原位地基续建)。
        if (hasOwnEntity)
        {
            EntityId? upBuilding = null;
            ZeroAD.Sim.Content.TemplateStats? upStats = null;
            foreach (var eid in _main.SelectedEntities)
            {
                if (!_main.IsOwn(eid)) continue;
                var id = _sim.Sim.QueryInterface<IdentityComponent>(eid);
                if (id == null) continue;
                var s = _sim.Sim.Templates?.ExtractStats(id.TemplateName);
                if (s != null && s.UpgradeToTemplate.Length > 0)
                {
                    upBuilding = eid;
                    upStats = s;
                    break;
                }
            }
            if (upBuilding.HasValue && upStats != null)
            {
                EntityId? upBuilder = null;
                foreach (var eid in _main.SelectedEntities)
                    if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null) { upBuilder = eid; break; }

                string targetName = upStats.UpgradeToTemplate;
                var tstats = _sim.Sim.Templates?.ExtractStats(
                    targetName.Replace("{civ}", _sim.Sim.GetPlayerEntity((int)_sim.LocalPlayerId)?.Civ ?? ""));
                string label = tstats?.GenericName.Length > 0 == true ? tstats.GenericName : targetName;
                var costs = new List<string>();
                if (upStats.UpgradeCostWood > 0) costs.Add($"{upStats.UpgradeCostWood}W");
                if (upStats.UpgradeCostFood > 0) costs.Add($"{upStats.UpgradeCostFood}F");
                if (upStats.UpgradeCostStone > 0) costs.Add($"{upStats.UpgradeCostStone}S");
                if (upStats.UpgradeCostMetal > 0) costs.Add($"{upStats.UpgradeCostMetal}M");
                string text = costs.Count > 0 ? $"Upgrade: {label}\n{string.Join(' ', costs)}" : $"Upgrade: {label}";
                var tex = tstats != null ? LoadPortraitFromIcon(tstats.Icon) : null;
                var ub = upBuilding.Value;
                AddCmdButton(tex, text, () => _main.CommandUpgrade(ub, upBuilder));
            }
        }

        // 门栏(原版 gate_panel):选中己方城门 → 显示当前锁态切换键
        // (locked=阻挡/unlocked=通行;GateComponent 联动阻挡+寻路网格)。
        if (hasOwnEntity)
        {
            EntityId? gate = null;
            bool gateLocked = false;
            foreach (var eid in _main.SelectedEntities)
            {
                if (!_main.IsOwn(eid)) continue;
                var g = _sim.Sim.QueryInterface<GateComponent>(eid);
                if (g != null) { gate = eid; gateLocked = g.Locked; break; }
            }
            if (gate.HasValue)
            {
                var tex = LoadIcon("garrison-out");
                var g = gate.Value;
                if (gateLocked)
                    AddCmdButton(tex, "Unlock Gate", () => _main.CommandToggleGate(g, false));
                else
                    AddCmdButton(tex, "Lock Gate", () => _main.CommandToggleGate(g, true));
            }
        }

        if (hasProducer)
        {
            // 数据驱动训练列表(原版 selection_panels 训练面板):取首个选中生产建筑的
            // ProductionQueue 解析列表(Trainer/Entities,{civ}=属主文明已实时解析,
            // 不存在的模板已过滤)——雅典 CC 出雅典兵,斯巴达 CC 出斯巴达兵。
            EntityId? producer = null;
            foreach (var eid in _main.SelectedEntities)
                if (_sim.Sim.QueryInterface<ProductionQueue>(eid) != null) { producer = eid; break; }
            if (producer.HasValue)
            {
                var queue = _sim.Sim.QueryInterface<ProductionQueue>(producer.Value)!;
                foreach (var tmpl in queue.GetTrainableEntities(_sim.Sim))
                    AddTrainButton(tmpl);
            }
        }

        if (hasBuilder)
        {
            // 数据驱动建造列表(原版 construction_panel,与训练面板同款):首个选中建造者的
            // Builder/Entities,{native}=模板原生文明/{civ}=属主文明实时解析,
            // TemplateExists 过滤。硬编码 5 项 + 教程限定项的写法废弃——
            // CC/码头/畜栏/图书馆/奇迹/城墙系/哨塔等全部按模板数据出现。
            EntityId? builder = null;
            foreach (var eid in _main.SelectedEntities)
                if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null) { builder = eid; break; }
            if (builder.HasValue)
            {
                var bIdentity = _sim.Sim.QueryInterface<IdentityComponent>(builder.Value);
                var bstats = bIdentity != null
                    ? _sim.Sim.Templates?.ExtractStats(bIdentity.TemplateName) : null;
                if (bstats != null && bstats.BuildableEntities.Length > 0)
                {
                    string ownerCiv = "";
                    var owner = _sim.Sim.QueryInterface<OwnershipComponent>(builder.Value);
                    if (owner != null)
                    {
                        var player = _sim.Sim.GetPlayerEntity(owner.PlayerId);
                        if (player != null) ownerCiv = player.Civ;
                    }
                    foreach (var raw in bstats.BuildableEntities.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
                    {
                        string token = raw;
                        if (bstats.Civ.Length > 0) token = token.Replace("{native}", bstats.Civ);
                        if (ownerCiv.Length > 0) token = token.Replace("{civ}", ownerCiv);
                        if (token.Contains('{')) continue;
                        if (_sim.Sim.Templates == null || !_sim.Sim.Templates.TemplateExists(token)) continue;
                        AddBuildButton(token);
                    }
                }
            }
        }

        if (researcherTemplates.Count > 0)
        {
            // 数据驱动研究列表(原版 research_panel,与训练/建造面板同款):首个选中研究者的
            // Researcher/Technologies,{civ}/{native} 实时解析;TechnologyManager 过滤
            // 已研究/不存在项,前置未满足的置灰(CanResearch 同款判定)。
            EntityId? researcher = null;
            foreach (var eid in _main.SelectedEntities)
                if (_sim.Sim.QueryInterface<ResearcherComponent>(eid) != null) { researcher = eid; break; }
            if (researcher.HasValue)
            {
                var rIdentity = _sim.Sim.QueryInterface<IdentityComponent>(researcher.Value);
                var rstats = rIdentity != null
                    ? _sim.Sim.Templates?.ExtractStats(rIdentity.TemplateName) : null;
                var tm = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.TechnologyManager>(
                    _sim.Sim.GetPlayerEntityId((int)_sim.LocalPlayerId) ?? default);
                if (rstats != null && rstats.ResearchableTechnologies.Length > 0 && tm != null)
                {
                    string ownerCiv = "";
                    var owner = _sim.Sim.QueryInterface<OwnershipComponent>(researcher.Value);
                    if (owner != null)
                    {
                        var player = _sim.Sim.GetPlayerEntity(owner.PlayerId);
                        if (player != null) ownerCiv = player.Civ;
                    }
                    // 原版 Researcher.GetTechnologiesList 的 supersedes 折叠:supersedes 目标
                    // 同在列表的科技(如 phase_city→phase_town)不作独立图标,只登记进链;
                    // 顶层项已研究/进行中时沿链走到下一个未研究项(按钮原位"变形"为下一
                    // 阶段)——每条链同时只显示一个图标(C++ 实测:村庄期只见 Town Phase,
                    // 研究完 Town 后同一位置变 City Phase,不会两个时代并排)。
                    var tokens = new List<string>();
                    foreach (var raw in rstats.ResearchableTechnologies.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
                    {
                        string tech = raw;
                        if (rstats.Civ.Length > 0) tech = tech.Replace("{native}", rstats.Civ);
                        if (ownerCiv.Length > 0) tech = tech.Replace("{civ}", ownerCiv);
                        if (tech.Contains('{')) continue;
                        // phase 归一(无特制文件的文明回落 *_generic),须在存在性过滤前。
                        if (tech.Contains("phase_") && tm.GetDefinition(tech) == null)
                        {
                            if (tech.StartsWith("phase_town_", System.StringComparison.Ordinal))
                                tech = "phase_town_generic";
                            else if (tech.StartsWith("phase_city_", System.StringComparison.Ordinal))
                                tech = "phase_city_generic";
                        }
                        if (tm.GetDefinition(tech) == null) continue;   // 该文明不可研究(原版 DeriveRequirements 过滤)
                        tokens.Add(tech);
                    }
                    var inList = new HashSet<string>(tokens, System.StringComparer.Ordinal);
                    var superseded = new Dictionary<string, string>(System.StringComparer.Ordinal);   // 被取代者 → 取代者
                    var topLevel = new List<string>();
                    foreach (var t in tokens)
                    {
                        var def = tm.GetDefinition(t)!;
                        if (def.Supersedes != null && inList.Contains(def.Supersedes))
                            superseded[def.Supersedes] = t;
                        else
                            topLevel.Add(t);
                    }
                    var researcherComp = _sim.Sim.QueryInterface<ResearcherComponent>(researcher.Value);
                    foreach (var head in topLevel)
                    {
                        string tech = head;
                        // 已研究/本建筑进行中 → 沿链取下一个(原版 IsTechnologyResearchedOrInProgress)。
                        while (tech.Length > 0
                               && (tm.IsResearched(tech) || (researcherComp?.CurrentTech != null && researcherComp.CurrentTech == tech)))
                            tech = superseded.TryGetValue(tech, out var next) ? next : "";
                        if (tech.Length == 0) continue;
                        AddResearchButton(tech, tm);
                    }
                }
            }
        }

        if (!hasBuilder && !hasProducer && researcherTemplates.Count == 0 && !hasArsenal)
        {
            var hint = new Label { Text = "Select a unit or building" };
            hint.AddThemeColorOverride("font_color", new Color(0.85f, 0.80f, 0.65f));
            hint.AddThemeFontSizeOverride("font_size", 13);
            _commandBox.AddChild(hint);
        }
    }

    /// <summary>训练按钮(数据驱动):头像优先模板 Identity/Icon 指向的原版立绘
    /// (binaries/.../portraits/),回落内置肖像映射;标签=GenericName+资源费。
    /// 点击走通用 TrainUnit(文明正确的完整模板名)。</summary>
    private void AddTrainButton(string template)
    {
        var stats = _sim.Sim.Templates?.ExtractStats(template);
        if (!RequirementsMet(stats)) return;   // 阶段过滤(原版同:未到阶段不显示)
        string label = stats != null && stats.GenericName.Length > 0
            ? stats.GenericName
            : template[(template.LastIndexOf('/') + 1)..];
        var costs = new List<string>();
        if (stats != null)
        {
            if (stats.FoodCost > 0) costs.Add($"{stats.FoodCost}F");
            if (stats.WoodCost > 0) costs.Add($"{stats.WoodCost}W");
            if (stats.StoneCost > 0) costs.Add($"{stats.StoneCost}S");
            if (stats.MetalCost > 0) costs.Add($"{stats.MetalCost}M");
        }
        string text = costs.Count > 0 ? $"{label}\n{string.Join(' ', costs)}" : label;
        var tex = (stats != null ? LoadPortraitFromIcon(stats.Icon) : null)
                  ?? LoadPortraitForTemplate(template);
        string t = template; // 闭包捕获迭代变量
        // 批量提示进 tooltip(原版训练按钮提示 "Shift = 5 个一批");按下瞬间取 Shift。
        var btn = AddCmdButton(tex, text, () => _main.TrainUnit(t, _shiftHeldAtMouseDown), enabled: true);
        btn.TooltipText += " — Shift+click: train 5 at once";
    }

    /// <summary>前置科技全满足?(原版训练/建造面板的阶段过滤:requirements 未满足
    /// → 整钮隐藏,如城镇阶段才解锁的市场/靶场)。空前置 = 恒真。</summary>
    private bool RequirementsMet(ZeroAD.Sim.Content.TemplateStats? stats)
    {
        if (stats == null || stats.RequiredTechs.Length == 0) return true;
        var tm = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.TechnologyManager>(
            _sim.Sim.GetPlayerEntityId((int)_sim.LocalPlayerId) ?? default);
        if (tm == null) return true;
        foreach (var tok in stats.RequiredTechs.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok.StartsWith("-") || tok.StartsWith("!")) continue;   // 否定项不参与
            if (!tm.IsResearched(tok)) return false;
        }
        return true;
    }

    /// <summary>研究按钮(数据驱动,research_panel):图标=JSON icon 字段的原版立绘
    /// (portraits/technologies/),标签=GenericName+资源费;已研究跳过,前置未满足
    /// (如未到本阶段)**整钮隐藏**——原版研究面板只列当前可研究项,不置灰展示。</summary>
    private void AddResearchButton(string tech, ZeroAD.Sim.Components.TechnologyManager tm)
    {
        // phase 归一(与 GameState.ResolveTechName 同款):无特制文件的文明回落 *_generic。
        if (!tech.Contains("phase_") || tm.GetDefinition(tech) == null)
        {
            if (tech.StartsWith("phase_town_", System.StringComparison.Ordinal))
                tech = "phase_town_generic";
            else if (tech.StartsWith("phase_city_", System.StringComparison.Ordinal))
                tech = "phase_city_generic";
        }
        if (tm.IsResearched(tech)) return;
        var def = tm.GetDefinition(tech);
        if (def == null) return;
        // 原版过滤:requirements 未满足(高阶段科技)→ 不显示(而非置灰)。
        if (!tm.CanResearch(tech))
        {
            // 阶段升级科技前置不足 → 置灰显示(原版研究面板:灰色图标+需求提示),
            // 其余科技保持隐藏。缺了它开局看不到"升级到城镇时代"的图标。
            if (!tech.Contains("phase")) return;
            string hint = tech.Contains("city") ? "需要 3 座 Town 建筑" : "需要 5 座 Village 建筑";
            var greyTex = def.Icon.Length > 0 ? LoadPortraitFromIcon("technologies/" + def.Icon) : null;
            AddCmdButton(greyTex, def.GenericName + "\n" + hint, () => { }, enabled: false);
            return;
        }

        string label = def.GenericName;
        var costs = new List<string>();
        if (def.Food > 0) costs.Add($"{def.Food}F");
        if (def.Wood > 0) costs.Add($"{def.Wood}W");
        if (def.Stone > 0) costs.Add($"{def.Stone}S");
        if (def.Metal > 0) costs.Add($"{def.Metal}M");
        string text = costs.Count > 0 ? $"{label}\n{string.Join(' ', costs)}" : label;

        var tex = def.Icon.Length > 0 ? LoadPortraitFromIcon("technologies/" + def.Icon) : null;
        string t = tech;
        AddCmdButton(tex, text, () => _main.ResearchTech(t));
    }

    /// <summary>建造按钮(数据驱动,construction_panel):头像取模板 Identity/Icon 原版立绘,
    /// 标签=GenericName+资源费;点击进入建造放置模式(完整模板名,文明已解析)。</summary>
    private void AddBuildButton(string template)
    {
        var stats = _sim.Sim.Templates?.ExtractStats(template);
        if (!RequirementsMet(stats)) return;   // 阶段过滤(原版同:未到阶段不显示)
        string label = stats != null && stats.GenericName.Length > 0
            ? stats.GenericName
            : template[(template.LastIndexOf('/') + 1)..];
        var costs = new List<string>();
        if (stats != null)
        {
            if (stats.WoodCost > 0) costs.Add($"{stats.WoodCost}W");
            if (stats.FoodCost > 0) costs.Add($"{stats.FoodCost}F");
            if (stats.StoneCost > 0) costs.Add($"{stats.StoneCost}S");
            if (stats.MetalCost > 0) costs.Add($"{stats.MetalCost}M");
        }
        string text = costs.Count > 0 ? $"{label}\n{string.Join(' ', costs)}" : label;
        var tex = (stats != null ? LoadPortraitFromIcon(stats.Icon) : null)
                  ?? LoadPortraitForTemplate(template);
        string t = template;
        AddCmdButton(tex, text, () => _main.EnterBuildMode(t));
    }

    /// <summary>从原版 art 树加载立绘(Identity/Icon 相对路径,如
    /// units/athen/infantry_spearman.png;全文明免拷贝,repo 内 binaries/ 即数据源)。
    /// 找不到返回 null(调用方回落内置映射)。</summary>
    private static Texture2D? LoadPortraitFromIcon(string icon)
    {
        if (icon.Length == 0) return null;
        string projRoot = ProjectSettings.GlobalizePath("res://");
        foreach (var up in new[] { "..", "../.." })
        {
            string p = System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, up,
                "binaries", "data", "mods", "public", "art", "textures", "ui", "session", "portraits",
                icon.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(p)) continue;
            var img = Image.LoadFromFile(p);
            if (img != null) return ImageTexture.CreateFromImage(img);
        }
        return null;
    }

    private static readonly Dictionary<string, string> PortraitMap = new()
    {
        ["support_civilian"] = "portraits/units/support_civilian.png",
        ["infantry_spearman"] = "portraits/units/infantry_spearman.png",
        ["infantry_javelinist"] = "portraits/units/infantry_javelinist.png",
        ["siege_ram"] = "portraits/units/siege_ram.png",
        ["stop"] = "session/icons/cancel.png",
        ["delete"] = "session/icons/die.png",
        ["house"] = "portraits/structures/house.png",
        ["storehouse"] = "portraits/structures/storehouse.png",
        ["farmstead"] = "portraits/structures/farmstead.png",
        ["field"] = "portraits/structures/field.png",
        ["barracks"] = "portraits/structures/barracks.png",
        ["outpost"] = "portraits/structures/outpost.png",
        ["defense_tower"] = "portraits/structures/defense_tower.png",
        ["blacksmith"] = "portraits/structures/blacksmith.png",
        ["market"] = "portraits/structures/market.png",
        ["temple"] = "portraits/structures/temple.png",
        ["arsenal"] = "portraits/structures/barracks.png",
        ["phase_town"] = "phase_town.png",
        ["phase_city"] = "phase_city.png",
        ["infantry_attack"] = "portraits/structures/blacksmith.png",
    };

    private void AddCmdButton(string iconKey, string text, System.Action onPressed) =>
        AddCmdButton(PortraitMap.TryGetValue(iconKey, out var p) ? LoadTex(p) : null, text, onPressed);

    private void AddCmdButton(Texture2D? tex, string text, System.Action onPressed) =>
        AddCmdButton(tex, text, onPressed, enabled: true);

    private Button AddCmdButton(Texture2D? tex, string text, System.Action onPressed, bool enabled)
    {
        var btn = new Button
        {
            Text = "",
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(46, 46),
            Disabled = !enabled,
        };
        ApplySessionIconButtonStyle(btn);
        if (tex != null)
        {
            btn.Icon = tex;
            btn.ExpandIcon = true;
            btn.IconAlignment = HorizontalAlignment.Center;
            btn.VerticalIconAlignment = VerticalAlignment.Top;
        }

        btn.TooltipText = text.Replace("\n", " ");
        btn.Pressed += onPressed;
        // 修饰键在按下瞬间捕获(原版在 mouse-down 读 Shift;Pressed 要等松开,
        // 用户先松 Shift 再松鼠标就丢批量)。
        btn.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                _shiftHeldAtMouseDown = mb.ShiftPressed;
        };

        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.AddThemeFontSizeOverride("font_size", 9);
        label.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.82f));
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 2);
        label.MouseFilter = Control.MouseFilterEnum.Ignore;

        var vbox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        vbox.AddThemeConstantOverride("separation", 0);
        vbox.SizeFlagsVertical = Control.SizeFlags.Fill;
        vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        btn.AddChild(vbox);
        // 顶部间隔(把标签压到按钮下半)——必须 Ignore:默认 Stop 会盖住按钮上半
        // (图标区)吞掉点击,表现为"点图标没反应,点文字有效"。
        vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 24), MouseFilter = Control.MouseFilterEnum.Ignore });
        vbox.AddChild(label);

        _commandBox.AddChild(btn);
        return btn;
    }

    private record struct ResourceCounter(Control Root, Label Count, Label Stats);

    /// <summary>鼠标按下瞬间的 Shift 状态（AddCmdButton GuiInput 捕获;
    /// 批量训练/批量卸载等修饰语义在 mouse-down 判定,同原版）。</summary>
    private bool _shiftHeldAtMouseDown;

    private IReadOnlySet<EntityId> _lastSelection = new HashSet<EntityId>();

    /// <summary>遇袭警报记录(由 Main 在 AttackLanded 且己方目标时调):图标闪现 10s。</summary>
    public void SetAlert(float x, float z)
    {
        _alertX = x; _alertZ = z;
        _alertElapsed = 0;
        _alertBtn.Visible = true;
    }

    public override void _Process(double delta)
    {
        // 警报闪烁(0.6s 周期)与 10s 过期(原版警报按钮同款)。
        if (_alertBtn.Visible)
        {
            _alertElapsed += (float)delta;
            _alertFlash += delta;
            if (_alertElapsed > 10f)
                _alertBtn.Visible = false;
            else
            {
                bool on = (int)(_alertFlash / 0.3) % 2 == 0;
                _alertBtn.Modulate = on ? new Color(1f, 0.35f, 0.3f) : new Color(1f, 1f, 1f);
            }
        }

        // 编队组图标条刷新(签名防抖:组集+各组成员数不变不重建)。
        RefreshGroupRow();

        // 顶栏资源/采集人数/研究进度:两个全实体扫描(GetGathererCounts、
        // RefreshResearchProgress 都遍历 AllEntities)——降频到 4Hz(数值 10Hz tick 才变,
        // 4Hz 刷新无视觉差异;此前每帧两次全表扫描约占 3ms)。
        _topBarAccum += (float)delta;
        if (_topBarAccum >= 0.25f)
        {
            _topBarAccum = 0f;
            RefreshResearchProgress();
            RefreshTopBar();
        }

        var selected = _main.SelectedEntities;
        if (!SelectionEqual(selected, _lastSelection))
        {
            _lastSelection = new HashSet<EntityId>(selected);
            RebuildCommands();
        }

        UpdateSelectionPanel(selected);
    }

    private float _topBarAccum;

    /// <summary>顶栏资源计数 + 各资源采集人数(4Hz 调用;GetGathererCounts 全实体扫描在此)。</summary>
    private void RefreshTopBar()
    {
        var player = _sim.GetPlayer();
        if (player == null) return;
        _resourceCounters[0].Count.Text = player.Food.ToString();
        _resourceCounters[1].Count.Text = player.Wood.ToString();
        _resourceCounters[2].Count.Text = player.Stone.ToString();
        _resourceCounters[3].Count.Text = player.Metal.ToString();
        _resourceCounters[4].Count.Text = $"{player.PopUsed}/{player.PopulationLimit}";

        int[] gatherers = { 0, 0, 0, 0 };
        var counts = _sim.Gui.GetGathererCounts(playerId: 1);
        gatherers[(int)ResourceType.Wood] = counts[ResourceType.Wood];
        gatherers[(int)ResourceType.Food] = counts[ResourceType.Food];
        gatherers[(int)ResourceType.Stone] = counts[ResourceType.Stone];
        gatherers[(int)ResourceType.Metal] = counts[ResourceType.Metal];
        for (int i = 0; i < 4; i++)
        {
            int g = gatherers[i];
            _resourceCounters[i].Stats.Text = g > 0 ? $"+{g}" : "";
            _resourceCounters[i].Stats.AddThemeColorOverride("font_color",
                g > 0 ? new Color(1f, 0.84f, 0f) : new Color(0.78f, 0.78f, 0.78f));
        }
        _resourceCounters[4].Stats.Text = "";
    }

    private static bool SelectionEqual(IReadOnlySet<EntityId> a, IReadOnlySet<EntityId> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var e in a) if (!b.Contains(e)) return false;
        return true;
    }

    private void UpdateSelectionPanel(IReadOnlySet<EntityId> selected)
    {
        if (selected.Count == 0)
        {
            _singleArea.Visible = false;
            _multiArea.Visible = false;
            _queueStrip.Visible = false;
            _selGarrison.Text = "";
            _garrisonRow.Visible = false;
            _garrisonSignature = "";
            RefreshStanceHighlight();
            RefreshFormationRow();
            return;
        }

        EntityId first = default;
        foreach (var e in selected) { first = e; break; }

        // 单选 → detailsAreaSingle;多选 → detailsAreaMultiple(图标网格)。
        if (selected.Count == 1)
        {
            _multiArea.Visible = false;
            _singleArea.Visible = true;
            FillSingleDetails(first);
        }
        else
        {
            _singleArea.Visible = false;
            _multiArea.Visible = true;
            FillMultiDetails(selected);
        }

        // 训练队列(仅生产建筑有非空 ProductionQueue 时显示):头项画进度遮罩,余者待训压暗。
        // 位置 = 第四面板上方横条(原版 unitQueuePanel);剩余时间 = 队列总秒数。
        var queue = _sim.Sim.QueryInterface<ProductionQueue>(first);
        if (queue != null && queue.QueueCount > 0)
        {
            int n = System.Math.Min(queue.QueueCount, _queueSlots.Length);
            float remaining = 0f;
            for (int i = 0; i < n; i++)
            {
                var item = queue.Queue[i];
                remaining += item.BuildTime * item.Count;
            }
            if (n > 0) remaining -= queue.Progress;
            _queueTime.Text = $"{(int)System.Math.Max(remaining, 0f)}s";
            for (int i = 0; i < _queueSlots.Length; i++)
            {
                var slot = _queueSlots[i];
                if (i >= n) { slot.Visible = false; continue; }
                var item = queue.Queue[i];
                slot.Portrait = LoadPortraitForTemplate(item.TemplateName);
                slot.Progress = i == 0 && item.BuildTime > 0f
                    ? Mathf.Clamp(queue.Progress / item.BuildTime, 0f, 1f) : 0f;
                slot.BatchCount = item.Count;
                slot.Visible = true;
                slot.RefreshCount();
                slot.QueueRedraw();
            }
            _queueStrip.Visible = true;
        }
        else
        {
            _queueStrip.Visible = false;
        }

        // 驻军数 + 驻军头像行(原版 garrison 选择面板:头像点击=卸载该单位,
        // 末位按钮=全部卸载;仅己方建筑可卸载——Main.Unload* 有归属门)。
        var holder = _sim.Sim.QueryInterface<GarrisonHolderComponent>(first);
        bool ownHolder = holder != null && _main.IsOwn(first);
        _selGarrison.Text = holder != null && holder.Entities.Count > 0
            ? $"Garrison: {holder.Entities.Count}/{holder.GetCapacity(_sim.Sim)}" : "";
        if (ownHolder && holder!.Entities.Count > 0)
        {
            // 签名 = 宿主 + 驻军名单;变才重建(每帧重建按钮会闪烁且打断 hover)。
            var sig = new System.Text.StringBuilder(first.Value.ToString());
            foreach (var ge in holder.Entities) sig.Append(':').Append(ge.Value);
            if (sig.ToString() != _garrisonSignature)
            {
                _garrisonSignature = sig.ToString();
                foreach (var child in _garrisonRow.GetChildren()) child.QueueFree();
                foreach (var ge in holder.Entities)
                {
                    var identity2 = _sim.Sim.QueryInterface<IdentityComponent>(ge);
                    var btn = new Button
                    {
                        Theme = UITheme.GetTheme(),
                        CustomMinimumSize = new Vector2(28, 28),
                        TooltipText = $"Unload {identity2?.Name ?? ge.Value.ToString()}",
                        ExpandIcon = true,
                        IconAlignment = HorizontalAlignment.Center,
                        VerticalIconAlignment = VerticalAlignment.Center,
                    };
                    ApplySessionIconButtonStyle(btn);
                    var tex = LoadPortraitForIdentity(identity2);
                    if (tex != null) btn.Icon = tex;
                    EntityId captured = ge;
                    btn.Pressed += () => _main.UnloadGarrison(first, captured);
                    _garrisonRow.AddChild(btn);
                }
                var allBtn = new Button
                {
                    Theme = UITheme.GetTheme(),
                    CustomMinimumSize = new Vector2(28, 28),
                    TooltipText = "Unload all",
                    ExpandIcon = true,
                    IconAlignment = HorizontalAlignment.Center,
                    VerticalIconAlignment = VerticalAlignment.Center,
                };
                ApplySessionIconButtonStyle(allBtn);
                var outTex = LoadIcon("garrison-out");
                if (outTex != null) allBtn.Icon = outTex;
                allBtn.Pressed += () => _main.UnloadAllGarrison(first);
                _garrisonRow.AddChild(allBtn);
            }
            _garrisonRow.Visible = true;
        }
        else
        {
            _garrisonRow.Visible = false;
            _garrisonSignature = "";
        }

        RefreshStanceHighlight();
        RefreshFormationRow();
    }

    // 通用名缓存(ExtractStats 每帧太贵;模板名 → GenericName)。
    private readonly Dictionary<string, string> _genericNameCache = new();
    private readonly Dictionary<string, string> _specificNameCache = new();
    private readonly Dictionary<string, Texture2D?> _portraitCache = new();

    /// <summary>专名(SpecificName,如 Loxodonta africana / Oikos)——模板 Identity/SpecificName,
    /// 缓存模式同 GenericNameOf。无专名返回 ""。</summary>
    private string SpecificNameOf(IdentityComponent identity)
    {
        if (_specificNameCache.TryGetValue(identity.TemplateName, out var cached)) return cached;
        string specific = "";
        try
        {
            var stats = _sim.Sim.Templates?.ExtractStats(identity.TemplateName);
            if (stats != null) specific = stats.SpecificName;
        }
        catch { }
        _specificNameCache[identity.TemplateName] = specific;
        return specific;
    }

    /// <summary>头像:数据驱动(模板 Identity/Icon,原版 selection_details 同款数据源,
    /// 经 PortraitLoader 读 junction 原图);解析失败回退旧的模板名硬编码映射。</summary>
    private Texture2D? ResolvePortrait(IdentityComponent? identity)
    {
        if (identity == null) return null;
        if (_portraitCache.TryGetValue(identity.TemplateName, out var cached)) return cached;
        Texture2D? tex = null;
        try
        {
            var icon = _sim.Sim.Templates?.ExtractStats(identity.TemplateName).Icon;
            if (!string.IsNullOrEmpty(icon)) tex = PortraitLoader.Load(icon);
        }
        catch { }
        tex ??= LoadPortraitForTemplate(identity.TemplateName, identity.IsBuilding);
        _portraitCache[identity.TemplateName] = tex;
        return tex;
    }

    private string GenericNameOf(IdentityComponent identity)
    {
        if (_genericNameCache.TryGetValue(identity.TemplateName, out var cached)) return cached;
        string generic = identity.Name;
        try
        {
            var stats = _sim.Sim.Templates?.ExtractStats(identity.TemplateName);
            if (stats != null && stats.GenericName.Length > 0) generic = stats.GenericName;
        }
        catch { }
        _genericNameCache[identity.TemplateName] = generic;
        return generic;
    }

    /// <summary>单选详情(原版 detailsAreaSingle 的逐字段填充)。</summary>
    private void FillSingleDetails(EntityId ent)
    {
        var identity = _sim.Sim.QueryInterface<IdentityComponent>(ent);
        var health = _sim.Sim.QueryInterface<HealthComponent>(ent);

        _selIcon.Texture = ResolvePortrait(identity);
        if (identity != null)
        {
            // 原版默认 howtoshownames=0:专名主显、通用名次显(无专名回退通用名)。
            string generic = GenericNameOf(identity);
            string specific = SpecificNameOf(identity);
            _selName.Text = specific.Length > 0 ? specific : generic;
            _selName2.Text = generic;
        }
        else
        {
            _selName.Text = "Entity";
            _selName2.Text = "";
        }

        // 军衔图标(Basic/Advanced/Elite → ranks/ 图标;无件或无军衔隐藏)。
        string rank = identity != null
            ? identity.HasClass("Elite") ? "Elite"
            : identity.HasClass("Advanced") ? "Advanced"
            : identity.HasClass("Basic") ? "Basic" : ""
            : "";
        var rankTex = rank.Length > 0 ? LoadIcon($"ranks/{rank}") : null;
        _rankIcon.Texture = rankTex;
        _rankIcon.Visible = rankTex != null;

        // 经验条(仅可晋升单位)。
        var promotion = _sim.Sim.QueryInterface<PromotionComponent>(ent);
        if (promotion != null && promotion.XpNext > 0)
        {
            _xpBar.Value = 100.0 * promotion.XP / promotion.XpNext;
            _xpBar.Visible = true;
        }
        else
        {
            _xpBar.Visible = false;
        }

        // 血条(原版 healthSection:无 Health 件整体隐藏——树/岩石不可攻击;
        // 尸体 hp=0 也按无血条处理,资源段上提到顶槽,与原版尸体显示一致)。
        bool showHealth = health is { Max: > 0, Current: > 0 };
        _selHealth.Visible = showHealth;
        _selHealthText.Visible = showHealth;
        if (showHealth)
        {
            _selHealth.Value = 100.0 * health.Current / health.Max;
            _selHealthText.Text = $"{health.Current}/{health.Max}";
        }

        // 占领条:仅可占领实体(Capturable)显示;分段宽=CP/max,玩家色,升序确定。
        var capturable = _sim.Sim.QueryInterface<CapturableComponent>(ent);
        float maxCp = capturable?.MaxCapturePoints.ToFloat() ?? 0f;
        if (capturable != null && maxCp > 0f)
        {
            float total = 0;
            int n = System.Math.Min(capturable.CapturePoints.Length, CaptureBar.MaxPlayers);
            var sb = new System.Text.StringBuilder("Capture");
            for (int p = 0; p < n; p++)
            {
                float cp = capturable.CapturePoints[p].ToFloat();
                _selCapture.Fractions[p] = cp / maxCp;
                total += cp;
                if (cp > 0f) sb.Append($"  P{p}:{(int)cp}");
            }
            _selCapture.Count = n;
            _selCapture.TooltipText = sb.Append($"/{(int)maxCp}").ToString();
            _selCapture.Visible = true;
            _selCapture.QueueRedraw();
            _captureLabel.Text = "Capture";
            _captureStats.Text = $"{(int)total}/{(int)maxCp}";
        }
        else
        {
            _selCapture.Visible = false;
            _captureLabel.Text = "";
            _captureStats.Text = "";
        }

        // 资源条(原版 resourceSection:gaia 资源/尸体剩余量;无 health 段时提到顶槽——
        // 原版 sectionPosTop/Middle/Bottom 重排,树/矿的资源段占据血条位置)。
        var supply = _sim.Sim.QueryInterface<ResourceSupply>(ent);
        bool showSupply = supply != null && supply.MaxAmount > 0;
        float resY = showHealth ? 50f : 26f;
        _resLabel.Position = new Vector2(100, resY);
        _resStats.Position = new Vector2(100, resY);
        _resBar.Position = new Vector2(100, resY + 14);
        if (showSupply)
        {
            _resLabel.Text = supply.Type.ToString();
            _resStats.Text = $"{supply.Amount}/{supply.MaxAmount}";
            _resBar.Value = 100.0 * supply.Amount / supply.MaxAmount;
            _resBar.Visible = true;
        }
        else
        {
            _resLabel.Text = "";
            _resStats.Text = "";
            _resBar.Visible = false;
        }

        // 携带量(原版 resourceCarryingText/Icon:采集者身上的资源)。
        var gatherer = _sim.Sim.QueryInterface<ResourceGatherer>(ent);
        if (gatherer != null && gatherer.CarryAmount > 0)
        {
            _carryText.Text = gatherer.CarryAmount.ToString();
            _carryIcon.Texture = LoadTex($"session/icons/resources/{gatherer.CarryType.ToString().ToLowerInvariant()}.png")
                ?? LoadTex($"icon_{gatherer.CarryType.ToString().ToLowerInvariant()}.png");
            _carryIcon.Visible = true;
        }
        else
        {
            _carryText.Text = "";
            _carryIcon.Visible = false;
        }

        // 攻防 tooltip(原版 attackAndResistanceStats 的悬浮详情)。
        var attack = _sim.Sim.QueryInterface<AttackComponent>(ent);
        var resistance = _sim.Sim.QueryInterface<ResistanceComponent>(ent);
        if (attack != null || resistance != null)
        {
            var sb = new System.Text.StringBuilder();
            if (attack != null)
                sb.Append($"Attack: {attack.Damage.TotalPhysical}");
            if (resistance != null)
            {
                if (sb.Length > 0) sb.Append("  ");
                sb.Append($"Resistance: H{resistance.Resistances.GetValueOrDefault(DamageType.Hack)} " +
                    $"P{resistance.Resistances.GetValueOrDefault(DamageType.Pierce)} " +
                    $"C{resistance.Resistances.GetValueOrDefault(DamageType.Crush)}");
            }
            _attackIcon.TooltipText = sb.ToString();
        }
        else
        {
            _attackIcon.TooltipText = "";
        }

        // 玩家带:色块 + 文明徽标 + 玩家名(原版 playerCivIcon/playerColorBackground/player)。
        // 无 OwnershipComponent = gaia(原版 entState.player=0,玩家名 "Gaia")——此前按 -1
        // 处理整条留空,gaia 单位/资源没有属主带,与 C++ 版不一致。
        var owner = _sim.Sim.QueryInterface<OwnershipComponent>(ent);
        int pid = owner?.PlayerId ?? 0;
        {
            _playerBand.Color = SimBridge.GetPlayerColor(pid) with { A = 0.55f };
            var player = _sim.Sim.GetPlayerEntity(pid);
            string civ = player?.Civ ?? "";
            _playerLabel.Text = pid == 0 ? "Gaia" : $"Player {pid}";
            var emblemTex = civ.Length > 0
                ? LoadTex($"session/portraits/emblems/emblem_{CivEmblemNames.GetValueOrDefault(civ, "hellenes")}.png")
                : null;
            _playerCivEmblem.Texture = emblemTex;
            _playerCivEmblem.Visible = emblemTex != null;
        }
    }

    // 多选网格的健康微条引用(按钮按签名重建,血条每帧刷新)。
    private readonly List<(ColorRect Bar, List<EntityId> Group)> _multiBars = new();
    private string _multiSignature = "";

    /// <summary>多选详情(原版 detailsAreaMultiple):按模板分组的图标网格
    /// (38×38,头像+计数+底部健康微条),右侧总数 + 竖向平均血条;
    /// 点击图标 = 选中该模板组(原版 unitSelectionButton 行为)。</summary>
    private void FillMultiDetails(IReadOnlySet<EntityId> selected)
    {
        // 分组:模板名 → 成员(组序按模板名字典序,确定性)。
        var groups = new Dictionary<string, List<EntityId>>(System.StringComparer.Ordinal);
        foreach (var eid in selected)
        {
            string key = _sim.Sim.QueryInterface<IdentityComponent>(eid)?.TemplateName ?? "?";
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<EntityId>();
            list.Add(eid);
        }
        var ordered = groups.OrderBy(k => k.Key, System.StringComparer.Ordinal).ToList();

        string sig = string.Join('|', ordered.Select(k => $"{k.Key}:{k.Value.Count}"));
        if (sig != _multiSignature)
        {
            _multiSignature = sig;
            foreach (var child in _multiGrid.GetChildren()) child.QueueFree();
            _multiBars.Clear();
            foreach (var (template, members) in ordered)
            {
                var btn = new Button
                {
                    Theme = UITheme.GetTheme(),
                    CustomMinimumSize = new Vector2(38, 38),
                    TooltipText = members[0].ToString(),
                    ExpandIcon = true,
                    IconAlignment = HorizontalAlignment.Center,
                    VerticalIconAlignment = VerticalAlignment.Center,
                };
                var identity = _sim.Sim.QueryInterface<IdentityComponent>(members[0]);
                btn.TooltipText = identity?.Name ?? template;
                var tex = LoadPortraitForIdentity(identity);
                if (tex != null) btn.Icon = tex;
                // 计数角标 + 底部健康微条。
                var count = new Label
                {
                    Text = members.Count > 1 ? members.Count.ToString() : "",
                    Position = new Vector2(20, 22),
                    Size = new Vector2(18, 14),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                count.AddThemeFontSizeOverride("font_size", 10);
                count.AddThemeColorOverride("font_color", Colors.White);
                count.AddThemeColorOverride("font_outline_color", Colors.Black);
                count.AddThemeConstantOverride("outline_size", 2);
                btn.AddChild(count);
                var bar = new ColorRect
                {
                    Position = new Vector2(2, 35),
                    Size = new Vector2(34, 3),
                    Color = new Color(0.1f, 0.7f, 0.1f),
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                btn.AddChild(bar);
                var group = members;
                btn.Pressed += () => _main.SelectOnly(group);
                _multiGrid.AddChild(btn);
                _multiBars.Add((bar, group));
            }
        }

        // 每帧刷新:微条宽度 = 组平均血;右侧竖条 = 全体平均血;计数 = 总数。
        float totalFrac = 0;
        int healthCounted = 0;
        foreach (var (bar, group) in _multiBars)
        {
            float sum = 0;
            int n = 0;
            foreach (var eid in group)
            {
                var h = _sim.Sim.QueryInterface<HealthComponent>(eid);
                if (h == null || h.Max <= 0) continue;
                sum += (float)h.Current / h.Max;
                n++;
            }
            float frac = n > 0 ? sum / n : 1f;
            bar.Size = new Vector2(34 * frac, 3);
            totalFrac += frac;
            healthCounted++;
        }
        _multiHealth.Value = healthCounted > 0 ? 100.0 * totalFrac / healthCounted : 100;
        _multiCount.Text = $"×{selected.Count}";
    }


    /// <summary>阵型行(原版 formation_panel):编队控制器选中 → 只显 null(解散);
    /// 否则:任一选中实体非 Unit 类 → 整行隐藏(原版 getItems 首门);有可编队单位 →
    /// 列出其模板 UnitAI/Formations 并集(原版:玩家可用阵型按"任一选中单位拥有"过滤,
    /// 与并集同效);每按钮按原版 CanMoveEntsIntoFormation 置灰——支持该阵型的选中单位数
    /// ≥ RequiredMemberCount 才可点,否则禁用+disabledTooltip。
    /// 签名防抖(成员集+阵型集不变不重建,与驻军行同款)。</summary>
    private void RefreshFormationRow()
    {
        var sel = _main.SelectedEntities;
        bool hasController = false;
        var ownUnits = new List<EntityId>();
        foreach (var eid in sel)
        {
            var ai = _sim.Sim.QueryInterface<UnitAIComponent>(eid);
            if (ai == null) continue;
            if (ai.IsFormationController) { hasController = true; continue; }
            if (_main.IsOwn(eid) && !ai.IsGarrisoned && !ai.IsTurret)
                ownUnits.Add(eid);
        }

        // 签名含全部选中 id:非 Unit 混选(首门)也要触发重建,不能只看可编队成员。
        string sig = hasController ? "ctrl" : string.Join(',', sel.Select(u => u.Value));
        if (sig == _formationSignature) return;
        _formationSignature = sig;

        foreach (var child in _formationRow.GetChildren())
            child.QueueFree();

        if (hasController)
        {
            var btn = MakeSmallIconButton(LoadIcon("formations/null"), "Disband formation");
            btn.Pressed += () => _main.FormSelectedUnits("null");
            _formationRow.AddChild(btn);
            _formationRow.Visible = true;
            return;
        }

        // 原版 getItems 首门:任一选中实体非 Unit 类 → 整行不显示(建筑/资源混选)。
        foreach (var eid in sel)
        {
            var identity = _sim.Sim.QueryInterface<IdentityComponent>(eid);
            if (identity == null
                || !ZeroAD.Sim.Content.EntityClassHelper.MatchesClassList(identity.Classes, "Unit"))
            {
                _formationRow.Visible = false;
                return;
            }
        }

        // 只数"可编队"单位(模板 FormationShapes 非空):support 系(村民)原版即
        // <Formations disable=""/>——不可编队单位不计数、不出现在成员表(原版
        // unitAI.formations 为空 → CanUseFormation 恒 false)。
        var formable = new List<(EntityId Id, ZeroAD.Sim.Content.TemplateStats Stats)>();
        foreach (var eid in ownUnits)
        {
            var id = _sim.Sim.QueryInterface<IdentityComponent>(eid);
            var st = id != null ? _sim.Sim.Templates?.ExtractStats(id.TemplateName) : null;
            if (st != null && st.FormationShapes.Length > 0)
                formable.Add((eid, st));
        }
        // 原版:全部选中单位都无 formations 才隐藏;仅 1 个可编队单位也显示(按钮全置灰)。
        if (formable.Count == 0)
        {
            _formationRow.Visible = false;
            return;
        }

        // 阵型列表 = 可编队单位模板 FormationShapes 并集(去重保序;原版:玩家可用阵型
        // 列表按"任一选中单位拥有"过滤,单位 token ⊆ 玩家列表,故与并集同效)。
        var shapes = new List<string>();
        foreach (var (_, st) in formable)
            foreach (var tok in st.FormationShapes.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
                if (!shapes.Contains(tok)) shapes.Add(tok);

        foreach (var tok in shapes)
        {
            string shape = tok.Replace("special/formations/", "");
            // 原版 CanMoveEntsIntoFormation:支持该阵型的选中单位数 ≥ RequiredMemberCount
            // 才可点(否则置灰+disabledTooltip);null(解散)恒可点。
            bool ok = tok == "special/formations/null";
            string disabledTip = "";
            if (!ok)
            {
                int capable = 0;
                foreach (var (_, st) in formable)
                {
                    foreach (var t in st.FormationShapes.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
                        if (t == tok) { capable++; break; }
                }
                int required = 1;
                ZeroAD.Sim.Content.TemplateStats? fst = null;
                try { fst = _sim.Sim.Templates?.ExtractStats(tok); } catch { }
                if (fst != null && fst.HasFormation)
                {
                    required = System.Math.Max(1, fst.FormationRequiredMemberCount);
                    disabledTip = fst.FormationDisabledTooltip;
                }
                ok = capable >= required;
            }
            var tex = LoadIcon($"formations/{shape}");
            string tip = $"Formation: {shape}";
            if (!ok && disabledTip.Length > 0) tip += $"\n{disabledTip}";
            var btn = MakeSmallIconButton(tex, tip);
            if (tex == null) btn.Text = shape;   // 贴图缺失时保底显示名(不致"空按钮看不见")
            btn.Disabled = !ok;
            string s = shape;
            btn.Pressed += () => _main.FormSelectedUnits(s);
            _formationRow.AddChild(btn);
        }
        _formationRow.Visible = true;
    }

    /// <summary>研究进度条(原版 session_objects research progress):首个己方在研建筑
    /// 的科技名+进度;图标取科技 JSON icon 的原版立绘。完成/无在研自动隐藏。</summary>
    private void RefreshResearchProgress()
    {
        string? tech = null;
        float progress = 0f, total = 1f;
        foreach (var eid in _sim.Sim.AllEntities)
        {
            var owner = _sim.Sim.QueryInterface<OwnershipComponent>(eid);
            if (owner == null || owner.PlayerId != (int)_sim.LocalPlayerId) continue;
            var r = _sim.Sim.QueryInterface<ResearcherComponent>(eid);
            if (r == null || !r.IsResearching || r.CurrentTech == null) continue;
            tech = r.CurrentTech;
            progress = r.Progress;
            var tm = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.TechnologyManager>(
                _sim.Sim.GetPlayerEntityId((int)_sim.LocalPlayerId) ?? default);
            var def = tm?.GetDefinition(tech);
            if (def != null && def.ResearchTime > 0) total = def.ResearchTime;
            break;
        }

        if (tech == null)
        {
            if (_researchPanel.Visible) _researchPanel.Visible = false;
            _researchTech = "";
            return;
        }

        _researchPanel.Visible = true;
        _researchBar.Value = 100f * progress / total;
        if (tech != _researchTech)
        {
            _researchTech = tech;
            var tm = _sim.Sim.QueryInterface<ZeroAD.Sim.Components.TechnologyManager>(
                _sim.Sim.GetPlayerEntityId((int)_sim.LocalPlayerId) ?? default);
            var def = tm?.GetDefinition(tech);
            _researchLabel.Text = def?.GenericName ?? tech;
            _researchIcon.Texture = def != null && def.Icon.Length > 0
                ? LoadPortraitFromIcon("technologies/" + def.Icon) : null;
        }
    }

    /// <summary>编队组图标条(原版 PanelEntityManager 紧凑版):已编入的组按号升序显示
    /// 小图标(数字+成员数),点击选中该组。签名防抖。</summary>
    private void RefreshGroupRow()
    {
        var info = _main.GetControlGroupInfo();
        string sig = string.Join(',', info.Select(i => $"{i.group}:{i.alive}"));
        if (sig == _groupSignature) return;
        _groupSignature = sig;

        foreach (var child in _groupRow.GetChildren())
            child.QueueFree();

        foreach (var (g, alive) in info)
        {
            var btn = new Button
            {
                Text = $"{g}",
                Theme = UITheme.GetTheme(),
                CustomMinimumSize = new Vector2(24, 24),
                TooltipText = $"Group {g} ({alive} entities)",
            };
            btn.AddThemeFontSizeOverride("font_size", 11);
            int captured = g;
            btn.Pressed += () => _main.SelectControlGroupPublic(captured);
            var count = new Label
            {
                Text = $"{alive}",
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            count.AddThemeFontSizeOverride("font_size", 8);
            count.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.82f));
            count.AddThemeColorOverride("font_outline_color", Colors.Black);
            count.AddThemeConstantOverride("outline_size", 2);
            count.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
            btn.AddChild(count);
            _groupRow.AddChild(btn);
        }
    }

    private static Button MakeSmallIconButton(Texture2D? tex, string tooltip)
    {
        var btn = new Button
        {
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(28, 28),
            TooltipText = tooltip,
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            VerticalIconAlignment = VerticalAlignment.Center,
        };
        ApplySessionIconButtonStyle(btn);
        if (tex != null) btn.Icon = tex;
        return btn;
    }

    /// <summary>站姿行:仅当选中含"有站姿的己方单位"时可见(对齐原版 stance 按钮条
    /// 只在单位选择时出现);当前站姿按钮黄色高亮,其余原色。每帧随选择面板刷新,
    /// 锁步命令落地(数回合后)也最多晚一帧反映。</summary>
    private void RefreshStanceHighlight()
    {
        string? current = _main.GetFirstSelectedStance();
        bool show = current != null;
        _stanceRow.Visible = show;
        _stanceLabel.Visible = show;
        foreach (var (name, btn) in _stanceButtons)
            btn.Modulate = name == current
                ? new Color(1f, 0.85f, 0.35f)
                : Colors.White;
    }

    private static Texture2D? LoadPortraitForIdentity(IdentityComponent? identity) =>
        identity == null ? null : LoadPortraitForTemplate(identity.TemplateName, identity.IsBuilding);

    /// <summary>按模板名解析头像路径。队列槽持模板名(无 IdentityComponent)直接调;
    /// isBuilding 仅兜底占位图。映射对齐原版 selection_details 头像选择。</summary>
    private static Texture2D? LoadPortraitForTemplate(string tmpl, bool isBuilding = false)
    {
        string portraitKey = tmpl switch
        {
            var t when t.Contains("civil_centre") || t.Contains("civic_centre") => "portraits/structures/civic_centre.png",
            var t when t.Contains("house") => "portraits/structures/house.png",
            var t when t.Contains("storehouse") => "portraits/structures/storehouse.png",
            var t when t.Contains("farmstead") => "portraits/structures/farmstead.png",
            var t when t.Contains("field") => "portraits/structures/field.png",
            var t when t.Contains("barracks") => "portraits/structures/barracks.png",
            var t when t.Contains("outpost") => "portraits/structures/outpost.png",
            var t when t.Contains("tower") => "portraits/structures/defense_tower.png",
            var t when t.Contains("blacksmith") || t.Contains("forge") => "portraits/structures/blacksmith.png",
            var t when t.Contains("market") => "portraits/structures/market.png",
            var t when t.Contains("temple") => "portraits/structures/temple.png",
            var t when t.Contains("arsenal") => "portraits/structures/barracks.png",
            var t when t.Contains("support_civilian") => "portraits/units/support_civilian.png",
            var t when t.Contains("infantry_spearman") => "portraits/units/infantry_spearman.png",
            var t when t.Contains("infantry_javelinist") => "portraits/units/infantry_javelinist.png",
            var t when t.Contains("cavalry") => "portraits/units/cavalry_javelinist.png",
            var t when t.Contains("siege_ram") => "portraits/units/siege_ram.png",
            var t when t.Contains("support_female") => "portraits/units/support_female_citizen.png",
            _ => isBuilding ? "icon_stone.png" : "icon_population.png",
        };

        return LoadTex(portraitKey);
    }

    private static Texture2D? LoadTex(string file)
    {
        string path = ProjectSettings.GlobalizePath($"res://assets/ui/{file}");
        if (!System.IO.File.Exists(path)) return null;
        var img = Image.LoadFromFile(path);
        return img != null ? ImageTexture.CreateFromImage(img) : null;
    }

    /// <summary>Loads a session icon (diplomacy/garrison/stance/menu etc.),
    /// searching the session/icons/ subdirectory. Accepts either a bare name
    /// ("diplomacy") or a relative path ("stances/aggressive").</summary>
    private static Texture2D? LoadIcon(string name)
    {
        // Preserve subdirectories (stances/aggressive) but strip file extension.
        string withoutExt = System.IO.Path.ChangeExtension(name, null);
        return LoadTex($"session/icons/{withoutExt}.png");
    }

    // ── 会话图标钮样式(原版 iconButton:sprite=snIconPortrait = portrait_black 深底,
    // hover add_color 42,disabled 灰阶)——第三面板命令条/右面板生产钮/站姿/阵型/驻防
    // 小图标钮共用;替代主题石纹底(C++ 版这些钮不是石头底)。──
    private static StyleBox? _iconBtnNormal, _iconBtnHover, _iconBtnPressed, _iconBtnDisabled;

    private static void ApplySessionIconButtonStyle(Button btn)
    {
        if (_iconBtnNormal == null)
        {
            var tex = UITheme.TryLoad("res://assets/textures/misc/portrait_black.png");
            if (tex == null) return;
            _iconBtnNormal = new StyleBoxTexture { Texture = tex };
            _iconBtnHover = new StyleBoxTexture { Texture = tex, ModulateColor = new Color(1.16f, 1.16f, 1.16f) };
            _iconBtnPressed = new StyleBoxTexture { Texture = tex, ModulateColor = new Color(0.85f, 0.85f, 0.85f) };
            _iconBtnDisabled = new StyleBoxTexture { Texture = tex, ModulateColor = new Color(0.55f, 0.55f, 0.55f, 0.7f) };
        }
        btn.AddThemeStyleboxOverride("normal", _iconBtnNormal);
        btn.AddThemeStyleboxOverride("hover", _iconBtnHover);
        btn.AddThemeStyleboxOverride("pressed", _iconBtnPressed);
        btn.AddThemeStyleboxOverride("disabled", _iconBtnDisabled);
        btn.AddThemeStyleboxOverride("focus", _iconBtnNormal);
    }

    /// <summary>占领条(对齐原版 selection_details 的 capture bar):自绘分段堆叠条,
    /// 每段=一玩家 CP 占比(玩家色,下标升序=确定性)。Fractions/Count 由 HUD 每帧填,
    /// QueueRedraw 触发重绘。</summary>
    private sealed partial class CaptureBar : Control
    {
        public const int MaxPlayers = 17;   // gaia(0) + 16 玩家(对齐 LosGrid.MaxPlayers+1)
        public readonly float[] Fractions = new float[MaxPlayers];
        public int Count;

        public override void _Draw()
        {
            var rect = new Rect2(Vector2.Zero, Size);
            DrawRect(rect, new Color(0f, 0f, 0f, 0.6f));
            float x = 0f;
            for (int p = 0; p < Count; p++)
            {
                float f = Fractions[p];
                if (f <= 0f) continue;
                float w = rect.Size.X * f;
                // 收尾段贴齐右缘,吸收浮点累计误差(条总宽恒=满宽)。
                if (x + w > rect.Size.X) w = rect.Size.X - x;
                DrawRect(new Rect2(x, 0, w, rect.Size.Y), SimBridge.GetPlayerColor(p));
                x += w;
            }
            DrawRect(rect, new Color(0f, 0f, 0f, 0.8f), filled: false, width: 1f);
        }
    }

    /// <summary>训练队列槽(镜像 CaptureBar 自绘范式):头像贴满 + 头项进度遮罩
    ///(未训部分压暗,训完从底部消散,对齐原版 selection_details 训练槽观感)+
    /// 批量数 ×N + 黑描边。Portrait/Progress/BatchCount 由 UpdateSelectionPanel 每帧填,
    /// QueueRedraw 触发重绘。池化复用,Visible 切换不重建。</summary>
    private sealed partial class QueueSlot : Control
    {
        public Texture2D? Portrait;
        public float Progress;      // 0..1,仅头项有意义(其余槽恒 0 = 待训压暗)
        public int BatchCount;      // item.Count>1 时右下角显示 ×N
        public int SlotIndex;       // 队列下标(点击取消用)

        /// <summary>左键点击 = 取消本槽生产项(原版点队列项取消,退全额资源)。</summary>
        public event System.Action<int>? Clicked;

        public override void _GuiInput(global::Godot.InputEvent @event)
        {
            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                Clicked?.Invoke(SlotIndex);
                AcceptEvent();
            }
        }

        private readonly Label _countLabel;

        public QueueSlot()
        {
            // 批量数 ×N 用 Label 子节点(规避 DrawString 跨版本签名差异),右下角白字黑描边。
            _countLabel = new Label
            {
                MouseFilter = MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            _countLabel.AddThemeFontSizeOverride("font_size", 12);
            _countLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
            _countLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
            _countLabel.AddThemeConstantOverride("outline_size", 3);
            _countLabel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_countLabel);
        }

        /// <summary>HUD 每帧填完字段后调,刷新批量数标签(_Draw 不画文字)。</summary>
        public void RefreshCount()
        {
            bool show = BatchCount > 1;
            _countLabel.Text = show ? $"×{BatchCount}" : "";
            _countLabel.Visible = show;
        }

        public override void _Draw()
        {
            var rect = new Rect2(Vector2.Zero, Size);
            if (Portrait != null)
                DrawTextureRect(Portrait, rect, tile: false);
            else
                DrawRect(rect, new Color(0.3f, 0.3f, 0.3f));

            // 进度遮罩:部分训完(0<Progress<1)→ 顶部未训段压暗;未开始(Progress<=0)→ 整槽压暗。
            if (Progress > 0f && Progress < 1f)
            {
                float doneH = rect.Size.Y * Progress;
                DrawRect(new Rect2(0, doneH, rect.Size.X, rect.Size.Y - doneH),
                    new Color(0f, 0f, 0f, 0.55f));
            }
            else if (Progress <= 0f)
            {
                DrawRect(rect, new Color(0f, 0f, 0f, 0.45f));
            }

            DrawRect(rect, new Color(0f, 0f, 0f, 0.8f), filled: false, width: 1f);
        }
    }
}
