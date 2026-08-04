using Godot;
using System.Collections.Generic;
using System.Linq;

namespace ZeroAD.Godot;

/// <summary>选图面板(SP "Matches" 入口;对齐原版 gamesetup 地图浏览器的简化版):
/// 左列类型过滤 + 地图列表,右列名称/类型/人数/描述 + 种子(仅 random 图生效)+ Start。
/// 打开方订阅 OnStart(MapEntry, seed) / OnCancelled。</summary>
public sealed partial class MapPickerPanel : Panel
{
    public event System.Action<MapEntry, uint>? OnStart;
    public event System.Action? OnCancelled;

    private readonly List<MapEntry> _maps;
    private List<MapEntry> _filtered = new();
    private ItemList _list = null!;
    private Label _nameLabel = null!;
    private Label _metaLabel = null!;
    private Label _descLabel = null!;
    private LineEdit _seedEdit = null!;
    private Label _seedLabel = null!;
    private Button _startBtn = null!;
    private OptionButton _filter = null!;
    private MapEntry? _selected;

    public MapPickerPanel(List<MapEntry> maps)
    {
        _maps = maps;
    }

    public override void _Ready()
    {
        Theme = UITheme.GetTheme();
        // 居中 760×520(直写 anchors+offsets,Center+Position 写法会跑偏)。
        AnchorLeft = 0.5f; AnchorRight = 0.5f; AnchorTop = 0.5f; AnchorBottom = 0.5f;
        OffsetLeft = -380; OffsetRight = 380; OffsetTop = -260; OffsetBottom = 260;
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;

        var vbox = new VBoxContainer();
        vbox.SetAnchorsPreset(LayoutPreset.FullRect);
        vbox.OffsetLeft = 16; vbox.OffsetTop = 12;
        vbox.OffsetRight = -16; vbox.OffsetBottom = -12;
        vbox.AddThemeConstantOverride("separation", 8);
        AddChild(vbox);

        var title = new Label { Text = Localization.Tr("Select Map"), HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 22);
        vbox.AddChild(title);

        var split = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        split.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(split);

        // 左:过滤 + 列表
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        left.AddThemeConstantOverride("separation", 6);
        split.AddChild(left);

        _filter = new OptionButton();
        foreach (var f in new[] { "All", "Random", "Skirmish", "Scenario" })
            _filter.AddItem(Localization.Tr(f));
        _filter.ItemSelected += _ => Refill();
        left.AddChild(_filter);

        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        _list.ItemSelected += idx => Select(_filtered[(int)idx]);
        left.AddChild(_list);

        // 右:详情 + 种子 + 按钮
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 6);
        split.AddChild(right);

        _nameLabel = new Label { Text = "" };
        _nameLabel.AddThemeFontSizeOverride("font_size", 18);
        right.AddChild(_nameLabel);

        _metaLabel = new Label { Text = "" };
        _metaLabel.AddThemeFontSizeOverride("font_size", 13);
        right.AddChild(_metaLabel);

        _descLabel = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _descLabel.AddThemeFontSizeOverride("font_size", 13);
        right.AddChild(_descLabel);

        var seedRow = new HBoxContainer();
        seedRow.AddThemeConstantOverride("separation", 8);
        _seedLabel = new Label { Text = Localization.Tr("Seed:"), CustomMinimumSize = new Vector2(50, 0) };
        seedRow.AddChild(_seedLabel);
        // 原版 gamesetup 每次进设置页摇新种子——固定 42 会让同图每次都生成逐位相同
        // 的布局(确定性特性),观感="选图不生效"。摇一个 6 位随机数预填,仍可手改锁种子。
        _seedEdit = new LineEdit
        {
            Text = ((uint)GD.RandRange(0, 999999)).ToString(),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "随机种子:同图同种子 = 同布局;每次打开本面板自动摇新",
        };
        seedRow.AddChild(_seedEdit);
        right.AddChild(seedRow);

        var btnRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        btnRow.AddThemeConstantOverride("separation", 12);
        _startBtn = new Button { Text = Localization.Tr("Start Game"), Disabled = true, CustomMinimumSize = new Vector2(160, 0) };
        _startBtn.Pressed += () =>
        {
            if (_selected == null) return;
            uint seed = uint.TryParse(_seedEdit.Text, out var s) ? s : 42;
            OnStart?.Invoke(_selected, seed);
        };
        btnRow.AddChild(_startBtn);
        var cancelBtn = new Button { Text = Localization.Tr("Cancel"), CustomMinimumSize = new Vector2(120, 0) };
        cancelBtn.Pressed += () => OnCancelled?.Invoke();
        btnRow.AddChild(cancelBtn);
        right.AddChild(btnRow);

        Refill();
        if (_filtered.Count > 0)
        {
            _list.Select(0);
            Select(_filtered[0]);
        }
    }

    private void Refill()
    {
        string type = _filter.Selected switch
        {
            1 => "random",
            2 => "skirmish",
            3 => "scenario",
            _ => "",
        };
        _filtered = type.Length == 0
            ? _maps.ToList()
            : _maps.Where(m => m.MapType == type).ToList();

        _list.Clear();
        foreach (var m in _filtered)
            _list.AddItem(m.DisplayName);
        Select(null);
    }

    private void Select(MapEntry? m)
    {
        _selected = m;
        _startBtn.Disabled = m == null;
        _nameLabel.Text = m?.DisplayName ?? "";
        _metaLabel.Text = m == null ? "" :
            $"{m.MapType}{(m.PlayerCount > 0 ? $" · {m.PlayerCount} players" : "")} · {m.RelPath}";
        _descLabel.Text = m?.Description ?? "";
        // 种子仅对 random 图有意义(scenario/skirmish 地形来自 pmp 文件)。
        bool isRandom = m?.MapType == "random";
        _seedEdit.Editable = isRandom;
        _seedLabel.Modulate = new Color(1, 1, 1, isRandom ? 1f : 0.4f);
        _seedEdit.Modulate = _seedLabel.Modulate;
    }
}
