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

        var scriptPath = Path.Combine(
            ProjectSettings.GlobalizePath("res://"),
            "tools", "convert_dae_to_gltf.py");

        if (!File.Exists(scriptPath))
        {
            GD.PrintErr($"Conversion script not found: {scriptPath}");
            return;
        }

        var args = $"--background --python \"{scriptPath}\" -- " +
                   $"--input \"{meshesDir}\" " +
                   $"--output \"{outputDir}\" " +
                   (skeletonsDir != null ? $"--skeletons \"{skeletonsDir}\" " : "") +
                   $"--filter \"{filter}\" " +
                   (maxFiles > 0 ? $"--max {maxFiles} " : "") +
                   (dryRun ? "--dry-run" : "");

        GD.Print($"Running: {blenderPath} {args}");

        var psi = new ProcessStartInfo
        {
            FileName = blenderPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            GD.PrintErr("Failed to start Blender process");
            return;
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        GD.Print($"Blender output:\n{stdout}");
        if (!string.IsNullOrEmpty(stderr))
            GD.PrintErr($"Blender stderr:\n{stderr}");

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

    public static bool IsBlenderAvailable(string blenderPath = "blender")
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = blenderPath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
