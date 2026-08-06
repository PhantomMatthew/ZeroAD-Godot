using System;
using System.IO;
using System.Linq;
using ZeroAD.Sim.Content;
using ZeroAD.Godot.Structree;
using Xunit;

namespace ZeroAD.Sim.Tests;

// 科技树构建(junction 数据缺失时跳过):BFS 须沿 Trainer(建筑→单位)与
// Builder(单位→建筑)双链展开——回归"只剩 CC 一张卡"。
public sealed class TechTreeBuilderTests
{
    private static string? FindSimRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "binaries/data/mods/public/simulation")))
                return Path.Combine(dir.FullName, "binaries/data/mods/public/simulation");
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void Athen_TreeHasManyBuildingsAcrossPhases()
    {
        string? root = FindSimRoot();
        if (root == null) return;
        var civs = CivDataLoader.LoadAll(Path.Combine(root, "data/civs"));
        var templates = new TemplateLoader(Path.Combine(root, "templates"));
        templates.LoadAllTemplates();
        var techCatalog = TechnologyLoader.LoadAll(Path.Combine(root, "data/technologies"));

        var tree = TechTreeBuilder.Build(civs["athen"], templates, techCatalog);
        int total = tree.Phases.Sum(p => p.Buildings.Count);
        Assert.True(total >= 12,
            $"athen tech tree should have ≥12 buildings, got {total} " +
            $"({string.Join(",", tree.Phases.Select(p => p.PhaseName + ":" + p.Buildings.Count))})");

        // CC 在村落列,且有可训练单位(female citizen 等)。
        var village = tree.Phases[0];
        var cc = village.Buildings.FirstOrDefault(b => b.Template.Contains("civil_centre"));
        Assert.NotNull(cc);
        Assert.NotEmpty(cc!.TrainableUnits);

        // 城镇/城市列非空(barracks 在村落列也能接受——按模板 phase 需求归列,
        // 但 fortress/wonder 必然在城镇+;总库非空即可)。
        Assert.True(tree.Phases[1].Buildings.Count + tree.Phases[2].Buildings.Count > 0,
            "town/city phases should not be empty");
    }

    [Fact]
    public void AllCivs_TreeBuilds()
    {
        string? root = FindSimRoot();
        if (root == null) return;
        var civs = CivDataLoader.LoadAll(Path.Combine(root, "data/civs"));
        var templates = new TemplateLoader(Path.Combine(root, "templates"));
        templates.LoadAllTemplates();
        var techCatalog = TechnologyLoader.LoadAll(Path.Combine(root, "data/technologies"));

        foreach (var (code, civ) in civs)
        {
            var tree = TechTreeBuilder.Build(civ, templates, techCatalog);
            int total = tree.Phases.Sum(p => p.Buildings.Count);
            Assert.True(total >= 8, $"civ {code} tech tree too small: {total} buildings");
        }
    }
}
