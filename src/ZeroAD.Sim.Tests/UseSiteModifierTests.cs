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

        attack.AttackTarget(target);
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
        attack.AttackTarget(target);
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
        attack.AttackTarget(target);
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
