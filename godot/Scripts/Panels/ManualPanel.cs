using System.Text.RegularExpressions;
using Godot;

namespace ZeroAD.Godot;

// Manual 手册页(会话外页之一)。复用 ModalPanelBase 外壳 + RichTextLabel(bbcode)。
// 文本源是端口自原版 gui/manual/intro.txt(res://data/manual/intro.txt),原版用 SGML 状态切换式
// `[font="sans-bold-18"]...[font="sans-14"]` 标签(非开闭对,而是"切到粗体大号"→"切回正文 14 号"),
// 运行时正则转成 Godot BBCode 对:`[b][font_size=N]` ... `[/b][font_size=14]`。hotkey.xxx 占位符本轮
// 保留原文(动态按 InputMap 替换留 backlog)。从 MainMenu(Manual 按钮)与 PauseMenu 两处打开。
public sealed partial class ManualPanel : ModalPanelBase
{
    private readonly string _path;
    private readonly int _layer;
    private RichTextLabel _body = null!;

    public ManualPanel(string path = "res://data/manual/intro.txt", int layer = 58)
    {
        _path = path;
        // 默认 58(高于普通菜单面板 55)。从 PauseMenu(Layer 60)打开时传 65,浮在暂停菜单之上。
        _layer = layer;
    }

    public override void _Ready()
    {
        Layer = _layer;
        var (content, _) = BuildShell("Manual", 640);

        _body = new RichTextLabel
        {
            BbcodeEnabled = true,
            Text = ConvertIntro(LoadIntro()),
            // FitContent=false + AutowrapMode + 固定高 + 滚动:正文超出在框内滚动,面板不撑满全屏。
            FitContent = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ScrollActive = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 460),
        };
        content.AddChild(_body);

        AddButton(content, "Close", Close);
    }

    private string LoadIntro()
    {
        using var f = global::Godot.FileAccess.Open(_path, global::Godot.FileAccess.ModeFlags.Read);
        if (f == null)
            return $"[color=red][Manual text not found at {_path}][/color]";
        return f.GetAsText();
    }

    // 原版 `[font="sans-bold-18"]`→粗体大号、`[font="sans-14"]`→正文(状态切换式,见类注释)。
    internal static string ConvertIntro(string raw)
    {
        var text = Regex.Replace(raw, @"\[font=""sans-bold-(\d+)""]", "[b][font_size=$1]");
        text = Regex.Replace(text, @"\[font=""sans-(\d+)""]", "[/b][font_size=$1]");
        // 原版 SGML 把字面 `[` 转义成 `\[`(如 \[on unit]);Godot bbcode 无此转义,还原为字面括号。
        // 含空格的 `[on unit]` 非合法标签,Godot 当字面文本渲染。
        text = text.Replace(@"\[", "[");
        // 热键占位符替换(原版 manual.js 的 substituteHotkeys:“hotkey.xxx” →
        // 当前绑定组合;未绑定/未知名保留原文)。直/弯引号两种包裹都收。
        text = Regex.Replace(text, @"[“""]?(hotkey\.[a-z0-9_.]+)[”""]?",
            m => FormatHotkey(m.Groups[1].Value, m.Value));
        return text;
    }

    /// <summary>hotkey action → 当前绑定显示串(UserConfig 有效值 → Parse → Format;
    /// 未知/未绑定 → 原文返回)。</summary>
    private static string FormatHotkey(string action, string fallback)
    {
        try
        {
            var cfg = (Engine.GetMainLoop() as SceneTree)?.Root
                .GetNodeOrNull<UserConfig>("/root/UserConfig");
            if (cfg == null) return fallback;
            string combo = cfg.GetEffective(action);
            if (combo.Length == 0) return fallback;
            var evt = Options.HotkeyCombo.Parse(combo);
            if (evt == null) return fallback;
            return Options.HotkeyCombo.Format(evt);
        }
        catch (Exception) { return fallback; }
    }
}
