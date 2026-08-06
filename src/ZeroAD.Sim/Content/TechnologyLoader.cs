using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.Content;

/// <summary>科技前置条件。entity 形态({class,number})= 拥有 N 个该类建筑
/// (phase_town=5×Village,phase_city=3×Town),由 TechnologyManager 按玩家实体计数评估。</summary>
public sealed record TechRequirement(string? Tech, string? Civ,
    IReadOnlyList<TechRequirement>? Any, IReadOnlyList<TechRequirement>? All,
    string? EntityClass = null, int EntityNumber = 0);

public sealed record TechnologyDefinition(
    string Name, string GenericName,
    int Wood, int Food, int Stone, int Metal, float ResearchTime,
    IReadOnlyList<TechRequirement> Requirements,
    IReadOnlyList<Modification> Modifications,
    bool AutoResearch,
    string? Supersedes,
    IReadOnlyList<string> Replaces,
    /// <summary>JSON icon 字段(如 "town_phase.png";HUD 取 portraits/technologies/ 下同名立绘)。</summary>
    string Icon = "");

public sealed record TechCatalog(
    IReadOnlyDictionary<string, TechnologyDefinition> Technologies,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Pairs);

/// <summary>
/// 加载 simulation/data/technologies/*.json(对齐原版数据格式)。
/// pair 文件({ "pair": [a, b] })单独收进 Pairs,不进 Technologies。
/// 单个坏文件不阻塞整体(与模板加载同款容错)。
/// </summary>
public static class TechnologyLoader
{
    public static TechCatalog LoadAll(string technologiesDir)
    {
        var techs = new Dictionary<string, TechnologyDefinition>();
        var pairs = new Dictionary<string, IReadOnlyList<string>>();
        if (!Directory.Exists(technologiesDir)) return new TechCatalog(techs, pairs);

        foreach (var file in Directory.GetFiles(technologiesDir, "*.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (root.TryGetProperty("pair", out var pairEl) && pairEl.ValueKind == JsonValueKind.Array)
                {
                    pairs[name] = pairEl.EnumerateArray().Select(e => e.GetString()!).ToList();
                    continue;
                }
                techs[name] = ParseTech(name, root);
            }
            catch { /* 容错:跳过坏文件 */ }
        }
        return new TechCatalog(techs, pairs);
    }

    private static TechnologyDefinition ParseTech(string name, JsonElement root)
    {
        var cost = root.TryGetProperty("cost", out var c) ? c : default;
        bool hasCost = root.TryGetProperty("cost", out _);

        var techAffects = ModificationParser.TryGetAffects(root, out var ta)
            ? ta : (IReadOnlyList<string>)Array.Empty<string>();
        var mods = ModificationParser.Derive(root, techAffects);

        return new TechnologyDefinition(
            name,
            root.TryGetProperty("genericName", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString()! : name,
            hasCost ? GetInt(cost, "wood") : 0,
            hasCost ? GetInt(cost, "food") : 0,
            hasCost ? GetInt(cost, "stone") : 0,
            hasCost ? GetInt(cost, "metal") : 0,
            ModificationParser.TryGetNumber(root, "researchTime", out var t) ? t : 0f,
            ParseRequirements(root),
            mods,
            root.TryGetProperty("autoResearch", out var ar) && ar.ValueKind == JsonValueKind.True,
            root.TryGetProperty("supersedes", out var su) && su.ValueKind == JsonValueKind.String ? su.GetString() : null,
            root.TryGetProperty("replaces", out var re) && re.ValueKind == JsonValueKind.Array
                ? re.EnumerateArray().Select(e => e.GetString()!).ToList()
                : (IReadOnlyList<string>)Array.Empty<string>(),
            root.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString()! : "");
    }

    /// <summary>requirements 对象的每个键 = 一个条件;多键 AND。</summary>
    private static IReadOnlyList<TechRequirement> ParseRequirements(JsonElement root)
    {
        var result = new List<TechRequirement>();
        if (!root.TryGetProperty("requirements", out var req) || req.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var prop in req.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "tech":
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        result.Add(new TechRequirement(prop.Value.GetString(), null, null, null));
                    break;
                case "civ":
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        result.Add(new TechRequirement(null, prop.Value.GetString(), null, null));
                    break;
                case "any":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                        result.Add(new TechRequirement(null, null, ParseReqList(prop.Value), null));
                    break;
                case "all":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                        result.Add(new TechRequirement(null, null, null, ParseReqList(prop.Value)));
                    break;
                case "entity":
                    // 原版 entity 形态:{class, number}(阶段科技用:phase_town 需 5 个
                    // Village 类建筑等);class 可为 null(number = 任意建筑数)。
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        string? cls = null;
                        int number = 0;
                        if (prop.Value.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String)
                            cls = c.GetString();
                        if (prop.Value.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number)
                            number = n.GetInt32();
                        result.Add(new TechRequirement(null, null, null, null, cls, number));
                    }
                    break;
            }
        }
        return result;
    }

    private static IReadOnlyList<TechRequirement> ParseReqList(JsonElement arr)
    {
        var result = new List<TechRequirement>();
        foreach (var item in arr.EnumerateArray())
        {
            var reqs = ParseRequirements(item);
            if (reqs.Count == 0)
                // 空对象项 → 恒真占位
                result.Add(new TechRequirement(null, null, null, null));
            else
                result.AddRange(reqs);
        }
        return result;
    }

    private static int GetInt(JsonElement cost, string key) =>
        cost.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
}
