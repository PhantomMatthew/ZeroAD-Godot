using System.Collections.Generic;
using Godot;
using ZeroAD.Sim.Content;

namespace ZeroAD.Godot;

/// <summary>模板查看器(原版 reference/viewer/page_viewer.xml + ViewerPage.js):
/// 选中实体的完整信息面板——头像/名称/完整 tooltip(统计+说明+生产/训练/研究列表)。
/// ModalPanelBase 外壳(模态挡鼠标,不暂停 sim);按模板名从 StructreePanel 的
/// TemplateLoader/TechCatalog 现取(与科技树同数据源,不重复装载)。</summary>
public sealed partial class ViewerPanel : ModalPanelBase
{
    private TemplateLoader? _templates;
    private TechCatalog? _techCatalog;
    private string? _currentTemplate;
    private string _currentCiv = "athen";

    private RichTextLabel? _info;
    private TextureRect? _iconRect;
    private Label? _nameLabel;

    protected override void OnOpen()
    {
        // 模板目录懒装载(与 StructreePanel 同数据源;null 时静默跳过)。
        if (_templates == null)
        {
            string? dataRoot = RuntimePaths.FindPublicModRoot();
            if (dataRoot != null)
            {
                _templates = new TemplateLoader(System.IO.Path.Combine(dataRoot, "simulation", "templates"));
                _templates.LoadAllTemplates();
                _techCatalog = TechnologyLoader.LoadAll(
                    System.IO.Path.Combine(dataRoot, "simulation", "data", "technologies"));
            }
        }
        if (_currentTemplate != null)
            DrawTemplate(_currentTemplate);
    }

    /// <summary>打开指定模板(原版 OpenChildPage("page_viewer.xml", {templateName, civ}))。</summary>
    public void OpenFor(string templateName, string civ = "")
    {
        _currentTemplate = templateName;
        if (civ.Length > 0) _currentCiv = civ;
        Open();
    }

    private void DrawTemplate(string templateName)
    {
        if (_templates == null) return;
        var node = _templates.LoadTemplate(templateName);
        var identity = node.GetChild("Identity");
        var specificName = identity.GetChild("SpecificName");
        var genericName = identity.GetChild("GenericName");
        string displayName = specificName.IsOk && specificName.ToString().Length > 0
            ? specificName.ToString()
            : genericName.IsOk ? genericName.ToString() : templateName;

        _nameLabel.Text = displayName;
        if (genericName.IsOk && genericName.ToString().Length > 0
            && genericName.ToString() != specificName.ToString())
            _nameLabel.Text += " (" + genericName.ToString() + ")";

        // 头像(原版 entityIcon:portraits/{Identity/Icon})。
        var iconPath = identity.GetChild("Icon");
        if (iconPath.IsOk && iconPath.ToString().Length > 0)
        {
            var tex = PortraitLoader.Load(iconPath.ToString());
            if (tex != null)
            {
                _iconRect.Texture = tex;
                _iconRect.Visible = true;
            }
            else
            {
                _iconRect.Visible = false;
            }
        }

        // 完整说明(原版 entityStats + entityInfo:StatsFunctions + InfoFunctions
        // 逐块对齐——Cost/Dropsite/Health/Attack/Resistance/Speed/Loot/描述/生产列表)。
        _info.Text = BuildTemplateInfo(templateName);
    }

