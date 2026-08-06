using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Templates;

namespace ZeroAD.Godot.Structree;

/// <summary>科技树数据模型（纯 POCO，供 StructreePanel 渲染）。</summary>

public sealed record TechEntry(string Name, string DisplayName, string Icon,
    int WoodCost, int FoodCost, int StoneCost, int MetalCost);

public sealed record TreeEntry(
    string Template,            // "structures/athen/civic_centre"
    string DisplayName,         // GenericName 或模板名
    string Icon,                // Identity/Icon（相对 portraits 路径）
    int PhaseIndex,             // 0=village, 1=town, 2=city
    List<TreeEntry> TrainableUnits,
    List<TechEntry> ResearchableTechs);

public sealed record PhaseColumn(string PhaseName, List<TreeEntry> Buildings);

public sealed record CivTree(List<PhaseColumn> Phases);

/// <summary>从 TemplateLoader + TechnologyLoader + CivData 构建 civ 科技树。
/// 移植原版 TemplateLister.compileTemplateLists 简化版：BFS 从 StartEntities 级联，
/// 读 Trainer/Researcher/Builder（直接查 ParamNode，不经 ExtractStats），按 phase 归类。
/// 不做变体折叠/精确坐标布局/wallset 展开。</summary>
public static class TechTreeBuilder
{
    private static readonly string[] PhaseOrder = { "phase_village", "phase_town", "phase_city" };

    /// <summary>构建指定文明的科技树。
    /// 遍历对齐原版 compileTemplateLists:从 StartEntities 出发,沿三类链接 BFS——
    /// Trainer/Entities(建筑/CC → 可训单位)、Builder/Entities(单位 → 可建建筑,
    /// 原版建筑列表实际挂在工人模板上)、Researcher/Technologies(叶子)。
    /// 此前只从建筑读 Builder(恒空)导致树只剩 CC 一张卡。</summary>
    public static CivTree Build(CivData civ, TemplateLoader templates, TechCatalog techCatalog)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        var buildings = new List<TreeEntry>();

        // 种子:全部起始实体(建筑与单位都入队——单位携 Builder/Entities 链接)。
        foreach (var seed in civ.StartEntities)
            if (templates.TemplateExists(seed))
                queue.Enqueue(seed);

        while (queue.Count > 0)
        {
            var tmpl = queue.Dequeue();
            if (!visited.Add(tmpl)) continue;  // 去重
            if (!templates.Cache.TryGetValue(tmpl, out var node)) continue;

            bool isStructure = tmpl.StartsWith("structures/", System.StringComparison.Ordinal);
            if (isStructure)
            {
                var stats = templates.ExtractStats(tmpl);
                int phaseIdx = GetPhaseIndex(node);

                // 读 Trainer/Entities（可训练单位）。
                var units = ReadTokenList(node, "Trainer", "Entities")
                    .Select(t => ResolveTemplate(t, civ.Code, templates))
                    .Where(t => t != null && templates.TemplateExists(t))
                    .Select(t => MakeUnitEntry(t!, templates))!;

                // 读 Researcher/Technologies（可研究科技）。
                var techs = ReadTokenList(node, "Researcher", "Technologies")
                    .Select(t => ResolveTech(t, civ.Code, techCatalog))
                    .Where(t => t != null)
                    .Select(t => MakeTechEntry(t!, techCatalog))!;

                buildings.Add(new TreeEntry(
                    tmpl, stats.GenericName.Length > 0 ? stats.GenericName : stats.Name,
                    stats.Icon, phaseIdx,
                    units.ToList(), techs.ToList()));
            }

            // 链接遍历(建筑/单位通用):Trainer/Entities(生产列表)+
            // Builder/Entities(可建列表;挂在工人单位模板上)。
            foreach (var child in ReadTokenList(node, "Trainer", "Entities"))
            {
                var resolved = ResolveTemplate(child, civ.Code, templates);
                if (resolved != null && templates.TemplateExists(resolved) && !visited.Contains(resolved))
                    queue.Enqueue(resolved);
            }
            foreach (var child in ReadTokenList(node, "Builder", "Entities"))
            {
                var resolved = ResolveTemplate(child, civ.Code, templates);
                if (resolved != null && templates.TemplateExists(resolved) && !visited.Contains(resolved))
                    queue.Enqueue(resolved);
            }
        }

