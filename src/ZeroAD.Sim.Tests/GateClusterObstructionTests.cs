using System;
using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;

namespace ZeroAD.Sim.Tests;

/// <summary>城墙门 Left/Right/Door 分形(原版 CCmpObstruction CLUSTER + Gate.js shape 0):
/// 外壳不挡走/寻路;两翼始终挡;开门只放行 Door。</summary>
public sealed class GateClusterObstructionTests
{
    private static TemplateLoader? TryLoadTemplates()
    {
        var dir = RepoPaths.Resolve("binaries/data/mods/public/simulation/templates");
        return dir == null ? null : new TemplateLoader(dir);
    }

    private static ObstructionShapeFilter MovementFilter() =>
        (_, flags, _, _) => (flags & ObstructionFlags.BlockMovement) == 0;

    private static ObstructionShapeFilter PathfindingFilter() =>
        (_, flags, _, _) => (flags & ObstructionFlags.BlockPathfinding) == 0;

    [Fact]
    public void AthenWallGate_Template_HasDoorLeftRightCluster()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var stats = templates.ExtractStats("structures/athen/wall_gate");
        Assert.True(stats.HasGate);
        Assert.Equal(3, stats.ObstructionSubShapes.Count);
        Assert.Equal("Door", stats.ObstructionSubShapes[0].Name);
        Assert.Contains(stats.ObstructionSubShapes, s => s.Name == "Left");
        Assert.Contains(stats.ObstructionSubShapes, s => s.Name == "Right");
        Assert.True(stats.ObstructionSize0.ToFloat() > 20f,
            "cluster hull width must come from subshape bounds, not the 6m parent wall Static");
        Assert.True((stats.ObstructionFlags & ObstructionFlags.BlockMovement) != 0);
    }

    [Fact]
    public void ClosedGate_DoorAndWingsBlockMovement_HullDoesNot()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var mgr = new ObstructionManager();
        SimSystem.SetObstructionManager(mgr);

        var gate = AddClusterGate(cm, 50f, 50f);
        var obs = cm.QueryInterface<ObstructionComponent>(gate)!;
        obs.SetDisableBlockMovementPathfinding(false, true);   // 未锁关门

        // 门洞中心:Door 挡移动。
        Assert.NotEmpty(mgr.TestUnitShape(MovementFilter(),
            Fixed.FromInt(50), Fixed.FromInt(50), Fixed.FromInt(1)));
        // 未锁关门:Door 不挡寻路(原版 UnlockGate)。
        Assert.Empty(mgr.TestUnitShape(PathfindingFilter(),
            Fixed.FromInt(50), Fixed.FromInt(50), Fixed.FromInt(1)));
        // 左翼仍挡移动和寻路。
        Assert.NotEmpty(mgr.TestUnitShape(MovementFilter(),
            Fixed.FromFloat(37.25f), Fixed.FromInt(50), Fixed.FromInt(1)));
        Assert.NotEmpty(mgr.TestUnitShape(PathfindingFilter(),
            Fixed.FromFloat(37.25f), Fixed.FromInt(50), Fixed.FromInt(1)));
        // 门外空地。
        Assert.Empty(mgr.TestUnitShape(MovementFilter(),
            Fixed.FromInt(50), Fixed.FromInt(80), Fixed.FromInt(1)));
    }

    [Fact]
    public void OpenGate_ReleasesDoorOnly_WingsStayBlocked()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var mgr = new ObstructionManager();
        SimSystem.SetObstructionManager(mgr);

        var gate = AddClusterGate(cm, 50f, 50f);
        var g = cm.QueryInterface<GateComponent>(gate)!;
        g.OpenGate(cm);

        Assert.Empty(mgr.TestUnitShape(MovementFilter(),
            Fixed.FromInt(50), Fixed.FromInt(50), Fixed.FromInt(1)));
        Assert.Empty(mgr.TestUnitShape(PathfindingFilter(),
            Fixed.FromInt(50), Fixed.FromInt(50), Fixed.FromInt(1)));
        Assert.NotEmpty(mgr.TestUnitShape(MovementFilter(),
            Fixed.FromFloat(37.25f), Fixed.FromInt(50), Fixed.FromInt(1)));
        Assert.NotEmpty(mgr.TestUnitShape(PathfindingFilter(),
            Fixed.FromFloat(62.75f), Fixed.FromInt(50), Fixed.FromInt(1)));
    }

    [Fact]
    public void SpawnedAthenGate_WingsBlock_OpenDoorDoesNot()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var cm = new ComponentManager(42, templates: templates);
        SimSystem.Init(cm);
        var mgr = new ObstructionManager(128, 4f);
        SimSystem.SetObstructionManager(mgr);

        var gate = cm.SpawnEntity("structures/athen/wall_gate", 64f, 64f, ownerPlayerId: 1);
        var obs = cm.QueryInterface<ObstructionComponent>(gate);
        Assert.NotNull(obs);
        Assert.Equal(3, obs!.SubShapes.Count);
        Assert.Equal("Door", obs.SubShapes[0].Name);

        // 生成路径已 UnlockGate:门洞不挡寻路,左翼挡。
        Assert.Empty(mgr.TestUnitShape(PathfindingFilter(),
            Fixed.FromInt(64), Fixed.FromInt(64), Fixed.FromInt(1)));
        Assert.NotEmpty(mgr.TestUnitShape(PathfindingFilter(),
            Fixed.FromFloat(51.25f), Fixed.FromInt(64), Fixed.FromInt(1)));

        cm.QueryInterface<GateComponent>(gate)!.OpenGate(cm);
        Assert.Empty(mgr.TestUnitShape(MovementFilter(),
            Fixed.FromInt(64), Fixed.FromInt(64), Fixed.FromInt(1)));
        Assert.NotEmpty(mgr.TestUnitShape(MovementFilter(),
            Fixed.FromFloat(51.25f), Fixed.FromInt(64), Fixed.FromInt(1)));
    }

    [Fact]
    public void OpenGate_DefaultClass_PassableAtDoor_ImpassableAtWing()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        const int tiles = 32;
        var cm = new ComponentManager(42, templates: templates);
        SimSystem.Init(cm);
        SimSystem.SetObstructionManager(new ObstructionManager(tiles * 4, 4f));
        var terrain = new TerrainComponent();
        terrain.Configure(tiles, 4f);
        var grid = new TerrainClass[tiles, tiles];
        for (int i = 0; i < tiles; i++)
            for (int j = 0; j < tiles; j++)
                grid[i, j] = TerrainClass.Land;
        terrain.SetPassabilityGrid(grid);
        var pf = new PathfinderComponent(cm);
        pf.SetTerrain(terrain);
        SimSystem.SetPathfinder(pf);

        var gate = cm.SpawnEntity("structures/athen/wall_gate", 64f, 64f, ownerPlayerId: 1);
        cm.QueryInterface<GateComponent>(gate)!.OpenGate(cm);
        pf.RebuildGrid();
        var nav = pf.PassabilityGrid;
        Assert.NotNull(nav);
        int doorI = PathfindingCore.WorldToNavcell(Fixed.FromInt(64));
        int doorJ = PathfindingCore.WorldToNavcell(Fixed.FromInt(64));
        Assert.True(PathfindingCore.IsPassable(nav!.Get(doorI, doorJ), pf.DefaultClass.Mask),
            "open door must be walkable");
        int wingI = PathfindingCore.WorldToNavcell(Fixed.FromFloat(51.25f));
        Assert.False(PathfindingCore.IsPassable(nav.Get(wingI, doorJ), pf.DefaultClass.Mask),
            "gate wing must still block pathfinding");
    }

    /// <summary>athen 门:Left/Right x=±12.75 w=10.5,Door w=15 居中。中心 (cx,cz)。</summary>
    private static EntityId AddClusterGate(ComponentManager cm, float cx, float cz)
    {
        var gate = cm.CreateEntity();
        cm.AddComponent(gate, new PositionComponent());
        cm.QueryInterface<PositionComponent>(gate)!.Position =
            new FixedVector3D(Fixed.FromFloat(cx), Fixed.Zero, Fixed.FromFloat(cz));
        cm.AddComponent(gate, new OwnershipComponent { PlayerId = 1 });
        cm.AddComponent(gate, new GateComponent());
        var obs = new ObstructionComponent
        {
            Type = ObstructionType.Static,
            Size0 = Fixed.FromFloat(36f),
            Size1 = Fixed.FromFloat(6.5f),
            Flags = ObstructionFlags.DefaultBlock,
        };
        obs.SubShapes.Add(("Door", Fixed.Zero, Fixed.Zero, Fixed.FromFloat(15f), Fixed.FromFloat(6.5f)));
        obs.SubShapes.Add(("Left", Fixed.FromFloat(-12.75f), Fixed.Zero, Fixed.FromFloat(10.5f), Fixed.FromFloat(6.5f)));
        obs.SubShapes.Add(("Right", Fixed.FromFloat(12.75f), Fixed.Zero, Fixed.FromFloat(10.5f), Fixed.FromFloat(6.5f)));
        cm.AddComponent(gate, obs);
        obs.EnsureRegistered();
        return gate;
    }
}
