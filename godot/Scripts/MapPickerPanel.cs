using Godot;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Net;

namespace ZeroAD.Godot;

/// <summary>对局设置面板(SP "Matches" 入口;布局/样式对齐原版 gamesetup GameSetupPage):
/// 左列 = 玩家面板(整行玩家色背景,PlayersPanel.xml 同款)+ 地图描述;右列 = 地图预览 +
/// 设置列表(Map Type/玩家数/种子)+ Cancel/Start Game!(StoneButton 风格)。地图浏览是
/// 设置页内覆盖页(原版 MapBrowserPage)。槽位数:skirmish/scenario 取 ScriptSettings.
/// PlayerData,random 1-4 可选。OnStart(MapEntry, seed, slots)。</summary>
public sealed partial class MapPickerPanel : Panel
{
    public event System.Action<MapEntry, uint, IReadOnlyList<PlayerSlotSetup>>? OnStart;
    public event System.Action? OnCancelled;

    private readonly List<MapEntry> _maps;
    private List<MapEntry> _filtered = new();
    private ItemList _list = null!;
    private Label _nameLabel = null!;
    private Label _descLabel = null!;
    private TextureRect _preview = null!;
    private LineEdit _seedEdit = null!;
    private Button _startBtn = null!;
    private OptionButton _mapTypeOpt = null!;
    private SpinBox _playerCount = null!;
    private VBoxContainer _slotRows = null!;
    private PanelContainer _browser = null!;
    private MapEntry? _selected;

