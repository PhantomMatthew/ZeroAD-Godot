using System.Collections.Generic;
using System.IO;
using global::Godot;

namespace ZeroAD.Godot.Dialogs;

// TermsDialog — 原版 gui/mod/gui/termsdialog 的端口(Clickwrap 协议页):
// 标题 + 条款正文(读文件,sprintf 参数替换;语言下拉:en-US 原文 / 当前 locale 逐行翻译)
// + URL 按钮行(可选 "View online" 置顶)+ Cancel / Accept 双钮。
// 结果经 onClosed(bool accepted) 回调(原版 resolve({page, accepted}))。
public sealed partial class TermsDialog : ModalPanelBase
{
    /// <summary>URL 按钮(caption + url;原版 urlButtons)。</summary>
    public sealed record UrlButton(string Caption, string Url);

    private string _titleText = "";
    private string _file = "";            // 相对 gui/ 的条款文件路径(如 "modio/Disclaimer.txt")
    private IReadOnlyDictionary<string, string>? _sprintf;
    private List<UrlButton> _urlButtons = new();
    private string? _termsUrl;            // 非空 → 顶部插 "View online" 按钮
    private System.Action<bool>? _onClose;

    private RichTextLabel _text = null!;
    private OptionButton _langOpt = null!;
    private string _rawText = "";

    public static TermsDialog Show(Node parent, string title, string file,
        IReadOnlyDictionary<string, string>? sprintfParams = null,
        IEnumerable<UrlButton>? urlButtons = null, string? termsUrl = null,
        System.Action<bool>? onClosed = null)
    {
        var dlg = new TermsDialog
        {
            _titleText = title,
            _file = file,
            _sprintf = sprintfParams,
            _urlButtons = urlButtons == null ? new List<UrlButton>() : new List<UrlButton>(urlButtons),
            _termsUrl = termsUrl,
            _onClose = onClosed,
        };
        parent.AddChild(dlg);
        dlg.Open();
        return dlg;
    }

    public override void _Ready()
    {
        var (content, _) = BuildShell(_titleText, 620);
        _rawText = LoadTermsFile();

        // URL 按钮行(原版 initURLButtons:termsURL 置顶 "View online")。
        var urlRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        urlRow.AddThemeConstantOverride("separation", 8);
        if (_termsUrl != null)
            _urlButtons.Insert(0, new UrlButton(Localization.Tr("View online"), _termsUrl));
        foreach (var ub in _urlButtons)
        {
            var b = AddButton(urlRow, ub.Caption, () => OpenUrl(ub.Url));
            b.TooltipText = string.Format(Localization.Tr("Open {0} in the browser."), ub.Url);
        }
        if (_urlButtons.Count > 0)
            content.AddChild(urlRow);

        // 语言选择(原版:en-US + 当前 locale 两项;切到 1 = 逐行翻译)。
        var langRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        langRow.AddThemeConstantOverride("separation", 8);
        var langLabel = MakeLabel(Localization.Tr("Language:"), 13);
        langRow.AddChild(langLabel);
        _langOpt = new OptionButton();
        _langOpt.AddItem("en-US (original)");
        if (Localization.CurrentLocale != "en" && Localization.CurrentLocale.Length > 0)
            _langOpt.AddItem(Localization.CurrentLocale);
        _langOpt.Selected = _langOpt.ItemCount - 1;
        _langOpt.ItemSelected += _ => RefreshText();
        langRow.AddChild(_langOpt);
        content.AddChild(langRow);

        _text = new RichTextLabel
        {
            CustomMinimumSize = new Vector2(560, 300),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            BbcodeEnabled = false,
            ScrollActive = true,
        };
        _text.AddThemeFontSizeOverride("normal_font_size", 13);
        content.AddChild(_text);
        RefreshText();

        var buttons = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        buttons.AddThemeConstantOverride("separation", 12);
        content.AddChild(buttons);
        AddButton(buttons, "Cancel", () => Finish(false));
        AddButton(buttons, "Accept", () => Finish(true));
    }

    /// <summary>条款文件:相对 gui/ 的路径,优先 mod 包(mod/gui/…)再 public 包。</summary>
    private string LoadTermsFile()
    {
        string? binDir = StoneButtonStyle.FindBinariesDir();
        if (binDir == null) return "(terms text unavailable)";
        foreach (var modDir in new[] { "mod", "public" })
        {
            string path = Path.Combine(binDir, "data", "mods", modDir, "gui",
                _file.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return "(terms text unavailable)";
    }

    private void RefreshText()
    {
        string text = _rawText;
        // 选中第二项(当前 locale)时逐行翻译(原版 TranslateLines)。
        if (_langOpt.Selected == 1)
        {
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].TrimEnd('\r');
                if (t.Length > 0) lines[i] = Localization.Tr(t);
            }
            text = string.Join('\n', lines);
        }
        if (_sprintf != null)
            foreach (var kv in _sprintf)
                text = text.Replace("%(" + kv.Key + ")s", kv.Value);
        _text.Text = text;
    }

    private static void OpenUrl(string url)
    {
        OS.ShellOpen(url);
        GameMsgBox.Show(Engine.GetMainLoop() is SceneTree t ? t.Root : null!,
            600, 200,
            string.Format(Localization.Tr("Opening {0}\n in default web browser. Please wait…"), url),
            Localization.Tr("Opening page"));
    }

    private void Finish(bool accepted)
    {
        Close();
        QueueFree();
        _onClose?.Invoke(accepted);
    }

    /// <summary>Esc = Cancel(原版 cancelButton)。</summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible) return;
        if (e is InputEventKey k && k.Pressed && k.Keycode == Key.Escape)
        {
            Finish(false);
            GetViewport().SetInputAsHandled();
        }
    }
}
