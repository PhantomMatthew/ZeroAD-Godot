using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 间谍请求(贿赂/视野共享)端到端测试——原版 Commands.js "spy-request" +
/// VisionSharing.js 的移植面。链路:NetCommand.SpyRequest → SimCommandExecutor
/// (随机挑可贿赂目标 → AddSpy 扣费/科技门/时长;无目标 → 失败成本 + 通知)→
/// VisionSharingComponent → RangeManager.SetExtraSeers(该实体视野圈计入请求者 LOS)。
/// 模板读真实暂存数据(special/spy:500 金属、unlock_spies 前置、Duration=15、
/// FailureCostRatio=0.25);缺失时整组跳过(同 ActorTemplateTests 模式)。
/// </summary>
public sealed class SpyRequestTests
{
    private const int World = 256;

    private static TemplateLoader? RealTemplates() =>
        RepoPaths.Resolve("binaries/data/mods/public/simulation/templates") is { } root
            ? new TemplateLoader(root) : null;

    /// <summary>含 unlock_spies / spy_counter 定义的假目录(spy_counter 带
    /// Player/SpyCostMultiplier ×1.5 修改,对齐原版 JSON)。</summary>
    private static TechCatalog SpyCatalog()
    {
        static TechnologyDefinition Def(string name, IReadOnlyList<Modification>? mods = null) =>
            new(name, name, 0, 0, 0, 0, 60f,
                Array.Empty<TechRequirement>(),
                mods ?? Array.Empty<Modification>(), false, null, Array.Empty<string>());
        var techs = new Dictionary<string, TechnologyDefinition>
        {
            ["unlock_spies"] = Def("unlock_spies"),
            ["spy_counter"] = Def("spy_counter", new List<Modification>
            {
                new("Player/SpyCostMultiplier", null, 1.5f, null, Array.Empty<string>()),
            }),
        };
        return new TechCatalog(techs, new Dictionary<string, IReadOnlyList<string>>());
    }

    /// <summary>双玩家世界:玩家实体带 Player/Ownership/Diplomacy/TechnologyManager/
    /// StatisticsTracker(对齐 SimBridge 建图组件集),系统实体挂 LosManagerComponent。</summary>
    private static (ComponentManager cm, RangeManager rm, EntityId sys) NewWorld(
        TemplateLoader templates, int players = 2)
    {
        var cm = new ComponentManager(42, templates: templates);
        cm.Registry.AutoRegister(typeof(PositionComponent).Assembly);
        SimSystem.Init(cm);
        var rm = new RangeManager(cm, Fixed.FromInt(World), Fixed.FromInt(World));
        rm.SetBounds(Fixed.FromInt(World));
        var catalog = SpyCatalog();
        for (int p = 1; p <= players; p++)
        {
            var ent = cm.CreateEntity();
            var pc = new PlayerComponent();
            cm.AddComponent(ent, pc);
            pc.Wood = 0; pc.Food = 0; pc.Stone = 0; pc.Metal = 1000;   // OnInit 重置后赋值
            cm.AddComponent(ent, new OwnershipComponent { PlayerId = p });
            cm.AddComponent(ent, new DiplomacyComponent());
            var tm = new TechnologyManager();
            cm.AddComponent(ent, tm);
            tm.Configure(catalog, "athen");
            var stats = new StatisticsTrackerComponent();
            cm.AddComponent(ent, stats);
            stats.Attach(cm);
            cm.Players.AddPlayer(p, ent);
        }
        var sys = cm.CreateEntity();
        var losComp = new LosManagerComponent();
        cm.AddComponent(sys, losComp);
        losComp.Attach(rm);
        return (cm, rm, sys);
    }

