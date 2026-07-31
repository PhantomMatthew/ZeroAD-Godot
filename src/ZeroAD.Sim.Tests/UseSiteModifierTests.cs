using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Tests;

/// <summary>use-site 接线测试(Task 4: Attack/Health;Task 5 继续累积)。</summary>
public sealed class UseSiteModifierTests
{
    private static TechnologyDefinition Def(string name,
        IReadOnlyList<Modification>? mods = null, IReadOnlyList<TechRequirement>? reqs = null) =>
        new(name, name, 0, 0, 0, 0, 10f,
            reqs ?? Array.Empty<TechRequirement>(),
            mods ?? Array.Empty<Modification>(), false, null, Array.Empty<string>());

    private static TechCatalog FakeCatalog()
    {
        var techs = new Dictionary<string, TechnologyDefinition>
        {
            ["attack_ranged_01"] = Def("attack_ranged_01", mods: new List<Modification>
            {
                new("Attack/Ranged/Damage/Pierce", null, 1.15f, null, new List<string> { "Soldier" })
            }),
            ["health_tower"] = Def("health_tower", mods: new List<Modification>
            {
                new("Health/Max", null, 1.25f, null, new List<string> { "Tower" })
            }),
            ["capture_points"] = Def("capture_points", mods: new List<Modification>
            {
                new("Capturable/CapturePoints", null, 1.4f, null, new List<string>())
            }),
        };
        return new TechCatalog(techs, new Dictionary<string, IReadOnlyList<string>>());
    }

