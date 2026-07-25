using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Godot;

namespace ZeroAD.Godot.Actors.Parsing;

/// <summary>
/// Parses 0 A.D. actor XML files into <see cref="ActorDoc"/>. Handles &lt;variant file="..."&gt;
/// recursion: the referenced variant is loaded as a base, inline children override/extend per-field.
/// </summary>
public static class ActorParser
{
    private const int MaxVariantFileDepth = 8;

    private static readonly Dictionary<string, ActorVariant> _variantFileCache = new();

    /// <summary>
    /// Root of the original 0 A.D. art directory. Set by <see cref="ActorLoader"/> on init.
    /// Defaults to &quot;../binaries/data/mods/public/art/&quot; resolved relative to the Godot project.
    /// </summary>
    public static string ArtRoot { get; set; } =
        Path.GetFullPath(ProjectSettings.GlobalizePath("res://..")
            + "/binaries/data/mods/public/art/");

    public static ActorDoc? Parse(string absActorPath)
    {
        if (!File.Exists(absActorPath))
        {
            GD.PushWarning($"ActorParser: actor file not found: {absActorPath}");
            return null;
        }

        XDocument doc;
        try { doc = XDocument.Load(absActorPath); }
        catch (Exception ex)
        {
            GD.PushWarning($"ActorParser: failed to parse '{absActorPath}': {ex.Message}");
            return null;
        }

        var root = doc.Root;
        if (root == null || root.Name.LocalName != "actor")
        {
            GD.PushWarning($"ActorParser: root is not <actor>: {absActorPath}");
            return null;
        }

        try
        {
            bool castShadow = root.Element("castshadow") != null;
            string? material = root.Element("material")?.Value.Trim();
            if (string.IsNullOrEmpty(material)) material = null;

            var groups = new List<VariantGroup>();
            foreach (var g in root.Elements("group"))
            {
                var variants = new List<ActorVariant>();
                foreach (var v in g.Elements("variant"))
                    variants.Add(ParseVariant(v, depth: 0));
                if (variants.Count > 0)
                    groups.Add(new VariantGroup(variants));
            }

            return new ActorDoc(absActorPath, castShadow, material, groups);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"ActorParser: error building doc for '{absActorPath}': {ex.Message}");
            return null;
        }
    }

    private static ActorVariant ParseVariant(XElement el, int depth)
    {
        string? fileAttr = (string?)el.Attribute("file");
        string nameAttr = ((string?)el.Attribute("name") ?? "").Trim().ToLowerInvariant();
        int freq = (int?)el.Attribute("frequency") ?? 0;

        // Empty variant: <variant name="Idle"/> or <variant file="..." /> with no inline children.
        if (fileAttr != null && !HasInlineFields(el))
        {
            // Pure file reference with no overrides.
            var fromFile = LoadVariantFile(fileAttr, depth);
            if (fromFile != null)
                return Rename(fromFile, nameAttr, freq);
            return ActorVariant.Empty(nameAttr, freq);
        }

        if (fileAttr != null)
        {
            // File + inline overrides: load base then apply overrides.
            var merged = LoadVariantFile(fileAttr, depth);
            if (merged != null)
                return MergeVariant(merged, el, nameAttr, freq);
        }

        return BuildInline(el, nameAttr, freq);
    }

    private static bool HasInlineFields(XElement el) =>
        el.Element("mesh") != null
        || el.Element("textures") != null
        || el.Element("props") != null
        || el.Element("animations") != null
        || el.Element("material") != null
        || el.Element("color") != null;

    private static ActorVariant BuildInline(XElement el, string name, int freq)
    {
        string? mesh = el.Element("mesh")?.Value.Trim();
        if (string.IsNullOrEmpty(mesh)) mesh = null;

        var textures = ParseTextures(el.Element("textures"));
        var props = ParseProps(el.Element("props"));
        var anims = ParseAnimations(el.Element("animations"));
        string? material = el.Element("material")?.Value.Trim();
        if (string.IsNullOrEmpty(material)) material = null;

        return new ActorVariant(name, freq, mesh, textures, props, anims, material, ParseColor(el));
    }

    /// <summary>Parses &lt;color&gt;r g b&lt;/color&gt; — authored as 0-255 ints
    /// (0-1 floats tolerated). Null when absent or malformed.</summary>
    private static ColorVec? ParseColor(XElement el)
    {
        string? raw = el.Element("color")?.Value.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        if (!float.TryParse(parts[0], out float r)
            || !float.TryParse(parts[1], out float g)
            || !float.TryParse(parts[2], out float b))
            return null;
        if (r <= 1f && g <= 1f && b <= 1f) { r *= 255f; g *= 255f; b *= 255f; }
        return new ColorVec(
            (byte)Math.Clamp((int)r, 0, 255),
            (byte)Math.Clamp((int)g, 0, 255),
            (byte)Math.Clamp((int)b, 0, 255));
    }

    private static ActorVariant MergeVariant(ActorVariant baseV, XElement inline, string name, int freq)
    {
        string? mesh = baseV.Mesh;
        var inlineMesh = inline.Element("mesh")?.Value.Trim();
        if (!string.IsNullOrEmpty(inlineMesh)) mesh = inlineMesh;

        var inlineTex = ParseTextures(inline.Element("textures"));
        var mergedTex = new Dictionary<string, string>(baseV.Textures);
        foreach (var kv in inlineTex)
            mergedTex[kv.Key] = kv.Value;

        var inlineProps = ParseProps(inline.Element("props"));
        var mergedProps = new Dictionary<string, PropRef>(baseV.Props);
        foreach (var kv in inlineProps)
            mergedProps[kv.Key] = kv.Value;

        var inlineAnims = ParseAnimations(inline.Element("animations"));
        var mergedAnims = new Dictionary<string, AnimRef>(baseV.Animations.Count + inlineAnims.Count);
        foreach (var a in baseV.Animations)
            mergedAnims[a.Name] = a;
        foreach (var a in inlineAnims)
            mergedAnims[a.Name] = a;

        string? material = baseV.Material;
        var inlineMat = inline.Element("material")?.Value.Trim();
        if (!string.IsNullOrEmpty(inlineMat)) material = inlineMat;

        var nameToUse = string.IsNullOrEmpty(name) ? baseV.Name : name;
        var freqToUse = freq == 0 ? baseV.Frequency : freq;

        return new ActorVariant(
            nameToUse, freqToUse, mesh,
            mergedTex, mergedProps,
            mergedAnims.Values.ToList(),
            material,
            ParseColor(inline) ?? baseV.Color);
    }

    private static ActorVariant Rename(ActorVariant v, string name, int freq)
    {
        var nameToUse = string.IsNullOrEmpty(name) ? v.Name : name;
        var freqToUse = freq == 0 ? v.Frequency : freq;
        return v with { Name = nameToUse, Frequency = freqToUse };
    }

    private static ActorVariant? LoadVariantFile(string relPath, int depth)
    {
        if (depth >= MaxVariantFileDepth)
        {
            GD.PushWarning($"ActorParser: variant file recursion depth exceeded at '{relPath}'");
            return null;
        }
        string abs = Path.GetFullPath(Path.Combine(ArtRoot, "variants", relPath));
        if (_variantFileCache.TryGetValue(abs, out var cached))
            return cached;
        if (!File.Exists(abs))
        {
            GD.PushWarning($"ActorParser: variant file not found: {abs}");
            _variantFileCache[abs] = null!;
            return null;
        }

        try
        {
            var doc = XDocument.Load(abs);
            var root = doc.Root;
            if (root == null || root.Name.LocalName != "variant")
            {
                GD.PushWarning($"ActorParser: variant file root is not <variant>: {abs}");
                _variantFileCache[abs] = null!;
                return null;
            }

            // Variant files may themselves reference other variant files.
            string? nestedFile = (string?)root.Attribute("file");
            ActorVariant result;
            if (nestedFile != null)
            {
                var nested = LoadVariantFile(nestedFile, depth + 1);
                if (nested == null)
                    result = BuildInline(root,
                        ((string?)root.Attribute("name") ?? "").Trim().ToLowerInvariant(),
                        (int?)root.Attribute("frequency") ?? 0);
                else
                    result = MergeVariant(nested, root,
                        ((string?)root.Attribute("name") ?? "").Trim().ToLowerInvariant(),
                        (int?)root.Attribute("frequency") ?? 0);
            }
            else
            {
                result = BuildInline(root,
                    ((string?)root.Attribute("name") ?? "").Trim().ToLowerInvariant(),
                    (int?)root.Attribute("frequency") ?? 0);
            }

            _variantFileCache[abs] = result;
            return result;
        }
        catch (Exception ex)
        {
            GD.PushWarning($"ActorParser: failed to parse variant file '{abs}': {ex.Message}");
            _variantFileCache[abs] = null!;
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> ParseTextures(XElement? container)
    {
        if (container == null) return EmptyDict<string, string>.Value;
        var dict = new Dictionary<string, string>();
        foreach (var tex in container.Elements("texture"))
        {
            string? name = (string?)tex.Attribute("name");
            string? file = (string?)tex.Attribute("file");
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(file))
                dict[name!] = file!;
        }
        return dict;
    }

    private static IReadOnlyDictionary<string, PropRef> ParseProps(XElement? container)
    {
        if (container == null) return EmptyDict<string, PropRef>.Value;
        var dict = new Dictionary<string, PropRef>();
        foreach (var p in container.Elements("prop"))
        {
            string? actor = (string?)p.Attribute("actor");
            string? attachpoint = (string?)p.Attribute("attachpoint");
            if (string.IsNullOrEmpty(attachpoint)) continue;
            // Empty <prop attachpoint="x"/> (no actor) is a CLEAR entry, not noise —
            // animation variants use it to hide weapons/shields while gathering etc.
            if (string.IsNullOrEmpty(actor)) actor = null;
            dict[attachpoint!] = new PropRef(actor, attachpoint!);
        }
        return dict;
    }

    private static IReadOnlyList<AnimRef> ParseAnimations(XElement? container)
    {
        if (container == null) return EmptyList<AnimRef>.Value;
        var list = new List<AnimRef>();
        foreach (var a in container.Elements("animation"))
        {
            string? name = (string?)a.Attribute("name");
            string? file = (string?)a.Attribute("file");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(file)) continue;
            int speed = (int?)a.Attribute("speed") ?? 1;
            list.Add(new AnimRef(name!, file!, speed));
        }
        return list;
    }
}
