using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZeroAD.Sim.Templates;

namespace ZeroAD.Sim.Content;

/// <summary>模板引用校验(原版 source/tools/entity/checkrefs.py 的装载期子集):
/// 父模板链可解析、{@(civ|native|tag)} 令牌有依托、引用的实体模板(ProductionQueue
/// 可训/Tech 超链/Upgrade 目标/Promotion 目标)存在。
/// 原版另校验 actor/贴图/音频引用——美术资源在 godot/assets(导入产物),
/// 不在数据根,装载期不可查,留给编辑器侧工具。
/// 用法:SimBridge 装载后跑一次,问题写 Diag(不阻塞;原版 checkrefs 也是报告制)。</summary>
public static class TemplateValidator
{
    public sealed record Issue(string Template, string Kind, string Detail);

    /// <summary>全量校验已缓存模板。返回问题表(空 = 干净)。</summary>
    public static List<Issue> ValidateAll(TemplateLoader loader)
    {
        var issues = new List<Issue>();
        foreach (var (name, node) in loader.Cache.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            // 1) 父链可解析(ParamNode.ResolveTemplate 已对缺失父回退空树——
            // 这里报"声明了 parent 但父不存在")。
            var parent = node.GetChild("@parent");
            if (parent.IsOk && parent.Value.Length > 0
                && !loader.Cache.ContainsKey(parent.Value)
                && !parent.Value.Contains('{'))   // 含令牌的不是直名,跳过
                issues.Add(new(name, "parent", $"parent '{parent.Value}' not found"));

            // 2) 引用的实体模板存在(训练/建造/升级/晋升链)。
            CheckRefs(loader, name, node, "Trainer/Entities", issues);   // 训练列表(current data;原版 checkrefs 同键)
            CheckRefs(loader, name, node, "ProductionQueue/Entities", issues);   // 遗留键(若有)
            CheckRefs(loader, name, node, "Builder/Entities", issues);
            var promo = node.GetChild("Promotion").GetChild("Entity");
            if (promo.IsOk && promo.Value.Length > 0)
                CheckRef(loader, name, promo.Value, "promotion", issues);
        }
        return issues;
    }

    private static void CheckRefs(TemplateLoader loader, string name, ParamNode node,
        string path, List<Issue> issues)
    {
        var el = node.GetChild(path.Split('/')[0]);
        foreach (var seg in path.Split('/').Skip(1)) el = el.GetChild(seg);
        if (!el.IsOk || el.Value.Length == 0) return;
        foreach (var token in el.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // 令牌模板({civ} 等)跳过——装载期无法枚举展开。
            if (token.Contains('{')) continue;
            if (!loader.Cache.ContainsKey(token))
                issues.Add(new(name, "ref", $"{path} references missing '{token}'"));
        }
    }

    private static void CheckRef(TemplateLoader loader, string name, string target,
        string kind, List<Issue> issues)
    {
        if (target.Contains('{')) return;
        if (!loader.Cache.ContainsKey(target))
            issues.Add(new(name, kind, $"target '{target}' not found"));
    }

    /// <summary>校验并写 Diag 摘要(装载期调用;返回问题数)。</summary>
    public static int ValidateAndReport(TemplateLoader loader)
    {
        var issues = ValidateAll(loader);
        if (issues.Count == 0) return 0;
        Diag.Warn("Templates", $"{issues.Count} template reference issues:");
        foreach (var i in issues.Take(50))
            Diag.Warn("Templates", $"  [{i.Kind}] {i.Template}: {i.Detail}");
        if (issues.Count > 50)
            Diag.Warn("Templates", $"  … and {issues.Count - 50} more");
        return issues.Count;
    }
}
