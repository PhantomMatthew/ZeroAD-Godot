using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace ZeroAD.Godot.Actors.Composition;

using ZeroAD.Godot.Actors.Variation;

public static class InstanceCustomizer
{
    private static readonly Dictionary<string, ImageTexture?> _texCache = new();
    private static readonly object _texLock = new();

    public static void Apply(Node3D instance, ResolvedActorSpec spec, Color teamColor, int entitySeed)
    {
        var rootCtx = new LayerContext(spec.ActorPath, spec.MeshGlbPath);
        Walk(instance, rootCtx, teamColor, entitySeed);
    }

    private static void Walk(Node node, LayerContext ctx, Color teamColor, int entitySeed)
    {
        LayerContext current = ctx;
        if (node is Node3D n3 && TryReadLayerMeta(n3, out var metaActor, out var metaMesh))
            current = new LayerContext(metaActor, metaMesh);

        if (node is MeshInstance3D mi)
            ApplyMaterial(mi, current, teamColor, entitySeed);

        foreach (var child in node.GetChildren())
            Walk(child, current, teamColor, entitySeed);
    }

    private static bool TryReadLayerMeta(Node3D node, out string actorPath, out string? meshGlbPath)
    {
        actorPath = string.Empty;
        meshGlbPath = null;
        if (!node.HasMeta(LayerMeta.ActorPath)) return false;
        var ap = node.GetMeta(LayerMeta.ActorPath);
        if (ap.VariantType != Variant.Type.String) return false;
        actorPath = (string)ap;
        if (node.HasMeta(LayerMeta.MeshGlbPath))
        {
            var mp = node.GetMeta(LayerMeta.MeshGlbPath);
            if (mp.VariantType == Variant.Type.String)
                meshGlbPath = (string)mp;
        }
        return true;
    }

    private static void ApplyMaterial(MeshInstance3D mi, LayerContext ctx, Color teamColor, int entitySeed)
    {
        var info = ActorLayerInfoCache.Get(ctx.ActorPath, ctx.MeshGlbPath);

        string? texPath = PickTexture(info, entitySeed);
        var baseTex = texPath != null ? LoadTextureCached(texPath) : null;
        var normTex = info.NormTexPath != null ? LoadTextureCached(info.NormTexPath) : null;
        var specTex = info.SpecTexPath != null ? LoadTextureCached(info.SpecTexPath) : null;

        var mat = MaterialBuilder.Build(baseTex, normTex, specTex, teamColor, info.Material);
        mi.MaterialOverride = mat;
    }

    private static string? PickTexture(ActorLayerInfo info, int entitySeed)
    {
        if (info.BaseTexVariants.Count == 0) return null;
        if (info.BaseTexVariants.Count == 1) return info.BaseTexVariants[0];
        int h = HashCode.Combine(entitySeed, info.ActorPath);
        int idx = (h & 0x7fffffff) % info.BaseTexVariants.Count;
        return info.BaseTexVariants[idx];
    }

    private static ImageTexture? LoadTextureCached(string relPath)
    {
        lock (_texLock)
        {
            if (_texCache.TryGetValue(relPath, out var cached))
                return cached;
        }

        string abs = ProjectSettings.GlobalizePath("res://assets/textures/") + relPath.Replace('\\', '/');
        ImageTexture? result = null;
        if (File.Exists(abs))
        {
            var img = Image.LoadFromFile(abs);
            if (img != null)
                result = ImageTexture.CreateFromImage(img);
        }

        lock (_texLock)
        {
            _texCache[relPath] = result;
        }
        return result;
    }

    private readonly record struct LayerContext(string ActorPath, string? MeshGlbPath);
}
