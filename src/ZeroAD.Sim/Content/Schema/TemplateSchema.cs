using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZeroAD.Sim.Templates;

namespace ZeroAD.Sim.Content.Schema;

/// <summary>模板 grammar 组合 + 全量校验驱动——原版 CComponentManager::GenerateSchema()
/// 与 CCmpTemplateManager 校验环节(Validate + m_TemplateSchemaValidity 记忆化)的移植。
/// grammar 骨架(上游 ComponentManager.cpp:1144-1216 逐字语义):
///   define decimal / nonNegativeDecimal / positiveDecimal / anything;
///   每组件 define component.NAME = &lt;element NAME&gt;&lt;interleave&gt;片段&lt;/interleave&gt;&lt;/element&gt;;
///   start = element(anyName) { optional attribute parent; 全部组件 optional ref(字母序) }。
/// 上游 start 用字母序 group(配合 libxml2 报错),此处用 interleave(语义等价,
/// 接受性与顺序无关;我们的合并树反正按 SortedDictionary 字母序)。</summary>
public sealed class TemplateSchema
{
    public RngGrammar Grammar { get; }
    /// <summary>组件提取/解析告警(JS 提取失败 → 该组件降级 anything 并在此记录)。</summary>
    public IReadOnlyList<string> Warnings { get; }

    private TemplateSchema(RngGrammar grammar, IReadOnlyList<string> warnings)
    {
        Grammar = grammar;
        Warnings = warnings;
    }

    /// <summary>VFS 分层构建:simulation/components/*.js(顶层;interfaces/、tests/
    /// 子目录排除)+ 原生组件表 + simulation/data/resources/*.json。</summary>
    public static TemplateSchema Build(VfsResolver vfs)
    {
        var warnings = new List<string>();
        var resources = SchemaHelpers.ResourceSchemaData.LoadFromVfs(vfs);

        var fragments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rel, absPath) in vfs.EnumerateLayered("simulation/components", "*.js"))
        {
            if (rel.Contains('/')) continue;   // interfaces/ tests/ 等子目录非组件
            string fileName = Path.GetFileNameWithoutExtension(rel);
            string js;
            try { js = File.ReadAllText(absPath); }
            catch (Exception e) { warnings.Add($"components/{rel}: unreadable ({e.Message})"); continue; }

            try
            {
                var result = ComponentSchemaExtractor.Extract(fileName, js, resources);
                string name = result?.ComponentName ?? fileName;
                // 上游:无 Schema 属性 → "<empty/>"(ComponentManager.cpp:263-267)。
                fragments[name] = result?.Schema ?? "<empty/>";
            }
            catch (ComponentSchemaExtractor.ExtractException e)
            {
                // 提取失败 → 降级 anything(单点失败不级联误伤数百模板),告警记录。
                warnings.Add($"components/{rel}: schema extraction failed ({e.Message}); degraded to <anything>");
                fragments[fileName] = "<ref name='anything'/>";
            }
        }

        // 原生组件(JS 未覆盖的);同名不覆盖(JS 优先——上游同名不会发生)。
        foreach (var (name, fragment) in NativeComponentSchemas.All)
            if (!fragments.ContainsKey(name))
                fragments[name] = fragment;

        return new TemplateSchema(ComposeGrammar(fragments, warnings), warnings);
    }

    /// <summary>直接由 名→片段 表构建(测试用)。</summary>
    public static TemplateSchema FromFragments(IReadOnlyDictionary<string, string> fragments)
        => new(ComposeGrammar(fragments, new List<string>()), new List<string>());

    private static RngGrammar ComposeGrammar(
        IReadOnlyDictionary<string, string> fragments, List<string> warnings)
    {
        var defines = new Dictionary<string, RngPattern>(StringComparer.Ordinal)
        {
            ["decimal"] = new RngData("decimal", new Dictionary<string, string>()),
            ["nonNegativeDecimal"] = new RngData("decimal",
                new Dictionary<string, string> { ["minInclusive"] = "0" }),
            ["positiveDecimal"] = new RngData("decimal",
                new Dictionary<string, string> { ["minExclusive"] = "0" }),
            // define anything:zeroOrMore(choice(attribute anyName, text, element anyName ref anything))
            ["anything"] = new RngZeroOrMore(new RngChoice(new RngPattern[]
            {
                new RngAttribute(new RngAnyName(), new RngText()),
                new RngText(),
                new RngElement(new RngAnyName(), new RngRef("anything")),
            })),
        };

        foreach (var (name, fragment) in fragments.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            RngPattern inner;
            try
            {
                inner = RngParser.ParseFragment(fragment);
            }
            catch (RngParser.ParseException e)
            {
                warnings.Add($"component '{name}': schema parse failed ({e.Message}); degraded to <anything>");
                inner = new RngRef("anything");
            }
            // 上游:<define component.NAME><element NAME><interleave>片段</interleave></element></define>。
            // 片段顶层序列(Group)摊平进 interleave。
            IReadOnlyList<RngPattern> interleaveItems = inner is RngGroup g ? g.Items : new[] { inner };
            defines["component." + name] = new RngElement(new RngNamedName(name),
                new RngInterleave(interleaveItems));
        }

        // start:element(anyName){ optional attribute parent; interleave(optional ref component.*) }
        var componentRefs = fragments.Keys
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => (RngPattern)new RngOptional(new RngRef("component." + k)))
            .ToList();
        var start = new RngElement(new RngAnyName(), new RngGroup(new RngPattern[]
        {
            new RngOptional(new RngAttribute(new RngNamedName("parent"), new RngText())),
            new RngInterleave(componentRefs),
        }));
        return new RngGrammar { Start = start, Defines = defines };
    }
}

