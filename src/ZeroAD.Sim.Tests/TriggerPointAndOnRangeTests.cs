using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;
using ZeroAD.Sim.Triggers;
using Xunit;

namespace ZeroAD.Sim.Tests;

// WS5 触发器收尾:OnInitGame 派发、触发点实体化注册 + 序列化骑缝、
// TriggerPoint 组件自动摄入/销毁移除、OnRange 主动查询增量。
public sealed class TriggerPointAndOnRangeTests
{
    private static readonly Fixed Dt = Fixed.FromFloat(0.1f);

    private sealed class RecordingSink : ITriggerSink
    {
        public readonly List<string> Messages = new();
        public void ShowMessage(string text) => Messages.Add(text);
        public IReadOnlyList<EntityId> SpawnEntities(string template, int playerId,
            float x, float z, int count, float spread) => System.Array.Empty<EntityId>();
    }

    private static ComponentManager SetupWorld()
    {
        var cm = new ComponentManager(rngSeed: 1);
        SimSystem.Init(cm);
        var p1 = cm.CreateEntity();
        cm.AddComponent(p1, new PlayerComponent());
        cm.Players.AddPlayer(1, p1);
        var range = new RangeManager(cm, Fixed.FromInt(256), Fixed.FromInt(256));
        SimSystem.SetRangeManager(range);
        return cm;
    }

    private static EntityId MakeUnit(ComponentManager cm, int owner, float x, float z,
        params string[] classes)
    {
        var e = cm.CreateEntity();
        var posComp = new PositionComponent();
        cm.AddComponent(e, posComp);
        var id = new IdentityComponent { Name = "U", IsUnit = true };
        id.Classes.AddRange(classes);
        cm.AddComponent(e, id);
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.NotifyEntityCreated(e);
        cm.NotifyOwnerChanged(e, -1, owner);
        var fx = Fixed.FromFloat(x);
        var fz = Fixed.FromFloat(z);
        posComp.Position = new FixedVector3D(fx, Fixed.Zero, fz);
        var pos = new FixedVector2D(fx, fz);
        cm.NotifyPositionChanged(e, pos, pos);
        return e;
    }

    private static EntityId MakeTriggerPoint(ComponentManager cm, string reference,
        float x, float z)
    {
        var e = cm.CreateEntity();
        var posComp = new PositionComponent();
        cm.AddComponent(e, posComp);
        var fx = Fixed.FromFloat(x);
        var fz = Fixed.FromFloat(z);
        posComp.Position = new FixedVector3D(fx, Fixed.Zero, fz);
        cm.NotifyEntityCreated(e);
        var pos = new FixedVector2D(fx, fz);
        cm.NotifyPositionChanged(e, pos, pos);
        cm.Triggers.RegisterTriggerPoint(reference, e);
        return e;
    }

    private static TriggerAction ShowMessage(string text)
    {
        var a = new TriggerAction { Type = "ShowMessage" };
        a.Params["Text"] = text;
        return a;
    }

    // ── OnInitGame 派发 ──

    [Fact]
    public void NotifyInitGame_DispatchesRegisteredTriggers_OnceSemanticsHold()
    {
        var cm = SetupWorld();
        var sink = new RecordingSink();
        var ts = new TriggerSystem { Sink = sink };
        ts.AddEventTrigger("OnInitGame", new TriggerDefinition
        {
            Name = "init_once", Once = true,
            Actions = { ShowMessage("init fired") },
        });

        ts.NotifyInitGame(cm);
        Assert.Single(sink.Messages);
        Assert.Equal("init fired", sink.Messages[0]);

        ts.NotifyInitGame(cm);   // Once:第二次不再触发
        Assert.Single(sink.Messages);
    }

    // ── 触发点注册表(实体存储 + 坐标解析)──

    [Fact]
    public void TriggerPoints_ResolvePositionsViaPositionComponent()
    {
        var cm = SetupWorld();
        var p1 = MakeTriggerPoint(cm, "A", 50, 60);
        var p2 = MakeTriggerPoint(cm, "A", 100, 110);

        Assert.Equal(new[] { p1, p2 }, cm.Triggers.GetTriggerPointEntities("A"));
        var positions = cm.Triggers.GetTriggerPoints(cm, "A");
        Assert.Equal(2, positions.Count);
        Assert.Equal(50f, positions[0].X.ToFloat(), 3);
        Assert.Equal(60f, positions[0].Y.ToFloat(), 3);
        Assert.Empty(cm.Triggers.GetTriggerPointEntities("MISSING"));
    }

