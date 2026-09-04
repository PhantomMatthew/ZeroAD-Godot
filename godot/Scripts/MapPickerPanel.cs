using Godot;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ZeroAD.Sim.Net;

namespace ZeroAD.Godot;

/// <summary>对局设置面板(SP "Matches" 入口;布局/样式对齐原版 gamesetup GameSetupPage):
/// 左列 = 玩家面板(整行玩家色背景,PlayersPanel.xml 同款)+ 地图描述;右列 = 地图预览 +
/// 设置选项卡(Map/Player/Game Type)+ 右下 Cancel/Start Game!(bottomRightPanel + StoneButton)。
/// 地图浏览是设置页内覆盖页(原版 MapBrowserPage)。槽位数:skirmish/scenario 取 ScriptSettings.
/// PlayerData,random 1-8 可选(原版 g_MaxPlayers=8)。OnStart(MapEntry, seed, slots) +
/// WriteOptions(cfg) 把 gamesetup 选项写进 GameLaunchConfig。</summary>
public sealed partial class MapPickerPanel : Panel
{
    public event System.Action<MapEntry, uint, IReadOnlyList<PlayerSlotSetup>>? OnStart;
    public event System.Action? OnCancelled;

    private readonly List<MapEntry> _maps;
    private readonly string? _dataRoot;
    private List<MapEntry> _filtered = new();
    private ItemList _list = null!;
    private GridContainer _gridContainer = null!;
    private Label _nameLabel = null!;
    private Label _descLabel = null!;
    private TextureRect _preview = null!;
    private Button _startBtn = null!;

    // ── Map 页签 ──
    private OptionButton _mapTypeOpt = null!;
    private OptionButton _mapSelectOpt = null!;
    private OptionButton _mapSizeOpt = null!;
    private OptionButton _placementOpt = null!;
    /// <summary>布置下拉 index → pattern id(0 = "random" 元选项;随图重建,见 RebuildPlacements)。</summary>
    private readonly System.Collections.Generic.List<string> _placementIds = new();
    private Label _statusLine = null!;
    private OptionButton _biomeOpt = null!;
    private Control _biomeRow = null!;
    private readonly List<string> _biomeIds = new();
    private CheckBox _nomadBox = null!;
    private CheckBox _treasuresBox = null!;
    private CheckBox _exploredBox = null!;
    private CheckBox _revealedBox = null!;
    private CheckBox _alliedViewBox = null!;

    // ── Player 页签 ──
    private OptionButton _playerCountOpt = null!;
    private OptionButton _popCapTypeOpt = null!;
    private HSlider _popCapSlider = null!;
    private Label _popCapValue = null!;
    private OptionButton _startResOpt = null!;
    private CheckBox _spiesBox = null!;
    private CheckBox _cheatsBox = null!;

    // ── Game Type 页签 ──
    private readonly Dictionary<string, CheckBox> _victoryBoxes = new();
    private OptionButton _gameSpeedOpt = null!;
    private HSlider _ceasefireSlider = null!;
    private Label _ceasefireValue = null!;
    private CheckBox _lockedTeamsBox = null!;
    private CheckBox _lastManBox = null!;

    private VBoxContainer _slotRows = null!;
    private PanelContainer _browser = null!;
    private MapEntry? _selected;
    private Control[] _tabPages = System.Array.Empty<Control>();
    private PanelContainer _pageHost = null!;
    private VerticalTabStrip _tabStrip = null!;

    // 每行的控件(kind/civ/team/diff/behavior),索引 = 行号。
    private static readonly string[] AiDifficulties =
        { "Sandbox", "Very Easy", "Easy", "Medium", "Hard", "Very Hard" };
    private static readonly string[] AiBehaviors = { "Random", "Aggressive", "Balanced", "Defensive" };
    private readonly List<OptionButton> _diffOpts = new();
    private readonly List<OptionButton> _behaviorOpts = new();
    private readonly List<OptionButton> _kindOpts = new();
    private readonly List<OptionButton> _civOpts = new();
    private readonly List<OptionButton> _teamOpts = new();

    // 15 文明(simulation/data/civs/*.json)——显示名对齐原版 gamesetup。
    private static readonly (string Code, string Name)[] Civs =
    {
        ("athen", "Athenians"), ("brit", "Britons"), ("cart", "Carthaginians"),
        ("gaul", "Gauls"), ("germ", "Germans"), ("han", "Han"), ("iber", "Iberians"),
        ("kush", "Kushites"), ("mace", "Macedonians"), ("maur", "Mauryas"),
        ("ptol", "Ptolemies"), ("rome", "Romans"), ("sele", "Seleucids"),
        ("spart", "Spartans"), ("achae", "Achaemenids"),
    };

    // 原版 player_defaults.json 的逐槽默认文明(athen/cart/gaul/iber…),不是全 Random。
    private static readonly string[] DefaultCivs = { "athen", "cart", "gaul", "iber" };

    // 原版 map_sizes.json(Tiles 即尺寸;Normal=256 默认)。
    private static readonly (string Name, int Tiles)[] MapSizes =
    {
        ("Tiny", 128), ("Small", 192), ("Normal", 256), ("Medium", 320),
        ("Large", 384), ("Very Large", 448), ("Giant", 512),
    };
    private const int DefaultMapSizeIndex = 2;   // Normal

    // 原版 player_placements.json。
    private static readonly (string Id, string Name)[] Placements =
    {
        ("circle", "Circle"), ("river", "River"), ("groupedLines", "Grouped Lines"),
        ("randomGroup", "Random Group"), ("stronghold", "Stronghold"),
    };

    // 原版 population_capacities.json(Type)与滑条公式(linearToLogarythmic)。
    private static readonly (string Id, string Title, int Factor)[] PopCapTypes =
    {
        ("player", "Player Population", 300),
        ("team", "Team Population", 400),
        ("world", "World Population", 600),
    };

