using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ZeroAD.Godot;

// Replay 录像浏览器(会话外页之一)。镜像 LoadGamePanel 结构，数据源换成 ReplayFileManager。
// Tree 列出全部录像(描述/日期/地图/类型)，右侧详情面板显示地图/类型/玩家表，
// 底部 Cancel/Delete/Watch。Watch → 写 GameLaunchConfig(Mode=Replay+ReplaySlot)→
// ChangeScene 到 session 场景 → Main._Ready 走 Replay 分支播放。从 MainMenu 打开。
public sealed partial class ReplayPanel : ModalPanelBase
{
    private readonly int _layer;
    private Tree _tree = null!;
    private VBoxContainer _detail = null!;
    private Button _watchButton = null!;
    private Button _deleteButton = null!;
    private ConfirmationDialog _deleteConfirm = null!;

    private List<ReplayFileManager.ReplayEntry> _replays = new();
    private string? _selectedSlot;
    private int _sortColumn = 1;      // 默认按日期(新→旧)
    private bool _sortAsc = false;

    public ReplayPanel(int layer = 58) => _layer = layer;

    public override void _Ready()
    {
        var (content, _) = BuildShell("Replays", 760);
        Layer = _layer;

        var split = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddThemeConstantOverride("separation", 14);
        content.AddChild(split);

        // 左:录像列表。
        _tree = new Tree
        {
            Columns = 4,
            ColumnTitlesVisible = true,
            HideRoot = true,
            CustomMinimumSize = new Vector2(420, 380),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _tree.SetColumnTitle(0, "Name");
        _tree.SetColumnTitle(1, "Date");
        _tree.SetColumnTitle(2, "Map");
        _tree.SetColumnTitle(3, "Type");
        _tree.ItemSelected += OnItemSelected;
        _tree.ItemActivated += WatchSelected;
        _tree.ColumnTitleClicked += OnColumnTitleClicked;
        split.AddChild(_tree);

        // 右:详情面板。
        _detail = new VBoxContainer { CustomMinimumSize = new Vector2(240, 0) };
        _detail.AddThemeConstantOverride("separation", 6);
        split.AddChild(_detail);

        // 底部按钮行。
        var buttons = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        buttons.AddThemeConstantOverride("separation", 12);
        content.AddChild(buttons);
        AddButton(buttons, "Cancel", Close);
        _deleteButton = AddButton(buttons, "Delete", ConfirmDelete, disabled: true);
        _watchButton = AddButton(buttons, "Watch", WatchSelected, disabled: true);

        _deleteConfirm = new ConfirmationDialog
        {
            Title = "Delete Replay",
            OkButtonText = "Delete",
        };
        _deleteConfirm.Confirmed += DeleteSelected;
        AddChild(_deleteConfirm);
    }

    protected override void OnOpen()
    {
        _selectedSlot = null;
        _replays = ReplayFileManager.ListReplays();
        Populate();
        UpdateDetail();
    }

    private void Populate()
    {
        _tree.Clear();
        var root = _tree.CreateItem();
        foreach (var entry in SortReplays())
        {
            var item = _tree.CreateItem(root);
            item.SetText(0, entry.Meta.Description);
            item.SetText(1, FormatDate(entry.Meta.TimeUnix));
            item.SetText(2, MapName(entry.Meta.MapPath));
            item.SetText(3, entry.Meta.MapType);
            item.SetMetadata(0, entry.Slot);
        }
    }

    private IEnumerable<ReplayFileManager.ReplayEntry> SortReplays() => _sortColumn switch
    {
        0 => _sortAsc ? _replays.OrderBy(e => e.Meta.Description) : _replays.OrderByDescending(e => e.Meta.Description),
        2 => _sortAsc ? _replays.OrderBy(e => e.Meta.MapPath) : _replays.OrderByDescending(e => e.Meta.MapPath),
        3 => _sortAsc ? _replays.OrderBy(e => e.Meta.MapType) : _replays.OrderByDescending(e => e.Meta.MapType),
        _ => _sortAsc ? _replays.OrderBy(e => e.Meta.TimeUnix) : _replays.OrderByDescending(e => e.Meta.TimeUnix),
    };

    private void OnColumnTitleClicked(long column, long mouseButton)
    {
        int col = (int)column;
        if (_sortColumn == col) _sortAsc = !_sortAsc;
        else { _sortColumn = col; _sortAsc = true; }
        Populate();
    }

    private void OnItemSelected()
    {
        _selectedSlot = _tree.GetSelected()?.GetMetadata(0).AsString();
        UpdateDetail();
    }

    private void UpdateDetail()
    {
        foreach (var child in _detail.GetChildren())
            child.QueueFree();
        var entry = _replays.FirstOrDefault(e => e.Slot == _selectedSlot);
        bool has = entry != null;
        _watchButton.Disabled = !has;
        _deleteButton.Disabled = !has;

        if (!has)
        {
            _detail.AddChild(MakeLabel(_replays.Count == 0 ? "No replays yet." : "Select a replay.", 14));
            return;
        }
        var meta = entry!.Meta;
        _detail.AddChild(MakeLabel(meta.Description, 16));
        _detail.AddChild(MakeLabel($"Map: {MapName(meta.MapPath)}", 13));
        _detail.AddChild(MakeLabel($"Type: {meta.MapType}", 13));
        _detail.AddChild(MakeLabel($"Command delay: {meta.CommandDelay}", 13));
        _detail.AddChild(MakeLabel($"Date: {FormatDate(meta.TimeUnix)}", 13));
        _detail.AddChild(MakeLabel($"Engine: {meta.EngineVersion}", 13));
        _detail.AddChild(MakeLabel("Players:", 13));
        foreach (var s in meta.Slots)
            _detail.AddChild(MakeLabel($"  {s.PlayerId}. {s.Civ} ({s.Kind}) team {s.Team}", 12));
    }

    private void WatchSelected()
    {
        if (_selectedSlot == null) return;
        var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
        cfg.Reset();
        cfg.Mode = GameLaunchConfig.LaunchMode.Replay;
        cfg.ReplaySlot = _selectedSlot;
        GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");
    }

    private void ConfirmDelete()
    {
        if (_selectedSlot == null) return;
        _deleteConfirm.DialogText = $"Delete replay '{_selectedSlot}'?";
        _deleteConfirm.PopupCentered();
    }

    private void DeleteSelected()
    {
        if (_selectedSlot == null) return;
        ReplayFileManager.Delete(_selectedSlot);
        _selectedSlot = null;
        _replays = ReplayFileManager.ListReplays();
        Populate();
        UpdateDetail();
    }

    private static string FormatDate(long timeUnix) =>
        DateTimeOffset.FromUnixTimeSeconds(timeUnix).LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    private static string MapName(string mapPath) =>
        string.IsNullOrEmpty(mapPath) ? "(unknown)" : System.IO.Path.GetFileNameWithoutExtension(mapPath);
}
