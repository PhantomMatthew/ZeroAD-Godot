using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>静态 gaia 资源(树/浆果)必须挡住地基——原版 template_gaia BlockFoundation
/// + tree/fruit Static。此前 AssembleGaia 漏挂 Obstruction,可在树丛上盖房。</summary>
public sealed class GaiaObstructionPlacementTests
{
    private const int Tiles = 32;
    private const float TileSize = 4f;

    private static TemplateLoader? TryLoadTemplates()
    {
        var templatesDir = RepoPaths.Resolve("binaries/data/mods/public/simulation/templates");
        return templatesDir == null ? null : new TemplateLoader(templatesDir);
    }

    private static (ComponentManager Cm, PathfinderComponent Pf) SetupLandWorld(TemplateLoader templates)
    {
        var cm = new ComponentManager(42, templates: templates);
        SimSystem.Init(cm);
        SimSystem.SetObstructionManager(new ObstructionManager(Tiles * (int)TileSize, TileSize));

        var terrain = new TerrainComponent();
        terrain.Configure(Tiles, TileSize);
        var grid = new TerrainClass[Tiles, Tiles];
        for (int i = 0; i < Tiles; i++)
            for (int j = 0; j < Tiles; j++)
                grid[i, j] = TerrainClass.Land;
        terrain.SetPassabilityGrid(grid);

        var pf = new PathfinderComponent(cm);
        pf.SetTerrain(terrain);
        pf.RebuildGrid();
        SimSystem.SetPathfinder(pf);
        return (cm, pf);
    }

    [Fact]
    public void Oak_RegistersStaticBlockFoundation()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;
        if (!templates.TemplateExists("gaia/tree/oak")) return;

        var (cm, _) = SetupLandWorld(templates);
        var eid = cm.SpawnEntity("gaia/tree/oak", 64f, 64f);
        var obs = cm.QueryInterface<ObstructionComponent>(eid);
        Assert.NotNull(obs);
        Assert.Equal(ObstructionType.Static, obs!.Type);
        Assert.True((obs.Flags & ObstructionFlags.BlockFoundation) != 0);
        Assert.True((obs.Flags & ObstructionFlags.DeleteUponConstruction) != 0);
    }

    [Fact]
    public void Oak_BlocksBuildingPlacement()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;
        if (!templates.TemplateExists("gaia/tree/oak")) return;

        var (cm, pf) = SetupLandWorld(templates);
        cm.SpawnEntity("gaia/tree/oak", 64f, 64f);
        var pr = pf.CheckBuildingPlacement(
            Fixed.FromInt(64), Fixed.FromInt(64),
            Fixed.FromInt(3), Fixed.FromInt(3));
        Assert.Equal(PlacementResult.FailObstructsFoundation, pr);
    }

    [Fact]
    public void Berry_BlocksBuildingPlacement()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;
        if (!templates.TemplateExists("gaia/fruit/berry_01")) return;

        var (cm, pf) = SetupLandWorld(templates);
        cm.SpawnEntity("gaia/fruit/berry_01", 64f, 64f);
        var pr = pf.CheckBuildingPlacement(
            Fixed.FromInt(64), Fixed.FromInt(64),
            Fixed.FromInt(3), Fixed.FromInt(3));
        Assert.Equal(PlacementResult.FailObstructsFoundation, pr);
    }

    [Fact]
    public void NearbyClearLand_StillPlaceable()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;
        if (!templates.TemplateExists("gaia/tree/oak")) return;

        var (cm, pf) = SetupLandWorld(templates);
        cm.SpawnEntity("gaia/tree/oak", 64f, 64f);
        var pr = pf.CheckBuildingPlacement(
            Fixed.FromInt(90), Fixed.FromInt(90),
            Fixed.FromInt(3), Fixed.FromInt(3));
        Assert.Equal(PlacementResult.Success, pr);
    }
}
