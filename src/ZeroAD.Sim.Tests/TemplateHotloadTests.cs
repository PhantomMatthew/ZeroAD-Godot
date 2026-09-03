using System.Collections.Generic;
using System.IO;
using Xunit;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Content.Schema;

namespace ZeroAD.Sim.Tests;

/// <summary>模板 hotload 与 strict 拒载的内核侧契约测试。</summary>
public class TemplateHotloadTests
{
    private static (TemplateLoader Loader, string Dir) MakeLoader(string templateXml)
    {
        string dir = Path.Combine(Path.GetTempPath(), "zad_hotload_" + Path.GetRandomFileName());
        string units = Path.Combine(dir, "units");
        Directory.CreateDirectory(units);
        File.WriteAllText(Path.Combine(units, "foo.xml"), templateXml);
        return (new TemplateLoader(dir), dir);
    }

    private static void Write(string dir, string xml)
        => File.WriteAllText(Path.Combine(dir, "units", "foo.xml"), xml);

    [Fact]
    public void InvalidateReloadsChangedTemplate()
    {
        var (loader, dir) = MakeLoader(
            "<Entity><Health><Max>100</Max></Health></Entity>");
        Assert.Equal(100, loader.LoadTemplate("units/foo").GetChild("Health").GetChild("Max").ToInt());

        Write(dir, "<Entity><Health><Max>200</Max></Health></Entity>");
        // 未失效 → 缓存仍旧值
        Assert.Equal(100, loader.LoadTemplate("units/foo").GetChild("Health").GetChild("Max").ToInt());
        loader.Invalidate("units/foo");
        Assert.Equal(200, loader.LoadTemplate("units/foo").GetChild("Health").GetChild("Max").ToInt());
        Directory.Delete(dir, true);
    }

    [Fact]
    public void InvalidateAllReloadsEverything()
    {
        var (loader, dir) = MakeLoader("<Entity><Health><Max>1</Max></Health></Entity>");
        loader.LoadTemplate("units/foo");
        Write(dir, "<Entity><Health><Max>9</Max></Health></Entity>");
        loader.InvalidateAll();
        Assert.Equal(9, loader.LoadTemplate("units/foo").GetChild("Health").GetChild("Max").ToInt());
        Directory.Delete(dir, true);
    }

    [Fact]
    public void StrictValidationRefusesInvalidTemplate()
    {
        var (loader, dir) = MakeLoader(
            "<Entity><Health><Max>not-a-number</Max></Health></Entity>");
        var schema = TemplateSchema.FromFragments(new Dictionary<string, string>
        {
            ["Health"] = "<element name='Max'><ref name='nonNegativeDecimal'/></element>",
        });
        loader.EnableSchemaValidation(schema, strict: true);

        // strict:无效 → 空节点(上游 GetTemplate NULL 语义)
        var node = loader.LoadTemplate("units/foo");
        Assert.False(node.GetChild("Health").IsOk);

        // 修复文件 + 失效 → 重载通过(memo 也被 Invalidate 清掉)
        Write(dir, "<Entity><Health><Max>42</Max></Health></Entity>");
        loader.Invalidate("units/foo");
        node = loader.LoadTemplate("units/foo");
        Assert.Equal(42, node.GetChild("Health").GetChild("Max").ToInt());
        Directory.Delete(dir, true);
    }

    [Fact]
    public void NonStrictValidationKeepsTemplate()
    {
        var (loader, dir) = MakeLoader(
            "<Entity><Health><Max>not-a-number</Max></Health></Entity>");
        var schema = TemplateSchema.FromFragments(new Dictionary<string, string>
        {
            ["Health"] = "<element name='Max'><ref name='nonNegativeDecimal'/></element>",
        });
        loader.EnableSchemaValidation(schema, strict: false);
        var node = loader.LoadTemplate("units/foo");
        Assert.True(node.GetChild("Health").IsOk);   // 告警但保留
        Directory.Delete(dir, true);
    }

    [Fact]
    public void DatatypeAttributeSurvivesMerge()
    {
        // 合并树必须保留 @datatype="tokens"(上游 ParamNode 同款;
        // 14 个 schema 把它声明为必需属性)。
        var (loader, dir) = MakeLoader("""
            <Entity parent="template_base"><Identity>
              <Classes datatype="tokens">Extra</Classes>
            </Identity></Entity>
            """);
        File.WriteAllText(Path.Combine(dir, "template_base.xml"), """
            <Entity><Identity>
              <Classes datatype="tokens">Base</Classes>
            </Identity></Entity>
            """);
        var node = loader.LoadTemplate("units/foo");
        var classes = node.GetChild("Identity").GetChild("Classes");
        Assert.Equal("Base Extra", classes.ToString());
        Assert.True(classes.HasChild("@datatype"));
        Assert.Equal("tokens", classes.GetChild("@datatype").ToString());
        Directory.Delete(dir, true);
    }
}