    // 原版 starting_resources.json(Low=300 默认)。
    private static readonly (string Name, int Amount)[] StartResources =
    {
        ("Very Low", 100), ("Low", 300), ("Medium", 500),
        ("High", 1000), ("Very High", 3000), ("Deathmatch", 50000),
    };
    private const int DefaultStartResIndex = 1;   // Low

    // 原版 game_speeds.json(1.0 默认)。
    private static readonly float[] GameSpeeds =
        { 0.1f, 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 5f, 10f, 20f };
    private const int DefaultGameSpeedIndex = 4;   // 1.0

    // 原版 victory_conditions/*(默认 conquest 单选;wonder 等可叠加)。
    private static readonly (string Id, string Name, bool Default)[] VictoryChoices =
    {
        ("conquest", "Conquest", true),
        ("wonder", "Wonder Victory", false),
        ("capture_the_relic", "Capture the Relic", false),
        ("regicide", "Regicide", false),
        ("conquest_civic_centers", "Conquest Civic Centers", false),
        ("conquest_structures", "Conquest Structures", false),
        ("conquest_units", "Conquest Units", false),
    };

    // 原版 gamesetup 玩家行底色:玩家色暗化(实测截图 P1 亮蓝 ×0.40 ≈ 行底深蓝;
    // 100% 原色太刺眼,C++ 观感是深色可辨色相的行)。colors = player_defaults.json。
    private static Color Darkened(byte r, byte g, byte b) =>
        new(r / 255f * 0.45f, g / 255f * 0.45f, b / 255f * 0.45f);

    private static readonly Color[] PlayerRowColors =
    {
        Darkened(21, 55, 149),    // P1 blue
        Darkened(150, 20, 20),    // P2 red
        Darkened(86, 180, 31),    // P3 green
        Darkened(231, 200, 5),    // P4 yellow
        Darkened(150, 20, 150),   // P5 purple
        Darkened(20, 160, 200),   // P6 cyan
        Darkened(230, 120, 20),   // P7 orange
        Darkened(200, 80, 120),   // P8 pink
    };

    /// <summary>强制图模式(战役 useGameSetup 分支):只列一张图且不可换,
    /// 其余 gamesetup 选项照常;null = 自由选图。</summary>
    public string? ForcedMapId { get; set; }
    /// <summary>页头附加标题(战役名——原版 gamesetup 的战役上下文)。</summary>
    public string TitleSuffix { get; set; } = "";

    public MapPickerPanel(List<MapEntry> maps, string? dataRoot)
    {
        _maps = maps;
        _dataRoot = dataRoot;
    }

    public override void _Ready()
    {
        Theme = UITheme.GetTheme();
        if (ForcedMapId != null)
        {
            // 战役强制图:隐藏地图列表/类型切换(gamesetup 的图锁语义——原版
            // lockSettings.map;选项页照常可调)。
            CallDeferred(nameof(DisableMapBrowsing));
        }
        // 近全屏(原版 gamesetup 就是全屏页):2%-3% 边距,随窗口缩放。
        AnchorLeft = 0.02f; AnchorRight = 0.98f; AnchorTop = 0.03f; AnchorBottom = 0.97f;
        OffsetLeft = 0; OffsetRight = 0; OffsetTop = 0; OffsetBottom = 0;

        // 整页滚动兜底:窗口不足(原版要求 ≥1024×768)时设置页内部滚动。底栏
        // 钉在面板外(原版 centerPanel 底边 100%-64,按钮不进滚动区)。
        var scroll = new ScrollContainer();
        var vbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(vbox);
        AddChild(scroll);
        scroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        scroll.OffsetBottom = -40;

        var title = new Label { Text = Localization.Tr("Match Setup"), HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 22);
        vbox.AddChild(title);

        // ── 主体两列(对齐原版:左 = 玩家面板+描述;右 = 预览+设置选项卡)──
        var cols = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        cols.AddThemeConstantOverride("separation", 16);
        vbox.AddChild(cols);

        cols.AddChild(BuildLeftColumn());
        cols.AddChild(BuildRightColumn());

        BuildBottomBar();
        BuildBrowser();

        Refill();
        if (_filtered.Count > 0)
            Select(_filtered[0]);
    }

    /// <summary>按所选图重建布置下拉(上游 gamesetup PlayerPlacement.js:可选项 =
    /// 图 JSON 的 PlayerPlacements + 首项 Random 元选项;图不声明 → 整排禁用)。
    /// 此前 84 张图全量提供 5 种布置,coast_range 这类"仅限 2 队"的图可被选死。</summary>
    private void RebuildPlacements(MapEntry? m)
    {
        _placementOpt.Clear();
        _placementIds.Clear();
        var declared = m?.MapType == "random" ? m.PlacementIds : null;
        if (declared == null || declared.Count == 0)
        {
            _placementOpt.Disabled = true;
            _placementOpt.TooltipText = "This map does not offer placement patterns.";
            return;
        }
        _placementOpt.Disabled = false;
        _placementOpt.TooltipText = "How players are placed on the map.";
        // Random 元选项(上游每个支持布置的图都有):开局时从图声明集里等概率摇一个。
        _placementOpt.AddItem(Localization.Tr("Random"));
        _placementIds.Add("random");
        foreach (var id in declared)
        {
            string display = Placements.FirstOrDefault(p => p.Id == id).Name ?? id;
            _placementOpt.AddItem(Localization.Tr(display));
            _placementIds.Add(id);
        }
        _placementOpt.Selected = 0;
    }

    /// <summary>下拉选择 → 具体 pattern id;"random" 元选项当场摇成具体值
    /// (菜单侧随机,cfg 携带解析后的具体 id 下发,MP 广播/回放不含二次随机)。</summary>
    private string ResolvePlacement()
    {
        if (_placementOpt.Selected < 0 || _placementOpt.Selected >= _placementIds.Count)
            return "";
        string id = _placementIds[_placementOpt.Selected];
        if (id != "random") return id;
        int idx = (int)GD.RandRange(1, _placementIds.Count - 1);
        return _placementIds[idx];
    }

