using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 采空销毁:原版 ResourceSupply.Change 在 amount==0 时 Engine.DestroyEntity;
/// Max=Infinity(农田)走 IsInfinite,TakeResources 不扣量、不销毁。
/// </summary>
public sealed class ResourceSupplyExhaustTests
{
    private static ResourceSupply AttachSupply(ComponentManager cm, EntityId e, int amount, int max)
    {
        cm.AddComponent(e, new ResourceSupply());
        var supply = cm.QueryInterface<ResourceSupply>(e)!;
        supply.Amount = amount;
        supply.MaxAmount = max;
        return supply;
    }

    [Fact]
    public void Take_LastUnit_DestroysEntity()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var tree = cm.CreateEntity();
        var supply = AttachSupply(cm, tree, amount: 1, max: 200);

        Assert.Equal(1, supply.Take(1, cm));
        Assert.DoesNotContain(tree, cm.AllEntities);
        Assert.Null(cm.QueryInterface<ResourceSupply>(tree));
    }

    [Fact]
    public void Take_Partial_LeavesEntity()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var tree = cm.CreateEntity();
        var supply = AttachSupply(cm, tree, amount: 5, max: 200);

        Assert.Equal(3, supply.Take(3, cm));
        Assert.Equal(2, supply.Amount);
        Assert.Contains(tree, cm.AllEntities);
    }

    [Fact]
    public void Take_InfiniteField_DoesNotDecrementOrDestroy()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var field = cm.CreateEntity();
        var supply = AttachSupply(cm, field, amount: int.MaxValue, max: int.MaxValue);

        Assert.Equal(10, supply.Take(10, cm));
        Assert.Equal(int.MaxValue, supply.Amount);
        Assert.Contains(field, cm.AllEntities);
        Assert.True(supply.IsInfinite);
    }

    [Fact]
    public void GatherLastUnit_RemovesSupplyFromWorld()
    {
        var cm = new ComponentManager(rngSeed: 1);
        Components.SimSystem.Init(cm);
        var unit = cm.CreateEntity();
        cm.AddComponent(unit, new PositionComponent());
        cm.AddComponent(unit, new UnitMotion());
        cm.AddComponent(unit, new IdentityComponent());
        cm.AddComponent(unit, new UnitAIComponent());
        cm.AddComponent(unit, new OwnershipComponent { PlayerId = 1 });
        cm.AddComponent(unit, new ResourceGatherer { GatherRate = 100 });

        var tree = cm.CreateEntity();
        cm.AddComponent(tree, new PositionComponent());
        AttachSupply(cm, tree, amount: 1, max: 1);

        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;
        ai.Gather(tree);
        for (int i = 0; i < 40; i++)
        {
            cm.QueryInterface<UnitMotion>(unit)?.Tick(0.1f);
            ai.Tick(0.1f, cm);
            if (!cm.AllEntities.Contains(tree))
                break;
        }

        Assert.DoesNotContain(tree, cm.AllEntities);
        var gatherer = cm.QueryInterface<ResourceGatherer>(unit)!;
        Assert.True(gatherer.CarryAmount > 0, "last unit must still be carried after the tree is gone");
    }
}
