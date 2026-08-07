using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Content;
using Xunit;
using Xunit.Abstractions;

namespace ZeroAD.Sim.Tests;

/// <summary>全量模板 ExtractStats 扫荡:模板解析任何一处抛异常(如 Attack/ApplyStatus
/// 解析的边缘形态)都会在游戏里打爆无保护的调用方(HUD 建造面板逐模板 ExtractStats
/// → 整面板刷新中断 = "无法建造")。此测试把全数据树跑一遍,把这类回归挡在 CI。</summary>
public sealed class AllTemplatesParseTests
{
    private readonly ITestOutputHelper _out;
    public AllTemplatesParseTests(ITestOutputHelper output) => _out = output;

    private static string? FindRepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, relative);
    }

    [Fact]
    public void AllTemplates_ExtractStats_NoThrow()
    {
        var root = FindRepoPath("binaries/data/mods/public/simulation/templates");
        if (root == null) return;   // 数据树未拉取则跳过

        var loader = new TemplateLoader(root);
        var failures = new SortedSet<string>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal);
        int count = 0;
        foreach (var file in files)
        {
            string rel = Path.GetRelativePath(root, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            string name = rel[..^".xml".Length];
            count++;
            try
            {
                loader.ExtractStats(name);
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (var f in failures) _out.WriteLine("PARSE FAIL: " + f);
        _out.WriteLine($"parsed {count} templates, {failures.Count} failures");
        Assert.True(count > 1000, $"sanity: expected thousands of templates, got {count}");
        Assert.Empty(failures);
    }
}
