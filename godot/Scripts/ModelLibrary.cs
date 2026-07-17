using Godot;
using System.Collections.Generic;
using System.Linq;

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
        if (string.IsNullOrEmpty(actorPath)) return null;

        int seed = (template.GetHashCode(), x.GetHashCode(), z.GetHashCode()).GetHashCode();
        var node = ActorLoader.Instance.Instantiate(actorPath!, seed, color);
        if (node == null) return null;

        NormalizeScale(node, template);
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

    private static readonly Dictionary<string, float> StructureTargetSize = new()
    {
        ["civil_centre"] = 24f,
        ["civic_centre"] = 24f,
        ["barracks"] = 15f,
        ["house"] = 7f,
        ["temple"] = 15f,
        ["storehouse"] = 9f,
        ["farmstead"] = 9f,
        ["gerousia"] = 15f,
        ["wall"] = 10f,
        ["tower"] = 8f,
        ["outpost"] = 8f,
        ["field"] = 8f,
    };

    private static readonly Dictionary<string, float> _scaleFactorCache = new();

    private static void NormalizeScale(Node3D node, string template)
    {
        if (_scaleFactorCache.TryGetValue(template, out var cachedFactor))
        {
            if (cachedFactor != 1f) node.Scale *= cachedFactor;
            return;
        }

        var aabb = ComputeLocalAabb(node, Transform3D.Identity);
        if (aabb == null)
        {
            _scaleFactorCache[template] = 1f;
            return;
        }
        var size = aabb.Value.Size;

        float factor = 1f;
        if (template.StartsWith("units/"))
        {
            float height = size.Y;
            if (height is > 0.001f and (< 0.5f or > 4f))
                factor = 1.85f / height;
        }
        else if (template.StartsWith("structures/"))
        {
            float horizontal = Mathf.Max(size.X, size.Z);
            string building = template.Split('/') is { Length: >= 3 } p ? p[2] : "";
            float target = 12f;
            foreach (var kv in StructureTargetSize.OrderByDescending(kv => kv.Key.Length))
            {
                if (building.Contains(kv.Key, System.StringComparison.OrdinalIgnoreCase))
                {
                    target = kv.Value;
                    break;
                }
            }

            if (horizontal is > 0.001f and (< 2f or > 60f))
                factor = target / horizontal;
        }
        else if (template.StartsWith("gaia/"))
        {
            float dim = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));
            if (dim is > 0.001f and (< 0.5f or > 20f))
                factor = 5f / dim;
        }

        _scaleFactorCache[template] = factor;
        if (factor != 1f)
        {
            node.Scale *= factor;
            GD.Print($"ModelLibrary: '{template}' had implausible size {size} -> corrected scale x{factor:F3}");
        }
    }

    private static Aabb? ComputeLocalAabb(Node3D node, Transform3D accum)
    {
        var xform = accum * node.Transform;
        Aabb? result = null;

        if (node is MeshInstance3D { Mesh: not null } mi)
            result = TransformAabb(xform, mi.Mesh.GetAabb());

        foreach (var child in node.GetChildren())
        {
            if (child is not Node3D n3) continue;
            var childAabb = ComputeLocalAabb(n3, xform);
            if (childAabb == null) continue;
            result = result?.Merge(childAabb.Value) ?? childAabb;
        }

        return result;
    }

    private static Aabb TransformAabb(Transform3D xform, Aabb aabb)
    {
        Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);

        for (int i = 0; i < 8; i++)
        {
            var corner = aabb.Position + new Vector3(
                (i & 1) != 0 ? aabb.Size.X : 0f,
                (i & 2) != 0 ? aabb.Size.Y : 0f,
                (i & 4) != 0 ? aabb.Size.Z : 0f);
            var t = xform * corner;
            min = new Vector3(Mathf.Min(min.X, t.X), Mathf.Min(min.Y, t.Y), Mathf.Min(min.Z, t.Z));
            max = new Vector3(Mathf.Max(max.X, t.X), Mathf.Max(max.Y, t.Y), Mathf.Max(max.Z, t.Z));
        }

        return new Aabb(min, max - min);
    }

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
