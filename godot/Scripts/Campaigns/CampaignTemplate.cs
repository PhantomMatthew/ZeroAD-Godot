using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ZeroAD.Godot.Campaigns;

// CampaignTemplate — 原版 gui/common/campaigns/CampaignTemplate.js 的 C# 端口。
// 数据源 = binaries 数据根 campaigns/*.json(原版 Engine.ReadJSONFile("campaigns/" + id + ".json")),
// 文件名(无扩展)即 identifier。字段原样保留(Name/Description/Image/Interface/Levels/Order/
// ShowUnavailable);Interface 缺省 "default_menu"(原版行为),当前只支持该界面。

/// <summary>单个战役关卡(campaigns/*.json 的 Levels 字典项)。</summary>
public sealed record CampaignLevel(
    string Id,
    string? Name,
    string Map,           // 相对 maps/ 的路径(原版存 "tutorials/x.xml";启动时换 .pmp)
    string MapType,       // scenario / skirmish / random
    string? Description,
    string? Preview,      // 预览图(相对 art/;null → 取地图自身预览)
    string? Requires,     // 空格分隔的前置关卡类列表("-" 前缀 = 不得完成)
    bool UseGameSetup);   // true → 进 gamesetup 让玩家配置(原版 hook;当前直接开局)

/// <summary>战役模板(campaigns/*.json)。isValid = Name 非空(原版同款校验)。</summary>
public sealed class CampaignTemplate
{
    public required string Identifier { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string? Image { get; init; }
    public string Interface { get; init; } = "default_menu";
    public bool ShowUnavailable { get; init; }
    public List<string> Order { get; init; } = new();
    public IReadOnlyDictionary<string, CampaignLevel> Levels { get; init; }
        = new Dictionary<string, CampaignLevel>();

    private static List<CampaignTemplate>? _cache;

    /// <summary>扫描 campaigns/*.json 得全部模板(原版 getAvailableTemplates;结果缓存)。</summary>
    public static IReadOnlyList<CampaignTemplate> GetAvailableTemplates(string? dataRoot)
    {
        if (_cache != null) return _cache;
        _cache = new List<CampaignTemplate>();
        if (dataRoot == null) return _cache;
        string dir = Path.Combine(dataRoot, "campaigns");
        if (!Directory.Exists(dir)) return _cache;
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var t = Load(Path.GetFileNameWithoutExtension(file), file);
            if (t != null) _cache.Add(t);
        }
        return _cache;
    }

    public static CampaignTemplate? GetTemplate(string? dataRoot, string identifier) =>
        GetAvailableTemplates(dataRoot).FirstOrDefault(t => t.Identifier == identifier);

    /// <summary>热重载/测试用:清缓存迫使下次重扫。</summary>
    public static void InvalidateCache() => _cache = null;

    private static CampaignTemplate? Load(string identifier, string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            string name = root.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()! : "";
            if (name.Length == 0) return null;   // isValid:Name 必须非空(原版同款)

            var levels = new Dictionary<string, CampaignLevel>(StringComparer.Ordinal);
            if (root.TryGetProperty("Levels", out var lv) && lv.ValueKind == JsonValueKind.Object)
                foreach (var prop in lv.EnumerateObject())
                {
                    var l = prop.Value;
                    string map = GetStr(l, "Map") ?? "";
                    if (map.Length == 0) continue;   // 无地图的占位关卡不可启动,跳过
                    levels[prop.Name] = new CampaignLevel(
                        Id: prop.Name,
                        Name: GetStr(l, "Name"),
                        Map: map,
                        MapType: GetStr(l, "MapType") ?? "scenario",
                        Description: GetStr(l, "Description"),
                        Preview: GetStr(l, "Preview"),
                        Requires: GetStr(l, "Requires"),
                        UseGameSetup: l.TryGetProperty("useGameSetup", out var u) && u.ValueKind == JsonValueKind.True);
                }

            var order = new List<string>();
            if (root.TryGetProperty("Order", out var o) && o.ValueKind == JsonValueKind.Array)
                foreach (var e in o.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) order.Add(e.GetString()!);

            return new CampaignTemplate
            {
                Identifier = identifier,
                Name = name,
                Description = GetStr(root, "Description") ?? "",
                Image = GetStr(root, "Image"),
                Interface = GetStr(root, "Interface") ?? "default_menu",
                ShowUnavailable = root.TryGetProperty("ShowUnavailable", out var s) && s.ValueKind == JsonValueKind.True,
                Order = order,
                Levels = levels,
            };
        }
        catch (Exception ex)
        {
            ZeroAD.Sim.Diag.Log("Campaign", $"template '{identifier}' skipped: {ex.Message}");
            return null;
        }
    }

    private static string? GetStr(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
