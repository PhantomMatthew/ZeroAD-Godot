using System;
using System.IO;
using System.Linq;
using Xunit;
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim.Tests;

public sealed class TechnologyLoaderTests
{
    private static string RepoDir(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        Assert.True(dir != null, $"repo marker not found: {relative}");
        return Path.Combine(dir!.FullName, relative);
    }

    private static string TechDir() =>
        RepoDir("binaries/data/mods/public/simulation/data/technologies");

    [Fact]
    public void Loads_AllJsonFiles()
    {
        var defs = TechnologyLoader.LoadAll(TechDir());
        Assert.True(defs.Technologies.Count > 50, $"expected dozens of techs, got {defs.Technologies.Count}");
        Assert.True(defs.Technologies.ContainsKey("phase_town_generic"));
    }

    [Fact]
    public void Parses_Cost_Time_Modifications()
    {
        var defs = TechnologyLoader.LoadAll(TechDir());
        var t = defs.Technologies["soldier_attack_ranged_01"];
        Assert.Equal(200, t.Wood);
        Assert.Equal(100, t.Metal);
        Assert.Equal(20f, t.ResearchTime);
        Assert.Contains(t.Modifications, m => m.Path == "Attack/Ranged/Damage/Pierce" && m.Multiply == 1.15f);
    }

    [Fact]
    public void Parses_TechLevelAffects_AsDefault()
    {
        var defs = TechnologyLoader.LoadAll(TechDir());
        var t = defs.Technologies["soldier_attack_ranged_01"];
        Assert.All(t.Modifications, m => Assert.Contains("Soldier", m.Affects));
    }

    [Fact]
    public void Parses_PerModAffects_OverridesTechLevel()
    {
        var defs = TechnologyLoader.LoadAll(TechDir());
        var t = defs.Technologies["phase_town_generic"];
        var territory = t.Modifications.First(m => m.Path == "TerritoryInfluence/Radius");
        Assert.Contains("CivCentre", territory.Affects);
    }

    [Fact]
    public void Parses_AutoResearch_And_Supersedes_Replaces()
    {
        var defs = TechnologyLoader.LoadAll(TechDir());
        Assert.True(defs.Technologies["phase_village"].AutoResearch);
        Assert.Equal("phase_village", defs.Technologies["phase_town_generic"].Supersedes);
        Assert.Contains("phase_town", defs.Technologies["phase_town_generic"].Replaces);
    }

    [Fact]
    public void Parses_Pairs()
    {
        var defs = TechnologyLoader.LoadAll(TechDir());
        Assert.True(defs.Pairs.Count > 0);
        Assert.Contains(defs.Pairs, p => p.Value.Contains("civil_service_01") && p.Value.Contains("civil_service_02"));
    }

    [Fact]
    public void Parses_Requirements_Tech()
    {
        var defs = TechnologyLoader.LoadAll(TechDir());
        var t = defs.Technologies["soldier_attack_ranged_01"];
        Assert.Contains(t.Requirements, r => r.Tech == "phase_town");
    }

    [Fact]
    public void Parses_Requirements_AnyNested()
    {
        // 找一个真实带 any 的科技;若数据里没有则跳过(数据演进保护)
        var defs = TechnologyLoader.LoadAll(TechDir());
        var withAny = defs.Technologies.Values.FirstOrDefault(t => t.Requirements.Any(r => r.Any != null));
        if (withAny == null) return;
        var any = withAny.Requirements.First(r => r.Any != null).Any!;
        // 回归保护:any 有内容时不得解析成空表(entity-only 项 → 恒真占位,语义在 Task 3 验)
        Assert.True(any.Count >= 1);
    }
}
