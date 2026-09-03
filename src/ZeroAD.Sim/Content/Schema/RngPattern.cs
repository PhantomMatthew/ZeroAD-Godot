using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace ZeroAD.Sim.Content.Schema;

/// <summary>RelaxNG 模式 AST(子集)。覆盖原版组件 Schema 实际用到的全部构造
/// (语料盘点:element/attribute/optional/zeroOrMore/oneOrMore/choice/interleave/
/// ref/text/data+param/value/empty/list/anyName;group/mixed/notAllowed 语料未用,
/// group 仍实现以保完整)。a: 命名空间注解(help/example/component)解析时丢弃。</summary>
public abstract record RngPattern;

/// <summary>名类:具名或 anyName 通配。(nsName 语料未用,遇到按解析错误处理。)</summary>
public abstract record RngNameClass;
public sealed record RngNamedName(string Name) : RngNameClass;
public sealed record RngAnyName : RngNameClass;

public sealed record RngEmpty : RngPattern;
public sealed record RngText : RngPattern;
public sealed record RngNotAllowed : RngPattern;
public sealed record RngData(string Type, IReadOnlyDictionary<string, string> Params) : RngPattern;
public sealed record RngValue(string Value) : RngPattern;
public sealed record RngElement(RngNameClass NameClass, RngPattern Content) : RngPattern;
public sealed record RngAttribute(RngNameClass NameClass, RngPattern Content) : RngPattern;
public sealed record RngChoice(IReadOnlyList<RngPattern> Options) : RngPattern;
public sealed record RngGroup(IReadOnlyList<RngPattern> Items) : RngPattern;
public sealed record RngInterleave(IReadOnlyList<RngPattern> Items) : RngPattern;
public sealed record RngOptional(RngPattern Inner) : RngPattern;
public sealed record RngZeroOrMore(RngPattern Inner) : RngPattern;
public sealed record RngOneOrMore(RngPattern Inner) : RngPattern;
public sealed record RngList(RngPattern Inner) : RngPattern;
public sealed record RngRef(string Name) : RngPattern;

/// <summary>编译后的 grammar:define 表 + start 模式。
/// 对应原版 CComponentManager::GenerateSchema 产出的整段 &lt;grammar&gt;。</summary>
public sealed class RngGrammar
{
    public required RngPattern Start { get; init; }
    public required IReadOnlyDictionary<string, RngPattern> Defines { get; init; }

    public RngPattern Resolve(RngRef r) =>
        Defines.TryGetValue(r.Name, out var p) ? p : new RngNotAllowed();
}

/// <summary>从 XElement 树解析 RelaxNG 子集。片段(组件 Schema 字符串)用
/// <see cref="ParseFragment"/> 包裹解析;命名空间宽松:RNG 默认命名空间或无命名空间皆收,
/// 注解命名空间(http://ns.wildfiregames.com/entity)的元素一律跳过。</summary>
public static class RngParser
{
    public const string RngNs = "http://relaxng.org/ns/structure/1.0";
    public const string AnnotationNs = "http://ns.wildfiregames.com/entity";

    public sealed class ParseException(string message) : Exception(message);

    /// <summary>解析组件 schema 片段(多个顶层模式的序列;空 → Empty;
    /// 单个 → 其本身;多个 → Group——上游裹 interleave 由 grammar 组合层负责)。</summary>
    public static RngPattern ParseFragment(string fragment)
    {
        // 片段内 a: 前缀与 RNG 默认命名空间都未声明,包一层声明后再解析。
        string wrapped = "<wrap xmlns='" + RngNs + "' xmlns:a='" + AnnotationNs + "'>"
            + fragment + "</wrap>";
        XElement root;
        try { root = XElement.Parse(wrapped); }
        catch (Exception e) { throw new ParseException("schema fragment is not well-formed XML: " + e.Message); }
        return ParseSequence(root.Elements());
    }

    /// <summary>解析完整 grammar 文档(&lt;grammar&gt; 根,含 define/start)。</summary>
    public static RngGrammar ParseGrammar(string grammarXml)
    {
        XElement root;
        try { root = XElement.Parse(grammarXml); }
        catch (Exception e) { throw new ParseException("grammar is not well-formed XML: " + e.Message); }
        if (!IsRng(root, "grammar"))
            throw new ParseException("grammar root is not <grammar>");

        var defines = new Dictionary<string, RngPattern>(StringComparer.Ordinal);
        RngPattern? start = null;
        foreach (var el in PatternElements(root))
        {
            if (IsRng(el, "define"))
            {
                string? name = (string?)el.Attribute("name")
                    ?? throw new ParseException("<define> without name");
                defines[name] = ParseSequence(el.Elements());
            }
            else if (IsRng(el, "start"))
            {
                start = ParseSequence(el.Elements());
            }
            // include/externalRef 语料未用;遇到即解析错误,不静默吞。
            else throw new ParseException("unsupported grammar child <" + el.Name.LocalName + ">");
        }
        if (start == null)
            throw new ParseException("grammar has no <start>");
        return new RngGrammar { Start = start, Defines = defines };
    }

