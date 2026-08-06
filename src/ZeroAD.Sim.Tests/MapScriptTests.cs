using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Net;
using ZeroAD.Sim.Triggers;
using Xunit;

namespace ZeroAD.Sim.Tests;

// 地图脚本移植件:polar_sea(科技禁用+狼群波次+袭击下单)、elephantine(防御站姿+驻军)。
// 全合成世界(无 junction 依赖);sink 真造实体以便验证命令落点。
public sealed class MapScriptTests
{
    /// <summary>真造实体的测试 sink:生成带 UnitAI/Health/Identity/Garrisonable 的最小单位。</summary>
    private sealed class SpawningSink : ITriggerSink
    {
        public required ComponentManager Cm;
        public readonly List<(string Template, int PlayerId, float X, float Z)> Spawned = new();

        public void ShowMessage(string text) { }

        public IReadOnlyList<EntityId> SpawnEntities(string template, int playerId, float x, float z, int count, float spread)
        {
            var result = new List<EntityId>();
            for (int i = 0; i < count; i++)
            {
                var e = Cm.CreateEntity();
                var pos = new PositionComponent();
                Cm.AddComponent(e, pos);
                pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
                    ZeroAD.Sim.Maths.Fixed.FromFloat(x), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromFloat(z));
                Cm.AddComponent(e, new UnitAIComponent());
                Cm.AddComponent(e, new HealthComponent { Current = 60, Max = 60 });
                var id = new IdentityComponent { TemplateName = template, IsUnit = true };
                id.Classes.Add("Unit");
                Cm.AddComponent(e, id);
                var atk = new AttackComponent { Range = 2f };
                Cm.AddComponent(e, atk);
                atk.Damage.Amounts[DamageType.Hack] = 5;
                Cm.AddComponent(e, new GarrisonableComponent { Size = 1 });
                Cm.AddComponent(e, new OwnershipComponent { PlayerId = playerId });
                Cm.NotifyEntityCreated(e);
                Cm.NotifyOwnerChanged(e, -1, playerId);
                var p = new ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z);
                Cm.NotifyPositionChanged(e, p, p);
                result.Add(e);
                Spawned.Add((template, playerId, x, z));
            }
            return result;
        }
    }

    private static ComponentManager SetupWorld()
    {
        var cm = new ComponentManager(rngSeed: 1);
        SimSystem.Init(cm);
        for (int pid = 1; pid <= 2; pid++)
        {
            var pe = cm.CreateEntity();
            cm.AddComponent(pe, new PlayerComponent());
            cm.AddComponent(pe, new TechnologyManager());
            cm.Players.AddPlayer(pid, pe);
        }
        var range = new RangeManager(cm, ZeroAD.Sim.Maths.Fixed.FromInt(256), ZeroAD.Sim.Maths.Fixed.FromInt(256));
        SimSystem.SetRangeManager(range);
        var net = new NetTurnManager(cm, commandDelay: 2, localPlayerId: 1,
            NetRole.Standalone, expectedPlayers: new HashSet<uint> { 1 });
        SimSystem.SetNet(net);
        return cm;
    }

    private static EntityId MakePlayerUnit(ComponentManager cm, int owner, float x, float z)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromFloat(x), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromFloat(z));
        var id = new IdentityComponent { TemplateName = "units/athen/support_female_citizen", IsUnit = true };
        id.Classes.AddRange(new[] { "Unit", "Organic", "Citizen" });
        cm.AddComponent(e, id);
        cm.AddComponent(e, new HealthComponent { Current = 50, Max = 50 });
        cm.AddComponent(e, new UnitAIComponent());
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var p = new ZeroAD.Sim.Maths.FixedVector2D(pos.Position.X, pos.Position.Z);
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    [Fact]
    public void PolarSea_OnInit_DisablesLumberingTechs()
    {
        var cm = SetupWorld();
        var script = new PolarSeaScript();
        script.OnInit(cm);

        foreach (int pid in new[] { 1, 2 })
        {
            var pe = cm.Players.GetPlayerEntity(pid)!;
            var tm = cm.QueryInterface<TechnologyManager>(pe.Entity)!;
            Assert.True(tm.IsTechDisabled("gather_lumbering_ironaxes"));
            Assert.True(tm.IsTechDisabled("gather_wicker_baskets"));
            Assert.False(tm.CanResearch("gather_lumbering_ironaxes"));
        }
    }

    [Fact]
    public void PolarSea_WaveSpawnsWolves_AndOrdersAttack()
    {
        var cm = SetupWorld();
        var sink = new SpawningSink { Cm = cm };
        cm.Triggers.Sink = sink;
        cm.Triggers.RegisterTriggerPoint("A",
            new ZeroAD.Sim.Maths.FixedVector2D(ZeroAD.Sim.Maths.Fixed.FromInt(50), ZeroAD.Sim.Maths.Fixed.FromInt(50)));
        var prey = MakePlayerUnit(cm, 1, 55, 50);   // 触发点附近

        var script = new PolarSeaScript();
        cm.Triggers.MapScript = script;
        // 首波 5 分钟 = 3000 回合;推 3100 拍。
        for (int i = 0; i < 3100; i++)
            cm.Triggers.Tick(cm, 0.1f);

        Assert.NotEmpty(sink.Spawned);
        Assert.Equal("gaia/fauna_wolf_arctic_violent", sink.Spawned[0].Template);
        Assert.Equal(0, sink.Spawned[0].PlayerId);   // gaia
        Assert.InRange(sink.Spawned.Count, 1, 3);    // 波次规模 1..3

        // 命令经 AI 通道(currentTurn+2 批次)→ 推进三回合落地 → 狼应收到 Attack 订单。
        SimSystem.Net!.AdvanceTurn();
        SimSystem.Net!.AdvanceTurn();
        SimSystem.Net!.AdvanceTurn();
        var wolf = cm.AllEntities.First(e =>
            cm.QueryInterface<IdentityComponent>(e)?.TemplateName == "gaia/fauna_wolf_arctic_violent");
        var wolfAi = cm.QueryInterface<UnitAIComponent>(wolf)!;
        wolfAi.Tick(0.1f, cm);   // 订单派发
        Assert.Equal("Attack", wolfAi.CurrentOrder?.Type);
        Assert.Equal(prey, wolfAi.CurrentOrder!.Target);
    }

    [Fact]
    public void Elephantine_OnInit_DefensiveStance_AndGarrisonsTower()
    {
        var cm = SetupWorld();
        var sink = new SpawningSink { Cm = cm };
        cm.Triggers.Sink = sink;

        // gaia 士兵(应改 defensive)。
        var soldier = cm.CreateEntity();
        var sp = new PositionComponent();
        cm.AddComponent(soldier, sp);
        sp.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromInt(20), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromInt(20));
        var sid = new IdentityComponent { TemplateName = "units/kush/infantry_spearman_b", IsUnit = true };
        sid.Classes.AddRange(new[] { "Unit", "Soldier" });
        cm.AddComponent(soldier, sid);
        cm.AddComponent(soldier, new UnitAIComponent());
        cm.AddComponent(soldier, new OwnershipComponent { PlayerId = 0 });
        cm.NotifyEntityCreated(soldier);
        cm.NotifyOwnerChanged(soldier, -1, 0);
        var spp = new ZeroAD.Sim.Maths.FixedVector2D(sp.Position.X, sp.Position.Z);
        cm.NotifyPositionChanged(soldier, spp, spp);

        // gaia 塔(带驻军位)。
        var tower = cm.CreateEntity();
        var tp = new PositionComponent();
        cm.AddComponent(tower, tp);
        tp.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromInt(30), ZeroAD.Sim.Maths.Fixed.Zero, ZeroAD.Sim.Maths.Fixed.FromInt(30));
        var tid = new IdentityComponent { TemplateName = "structures/kush/defense_tower", IsBuilding = true };
        tid.Classes.AddRange(new[] { "Structure", "Tower" });
        cm.AddComponent(tower, tid);
        var holder = new GarrisonHolderComponent { Max = 5 };
        cm.AddComponent(tower, holder);
        holder.AllowedClasses.Add("Unit");
        cm.AddComponent(tower, new OwnershipComponent { PlayerId = 0 });
        cm.NotifyEntityCreated(tower);
        cm.NotifyOwnerChanged(tower, -1, 0);
        var tpp = new ZeroAD.Sim.Maths.FixedVector2D(tp.Position.X, tp.Position.Z);
        cm.NotifyPositionChanged(tower, tpp, tpp);

        var script = new ElephantineScript();
        script.OnInit(cm);

        Assert.Equal("defensive", cm.QueryInterface<UnitAIComponent>(soldier)!.Stance);
        Assert.Single(holder.Entities);   // 塔内驻进 1 名
    }
}
