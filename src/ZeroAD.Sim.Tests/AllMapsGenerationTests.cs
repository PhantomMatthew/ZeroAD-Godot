using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;
using ZeroAD.Godot;   // PmpMap(csproj Compile Include;纯 C# 无 Godot 依赖)
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Rmgen;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.Rmgen.Maps;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 全地图生成扫雷:78 张 rmgen 图 × 多种子全生成,校验导出结构完整
/// (尺寸/高度/贴图索引/实体越界),并全量校验贴图名(terrain XML 注册表)
/// 与实体模板(junction templates)可解析性。任何一张图坏掉都点名。
/// </summary>
public sealed class AllMapsGenerationTests
{
    private readonly ITestOutputHelper _out;
    public AllMapsGenerationTests(ITestOutputHelper o) => _out = o;

    private static string? FindRepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, relative);
    }

    private static MapSettings MakeSettings(string? dataRoot, int numPlayers = 2)
    {
        var s = new MapSettings { Size = 192, Seed = 42, CircularMap = false, DataRoot = dataRoot };
        s.PlayerData.Add(new PlayerData { Civ = "gaia" });
        for (int p = 1; p <= numPlayers; p++)
            s.PlayerData.Add(new PlayerData { Civ = p == 1 ? "athen" : "gaul" });
        return s;
    }

    [Fact]
    public void AllMaps_Generate_WithoutException_AndStructurallyValid()
    {
        var root = FindRepoPath("binaries/data/mods/public");
        var failures = new List<string>();
        var maps = MapRegistry.AvailableMaps.ToList();
        _out.WriteLine($"sweeping {maps.Count} maps × 3 seeds");

        foreach (var name in maps)
        {
            foreach (uint seed in new uint[] { 1, 42, 1337 })
            {
                MapExport? export = null;
                try
                {
                    export = MapRegistry.Generate(name, new RmgenRng(seed), MakeSettings(root));
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}#{seed}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                    continue;
                }
                if (export == null) { failures.Add($"{name}#{seed}: null export"); continue; }

                int size = export.Size;
                if (size <= 0) failures.Add($"{name}#{seed}: Size={size}");
                if (export.Height.Length != (size + 1) * (size + 1))
                    failures.Add($"{name}#{seed}: Height={export.Height.Length} expected {(size + 1) * (size + 1)}");
                if (export.TileIndex.Length != size * size)
                    failures.Add($"{name}#{seed}: TileIndex={export.TileIndex.Length} expected {size * size}");
                int texCount = export.TextureNames.Count;
                if (texCount == 0) failures.Add($"{name}#{seed}: no textures");
                int badIdx = export.TileIndex.Count(i => i >= texCount);
                if (badIdx > 0) failures.Add($"{name}#{seed}: {badIdx} tile indices >= texture count");
                int badEnt = export.Entities.Count(e =>
                    e.Position.X < 0 || e.Position.Y < 0 || e.Position.X >= size || e.Position.Y >= size);
                if (badEnt > 0) failures.Add($"{name}#{seed}: {badEnt} entities out of bounds");
            }
        }

        foreach (var f in failures) _out.WriteLine("FAIL: " + f);
        Assert.Empty(failures);
    }

    [Fact]
    public void AllMaps_TextureNames_ResolveInTerrainRegistry()
    {
        var root = FindRepoPath("binaries/data/mods/public");
        if (root == null) return;

        // 上游地形名注册表 = art/terrains/**/*.xml 的文件名集(同 SplatBaker 的 XML 解析域)
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string terrainsRoot = Path.Combine(root, "art", "terrains");
        foreach (var xml in Directory.EnumerateFiles(terrainsRoot, "*.xml", SearchOption.AllDirectories))
            known.Add(Path.GetFileNameWithoutExtension(xml));
        // 直取 types 贴图名(直接命中路径)
        string typesRoot = Path.Combine(root, "art", "textures", "terrain");
        foreach (var png in Directory.EnumerateFiles(typesRoot, "*.png", SearchOption.AllDirectories))
            known.Add(Path.GetFileNameWithoutExtension(png));
        _out.WriteLine($"terrain registry: {known.Count} names");

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in MapRegistry.AvailableMaps)
        {
            var export = MapRegistry.Generate(name, new RmgenRng(42), MakeSettings(root));
            if (export == null) { missing.Add($"{name}: NULL EXPORT"); continue; }
            foreach (var tex in export.TextureNames)
                if (!known.Contains(tex))
                    missing.Add($"{name}: '{tex}'");
        }

        foreach (var m in missing) _out.WriteLine("MISSING: " + m);
        Assert.Empty(missing);
    }

    [Fact]
    public void AllMaps_EntityTemplates_Exist()
    {
        var root = FindRepoPath("binaries/data/mods/public/simulation/templates");
        if (root == null) return;

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in MapRegistry.AvailableMaps)
        {
            var export = MapRegistry.Generate(name, new RmgenRng(42), MakeSettings(root));
            if (export == null) continue;
            foreach (var ent in export.Entities)
            {
                string t = ent.TemplateName;
                if (t.StartsWith("actor|", StringComparison.Ordinal)) continue;   // 装饰物不走 sim 模板
                string rel = t.Replace('/', Path.DirectorySeparatorChar) + ".xml";
                if (!File.Exists(Path.Combine(root, rel)))
                    missing.Add($"{name}: '{t}'");
            }
        }

        foreach (var m in missing) _out.WriteLine("MISSING TEMPLATE: " + m);
        Assert.Empty(missing);
    }

    [Fact]
    public void AllPmpMaps_Load_AndXmlParses()
    {
        var mapsRoot = FindRepoPath("binaries/data/mods/public/maps");
        if (mapsRoot == null) return;

        var failures = new List<string>();
        int pmpCount = 0, xmlCount = 0;
        foreach (var dir in new[] { "scenarios", "skirmishes" })
        {
            string full = Path.Combine(mapsRoot, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var pmpPath in Directory.GetFiles(full, "*.pmp"))
            {
                string rel = $"{dir}/{Path.GetFileNameWithoutExtension(pmpPath)}";
                try
                {
                    var pmp = PmpMap.Load(pmpPath);
                    pmpCount++;
                    if (pmp.VerticesPerSide < 2)
                        failures.Add($"{rel}: VerticesPerSide={pmp.VerticesPerSide}");
                    if (pmp.Heightmap.Length != pmp.VerticesPerSide * pmp.VerticesPerSide)
                        failures.Add($"{rel}: heightmap {pmp.Heightmap.Length} != {pmp.VerticesPerSide}²");
                    if (pmp.TileTex1.Length != pmp.TilesPerSide * pmp.TilesPerSide)
                        failures.Add($"{rel}: tex1 {pmp.TileTex1.Length} != {pmp.TilesPerSide}²");
                    if (pmp.TileTex2.Length != pmp.TileTex1.Length)
                        failures.Add($"{rel}: tex2 {pmp.TileTex2.Length} != tex1 {pmp.TileTex1.Length}");
                }
                catch (Exception ex)
                {
                    failures.Add($"{rel}: PMP EXCEPTION {ex.GetType().Name}: {ex.Message}");
                }

                string xmlPath = Path.ChangeExtension(pmpPath, ".xml");
                if (!File.Exists(xmlPath)) { failures.Add($"{rel}: XML missing"); continue; }
                try
                {
                    var data = ScenarioLoader.Load(xmlPath);
                    xmlCount++;
                    // 0 实体是合法数据(空地形演示图如 temperate_map),不算缺陷;
                    // 本断言只保证 XML 可解析。
                    _ = data;
                }
                catch (Exception ex)
                {
                    failures.Add($"{rel}: XML EXCEPTION {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        _out.WriteLine($"pmp loaded: {pmpCount}, xml parsed: {xmlCount}");
        foreach (var f in failures) _out.WriteLine("FAIL: " + f);
        Assert.True(pmpCount > 100, $"expected 154 pmp maps, got {pmpCount}");
        Assert.Empty(failures);
    }

    [Fact]
    public void AllSkirmishMaps_ReplaceCleanly_ForEveryCiv()
    {
        var mapsRoot = FindRepoPath("binaries/data/mods/public");
        var templatesRoot = FindRepoPath("binaries/data/mods/public/simulation/templates");
        if (mapsRoot == null || templatesRoot == null) return;

        // 本版上游全文明(simulation/data/civs/*.json;无 pers——A27 波斯未列入)
        var civs = new[] { "achae", "athen", "brit", "cart", "gaul", "germ", "han", "iber",
                           "kush", "mace", "maur", "ptol", "rome", "sele", "spart" };
        var templates = new TemplateLoader(templatesRoot);
        string? civsRoot = SkirmishReplacer.CivsRootFromTemplatesRoot(templatesRoot);
        var failures = new List<string>();
        int mapsWithPlaceholders = 0;

        foreach (var xmlPath in Directory.GetFiles(Path.Combine(mapsRoot, "maps", "skirmishes"), "*.xml"))
        {
            string rel = Path.GetFileNameWithoutExtension(xmlPath);
            var data = ScenarioLoader.Load(xmlPath);
            if (!data.Entities.Any(e => e.Template.StartsWith("skirmish/", StringComparison.Ordinal)))
                continue;
            mapsWithPlaceholders++;

            foreach (var civ in civs)
            {
                // 每张图每个文明独立替换(替换器内部缓存 civ 表,可复用实例)
                var replacer = new SkirmishReplacer(templates, civsRoot);
                var copy = data.Entities.Select(e => new ScenarioEntityDef
                {
                    Uid = e.Uid, Template = e.Template, Player = e.Player,
                    X = e.X, Z = e.Z, OrientationY = e.OrientationY,
                    IsActor = e.IsActor, IsSimulationEntity = e.IsSimulationEntity,
                }).ToList();
                replacer.Apply(copy, pid => pid == 0 ? "gaia" : civ);

                foreach (var ent in copy)
                {
                    if (ent.Template.StartsWith("skirmish/", StringComparison.Ordinal))
                        failures.Add($"{rel}/{civ}: unreplaced '{ent.Template}'");
                    else if (ent.IsSimulationEntity &&
                             !templates.TemplateExists(ent.Template) &&
                             !ent.Template.StartsWith("actor/", StringComparison.Ordinal))
                        failures.Add($"{rel}/{civ}: replaced to missing template '{ent.Template}'");
                }
            }
        }
        _out.WriteLine($"skirmish maps with placeholders: {mapsWithPlaceholders}, civs swept: {civs.Length}");
        foreach (var f in failures.Take(30)) _out.WriteLine("FAIL: " + f);
        Assert.True(mapsWithPlaceholders > 0, "no skirmish placeholders found — sweep is not exercising anything");
        Assert.Empty(failures);
    }
}
