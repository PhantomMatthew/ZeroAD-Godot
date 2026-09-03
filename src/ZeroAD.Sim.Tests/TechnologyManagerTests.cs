using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Tests;

public sealed class TechnologyManagerTests
{
    // ---------- 假数据目录 ----------

    private static TechnologyDefinition Def(string name, int wood = 0, int food = 0, int stone = 0,
        int metal = 0, float time = 10f, IReadOnlyList<TechRequirement>? reqs = null,
        IReadOnlyList<Modification>? mods = null, bool auto = false,
        string? supersedes = null, IReadOnlyList<string>? replaces = null) =>
        new(name, name, wood, food, stone, metal, time,
            reqs ?? Array.Empty<TechRequirement>(),
            mods ?? Array.Empty<Modification>(), auto, supersedes,
            replaces ?? Array.Empty<string>());

    private static TechCatalog FakeCatalog()
    {
        var techs = new Dictionary<string, TechnologyDefinition>
        {
            ["phase_village"] = Def("phase_village", auto: true),
            ["phase_town_generic"] = Def("phase_town_generic", food: 500, wood: 500, time: 30f,
                supersedes: "phase_village", replaces: new List<string> { "phase_town" }),
            ["attack_ranged_01"] = Def("attack_ranged_01", wood: 200, metal: 100, time: 20f,
                reqs: new List<TechRequirement> { new("phase_town", null, null, null) },
                mods: new List<Modification>
                {
                    new("Attack/Ranged/Damage/Pierce", null, 1.15f, null, new List<string> { "Soldier" })
                }),
            ["han_auto"] = Def("han_auto", auto: true,
                reqs: new List<TechRequirement> { new(null, "han", null, null) }),
            ["athen_auto"] = Def("athen_auto", auto: true,
                reqs: new List<TechRequirement> { new(null, "athen", null, null) }),
            ["pair_a"] = Def("pair_a", stone: 50),
            ["pair_b"] = Def("pair_b", stone: 50),
        };
        var pairs = new Dictionary<string, IReadOnlyList<string>>
        {
            ["pair_ab"] = new List<string> { "pair_a", "pair_b" }
        };
        return new TechCatalog(techs, pairs);
    }

    private static (ComponentManager cm, EntityId playerEnt, TechnologyManager tm) World(string civ = "athen")
    {
        var cm = new ComponentManager(rngSeed: 1);
        var playerEnt = cm.CreateEntity();
        var player = new PlayerComponent { Civ = civ };
        cm.AddComponent(playerEnt, player);
        // AddComponent 会触发 OnInit 重置默认值 → 资源必须在挂载后设置
        player.Wood = 1000; player.Food = 1000; player.Stone = 1000; player.Metal = 1000;
        var tm = new TechnologyManager();
        cm.AddComponent(playerEnt, tm);
        cm.Players.AddPlayer(1, playerEnt);
        tm.Configure(FakeCatalog(), civ);
        return (cm, playerEnt, tm);
    }

    private EntityId MakeSoldier(ComponentManager cm)
    {
        var unit = cm.CreateEntity();
        cm.AddComponent(unit, new IdentityComponent { Classes = new List<string> { "Unit", "Soldier", "Ranged" } });
        cm.AddComponent(unit, new OwnershipComponent { PlayerId = 1 });
        return unit;
    }

    // ---------- 测试 ----------

    [Fact]
    public void CanResearch_BlockedUntilPrereq()
    {
        var (cm, _, tm) = World();
        Assert.False(tm.CanResearch("attack_ranged_01"));
        tm.ApplyResearch("phase_town_generic", cm); // replaces 含 phase_town
        Assert.True(tm.CanResearch("attack_ranged_01"));
    }

    [Fact]
    public void ApplyResearch_AppliesModsToPlayerEntity()
    {
        var (cm, _, tm) = World();
        var unit = MakeSoldier(cm);
        tm.ApplyResearch("attack_ranged_01", cm);
        Assert.Equal(11.5f, cm.Modifiers.Apply("Attack/Ranged/Damage/Pierce", 10f, unit), 3);
    }

    [Fact]
    public void ApplyResearch_MarksReplacesAndSupersedes()
    {
        var (cm, _, tm) = World();
        tm.ApplyResearch("phase_town_generic", cm);
        Assert.True(tm.IsResearched("phase_town"));
        Assert.True(tm.IsResearched("phase_village"));
    }

    [Fact]
    public void Pair_ResearchingOne_LocksOther()
    {
        var (cm, _, tm) = World();
        tm.ApplyResearch("pair_a", cm);
        Assert.False(tm.CanResearch("pair_b"));
        Assert.True(tm.IsResearched("pair_ab")); // pair 伪科技视为已研究
    }