    // ── 触发点序列化 round-trip(v20)──

    [Fact]
    public void TriggerPoints_SerializeRoundTrip()
    {
        var ts = new TriggerSystem();
        ts.Add(new TriggerDefinition { Name = "t1", Enabled = false });
        ts.RegisterTriggerPoint("B", new EntityId(9));
        ts.RegisterTriggerPoint("A", new EntityId(5));
        ts.RegisterTriggerPoint("A", new EntityId(3));

        var cap = new CapturingSerializer();
        ts.Serialize(cap);

        var ts2 = new TriggerSystem();
        ts2.Deserialize(new ReplayingDeserializer(cap));

        Assert.Single(ts2.Triggers);
        Assert.Equal("t1", ts2.Triggers[0].Name);
        Assert.False(ts2.Triggers[0].Enabled);
        Assert.Equal(new[] { new EntityId(3), new EntityId(5) },
            ts2.GetTriggerPointEntities("A"));
        Assert.Equal(new[] { new EntityId(9) }, ts2.GetTriggerPointEntities("B"));
    }

    [Fact]
    public void TriggerPoints_Deserialize_LegacyV19Save_LeavesRegistryEmpty()
    {
        // v19 档只有触发器段,无触发点尾段:LoadedVersion<20 → 不读尾段,注册表置空。
        var cap = new CapturingSerializer();
        cap.NumberI32("count", 0);
        var ts = new TriggerSystem();
        ts.RegisterTriggerPoint("A", new EntityId(1));   // 预置脏数据应被清掉
        uint previous = SaveFormat.LoadedVersion;
        SaveFormat.LoadedVersion = 19;
        try
        {
            ts.Deserialize(new ReplayingDeserializer(cap));
        }
        finally
        {
            SaveFormat.LoadedVersion = previous;
        }
        Assert.Empty(ts.Triggers);
        Assert.Empty(ts.GetTriggerPointEntities("A"));
    }

    // ── TriggerPoint 组件自动摄入 + 销毁移除 ──

    [Fact]
    public void AssembleUnit_WithTriggerPointTemplate_AutoRegisters_AndDestroyRemoves()
    {
        var cm = SetupWorld();
        var stats = new Content.TemplateStats
        {
            Name = "Trigger Point A",
            TriggerPointReference = "A",
        };
        var e = cm.CreateEntity();
        EntityAssembler.AssembleUnit(cm, e, "trigger/trigger_point_A", stats, 40, 70);

        var comp = cm.QueryInterface<TriggerPointComponent>(e);
        Assert.NotNull(comp);
        Assert.Equal("A", comp!.Reference);
        Assert.Equal(new[] { e }, cm.Triggers.GetTriggerPointEntities("A"));
        var positions = cm.Triggers.GetTriggerPoints(cm, "A");
        Assert.Single(positions);
        Assert.Equal(40f, positions[0].X.ToFloat(), 3);

        // 组件序列化 round-trip(Reference 字符串)。
        var cap = new CapturingSerializer();
        comp.Serialize(cap);
        var comp2 = new TriggerPointComponent();
        comp2.Deserialize(new ReplayingDeserializer(cap));
        Assert.Equal("A", comp2.Reference);

        // 销毁 → 注册表移除(原版 TriggerPoint.OnDestroy)。
        cm.DestroyEntity(e);
        Assert.Empty(cm.Triggers.GetTriggerPointEntities("A"));
    }

    [Fact]
    public void AttachTriggerPoint_IsIdempotent()
    {
        var cm = SetupWorld();
        var e = cm.CreateEntity();
        EntityAssembler.AttachTriggerPoint(cm, e, "A");
        EntityAssembler.AttachTriggerPoint(cm, e, "A");
        Assert.Equal(new[] { e }, cm.Triggers.GetTriggerPointEntities("A"));
    }

    // ── OnRange 主动查询增量 ──

