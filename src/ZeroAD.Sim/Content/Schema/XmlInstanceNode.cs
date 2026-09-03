using System.Collections.Generic;
using System.Xml.Linq;
using ZeroAD.Sim.Templates;

namespace ZeroAD.Sim.Content.Schema;

/// <summary>校验用 XML 实例模型(原版 CXeromyces 验证端的输入)。
/// 两种来源:ParamNode 合并树("@" 前缀伪子节点 → 属性;对应原版 CParamNode::ToXMLString
/// 重序列化后的树)与 XElement(测试直接用)。文本取节点 Value(ParamNode 已 trim,
/// 与原版重序列化行为一致)。</summary>
public sealed class XmlInstanceNode
{
    public required string Name { get; init; }
    public string Text { get; set; } = "";
    public List<(string Name, string Value)> Attributes { get; } = new();
    public List<XmlInstanceNode> Children { get; } = new();

    /// <summary>从合并后的模板根(组件层)构建合成根节点。rootName 任意
    /// (grammar 对根元素名 anyName;上游 ded41eab 起不限 Entity)。</summary>
    public static XmlInstanceNode FromTemplateRoot(ParamNode root, string rootName = "Entity")
    {
        var node = new XmlInstanceNode { Name = rootName };
        FillFromParamNode(node, root);
        return node;
    }

    private static XmlInstanceNode FromParamNodeChild(string name, ParamNode param)
    {
        var node = new XmlInstanceNode { Name = name };
        FillFromParamNode(node, param);
        return node;
    }

    private static void FillFromParamNode(XmlInstanceNode node, ParamNode param)
    {
        node.Text = param.Value ?? "";
        foreach (var (name, child) in param.Children)
        {
            if (name.StartsWith('@'))
                node.Attributes.Add((name[1..], child.Value ?? ""));
            else
                node.Children.Add(FromParamNodeChild(name, child));
        }
    }

    public static XmlInstanceNode FromXElement(XElement el)
    {
        var node = new XmlInstanceNode { Name = el.Name.LocalName };
        var text = new System.Text.StringBuilder();
        foreach (var n in el.Nodes())
            if (n is XText t)
                text.Append(t.Value);
        node.Text = text.ToString().Trim();
        foreach (var attr in el.Attributes())
            node.Attributes.Add((attr.Name.LocalName, attr.Value));
        foreach (var child in el.Elements())
            node.Children.Add(FromXElement(child));
        return node;
    }
}
