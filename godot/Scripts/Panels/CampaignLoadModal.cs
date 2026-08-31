using System.Collections.Generic;
using Godot;
using ZeroAD.Godot.Campaigns;

namespace ZeroAD.Godot;

// CampaignLoadModal — 已有战役 run 管理(原版 campaigns/load_modal/LoadModal.js):
// 列出 user://saves/campaigns/*.0adcampaign(坏档红字占位、可删不可开),
// 选中显示 run 描述;Delete 先确认(原版 messageBox),Start 载入并置当前 → 战役主菜单。
public sealed partial class CampaignLoadModal : ModalPanelBase
{
    private readonly string? _dataRoot;

    private ItemList _list = null!;
    private Label _desc = null!;
    private Label _empty = null!;
    private Button _deleteButton = null!;
    private Button _startButton = null!;
    private ConfirmationDialog _confirm = null!;

    private List<CampaignRun> _runs = new();
    private int _selected = -1;

    /// <summary>run 载入完成(已置当前)。</summary>
    public event System.Action<CampaignRun>? OnRunLoaded;

    public CampaignLoadModal(string? dataRoot) => _dataRoot = dataRoot;

    public override void _Ready()
    {
        Layer = 62;
        var (content, _) = BuildShell("Load Campaign", 520);
        Layer = 62;

        _empty = MakeLabel("There are no campaign runs to load.", 14);
        content.AddChild(_empty);

        _list = new ItemList
        {
            CustomMinimumSize = new Vector2(460, 300),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _list.ItemSelected += idx =>
        {
            _selected = (int)idx;
            _desc.Text = _runs[_selected].Broken ? "" : _runs[_selected].GetLabel();
            UpdateButtons();
        };
        _list.ItemActivated += _ => StartSelected();
        content.AddChild(_list);

        _desc = MakeLabel("", 13);
        content.AddChild(_desc);

        var buttons = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        buttons.AddThemeConstantOverride("separation", 12);
        content.AddChild(buttons);
        AddButton(buttons, "Cancel", () => { Close(); QueueFree(); });
        _deleteButton = AddButton(buttons, "Delete", ConfirmDelete, disabled: true);
        _startButton = AddButton(buttons, "Start", StartSelected, disabled: true);

        _confirm = new ConfirmationDialog
        {
            Title = "Confirmation",
            OkButtonText = "Yes",
            CancelButtonText = "No",
        };
        _confirm.Confirmed += DeleteSelected;
        AddChild(_confirm);
    }

    protected override void OnOpen()
    {
        _runs = CampaignRun.ListRuns(_dataRoot);
        _selected = -1;
        Populate();
    }

    private void Populate()
    {
        _list.Clear();
        foreach (var run in _runs)
        {
            // getLabel(forList):坏档 = "filename.0adcampaign (file cannot be loaded)" 红字。
            int idx = _list.AddItem(run.Broken
                ? $"{run.Filename}.0adcampaign ({Localization.Tr("file cannot be loaded")})"
                : run.GetLabel(full: true));
            if (run.Broken)
                _list.SetItemCustomFgColor(idx, new Color(0.9f, 0.3f, 0.3f));
        }
        _empty.Visible = _runs.Count == 0;
        _list.Visible = _runs.Count > 0;
        _desc.Text = "";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool has = _selected >= 0 && _selected < _runs.Count;
        _deleteButton.Disabled = !has;
        _startButton.Disabled = !has || _runs[_selected].Broken;
    }

    private void ConfirmDelete()
    {
        if (_selected < 0) return;
        _confirm.DialogText = string.Format(
            Localization.Tr("Are you sure you want to delete run {0}? This cannot be undone."),
            _runs[_selected].Broken ? _runs[_selected].Filename : _runs[_selected].GetLabel());
        _confirm.PopupCentered();
    }

    private void DeleteSelected()
    {
        if (_selected < 0) return;
        _runs[_selected].Destroy();
        _runs.RemoveAt(_selected);
        _selected = -1;
        Populate();
    }

    private void StartSelected()
    {
        if (_selected < 0 || _runs[_selected].Broken) return;
        var run = _runs[_selected];
        run.SetCurrent();
        Close();
        QueueFree();
        OnRunLoaded?.Invoke(run);
    }
}
