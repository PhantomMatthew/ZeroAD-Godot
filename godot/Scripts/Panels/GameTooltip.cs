using System;
using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot;

/// <summary>自绘游戏内 tooltip(替代 Godot 内建 TooltipText)。
/// 内建 tooltip 画在根视口的基础画布层——本项目的模态面板全在 CanvasLayer 55+,
/// 内建弹窗被整层压住永远不可见。本类在高 CanvasLayer(100)上自建说明卡,样式
/// 逐项对齐原版 referenceTooltip(gui/reference/common/setup.xml + sprites.xml):
/// 黑底 α192 + 金色细框(bkTooltip)、白字、offset 16/24、避让屏幕边缘。
///
/// 内容用富文本行(RichTextLabel,bbcode):标题行 sans-bold-16(原版
/// namePrimaryBig),统计行"标题: 值"的标题为 sans-bold-13(headerFont)——调用方
/// 用 Header(text)/Body(text) 组行;资源数字行可用 ResourceRow(图标+数值)。</summary>
public sealed partial class GameTooltip : CanvasLayer
{
    private static GameTooltip? _instance;

    public static GameTooltip Instance =>
        _instance ??= Create();

    private RichTextLabel _label = null!;
    private PanelContainer _card = null!;
    private Control? _owner;
    private StyleBoxFlat _box = null!;

    private static GameTooltip Create()
    {
        var t = new GameTooltip();
        // 挂到树根(SceneTree.root)而非某个场景——面板销毁不带走 tooltip 层。
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(t);
        return t;
    }

