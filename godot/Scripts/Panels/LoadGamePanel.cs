using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ZeroAD.Godot;

// LoadGame 存档浏览器(会话外页之一,原版 gui/page_loadgame.xml)。复用 ModalPanelBase 外壳。
// Tree 列出全部存档(名称/日期/地图/回合,列头点击排序),右侧详情面板显示地图/类型/玩家表,
// 底部 Cancel/Delete/Load。Load → 写 GameLaunchConfig(Mode=Load+LoadSlot)→ ChangeScene 到
// session 场景 → Main._Ready 走 Load 分支冷加载。从 MainMenu(Load Game 按钮)与 PauseMenu
// (Load Game 子项)两处打开——后者传 Layer 65 浮在暂停菜单(60)之上。
public sealed partial class LoadGamePanel : ModalPanelBase
{
    private readonly int _layer;
    private Tree _tree = null!;
    private VBoxContainer _detail = null!;
    private Button _loadButton = null!;
    private Button _deleteButton = null!;
    private ConfirmationDialog _deleteConfirm = null!;

    private List<SaveMeta> _saves = new();
    private string? _selectedSlot;
    private int _sortColumn = 1;      // 默认按日期(新→旧,与 ListSaves 一致)
    private bool _sortAsc = false;

    public LoadGamePanel(int layer = 58)
    {
        // 默认 58(高于普通菜单面板 55)。从 PauseMenu(Layer 60)打开时传 65。
        _layer = layer;
    }

    public override void _Ready()
    {
        var (content, _) = BuildShell("Load Game", 760);
        // BuildShell 硬编码 Layer=55——覆盖为请求层,须在其后设置才生效。
        Layer = _layer;

        var split = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddThemeConstantOverride("separation", 14);
        content.AddChild(split);

        // 左:存档列表(Tree,列头排序)。
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
        _tree.SetColumnTitle(3, "Turn");
        _tree.SetColumnCustomMinimumWidth(3, 50);
        _tree.ItemSelected += OnItemSelected;
        _tree.ItemActivated += LoadSelected;   // 双击/回车 = Load
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
        _loadButton = AddButton(buttons, "Load", LoadSelected, disabled: true);

        _deleteConfirm = new ConfirmationDialog
        {
            Title = "Delete Saved Game",
            OkButtonText = "Delete",
        };
        _deleteConfirm.Confirmed += DeleteSelected;
        AddChild(_deleteConfirm);
    }

    protected override void OnOpen()
    {
        _selectedSlot = null;
        _saves = SaveGameManager.ListSaves();
        Populate();
        UpdateDetail();
    }

    private void Populate()
    {
        _tree.Clear();
        var root = _tree.CreateItem();
        foreach (var meta in SortSaves())
        {
            var item = _tree.CreateItem(root);
            item.SetText(0, meta.Description);
            item.SetText(1, FormatDate(meta.TimeUnix));
            item.SetText(2, MapName(meta.MapPath));
            item.SetText(3, meta.Turn.ToString());
            item.SetMetadata(0, meta.Slot);
        }
    }

    private IEnumerable<SaveMeta> SortSaves() => _sortColumn switch
    {
        0 => _sortAsc ? _saves.OrderBy(m => m.Description) : _saves.OrderByDescending(m => m.Description),
        2 => _sortAsc ? _saves.OrderBy(m => m.MapPath) : _saves.OrderByDescending(m => m.MapPath),
        3 => _sortAsc ? _saves.OrderBy(m => m.Turn) : _saves.OrderByDescending(m => m.Turn),
        _ => _sortAsc ? _saves.OrderBy(m => m.TimeUnix) : _saves.OrderByDescending(m => m.TimeUnix),
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
        var meta = _saves.FirstOrDefault(m => m.Slot == _selectedSlot);
        bool has = meta != null;
        _loadButton.Disabled = !has;
        _deleteButton.Disabled = !has;

        if (!has)
        {
            _detail.AddChild(MakeLabel(_saves.Count == 0 ? "No saved games." : "Select a save.", 14));
            return;
        }
        _detail.AddChild(MakeLabel(meta!.Description, 16));
        _detail.AddChild(MakeLabel($"Map: {MapName(meta.MapPath)}", 13));
        _detail.AddChild(MakeLabel($"Type: {meta.MapType}", 13));
        _detail.AddChild(MakeLabel($"Turn: {meta.Turn}", 13));
        _detail.AddChild(MakeLabel($"Date: {FormatDate(meta.TimeUnix)}", 13));
        _detail.AddChild(MakeLabel("Players:", 13));
        foreach (var s in meta.Slots)
            _detail.AddChild(MakeLabel($"  {s.PlayerId}. {s.Civ} ({s.Kind}) team {s.Team}", 12));
    }

    private void LoadSelected()
    {
        if (_selectedSlot == null) return;
        var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
        cfg.Reset();
        cfg.Mode = GameLaunchConfig.LaunchMode.Load;
        cfg.LoadSlot = _selectedSlot;
        GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");
    }

    private void ConfirmDelete()
    {
        if (_selectedSlot == null) return;
        _deleteConfirm.DialogText = $"Delete '{_selectedSlot}'?";
        _deleteConfirm.PopupCentered();
    }

    private void DeleteSelected()
    {
        if (_selectedSlot == null) return;
        SaveGameManager.Delete(_selectedSlot);
        _selectedSlot = null;
        _saves = SaveGameManager.ListSaves();
        Populate();
        UpdateDetail();
    }

    private static string FormatDate(long timeUnix) =>
        DateTimeOffset.FromUnixTimeSeconds(timeUnix).LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    private static string MapName(string? mapPath) =>
        mapPath == null ? "(generated)" : System.IO.Path.GetFileNameWithoutExtension(mapPath);
}
