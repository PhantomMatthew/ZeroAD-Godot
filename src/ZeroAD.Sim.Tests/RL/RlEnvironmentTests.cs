using System;
using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.RL;

namespace ZeroAD.Sim.Tests.RL;

public sealed class RlEnvironmentTests
{
    [Fact]
    public void FogOff_HidesFarEnemy_InEntityTable()
    {
        using var env = new RlEnvironment(new RlConfig { Seed = 7, PrivilegedVision = false });
        var obs = env.Reset();
        Assert.Equal(0, obs.Scalars[RlSpec.Scal.Privileged]);
        Assert.True(CountValid(obs) >= 1, "own seer is always visible");
        Assert.False(HasOwnerRel(obs, RlSpec.OwnerRel.Enemy),
            "far enemy must not enter the table under fog");
    }

    [Fact]
    public void PrivilegedVision_IncludesEnemy()
    {
        using var env = new RlEnvironment(new RlConfig { Seed = 7, PrivilegedVision = true });
        var obs = env.Reset();
        Assert.Equal(1, obs.Scalars[RlSpec.Scal.Privileged]);
        Assert.True(HasOwnerRel(obs, RlSpec.OwnerRel.Enemy));
        Assert.True(HasOwnerRel(obs, RlSpec.OwnerRel.Self));
    }

    [Fact]
    public void SameSeed_SameStateHash()
    {
        using var a = new RlEnvironment(new RlConfig { Seed = 99, PrivilegedVision = false });
        using var b = new RlEnvironment(new RlConfig { Seed = 99, PrivilegedVision = false });
        a.Reset();
        b.Reset();
        var ha = Convert.ToHexString(a.Sim.ComputeStateHash());
        var hb = Convert.ToHexString(b.Sim.ComputeStateHash());
        Assert.Equal(ha, hb);

        a.Step(RlAction.NoOp());
        b.Step(RlAction.NoOp());
        Assert.Equal(Convert.ToHexString(a.Sim.ComputeStateHash()),
            Convert.ToHexString(b.Sim.ComputeStateHash()));
    }

    [Fact]
    public void BadTargetIndex_MatchesNoOpHash()
    {
        using var good = new RlEnvironment(new RlConfig { Seed = 3, CommandDelay = 1 });
        using var bad = new RlEnvironment(new RlConfig { Seed = 3, CommandDelay = 1 });
        good.Reset();
        var obs = bad.Reset();
        int n = 8;
        for (int i = 0; i < n; i++)
            good.Step(RlAction.NoOp());
        var attackNothing = new RlAction(RlFunction.Attack, selectedIndex: 0, targetEntityIndex: 400);
        for (int i = 0; i < n; i++)
            bad.Step(attackNothing);
        Assert.Equal(Convert.ToHexString(good.Sim.ComputeStateHash()),
            Convert.ToHexString(bad.Sim.ComputeStateHash()));
        Assert.Equal(1, obs.FunctionMask[(int)RlFunction.NoOp]);
    }

    [Fact]
    public void Timeout_EndsEpisode()
    {
        using var env = new RlEnvironment(new RlConfig
        {
            Seed = 1,
            MaxEpisodeTurns = 3,
            CommandDelay = 1,
            StepMul = 1
        });
        env.Reset();
        RlStepResult? last = null;
        for (int i = 0; i < 8 && last?.Done != true; i++)
            last = env.Step(RlAction.NoOp());
        Assert.NotNull(last);
        Assert.True(last!.Done);
        Assert.Equal(0, last.Reward);
    }

    [Fact]
    public void RealMatch_FillsCatalogAndFunctionMask()
    {
        using var env = new RlEnvironment(new RlConfig { Seed = 1, PrivilegedVision = true });
        var obs = env.Reset();
        if (!env.LoadedRealMatch) return;

        Assert.True(env.Catalog.TemplateCount > 100, "template catalog should intern the public set");
        Assert.True(env.Catalog.TechCount > 10, "tech catalog should intern technologies");
        Assert.Equal(1, obs.FunctionMask[(int)RlFunction.Attack]);
        Assert.Equal(1, obs.FunctionMask[(int)RlFunction.Build]);
        Assert.Equal(1, obs.FunctionMask[(int)RlFunction.Train]);
        Assert.Equal(1, obs.FunctionMask[(int)RlFunction.Research]);
        Assert.Equal(1, obs.FunctionMask[(int)RlFunction.Gather]);
        Assert.Equal(0, obs.Scalars[RlSpec.Scal.Phase]);
        Assert.True(HasOwnerRel(obs, RlSpec.OwnerRel.Enemy));
        Assert.True(HasFlag(obs, RlSpec.Ent.FlagBuilding));
        Assert.True(HasFlag(obs, RlSpec.Ent.FlagCanGather));
        Assert.True(obs.Scalars[RlSpec.Scal.Wood] > 0);
    }

    [Fact]
    public void ConquestUnits_FiresWhenEnemyUnitsDie()
    {
        using var env = new RlEnvironment(new RlConfig
        {
            Seed = 1,
            PrivilegedVision = true,
            CommandDelay = 1,
            StepMul = 1
        });
        env.Reset();
        if (!env.LoadedRealMatch) return;

        KillPlayerUnits(env.Sim, 2);
        RlStepResult? last = null;
        for (int i = 0; i < 30 && last?.Done != true; i++)
            last = env.Step(RlAction.NoOp());
        Assert.NotNull(last);
        Assert.True(last!.Done);
        Assert.Equal(1, last.Reward);
        Assert.Equal(1, last.Observation.Scalars[RlSpec.Scal.Won]);
    }

    [Fact]
    public void PetraTick_DoesNotThrow()
    {
        using var env = new RlEnvironment(new RlConfig
        {
            Seed = 2,
            PrivilegedVision = true,
            TickOpponentAi = true,
            CommandDelay = 1
        });
        env.Reset();
        if (!env.LoadedRealMatch) return;
        var result = env.Step(RlAction.NoOp());
        Assert.False(result.Done);
    }

    private static void KillPlayerUnits(ComponentManager cm, int playerId)
    {
        foreach (var e in cm.AllEntities)
        {
            var own = cm.QueryInterface<OwnershipComponent>(e);
            if (own == null || own.PlayerId != playerId) continue;
            var id = cm.QueryInterface<IdentityComponent>(e);
            if (id == null || !id.IsUnit) continue;
            var hp = cm.QueryInterface<HealthComponent>(e);
            if (hp != null) hp.Current = 0;
        }
    }

    private static bool HasFlag(RlObservation obs, int flag)
    {
        for (int i = 0; i < RlSpec.MaxEntities; i++)
        {
            if (obs.Entity(i, RlSpec.Ent.Valid) == 0) continue;
            if ((obs.Entity(i, RlSpec.Ent.Flags) & flag) != 0) return true;
        }
        return false;
    }

    private static int CountValid(RlObservation obs)
    {
        int n = 0;
        for (int i = 0; i < RlSpec.MaxEntities; i++)
            if (obs.Entity(i, RlSpec.Ent.Valid) != 0) n++;
        return n;
    }

    private static bool HasOwnerRel(RlObservation obs, int rel)
    {
        for (int i = 0; i < RlSpec.MaxEntities; i++)
        {
            if (obs.Entity(i, RlSpec.Ent.Valid) == 0) continue;
            if (obs.Entity(i, RlSpec.Ent.OwnerRel) == rel) return true;
        }
        return false;
    }
}
