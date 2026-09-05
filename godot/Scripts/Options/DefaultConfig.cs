using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;

namespace ZeroAD.Godot.Options;

// default.cfg 默认值源(忠实端口原版 CConfigDB::Reload 的行解析,source/ps/ConfigDB.cpp:346-464):
// - `;` 在行内任意处起注释(引号内除外);[section] 头作键前缀(header + "." + name)。
// - 值支持双引号(剥离,`\` 转义)与逗号分隔多值;引号外空白全部忽略(原版 case ' ': continue)。
// - 96 个 options 键全为单值,多值取首个。
// 不复制 default.cfg——经 RuntimePaths.FindConfigFile 直读 binaries/data/config/。
public static class DefaultConfig
{
    private static Dictionary<string, string>? _cache;

    public static IReadOnlyDictionary<string, string> All => _cache ??= Load();

    public static string? Get(string key) => All.TryGetValue(key, out var v) ? v : null;

    /// <summary>纯解析(无 IO),便于核对与潜在单测。</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var map = new Dictionary<string, string>();
        string header = "";
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } rawLine)
        {
            // 1) 截掉引号外 `;` 起的注释。
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;

            // 2) [section] 头。
            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                if (close > 1)
                    header = line.Substring(1, close - 1).Trim() + ".";
                continue;
            }

            // 3) name = value(键不含引号/等号)。
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string name = line.Substring(0, eq).Trim();
            if (name.Length == 0) continue;

            string value = ParseFirstValue(line.Substring(eq + 1));
            map[header + name] = value;
        }
        return map;
    }

    private static string StripComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && quoted) { i++; continue; }   // 转义符跳过下一字符
            if (c == '"') quoted = !quoted;
            else if (c == ';' && !quoted) return line.Substring(0, i);
        }
        return line;
    }

    /// <summary>解析值段:逗号分隔多值取首个;引号段剥离引号+处理 `\` 转义;裸段删全部空白(原版语义)。</summary>
    private static string ParseFirstValue(string raw)
    {
        int i = 0;
        // 跳过前导空白定位首个值段。
        while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
        if (i >= raw.Length) return "";

        var sb = new StringBuilder();
        if (raw[i] == '"')
        {
            // 引号段:逐字符收,`\` 转义,到闭引号止。
            for (i++; i < raw.Length && raw[i] != '"'; i++)
            {
                if (raw[i] == '\\' && i + 1 < raw.Length) i++;
                sb.Append(raw[i]);
            }
        }
        else
        {
            // 裸段:到逗号止,删全部空白(原版忽略引号外空格/制表符)。
            for (; i < raw.Length && raw[i] != ','; i++)
                if (!char.IsWhiteSpace(raw[i]))
                    sb.Append(raw[i]);
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> Load()
    {
        string? path = RuntimePaths.FindConfigFile("default.cfg");
        if (path == null)
        {
            ZeroAD.Sim.Diag.Err("Options", "DefaultConfig: default.cfg not found under binaries/data/config");
            return new Dictionary<string, string>();
        }
        return Parse(File.ReadAllText(path));
    }
}
