using System;
using System.IO;
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim.RL;

/// <summary>Locate the staged public mod (templates + technologies) without Godot.
/// Prefers <c>godot/export/data/mods/public</c> walking up from the process, then
/// <c>ZEROAD_DATA</c> / an explicit config path.</summary>
public static class RlDataRoot
{
    public static string? FindModsPublic(string? explicitRoot = null)
    {
        if (TryModsPublic(explicitRoot, out var hit)) return hit;
        string? env = Environment.GetEnvironmentVariable("ZEROAD_DATA");
        if (TryModsPublic(env, out hit)) return hit;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string staged = Path.Combine(dir.FullName, "godot", "export", "data", "mods", "public");
            if (Directory.Exists(Path.Combine(staged, "simulation", "templates")))
                return staged;
            dir = dir.Parent;
        }
        return null;
    }

    private static bool TryModsPublic(string? root, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(root)) return false;
        if (Directory.Exists(Path.Combine(root, "simulation", "templates")))
        {
            path = root;
            return true;
        }
        string nested = Path.Combine(root, "mods", "public");
        if (Directory.Exists(Path.Combine(nested, "simulation", "templates")))
        {
            path = nested;
            return true;
        }
        string export = Path.Combine(root, "godot", "export", "data", "mods", "public");
        if (Directory.Exists(Path.Combine(export, "simulation", "templates")))
        {
            path = export;
            return true;
        }
        return false;
    }
}

internal static class RlContentCache
{
    private static readonly object Gate = new();
    private static string? _root;
    private static TemplateLoader? _templates;
    private static TechCatalog? _techs;
    private static RlCatalog? _catalog;

    public static bool TryGet(string? dataRoot,
        out TemplateLoader templates, out TechCatalog techs, out RlCatalog catalog)
    {
        templates = null!;
        techs = null!;
        catalog = null!;
        string? root = RlDataRoot.FindModsPublic(dataRoot);
        if (root == null) return false;
        lock (Gate)
        {
            if (_templates != null && _root == root)
            {
                templates = _templates;
                techs = _techs!;
                catalog = _catalog!;
                return true;
            }
            var loader = new TemplateLoader(Path.Combine(root, "simulation", "templates"));
            var tech = TechnologyLoader.LoadAll(Path.Combine(root, "simulation", "data", "technologies"));
            var cat = RlCatalog.FromNames(loader.EnumerateTemplateNames(), tech);
            _root = root;
            _templates = loader;
            _techs = tech;
            _catalog = cat;
            templates = loader;
            techs = tech;
            catalog = cat;
            return true;
        }
    }
}
