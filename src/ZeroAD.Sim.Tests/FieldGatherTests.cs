using System;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;
using ZeroAD.Sim.Simulation;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 农田采集:模板 Max=Infinity、平民 food.grain=0.5/s、完工 autoharvest。
/// 原版 PerformGather 按 1000/rate 毫秒取 1;C# 若 (int)(0.5×0.1) 截断则永远不进账。
/// </summary>
public sealed class FieldGatherTests
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

        var p1 = cm.CreateEntity();
        var pc = new PlayerComponent();
        cm.AddComponent(p1, pc);
        cm.Players.AddPlayer(1, p1);
        pc.Food = 0;
        pc.Wood = 5000;
        return (cm, pf);
    }

    [Fact]
    public void FieldTemplate_ParsesInfiniteGrainSupply()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var field = templates.ExtractStats("structures/athen/field");
        Assert.Equal("food.grain", field.ResourceTypeString);
        Assert.True(field.ResourceAmount > 10_000,
            $"field Max=Infinity must not parse as {field.ResourceAmount}");
    }

    [Fact]
    public void SpawnedField_HasGatherableGrain()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var (cm, _) = SetupLandWorld(templates);
        var field = cm.SpawnEntity("structures/athen/field", 64f, 64f, ownerPlayerId: 1);
        var supply = cm.QueryInterface<ResourceSupply>(field);
        Assert.NotNull(supply);
        Assert.Equal("grain", supply!.SpecificType);
        Assert.Equal(ResourceType.Food, supply.Type);
        Assert.False(supply.IsEmpty);
        Assert.True(supply.Amount > 10_000, $"expected infinite field, got Amount={supply.Amount}");
    }

    [Fact]
    public void FieldFootprint_ContainsCornerThatFifteenMetreCircleMisses()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var (cm, _) = SetupLandWorld(templates);
        var field = cm.SpawnEntity("structures/athen/field", 64f, 64f, ownerPlayerId: 1);
        var fp = cm.QueryInterface<FootprintComponent>(field);
        Assert.NotNull(fp);

        // 28×28 正方形角点距中心 ≈ 19.8m,硬编码 15m 点击圆会漏。
        Assert.True(fp!.ContainsWorldPoint(64f + 13f, 64f + 13f));
        Assert.False(fp.ContainsWorldPoint(64f + 20f, 64f + 20f));
        Assert.True(fp.ContainsWorldPoint(64f + 13.9f, 64f));
        Assert.False(fp.ContainsWorldPoint(64f + 14.2f, 64f));
    }

    [Fact]
    public void Civilian_GathersGrainFromField_AtTemplateRate()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var (cm, pf) = SetupLandWorld(templates);
        var field = cm.SpawnEntity("structures/athen/field", 64f, 64f, ownerPlayerId: 1);
        var villager = cm.SpawnEntity("units/athen/support_female_citizen", 48f, 64f, ownerPlayerId: 1);
        cm.SpawnEntity("structures/athen/civil_centre", 20f, 64f, ownerPlayerId: 1);
        pf.RebuildGrid();

        var gatherer = cm.QueryInterface<ResourceGatherer>(villager);
        Assert.NotNull(gatherer);
        Assert.True(gatherer!.Rates.ContainsKey("food.grain"),
            "civilian template Rates/food.grain must be copied onto ResourceGatherer");

        var ai = cm.QueryInterface<UnitAIComponent>(villager)!;
        ai.Gather(field);

        int gatheringTicks = 0;
        int carryAtGatherStart = 0;
        for (int i = 0; i < 400; i++)
        {
            cm.QueryInterface<UnitMotion>(villager)?.Tick(0.1f);
            ai.Tick(0.1f, cm);
            string fsm = ai.FsmStateName ?? "";
            if (fsm.Contains("GATHER.GATHERING", StringComparison.Ordinal))
            {
                if (gatheringTicks == 0)
                    carryAtGatherStart = gatherer.CarryAmount;
                gatheringTicks++;
                if (gatheringTicks == 5)
                    Assert.Equal(carryAtGatherStart, gatherer.CarryAmount);
            }
        }

        Assert.True(gatheringTicks > 20,
            $"never stayed in GATHERING (ticks={gatheringTicks} state={ai.FsmStateName})");
        int food = cm.GetPlayerEntity(1)!.Food;
        Assert.True(gatherer.CarryAmount > 0 || food > 0,
            $"no grain taken: carry={gatherer.CarryAmount} playerFood={food} state={ai.FsmStateName}");
    }

    [Fact]
    public void Autoharvest_CompletedFieldAssignsGather()
    {
        var templates = TryLoadTemplates();
        if (templates == null) return;

        var (cm, pf) = SetupLandWorld(templates);
        var villager = cm.SpawnEntity("units/athen/support_female_citizen", 50f, 50f, ownerPlayerId: 1);
        var foundation = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(foundation, pos);
        pos.Position = new FixedVector3D(Fixed.FromFloat(64f), Fixed.Zero, Fixed.FromFloat(64f));
        cm.AddComponent(foundation, new OwnershipComponent { PlayerId = 1 });
        cm.AddComponent(foundation, new IdentityComponent
        {
            IsBuilding = true,
            TemplateName = "structures/athen/field"
        });
        cm.AddComponent(foundation, new FoundationComponent());
        cm.QueryInterface<FoundationComponent>(foundation)!.Configure("structures/athen/field", 1f);
        cm.QueryInterface<FoundationComponent>(foundation)!.AddBuilder(villager, 1f);
        cm.QueryInterface<FoundationComponent>(foundation)!.AddProgress(1f);
        cm.QueryInterface<UnitAIComponent>(villager)!.Repair(foundation);
        pf.RebuildGrid();

        SimLoop.Tick(cm, 0.1f, new SimLoopState(), hooks: new SimLoopHooks
        {
            SpawnBuilding = (tpl, x, z, owner, _) => cm.SpawnEntity(tpl, x, z, owner)
        });

        var order = cm.QueryInterface<UnitAIComponent>(villager)!.CurrentOrder;
        Assert.Equal("Gather", order?.Type);
        var target = order!.Target;
        Assert.True(target.HasValue);
        var supply = cm.QueryInterface<ResourceSupply>(target!.Value);
        Assert.NotNull(supply);
        Assert.Equal("grain", supply!.SpecificType);
        Assert.False(supply.IsEmpty);
    }
}
