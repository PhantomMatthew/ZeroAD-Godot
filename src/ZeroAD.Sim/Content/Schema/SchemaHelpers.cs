using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ZeroAD.Sim.Content.Schema;

/// <summary>组件 Schema 里 JS helper 调用的 C# 移植。原版在组件注册时执行 JS 求值
/// schema 片段;此处确定性展开同样的字符串。覆盖语料全部四种 helper:
/// Resources.BuildSchema / Resources.BuildChoicesSchema(simulation/helpers/Resources.js)、
/// RequirementsHelper.BuildSchema(helpers/Requirements.js)、
/// AttackHelper.BuildAttackEffectsSchema(helpers/Attack.js,含 globalscripts/
/// ModificationTemplates.js 的 ModificationSchema/ModificationsSchema 常量链)、
/// Resistance.prototype.BuildResistanceSchema(components/Resistance.js,同文件方法)。</summary>
public static class SchemaHelpers
{
    /// <summary>资源 schema 数据(simulation/data/resources/*.json 的 code + subtypes)。
    /// 原版 Resources.js 从 VFS 读全部资源定义(含禁用项,"disabled resources are
    /// included in the schema");顺序无关(schema 均裹 interleave)。</summary>
    public sealed record ResourceSchemaData(
        IReadOnlyList<string> Codes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Subtypes)
    {
        /// <summary>上游 public mod 的四资源兜底(VFS 缺失时用,如纯测试环境)。</summary>
        public static readonly ResourceSchemaData Default = new(
            new[] { "food", "wood", "stone", "metal" },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["food"] = new[] { "fish", "fruit", "grain", "meat" },
                ["wood"] = new[] { "tree", "ruins" },
                ["stone"] = new[] { "rock", "ruins" },
                ["metal"] = new[] { "ore", "ruins" },
            });

