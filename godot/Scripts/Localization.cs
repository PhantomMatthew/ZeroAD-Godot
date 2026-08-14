using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZeroAD.Godot;

/// <summary>界面本地化——对齐原版 l10n 机制:gettext .po 包 + locale 配置项。
/// 包发现(与 C++ 同布局,可直接吃 Transifex 包):
///   1. res://data/l10n/*.po(本仓库自带包,如 zh_CN.po)
///   2. binaries/data/l10n/*.po(原版位置;上游源码树不带翻译,用户放包即被双端发现)
/// 用法:面板构建时 Tr(msgid);msgid = 英文字符串原文(同 gettext 惯例),未命中回退英文。
/// 懒初始化:首次 Tr/AvailableLocales 时从 UserConfig 读 locale 生效值并加载。</summary>
public static class Localization
{
    private static Dictionary<string, string>? _table;
    private static bool _initialized;
    private static string _locale = "";

    /// <summary>当前 locale 代码("" / "en" = 英文原文)。</summary>
    public static string CurrentLocale => _locale;

    /// <summary>翻译 msgid;未加载包或未命中返回原文(= 英文)。</summary>
    public static string Tr(string msgid)
    {
        EnsureInitialized();
        if (_table != null && _table.TryGetValue(msgid, out var s) && s.Length > 0)
            return s;
        return msgid;
    }

    /// <summary>可选语言列表:(code, 显示名)。英文恒在首位;其余按 .po 文件名发现,
    /// 显示名取 po 头 "Language:" 字段,缺失用文件名。</summary>
    public static List<(string Code, string Name)> AvailableLocales()
    {
        EnsureInitialized();
        var list = new List<(string, string)> { ("", "English") };
        foreach (var dir in PackDirs())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.po"))
            {
                string code = Path.GetFileNameWithoutExtension(file);
                if (code.Equals("en", StringComparison.OrdinalIgnoreCase)) continue;
                if (list.Exists(l => l.Item1 == code)) continue;
                list.Add((code, ReadLanguageHeader(file) ?? code));
            }
        }
        return list;
    }

    /// <summary>切换语言并加载包(""/en → 原文)。Options locale 改动时调用。</summary>
    public static void SetLocale(string code)
    {
        _locale = code;
        _table = null;
        if (string.IsNullOrEmpty(code) || code == "en") return;

        foreach (var dir in PackDirs())
        {
            string file = Path.Combine(dir, code + ".po");
            if (File.Exists(file))
            {
                _table = ParsePo(file);
                ZeroAD.Sim.Diag.Log("Localization", $"locale={code}, {_table.Count} entries from {file}");
                return;
            }
        }
        ZeroAD.Sim.Diag.Warn("Localization", $"no pack for locale '{code}', falling back to English");
    }

    /// <summary>懒初始化:从 UserConfig 读 locale(自动加载存档值,同原版启动时
    /// Engine.GetDefaultLocale 路径)。Autoload 就绪前(编辑器工具等)安全回退英文。</summary>
    private static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        string code = "";
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree && tree.Root.HasNode("/root/UserConfig"))
                code = tree.Root.GetNode<UserConfig>("/root/UserConfig").GetEffective("locale");
        }
        catch (Exception) { /* headless/工具环境 → 英文 */ }
        SetLocale(code);
    }

    private static IEnumerable<string> PackDirs()
    {
        // 自带包在 godot/data/(版本跟踪,同 options.json;assets/ 是管线产物不入库)
        yield return ProjectSettings.GlobalizePath("res://data/l10n");
        // 原版位置(经 junction;用户放 Transifex 包于此,与 C++ 版共享发现)
        string projRoot = ProjectSettings.GlobalizePath("res://");
        yield return Path.GetFullPath(Path.Combine(projRoot, "..", "binaries", "data", "l10n"));
    }

    /// <summary>po 头里的 Language: 字段(显示名用)。只扫文件头 40 行内的续行串
    /// (头块形如 "Language: xxx\n"),找不到回退文件名。</summary>
    private static string? ReadLanguageHeader(string path)
    {
        try
        {
            int n = 0;
            foreach (var line in File.ReadLines(path))
            {
                if (++n > 40) break;
                var t = line.Trim();
                const string key = "Language:";
                int i = t.IndexOf(key, StringComparison.Ordinal);
                if (i < 0) continue;
                string v = t[(i + key.Length)..].Trim();
                // 去掉尾部 \n 转义与引号
                if (v.EndsWith("\\n", StringComparison.Ordinal)) v = v[..^2];
                v = v.Trim('"').Trim();
                if (v.Length > 0) return v;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>极简 gettext .po 解析:msgid/msgstr 对 + 续行字符串;"#, fuzzy" 跳过;
    /// 头(msgid "")跳过;转义 \" \n \t \\。</summary>
    private static Dictionary<string, string> ParsePo(string path)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        string? msgid = null, msgstr = null;
        string? state = null;   // "id" | "str"
        bool fuzzy = false;

        void Flush()
        {
            if (msgid != null && msgstr != null && msgid.Length > 0 && !fuzzy)
                table[msgid] = msgstr;
            msgid = msgstr = null;
            fuzzy = false;
        }

        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                if (line.Contains("fuzzy", StringComparison.Ordinal)) fuzzy = true;
                continue;
            }
            if (line.StartsWith("msgid ", StringComparison.Ordinal))
            {
                Flush();
                msgid = Unquote(line.Substring(6));
                state = "id";
            }
            else if (line.StartsWith("msgstr ", StringComparison.Ordinal))
            {
                msgstr = Unquote(line.Substring(7));
                state = "str";
            }
            else if (line.StartsWith('"') && state != null)
            {
                if (state == "id") msgid += Unquote(line);
                else msgstr += Unquote(line);
            }
            else if (line.Length == 0)
            {
                state = null;
            }
        }
        Flush();
        return table;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s.Substring(1, s.Length - 2);
        return s.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");
    }
}
