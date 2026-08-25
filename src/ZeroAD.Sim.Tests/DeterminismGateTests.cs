using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// 确定性门禁(PORTING-GAPS §2):内核源码禁止出现跨平台不确定构造。
/// 超越函数(Math.Sin/Cos/Pow/Atan… 及 MathF 变体)各平台 libm 实现低位可能不同,
/// 会造成状态哈希漂移(OOS);非 Rand48 随机源与时钟读取同理。
/// 允许:IEEE 精确运算(Sqrt/Floor/Ceiling/Round/Abs/Min/Max/Clamp/基本算术)。
/// 白名单文件:SafeMath(确定性重实现的内部)、Fixed/Trig(定点数学库自身)。
/// </summary>
public sealed class DeterminismGateTests
{
    // 超越函数白名单:文件名(不含路径前缀,含目录以精确定位)。
    private static readonly HashSet<string> Whitelist = new(StringComparer.Ordinal)
    {
        "RmgenMath/SafeMath.cs",
        "Maths/Fixed.cs",
        "Maths/Trig.cs",
    };

    // 按文件豁免(良性用途,逐条注明理由;值均不回流 sim 状态):
    // - Diag.cs / LongPathfinder.cs / PathfinderComponent.cs / UnitAI.cs / Headquarters.cs:
    //   "clock" — 时间戳/Stopwatch 仅日志与性能探针累加,不参与任何 sim 判定。
    // - Serialization/Serializer.cs:"random" — Crypto 是 HashSerializer 状态哈希,非随机源。
    private static readonly Dictionary<string, string[]> PerFileExemptions = new()
    {
        ["Diag.cs"] = new[] { "clock" },
        ["Pathfinding/LongPathfinder.cs"] = new[] { "clock" },
        ["Components/PathfinderComponent.cs"] = new[] { "clock" },
        ["Components/UnitAI.cs"] = new[] { "clock" },
        ["AI/Petra/Headquarters.cs"] = new[] { "clock" },
        ["Serialization/Serializer.cs"] = new[] { "random" },
    };

    // 非确定调用:libm 超越函数(Math/MathF 均拦)。
    private static readonly Regex Transcendental = new(
        @"(System\.Math|MathF|\bMath)\.(Sin|Cos|Tan|Asin|Acos|Atan2?|Sinh|Cosh|Tanh|Pow|Log2?|Log10|Exp|Cbrt|FusedMultiplyAdd)\s*\(",
        RegexOptions.Compiled);

    // 非确定随机源:必须走 Rand48(cm.RNG / RmgenRng)。
    private static readonly Regex BadRandom = new(
        @"new\s+Random\s*\(|Random\.Shared|System\.Security\.Cryptography",
        RegexOptions.Compiled);

    // 时钟读取:回合内任何时间采样都破坏锁步。
    private static readonly Regex BadClock = new(
        @"DateTime\.(Now|UtcNow|Today)|Environment\.TickCount|Stopwatch\.",
        RegexOptions.Compiled);

    [Fact]
    public void KernelSourcesContainNoNonDeterministicCalls()
    {
        string kernelRoot = FindKernelRoot();
        Assert.True(Directory.Exists(kernelRoot), $"kernel source dir not found: {kernelRoot}");

        var violations = new List<string>();
        int filesChecked = 0;
        foreach (string path in Directory.GetFiles(kernelRoot, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(kernelRoot, path).Replace('\\', '/');
            if (Whitelist.Contains(rel)) continue;
            bool exemptClock = PerFileExemptions.TryGetValue(rel, out var exempt)
                && Array.IndexOf(exempt, "clock") >= 0;
            bool exemptRandom = exempt != null && Array.IndexOf(exempt, "random") >= 0;

            filesChecked++;
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = StripComment(lines[i]);
                Check(violations, rel, i + 1, line, Transcendental, "libm 超越函数(用 Trig/SafeMath)");
                if (!exemptRandom)
                    Check(violations, rel, i + 1, line, BadRandom, "非 Rand48 随机源");
                if (!exemptClock)
                    Check(violations, rel, i + 1, line, BadClock, "时钟读取");
            }
        }

        Assert.True(filesChecked > 50,
            $"suspiciously few files scanned ({filesChecked}) — source root mislocated?");
        Assert.Empty(violations);
    }

    private static void Check(List<string> violations, string rel, int lineNo, string line,
        Regex pattern, string what)
    {
        if (pattern.IsMatch(line))
            violations.Add($"{rel}:{lineNo} [{what}] {line.Trim()}");
    }

    /// <summary>去掉行注释与字符串字面量(降低误报;逐行处理足够门禁用途)。</summary>
    private static string StripComment(string line)
    {
        int idx = line.IndexOf("//", StringComparison.Ordinal);
        if (idx >= 0) line = line[..idx];
        int s = line.IndexOf('"');
        while (s >= 0)
        {
            int e = line.IndexOf('"', s + 1);
            if (e < 0) break;
            line = line[..s] + "\"\"" + line[(e + 1)..];
            s = line.IndexOf('"', s + 2);
        }
        return line;
    }

    private static string FindKernelRoot()
    {
        // 从测试程序集位置向上走,找到仓库根(src/ZeroAD.Sim.Tests/bin/… → 仓库根)。
        string? dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            string candidate = Path.Combine(dir, "src", "ZeroAD.Sim");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        // 回退:相对当前目录(仓库根直接 dotnet test 时)。
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "ZeroAD.Sim"));
    }
}