    [Fact]
    public void AutoResearch_RunsAtInit()
    {
        var (cm, _, tm) = World();
        var done = tm.UpdateAutoResearch(cm);
        Assert.Contains("phase_village", done);
        Assert.True(tm.IsResearched("phase_village"));
    }

    [Fact]
    public void AutoResearch_CivGated()
    {
        var (cm, _, tm) = World("athen");
        tm.UpdateAutoResearch(cm);
        Assert.True(tm.IsResearched("athen_auto"));
        Assert.False(tm.IsResearched("han_auto"));

        var (cm2, _, tm2) = World("han");
        tm2.UpdateAutoResearch(cm2);
        Assert.True(tm2.IsResearched("han_auto"));
        Assert.False(tm2.IsResearched("athen_auto"));
    }

    [Fact]
    public void SerializeDeserialize_ReplayRebuildsModifiers()
    {
        var (cm, _, tm) = World();
        var unit = MakeSoldier(cm);
        tm.ApplyResearch("attack_ranged_01", cm);

        var cap = new StringCapturingSerializer();
        tm.Serialize(cap);

        var (cm2, _, tm2) = World();
        var unit2 = MakeSoldier(cm2);
        tm2.Deserialize(new StringReplayingDeserializer(cap.Values));
        tm2.RebuildModifiers(cm2);

        Assert.True(tm2.IsResearched("attack_ranged_01"));
        Assert.Equal(
            cm.Modifiers.Apply("Attack/Ranged/Damage/Pierce", 10f, unit),
            cm2.Modifiers.Apply("Attack/Ranged/Damage/Pierce", 10f, unit2));
    }

    [Fact]
    public void StartResearch_Refuses_WhenPrereqUnmet()
    {
        var (cm, _, tm) = World();
        var building = cm.CreateEntity();
        cm.AddComponent(building, new ResearcherComponent());
        var researcher = cm.QueryInterface<ResearcherComponent>(building)!;
        var player = cm.GetPlayerEntity(1)!;
        Assert.False(researcher.StartResearch("attack_ranged_01", tm, player));
    }

    [Fact]
    public void StartResearch_ChargesAllFourResources()
    {
        var (cm, _, tm) = World();
        tm.ApplyResearch("phase_town_generic", cm); // 解锁前置
        var building = cm.CreateEntity();
        cm.AddComponent(building, new ResearcherComponent());
        var researcher = cm.QueryInterface<ResearcherComponent>(building)!;
        var player = cm.GetPlayerEntity(1)!;

        // attack_ranged_01: wood 200, metal 100
        Assert.True(researcher.StartResearch("attack_ranged_01", tm, player));
        Assert.Equal(800, player.Wood);
        Assert.Equal(900, player.Metal);
    }

    [Fact]
    public void StartResearch_Refuses_WhenMetalShort()
    {
        var (cm, _, tm) = World();
        tm.ApplyResearch("phase_town_generic", cm);
        var building = cm.CreateEntity();
        cm.AddComponent(building, new ResearcherComponent());
        var researcher = cm.QueryInterface<ResearcherComponent>(building)!;
        var player = cm.GetPlayerEntity(1)!;
        player.Metal = 50; // attack_ranged_01 需 100
        Assert.False(researcher.StartResearch("attack_ranged_01", tm, player));
        Assert.Equal(1000, player.Wood); // 未扣
    }

    // ---------- 真实 JSON 冒烟 ----------

    private static string RepoDir(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        Assert.True(dir != null, $"repo marker not found: {relative}");
        return Path.Combine(dir!.FullName, relative);
    }

    [Fact]
    public void RealJson_PhaseChain_AutoVillage_TownUnlocksSoldierTech()
    {
        var catalog = TechnologyLoader.LoadAll(
            RepoDir("binaries/data/mods/public/simulation/data/technologies"));
        var cm = new ComponentManager(rngSeed: 1);
        var playerEnt = cm.CreateEntity();
        cm.AddComponent(playerEnt, new PlayerComponent { Wood = 5000, Food = 5000, Civ = "athen" });
        var tm = new TechnologyManager();
        cm.AddComponent(playerEnt, tm);
        cm.Players.AddPlayer(1, playerEnt);
        tm.Configure(catalog, "athen");

        tm.UpdateAutoResearch(cm);
        Assert.True(tm.IsResearched("phase_village"));

        Assert.False(tm.CanResearch("soldier_attack_ranged_01")); // 需 phase_town
        tm.ApplyResearch("phase_town_generic", cm);
        Assert.True(tm.CanResearch("soldier_attack_ranged_01"));
    }

    // ---------- 支持字符串的捕获/重放序列化桩 ----------

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
