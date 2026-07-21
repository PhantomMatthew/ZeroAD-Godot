using Godot;
using System.Collections.Generic;

namespace ZeroAD.Godot;

using ZeroAD.Godot.Actors;

public static class ModelLibrary
{
    private static readonly Dictionary<string, PackedScene?> _glbCache = new();

    private static readonly string _meshesRoot = ProjectSettings.GlobalizePath("res://assets/meshes");

    public static Node3D? InstantiateForTemplate(string template, float x, float z, Color? teamColor)
    {
        var color = teamColor ?? new Color(0.7f, 0.6f, 0.4f);

        var actorNode = TryInstantiateViaActorSystem(template, x, z, color);
        if (actorNode != null)
            return actorNode;

        // Actor system miss: fall back to SimBridge's EntityMeshFactory by returning null.
        return null;
    }

    private static Node3D? TryInstantiateViaActorSystem(string template, float x, float z, Color color)
    {
        var actorPath = ActorLoader.ExtractActorFromTemplate(template);
        if (string.IsNullOrEmpty(actorPath))
        {
            ZeroAD.Godot.Actors.ActorDiagnostics.Fallback(template, "no-VisualActor");
            return null;
        }

        int seed = (template.GetHashCode(), x.GetHashCode(), z.GetHashCode()).GetHashCode();
        var node = ActorLoader.Instance.Instantiate(actorPath!, seed, color);
        if (node == null)
        {
            ZeroAD.Godot.Actors.ActorDiagnostics.Fallback(template, $"actor-instantiate-failed -> {actorPath}");
            return null;
        }

        ZeroAD.Godot.Actors.ActorDiagnostics.Resolved(template, actorPath!);

        float y = TerrainHeightService.Sample(x, z);
        node.Position = new Vector3(x, y, z);

        var player = FindAnimationPlayer(node);
        if (player != null)
        {
            string idle = ResolveClip(player, "idle");
            if (idle != "")
            {
                player.Play(idle);
                player.Advance((double)GD.Randf() * 2.0);
            }
        }
        return node;
    }

    public static Node3D? TryInstantiate(string kind, Color teamColor) =>
        InstantiateForTemplate(kind, 0, 0, teamColor);

    public static bool IsAnimated(string kind) => kind.Contains("units/");

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
