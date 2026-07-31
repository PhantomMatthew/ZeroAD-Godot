using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim.Templates;

namespace ZeroAD.Sim.Tests;

public class ParamNodeTests
{
    [Fact]
    public void LoadXml_SimpleElement()
    {
        var node = ParamNode.LoadXml("<Entity><Health>100</Health></Entity>");
        Assert.True(node.HasChild("Health"));
        Assert.Equal("100", node.GetChild("Health").ToString());
    }

    [Fact]
    public void LoadXml_NestedElements()
    {
        var node = ParamNode.LoadXml(
            "<Entity><Cost><Population>1</Population><BuildTime>10</BuildTime></Cost></Entity>");
        var cost = node.GetChild("Cost");
        Assert.True(cost.IsOk);
        Assert.Equal("1", cost.GetChild("Population").ToString());
        Assert.Equal("10", cost.GetChild("BuildTime").ToString());
    }

    [Fact]
    public void LoadXml_AttributesBecomeChildren()
    {
        var node = ParamNode.LoadXml("<Entity><Actor file='units/foo.xml'>test</Actor></Entity>");
        var actor = node.GetChild("Actor");
        Assert.Equal("test", actor.ToString());
        Assert.True(actor.HasChild("@file"));
        Assert.Equal("units/foo.xml", actor.GetChild("@file").ToString());
    }

    [Fact]
    public void Merge_OverlayReplacesValue()
    {
        var node = ParamNode.LoadXml("<Entity><Name>Base</Name><Health>100</Health></Entity>");
        node.MergeWithXml("<Entity><Name>Override</Name></Entity>");
        Assert.Equal("Override", node.GetChild("Name").ToString());
        Assert.Equal("100", node.GetChild("Health").ToString());
    }

    [Fact]
    public void Merge_DisableRemovesElement()
    {
        var node = ParamNode.LoadXml("<Entity><Health>100</Health><Cost>50</Cost></Entity>");
        node.MergeWithXml("<Entity><Health disable=''/></Entity>");
        Assert.False(node.HasChild("Health"));
        Assert.True(node.HasChild("Cost"));
    }

    [Fact]
    public void Merge_ReplaceClearsChildren()
    {
        var node = ParamNode.LoadXml(
            "<Entity><Cost><Food>100</Food><Wood>50</Wood></Cost></Entity>");
        node.MergeWithXml("<Entity><Cost replace=''><Stone>25</Stone></Cost></Entity>");

        var cost = node.GetChild("Cost");
        Assert.False(cost.HasChild("Food"));
        Assert.False(cost.HasChild("Wood"));
        Assert.True(cost.HasChild("Stone"));
        Assert.Equal("25", cost.GetChild("Stone").ToString());
    }

    [Fact]
    public void Merge_TokenList_AddsAndRemoves()
    {
        var node = ParamNode.LoadXml(
            "<Entity><Classes datatype='tokens'>unit infantry sword</Classes></Entity>");
        node.MergeWithXml(
            "<Entity><Classes datatype='tokens'>cavalry -sword</Classes></Entity>");

        string classes = node.GetChild("Classes").ToString();
        Assert.Contains("unit", classes);
        Assert.Contains("infantry", classes);
        Assert.Contains("cavalry", classes);
        Assert.DoesNotContain("sword", classes);
    }

    [Fact]
    public void Merge_FilteredKeepsOnlySpecified()
    {
        var node = ParamNode.LoadXml(
            "<Entity><Cost><Food>100</Food><Wood>50</Wood><Stone>25</Stone></Cost></Entity>");
        node.MergeWithXml("<Entity><Cost filtered=''><Food>200</Food></Cost></Entity>");

        var cost = node.GetChild("Cost");
        Assert.True(cost.HasChild("Food"));
        Assert.Equal("200", cost.GetChild("Food").ToString());
        Assert.False(cost.HasChild("Wood"));
        Assert.False(cost.HasChild("Stone"));
    }

    [Fact]
    public void Merge_MergeAttribute_OnlyAppliesIfChildExists()
    {
        var node = ParamNode.LoadXml(
            "<Entity><Cost><Food>100</Food></Cost></Entity>");
        node.MergeWithXml(
            "<Entity><Cost filtered=''><Food merge=''>200</Food><Wood merge=''>50</Wood></Cost></Entity>");

        var cost = node.GetChild("Cost");
        Assert.True(cost.HasChild("Food"));
        Assert.Equal("200", cost.GetChild("Food").ToString());
        Assert.False(cost.HasChild("Wood"));
    }

    [Fact]
    public void ResolveTemplate_ParentInheritance()
    {
        var templates = new Dictionary<string, string>
        {
            ["template_unit"] = "<Entity><Health>100</Health><Cost>50</Cost></Entity>",
            ["civ/athen"] = "<Entity><Cost>60</Cost></Entity>",
            ["units/athen/soldier"] =
                "<Entity parent='civ/athen|template_unit'><Health>120</Health></Entity>",
        };

        var node = ParamNode.ResolveTemplate(
            "units/athen/soldier",
            name => System.Xml.Linq.XDocument.Parse(templates[name]));

        Assert.Equal("120", node.GetChild("Health").ToString());
        Assert.Equal("60", node.GetChild("Cost").ToString());
    }