    /// <summary>模板完整信息(原版 ViewerPage.draw 的 entityStats + entityInfo):
    /// 统计行 + 说明 + 生产/训练/研究列表。</summary>
    private string BuildTemplateInfo(string templateName)
    {
        if (_templates == null) return "";
        var lines = new List<string>();

        var st = _templates.ExtractStats(templateName);
        if (st == null) return "";

        // 统计(原版 StatsFunctions:Cost/Dropsite/Health/Attack/Resistance/Speed/Loot)。
        if (st.FoodCost > 0 || st.WoodCost > 0 || st.StoneCost > 0 || st.MetalCost > 0)
        {
            var costs = new List<string>();
            if (st.FoodCost > 0) costs.Add($"[img=16]res://assets/ui/resources/food_small.png[/img] {st.FoodCost}");
            if (st.WoodCost > 0) costs.Add($"[img=16]res://assets/ui/resources/wood_small.png[/img] {st.WoodCost}");
            if (st.StoneCost > 0) costs.Add($"[img=16]res://assets/ui/resources/stone_small.png[/img] {st.StoneCost}");
            if (st.MetalCost > 0) costs.Add($"[img=16]res://assets/ui/resources/metal_small.png[/img] {st.MetalCost}");
            lines.Add("[b][font_size=13]Cost:[/font_size][/b] " + string.Join("  ", costs));
        }
        if (st.IsDropsite && st.DropsiteTypes.Length > 0)
        {
            var icons = new List<string>();
            foreach (var t in st.DropsiteTypes.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
                icons.Add($"[img=16]res://assets/ui/resources/{t}_small.png[/img]");
            lines.Add("[b][font_size=13]Dropsite for:[/font_size][/b] " + string.Join("  ", icons));
        }
        if (st.HasHealth)
            lines.Add($"[b][font_size=13]Health:[/font_size][/b] {st.MaxHealth}");
        if (st.AttackDamage > 0)
            lines.Add($"[b][font_size=13]Attack:[/font_size][/b] {st.AttackDamage}");
        if (st.HasUnitMotion)
            lines.Add($"[b][font_size=13]Speed:[/font_size][/b] {st.WalkSpeed:F1}");
        if (st.HasLoot && (st.LootFood > 0 || st.LootWood > 0 || st.LootStone > 0 || st.LootMetal > 0 || st.LootXp > 0))
        {
            var loot = new List<string>();
            if (st.LootFood > 0) loot.Add($"[img=16]res://assets/ui/resources/food_small.png[/img] {st.LootFood}");
            if (st.LootWood > 0) loot.Add($"[img=16]res://assets/ui/resources/wood_small.png[/img] {st.LootWood}");
            if (st.LootStone > 0) loot.Add($"[img=16]res://assets/ui/resources/stone_small.png[/img] {st.LootStone}");
            if (st.LootMetal > 0) loot.Add($"[img=16]res://assets/ui/resources/metal_small.png[/img] {st.LootMetal}");
            if (st.LootXp > 0) loot.Add($"[img=16]res://assets/ui/resources/xp.png[/img] {st.LootXp}");
            lines.Add("[b][font_size=13]Loot:[/font_size][/b] " + string.Join("  ", loot));
        }

        // 说明(原版 InfoFunctions:getEntityTooltip/getHistoryTooltip/getDescriptionTooltip)。
        var desc = _templates.LoadTemplate(templateName).GetChild("Identity").GetChild("Tooltip");
        if (desc.IsOk && desc.ToString().Length > 0)
            lines.Add("");
        if (desc.IsOk && desc.ToString().Length > 0)
            lines.Add(desc.ToString());
        var history = _templates.LoadTemplate(templateName).GetChild("Identity").GetChild("History");
        if (history.IsOk && history.ToString().Length > 0)
        {
            lines.Add("");
            lines.Add(history.ToString());
        }

        // 生产/训练/研究列表(原版 InfoFunctions 的 getBuildText/getTrainText/
        // getResearchText/getUpgradeText/getBuiltByText/getTrainedByText/getResearchedByText)。
        if (st.BuildableEntities.Length > 0)
            lines.Add("[b][font_size=13]Builds:[/font_size][/b] " + string.Join(", ",
                st.BuildableEntities.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries)));
        if (st.TrainableEntities.Length > 0)
            lines.Add("[b][font_size=13]Trains:[/font_size][/b] " + string.Join(", ",
                st.TrainableEntities.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries)));
        if (st.ResearchableTechnologies.Length > 0)
            lines.Add("[b][font_size=13]Researches:[/font_size][/b] " + string.Join(", ",
                st.ResearchableTechnologies.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries)));
        if (st.UpgradeToTemplate.Length > 0)
            lines.Add("[b][font_size=13]Upgradable to:[/font_size][/b] " + st.UpgradeToTemplate);

        return string.Join("\n", lines);
    }

    public override void _Ready()
    {
        var (content, status) = BuildShell("Template Viewer", 520);
        status.Text = "";

        var hbox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        hbox.AddThemeConstantOverride("separation", 12);
        content.AddChild(hbox);

        _iconRect = new TextureRect
        {
            CustomMinimumSize = new Vector2(96, 96),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        hbox.AddChild(_iconRect);

        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        hbox.AddChild(right);
        right.AddThemeConstantOverride("separation", 6);

        _nameLabel = new Label { HorizontalAlignment = HorizontalAlignment.Left };
        _nameLabel.AddThemeFontSizeOverride("font_size", 16);
        right.AddChild(_nameLabel);

        _info = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 300),
        };
        _info.AddThemeFontSizeOverride("normal_font_size", 13);
        right.AddChild(_info);

        AddButton(content, "Close", Close, minWidth: 160);
    }
}
