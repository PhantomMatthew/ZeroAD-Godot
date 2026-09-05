using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

// RallyPoint 集结点收尾(WS1):易主清空、EntityRenamed 改指/迁移、卸载走全队列。
// 对照原版 simulation/components/RallyPoint.js(OnOwnershipChanged / OnGlobalEntityRenamed /
// OrderToRallyPoint)与 helpers/RallyPointCommands.js。
public sealed class RallyPointTests
{
    private static EntityId MakeBuilding(ComponentManager cm, int player = 1, float x = 0f, float z = 0f,
        bool withRally = true)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        if (withRally) cm.AddComponent(e, new RallyPointComponent());
        return e;
    }

    private static EntityId MakeUnit(ComponentManager cm, int player = 1, float x = 0f, float z = 0f)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        var motion = new UnitMotion();
        cm.AddComponent(e, motion);
        motion.Speed = Fixed.FromFloat(3f);
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        cm.AddComponent(e, new GarrisonableComponent { Size = 1 });
        var health = new HealthComponent();
        cm.AddComponent(e, health);
        health.Current = 100; health.Max = 100;                 // OnInit 清空后赋值(防 clobber)
        cm.AddComponent(e, new UnitAIComponent());
        return e;
    }

    private static PlayerComponent AddPlayer(ComponentManager cm, int playerId)
    {
        var pe = cm.CreateEntity();
        var pc = new PlayerComponent();
        cm.AddComponent(pe, pc);
        pc.Wood = 0; pc.Food = 0; pc.Stone = 0; pc.Metal = 0;
        cm.AddComponent(pe, new DiplomacyComponent());
        cm.Players.AddPlayer(playerId, pe);
        return pc;
    }

    [Fact]
    public void OwnerChange_ClearsQueue_ExceptInvalidPlayerTransitions()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        AddPlayer(cm, 2);
        var building = MakeBuilding(cm);
        var rally = cm.QueryInterface<RallyPointComponent>(building)!;

        rally.AddPosition(new FixedVector2D(Fixed.FromInt(10), Fixed.FromInt(10)), 1);
        Assert.True(rally.HasPositions(1));

        // 原版豁免(RallyPoint.js:149-156):构造(from=-1)/析构(to=-1)易主不清。
        cm.NotifyOwnerChanged(building, -1, 1);
        Assert.True(rally.HasPositions(1));
        cm.NotifyOwnerChanged(building, 1, -1);
        Assert.True(rally.HasPositions(1));

        // 别家实体易主与本建筑无关。
        var other = MakeBuilding(cm, player: 2, x: 30f);
        cm.NotifyOwnerChanged(other, 2, 1);
        Assert.True(rally.HasPositions(1));

        // 真正易主(1→2)→ 清空全队列。
        cm.NotifyOwnerChanged(building, 1, 2);
        Assert.False(rally.HasPositions(1));
        Assert.False(rally.HasAnyPositions);
    }

    [Fact]
    public void EntityRenamed_RetargetsData_AndMigratesQueueForRenamedBuilding()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var building = MakeBuilding(cm);
        var rally = cm.QueryInterface<RallyPointComponent>(building)!;
        var target = MakeUnit(cm, x: 40f);
        var source = MakeBuilding(cm, x: 50f);

        rally.AddPosition(new FixedVector2D(Fixed.FromInt(40), Fixed.FromInt(0)), 1);
        rally.AddData(new RallyPointComponent.RallyPointData
        { Command = "trade", Target = target.Value, Source = source.Value }, 1);

        // 目标/来源换号 → 全玩家队列 Data 改指新号。
        var target2 = MakeUnit(cm, x: 41f);
        cm.Events.RaiseEntityRenamed(new EntityRenamedEvent { OldEntity = target, NewEntity = target2 });
        var data = rally.GetData(1);
        Assert.Equal(target2.Value, data[0].Target);
        Assert.Equal(source.Value, data[0].Source);              // 未改名者不动

        // 建筑自身换名(晋升/变身)且新实体带 RallyPoint → 整条队列迁移。
        var promoted = MakeBuilding(cm, x: 0f);
        cm.Events.RaiseEntityRenamed(new EntityRenamedEvent { OldEntity = building, NewEntity = promoted });
        var newRally = cm.QueryInterface<RallyPointComponent>(promoted)!;
        Assert.True(newRally.HasPositions(1));
        var migratedData = newRally.GetData(1);
        Assert.Single(migratedData);
        Assert.Equal("trade", migratedData[0].Command);
        Assert.Equal(target2.Value, migratedData[0].Target);     // 迁移的是改指后的值

        // 新实体无 RallyPoint → 不迁移(原版 cmpRallyPointNew 空判)。
        var rallyless = cm.CreateEntity();
        cm.AddComponent(rallyless, new PositionComponent());
        var building2 = MakeBuilding(cm, x: 60f);
        var rally2 = cm.QueryInterface<RallyPointComponent>(building2)!;
        rally2.AddPosition(new FixedVector2D(Fixed.FromInt(5), Fixed.FromInt(5)), 1);
        cm.Events.RaiseEntityRenamed(new EntityRenamedEvent { OldEntity = building2, NewEntity = rallyless });
        Assert.True(rally2.HasPositions(1));                     // 旧队列保留(实体将毁,由清扫管)
    }

    [Fact]
    public void UnGarrison_WalksFullRallyQueue_NotSinglePoint()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var holder = MakeBuilding(cm);
        var gh = new GarrisonHolderComponent { Max = 4, LoadingRange = 2f };
        cm.AddComponent(holder, gh);
        gh.AllowedClasses.Add("Infantry");
        var rally = cm.QueryInterface<RallyPointComponent>(holder)!;

        // 两点集结队列(原版 Shift+点击多点排队)。
        rally.AddPosition(new FixedVector2D(Fixed.FromInt(10), Fixed.FromInt(10)), 1);
        rally.AddPosition(new FixedVector2D(Fixed.FromInt(20), Fixed.FromInt(20)), 1);

        var unit = MakeUnit(cm);
        var id = new IdentityComponent();
        cm.AddComponent(unit, id);
        id.Classes.Add("Infantry");
        var g = cm.QueryInterface<GarrisonableComponent>(unit)!;
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;

        Assert.True(g.Garrison(cm, holder));
        Assert.True(g.UnGarrison(cm));

        // 卸载走全队列:两个 Walk 依序入队(非单点退化);指向持有者自身的
        // "garrison" 点才跳过(本例无)。
        var orders = ai.OrderQueueSnapshot;
        Assert.Equal(2, orders.Count);
        Assert.Equal("Walk", orders[0].Type);
        Assert.Equal("Walk", orders[1].Type);
        Assert.Equal(Fixed.FromInt(10), orders[0].Position.X);
        Assert.Equal(Fixed.FromInt(10), orders[0].Position.Y);
        Assert.Equal(Fixed.FromInt(20), orders[1].Position.X);
        Assert.Equal(Fixed.FromInt(20), orders[1].Position.Y);
    }
}