    [Fact]
    public void ResolveTemplate_DeepInheritance()
    {
        var templates = new Dictionary<string, string>
        {
            ["template_entity"] = "<Entity><Health>1</Health></Entity>",
            ["template_unit"] = "<Entity parent='template_entity'><Cost>10</Cost></Entity>",
            ["template_unit_infantry"] =
                "<Entity parent='template_unit'><Armor>2</Armor></Entity>",
            ["units/soldier"] =
                "<Entity parent='template_unit_infantry'><Health>100</Health></Entity>",
        };

        var node = ParamNode.ResolveTemplate(
            "units/soldier",
            name => System.Xml.Linq.XDocument.Parse(templates[name]));

        Assert.Equal("100", node.GetChild("Health").ToString());
        Assert.Equal("10", node.GetChild("Cost").ToString());
        Assert.Equal("2", node.GetChild("Armor").ToString());
    }

    [Fact]
    public void ToFixed_ParsesValue()
    {
        var node = ParamNode.LoadXml("<Entity><Speed>3.5</Speed></Entity>");
        var speed = node.GetChild("Speed").ToFixed();
        Assert.Equal(3 << 16 | (1 << 15), speed.InternalValue);
    }

    [Fact]
    public void Clone_ProducesIndependentCopy()
    {
        var original = ParamNode.LoadXml("<Entity><Health>100</Health></Entity>");
        var clone = original.Clone();
        clone.MergeWithXml("<Entity><Health>200</Health></Entity>");

        Assert.Equal("100", original.GetChild("Health").ToString());
        Assert.Equal("200", clone.GetChild("Health").ToString());
    }

    [Fact]
    public void GetChild_ReturnsInvalidForMissing()
    {
        var node = ParamNode.LoadXml("<Entity><Health>100</Health></Entity>");
        Assert.False(node.GetChild("NonExistent").IsOk);
    }

    [Fact]
    public void ToInt_ParsesInteger()
    {
        var node = ParamNode.LoadXml("<Entity><Count>42</Count></Entity>");
        Assert.Equal(42, node.GetChild("Count").ToInt());
    }

    [Fact]
    public void ToBool_ParsesTrue()
    {
        var node = ParamNode.LoadXml("<Entity><Flag>true</Flag></Entity>");
        Assert.True(node.GetChild("Flag").ToBool());

        var node2 = ParamNode.LoadXml("<Entity><Flag>false</Flag></Entity>");
        Assert.False(node2.GetChild("Flag").ToBool());
    }

    [Fact]
    public void Merge_OpMul_AppliesArithmeticToInheritedBase()
    {
        var node = ParamNode.LoadXml("<Entity><Health><Max>50</Max></Health></Entity>");
        node.MergeWithXml("<Entity><Health><Max op='mul'>1.4</Max></Health></Entity>");

        // 50 × 1.4 = 70 (Fixed can't hold 1.4 exactly; nearest-representable rounds to 70).
        // Regression: previously op was ignored, the base was dropped, and ToInt("1.4") read 0.
        var max = node.GetChild("Health").GetChild("Max");
        Assert.InRange(max.ToInt(), 69, 70);
        Assert.False(max.HasChild("@op"), "op must be consumed, not stored as a child");
    }

    [Fact]
    public void ResolveTemplate_OpMulAgainstParentBase()
    {
        var templates = new Dictionary<string, string>
        {
            ["template_unit"] = "<Entity><Health><Max>50</Max></Health></Entity>",
            ["units/spart/support_civilian"] =
                "<Entity parent='template_unit'><Health><Max op='mul'>1.4</Max></Health></Entity>",
        };

        var node = ParamNode.ResolveTemplate("units/spart/support_civilian",
            name => System.Xml.Linq.XDocument.Parse(templates[name]));

        Assert.InRange(node.GetChild("Health").GetChild("Max").ToInt(), 69, 70);
    }

    [Fact]
    public void Merge_OpAddAndSub_AreArithmetic()
    {
        var node = ParamNode.LoadXml("<Entity><Health><Max>100</Max></Health></Entity>");
        node.MergeWithXml("<Entity><Health><Max op='add'>5</Max></Health></Entity>");
        Assert.Equal(105, node.GetChild("Health").GetChild("Max").ToInt());

        node.MergeWithXml("<Entity><Health><Max op='sub'>10</Max></Health></Entity>");
        Assert.Equal(95, node.GetChild("Health").GetChild("Max").ToInt());
    }

    [Fact]
    public void ToInt_RoundsFractionalInsteadOfZero()
    {
        var node = ParamNode.LoadXml("<Entity><Max>1.4</Max></Entity>");
        // Previously ToInt("1.4") returned 0 (int.TryParse failed); now rounds to nearest.
        Assert.Equal(1, node.GetChild("Max").ToInt());
    }
}
