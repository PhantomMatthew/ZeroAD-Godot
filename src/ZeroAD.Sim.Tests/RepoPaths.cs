using System;
using System.IO;

namespace ZeroAD.Sim.Tests;

/// <summary>测试 fixture 数据根解析:一律优先读仓库内暂存数据(godot/export/data,
/// godot/tools/stage_release_data.sh 生成),不再依赖 binaries/ 上游软链。
/// 以 "binaries/" 开头的相对路径先映射到 "godot/export/" 下同路径探测,找不到
/// 再按原相对路径探测(向后兼容仍有 binaries/ 的环境)。从测试程序集目录逐级向上找。</summary>
public static class RepoPaths
{
    public static string? Resolve(string relative)
    {
        string staged = relative.StartsWith("binaries/", StringComparison.Ordinal)
            ? "godot/export/" + relative.Substring("binaries/".Length)
            : relative;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string stagedCandidate = Path.Combine(dir.FullName, staged);
            if (Directory.Exists(stagedCandidate) || File.Exists(stagedCandidate))
                return stagedCandidate;
            string candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate) || File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}

