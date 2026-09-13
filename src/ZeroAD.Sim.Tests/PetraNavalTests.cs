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
/// 暂存数据(模板)缺失时按惯例跳过。
/// </summary>
public sealed class PetraNavalTests
{
    private static string? FindRepoPath(string relative) => RepoPaths.Resolve(relative);

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

    // ── 渔业(BaseManager.StartFishing/UpdateFisher,原版 worker.js startFishing 移植)──

    private sealed class FisherWorld
    {
        public required ComponentManager Cm;
        public required GameState Gs;
        public required BaseManager Base;
        public required EntityId Boat;
        public EntityId Fish1;
        public EntityId Fish2;
    }

    /// <summary>渔船世界:微型海陆图(默认 BuildTestAccessibility 左陆/中水/右陆),
    /// 渔船在水中 (7.5,8.5),码头在水中格 (6.5,8.5)(码头须落水域格——
    /// getSeaAccess 直读 navalPassMap,与原版一致),鱼点 (9.5,8.5)/(10.5,3.5)。</summary>
    private static FisherWorld? NewFisherWorld(
        bool withDock = true, bool withFish = true, Accessibility? acc = null,
        float boatX = 7.5f, float boatZ = 8.5f, float dockX = 6.5f, float dockZ = 8.5f,
        float fish1X = 9.5f, float fish1Z = 8.5f, float fish2X = 10.5f, float fish2Z = 3.5f)
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

