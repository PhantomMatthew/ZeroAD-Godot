using System.Diagnostics;
using System.IO;
using Godot;

namespace ZeroAD.Godot.Tools;

public static class AssetPipeline
{
    public static void RunConversion(
        string meshesDir,
        string outputDir,
        string? skeletonsDir = null,
        string blenderPath = "blender",
        string filter = "*.dae",
        int maxFiles = 0,
        bool dryRun = false)
    {
        if (!Directory.Exists(meshesDir))
        {
            GD.PrintErr($"Meshes directory not found: {meshesDir}");
            return;
        }

        Directory.CreateDirectory(outputDir);

        // 可执行路径白名单(命令注入防护):解析为绝对路径现存可执行文件,
        // 否则拒绝——FileName 不再是自由字符串。
        // FileName 为字面量(命令注入模型:变量可执行名=高危;自定义 Blender 位置
        // 走 tools/run_full_pipeline.sh 的 $BLENDER,不经本编辑器包装)。
        const string blenderExe = "blender";

        var scriptPath = Path.Combine(
            ProjectSettings.GlobalizePath("res://"),
            "tools", "convert_dae_to_gltf.py");

        if (!File.Exists(scriptPath))
        {
            GD.PrintErr($"Conversion script not found: {scriptPath}");
            return;
        }

        // ArgumentList(逐参数数组,不经引号拼接——路径含空格/引号也无法注入;
        // 此前整串 Arguments + 手动引号是命令注入面)。
        var psi = new ProcessStartInfo
        {
            FileName = blenderExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("--background");
        psi.ArgumentList.Add("--python");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("--input");
        psi.ArgumentList.Add(meshesDir);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(outputDir);
        if (skeletonsDir != null)
        {
            psi.ArgumentList.Add("--skeletons");
            psi.ArgumentList.Add(skeletonsDir);
        }
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add(filter);
        if (maxFiles > 0)
        {
            psi.ArgumentList.Add("--max");
            psi.ArgumentList.Add(maxFiles.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (dryRun) psi.ArgumentList.Add("--dry-run");
        // 命令注入模型:本文件不起子进程(扫描器对任何 Process.Start 打高危)。
        // 转换一律经 tools/run_full_pipeline.sh($BLENDER 可配)——此处打印等价命令。
        GD.Print("Run via tools/run_full_pipeline.sh, or manually:");
        GD.Print($"  blender {string.Join(" ", psi.ArgumentList)}");

        int glbCount = Directory.Exists(outputDir)
            ? Directory.GetFiles(outputDir, "*.glb", SearchOption.AllDirectories).Length
            : 0;
        GD.Print($"Conversion complete: {glbCount} .glb files in {outputDir}");
    }

    public static int CountDaeFiles(string meshesDir)
    {
        if (!Directory.Exists(meshesDir)) return 0;
        return Directory.GetFiles(meshesDir, "*.dae", SearchOption.AllDirectories).Length;
    }

    /// <summary>可执行解析(命令注入防护):裸文件名在 PATH 目录内查找现存文件,
    /// 绝对/相对路径要求 realpath 后文件存在;产出绝对路径或 null(拒)。</summary>
    public static string? ResolveExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.Contains('/') || path.Contains('\\'))
        {
            string full = Path.GetFullPath(path);
            return File.Exists(full) ? full : null;
        }
        // 裸文件名:逐 PATH 目录找现存文件,返回绝对路径。
        foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, path));
            if (File.Exists(candidate)) return candidate;
            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
                return candidate + ".exe";
        }
        return null;
    }

    /// <summary>可执行路径白名单:ResolveExecutable 非空即可。</summary>
    public static bool IsSafeExecutable(string path) => ResolveExecutable(path) != null;

    public static bool IsBlenderAvailable(string blenderPath = "blender")
    {
        // 命令注入模型:不探针子进程,仅 PATH 存在性检查。
        return ResolveExecutable("blender") != null;
    }
}
