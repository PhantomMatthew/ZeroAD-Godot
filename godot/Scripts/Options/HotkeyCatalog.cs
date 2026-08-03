using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace ZeroAD.Godot.Options;

/// <summary>单个热键 action 的描述。</summary>
/// <param name="FullName">完整 action 名，如 "hotkey.camera.left"（也是 InputMap 的 action 名）。</param>
/// <param name="Category">分类显示名，如 "Camera"。</param>
/// <param name="DisplayLabel">单行显示标签，如 "Camera Left"（标题化的 action 末段 + 分类上下文）。</param>
/// <param name="DefaultCombos">default.cfg 里的默认组合列表（如 ["A", "LeftArrow"]），保留多组合。</param>
public sealed record HotkeyAction(string FullName, string Category, string DisplayLabel, IReadOnlyList<string> DefaultCombos);

/// <summary>热键目录。从 default.cfg 的 [hotkey.*] 段扫描全部 action，按前缀分类。
/// 注意：不复用 DefaultConfig.All（它只保留首个组合）；这里重新解析以保留完整多组合。</summary>
public static class HotkeyCatalog
{
    private static List<HotkeyAction>? _all;
    private static List<string>? _categories;

    /// <summary>全部热键 action（启动时从 default.cfg 扫描，惰性缓存）。</summary>
    public static IReadOnlyList<HotkeyAction> AllActions => _all ??= LoadAll();

    /// <summary>去重排序的分类列表。</summary>
    public static IReadOnlyList<string> Categories => _categories ??= AllActions
        .Select(a => a.Category).Distinct().OrderBy(c => c).ToList();

    /// <summary>按分类筛选 action。</summary>
    public static IEnumerable<HotkeyAction> ForCategory(string category)
        => AllActions.Where(a => a.Category == category);

    private static List<HotkeyAction> LoadAll()
    {
        var result = new List<HotkeyAction>();
        string? path = FindDefaultCfg();
        if (path == null)
        {
            GD.PrintErr("[Hotkeys] default.cfg not found");
            return result;
        }
        var (actions, _) = ParseHotkeys(File.ReadAllText(path));
        result.AddRange(actions);
        return result;
    }

    /// <summary>解析 default.cfg 文本，提取 [hotkey.*] 段的所有 action（保留多组合）。</summary>
    /// <returns>(action 列表, 当前 section 名)</returns>
    internal static (List<HotkeyAction> actions, string lastSection) ParseHotkeys(string text)
    {
        var result = new List<HotkeyAction>();
        string section = "";
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                section = close > 1 ? line.Substring(1, close - 1).Trim() : "";
                continue;
            }
            // 只处理 hotkey 开头的 section（含 [hotkey] 本身和 [hotkey.camera] 等子段）。
            if (!section.StartsWith("hotkey")) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string name = line.Substring(0, eq).Trim();
            string value = StripInlineComment(line.Substring(eq + 1)).Trim();
            // 完整多组合：逗号分隔，剥离引号。未绑定（空值）的 action 也保留——原版列表同样
            // 显示无映射的热键(空 mapping,可点击绑定)。
            var combos = ParseAllCombos(value);
            string full = section + "." + name;
            result.Add(new HotkeyAction(full, Classify(full), MakeLabel(full, name), combos));
        }
        return (result, section);
    }

    /// <summary>剥行内注释:default.cfg 允许 `reset = "R" ; Reset camera`——; 在引号外即注释起点。
    /// 不剥会把注释尾巴吃成组合串(显示成 "R" ; Reset camera,还绕过引号剥离)。</summary>
    internal static string StripInlineComment(string raw)
    {
        bool inQuotes = false;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '"') inQuotes = !inQuotes;
            else if (raw[i] == ';' && !inQuotes) return raw.Substring(0, i);
        }
        return raw;
    }

    /// <summary>解析值段的全部组合（逗号分隔，引号剥离）。与 DefaultConfig.ParseFirstValue 不同——这里保留全部。</summary>
    internal static List<string> ParseAllCombos(string raw)
    {
        var result = new List<string>();
        foreach (var part in raw.Split(','))
        {
            string s = part.Trim();
            if (s.Length == 0) continue;
            if (s[0] == '"' && s.Length >= 2 && s[^1] == '"')
                s = s.Substring(1, s.Length - 2);
            if (s.Length > 0) result.Add(s);
        }
        return result;
    }

    /// <summary>action 前缀 → 分类显示名（硬编码，避免依赖上游 spec JSON）。</summary>
    private static string Classify(string fullActionName)
    {
        if (fullActionName.StartsWith("hotkey.camera")) return "Camera";
        if (fullActionName.StartsWith("hotkey.selection")) return "Selection";
        if (fullActionName.StartsWith("hotkey.session")) return "Session";
        if (fullActionName.StartsWith("hotkey.profile")) return "Profiler";
        if (fullActionName.StartsWith("hotkey.tab") || fullActionName.StartsWith("hotkey.item")
            || fullActionName.StartsWith("hotkey.text")) return "GUI";
        return "Global";
    }

    /// <summary>生成单行显示标签：标题化 action 末段，前置分类上下文。</summary>
    private static string MakeLabel(string full, string lastSegment)
    {
        // "hotkey.camera.rotate.cw" → "Camera Rotate Cw"；末段标题化。
        string mid = full.StartsWith("hotkey.") ? full.Substring("hotkey.".Length) : full;
        // 取分类段 + 末段，标题化各词。
        var parts = mid.Split('.').Select(TitleCase);
        return string.Join(" ", parts);
    }

    private static string TitleCase(string s)
    {
        if (s.Length == 0) return s;
        if (s.Length <= 2) return s.ToUpper();  // cw/ccw/1/2 等短段大写
        return char.ToUpper(s[0]) + s.Substring(1);
    }

    private static string? FindDefaultCfg()
    {
        string projRoot = ProjectSettings.GlobalizePath("res://");
        foreach (var candidate in new[]
        {
            Path.GetFullPath(Path.Combine(projRoot, "..", "binaries", "data", "config", "default.cfg")),
            Path.GetFullPath(Path.Combine(projRoot, "..", "..", "binaries", "data", "config", "default.cfg")),
        })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
