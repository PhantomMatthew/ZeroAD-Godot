using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using ZeroAD.Godot.Structree;
using ZeroAD.Sim.Content;

namespace ZeroAD.Godot;

/// <summary>科技树页(structree)。镜像原版 gui/reference/structree 的布局:
/// ModernDialog 大窗 + 红绸标题 "Structure Tree";头部 = 文明徽标圆章 + 文明名 +
/// 史述(滚动) + 右侧 Civilization 下拉;主体 = 三个相位段(金数徽块 I/II/III +
/// 建筑列:专名标题 + 大立绘 + 生产小图标格);底部右置 Close。
/// 从 MainMenu → Learn to Play → Structure Tree / 会话顶栏徽标打开。</summary>
public sealed partial class StructreePanel : ModalPanelBase
{
    private OptionButton _civSelector = null!;
    private TextureRect _emblem = null!;
    private Label _civName = null!;
    private Label _civHistory = null!;
    private VBoxContainer _sections = null!;

    private Dictionary<string, CivData> _civs = new();
    private TemplateLoader? _templates;
    private TechCatalog? _techCatalog;

    // 相位徽块贴图(原版 structree TreeSection 的 I/II/III 金数徽章)。
    private static readonly string[] PhaseEmblems =
    {
        "panel_phase_emblems_village.png", "panel_phase_emblems_town.png", "panel_phase_emblems_city.png",
    };

    /// <summary>文明代码 → 徽标文件名(原版 civData.Emblem 命名约定;与 HUD 同表)。</summary>
    private static readonly Dictionary<string, string> CivEmblemNames = new(System.StringComparer.Ordinal)
    {
        ["athen"] = "athenians", ["spart"] = "spartans", ["gaul"] = "celts",
        ["brit"] = "britons", ["rome"] = "romans", ["kart"] = "carthaginians",
        ["ptol"] = "ptolemies", ["sele"] = "seleucids", ["kush"] = "kushites",
        ["maur"] = "mauryas", ["iber"] = "iberians", ["pers"] = "achaemenids",
        ["theb"] = "thebans", ["mace"] = "macedonians",
    };

    public StructreePanel(int layer = 58) => Layer = layer;  // 注:ModalPanelBase 默认 55

    public override void _Ready()
    {
        LoadData();
        var (content, _) = BuildShell("Structure Tree", 1000);

        // ── 头部:徽标圆章 + 文明名/史述 + 右侧 Civilization 下拉 ──
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        content.AddChild(header);

        _emblem = new TextureRect
        {
            CustomMinimumSize = new Vector2(96, 96),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        header.AddChild(_emblem);

        var nameBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        header.AddChild(nameBox);
        _civName = new Label();
        _civName.AddThemeFontSizeOverride("font_size", 20);
        nameBox.AddChild(_civName);
        var histScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 76),
            SizeFlagsVertical = Control.SizeFlags.ShrinkEnd,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _civHistory = new Label
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _civHistory.AddThemeFontSizeOverride("font_size", 12);
        histScroll.AddChild(_civHistory);
        nameBox.AddChild(histScroll);

        var civPick = new VBoxContainer();
        header.AddChild(civPick);
        var civLbl = new Label { Text = "Civilization:", HorizontalAlignment = HorizontalAlignment.Right };
        civPick.AddChild(civLbl);
        _civSelector = new OptionButton { CustomMinimumSize = new Vector2(200, 0) };
        foreach (var civ in _civs.Values.OrderBy(c => c.Name))
            _civSelector.AddItem(civ.Name);
        _civSelector.ItemSelected += OnCivSelected;
        civPick.AddChild(_civSelector);

        // ── 主体:相位段滚动区(竖滚;横向内容超宽时横滚)──
        // 必须给定最小高宽:面板按内容自动撑高,ExpandFill 的滚动区在自动求高中
        // 最小高=0 → 列内容全被压没(此前"有数据无显示"的成因)。
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(940, 430),
        };
        _sections = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _sections.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(_sections);
        content.AddChild(scroll);

        // ── 底部:Close(右置,原版 CivInfoButton 未移植——文明信息已在头部)──
        var bottomRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        content.AddChild(bottomRow);
        var closeBtn = new Button { Text = "Close", CustomMinimumSize = new Vector2(150, 0) };
        closeBtn.Pressed += Close;
        bottomRow.AddChild(closeBtn);

