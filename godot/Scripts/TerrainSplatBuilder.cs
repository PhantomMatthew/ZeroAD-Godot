using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace ZeroAD.Godot;

/// <summary>
/// Builds the splat-textured terrain material from a loaded <see cref="PmpMap"/>.
/// The C++ engine composites per-tile texture pairs (STileDesc tex1/tex2) into
/// per-patch blend maps; we ship the equivalent as a Texture2DArray plus three
/// tile-resolution control maps (base index / blend index / blend weight) and
/// let terrain_splat.gdshader do the mix. Weights are binary per tile (arcadia
/// uses zero blended tiles); the linear sampler on the weight map still yields
/// smooth one-tile transitions where blends do exist.
/// </summary>
public static class TerrainSplatBuilder
{
    private const int ArrayTextureSize = 512;
    private static readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

    public static ShaderMaterial? BuildMaterial(PmpMap map)
    {
        int texCount = map.TextureNames.Count;
        if (texCount == 0 || map.TileTex1.Length == 0)
        {
            GD.PushWarning("TerrainSplatBuilder: no texture data in PMP; falling back to flat terrain");
            return null;
        }

        var layers = new global::Godot.Collections.Array<Image>();
        foreach (var name in map.TextureNames)
            layers.Add(LoadTerrainLayer(name));

        var array = new Texture2DArray();
        array.CreateFromImages(layers);

        int t = map.TilesPerSide;
        var imgA = Image.CreateEmpty(t, t, false, Image.Format.R8);
        var imgB = Image.CreateEmpty(t, t, false, Image.Format.R8);
        var imgW = Image.CreateEmpty(t, t, false, Image.Format.R8);
        for (int z = 0; z < t; z++)
        {
            for (int x = 0; x < t; x++)
            {
                int i = z * t + x;
                int a = Math.Clamp(map.TileTex1[i], 0, texCount - 1);
                bool hasBlend = map.TileTex2[i] != PmpMap.NoTexture;
                int b = hasBlend ? Math.Clamp(map.TileTex2[i], 0, texCount - 1) : a;
                imgA.SetPixel(x, z, new Color(a / 255f, 0, 0));
                imgB.SetPixel(x, z, new Color(b / 255f, 0, 0));
                imgW.SetPixel(x, z, hasBlend ? Colors.White : Colors.Black);
            }
        }

        var mat = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://Shaders/terrain_splat.gdshader")
        };
        mat.SetShaderParameter("albedo_array", array);
        mat.SetShaderParameter("idx_a", ImageTexture.CreateFromImage(imgA));
        mat.SetShaderParameter("idx_b", ImageTexture.CreateFromImage(imgB));
        mat.SetShaderParameter("weight_b", ImageTexture.CreateFromImage(imgW));
        mat.SetShaderParameter("tiles_per_side", (float)t);
        mat.SetShaderParameter("world_size", map.MapSizeMeters);
        GD.Print($"Terrain splat: {texCount} textures, {t}x{t} tiles");
        return mat;
    }

    /// <summary>Resolves a PMP texture name (e.g. "medit_rocks_grass") to a 512x512
    /// RGBA image. PMP names equal the terrain XML basename, which in turn names its
    /// baseTex file (types/&lt;name&gt;.dds) — so the direct path almost always hits;
    /// the art/terrains XML scan is the fallback for renames. Missing textures become
    /// neutral grass green rather than aborting the whole terrain.</summary>
    private static Image LoadTerrainLayer(string name)
    {
        string texRoot = ProjectSettings.GlobalizePath("res://assets/textures/");
        string direct = Path.Combine(texRoot, "terrain", name + ".png");
        if (File.Exists(direct))
            return Normalize(direct);

        string? viaXml = ResolveViaTerrainXml(name, texRoot);
        if (viaXml != null)
            return Normalize(viaXml);

        if (_warned.Add(name))
            GD.PushWarning($"TerrainSplatBuilder: texture '{name}' not found; using placeholder");
        var fallback = Image.CreateEmpty(ArrayTextureSize, ArrayTextureSize, false, Image.Format.Rgba8);
        fallback.Fill(new Color(0.35f, 0.50f, 0.20f));
        return fallback;
    }

    private static Image Normalize(string pngPath)
    {
        var img = Image.LoadFromFile(pngPath);
        if (img.GetWidth() != ArrayTextureSize || img.GetHeight() != ArrayTextureSize)
            img.Resize(ArrayTextureSize, ArrayTextureSize, Image.Interpolation.Bilinear);
        if (img.GetFormat() != Image.Format.Rgba8)
            img.Convert(Image.Format.Rgba8);
        return img;
    }

    /// <summary>Scans art/terrains/**/&lt;name&gt;.xml for its baseTex file, then finds a
    /// converted PNG of the same basename anywhere under assets/textures.</summary>
    private static string? ResolveViaTerrainXml(string name, string texRoot)
    {
        string terrainsRoot = ProjectSettings.GlobalizePath("res://..")
            + "/binaries/data/mods/public/art/terrains";
        try
        {
            foreach (var xml in Directory.EnumerateFiles(terrainsRoot, name + ".xml", SearchOption.AllDirectories))
            {
                var doc = System.Xml.Linq.XDocument.Load(xml);
                foreach (var tex in doc.Descendants("texture"))
                {
                    if ((string?)tex.Attribute("name") != "baseTex") continue;
                    string? file = (string?)tex.Attribute("file");
                    if (string.IsNullOrEmpty(file)) continue;
                    string pngName = Path.GetFileNameWithoutExtension(file) + ".png";
                    foreach (var candidate in Directory.EnumerateFiles(texRoot, pngName, SearchOption.AllDirectories))
                        return candidate;
                }
            }
        }
        catch (Exception ex)
        {
            if (_warned.Add("xml:" + name))
                GD.PushWarning($"TerrainSplatBuilder: terrain XML scan failed for '{name}': {ex.Message}");
        }
        return null;
    }
}
