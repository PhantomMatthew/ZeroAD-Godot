using System.Linq;
using Godot;
using ZeroAD.Godot.Options;

namespace ZeroAD.Godot;

/// <summary>热键设置页。镜像原版 gui/hotkeys/hotkeys.xml(ModernDialog 700×688):
/// 顶行 "Category:" 下拉(All Hotkeys + 各分类,ModernDropDown)+"Filter:" 输入框(ModernInput);
/// 中行 Name/Mapping 列表(ModernSortedList,行 = HotkeyPicker);底行 Reset / Save / Close
/// (ModernButtonRed,156×28)。改动即时生效但仅本会话,Save 持久化,Close 放弃未存改动。
/// 从 MainMenu 和 PauseMenu 打开(同 OptionsPanel 的两个入口点)。</summary>
public sealed partial class HotkeysPanel : ModalPanelBase
{
    private readonly int _layer;
    private UserConfig _cfg = null!;
    private VBoxContainer _rowContainer = null!;
    private LineEdit _filterBox = null!;
    private OptionButton _catDropdown = null!;
    private Button _saveButton = null!;
    private Label _status = null!;
    private string _filterText = "";

    public HotkeysPanel(int layer = 58) : base() => _layer = layer;

    public override void _Ready()
    {
        _cfg = GetNode<UserConfig>("/root/UserConfig");
        var (content, status) = BuildShell("Hotkeys", 700);
        Layer = _layer;
        _status = status;

        // 顶行(原版 y 32..58):"Category:" 下拉(132..350)+ "Filter:" 输入框(100%-200..100%-32)。
        var top = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        top.AddThemeConstantOverride("separation", 8);
        content.AddChild(top);

        var catLabel = new Label { Text = "Category:", VerticalAlignment = VerticalAlignment.Center };
        top.AddChild(catLabel);
        _catDropdown = new OptionButton { CustomMinimumSize = new Vector2(218, 26) };
        UITheme.ApplyModernInput(_catDropdown);
        _catDropdown.AddItem("All Hotkeys");
        foreach (var cat in HotkeyCatalog.Categories)
            _catDropdown.AddItem(cat);
        _catDropdown.Selected = 0;
        _catDropdown.ItemSelected += _ => PopulateRows();
        top.AddChild(_catDropdown);

        var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        top.AddChild(spacer);

        var filterLabel = new Label { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center };
        top.AddChild(filterLabel);
        _filterBox = new LineEdit { CustomMinimumSize = new Vector2(168, 26) };
        UITheme.ApplyModernInput(_filterBox);
        _filterBox.TextChanged += OnFilterChanged;
        top.AddChild(_filterBox);

        // 列表头(原版 ModernSortedList 列头:Name 60% / Mapping 40%)。
        var header = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        header.AddThemeConstantOverride("separation", 8);
        var nameHead = new Label
        {
            Text = "Name",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 0.6f,
        };
        var mapHead = new Label
        {
            Text = "Mapping",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 0.4f,
        };
        header.AddChild(nameHead);
        header.AddChild(mapHead);
        content.AddChild(header);
        content.AddChild(new HSeparator());

        // 列表(原版 32 70 100%-32 100%-70)。
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(636, 500),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _rowContainer = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _rowContainer.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_rowContainer);
        content.AddChild(scroll);

        // 底行(原版 y 100%-52..100%-24):Reset 居左,Save/Close 居右,各 156×28。
        var footer = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        footer.AddThemeConstantOverride("separation", 8);
        content.AddChild(footer);
        AddButton(footer, "Reset", OnResetAll, minWidth: 156);
        var footSpacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        footer.AddChild(footSpacer);
        _saveButton = AddButton(footer, "Save", OnSave, minWidth: 156);
        AddButton(footer, "Close", CloseRequested, minWidth: 156);

        PopulateRows();
        UpdateSaveButton();
    }

    protected override void OnOpen()
    {
        PopulateRows();
        UpdateSaveButton();
    }

    private void OnFilterChanged(string text)
    {
        _filterText = text.ToLowerInvariant();
        PopulateRows();
    }

    private void PopulateRows()
    {
        foreach (var child in _rowContainer.GetChildren())
            ((Node)child).QueueFree();

        // 下拉 0 = All Hotkeys(原版 list_data -1);其余按下标-1 映射分类。
        var actions = _catDropdown.Selected <= 0
            ? HotkeyCatalog.AllActions
            : HotkeyCatalog.ForCategory(HotkeyCatalog.Categories[_catDropdown.Selected - 1]);
        foreach (var action in actions)
        {
            if (_filterText.Length > 0 && !action.DisplayLabel.ToLowerInvariant().Contains(_filterText)
                && !action.FullName.ToLowerInvariant().Contains(_filterText))
                continue;
            _rowContainer.AddChild(new HotkeyPicker(_cfg, action));
        }
        if (_rowContainer.GetChildCount() == 0)
            _rowContainer.AddChild(new Label { Text = "(No matches)" });
        UpdateSaveButton();
    }

    /// <summary>原版 Reset:清全部热键用户值回落默认(会话内生效,Save 后持久)。</summary>
    private void OnResetAll()
    {
        foreach (var action in HotkeyCatalog.AllActions)
            HotkeyApplier.Reset(_cfg, action.FullName);
        PopulateRows();
        _status.Text = "Hotkeys reset to defaults.";
    }

    private void OnSave()
    {
        _cfg.Save();
        UpdateSaveButton();
        _status.Text = "Hotkeys saved.";
    }

    private void CloseRequested()
    {
        if (!_cfg.HasChanges)
        {
            Close();
            return;
        }
        // 原版 Close tooltip:"Unsaved changes will be lost"。
        var dlg = new ConfirmationDialog
        {
            Title = "Unsaved Changes",
            DialogText = "You have unsaved changes, do you want to close this window?\nUnsaved changes affect this session only.",
            OkButtonText = "Close",
        };
        dlg.Confirmed += Close;
        AddChild(dlg);
        dlg.PopupCentered();
    }

    private void UpdateSaveButton()
    {
        if (_saveButton != null)
            _saveButton.Disabled = !_cfg.HasChanges;
    }
}