        /// <summary>VFS 分层读取 simulation/data/resources/*.json(mod 可加资源)。</summary>
        public static ResourceSchemaData LoadFromVfs(VfsResolver vfs)
        {
            var files = vfs.EnumerateLayered("simulation/data/resources", "*.json");
            if (files.Count == 0) return Default;
            var codes = new List<string>();
            var subtypes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var (_, absPath) in files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(absPath));
                    string? code = doc.RootElement.GetProperty("code").GetString();
                    if (string.IsNullOrEmpty(code)) continue;
                    codes.Add(code);
                    var subs = new List<string>();
                    if (doc.RootElement.TryGetProperty("subtypes", out var st)
                        && st.ValueKind == JsonValueKind.Object)
                        foreach (var prop in st.EnumerateObject())
                            subs.Add(prop.Name);
                    subtypes[code] = subs;
                }
                catch (Exception) { /* 单个资源文件损坏不阻塞 grammar */ }
            }
            return codes.Count == 0 ? Default : new ResourceSchemaData(codes, subtypes);
        }
    }

    // ── Resources.js ──

    public static string ResourcesBuildSchema(string datatype,
        IReadOnlyList<string>? additional = null, bool subtypes = false,
        ResourceSchemaData? res = null)
    {
        res ??= ResourceSchemaData.Default;
        additional ??= Array.Empty<string>();
        string dt = datatype is "decimal" or "nonNegativeDecimal" or "positiveDecimal"
            ? "<ref name='" + datatype + "'/>"
            : "<data type='" + datatype + "'/>";

        string schema = "";
        foreach (string code in res.Codes.Concat(additional))
            schema += "<optional><element name='" + code + "'>" + dt + "</element></optional>";
        if (subtypes)
            foreach (string code in res.Codes)
                foreach (string sub in res.Subtypes[code])
                    schema += "<optional><element name='" + code + "." + sub + "'>" + dt + "</element></optional>";
        return "<interleave>" + schema + "</interleave>";
    }

    public static string ResourcesBuildChoicesSchema(bool subtypes = false, ResourceSchemaData? res = null)
    {
        res ??= ResourceSchemaData.Default;
        string schema = "";
        if (!subtypes)
            foreach (string code in res.Codes)
                schema += "<value>" + code + "</value>";
        else
            foreach (string code in res.Codes)
                foreach (string sub in res.Subtypes[code])
                    schema += "<value>" + code + "." + sub + "</value>";
        return "<choice>" + schema + "</choice>";
    }

    // ── Requirements.js(RequirementsHelper.BuildSchema(),默认递归深度 1)──

    private const string EntityRequirementsSchema =
        "<element name='Entities' a:help='Entities that need to be controlled.'>" +
            "<oneOrMore>" +
                "<element a:help='Class of entity that needs to be controlled.'>" +
                    "<anyName/>" +
                    "<oneOrMore>" +
                        "<choice>" +
                            "<element name='Count' a:help='Number of entities required.'>" +
                                "<data type='nonNegativeInteger'/>" +
                            "</element>" +
                            "<element name='Variants' a:help='Number of different entities of this class required.'>" +
                                "<data type='nonNegativeInteger'/>" +
                            "</element>" +
                        "</choice>" +
                    "</oneOrMore>" +
                "</element>" +
            "</oneOrMore>" +
        "</element>";

    private const string TechnologyRequirementsSchema =
        "<element name='Techs' a:help='White-space separated list of technologies that need to be researched. ! negates a tech.'>" +
            "<attribute name='datatype'>" +
                "<value>tokens</value>" +
            "</attribute>" +
            "<text/>" +
        "</element>";

    private static string RequirementsSchema(int recursionDepth) =>
        "<oneOrMore>" + ChoicesSchema(recursionDepth - 1) + "</oneOrMore>";

    private static string ChoicesSchema(int recursionDepth)
    {
        string allAny = recursionDepth > 0
            ? "<element name='All' a:help='Requires all of the conditions to be met.'>" +
                RequirementsSchema(recursionDepth) + "</element>" +
              "<element name='Any' a:help='Requires at least one of the following conditions met.'>" +
                RequirementsSchema(recursionDepth) + "</element>"
            : "";
        return "<choice>" + allAny + EntityRequirementsSchema + TechnologyRequirementsSchema + "</choice>";
    }

    public static string RequirementsBuildSchema(int recursionDepth = 1) =>
        "<element name='Requirements' a:help='The requirements that ought to be met before this entity can be produced.'>" +
            "<optional>" + ChoicesSchema(recursionDepth) + "</optional>" +
            "<optional>" +
                "<element name='Tooltip' a:help='A tooltip explaining the requirements.'>" +
                    "<text/>" +
                "</element>" +
            "</optional>" +
        "</element>";

    // ── helpers/Attack.js + globalscripts/ModificationTemplates.js 常量链 ──

    /// <summary>globalscripts/ModificationTemplates.js: ModificationSchema(逐字)。</summary>
    private const string ModificationSchema =
        "<interleave>" +
            "<element name='Paths' a:help='Space separated value paths to modify.'>" +
                "<attribute name='datatype'>" +
                    "<value>tokens</value>" +
                "</attribute>" +
                "<text/>" +
            "</element>" +
            "<element name='Affects' a:help='An array of classes to affect.'>" +
                "<attribute name='datatype'>" +
                    "<value>tokens</value>" +
                "</attribute>" +
                "<text/>" +
            "</element>" +
            "<choice>" +
                "<element name='Add'>" +
                    "<data type='decimal' />" +
                "</element>" +
                "<element name='Multiply'>" +
                    "<data type='decimal' />" +
                "</element>" +
                "<element name='Replace'>" +
                    "<text/>" +
                "</element>" +
            "</choice>" +
        "</interleave>";

    private const string ModificationsSchema =
        "<element name='Modifiers' a:help='List of modifiers.'>" +
            "<oneOrMore>" +
                "<element>" +
                    "<anyName />" +
                    ModificationSchema +
                "</element>" +
            "</oneOrMore>" +
        "</element>";

    private const string DirectEffectsSchema =
        "<element name='Damage'>" +
            "<oneOrMore>" +
                "<element a:help='One or more elements describing damage types'>" +
                    "<anyName/>" +
                    "<ref name='nonNegativeDecimal' />" +
                "</element>" +
            "</oneOrMore>" +
        "</element>" +
        "<element name='Capture' a:help='Capture points value'>" +
            "<ref name='nonNegativeDecimal'/>" +
        "</element>";

    private const string StatusEffectsSchema =
        "<element name='ApplyStatus' a:help='Effects like poisoning or burning a unit.'>" +
            "<oneOrMore>" +
                "<element>" +
                    "<anyName a:help='The name must have a matching JSON file in data/status_effects.'/>" +
                    "<interleave>" +
                        "<optional>" +
                            "<element name='Duration' a:help='The duration of the status while the effect occurs.'><ref name='nonNegativeDecimal'/></element>" +
                        "</optional>" +
                        "<optional>" +
                            "<interleave>" +
                                "<element name='Interval' a:help='Interval between the occurrences of the effect.'><ref name='nonNegativeDecimal'/></element>" +
                                "<oneOrMore>" +
                                    "<choice>" +
                                        DirectEffectsSchema +
                                    "</choice>" +
                                "</oneOrMore>" +
                            "</interleave>" +
                        "</optional>" +
                        "<optional>" +
                            ModificationsSchema +
                        "</optional>" +
                        "<element name='Stackability' a:help='Defines how this status effect stacks'>" +
                            "<choice>" +
                                "<value>Ignore</value>" +
                                "<value>Extend</value>" +
                                "<value>Replace</value>" +
                                "<value>Stack</value>" +
                            "</choice>" +
                        "</element>" +
                    "</interleave>" +
                "</element>" +
            "</oneOrMore>" +
        "</element>";

    /// <summary>AttackHelper.BuildAttackEffectsSchema()(逐字语义;Stackability 的
    /// 长 a:help 注解略——注解解析时即丢弃,不影响接受性)。</summary>
    public static string AttackEffectsBuildSchema() =>
        "<oneOrMore>" +
            "<choice>" +
                DirectEffectsSchema +
                StatusEffectsSchema +
            "</choice>" +
        "</oneOrMore>" +
        "<optional>" +
            "<element name='Bonuses'>" +
                "<zeroOrMore>" +
                    "<element>" +
                        "<anyName/>" +
                        "<interleave>" +
                            "<optional>" +
                                "<element name='Civ' a:help='If an entity has this civ then the bonus is applied'><text/></element>" +
                            "</optional>" +
                            "<element name='Classes' a:help='If an entity has all these classes then the bonus is applied'><text/></element>" +
                            "<element name='Multiplier' a:help='The effect strength is multiplied by this'><ref name='nonNegativeDecimal'/></element>" +
                        "</interleave>" +
                    "</element>" +
                "</zeroOrMore>" +
            "</element>" +
        "</optional>";

    // ── components/Resistance.js: Resistance.prototype.BuildResistanceSchema() ──

    public static string ResistanceBuildSchema() =>
        "<oneOrMore>" +
            "<choice>" +
                "<element name='Damage'>" +
                    "<oneOrMore>" +
                        "<element a:help='Resistance against any number of damage types affecting health.'>" +
                            "<anyName/>" +
                            "<ref name='nonNegativeDecimal'/>" +
                        "</element>" +
                    "</oneOrMore>" +
                "</element>" +
                "<element name='Capture' a:help='Resistance against Capture attacks.'>" +
                    "<ref name='nonNegativeDecimal'/>" +
                "</element>" +
                "<element name='ApplyStatus' a:help='Resistance against StatusEffects.'>" +
                    "<oneOrMore>" +
                        "<element a:help='Resistance against any number of status effects.'>" +
                            "<anyName/>" +
                            "<interleave>" +
                                "<optional>" +
                                    "<element name='Duration' a:help='The reduction in duration of the status. The normal duration time is multiplied by this factor.'>" +
                                        "<ref name='nonNegativeDecimal'/>" +
                                    "</element>" +
                                "</optional>" +
                                "<optional>" +
                                    "<element name='BlockChance' a:help='The chance of blocking the status. In the interval [0,1].'><ref name='nonNegativeDecimal'/></element>" +
                                "</optional>" +
                            "</interleave>" +
                        "</element>" +
                    "</oneOrMore>" +
                "</element>" +
            "</choice>" +
        "</oneOrMore>";
}