    /// <summary>把 gamesetup 选项写进 GameLaunchConfig(MainMenu 的 OnStart 里调用)。</summary>
    public void WriteOptions(GameLaunchConfig cfg)
    {
        bool isRandom = _selected?.MapType == "random";
        cfg.MapSize = isRandom ? MapSizes[_mapSizeOpt.Selected].Tiles : 0;
        cfg.BiomeId = isRandom && _biomeRow.Visible && _biomeOpt.Selected > 0
            ? _biomeIds[_biomeOpt.Selected]
            : "";
        cfg.PlayerPlacement = isRandom && !_placementOpt.Disabled ? ResolvePlacement() : "";
        cfg.StartingResources = StartResources[_startResOpt.Selected].Amount;
        cfg.PopulationCap = ReadPopCap();
        cfg.GameSpeed = GameSpeeds[_gameSpeedOpt.Selected];
        cfg.CeasefireMinutes = (int)_ceasefireSlider.Value;
        cfg.Nomad = isRandom && _nomadBox.ButtonPressed;
        cfg.Treasures = _treasuresBox.ButtonPressed;
        cfg.ExploredMap = _exploredBox.ButtonPressed;
        cfg.RevealedMap = _revealedBox.ButtonPressed;
        cfg.AlliedView = _alliedViewBox.ButtonPressed;
        cfg.LockedTeams = _lockedTeamsBox.ButtonPressed;
        cfg.Cheats = _cheatsBox.ButtonPressed;
        cfg.VictoryConditions = _victoryBoxes
            .Where(kv => kv.Value.ButtonPressed).Select(kv => kv.Key).ToList();
    }

    private int ReadPopCap()
    {
        double v = _popCapSlider.Value;
        if (v >= 0.995) return 0;   // 滑到最右 = Unlimited(0 表示不改,用模板默认)
        // 原版 linearToLogarythmic:round((1/(1-v) + 28v/(1+5v)) * Factor/6),再 10 取整
        double factor = PopCapTypes[_popCapTypeOpt.Selected].Factor;
        return (int)System.Math.Round((1 / (1 - v) + 28 * v / (1 + 5 * v)) * factor / 6 / 10) * 10;
    }

    /// <summary>原版 GameSetupPage.xml bottomRightPanel:右下 314×32,Cancel 0..140,
    /// Start Game! 150..290,均 StoneButton。钉在面板上,不进 ScrollContainer。</summary>
    private void BuildBottomBar()
    {
        var bar = new Control { MouseFilter = MouseFilterEnum.Ignore };
        bar.SetAnchorsPreset(LayoutPreset.BottomRight);
        bar.OffsetLeft = -314;
        bar.OffsetTop = -32;
        bar.OffsetRight = 0;
        bar.OffsetBottom = 0;
        AddChild(bar);

        var cancelBtn = MakeStoneBarButton(Localization.Tr("Cancel"), 140);
        cancelBtn.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
        cancelBtn.OffsetLeft = 0;
        cancelBtn.OffsetTop = 0;
        cancelBtn.OffsetRight = 140;
        cancelBtn.OffsetBottom = 32;
        cancelBtn.TooltipText = Localization.Tr("Return to the main menu.");
        cancelBtn.Pressed += () => OnCancelled?.Invoke();
        bar.AddChild(cancelBtn);

        _startBtn = MakeStoneBarButton(Localization.Tr("Start Game!"), 140);
        _startBtn.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
        _startBtn.OffsetLeft = 150;
        _startBtn.OffsetTop = 0;
        _startBtn.OffsetRight = 290;
        _startBtn.OffsetBottom = 32;
        _startBtn.TooltipText = "Start a new game with the current settings.";
        _startBtn.Pressed += () =>
        {
            if (_selected == null) return;
            // 原版 gamesetup 无种子 UI——每局随机摇(菜单侧随机;sim 种子由此下发)。
            uint seed = (uint)GD.RandRange(0, 999999);
            var slots = BuildSlots();
            // 此前 slots==null 静默无反应("Start 按了没动静"投诉源);给状态行说明。
            if (slots == null)
            {
                _statusLine.Text = Localization.Tr(
                    "Need exactly one Human player slot to start.");
                return;
            }
            _statusLine.Text = "";
            OnStart?.Invoke(_selected, seed, slots);
        };
        bar.AddChild(_startBtn);

        // 状态行(Start 拒绝原因等):底栏上方一行,右对齐,错误红。
        _statusLine = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Right,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _statusLine.SetAnchorsPreset(LayoutPreset.BottomRight);
        _statusLine.OffsetLeft = -620;
        _statusLine.OffsetTop = -64;
        _statusLine.OffsetRight = -24;
        _statusLine.OffsetBottom = -36;
        _statusLine.AddThemeColorOverride("font_color", new Color(0.95f, 0.55f, 0.45f));
        _statusLine.AddThemeFontSizeOverride("font_size", 13);
        AddChild(_statusLine);
    }

    private static Button MakeStoneBarButton(string caption, float width)
    {
        var btn = new Button
        {
            Text = caption,
            CustomMinimumSize = new Vector2(width, 32),
        };
        StoneButtonStyle.Apply(btn, StoneButtonStyle.FindBinariesDir());
        return btn;
    }

