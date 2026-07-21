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

        TryLoadExternalAnimations(instance, spec.Animations);

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
        if (skeleton != null)
        {
            int boneIdx = AttachpointResolver.FindBoneIndex(skeleton, attachpoint);
            if (boneIdx != -1)
            {
                var ba = new BoneAttachment3D();
                skeleton.AddChild(ba);
                ba.BoneIdx = boneIdx;
                ba.AddChild(childNode);
                return;
            }
        }

        var attachNode = AttachpointResolver.FindNode(instance, attachpoint);
        if (attachNode != null)
        {
            attachNode.AddChild(childNode);
        }
        else
        {
            root.AddChild(childNode);
            WarnAttachpointOnce(actorPath, attachpoint);
        }
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
        var player = ModelLibrary.FindAnimationPlayer(instance);
        if (player == null) return;
        string clip = ModelLibrary.ResolveClip(player, state);
        if (!string.IsNullOrEmpty(clip))
            player.Play(clip);
    }

    private static readonly HashSet<string> _animWarned = new();

    private static void TryLoadExternalAnimations(Node3D baseInstance, IReadOnlyList<AnimRef> animations)
    {
        if (animations.Count == 0) return;
        var target = ModelLibrary.FindAnimationPlayer(baseInstance);
        if (target == null) return;

        if (!target.HasAnimationLibrary(""))
            target.AddAnimationLibrary("", new AnimationLibrary());
        var lib = target.GetAnimationLibrary("");

        var added = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var anim in animations)
        {
            string stateName = anim.Name.ToLowerInvariant();
            if (added.Contains(stateName)) continue;

            var resolved = AssetPathResolver.Instance.ResolveAnimation(anim.File);
            if (!resolved.Found || resolved.Value == null) continue;

            var scene = ModelLibrary.LoadGlb(resolved.Value);
            if (scene == null) continue;

            var temp = scene.Instantiate();
            if (temp == null) continue;

            try
            {
                var src = ModelLibrary.FindAnimationPlayer(temp);
                if (src == null) continue;

                foreach (var libNameVar in src.GetAnimationLibraryList())
                {
                    var srcLib = src.GetAnimationLibrary(libNameVar.ToString());
                    if (srcLib == null) continue;
                    foreach (var animNameVar in srcLib.GetAnimationList())
                    {
                        string an = animNameVar.ToString();
                        if (!srcLib.HasAnimation(an)) continue;
                        var clip = srcLib.GetAnimation(an);
                        if (clip == null) continue;

                        var dup = (Animation)clip.Duplicate();
                        if (stateName.Contains("idle") || stateName.Contains("walk") || stateName.Contains("trot"))
                            dup.LoopMode = Animation.LoopModeEnum.Linear;

                        string clipName = stateName;
                        if (lib.HasAnimation(clipName))
                            clipName = stateName + "_" + added.Count;
                        lib.AddAnimation(clipName, dup);
                        added.Add(stateName);
                        break;
                    }
                    break;
                }
            }
            finally
            {
                temp.QueueFree();
            }
        }
    }

    public static void LoadAnimationClips(Node3D baseInstance, IEnumerable<string> animGlbRelPaths)
    {
        var target = ModelLibrary.FindAnimationPlayer(baseInstance);
        if (target == null) return;

        foreach (var relPath in animGlbRelPaths)
        {
            if (string.IsNullOrEmpty(relPath)) continue;
            var scene = ModelLibrary.LoadGlb(relPath);
            if (scene == null) continue;
            var temp = scene.Instantiate();
            if (temp == null) continue;

            try
            {
                var src = ModelLibrary.FindAnimationPlayer(temp);
                if (src == null) continue;
                foreach (var libNameVar in src.GetAnimationLibraryList())
                {
                    string libName = libNameVar.ToString();
                    var lib = src.GetAnimationLibrary(libName);
                    if (lib == null) continue;

                    if (target.HasAnimationLibrary(libName))
                    {
                        var existing = target.GetAnimationLibrary(libName);
                        foreach (var animNameVar in lib.GetAnimationList())
                        {
                            string animName = animNameVar.ToString();
                            if (!existing.HasAnimation(animName))
                                existing.AddAnimation(animName, lib.GetAnimation(animName));
                        }
                    }
                    else
                    {
                        target.AddAnimationLibrary(libName, lib);
                    }
                }
            }
            finally
            {
                temp.QueueFree();
            }
        }
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
        var player = ModelLibrary.FindAnimationPlayer(instance);
        if (player == null) return;
        string clip = ModelLibrary.ResolveClip(player, "idle");
        if (!string.IsNullOrEmpty(clip))
            player.Play(clip);
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
