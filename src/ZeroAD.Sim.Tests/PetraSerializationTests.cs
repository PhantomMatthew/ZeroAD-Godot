using System.IO;
using System.Linq;
using Xunit;
using ZeroAD.Sim.AI.Petra;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Tests;

// AI 存档序列化(PORTING-GAPS §10 缺口):AttackPlan/AttackManager/TransportPlan/
// NavalManager/QueueManager/DefenseManager 骑缝往返——读档后计划状态不丢。
public sealed class PetraSerializationTests
{
    private static (byte[] data, T result) RoundTrip<T>(System.Action<ZeroAD.Sim.Serialization.ISerializer> write,
        System.Func<ZeroAD.Sim.Serialization.IDeserializer, T> read)
    {
        using var ms = new MemoryStream();
        write(new BinarySerializer(new BinaryWriter(ms)));
        ms.Position = 0;
        return (ms.ToArray(), read(new BinaryDeserializer(new BinaryReader(ms))));
    }

    [Fact]
    public void QueueManager_PlanRoundTrip_PreservesTrainingPlanMetadata()
    {
        var w = PetraEconomyFixtures.NewAiWorld();
        if (w == null) return;
        var hq = w.Hq;
        hq.Queues.AddPlan("villager", new TrainingPlan(w.Gs, "units/gaul/support_civilian",
            new System.Collections.Generic.Dictionary<string, object>
            { ["plan"] = 7, ["special"] = "Plan_7_Infantry", ["base"] = 0 }, 3, 5));

        var (_, result) = RoundTrip<QueueManager>(
            s => hq.Queues.Serialize(s),
            d =>
            {
                var qm = new QueueManager(new PetraConfig());
                qm.Deserialize(d, w.Gs);
                return qm;
            });

        var q = result.GetQueue("villager")!;
        Assert.Single(q.Plans);
        var plan = q.Plans[0] as TrainingPlan;
        Assert.NotNull(plan);
        Assert.Equal("units/gaul/support_civilian", plan!.Type);
        Assert.Equal(3, plan.Number);
        Assert.Equal(7, plan.Metadata["plan"]);
        Assert.Equal("Plan_7_Infantry", plan.Metadata["special"]);
    }

    [Fact]
    public void AttackPlan_RoundTrip_PreservesStateAndBuildOrders()
    {
        var w = PetraEconomyFixtures.NewAiWorld();
        if (w == null) return;
        var config = new PetraConfig(DifficultyLevel.Medium);
        var plan = new AttackPlan(w.Gs, 42, AttackPlan.TypeDefault, config);
        plan.Init(w.Gs, w.Hq.Queues);
        plan.SetInitialRallyPoint(w.Gs);
        plan.UnitCollection.Add(1001);
        plan.UnitCollection.Add(1002);

        var (_, result) = RoundTrip<AttackPlan>(
            s => plan.Serialize(s),
            d => AttackPlan.Deserialize(d, w.Gs, config));

        Assert.Equal(42, result.Name);
        Assert.Equal(AttackPlan.TypeDefault, result.Type);
        Assert.Equal(2, result.UnitCollection.Count);
        Assert.Contains(1001u, result.UnitCollection);
        Assert.NotEmpty(result.BuildOrders);
        Assert.Equal(plan.BuildOrders.Count, result.BuildOrders.Count);
        Assert.Equal(plan.BuildOrders[0].Stats.TargetSize, result.BuildOrders[0].Stats.TargetSize);
    }

    [Fact]
    public void DefenseArmy_RoundTrip_PreservesArmiesAndAssignments()
    {
        var w = PetraEconomyFixtures.NewAiWorld();
        if (w == null) return;
        // 敌方目标(无模板装配的轻量实体:位置+属主即可)。
        var enemy = w.Cm.CreateEntity();
        var epos = new PositionComponent();
        w.Cm.AddComponent(enemy, epos);
        epos.Position = new ZeroAD.Sim.Maths.FixedVector3D(
            ZeroAD.Sim.Maths.Fixed.FromInt(120), ZeroAD.Sim.Maths.Fixed.Zero,
            ZeroAD.Sim.Maths.Fixed.FromInt(120));
        w.Cm.AddComponent(enemy, new OwnershipComponent { PlayerId = 3 });
        // GameState 只索引有真模板的实体(MakeEntity 过滤)——补身份件。
        w.Cm.AddComponent(enemy, new IdentityComponent
        {
            TemplateName = "units/athen/infantry_spearman_b",
            IsUnit = true,
            Classes = new System.Collections.Generic.List<string> { "Unit" },
        });

        var dm = new DefenseManager(new PetraConfig(DifficultyLevel.Medium));
        dm.MakeIntoArmy(w.Gs, enemy.Value);
        Assert.Single(dm.Armies);

        var (_, result) = RoundTrip<DefenseManager>(
            s => dm.Serialize(s),
            d =>
            {
                var m = new DefenseManager(new PetraConfig(DifficultyLevel.Medium));
                m.Deserialize(d, w.Gs);
                return m;
            });

        Assert.True(result.Armies.Count == 1,
            $"armies={result.Armies.Count} srcFoes={dm.Armies[0].FoeEntities.Count} " +
            $"dstFoes={(result.Armies.Count > 0 ? result.Armies[0].FoeEntities.Count : -1)} " +
            $"srcTargets={dm.TargetList.Count} srcAllies={dm.AttackedAllies.Count}");
        Assert.Equal(enemy.Value, result.Armies[0].FoeEntities[0]);
        Assert.Equal(dm.Armies[0].ID, result.Armies[0].ID);
        // PartOfArmy 元数据随反序列化回填(后续 Update 依此排除已编军单位)。
        Assert.Equal(result.Armies[0].ID, w.Gs.Metadata.GetObject(enemy.Value, "PartOfArmy"));
    }

    [Fact]
    public void TransportPlan_RoundTrip_PreservesVoyage()
    {
        var plan = new TransportPlan(7, 3, 8, 12,
            new ZeroAD.Sim.Maths.FixedVector2D(ZeroAD.Sim.Maths.Fixed.FromInt(500),
                ZeroAD.Sim.Maths.Fixed.FromInt(600)));
        plan.Units.Add(11u);
        plan.Ships.Add(22u);

        var (_, result) = RoundTrip<TransportPlan>(
            s => plan.Serialize(s),
            TransportPlan.Deserialize);

        Assert.Equal(7, result.ID);
        Assert.Equal(3, result.StartIndex);
        Assert.Equal(8, result.EndIndex);
        Assert.Equal(12, result.Sea);
        Assert.Equal(500f, result.EndPos.X.ToFloat());
        Assert.Single(result.Units);
        Assert.Single(result.Ships);
    }
}
