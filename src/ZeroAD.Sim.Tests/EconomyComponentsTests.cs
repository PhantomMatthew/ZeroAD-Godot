using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Tests for the economy components added in the M2-P0 economy-closure pass:
/// CostComponent, PopulationComponent, TrainingRestrictionsComponent, EntityLimitsComponent,
/// and the player pop-accounting helpers on ComponentManager.
/// </summary>
public class EconomyComponentsTests
{
    [Fact]
    public void EntityLimits_AllowedToTrain_RespectsCap()
    {
        var limits = new EntityLimitsComponent();
        limits.Limits["Hero"] = 1;

        Assert.True(limits.AllowedToTrain("Hero", 1));
        limits.ChangeCount("Hero", 1);
        Assert.False(limits.AllowedToTrain("Hero", 1)); // already at cap
    }

    [Fact]
    public void EntityLimits_AllowedToTrain_EmptyCategoryAlwaysAllowed()
    {
        var limits = new EntityLimitsComponent();
        Assert.True(limits.AllowedToTrain("", 100));
        Assert.True(limits.AllowedToTrain("Anything", 1000)); // no cap defined
    }

    [Fact]
    public void EntityLimits_ChangeCount_NeverGoesStale()
    {
        var limits = new EntityLimitsComponent();
        limits.ChangeCount("WarDog", 3);
        limits.ChangeCount("WarDog", -1);
        Assert.Equal(2, limits.Counts["WarDog"]);
    }

    [Fact]
    public void PlayerComponent_PopulationLimit_IsMinOfCapAndBonuses()
    {
        var player = new PlayerComponent { PopBonuses = 15, MaxPopCap = 300 };
        Assert.Equal(15, player.PopulationLimit);

        player.PopBonuses = 400;
        Assert.Equal(300, player.PopulationLimit); // capped by MaxPopCap
    }

    [Fact]
    public void PlayerComponent_PopHeadroom_NeverNegative()
    {
        var player = new PlayerComponent { PopUsed = 50, PopBonuses = 20 };
        Assert.Equal(0, player.PopHeadroom); // over capacity, clamped to 0
    }

    [Fact]
    public void PlayerComponent_CanAfford_FourResource()
    {
        var player = new PlayerComponent { Wood = 100, Food = 100, Stone = 50, Metal = 25 };
        Assert.True(player.CanAfford(100, 100, 50, 25));
        Assert.False(player.CanAfford(100, 100, 50, 26)); // metal short
    }

    [Fact]
    public void ComponentManager_ApplyOwnershipPopChange_ChargesAndRefunds()
    {
        var cm = new ComponentManager(42);

        // Set up player 1.
        var player = cm.CreateEntity();
        cm.AddComponent(player, new PlayerComponent { PopBonuses = 50 });
        cm.RegisterPlayer(1, player);

        // A unit that costs 2 pop.
        var unit = cm.CreateEntity();
        cm.AddComponent(unit, new CostComponent { PopulationCost = 2 });
        cm.AddComponent(unit, new OwnershipComponent { PlayerId = 1 });

        var p = cm.QueryInterface<PlayerComponent>(player)!;
        Assert.Equal(0, p.PopUsed);

        cm.ApplyOwnershipPopChange(unit, oldOwner: -1, newOwner: 1);
        Assert.Equal(2, p.PopUsed);

        // Unit dies / changes owner away: pop refunded.
        cm.ApplyOwnershipPopChange(unit, oldOwner: 1, newOwner: -1);
        Assert.Equal(0, p.PopUsed);
    }

    [Fact]
    public void ComponentManager_RecomputePlayerPopBonus_AggregatesPopulationComponents()
    {
        var cm = new ComponentManager(42);
        var player = cm.CreateEntity();
        cm.AddComponent(player, new PlayerComponent());
        cm.RegisterPlayer(1, player);

        var house1 = cm.CreateEntity();
        cm.AddComponent(house1, new PopulationComponent { Bonus = 10 });
        cm.AddComponent(house1, new OwnershipComponent { PlayerId = 1 });

        var house2 = cm.CreateEntity();
        cm.AddComponent(house2, new PopulationComponent { Bonus = 10 });
        cm.AddComponent(house2, new OwnershipComponent { PlayerId = 1 });

        // A house owned by another player shouldn't count.
        var enemyHouse = cm.CreateEntity();
        cm.AddComponent(enemyHouse, new PopulationComponent { Bonus = 10 });
        cm.AddComponent(enemyHouse, new OwnershipComponent { PlayerId = 2 });

        cm.RecomputePlayerPopBonus(1);
        Assert.Equal(20, cm.QueryInterface<PlayerComponent>(player)!.PopBonuses);
    }

    [Fact]
    public void CostComponent_Serialize_StableAcrossInstances()
    {
        // Two identical CostComponents must hash identically (EntityLimits/Dictionary ordering
        // is irrelevant here, but Cost has no collections — sanity check for the OOS hash).
        var cm1 = new ComponentManager(1);
        var e1 = cm1.CreateEntity();
        cm1.AddComponent(e1, new CostComponent { WoodCost = 50, FoodCost = 50, PopulationCost = 1, BuildTime = 5f });

        var cm2 = new ComponentManager(1);
        var e2 = cm2.CreateEntity();
        cm2.AddComponent(e2, new CostComponent { WoodCost = 50, FoodCost = 50, PopulationCost = 1, BuildTime = 5f });

        Assert.Equal(cm1.ComputeStateHash(), cm2.ComputeStateHash());
    }

    [Fact]
    public void EntityLimits_Serialize_DeterministicAcrossInsertionOrder()
    {
        // Insert keys in different orders; the sorted-key serialization must still hash identically.
        var cm1 = new ComponentManager(1);
        var e1 = cm1.CreateEntity();
        var l1 = new EntityLimitsComponent();
        l1.Counts["Alpha"] = 1;
        l1.Counts["Beta"] = 2;
        l1.Counts["Gamma"] = 3;
        cm1.AddComponent(e1, l1);

        var cm2 = new ComponentManager(1);
        var e2 = cm2.CreateEntity();
        var l2 = new EntityLimitsComponent();
        l2.Counts["Gamma"] = 3;
        l2.Counts["Beta"] = 2;
        l2.Counts["Alpha"] = 1;
        cm2.AddComponent(e2, l2);

        Assert.Equal(cm1.ComputeStateHash(), cm2.ComputeStateHash());
    }
}
