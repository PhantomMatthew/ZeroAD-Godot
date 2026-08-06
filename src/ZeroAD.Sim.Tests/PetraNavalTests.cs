using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using ZeroAD.Sim.AI;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.AI.Petra;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Net;
using ZeroAD.Sim.Pathfinding;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// NavalManager 启用:海图判定 → 岸线选点建码头 → 码头建成后训船。
/// junction 数据(模板)缺失时按惯例跳过。
/// </summary>
public sealed class PetraNavalTests
{
    private static string? FindRepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, relative);
    }

    // 16×16 navcell 微型海图:左 6 列陆,中 6 列水,右 4 列陆(cellSize=1m)。
    private static Accessibility BuildTestAccessibility()
    {
        const ushort land = 0x2, water = 0x1;   // 陆=水阻;水=陆阻(掩码 陆0x1/水0x2)
        var grid = new Grid<NavcellData>(16, 16);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                grid.Set(x, y, new NavcellData(x < 6 ? land : x < 12 ? water : land));
        return new Accessibility(grid, new PassClass(0x1), new PassClass(0x2), 16, 1);
    }

    private sealed class NavalWorld
    {
        public required ComponentManager Cm;
        public required NetTurnManager Net;
        public required GameState Gs;
        public required QueueManager Queues;
        public required EntityId Cc;
    }

    private static NavalWorld? NewNavalWorld(bool withDock)
    {
        var templatesRoot = FindRepoPath("binaries/data/mods/public/simulation/templates");
        var techRoot = FindRepoPath("binaries/data/mods/public/simulation/data/technologies");
        if (templatesRoot == null || techRoot == null) return null;

        var templates = new TemplateLoader(templatesRoot);
        templates.LoadAllTemplates();
        var techCatalog = TechnologyLoader.LoadAll(techRoot);

        var cm = new ComponentManager(rngSeed: 42, templates: templates);
        SimSystem.Init(cm);
        var events = new AIEventBuffer();
        events.Attach(cm);

        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent { Civ = "gaul" });
        cm.AddComponent(playerEntity, new OwnershipComponent { PlayerId = 2 });
        cm.RegisterPlayer(2, playerEntity);
        // OnInit 重置 PopUsed → 之后赋值:30 > popForDock(25) 码头门槛放行。
        cm.GetPlayerEntity(2)!.PopUsed = 30;

        // CC 在陆上 (2.5, 8.5)(微型图左陆块)。
        var cc = cm.CreateEntity();
        var ccPos = new PositionComponent();
        cm.AddComponent(cc, ccPos);
        ccPos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromFloat(2.5f), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromFloat(8.5f));
        cm.AddComponent(cc, new OwnershipComponent { PlayerId = 2 });
        cm.AddComponent(cc, new IdentityComponent
        {
            TemplateName = "structures/gaul/civil_centre",
            IsBuilding = true,
            Classes = new List<string> { "CivCentre", "Structure" },
        });

        if (withDock)
        {
            var dock = cm.CreateEntity();
            var dp = new PositionComponent();
            cm.AddComponent(dock, dp);
            dp.Position = new ZeroAD.Sim.Maths.FixedVector3D(
                ZeroAD.Sim.Maths.Fixed.FromFloat(5.5f), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromFloat(8.5f));
            cm.AddComponent(dock, new OwnershipComponent { PlayerId = 2 });
            cm.AddComponent(dock, new IdentityComponent
            {
                TemplateName = "structures/gaul/dock",
                IsBuilding = true,
                Classes = new List<string> { "Dock", "Structure" },
            });
        }

        var net = new NetTurnManager(cm, commandDelay: 2, localPlayerId: 2,
            NetRole.Standalone, expectedPlayers: new HashSet<uint> { 2 });
        var gs = new GameState(cm, templates, techCatalog, 2, new EntityMetadata(), events,
            BuildTestAccessibility()) { Net = net };
        var queues = new QueueManager(new PetraConfig(DifficultyLevel.Medium));
        return new NavalWorld { Cm = cm, Net = net, Gs = gs, Queues = queues, Cc = cc };
    }

    [Fact]
    public void NavalManager_NavalMap_QueuesDockAtShoreline()
    {
        var w = NewNavalWorld(withDock: false);
        if (w == null) return;
        var nm = new NavalManager(new PetraConfig(DifficultyLevel.Medium));

        nm.Update(w.Gs, w.Queues, w.Gs.Events);

        var queue = w.Queues.GetQueue("dock");
        Assert.NotNull(queue);
        Assert.True(queue!.HasQueuedUnits, "expected a dock construction plan queued");
        var plan = queue.Plans[0];
        // 计划带显式岸线位置:陆格、4 邻接水域。
        Assert.True(plan.Metadata.TryGetValue("position", out var pobj));
        var pos = (ZeroAD.Sim.Maths.FixedVector2D)pobj;
        float px = pos.X.ToFloat(), pz = pos.Y.ToFloat();
        var acc = w.Gs.Accessibility!;
        Assert.True(acc.LandRegionAt(px, pz) > 1);
        bool touchesWater =
            acc.WaterRegionAt(px - 1, pz) > 1 || acc.WaterRegionAt(px + 1, pz) > 1 ||
            acc.WaterRegionAt(px, pz - 1) > 1 || acc.WaterRegionAt(px, pz + 1) > 1;
        Assert.True(touchesWater, $"dock position ({px},{pz}) not on shoreline");
    }

    [Fact]
    public void NavalManager_NoPop_NoDock()
    {
        var w = NewNavalWorld(withDock: false);
        if (w == null) return;
        w.Cm.GetPlayerEntity(2)!.PopUsed = 5;   // 低于 popForDock
        var nm = new NavalManager(new PetraConfig(DifficultyLevel.Medium));

        nm.Update(w.Gs, w.Queues, w.Gs.Events);

        var queue = w.Queues.GetQueue("dock");
        Assert.True(queue == null || !queue.HasQueuedUnits);
    }

    [Fact]
    public void NavalManager_DockBuilt_TrainsShip()
    {
        var w = NewNavalWorld(withDock: true);
        if (w == null) return;
        var nm = new NavalManager(new PetraConfig(DifficultyLevel.Medium));

        nm.Update(w.Gs, w.Queues, w.Gs.Events);

        var queue = w.Queues.GetQueue("ships");
        Assert.NotNull(queue);
        Assert.True(queue!.HasQueuedUnits, "expected a ship training plan queued");
        Assert.Contains("ship_", queue.Plans[0].Type);
    }

    [Fact]
    public void NavalManager_LowWaterRegion_NotNavalMap()
    {
        // HQ 的海图判定:小水洼(<200 格)不当海图。直接测 LargestWaterRegionSize。
        var acc = BuildTestAccessibility();   // 6×16=96 格水 < 200
        Assert.True(acc.LargestWaterRegionSize() < 200);
        // 大水域 → 海图。
        const ushort water = 0x1;
        var big = new Grid<NavcellData>(20, 20);
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 20; x++)
                big.Set(x, y, new NavcellData(water));
        var accBig = new Accessibility(big, new PassClass(0x1), new PassClass(0x2), 20, 1);
        Assert.True(accBig.LargestWaterRegionSize() >= 200);
    }
}
