using Xunit;
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim.Tests;

/// <summary>Selectable/Overlay 类型:单位/gaia 是 Texture 椭圆圈,建筑是 Outline 描边。</summary>
public class SelectableOverlayTests
{
    private static TemplateLoader? TryLoadTemplates()
    {
        var templatesDir = RepoPaths.Resolve("binaries/data/mods/public/simulation/templates");
        return templatesDir == null ? null : new TemplateLoader(templatesDir);
    }

    [Fact]
    public void Spearman_UsesTextureEllipseOverlay()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;
        var stats = templates.ExtractStats("units/athen/infantry_spearman_b");
        Assert.True(stats.SelectableOverlayTexture);
        Assert.Contains("128x128/ellipse", stats.SelectableMainTexture, System.StringComparison.Ordinal);
        Assert.Equal("circle", stats.FootprintShape);
        Assert.Equal(1.5f, stats.FootprintSize0.ToFloat(), 2);
    }

    [Fact]
    public void House_IsSquareOutlineNotTexture()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;
        var stats = templates.ExtractStats("structures/athen/house");
        Assert.False(stats.SelectableOverlayTexture);
        Assert.Equal("square", stats.FootprintShape);
    }

    [Fact]
    public void BritCivilCentre_IsCircularOutlineNotTexture()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;
        if (!templates.TemplateExists("structures/brit/civil_centre")) return;
        var stats = templates.ExtractStats("structures/brit/civil_centre");
        Assert.False(stats.SelectableOverlayTexture);
        Assert.Equal("circle", stats.FootprintShape);
        Assert.Equal(15f, stats.FootprintSize0.ToFloat(), 2);
    }

    [Fact]
    public void GaiaTree_UsesTextureEllipseSizedToRadius()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;
        string name = templates.TemplateExists("gaia/tree/acacia") ? "gaia/tree/acacia" : "template_gaia_tree";
        if (!templates.TemplateExists(name)) return;
        var stats = templates.ExtractStats(name);
        Assert.True(stats.SelectableOverlayTexture);
        Assert.Equal("circle", stats.FootprintShape);
        Assert.Equal(2.5f, stats.FootprintSize0.ToFloat(), 2);
    }

    [Fact]
    public void Fauna_UsesRectangularTextureOverlay()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;
        if (!templates.TemplateExists("gaia/fauna_deer")) return;
        var stats = templates.ExtractStats("gaia/fauna_deer");
        Assert.True(stats.SelectableOverlayTexture);
        Assert.Contains("128x256/ellipse", stats.SelectableMainTexture, System.StringComparison.Ordinal);
        Assert.Equal("square", stats.FootprintShape);
        Assert.Equal(2f, stats.FootprintSize0.ToFloat(), 2);
        Assert.Equal(4f, stats.FootprintSize1.ToFloat(), 2);
    }
}