    /// <summary>生成一个可贿赂商人(合成组件集:Position+Ownership+Vision+VisionSharing),
    /// 走完 RangeManager 注册全路径(同 VisionSharingTests.Spawn)。</summary>
    private static EntityId SpawnTrader(ComponentManager cm, RangeManager rm,
        int x, int z, int owner, int range, bool bribable = true)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));
        var id = new IdentityComponent();
        cm.AddComponent(e, id);
        id.Classes.Add("Trader");
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        if (range > 0)
        {
            cm.AddComponent(e, new VisionComponent());
            cm.QueryInterface<VisionComponent>(e)!.Range = Fixed.FromInt(range);
        }
        cm.AddComponent(e, new VisionSharingComponent { Bribable = bribable });
        cm.NotifyEntityCreated(e);
        rm.RefreshFromComponents(e);
        var p = new FixedVector2D(Fixed.FromInt(x), Fixed.FromInt(z));
        cm.NotifyPositionChanged(e, p, p);
        return e;
    }

    private static TechnologyManager TechMgr(ComponentManager cm, int player) =>
        cm.QueryInterface<TechnologyManager>(cm.Players.GetPlayerEntityId(player)!.Value)!;

    private static StatisticsTrackerComponent Stats(ComponentManager cm, int player) =>
        cm.QueryInterface<StatisticsTrackerComponent>(cm.Players.GetPlayerEntityId(player)!.Value)!;

    // 世界 (100,100) → LOS 顶点 (25,25)(同 VisionSharingTests 的换算)。
    private const int Vx = 25, Vz = 25;

    [Fact]
    public void Bribe_Success_SharesVisionAndChargesFullCost()
    {
        if (RealTemplates() is not { } templates) return;
        var (cm, rm, _) = NewWorld(templates);
        TechMgr(cm, 1).ApplyResearch("unlock_spies", cm);
        var trader = SpawnTrader(cm, rm, 100, 100, owner: 2, range: 120);
        Assert.False(rm.Los.IsVisible(1, Vx, Vz), "请求者此前看不到目标圈");

        SpyResponseEvent? response = null;
        cm.Events.SpyResponse += e => response = e;
        new SimCommandExecutor(cm).Apply(NetCommand.SpyRequest(1, 2));

        var vs = cm.QueryInterface<VisionSharingComponent>(trader)!;
        Assert.Single(vs.Spies);
        Assert.Equal(1, vs.Spies.Values.First().Player);
        Assert.True(vs.Activated);
        // 原版 special/spy:500 金属 × SpyCostMultiplier 1.0 = 500。
        Assert.Equal(500, cm.GetPlayerEntity(1)!.Metal);
        Assert.True(rm.Los.IsVisible(1, Vx, Vz), "贿赂成功后目标圈计入请求者 LOS");
        Assert.NotNull(response);
        Assert.Equal(trader.Value, response!.BribedEntity);
        Assert.Equal(2, response.Target);
        Assert.Equal(1, Stats(cm, 1).SuccessfulBribes);
        Assert.True(vs.ShareVisionWith(cm, 1));
        Assert.True(rm.Los.IsVisible(2, Vx, Vz), "属主视野不受影响");
    }

    [Fact]
    public void Bribe_Expires_AfterDurationTicks()
    {
        if (RealTemplates() is not { } templates) return;
        var (cm, rm, _) = NewWorld(templates);
        TechMgr(cm, 1).ApplyResearch("unlock_spies", cm);
        // range 120 → 时长 15s × 60/max(30,120) = 7.5s → 75 回合。
        var trader = SpawnTrader(cm, rm, 100, 100, owner: 2, range: 120);
        new SimCommandExecutor(cm).Apply(NetCommand.SpyRequest(1, 2));
        Assert.True(rm.Los.IsVisible(1, Vx, Vz));

        var vs = cm.QueryInterface<VisionSharingComponent>(trader)!;
        Assert.Equal(75, vs.Spies.Values.First().RemainingTicks);
        for (int t = 0; t < 74; t++) VisionSharingComponent.TickAll(cm, rm);
        Assert.True(rm.Los.IsVisible(1, Vx, Vz), "74 回合后间谍仍在");
        VisionSharingComponent.TickAll(cm, rm);   // 第 75 回合到期
        Assert.Empty(vs.Spies);
        Assert.False(rm.Los.IsVisible(1, Vx, Vz), "到期后请求者失去共享圈");
        Assert.True(rm.Los.IsVisible(2, Vx, Vz), "属主圈不受间谍到期影响");
    }

    [Fact]
    public void Bribe_NoBribableUnits_ChargesFailureCostAndNotifies()
    {
        if (RealTemplates() is not { } templates) return;
        var (cm, rm, _) = NewWorld(templates);
        // 目标玩家只有一个不可贿赂实体(Bribable=false,对齐 template_unit 基模板)。
        SpawnTrader(cm, rm, 100, 100, owner: 2, range: 20, bribable: false);

        SpyResponseEvent? response = null;
        cm.Events.SpyResponse += e => response = e;
        PlayerCommandEvent? failed = null;
        cm.Events.PlayerCommand += e => { if (e.Type == "spy-failed") failed = e; };
        new SimCommandExecutor(cm).Apply(NetCommand.SpyRequest(1, 2));

        // 失败成本 = 500 × 1.0 × FailureCostRatio 0.25 = 125(原版同序:扣费→计数→通知)。
        Assert.Equal(875, cm.GetPlayerEntity(1)!.Metal);
        Assert.Equal(1, Stats(cm, 1).FailedBribes);
        Assert.Equal(0, Stats(cm, 1).SuccessfulBribes);
        Assert.NotNull(response);
        Assert.Equal(0u, response!.BribedEntity);
        Assert.NotNull(failed);
        Assert.Equal(1, failed!.Data["player"]);
    }

    [Fact]
    public void Bribe_AlreadySharing_PicksDifferentEntity()
    {
        if (RealTemplates() is not { } templates) return;
        var (cm, rm, _) = NewWorld(templates);
        TechMgr(cm, 1).ApplyResearch("unlock_spies", cm);
        var t1 = SpawnTrader(cm, rm, 100, 100, owner: 2, range: 120);
        var t2 = SpawnTrader(cm, rm, 140, 140, owner: 2, range: 120);
        var exec = new SimCommandExecutor(cm);

        exec.Apply(NetCommand.SpyRequest(1, 2));
        var first = cm.AllEntities
            .Where(e => cm.QueryInterface<VisionSharingComponent>(e)?.Spies.Count > 0)
            .ToList();
        Assert.Single(first);

        exec.Apply(NetCommand.SpyRequest(1, 2));
        var spied = cm.AllEntities
            .Where(e => cm.QueryInterface<VisionSharingComponent>(e)?.Spies.Count > 0)
            .ToList();
        // 第二次请求不得重复收买已共享者(ShareVisionWith 过滤),两个商人各被收一次。
        Assert.Equal(2, spied.Count);
        Assert.Contains(t1, spied);
        Assert.Contains(t2, spied);
        Assert.Equal(0, cm.GetPlayerEntity(1)!.Metal);   // 2 × 500
    }

    [Fact]
    public void Bribe_WithoutUnlockSpies_RefusedNoCost()
    {
        if (RealTemplates() is not { } templates) return;
        var (cm, rm, _) = NewWorld(templates);
        var trader = SpawnTrader(cm, rm, 100, 100, owner: 2, range: 120);

        SpyResponseEvent? response = null;
        cm.Events.SpyResponse += e => response = e;
        new SimCommandExecutor(cm).Apply(NetCommand.SpyRequest(1, 2));

        // 原版:AddSpy 的 CanProduce("special/spy") 门拒收,但 spy-response 已带实体回。
        var vs = cm.QueryInterface<VisionSharingComponent>(trader)!;
        Assert.Empty(vs.Spies);
        Assert.Equal(1000, cm.GetPlayerEntity(1)!.Metal);
        Assert.False(rm.Los.IsVisible(1, Vx, Vz));
        Assert.NotNull(response);
        Assert.Equal(trader.Value, response!.BribedEntity);
        Assert.Equal(0, Stats(cm, 1).SuccessfulBribes);
    }

    [Fact]
    public void Garrison_ForeignPassenger_SharesHolderVision()
    {
        if (RealTemplates() is not { } templates) return;
        var (cm, rm, _) = NewWorld(templates);
        // 互盟才可驻防(原版 GarrisonHolder.IsAllowedToGarrison);关掉盟友视野共享,
        // 隔离出"驻军带来的共享"这一条通路。
        bool prev = RangeManager.AlliedVisionEnabled;
        RangeManager.AlliedVisionEnabled = false;
        try
        {
            var e1 = cm.Players.GetPlayerEntityId(1)!.Value;
            var e2 = cm.Players.GetPlayerEntityId(2)!.Value;
            cm.QueryInterface<DiplomacyComponent>(e1)!.SetAlly(2);
            cm.QueryInterface<DiplomacyComponent>(e2)!.SetAlly(1);

            // 持有者:P2 的建筑(Vision 16 + VisionSharing Bribable=false——
            // 对齐 template_structure 基模板:驻军共享广谱生效,与可贿赂无关)。
            var holder = cm.CreateEntity();
            cm.AddComponent(holder, new PositionComponent());
            cm.QueryInterface<PositionComponent>(holder)!.Position =
                new FixedVector3D(Fixed.FromInt(100), Fixed.Zero, Fixed.FromInt(100));
            var hid = new IdentityComponent();
            cm.AddComponent(holder, hid);
            hid.Classes.Add("Structure");
            cm.AddComponent(holder, new OwnershipComponent { PlayerId = 2 });
            var gh = new GarrisonHolderComponent { Max = 10 };
            cm.AddComponent(holder, gh);
            gh.AllowedClasses.Add("Infantry");
            cm.AddComponent(holder, new VisionComponent());
            cm.QueryInterface<VisionComponent>(holder)!.Range = Fixed.FromInt(16);
            cm.AddComponent(holder, new VisionSharingComponent { Bribable = false });
            cm.NotifyEntityCreated(holder);
            rm.RefreshFromComponents(holder);
            var hp = new FixedVector2D(Fixed.FromInt(100), Fixed.FromInt(100));
            cm.NotifyPositionChanged(holder, hp, hp);

            // 乘客:P1 的步兵,贴旁(装载射程内)。
            var passenger = cm.CreateEntity();
            cm.AddComponent(passenger, new PositionComponent());
            cm.QueryInterface<PositionComponent>(passenger)!.Position =
                new FixedVector3D(Fixed.FromInt(101), Fixed.Zero, Fixed.FromInt(100));
            var pid = new IdentityComponent();
            cm.AddComponent(passenger, pid);
            pid.Classes.Add("Infantry");
            cm.AddComponent(passenger, new OwnershipComponent { PlayerId = 1 });
            cm.AddComponent(passenger, new GarrisonableComponent { Size = 1 });
            cm.NotifyEntityCreated(passenger);
            rm.RefreshFromComponents(passenger);

            Assert.False(rm.Los.IsVisible(1, Vx, Vz), "盟友视野关闭时 P1 看不到 P2 建筑圈");
            Assert.True(cm.QueryInterface<GarrisonableComponent>(passenger)!.Garrison(cm, holder));

            VisionSharingComponent.TickAll(cm, rm);   // 回合制吸收驻军变化
            Assert.True(rm.Los.IsVisible(1, Vx, Vz),
                "异主乘客属主获得持有者视野圈(原版 OnGarrisonedUnitsChanged)");

            Assert.True(cm.QueryInterface<GarrisonableComponent>(passenger)!.UnGarrison(cm));
            VisionSharingComponent.TickAll(cm, rm);
            Assert.False(rm.Los.IsVisible(1, Vx, Vz), "乘客出舱后共享立即收回");
        }
        finally
        {
            RangeManager.AlliedVisionEnabled = prev;
        }
    }

    [Fact]
    public void SaveLoad_MidSpy_RestoresSpyStateLosAndHash()
    {
        if (RealTemplates() is not { } templates) return;
        var (cmA, rmA, _) = NewWorld(templates);
        TechMgr(cmA, 1).ApplyResearch("unlock_spies", cmA);
        var trader = SpawnTrader(cmA, rmA, 100, 100, owner: 2, range: 120);
        new SimCommandExecutor(cmA).Apply(NetCommand.SpyRequest(1, 2));
        VisionSharingComponent.TickAll(cmA, rmA);   // 推进一回合,让剩余计数非整
        rmA.UpdateVisibilityData();
        byte[] hashA = cmA.ComputeStateHash();

        var ms = new MemoryStream();
        cmA.SerializeSaveGame(new BinarySerializer(new BinaryWriter(ms)));
        ms.Position = 0;

        // 冷侧:全新 ComponentManager + RangeManager(同 ColdLoadRebuildTests)。
        var cmB = new ComponentManager(42, templates: templates);
        cmB.Registry.AutoRegister(typeof(PositionComponent).Assembly);
        SimSystem.Init(cmB);
        var rmB = new RangeManager(cmB, Fixed.FromInt(World), Fixed.FromInt(World));
        rmB.SetBounds(Fixed.FromInt(World));
        cmB.DeserializeSaveGame(new BinaryDeserializer(new BinaryReader(ms)), comp =>
        {
            if (comp is LosManagerComponent l) l.Attach(rmB);
            if (comp is StatisticsTrackerComponent st) st.Attach(cmB);
        });
        rmB.Repopulate(cmB.AllEntities);
        rmB.UpdateVisibilityData();

        // 间谍状态随组件流往返。
        var vsB = cmB.QueryInterface<VisionSharingComponent>(trader);
        Assert.NotNull(vsB);
        Assert.Single(vsB!.Spies);
        Assert.Equal(74, vsB.Spies.Values.First().RemainingTicks);
        Assert.True(vsB.Activated);
        // 共享圈经 RefreshFromComponents 的 ExtraSeerMask 回本铺回请求者 LOS。
        Assert.True(rmB.Los.IsVisible(1, Vx, Vz));
        Assert.True(rmB.Los.IsVisible(2, Vx, Vz));
        Assert.Equal(500, cmB.GetPlayerEntity(1)!.Metal);
        Assert.Equal(1, Stats(cmB, 1).SuccessfulBribes);

        byte[] hashB = cmB.ComputeStateHash();
        Assert.True(hashA.AsSpan().SequenceEqual(hashB), "间谍中存续档往返后全状态哈希一致");

        // 倒计时在冷侧继续走,到期后共享圈照样收回。
        for (int t = 0; t < 74; t++) VisionSharingComponent.TickAll(cmB, rmB);
        Assert.Empty(vsB.Spies);
        Assert.False(rmB.Los.IsVisible(1, Vx, Vz));
    }

    [Fact]
    public void Bribe_SpyCounterTech_RaisesCostByHalf()
    {
        if (RealTemplates() is not { } templates) return;
        var (cm, rm, _) = NewWorld(templates);
        TechMgr(cm, 1).ApplyResearch("unlock_spies", cm);
        SpawnTrader(cm, rm, 100, 100, owner: 2, range: 120);

        // 目标玩家研究 spy_counter(原版:Player/SpyCostMultiplier ×1.5,supersedes unlock_spies)。
        TechMgr(cm, 2).ApplyResearch("spy_counter", cm);
        Assert.Equal(1.5f, cm.GetPlayerEntity(2)!.GetSpyCostMultiplier(cm));

        new SimCommandExecutor(cm).Apply(NetCommand.SpyRequest(1, 2));
        // floor(500 × 1.5) = 750。
        Assert.Equal(250, cm.GetPlayerEntity(1)!.Metal);
        Assert.True(rm.Los.IsVisible(1, Vx, Vz));
        Assert.Equal(1, Stats(cm, 1).SuccessfulBribes);
    }
}
