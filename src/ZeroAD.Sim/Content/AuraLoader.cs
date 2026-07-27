using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.Content;

/// <summary>单个光环定义(对齐 simulation/data/auras/*.json)。</summary>
/// <param name="Name">文件名 key,如 <c>structures/farmstead_60</c>。</param>
/// <param name="Type">range / global / player(MVP);其余 type 加载期跳过。</param>
/// <param name="Radius">range 型半径(原版 <c>radius</c>);非 range 型为 0。</param>
/// <param name="Affects">类过滤(Worker / Field / CivilCentre / ...),空 = 全部。</param>
/// <param name="AffectedPlayers">缺省 <c>["Player"]</c>(原版 <c>Auras.js:116</c>)。MVP 只认 "Player"。</param>
/// <param name="Modifications">派生修正值,喂 <see cref="ModifiersManager.AddModifiers"/>。</param>
/// <param name="RequiredTechnology">研发门控;null = 无条件。</param>
/// <param name="Stackable">true → modId 带 source entity 后缀(多源叠加);false → 同名单份。</param>
public sealed record AuraDefinition(
    string Name,
    string Type,
    float Radius,
    IReadOnlyList<string> Affects,
    IReadOnlyList<string> AffectedPlayers,
    IReadOnlyList<Modification> Modifications,
    string? RequiredTechnology,
    bool Stackable,
    string AuraName,
    string AuraDescription);

/// <summary>全部光环定义,按文件名 key 查(对齐原版全局 <c>AuraTemplates</c>)。</summary>
public sealed record AuraCatalog(IReadOnlyDictionary<string, AuraDefinition> Auras);

/// <summary>
/// 加载 <c>simulation/data/auras/**/*.json</c>(对齐原版 AuraTemplates.js)。
/// 单文件坏 / type 缺失 / 未知 type → 跳过(与 <see cref="TechnologyLoader"/> 同款容错)。
/// MVP 仅收 range/global/player 三型(覆盖 137/151 ≈ 91%);formation/garrison*/turreted*
/// 跳过(内核无 holder 组件)。
/// </summary>
public static class AuraLoader
{
    public static AuraCatalog LoadAll(string aurasDir)
    {
        var auras = new Dictionary<string, AuraDefinition>(StringComparer.Ordinal);
        if (!Directory.Exists(aurasDir)) return new AuraCatalog(auras);

        foreach (var file in Directory.GetFiles(aurasDir, "*.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            // key 含相对目录路径(对齐 template <Auras> token:teambonuses/spart_player_teambonus)。
            // 文件名虽全局唯一,但 token 约定路径式,catalog 查找必须用同形 key。
            string name = Path.GetRelativePath(aurasDir, file)
                .Replace('\\', '/')
                .Replace(".json", "");
            AuraDefinition? def;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                def = Parse(name, doc.RootElement);
            }
            catch { continue; /* 容错:坏文件跳过,不阻塞整体 */ }
            if (def != null)
                auras[name] = def;
        }
        return new AuraCatalog(auras);
    }

    private static AuraDefinition? Parse(string name, JsonElement root)
    {
        // type 缺失 → 跳过(数据集 3 个无 type 文件 + 任何漏字段)。
        if (!root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
            return null;
        string type = t.GetString()!;

        // MVP:只收 range/global/player。其余(formation/garrison*/turreted*)内核无对应组件。
        if (type != "range" && type != "global" && type != "player")
            return null;

        var affects = ModificationParser.TryGetAffects(root, out var af) ? af : Array.Empty<string>();
        var affectedPlayers = root.TryGetProperty("affectedPlayers", out var ap) && ap.ValueKind == JsonValueKind.Array
            ? ap.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).ToList()
            : new List<string> { "Player" }; // 缺省 ["Player"](原版 Auras.js:116)

        var mods = ModificationParser.Derive(root, affects);
        float radius = ModificationParser.TryGetNumber(root, "radius", out var r) ? r : 0f;
        string? reqTech = root.TryGetProperty("requiredTechnology", out var rt) && rt.ValueKind == JsonValueKind.String
            ? rt.GetString() : null;
        bool stackable = root.TryGetProperty("stackable", out var st) && st.ValueKind == JsonValueKind.True;
        string auraName = root.TryGetProperty("auraName", out var an) && an.ValueKind == JsonValueKind.String
            ? an.GetString()! : name;
        string auraDesc = root.TryGetProperty("auraDescription", out var ad) && ad.ValueKind == JsonValueKind.String
            ? ad.GetString()! : "";

        return new AuraDefinition(name, type, radius, affects, affectedPlayers, mods,
            reqTech, stackable, auraName, auraDesc);
    }
}