    [Fact]
    public void RangeManager_ActiveQuery_ProducesAddedRemovedDeltas()
    {
        var cm = SetupWorld();
        var range = SimSystem.Range!;
        var source = MakeTriggerPoint(cm, "A", 50, 50);
        var unit = MakeUnit(cm, 1, 55, 50, "Unit");

        int tag = range.CreateActiveQuery(source, Fixed.Zero, Fixed.FromInt(20),
            new List<int> { 1 }, "Unit");

        // 首次:空 → 全部匹配为 added。
        var updates = range.UpdateActiveQueries();
        var u = Assert.Single(updates);
        Assert.Equal(tag, u.Tag);
        Assert.Equal(new[] { unit }, u.Added);
        Assert.Empty(u.Removed);
        Assert.Equal(new[] { unit }, u.Current);

        // 无变化 → 无增量。
        Assert.Empty(range.UpdateActiveQueries());

        // 移出范围 → removed。
        var pos = cm.QueryInterface<PositionComponent>(unit)!;
        var far = new FixedVector2D(Fixed.FromInt(200), Fixed.FromInt(200));
        pos.Position = new FixedVector3D(far.X, Fixed.Zero, far.Y);
        cm.NotifyPositionChanged(unit, new FixedVector2D(Fixed.FromInt(55), Fixed.FromInt(50)), far);
        updates = range.UpdateActiveQueries();
        u = Assert.Single(updates);
        Assert.Empty(u.Added);
        Assert.Equal(new[] { unit }, u.Removed);
        Assert.Empty(u.Current);

        // 禁用 → 不重跑;重新启用 → 与停用前存量 diff。
        range.DisableActiveQuery(tag);
        Assert.Empty(range.UpdateActiveQueries());
        range.EnableActiveQuery(tag);
        var back = new FixedVector2D(Fixed.FromInt(52), Fixed.FromInt(50));
        pos.Position = new FixedVector3D(back.X, Fixed.Zero, back.Y);
        cm.NotifyPositionChanged(unit, far, back);
        updates = range.UpdateActiveQueries();
        u = Assert.Single(updates);
        Assert.Equal(new[] { unit }, u.Added);
    }

    [Fact]
    public void TriggerSystem_OnRange_DispatchesAtTurnEnd_WithDeltas()
    {
        var cm = SetupWorld();
        var sink = new RecordingSink();
        var ts = cm.Triggers;
        ts.Sink = sink;
        var source = MakeTriggerPoint(cm, "A", 50, 50);
        var unit = MakeUnit(cm, 1, 55, 50, "Unit");
        ts.AddEventTrigger("OnRange", new TriggerDefinition
        {
            Name = "wave",
            Actions = { ShowMessage("range changed") },
        });
        ts.AddRangeTrigger(cm, source, "wave", Fixed.Zero, Fixed.FromInt(20),
            new List<int> { 1 }, "Unit");

        // 首个 Tick:unit 进入集合 → added → 派发。
        ts.Tick(cm, Dt);
        Assert.Single(sink.Messages);

        // 无变化 → 不派发。
        ts.Tick(cm, Dt);
        Assert.Single(sink.Messages);

        // 移出 → removed → 派发。
        var pos = cm.QueryInterface<PositionComponent>(unit)!;
        var far = new FixedVector2D(Fixed.FromInt(200), Fixed.FromInt(200));
        pos.Position = new FixedVector3D(far.X, Fixed.Zero, far.Y);
        cm.NotifyPositionChanged(unit, new FixedVector2D(Fixed.FromInt(55), Fixed.FromInt(50)), far);
        ts.Tick(cm, Dt);
        Assert.Equal(2, sink.Messages.Count);

        // 名字不匹配的 OnRange 触发器不吃这次增量。
        ts.AddEventTrigger("OnRange", new TriggerDefinition
        {
            Name = "other",
            Actions = { ShowMessage("other fired") },
        });
        var back = new FixedVector2D(Fixed.FromInt(52), Fixed.FromInt(50));
        pos.Position = new FixedVector3D(back.X, Fixed.Zero, back.Y);
        cm.NotifyPositionChanged(unit, far, back);
        ts.Tick(cm, Dt);
        Assert.Equal(3, sink.Messages.Count);
        Assert.DoesNotContain("other fired", sink.Messages);
    }
}
