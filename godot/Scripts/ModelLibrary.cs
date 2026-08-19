using Godot;
using System;
using System.Collections.Generic;

namespace ZeroAD.Godot;

using ZeroAD.Godot.Actors;

public static class ModelLibrary
{
    private static readonly Dictionary<string, PackedScene?> _glbCache = new();
    private static readonly Dictionary<string, PackedScene?> _animGlbCache = new();

    private static readonly string _meshesRoot = ProjectSettings.GlobalizePath("res://assets/meshes");
    private static readonly string _animationsRoot = ProjectSettings.GlobalizePath("res://assets/animations");

    public static Node3D? InstantiateForTemplate(string template, float x, float z, Color? teamColor,
        Actors.Variation.VariationResolver.Selections? selections = null)
    {
        var color = teamColor ?? new Color(0.7f, 0.6f, 0.4f);

        var actorNode = TryInstantiateViaActorSystem(template, x, z, color, selections);
        if (actorNode != null)
            return actorNode;

        // Actor system miss: fall back to SimBridge's EntityMeshFactory by returning null.
        return null;
    }

    private static Node3D? TryInstantiateViaActorSystem(string template, float x, float z, Color color,
        Actors.Variation.VariationResolver.Selections? selections)
    {
        var actorPath = ActorLoader.ExtractActorFromTemplate(template);
        if (string.IsNullOrEmpty(actorPath))
        {
            ZeroAD.Godot.Actors.ActorDiagnostics.Fallback(template, "no-VisualActor");
            return null;
        }

        int seed = (template.GetHashCode(), x.GetHashCode(), z.GetHashCode()).GetHashCode();
        var node = ActorLoader.Instance.Instantiate(actorPath!, seed, color, selections);
        if (node == null)
        {
            ZeroAD.Godot.Actors.ActorDiagnostics.Fallback(template, $"actor-instantiate-failed -> {actorPath}");
            return null;
        }

        ZeroAD.Godot.Actors.ActorDiagnostics.Resolved(template, actorPath!);

        float y = TerrainHeightService.Sample(x, z);
        node.Position = new Vector3(x, y, z);

        var animator = FindManualAnimator(node);
        if (animator != null && animator.HasState("idle"))
        {
            animator.Play("idle");
            // Desync units so a group doesn't animate in lockstep.
            animator.Advance((float)GD.Randf() * 2.0f);
        }
        return node;
    }

    public static Node3D? TryInstantiate(string kind, Color teamColor) =>
        InstantiateForTemplate(kind, 0, 0, teamColor);

    public static bool IsAnimated(string kind) => kind.Contains("units/");

    /// <summary>
    /// Loads an animation source scene from <c>res://assets/animations</c> by extension.
    /// .glb goes through GltfDocument (headless, no import pass needed); .dae goes
    /// through ResourceLoader (Godot's native Collada import — needs the one-time
    /// editor import pass that produced the .import artifacts). Animation GLBs live
    /// under <c>assets/animations</c>, NOT <c>assets/meshes</c>, so they cannot share
    /// <see cref="LoadGlb"/> (which is rooted at the meshes dir).
    /// </summary>
    internal static PackedScene? LoadAnimationScene(string relPath)
    {
        if (relPath.EndsWith(".dae", StringComparison.OrdinalIgnoreCase))
        {
            if (_daeCache.TryGetValue(relPath, out var cachedDae))
                return cachedDae;
            var loaded = ResourceLoader.Load<PackedScene>("res://assets/animations/" + relPath);
            _daeCache[relPath] = loaded;
            return loaded;
        }

        if (_animGlbCache.TryGetValue(relPath, out var cachedGlb))
            return cachedGlb;

        string absPath = System.IO.Path.Combine(
            _animationsRoot, relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        PackedScene? result = null;
        if (System.IO.File.Exists(absPath))
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            if (doc.AppendFromFile(absPath, state) == Error.Ok)
            {
                var root = doc.GenerateScene(state);
                if (root != null)
                {
                    SetOwnerRecursive(root, root);
                    var packed = new PackedScene();
                    if (packed.Pack(root) == Error.Ok)
                        result = packed;
                    root.QueueFree();
                }
            }
        }
        _animGlbCache[relPath] = result;
        return result;
    }

    private static readonly Dictionary<string, PackedScene?> _daeCache = new();

    internal static PackedScene? LoadGlb(string relPath)
    {
        if (_glbCache.TryGetValue(relPath, out var cached))
            return cached;

        string absPath = System.IO.Path.Combine(_meshesRoot, relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        PackedScene? result = null;

        if (System.IO.File.Exists(absPath))
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            if (doc.AppendFromFile(absPath, state) == Error.Ok)
            {
                var root = doc.GenerateScene(state);
                if (root != null)
                {
                    SetOwnerRecursive(root, root);
                    var packed = new PackedScene();
                    if (packed.Pack(root) == Error.Ok)
                        result = packed;
                    root.QueueFree();
                }
            }
        }

        _glbCache[relPath] = result;
        return result;
    }

    private static void SetOwnerRecursive(Node node, Node root)
    {
        foreach (var child in node.GetChildren())
        {
            child.Owner = root;
            SetOwnerRecursive(child, root);
        }
    }

    public static AnimationPlayer? FindAnimationPlayer(Node node)
    {
        if (node is AnimationPlayer ap) return ap;
        foreach (var child in node.GetChildren())
        {
            var found = FindAnimationPlayer(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Finds the <see cref="ZeroAD.Godot.SkeletalAnim.ManualAnimator"/> attached by
    /// <c>ActorComposer</c>. This is the bone-driver used at runtime; AnimationPlayer
    /// is only consulted during clip parsing (Collada import).</summary>
    public static ZeroAD.Godot.SkeletalAnim.ManualAnimator? FindManualAnimator(Node node)
    {
        if (node is ZeroAD.Godot.SkeletalAnim.ManualAnimator ma) return ma;
        foreach (var child in node.GetChildren())
        {
            var found = FindManualAnimator(child);
            if (found != null) return found;
        }
        return null;
    }

    public static string ResolveClip(AnimationPlayer player, string want)
    {
        foreach (var name in player.GetAnimationList())
        {
            string n = name.ToString();
            if (n.Contains(want)) return n;
        }
        return "";
    }
}
