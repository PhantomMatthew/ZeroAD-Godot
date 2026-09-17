using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using ZeroAD.Godot;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 原版 GuiInterface.GetStartedResearch = TechnologyManager.GetBasicInfoOfStartedTechs:
/// 所有已开始的在研科技(按名),不是只返回第一座建筑。HUD ResearchProgress 最多画 10 个。
/// </summary>
public sealed class GetStartedResearchTests
{
    private static TechnologyDefinition Def(string name, string icon, float time) =>
        new(name, name, 0, 0, 0, 0, time,
            Array.Empty<TechRequirement>(), Array.Empty<Modification>(),
            false, null, Array.Empty<string>(), icon);

    private static (ComponentManager cm, PlayerComponent player, TechnologyManager tm)
        World()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var playerEnt = cm.CreateEntity();
        var player = new PlayerComponent { Civ = "athen" };
        cm.AddComponent(playerEnt, player);
        player.Wood = 1000; player.Food = 1000; player.Stone = 1000; player.Metal = 1000;
        var tm = new TechnologyManager();
        cm.AddComponent(playerEnt, tm);
        cm.Players.AddPlayer(1, playerEnt);
        tm.Configure(new TechCatalog(new Dictionary<string, TechnologyDefinition>
        {
            ["tech_a"] = Def("tech_a", "a.png", 20f),
            ["tech_b"] = Def("tech_b", "b.png", 40f),
        }, new Dictionary<string, IReadOnlyList<string>>()), "athen");
        return (cm, player, tm);
    }

    private static EntityId Building(ComponentManager cm, int owner)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new ResearcherComponent());
        return e;
    }

    [Fact]
    public void GetStartedResearch_None_IsEmpty()
    {
        var (cm, _, _) = World();
        var gui = new GuiInterface(cm);
        Assert.Empty(gui.GetStartedResearch(1));
    }

    [Fact]
    public void GetStartedResearch_TwoBuildings_ReturnsBoth()
    {
        var (cm, player, tm) = World();
        var a = Building(cm, 1);
        var b = Building(cm, 1);
        Assert.True(cm.QueryInterface<ResearcherComponent>(a)!.StartResearch("tech_a", tm, player));
        Assert.True(cm.QueryInterface<ResearcherComponent>(b)!.StartResearch("tech_b", tm, player));
        cm.QueryInterface<ResearcherComponent>(a)!.Tick(5f, tm, cm);

        var list = new GuiInterface(cm).GetStartedResearch(1);
        Assert.Equal(2, list.Count);
        var byTech = list.ToDictionary(x => x.Tech, StringComparer.Ordinal);
        Assert.True(byTech.ContainsKey("tech_a"));
        Assert.True(byTech.ContainsKey("tech_b"));
        Assert.Equal(a, byTech["tech_a"].Researcher);
        Assert.Equal(b, byTech["tech_b"].Researcher);
        Assert.Equal("a.png", byTech["tech_a"].Icon);
        Assert.Equal(0.25f, byTech["tech_a"].Progress, 3);
        Assert.Equal(15f, byTech["tech_a"].TimeRemaining, 3);
        Assert.Equal(0f, byTech["tech_b"].Progress, 3);
        Assert.Equal(40f, byTech["tech_b"].TimeRemaining, 3);
    }

    [Fact]
    public void GetQueueStripState_ResearchOnly_ShowsTechSlot()
    {
        var (cm, player, tm) = World();
        var forge = Building(cm, 1);
        Assert.True(cm.QueryInterface<ResearcherComponent>(forge)!.StartResearch("tech_a", tm, player));
        cm.QueryInterface<ResearcherComponent>(forge)!.Tick(10f, tm, cm);

        var strip = new GuiInterface(cm).GetQueueStripState(forge, 16);
        Assert.NotNull(strip);
        Assert.Single(strip!.Items);
        Assert.True(strip.Items[0].IsTechnology);
        Assert.Equal("tech_a", strip.Items[0].TemplateName);
        Assert.Equal("a.png", strip.Items[0].Icon);
        Assert.Equal(0.5f, strip.Items[0].Progress, 3);
        Assert.Equal(10, strip.RemainingSeconds);
    }
}
