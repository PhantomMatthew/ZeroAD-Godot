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
            ContentMarginLeft = 4 + 6, ContentMarginRight = 4 + 6,
            ContentMarginTop = 4 + 4, ContentMarginBottom = 4 + 6,
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
        _card.AddChild(_label);
        _card.Visible = false;
        _card.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_card);
    }

    public override void _Process(double delta)
    {
        if (_card.Visible && _owner != null)
            PlaceAt(GetViewport().GetMousePosition());
    }

    /// <summary>给控件挂 tooltip(悬停显示 text,多段用 Header/Body 组好的 bbcode)。</summary>
    public static void Attach(Control control, Func<string> text)
    {
        control.MouseEntered += () => Instance.ShowFor(control, text());
        control.MouseExited += () => Instance.Hide(control);
    }

    // ── 内容构造助手(对齐 g_TooltipTextFormats)──

    /// <summary>主名称行(namePrimaryBig:sans-bold-16)。</summary>
    public static string Title(string text) => $"[b][font_size=16]{Escape(text)}[/font_size][/b]";

    /// <summary>次名称行(nameSecondary:sans-bold-16)。</summary>
    public static string Secondary(string text) => Title(text);

    /// <summary>统计块标题(headerFont:sans-bold-13)。</summary>
    public static string Header(string text) => $"[b][font_size=13]{Escape(text)}[/font_size][/b]";

    /// <summary>正文(bodyFont:sans-13)。</summary>
    public static string Body(string text) => $"[font_size=13]{Escape(text)}[/font_size]";

    /// <summary>资源行:小图标 + 数值(session/icons/resources/*_small.png 16px)。
    /// parts 交替 (资源码, 数值)。</summary>
    public static string ResourceRow(params (string Code, int Amount)[] parts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (code, amount) in parts)
        {
            if (amount <= 0) continue;
            if (sb.Length > 0) sb.Append("  ");
            sb.Append($"[img=16]{ResourceIconPath(code)}[/img] {amount}");
        }
        return sb.ToString();
    }

    private static string Escape(string t) =>
        t.Replace("&", "&amp;").Replace("[", "&#91;").Replace("]", "&#93;");

    /// <summary>资源小图标路径(res://assets/ui/resources/*_small.png——图标须在
    /// res:// 内被 Godot 导入,RichTextLabel 的 [img] 才能加载;binaries 外部路径
    /// 报 No loader found。图标由资产管线从 session/icons/resources 拷入)。</summary>
    private static string ResourceIconPath(string code) =>
        $"res://assets/ui/resources/{code}_small.png";

    private void ShowFor(Control owner, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _owner = owner;
        _label.Clear();
        _label.AppendText(text);
        _card.Visible = true;
        PlaceAt(GetViewport().GetMousePosition());
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

    private void PlaceAt(Vector2 mouse)
    {
        // 原版 offset = "16 24"(卡在鼠标右下 16,24);maxwidth 480。
        // 宽度策略:先按最长行算期望宽(不依赖 FitContent 的最小尺寸——它量不出
        // 富文本宽度),设置标签最小宽再取整卡实际需要的高度。
        string txt = _label.Text;
        float want = DesiredWidth(txt);
        _label.CustomMinimumSize = new Vector2(want, 0);
        var size = _card.GetCombinedMinimumSize();
        // 高度自适应:卡尺寸 = 内容最小尺寸(宽度按 want,高度按富文本排完)。
        _card.CustomMinimumSize = new Vector2(size.X, size.Y);
        size = _card.GetCombinedMinimumSize();
        float x = mouse.X + 16;
        float y = mouse.Y + 24;
        var vp = GetViewport().GetVisibleRect().Size;
        if (x + size.X > vp.X) x = mouse.X - size.X - 8;
        if (y + size.Y > vp.Y) y = mouse.Y - size.Y - 8;
        _card.Position = new Vector2(MathF.Max(x, 4), MathF.Max(y, 4));
        _card.Size = size;
    }
}
