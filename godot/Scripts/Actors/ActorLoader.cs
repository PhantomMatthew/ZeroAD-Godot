using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Godot;

namespace ZeroAD.Godot.Actors;

using ZeroAD.Godot.Actors.Composition;
using ZeroAD.Godot.Actors.Parsing;
using ZeroAD.Godot.Actors.Variation;

/// <summary>
/// Top-level entry point: maps a 0 A.D. actor path (e.g. "units/athenians/cavalry_swordsman_b_m.xml")
/// to a fully composed Godot Node3D tree with props and material applied.
/// </summary>
public sealed class ActorLoader
{
    public static readonly ActorLoader Instance = new();

    private static readonly Lazy<string> _artRootLazy = new(ComputeAndPropagateArtRoot);
    public static string ArtRoot => _artRootLazy.Value;

    private static string ComputeAndPropagateArtRoot()
    {
        string parent = ProjectSettings.GlobalizePath("res://..");
        string root = Path.GetFullPath(Path.Combine(parent, "binaries/data/mods/public/art/"));
        ActorParser.ArtRoot = root; // propagate so variant-file resolution uses the same root
        return root;
    }

    private readonly ActorComposer _composer = new();

    private static readonly Dictionary<string, string?> _templateActorCache = new();
    private static readonly object _templateCacheLock = new();

    public Node3D? Instantiate(string actorRelPath, int seed, Color teamColor)
    {
        actorRelPath = ResolvePlaceholders(actorRelPath, seed);
        string abs = ResolveActorAbsPath(actorRelPath);
        var spec = SpecMerger.MergeFromActorPath(abs, seed, AssetPathResolver.Instance);
        if (spec == null) return null;

        string sig = StructuralSignature.Compute(spec);
        string key = abs + "#" + sig;

        var scene = ComposedSceneCache.Instance.GetOrBuild(
            key, () => _composer.BuildStructural(spec));

        var instance = scene.Instantiate<Node3D>();
        InstanceCustomizer.Apply(instance, spec, teamColor, seed);
        // Attach animations to the final instance (not during BuildStructural, whose
        // result is packed into a PackedScene by the cache and would lose C# clip state).
        ActorComposer.TryLoadExternalAnimations(instance, spec.Animations);
        // Same reasoning for the per-state prop switcher (axe while chopping etc.).
        StatePropSwitcher.Attach(instance, spec, teamColor, seed);
        ActorComposer.TryPlayIdle(instance);
        return instance;
    }

    private static string ResolvePlaceholders(string actorRelPath, int seed)
    {
        if (actorRelPath.Contains("{phenotype}"))
        {
            string first = (Math.Abs(seed) % 2 == 0) ? "female" : "male";
            string second = first == "male" ? "female" : "male";
            string a = actorRelPath.Replace("{phenotype}", first);
            if (ActorFileExists(a)) return a;
            string b = actorRelPath.Replace("{phenotype}", second);
            if (ActorFileExists(b)) return b;
            return a;
        }
        if (actorRelPath.Contains("{civ}"))
        {
            string? resolved = ResolveCivGlob(actorRelPath);
            if (resolved != null) return resolved;
        }
        return actorRelPath;
    }

    private static bool ActorFileExists(string actorRelPath) =>
        File.Exists(ResolveActorAbsPath(actorRelPath));

    private static string? ResolveCivGlob(string actorRelPath)
    {
        int idx = actorRelPath.IndexOf("{civ}", StringComparison.Ordinal);
        if (idx < 0) return actorRelPath;
        string prefix = actorRelPath[..idx];
        string suffix = actorRelPath[(idx + "{civ}".Length)..];
        string dir = Path.GetFullPath(Path.Combine(ArtRoot, "actors", prefix));
        string dirOnly = Path.GetDirectoryName(dir) ?? dir;
        if (!Directory.Exists(dirOnly)) return null;
        string pattern = Path.GetFileName(prefix) + "*" + suffix;
        var match = Directory.GetFiles(dirOnly, pattern).FirstOrDefault();
        if (match == null) return null;
        string rel = Path.GetRelativePath(Path.GetFullPath(Path.Combine(ArtRoot, "actors")), match).Replace('\\', '/');
        return rel;
    }

    /// <summary>Combines <see cref="ArtRoot"/>/actors/ with the given repo-relative actor path.</summary>
    public static string ResolveActorAbsPath(string actorRelPath)
    {
        string p = actorRelPath.Replace('\\', '/');
        if (p.StartsWith("actors/", StringComparison.OrdinalIgnoreCase))
            p = p["actors/".Length..];
        return Path.GetFullPath(Path.Combine(ArtRoot, "actors", p));
    }

    /// <summary>
    /// Reads simulation/templates/&lt;template&gt;.xml and returns the &lt;VisualActor&gt;&lt;Actor&gt; value
    /// or null. Caches results per template path.
    /// </summary>
    public static string? ExtractActorFromTemplate(string template)
    {
        string rel = template.Replace('\\', '/');

        if (rel.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            if (rel.StartsWith("actors/", StringComparison.OrdinalIgnoreCase))
                rel = rel["actors/".Length..];
            else if (rel.StartsWith("actor/", StringComparison.OrdinalIgnoreCase))
                rel = rel["actor/".Length..];
            return rel;
        }

        lock (_templateCacheLock)
        {
            if (_templateActorCache.TryGetValue(template, out var cached))
                return cached;
        }

        string? result = ComputeActorFromTemplate(template);
        lock (_templateCacheLock)
        {
            _templateActorCache[template] = result;
        }
        return result;
    }

    private static string? ComputeActorFromTemplate(string template)
    {
        string templatesRoot = Path.GetFullPath(Path.Combine(
            ProjectSettings.GlobalizePath("res://.."),
            "binaries/data/mods/public/simulation/templates"));
        string rel = template.Replace('\\', '/');
        string abs = Path.GetFullPath(Path.Combine(templatesRoot, rel + ".xml"));
        if (!File.Exists(abs))
            return null;

        try
        {
            var doc = XDocument.Load(abs);
            var actorEl = doc.Root?.Element("VisualActor")?.Element("Actor");
            var value = actorEl?.Value?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception ex)
        {
            ZeroAD.Sim.Diag.Warn("Actor", $"ActorLoader: failed to parse template '{abs}': {ex.Message}");
            return null;
        }
    }
}
