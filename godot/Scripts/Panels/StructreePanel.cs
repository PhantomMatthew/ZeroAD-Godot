using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using ZeroAD.Godot.Structree;
using ZeroAD.Sim.Content;

namespace ZeroAD.Godot;

/// <summary>科技树页(structree)。逐像素镜像原版 gui/reference/structree 的布局:
/// ModernDialog 大窗 + 红绸标题 "Structure Tree";头部 = 文明徽标 96×96 + 文明名
/// (sans-bold-20 白) + 史述滚动区(sans-12 白,80px) + 右上 Civilization 下拉(180×26);
/// 主体 treeSection = 左相位列(48×48 阶段金徽 @ (16,32)) + ProdBar 灰条(136 136 136 102,
/// x40 起通宽,行 r≥1 各一条,左端 20×20 下一阶段图标 @ x42) + TreeDisplay(ModernDarkBoxGold,
/// x72 起)内横向滚动的建筑盒;建筑盒 = ModernDarkBoxGold 底 + 专名标题(sans-12 居中白,20px)
/// + 48×48 立绘 @ y24 + 生产图标行(20×20,步进 22,行带 [80+28r, +24],单位在前科技在后,
/// 行内居中);盒宽 = max(96, 题名宽, 最宽行宽),盒高 = 80+28·(阶段数−阶段),盒距 8/4。
/// 底部右置 Civilization Overview(197×32) + Close(192×32)。
/// 头部名称/徽标/史述取自 special/players/{civ} 模板的 Identity(原版 loadCivFiles
/// 同款数据源——civ JSON 里没有 History)。所有几何公式移植自原版
/// TreeSection.getPositionOffset / EntityBox / ProductionIcon / PhaseIdent。</summary>
public sealed partial class StructreePanel : ModalPanelBase
{
    // ── 原版布局常量(gui/reference/structree: styles.xml + EntityBox.js +
    // ProductionIcon.js + TreeSection.js + PhaseIdent.js)──
    private const float CaptionHeight = 20;       // StructNamePrimary: 0 0 100% 20
    private const float BuildingIconY = 24;       // StructIcon: 50%-24 8+16 50%+24 8+16+48
    private const float BuildingIconSize = 48;
    private const float IconAndCaptionHeight = 80; // EntityBox.IconAndCaptionHeight = icon.bottom(72)+IconPadding(8)
    private const float BoxHMargin = 8;            // EntityBox.HMargin(首盒左 8,盒间 +HMargin/2)
    private const float BoxVMargin = 12;           // EntityBox.VMargin
    private const float BoxMinWidth = 96;          // EntityBox.MinWidth
    private const float ProdIcon = 20;             // ProdBox: 2 2 22 22 → 20×20
    private const float ProdMargin = 2;            // ProdBox hMargin/vMargin
    private const float ProdStride = 22;           // ProductionIcon.rowWidth = 20+2
    private const float ProdRowHeight = 28;        // ProductionIcon.rowHeight = 24+2·2
    private const float ProdBandHeight = 24;       // ProdBar 高(paddedHeight)
    private const float BarLeft = 40;              // ProdBar size: 40 0 100% 0
    private const float PhaseIconX = 16;           // phase[n]_icon: 16 32 16+48 32+48
    private const float PhaseIconY = 32;
    private const float PhaseIconSize = 48;
    private const float TreeDisplayLeft = 72;      // TreeDisplay: 48+16+8 0 100% 100%

    /// <summary>ProdBar 底色(sprites.xml: backcolor 136 136 136 102)。</summary>
    private static readonly Color ProdBarColor = new(136f / 255f, 136f / 255f, 136f / 255f, 102f / 255f);

    // 相位徽块:阶段科技 JSON 的 icon(portraits/technologies/ 下)——原版的金色罗马数字徽。
    private static readonly string[] PhaseTechs = { "phase_village", "phase_town", "phase_city" };

    private OptionButton _civSelector = null!;
    private TextureRect _emblem = null!;
    private Label _civName = null!;
    private Label _civHistory = null!;
    private Control _treeSection = null!;    // treeSection:灰条层 + 相位列 + TreeDisplay
    private Control _bars = null!;           // ProdBar 灰条层(通宽,垫在 TreeDisplay 下)
    private Control _phaseIcons = null!;     // 相位列(48×48 金徽)
    private Control _structures = null!;     // 滚动内容:绝对定位的建筑盒

