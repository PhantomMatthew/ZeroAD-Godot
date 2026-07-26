using System;
using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot.Actors.Composition;

using ZeroAD.Godot.Actors.Variation;
using ZeroAD.Godot.Actors.Parsing;

public sealed class ActorComposer
{
    private const int MaxPropDepth = 5;

    private readonly HashSet<string> _warnedActors = new();
    private readonly HashSet<string> _warnedAttachpoints = new();

    public Node3D BuildStructural(ResolvedActorSpec spec, int depth = 0)
    {
        var root = new Node3D();
        root.SetMeta(LayerMeta.ActorPath, spec.ActorPath);
        if (!string.IsNullOrEmpty(spec.MeshGlbPath))
            root.SetMeta(LayerMeta.MeshGlbPath, spec.MeshGlbPath!);

        Node3D? instance = null;
        if (!string.IsNullOrEmpty(spec.MeshGlbPath))
            instance = LoadAndInstantiateGlb(spec.MeshGlbPath!);

        if (instance == null)
        {
            root.AddChild(MakeFallbackBox(Colors.White));
            string meshReason = string.IsNullOrEmpty(spec.MeshGlbPath)
                ? "no-mesh-resolved"
                : $"glb-load-failed:{spec.MeshGlbPath}";
            ZeroAD.Godot.Actors.ActorDiagnostics.Fallback(spec.ActorPath, meshReason);
            WarnActorOnce(spec.ActorPath, $"BuildStructural: {meshReason}; using fallback box");
            return root;
        }

        root.AddChild(instance);

        // NOTE: animations are NOT loaded here. BuildStructural's result is packed into a
        // PackedScene by ComposedSceneCache and re-instantiated per spawn; PackedScene only
        // serializes Godot-native properties, so ManualAnimator's C# clip state would be
        // lost. Clips are attached to the final instance in ActorLoader.Instantiate instead.
        var skeleton = AttachpointResolver.FindSkeleton(instance);

        if (depth < MaxPropDepth)
        {
            foreach (var kv in spec.Props)
            {
                string attachpoint = kv.Key;
                var propSpec = kv.Value;

                var childSpec = ResolveChildSpec(propSpec);
                if (childSpec == null) continue;

                var childNode = BuildStructural(childSpec, depth + 1);
                AttachProp(root, instance, skeleton, attachpoint, childNode, spec.ActorPath);
            }
        }

        return root;
    }

    private void AttachProp(Node3D root, Node3D instance, Skeleton3D? skeleton, string attachpoint, Node3D childNode, string actorPath)
    {
        if (!AttachPropAt(root, skeleton, attachpoint, childNode))
            WarnAttachpointOnce(actorPath, attachpoint);
    }

