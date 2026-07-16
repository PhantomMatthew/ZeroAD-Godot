using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Templates;

/// <summary>
/// Immutable entity template node — C# translation of <c>CParamNode</c>.
/// Supports parent inheritance (<c>parent="a|b"</c>), overlay merge, token lists,
/// <c>disable</c>, <c>replace</c>, <c>filtered</c>, <c>merge</c> directives.
/// </summary>
public sealed class ParamNode
{
    public string Value { get; private set; } = "";
    public IReadOnlyDictionary<string, ParamNode> Children => _children;
    private readonly SortedDictionary<string, ParamNode> _children = new();
    public bool IsOk { get; private set; } = true;

    private static readonly ParamNode InvalidNode = new() { IsOk = false };

    public ParamNode() { }
    public ParamNode(string value) { Value = value; }

    // --- Lookup ---

    public ParamNode GetChild(string name) =>
        _children.TryGetValue(name, out var child) ? child : InvalidNode;

    public ParamNode GetOnlyChild() =>
        _children.Count == 1 ? _children.First().Value : InvalidNode;

    public bool HasChild(string name) => _children.ContainsKey(name);

    // --- Conversions ---

    public int ToInt() => int.TryParse(Value, out var v) ? v : 0;
    public Fixed ToFixed() => Fixed.FromString(Value);
    public float ToFloat() => float.TryParse(Value, out var v) ? v : 0f;
    public bool ToBool() => Value == "true";

    public override string ToString() => Value;

    // --- XML loading ---

    public static ParamNode LoadXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        var node = new ParamNode();
        if (doc.Root != null)
            ApplyLayer(node, doc.Root);
        return node;
    }

    /// <summary>Overlay another XML document onto this node (merge semantics).</summary>
    public void MergeWithXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        if (doc.Root != null)
            ApplyLayer(this, doc.Root);
    }

    /// <summary>Create a deep copy of this node.</summary>
    public ParamNode Clone()
    {
        var clone = new ParamNode { Value = Value, IsOk = IsOk };
        foreach (var kvp in _children)
            clone._children[kvp.Key] = kvp.Value.Clone();
        return clone;
    }

    public static ParamNode LoadFile(string path)
    {
        return LoadXml(System.IO.File.ReadAllText(path));
    }

    // --- Parent resolution ---

    /// <summary>
    /// Resolve a template by name, loading parent chain first.
    /// parent="civ/athen|template_unit_cavalry" → load template_unit_cavalry (base),
    /// then overlay civ/athen, then overlay this template.
    /// The | separator splits right-to-left: rightmost is base, leftmost is most specific overlay.
    /// </summary>
    public static ParamNode ResolveTemplate(
        string templateName,
        Func<string, XDocument> loadTemplateXml)
    {
        var node = new ParamNode();
        ResolveTemplateInto(node, templateName, loadTemplateXml);
        return node;
    }

    private static void ResolveTemplateInto(
        ParamNode node,
        string templateName,
        Func<string, XDocument> loadTemplateXml)
    {
        int pipePos = templateName.IndexOf('|');
        if (pipePos >= 0)
        {
            ResolveTemplateInto(node, templateName[(pipePos + 1)..].Trim(), loadTemplateXml);
            ResolveTemplateInto(node, templateName[..pipePos].Trim(), loadTemplateXml);
            return;
        }

        var doc = loadTemplateXml(templateName);
        if (doc.Root == null)
            return;

        string? parentAttr = doc.Root.Attribute("parent")?.Value;
        if (!string.IsNullOrEmpty(parentAttr))
            ResolveTemplateInto(node, parentAttr, loadTemplateXml);

        ApplyLayer(node, doc.Root);
    }

    // --- Merge logic (ApplyLayer) ---

    private static void ApplyLayer(ParamNode target, XElement element)
    {
        bool replace = element.Attribute("replace") != null;
        bool filtered = element.Attribute("filtered") != null;
        bool disable = element.Attribute("disable") != null;

        if (disable)
            return;

        bool isTokens = element.Attribute("datatype")?.Value == "tokens";

        if (isTokens && !string.IsNullOrEmpty(target.Value))
        {
            target.Value = MergeTokens(target.Value, element.Value);
        }
        else
        {
            string text = element.Nodes().OfType<XText>().Select(t => t.Value).FirstOrDefault() ?? "";
            if (!string.IsNullOrEmpty(text) || !element.HasElements)
                target.Value = text.Trim();
        }

        if (replace)
            target._children.Clear();

        var existingNames = new HashSet<string>(target._children.Keys);
        var mentionedNames = new HashSet<string>();

        foreach (var attr in element.Attributes())
        {
            if (attr.Name.LocalName is "replace" or "filtered" or "disable" or "merge" or "datatype")
                continue;
            string name = "@" + attr.Name.LocalName;
            mentionedNames.Add(name);
            if (filtered && !existingNames.Contains(name))
                continue;
            target._children[name] = new ParamNode(attr.Value);
        }

        foreach (var childElem in element.Elements())
        {
            bool childDisable = childElem.Attribute("disable") != null;
            bool childMerge = childElem.Attribute("merge") != null;
            string name = childElem.Name.LocalName;
            mentionedNames.Add(name);

            if (childDisable)
            {
                target._children.Remove(name);
                continue;
            }

            if (filtered && !childMerge && !existingNames.Contains(name))
                continue;

            if (childMerge && !existingNames.Contains(name))
                continue;

            if (!target._children.TryGetValue(name, out var child))
            {
                child = new ParamNode();
                target._children[name] = child;
            }

            ApplyLayer(child, childElem);
        }

        if (filtered)
        {
            var toRemove = new List<string>();
            foreach (var name in target._children.Keys)
                if (!mentionedNames.Contains(name))
                    toRemove.Add(name);
            foreach (var name in toRemove)
                target._children.Remove(name);
        }
    }

    private static string MergeTokens(string existing, string overlay)
    {
        var tokens = new LinkedHashSet<string>();
        foreach (string t in existing.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            tokens.Add(t);

        foreach (string tok in overlay.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok.StartsWith('-') && tok.Length > 1)
                tokens.Remove(tok[1..]);
            else
                tokens.Add(tok);
        }

        return string.Join(' ', tokens);
    }

    // --- XML serialization ---

    public string ToXmlString()
    {
        var sb = new System.Text.StringBuilder();
        ToXmlString(sb);
        return sb.ToString();
    }

    private void ToXmlString(System.Text.StringBuilder sb)
    {
        if (!string.IsNullOrEmpty(Value) && _children.Count == 0)
        {
            sb.Append(EscapeXml(Value));
            return;
        }

        foreach (var kvp in _children)
        {
            sb.Append('<').Append(kvp.Key).Append('>');
            kvp.Value.ToXmlString(sb);
            sb.Append("</").Append(kvp.Key).Append('>');
        }

        if (!string.IsNullOrEmpty(Value) && _children.Count > 0)
            sb.Append(EscapeXml(Value));
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

internal sealed class LinkedHashSet<T> : IEnumerable<T>
{
    private readonly HashSet<T> _set = new();
    private readonly List<T> _order = new();

    public void Add(T item)
    {
        if (_set.Add(item))
            _order.Add(item);
    }

    public void Remove(T item)
    {
        if (_set.Remove(item))
            _order.Remove(item);
    }

    public IEnumerator<T> GetEnumerator() => _order.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
