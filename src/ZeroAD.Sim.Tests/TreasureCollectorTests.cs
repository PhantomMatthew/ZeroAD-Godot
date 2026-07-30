using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

// TreasureCollector + Treasure — ports of TreasureCollector.js / Treasure.js. A collector
// stands in range for the treasure's CollectTime, then the treasure grants its resources to
// the collector's owner and is destroyed (MT_Fogging/StatisticsTracker/Trigger 通知略)。
public sealed class TreasureCollectorTests
{
    private static EntityId MakeUnit(ComponentManager cm, int player = 1, float x = 0f, float z = 0f)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new UnitMotion());
        cm.AddComponent(e, new IdentityComponent());
        if (player >= 0)
            cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        return e;
    }

    private static EntityId MakeTreasure(ComponentManager cm, float x, float z,
        float collectTime = 1f, int food = 0, int wood = 0)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new TreasureComponent
        {
            CollectTimeSec = collectTime,
            Food = food,
            Wood = wood,
        });
        return e;
    }

    private static PlayerComponent AddPlayer(ComponentManager cm, int playerId)
    {
        var pe = cm.CreateEntity();
        var pc = new PlayerComponent();
        cm.AddComponent(pe, pc);
        // 赋值须在 AddComponent 之后(OnInit 重置 300/300/200/100 陷阱)。
        pc.Wood = 0; pc.Food = 100; pc.Stone = 0; pc.Metal = 0;
        cm.Players.AddPlayer(playerId, pe);
        return pc;
    }

    [Fact]
    public void CanCollect_RequiresAvailableTreasure()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);
        var treasure = MakeTreasure(cm, 3f, 0f);
        var tc = new TreasureCollectorComponent { MaxDistance = 24f };
        cm.AddComponent(unit, tc);

        Assert.True(tc.CanCollect(cm, treasure));
        Assert.False(tc.CanCollect(cm, unit));          // 无 Treasure 件

        cm.QueryInterface<TreasureComponent>(treasure)!.IsTaken = true;
        Assert.False(tc.CanCollect(cm, treasure));      // 已被取走
    }

    [Fact]
    public void Tick_BeforeCollectTime_NoReward()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var unit = MakeUnit(cm, x: 0f);
        var treasure = MakeTreasure(cm, 3f, 0f, collectTime: 1f, food: 50);
        var tc = new TreasureCollectorComponent { MaxDistance = 24f };
        cm.AddComponent(unit, tc);

        Assert.True(tc.StartCollecting(cm, treasure));
        Assert.Equal(CollectTickResult.Collecting, tc.Tick(0.5f, cm));
        Assert.NotNull(cm.QueryInterface<TreasureComponent>(treasure));   // 未提前结算
    }

    [Fact]
    public void Tick_AfterCollectTime_GrantsResourcesAndDestroys()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var player = AddPlayer(cm, 1);
        var unit = MakeUnit(cm, x: 0f);
        var treasure = MakeTreasure(cm, 3f, 0f, collectTime: 1f, food: 50, wood: 25);
        var tc = new TreasureCollectorComponent { MaxDistance = 24f };
        cm.AddComponent(unit, tc);

        tc.StartCollecting(cm, treasure);
        Assert.Equal(CollectTickResult.Done, tc.Tick(1.1f, cm));

        Assert.Equal(150, player.Food);                 // 100 + 50
        Assert.Equal(25, player.Wood);
        Assert.Null(cm.QueryInterface<TreasureComponent>(treasure));   // 已销毁
        Assert.Null(tc.Treasure);
    }

    [Fact]
    public void Tick_TreasureTakenByOther_MidCollect_TargetInvalid()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        AddPlayer(cm, 2);
        var unit = MakeUnit(cm, x: 0f);
        var other = MakeUnit(cm, player: 2, x: 1f);
        var treasure = MakeTreasure(cm, 3f, 0f, collectTime: 10f, food: 50);
        var tc = new TreasureCollectorComponent { MaxDistance = 24f };
        cm.AddComponent(unit, tc);

        tc.StartCollecting(cm, treasure);
        tc.Tick(0.5f, cm);
        // 另一人先取走(销毁实体)。
        cm.QueryInterface<TreasureComponent>(treasure)!.Reward(cm, other);

        Assert.Equal(CollectTickResult.TargetInvalid, tc.Tick(9.6f, cm));
        Assert.Null(tc.Treasure);
    }

    [Fact]
    public void Tick_OutOfRangeAtFire_OutOfRange()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var unit = MakeUnit(cm, x: 0f);
        var treasure = MakeTreasure(cm, 3f, 0f, collectTime: 0.5f, food: 50);
        var tc = new TreasureCollectorComponent { MaxDistance = 4f };
        cm.AddComponent(unit, tc);

        tc.StartCollecting(cm, treasure);
        // 结算前单位被挪出射程。
        cm.QueryInterface<PositionComponent>(unit)!.Position =
            new FixedVector3D(Fixed.FromFloat(30f), Fixed.Zero, Fixed.Zero);
        Assert.Equal(CollectTickResult.OutOfRange, tc.Tick(0.6f, cm));
        Assert.NotNull(cm.QueryInterface<TreasureComponent>(treasure));   // 未结算
    }

    [Fact]
    public void RoundTrip_PreservesProgress()
    {
        var tc = new TreasureCollectorComponent
        {
            MaxDistance = 12.5f,
            Treasure = new EntityId(9),
            CollectTime = 2.5f,
            Elapsed = 0.75f,
        };
        var ms = new System.IO.MemoryStream();
        tc.Serialize(new Serialization.BinarySerializer(new System.IO.BinaryWriter(ms)));
        ms.Position = 0;
        var back = new TreasureCollectorComponent();
        back.Deserialize(new Serialization.BinaryDeserializer(new System.IO.BinaryReader(ms)));

        Assert.Equal(12.5f, back.MaxDistance, 3);
        Assert.Equal(new EntityId(9), back.Treasure);
        Assert.Equal(2.5f, back.CollectTime, 3);
        Assert.Equal(0.75f, back.Elapsed, 3);
    }

    // --- UnitAI 集成 ---

    [Fact]
    public void UnitAI_CollectTreasureOrder_CollectsAndFinishes()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var player = AddPlayer(cm, 1);
        var unit = MakeUnit(cm, x: 0f);
        var treasure = MakeTreasure(cm, 5f, 0f, collectTime: 1f, food: 50);
        cm.AddComponent(unit, new TreasureCollectorComponent { MaxDistance = 24f });
        var ai = new UnitAIComponent();
        cm.AddComponent(unit, ai);

        ai.CollectTreasure(treasure);
        ai.Tick(0.1f, cm);                          // 派发:射程内 → 直接 COLLECTING
        Assert.Equal("INDIVIDUAL.COLLECTTREASURE.COLLECTING", ai.FsmStateName);

        for (int i = 0; i < 30 && !ai.IsIdle; i++)
            ai.Tick(0.1f, cm);

        Assert.True(ai.IsIdle);
        Assert.Equal(150, player.Food);
        Assert.Null(cm.QueryInterface<TreasureComponent>(treasure));
    }

    [Fact]
    public void UnitAI_CollectTreasureRejected_WithoutCollector_NoFsmCrash()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var unit = MakeUnit(cm);                    // 无 TreasureCollectorComponent → 拒收
        var treasure = MakeTreasure(cm, 5f, 0f);
        var ai = new UnitAIComponent();
        cm.AddComponent(unit, ai);

        ai.CollectTreasure(treasure);
        ai.Tick(0.1f, cm);
        ai.Tick(0.1f, cm);

        Assert.EndsWith("IDLE", ai.FsmStateName);
    }
}
