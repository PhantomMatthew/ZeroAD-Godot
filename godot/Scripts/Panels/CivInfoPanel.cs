using System.IO;
using System.Linq;
using Godot;
using ZeroAD.Sim.Content;

namespace ZeroAD.Godot;

/// <summary>文明百科页（civinfo）。展示各文明的历史简介 + 文明加成。
/// 镜像原版 gui/reference/civinfo。从 MainMenu → Learn to Play → Civilization Overview 打开。
/// 数据复用 CivDataLoader（同 StructreePanel 的数据源，零新加载逻辑）。</summary>
public sealed partial class CivInfoPanel : ModalPanelBase
{
    private OptionButton _civSelector = null!;
    private Label _civName = null!;
    private Label _civDescription = null!;
    private Label _civHistory = null!;
    private VBoxContainer _bonusesContainer = null!;

    private System.Collections.Generic.Dictionary<string, CivData> _civs = new();

    public CivInfoPanel(int layer = 58) => Layer = layer;

    public override void _Ready()
    {
        LoadCivs();
        var (content, _) = BuildShell("Civilization Overview", 720);

        // 顶部：文明选择器
        var header = new HBoxContainer();
        content.AddChild(header);
        _civSelector = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
        foreach (var civ in _civs.Values.OrderBy(c => c.Name))
            _civSelector.AddItem($"{civ.Name} ({civ.Code})");
        _civSelector.ItemSelected += _ => ShowCiv((int)_civSelector.Selected);
        header.AddChild(_civSelector);

        // 滚动内容区
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(body);
        content.AddChild(scroll);

        // 文明名（大字标题）
        _civName = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _civName.AddThemeFontSizeOverride("font_size", 22);
        body.AddChild(_civName);

        // 简介（一两句）
        _civDescription = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.Fill };
        _civDescription.AddThemeFontSizeOverride("font_size", 14);
        body.AddChild(_civDescription);

        // 加成标题 + 列表容器
        var bonusHeader = new Label { Text = "Civilization Bonuses" };
        bonusHeader.AddThemeFontSizeOverride("font_size", 16);
        body.AddChild(bonusHeader);
        _bonusesContainer = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.Fill };
        _bonusesContainer.AddThemeConstantOverride("separation", 8);
        body.AddChild(_bonusesContainer);

        // 历史（长文）
        var histHeader = new Label { Text = "History" };
        histHeader.AddThemeFontSizeOverride("font_size", 16);
        body.AddChild(histHeader);
        _civHistory = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.Fill };
        _civHistory.AddThemeFontSizeOverride("font_size", 13);
        body.AddChild(_civHistory);

        // 底部 Close
        var closeBtn = new Button { Text = "Close", SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        closeBtn.Pressed += Close;
        content.AddChild(closeBtn);

        // 默认选第一个文明
        if (_civSelector.ItemCount > 0)
        {
            _civSelector.Selected = 0;
            ShowCiv(0);
        }
    }

    private void ShowCiv(int index)
    {
        if (index < 0 || index >= _civSelector.ItemCount) return;
        var civ = _civs.Values.OrderBy(c => c.Name).ElementAt(index);

        _civName.Text = civ.Name;
        _civDescription.Text = civ.Description;
        _civHistory.Text = civ.History;

        // 重建加成列表
        foreach (var child in _bonusesContainer.GetChildren())
            ((Node)child).QueueFree();

        if (civ.Bonuses.Count == 0)
        {
            _bonusesContainer.AddChild(new Label { Text = "(无加成)" });
            return;
        }

        foreach (var bonus in civ.Bonuses)
        {
            var box = new PanelContainer();
            var bg = new StyleBoxFlat { BgColor = new Color(0.1f, 0.09f, 0.08f, 0.85f) };
            bg.SetContentMarginAll(8);
            box.AddThemeStyleboxOverride("panel", bg);

            var vbox = new VBoxContainer();
            box.AddChild(vbox);
            vbox.AddChild(new Label { Text = bonus.Name });
            if (bonus.Description.Length > 0)
                vbox.AddChild(new Label { Text = bonus.Description, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.Fill });
            if (bonus.History.Length > 0)
                vbox.AddChild(new Label { Text = bonus.History, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.Fill, CustomMinimumSize = new Vector2(0, 0) });
            _bonusesContainer.AddChild(box);
        }
    }

    private void LoadCivs()
    {
        string projRoot = ProjectSettings.GlobalizePath("res://");
        foreach (var up in new[] { "..", "../.." })
        {
            var civsPath = Path.GetFullPath(Path.Combine(projRoot, up, "binaries", "data", "mods", "public", "simulation", "data", "civs"));
            if (Directory.Exists(civsPath))
            {
                _civs = CivDataLoader.LoadAll(civsPath);
                return;
            }
        }
        GD.PrintErr("[CivInfo] civs data not found");
    }
}