    private static bool IsRng(XElement el, string local) =>
        el.Name.LocalName == local &&
        (el.Name.NamespaceName is "" or RngNs);

    private static bool IsAnnotation(XElement el) =>
        el.Name.NamespaceName == AnnotationNs;

    /// <summary>元素序列 → 模式(注解元素丢弃;0 → Empty;1 → 自身;多 → Group)。</summary>
    private static RngPattern ParseSequence(IEnumerable<XElement> elements)
    {
        var parts = new List<RngPattern>();
        foreach (var el in elements)
        {
            if (IsAnnotation(el)) continue;
            parts.Add(ParsePattern(el));
        }
        return parts.Count switch
        {
            0 => new RngEmpty(),
            1 => parts[0],
            _ => new RngGroup(parts),
        };
    }

    private static RngPattern ParsePattern(XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "element":
            case "attribute":
            {
                var (nameClass, rest) = ParseNameClass(el);
                var content = ParseSequence(rest);
                return el.Name.LocalName == "element"
                    ? new RngElement(nameClass, content)
                    : (RngPattern)new RngAttribute(nameClass, content);
            }
            case "optional": return new RngOptional(ParseSequence(el.Elements()));
            case "zeroOrMore": return new RngZeroOrMore(ParseSequence(el.Elements()));
            case "oneOrMore": return new RngOneOrMore(ParseSequence(el.Elements()));
            case "choice": return new RngChoice(ParseList(el));
            case "group": return new RngGroup(ParseList(el));
            case "interleave": return new RngInterleave(ParseList(el));
            case "list": return new RngList(ParseSequence(el.Elements()));
            case "ref":
                return new RngRef((string?)el.Attribute("name")
                    ?? throw new ParseException("<ref> without name"));
            case "text": return new RngText();
            case "empty": return new RngEmpty();
            case "notAllowed": return new RngNotAllowed();
            case "data":
            {
                string type = (string?)el.Attribute("type")
                    ?? throw new ParseException("<data> without type");
                var prms = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in el.Elements())
                {
                    if (IsAnnotation(p)) continue;
                    if (!IsRng(p, "param"))
                        throw new ParseException("unsupported <data> child <" + p.Name.LocalName + ">");
                    string? pname = (string?)p.Attribute("name")
                        ?? throw new ParseException("<param> without name");
                    prms[pname] = p.Value.Trim();
                }
                return new RngData(type, prms);
            }
            case "value": return new RngValue(el.Value.Trim());
            default:
                throw new ParseException("unsupported pattern <" + el.Name.LocalName + ">");
        }
    }

    private static List<RngPattern> ParseList(XElement el)
    {
        var list = new List<RngPattern>();
        foreach (var child in el.Elements())
        {
            if (IsAnnotation(child)) continue;
            list.Add(ParsePattern(child));
        }
        return list;
    }

    /// <summary>element/attribute 的名类:name 属性,或首个子节点 &lt;anyName/&gt;。
    /// 返回名类 + 剩余内容子节点。</summary>
    private static (RngNameClass, List<XElement>) ParseNameClass(XElement el)
    {
        string? name = (string?)el.Attribute("name");
        var rest = new List<XElement>();
        RngNameClass? nameClass = name != null ? new RngNamedName(name) : null;

        foreach (var child in el.Elements())
        {
            if (IsAnnotation(child)) continue;
            if (nameClass == null && IsRng(child, "anyName"))
            {
                nameClass = new RngAnyName();
                continue;
            }
            if (nameClass == null)
                throw new ParseException("<" + el.Name.LocalName +
                    "> without name attribute or anyName (nsName unsupported)");
            rest.Add(child);
        }
        if (nameClass == null)
            throw new ParseException("<" + el.Name.LocalName + "> has no name");
        return (nameClass, rest);
    }

    /// <summary>遍历元素子节点(供 grammar 顶层;注解跳过)。</summary>
    private static IEnumerable<XElement> PatternElements(XElement el)
    {
        foreach (var child in el.Elements())
            if (!IsAnnotation(child))
                yield return child;
    }
}
