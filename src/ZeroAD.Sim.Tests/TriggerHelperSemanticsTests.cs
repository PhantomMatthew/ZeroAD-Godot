using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Triggers;
using Xunit;

namespace ZeroAD.Sim.Tests;

// WS5 TriggerHelper 语义修正:GetAllPlayersEntities 不含 gaia、HasDealtWithTech 含在研、
// SetPlayerWon 盟友连带、SpawnUnitsFromTriggerPoints 按点分组、
// BalancedTemplateComposition 原版两段式、AddUpgradeTemplate 占位。
public sealed class TriggerHelperSemanticsTests
{
    private static ComponentManager SetupWorld()
    {
        var cm = new ComponentManager(rngSeed: 7);
        SimSystem.Init(cm);
        var range = new RangeManager(cm, Fixed.FromInt(256), Fixed.FromInt(256));
        SimSystem.SetRangeManager(range);
        return cm;
    }

    private static EntityId AddPlayer(ComponentManager cm, int pid)
    {
        var pe = cm.CreateEntity();
        cm.AddComponent(pe, new PlayerComponent());
        cm.AddComponent(pe, new DiplomacyComponent());
        cm.Players.AddPlayer(pid, pe);
        return pe;
    }

    private static EntityId MakeUnit(ComponentManager cm, int owner, float x, float z,
        string template = "units/athen/infantry_spearman_b", params string[] classes)
    {
        var e = cm.CreateEntity();
        var posComp = new PositionComponent();
        cm.AddComponent(e, posComp);
        var id = new IdentityComponent { Name = "U", IsUnit = true, TemplateName = template };
        id.Classes.AddRange(classes.Length > 0 ? classes : new[] { "Unit" });
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

    // ── GetAllPlayersEntities:仅非 gaia ──

    [Fact]
    public void GetAllPlayersEntities_ExcludesGaia()
    {
        var cm = SetupWorld();
        var gaia = MakeUnit(cm, 0, 10, 10);
        var mine = MakeUnit(cm, 1, 20, 20);

        var all = TriggerHelper.GetAllPlayersEntities(cm);
        Assert.Contains(mine, all);
        Assert.DoesNotContain(gaia, all);
    }

    // ── HasDealtWithTech:已研或在研 ──

    private static TechCatalog FakeCatalog()
    {
        var techs = new Dictionary<string, TechnologyDefinition>
        {
            ["tech_x"] = new("tech_x", "tech_x", 0, 0, 0, 0, 30f,
                Array.Empty<TechRequirement>(), Array.Empty<Modification>(),
                false, null, Array.Empty<string>()),
        };
        return new TechCatalog(techs, new Dictionary<string, IReadOnlyList<string>>());
    }

    [Fact]
    public void HasDealtWithTech_TrueWhenQueued_OrResearched()
    {
        var cm = SetupWorld();
        var pe = cm.CreateEntity();
        var player = new PlayerComponent();
        cm.AddComponent(pe, player);
        player.Wood = 100; player.Food = 100; player.Stone = 100; player.Metal = 100;
        var tm = new TechnologyManager();
        cm.AddComponent(pe, tm);
        cm.Players.AddPlayer(1, pe);
        tm.Configure(FakeCatalog(), "athen");

        Assert.False(TriggerHelper.HasDealtWithTech(cm, 1, "tech_x"));

        // 在研(研究建筑队列含该科技)→ true(原版 IsTechnologyQueued)。
        var lab = MakeUnit(cm, 1, 10, 10, "structures/athen/gymnasium", "Structure");
        var researcher = new ResearcherComponent();
        cm.AddComponent(lab, researcher);
        Assert.True(researcher.StartResearch("tech_x", tm, player));
        Assert.True(TriggerHelper.HasDealtWithTech(cm, 1, "tech_x"));

        // 取消后不再算;已研究则恒 true。
        Assert.True(researcher.CancelResearch("tech_x", tm, player));
        Assert.False(TriggerHelper.HasDealtWithTech(cm, 1, "tech_x"));
        tm.ApplyResearch("tech_x", cm);
        Assert.True(TriggerHelper.HasDealtWithTech(cm, 1, "tech_x"));
    }

    // ── SetPlayerWon:盟友连带胜、其余判负 ──

    [Fact]
    public void SetPlayerWon_AlliedVictory_CrownsAllies_DefeatsRest()
    {
        var cm = SetupWorld();
        AddPlayer(cm, 1);
        AddPlayer(cm, 2);
        AddPlayer(cm, 3);
        // 1↔2 互盟;3 独立。
        cm.QueryInterface<DiplomacyComponent>(cm.Players.GetPlayerEntityId(1)!.Value)!.SetAlly(2);
        cm.QueryInterface<DiplomacyComponent>(cm.Players.GetPlayerEntityId(2)!.Value)!.SetAlly(1);
        MakeUnit(cm, 1, 10, 10);   // 防征服清零干扰
        MakeUnit(cm, 2, 20, 20);

        TriggerHelper.SetPlayerWon(cm, 1, "won by trigger", "lost by trigger");

        Assert.True(cm.Players.GetPlayerEntity(1)!.HasWon());
        Assert.True(cm.Players.GetPlayerEntity(2)!.HasWon());       // 盟友连带
        Assert.True(cm.Players.GetPlayerEntity(3)!.IsDefeated());   // 其余判负

        // 下一回合 TickVictory 补 GameEnded(MarkPlayerAndAlliesAsWon 路径收尾)。
        cm.TickVictory();
        Assert.True(cm.IsGameOver);
    }

    [Fact]
    public void SetPlayerWon_LastManStanding_OnlyCrownsSelf()
    {
        var cm = SetupWorld();
        AddPlayer(cm, 1);
        AddPlayer(cm, 2);
        cm.EndGame.AlliedVictory = false;
        cm.QueryInterface<DiplomacyComponent>(cm.Players.GetPlayerEntityId(1)!.Value)!.SetAlly(2);
        cm.QueryInterface<DiplomacyComponent>(cm.Players.GetPlayerEntityId(2)!.Value)!.SetAlly(1);
        MakeUnit(cm, 1, 10, 10);
        MakeUnit(cm, 2, 20, 20);

        TriggerHelper.SetPlayerWon(cm, 1);

        Assert.True(cm.Players.GetPlayerEntity(1)!.HasWon());
        Assert.True(cm.Players.GetPlayerEntity(2)!.IsDefeated());   // LMS:盟友不连带
    }

    // ── SpawnUnitsFromTriggerPoints:按点分组返回 ──

    [Fact]
    public void SpawnUnitsFromTriggerPoints_GroupsByPoint()
    {
        var cm = SetupWorld();
        var pa = MakeUnit(cm, 0, 30, 30);
        var pb = MakeUnit(cm, 0, 90, 90);
        cm.Triggers.RegisterTriggerPoint("A", pa);
        cm.Triggers.RegisterTriggerPoint("A", pb);

        var result = TriggerHelper.SpawnUnitsFromTriggerPoints(cm, cm.Triggers, "A",
            "units/athen/support_female_citizen", 2, owner: 1);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, result[pa].Count);
        Assert.Equal(2, result[pb].Count);
        // 生成位置在各触发点坐标。
        var pos = cm.QueryInterface<PositionComponent>(result[pa][0])!;
        Assert.Equal(30f, pos.Position.X.ToFloat(), 2);
    }

