using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ZeroAD.Sim.Content;

/// <summary>文明加成条目（civ JSON 的 CivBonuses 数组元素）。</summary>
public sealed record CivBonus(string Name, string History, string Description);

/// <summary>文明数据（civs/*.json 的结构化投影）。用于科技树页的 BFS 种子 + 百科页展示。</summary>
public sealed record CivData(
    string Code,           // "athen"
    string Name,           // "Athenians"（显示名）
    string History,        // 历史长文
    string Description,    // 文明简介（一两句）
    string Emblem,         // 徽标路径（相对 portraits）
    IReadOnlyList<string> StartEntities,  // BFS 种子模板列表
    IReadOnlyList<CivBonus> Bonuses);      // 文明加成列表（百科页展示）

/// <summary>加载 simulation/data/civs/*.json。模式同 TechnologyLoader.LoadAll：
/// Directory.GetFiles + JsonDocument.Parse，单文件坏不阻塞整体。</summary>
public static class CivDataLoader
{
    public static Dictionary<string, CivData> LoadAll(string civsDir)
    {
        var result = new Dictionary<string, CivData>(StringComparer.Ordinal);
        if (!Directory.Exists(civsDir)) return result;
        foreach (var file in Directory.GetFiles(civsDir, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                string code = root.TryGetProperty("Code", out var c) ? c.GetString() ?? "" : "";
                if (code.Length == 0) continue;
                string name = root.TryGetProperty("Name", out var n) ? n.GetString() ?? code : code;
                string history = root.TryGetProperty("History", out var h) ? h.GetString() ?? "" : "";
                string description = root.TryGetProperty("Description", out var d) ? d.GetString() ?? "" : "";
                string emblem = root.TryGetProperty("Emblem", out var e) ? e.GetString() ?? "" : "";
                var start = new List<string>();
                if (root.TryGetProperty("StartEntities", out var se) && se.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in se.EnumerateArray())
                        if (item.TryGetProperty("Template", out var tmpl))
                            start.Add(tmpl.GetString() ?? "");
                }
                var bonuses = new List<CivBonus>();
                if (root.TryGetProperty("CivBonuses", out var cb) && cb.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in cb.EnumerateArray())
                    {
                        string bName = item.TryGetProperty("Name", out var bn) ? bn.GetString() ?? "" : "";
                        string bHist = item.TryGetProperty("History", out var bh) ? bh.GetString() ?? "" : "";
                        string bDesc = item.TryGetProperty("Description", out var bd) ? bd.GetString() ?? "" : "";
                        if (bName.Length > 0) bonuses.Add(new CivBonus(bName, bHist, bDesc));
                    }
                }
                result[code] = new CivData(code, name, history, description, emblem, start, bonuses);
            }
            catch { /* 容错：跳过坏文件 */ }
        }
        return result;
    }
}
