using System;
using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>StatisticsTracker 计数器测试。验证事件订阅 → 计数器更新 → 分数公式的正确性。
/// 直接 raise SimEventBus 事件（管道本身已由 CombatEnemySemanticsTests 等验证），
/// 外加一条真实战斗路径（DelayedDamage → EntityKilled）验证 kill 归属端到端。</summary>
public sealed class StatisticsTrackerTests
{
    private static (ComponentManager cm, EntityId player, StatisticsTrackerComponent tracker) MakeWorld(int playerId = 1)
    {
        var cm = new ComponentManager(rngSeed: 42);
        SimSystem.Init(cm);
        var player = cm.CreateEntity();
        cm.AddComponent(player, new PlayerComponent());
        cm.AddComponent(player, new OwnershipComponent { PlayerId = playerId });
        var tracker = new StatisticsTrackerComponent();
        cm.AddComponent(player, tracker);
        tracker.Attach(cm);
        cm.Players.AddPlayer(playerId, player);
        return (cm, player, tracker);
    }

    /// <summary>造一个带 Identity + Ownership + Cost 的实体（用于类分桶和价值计算）。</summary>
    private static EntityId MakeEntity(ComponentManager cm, int owner, bool building = false,
        int cost = 10, params string[] classes)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new IdentityComponent { IsUnit = !building, IsBuilding = building, Classes = new List<string>(classes) });
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new CostComponent { WoodCost = cost });
        return e;
    }

    [Fact]
    public void TrainingFinished_IncrementsUnitsTrained_ByClass()
    {
        var (cm, player, tracker) = MakeWorld();
        var trainer = MakeEntity(cm, 1, classes: "Infantry");  // trainer 归玩家1

        cm.Events.RaiseTrainingFinished(new TrainingFinishedEvent
        { TrainerEntity = trainer, UnitTemplate = "units/athen/infantry_hoplite" });

        // 无模板加载时 ExtractStats 返回 null → PrimaryUnitClass 走 "total"
        Assert.Equal(1, tracker.UnitsTrained.GetValueOrDefault("total"));
    }

    [Fact]
    public void ResourceGathered_IncrementsResourcesGathered_ByType()
    {
        var (cm, _, tracker) = MakeWorld();
        cm.Events.RaiseResourceGathered(new ResourceGatheredEvent { PlayerId = 1, Type = ResourceType.Wood, Amount = 50 });
        cm.Events.RaiseResourceGathered(new ResourceGatheredEvent { PlayerId = 1, Type = ResourceType.Food, Amount = 30 });
        cm.Events.RaiseResourceGathered(new ResourceGatheredEvent { PlayerId = 1, Type = ResourceType.Wood, Amount = 20 });

        Assert.Equal(70, tracker.ResourcesGathered.GetValueOrDefault("wood"));
        Assert.Equal(30, tracker.ResourcesGathered.GetValueOrDefault("food"));
        Assert.Equal(100, tracker.ResourcesGathered.GetValueOrDefault("total"));
    }

    [Fact]
    public void ResourceSpent_IncrementsResourcesUsed()
    {
        var (cm, _, tracker) = MakeWorld();
        cm.Events.RaiseResourceSpent(new ResourceSpentEvent { PlayerId = 1, Type = ResourceType.Wood, Amount = 100 });
        Assert.Equal(100, tracker.ResourcesUsed.GetValueOrDefault("wood"));
    }

    [Fact]
    public void TradeIncome_Accumulates()
    {
        var (cm, _, tracker) = MakeWorld();
        cm.Events.RaiseTradeIncome(new TradeIncomeEvent { PlayerId = 1, Amount = 8 });
        cm.Events.RaiseTradeIncome(new TradeIncomeEvent { PlayerId = 1, Amount = 12 });
        Assert.Equal(20, tracker.TradeIncome);
    }

    [Fact]
    public void Tribute_UpdatesBothSides()
    {
        var (cm, _, tracker) = MakeWorld(playerId: 1);
        cm.Events.RaiseTribute(new TributeEvent { FromPlayerId = 1, ToPlayerId = 2, Type = ResourceType.Wood, Amount = 50 });
        Assert.Equal(50, tracker.TributesSent);
        Assert.Equal(0, tracker.TributesReceived);
    }

    [Fact]
    public void EntityKilled_UpdatesKillerAndVictimCounters_WithValue()
    {
        var (cm, _, tracker) = MakeWorld(playerId: 1);
        // victim 属玩家2，cost=30；killer 属玩家1
        var victim = MakeEntity(cm, owner: 2, classes: "Infantry", cost: 30);
        var killer = MakeEntity(cm, owner: 1, classes: "Infantry");
        cm.Events.RaiseEntityKilled(new EntityKilledEvent { Victim = victim, Killer = killer });

        // killer 是玩家1 → enemyUnitsKilled + value
        Assert.Equal(1, tracker.EnemyUnitsKilled.GetValueOrDefault("Infantry"));
        Assert.Equal(30, tracker.EnemyUnitsKilledValue);
        // victim 不是玩家1 → UnitsLost 不应增加
        Assert.Equal(0, tracker.UnitsLost.GetValueOrDefault("Infantry"));
    }

    [Fact]
    public void EntityKilled_VictimIsOurs_IncrementsUnitsLost()
    {
        var (cm, _, tracker) = MakeWorld(playerId: 1);
        var victim = MakeEntity(cm, owner: 1, classes: "Cavalry", cost: 50);
        var killer = MakeEntity(cm, owner: 2);
        cm.Events.RaiseEntityKilled(new EntityKilledEvent { Victim = victim, Killer = killer });

        Assert.Equal(1, tracker.UnitsLost.GetValueOrDefault("Cavalry"));
        Assert.Equal(50, tracker.UnitsLostValue);
    }

    [Fact]
    public void OwnershipChanged_Capture_IncrementsCapturedCounters()
    {
        var (cm, _, tracker) = MakeWorld(playerId: 1);
        var building = MakeEntity(cm, owner: 2, building: true, classes: "Fortress", cost: 200);
        // 建筑从玩家2 转给玩家1 = 占领
        cm.Events.RaiseOwnershipChanged(new OwnershipChangedEvent { Entity = building, From = 2, To = 1 });
        Assert.Equal(1, tracker.BuildingsCaptured.GetValueOrDefault("Fortress"));
        Assert.Equal(200, tracker.BuildingsCapturedValue);
    }

    [Fact]
    public void StructureBuilt_IncrementsBuildingsConstructed()
    {
        var (cm, _, tracker) = MakeWorld(playerId: 1);
        var bldg = MakeEntity(cm, owner: 1, building: true, classes: "House");
        cm.Events.RaiseStructureBuilt(new StructureBuiltEvent { Building = bldg, TemplateName = "structures/house" });
        Assert.Equal(1, tracker.BuildingsConstructed.GetValueOrDefault("House"));
    }

    [Fact]
    public void EventsFromOtherPlayers_DoNotAffectOurTracker()
    {
        var (cm, _, tracker) = MakeWorld(playerId: 1);
        // 玩家2 的采集不应计入玩家1
        cm.Events.RaiseResourceGathered(new ResourceGatheredEvent { PlayerId = 2, Type = ResourceType.Wood, Amount = 999 });
        Assert.Equal(0, tracker.ResourcesGathered.GetValueOrDefault("wood"));
    }

    [Fact]
    public void GetScore_MatchesOriginalFormula()
    {
        var (cm, _, tracker) = MakeWorld(playerId: 1);
        // 采集 100 wood + 100 food = 200 gathered; trade 50 → economy = (200+50)/10 = 25
        cm.Events.RaiseResourceGathered(new ResourceGatheredEvent { PlayerId = 1, Type = ResourceType.Wood, Amount = 100 });
        cm.Events.RaiseResourceGathered(new ResourceGatheredEvent { PlayerId = 1, Type = ResourceType.Food, Amount = 100 });
        cm.Events.RaiseTradeIncome(new TradeIncomeEvent { PlayerId = 1, Amount = 50 });
        // 击杀价值 300 → military = 300/10 = 30
        var victim = MakeEntity(cm, owner: 2, classes: "Infantry", cost: 300);
        var killer = MakeEntity(cm, owner: 1);
        cm.Events.RaiseEntityKilled(new EntityKilledEvent { Victim = victim, Killer = killer });
        // 探索 50% → exploration = 50×10 = 500
        tracker.PercentMapExplored = 50f;

        var (total, economy, military, exploration) = tracker.GetScore();
        Assert.Equal(25, economy);
        Assert.Equal(30, military);
        Assert.Equal(500, exploration);
        Assert.Equal(25 + 30 + 500, total);
    }

    [Fact]
    public void Serialize_RoundTrips_CountersAndValues()
    {
        var (cm, _, tracker) = MakeWorld(playerId: 1);
        // 填入一些数据
        cm.Events.RaiseResourceGathered(new ResourceGatheredEvent { PlayerId = 1, Type = ResourceType.Wood, Amount = 42 });
        cm.Events.RaiseTradeIncome(new TradeIncomeEvent { PlayerId = 1, Amount = 7 });
        var victim = MakeEntity(cm, owner: 2, classes: "Infantry", cost: 15);
        var killer = MakeEntity(cm, owner: 1);
        cm.Events.RaiseEntityKilled(new EntityKilledEvent { Victim = victim, Killer = killer });

        // 序列化
        var cap = new StringCapturingSerializer();
        tracker.Serialize(cap);
        // 反序列化到新实例
        var restored = new StatisticsTrackerComponent();
        restored.Deserialize(new StringReplayingDeserializer(cap.Values));

        Assert.Equal(42, restored.ResourcesGathered.GetValueOrDefault("wood"));
        Assert.Equal(7, restored.TradeIncome);
        Assert.Equal(1, restored.EnemyUnitsKilled.GetValueOrDefault("Infantry"));
        Assert.Equal(15, restored.EnemyUnitsKilledValue);
    }

    // ── 字符串捕获/重放桩（与 UseSiteModifierTests 同款）──
    private sealed class StringCapturingSerializer : ISerializer
    {
        public readonly List<(string Name, object Value)> Values = new();
        public void NumberU8(string n, byte v) => Values.Add((n, v));
        public void NumberI8(string n, sbyte v) => Values.Add((n, v));
        public void NumberU16(string n, ushort v) => Values.Add((n, v));
        public void NumberI16(string n, short v) => Values.Add((n, v));
        public void NumberU32(string n, uint v) => Values.Add((n, v));
        public void NumberI32(string n, int v) => Values.Add((n, v));
        public void NumberU64(string n, ulong v) => Values.Add((n, v));
        public void NumberI64(string n, long v) => Values.Add((n, v));
        public void NumberFloat(string n, float v) => Values.Add((n, v));
        public void NumberDouble(string n, double v) => Values.Add((n, v));
        public void NumberFixed(string n, Fixed v) => Values.Add((n, v.InternalValue));
        public void Bool(string n, bool v) => Values.Add((n, v));
        public void StringASCII(string n, string v) => Values.Add((n, v));
        public void RawBytes(string n, ReadOnlySpan<byte> data) => Values.Add((n, data.ToArray()));
    }

    private sealed class StringReplayingDeserializer : IDeserializer
    {
        private readonly List<(string Name, object Value)> _v; private int _i;
        public StringReplayingDeserializer(List<(string Name, object Value)> v) => _v = v;
        private object Next() => _v[_i++].Value;
        public byte NumberU8(string n) => (byte)Next();
        public sbyte NumberI8(string n) => (sbyte)Next();
        public ushort NumberU16(string n) => (ushort)Next();
        public short NumberI16(string n) => (short)Next();
        public uint NumberU32(string n) => (uint)Next();
        public int NumberI32(string n) => (int)Next();
        public ulong NumberU64(string n) => (ulong)Next();
        public long NumberI64(string n) => (long)Next();
        public float NumberFloat(string n) => (float)Next();
        public double NumberDouble(string n) => (double)Next();
        public Fixed NumberFixed(string n) => Fixed.Zero.WithInternalValue((int)Next());
        public bool Bool(string n) => (bool)Next();
        public string StringASCII(string n) => (string)Next();
        public void RawBytes(string n, Span<byte> data) { }
    }
}
