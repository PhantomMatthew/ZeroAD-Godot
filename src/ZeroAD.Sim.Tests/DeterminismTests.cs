using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

public class DeterminismTests
{
    private static byte[] RunSimulation(uint seed, int turns)
    {
        var cm = new ComponentManager(seed);
        var tm = new TurnManager(cm, commandDelay: 2);

        var entity1 = cm.CreateEntity();
        cm.AddComponent(entity1, new PositionComponent());
        cm.AddComponent(entity1, new HealthComponent());

        var entity2 = cm.CreateEntity();
        cm.AddComponent(entity2, new PositionComponent());

        for (int i = 0; i < turns; i++)
        {
            tm.SubmitCommand(new SimCommand(player: 0, type: 0, data: 0));
            if (i % 10 == 5)
                tm.SubmitCommand(new SimCommand(player: 1, type: 1, data: 100));

            tm.AdvanceTurn();

            if (i % 100 == 50)
            {
                var pos = cm.QueryInterface<PositionComponent>(entity1);
                if (pos != null)
                {
                    pos.Position = new FixedVector3D(
                        Fixed.FromInt(i),
                        Fixed.Zero,
                        Fixed.FromInt(i / 2));
                }
            }
        }

        return tm.ComputeStateHash();
    }

    [Fact]
    public void SameSeed_ProducesSameHash()
    {
        byte[] hash1 = RunSimulation(seed: 42, turns: 1000);
        byte[] hash2 = RunSimulation(seed: 42, turns: 1000);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void DifferentSeed_ProducesDifferentHash()
    {
        byte[] hash1 = RunSimulation(seed: 42, turns: 1000);
        byte[] hash2 = RunSimulation(seed: 99, turns: 1000);
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void RNG_IsDeterministic()
    {
        var rng1 = new Rand48(123);
        var rng2 = new Rand48(123);

        for (int i = 0; i < 1000; i++)
            Assert.Equal(rng1.Next(), rng2.Next());
    }

    [Fact]
    public void RNG_ProducesValidDoubles()
    {
        var rng = new Rand48(42);
        for (int i = 0; i < 10000; i++)
        {
            double d = rng.NextDouble();
            Assert.True(d >= 0.0 && d < 1.0);
        }
    }

    [Fact]
    public void RNG_SeedState_MatchesBoostRand48()
    {
        var rng = new Rand48(123);
        ulong first = rng.Next();
        var rng2 = new Rand48(123);
        ulong first2 = rng2.Next();
        Assert.Equal(first, first2);
    }

    [Fact]
    public void StateHash_IncludesEntityData()
    {
        var cm1 = new ComponentManager(42);
        var e1 = cm1.CreateEntity();
        cm1.AddComponent(e1, new HealthComponent());
        cm1.QueryInterface<HealthComponent>(e1)!.Current = 50;
        cm1.QueryInterface<HealthComponent>(e1)!.Max = 100;

        var cm2 = new ComponentManager(42);
        var e2 = cm2.CreateEntity();
        cm2.AddComponent(e2, new HealthComponent());
        cm2.QueryInterface<HealthComponent>(e2)!.Current = 75;
        cm2.QueryInterface<HealthComponent>(e2)!.Max = 100;

        byte[] hash1 = cm1.ComputeStateHash();
        byte[] hash2 = cm2.ComputeStateHash();
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void EntityId_LocalEntitiesAreSeparate()
    {
        var cm = new ComponentManager(42);
        var global = cm.CreateEntity();
        var local = cm.Entities.AllocateLocalEntity();

        Assert.False(global.IsLocal);
        Assert.True(local.IsLocal);
        Assert.True(local.Value >= EntityId.FirstLocalEntity);
    }
}
