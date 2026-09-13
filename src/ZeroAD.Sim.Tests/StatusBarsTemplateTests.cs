using Xunit;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Templates;

namespace ZeroAD.Sim.Tests;

/// <summary>StatusBars 模板尺寸(原版 StatusBars.js Schema):表现层头顶条按这些
/// 世界单位拉 health_fg/bg.png 斜面,解析错会把建筑条画成单位细条。</summary>
public class StatusBarsTemplateTests
{
    private static TemplateLoader? TryLoadTemplates()
    {
        var templatesDir = RepoPaths.Resolve("binaries/data/mods/public/simulation/templates");
        return templatesDir == null ? null : new TemplateLoader(templatesDir);
    }

    [Fact]
    public void CivilCentre_UsesStructureBarSize()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var stats = templates.ExtractStats("structures/athen/civil_centre");
        Assert.Equal(6.0f, stats.BarWidth);
        Assert.Equal(0.6f, stats.BarHeight);
        Assert.Equal(12.0f, stats.HeightOffset);
    }

    [Fact]
    public void House_InheritsWidth_OverridesHeightOffset()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var stats = templates.ExtractStats("structures/athen/house");
        Assert.Equal(6.0f, stats.BarWidth);
        Assert.Equal(0.6f, stats.BarHeight);
        Assert.Equal(8.0f, stats.HeightOffset);
    }

    [Fact]
    public void Spearman_UsesUnitBarSize()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var stats = templates.ExtractStats("units/athen/infantry_spearman_b");
        Assert.Equal(2.0f, stats.BarWidth);
        Assert.Equal(0.333f, stats.BarHeight);
        Assert.Equal(5.0f, stats.HeightOffset);
    }

    [Fact]
    public void ExtractStatsFromNode_DefaultsToUnitSizeWhenMissing()
    {
        var stats = TemplateLoader.ExtractStatsFromNode(ParamNode.LoadXml(
            "<Entity><Health><Max>10</Max></Health></Entity>"));
        Assert.Equal(2.0f, stats.BarWidth);
        Assert.Equal(0.333f, stats.BarHeight);
        Assert.Equal(5.0f, stats.HeightOffset);
    }
}
