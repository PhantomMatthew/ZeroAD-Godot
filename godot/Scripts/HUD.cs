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
    private readonly List<ResourceCounter> _resourceCounters = new();
    private Minimap _minimap = null!;
    private Panel _bottomBar = null!;

    private TextureRect _selIcon = null!;
    private Label _selName = null!;
    private ProgressBar _selHealth = null!;
    private Label _selHealthText = null!;
    private CaptureBar _selCapture = null!;
    private Label _selExtra = null!;
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
    private const int QueueSlotCount = 8;     // 训练队列池大小(选中面板最多显示的槽数)
    private HBoxContainer _queueRow = null!;
    private readonly QueueSlot[] _queueSlots = new QueueSlot[QueueSlotCount];
    private HBoxContainer _commandBox = null!;

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
        _researchPanel.OffsetLeft = -150; _researchPanel.OffsetRight = 150;
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

        // 对齐 C++ TopPanel 右侧(top_panel/MenuButton.xml + IconButtons/*):
        // 从左到右 GameSpeed(100%−284) / Diplomacy / Trade / MatchSettings(28×28 图标,
        // 间距 2) / **Menu 在最右**(100%−164..100%−8,156×28 文字按钮,StoneButtonFancy)。
        var menuBox = new HBoxContainer();
        menuBox.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        menuBox.OffsetLeft = -284; menuBox.OffsetTop = 4;
        menuBox.OffsetRight = -8; menuBox.OffsetBottom = 32;
        menuBox.AddThemeConstantOverride("separation", 2);
        _topBar.AddChild(menuBox);

        AddMenuButton(menuBox, "time_small", "Game Speed", () => _main.OpenGameSpeedPanel());
        AddMenuButton(menuBox, "diplomacy", "Diplomacy", () => _main.OpenDiplomacyPanel());
        AddMenuButton(menuBox, "economics", "Trade", () => _main.OpenTradePanel());
        AddMenuButton(menuBox, "match-settings", "Settings", () => _main.OpenMatchSettingsPanel());

        // 暂停键(原版 PauseControl 顶栏按钮:直接切 sim 冻结,不开菜单叠层;
        // 菜单叠层仍由 Menu/Esc 开)。
        var pauseBtn = new Button
        {
            Text = "❚❚",
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(34, 28),
            TooltipText = "Pause",
        };
        StoneButtonStyle.Apply(pauseBtn, FindBinariesDir());
        pauseBtn.Pressed += () => _main.TogglePause();
        menuBox.AddChild(pauseBtn);

        // 速度 +/-(原版 GameSpeedControl 顶栏步进键):当前档 ±1 档。
        var slowerBtn = new Button
        {
            Text = "−",
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(30, 28),
            TooltipText = "Slower",
        };
        StoneButtonStyle.Apply(slowerBtn, FindBinariesDir());
        slowerBtn.Pressed += () => _main.AdjustGameSpeed(-1);
        menuBox.AddChild(slowerBtn);
        var fasterBtn = new Button
        {
            Text = "+",
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(30, 28),
            TooltipText = "Faster",
        };
        StoneButtonStyle.Apply(fasterBtn, FindBinariesDir());
        fasterBtn.Pressed += () => _main.AdjustGameSpeed(+1);
        menuBox.AddChild(fasterBtn);

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

        AddChild(_bottomBar);
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
        var alertTex = LoadIcon("die");
        if (alertTex != null) _alertBtn.Icon = alertTex;
        _alertBtn.Modulate = new Color(1f, 0.35f, 0.3f);
        _alertBtn.Pressed += () =>
        {
            _main.FocusWorldPosition(_alertX, _alertZ);
            _alertBtn.Visible = false;
        };
        vbox.AddChild(_alertBtn);

        // 快捷易物行(原版 barter_panel 左栏的紧凑版):选资源 + Buy/Sell 100。
        // 原版以物易物仅 town+(market),校验在 BarterSystem 服务端,拒绝会 toast。
        var barterRow = new HBoxContainer();
        barterRow.AddThemeConstantOverride("separation", 4);
        _barterResource = new OptionButton { Theme = UITheme.GetTheme(), CustomMinimumSize = new Vector2(80, 0) };
        foreach (var r in new[] { "wood", "food", "stone", "metal" })
            _barterResource.AddItem(r);
        barterRow.AddChild(_barterResource);
        var buyBtn = new Button { Text = "Buy", Theme = UITheme.GetTheme(), TooltipText = "Buy 100 (barter)" };
        buyBtn.Pressed += () => Barter(buy: true);
        barterRow.AddChild(buyBtn);
        var sellBtn = new Button { Text = "Sell", Theme = UITheme.GetTheme(), TooltipText = "Sell 100 (barter)" };
        sellBtn.Pressed += () => Barter(buy: false);
        barterRow.AddChild(sellBtn);
        vbox.AddChild(barterRow);

        parent.AddChild(panel);
    }

    private OptionButton _barterResource = null!;

    /// <summary>快捷易物(原版 barter_panel):卖所选资源买食物(默认配对;
    /// 完整配比在 Trade 面板)。amount=100(原版快捷档)。</summary>
    private void Barter(bool buy)
    {
        var type = (ZeroAD.Sim.Components.ResourceType)_barterResource.Selected;
        if (buy)
            _main.CommandBarter(ZeroAD.Sim.Components.ResourceType.Food, type, 100);
        else
            _main.CommandBarter(type, ZeroAD.Sim.Components.ResourceType.Food, 100);
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

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        vbox.OffsetLeft = 8; vbox.OffsetTop = 8;
        vbox.OffsetRight = -8; vbox.OffsetBottom = -8;
        panel.AddChild(vbox);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(header);

        _selIcon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(96, 96),
        };
        header.AddChild(_selIcon);

        _selName = new Label { Text = "", VerticalAlignment = VerticalAlignment.Center };
        _selName.AddThemeFontSizeOverride("font_size", 14);
        _selName.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.82f));
        _selName.AddThemeColorOverride("font_outline_color", Colors.Black);
        _selName.AddThemeConstantOverride("outline_size", 3);
        header.AddChild(_selName);

        // 训练队列行(原版 selection_details training queue):池化 8 槽,选中生产建筑时
        // 显示在训单位头像 + 头项进度遮罩。UpdateSelectionPanel 每帧填字段 + QueueRedraw。
        _queueRow = new HBoxContainer { Visible = false };
        _queueRow.AddThemeConstantOverride("separation", 2);
        for (int i = 0; i < _queueSlots.Length; i++)
        {
            _queueSlots[i] = new QueueSlot
            {
                CustomMinimumSize = new Vector2(36, 36),
                Visible = false,
                SlotIndex = i,
                TooltipText = "Click to cancel (full refund)",
            };
            _queueSlots[i].Clicked += idx => _main.CancelProductionAt(idx);
            _queueRow.AddChild(_queueSlots[i]);
        }
        vbox.AddChild(_queueRow);

        var healthRow = new HBoxContainer();
        healthRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(healthRow);

        _selHealth = new ProgressBar
        {
            MinValue = 0, MaxValue = 100, Value = 100,
            CustomMinimumSize = new Vector2(200, 7),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ShowPercentage = false,
        };
        _selHealth.AddThemeStyleboxOverride("background", new StyleBoxFlat
        {
            BgColor = new Color(0.5f, 0, 0, 0.8f),
            BorderColor = new Color(0, 0, 0, 0.5f),
        });
        _selHealth.AddThemeStyleboxOverride("fill", new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.7f, 0.1f),
        });
        healthRow.AddChild(_selHealth);

        _selHealthText = new Label { Text = "", VerticalAlignment = VerticalAlignment.Center };
        _selHealthText.AddThemeFontSizeOverride("font_size", 13);
        _selHealthText.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.82f));
        healthRow.AddChild(_selHealthText);

        // 占领条(原版 selection_details 的 capture bar):选中可占领实体时显示,
        // 分段=各玩家 CP 占比(玩家色,升序确定),tooltip 给数值明细。
        _selCapture = new CaptureBar
        {
            CustomMinimumSize = new Vector2(200, 7),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Visible = false,
            TooltipText = "",
        };
        vbox.AddChild(_selCapture);

        _selExtra = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _selExtra.AddThemeFontSizeOverride("font_size", 13);
        _selExtra.AddThemeColorOverride("font_color", new Color(0.85f, 0.80f, 0.65f));
        vbox.AddChild(_selExtra);

        parent.AddChild(panel);
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

        _commandBox = new HBoxContainer();
        _commandBox.AddThemeConstantOverride("separation", 6);
        _commandBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_commandBox);

        parent.AddChild(panel);

        RebuildCommands();
    }

    private void RebuildCommands()
    {
        foreach (var child in _commandBox.GetChildren())
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

        // Stop/Delete(原版 unit_commands 命令区):Stop 仅己方单位,Delete 任意己方实体。
        if (hasOwnUnit)
            AddCmdButton("stop", "Stop", () => _main.StopSelectedUnits());
        if (hasOwnEntity)
            AddCmdButton("delete", "Delete", () => _main.DeleteSelectedEntities());

        // 命令键行(原版 unit_actions 按钮):Garrison(可驻防单位)/Repair(建造者)/
        // Guard(任意己方单位)/Patrol——进目标模式,下击选目标(与右键分流/热键同路)。
        if (hasOwnUnit)
        {
            bool anyGarrisonable = false, anyBuilder = false;
            foreach (var eid in _main.SelectedEntities)
            {
                if (_sim.Sim.QueryInterface<GarrisonableComponent>(eid) != null) anyGarrisonable = true;
                if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null) anyBuilder = true;
            }
            if (anyGarrisonable)
                AddCmdButton(LoadIcon("garrison"), "Garrison", () => _main.EnterCommandTargetMode("garrison"));
            if (anyBuilder)
                AddCmdButton(LoadIcon("repair"), "Repair", () => _main.EnterCommandTargetMode("repair"));
            AddCmdButton(LoadIcon("add-guard"), "Guard", () => _main.EnterCommandTargetMode("guard"));
            AddCmdButton(LoadIcon("patrol"), "Patrol", () => _main.EnterCommandTargetMode("patrol"));

            // 打包栏(原版 pack_panel):选中含可打包/解包攻城器时显示对应按钮。
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
                    foreach (var raw in rstats.ResearchableTechnologies.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
                    {
                        string tech = raw;
                        if (rstats.Civ.Length > 0) tech = tech.Replace("{native}", rstats.Civ);
                        if (ownerCiv.Length > 0) tech = tech.Replace("{civ}", ownerCiv);
                        if (tech.Contains('{')) continue;
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
        AddCmdButton(tex, text, () => _main.TrainUnit(t, Input.IsKeyPressed(Key.Shift)));
    }

    /// <summary>研究按钮(数据驱动,research_panel):图标=JSON icon 字段的原版立绘
    /// (portraits/technologies/),标签=GenericName+资源费;已研究跳过,前置未满足置灰禁用
    /// (与服务端 CanResearch 同判定)。</summary>
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

        string label = def.GenericName;
        var costs = new List<string>();
        if (def.Food > 0) costs.Add($"{def.Food}F");
        if (def.Wood > 0) costs.Add($"{def.Wood}W");
        if (def.Stone > 0) costs.Add($"{def.Stone}S");
        if (def.Metal > 0) costs.Add($"{def.Metal}M");
        string text = costs.Count > 0 ? $"{label}\n{string.Join(' ', costs)}" : label;

        var tex = def.Icon.Length > 0 ? LoadPortraitFromIcon("technologies/" + def.Icon) : null;
        bool canResearch = tm.CanResearch(tech);
        string t = tech;
        var btn = AddCmdButton(tex, text, () => _main.ResearchTech(t), canResearch);
        if (!canResearch) btn.Modulate = new Color(1f, 1f, 1f, 0.45f);
    }

    /// <summary>建造按钮(数据驱动,construction_panel):头像取模板 Identity/Icon 原版立绘,
    /// 标签=GenericName+资源费;点击进入建造放置模式(完整模板名,文明已解析)。</summary>
    private void AddBuildButton(string template)
    {
        var stats = _sim.Sim.Templates?.ExtractStats(template);
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

        if (tex != null)
        {
            btn.Icon = tex;
            btn.ExpandIcon = true;
            btn.IconAlignment = HorizontalAlignment.Center;
            btn.VerticalIconAlignment = VerticalAlignment.Top;
        }

        btn.TooltipText = text.Replace("\n", " ");
        btn.Pressed += onPressed;

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
        // 研究进度条刷新(任一己方在研建筑 → 图标+名+进度)。
        RefreshResearchProgress();

        var player = _sim.GetPlayer();
        if (player != null)
        {
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

        var selected = _main.SelectedEntities;
        if (!SelectionEqual(selected, _lastSelection))
        {
            _lastSelection = new HashSet<EntityId>(selected);
            RebuildCommands();
        }

        UpdateSelectionPanel(selected);
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
            _selName.Text = "";
            _selHealth.Value = 0;
            _selHealthText.Text = "";
            _selCapture.Visible = false;
            _selExtra.Text = "";
            _queueRow.Visible = false;
            _selGarrison.Text = "";
            _garrisonRow.Visible = false;
            _garrisonSignature = "";
            _selIcon.Texture = null;
            RefreshStanceHighlight();
            return;
        }

        EntityId first = default;
        foreach (var e in selected) { first = e; break; }

        var identity = _sim.Sim.QueryInterface<IdentityComponent>(first);
        var health = _sim.Sim.QueryInterface<HealthComponent>(first);

        string name = identity?.Name ?? "Entity";
        _selName.Text = selected.Count > 1 ? $"{name} (+{selected.Count - 1})" : name;

        if (health != null && health.Max > 0)
        {
            _selHealth.Value = 100.0 * health.Current / health.Max;
            _selHealthText.Text = $"{health.Current}/{health.Max}";
        }
        else
        {
            _selHealth.Value = 100;
            _selHealthText.Text = "";
        }

        // 占领条:仅可占领实体(Capturable)显示;分段宽=CP/max,玩家色,升序确定。
        var capturable = _sim.Sim.QueryInterface<CapturableComponent>(first);
        float maxCp = capturable?.MaxCapturePoints.ToFloat() ?? 0f;
        if (capturable != null && maxCp > 0f)
        {
            int n = System.Math.Min(capturable.CapturePoints.Length, CaptureBar.MaxPlayers);
            var sb = new System.Text.StringBuilder("Capture");
            for (int p = 0; p < n; p++)
            {
                float cp = capturable.CapturePoints[p].ToFloat();
                _selCapture.Fractions[p] = cp / maxCp;
                if (cp > 0f) sb.Append($"  P{p}:{(int)cp}");
            }
            _selCapture.Count = n;
            _selCapture.TooltipText = sb.Append($"/{(int)maxCp}").ToString();
            _selCapture.Visible = true;
            _selCapture.QueueRedraw();
        }
        else
        {
            _selCapture.Visible = false;
        }

        var supply = _sim.Sim.QueryInterface<ResourceSupply>(first);
        if (supply != null && supply.Amount > 0)
            _selExtra.Text = $"Resources: {supply.Amount}";
        else if (identity != null)
            _selExtra.Text = identity.IsBuilding ? "Building" : identity.IsUnit ? "Unit" : "";
        else
            _selExtra.Text = "";

        // 训练队列(仅生产建筑有非空 ProductionQueue 时显示):头项画进度遮罩,余者待训压暗。
        var queue = _sim.Sim.QueryInterface<ProductionQueue>(first);
        if (queue != null && queue.QueueCount > 0)
        {
            int n = System.Math.Min(queue.QueueCount, _queueSlots.Length);
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
            _queueRow.Visible = true;
        }
        else
        {
            _queueRow.Visible = false;
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

        _selIcon.Texture = LoadPortraitForIdentity(identity);
        RefreshStanceHighlight();
        RefreshFormationRow();
    }

    /// <summary>阵型行(原版 formation_panel):编队控制器选中 → 只显 null(解散);
    /// ≥2 同主可编队单位 → 按首个单位模板 UnitAI/Formations 列出阵型图标。
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

        string sig = hasController ? "ctrl" : string.Join(',', ownUnits.Select(u => u.Value));
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

        if (ownUnits.Count < 2)
        {
            _formationRow.Visible = false;
            return;
        }

        // 首个单位的可编队形列表(模板 UnitAI/Formations;strip special/formations/ 前缀)
        var identity = _sim.Sim.QueryInterface<IdentityComponent>(ownUnits[0]);
        var stats = identity != null ? _sim.Sim.Templates?.ExtractStats(identity.TemplateName) : null;
        if (stats == null || stats.FormationShapes.Length == 0)
        {
            _formationRow.Visible = false;
            return;
        }

        foreach (var raw in stats.FormationShapes.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
        {
            string shape = raw.Replace("special/formations/", "");
            var btn = MakeSmallIconButton(LoadIcon($"formations/{shape}"), $"Formation: {shape}");
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
