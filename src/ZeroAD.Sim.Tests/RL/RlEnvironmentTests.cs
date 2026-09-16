using System;
using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Net;
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
        Assert.Equal(1, obs.FunctionMask[(int)RlFunction.Patrol]);
        Assert.Equal(1, obs.FunctionMask[(int)RlFunction.AttackWalk]);
        Assert.Equal(1, obs.FunctionMask[(int)RlFunction.ReturnResource]);
        Assert.Equal(1, obs.FunctionMask[(int)RlFunction.Guard]);
        Assert.Equal(1, obs.FunctionMask[(int)RlFunction.Rally]);
        Assert.Equal(0, obs.FunctionMask[(int)RlFunction.Ungarrison]);
        Assert.Equal(0, obs.Scalars[RlSpec.Scal.Phase]);
        Assert.True(HasOwnerRel(obs, RlSpec.OwnerRel.Enemy));
        Assert.True(HasFlag(obs, RlSpec.Ent.FlagBuilding));
        Assert.True(HasFlag(obs, RlSpec.Ent.FlagCanGather));
        Assert.True(obs.Scalars[RlSpec.Scal.Wood] > 0);
    }

    [Fact]
    public void RealMatch_PerEntityCatalog_CcTrainsAndVillagerBuilds()
    {
        using var env = new RlEnvironment(new RlConfig { Seed = 1, PrivilegedVision = true });
        var obs = env.Reset();
        if (!env.LoadedRealMatch) return;

        int ccTmpl = env.Catalog.LookupTemplate("structures/athen/civil_centre");
        int spearTmpl = env.Catalog.LookupTemplate("units/athen/infantry_spearman_b");
        int civilianTmpl = env.Catalog.LookupTemplate("units/athen/support_civilian");
        int houseTmpl = env.Catalog.LookupTemplate("structures/athen/house");
        int phase = env.Catalog.LookupTech("phase_town_athen");
        if (phase == 0) phase = env.Catalog.LookupTech("phase_town");
        Assert.True(ccTmpl > 0 && spearTmpl > 0 && civilianTmpl > 0);

        int ccRow = FindRow(obs, ccTmpl, RlSpec.OwnerRel.Self);
        int spearRow = FindRow(obs, spearTmpl, RlSpec.OwnerRel.Self);
        int civilianRow = FindRow(obs, civilianTmpl, RlSpec.OwnerRel.Self);
        Assert.True(ccRow >= 0, "own civic centre should be in the entity table");
        Assert.True(spearRow >= 0, "own spearman should be in the entity table");
        Assert.True(civilianRow >= 0, "own civilian should be in the entity table");

        uint trainBit = 1u << (int)RlFunction.Train;
        uint buildBit = 1u << (int)RlFunction.Build;
        uint researchBit = 1u << (int)RlFunction.Research;
        Assert.True((obs.EntityMask[ccRow] & trainBit) != 0);
        Assert.True((obs.EntityMask[ccRow] & researchBit) != 0);
        Assert.Equal(0u, obs.EntityMask[spearRow] & trainBit);
        Assert.True((obs.EntityMask[civilianRow] & buildBit) != 0);
        Assert.Equal(0u, obs.EntityMask[civilianRow] & trainBit);

        Assert.True(CatalogContains(obs, ccRow, RlSpec.Cat.Train, spearTmpl));
        Assert.True(CatalogContains(obs, ccRow, RlSpec.Cat.Train, civilianTmpl));
        if (houseTmpl > 0)
            Assert.True(CatalogContains(obs, civilianRow, RlSpec.Cat.Build, houseTmpl));
        if (phase > 0)
            Assert.True(CatalogContains(obs, ccRow, RlSpec.Cat.Research, phase));
    }

    [Fact]
    public void RealMatch_CarryAndQueueFeats_StartEmpty_ThenCarryShows()
    {
        using var env = new RlEnvironment(new RlConfig { Seed = 1, PrivilegedVision = true });
        var obs = env.Reset();
        if (!env.LoadedRealMatch) return;
        Assert.False(HasEntityFeat(obs, RlSpec.Ent.CarryAmount, v => v > 0));
        Assert.False(HasEntityFeat(obs, RlSpec.Ent.QueueCount, v => v > 0));

        EntityId? civilian = null;
        foreach (var e in env.Sim.AllEntities)
        {
            var g = env.Sim.QueryInterface<ResourceGatherer>(e);
            if (g == null) continue;
            var own = env.Sim.QueryInterface<OwnershipComponent>(e);
            if (own?.PlayerId != 1) continue;
            g.CarryAmount = 17;
            g.CarryType = ResourceType.Food;
            civilian = e;
            break;
        }
        Assert.True(civilian != null, "own gatherer");
        obs = env.Step(RlAction.NoOp()).Observation;
        bool found = false;
        for (int i = 0; i < RlSpec.MaxEntities; i++)
        {
            if (obs.Entity(i, RlSpec.Ent.Valid) == 0) continue;
            if (obs.Entity(i, RlSpec.Ent.CarryAmount) == 17
                && obs.Entity(i, RlSpec.Ent.CarryType) == (int)ResourceType.Food + 1)
            {
                found = true;
                break;
            }
        }
        Assert.True(found, "carry amount 17 food should appear on a visible own gatherer");
    }

    [Fact]
    public void EncodePriority_OwnCivicCentre_BeatsGaiaTree()
    {
        using var env = new RlEnvironment(new RlConfig { Seed = 1, PrivilegedVision = true });
        env.Reset();
        if (!env.LoadedRealMatch) return;
        EntityId? cc = null;
        EntityId? tree = null;
        foreach (var e in env.Sim.AllEntities)
        {
            var id = env.Sim.QueryInterface<IdentityComponent>(e);
            if (id == null) continue;
            var own = env.Sim.QueryInterface<OwnershipComponent>(e);
            if (cc == null && own?.PlayerId == 1 && id.TemplateName.Contains("civil_centre", StringComparison.Ordinal))
                cc = e;
            if (tree == null && id.TemplateName.Contains("aleppo_pine", StringComparison.Ordinal))
                tree = e;
        }
        Assert.True(cc != null && tree != null);
        Assert.True(ObservationEncoder.EncodePriority(env.Sim, cc!.Value, 1)
            < ObservationEncoder.EncodePriority(env.Sim, tree!.Value, 1));
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

    [Fact]
    public void InterleavedWorlds_MoveHash_MatchesSolo()
    {
        var soloCfg = SandboxCfg(11);
        using var solo = new RlEnvironment(soloCfg);
        var obs = solo.Reset();
        int sel = FirstSelf(obs);
        var move = new RlAction(RlFunction.Move, sel, -1, 40, 40);
        solo.Step(move);
        string hSolo = Convert.ToHexString(solo.Sim.ComputeStateHash());

        using var a = new RlEnvironment(soloCfg);
        using var b = new RlEnvironment(SandboxCfg(22));
        var obsA = a.Reset();
        b.Reset();
        b.Step(new RlAction(RlFunction.Move, FirstSelf(b.Observation), -1, 20, 20));
        a.Step(new RlAction(RlFunction.Move, FirstSelf(obsA), -1, 40, 40));
        Assert.Equal(hSolo, Convert.ToHexString(a.Sim.ComputeStateHash()));
    }

    [Fact]
    public void Translate_NewFunctions_EmitCommands()
    {
        var obs = new RlObservation();
        obs.SetEntity(0, RlSpec.Ent.Valid, 1);
        obs.SetEntity(0, RlSpec.Ent.EntityId, 7);
        obs.SetEntity(1, RlSpec.Ent.Valid, 1);
        obs.SetEntity(1, RlSpec.Ent.EntityId, 9);
        obs.FunctionMask[(int)RlFunction.Patrol] = 1;
        obs.FunctionMask[(int)RlFunction.Guard] = 1;
        obs.FunctionMask[(int)RlFunction.Ungarrison] = 1;
        Assert.True(ActionTranslator.TryTranslate(obs,
            new RlAction(RlFunction.Patrol, 0, -1, 8, 9), 1, 256, new RlCatalog(), out var patrol));
        Assert.Equal(NetCommandType.Patrol, patrol.Type);
        Assert.True(ActionTranslator.TryTranslate(obs,
            new RlAction(RlFunction.Guard, 0, 1), 1, 256, new RlCatalog(), out var guard));
        Assert.Equal(NetCommandType.Guard, guard.Type);
        Assert.True(ActionTranslator.TryTranslate(obs,
            new RlAction(RlFunction.Ungarrison, 0, -1), 1, 256, new RlCatalog(), out var unload));
        Assert.Equal(NetCommandType.Ungarrison, unload.Type);
        obs.FunctionMask[(int)RlFunction.Delete] = 1;
        obs.FunctionMask[(int)RlFunction.Stance] = 1;
        Assert.True(ActionTranslator.TryTranslate(obs,
            new RlAction(RlFunction.Delete, 0), 1, 256, new RlCatalog(), out var delete));
        Assert.Equal(NetCommandType.Delete, delete.Type);
        Assert.True(ActionTranslator.TryTranslate(obs,
            new RlAction(RlFunction.Stance, 0, catalogId: 2), 1, 256, new RlCatalog(), out var stance));
        Assert.Equal(NetCommandType.SetUnitStance, stance.Type);
        Assert.Equal("defensive", stance.TemplateName);
        obs.FunctionMask[(int)RlFunction.Formation] = 1;
        obs.FunctionMask[(int)RlFunction.Barter] = 1;
        Assert.True(ActionTranslator.TryTranslate(obs,
            new RlAction(RlFunction.Formation, 0, selected1: 1), 1, 256, new RlCatalog(),
            out var form));
        Assert.Equal(NetCommandType.Formation, form.Type);
        Assert.StartsWith("box|", form.TemplateName);
        Assert.True(ActionTranslator.TryTranslate(obs,
            new RlAction(RlFunction.Barter, 0), 1, 256, new RlCatalog(), out var barter));
        Assert.Equal(NetCommandType.Barter, barter.Type);
        obs.FunctionMask[(int)RlFunction.Tribute] = 1;
        obs.FunctionMask[(int)RlFunction.SetTradingGoods] = 1;
        Assert.True(ActionTranslator.TryTranslate(obs,
            new RlAction(RlFunction.Tribute, 0, catalogId: 1 | (1 << 2)), 1, 256, new RlCatalog(),
            out var tribute));
        Assert.Equal(NetCommandType.Tribute, tribute.Type);
        Assert.Equal(500, tribute.IntParam2);
        Assert.Equal((int)ResourceType.Food, tribute.FixedParam1);
        Assert.True(ActionTranslator.TryTranslate(obs,
            new RlAction(RlFunction.Barter, 0, catalogId: 0 | (2 << 2) | (1 << 4)), 1, 256,
            new RlCatalog(), out var barterPacked));
        Assert.Equal((int)ResourceType.Wood, barterPacked.IntParam1);
        Assert.Equal((int)ResourceType.Stone, barterPacked.IntParam2);
        Assert.Equal(500, barterPacked.FixedParam1);
        Assert.True(ActionTranslator.TryTranslate(obs,
            new RlAction(RlFunction.SetTradingGoods, 0, catalogId: 1 | (1 << 2)), 1, 256,
            new RlCatalog(), out var goods));
        Assert.Equal(50, goods.IntParam1);
        Assert.Equal(50, goods.IntParam2);
        Assert.Equal(0, goods.FixedParam1);
        Assert.Equal(0, goods.FixedParam2);
        Assert.False(ActionTranslator.TryTranslate(obs,
            new RlAction(RlFunction.Formation, 0, catalogId: RlPacking.ControlAssignBase),
            1, 256, new RlCatalog(), out _));
    }

    [Fact]
    public void RandomizeCivs_PicksTwoPlayableCivs()
    {
        using var env = new RlEnvironment(new RlConfig { Seed = 7, RandomizeCivs = true });
        env.Reset();
        Assert.Contains(env.AgentCiv, RlMapApply.PlayableCivs);
        Assert.Contains(env.OpponentCiv, RlMapApply.PlayableCivs);
        Assert.NotEqual(env.AgentCiv, env.OpponentCiv);
    }

    [Fact]
    public void RmgenMainland_SpawnsWhenDataPresent()
    {
        using var env = new RlEnvironment(new RlConfig
        {
            Seed = 3,
            PrivilegedVision = true,
            MapName = "mainland",
            MapSize = 128,
            TickOpponentAi = false,
            AgentCiv = "gaul",
            OpponentCiv = "rome"
        });
        env.Reset();
        if (!env.LoadedRealMatch) return;
        Assert.True(env.WorldMeters >= 512, "rmgen mainland should enlarge the world past the 256m encounter");
        Assert.Equal("gaul", env.AgentCiv);
        Assert.Equal("rome", env.OpponentCiv);
        Assert.True(CountValid(env.Observation) >= 2);
    }

    [Fact]
    public void EntityMask_MarksAttackOnOwnSeer()
    {
        using var env = new RlEnvironment(new RlConfig
        {
            Seed = 1,
            PrivilegedVision = true,
            UseRealMatch = false
        });
        var obs = env.Reset();
        int self = FirstSelf(obs);
        Assert.True((obs.EntityMask[self] & (1u << (int)RlFunction.Attack)) != 0);
        Assert.True((obs.EntityMask[self] & (1u << (int)RlFunction.Delete)) != 0);
        Assert.Equal(1, obs.FunctionMask[(int)RlFunction.Delete]);
        int enemy = FirstOwner(obs, RlSpec.OwnerRel.Enemy);
        Assert.True(enemy >= 0);
        Assert.True((obs.EntityMask[enemy] & (1u << (int)RlFunction.Attack)) != 0);
        Assert.True((obs.EntityMask[enemy] & (1u << (int)RlFunction.Delete)) != 0);
    }

    [Fact]
    public void RealMatch_FillsResourceChannelAndEntityMask()
    {
        using var env = new RlEnvironment(new RlConfig { Seed = 1, PrivilegedVision = true });
        var obs = env.Reset();
        if (!env.LoadedRealMatch) return;

        int n = RlSpec.SpatialSize;
        int res = 0;
        int baseIdx = RlSpec.Spat.Resource * n * n;
        for (int i = 0; i < n * n; i++)
            res += obs.Spatial[baseIdx + i];
        Assert.True(res > 0, "trees/berries should paint the resource spatial channel");

        bool sawSoldierAttack = false;
        for (int i = 0; i < RlSpec.MaxEntities; i++)
        {
            if (obs.Entity(i, RlSpec.Ent.Valid) == 0) continue;
            if (obs.Entity(i, RlSpec.Ent.OwnerRel) != RlSpec.OwnerRel.Self) continue;
            bool canAttack = (obs.Entity(i, RlSpec.Ent.Flags) & RlSpec.Ent.FlagCanAttack) != 0;
            bool attackBit = (obs.EntityMask[i] & (1u << (int)RlFunction.Attack)) != 0;
            Assert.Equal(canAttack, attackBit);
            if (canAttack && (obs.Entity(i, RlSpec.Ent.Flags) & RlSpec.Ent.FlagBuilding) == 0)
                sawSoldierAttack = true;
        }
        Assert.True(sawSoldierAttack);
    }

    [Fact]
    public void OpponentMove_UsesEnemyRow_NotAgent()
    {
        using var a = new RlEnvironment(SandboxCfg(11));
        using var b = new RlEnvironment(SandboxCfg(11));
        a.Reset();
        var obs = b.Reset();
        int self = FirstSelf(obs);
        int enemy = FirstOwner(obs, RlSpec.OwnerRel.Enemy);
        Assert.True(enemy >= 0);
        var opp = new RlAction(RlFunction.Move, enemy, self, -1, -1);
        for (int i = 0; i < 8; i++)
        {
            a.Step(RlAction.NoOp());
            b.Step(RlAction.NoOp(), opp);
        }
        Assert.NotEqual(Convert.ToHexString(a.Sim.ComputeStateHash()),
            Convert.ToHexString(b.Sim.ComputeStateHash()));
    }

    private static int FirstOwner(RlObservation obs, int rel)
    {
        for (int i = 0; i < RlSpec.MaxEntities; i++)
        {
            if (obs.Entity(i, RlSpec.Ent.Valid) == 0) continue;
            if (obs.Entity(i, RlSpec.Ent.OwnerRel) == rel) return i;
        }
        return -1;
    }

    private static RlConfig SandboxCfg(uint seed) => new()
    {
        Seed = seed,
        CommandDelay = 1,
        StepMul = 1,
        PrivilegedVision = true,
        UseRealMatch = false
    };

    private static int FirstSelf(RlObservation obs)
    {
        for (int i = 0; i < RlSpec.MaxEntities; i++)
        {
            if (obs.Entity(i, RlSpec.Ent.Valid) == 0) continue;
            if (obs.Entity(i, RlSpec.Ent.OwnerRel) == RlSpec.OwnerRel.Self) return i;
        }
        return 0;
    }

    private static int FindRow(RlObservation obs, int templateId, int ownerRel)
    {
        for (int i = 0; i < RlSpec.MaxEntities; i++)
        {
            if (obs.Entity(i, RlSpec.Ent.Valid) == 0) continue;
            if (obs.Entity(i, RlSpec.Ent.TemplateId) != templateId) continue;
            if (obs.Entity(i, RlSpec.Ent.OwnerRel) != ownerRel) continue;
            return i;
        }
        return -1;
    }

    private static bool CatalogContains(RlObservation obs, int row, int kind, int internId)
    {
        for (int i = 0; i < RlSpec.MaxCatalogChoices; i++)
        {
            if (obs.CatalogAt(row, kind, i) == internId) return true;
        }
        return false;
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

    private static bool HasEntityFeat(RlObservation obs, int feat, Func<int, bool> pred)
    {
        for (int i = 0; i < RlSpec.MaxEntities; i++)
        {
            if (obs.Entity(i, RlSpec.Ent.Valid) == 0) continue;
            if (pred(obs.Entity(i, feat))) return true;
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