/// <summary>校验驱动:合并树 → 合成根 → RngValidator。对应上游
/// CCmpTemplateManager::GetTemplate 里 m_Validator.Validate(templateName, merged.ToXMLString())。</summary>
public sealed class TemplateSchemaValidator
{
    private readonly RngValidator _validator;

    public TemplateSchemaValidator(TemplateSchema schema) => _validator = new RngValidator(schema.Grammar);

    public sealed record Issue(string Template, IReadOnlyList<string> Errors);

    /// <summary>单模板(合并根)校验;返回错误列表(空 = 通过)。</summary>
    public List<string> ValidateOne(ParamNode mergedRoot)
        => _validator.Validate(XmlInstanceNode.FromTemplateRoot(mergedRoot));

    /// <summary>模板名是否指代一个"可直接请求的独立模板"。mixins/ 与 special/filter/
    /// 的 XML 是继承图层(partial overlay),从不以独立模板身份校验——上游同样只在
    /// 合并后校验(m_TemplateSchemaValidity 注释:inherited parents may individually
    /// be invalid)。对它们单独套用 grammar 必然误报。</summary>
    public static bool IsStandaloneTemplateName(string name) =>
        !name.StartsWith("mixins/", StringComparison.Ordinal)
        && !name.StartsWith("special/filter/", StringComparison.Ordinal);

    /// <summary>校验加载器缓存的全部模板(上游 memo 语义:每模板一次;
    /// mixin/filter 图层跳过,见 IsStandaloneTemplateName)。</summary>
    public List<Issue> ValidateAll(TemplateLoader loader)
    {
        var issues = new List<Issue>();
        foreach (var (name, node) in loader.Cache.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!IsStandaloneTemplateName(name)) continue;
            var errors = ValidateOne(node);
            if (errors.Count > 0)
                issues.Add(new Issue(name, errors));
        }
        return issues;
    }

    /// <summary>全量校验 + Diag 报告(与 TemplateValidator.ValidateAndReport 同风格)。</summary>
    public int ValidateAndReport(TemplateLoader loader)
    {
        var issues = ValidateAll(loader);
        if (issues.Count == 0)
        {
            Diag.Log("Templates", $"schema validation: {loader.Cache.Count} templates, all valid");
            return 0;
        }
        Diag.Warn("Templates", $"schema validation: {issues.Count} template(s) INVALID:");
        foreach (var issue in issues.Take(50))
        {
            Diag.Warn("Templates", $"  {issue.Template}: {issue.Errors.Count} error(s); first: {issue.Errors[0]}");
        }
        if (issues.Count > 50)
            Diag.Warn("Templates", $"  … and {issues.Count - 50} more");
        return issues.Count;
    }
}
