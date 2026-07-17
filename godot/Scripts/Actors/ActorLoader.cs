using System;
using System.Collections.Generic;
using System.IO;
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
        string abs = ResolveActorAbsPath(actorRelPath);
        var spec = SpecMerger.MergeFromActorPath(abs, seed, AssetPathResolver.Instance);
        if (spec == null) return null;

        string sig = StructuralSignature.Compute(spec);
        string key = abs + "#" + sig;

        var scene = ComposedSceneCache.Instance.GetOrBuild(
            key, () => _composer.BuildStructural(spec));

        var instance = scene.Instantiate<Node3D>();
        InstanceCustomizer.Apply(instance, spec, teamColor, seed);
        ActorComposer.TryPlayIdle(instance);
        return instance;
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
            GD.PushWarning($"ActorLoader: failed to parse template '{abs}': {ex.Message}");
            return null;
        }
    }
}