    /// <summary>Attaches a prop at the given attachpoint: bone attachment when the
    /// skeleton has a matching bone, prop-point node otherwise, unit root as last
    /// resort. Tags the child with <see cref="LayerMeta.PropAttachpoint"/> so
    /// StatePropSwitcher can find base props later. Returns false when no real
    /// attachpoint matched (child attached to root).</summary>
    public static bool AttachPropAt(Node3D root, Skeleton3D? skeleton, string attachpoint, Node3D childNode)
    {
        childNode.SetMeta(LayerMeta.PropAttachpoint, attachpoint);

        if (skeleton != null)
        {
            int boneIdx = AttachpointResolver.FindBoneIndex(skeleton, attachpoint);
            if (boneIdx != -1)
            {
                // Static prop meshes carry a baked -90°X from the DAE→GLB
                // conversion (DAE prop-frame +Z becomes GLB +Y — bounds-verified).
                // Whether to undo it depends on the bone class: LEAF prop bones
                // (helmet, weapon_R/L, shield_arm, ammo...) kept the DAE joint
                // frame through conversion, so their props need +90°X; CHAIN
                // bones (head, neck, spine) were reoriented by Blender's
                // Y-along-child convention, which already cancels the bake —
                // compensating them double-rotates (-90°X put faces skyward,
                // +90°X planted them on the ground; raw/identity was correct).
                if (!attachpoint.Equals("head", StringComparison.OrdinalIgnoreCase))
                    childNode.Rotation = new Vector3(Mathf.Pi / 2f, 0f, 0f);
                var ba = new BoneAttachment3D();
                skeleton.AddChild(ba);
                ba.BoneIdx = boneIdx;
                ba.AddChild(childNode);
                return true;
            }
        }

        var attachNode = AttachpointResolver.FindNode(root, attachpoint);
        if (attachNode != null)
        {
            // Match the C++ engine: PMDConvert::AddStaticPropPoints decomposes each
            // prop-point's world transform into translation + rotation and DISCARDS the
            // scale (PropPoint stores only t and q, never scale). So a prop_* node that
            // carries a non-unity scale (e.g. sparta_civic_center's prop_bush at 2.41x)
            // must NOT pass that scale on to the attached prop, or trees/shields/flags get
            // inflated. Force the attachpoint's own scale to 1 (keeps position+rotation)
            // via the Scale property so it applies regardless of how the transform was
            // authored, then attach the child at 1:1.
            attachNode.Scale = Vector3.One;
            attachNode.AddChild(childNode);
            return true;
        }

        root.AddChild(childNode);
        return false;
    }

    public Node3D Compose(ResolvedActorSpec spec, Color teamColor, int depth = 0)
    {
        var root = BuildStructural(spec, depth);
        InstanceCustomizer.Apply(root, spec, teamColor, entitySeed: 0);
        TryPlayIdle(root);
        return root;
    }

    public static void SetAnimationState(Node3D instance, string state)
    {
        if (string.IsNullOrEmpty(state)) return;
        var animator = ModelLibrary.FindManualAnimator(instance);
        animator?.Play(state);
        var switcher = StatePropSwitcher.Find(instance);
        switcher?.Apply(state);
    }

    private static readonly HashSet<string> _animWarned = new();

    // Parsed DAE clips keyed by resolved animation path — shared across every unit
    // (same DAE → same keyframes). Avoids re-parsing 200+ tracks per spawn.
    private static readonly Dictionary<string, ZeroAD.Godot.SkeletalAnim.AnimClip> _clipCache = new();

    public static void TryLoadExternalAnimations(Node3D baseInstance, IReadOnlyList<AnimRef> animations)
    {
        if (animations.Count == 0) return;

        var skeleton = AttachpointResolver.FindSkeleton(baseInstance);
        if (skeleton == null) return; // props (capes etc.) have no skeleton — skip.

        var animator = new ZeroAD.Godot.SkeletalAnim.ManualAnimator { Name = "ManualAnimator" };
        baseInstance.AddChild(animator);

        var clips = new Dictionary<string, ZeroAD.Godot.SkeletalAnim.AnimClip>(System.StringComparer.OrdinalIgnoreCase);
        var added = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var anim in animations)
        {
            string stateName = anim.Name.ToLowerInvariant();
            // All same-name candidates are tried in actor order until one resolves —
            // a missing/stale conversion must not cost the whole animation state.
            if (added.Contains(stateName)) continue;

            var resolved = AssetPathResolver.Instance.ResolveAnimation(anim.File);
            if (!resolved.Found || resolved.Value == null) continue;

            var clip = LoadClip(resolved.Value);
            if (clip == null) continue;

            clips[stateName] = clip;
            added.Add(stateName);
        }

