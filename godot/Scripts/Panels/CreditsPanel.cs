using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace ZeroAD.Godot;

/// <summary>制作名单(原版 gui/credits/page_credits.xml + credits.js):
/// 按类别分页展示(gui/credits/texts/*.json 的 Title/Subtitle/LangName/List/Content
/// 递归解析成富文本)。左侧类别按钮列 + 右侧内容区,原版 placeTabButtons 同款。</summary>
public sealed partial class CreditsPanel : ModalPanelBase
{
    private VBoxContainer _tabButtons = null!;
    private RichTextLabel _contentText = null!;
    private readonly List<(string Label, string Content)> _panels = new();
    private int _selected = -1;

    /// <summary>类别顺序(原版 g_OrderTabNames)。</summary>
    private static readonly string[] OrderTabNames =
    {
        "special", "programming", "art", "audio", "maps", "history",
        "balancing", "community", "translators", "donators",
    };

    public override void _Ready()
    {
        var (content, status) = BuildShell("Credits", 720);
        status.Text = "";

        var split = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddThemeConstantOverride("separation", 12);
        content.AddChild(split);

        // 左:类别按钮列(原版 placeTabButtons:竖排按钮,按 g_OrderTabNames 顺序)。
        _tabButtons = new VBoxContainer { CustomMinimumSize = new Vector2(180, 0) };
        _tabButtons.AddThemeConstantOverride("separation", 4);
        split.AddChild(_tabButtons);

        // 右:内容区(原版 creditsText:富文本滚动)。
        _contentText = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = false,
            ScrollActive = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(480, 400),
        };
        _contentText.AddThemeFontSizeOverride("normal_font_size", 14);
        split.AddChild(_contentText);

        LoadPanels();
        RebuildTabs();
        if (_panels.Count > 0) SelectPanel(0);
    }

    protected override void OnOpen()
    {
        if (_panels.Count > 0 && _selected < 0) SelectPanel(0);
    }

    /// <summary>加载全部类别(原版 init:ReadJSONFile + parseHelper 递归解析)。</summary>
    private void LoadPanels()
    {
        string? dir = RuntimePaths.FindPublicPath("gui", "credits", "texts");
        if (dir == null) return;

        foreach (var category in OrderTabNames)
        {
            string path = Path.Combine(dir, category + ".json");
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("Content", out var content)) continue;
                string title = doc.RootElement.TryGetProperty("Title", out var t)
                    ? t.GetString() ?? category : category;
                string parsed = ParseContent(content);
                _panels.Add((title, parsed));
            }
            catch { /* 单文件解析失败跳过 */ }
        }
    }

    /// <summary>递归解析 Content(原版 parseHelper:Title/Subtitle/LangName/List/Content
    /// 递归成富文本;LangName/Title 粗体,Subtitle 次级粗体,List 项正文)。</summary>
    private static string ParseContent(JsonElement list)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var item in list.EnumerateArray())
        {
            if (item.TryGetProperty("LangName", out var langName))
                sb.AppendLine($"[b]{Escape(langName.GetString() ?? "")}[/b]\n");
            if (item.TryGetProperty("Title", out var title))
                sb.AppendLine($"[b]{Escape(title.GetString() ?? "")}[/b]\n");
            if (item.TryGetProperty("Subtitle", out var subtitle))
                sb.AppendLine($"{Escape(subtitle.GetString() ?? "")}\n");
            if (item.TryGetProperty("List", out var listEl))
            {
                foreach (var element in listEl.EnumerateArray())
                {
                    string credit = "";
                    if (element.TryGetProperty("nick", out var nick) && element.TryGetProperty("name", out var name))
                        credit = $"{nick.GetString()} — {name.GetString()}";
                    else if (element.TryGetProperty("nick", out var n))
                        credit = n.GetString() ?? "";
                    else if (element.TryGetProperty("name", out var nm))
                        credit = nm.GetString() ?? "";
                    if (credit.Length > 0)
                        sb.AppendLine(credit);
                }
                sb.AppendLine();
            }
            if (item.TryGetProperty("Content", out var subContent))
            {
                sb.AppendLine();
                sb.Append(ParseContent(subContent));
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private void SelectPanel(int index)
    {
        if (index < 0 || index >= _panels.Count) return;
        _selected = index;
        _contentText.Text = _panels[index].Content;
    }

    private void RebuildTabs()
    {
        foreach (var child in _tabButtons.GetChildren()) child.QueueFree();
        for (int i = 0; i < _panels.Count; i++)
        {
            int idx = i;
            var btn = new Button { Text = _panels[i].Label, CustomMinimumSize = new Vector2(0, 30) };
            btn.Pressed += () => SelectPanel(idx);
            _tabButtons.AddChild(btn);
        }
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("[", "&#91;").Replace("]", "&#93;");
}