    // 每行的控件(kind/civ/team),索引 = 行号。
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
    };

    public MapPickerPanel(List<MapEntry> maps)
    {
        _maps = maps;
    }

    public override void _Ready()
    {
        Theme = UITheme.GetTheme();
        // 近全屏(原版 gamesetup 就是全屏页):2%-3% 边距,随窗口缩放。
        AnchorLeft = 0.02f; AnchorRight = 0.98f; AnchorTop = 0.03f; AnchorBottom = 0.97f;
        OffsetLeft = 0; OffsetRight = 0; OffsetTop = 0; OffsetBottom = 0;

        // 整页滚动兜底:窗口不足(原版要求 ≥1024×768)时设置页内部滚动,保证
        // 按钮/设置永远可达;大窗口无滚动条、观感不变。
        var scroll = new ScrollContainer();
        var vbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(vbox);
        AddChild(scroll);
        scroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var title = new Label { Text = Localization.Tr("Match Setup"), HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 22);
        vbox.AddChild(title);

        // ── 主体两列(对齐原版:左 = 玩家面板+描述;右 = 预览+设置)──
        var cols = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        cols.AddThemeConstantOverride("separation", 16);
        vbox.AddChild(cols);

        cols.AddChild(BuildLeftColumn());
        cols.AddChild(BuildRightColumn());

        // ── 底部按钮行(原版 bottomPanel:右下 Cancel + Start Game!)──
        var btnRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        btnRow.AddThemeConstantOverride("separation", 10);
        var cancelBtn = new Button { Text = Localization.Tr("Cancel"), CustomMinimumSize = new Vector2(110, 32) };
        cancelBtn.Pressed += () => OnCancelled?.Invoke();
        btnRow.AddChild(cancelBtn);
        _startBtn = BuildStartButton();
        btnRow.AddChild(_startBtn);
        vbox.AddChild(btnRow);

        BuildBrowser();

        Refill();
        if (_filtered.Count > 0)
            Select(_filtered[0]);
    }

    /// <summary>Start Game! 按钮(原版 StoneButton 米金风格 + tooltip)。</summary>
    private Button BuildStartButton()
    {
        var btn = new Button
        {
            Text = Localization.Tr("Start Game!"),
            CustomMinimumSize = new Vector2(150, 32),
            TooltipText = "Start a new game with the current settings.",
        };
        btn.AddThemeColorOverride("font_color", new Color(0.1f, 0.08f, 0.04f));
        btn.AddThemeColorOverride("font_hover_color", new Color(0.05f, 0.04f, 0.02f));
        btn.AddThemeColorOverride("font_pressed_color", new Color(0.05f, 0.04f, 0.02f));
        btn.AddThemeColorOverride("font_disabled_color", new Color(0.35f, 0.30f, 0.22f));
        StyleBoxFlat CloneStartStyle(Color bg) => new()
        {
            BgColor = bg,
            BorderWidthTop = 1, BorderWidthBottom = 2, BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = new Color(0.45f, 0.36f, 0.2f),
            ContentMarginTop = 4, ContentMarginBottom = 4,
            ContentMarginLeft = 10, ContentMarginRight = 10,
        };
        btn.AddThemeStyleboxOverride("normal", CloneStartStyle(new Color(0.82f, 0.72f, 0.48f)));
        btn.AddThemeStyleboxOverride("hover", CloneStartStyle(new Color(0.92f, 0.83f, 0.6f)));
        btn.AddThemeStyleboxOverride("pressed", CloneStartStyle(new Color(0.7f, 0.6f, 0.38f)));
        btn.AddThemeStyleboxOverride("disabled", CloneStartStyle(new Color(0.5f, 0.46f, 0.36f)));
        btn.Pressed += () =>
        {
            if (_selected == null) return;
            uint seed = uint.TryParse(_seedEdit.Text, out var s) ? s : 42;
            var slots = BuildSlots();
            if (slots != null) OnStart?.Invoke(_selected, seed, slots);
        };
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

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 10);
        var headerLabel = new Label
        {
            Text = Localization.Tr("Players"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerLabel.AddThemeFontSizeOverride("font_size", 16);
        header.AddChild(headerLabel);
        _playerCount = new SpinBox
        {
            MinValue = 1, MaxValue = 4, Value = 2, Step = 1,
            CustomMinimumSize = new Vector2(70, 28),
            TooltipText = Localization.Tr("Number of Players"),
        };
        _playerCount.ValueChanged += _ => RebuildSlotRows();
        header.AddChild(_playerCount);
        playersInner.AddChild(header);

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

    /// <summary>右列:地图预览(上)+ 设置列表(中)+ 描述(下)。宽 ~400(原版 402px)。</summary>
    private Control BuildRightColumn()
    {
        var col = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(400, 0),
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

        // 设置列表(原版 GameSettingsPanel:每项 = 标签 + 控件横排,行距紧凑)
        var settings = new VBoxContainer();
        settings.AddThemeConstantOverride("separation", 4);
        col.AddChild(settings);

        _mapTypeOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var f in new[] { "Random", "Skirmish", "Scenario" })
            _mapTypeOpt.AddItem(Localization.Tr(f));
        _mapTypeOpt.Selected = 0;
        _mapTypeOpt.TooltipText = "Select a map type.";
        _mapTypeOpt.ItemSelected += _ => { Refill(); if (_filtered.Count > 0) Select(_filtered[0]); };
        settings.AddChild(MakeSettingRow("Map Type", _mapTypeOpt));

        var browseBtn = new Button
        {
            Text = Localization.Tr("Browse Maps"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Press to open the map browser.",
        };
        browseBtn.Pressed += OpenBrowser;
        settings.AddChild(MakeSettingRow("Map", browseBtn));

        _seedEdit = new LineEdit
        {
            Text = ((uint)GD.RandRange(0, 999999)).ToString(),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "随机种子:同图同种子 = 同布局;每次打开本面板自动摇新",
        };
        settings.AddChild(MakeSettingRow("Seed", _seedEdit));

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

        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
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

    private void Refill()
    {
        string type = _mapTypeOpt?.Selected switch
        {
            0 => "random",
            1 => "skirmish",
            2 => "scenario",
            _ => "random",   // 默认视图 = random(原版 gamesetup 同款)
        };
        _filtered = _maps.Where(m => m.MapType == type).ToList();

        _list.Clear();
        foreach (var m in _filtered)
            _list.AddItem(m.DisplayName);
    }

    private void Select(MapEntry? m)
    {
        _selected = m;
        _startBtn.Disabled = m == null;
        _nameLabel.Text = m?.DisplayName ?? "";
        _descLabel.Text = m?.Description ?? "";
        // 种子/玩家数仅对 random 图有意义(scenario/skirmish 地形与槽位来自 pmp)。
        bool isRandom = m?.MapType == "random";
        _seedEdit.Editable = isRandom;
        _seedEdit.Modulate = new Color(1, 1, 1, isRandom ? 1f : 0.4f);
        _playerCount.Editable = isRandom;
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

    /// <summary>按当前地图重建槽位行。原版样式:整行玩家色底 + 白字(PlayersPanel 的
    /// playerBackgroundColor)。skirmish/scenario 行数 = 地图 PlayerData(pmp 实体按
    /// player id 绑定,不允许 Closed);random 行数 = SpinBox,允许 Closed。</summary>
    private void RebuildSlotRows()
    {
        foreach (var c in _slotRows.GetChildren()) c.QueueFree();
        _kindOpts.Clear(); _civOpts.Clear(); _teamOpts.Clear();
        if (_selected == null) return;

        bool isRandom = _selected.MapType == "random";
        bool isScenario = _selected.MapType == "scenario";
        int count = isRandom
            ? (int)_playerCount.Value
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

            _slotRows.AddChild(card);
            _kindOpts.Add(kind); _civOpts.Add(civ); _teamOpts.Add(team);
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
            });
        }
        return humans == 1 ? slots : null;   // 恰好一个本地玩家才允许开局
    }
}