    private Dictionary<string, CivData> _civs = new();
    private TemplateLoader? _templates;
    private TechCatalog? _techCatalog;

    public StructreePanel(int layer = 58) => Layer = layer;  // 注:ModalPanelBase 默认 55

    /// <summary>TreeSection.getPositionOffset:80·i + 12·(i+1) + 28·(P·i − (i−1)·i/2)。</summary>
    private static float PhaseTop(int idx, int phaseCount) =>
        IconAndCaptionHeight * idx + BoxVMargin * (idx + 1)
        + ProdRowHeight * (phaseCount * idx - (idx - 1) * idx / 2f);

    /// <summary>建筑盒高 = offset(i+1) − offset(i) − VMargin = 80 + 28·(P−i)。</summary>
    private static float BoxHeight(int phaseIdx, int phaseCount) =>
        IconAndCaptionHeight + ProdRowHeight * (phaseCount - phaseIdx);

    public override void _Ready()
    {
        LoadData();
        var (content, _) = BuildShell("Structure Tree", 1000);

        // ── 头部:徽标 96×96 + 文明名/史述 + 右上 Civilization 下拉 ──
        // 原版:emblem 16 24 112 120;name 114 24 .. 56;history 114 52 .. 132(80px 滚动)。
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 2);   // 原版 name 起点 114 = 徽标右 112 + 2
        content.AddChild(header);

        _emblem = new TextureRect
        {
            CustomMinimumSize = new Vector2(96, 96),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        };
        header.AddChild(_emblem);

        var nameBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        nameBox.AddThemeConstantOverride("separation", 0);
        header.AddChild(nameBox);

        // 名行:文明名(左,20 白)+ Civilization: 标签(右对齐,16 白)+ 下拉(180×26)。
        // 原版 CivSelectDropdown:heading 右对齐止于下拉左 8px,下拉 100%-180 8 100% 34。
        var nameRow = new HBoxContainer();
        nameRow.AddThemeConstantOverride("separation", 8);
        nameBox.AddChild(nameRow);
        _civName = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _civName.AddThemeFontSizeOverride("font_size", 20);
        _civName.AddThemeColorOverride("font_color", Colors.White);
        nameRow.AddChild(_civName);
        var civLbl = new Label
        {
            Text = "Civilization:",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        civLbl.AddThemeFontSizeOverride("font_size", 16);
        civLbl.AddThemeColorOverride("font_color", Colors.White);
        nameRow.AddChild(civLbl);
        _civSelector = new OptionButton { CustomMinimumSize = new Vector2(180, 26) };
        // 原版 loadCivFiles:Name 取自 special/players/{civ} 模板 Identity/GenericName
        // (civ JSON 没有 Name 字段,直接显示会退化成 "spart" 这类代码)。
        foreach (var civ in OrderedCivs())
            _civSelector.AddItem(CivDisplayName(civ));
        _civSelector.ItemSelected += OnCivSelected;
        StyleDropdown(_civSelector);
        nameRow.AddChild(_civSelector);

        var histScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 80),
            SizeFlagsVertical = Control.SizeFlags.ShrinkEnd,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _civHistory = new Label
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _civHistory.AddThemeFontSizeOverride("font_size", 12);
        _civHistory.AddThemeColorOverride("font_color", Colors.White);
        histScroll.AddChild(_civHistory);
        StyleScrollbar(histScroll.GetVScrollBar(), vertical: true);
        nameBox.AddChild(histScroll);

        // ── 主体:treeSection(0 135 100%-16 100%-66)──
        // 绝对定位区(原版布局即绝对坐标):灰条层(通宽,垫底层) + 相位列 + TreeDisplay(x72,金边
        // 暗盒)内横向滚动。内容高 = PhaseTop(P) − VMargin;固定不可竖滚(原版无竖滚)。
        int phaseCount = PhaseTechs.Length;
        float treeHeight = PhaseTop(phaseCount, phaseCount) - BoxVMargin;
        _treeSection = new Control { CustomMinimumSize = new Vector2(0, treeHeight) };
        content.AddChild(_treeSection);

