using System.Collections.Generic;
using System.IO;
using Godot;

namespace ZeroAD.Godot.Actors.Composition;

using ZeroAD.Godot.Actors.Variation;
using ZeroAD.Godot.Actors.Parsing;

/// <summary>
/// Builds a Godot Node3D tree from a <see cref="ResolvedActorSpec"/>: instantiates the mesh GLB,
/// applies team-color/base-texture material, recursively attaches props at their attachpoints.
/// Phase-1: standard material only, no normal/spec maps, idle animation only.
/// </summary>
public sealed class ActorComposer
{
    private const int MaxPropDepth = 5;

    private readonly HashSet<string> _warnedActors = new();
    private readonly HashSet<string> _warnedAttachpoints = new();

    public Node3D Compose(ResolvedActorSpec spec, Color teamColor, int depth = 0)
    {
        var root = new Node3D();
        root.SetMeta("actorPath", spec.ActorPath);

        if (string.IsNullOrEmpty(spec.MeshGlbPath))
        {
            root.AddChild(MakeFallbackBox(teamColor));
            WarnActorOnce(spec.ActorPath, "Compose: no mesh resolved; using fallback box");
            return root;
        }

        var instance = LoadAndInstantiateGlb(spec.MeshGlbPath!);
        if (instance == null)
        {
            root.AddChild(MakeFallbackBox(teamColor));
            WarnActorOnce(spec.ActorPath, $"Compose: GLB load failed for '{spec.MeshGlbPath}'; using fallback box");
            return root;
        }
        root.AddChild(instance);

        ApplyMaterial(instance, spec, teamColor);
        TryPlayIdle(instance);

        if (depth < MaxPropDepth)
        {
            foreach (var kv in spec.Props)
            {
                string attachpoint = kv.Key;
                var propSpec = kv.Value;

                var childSpec = ResolveChildSpec(propSpec);
                if (childSpec == null) continue;

                var childNode = Compose(childSpec, teamColor, depth + 1);
                var attachNode = AttachpointResolver.FindAttachpoint(instance, attachpoint);
                if (attachNode != null)
                {
                    attachNode.AddChild(childNode);
                }
                else
                {
                    root.AddChild(childNode);
                    WarnAttachpointOnce(spec.ActorPath, attachpoint);
                }
            }
        }

        return root;
    }

    private static ResolvedActorSpec? ResolveChildSpec(PropSpec prop)
    {
        var doc = ActorDocCache.GetOrLoad(ActorLoader.ResolveActorAbsPath(prop.ActorPath));
        if (doc == null) return null;
        var chosen = VariationResolver.Resolve(doc, prop.SubSeed, VariationResolver.IdleOnly);
        return SpecMerger.Merge(doc, chosen, AssetPathResolver.Instance, prop.SubSeed);
    }

    private static Node3D? LoadAndInstantiateGlb(string relGlbPath)
    {
        var scene = ModelLibrary.LoadGlb(relGlbPath);
        if (scene == null) return null;
        var node = scene.Instantiate<Node3D>();
        return node;
    }

    private static void ApplyMaterial(Node3D instance, ResolvedActorSpec spec, Color teamColor)
    {
        ImageTexture? baseTex = null;
        if (spec.Textures.TryGetValue("baseTex", out var texPath))
            baseTex = LoadTextureByRelPath(texPath);

        var mat = new StandardMaterial3D();
        if (baseTex != null)
        {
            mat.AlbedoTexture = baseTex;
            mat.AlbedoColor = Colors.White;
        }
        else
        {
            mat.AlbedoColor = teamColor;
        }

        foreach (var n in EnumerateDescendants(instance))
        {
            if (n is MeshInstance3D mi)
                mi.MaterialOverride = mat;
        }
    }

    private static ImageTexture? LoadTextureByRelPath(string relPath)
    {
        string abs = ProjectSettings.GlobalizePath("res://assets/textures/") + relPath;
        abs = abs.Replace('\\', '/');
        if (!File.Exists(abs)) return null;
        var img = Image.LoadFromFile(abs);
        return img != null ? ImageTexture.CreateFromImage(img) : null;
    }

    private static void TryPlayIdle(Node3D instance)
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

    private static IEnumerable<Node> EnumerateDescendants(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var d in EnumerateDescendants(child))
                yield return d;
        }
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
