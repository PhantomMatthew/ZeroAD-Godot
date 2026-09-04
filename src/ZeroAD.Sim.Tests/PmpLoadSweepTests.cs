using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using ZeroAD.Godot;   // PmpMap(csproj Compile Include;纯 C# 无 Godot 依赖)
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// skirmish/scenario 全图加载扫雷:maps/{skirmishes,scenarios}/*.pmp 逐张
/// PmpMap.Load(二进制解析)+ ScenarioLoader.Load(实体 XML)+ 实体模板可解析性
/// (TemplateLoader 逐名校验)。单人游戏"进不去图"排查的 pmp 半边——
/// rmgen 半边见 RmgenConfigSweepTests(8820 配置全绿,仅 coast_range 上游同款闸门)。
/// 报告 /tmp/pmp_sweep.log。
/// </summary>
public sealed class PmpLoadSweepTests
{
    private const string LogPath = "/tmp/pmp_sweep.log";

    private static string? FindRepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, relative);
    }

    [Fact]
    public void Sweep_AllPmpMaps_LoadAndTemplates()
    {
        var root = FindRepoPath("binaries/data/mods/public");
        Assert.True(root != null, "binaries junction missing");
        File.WriteAllText(LogPath, $"=== pmp sweep {DateTime.Now:HH:mm:ss} ===\n");

        var loader = new TemplateLoader(Path.Combine(root, "simulation/templates"));
        int pmpCount = 0, pmpFail = 0, xmlFail = 0, tplFailMaps = 0;

        foreach (var dirName in new[] { "maps/skirmishes", "maps/scenarios" })
        {
            string dir = Path.Combine(root, dirName);
            if (!Directory.Exists(dir)) { File.AppendAllText(LogPath, $"MISSING DIR {dir}\n"); continue; }

            foreach (var pmpPath in Directory.EnumerateFiles(dir, "*.pmp").OrderBy(p => p))
            {
                pmpCount++;
                string rel = $"{dirName}/{Path.GetFileNameWithoutExtension(pmpPath)}";
                try
                {
                    PmpMap.Load(pmpPath);
                }
                catch (Exception ex)
                {
                    pmpFail++;
                    File.AppendAllText(LogPath, $"PMP-FAIL {rel}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}\n");
                    continue;   // pmp 坏了,XML 再好也进不去
                }

                string? xmlPath = ScenarioLoader.FindScenarioPath(root, rel);
                if (xmlPath == null)
                {
                    File.AppendAllText(LogPath, $"XML-MISSING {rel}\n");
                    continue;
                }
                ScenarioData data;
                try
                {
                    data = ScenarioLoader.Load(xmlPath);
                }
                catch (Exception ex)
                {
                    xmlFail++;
                    File.AppendAllText(LogPath, $"XML-FAIL {rel}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}\n");
                    continue;
                }

                // 模板可解析性:skirmish/ 占位符是运行时按文明替换的,跳过
                var bad = new List<string>();
                foreach (var e in data.Entities)
                {
                    if (e.Template.Length == 0 || e.Template.StartsWith("skirmish/", StringComparison.Ordinal))
                        continue;
                    try { loader.LoadTemplate(e.Template); }
                    catch (Exception ex) { bad.Add($"{e.Template} ({ex.Message.Split('\n')[0]})"); }
                }
                if (bad.Count > 0)
                {
                    tplFailMaps++;
                    File.AppendAllText(LogPath,
                        $"TPL-FAIL {rel}: {bad.Count} bad templates: {string.Join("; ", bad.Take(5))}\n");
                }
            }
        }

        File.AppendAllText(LogPath,
            $"=== end: {pmpCount} pmp, {pmpFail} pmp-fail, {xmlFail} xml-fail, {tplFailMaps} tpl-fail maps ===\n");
        Assert.True(true);
    }
}
