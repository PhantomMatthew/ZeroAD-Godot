using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace ZeroAD.Godot.Actors;

/// <summary>
/// Scans converted game assets under res://assets/ once (lazily, thread-safely) and
/// resolves original 0 A.D. art paths (DAE / PNG-with-skins-prefix) to Godot-relative paths.
/// </summary>
public sealed class AssetPathResolver
{
    public sealed record Result<T>(T? Value, bool Found, string OriginalPath)
    {
        public static Result<T> Miss(string originalPath) => new(default, false, originalPath);
    }

    private static readonly Lazy<AssetPathResolver> _instance = new(CreateInstance);
    public static AssetPathResolver Instance => _instance.Value;

    private readonly HashSet<string> _meshRelPaths;
    private readonly HashSet<string> _texRelPaths;
    private readonly HashSet<string> _warned = new();
    private readonly object _warnLock = new();

    private AssetPathResolver(HashSet<string> meshRelPaths, HashSet<string> texRelPaths)
    {
        _meshRelPaths = meshRelPaths;
        _texRelPaths = texRelPaths;
    }

    private static AssetPathResolver CreateInstance()
    {
        string assetsAbs = ProjectSettings.GlobalizePath("res://assets");
        var meshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string meshesRoot = Path.Combine(assetsAbs, "meshes");
        if (Directory.Exists(meshesRoot))
        {
            foreach (var f in Directory.GetFiles(meshesRoot, "*.glb", SearchOption.AllDirectories))
                if (!f.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
                    meshes.Add(Path.GetRelativePath(meshesRoot, f).Replace('\\', '/'));
        }

        string texRoot = Path.Combine(assetsAbs, "textures");
        if (Directory.Exists(texRoot))
        {
            foreach (var f in Directory.GetFiles(texRoot, "*.png", SearchOption.AllDirectories))
                if (!f.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
                    textures.Add(Path.GetRelativePath(texRoot, f).Replace('\\', '/'));
        }

        return new AssetPathResolver(meshes, textures);
    }

    public bool Exists(string relPath) =>
        _meshRelPaths.Contains(relPath) || _texRelPaths.Contains(relPath);

    public Result<string> ResolveMesh(string rawDaePath)
    {
        if (string.IsNullOrEmpty(rawDaePath))
            return Result<string>.Miss(rawDaePath);

        string glbRel = SwapOrStripExt(rawDaePath, ".glb");
        if (_meshRelPaths.Contains(glbRel))
            return new Result<string>(glbRel, true, rawDaePath);

        // Asset pipeline occasionally dropped the leading category dir; try basename fallback.
        string basenameFallback = Path.GetFileName(glbRel);
        if (_meshRelPaths.Contains(basenameFallback))
            return new Result<string>(basenameFallback, true, rawDaePath);

        WarnOnceMiss($"AssetPathResolver.ResolveMesh miss: '{rawDaePath}' (tried '{glbRel}', '{basenameFallback}')");
        return Result<string>.Miss(rawDaePath);
    }

    public Result<string> ResolveTexture(string rawTexPath)
    {
        if (string.IsNullOrEmpty(rawTexPath))
            return Result<string>.Miss(rawTexPath);

        // .dds inputs swap to .png (DDS→PNG conversion done in pipeline).
        string png = rawTexPath;
        if (png.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            png = Path.ChangeExtension(png, ".png");

        // C++ uses art/textures/skins/ prefix; try with skins/ first, then without.
        string withSkins = png.StartsWith("skins/", StringComparison.OrdinalIgnoreCase)
            ? png
            : "skins/" + png;
        if (_texRelPaths.Contains(withSkins))
            return new Result<string>(withSkins, true, rawTexPath);

        if (_texRelPaths.Contains(png))
            return new Result<string>(png, true, rawTexPath);

        // Pipeline flattened skins/ subdir: try skins/<basename> and plain <basename>.
        string basename = Path.GetFileName(png);
        string flatSkins = "skins/" + basename;
        if (_texRelPaths.Contains(flatSkins))
            return new Result<string>(flatSkins, true, rawTexPath);
        if (_texRelPaths.Contains(basename))
            return new Result<string>(basename, true, rawTexPath);

        WarnOnceMiss($"AssetPathResolver.ResolveTexture miss: '{rawTexPath}' (tried '{withSkins}', '{png}', '{flatSkins}', '{basename}')");
        return Result<string>.Miss(rawTexPath);
    }

    private static string SwapOrStripExt(string path, string newExt)
    {
        if (path.EndsWith(".dae", StringComparison.OrdinalIgnoreCase))
            return Path.ChangeExtension(path, newExt);
        if (path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
            return path;
        return Path.ChangeExtension(path, newExt);
    }

    private void WarnOnceMiss(string message)
    {
        lock (_warnLock)
        {
            if (!_warned.Add(message))
                return;
        }
        GD.PushWarning(message);
    }
}
