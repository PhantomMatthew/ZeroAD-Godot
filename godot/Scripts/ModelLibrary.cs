using Godot;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZeroAD.Godot;

using ZeroAD.Godot.Actors;

public static class ModelLibrary
{
    private static readonly Dictionary<string, PackedScene?> _glbCache = new();
    private static readonly Dictionary<string, ImageTexture?> _texCache = new();
    private static readonly Dictionary<string, string?> _resolveCache = new();

    private static readonly string _meshesRoot = ProjectSettings.GlobalizePath("res://assets/meshes");
    private static readonly string _texRoot = ProjectSettings.GlobalizePath("res://assets/textures");

    private static string[]? _allStructuralGlbs;
    private static string[]? _allGaiaGlbs;

    public static Node3D? InstantiateForTemplate(string template, float x, float z, Color? teamColor)
    {
        var color = teamColor ?? new Color(0.7f, 0.6f, 0.4f);

        var actorNode = TryInstantiateViaActorSystem(template, x, z, color);
        if (actorNode != null)
            return actorNode;

        var glbPath = ResolveGlbForTemplate(template);
        if (glbPath == null)
        {
            GD.PrintErr($"ModelLibrary: no GLB for '{template}'");
            return null;
        }

        var scene = LoadGlb(glbPath);
        if (scene == null)
        {
            GD.PrintErr($"ModelLibrary: GLB load failed '{glbPath}' for '{template}'");
            return null;
        }

        var node = scene.Instantiate<Node3D>();
        NormalizeScale(node, template);
        float y = TerrainHeightService.Sample(x, z);
        node.Position = new Vector3(x, y, z);

        var tex = ResolveTextureForTemplate(template);
        var headTex = LoadTexture("villager_head.png", "skins", "skins/skeletal") ??
                      LoadTexture("soldier_head.png", "skins", "skins/skeletal");
        ApplyMaterial(node, tex, color);

        if (template.StartsWith("units/"))
            AttachSimpleHead(node, headTex ?? tex, template.Contains("support") || template.Contains("female"));

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

    private static void AttachSimpleHead(Node3D root, ImageTexture? headTex, bool isFemale)
    {
        var head = new MeshInstance3D();
        var sphere = new SphereMesh();
        sphere.Radius = 0.20f;
        sphere.Height = 0.40f;
        head.Mesh = sphere;

        var mat = new StandardMaterial3D();
        if (headTex != null)
        {
            mat.AlbedoTexture = headTex;
            mat.AlbedoColor = Colors.White;
        }
        else
        {
            mat.AlbedoColor = isFemale ? new Color(0.9f, 0.72f, 0.6f) : new Color(0.85f, 0.68f, 0.5f);
        }
        head.MaterialOverride = mat;
        head.Position = new Vector3(0, 1.70f, 0);
        root.AddChild(head);
    }

    private static string? ResolveGlbForTemplate(string template)
    {
        if (_resolveCache.TryGetValue(template, out var cached))
            return cached;

        string? result = null;
        var parts = template.Split('/');

        if (template.StartsWith("gaia/tree") || template.Contains("tree"))
        {
            result = FindFirstGlb("gaia", "oak_tree");
            if (result == null) result = FindFirstGlb("gaia", "tree");
        }
        else if (template.StartsWith("gaia/rock") || template.StartsWith("gaia/ore"))
        {
            result = FindFirstGlb("gaia", "stone_granite_peak") ??
                     FindFirstGlb("gaia", "stone_granite_large") ??
                     FindFirstGlb("gaia", "stone_medit") ??
                     FindFirstGlb("gaia", "stone");
        }
        else if (template.StartsWith("gaia/fruit") || template.Contains("grape") || template.Contains("bush"))
        {
            result = FindFirstGlb("gaia", "bush_medit") ??
                     FindFirstGlb("gaia", "bush_tempe") ??
                     FindFirstGlb("gaia", "bush");
        }
        else if (template.StartsWith("gaia/"))
        {
            string last = parts.Length > 1 ? parts[^1] : template;
            result = FindFirstGlb("gaia", last) ?? FindFirstGlb("gaia", "oak_tree");
        }
        else if (template.StartsWith("structures/"))
        {
            string building = parts.Length >= 3 ? parts[2] : "";
            string civ = parts.Length >= 2 ? parts[1] : "";

            string meshName = building switch
            {
                "civil_centre" => "cc",
                "civic_centre" => "cc",
                _ => building
            };

            foreach (var tryCiv in GetCivPrefixes(civ))
            {
                result = FindFirstGlb("structural", $"{tryCiv}_{meshName}_struct")
                      ?? FindFirstGlb("structural", $"{tryCiv}_{meshName}")
                      ?? FindFirstGlb("structural", $"{tryCiv}_{building}_struct")
                      ?? FindFirstGlb("structural", $"{tryCiv}_{building}");
                if (result != null) break;
            }

            if (result == null)
                result = FindFirstGlb("structural", building);

            if (result == null && building.Contains("house"))
                result = FindFirstGlb("structural", "house");
            if (result == null && building.Contains("field"))
                result = FindFirstGlb("structural", "field");
            if (result == null && building.Contains("farm"))
                result = FindFirstGlb("structural", "farm");
            if (result == null && building.Contains("wall"))
                result = FindFirstGlb("structural", "wall");
            if (result == null && (building.Contains("tower") || building.Contains("outpost")))
                result = FindFirstGlb("structural", "outpost") ?? FindFirstGlb("structural", "tower");

            result ??= FindFirstGlb("structural", "athen_cc_struct");
        }
        else if (template.StartsWith("units/"))
        {
            if (template.Contains("cavalry") || template.Contains("horse"))
                result = FindFirstGlb("skeletal", "horse");
            else if (template.Contains("female") || template.Contains("support"))
                result = FindExactGlb("skeletal/villager_anim.glb");
            else
                result = FindExactGlb("skeletal/soldier_anim.glb");
        }
        else if (template.StartsWith("birds/"))
        {
            result = FindFirstGlb("gaia", "bird") ?? FindFirstGlb("gaia", "buzzard");
        }

        _resolveCache[template] = result;
        return result;
    }

    private static string? FindFirstGlb(string subdir, string nameFragment)
    {
        var allFiles = subdir switch
        {
            "structural" => _allStructuralGlbs ??= Directory.GetFiles(
                Path.Combine(_meshesRoot, subdir), "*.glb", SearchOption.AllDirectories),
            "gaia" => _allGaiaGlbs ??= Directory.GetFiles(
                Path.Combine(_meshesRoot, subdir), "*.glb", SearchOption.AllDirectories),
            _ => Directory.GetFiles(Path.Combine(_meshesRoot, subdir), "*.glb", SearchOption.AllDirectories)
        };

        string? bestMatch = null;
        int bestScore = -1;

        foreach (var f in allFiles)
        {
            if (f.EndsWith(".import")) continue;
            var basename = Path.GetFileNameWithoutExtension(f);
            if (!basename.Contains(nameFragment, System.StringComparison.OrdinalIgnoreCase))
                continue;

            int score = 0;
            if (basename.Contains("_struct", System.StringComparison.OrdinalIgnoreCase)) score += 10;
            if (basename.Equals(nameFragment, System.StringComparison.OrdinalIgnoreCase)) score += 20;
            if (basename.StartsWith(nameFragment + "_struct", System.StringComparison.OrdinalIgnoreCase)) score += 15;
            if (basename.StartsWith(nameFragment, System.StringComparison.OrdinalIgnoreCase)) score += 5;
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = f;
            }
        }

        if (bestMatch != null)
            return Path.GetRelativePath(_meshesRoot, bestMatch).Replace('\\', '/');

        foreach (var f in allFiles)
        {
            if (f.EndsWith(".import")) continue;
            var basename = Path.GetFileNameWithoutExtension(f);
            if (basename.Contains(nameFragment, System.StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(_meshesRoot, f).Replace('\\', '/');
        }
        return null;
    }

    private static readonly Dictionary<string, string[]> CivMap = new()
    {
        ["spart"] = new[] { "spart", "sparta", "hele" },
        ["athen"] = new[] { "athen", "hele" },
        ["hele"] = new[] { "hele", "athen" },
        ["mace"] = new[] { "mace", "hele" },
        ["theb"] = new[] { "theb", "hele" },
        ["rome"] = new[] { "rome" },
        ["cart"] = new[] { "cart" },
        ["pers"] = new[] { "pers", "achae" },
        ["sele"] = new[] { "sele" },
        ["ptol"] = new[] { "ptol" },
        ["kush"] = new[] { "kush" },
        ["iber"] = new[] { "iber" },
        ["brit"] = new[] { "brit", "celt" },
        ["gaul"] = new[] { "gaul", "celt" },
        ["han"] = new[] { "han" },
        ["maur"] = new[] { "maur" },
    };

    private static string[] GetCivPrefixes(string civ) =>
        CivMap.TryGetValue(civ, out var arr) ? arr : new[] { civ, "athen", "hele" };

    private static string? FindExactGlb(string relPath)
    {
        string abs = Path.Combine(_meshesRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(abs) ? relPath : null;
    }

    private static readonly Dictionary<string, string[]> BuildingTextureAliases = new()
    {
        ["civil_centre"] = new[] { "civic_centre", "civic_center", "civiccentre", "cc" },
        ["civic_centre"] = new[] { "civic_centre", "civic_center", "civiccentre", "cc" },
        ["barracks"] = new[] { "barracks" },
        ["house"] = new[] { "house" },
        ["temple"] = new[] { "temple" },
        ["storehouse"] = new[] { "storehouse" },
        ["farmstead"] = new[] { "farmstead" },
        ["gerousia"] = new[] { "gerousia" },
        ["wall"] = new[] { "wall" },
        ["tower"] = new[] { "tower" },
        ["outpost"] = new[] { "outpost", "tower" },
        ["field"] = new[] { "field" },
    };

    private static readonly string[] TextureMapSuffixBlacklist = { "_norm", "_spec", "_ao" };

    private static ImageTexture? ResolveTextureForTemplate(string template)
    {
        var parts = template.Split('/');

        if (template.StartsWith("structures/"))
        {
            string civ = parts.Length >= 2 ? parts[1] : "";
            string building = parts.Length >= 3 ? parts[2] : "";
            return ResolveStructureTexture(civ, building);
        }

        if (template.StartsWith("gaia/"))
            return ResolveGaiaTexture(parts);

        if (template.StartsWith("units/"))
            return template.Contains("support") || template.Contains("female")
                ? LoadTexture("villager.png", "skins", "skins/skeletal")
                : LoadTexture("soldier.png", "skins", "skins/skeletal");

        return null;
    }

    private static ImageTexture? ResolveStructureTexture(string civ, string building)
    {
        string cacheKey = $"structural:{civ}:{building}";
        if (_texCache.TryGetValue(cacheKey, out var cached))
            return cached;

        string[] aliases = new[] { building };
        foreach (var kv in BuildingTextureAliases.OrderByDescending(kv => kv.Key.Length))
        {
            if (building.Contains(kv.Key, System.StringComparison.OrdinalIgnoreCase))
            {
                aliases = kv.Value;
                break;
            }
        }

        var weighted = new List<(string token, int weight)>();
        var civPrefixes = GetCivPrefixes(civ);
        for (int i = 0; i < civPrefixes.Length; i++)
            weighted.Add((civPrefixes[i], i == 0 ? 6 : 3));
        for (int i = 0; i < aliases.Length; i++)
            weighted.Add((aliases[i], i == 0 ? 10 : 7));

        var match = FindBestTextureFile(Path.Combine(_texRoot, "structural"), weighted);
        GD.Print($"ModelLibrary: texture for structures/{civ}/{building} -> {(match != null ? Path.GetFileName(match) : "NONE (fallback)")}");
        var result = LoadImageAt(match);
        _texCache[cacheKey] = result;
        return result;
    }

    private static ImageTexture? ResolveGaiaTexture(string[] parts)
    {
        string category = parts.Length >= 2 ? parts[1] : "";
        string name = parts.Length >= 3 ? parts[2] : "";
        string cacheKey = $"gaia:{category}:{name}";
        if (_texCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var tokens = name.Split('_', System.StringSplitOptions.RemoveEmptyEntries);
        var weighted = new List<(string token, int weight)>();
        for (int i = 0; i < tokens.Length; i++)
            weighted.Add((tokens[i], i == 0 ? 10 : 4));

        switch (category)
        {
            case "fruit":
                weighted.Add(("berry", 8));
                weighted.Add(("berries", 8));
                break;
            case "rock":
            case "ore":
                weighted.Add(("stone", 6));
                weighted.Add(("rock", 5));
                break;
            case "tree":
                weighted.Add(("tree", 2));
                break;
        }

        var match = FindBestTextureFile(Path.Combine(_texRoot, "gaia"), weighted);
        GD.Print($"ModelLibrary: texture for gaia/{category}/{name} -> {(match != null ? Path.GetFileName(match) : "NONE (fallback to oak)")}");
        var result = LoadImageAt(match) ?? LoadTexture("oak_tree_a.png", "skins/gaia", "gaia");
        _texCache[cacheKey] = result;
        return result;
    }

    private static string? FindBestTextureFile(string dirAbs, List<(string token, int weight)> weightedTokens)
    {
        if (!Directory.Exists(dirAbs)) return null;

        string? bestFile = null;
        int bestScore = 0;
        string? bestMapFile = null;
        int bestMapScore = 0;

        foreach (var f in Directory.GetFiles(dirAbs, "*.png", SearchOption.TopDirectoryOnly))
        {
            var basename = Path.GetFileNameWithoutExtension(f);
            bool isMap = TextureMapSuffixBlacklist.Any(b =>
                basename.Contains(b, System.StringComparison.OrdinalIgnoreCase));

            int score = 0;
            foreach (var (token, weight) in weightedTokens)
            {
                if (string.IsNullOrEmpty(token)) continue;
                if (basename.Equals(token, System.StringComparison.OrdinalIgnoreCase))
                    score += weight * 3;
                else if (basename.Contains(token, System.StringComparison.OrdinalIgnoreCase))
                    score += weight;
            }
            if (score <= 0) continue;

            if (isMap)
            {
                if (score > bestMapScore) { bestMapScore = score; bestMapFile = f; }
            }
            else if (score > bestScore)
            {
                bestScore = score;
                bestFile = f;
            }
        }

        return bestFile ?? bestMapFile;
    }

    private static ImageTexture? LoadImageAt(string? absPath)
    {
        if (absPath == null || !File.Exists(absPath)) return null;
        var img = Image.LoadFromFile(absPath);
        return img != null ? ImageTexture.CreateFromImage(img) : null;
    }

    internal static PackedScene? LoadGlb(string relPath)
    {
        if (_glbCache.TryGetValue(relPath, out var cached))
            return cached;

        string absPath = Path.Combine(_meshesRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        PackedScene? result = null;

        if (File.Exists(absPath))
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            if (doc.AppendFromFile(absPath, state) == Error.Ok)
            {
                var root = doc.GenerateScene(state);
                if (root != null)
                {
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

    private static ImageTexture? LoadTexture(string filename, params string[] subdirs)
    {
        string key = filename + string.Join(",", subdirs);
        if (_texCache.TryGetValue(key, out var cached))
            return cached;

        foreach (var dir in subdirs)
        {
            string path = Path.Combine(_texRoot, dir, filename);
            if (File.Exists(path))
            {
                var img = Image.LoadFromFile(path);
                if (img != null)
                {
                    var tex = ImageTexture.CreateFromImage(img);
                    _texCache[key] = tex;
                    return tex;
                }
            }
        }

        string flatPath = Path.Combine(_texRoot, filename);
        if (File.Exists(flatPath))
        {
            var img = Image.LoadFromFile(flatPath);
            if (img != null)
            {
                var tex = ImageTexture.CreateFromImage(img);
                _texCache[key] = tex;
                return tex;
            }
        }

        _texCache[key] = null;
        return null;
    }

    private static void ApplyMaterial(Node node, ImageTexture? tex, Color teamColor)
    {
        if (node is MeshInstance3D mi)
        {
            var mat = new StandardMaterial3D();
            if (tex != null)
            {
                mat.AlbedoTexture = tex;
                mat.AlbedoColor = Colors.White;
            }
            else
            {
                mat.AlbedoColor = teamColor.Lightened(0.3f);
            }
            mi.MaterialOverride = mat;
        }
        foreach (var child in node.GetChildren())
            ApplyMaterial(child, tex, teamColor);
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