        // 默认选第一个文明
        if (_civSelector.ItemCount > 0)
        {
            _civSelector.Selected = 0;
            ShowCiv(_civSelector.Selected);
        }
    }

    private void OnCivSelected(long index) => ShowCiv((int)index);

    /// <summary>按文明代码预选(原版 OpenChildPage 的 civ 参数;会话内顶栏徽标进树用)。</summary>
    public void SetCiv(string code)
    {
        if (_civs.Count == 0) LoadData();   // 兜底:首次加载失败(路径/时序)时重试
        var ordered = _civs.Values.OrderBy(c => c.Name).ToList();
        int idx = ordered.FindIndex(c => c.Code == code);
        if (idx < 0) return;
        _civSelector.Selected = idx;
        ShowCiv(idx);
    }

    private void ShowCiv(int index)
    {
        if (index < 0 || index >= _civSelector.ItemCount) return;
        var civ = _civs.Values.OrderBy(c => c.Name).ElementAt(index);
        _civName.Text = civ.Name;
        _civHistory.Text = civ.History;

        // 徽标圆章(session/portraits/emblems/emblem_<名>.png)。
        string emblemName = CivEmblemNames.GetValueOrDefault(civ.Code, "hellenes");
        _emblem.Texture = PortraitLoader.Load($"emblems/emblem_{emblemName}.png");

        foreach (var child in _sections.GetChildren())
            ((Node)child).QueueFree();

        if (_templates == null || _techCatalog == null)
        {
            GD.PrintErr($"[Structree] ShowCiv({civ.Code}): LoadData 未完成");
            return;
        }

        var tree = TechTreeBuilder.Build(civ, _templates, _techCatalog);
        for (int i = 0; i < tree.Phases.Count; i++)
            _sections.AddChild(BuildPhaseSection(tree.Phases[i], i));
    }

    /// <summary>相位段:左 = 金数徽块(村/镇/城),右 = 建筑列横排。</summary>
    private Control BuildPhaseSection(PhaseColumn phase, int phaseIndex)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 10);

        var emblemTex = LoadSessionTex(phaseIndex < PhaseEmblems.Length ? PhaseEmblems[phaseIndex] : PhaseEmblems[0]);
        var emblem = new TextureRect
        {
            Texture = emblemTex,
            CustomMinimumSize = new Vector2(110, 110),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        };
        row.AddChild(emblem);

        var buildingsBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        buildingsBox.AddThemeConstantOverride("separation", 6);
        row.AddChild(buildingsBox);

        if (phase.Buildings.Count == 0)
        {
            buildingsBox.AddChild(new Label { Text = "(无建筑)" });
            return row;
        }
        foreach (var bldg in phase.Buildings)
            buildingsBox.AddChild(BuildBuildingColumn(bldg));
        return row;
    }

    /// <summary>建筑列(原版 EntityBox):专名标题 + 大立绘 + 生产小图标格。</summary>
    private Control BuildBuildingColumn(TreeEntry bldg)
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(132, 0) };
        col.AddThemeConstantOverride("separation", 2);

        var name = new Label
        {
            Text = bldg.DisplayName,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        name.AddThemeFontSizeOverride("font_size", 13);
        col.AddChild(name);

        var tex = PortraitLoader.Load(bldg.Icon);
        if (tex != null)
        {
            col.AddChild(new TextureRect
            {
                Texture = tex,
                CustomMinimumSize = new Vector2(96, 96),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            });
        }

        // 生产图标格(单位+科技,小图标流式换行;原版 ProductionRow 图标阵列)。
        var prod = new HFlowContainer();
        prod.AddThemeConstantOverride("h_separation", 2);
        prod.AddThemeConstantOverride("v_separation", 2);
        foreach (var unit in bldg.TrainableUnits)
        {
            var uTex = PortraitLoader.Load(unit.Icon);
            if (uTex == null) continue;
            prod.AddChild(new TextureRect
            {
                Texture = uTex,
                CustomMinimumSize = new Vector2(28, 28),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                TooltipText = unit.DisplayName,
            });
        }
        foreach (var tech in bldg.ResearchableTechs)
        {
            var tTex = PortraitLoader.Load(tech.Icon);
            if (tTex == null) continue;
            prod.AddChild(new TextureRect
            {
                Texture = tTex,
                CustomMinimumSize = new Vector2(28, 28),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                TooltipText = tech.DisplayName,
            });
        }
        col.AddChild(prod);
        return col;
    }

    /// <summary>ui/session/ 下的贴图(junction 直读,相位徽块用)。</summary>
    private static Texture2D? LoadSessionTex(string file)
    {
        string projRoot = ProjectSettings.GlobalizePath("res://");
        foreach (var up in new[] { "..", "../.." })
        {
            string p = Path.GetFullPath(Path.Combine(projRoot, up,
                "binaries", "data", "mods", "public", "art", "textures", "ui", "session", file));
            if (!File.Exists(p)) continue;
            var img = Image.LoadFromFile(p);
            if (img != null) return ImageTexture.CreateFromImage(img);
        }
        return null;
    }

    // ── 数据加载(静态,会话外可用)──

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
}
