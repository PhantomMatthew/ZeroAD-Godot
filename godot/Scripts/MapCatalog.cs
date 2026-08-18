using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZeroAD.Godot;

/// <summary>一张可选地图（选图 UI 目录项）。RelPath 即 GameLaunchConfig.MapPath 的取值:
/// scenario/skirmish 为 pmp rel 路径,random 为 "random/&lt;registry 名&gt;"。</summary>
public sealed class MapEntry
{
    public string DisplayName = "";
    public string RelPath = "";
    public string MapType = "scenario";   // scenario / skirmish / random
    public string Description = "";
    public int PlayerCount;               // PlayerData 槽数(0 = 未知)
    /// <summary>ScriptSettings.PlayerData[1..] 的 (Civ, Team)([0] 是 gaia,跳过)——
    /// 选图面板的槽位默认值;random 图无此数据(生成时按槽位表)。</summary>
    public List<(string Civ, int Team)> Players = new();
    /// <summary>预览图绝对路径(random: maps/random/&lt;name&gt;.png;pmp: ScriptSettings
    /// 的 Preview 字段,相对地图目录)。不存在时为 null(面板给占位)。</summary>
    public string? PreviewPath;
}

/// <summary>可选地图目录:扫 binaries 数据根的 maps/scenarios + maps/skirmishes(读文件头
/// 16KB 提取 ScriptSettings 的 Name/Description/PlayerData——ScriptSettings 位于文件开头,
/// ~1KB 偏移处,无需解析整份几 MB 的实体表)+ rmgen MapRegistry 的 random 图。
/// 扫描结果缓存,菜单每次打开复用。</summary>
public static class MapCatalog
{
    private static List<MapEntry>? _cache;

    /// <summary>完整目录(random 在前——原版 gamesetup 默认视图即 random)。dataRoot =
    /// binaries/data/mods/public;null/目录缺失时只回 random 图且不缓存(避免首次空调用
    /// 把"无 pmp 图"烙进缓存,后续拿到 dataRoot 也刷不出)。</summary>
    public static List<MapEntry> Scan(string? dataRoot)
    {
        if (dataRoot == null)
            return ScanRandomOnly(null);
        if (_cache != null) return _cache;
        var list = ScanRandomOnly(dataRoot);

        ScanDir(list, Path.Combine(dataRoot, "maps", "skirmishes"), "skirmish");
        ScanDir(list, Path.Combine(dataRoot, "maps", "scenarios"), "scenario");

        _cache = list;
        return list;
    }

    /// <summary>预览图目录(原版 MapCache.js:PreviewsPath):art/textures/ui/session/icons/mappreview/。
    /// 所有图共用一个目录——maps/random/*.png 里混着高度图数据(hellas.png 就是灰度高度图),
    /// 不能直接当预览。</summary>
    private static string? FindPreview(string? dataRoot, string fileName)
    {
        if (dataRoot == null) return null;
        string abs = Path.Combine(dataRoot, "art", "textures", "ui", "session", "icons", "mappreview", fileName);
        return File.Exists(abs) ? abs : null;
    }

    private static List<MapEntry> ScanRandomOnly(string? dataRoot)
    {
        var list = new List<MapEntry>();
        foreach (var name in ZeroAD.Sim.Rmgen.Maps.MapRegistry.AvailableMaps)
        {
            list.Add(new MapEntry
            {
                DisplayName = PrettifyName(name),
                RelPath = "random/" + name,
                MapType = "random",
                Description = "Randomly generated map (seed-driven).",
                PreviewPath = FindPreview(dataRoot, name + ".png"),
            });
        }
        list.Sort((a, b) => string.Compare(a.RelPath, b.RelPath, StringComparison.Ordinal));
        return list;
    }

    private static void ScanDir(List<MapEntry> list, string dir, string mapType)
    {
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.pmp");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var pmp in files)
        {
            string rel = RelPathOf(pmp);
            var entry = new MapEntry
            {
                DisplayName = PrettifyName(Path.GetFileNameWithoutExtension(pmp)),
                RelPath = rel,
                MapType = mapType,
            };
            FillFromScriptSettings(Path.ChangeExtension(pmp, ".xml"), entry);
            list.Add(entry);
        }
    }

