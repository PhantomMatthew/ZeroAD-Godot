using System;
using Godot;

namespace ZeroAD.Godot.Actors.Composition;

/// <summary>
/// Builds a per-mesh material for an actor layer. Routes by material name:
/// "player*" → ShaderMaterial reproducing 0 A.D.'s player-color mix formula
/// (baseTex alpha channel IS the player-color mask), else → StandardMaterial3D.
/// The compiled Shader is cached once; ShaderMaterials are duplicated per instance.
/// </summary>
public static class MaterialBuilder
{
    private static readonly Lazy<Shader> _playerColorShader = new(BuildPlayerColorShader);

    private static Shader BuildPlayerColorShader()
    {
        var code = @"
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_lambert, specular_schlick_ggx;

uniform sampler2D baseTex;
uniform vec4 playerColor : source_color;
uniform sampler2D normTex : hint_normal;
uniform sampler2D specTex;
uniform bool useNormal = false;
uniform bool useSpec = false;
uniform float normalScale = 1.0;

void fragment() {
    vec4 tex = texture(baseTex, UV);
    // 0 A.D. player-color: alpha is a tint-mask (0 = full playerColor, 1 = untinted).
    ALBEDO = tex.rgb * mix(playerColor.rgb, vec3(1.0), tex.a);
    if (useNormal) {
        NORMAL_MAP = texture(normTex, UV).rgb;
        NORMAL_MAP_DEPTH = normalScale;
    }
    if (useSpec) {
        ROUGHNESS = 1.0 - texture(specTex, UV).r;
    }
}
";
        var shader = new Shader { Code = code };
        return shader;
    }

    /// <summary>
    /// True if the material name routes to the team-color ShaderMaterial path.
    /// Matches 0 A.D. materials like player_trans.xml, player_trans_norm_spec.xml.
    /// </summary>
    public static bool IsPlayerColorMaterial(string? materialName) =>
        !string.IsNullOrEmpty(materialName) &&
        materialName!.Contains("player", StringComparison.OrdinalIgnoreCase);

    public static Material Build(
        ImageTexture? baseTex,
        ImageTexture? normTex,
        ImageTexture? specTex,
        Color teamColor,
        string? materialName)
    {
        if (IsPlayerColorMaterial(materialName))
            return BuildPlayerColor(baseTex, normTex, specTex, teamColor);
        return BuildStandard(baseTex, normTex, specTex, teamColor);
    }

    private static Material BuildPlayerColor(
        ImageTexture? baseTex,
        ImageTexture? normTex,
        ImageTexture? specTex,
        Color teamColor)
    {
        // Fresh ShaderMaterial per instance: team color and texture bindings vary.
        var mat = new ShaderMaterial { Shader = _playerColorShader.Value };
        mat.SetShaderParameter("playerColor", teamColor);
        if (baseTex != null)
            mat.SetShaderParameter("baseTex", baseTex);
        if (normTex != null)
        {
            mat.SetShaderParameter("normTex", normTex);
            mat.SetShaderParameter("useNormal", true);
        }
        if (specTex != null)
        {
            mat.SetShaderParameter("specTex", specTex);
            mat.SetShaderParameter("useSpec", true);
        }
        return mat;
    }

    private static Material BuildStandard(
        ImageTexture? baseTex,
        ImageTexture? normTex,
        ImageTexture? specTex,
        Color teamColor)
    {
        var mat = new StandardMaterial3D();
        if (baseTex != null)
        {
            mat.AlbedoTexture = baseTex;
            mat.AlbedoColor = Colors.White;
            mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            mat.AlphaScissorThreshold = 0.5f;
            mat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }
        else
        {
            mat.AlbedoColor = teamColor;
        }

        if (normTex != null)
        {
            mat.NormalTexture = normTex;
            mat.NormalEnabled = true;
        }

        if (specTex != null)
        {
            // Pragmatic mapping: specTex.R treated as shininess → invert into roughness.
            mat.RoughnessTexture = specTex;
        }
        return mat;
    }
}
