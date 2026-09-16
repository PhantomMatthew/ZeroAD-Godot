using System;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 农田可穿行(原版 template_structure_resource_field:BlockMovement/Pathfinding=false,
/// BlockFoundation=true)。已建成农田不挡单位寻路,仍挡新地基。
/// </summary>
public sealed class FieldPassabilityTests
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
    public void FieldTemplate_DoesNotBlockMovementOrPathfinding()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var field = templates.ExtractStats("structures/athen/field");
        Assert.False((field.ObstructionFlags & ObstructionFlags.BlockMovement) != 0,
            "field must not block movement");
        Assert.False((field.ObstructionFlags & ObstructionFlags.BlockPathfinding) != 0,
            "field must not block pathfinding");
        Assert.True((field.ObstructionFlags & ObstructionFlags.BlockFoundation) != 0,
            "field must still block foundations");

        var house = templates.ExtractStats("structures/athen/house");
        Assert.True((house.ObstructionFlags & ObstructionFlags.BlockMovement) != 0);
        Assert.True((house.ObstructionFlags & ObstructionFlags.BlockPathfinding) != 0);
    }

    [Fact]
    public void FieldCenter_IsPassableForDefaultClass_ImpassableForBuildingLand()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var (cm, pf) = SetupLandWorld(templates);
        var field = cm.SpawnEntity("structures/athen/field", 64f, 64f, ownerPlayerId: 1);
        var obs = cm.QueryInterface<ObstructionComponent>(field);
        Assert.NotNull(obs);
        Assert.False((obs!.EffectiveFlags() & ObstructionFlags.BlockMovement) != 0);
        Assert.False((obs.EffectiveFlags() & ObstructionFlags.BlockPathfinding) != 0);

        pf.RebuildGrid();
        var grid = pf.PassabilityGrid;
        Assert.NotNull(grid);
        int ni = PathfindingCore.WorldToNavcell(Fixed.FromInt(64));
        int nj = PathfindingCore.WorldToNavcell(Fixed.FromInt(64));
        Assert.True(PathfindingCore.IsPassable(grid!.Get(ni, nj), pf.DefaultClass.Mask),
            "default class must walk through the field");
        var buildingLand = pf.GetClassByName("building-land");
        Assert.NotNull(buildingLand);
        Assert.False(PathfindingCore.IsPassable(grid.Get(ni, nj), buildingLand!.Mask),
            "building-land must still treat the field as occupied");
    }

    [Fact]
    public void Unit_WalksStraightThroughCompletedField()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var (cm, pf) = SetupLandWorld(templates);
        cm.SpawnEntity("structures/athen/field", 64f, 64f, ownerPlayerId: 1);
        pf.RebuildGrid();

        var walker = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(walker, pos);
        pos.Position = new FixedVector3D(Fixed.FromFloat(40f), Fixed.Zero, Fixed.FromFloat(64f));
        var motion = new UnitMotion();
        cm.AddComponent(walker, motion);
        motion.Speed = Fixed.FromInt(8);
        motion.PassClassName = "default";
        motion.MoveToPoint(new FixedVector2D(Fixed.FromFloat(88f), Fixed.FromFloat(64f)));

        bool crossedInterior = false;
        for (int i = 0; i < 400 && motion.HasMoveTarget; i++)
        {
            motion.Tick(0.1f);
            var p = cm.QueryInterface<PositionComponent>(walker)!.Position;
            float x = p.X.ToFloat();
            float z = p.Z.ToFloat();
            if (Math.Abs(x - 64f) < 3f && Math.Abs(z - 64f) < 4f)
                crossedInterior = true;
            Assert.True(Math.Abs(z - 64f) < 8f,
                $"unit detoured around the field: x={x:F1} z={z:F1}");
        }

        float fx = cm.QueryInterface<PositionComponent>(walker)!.Position.X.ToFloat();
        Assert.True(crossedInterior, "unit never entered the field footprint");
        Assert.True(fx > 75f, $"unit did not reach the far side of the field: x={fx:F1}");
    }

    [Fact]
    public void ShortPath_DoesNotDetourAroundField()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var (cm, pf) = SetupLandWorld(templates);
        cm.SpawnEntity("structures/athen/field", 64f, 64f, ownerPlayerId: 1);
        pf.RebuildGrid();

        var path = pf.ComputeShortPath(
            new FixedVector2D(Fixed.FromFloat(40f), Fixed.FromFloat(64f)),
            PathGoal.Point(Fixed.FromFloat(88f), Fixed.FromFloat(64f)),
            Fixed.FromFraction(4, 5),
            Fixed.FromInt(50),
            pf.DefaultClass.Mask);
        Assert.False(path.IsEmpty);
        foreach (var wp in path.Waypoints)
            Assert.True(Math.Abs(wp.Z.ToFloat() - 64f) < 1f,
                $"short path detoured around the field: ({wp.X.ToFloat():F1},{wp.Z.ToFloat():F1})");
    }

    [Fact]
    public void House_BlocksDefaultClass_Contrast()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var (cm, pf) = SetupLandWorld(templates);
        cm.SpawnEntity("structures/athen/house", 64f, 64f, ownerPlayerId: 1);
        pf.RebuildGrid();
        var grid = pf.PassabilityGrid;
        Assert.NotNull(grid);
        int ni = PathfindingCore.WorldToNavcell(Fixed.FromInt(64));
        int nj = PathfindingCore.WorldToNavcell(Fixed.FromInt(64));
        Assert.False(PathfindingCore.IsPassable(grid!.Get(ni, nj), pf.DefaultClass.Mask),
            "house must still block land pathfinding");
    }
}