        EntityId Placed(string template, int owner, float x, float z, bool isUnit)
        {
            var e = cm.CreateEntity();
            var pos = new PositionComponent();
            cm.AddComponent(e, pos);
            pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
                ZeroAD.Sim.Maths.Fixed.FromFloat(x), ZeroAD.Sim.Maths.Fixed.Zero,
                ZeroAD.Sim.Maths.Fixed.FromFloat(z));
            cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
            cm.AddComponent(e, new IdentityComponent { TemplateName = template, IsUnit = isUnit, IsBuilding = !isUnit });
            cm.NotifyEntityCreated(e);
            cm.NotifyOwnerChanged(e, -1, owner);
            var p = new ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z);
            cm.NotifyPositionChanged(e, p, p);
            // AI 事件缓冲的 Create 走 SimEventBus(cm.Notify* 是内核内部钩子,不到缓冲)。
            cm.Events.RaiseEntityCreated(new ZeroAD.Sim.Events.EntityCreatedEvent
            { Entity = e, TemplateName = template, OwnerPlayerId = owner });
            return e;
        }

        EntityId dock = default;
        if (withDock)
            dock = Placed("structures/gaul/dock", 2, dockX, dockZ, isUnit: false);

        var boat = Placed("units/gaul/ship_fishing", 2, boatX, boatZ, isUnit: true);
        cm.AddComponent(boat, new ResourceGatherer());

        EntityId fish1 = default, fish2 = default;
        if (withFish)
        {
            fish1 = Placed("gaia/fish/tuna", 0, fish1X, fish1Z, isUnit: true);
            var s1 = new ResourceSupply { Amount = 500 };
            s1.SetTypeString("food.fish");
            cm.AddComponent(fish1, s1);
            fish2 = Placed("gaia/fish/tuna", 0, fish2X, fish2Z, isUnit: true);
            var s2 = new ResourceSupply { Amount = 500 };
            s2.SetTypeString("food.fish");
            cm.AddComponent(fish2, s2);
        }

        var net = new NetTurnManager(cm, commandDelay: 2, localPlayerId: 2,
            NetRole.Standalone, expectedPlayers: new HashSet<uint> { 2 });
        var gs = new GameState(cm, templates, techCatalog, 2, new EntityMetadata(),
            events, acc ?? BuildTestAccessibility()) { Net = net };
        var bases = new BasesManager(new PetraConfig(DifficultyLevel.Medium));
        var baseMgr = bases.CreateBase(gs, withDock ? dock.Value : boat.Value);
        return new FisherWorld { Cm = cm, Gs = gs, Base = baseMgr, Boat = boat, Fish1 = fish1, Fish2 = fish2 };
    }

    [Fact]
    public void StartFishing_PicksFishNearestToDock()
    {
        // 原版 startFishing:按"鱼点→最近同海域码头"距离选点(非离船距离)。
        // fish1 (9.5,8.5) 距码头 9;fish2 (10.5,3.5) 距码头 41 → 选 fish1。
        var w = NewFisherWorld();
        if (w == null) return;
        var boat = w.Gs.GetEntityById(w.Boat.Value)!;

        Assert.True(w.Base.StartFishing(w.Gs, boat));
        Assert.Equal(w.Fish1.Value, (uint)w.Gs.Metadata.GetObject(w.Boat.Value, "supply")!);
    }

    [Fact]
    public void StartFishing_NoFish_FallsBackToWorker()
    {
        // 无鱼 → false(任务规格:回退普通 worker;原版毁船)。
        var w = NewFisherWorld(withFish: false);
        if (w == null) return;
        var boat = w.Gs.GetEntityById(w.Boat.Value)!;
        Assert.False(w.Base.StartFishing(w.Gs, boat));
        Assert.Null(w.Gs.Metadata.GetObject(w.Boat.Value, "supply"));
    }

    [Fact]
    public void StartFishing_NoSameSeaDock_FallsBackToWorker()
    {
        // 有鱼无码头 → false(任务规格;原版会以 distMin=1e6 硬选导致货物卡死)。
        var w = NewFisherWorld(withDock: false);
        if (w == null) return;
        var boat = w.Gs.GetEntityById(w.Boat.Value)!;
        Assert.False(w.Base.StartFishing(w.Gs, boat));
    }

    [Fact]
    public void StartFishing_FishInOtherSea_NotPicked()
    {
        // 双海图:船/码头在海 A,鱼在海 B → exhausted → false(原版海域过滤)。
        const ushort land = 0x2, water = 0x1;
        var grid = new Grid<NavcellData>(24, 16);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 24; x++)
                grid.Set(x, y, new NavcellData(x >= 3 && x < 6 || x >= 12 && x < 15 ? water : land));
        var twoSeas = new Accessibility(grid, new PassClass(0x1), new PassClass(0x2), 24, 1);
        // 海 A 与 海 B 须为不同水域区域。
        Assert.NotEqual(twoSeas.WaterRegionAt(4, 8), twoSeas.WaterRegionAt(13, 8));

        var w = NewFisherWorld(acc: twoSeas, boatX: 4.5f, boatZ: 8.5f,
            dockX: 3.5f, dockZ: 8.5f, fish1X: 13.5f, fish1Z: 8.5f, fish2X: 13.5f, fish2Z: 4.5f);
        if (w == null) return;
        var boat = w.Gs.GetEntityById(w.Boat.Value)!;
        Assert.False(w.Base.StartFishing(w.Gs, boat));
    }

    [Fact]
    public void BaseManager_Update_FishingBoat_FullPipeline()
    {
        // 端到端:Update 一轮完成 role 分配(idle)→ fisher 子角色(有鱼)→
        // UpdateFisher(空闲)→ StartFishing 派点(supply 元数据落 fish1)。
        var w = NewFisherWorld();
        if (w == null) return;
        w.Base.AssignEntity(w.Gs, w.Gs.GetEntityById(w.Boat.Value)!);   // 船入基地籍
        w.Base.Update(w.Gs, w.Gs.Events);
        Assert.Equal(WorkerRoles.RoleWorker,
            w.Gs.Metadata.GetObject(w.Boat.Value, "role")?.ToString());
        Assert.Equal(WorkerRoles.SubroleFisher,
            w.Gs.Metadata.GetObject(w.Boat.Value, "subrole")?.ToString());
        Assert.Equal(w.Fish1.Value, (uint)w.Gs.Metadata.GetObject(w.Boat.Value, "supply")!);
    }

    [Fact]
    public void ReassignIdleWorkers_FishingBoat_NoFish_StaysWorker()
    {
        // 无鱼门:不打 fisher 子角色(避免 idle↔fisher 振荡;普通 worker 回退)。
        var w = NewFisherWorld(withFish: false);
        if (w == null) return;
        var boat = w.Gs.GetEntityById(w.Boat.Value)!;
        w.Gs.Metadata.Set(w.Boat.Value, "subrole", WorkerRoles.SubroleIdle);
        w.Base.ReassignIdleWorkers(w.Gs, boat);
        Assert.NotEqual(WorkerRoles.SubroleFisher,
            w.Gs.Metadata.GetObject(w.Boat.Value, "subrole")?.ToString());
    }
}
