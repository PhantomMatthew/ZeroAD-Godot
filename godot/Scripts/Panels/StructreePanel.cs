using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using ZeroAD.Godot.Structree;
using ZeroAD.Sim.Content;

namespace ZeroAD.Godot;

/// <summary>科技树页（structree）。按 phase（village/town/city）分栏展示文明建筑树。
/// 镜像原版 gui/reference/structree 的布局，简化为流式排列（不做精确坐标）。
/// 从 MainMenu → Learn to Play → Structure Tree 打开。</summary>
public sealed partial class StructreePanel : ModalPanelBase
{
    private OptionButton _civSelector = null!;
    private Label _civInfo = null!;
    private HBoxContainer _columns = null!;

    private Dictionary<string, CivData> _civs = new();
    private TemplateLoader? _templates;
    private TechCatalog? _techCatalog;

    public StructreePanel(int layer = 58) => Layer = layer;  // 注：ModalPanelBase 默认 55

    public override void _Ready()
    {
        LoadData();
        var (content, _) = BuildShell("Structure Tree", 960);

        // 顶部：文明选择器 + 文明简介
        var header = new HBoxContainer();
        content.AddChild(header);
        _civSelector = new OptionButton { CustomMinimumSize = new Vector2(200, 0) };
        foreach (var civ in _civs.Values.OrderBy(c => c.Name))
            _civSelector.AddItem($"{civ.Name} ({civ.Code})");
        _civSelector.ItemSelected += OnCivSelected;
        header.AddChild(_civSelector);
        _civInfo = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        header.AddChild(_civInfo);

        // 中部：3 个 phase 列（ScrollContainer 包裹）
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _columns = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _columns.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(_columns);
        content.AddChild(scroll);

        // 底部：Close
        var closeBtn = new Button { Text = "Close", SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        closeBtn.Pressed += Close;
        content.AddChild(closeBtn);

        // 默认选第一个文明
        if (_civSelector.ItemCount > 0)
        {
            _civSelector.Selected = 0;
            ShowCiv(_civSelector.Selected);
        }
    }

    private void OnCivSelected(long index) => ShowCiv((int)index);

    private void ShowCiv(int index)
    {
        if (index < 0 || index >= _civSelector.ItemCount) return;
        var code = _civs.Values.OrderBy(c => c.Name).ElementAt(index).Code;
        if (!_civs.TryGetValue(code, out var civ)) return;
        _civInfo.Text = civ.History;

        // 清空列
        foreach (var child in _columns.GetChildren())
            ((Node)child).QueueFree();

        if (_templates == null || _techCatalog == null) return;

        var tree = TechTreeBuilder.Build(civ, _templates, _techCatalog);
        foreach (var phase in tree.Phases)
            _columns.AddChild(BuildPhaseColumn(phase));
    }

    private Control BuildPhaseColumn(PhaseColumn phase)
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(300, 0), SizeFlagsVertical = Control.SizeFlags.Fill };
        var titleLabel = new Label { Text = TitleCasePhase(phase.PhaseName), HorizontalAlignment = HorizontalAlignment.Center };
        titleLabel.AddThemeFontSizeOverride("font_size", 18);
        col.AddChild(titleLabel);

        if (phase.Buildings.Count == 0)
        {
            col.AddChild(new Label { Text = "(无建筑)", HorizontalAlignment = HorizontalAlignment.Center });
            return col;
        }

        foreach (var bldg in phase.Buildings)
            col.AddChild(BuildBuildingBox(bldg));
        return col;
    }

    private Control BuildBuildingBox(TreeEntry bldg)
    {
        var box = new PanelContainer { CustomMinimumSize = new Vector2(280, 0) };
        var bg = new StyleBoxFlat { BgColor = new Color(0.1f, 0.09f, 0.08f, 0.9f), BorderColor = new Color(0.4f, 0.35f, 0.25f) };
        bg.SetBorderWidthAll(1); bg.SetContentMarginAll(8);
        box.AddThemeStyleboxOverride("panel", bg);

        var vbox = new VBoxContainer();
        box.AddChild(vbox);

        // 建筑图标 + 名称
        var iconRow = new HBoxContainer();
        var tex = PortraitLoader.Load(bldg.Icon);
        if (tex != null)
            iconRow.AddChild(new TextureRect { Texture = tex, CustomMinimumSize = new Vector2(48, 48), ExpandMode = TextureRect.ExpandModeEnum.FitWidth });
        iconRow.AddChild(new Label { Text = bldg.DisplayName, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        vbox.AddChild(iconRow);

        // 可训练单位
        if (bldg.TrainableUnits.Count > 0)
        {
            vbox.AddChild(new Label { Text = "单位:" });
            foreach (var unit in bldg.TrainableUnits)
            {
                var row = new HBoxContainer();
                var uTex = PortraitLoader.Load(unit.Icon);
                if (uTex != null)
                    row.AddChild(new TextureRect { Texture = uTex, CustomMinimumSize = new Vector2(32, 32), ExpandMode = TextureRect.ExpandModeEnum.FitWidth });
                row.AddChild(new Label { Text = unit.DisplayName });
                vbox.AddChild(row);
            }
        }

        // 可研究科技
        if (bldg.ResearchableTechs.Count > 0)
        {
            vbox.AddChild(new Label { Text = "科技:" });
            foreach (var tech in bldg.ResearchableTechs)
                vbox.AddChild(new Label { Text = $"  {tech.DisplayName}" });
        }
        return box;
    }

    // ── 数据加载（静态，会话外可用）──

    private void LoadData()
    {
        string projRoot = ProjectSettings.GlobalizePath("res://");
        string? dataRoot = null;
        foreach (var up in new[] { "..", "../.." })
        {
            var candidate = Path.GetFullPath(Path.Combine(projRoot, up, "binaries", "data", "mods", "public"));
            if (Directory.Exists(candidate)) { dataRoot = candidate; break; }
        }
        if (dataRoot == null) { GD.PrintErr("[Structree] data root not found"); return; }

        var templatesPath = Path.Combine(dataRoot, "simulation", "templates");
        var techsPath = Path.Combine(dataRoot, "simulation", "data", "technologies");
        var civsPath = Path.Combine(dataRoot, "simulation", "data", "civs");

        _civs = CivDataLoader.LoadAll(civsPath);
        _templates = new TemplateLoader(templatesPath);
        _templates.LoadAllTemplates();
        _techCatalog = TechnologyLoader.LoadAll(techsPath);
        GD.Print($"[Structree] loaded {_civs.Count} civs, {_templates.Cache.Count} templates, {_techCatalog.Technologies.Count} techs");
    }

    private static string TitleCasePhase(string phase)
    {
        // phase_village → Village
        var parts = phase.Split('_');
        return parts.Length > 1 ? char.ToUpper(parts[1][0]) + parts[1].Substring(1) : phase;
    }
}
