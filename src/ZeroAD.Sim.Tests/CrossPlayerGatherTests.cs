using System.Collections.Generic;
using System.IO;
using ZeroAD.Sim;
using ZeroAD.Sim.AI;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using ZeroAD.Sim.Simulation;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// P2 村民不应跑到 P1 基地采浆果/农田。上一轮只修了投放点属主,
/// 玩具 EconomyManager 仍按全图最近食物派工,交完货 UnitAI 还会走回敌方供应点。
/// </summary>
public sealed class CrossPlayerGatherTests
{
    private static void AddPlayer(ComponentManager cm, int id)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PlayerComponent());
        cm.AddComponent(e, new OwnershipComponent { PlayerId = id });
        cm.AddComponent(e, new DiplomacyComponent());
        cm.Players.AddPlayer(id, e);
    }

    private static EntityId MakeDropsite(ComponentManager cm, int owner, float x, float z)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new IdentityComponent { IsBuilding = true, TemplateName = "structures/athen/civil_centre" });
        cm.AddComponent(e, new ResourceDropsite());
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var p = new FixedVector2D(pos.Position.X, pos.Position.Z);
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    private static EntityId MakeVillager(ComponentManager cm, int owner, float x, float z)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new UnitAIComponent());
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new ResourceGatherer());
        cm.AddComponent(e, new IdentityComponent { IsUnit = true, TemplateName = "units/athen/support_female_citizen" });
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var p = new FixedVector2D(pos.Position.X, pos.Position.Z);
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    private static EntityId MakeSupply(ComponentManager cm, float x, float z, string typeString,
        int owner = 0, int amount = 200)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new IdentityComponent { TemplateName = "gaia/fruit/berry_01" });
        cm.AddComponent(e, new ResourceSupply());
        var supply = cm.QueryInterface<ResourceSupply>(e)!;
        supply.Amount = amount;
        supply.MaxAmount = amount;
        supply.SetTypeString(typeString);
        if (owner > 0)
            cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.NotifyEntityCreated(e);
        var p = new FixedVector2D(pos.Position.X, pos.Position.Z);
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    private static ComponentManager TwoPlayerWorld(out TerritoryManager tm)
    {
        var cm = new ComponentManager(7);
        SimSystem.Init(cm);
        AddPlayer(cm, 1);
        AddPlayer(cm, 2);
        cm.Players.SeedDiplomacyFromTeams(new Dictionary<int, int> { [1] = 0, [2] = 1 });
        tm = new TerritoryManager(cm, 256);
        SimSystem.SetTerritoryManager(tm);
        var range = new RangeManager(cm, Fixed.FromInt(256), Fixed.FromInt(256));
        SimSystem.SetRangeManager(range);
        return cm;
    }

    private static void AddCcInfluence(ComponentManager cm, int owner, float x, float z)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new TerritoryInfluenceComponent
        {
            Radius = Fixed.FromInt(140),
            Weight = 10000,
            Root = true,
        });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var p = new FixedVector2D(pos.Position.X, pos.Position.Z);
        cm.NotifyPositionChanged(e, p, p);
    }

    [Fact]
    public void DropAtNearestDropSite_IgnoresCloserEnemyCc()
    {
        var cm = TwoPlayerWorld(out _);
        var worker = MakeVillager(cm, 2, 80, 10);
        cm.QueryInterface<ResourceGatherer>(worker)!.CarryAmount = 8;
        cm.QueryInterface<ResourceGatherer>(worker)!.CarryType = ResourceType.Food;
        MakeDropsite(cm, 1, 82, 10);   // 敌方 CC 更近
        var own = MakeDropsite(cm, 2, 140, 10);

        var ai = cm.QueryInterface<UnitAIComponent>(worker)!;
        ai.DropAtNearestDropSite();
        for (int i = 0; i < 4; i++) ai.Tick(0.1f, cm);

        Assert.Equal(own, cm.QueryInterface<ResourceGatherer>(worker)!.TargetDropsite);
    }

    [Fact]
    public void EconomyManager_DoesNotGatherEnemyOwnedField()
    {
        var cm = TwoPlayerWorld(out _);
        var worker = MakeVillager(cm, 2, 200, 10);
        var ownBerry = MakeSupply(cm, 208, 10, "food.fruit");
        MakeSupply(cm, 202, 10, "food.grain", owner: 1);   // 更近,但是 P1 农田
        MakeDropsite(cm, 2, 200, 10);

        var player = cm.GetPlayerEntity(2)!;
        player.Food = 0;
        var net = new NetTurnManager(cm, commandDelay: 1, localPlayerId: 2,
            NetRole.Standalone, new HashSet<uint> { 2 });
        var economy = new EconomyManager(cm, net);
        var snap = new AISnapshot
        {
            Player = player,
            Villagers = { worker },
        };
        economy.Update(snap, 2);
        net.AdvanceTurn();
        net.AdvanceTurn();
        cm.QueryInterface<UnitAIComponent>(worker)!.Tick(0.1f, cm);

        Assert.Equal(ownBerry, cm.QueryInterface<ResourceGatherer>(worker)!.TargetSupply);
    }

    [Fact]
    public void EconomyManager_DoesNotGatherBerryOnEnemyTerritory()
    {
        var cm = TwoPlayerWorld(out var tm);
        AddCcInfluence(cm, 1, 40, 40);
        AddCcInfluence(cm, 2, 200, 40);
        Assert.Equal(1, tm.GetOwner(Fixed.FromInt(40), Fixed.FromInt(40)));

        var worker = MakeVillager(cm, 2, 200, 40);
        // 本方浆果更远:无领土过滤时全图最近是 P1 地盘上那丛。
        var ownBerry = MakeSupply(cm, 380, 40, "food.fruit");
        MakeSupply(cm, 48, 40, "food.fruit");
        MakeDropsite(cm, 2, 200, 40);
        MakeDropsite(cm, 1, 40, 40);

        var player = cm.GetPlayerEntity(2)!;
        player.Food = 0;
        var net = new NetTurnManager(cm, commandDelay: 1, localPlayerId: 2,
            NetRole.Standalone, new HashSet<uint> { 2 });
        var economy = new EconomyManager(cm, net);
        economy.Update(new AISnapshot { Player = player, Villagers = { worker } }, 2);
        net.AdvanceTurn();
        net.AdvanceTurn();
        cm.QueryInterface<UnitAIComponent>(worker)!.Tick(0.1f, cm);

        Assert.Equal(ownBerry, cm.QueryInterface<ResourceGatherer>(worker)!.TargetSupply);
    }

    [Fact]
    public void GatherReturn_DoesNotResumeEnemyTerritorySupply()
    {
        var cm = TwoPlayerWorld(out var tm);
        AddCcInfluence(cm, 1, 40, 40);
        Assert.Equal(1, tm.GetOwner(Fixed.FromInt(48), Fixed.FromInt(40)));

        var worker = MakeVillager(cm, 2, 48, 40);
        var enemyBerry = MakeSupply(cm, 48, 40, "food.fruit");
        MakeSupply(cm, 200, 40, "food.fruit");
        MakeDropsite(cm, 2, 50, 40);   // 己方投放点就在身边,交完货即可观察是否走回

        var gatherer = cm.QueryInterface<ResourceGatherer>(worker)!;
        gatherer.CarryAmount = 10;
        gatherer.CarryType = ResourceType.Food;
        gatherer.TargetSupply = enemyBerry;

        var ai = cm.QueryInterface<UnitAIComponent>(worker)!;
        ai.Gather(enemyBerry);
        for (int i = 0; i < 40; i++)
        {
            cm.QueryInterface<UnitMotion>(worker)?.Tick(0.1f);
            ai.Tick(0.1f, cm);
            if (ai.IsIdle) break;
        }

        Assert.True(ai.IsIdle, "should finish after dropping off instead of walking back to P1 berries");
        Assert.NotEqual(enemyBerry, gatherer.TargetSupply);
    }

    private static EntityId MakeIdleBuilderVillager(ComponentManager cm, int owner, float x, float z)
    {
        var e = MakeVillager(cm, owner, x, z);
        cm.AddComponent(e, new BuilderComponent());
        return e;
    }

    private static EntityId MakeFoundation(ComponentManager cm, int owner, float x, float z,
        string template, float buildTime)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new IdentityComponent { IsBuilding = true, TemplateName = template });
        cm.AddComponent(e, new FoundationComponent());
        cm.QueryInterface<FoundationComponent>(e)!.Configure(template, buildTime);
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var p = new FixedVector2D(pos.Position.X, pos.Position.Z);
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    private static TemplateLoader DummyTemplates()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zeroad-autoharvest-tpl");
        Directory.CreateDirectory(dir);
        return new TemplateLoader(dir);
    }

    [Fact]
    public void HouseComplete_DoesNotSendIdleEnemyVillagersToNearbyTrees()
    {
        var cm = TwoPlayerWorld(out _);
        cm.Templates = DummyTemplates();
        var p1 = MakeIdleBuilderVillager(cm, 1, 50, 50);
        var p2 = MakeIdleBuilderVillager(cm, 2, 400, 400);
        MakeSupply(cm, 55, 50, "wood.tree");
        var foundation = MakeFoundation(cm, 1, 50, 50, "structures/athen/house", 1f);
        cm.QueryInterface<FoundationComponent>(foundation)!.AddBuilder(p1, 1f);
        cm.QueryInterface<FoundationComponent>(foundation)!.AddProgress(1f);
        cm.QueryInterface<UnitAIComponent>(p1)!.Repair(foundation);

        EntityId built = default;
        SimLoop.Tick(cm, 0.1f, new SimLoopState(), hooks: new SimLoopHooks
        {
            SpawnBuilding = (tpl, x, z, owner, _) =>
            {
                built = cm.CreateEntity();
                var pos = new PositionComponent();
                cm.AddComponent(built, pos);
                pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
                cm.AddComponent(built, new OwnershipComponent { PlayerId = owner });
                cm.AddComponent(built, new IdentityComponent { IsBuilding = true, TemplateName = tpl });
                cm.NotifyEntityCreated(built);
                return built;
            }
        });

        Assert.True(built.Value != 0);
        var p2Order = cm.QueryInterface<UnitAIComponent>(p2)!.CurrentOrder;
        Assert.True(p2Order == null, "tutorial P2 idle villagers must not autoharvest P1's trees");
        Assert.Null(cm.QueryInterface<ResourceGatherer>(p2)!.TargetSupply);
    }

    [Fact]
    public void StorehouseComplete_OnlyOwnBuildersAutoharvestNearbyWood()
    {
        var cm = TwoPlayerWorld(out _);
        cm.Templates = DummyTemplates();
        var p1 = MakeIdleBuilderVillager(cm, 1, 50, 50);
        var p2 = MakeIdleBuilderVillager(cm, 2, 400, 400);
        var idleOwn = MakeIdleBuilderVillager(cm, 1, 80, 50);
        var tree = MakeSupply(cm, 55, 50, "wood.tree");
        var foundation = MakeFoundation(cm, 1, 50, 50, "structures/athen/storehouse", 1f);
        cm.QueryInterface<FoundationComponent>(foundation)!.AddBuilder(p1, 1f);
        cm.QueryInterface<FoundationComponent>(foundation)!.AddProgress(1f);
        cm.QueryInterface<UnitAIComponent>(p1)!.Repair(foundation);

        SimLoop.Tick(cm, 0.1f, new SimLoopState(), hooks: new SimLoopHooks
        {
            SpawnBuilding = (tpl, x, z, owner, _) =>
            {
                var e = cm.CreateEntity();
                var pos = new PositionComponent();
                cm.AddComponent(e, pos);
                pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
                cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
                cm.AddComponent(e, new IdentityComponent { IsBuilding = true, TemplateName = tpl });
                cm.AddComponent(e, new ResourceDropsite());
                cm.NotifyEntityCreated(e);
                return e;
            }
        });

        Assert.Equal("Gather", cm.QueryInterface<UnitAIComponent>(p1)!.CurrentOrder?.Type);
        Assert.Equal(tree, cm.QueryInterface<UnitAIComponent>(p1)!.CurrentOrder?.Target);
        Assert.Null(cm.QueryInterface<UnitAIComponent>(p2)!.CurrentOrder);
        Assert.Null(cm.QueryInterface<UnitAIComponent>(idleOwn)!.CurrentOrder);
    }

    [Fact]
    public void Gather_IncompleteFoundation_IsRejected()
    {
        var cm = TwoPlayerWorld(out _);
        var worker = MakeIdleBuilderVillager(cm, 1, 50, 50);
        var foundation = MakeFoundation(cm, 1, 55, 50, "structures/athen/field", 10f);
        cm.AddComponent(foundation, new ResourceSupply());
        var supply = cm.QueryInterface<ResourceSupply>(foundation)!;
        supply.SetTypeString("food.grain");
        supply.Amount = 100;
        supply.MaxAmount = 100;

        var ai = cm.QueryInterface<UnitAIComponent>(worker)!;
        ai.Gather(foundation);
        ai.Tick(0.1f, cm);

        Assert.True(ai.IsIdle);
        Assert.DoesNotContain("GATHER", ai.FsmStateName ?? "");
        Assert.Null(cm.QueryInterface<ResourceGatherer>(worker)!.TargetSupply);
    }

    [Fact]
    public void StorehouseComplete_ContinuesNearbyFoundation_InsteadOfGatheringWood()
    {
        var cm = TwoPlayerWorld(out _);
        cm.Templates = DummyTemplates();
        var p1 = MakeIdleBuilderVillager(cm, 1, 50, 50);
        MakeSupply(cm, 55, 50, "wood.tree");
        var storehouse = MakeFoundation(cm, 1, 50, 50, "structures/athen/storehouse", 1f);
        var house = MakeFoundation(cm, 1, 60, 50, "structures/athen/house", 10f);
        cm.QueryInterface<FoundationComponent>(storehouse)!.AddBuilder(p1, 1f);
        cm.QueryInterface<FoundationComponent>(storehouse)!.AddProgress(1f);
        cm.QueryInterface<UnitAIComponent>(p1)!.Repair(storehouse);

        SimLoop.Tick(cm, 0.1f, new SimLoopState(), hooks: new SimLoopHooks
        {
            SpawnBuilding = (tpl, x, z, owner, _) =>
            {
                var e = cm.CreateEntity();
                var pos = new PositionComponent();
                cm.AddComponent(e, pos);
                pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
                cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
                cm.AddComponent(e, new IdentityComponent { IsBuilding = true, TemplateName = tpl });
                cm.AddComponent(e, new ResourceDropsite());
                cm.NotifyEntityCreated(e);
                return e;
            }
        });

        var order = cm.QueryInterface<UnitAIComponent>(p1)!.CurrentOrder;
        Assert.NotEqual("Gather", order?.Type);
        Assert.True(cm.QueryInterface<FoundationComponent>(house) != null
            && !cm.QueryInterface<FoundationComponent>(house)!.IsBuilt);
    }
}
