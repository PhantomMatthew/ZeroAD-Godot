using System.Collections.Generic;

namespace ZeroAD.Godot.Actors.Composition;

using ZeroAD.Godot.Actors.Parsing;

public sealed record ActorLayerInfo(
    string ActorPath,
    string? MeshGlbPath,
    IReadOnlyList<string> BaseTexVariants,
    string? NormTexPath,
    string? SpecTexPath,
    string? Material,
    IReadOnlyList<ColorVec> ColorVariants);

public static class ActorLayerInfoCache
{
    private static readonly Dictionary<string, ActorLayerInfo> _cache = new();
    private static readonly object _lock = new();

    public static ActorLayerInfo Get(string absActorPath, string? meshGlbPath)
    {
        string key = absActorPath + "\0" + (meshGlbPath ?? string.Empty);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var info = Compute(absActorPath, meshGlbPath);
        lock (_lock)
        {
            _cache[key] = info;
        }
        return info;
    }

    private static ActorLayerInfo Compute(string absActorPath, string? meshGlbPath)
    {
        var doc = ActorDocCache.GetOrLoad(absActorPath);
        if (doc == null)
            return new ActorLayerInfo(
                absActorPath,
                meshGlbPath,
                System.Array.Empty<string>(),
                NormTexPath: null,
                SpecTexPath: null,
                Material: null,
                ColorVariants: System.Array.Empty<ColorVec>());

        var paths = AssetPathResolver.Instance;
        var variants = new HashSet<string>();
        var colors = new List<ColorVec>();
        string? normTex = null;
        string? specTex = null;

        foreach (var g in doc.Groups)
        {
            foreach (var v in g.Variants)
            {
                if (!string.IsNullOrEmpty(v.Mesh))
                {
                    var vr = paths.ResolveMesh(v.Mesh!);
                    if (vr.Found && vr.Value != null && vr.Value != meshGlbPath)
                        continue;
                }

                if (v.Color is ColorVec cv && !colors.Contains(cv))
                    colors.Add(cv);

                if (v.Textures.TryGetValue("baseTex", out var rawTex))
                {
                    var tr = paths.ResolveTexture(rawTex);
                    if (tr.Found && tr.Value != null)
                        variants.Add(tr.Value);
                }

                // First non-null resolved normTex/specTex wins (no per-instance randomization).
                if (normTex == null && v.Textures.TryGetValue("normTex", out var rawNorm))
                {
                    var nr = paths.ResolveTexture(rawNorm);
                    if (nr.Found && nr.Value != null) normTex = nr.Value;
                }
                if (specTex == null && v.Textures.TryGetValue("specTex", out var rawSpec))
                {
                    var sr = paths.ResolveTexture(rawSpec);
                    if (sr.Found && sr.Value != null) specTex = sr.Value;
                }
            }
        }

        return new ActorLayerInfo(
            absActorPath,
            meshGlbPath,
            new List<string>(variants),
            normTex,
            specTex,
            doc.Material,
            colors);
    }
}