    /// <summary>左列:玩家面板(上,吃满高度)+ 地图名与描述(下)。</summary>
    private Control BuildLeftColumn()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 8);

        // 玩家面板(原版 ModernDarkBoxGold 底 + 金边),占满左列高度
        var playersBox = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        playersBox.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.13f, 0.12f, 0.11f),
            BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = new Color(0.72f, 0.60f, 0.35f),
            ContentMarginTop = 6, ContentMarginBottom = 6,
            ContentMarginLeft = 6, ContentMarginRight = 6,
        });
        col.AddChild(playersBox);

        var playersInner = new VBoxContainer();
        playersInner.AddThemeConstantOverride("separation", 4);
        playersBox.AddChild(playersInner);

        var headerLabel = new Label
        {
            Text = Localization.Tr("Players"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerLabel.AddThemeFontSizeOverride("font_size", 16);
        playersInner.AddChild(headerLabel);

        // 列标题行(原版 PlayersPanel.xml 顶部 heading:黑底灰白小字)
        var gridHeader = new PanelContainer();
        gridHeader.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.04f, 0.04f),
            ContentMarginTop = 3, ContentMarginBottom = 3,
            ContentMarginLeft = 8, ContentMarginRight = 8,
        });
        var headRow = new HBoxContainer();
        headRow.AddThemeConstantOverride("separation", 8);
        gridHeader.AddChild(headRow);
        void AddHead(string text, float minW)
        {
            var l = new Label
            {
                Text = Localization.Tr(text),
                CustomMinimumSize = new Vector2(minW, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            l.AddThemeFontSizeOverride("font_size", 11);
            l.Modulate = new Color(1, 1, 1, 0.6f);
            headRow.AddChild(l);
        }
        AddHead("Player Name", 130);
        AddHead("Player Placement", 110);
        AddHead("Civilization", 150);
        AddHead("Team", 60);
        playersInner.AddChild(gridHeader);

        _slotRows = new VBoxContainer();
        _slotRows.AddThemeConstantOverride("separation", 2);
        playersInner.AddChild(_slotRows);
        return col;
    }

    /// <summary>右列:地图预览(上)+ 设置选项卡(中)+ 描述(下)。宽 ~400(原版 402px)。</summary>
    private Control BuildRightColumn()
    {
        var col = new VBoxContainer
        {
            // 右列加宽:内容区(原 TabContainer 页) + 右侧 150px 纵向页签条。
            CustomMinimumSize = new Vector2(560, 0),
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
        };
        col.AddThemeConstantOverride("separation", 8);

        // 地图预览(原版右上角 402px 宽框,带黑边)
        var frame = new PanelContainer();
        frame.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.02f, 0.02f, 0.02f),
            BorderWidthTop = 2, BorderWidthBottom = 2, BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderColor = new Color(0, 0, 0),
            ContentMarginTop = 6, ContentMarginBottom = 6,
            ContentMarginLeft = 6, ContentMarginRight = 6,
        });
        _preview = new TextureRect
        {
            CustomMinimumSize = new Vector2(360, 230),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        frame.AddChild(_preview);
        col.AddChild(frame);

        // 设置区(原版 GameSettingsPanel+GameSettingsTabs:内容在左,纵向页签条
        // 贴右缘——A28 gamesetup 的 centerRightPanel 布局;页签"贴图"见 VerticalTabStrip)。
        _tabPages = new Control[] { BuildMapTab(), BuildPlayerTab(), BuildGameTypeTab() };
        var settingsRow = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        settingsRow.AddThemeConstantOverride("separation", 8);
        // PanelContainer 宿主:只排可见页,最小尺寸随当前页(裸 Control 宿主最小尺寸为 0,
        // 会把外层 ScrollContainer 的整页布局压塌)。
        _pageHost = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _pageHost.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        for (int i = 0; i < _tabPages.Length; i++)
        {
            _tabPages[i].Visible = i == 0;
            _pageHost.AddChild(_tabPages[i]);
        }
        settingsRow.AddChild(_pageHost);
        _tabStrip = new VerticalTabStrip(new[]
            { Localization.Tr("Map"), Localization.Tr("Player"), Localization.Tr("Game Type") })
        {
            CustomMinimumSize = new Vector2(150, 0),
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
        };
        _tabStrip.TabSelected += idx =>
        {
            for (int i = 0; i < _tabPages.Length; i++)
                _tabPages[i].Visible = i == idx;
        };
        settingsRow.AddChild(_tabStrip);
        col.AddChild(settingsRow);

        // 地图描述(原版 GameDescription:右列设置列表下方,白字多行)
        _nameLabel = new Label { Text = "" };
        _nameLabel.AddThemeFontSizeOverride("font_size", 14);
        col.AddChild(_nameLabel);
        _descLabel = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _descLabel.AddThemeFontSizeOverride("font_size", 12);
        col.AddChild(_descLabel);

        return col;
    }

    /// <summary>dev 截图钩子:切到指定页签(ZEROAD_MATCH_TAB=0/1/2)。</summary>
    public void DevSelectTab(int idx) => _tabStrip.Select(idx);

    private void DisableMapBrowsing()
    {
        if (_browser != null) _browser.Visible = false;
    }

    // ══════════ Map 页签(原版 GameSettingsLayout 第一段)══════════
    private Control BuildMapTab()
    {
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 4);

        _mapTypeOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var f in new[] { "Random", "Skirmish", "Scenario" })
            _mapTypeOpt.AddItem(Localization.Tr(f));
        _mapTypeOpt.Selected = 0;
        _mapTypeOpt.TooltipText = "Select a map type.";
        _mapTypeOpt.ItemSelected += _ =>
        {
            Refill();
            if (_filtered.Count > 0) Select(_filtered[0]);
        };
        page.AddChild(MakeSettingRow("Map Type", _mapTypeOpt));

        // 地图选择下拉框(原版 MapSelection——当前类型全部地图按名列出)
        _mapSelectOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _mapSelectOpt.TooltipText = "Select a map.";
        _mapSelectOpt.ItemSelected += idx =>
        {
            if (idx >= 0 && idx < _filtered.Count) Select(_filtered[(int)idx]);
        };
        page.AddChild(MakeSettingRow("Map Selection", _mapSelectOpt));

        var browseBtn = new Button
        {
            Text = Localization.Tr("Browse Maps"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Press to open the map browser.",
        };
        browseBtn.Pressed += OpenBrowser;
        page.AddChild(MakeSettingRow("Map Browser", browseBtn));

        _mapSizeOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var (name, tiles) in MapSizes)
            _mapSizeOpt.AddItem($"{Localization.Tr(name)} ({tiles})");
        _mapSizeOpt.Selected = DefaultMapSizeIndex;
        _mapSizeOpt.TooltipText = "Map size in tiles (bigger maps fit more players).";
        page.AddChild(MakeSettingRow("Map Size", _mapSizeOpt));

        _placementOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _placementOpt.TooltipText = "How players are placed on the map.";
        page.AddChild(MakeSettingRow("Player Placement", _placementOpt));

        // Biome(原版:图支持 biome 才显示;首项 Random)
        _biomeOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _biomeOpt.TooltipText = "The flora/fauna/terrain set the map is generated with.";
        _biomeRow = MakeSettingRow("Biome", _biomeOpt);
        page.AddChild(_biomeRow);

        _nomadBox = AddCheck(page, "Nomad", "Start without a civic center — only units.");
        _treasuresBox = AddCheck(page, "Treasures", "Place collectible treasures on the map.");
        _treasuresBox.ButtonPressed = true;   // 原版默认开
        _exploredBox = AddCheck(page, "Explored Map", "The map starts explored (fog of war remains).");
        _revealedBox = AddCheck(page, "Revealed Map", "No fog of war — everything is visible.");
        _alliedViewBox = AddCheck(page, "Allied View", "Allies share their vision.");
        _alliedViewBox.ButtonPressed = true;  // 原版默认开

        return page;
    }

    // ══════════ Player 页签 ══════════
    private Control BuildPlayerTab()
    {
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 4);

        // 玩家数(原版 PlayerCount:g_MaxPlayers=8)
        _playerCountOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        for (int n = 1; n <= 8; n++) _playerCountOpt.AddItem(n.ToString());
        _playerCountOpt.Selected = 1;   // 默认 2 人局
        _playerCountOpt.TooltipText = "Number of players on the map.";
        _playerCountOpt.ItemSelected += _ => RebuildSlotRows();
        page.AddChild(MakeSettingRow("Number of Players", _playerCountOpt));

        _popCapTypeOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var (_, title, _) in PopCapTypes) _popCapTypeOpt.AddItem(Localization.Tr(title));
        _popCapTypeOpt.Selected = 0;
        _popCapTypeOpt.TooltipText = "How the population cap is distributed.";
        _popCapTypeOpt.ItemSelected += _ => UpdatePopCapLabel();
        page.AddChild(MakeSettingRow("Population Cap Type", _popCapTypeOpt));

        // 原版 PopulationCap 滑条(对数 0..1,最右 Unlimited)
        var capRow = new HBoxContainer();
        capRow.AddThemeConstantOverride("separation", 8);
        var capLabel = new Label
        {
            Text = Localization.Tr("Player Population Cap"),
            CustomMinimumSize = new Vector2(110, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        capLabel.AddThemeFontSizeOverride("font_size", 13);
        capLabel.Modulate = new Color(1, 1, 1, 0.75f);
        capRow.AddChild(capLabel);
        _popCapSlider = new HSlider
        {
            MinValue = 0, MaxValue = 1, Step = 0.01, Value = 0.5,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Choose the population cap (rightmost = Unlimited).",
        };
        _popCapSlider.ValueChanged += _ => UpdatePopCapLabel();
        capRow.AddChild(_popCapSlider);
        _popCapValue = new Label { Text = "300", CustomMinimumSize = new Vector2(64, 0) };
        _popCapValue.AddThemeFontSizeOverride("font_size", 13);
        capRow.AddChild(_popCapValue);
        page.AddChild(capRow);

        _startResOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var (name, amount) in StartResources)
            _startResOpt.AddItem($"{Localization.Tr(name)} ({amount})");
        _startResOpt.Selected = DefaultStartResIndex;
        _startResOpt.TooltipText = "Resources each player starts with.";
        page.AddChild(MakeSettingRow("Starting Resources", _startResOpt));

        _spiesBox = AddCheck(page, "Spies", "Allow training spy units.");
        _cheatsBox = AddCheck(page, "Cheats", "Enable cheat codes in this match.");

        return page;
    }

    // ══════════ Game Type 页签 ══════════
    private Control BuildGameTypeTab()
    {
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 4);

        foreach (var (id, name, def) in VictoryChoices)
        {
            var box = AddCheck(page, name, $"Victory condition: {name}.");
            box.ButtonPressed = def;
            _victoryBoxes[id] = box;
        }

        _gameSpeedOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var s in GameSpeeds) _gameSpeedOpt.AddItem($"{s:0.##}×");
        _gameSpeedOpt.Selected = DefaultGameSpeedIndex;
        _gameSpeedOpt.TooltipText = "Game speed multiplier.";
        page.AddChild(MakeSettingRow("Game Speed", _gameSpeedOpt));

        // 原版 Ceasefire 滑条(0..45 分钟)
        var cfRow = new HBoxContainer();
        cfRow.AddThemeConstantOverride("separation", 8);
        var cfLabel = new Label
        {
            Text = Localization.Tr("Ceasefire"),
            CustomMinimumSize = new Vector2(110, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        cfLabel.AddThemeFontSizeOverride("font_size", 13);
        cfLabel.Modulate = new Color(1, 1, 1, 0.75f);
        cfRow.AddChild(cfLabel);
        _ceasefireSlider = new HSlider
        {
            MinValue = 0, MaxValue = 45, Step = 1, Value = 0,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Ceasefire duration — players can't attack each other until it expires.",
        };
        _ceasefireSlider.ValueChanged += v =>
            _ceasefireValue.Text = v < 0.5 ? Localization.Tr("Off") : $"{(int)v} min";
        cfRow.AddChild(_ceasefireSlider);
        _ceasefireValue = new Label { Text = Localization.Tr("Off"), CustomMinimumSize = new Vector2(64, 0) };
        _ceasefireValue.AddThemeFontSizeOverride("font_size", 13);
        cfRow.AddChild(_ceasefireValue);
        page.AddChild(cfRow);

        _lockedTeamsBox = AddCheck(page, "Locked Teams", "Players can't change diplomacy mid-game.");
        _lastManBox = AddCheck(page, "Last Man Standing", "Allied victory is disabled — only one winner.");

        return page;
    }

    private CheckBox AddCheck(Control parent, string text, string tooltip)
    {
        var box = new CheckBox { Text = Localization.Tr(text), TooltipText = tooltip };
        UITheme.ApplyCheckboxIcons(box);
        parent.AddChild(box);
        return box;
    }

    /// <summary>设置行:左标签 + 右控件(原版 GameSettingsPanel 逐项格式)。</summary>
    private static Control MakeSettingRow(string label, Control widget)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var l = new Label
        {
            Text = Localization.Tr(label),
            CustomMinimumSize = new Vector2(110, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        l.AddThemeFontSizeOverride("font_size", 13);
        l.Modulate = new Color(1, 1, 1, 0.75f);
        row.AddChild(l);
        row.AddChild(widget);
        return row;
    }

    private void UpdatePopCapLabel()
    {
        int cap = ReadPopCap();
        _popCapValue.Text = cap <= 0 ? Localization.Tr("Unlimited") : cap.ToString();
    }

    /// <summary>地图浏览覆盖页(原版 MapBrowserPage):列表 + Back。</summary>
    private void BuildBrowser()
    {
        _browser = new PanelContainer { Visible = false };
        _browser.SetAnchorsPreset(LayoutPreset.FullRect);
        _browser.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.08f, 0.07f, 0.97f),
        });
        AddChild(_browser);
        var vb = new VBoxContainer();
        vb.SetAnchorsPreset(LayoutPreset.FullRect);
        vb.OffsetLeft = 12; vb.OffsetTop = 12; vb.OffsetRight = -12; vb.OffsetBottom = -12;
        vb.AddThemeConstantOverride("separation", 6);
        _browser.AddChild(vb);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        var browserTitle = new Label { Text = Localization.Tr("Select Map"), VerticalAlignment = VerticalAlignment.Center };
        browserTitle.AddThemeFontSizeOverride("font_size", 17);
        head.AddChild(browserTitle);
        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        head.AddChild(spacer);
        var backBtn = new Button { Text = Localization.Tr("Back") };
        backBtn.Pressed += () => _browser.Visible = false;
        head.AddChild(backBtn);
        vb.AddChild(head);

        // 网格浏览(原版 MapGridBrowser:预览图格+名称,分页滚轮翻页;
        // 替代纯列表——原版 gamesetup 的 Select Map 弹窗同款)。
        var gridScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        vb.AddChild(gridScroll);
        _gridContainer = new GridContainer { Columns = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _gridContainer.AddThemeConstantOverride("h_separation", 12);
        _gridContainer.AddThemeConstantOverride("v_separation", 12);
        gridScroll.AddChild(_gridContainer);

        // 列表兜底(无预览图时仍可选;网格与列表并存,网格优先)。
        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, Visible = false };
        _list.ItemSelected += idx =>
        {
            Select(_filtered[(int)idx]);
            _browser.Visible = false;
        };
        vb.AddChild(_list);
    }

    private void OpenBrowser()
    {
        _browser.Visible = true;
        if (_selected != null)
        {
            int idx = _filtered.IndexOf(_selected);
            if (idx >= 0)
            {
                _list.Select(idx);
                _list.EnsureCurrentIsVisible();
            }
        }
    }

    /// <summary>dev 截图钩子:预选 Map Type(0=random/1=skirmish/2=scenario)并打开
    /// 地图浏览器展示该类型完整列表(程序化 Selected 不发 ItemSelected,须手动 Refill)。</summary>
    public void DevShowMapType(int idx)
    {
        _mapTypeOpt.Selected = idx;
        Refill();
        if (_filtered.Count > 0) Select(_filtered[0]);
        OpenBrowser();
    }

    private void Refill()
    {
        string type = _mapTypeOpt?.Selected switch
        {
            0 => "random",
            1 => "skirmish",
            2 => "scenario",
            _ => "random",   // 默认视图 = random(原版 gamesetup 同款)
        };
        // 战役强制图(不可换):RelPath 含该图基名(level.Map 的文件主干)。
        _filtered = ForcedMapId != null
            ? _maps.Where(m => m.RelPath.Contains(ForcedMapId)).ToList()
            : _maps.Where(m => m.MapType == type).ToList();

        _list.Clear();
        _mapSelectOpt?.Clear();
        // 网格填充(原版 MapGridBrowser:预览图格+名称,点击选中)。
        foreach (var child in _gridContainer.GetChildren()) child.QueueFree();
        foreach (var m in _filtered)
        {
            _list.AddItem(m.DisplayName);
            _mapSelectOpt?.AddItem(m.DisplayName);

            var item = new PanelContainer
            {
                CustomMinimumSize = new Vector2(180, 160),
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            var itemVbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            itemVbox.AddThemeConstantOverride("separation", 2);
            item.AddChild(itemVbox);

            var preview = new TextureRect
            {
                CustomMinimumSize = new Vector2(170, 120),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            };
            if (m.PreviewPath != null)
            {
                var img = Image.LoadFromFile(m.PreviewPath);
                if (img != null) preview.Texture = ImageTexture.CreateFromImage(img);
            }
            itemVbox.AddChild(preview);

            var nameLabel = new Label
            {
                Text = m.DisplayName,
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.Off,
            };
            nameLabel.AddThemeFontSizeOverride("font_size", 11);
            itemVbox.AddChild(nameLabel);

            var itemRef = m;
            item.GuiInput += ev =>
            {
                if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                {
                    Select(itemRef);
                    _browser.Visible = false;
                }
            };
            _gridContainer.AddChild(item);
        }
        if (_mapSelectOpt != null && _filtered.Count > 0)
            _mapSelectOpt.Selected = 0;
    }

    private void Select(MapEntry? m)
    {
        _selected = m;
        _startBtn.Disabled = m == null;
        _nameLabel.Text = m?.DisplayName ?? "";
        _descLabel.Text = m?.Description ?? "";
        int idx = m != null ? _filtered.IndexOf(m) : -1;
        if (idx >= 0) _mapSelectOpt.Selected = idx;

        // random 图才有尺寸/布置/biome/玩家数等生成选项(scenario/skirmish 全来自 pmp)。
        bool isRandom = m?.MapType == "random";
        _mapSizeOpt.Disabled = !isRandom;
        RebuildPlacements(m);
        _playerCountOpt.Disabled = !isRandom;
        _nomadBox.Disabled = !isRandom;
        _statusLine.Text = "";
        RebuildBiomeOptions(m);

        // 预览图
        if (m?.PreviewPath != null)
        {
            var img = Image.LoadFromFile(m.PreviewPath);
            _preview.Texture = img != null ? ImageTexture.CreateFromImage(img) : null;
        }
        else
        {
            _preview.Texture = null;
        }
        RebuildSlotRows();
    }

    /// <summary>按图的 SupportedBiomes 填 biome 下拉(首项 Random;无 biome 支持则隐藏行)。
    /// 读 maps/random/{name}.json 的 settings.SupportedBiomes,标题取 rmbiome 各 JSON
    /// 的 Description.Title(同原版 biome 下拉)。</summary>
    private void RebuildBiomeOptions(MapEntry? m)
    {
        _biomeOpt.Clear();
        _biomeIds.Clear();
        bool visible = false;
        if (m?.MapType == "random" && _dataRoot != null)
        {
            var entries = LoadSupportedBiomes(m.RelPath.Substring("random/".Length));
            if (entries.Count > 0)
            {
                _biomeOpt.AddItem(Localization.Tr("Random"));
                _biomeIds.Add("random");
                foreach (var (id, title) in entries)
                {
                    _biomeOpt.AddItem(Localization.Tr(title));
                    _biomeIds.Add(id);
                }
                _biomeOpt.Selected = 0;
                visible = true;
            }
        }
        _biomeRow.Visible = visible;
    }

    private List<(string Id, string Title)> LoadSupportedBiomes(string mapName)
    {
        var result = new List<(string, string)>();
        string jsonPath = Path.Combine(_dataRoot!, "maps", "random", mapName + ".json");
        try
        {
            if (!File.Exists(jsonPath)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (!doc.RootElement.TryGetProperty("settings", out var settings) ||
                !settings.TryGetProperty("SupportedBiomes", out var sb))
                return result;

            if (sb.ValueKind == JsonValueKind.String)
            {
                // "generic/" / "alpine/" —— 目录下全部 biome JSON
                string dir = sb.GetString() ?? "";
                string absDir = Path.Combine(_dataRoot!, "maps", "random", "rmbiome",
                    dir.TrimEnd('/'));
                if (Directory.Exists(absDir))
                    foreach (var file in Directory.GetFiles(absDir, "*.json").OrderBy(f => f, System.StringComparer.Ordinal))
                        result.Add((dir + Path.GetFileNameWithoutExtension(file), BiomeTitle(file)));
            }
            else if (sb.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in sb.EnumerateArray())
                {
                    string id = item.GetString() ?? "";
                    if (id.Length == 0) continue;
                    string file = Path.Combine(_dataRoot!, "maps", "random", "rmbiome",
                        id + ".json");
                    result.Add((id, File.Exists(file) ? BiomeTitle(file) : id));
                }
            }
        }
        catch { /* 读不到即无 biome 行 */ }
        return result;
    }

    private static string BiomeTitle(string jsonFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonFile));
            if (doc.RootElement.TryGetProperty("Description", out var d) &&
                d.TryGetProperty("Title", out var t))
                return t.GetString() ?? Path.GetFileNameWithoutExtension(jsonFile);
        }
        catch { }
        return Path.GetFileNameWithoutExtension(jsonFile);
    }

    /// <summary>按当前地图重建槽位行。原版样式:整行玩家色底 + 白字(PlayersPanel 的
    /// playerBackgroundColor)。skirmish/scenario 行数 = 地图 PlayerData(pmp 实体按
    /// player id 绑定,不允许 Closed);random 行数 = 玩家数下拉,允许 Closed。</summary>
    private void RebuildSlotRows()
    {
        foreach (var c in _slotRows.GetChildren()) c.QueueFree();
        _kindOpts.Clear(); _civOpts.Clear(); _teamOpts.Clear();
        if (_statusLine != null) _statusLine.Text = "";   // 槽位变了,旧的拒绝原因作废
        if (_selected == null) return;

        bool isRandom = _selected.MapType == "random";
        bool isScenario = _selected.MapType == "scenario";
        int count = isRandom
            ? _playerCountOpt.Selected + 1
            : System.Math.Max(1, _selected.Players.Count);

        for (int i = 0; i < count; i++)
        {
            int row = i;
            var card = new PanelContainer();
            var rowColor = PlayerRowColors[System.Math.Min(i, PlayerRowColors.Length - 1)];
            // 行高 32(原版 playerFrame[n] size 100% 32)。
            card.CustomMinimumSize = new Vector2(0, 32);
            card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = rowColor,
                ContentMarginTop = 2, ContentMarginBottom = 2,
                ContentMarginLeft = 8, ContentMarginRight = 8,
            });
            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 8);
            card.AddChild(hbox);

            var nameLabel = new Label
            {
                Text = $"Player {i + 1}",
                CustomMinimumSize = new Vector2(130, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            nameLabel.AddThemeFontSizeOverride("font_size", 14);
            hbox.AddChild(nameLabel);

            var kind = new OptionButton { CustomMinimumSize = new Vector2(110, 0) };
            kind.AddItem(Localization.Tr("You"));      // 0
            kind.AddItem(Localization.Tr("AI"));       // 1
            if (isRandom) kind.AddItem(Localization.Tr("Closed"));  // 2
            kind.Selected = i == 0 ? 0 : 1;
            kind.TooltipText = "Player assignment.";
            kind.ItemSelected += sel => OnKindSelected(row, (int)sel);
            hbox.AddChild(kind);

            var civ = new OptionButton
            {
                CustomMinimumSize = new Vector2(150, 0),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "Player civilization.",
            };
            civ.AddItem(Localization.Tr("Random"));
            foreach (var (_, name) in Civs) civ.AddItem(name);
            // 默认文明:pmp 图取地图 PlayerData 的 civ;random 图用原版 player_defaults
            // 的逐槽默认(athen/cart/gaul/iber);scenario 锁定(作者实体文明固定)。
            string? mapCiv = i < _selected.Players.Count ? _selected.Players[i].Civ : null;
            if (mapCiv == null && isRandom && i < DefaultCivs.Length)
                mapCiv = DefaultCivs[i];
            int civIdx = mapCiv != null ? System.Array.FindIndex(Civs, c => c.Code == mapCiv) + 1 : 0;
            civ.Selected = System.Math.Max(0, civIdx);
            civ.Disabled = isScenario && civIdx > 0;
            hbox.AddChild(civ);

            var team = new OptionButton
            {
                CustomMinimumSize = new Vector2(60, 0),
                TooltipText = "Players in the same team are allies.",
            };
            team.AddItem("—");   // 0 → Team -1(无队)
            for (int t2 = 1; t2 <= 4; t2++) team.AddItem(t2.ToString());
            int mapTeam = i < _selected.Players.Count ? _selected.Players[i].Team : -1;
            team.Selected = mapTeam >= 0 && mapTeam <= 3 ? mapTeam + 1 : 0;
            hbox.AddChild(team);

            // AI 难度/性格(原版 gamesetup aiDifficulties/aiBehaviors;仅 AI 行可见)。
            var diff = new OptionButton { CustomMinimumSize = new Vector2(96, 0),
                TooltipText = "AI difficulty." };
            foreach (var d in AiDifficulties) diff.AddItem(Localization.Tr(d));
            diff.Selected = 3;   // Medium(原版默认)
            var behavior = new OptionButton { CustomMinimumSize = new Vector2(96, 0),
                TooltipText = "AI behavior." };
            foreach (var b in AiBehaviors) behavior.AddItem(Localization.Tr(b));
            behavior.Selected = 0;   // Random(原版默认)
            diff.Visible = behavior.Visible = kind.Selected == 1;
            kind.ItemSelected += sel =>
            {
                diff.Visible = behavior.Visible = sel == 1;
            };
            hbox.AddChild(diff);
            hbox.AddChild(behavior);

            _slotRows.AddChild(card);
            _kindOpts.Add(kind); _civOpts.Add(civ); _teamOpts.Add(team);
            _diffOpts.Add(diff); _behaviorOpts.Add(behavior);
        }
    }

    /// <summary>"You" 全表唯一(本地玩家只有一个):某行切到 You,旧的 You 降级为 AI。</summary>
    private void OnKindSelected(int row, int sel)
    {
        if (sel != 0) return;
        for (int i = 0; i < _kindOpts.Count; i++)
            if (i != row && _kindOpts[i].Selected == 0)
                _kindOpts[i].Selected = 1;
    }

    /// <summary>行 → 槽位表:跳过 Closed,PlayerId 连续重排;恰好一个 Human(其 PlayerId
    /// 即本地玩家 id,下游按 Kind==Human 找)。"Random" 文明此刻摇定(菜单侧随机,不进 sim
    /// 种子——sim 拿到的是具体文明码,确定性不受影响)。</summary>
    private IReadOnlyList<PlayerSlotSetup>? BuildSlots()
    {
        var slots = new List<PlayerSlotSetup>();
        int humans = 0;
        for (int i = 0; i < _kindOpts.Count; i++)
        {
            int kindSel = _kindOpts[i].Selected;
            var kind = kindSel == 0 ? PlayerSlotKind.Human
                : kindSel == 1 ? PlayerSlotKind.AI
                : PlayerSlotKind.Closed;
            if (kind == PlayerSlotKind.Closed) continue;
            if (kind == PlayerSlotKind.Human) humans++;

            int civSel = _civOpts[i].Selected - 1;   // 0 = Random
            string civ = civSel >= 0 ? Civs[civSel].Code : Civs[GD.RandRange(0, Civs.Length - 1)].Code;
            int teamSel = _teamOpts[i].Selected;      // 0 = 无队;1..4 → 0..3
            slots.Add(new PlayerSlotSetup
            {
                PlayerId = slots.Count + 1,
                Kind = kind,
                Civ = civ,
                Team = teamSel - 1,
                // AI 槽:难度/性格随槽走(原版 playerAI.difficulty/behavior)。
                AIDifficulty = kind == PlayerSlotKind.AI ? _diffOpts[i].Selected : -1,
                AIBehavior = kind == PlayerSlotKind.AI
                    ? AiBehaviors[_behaviorOpts[i].Selected].ToLowerInvariant() : "",
            });
        }
        return humans == 1 ? slots : null;   // 恰好一个本地玩家才允许开局
    }
}
