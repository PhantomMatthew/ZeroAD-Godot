using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.Rmgen.Maps;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 单人游戏真实配置扫雷(流式):84 图 × {128,256,512} × {2,4,8}人 × 5 布置,
/// 复刻 MapPickerPanel 可选组合。每条配置先生成、立即落盘(START/结果各一行,
/// flush 即时),挂起配置由 90s watchdog 标记 TIMEOUT 并跳过——
/// 上一轮扫雷在某配置上 56 分钟无输出,此版为定位该挂点而生。
/// 报告:/tmp/rmgen_sweep.log(逐行追加,可 tail -f)。
/// </summary>
public sealed class RmgenConfigSweepTests
{
    private const string LogPath = "/tmp/rmgen_sweep.log";
    private static readonly TimeSpan PerConfigTimeout = TimeSpan.FromSeconds(90);
    private static readonly object Gate = new();

    private static string? FindRepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, relative);
    }

    private static MapSettings MakeSettings(string? dataRoot, int size, int numPlayers,
        string placement, bool twoTeams, uint seed)
    {
        var s = new MapSettings
        {
            Size = size, Seed = seed, CircularMap = false,
            DataRoot = dataRoot, PlayerPlacement = placement,
        };
        s.PlayerData.Add(new PlayerData { Civ = "gaia" });
        for (int p = 1; p <= numPlayers; p++)
            s.PlayerData.Add(new PlayerData
            {
                Civ = p == 1 ? "athen" : "gaul",
                Team = twoTeams ? (p - 1) % 2 : -1,
            });
        return s;
    }

    private static void Line(string text)
    {
        lock (Gate) File.AppendAllText(LogPath, text + "\n");
    }

    /// <summary>单次生成,带 watchdog;返回 null=成功,否则错误描述。</summary>
    private static string? TryGenerate(string name, string? root, int size, int players,
        string placement, bool twoTeams, uint seed)
    {
        var task = Task.Run(() =>
        {
            try
            {
                var settings = MakeSettings(root, size, players, placement, twoTeams, seed);
                MapRegistry.Generate(name, new RmgenRng(seed), settings);
                return (string?)null;
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
            }
        });
        if (task.Wait(PerConfigTimeout))
            return task.Result;
        return "TIMEOUT(>90s, 疑似无界重试环)";
    }

    [Fact(Skip = "手动诊断扫雷:8820 配置全量约 80 分钟。结论已固化:仅 coast_range " +
        "river/stronghold + >2 队 FFA 失败(上游同款抛错);排查进图回归时去掉 Skip 运行。")]
    public void Sweep_AllMaps_AllPickerConfigs()
    {
        var root = FindRepoPath("binaries/data/mods/public");
        int[] sizes = { 192, 320, 384, 448 };     // 第一轮已覆盖 128/256/512
        int[] playerCounts = { 2, 4, 8 };
        string[] placements = { "circle", "river", "groupedLines", "randomGroup", "stronghold" };

        File.WriteAllText(LogPath, $"=== sweep start {DateTime.Now:HH:mm:ss} ===\n");
        int total = 0, fails = 0, timeouts = 0;

        foreach (int size in sizes)
        foreach (var name in MapRegistry.AvailableMaps.OrderBy(n => n))
        foreach (int players in playerCounts)
        foreach (var placement in placements)
        {
            total++;
            string cfg = $"{name} {size}t/{players}p/{placement}";
            Line($"START {cfg}");

            string? err = TryGenerate(name, root, size, players, placement, twoTeams: false, seed: 42);
            if (err == null)
            {
                Line($"  ok  {cfg}");
                continue;
            }
            if (err.StartsWith("TIMEOUT")) timeouts++;
            fails++;

            // 失败配置:换种子一次 + 2 队复跑一次,区分种子敏感 / >2队闸门 / 结构性
            string? seedErr = TryGenerate(name, root, size, players, placement, false, 1337);
            string? teamErr = TryGenerate(name, root, size, players, placement, true, 42);
            string note = err;
            if (seedErr == null) note += " [换种子可通过]";
            if (teamErr == null) note += " [2队可通过]";
            else if (teamErr != err && !teamErr.StartsWith("TIMEOUT")) note += $" [2队仍败:{teamErr}]";
            Line($"FAIL  {cfg}: {note}");
        }

        Line($"=== sweep end {DateTime.Now:HH:mm:ss}: {total} configs, {fails} failures ({timeouts} timeouts) ===");
    }
}