        _bars = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _bars.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _treeSection.AddChild(_bars);
        _phaseIcons = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _phaseIcons.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _treeSection.AddChild(_phaseIcons);

        var treeDisplay = new Panel();
        treeDisplay.AddThemeStyleboxOverride("panel", UITheme.MakeModernDarkBox());
        treeDisplay.AnchorRight = 1; treeDisplay.AnchorBottom = 1;
        treeDisplay.OffsetLeft = TreeDisplayLeft;
        treeDisplay.ClipContents = true;
        _treeSection.AddChild(treeDisplay);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        treeDisplay.AddChild(scroll);
        StyleScrollbar(scroll.GetHScrollBar(), vertical: false);
        // 注意:滚动内容不给最小高度——否则 ScrollContainer 的最小高 = 内容高 + 横滚条 15,
        // 超过锚定高度时被钳到最小尺寸,底部横滚条被 treeDisplay 裁掉(此前滚动条隐形的原因)。
        _structures = new Control();
        scroll.AddChild(_structures);

        // ── 底部:Civilization Overview + Close(原版 197×32 / 192×32,右置)──
        var bottomRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        bottomRow.AddThemeConstantOverride("separation", 8);
        content.AddChild(bottomRow);
        var overviewBtn = MakeBottomButton("Civilization Overview", 197);
        overviewBtn.TooltipText = "Open the civilization overview.";
        overviewBtn.Pressed += () =>
        {
            var civPanel = new CivInfoPanel();
            AddChild(civPanel);
            civPanel.Open();
        };
        bottomRow.AddChild(overviewBtn);
        var closeBtn = MakeBottomButton("Close", 192);
        closeBtn.Pressed += Close;
        bottomRow.AddChild(closeBtn);

