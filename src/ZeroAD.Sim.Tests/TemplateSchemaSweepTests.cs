using System;
using System.IO;
using System.Linq;
using Xunit;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Content.Schema;

namespace ZeroAD.Sim.Tests;

/// <summary>全量 schema sweep:对真实数据树(binaries/  junction)构建完整 grammar
/// (JS 组件提取 + 原生表),校验全部模板的合并树。这是 schema 移植正确性的金丝雀:
/// 任何提取器/grammar/校验器缺陷都会在这里现形(原版 libxml2 校验同一语料通过)。
/// junction 缺失时静默跳过(与 AllTemplatesParseTests 同约定)。</summary>
public class TemplateSchemaSweepTests
{
    private static string? FindRepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, relative);
    }

    [Fact]
    public void AllComponentsExtractCleanly()
    {
        string? modsRoot = FindRepoPath("binaries/data/mods");
        if (modsRoot == null) return;
        var vfs = new VfsResolver(modsRoot, new[] { "mod", "public" });
        var schema = TemplateSchema.Build(vfs);
        // 提取告警(JS 解析失败/片段解析失败降级 anything)必须为零——
        // 降级意味着该组件的校验被跳过,parity 破洞。
        Assert.Empty(schema.Warnings);
    }

    [Fact]
    public void GrammarCoversAllComponentNamesInUse()
    {
        string? modsRoot = FindRepoPath("binaries/data/mods");
        if (modsRoot == null) return;
        var vfs = new VfsResolver(modsRoot, new[] { "mod", "public" });
        var schema = TemplateSchema.Build(vfs);
        // 语料盘点(2026-09):全部 73 个顶层元素名都应有 define。
        foreach (var name in new[]
        {
            "AIProxy", "AlertRaiser", "Attack", "Auras", "Builder", "BuildingAI",
            "BuildRestrictions", "Capturable", "Cost", "Decay", "Diplomacy",
            "EntityLimits", "Fogging", "Footprint", "Formation", "FormationAttack",
            "Foundation", "Garrisonable", "GarrisonHolder", "Gate", "Guard", "Heal",
            "Health", "Identity", "Loot", "Looter", "Market", "Minimap", "Mirage",
            "Obstruction", "OverlayRenderer", "Ownership", "Pack", "Player",
            "Population", "Position", "ProductionQueue", "Promotion", "RallyPoint",
            "RallyPointRenderer", "RangeOverlayManager", "RangeOverlayRenderer",
            "Repairable", "Researcher", "Resistance", "ResourceDropsite",
            "ResourceGatherer", "ResourceSupply", "ResourceTrickle", "Selectable",
            "SkirmishReplacer", "Sound", "StatisticsTracker", "StatusBars",
            "StatusEffectsReceiver", "TechnologyManager", "TerritoryDecay",
            "TerritoryInfluence", "Trader", "Trainer", "TrainingRestrictions",
            "Treasure", "TreasureCollector", "TriggerPoint", "Turretable",
            "TurretHolder", "UnitAI", "UnitMotion", "UnitMotionFlying", "Upgrade",
            "Visibility", "Vision", "VisionSharing", "VisualActor", "WallPiece",
            "WallSet", "Wonder",
        })
        {
            Assert.True(schema.Grammar.Defines.ContainsKey("component." + name),
                $"grammar missing define for component '{name}'");
        }
    }

    [Fact]
    public void AllTemplatesPassSchemaValidation()
    {
        string? modsRoot = FindRepoPath("binaries/data/mods");
        if (modsRoot == null) return;
        var vfs = new VfsResolver(modsRoot, new[] { "mod", "public" });
        var schema = TemplateSchema.Build(vfs);
        Assert.Empty(schema.Warnings);

        var loader = new TemplateLoader(vfs);
        loader.LoadAllTemplates();
        Assert.True(loader.Cache.Count > 1000, $"expected >1000 templates, got {loader.Cache.Count}");

        var validator = new TemplateSchemaValidator(schema);
        var issues = validator.ValidateAll(loader);
        // 预期失败集 = template_* 抽象父 + special/actor(上游同此结局:grammar 对它们
        // 同样拒载——Identity/Icon、VisualActor/Actor 等必需项在抽象父留空;上游注释
        // "inherited parents may individually be invalid"。运行时从不以独立名请求)。
        // 不变量:除此之外的一切模板(全部具体可生成内容,含 special/players 等)
        // 必须零错误。
        var unexpected = issues.Where(i =>
            !i.Template.StartsWith("template_") && i.Template != "special/actor").ToList();
        if (unexpected.Count > 0)
        {
            string dump = string.Join("\n", unexpected.Take(30).Select(i =>
                $"{i.Template}:\n  {string.Join("\n  ", i.Errors.Take(3))}"));
            Assert.Fail($"{unexpected.Count} non-abstract template(s) failed schema validation:\n{dump}");
        }
        // 抽象父失败数上界(2026-09 基线 177;防回归冒头)。
        Assert.True(issues.Count <= 250,
            $"abstract-parent failures grew unexpectedly: {issues.Count}");
    }
}
