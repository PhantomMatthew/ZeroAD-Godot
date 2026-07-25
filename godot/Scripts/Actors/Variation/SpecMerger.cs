using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeroAD.Godot.Actors.Variation;

using ZeroAD.Godot.Actors.Parsing;

public sealed record ResolvedActorSpec(
    string ActorPath,
    string? MeshGlbPath,                            // remapped via AssetPathResolver (null on miss)
    IReadOnlyDictionary<string, string> Textures,   // sampler -> resolved png path
    IReadOnlyDictionary<string, PropSpec> Props,    // attachpoint -> prop (later groups win)
    IReadOnlyList<AnimRef> Animations,
    string? Material,
    bool CastShadow);

public sealed record PropSpec(string ActorPath, int SubSeed);

/// <summary>
/// Walks an <see cref="ActorDoc"/> in group order with chosen variant indices, accumulating
/// mesh/textures/props/animations (later groups override earlier — matches C++ erase+insert).
/// Remaps mesh and texture paths through <see cref="AssetPathResolver"/>.
/// </summary>
public static class SpecMerger
{
    public static ResolvedActorSpec Merge(
        ActorDoc doc,
        IReadOnlyList<int> chosen,
        AssetPathResolver paths,
        int seed)
    {
        string? mesh = null;
        string? material = doc.Material;
        bool castShadow = doc.CastShadow;
        var textures = new Dictionary<string, string>();
        var props = new Dictionary<string, PropSpec>();
        var anims = new List<AnimRef>();

        for (int gi = 0; gi < doc.Groups.Count; gi++)
        {
            if (gi >= chosen.Count) break;
            int idx = chosen[gi];
            if (idx < 0) continue;
            var variants = doc.Groups[gi].Variants;
            if (idx >= variants.Count) continue;
            var v = variants[idx];

            if (!string.IsNullOrEmpty(v.Mesh)) mesh = v.Mesh;
            if (!string.IsNullOrEmpty(v.Material)) material = v.Material;

            foreach (var kv in v.Textures)
                textures[kv.Key] = kv.Value;

            // C++ erase+insert: later group fully replaces attachpoint entry.
            foreach (var kv in v.Props)
                props[kv.Key] = new PropSpec(kv.Value.ActorPath, HashCode.Combine(seed, kv.Key));
        }

        // Animations are merged across ALL variants of ALL groups, not just the
        // chosen visual variant. The original re-runs variant selection per
        // animation state (ObjectBase::CalculateVariation matches variants by
        // state name — "gather_tree", "Build", ...), so every state-named
        // variant's clips must be reachable from one spec. Mesh/props/textures
        // still come from the chosen variant only. Name clashes (idle/walk/run
        // exist in base, carry-* and combat-stance variants alike) keep ALL
        // candidates in actor order — the composer picks the first whose source
        // file resolves, so a missing conversion never costs a whole state.
        var animSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in doc.Groups)
            foreach (var v in group.Variants)
                foreach (var a in v.Animations)
                    if (animSeen.Add(a.Name + "|" + a.File))
                        anims.Add(a);

        string? meshGlb = null;
        if (!string.IsNullOrEmpty(mesh))
        {
            var r = paths.ResolveMesh(mesh!);
            if (r.Found && r.Value != null) meshGlb = r.Value;
        }

        var remappedTex = new Dictionary<string, string>(textures.Count);
        foreach (var kv in textures)
        {
            var r = paths.ResolveTexture(kv.Value);
            if (r.Found && r.Value != null)
                remappedTex[kv.Key] = r.Value;
        }

        return new ResolvedActorSpec(
            doc.Path,
            meshGlb,
            remappedTex,
            props,
            anims,
            material,
            castShadow);
    }

    public static ResolvedActorSpec? MergeFromActorPath(
        string absActorPath,
        int seed,
        AssetPathResolver paths)
    {
        var doc = ActorDocCache.GetOrLoad(absActorPath);
        if (doc == null) return null;
        var chosen = VariationResolver.Resolve(doc, seed, VariationResolver.IdleOnly);
        return Merge(doc, chosen, paths, seed);
    }
}