    /// <summary>binaries/data/mods/public 之后的 rel 路径(正斜杠,与 FindDataPath 契约一致)。</summary>
    private static string RelPathOf(string fullPath)
    {
        // 从 ".../binaries/data/mods/public/maps/..." 截 "maps/..."
        string norm = fullPath.Replace('\\', '/');
        int idx = norm.IndexOf("/maps/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? norm[(idx + 1)..] : norm;
    }

    private static readonly Regex SettingsRe = new(
        // CDATA 开标记和 JSON 之间允许空白:Atlas 导出的地图 XML 是
        // <ScriptSettings><![CDATA[\n{...} —— 少了这个 \s* 时全部 pmp 图回退到文件名显示,
        // 描述/人数全丢(原版正式 Name 如 "Acropolis Bay (2)" 也拿不到)。
        "<ScriptSettings>\\s*(?:<!\\[CDATA\\[)?\\s*(?<json>\\{.*?\\})\\s*(?:\\]\\]>)?</ScriptSettings>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>只读文件头 16KB(ScriptSettings 在 ~1KB 偏移),正则提 JSON,填 Name/
    /// Description/PlayerCount。解析失败保留文件名推导的 DisplayName。</summary>
    private static void FillFromScriptSettings(string xmlPath, MapEntry entry)
    {
        try
        {
            if (!File.Exists(xmlPath)) return;
            string head;
            using (var fs = new FileStream(xmlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var buf = new byte[16384];
                int n = fs.Read(buf, 0, buf.Length);
                head = System.Text.Encoding.UTF8.GetString(buf, 0, n);
            }
            var m = SettingsRe.Match(head);
            if (!m.Success) return;
            using var doc = JsonDocument.Parse(m.Groups["json"].Value);
            var root = doc.RootElement;
            if (root.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String)
                entry.DisplayName = name.GetString() ?? entry.DisplayName;
            if (root.TryGetProperty("Description", out var desc) && desc.ValueKind == JsonValueKind.String)
                entry.Description = desc.GetString() ?? "";
            if (root.TryGetProperty("PlayerData", out var pd) && pd.ValueKind == JsonValueKind.Array)
            {
                entry.PlayerCount = pd.GetArrayLength() - 1;   // [0] = gaia
                // 逐槽 civ/team(gamesetup 默认槽位表):gaia 跳过;Civ 缺省 = random。
                foreach (var player in pd.EnumerateArray().Skip(1))
                {
                    string civ = "random";
                    int team = -1;
                    if (player.TryGetProperty("Civ", out var c) && c.ValueKind == JsonValueKind.String)
                        civ = c.GetString() ?? "random";
                    if (player.TryGetProperty("Team", out var tm) && tm.ValueKind == JsonValueKind.Number)
                    {
                        // 0AD 的 Team 有 int/浮点两种写法(-1 或 -1.0),宽容解析。
                        if (tm.TryGetInt32(out int tv)) team = tv;
                        else if (tm.TryGetDouble(out double td)) team = (int)td;
                    }
                    entry.Players.Add((civ, team));
                }
            }
            // 预览图:ScriptSettings.Preview 是文件名,实际在 art/textures/ui/session/icons/
            // mappreview/ 下(MapCache.js PreviewsPath);无则回退 nopreview.png。
            string norm = xmlPath.Replace('\\', '/');
            int mapsIdx = norm.IndexOf("/maps/", StringComparison.OrdinalIgnoreCase);
            string? dataRoot = mapsIdx > 0 ? norm[..mapsIdx] : null;
            string? previewName = null;
            if (root.TryGetProperty("Preview", out var pv) && pv.ValueKind == JsonValueKind.String)
                previewName = pv.GetString();
            entry.PreviewPath = previewName != null ? FindPreview(dataRoot, previewName) : null;
            entry.PreviewPath ??= FindPreview(dataRoot, "nopreview.png");
        }
        catch (Exception)
        {
            // 头 16KB 截断破坏 JSON / 编码问题等——保留默认显示名即可
        }
    }

    /// <summary>文件名 → 显示名(alpine_valleys_2p → Alpine Valleys 2p);ScriptSettings
    /// 有正式 Name 时会被覆盖。</summary>
    private static string PrettifyName(string fileName)
    {
        string s = fileName.Replace('_', ' ').Trim();
        if (s.Length == 0) return fileName;
        var parts = s.Split(' ');
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0 && char.IsLetter(parts[i][0]))
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        return string.Join(' ', parts);
    }
}
