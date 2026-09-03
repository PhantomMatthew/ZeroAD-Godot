using Xunit;
using ZeroAD.Sim.Content.Schema;

namespace ZeroAD.Sim.Tests;

/// <summary>ComponentSchemaExtractor 单元测试:JS schema 表达式的受限求值。</summary>
public class ComponentSchemaExtractorTests
{
    [Fact]
    public void SimpleConcat()
    {
        var r = ComponentSchemaExtractor.Extract("Health", """
            function Health() {}
            Health.prototype.Schema =
                "<a:help>Deals with hitpoints.</a:help>" +
                "<element name='Max'>" +
                    "<ref name='nonNegativeDecimal'/>" +
                "</element>";
            """)!;
        Assert.Equal("Health", r.ComponentName);
        Assert.Equal("<a:help>Deals with hitpoints.</a:help><element name='Max'><ref name='nonNegativeDecimal'/></element>",
            r.Schema);
    }

    [Fact]
    public void NoSchemaReturnsNull()
    {
        Assert.Null(ComponentSchemaExtractor.Extract("X", "function X() {}\nX.prototype.Init = function() {};"));
    }

    [Fact]
    public void RegisteredNameWins()
    {
        var r = ComponentSchemaExtractor.Extract("MotionBall", """
            function MotionBall() {}
            MotionBall.prototype.Schema = "<ref name='anything'/>";
            Engine.RegisterComponentType(IID_MotionBall, "MotionBallScripted", MotionBall);
            """)!;
        Assert.Equal("MotionBallScripted", r.ComponentName);
    }

    [Fact]
    public void BacktickTemplateLiteral()
    {
        var r = ComponentSchemaExtractor.Extract("TerritoryDecay", """
            function TerritoryDecay() {}
            TerritoryDecay.prototype.Schema = `
                <element name='DecayRate'>
                    <choice><ref name='positiveDecimal'/><value>Infinity</value></choice>
                </element>
                `;
            """)!;
        Assert.Contains("DecayRate", r.Schema);
    }

    [Fact]
    public void CommentsSkipped()
    {
        var r = ComponentSchemaExtractor.Extract("Sound", """
            Sound.prototype.Schema =
                "<element name='SoundGroups'>" + /* TODO: make this more specific */
                    "<text/>" + // trailing comment
                "</element>";
            """)!;
        Assert.Equal("<element name='SoundGroups'><text/></element>", r.Schema);
    }

    [Fact]
    public void EscapedQuotesInStrings()
    {
        var r = ComponentSchemaExtractor.Extract("Builder", """
            Builder.prototype.Schema =
                "<element name='Entities' a:help='The special string \"{civ}\" will be replaced.'>" +
                    "<text/>" +
                "</element>";
            """)!;
        Assert.Contains("\"{civ}\"", r.Schema);   // JS \" 转义 → 字面引号
    }

    [Fact]
    public void ResourcesBuildSchemaCall()
    {
        var r = ComponentSchemaExtractor.Extract("Cost", """
            Cost.prototype.Schema =
                "<element name='Resources'>" +
                    Resources.BuildSchema("nonNegativeInteger") +
                "</element>";
            """)!;
        Assert.Contains("<element name='food'>", r.Schema);
        Assert.Contains("<data type='nonNegativeInteger'/>", r.Schema);
        Assert.Contains("<element name='metal'>", r.Schema);
    }

    [Fact]
    public void UnknownIdentifierThrows()
    {
        Assert.Throws<ComponentSchemaExtractor.ExtractException>(() =>
            ComponentSchemaExtractor.Extract("Loot", """
                Loot.prototype.Schema =
                    Resources.BuildSchema("nonNegativeInteger", ["xp"]) +
                    SomethingUndefined;
                """));
    }

    [Fact]
    public void ResourcesBuildSchemaAdditionalArray()
    {
        var r = ComponentSchemaExtractor.Extract("Loot", """
            Loot.prototype.Schema = Resources.BuildSchema("nonNegativeInteger", ["xp"]);
            """)!;
        Assert.Contains("<element name='xp'>", r.Schema);
        Assert.StartsWith("<interleave>", r.Schema);
    }

    [Fact]
    public void ResourcesBuildSchemaSubtypes()
    {
        var r = ComponentSchemaExtractor.Extract("ResourceGatherer", """
            ResourceGatherer.prototype.Schema = Resources.BuildSchema("positiveDecimal", [], true);
            """)!;
        Assert.Contains("<element name='food.fish'>", r.Schema);
        Assert.Contains("<element name='metal.ruins'>", r.Schema);
    }

    [Fact]
    public void ResourcesBuildChoicesSchemaCall()
    {
        var r = ComponentSchemaExtractor.Extract("ResourceSupply", """
            ResourceSupply.prototype.Schema = Resources.BuildChoicesSchema(true);
            """)!;
        Assert.Equal("<choice><value>food.fish</value><value>food.fruit</value>" +
            "<value>food.grain</value><value>food.meat</value><value>wood.tree</value>" +
            "<value>wood.ruins</value><value>stone.rock</value><value>stone.ruins</value>" +
            "<value>metal.ore</value><value>metal.ruins</value></choice>", r.Schema);
    }

    [Fact]
    public void RequirementsHelperCall()
    {
        var r = ComponentSchemaExtractor.Extract("Identity", """
            Identity.prototype.Schema = RequirementsHelper.BuildSchema();
            """)!;
        Assert.Contains("<element name='Requirements'", r.Schema);
        Assert.Contains("<element name='All'", r.Schema);
        Assert.Contains("<element name='Techs'", r.Schema);
    }

    [Fact]
    public void AttackHelperCall()
    {
        var r = ComponentSchemaExtractor.Extract("DeathDamage", """
            DeathDamage.prototype.Schema = AttackHelper.BuildAttackEffectsSchema();
            """)!;
        Assert.Contains("<element name='Damage'>", r.Schema);
        Assert.Contains("<element name='ApplyStatus'", r.Schema);
        Assert.Contains("<element name='Bonuses'>", r.Schema);
    }

    [Fact]
    public void SameFileMethodReference()
    {
        var r = ComponentSchemaExtractor.Extract("Resistance", """
            Resistance.prototype.BuildResistanceSchema = function()
            {
                return "" +
                    "<oneOrMore>" +
                        "<element name='Damage'><text/></element>" +
                    "</oneOrMore>";
            };
            Resistance.prototype.Schema =
                "<zeroOrMore>" +
                    "<element name='Entity'>" +
                        Resistance.prototype.BuildResistanceSchema() +
                    "</element>" +
                "</zeroOrMore>";
            """)!;
        Assert.Equal(
            "<zeroOrMore><element name='Entity'><oneOrMore><element name='Damage'><text/></element></oneOrMore></element></zeroOrMore>",
            r.Schema);
    }

    [Fact]
    public void SameFilePropertyReference()
    {
        var r = ComponentSchemaExtractor.Extract("Attack", """
            Attack.prototype.preferredClassesSchema =
                "<optional>" +
                    "<element name='PreferredClasses'><text/></element>" +
                "</optional>";
            Attack.prototype.Schema =
                "<oneOrMore>" +
                    "<element><anyName/>" +
                        Attack.prototype.preferredClassesSchema +
                    "</element>" +
                "</oneOrMore>";
            """)!;
        Assert.Contains("PreferredClasses", r.Schema);
    }
}
