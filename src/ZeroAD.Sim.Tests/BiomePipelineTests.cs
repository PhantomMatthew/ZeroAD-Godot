using System;
using System.IO;
using System.Linq;
using Xunit;
using ZeroAD.Sim.Rmgen;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.Rmgen.Maps;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// biome 刷漆管线回归测试(BiomeSet 加载 + mainland.js 式森林地表/分层斑块)。
/// 锁住"每图 2 贴图"的简化版回退:管线完成后 mainland 必须产出 10+ 贴图、
/// 森林地表贴图进表、biome 树模板进实体表,且同种子两次生成逐位一致(确定性)。
/// </summary>
public sealed class BiomePipelineTests
{
    private static string? FindRepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, relative);
    }

    private static MapSettings MakeSettings(string? dataRoot, BiomeSet? biome = null)
    {
        var s = new MapSettings { Size = 192, Seed = 42, CircularMap = false, DataRoot = dataRoot, BiomeData = biome };
        s.PlayerData.Add(new PlayerData { Civ = "gaia" });
        s.PlayerData.Add(new PlayerData { Civ = "athen" });
        s.PlayerData.Add(new PlayerData { Civ = "gaul" });
        return s;
    }

    [Fact]
    public void BiomeLoader_TemperateOverlay_ProducesKnownVariants()
    {
        // 无数据根 → 内置默认 + .js 覆盖层;两个 randBool 分支的地形名都必须合法
        var a = BiomeLoader.Load(null, "generic/temperate", new RmgenRng(1));
        var b = BiomeLoader.Load(null, "generic/temperate", new RmgenRng(2));
        Assert.StartsWith("temperate_grass", a.MainTerrain0);
        Assert.StartsWith("temperate_forestfloor", a.ForestFloor1);
        Assert.StartsWith("gaia/tree/", a.Tree1);
        Assert.StartsWith("temperate_grass", b.MainTerrain0);
    }

    [Fact]
    public void BiomeLoader_FromJunction_LoadsRealJson()
    {
        var root = FindRepoPath("binaries/data/mods/public");
        if (root == null) return;   // junction 缺失按惯例跳过
        var alpine = BiomeLoader.Load(root, "generic/alpine", new RmgenRng(1));
        Assert.Equal("alpine_forestfloor_01", alpine.ForestFloor1);
        Assert.StartsWith("steppe_grass", alpine.MainTerrain0);   // alpine.json: steppe_grass_02
    }

    [Fact]
    public void Mainland_GeneratesRichTextureSet_NotJustBasePlusCliff()
    {
        var root = FindRepoPath("binaries/data/mods/public");
        // 固定 temperate(随机 biome 会落在小色板生物群系如 savanna——那是对的,不该按
        // temperate 的贴图数量断言);管线后应有:main, road, roadWild, cliff×2,
        // hill×2, ff1, ff2, tier1-4, dirt×2 ≈ 14
        var biome = BiomeLoader.Load(root, "generic/temperate", new RmgenRng(42));
        var export = MapRegistry.Generate("mainland", new RmgenRng(42), MakeSettings(root, biome));
        Assert.NotNull(export);

        Assert.True(export!.TextureNames.Count >= 10,
            $"expected >=10 textures, got {export.TextureNames.Count}: {string.Join(",", export.TextureNames)}");

        // 森林地表贴图必须进表(森林不再是"草地上摆树")
        Assert.Contains(export.TextureNames, n => n.Contains("forestfloor", StringComparison.OrdinalIgnoreCase));

        // 基地 road 贴图必须进表(CityPatch)
        Assert.Contains(export.TextureNames, n => n.Contains("paving", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Mainland_BiomeEntities_ArePlaced()
    {
        var root = FindRepoPath("binaries/data/mods/public");
        var biome = BiomeLoader.Load(root, "generic/temperate", new RmgenRng(42));
        var export = MapRegistry.Generate("mainland", new RmgenRng(42), MakeSettings(root, biome));
        Assert.NotNull(export);
        // biome 树模板( temperate 默认 oak 系)出现在实体表
        Assert.Contains(export!.Entities, e => e.TemplateName.StartsWith("gaia/tree/", StringComparison.Ordinal));
        // 玩家 CC + 起始单位
        Assert.Contains(export.Entities, e => e.TemplateName == "structures/athen/civil_centre" && e.PlayerID == 1);
        Assert.Contains(export.Entities, e => e.TemplateName.StartsWith("units/athen/", StringComparison.Ordinal) && e.PlayerID == 1);
    }

    [Fact]
    public void Mainland_SameSeed_IsBitIdentical()
    {
        var root = FindRepoPath("binaries/data/mods/public");
        var a = MapRegistry.Generate("mainland", new RmgenRng(7), MakeSettings(root));
        var b = MapRegistry.Generate("mainland", new RmgenRng(7), MakeSettings(root));
        Assert.NotNull(a); Assert.NotNull(b);
        Assert.Equal(a!.TextureNames, b!.TextureNames);
        Assert.Equal(a.TileIndex, b.TileIndex);
        Assert.Equal(a.Height, b.Height);
        Assert.Equal(a.Entities.Select(e => (e.TemplateName, e.PlayerID, e.Position.X, e.Position.Y)).ToList(),
                     b.Entities.Select(e => (e.TemplateName, e.PlayerID, e.Position.X, e.Position.Y)).ToList());
    }

    [Fact]
    public void DeepForest_And_ThemedMaps_RespectBiomeWhitelist()
    {
        var root = FindRepoPath("binaries/data/mods/public");
        // alpine_lakes 白名单只有 alpine → 贴图必须带 alpine 系
        var alpine = MapRegistry.Generate("alpine_lakes", new RmgenRng(9), MakeSettings(root));
        Assert.NotNull(alpine);
        Assert.Contains(alpine!.TextureNames, n => n.StartsWith("alpine", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("steppe", StringComparison.OrdinalIgnoreCase));
    }
}