        // 默认选第一个文明
        if (_civSelector.ItemCount > 0)
        {
            _civSelector.Selected = 0;
            ShowCiv(_civSelector.Selected);
        }
    }

    /// <summary>底部 StoneButton(主题自带石纹);原版 structree 按钮文案 14px 白。</summary>
    private static Button MakeBottomButton(string label, float width)
    {
        var btn = new Button
        {
            Text = Localization.Tr(label),
            CustomMinimumSize = new Vector2(width, 32),
        };
        btn.AddThemeFontSizeOverride("font_size", 14);
        btn.AddThemeColorOverride("font_color", Colors.White);
        btn.AddThemeColorOverride("font_hover_color", Colors.White);
        return btn;
    }

    /// <summary>ModernDropDown 细节:主题已给半透黑盒+金线;补原版金色下拉箭头
    /// (global/modern/dropdown-arrow.png)与深色弹出列表。</summary>
    private static void StyleDropdown(OptionButton dropdown)
    {
        var arrow = LoadModernTexture("dropdown-arrow.png");
        if (arrow != null) dropdown.AddThemeIconOverride("arrow", arrow);
        dropdown.Alignment = HorizontalAlignment.Left;   // ModernDropDown: text_align="left"
        var popup = dropdown.GetPopup();
        var bg = UITheme.MakeModernDarkBox();
        bg.BgColor = new Color(12f / 255f, 12f / 255f, 12f / 255f, 0.95f);  // 弹出列表需近乎不透明
        popup.AddThemeStyleboxOverride("panel", bg);
        popup.AddThemeColorOverride("font_color", Colors.White);
        popup.AddThemeColorOverride("font_hover_color", Colors.White);
    }

    /// <summary>ModernScrollBar(setup.xml):宽 15;轨道 = scroll-background-*.png
    /// 浅灰圆角条,滑块 = scrollbar.png 金圆钮。原版滑块固定 15px(min=max=15);
    /// Godot 滑块随内容比例拉伸,给 7px 纹理边距保住两端圆头。素材缺失回退平色。</summary>
    private static void StyleScrollbar(ScrollBar bar, bool vertical)
    {
        var trackTex = LoadModernTexture(vertical
            ? "scroll-background-vertical.png" : "scroll-background-horizontal.png");
        StyleBox track;
        if (trackTex != null)
        {
            track = new StyleBoxTexture { Texture = trackTex };
            ((StyleBoxTexture)track).SetTextureMarginAll(7);
        }
        else
        {
            var flat = new StyleBoxFlat
            {
                BgColor = new Color(43f / 255f, 42f / 255f, 40f / 255f),
                BorderColor = Colors.Black,
            };
            flat.SetBorderWidthAll(1);
            track = flat;
        }
        bar.AddThemeStyleboxOverride("scroll", track);
        bar.AddThemeStyleboxOverride("scroll_focus", track);

        var knobTex = LoadModernTexture("scrollbar.png");
        StyleBox knob;
        if (knobTex != null)
        {
            knob = new StyleBoxTexture { Texture = knobTex };
            ((StyleBoxTexture)knob).SetTextureMarginAll(7);
        }
        else
        {
            var flatKnob = new StyleBoxFlat { BgColor = new Color(0.77f, 0.66f, 0.25f) };
            flatKnob.SetCornerRadiusAll(7);
            knob = flatKnob;
        }
        bar.AddThemeStyleboxOverride("grabber", knob);
        bar.AddThemeStyleboxOverride("grabber_highlight", knob);
        bar.AddThemeStyleboxOverride("grabber_pressed", knob);
        bar.CustomMinimumSize = new Vector2(15, 15);
    }

    /// <summary>从 binaries 的 mods/mod modern 贴图目录读一张图;junction 缺失时返回 null。</summary>
    private static Texture2D? LoadModernTexture(string file)
    {
        string? binDir = StoneButtonStyle.FindBinariesDir();
        if (binDir == null) return null;
        string path = Path.Combine(binDir,
            "data", "mods", "mod", "art", "textures", "ui", "global", "modern", file);
        var img = Image.LoadFromFile(path);
        return img != null ? ImageTexture.CreateFromImage(img) : null;
    }

    /// <summary>文明显示名(原版 loadCivFiles):special/players/{civ} 模板
    /// Identity/GenericName;模板缺失回退 civ JSON Name(再退 Code)。</summary>
    private string CivDisplayName(CivData civ)
    {
        if (_templates != null && _templates.Cache.TryGetValue($"special/players/{civ.Code}", out var node))
        {
            var gn = node.GetChild("Identity").GetChild("GenericName").Value;
            if (!string.IsNullOrWhiteSpace(gn)) return gn;
        }
        return civ.Name;
    }

    /// <summary>下拉排序(原版 sortNameIgnoreCase:按显示名忽略大小写)。</summary>
    private List<CivData> OrderedCivs() =>
        _civs.Values.OrderBy(CivDisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    private void OnCivSelected(long index) => ShowCiv((int)index);

    /// <summary>按文明代码预选(原版 OpenChildPage 的 civ 参数;会话内顶栏徽标进树用)。</summary>
    public void SetCiv(string code)
    {
        if (_civs.Count == 0) LoadData();   // 兜底:首次加载失败(路径/时序)时重试
        var ordered = OrderedCivs();
        int idx = ordered.FindIndex(c => c.Code == code);
        if (idx < 0) return;
        _civSelector.Selected = idx;
        ShowCiv(idx);
    }

    private void ShowCiv(int index)
    {
        if (index < 0 || index >= _civSelector.ItemCount) return;
        var civ = OrderedCivs()[index];

        // 名称/徽标/史述:special/players/{civ} 模板的 Identity(原版 loadCivFiles
        // 同款:GenericName/Icon/History)。模板缺失时回退 civ JSON。
        string displayName = civ.Name;
        string history = civ.History;
        string emblemIcon = "";
        if (_templates != null && _templates.Cache.TryGetValue($"special/players/{civ.Code}", out var playerNode))
        {
            var identity = playerNode.GetChild("Identity");
            if (identity.IsOk)
            {
                var gn = identity.GetChild("GenericName").Value;
                if (!string.IsNullOrWhiteSpace(gn)) displayName = gn;
                var h = identity.GetChild("History").Value;
                if (!string.IsNullOrWhiteSpace(h)) history = h;
                var ic = identity.GetChild("Icon").Value;
                if (!string.IsNullOrWhiteSpace(ic)) emblemIcon = ic;   // 如 "emblems/emblem_spartans.png"
            }
        }
        _civName.Text = displayName;
        // 模板里的换行写作字面 "\n"(原版由 GUI 文本引擎转义),这里手动还原。
        _civHistory.Text = history.Replace("\\n", "\n");
        _emblem.Texture = emblemIcon.Length > 0 ? PortraitLoader.Load(emblemIcon) : null;

        foreach (var child in _structures.GetChildren())
            ((Node)child).QueueFree();
        foreach (var child in _bars.GetChildren())
            ((Node)child).QueueFree();
        foreach (var child in _phaseIcons.GetChildren())
            ((Node)child).QueueFree();

        if (_templates == null || _techCatalog == null)
        {
            ZeroAD.Sim.Diag.Err("Structree", $"ShowCiv({civ.Code}): LoadData 未完成");
            return;
        }

        var tree = TechTreeBuilder.Build(civ, _templates, _techCatalog);
        int phaseCount = tree.Phases.Count;
        float contentWidth = 0;
        for (int p = 0; p < phaseCount; p++)
        {
            float top = PhaseTop(p, phaseCount);

            // 相位徽:48×48 阶段科技金徽 @ (16, 段顶+32)(phase[n]_icon)。
            AddPhaseIcon(_phaseIcons, p, civ, PhaseIconX, top + PhaseIconY, PhaseIconSize);

            // ProdBar 灰条:行 r≥1 各一条(PhaseIdent.draw:bar[r−1] 挂 phase[p+r] 图标)。
            for (int r = 1; r + p < phaseCount; r++)
            {
                float bandTop = top + IconAndCaptionHeight + ProdRowHeight * r;
                var bar = new ColorRect
                {
                    Color = ProdBarColor,
                    AnchorRight = 1,
                    OffsetLeft = BarLeft,
                    OffsetTop = bandTop,
                    OffsetBottom = bandTop + ProdBandHeight,
                    OffsetRight = 0,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                _bars.AddChild(bar);
                AddPhaseIcon(_bars, p + r, civ, BarLeft + ProdMargin, bandTop + ProdMargin, ProdIcon);
            }

            // 建筑盒(StructureBox.draw):left = 8 + running;width = max(96, 题名, 最宽行)。
            float running = 0;
            foreach (var bldg in tree.Phases[p].Buildings)
            {
                var box = BuildBuildingBox(bldg, p, phaseCount, out float boxWidth);
                box.Position = new Vector2(BoxHMargin + running, top);
                running += boxWidth + BoxHMargin / 2;
                _structures.AddChild(box);
            }
            contentWidth = Mathf.Max(contentWidth, BoxHMargin + running);
        }
        // 只给宽度(驱动横向滚动范围);高度保持 0,见 _Ready 注释。
        _structures.CustomMinimumSize = new Vector2(contentWidth, 0);
    }

    /// <summary>相位科技图标(PhaseIdent.drawPhaseIcon):优先 {phase}_{civ},回退
    /// {phase}_generic 再回退 {phase};图标在 portraits/technologies/ 下。</summary>
    private void AddPhaseIcon(Control parent, int phaseIdx, CivData civ,
        float x, float y, float size)
    {
        if (_techCatalog == null || phaseIdx >= PhaseTechs.Length) return;
        string baseName = PhaseTechs[phaseIdx];
        string icon = "";
        string tooltip = "";
        foreach (var candidate in new[] { $"{baseName}_{civ.Code}", $"{baseName}_generic", baseName })
        {
            if (_techCatalog.Technologies.TryGetValue(candidate, out var tech) && tech.Icon.Length > 0)
            {
                icon = tech.Icon;
                tooltip = tech.GenericName;
                break;
            }
        }
        var tex = icon.Length > 0 ? PortraitLoader.Load("technologies/" + icon) : null;
        if (tex == null) return;
        var phaseIcon = new TextureRect
        {
            // ExpandMode 必须先于 Texture/Size 赋值:默认 KeepSize 会把最小尺寸设为贴图
            // 原生尺寸,Control.Size 赋值钳到最小尺寸 → 图标渲染成原图大小。
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = tex,
            Position = new Vector2(x, y),
            Size = new Vector2(size, size),
        };
        // 内建 TooltipText 画在基础画布层,被 CanvasLayer 55 的面板整层压住(永不可见)
        // ——走自绘 GameTooltip(高 CanvasLayer 100)。
        string phaseTip = tooltip;
        GameTooltip.Attach(phaseIcon, () => phaseTip);
        parent.AddChild(phaseIcon);
    }

    /// <summary>建筑盒(原版 StructBox):ModernDarkBoxGold 底 + 专名标题(0 0 100% 20,
    /// sans-12 居中白)+ 48×48 立绘(50%±24, y24)+ 生产图标行。行 r 行带 = [80+28r, +24],
    /// 图标 20×20 @ +2;行宽 n·22+2,整体行内居中;单位在前科技在后(ProductionRowManager)。</summary>
    private Control BuildBuildingBox(TreeEntry bldg, int phaseIdx, int phaseCount, out float boxWidth)
    {
        // 生产项按物品阶段分行:rowIdx = max(0, 物品阶段 − 建筑阶段)。
        // tip 在此构建完整说明(单位:名称/费用/统计/描述;科技:名称/费用)。
        var rows = new List<List<(Texture2D? Tex, string Tip)>>();
        void AddEntry(Texture2D? tex, string tip, int itemPhase)
        {
            int r = System.Math.Max(0, itemPhase - phaseIdx);
            while (rows.Count <= r) rows.Add(new List<(Texture2D?, string)>());
            rows[r].Add((tex, tip));
        }
        foreach (var u in bldg.TrainableUnits)
            AddEntry(PortraitLoader.Load(u.Icon), BuildEntityTooltip(u), u.PhaseIndex);
        foreach (var t in bldg.ResearchableTechs)
            if (t.Icon.Length > 0)
                AddEntry(PortraitLoader.Load("technologies/" + t.Icon),
                    BuildProdTooltip(null, t.DisplayName, null, t), t.PhaseIndex);

        float maxRowWidth = rows.Count > 0 ? rows.Max(r => r.Count) * ProdStride + ProdMargin : 0;
        float captionWidth = MeasureCaptionWidth(bldg.DisplayName);
        boxWidth = Mathf.Max(BoxMinWidth, Mathf.Max(captionWidth, maxRowWidth));

        var box = new Panel { Size = new Vector2(boxWidth, BoxHeight(phaseIdx, phaseCount)) };
        box.AddThemeStyleboxOverride("panel", UITheme.MakeModernDarkBox());

        var caption = new Label
        {
            Text = bldg.DisplayName,
            Position = new Vector2(0, 0),
            Size = new Vector2(boxWidth, CaptionHeight),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        caption.AddThemeFontSizeOverride("font_size", 12);
        caption.AddThemeColorOverride("font_color", Colors.White);
        box.AddChild(caption);

        var tex = PortraitLoader.Load(bldg.Icon);
        if (tex != null)
        {
            var bigIcon = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,   // 见 AddPhaseIcon 注释
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Texture = tex,
                Position = new Vector2(boxWidth / 2 - BuildingIconSize / 2, BuildingIconY),
                Size = new Vector2(BuildingIconSize, BuildingIconSize),
            };
            string bigTip = BuildEntityTooltip(bldg);
            GameTooltip.Attach(bigIcon, () => bigTip);
            box.AddChild(bigIcon);
        }

        for (int r = 0; r < rows.Count; r++)
        {
            if (rows[r].Count == 0) continue;
            // ProductionRow.finishDraw:rowWidth = n·22+2,row.left = 50% − rowWidth/2,
            // 图标自 +2 起步进 22;行带 [80+28r, +24],图标顶 +2。
            float rowWidth = rows[r].Count * ProdStride + ProdMargin;
            float x = boxWidth / 2 - rowWidth / 2 + ProdMargin;
            float y = IconAndCaptionHeight + ProdRowHeight * r + ProdMargin;
            foreach (var (iconTex, tip) in rows[r])
            {
                var prodIcon = new TextureRect
                {
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,   // 见 AddPhaseIcon 注释
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    Texture = iconTex,
                    Position = new Vector2(x, y),
                    Size = new Vector2(ProdIcon, ProdIcon),
                };
                string prodTip = tip;
                GameTooltip.Attach(prodIcon, () => prodTip);
                box.AddChild(prodIcon);
                x += ProdStride;
            }
        }
        return box;
    }

    /// <summary>实体完整说明——逐块逐序对齐原版 EntityBox.compileTooltip:
    /// TooltipFunctions = [getEntityNamesFormatted, getEntityCostTooltip,
    /// getEntityTooltip, getAurasTooltip] + StatsFunctions
    /// [Dropsite, Health, Attack, Healer, Resistance, Garrison, Turrets,
    /// Projectiles, Speed, Gather, Supply, Treasure, PopBonus, Trickle,
    /// Upkeep, Loot] + "\nClick for more information."
    /// 截图对照(Apothēkē 卡):专名为主名,generic 括注同行;
    /// Resistance 百分比 = 100 − round(0.9^level×100)(tooltips.js 同款)。</summary>
    private string BuildEntityTooltip(TreeEntry entry)
    {
        var lines = new List<string>();
        string? specificName = null;
        if (_templates != null && _templates.Cache.TryGetValue(entry.Template, out var node))
        {
            var identity = node.GetChild("Identity");
            var sp = identity.GetChild("SpecificName");
            if (sp.IsOk && sp.ToString().Length > 0) specificName = sp.ToString();
        }
        // getEntityNamesFormatted:默认 howtoshownames=0 → specific 为主。
        // 主名 bold-16;有次名时同行 "(generic)"(截图:Apothēkē (Storehouse))。
        lines.Add(specificName != null
            ? $"{GameTooltip.Title(specificName)} {GameTooltip.SecondaryInline($"({entry.DisplayName})")}"
            : GameTooltip.Title(entry.DisplayName));
        if (_templates == null) return Join();
        if (!_templates.Cache.TryGetValue(entry.Template, out var node2)) return Join();

        var identity2 = node2.GetChild("Identity");
        try
        {
            var st = _templates.ExtractStats(entry.Template);
            if (st == null) return Join();

            // getEntityCostTooltip:"Cost:" 粗头 + 图标数值行。
            var cost = GameTooltip.ResourceRow(
                ("food", st.FoodCost), ("wood", st.WoodCost),
                ("stone", st.StoneCost), ("metal", st.MetalCost));
            if (cost.Length > 0)
                lines.Add($"{GameTooltip.Header("Cost:")} {cost}");

            // getEntityTooltip:描述正文 13px。
            var desc = identity2.GetChild("Tooltip");
            if (desc.IsOk && desc.ToString().Length > 0)
                lines.Add(GameTooltip.Body(desc.ToString()));

            // getResourceDropsiteTooltip:"Dropsite for:" + 图标行(Types 空格分隔)。
            if (st.IsDropsite && st.DropsiteTypes.Length > 0)
            {
                var icons = new List<string>();
                foreach (var t in st.DropsiteTypes.Split((char[]?)null,
                    System.StringSplitOptions.RemoveEmptyEntries))
                    icons.Add($"[img=16]{GameTooltip.ResourceIconPathOf(t)}[/img]");
                if (icons.Count > 0)
                    lines.Add($"{GameTooltip.Header("Dropsite for:")} {string.Join("  ", icons)}");
            }

            // getHealthTooltip
            if (st.HasHealth)
                lines.Add($"{GameTooltip.Header("Health:")} {GameTooltip.Body(st.MaxHealth.ToString())}");

            // getResistanceTooltip:"Resistance:" + "Damage: L Hack (P%), ..." 小字百分数。
            if (st.ResistanceHack != 0 || st.ResistancePierce != 0 || st.ResistanceCrush != 0)
            {
                var parts = new List<string>();
                void Dmg(float lvl, string name)
                {
                    if (lvl == 0) return;
                    int pct = 100 - (int)System.Math.Round(System.Math.Pow(0.9, lvl) * 100);
                    parts.Add($"{GameTooltip.Unit($"{lvl:F1} {name}")} {GameTooltip.Small($"({pct}%)")}");
                }
                Dmg(st.ResistanceHack, "Hack");
                Dmg(st.ResistancePierce, "Pierce");
                Dmg(st.ResistanceCrush, "Crush");
                if (parts.Count > 0)
                    lines.Add($"{GameTooltip.Header("Resistance:")} {GameTooltip.Header("Damage:")}\n  {string.Join(", ", parts)}");
            }

            // getAttackTooltip(简化:总伤)
            if (st.AttackDamage > 0)
                lines.Add($"{GameTooltip.Header("Attack:")} {GameTooltip.Body(st.AttackDamage.ToString())}");

            // getSpeedTooltip:"Speed: 8.0 / 20.0"(walk/run)。
            if (st.WalkSpeed > 0.01f)
                lines.Add($"{GameTooltip.Header("Speed:")} {GameTooltip.Body(st.WalkSpeed.ToString("F1"))}");

            // getLootTooltip:"Loot:" + 图标数值行。
            var loot = GameTooltip.ResourceRow(
                ("food", st.LootFood), ("wood", st.LootWood),
                ("stone", st.LootStone), ("metal", st.LootMetal));
            if (st.HasLoot && loot.Length > 0)
                lines.Add($"{GameTooltip.Header("Loot:")} {loot}");
        }
        catch { /* 统计缺失不阻塞名称/描述 */ }

        // getTemplateViewerOnClickTooltip(compileTooltip 尾行,截图实见)。
        lines.Add(GameTooltip.Body("Click for more information."));
        return Join();

        string Join() => string.Join('\n', lines);
    }

    /// <summary>科技完整说明:标题 + Cost: 图标行 + 研究时间 + 效果摘要(tooltip)
    /// + 长文说明(description)+ 解锁提示(requirementsTooltip)。</summary>
    private string BuildTechTooltip(string techName)
    {
        if (_techCatalog != null && _techCatalog.Technologies.TryGetValue(techName, out var tech))
        {
            var lines = new List<string> { GameTooltip.Title(tech.GenericName) };
            var res = GameTooltip.ResourceRow(
                ("food", tech.Food), ("wood", tech.Wood),
                ("stone", tech.Stone), ("metal", tech.Metal));
            if (res.Length > 0)
                lines.Add($"{GameTooltip.Header("Cost:")} {res}");
            if (tech.ResearchTime > 0)
                lines.Add($"{GameTooltip.Header("Time:")} {GameTooltip.Body($"{tech.ResearchTime:0}s")}");
            if (tech.Tooltip.Length > 0)
                lines.Add(GameTooltip.Body(tech.Tooltip));
            if (tech.Description.Length > 0)
                lines.Add(GameTooltip.Body(tech.Description));
            if (tech.RequirementsTooltip.Length > 0)
                lines.Add(GameTooltip.Body(tech.RequirementsTooltip));
            lines.Add(GameTooltip.Body("Click for more information."));
            return string.Join('\n', lines);
        }
        return GameTooltip.Title(techName);
    }

    /// <summary>生产行图标的说明:单位→BuildEntityTooltip(模板名查回);
    /// 科技(TechEntry 无模板名)→名称(+科技描述需 catalog 名,此处按显示名回退)。</summary>
    private string BuildProdTooltip(Texture2D? tex, string tip, TreeEntry? unit, TechEntry? tech)
    {
        if (unit != null) return BuildEntityTooltip(unit);
        if (tech != null)
        {
            // TechEntry 无原始科技名;用 DisplayName 反查 catalog(名称唯一性足够)。
            if (_techCatalog != null)
            {
                foreach (var kv in _techCatalog.Technologies)
                    if (kv.Value.GenericName == tech.DisplayName)
                        return BuildTechTooltip(kv.Key);
            }
            return tech.DisplayName;
        }
        return tip;
    }

    /// <summary>题名宽(EntityBox.captionWidth):12px 字宽;决定盒宽上限之一。</summary>
    private float MeasureCaptionWidth(string text)
    {
        var font = ThemeDB.FallbackFont;
        return font.GetStringSize(text, HorizontalAlignment.Left, -1, 12).X;
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
        if (dataRoot == null) { ZeroAD.Sim.Diag.Err("Structree", "data root not found"); return; }

        var templatesPath = Path.Combine(dataRoot, "simulation", "templates");
        var techsPath = Path.Combine(dataRoot, "simulation", "data", "technologies");
        var civsPath = Path.Combine(dataRoot, "simulation", "data", "civs");

        _civs = CivDataLoader.LoadAll(civsPath);
        _templates = new TemplateLoader(templatesPath);
        _templates.LoadAllTemplates();
        _techCatalog = TechnologyLoader.LoadAll(techsPath);
        ZeroAD.Sim.Diag.Log("Structree", $"loaded {_civs.Count} civs, {_templates.Cache.Count} templates, {_techCatalog.Technologies.Count} techs");
    }
}