    private static (ComponentManager cm, EntityId playerEnt, TechnologyManager tm) World()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var playerEnt = cm.CreateEntity();
        var player = new PlayerComponent();
        cm.AddComponent(playerEnt, player);
        player.Wood = 10000; player.Food = 10000; player.Stone = 10000; player.Metal = 10000;
        cm.AddComponent(playerEnt, new OwnershipComponent { PlayerId = 1 });
        var tm = new TechnologyManager();
        cm.AddComponent(playerEnt, tm);
        cm.Players.AddPlayer(1, playerEnt);
        tm.Configure(FakeCatalog(), "athen");
        return (cm, playerEnt, tm);
    }

    private static EntityId MakeEntity(ComponentManager cm, int playerId, params string[] classes)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new IdentityComponent { Classes = new List<string>(classes) });
        cm.AddComponent(e, new OwnershipComponent { PlayerId = playerId });
        return e;
    }

    // ---------- Task 4: Attack ----------

    [Fact]
    public void Research_RangedAttack_SoldierDamageUp()
    {
        var (cm, _, tm) = World();
        var soldier = MakeEntity(cm, 1, "Unit", "Soldier", "Ranged");
        var dmg = new DamageBlock();
        dmg.Amounts[DamageType.Pierce] = 10;
        cm.AddComponent(soldier, new AttackComponent { Damage = dmg, IsRanged = true });
        var target = cm.CreateEntity();
        cm.AddComponent(target, new HealthComponent { Current = 100, Max = 100 });

        var attack = cm.QueryInterface<AttackComponent>(soldier)!;
        var health = cm.QueryInterface<HealthComponent>(target)!;

        attack.AttackTarget(cm, target);
        attack.PerformAttack(cm);
        cm.DelayedDamage.TickPending(cm);
        Assert.Equal(90, health.Current); // 研究前:10 点

        tm.ApplyResearch("attack_ranged_01", cm);
        health.Current = 100;
        attack.PerformAttack(cm);
        cm.DelayedDamage.TickPending(cm);
        Assert.Equal(88, health.Current); // 研究后:round(10 × 1.15) = 12
    }

    [Fact]
    public void Research_RangedAttack_CivilianUnchanged()
    {
        var (cm, _, tm) = World();
        var civilian = MakeEntity(cm, 1, "Unit", "Support"); // 无 Soldier 类
        var dmg = new DamageBlock();
        dmg.Amounts[DamageType.Pierce] = 10;
        cm.AddComponent(civilian, new AttackComponent { Damage = dmg, IsRanged = true });
        var target = cm.CreateEntity();
        cm.AddComponent(target, new HealthComponent { Current = 100, Max = 100 });

        tm.ApplyResearch("attack_ranged_01", cm);
        var attack = cm.QueryInterface<AttackComponent>(civilian)!;
        attack.AttackTarget(cm, target);
        attack.PerformAttack(cm);
        cm.DelayedDamage.TickPending(cm);
        Assert.Equal(90, cm.QueryInterface<HealthComponent>(target)!.Current); // 不受加成
    }

    [Fact]
    public void MeleeAttack_UsesMeleePath()
    {
        var (cm, _, tm) = World();
        // 给近战路径单独挂一条科技(melee 专用,不带 Soldier 过滤)
        var catalog = new TechCatalog(new Dictionary<string, TechnologyDefinition>
        {
            ["melee_hack"] = Def("melee_hack", mods: new List<Modification>
            {
                new("Attack/Melee/Damage/Hack", null, 2f, null, new List<string>())
            })
        }, new Dictionary<string, IReadOnlyList<string>>());
        tm.Configure(catalog, "athen");

        var soldier = MakeEntity(cm, 1, "Unit", "Soldier");
        var dmg = new DamageBlock();
        dmg.Amounts[DamageType.Hack] = 10;
        cm.AddComponent(soldier, new AttackComponent { Damage = dmg, IsRanged = false });
        var target = cm.CreateEntity();
        cm.AddComponent(target, new HealthComponent { Current = 100, Max = 100 });

        tm.ApplyResearch("melee_hack", cm);
        var attack = cm.QueryInterface<AttackComponent>(soldier)!;
        attack.AttackTarget(cm, target);
        attack.PerformAttack(cm);
        cm.DelayedDamage.TickPending(cm);
        Assert.Equal(80, cm.QueryInterface<HealthComponent>(target)!.Current); // 10 × 2
    }

    // ---------- Task 4: Health 缩放 ----------

    [Fact]
    public void Research_TowerHealth_MaxUp_CurrentScalesProportionally()
    {
        var (cm, playerEnt, tm) = World();
        var tower = MakeEntity(cm, 1, "Structure", "Tower");
        var hp = new HealthComponent();
        cm.AddComponent(tower, hp);
        hp.Current = 200; hp.Max = 400; // OnInit 默认值之后设置

        tm.ApplyResearch("health_tower", cm);
        ValueModificationApplier.RescaleHealth(cm, playerEnt);

        Assert.Equal(500, hp.Max);    // 400 × 1.25
        Assert.Equal(250, hp.Current); // 200 × 500/400
    }

    [Fact]
    public void RescaleHealth_IgnoresOtherPlayers()
    {
        var (cm, playerEnt, tm) = World();
        var enemyTower = MakeEntity(cm, 2, "Structure", "Tower");
        var hp = new HealthComponent();
        cm.AddComponent(enemyTower, hp);
        hp.Current = 200; hp.Max = 400;

        tm.ApplyResearch("health_tower", cm); // 玩家 1 的科技
        ValueModificationApplier.RescaleHealth(cm, playerEnt);

        Assert.Equal(400, hp.Max);
        Assert.Equal(200, hp.Current);
    }

    [Fact]
    public void Health_Serialize_RoundTrips_BaseMax()
    {
        var hp = new HealthComponent { Current = 150, Max = 400, BaseMax = 321 };
        var cap = new StringCapturingSerializer();
        hp.Serialize(cap);
        var restored = new HealthComponent();
        restored.Deserialize(new StringReplayingDeserializer(cap.Values));
        Assert.Equal(321, restored.BaseMax);
        Assert.Equal(150, restored.Current);
        Assert.Equal(400, restored.Max);
    }

    // ---------- Capturable/CapturePoints 缩放(镜像 RescaleHealth) ----------

    [Fact]
    public void Research_CapturePoints_MaxUp_CpArrayScalesProportionally()
    {
        var (cm, playerEnt, tm) = World();
        var bldg = MakeEntity(cm, 1, "Structure");
        var cap = new CapturableComponent();
        cm.AddComponent(bldg, cap);
        cap.MaxCapturePoints = Fixed.FromFloat(1000);
        cap.BaseMaxCapturePoints = Fixed.FromFloat(1000);
        cap.InitForOwner(1);   // CP[1] = 1000

        tm.ApplyResearch("capture_points", cm);
        ValueModificationApplier.RescaleMaxCapturePoints(cm, playerEnt);

        // Fixed 不能精确表示 1.4(1000×1.4=1399.9939),用容差断言(对齐 CapturableTests 既有 InRange 范式)。
        Assert.Equal(1400.0, cap.MaxCapturePoints.ToFloat(), 1);   // 1000 × 1.4
        Assert.Equal(1400.0, cap.CapturePoints[1].ToFloat(), 1);   // 按比例
        Fixed sum = Fixed.Zero;                                     // ΣCP == newMax(不变式保)
        for (int i = 0; i < cap.CapturePoints.Length; i++) sum += cap.CapturePoints[i];
        Assert.Equal(cap.MaxCapturePoints.ToFloat(), sum.ToFloat(), 1);
    }

    [Fact]
    public void RescaleMaxCapturePoints_IsIdempotent_NoCompounding()
    {
        // 核心正确性:重算始终 Apply(模板基值),不复合。朴素"对当前 Max apply"会逐次 ×1.4。
        var (cm, playerEnt, tm) = World();
        var bldg = MakeEntity(cm, 1, "Structure");
        var cap = new CapturableComponent();
        cm.AddComponent(bldg, cap);
        cap.MaxCapturePoints = Fixed.FromFloat(1000);
        cap.BaseMaxCapturePoints = Fixed.FromFloat(1000);
        cap.InitForOwner(1);

        tm.ApplyResearch("capture_points", cm);
        ValueModificationApplier.RescaleMaxCapturePoints(cm, playerEnt);   // → 1000×1.4
        Fixed maxAfter1 = cap.MaxCapturePoints;
        Fixed cp1After1 = cap.CapturePoints[1];
        ValueModificationApplier.RescaleMaxCapturePoints(cm, playerEnt);   // 幂等:重算 Apply(模板基值)命中 early-out

        Assert.Equal(maxAfter1, cap.MaxCapturePoints);   // 两次结果逐位相同(幂等核心)
        Assert.Equal(cp1After1, cap.CapturePoints[1]);
    }

    [Fact]
    public void RescaleMaxCapturePoints_PreservesOwnershipAndProportions()
    {
        var (cm, playerEnt, tm) = World();
        var bldg = MakeEntity(cm, 1, "Structure");
        var cap = new CapturableComponent();
        cm.AddComponent(bldg, cap);
        cap.MaxCapturePoints = Fixed.FromFloat(1000);
        cap.BaseMaxCapturePoints = Fixed.FromFloat(1000);
        cap.CapturePoints[1] = Fixed.FromFloat(600);   // 主人(最多)
        cap.CapturePoints[2] = Fixed.FromFloat(400);   // 总和 1000 = max

        tm.ApplyResearch("capture_points", cm);
        ValueModificationApplier.RescaleMaxCapturePoints(cm, playerEnt);

        Assert.Equal(1, cm.QueryInterface<OwnershipComponent>(bldg)!.PlayerId); // argmax 不变
        Assert.Equal(840.0, cap.CapturePoints[1].ToFloat(), 1);   // 600 × 1.4
        Assert.Equal(560.0, cap.CapturePoints[2].ToFloat(), 1);   // 400 × 1.4
    }

    [Fact]
    public void RescaleMaxCapturePoints_IgnoresOtherPlayers()
    {
        var (cm, playerEnt, tm) = World();
        var enemy = MakeEntity(cm, 2, "Structure");   // P2 的;tm 是 P1 的
        var cap = new CapturableComponent();
        cm.AddComponent(enemy, cap);
        cap.MaxCapturePoints = Fixed.FromFloat(1000);
        cap.BaseMaxCapturePoints = Fixed.FromFloat(1000);
        cap.InitForOwner(2);

        tm.ApplyResearch("capture_points", cm);   // P1 的科技
        ValueModificationApplier.RescaleMaxCapturePoints(cm, playerEnt);

        Assert.Equal(Fixed.FromFloat(1000), cap.MaxCapturePoints);   // P2 不受影响
    }

    // ---------- Task 5: 采集/移速/建造/训练/人口 ----------

    [Fact]
    public void GatherRate_WoodTech_AppliesPrefixMatch()
    {
        var (cm, _, tm) = World();
        var catalog = new TechCatalog(new Dictionary<string, TechnologyDefinition>
        {
            ["gather_wood"] = Def("gather_wood", mods: new List<Modification>
            {
                new("ResourceGatherer/Rates/wood.tree", null, 1.15f, null, new List<string>())
            })
        }, new Dictionary<string, IReadOnlyList<string>>());
        tm.Configure(catalog, "athen");

        var worker = MakeEntity(cm, 1, "Unit", "Support");
        var g = new ResourceGatherer();
        cm.AddComponent(worker, g);
        g.GatherRate = 10;

        Assert.Equal(10, g.EffectiveRate(cm, ResourceType.Wood)); // 研究前
        tm.ApplyResearch("gather_wood", cm);
        Assert.Equal(12, g.EffectiveRate(cm, ResourceType.Wood)); // round(10 × 1.15)
        Assert.Equal(10, g.EffectiveRate(cm, ResourceType.Food)); // 其他资源不受影响
    }

    [Fact]
    public void WalkSpeed_Tech_AppliesAtMoveAdvance()
    {
        var (cm, _, tm) = World();
        var catalog = new TechCatalog(new Dictionary<string, TechnologyDefinition>
        {
            ["speed_up"] = Def("speed_up", mods: new List<Modification>
            {
                new("UnitMotion/WalkSpeed", null, 1.5f, null, new List<string>())
            })
        }, new Dictionary<string, IReadOnlyList<string>>());
        tm.Configure(catalog, "athen");

        SimSystem.Init(cm);
        var unit = MakeEntity(cm, 1, "Unit");
        cm.AddComponent(unit, new PositionComponent());
        var motion = new UnitMotion();
        cm.AddComponent(unit, motion);
        motion.Speed = Fixed.FromFloat(8f);

        motion.MoveToPoint(new FixedVector2D(Fixed.FromFloat(10f), Fixed.Zero));
        motion.Tick(0.1f);
        float before = cm.QueryInterface<PositionComponent>(unit)!.Position.X.ToFloat();
        Assert.Equal(0.8f, before, 2); // 8 × 0.1

        tm.ApplyResearch("speed_up", cm);
        motion.Tick(0.1f);
        float after = cm.QueryInterface<PositionComponent>(unit)!.Position.X.ToFloat();
        Assert.Equal(0.8f + 1.2f, after, 2); // 再走一步:12 × 0.1
    }

    [Fact]
    public void BuilderRate_Tech_SpeedsUpFoundation()
    {
        var (cm, _, tm) = World();
        var catalog = new TechCatalog(new Dictionary<string, TechnologyDefinition>
        {
            ["eng"] = Def("eng", mods: new List<Modification>
            {
                new("Builder/Rate", null, 2f, null, new List<string>())
            })
        }, new Dictionary<string, IReadOnlyList<string>>());
        tm.Configure(catalog, "athen");

        var builderEnt = MakeEntity(cm, 1, "Unit");
        cm.AddComponent(builderEnt, new PositionComponent());
        var builder = new BuilderComponent();
        cm.AddComponent(builderEnt, builder);
        builder.BuildSpeed = 1f;

        var foundation = cm.CreateEntity();
        cm.AddComponent(foundation, new PositionComponent());
        var f = new FoundationComponent();
        cm.AddComponent(foundation, f);
        f.Configure("structures/x", buildTime: 10f);

        builder.Build(foundation);
        builder.Tick(cm);
        Assert.Equal(0.1f, f.Progress, 3); // 研究前:1 × 0.1

        tm.ApplyResearch("eng", cm);
        builder.Build(foundation);
        builder.Tick(cm);
        Assert.Equal(0.1f + 0.2f, f.Progress, 3); // 研究后:2 × 0.1
    }

    [Fact]
    public void TrainTime_CostTech_Reduced()
    {
        var (cm, _, tm) = World();
        var templatesPath = FindRepoPath("binaries/data/mods/public/simulation/templates");
        if (templatesPath == null) return; // LFS 数据缺失则跳过
        cm.Templates = new TemplateLoader(templatesPath);
        var catalog = new TechCatalog(new Dictionary<string, TechnologyDefinition>
        {
            ["cheap"] = Def("cheap", mods: new List<Modification>
            {
                new("Cost/BuildTime", null, 0.9f, null, new List<string>())
            })
        }, new Dictionary<string, IReadOnlyList<string>>());
        tm.Configure(catalog, "athen");

        const string template = "units/spart/support_civilian";
        var stats = cm.Templates.ExtractStats(template);
        var building = MakeEntity(cm, 1, "Structure");
        var queue = new ProductionQueue();
        cm.AddComponent(building, queue);

        Assert.True(queue.EnqueueTraining(template, 1, cm));
        Assert.Equal(stats.BuildTime, queue.Queue[0].BuildTime, 3); // 研究前

        tm.ApplyResearch("cheap", cm);
        Assert.True(queue.EnqueueTraining(template, 1, cm));
        Assert.Equal(stats.BuildTime * 0.9f, queue.Queue[1].BuildTime, 3); // 研究后
    }

    [Fact]
    public void PopulationBonus_Tech_IncreasesLimit()
    {
        var (cm, _, tm) = World();
        var catalog = new TechCatalog(new Dictionary<string, TechnologyDefinition>
        {
            ["housing"] = Def("housing", mods: new List<Modification>
            {
                new("Population/Bonus", 5f, null, null, new List<string> { "House" })
            })
        }, new Dictionary<string, IReadOnlyList<string>>());
        tm.Configure(catalog, "athen");

        var house = MakeEntity(cm, 1, "Structure", "House");
        var pop = new PopulationComponent();
        cm.AddComponent(house, pop);
        pop.Bonus = 10;

        cm.Players.RecomputePlayerPopBonus(1);
        Assert.Equal(10, cm.GetPlayerEntity(1)!.PopBonuses); // 研究前

        tm.ApplyResearch("housing", cm);
        cm.Players.RecomputePlayerPopBonus(1);
        Assert.Equal(15, cm.GetPlayerEntity(1)!.PopBonuses); // 研究后:10 + 5
    }

    [Fact]
    public void Determinism_SameResearchOrder_SameStateHash()
    {
        var (cm1, _, tm1) = World();
        var (cm2, _, tm2) = World();
        MakeEntity(cm1, 1, "Unit", "Soldier");
        MakeEntity(cm2, 1, "Unit", "Soldier");

        // 同序研究
        tm1.ApplyResearch("attack_ranged_01", cm1);
        tm1.ApplyResearch("health_tower", cm1);
        tm2.ApplyResearch("attack_ranged_01", cm2);
        tm2.ApplyResearch("health_tower", cm2);
        Assert.Equal(cm1.ComputeStateHash(), cm2.ComputeStateHash());

        // 反序也一致(序列化排序 + 合成排序固定)
        var (cm3, _, tm3) = World();
        var (cm4, _, tm4) = World();
        MakeEntity(cm3, 1, "Unit", "Soldier");
        MakeEntity(cm4, 1, "Unit", "Soldier");
        tm3.ApplyResearch("health_tower", cm3);
        tm3.ApplyResearch("attack_ranged_01", cm3);
        tm4.ApplyResearch("attack_ranged_01", cm4);
        tm4.ApplyResearch("health_tower", cm4);
        Assert.Equal(cm3.ComputeStateHash(), cm4.ComputeStateHash());
    }

    private static string? FindRepoPath(string relative)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : System.IO.Path.Combine(dir.FullName, relative);
    }

    // ---------- 支持字符串的捕获/重放序列化桩(与 TechnologyManagerTests 同款) ----------

    private sealed class StringCapturingSerializer : ISerializer
    {
        public readonly List<(string Name, object Value)> Values = new();
        public void NumberU8(string n, byte v) => Values.Add((n, v));
        public void NumberI8(string n, sbyte v) => Values.Add((n, v));
        public void NumberU16(string n, ushort v) => Values.Add((n, v));
        public void NumberI16(string n, short v) => Values.Add((n, v));
        public void NumberU32(string n, uint v) => Values.Add((n, v));
        public void NumberI32(string n, int v) => Values.Add((n, v));
        public void NumberFixed(string n, Fixed v) => Values.Add((n, v.InternalValue));
        public void Bool(string n, bool v) => Values.Add((n, v));
        public void StringASCII(string n, string v) => Values.Add((n, v));
        public void RawBytes(string n, ReadOnlySpan<byte> data) => Values.Add((n, data.ToArray()));
    }

    private sealed class StringReplayingDeserializer : IDeserializer
    {
        private readonly List<(string Name, object Value)> _v;
        private int _i;
        public StringReplayingDeserializer(List<(string Name, object Value)> v) => _v = v;
        private object Next() => _v[_i++].Value;
        public byte NumberU8(string n) => (byte)Next();
        public sbyte NumberI8(string n) => (sbyte)Next();
        public ushort NumberU16(string n) => (ushort)Next();
        public short NumberI16(string n) => (short)Next();
        public uint NumberU32(string n) => (uint)Next();
        public int NumberI32(string n) => (int)Next();
        public Fixed NumberFixed(string n) => Fixed.Zero.WithInternalValue((int)Next());
        public bool Bool(string n) => (bool)Next();
        public string StringASCII(string n) => (string)Next();
        public void RawBytes(string n, Span<byte> data) { }
    }
}
