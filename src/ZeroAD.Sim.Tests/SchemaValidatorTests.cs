using System.Collections.Generic;
using System.Xml.Linq;
using Xunit;
using ZeroAD.Sim.Content.Schema;

namespace ZeroAD.Sim.Tests;

/// <summary>RngValidator 单元测试:mini grammar 直构,覆盖语料全部构造。</summary>
public class RngValidatorTests
{
    private static RngValidator ValidatorFor(string componentFragment, string componentName = "Comp")
    {
        var schema = TemplateSchema.FromFragments(new Dictionary<string, string>
        {
            [componentName] = componentFragment,
        });
        return new RngValidator(schema.Grammar);
    }

    private static XmlInstanceNode Parse(string xml)
        => XmlInstanceNode.FromXElement(XElement.Parse(xml));

    private static List<string> Run(string fragment, string instanceXml, string name = "Comp")
        => ValidatorFor(fragment, name).Validate(Parse(instanceXml));

    [Fact]
    public void ValidSimpleElementPasses()
    {
        var errors = Run(
            "<element name='Max'><ref name='nonNegativeDecimal'/></element>",
            "<Entity><Comp><Max>100</Max></Comp></Entity>");
        Assert.Empty(errors);
    }

    [Fact]
    public void MissingRequiredElementFails()
    {
        var errors = Run(
            "<element name='Max'><text/></element>",
            "<Entity><Comp/></Entity>");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void UnknownComponentElementFails()
    {
        var errors = Run("<empty/>", "<Entity><Nope/></Entity>");
        Assert.NotEmpty(errors);
        Assert.Contains("not allowed", errors[0]);
    }

    [Fact]
    public void UnknownChildElementFails()
    {
        var errors = Run("<element name='Max'><text/></element>",
            "<Entity><Comp><Max>1</Max><Extra>x</Extra></Comp></Entity>");
        Assert.NotEmpty(errors);
        Assert.Contains("Extra", errors[^1]);
    }

    [Fact]
    public void BadDecimalFails()
    {
        var errors = Run("<element name='Max'><ref name='nonNegativeDecimal'/></element>",
            "<Entity><Comp><Max>abc</Max></Comp></Entity>");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void NegativeNonNegativeDecimalFails()
    {
        var errors = Run("<element name='Max'><ref name='nonNegativeDecimal'/></element>",
            "<Entity><Comp><Max>-5</Max></Comp></Entity>");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void OptionalElementMayBeAbsent()
    {
        var errors = Run(
            "<element name='Max'><text/></element><optional><element name='Regen'><text/></element></optional>",
            "<Entity><Comp><Max>1</Max></Comp></Entity>");
        Assert.Empty(errors);
    }

    [Fact]
    public void InterleaveAcceptsAnyOrder()
    {
        var errors = Run(
            "<element name='A'><text/></element><element name='B'><text/></element>",
            "<Entity><Comp><B>2</B><A>1</A></Comp></Entity>");
        Assert.Empty(errors);
    }

    [Fact]
    public void ChoiceOfValues()
    {
        var frag = "<element name='DeathType'><choice>" +
            "<value>vanish</value><value>corpse</value><value>remain</value></choice></element>";
        Assert.Empty(Run(frag, "<Entity><Comp><DeathType>corpse</DeathType></Comp></Entity>"));
        Assert.NotEmpty(Run(frag, "<Entity><Comp><DeathType>explode</DeathType></Comp></Entity>"));
    }

    [Fact]
    public void OneOrMoreWildcardElements()
    {
        var frag = "<element name='Damage'><oneOrMore><element><anyName/>" +
            "<ref name='nonNegativeDecimal'/></element></oneOrMore></element>";
        Assert.Empty(Run(frag,
            "<Entity><Comp><Damage><Hack>10</Hack><Pierce>0</Pierce></Damage></Comp></Entity>"));
        // 空 Damage 不行(oneOrMore)
        Assert.NotEmpty(Run(frag, "<Entity><Comp><Damage/></Comp></Entity>"));
    }

    [Fact]
    public void RequiredAttributeEnforced()
    {
        var frag = "<element name='Entities'>" +
            "<attribute name='datatype'><value>tokens</value></attribute><text/></element>";
        Assert.Empty(Run(frag,
            "<Entity><Comp><Entities datatype=\"tokens\">a b</Entities></Comp></Entity>"));
        var errors = Run(frag, "<Entity><Comp><Entities>a b</Entities></Comp></Entity>");
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("datatype"));
    }

    [Fact]
    public void WrongAttributeValueFails()
    {
        var frag = "<element name='Entities'>" +
            "<attribute name='datatype'><value>tokens</value></attribute><text/></element>";
        var errors = Run(frag,
            "<Entity><Comp><Entities datatype=\"other\">a b</Entities></Comp></Entity>");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void UnexpectedAttributeFails()
    {
        var errors = Run("<element name='Max'><text/></element>",
            "<Entity><Comp><Max bonus=\"x\">1</Max></Comp></Entity>");
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("bonus"));
    }

    [Fact]
    public void RootParentAttributeAllowed()
    {
        var errors = Run("<empty/>",
            "<Entity parent=\"template_unit\"><Comp/></Entity>");
        Assert.Empty(errors);
    }

    [Fact]
    public void RootOtherAttributeRejected()
    {
        var errors = Run("<empty/>", "<Entity rogue=\"x\"><Comp/></Entity>");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void BooleanDataType()
    {
        var frag = "<element name='Active'><data type='boolean'/></element>";
        Assert.Empty(Run(frag, "<Entity><Comp><Active>true</Active></Comp></Entity>"));
        Assert.Empty(Run(frag, "<Entity><Comp><Active>0</Active></Comp></Entity>"));
        Assert.NotEmpty(Run(frag, "<Entity><Comp><Active>yes</Active></Comp></Entity>"));
    }

    [Fact]
    public void DataParamsEnforced()
    {
        var frag = "<element name='Ratio'><data type='decimal'>" +
            "<param name='minInclusive'>0</param><param name='maxInclusive'>1</param>" +
            "</data></element>";
        Assert.Empty(Run(frag, "<Entity><Comp><Ratio>0.5</Ratio></Comp></Entity>"));
        Assert.NotEmpty(Run(frag, "<Entity><Comp><Ratio>1.5</Ratio></Comp></Entity>"));
    }

    [Fact]
    public void EmptyPatternRejectsText()
    {
        var errors = Run("<element name='Flag'><empty/></element>",
            "<Entity><Comp><Flag>x</Flag></Comp></Entity>");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ListOfTokens()
    {
        var frag = "<element name='Territory'><list><oneOrMore><choice>" +
            "<value>neutral</value><value>enemy</value></choice></oneOrMore></list></element>";
        Assert.Empty(Run(frag, "<Entity><Comp><Territory>neutral enemy</Territory></Comp></Entity>"));
        Assert.NotEmpty(Run(frag, "<Entity><Comp><Territory>ally</Territory></Comp></Entity>"));
    }

    [Fact]
    public void AnnotationsAreIgnored()
    {
        var frag = "<a:help>doc</a:help><a:example><Bogus><Nested>1</Nested></Bogus></a:example>" +
            "<element name='Max' a:help='hp'><text/></element>";
        Assert.Empty(Run(frag, "<Entity><Comp><Max>1</Max></Comp></Entity>"));
    }

    [Fact]
    public void AnythingDefineAcceptsArbitrary()
    {
        var errors = Run("<ref name='anything'/>",
            "<Entity><Comp foo=\"1\"><A><B x=\"y\">text</B></A>tail</Comp></Entity>");
        Assert.Empty(errors);
    }

    [Fact]
    public void ZeroOrMoreRepeated()
    {
        var frag = "<zeroOrMore><element name='Item'><text/></element></zeroOrMore>";
        Assert.Empty(Run(frag, "<Entity><Comp><Item>a</Item><Item>b</Item></Comp></Entity>"));
        Assert.Empty(Run(frag, "<Entity><Comp/></Entity>"));
    }

    [Fact]
    public void MultipleComponentsInAnyOrder()
    {
        var schema = TemplateSchema.FromFragments(new Dictionary<string, string>
        {
            ["Health"] = "<element name='Max'><text/></element>",
            ["Identity"] = "<element name='Civ'><text/></element>",
        });
        var v = new RngValidator(schema.Grammar);
        Assert.Empty(v.Validate(Parse(
            "<Entity><Identity><Civ>athen</Civ></Identity><Health><Max>1</Max></Health></Entity>")));
        Assert.Empty(v.Validate(Parse(
            "<Entity><Health><Max>1</Max></Health><Identity><Civ>athen</Civ></Identity></Entity>")));
    }
}
