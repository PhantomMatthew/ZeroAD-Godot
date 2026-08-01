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
        _bottomBar = new Panel();
        _bottomBar.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _bottomBar.OffsetTop = -204;
        _bottomBar.CustomMinimumSize = new Vector2(0, 204);
        // Transparent base — C++ draws no full-bar background, each zone has its own frame.
        _bottomBar.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) });

        var hbox = new HBoxContainer();
        hbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        hbox.OffsetLeft = 4; hbox.OffsetTop = 4;
        hbox.OffsetRight = -4; hbox.OffsetBottom = -4;
        hbox.AddThemeConstantOverride("separation", 4);
        _bottomBar.AddChild(hbox);

        SetupMinimapZone(hbox);
        SetupSupplementalZone(hbox);
        SetupSelectionZone(hbox);
        SetupCommandZone(hbox);

        AddChild(_bottomBar);
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

    private void SetupMinimapZone(HBoxContainer parent)
    {
        var frame = new Control { CustomMinimumSize = new Vector2(200, 200) };
        frame.SizeFlagsHorizontal = Control.SizeFlags.Fill;
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

        parent.AddChild(frame);
    }

    /// <summary>Supplemental panel (C++ "selection_panels_left"): stance buttons,
    /// garrison count, and formation placeholder.</summary>
    private void SetupSupplementalZone(HBoxContainer parent)
    {
        var panel = new Control
        {
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            CustomMinimumSize = new Vector2(180, 0),
        };
        AddBorderFrame(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        vbox.OffsetLeft = 8; vbox.OffsetTop = 8;
        vbox.OffsetRight = -8; vbox.OffsetBottom = -8;
        panel.AddChild(vbox);

        // Stance row: 5 stance icons (violent/aggressive/defensive/passive/standground).
        // Placeholder — clicking changes selection highlight only (no sim wiring yet).
        var stanceLabel = new Label { Text = "Stance" };
        stanceLabel.AddThemeFontSizeOverride("font_size", 11);
        stanceLabel.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.82f));
        stanceLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        stanceLabel.AddThemeConstantOverride("outline_size", 2);
        vbox.AddChild(stanceLabel);

        var stanceRow = new HBoxContainer();
        stanceRow.AddThemeConstantOverride("separation", 2);
        vbox.AddChild(stanceRow);
        foreach (var stance in new[] { "violent", "aggressive", "defensive", "passive", "standground" })
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
            stanceRow.AddChild(btn);
        }

        // Garrison indicator (placeholder count).
        _selGarrison = new Label { Text = "" };
        _selGarrison.AddThemeFontSizeOverride("font_size", 12);
        _selGarrison.AddThemeColorOverride("font_color", new Color(0.85f, 0.80f, 0.65f));
        _selGarrison.AddThemeColorOverride("font_outline_color", Colors.Black);
        _selGarrison.AddThemeConstantOverride("outline_size", 2);
        vbox.AddChild(_selGarrison);

        parent.AddChild(panel);
    }

    private void SetupSelectionZone(HBoxContainer parent)
    {
        // C++ selectionDetailsPanel is a fixed ~228px wide panel. Fill (not Expand)
        // so the Commands zone gets the remaining horizontal space.
        var panel = new Control
        {
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            CustomMinimumSize = new Vector2(228, 0),
        };

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
            };
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

    private void SetupCommandZone(HBoxContainer parent)
    {
        // C++ unitCommandsPanel is the widest zone (~402px). ExpandFill so it
        // claims remaining horizontal space after the minimap/supplemental/selection
        // fixed-width zones are laid out.
        var panel = new Control
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(300, 0),
        };
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
        bool hasArsenal = false;
        var researcherTemplates = new HashSet<string>();
        foreach (var eid in _main.SelectedEntities)
        {
            if (_sim.Sim.QueryInterface<BuilderComponent>(eid) != null) hasBuilder = true;
            if (_sim.Sim.QueryInterface<ProductionQueue>(eid) != null) hasProducer = true;
            var identity = _sim.Sim.QueryInterface<IdentityComponent>(eid);
            if (identity is null) continue;
            if (_sim.Sim.QueryInterface<ResearcherComponent>(eid) != null)
                researcherTemplates.Add(identity.TemplateName);
            if (identity.TemplateName.Contains("arsenal")) hasArsenal = true;
        }

        if (hasProducer)
        {
            AddCmdButton("support_civilian", "Villager\n50F", () => _main.TrainVillager(Input.IsKeyPressed(Key.Shift)));
            AddCmdButton("infantry_spearman", "Soldier\n80F 20M", () => _main.TrainSoldier(Input.IsKeyPressed(Key.Shift)));
            if (_main.IsTutorial)
                AddCmdButton("infantry_javelinist", "Skirmisher\n70F 30W", () => _main.TrainSkirmisher(true));
        }

        if (hasBuilder)
        {
            AddCmdButton("house", "House\n30W", () => _main.EnterBuildMode("House"));
            AddCmdButton("storehouse", "Storehouse\n80W", () => _main.EnterBuildMode("Storehouse"));
            AddCmdButton("farmstead", "Farmstead\n80W", () => _main.EnterBuildMode("Farmstead"));
            AddCmdButton("field", "Field\n60W", () => _main.EnterBuildMode("Field"));
            AddCmdButton("barracks", "Barracks\n100W", () => _main.EnterBuildMode("Barracks"));

            if (_main.IsTutorial)
            {
                AddCmdButton("outpost", "Outpost\n80W", () => _main.EnterBuildMode("Outpost"));
                AddCmdButton("defense_tower", "Tower\n100W\n50S", () => _main.EnterBuildMode("Tower"));
                AddCmdButton("blacksmith", "Forge\n120W\n30M", () => _main.EnterBuildMode("Forge"));
                AddCmdButton("market", "Market\n100W", () => _main.EnterBuildMode("Market"));
                AddCmdButton("temple", "Temple\n150W\n50S", () => _main.EnterBuildMode("Temple"));
                AddCmdButton("arsenal", "Arsenal\n150W\n50M", () => _main.EnterBuildMode("Arsenal"));
            }
        }

        foreach (var template in researcherTemplates)
        {
            if (template.Contains("civil_centre") || template.Contains("civic_centre"))
            {
                AddCmdButton("phase_town", "Town Phase\n150W\n100M", () => _main.ResearchTech("phase_town_generic"));
                AddCmdButton("phase_city", "City Phase\n300W\n200M", () => _main.ResearchTech("phase_city_generic"));
            }
            else if (template.Contains("forge") || template.Contains("blacksmith"))
            {
                AddCmdButton("infantry_attack", "Infantry\nTraining", () => _main.ResearchTech("infantry_attack"));
            }
        }

        if (hasArsenal)
            AddCmdButton("siege_ram", "Battering\nRam\n200W\n50M", () => _main.TrainUnit("units/spart/siege_ram", Input.IsKeyPressed(Key.Shift)));

        if (!hasBuilder && !hasProducer && researcherTemplates.Count == 0 && !hasArsenal)
        {
            var hint = new Label { Text = "Select a unit or building" };
            hint.AddThemeColorOverride("font_color", new Color(0.85f, 0.80f, 0.65f));
            hint.AddThemeFontSizeOverride("font_size", 13);
            _commandBox.AddChild(hint);
        }
    }

    private static readonly Dictionary<string, string> PortraitMap = new()
    {
        ["support_civilian"] = "portraits/units/support_civilian.png",
        ["infantry_spearman"] = "portraits/units/infantry_spearman.png",
        ["infantry_javelinist"] = "portraits/units/infantry_javelinist.png",
        ["siege_ram"] = "portraits/units/siege_ram.png",
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

    private void AddCmdButton(string iconKey, string text, System.Action onPressed)
    {
        var btn = new Button
        {
            Text = "",
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(46, 46),
        };

        if (PortraitMap.TryGetValue(iconKey, out var iconPath))
        {
            var tex = LoadTex(iconPath);
            if (tex != null)
            {
                btn.Icon = tex;
                btn.ExpandIcon = true;
                btn.IconAlignment = HorizontalAlignment.Center;
                btn.VerticalIconAlignment = VerticalAlignment.Top;
            }
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
        vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 24) });
        vbox.AddChild(label);

        _commandBox.AddChild(btn);
    }

    private record struct ResourceCounter(Control Root, Label Count, Label Stats);

    private IReadOnlySet<EntityId> _lastSelection = new HashSet<EntityId>();

    public override void _Process(double delta)
    {
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
            _selIcon.Texture = null;
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

        // 驻军数(填既有空标签 _selGarrison)。
        var holder = _sim.Sim.QueryInterface<GarrisonHolderComponent>(first);
        _selGarrison.Text = holder != null && holder.Entities.Count > 0
            ? $"Garrison: {holder.Entities.Count}/{holder.GetCapacity(_sim.Sim)}" : "";

        _selIcon.Texture = LoadPortraitForIdentity(identity);
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
