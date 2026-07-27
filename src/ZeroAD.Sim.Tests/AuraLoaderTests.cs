using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim.Tests;

/// <summary>AuraLoader 解析测试(真实数据 simulation/data/auras)。对齐 TechnologyLoaderTests 模式。</summary>
public sealed class AuraLoaderTests
{
    private static string RepoDir(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        Assert.True(dir != null, $"repo marker not found: {relative}");
        return Path.Combine(dir!.FullName, relative);
    }

    private static string AuraDir() =>
        RepoDir("binaries/data/mods/public/simulation/data/auras");

    [Fact]
    public void Loads_AllJsonFiles()
    {
        var catalog = AuraLoader.LoadAll(AuraDir());
        // MVP 收 range/global/player,3 个无 type 文件天然过滤。数据集 151 文件,148 ± 波动入集。
        Assert.True(catalog.Auras.Count > 50, $"expected dozens of auras, got {catalog.Auras.Count}");
    }

    [Fact]
    public void Key_Is_RelativePath_Not_BareFilename()
    {
        // template <Auras> token 是路径式(teambonuses/spart_player_teambonus),catalog key 必须同形。
        var catalog = AuraLoader.LoadAll(AuraDir());
        Assert.Contains("structures/farmstead_60", catalog.Auras.Keys);
    }

    [Fact]
    public void Parses_Range_Aura_Farmstead()
    {
        var catalog = AuraLoader.LoadAll(AuraDir());
        var def = catalog.Auras["structures/farmstead_60"];
        Assert.Equal("range", def.Type);
        Assert.True(def.Radius > 0, "range aura must have radius");
        Assert.NotEmpty(def.Modifications);
        // affectedPlayers 缺省 ["Player"](原版 Auras.js:116)
        Assert.Equal(new[] { "Player" }, def.AffectedPlayers);
    }

    [Fact]
    public void Modifications_Derive_Multiply_On_Farmstead()
    {
        var catalog = AuraLoader.LoadAll(AuraDir());
        var f = catalog.Auras["structures/farmstead_60"];
        // grain 采集速率 ×1.75(multiply 形态)。
        Assert.Contains(f.Modifications, m => m.Multiply.HasValue && m.Multiply.Value > 1f);
    }

    [Fact]
    public void Parses_Global_Aura()
    {
        var catalog = AuraLoader.LoadAll(AuraDir());
        var global = catalog.Auras.Values.FirstOrDefault(a => a.Type == "global");
        Assert.NotNull(global);
        Assert.Equal("global", global!.Type);
    }

    [Fact]
    public void Default_AffectedPlayers_Is_Player_When_Absent()
    {
        var catalog = AuraLoader.LoadAll(AuraDir());
        // farmstead_60 无 affectedPlayers 字段 → 缺省 ["Player"]。
        Assert.Equal(new[] { "Player" }, catalog.Auras["structures/farmstead_60"].AffectedPlayers);
    }

    [Fact]
    public void Only_Range_Global_Player_Types_Loaded()
    {
        var catalog = AuraLoader.LoadAll(AuraDir());
        // MVP 仅收三型;formation/garrison*/turreted* 跳过(内核无 holder 组件)。
        var types = catalog.Auras.Values.Select(a => a.Type).Distinct().ToHashSet();
        Assert.Subset(new HashSet<string> { "range", "global", "player" }, types);
    }

    [Fact]
    public void Bad_Directory_Returns_Empty_Not_Throw()
    {
        var catalog = AuraLoader.LoadAll("/nonexistent/auras/path");
        Assert.Empty(catalog.Auras);
    }
}