        // 按 phase 分栏。
        var phases = new List<PhaseColumn>();
        for (int i = 0; i < PhaseOrder.Length; i++)
        {
            var phaseBuildings = buildings.Where(b => b.PhaseIndex == i).ToList();
            // 去重同名建筑（BFS 可能从多个路径到达）。
            var deduped = phaseBuildings.GroupBy(b => b.Template).Select(g => g.First()).ToList();
            phases.Add(new PhaseColumn(PhaseOrder[i], deduped));
        }
        return new CivTree(phases);
    }

    // ── ParamNode 读取辅助 ──

    /// <summary>读 child1/child2 的 token 列表（空格分隔）。返回原始 token（含 {civ} 占位符）。</summary>
    private static IEnumerable<string> ReadTokenList(ParamNode node, string child1, string child2)
    {
        if (!node.HasChild(child1)) return System.Array.Empty<string>();
        var child = node.GetChild(child1);
        if (!child.HasChild(child2)) return System.Array.Empty<string>();
        var value = child.GetChild(child2).Value;
        if (string.IsNullOrWhiteSpace(value)) return System.Array.Empty<string>();
        // 按全部空白符分词(XML 原文有换行/缩进;按空格分会把 \n 带进 token)。
        return value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>解析 {civ} 占位符 → 实际模板名。{civ}=civ code；{native}=模板的 Identity/Civ。
    /// 尝试 {civ} 替换；若结果不存在则 fallback 试 generic（去 civ 段）。</summary>
    private static string? ResolveTemplate(string token, string civCode, TemplateLoader templates)
    {
        string resolved = token.Replace("{civ}", civCode).Replace("{native}", civCode);
        if (templates.TemplateExists(resolved)) return resolved;
        // Fallback：尝试 generic（如 units/{native}/support_civilian → units/athen/support_civilian 已试过；
        // 再试去掉中间 civ 段的通用模板）。
        return null;  // 不存在则跳过
    }

    /// <summary>解析科技 token（含 {civ}）。展开 pair → 两个子科技。</summary>
    private static string? ResolveTech(string token, string civCode, TechCatalog catalog)
    {
        string resolved = token.Replace("{civ}", civCode);
        if (catalog.Technologies.ContainsKey(resolved)) return resolved;
        if (catalog.Pairs.ContainsKey(resolved)) return resolved;  // pair 在 UI 展开时处理
        return null;
    }

    // ── 入口构造 ──

    private static TreeEntry MakeUnitEntry(string template, TemplateLoader templates)
    {
        var stats = templates.ExtractStats(template);
        var node = templates.Cache.TryGetValue(template, out var n) ? n : null;
        return new TreeEntry(
            template,
            stats.GenericName.Length > 0 ? stats.GenericName : stats.Name,
            stats.Icon,
            node != null ? GetPhaseIndex(node) : 0,
            new List<TreeEntry>(), new List<TechEntry>());
    }

    private static TechEntry MakeTechEntry(string techName, TechCatalog catalog)
    {
        // pair：取第一个子科技展示（简化）。
        if (catalog.Pairs.TryGetValue(techName, out var pairTechs) && pairTechs.Count > 0)
            return MakeTechEntry(pairTechs[0], catalog);
        var def = catalog.Technologies.TryGetValue(techName, out var d) ? d : null;
        return new TechEntry(
            techName,
            def?.GenericName ?? techName,
            "",  // 科技图标（Identity/Icon 在科技 JSON 里无标准字段，暂空）
            def?.Wood ?? 0, def?.Food ?? 0, def?.Stone ?? 0, def?.Metal ?? 0);
    }

    /// <summary>从 Identity/Requirements/Techs 取 phase 索引。
    /// 最高 phase token 决定（phase_town > phase_village）；无则 0（village）。</summary>
    private static int GetPhaseIndex(ParamNode node)
    {
        if (!node.HasChild("Identity")) return 0;
        var identity = node.GetChild("Identity");
        if (!identity.HasChild("Requirements")) return 0;
        var req = identity.GetChild("Requirements");
        if (!req.HasChild("Techs")) return 0;
        var techs = req.GetChild("Techs").Value;
        int best = 0;
        foreach (var token in techs.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("-") || token.StartsWith("!")) continue;  // 否定跳过
            for (int i = 0; i < PhaseOrder.Length; i++)
                if (token == PhaseOrder[i] || token.StartsWith(PhaseOrder[i])) best = System.Math.Max(best, i);
        }
        return best;
    }
}
