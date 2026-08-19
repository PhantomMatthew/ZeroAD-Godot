using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeroAD.Godot.Actors.Variation;

using ZeroAD.Godot.Actors.Parsing;

public sealed record ResolvedActorSpec(
	string ActorPath,
	string? MeshGlbPath,                            // remapped via AssetPathResolver (null on miss)
	IReadOnlyDictionary<string, string> Textures,   // sampler -> resolved png path
	IReadOnlyList<KeyValuePair<string, PropSpec>> Props, // 保序 attachpoint->prop;同 attachpoint
	                                                // 多个并存(对齐原版 multimap,雅典 CC 7 个 root
	                                                // 装饰 prop 全保留;KeyValuePair 兼容下游 kv.Key/Value)
	IReadOnlyList<AnimRef> Animations,
	string? Material,
	bool CastShadow,
	IReadOnlyDictionary<string, StatePropDelta> StateProps, // animation-state name -> prop delta
	DecalSpec? Decal = null,                        // <decal/> 贴花(替代 mesh 渲染)
	string? Particles = null);                      // <particles/> 粒子(跳过渲染)

public sealed record PropSpec(string ActorPath, int SubSeed);

/// <summary>Per-animation-state prop changes from state-named variants (e.g. gather_tree.xml):
/// <see cref="Adds"/> attach a prop actor for the duration of the state (axe while chopping),
/// <see cref="Clears"/> hide base props at those attachpoints (weapons/shield).</summary>
public sealed record StatePropDelta(
	IReadOnlyDictionary<string, PropSpec> Adds,
	IReadOnlySet<string> Clears);

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
		DecalSpec? decal = null;
		string? particles = null;
		var textures = new Dictionary<string, string>();
		var props = new List<KeyValuePair<string, PropSpec>>();
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
			if (v.Decal != null) decal = v.Decal;
			if (v.Particles != null) particles = v.Particles;

			foreach (var kv in v.Textures)
				textures[kv.Key] = kv.Value;

			// C++ multimap erase+insert(ObjectBase.cpp:493-497 双循环):先把本 variant
			// 涉及的所有 attachpoint 的旧条目一次性移除(erase(key) 删同 key 全部),
			// 再把本 variant 的 prop 全加进去(同 attachpoint 多个并存——雅典 CC 的 7 个
			// root 装饰 prop)。绝不能在单循环里逐 prop RemoveAll,否则同 attachpoint 的
			// 后一个 prop 会把前一个刚加的删掉,只剩最后一个。clear 条目(null ActorPath)
			// 只参与移除不新增。
			var touched = new HashSet<string>();
			foreach (var kv in v.Props)
				touched.Add(kv.Attachpoint);
			props.RemoveAll(p => touched.Contains(p.Key));
			foreach (var kv in v.Props)
			{
				if (kv.ActorPath != null)
					props.Add(new KeyValuePair<string, PropSpec>(kv.Attachpoint,
						new PropSpec(kv.ActorPath!, HashCode.Combine(seed, kv.Attachpoint))));
			}
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

        // State-named variants also carry prop deltas: adds (axe while chopping)
        // and clears (hide weapon/shield). Keyed by VARIANT NAME ONLY, one
        // variant per key, first in actor order wins — never merged across
        // files and never aliased by animation name. The locomotion clips
        // inside approach_*/carry_* variants are named "Idle"/"Walk"/"Run",
        // so animation-name aliasing merged their weapon/shield/helmet clears
        // into the plain idle/walk states (soldiers idled bare-headed and
        // unarmed). Variant names ARE the state names across the board
        // (female_gather_tree.xml declares name="gather_tree"), and each
        // actor references its own file set, so per-actor resolution stays
        // exact. CHOSEN variants are skipped — their props are already
        // attached as base props; re-registering them as state adds would
        // spawn duplicates (e.g. the head prop on every idle/walk entry).
        var stateProps = new Dictionary<string, StatePropDelta>(StringComparer.OrdinalIgnoreCase);
        for (int gi = 0; gi < doc.Groups.Count; gi++)
        {
            var variants = doc.Groups[gi].Variants;
            int chosenIdx = gi < chosen.Count ? chosen[gi] : -1;
            for (int vi = 0; vi < variants.Count; vi++)
            {
                if (vi == chosenIdx) continue;
                var v = variants[vi];
                if (v.Props.Count == 0) continue;
                if (string.IsNullOrEmpty(v.Name)) continue;
                if (stateProps.ContainsKey(v.Name)) continue;

			var adds = new Dictionary<string, PropSpec>();
			var clears = new HashSet<string>();
			foreach (var kv in v.Props)
			{
				if (kv.ActorPath == null)
					clears.Add(kv.Attachpoint);
				else
					adds[kv.Attachpoint] = new PropSpec(kv.ActorPath!, HashCode.Combine(seed, kv.Attachpoint));
			}
			stateProps[v.Name] = new StatePropDelta(adds, clears);
            }
        }

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
            castShadow,
            stateProps,
            decal,
            particles);
    }

    public static ResolvedActorSpec? MergeFromActorPath(
        string absActorPath,
        int seed,
        AssetPathResolver paths,
        VariationResolver.Selections? selections = null)
    {
        var doc = ActorDocCache.GetOrLoad(absActorPath);
        if (doc == null) return null;
        var chosen = VariationResolver.Resolve(doc, seed, selections ?? VariationResolver.IdleOnly);
        return Merge(doc, chosen, paths, seed);
    }
}
