using System.Linq;
using Godot;
using ZeroAD.Godot.Options;

namespace ZeroAD.Godot;

/// <summary>热键设置页（独立页面，非 Options tab）。镜像原版 page_hotkeys.xml。
/// 左列分类列表，右列该分类的 HotkeyPicker 行（ScrollContainer），底部搜索框 + Reset All + Close。
/// 从 MainMenu 和 PauseMenu 打开（同 OptionsPanel 的两个入口点）。</summary>
public sealed partial class HotkeysPanel : ModalPanelBase
{
    private readonly int _layer;
    private UserConfig _cfg = null!;
    private VBoxContainer _rowContainer = null!;
    private LineEdit _searchBox = null!;
    private string _selectedCategory = "";
    private string _searchText = "";

    public HotkeysPanel(int layer = 58) : base() => _layer = layer;

    public override void _Ready()
    {
        _cfg = GetNode<UserConfig>("/root/UserConfig");
        var (content, _) = BuildShell("Hotkeys", 820);
        Layer = _layer;

        var split = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddThemeConstantOverride("separation", 12);
        content.AddChild(split);

        // 左列：分类列表
        var catList = new VBoxContainer { CustomMinimumSize = new Vector2(140, 0) };
        foreach (var cat in HotkeyCatalog.Categories)
        {
            var btn = new Button { Text = cat, ToggleMode = true };
            string captured = cat;
            btn.Pressed += () => SelectCategory(captured, btn);
            catList.AddChild(btn);
        }
        split.AddChild(catList);

        // 右列：滚动行容器
        var scroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _rowContainer = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_rowContainer);
        split.AddChild(scroll);

        // 底部：搜索框 + Reset All + Close
        var footer = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.Fill };
        content.AddChild(footer);
        _searchBox = new LineEdit { PlaceholderText = "搜索...", CustomMinimumSize = new Vector2(200, 0) };
        _searchBox.TextChanged += OnSearchChanged;
        footer.AddChild(_searchBox);
        var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        footer.AddChild(spacer);
        var resetAll = new Button { Text = "Reset All" };
        resetAll.Pressed += OnResetAll;
        footer.AddChild(resetAll);
        var closeBtn = new Button { Text = "Close" };
        closeBtn.Pressed += Close;
        footer.AddChild(closeBtn);

        // 默认选第一个分类
        if (HotkeyCatalog.Categories.Count > 0)
            SelectCategory(HotkeyCatalog.Categories[0], null);
    }

    private void SelectCategory(string cat, Button? sender)
    {
        _selectedCategory = cat;
        // 单选：取消同组其它按钮
        if (sender != null)
            foreach (var child in ((Control)sender.GetParent()).GetChildren())
                if (child is Button b && b.ToggleMode && b != sender) b.ButtonPressed = false;
        PopulateRows();
    }

    private void OnSearchChanged(string text)
    {
        _searchText = text.ToLowerInvariant();
        PopulateRows();
    }

    private void PopulateRows()
    {
        foreach (var child in _rowContainer.GetChildren())
            ((Node)child).QueueFree();

        var actions = HotkeyCatalog.ForCategory(_selectedCategory);
        foreach (var action in actions)
        {
            if (_searchText.Length > 0 && !action.DisplayLabel.ToLowerInvariant().Contains(_searchText)
                && !action.FullName.ToLowerInvariant().Contains(_searchText))
                continue;
            _rowContainer.AddChild(new HotkeyPicker(_cfg, action));
        }
        if (_rowContainer.GetChildCount() == 0)
            _rowContainer.AddChild(new Label { Text = "（无匹配）" });
    }

    private void OnResetAll()
    {
        foreach (var action in HotkeyCatalog.AllActions)
            HotkeyApplier.Reset(_cfg, action.FullName);
        PopulateRows();
    }
}
