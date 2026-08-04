using System;
using System.Collections.Generic;
using System.IO;
using ZeroAD.Sim.Content;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// SkirmishReplacer 测试——原版 components/SkirmishReplacer.js ReplaceEntities 的移植验证。
/// 合成夹具精确测决策矩阵（civ 表优先 / general 兜底 / 销毁 / 保留 / {civ} 代入），
/// 真实数据冒烟测走 binaries junction（缺失时按惯例跳过）。
/// </summary>
public sealed class SkirmishReplacerTests : IDisposable
{
    // ---------- 合成夹具 ----------

    private readonly string _tempRoot;
    private readonly string _templatesRoot;
    private readonly string _civsRoot;

    public SkirmishReplacerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "skirmish_replacer_tests_" + Guid.NewGuid().ToString("N"));
        _templatesRoot = Path.Combine(_tempRoot, "templates");
        _civsRoot = Path.Combine(_tempRoot, "civs");
        Directory.CreateDirectory(Path.Combine(_templatesRoot, "skirmish", "units"));
        Directory.CreateDirectory(Path.Combine(_templatesRoot, "skirmish", "structures"));
        Directory.CreateDirectory(_civsRoot);

        // 占位模板：有 general 兜底
        File.WriteAllText(Path.Combine(_templatesRoot, "skirmish", "units", "default_infantry_melee_b.xml"),
            "<Entity><SkirmishReplacer><general>units/{civ}/infantry_spearman_b</general></SkirmishReplacer></Entity>");
        // 占位模板：无 general（空组件）
        File.WriteAllText(Path.Combine(_templatesRoot, "skirmish", "units", "special_starting_unit.xml"),
            "<Entity><SkirmishReplacer/></Entity>");
        // 占位模板：general 也会被 civ 表覆盖
        File.WriteAllText(Path.Combine(_templatesRoot, "skirmish", "structures", "default_house_10.xml"),
            "<Entity><SkirmishReplacer><general>structures/{civ}/house_10</general></SkirmishReplacer></Entity>");

        // civ JSON：一条显式映射（值本身含 {civ}）
        File.WriteAllText(Path.Combine(_civsRoot, "test.json"),
            "{\"Code\":\"test\",\"SkirmishReplacements\":{\"skirmish/structures/default_house_10\":\"structures/{civ}/house\"}}");
        // civ JSON 无 SkirmishReplacements 段
        File.WriteAllText(Path.Combine(_civsRoot, "bare.json"), "{\"Code\":\"bare\"}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    private SkirmishReplacer MakeReplacer() =>
        new(new TemplateLoader(_templatesRoot), _civsRoot);

    [Fact]
    public void CivTableMapping_WinsOverGeneral_AndSubstitutesCiv()
    {
        var r = MakeReplacer();
        // civ 表显式映射优先于模板的 general（house_10 → house，不是 house_10）
        Assert.Equal("structures/test/house",
            r.ResolveReplacement("skirmish/structures/default_house_10", "test"));
    }

    [Fact]
    public void GeneralFallback_UsedWhenCivTableHasNoEntry()
    {
        var r = MakeReplacer();
        Assert.Equal("units/test/infantry_spearman_b",
            r.ResolveReplacement("skirmish/units/default_infantry_melee_b", "test"));
    }

    [Fact]
    public void NoMappingNoGeneral_ReturnsNull_Destroy()
    {
        var r = MakeReplacer();
        Assert.Null(r.ResolveReplacement("skirmish/units/special_starting_unit", "test"));
    }

    [Fact]
    public void CivJsonWithoutTable_FallsThroughToGeneral()
    {
        var r = MakeReplacer();
        Assert.Equal("units/bare/infantry_spearman_b",
            r.ResolveReplacement("skirmish/units/default_infantry_melee_b", "bare"));
    }

    [Fact]
    public void UnknownCiv_MissingJson_FallsThroughToGeneral()
    {
        var r = MakeReplacer();
        Assert.Equal("units/nosuchciv/infantry_spearman_b",
            r.ResolveReplacement("skirmish/units/default_infantry_melee_b", "nosuchciv"));
    }

    [Fact]
    public void GaiaOwner_ReturnsNull_Destroy()
    {
        var r = MakeReplacer();
        Assert.Null(r.ResolveReplacement("skirmish/units/default_infantry_melee_b", "gaia"));
    }

    [Fact]
    public void Apply_ReplacesDestroysAndKeeps_PerUpstreamSemantics()
    {
        var r = MakeReplacer();
        var entities = new List<ScenarioEntityDef>
        {
            new() { Template = "skirmish/units/default_infantry_melee_b", Player = 1 },   // → 替换
            new() { Template = "skirmish/units/special_starting_unit", Player = 1 },      // → 销毁
            new() { Template = "skirmish/units/default_infantry_melee_b", Player = 0 },   // gaia → 销毁
            new() { Template = "skirmish/units/default_infantry_melee_b", Player = 9 },   // 无 civ → 保留
            new() { Template = "gaia/tree/oak", Player = 0 },                             // 非 skirmish → 不动
        };

        var (replaced, destroyed) = r.Apply(entities, pid => pid switch
        {
            0 => "gaia",
            1 => "test",
            _ => null,
        });

        Assert.Equal(1, replaced);
        Assert.Equal(2, destroyed);
        Assert.Equal(3, entities.Count);
        Assert.Equal("units/test/infantry_spearman_b", entities[0].Template);
        Assert.Equal("skirmish/units/default_infantry_melee_b", entities[1].Template);   // 保留
        Assert.Equal("gaia/tree/oak", entities[2].Template);
    }

    [Fact]
    public void NullTemplatesAndCivsRoot_StillResolvesToDestroyOrKeep()
    {
        var r = new SkirmishReplacer(null, null);
        // 无模板/无 civ 表 → 一切占位都查不到替换 → 销毁
        Assert.Null(r.ResolveReplacement("skirmish/units/default_infantry_melee_b", "test"));
        // 但 civ 为 null（保留语义）由 Apply 表达,不经 ResolveReplacement
        var entities = new List<ScenarioEntityDef>
        {
            new() { Template = "skirmish/units/default_infantry_melee_b", Player = 3 },
        };
        var (replaced, destroyed) = r.Apply(entities, _ => null);
        Assert.Equal(0, replaced);
        Assert.Equal(0, destroyed);
        Assert.Single(entities);
    }

    // ---------- 真实数据冒烟（binaries junction 缺失时跳过,同既有测试惯例） ----------

    private static string? FindRepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, relative);
    }

    private static SkirmishReplacer? MakeRealReplacer()
    {
        var templatesPath = FindRepoPath("binaries/data/mods/public/simulation/templates");
        if (templatesPath == null) return null;
        var civsRoot = SkirmishReplacer.CivsRootFromTemplatesRoot(templatesPath);
        if (civsRoot == null) return null;
        return new SkirmishReplacer(new TemplateLoader(templatesPath), civsRoot);
    }

    [Fact]
    public void RealData_Athen_ExplicitCivTableMapping()
    {
        var r = MakeRealReplacer();
        if (r == null) return;
        // athen.json: skirmish/units/default_infantry_ranged_b → units/athen/infantry_slinger_b
        Assert.Equal("units/athen/infantry_slinger_b",
            r.ResolveReplacement("skirmish/units/default_infantry_ranged_b", "athen"));
        // athen.json: skirmish/structures/default_house_10 → structures/{civ}/house
        Assert.Equal("structures/athen/house",
            r.ResolveReplacement("skirmish/structures/default_house_10", "athen"));
    }

    [Fact]
    public void RealData_GeneralFallback_PerCivSubstitution()
    {
        var r = MakeRealReplacer();
        if (r == null) return;
        // default_infantry_melee_b.xml general = units/{civ}/infantry_spearman_b（civ 表无此项时）
        Assert.Equal("units/gaul/infantry_spearman_b",
            r.ResolveReplacement("skirmish/units/default_infantry_melee_b", "gaul"));
        // default_civil_centre.xml general = structures/{civ}/civil_centre
        Assert.Equal("structures/spart/civil_centre",
            r.ResolveReplacement("skirmish/structures/default_civil_centre", "spart"));
    }

    [Fact]
    public void RealData_SpecialStartingUnit_OnlySomeCivsGetOne()
    {
        var r = MakeRealReplacer();
        if (r == null) return;
        // maur.json 有映射（战象）;athen 无映射且模板无 general → 销毁
        Assert.Equal("units/maur/support_elephant",
            r.ResolveReplacement("skirmish/units/special_starting_unit", "maur"));
        Assert.Null(r.ResolveReplacement("skirmish/units/special_starting_unit", "athen"));
    }
}