        animator.Init(skeleton, clips);
    }

    /// <summary>Loads + parses a DAE/GLB animation scene into an <see cref="ZeroAD.Godot.SkeletalAnim.AnimClip"/>,
    /// cached by resolved path.</summary>
    private static ZeroAD.Godot.SkeletalAnim.AnimClip? LoadClip(string resolvedPath)
    {
        if (_clipCache.TryGetValue(resolvedPath, out var cached)) return cached;

        var scene = ModelLibrary.LoadAnimationScene(resolvedPath);
        if (scene == null) { _clipCache[resolvedPath] = null!; return null; }

        var temp = scene.Instantiate();
        if (temp == null) { _clipCache[resolvedPath] = null!; return null; }

        ZeroAD.Godot.SkeletalAnim.AnimClip? result = null;
        try
        {
            var src = ModelLibrary.FindAnimationPlayer(temp);
            // The DAE's own skeleton carries the reference rest rotations used to
            // correct the Blender-converted mesh GLB's frame mismatch.
            var daeSkel = AttachpointResolver.FindSkeleton(temp);
            if (src != null)
            {
                // A GLB can carry several actions (e.g. gather_wood.dae has two); Blender's
                // gltf export often emits an empty placeholder as the first one. Pick the
                // animation with the most tracks rather than the first, so the real clip wins.
                Animation? best = null;
                int bestTracks = 0;
                foreach (var libNameVar in src.GetAnimationLibraryList())
                {
                    var lib = src.GetAnimationLibrary(libNameVar.ToString());
                    if (lib == null) continue;
                    foreach (var animNameVar in lib.GetAnimationList())
                    {
                        var a = lib.GetAnimation(animNameVar.ToString());
                        if (a != null && a.GetTrackCount() > bestTracks)
                        {
                            best = a;
                            bestTracks = a.GetTrackCount();
                        }
                    }
                }
                if (best != null)
                    result = ZeroAD.Godot.SkeletalAnim.AnimClipParser.Parse(best, daeSkel);
            }
        }
        finally
        {
            temp.QueueFree();
        }

        _clipCache[resolvedPath] = result!;
        return result;
    }

    public static void LoadAnimationClips(Node3D baseInstance, IEnumerable<string> animGlbRelPaths)
    {
        // Legacy entry point — delegate to the manual-animator pipeline.
        var refs = new List<AnimRef>();
        foreach (var p in animGlbRelPaths)
            if (!string.IsNullOrEmpty(p)) refs.Add(new AnimRef("clip", p, 1));
        if (refs.Count > 0) TryLoadExternalAnimations(baseInstance, refs);
    }

    internal static ResolvedActorSpec? ResolveChildSpec(PropSpec prop) =>
        SpecMerger.MergeFromActorPath(
            ActorLoader.ResolveActorAbsPath(prop.ActorPath),
            prop.SubSeed,
            AssetPathResolver.Instance);

    private static Node3D? LoadAndInstantiateGlb(string relGlbPath)
    {
        var scene = ModelLibrary.LoadGlb(relGlbPath);
        if (scene == null) return null;
        // Never rescale here: the original engine ignores DAE <unit> metadata and
        // treats raw coordinates as game meters. GLBs are repaired to match that
        // convention by godot/tools/fix_glb_unit_scale.py; runtime scale hacks
        // reintroduce the proportion bugs that script exists to fix.
        return scene.Instantiate<Node3D>();
    }

    internal static void TryPlayIdle(Node3D instance)
    {
        var animator = ModelLibrary.FindManualAnimator(instance);
        if (animator == null) return;
        if (animator.HasState("idle")) SetAnimationState(instance, "idle");
    }

    public static MeshInstance3D MakeFallbackBox(Color color)
    {
        var mi = new MeshInstance3D();
        var box = new BoxMesh { Size = new Vector3(1.5f, 2f, 1.5f) };
        mi.Mesh = box;
        var mat = new StandardMaterial3D { AlbedoColor = color };
        mi.MaterialOverride = mat;
        mi.Position = new Vector3(0, 1f, 0);
        return mi;
    }

    private void WarnActorOnce(string actor, string message)
    {
        if (_warnedActors.Add(actor + message))
            GD.PushWarning(message);
    }

    private void WarnAttachpointOnce(string actor, string attachpoint)
    {
        if (_warnedAttachpoints.Add(actor + "|" + attachpoint))
            GD.PushWarning($"ActorComposer: attachpoint '{attachpoint}' not found in '{actor}'");
    }
}