    // ── BalancedTemplateComposition:原版两段式 ──

    [Fact]
    public void BalancedTemplateComposition_CountGroupsFirst_FrequencySplitsRemainder()
    {
        var cm = SetupWorld();
        var balancing = new List<TriggerHelper.TemplateBalance>
        {
            // count 组:定额 2(A/B 随机分)。
            new(new List<string> { "tpl/a", "tpl/b" }, Count: 2),
            // frequency 组:权重 1 vs 3,分 totalCount=10 的余额。
            new(new List<string> { "tpl/c" }, Frequency: 1),
            new(new List<string> { "tpl/d" }, Frequency: 3),
        };

        var result = TriggerHelper.BalancedTemplateComposition(cm, balancing, 10);

        int ab = result.GetValueOrDefault("tpl/a") + result.GetValueOrDefault("tpl/b");
        Assert.Equal(2, ab);                        // count 组定额
        Assert.Equal(3, result["tpl/c"]);           // round(1/4×10)=round(2.5)→3(half-up)
        Assert.Equal(5, result["tpl/d"]);           // 末组吃余数:10-2-3
        Assert.Equal(10, result.Values.Sum());
    }

    [Fact]
    public void BalancedTemplateComposition_UniqueEntities_ExcludeInWorldTemplates()
    {
        var cm = SetupWorld();
        var hero = MakeUnit(cm, 1, 10, 10, "units/athen/hero_themistocles", "Hero");
        var balancing = new List<TriggerHelper.TemplateBalance>
        {
            new(new List<string> { "units/athen/hero_themistocles", "units/athen/hero_pericles" },
                Count: 1, UniqueEntities: new List<uint> { hero.Value }),
        };

        var result = TriggerHelper.BalancedTemplateComposition(cm, balancing, 3);

        // 在场英雄被剔除 → 只能选另一位;定额 1。
        Assert.Equal(1, result.GetValueOrDefault("units/athen/hero_pericles"));
        Assert.False(result.ContainsKey("units/athen/hero_themistocles"));
    }

    // ── AddUpgradeTemplate:占位(晋升链 TODO)──

    [Fact]
    public void AddUpgradeTemplate_ReturnsTemplateUnchanged_Placeholder()
    {
        var cm = SetupWorld();
        Assert.Equal("units/athen/infantry_spearman_b",
            TriggerHelper.AddUpgradeTemplate(cm, 1, "units/athen/infantry_spearman_b"));
    }
}