    public override void _Ready()
    {
        Layer = 100;   // 高于全部模态面板(55)与 HUD(45-50)
        ProcessMode = ProcessModeEnum.Always;
        _card = new PanelContainer();

        // bkTooltip(reference/common/sprites.xml):backcolor 0 0 0 192 满幅 +
        // 四边金细线(此处金边 1px,等价 line_horiz/vert.png 视觉)。
        _box = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 192f / 255f),
            BorderColor = new Color(0.78f, 0.66f, 0.40f),   // 金线(line_*.png 观感)
            // 原版 buffer_zone=4:文字到边框 4px(此前 10/6 导致卡上下虚高)。
            ContentMarginLeft = 8, ContentMarginRight = 8,
            ContentMarginTop = 6, ContentMarginBottom = 6,
        };
        _box.SetBorderWidthAll(1);
        _card.AddThemeStyleboxOverride("panel", _box);

        _label = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(0, 0),
        };
        // 原版 tooltip 主字体 sans-14 白字;内容行内另有 bold-16/13 混排(bbcode 控制)。
        _label.AddThemeFontSizeOverride("normal_font_size", 14);
        _label.AddThemeColorOverride("default_color", Colors.White);
        // 紧凑行距:原版 CTooltip 一行 = 字高(buffer 4),Godot RichTextLabel 默认
        // 行距偏大导致卡身过高(用户截图实拍对比)。压到 0(行间只有字高本身)。
        _label.AddThemeConstantOverride("line_separation", 0);
        _label.AddThemeConstantOverride("paragraph_separation", 0);
        _card.AddChild(_label);
        _card.Visible = false;
        _card.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_card);
    }

    public override void _Process(double delta)
    {
        if (_card.Visible && _owner != null)
            PlaceAt(GetViewport().GetMousePosition(), _card.Size);
    }

    /// <summary>给控件挂 tooltip(悬停显示 text,多段用 Header/Body 组好的 bbcode)。</summary>
    public static void Attach(Control control, Func<string> text)
    {
        control.MouseEntered += () => Instance.ShowFor(control, text());
        control.MouseExited += () => Instance.Hide(control);
    }

    // ── 内容构造助手(对齐 g_TooltipTextFormats)──

    /// <summary>标题行(namePrimaryBig:sans-bold-16)。</summary>
    public static string Title(string text) => $"[b][font_size=16]{Escape(text)}[/font_size][/b]";

    /// <summary>实体名行(getEntityNamesFormatted,默认 specific 主名 + generic 次名):
    /// 首字符 sans-bold-16 + 其余大写 sans-bold-12,次名 "(generic)" sans-bold-16。
    /// 无次名/同名 → 整名 bold-16(原版单样式分支)。specific 为空 → generic 整名。</summary>
    public static string NamesFormatted(string? specific, string generic)
    {
        if (string.IsNullOrEmpty(specific))
            return Title(generic);
        if (specific == generic)
            return Title(specific);
        string first = Escape(specific.Substring(0, 1));
        string rest = Escape(specific.Substring(1).ToUpperInvariant());
        return $"[b][font_size=16]{first}[/font_size][font_size=12]{rest}[/font_size][/b]" +
            $" [b][font_size=16]({Escape(generic)})[/font_size][/b]";
    }

    /// <summary>统计块标题(headerFont:sans-bold-13)。</summary>
    public static string Header(string text) => $"[b][font_size=13]{Escape(text)}[/font_size][/b]";

    /// <summary>正文(bodyFont:sans-13)。</summary>
    public static string Body(string text) => $"[font_size=13]{Escape(text)}[/font_size]";

    /// <summary>单位字(unitFont:sans-10 橙色——伤害类型/数值单位)。</summary>
    public static string Unit(string text) =>
        $"[color=orange][font_size=10]{Escape(text)}[/font_size][/color]";

    /// <summary>小字括注(原版 '[font="sans-10"](...)' ——抗性百分数等)。</summary>
    public static string Small(string text) => $"[font_size=10]{Escape(text)}[/font_size]";

    /// <summary>资源行:小图标 + 数值(session/icons/resources/*_small.png 16px——与原版
    /// icon_* sprite size="16 16" 一致)。parts 交替 (图标码, 数值);码除四资源外含
    /// population/time/xp(getEntityCostComponentsTooltipString 的全部费用类型)。</summary>
    public static string ResourceRow(params (string Code, float Amount)[] parts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (code, amount) in parts)
        {
            if (amount <= 0) continue;
            if (sb.Length > 0) sb.Append("  ");
            sb.Append($"[img=16]{IconPath(code)}[/img] {amount:0.###}");
        }
        return sb.ToString();
    }

    private static string Escape(string t) =>
        t.Replace("&", "&amp;").Replace("[", "&#91;").Replace("]", "&#93;");

    /// <summary>图标码 → res:// 路径。资源/费用类直接映射 population/time 等小图;
    /// 采集子类型("food.meat" 等)映射 gui/common/resources/{subtype}.xml 声明的贴图
    /// (food_meat→meat_small、wood_tree→wood_small…),xp 用 icons/promote.png。
    /// 图标须在 res:// 内被 Godot 导入,RichTextLabel 的 [img] 才能加载;由
    /// godot/tools/copy_ui_icons.py 从 binaries 拷入(assets/ 为 gitignored 构建产物)。</summary>
    public static string IconPath(string code) =>
        $"res://assets/ui/resources/{IconFile(code)}";

    private static string IconFile(string code) => code switch
    {
        "population" => "population_small.png",
        "time" => "time_small.png",
        "xp" => "xp.png",
        // 采集子类型 → 原版 resourceIcon 的 icon_{code},贴图见 resources/*.xml。
        "food.fruit" => "fruit_small.png",
        "food.grain" => "grain_small.png",
        "food.meat" => "meat_small.png",
        "food.rice" => "rice_small.png",
        "food.fish" => "fish_small.png",
        "wood.tree" or "stone.rock" or "metal.ore"
            => code.Split('.')[0] + "_small.png",
        _ => code + "_small.png",
    };

    /// <summary>资源小图标路径(旧四资源码用;新码走 IconPath)。</summary>
    public static string ResourceIconPath(string code) => IconPath(code);

    public static string ResourceIconPathOf(string code) => IconPath(code);

    private void ShowFor(Control owner, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _owner = owner;
        _label.Clear();
        _label.AppendText(text);
        // 先定宽再排版:文字换行取决于宽度,必须让 RichTextLabel 在最终宽下
        // 重排完成后才能量高度(同帧量会拿到旧宽的行数 → 高度爆表,用户截图
        // 实拍:2 行内容撑满全屏)。延迟一帧排版后再量。
        _label.CustomMinimumSize = new Vector2(DesiredWidth(text), 0);
        _card.Visible = true;
        CallDeferred(nameof(PlaceDeferred));
    }

    private void PlaceDeferred()
    {
        if (!_card.Visible) return;
        // GetContentHeight = 富文本在当前宽下排完的实际内容高。
        float contentH = _label.GetContentHeight();
        float w = _label.CustomMinimumSize.X;
        _card.Size = new Vector2(w + _box.ContentMarginLeft + _box.ContentMarginRight + 2,
            contentH + _box.ContentMarginTop + _box.ContentMarginBottom + 2);
        _card.CustomMinimumSize = Vector2.Zero;   // 不留旧尺寸
        PlaceAt(GetViewport().GetMousePosition(), _card.Size);
    }

    private void Hide(Control owner)
    {
        if (_owner != owner) return;   // 已切给别的控件,不打断
        _owner = null;
        _card.Visible = false;
    }

    /// <summary>tooltip 文本的期望卡宽:最长纯文本行宽 + 边距,clamp [220, 480]
    /// (原版 maxwidth 480;RichTextLabel 的 FitContent 最小宽会退化到接近 0,
    /// 把每行文字挤成一列窄条——截图实测根因)。</summary>
    private float DesiredWidth(string text)
    {
        var font = ThemeDB.FallbackFont;
        float max = 0;
        foreach (var raw in text.Split('\n'))
        {
            // 去掉 bbcode 标签量纯文本(粗略:剥 [..] 段)。
            string plain = System.Text.RegularExpressions.Regex.Replace(raw, @"\[[^\]]*\]", "");
            float lineW = font.GetStringSize(plain, HorizontalAlignment.Left, -1, 14).X;
            // 资源图标行:[img=16] 计入 20px/个。
            lineW += 20 * System.Text.RegularExpressions.Regex.Matches(raw, @"\[img").Count;
            max = System.MathF.Max(max, lineW);
        }
        return System.Math.Clamp(max + 24, 220f, 480f);
    }

    private void PlaceAt(Vector2 mouse, Vector2 size)
    {
        // 原版 offset = "16 24"(卡在鼠标右下 16,24);避让屏幕边缘。
        float x = mouse.X + 16;
        float y = mouse.Y + 24;
        var vp = GetViewport().GetVisibleRect().Size;
        if (x + size.X > vp.X) x = mouse.X - size.X - 8;
        if (y + size.Y > vp.Y) y = mouse.Y - size.Y - 8;
        _card.Position = new Vector2(MathF.Max(x, 4), MathF.Max(y, 4));
    }
}
