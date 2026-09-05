using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

// GarrisonHolder + Garrisonable — ports of GarrisonHolder.js / Garrisonable.js.
// 驻军:单位进持有者(建筑/船)后离世界(InWorld=false,FSM 冻结),持有者可 BuffHeal 回血、
// 低血逐出(EjectHealth)、被毁时按 EjectClassesOnDestroy 逐出/同灭。出驻落到持有者旁,
// 有集结点则自动 Walk。舰载商人给 Trader 增益(Trader.js CalculateGain 的 garrison 段)。
public sealed class GarrisonTests
{
    private static EntityId MakeUnit(ComponentManager cm, int player = 1, float x = 0f, float z = 0f,
        string classes = "Infantry", int size = 1, bool withAI = false)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        var motion = new UnitMotion();
        cm.AddComponent(e, motion);
        motion.Speed = Fixed.FromFloat(3f);
        var id = new IdentityComponent();
        cm.AddComponent(e, id);
        id.Classes.AddRange(classes.Split(' '));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        cm.AddComponent(e, new GarrisonableComponent { Size = size });
        var health = new HealthComponent();
        cm.AddComponent(e, health);
        health.Current = 100; health.Max = 100;                 // OnInit 清空后赋值(防 clobber)
        if (withAI) cm.AddComponent(e, new UnitAIComponent());
        return e;
    }

    private static EntityId MakeHolder(ComponentManager cm, int player = 1, float x = 0f, float z = 0f,
        int max = 10, string list = "Infantry", float buffHeal = 0f, float ejectHealth = -1f,
        string ejectClasses = "Infantry", float loadingRange = 2f, bool dropsite = false)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        var id = new IdentityComponent();
        cm.AddComponent(e, id);
        id.Classes.Add("Structure");
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        var gh = new GarrisonHolderComponent
        {
            Max = max,
            BuffHeal = buffHeal,
            EjectHealth = ejectHealth,
            EjectClassesOnDestroy = ejectClasses,
            LoadingRange = loadingRange,
        };
        cm.AddComponent(e, gh);
        gh.AllowedClasses.AddRange(list.Split(' '));
        if (dropsite) cm.AddComponent(e, new ResourceDropsite());
        return e;
    }

    private static PlayerComponent AddPlayer(ComponentManager cm, int playerId)
    {
        var pe = cm.CreateEntity();
        var pc = new PlayerComponent();
        cm.AddComponent(pe, pc);
        pc.Wood = 0; pc.Food = 0; pc.Stone = 0; pc.Metal = 0;   // OnInit 重置后赋值
        cm.AddComponent(pe, new DiplomacyComponent());          // SeedDiplomacyFromTeams 需要
        cm.Players.AddPlayer(playerId, pe);
        return pc;
    }

    [Fact]
    public void IsAllowedToGarrison_CapacityClassDiplomacy_Matrix()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        AddPlayer(cm, 2);
        var holder = MakeHolder(cm, max: 2, list: "Infantry");
        var gh = cm.QueryInterface<GarrisonHolderComponent>(holder)!;
        var a = MakeUnit(cm, classes: "Infantry");
        var cav = MakeUnit(cm, classes: "Cavalry");

        Assert.True(gh.IsAllowedToGarrison(cm, a));                 // 类别+容量+同主 → 可
        Assert.False(gh.IsAllowedToGarrison(cm, cav));              // 类别不符 → 拒

        Assert.True(gh.Garrison(cm, a));
        var b = MakeUnit(cm, classes: "Infantry", size: 2);         // 占 2 格:1+2>2 → 拒
        Assert.False(gh.IsAllowedToGarrison(cm, b));

        // 敌对持有者(无外交数据 = 非互盟)→ 拒。
        var enemyHolder = MakeHolder(cm, player: 2, x: 50f);
        Assert.False(cm.QueryInterface<GarrisonHolderComponent>(enemyHolder)!.IsAllowedToGarrison(cm, b));

        // 互盟(同队)→ 可。
        cm.Players.SeedDiplomacyFromTeams(new System.Collections.Generic.Dictionary<int, int> { [1] = 0, [2] = 0 });
        Assert.True(cm.QueryInterface<GarrisonHolderComponent>(enemyHolder)!.IsAllowedToGarrison(cm, b));
    }

    [Fact]
    public void Garrison_Success_SetsHolderAndLeavesWorld()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var holder = MakeHolder(cm);
        var unit = MakeUnit(cm, withAI: true);
        var g = cm.QueryInterface<GarrisonableComponent>(unit)!;
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;

        Assert.True(g.Garrison(cm, holder));

        Assert.Equal(holder, g.Holder);
        Assert.Contains(unit, cm.QueryInterface<GarrisonHolderComponent>(holder)!.Entities);
        Assert.False(cm.QueryInterface<PositionComponent>(unit)!.InWorld);   // MoveOutOfWorld
        Assert.True(ai.IsGarrisoned);                                        // SetGarrisoned

        Assert.False(g.Garrison(cm, holder));                                // 已驻防 → 拒(原版 Holder 检查)
    }

    [Fact]
    public void OccupiedSlots_SumsGarrisonableTotalSize()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var holder = MakeHolder(cm, max: 3);
        var gh = cm.QueryInterface<GarrisonHolderComponent>(holder)!;
        var big = MakeUnit(cm, classes: "Infantry", size: 2);

        Assert.True(gh.Garrison(cm, big));
        Assert.Equal(2, gh.OccupiedSlots(cm));

        var small = MakeUnit(cm, classes: "Infantry", size: 2, x: 5f);       // 2+2>3 → 拒
        Assert.False(gh.IsAllowedToGarrison(cm, small));
        var one = MakeUnit(cm, classes: "Infantry", size: 1, x: 9f);         // 2+1≤3 → 可
        Assert.True(gh.IsAllowedToGarrison(cm, one));
    }

    [Fact]
    public void BuffHeal_HealsOncePerSecond_SkipsUnhealable()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var holder = MakeHolder(cm, buffHeal: 1f);
        var gh = cm.QueryInterface<GarrisonHolderComponent>(holder)!;
        var unit = MakeUnit(cm);
        cm.QueryInterface<HealthComponent>(unit)!.Current = 50;
        var unhealable = MakeUnit(cm, x: 5f);
        var uh = cm.QueryInterface<HealthComponent>(unhealable)!;
        uh.Current = 50; uh.Unhealable = true;

        gh.Garrison(cm, unit);
        gh.Garrison(cm, unhealable);

        gh.Tick(0.5f, cm);                                                   // 不足 1s → 不回
        Assert.Equal(50, cm.QueryInterface<HealthComponent>(unit)!.Current);
        gh.Tick(0.5f, cm);                                                   // 满 1s → +1
        Assert.Equal(51, cm.QueryInterface<HealthComponent>(unit)!.Current);
        gh.Tick(1.0f, cm);
        Assert.Equal(52, cm.QueryInterface<HealthComponent>(unit)!.Current);
        Assert.Equal(50, uh.Current);                                        // Unhealable 跳过
    }

    [Fact]
    public void EjectHealth_Tick_EjectsMatchingClass_KillsRest()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var holder = MakeHolder(cm, list: "Infantry Hero", ejectHealth: 0.1f, ejectClasses: "Infantry");
        var gh = cm.QueryInterface<GarrisonHolderComponent>(holder)!;
        var holderHealth = new HealthComponent();
        cm.AddComponent(holder, holderHealth);
        holderHealth.Max = 100; holderHealth.Current = 100;

        var infantry = MakeUnit(cm, classes: "Infantry");
        var hero = MakeUnit(cm, classes: "Hero", x: 5f);
        cm.QueryInterface<GarrisonableComponent>(infantry)!.Garrison(cm, holder);
        cm.QueryInterface<GarrisonableComponent>(hero)!.Garrison(cm, holder);
        Assert.Equal(2, gh.Entities.Count);

        holderHealth.Current = 5;   // ≤ floor(0.1×100) → 低血(入驻后再扣,原版低血拒入驻)
        gh.Tick(0.1f, cm);                                                   // BuffHeal=0 也做低血检查

        Assert.Empty(gh.Entities);
        // Infantry 在逐出类别 → 弹回世界、存活。
        Assert.True(cm.QueryInterface<PositionComponent>(infantry)!.InWorld);
        Assert.Null(cm.QueryInterface<GarrisonableComponent>(infantry)!.Holder);
        Assert.True(cm.QueryInterface<HealthComponent>(infantry)!.Current > 0);
        // Hero 不在逐出类别 → 随主同灭(Health=0,等 RemoveDeadEntities 清扫)。
        Assert.Equal(0, cm.QueryInterface<HealthComponent>(hero)!.Current);
    }

    [Fact]
    public void UnGarrison_RestoresWorldState_AndWalksToRally()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var holder = MakeHolder(cm);
        var rally = new RallyPointComponent();
        cm.AddComponent(holder, rally);
        rally.Set(new FixedVector2D(Fixed.FromInt(20), Fixed.FromInt(20)));
        var unit = MakeUnit(cm, withAI: true);
        var g = cm.QueryInterface<GarrisonableComponent>(unit)!;
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;

        g.Garrison(cm, holder);
        Assert.True(g.UnGarrison(cm));

        Assert.Null(g.Holder);
        Assert.DoesNotContain(unit, cm.QueryInterface<GarrisonHolderComponent>(holder)!.Entities);
        var pos = cm.QueryInterface<PositionComponent>(unit)!;
        Assert.True(pos.InWorld);
        Assert.NotEqual(Fixed.Zero, pos.Position.X);                         // 移到持有者旁(非原点重叠)
        Assert.False(ai.IsGarrisoned);                                       // UnsetGarrisoned

        // 原版 Ungarrison 标记指令不移植(FSM 派发雷区);集结点 Walk 直接入队。
        Assert.Equal("Walk", ai.CurrentOrder?.Type);
        ai.Tick(0.1f, cm);
        Assert.EndsWith("WALKING", ai.FsmStateName);
    }

    [Fact]
    public void TraderGain_BoostedByGarrisonedTraders()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        SimSystem.Init(cm);
        // 商船:Trader + 自带 GarrisonHolder,GarrisonGainMultiplier=1.0。
        var ship = cm.CreateEntity();
        cm.AddComponent(ship, new PositionComponent());
        cm.QueryInterface<PositionComponent>(ship)!.Position =
            new FixedVector3D(Fixed.Zero, Fixed.Zero, Fixed.Zero);
        var id = new IdentityComponent();
        cm.AddComponent(ship, id);
        id.Classes.Add("Ship");
        cm.AddComponent(ship, new OwnershipComponent { PlayerId = 1 });
        var trader = new TraderComponent { GainMultiplier = 0.75f, GarrisonGainMultiplier = 1.0f };
        cm.AddComponent(ship, trader);
        var hold = new GarrisonHolderComponent { Max = 4 };
        cm.AddComponent(ship, hold);
        hold.AllowedClasses.Add("Trader");
        // 舱内一名商人(直接入列表,绕过上船流程)。
        var passenger = MakeUnit(cm, classes: "Trader", x: 3f);
        cm.AddComponent(passenger, new TraderComponent());
        hold.Entities.Add(passenger);

        // 双市场 100m:基准 traderGain=3(同 TraderTests 公式)。
        EntityId MarketAt(float x)
        {
            var m = cm.CreateEntity();
            cm.AddComponent(m, new PositionComponent());
            cm.QueryInterface<PositionComponent>(m)!.Position =
                new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.Zero);
            var mk = new MarketComponent();
            cm.AddComponent(m, mk);
            mk.TradeTypes.Add("naval");
            cm.AddComponent(m, new OwnershipComponent { PlayerId = 1 });
            return m;
        }
        var a = MarketAt(0f);
        var b = MarketAt(100f);
        trader.SetTargetMarket(cm, a);
        trader.SetTargetMarket(cm, b);

        Assert.Equal(b, trader.PerformTrade(cm, a));
        Assert.True(trader.HasGain);
        Assert.Equal(6, trader.TraderGain);                                  // 3 × (1 + 1.0×1)
    }

    [Fact]
    public void UnitAI_GarrisonOrder_ApproachesThenGarrisons()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var holder = MakeHolder(cm, x: 6f);                                  // LoadingRange=2:6>2 需接近
        var unit = MakeUnit(cm, withAI: true);
        var g = cm.QueryInterface<GarrisonableComponent>(unit)!;
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;

        ai.Garrison(holder);
        ai.Tick(0.1f, cm);                                                   // 派发 Order.Garrison
        Assert.EndsWith("GARRISON.APPROACHING", ai.FsmStateName);

        for (int i = 0; i < 300 && !ai.IsGarrisoned; i++)
        {
            cm.QueryInterface<UnitMotion>(unit)?.Tick(0.1f);
            ai.Tick(0.1f, cm);
        }

        Assert.True(ai.IsGarrisoned);
        Assert.Equal(holder, g.Holder);
        Assert.False(cm.QueryInterface<PositionComponent>(unit)!.InWorld);
        Assert.EndsWith("IDLE", ai.FsmStateName);                            // 入驻即完成指令
    }

    [Fact]
    public void UnitAI_GarrisonRejected_WhenFull()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var holder = MakeHolder(cm, max: 1);
        var first = MakeUnit(cm);
        cm.QueryInterface<GarrisonableComponent>(first)!.Garrison(cm, holder);

        var second = MakeUnit(cm, x: 1f, withAI: true);                      // 射程内但满员
        var ai = cm.QueryInterface<UnitAIComponent>(second)!;
        ai.Garrison(holder);
        ai.Tick(0.1f, cm);
        ai.Tick(0.1f, cm);

        Assert.EndsWith("IDLE", ai.FsmStateName);
        Assert.False(ai.IsGarrisoned);
        Assert.Null(cm.QueryInterface<GarrisonableComponent>(second)!.Holder);
    }

    [Fact]
    public void UnitAI_Garrison_DepositsCarriedResources_AtDropsite()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var player = AddPlayer(cm, 1);
        var holder = MakeHolder(cm, x: 1f, dropsite: true);                  // 射程内 → 直接入驻
        var unit = MakeUnit(cm, withAI: true);
        var gatherer = new ResourceGatherer();
        cm.AddComponent(unit, gatherer);
        gatherer.CarryAmount = 8;
        gatherer.CarryType = ResourceType.Wood;
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;

        ai.Garrison(holder);
        ai.Tick(0.1f, cm);                                                   // 派发 → GARRISONING.enter → 入驻+交付

        Assert.True(ai.IsGarrisoned);
        Assert.Equal(8, player.Wood);                                        // CommitResources
        Assert.Equal(0, gatherer.CarryAmount);
    }

    [Fact]
    public void Unload_EjectsUnitBackIntoWorld()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var holder = MakeHolder(cm);
        var gh = cm.QueryInterface<GarrisonHolderComponent>(holder)!;
        var unit = MakeUnit(cm);
        var g = cm.QueryInterface<GarrisonableComponent>(unit)!;

        g.Garrison(cm, holder);
        Assert.True(gh.Unload(cm, unit));

        Assert.Null(g.Holder);
        Assert.True(cm.QueryInterface<PositionComponent>(unit)!.InWorld);
        Assert.Empty(gh.Entities);

        Assert.True(gh.Unload(cm, unit));                                    // 非乘员卸载 = 成功(原版 Eject 语义)
    }

    [Fact]
    public void RoundTrip_PreservesHolderAndGarrisonable()
    {
        var gh = new GarrisonHolderComponent
        {
            Max = 20,
            BuffHeal = 1.5f,
            LoadingRange = 3f,
            EjectHealth = 0.1f,
            Pickup = true,
            EjectClassesOnDestroy = "Unit Infantry",
            HealElapsed = 0.4f,
        };
        gh.AllowedClasses.AddRange(new[] { "Support", "Infantry" });
        gh.Entities.Add(new EntityId(7));
        gh.Entities.Add(new EntityId(9));

        var ms = new System.IO.MemoryStream();
        gh.Serialize(new Serialization.BinarySerializer(new System.IO.BinaryWriter(ms)));
        ms.Position = 0;
        var ghBack = new GarrisonHolderComponent();
        ghBack.Deserialize(new Serialization.BinaryDeserializer(new System.IO.BinaryReader(ms)));

        Assert.Equal(20, ghBack.Max);
        Assert.Equal(1.5f, ghBack.BuffHeal, 3);
        Assert.Equal(3f, ghBack.LoadingRange, 3);
        Assert.Equal(0.1f, ghBack.EjectHealth, 3);
        Assert.True(ghBack.Pickup);
        Assert.Equal("Unit Infantry", ghBack.EjectClassesOnDestroy);
        Assert.Equal(0.4f, ghBack.HealElapsed, 3);
        Assert.Equal(new[] { "Support", "Infantry" }, ghBack.AllowedClasses);
        Assert.Equal(new[] { new EntityId(7), new EntityId(9) }, ghBack.Entities);

        var g = new GarrisonableComponent { Holder = new EntityId(42), Size = 2 };
        var ms2 = new System.IO.MemoryStream();
        g.Serialize(new Serialization.BinarySerializer(new System.IO.BinaryWriter(ms2)));
        ms2.Position = 0;
        var gBack = new GarrisonableComponent();
        gBack.Deserialize(new Serialization.BinaryDeserializer(new System.IO.BinaryReader(ms2)));
        Assert.Equal(new EntityId(42), gBack.Holder);
        Assert.Equal(2, gBack.Size);
    }

    [Fact]
    public void AllowGarrisoning_Lock_BlocksEntryAndExit_UntilReleased()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var holder = MakeHolder(cm);
        var gh = cm.QueryInterface<GarrisonHolderComponent>(holder)!;
        var unit = MakeUnit(cm);
        var g = cm.QueryInterface<GarrisonableComponent>(unit)!;

        // 与门:一票否决;其他 caller 放行不顶用,本 caller 放行才开。
        gh.SetGarrisoningAllowed("vehicle-driving", false);
        gh.SetGarrisoningAllowed("other-system", true);
        Assert.False(gh.IsGarrisoningAllowed());
        Assert.False(gh.IsAllowedToGarrison(cm, unit));                    // 锁定拒进
        Assert.False(g.Garrison(cm, holder));
        gh.SetGarrisoningAllowed("vehicle-driving", true);
        Assert.True(gh.IsGarrisoningAllowed());
        Assert.True(g.Garrison(cm, holder));

        // 锁定拒出(原版:舱内单位须等放行才可出驻);放行后可出。
        gh.SetGarrisoningAllowed("vehicle-driving", false);
        Assert.False(gh.Unload(cm, unit));
        Assert.NotNull(g.Holder);
        gh.SetGarrisoningAllowed("vehicle-driving", true);
        Assert.True(gh.Unload(cm, unit));
        Assert.Null(g.Holder);
    }

    [Fact]
    public void AllowGarrisoning_Locked_EjectOrKill_ForcedStillEjects()
    {
        // 锁定时建筑被毁/外交逐出走 forced=true:可逐类别仍弹回世界(原版 Eject 的
        // forced 例外——否则锁定的建筑被毁,舱内可逐单位逐不出)。
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var holder = MakeHolder(cm, ejectClasses: "Infantry");
        var gh = cm.QueryInterface<GarrisonHolderComponent>(holder)!;
        var unit = MakeUnit(cm, classes: "Infantry");
        cm.QueryInterface<GarrisonableComponent>(unit)!.Garrison(cm, holder);

        gh.SetGarrisoningAllowed("vehicle-driving", false);
        gh.EjectOrKillAll(cm);

        Assert.Empty(gh.Entities);
        Assert.True(cm.QueryInterface<PositionComponent>(unit)!.InWorld);
        Assert.True(cm.QueryInterface<HealthComponent>(unit)!.Current > 0);
    }

    [Fact]
    public void ClassRecheck_Tick_EjectsNoLongerMatching()
    {
        // 类别表变更逐出(原版 OnValueModification 改 GarrisonHolder/List 后 EjectOrKill
        // 失配者;本移植无修正值变更钩子 → Tick 1s 低频复查)。直接改 AllowedClasses
        // 模拟修正值效果。
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var holder = MakeHolder(cm, list: "Infantry", ejectClasses: "Infantry");
        var gh = cm.QueryInterface<GarrisonHolderComponent>(holder)!;
        var unit = MakeUnit(cm, classes: "Infantry");
        var g = cm.QueryInterface<GarrisonableComponent>(unit)!;
        g.Garrison(cm, holder);

        gh.AllowedClasses.Clear();
        gh.AllowedClasses.Add("Cavalry");

        gh.Tick(0.5f, cm);                                                 // 不足 1s → 不逐
        Assert.Contains(unit, gh.Entities);
        gh.Tick(0.6f, cm);                                                 // 复查 → 逐出
        Assert.Empty(gh.Entities);
        Assert.True(cm.QueryInterface<PositionComponent>(unit)!.InWorld);
        Assert.Null(g.Holder);
    }

    [Fact]
    public void RoundTrip_PreservesGarrisoningLocks()
    {
        var gh = new GarrisonHolderComponent { Max = 4 };
        gh.SetGarrisoningAllowed("vehicle-driving", false);
        gh.SetGarrisoningAllowed("aura-bonus", true);

        var ms = new System.IO.MemoryStream();
        gh.Serialize(new Serialization.BinarySerializer(new System.IO.BinaryWriter(ms)));
        ms.Position = 0;
        var back = new GarrisonHolderComponent();
        back.Deserialize(new Serialization.BinaryDeserializer(new System.IO.BinaryReader(ms)));

        Assert.False(back.IsGarrisoningAllowed());                         // 与门:一票否决
        back.SetGarrisoningAllowed("vehicle-driving", true);
        Assert.True(back.IsGarrisoningAllowed());
    }

    [Fact]
    public void Deserialize_V17Save_MissingLockTail_ReadsEmpty()
    {
        // v17 写序(无 v18 锁表/复查计时器尾段)手工拼流;版本上下文=17 → 按空表读。
        var ms = new System.IO.MemoryStream();
        var s = new Serialization.BinarySerializer(new System.IO.BinaryWriter(ms));
        s.NumberI32("ent_n", 0);
        s.NumberI32("max", 7);
        s.NumberI32("allowed_n", 1);
        s.StringASCII("allowed", "Infantry");
        s.StringASCII("ejectClasses", "Infantry");
        s.NumberFixed("buffHeal", Fixed.FromFloat(0f));
        s.NumberFixed("loadingRange", Fixed.FromFloat(2f));
        s.NumberFixed("ejectHealth", Fixed.FromFloat(-1f));
        s.Bool("pickup", false);
        s.NumberFixed("healElapsed", Fixed.FromFloat(0f));
        ms.Position = 0;

        Serialization.SaveFormat.LoadedVersion = 17;
        try
        {
            var gh = new GarrisonHolderComponent();
            gh.Deserialize(new Serialization.BinaryDeserializer(new System.IO.BinaryReader(ms)));
            Assert.Equal(7, gh.Max);
            Assert.Equal(new[] { "Infantry" }, gh.AllowedClasses);
            Assert.True(gh.IsGarrisoningAllowed());                        // 空表 → 放行
        }
        finally
        {
            Serialization.SaveFormat.LoadedVersion = Serialization.SaveFormat.CurrentVersion;
        }
    }
}
