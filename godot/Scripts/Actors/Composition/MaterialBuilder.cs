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
    private static readonly Lazy<Shader> _windShader = new(BuildWindShader);

    /// <summary>0 A.D. 风摆着色器(shaders/glsl/model_common.vs 的 USE_WIND 移植):
    /// 树/灌木/谷物(basic_trans_wind*.xml)的世界空间微风摆动。逐实例相位 =
    /// fract(世界原点);摆幅按局部顶点位置加权(树冠大、树干小);fakeCos =
    /// 平滑三角波。原版把位移加在 instancingTransform 之后(世界米)。
    /// 回局部必须用 inverse:transpose 带不走缩放,GLB 节点 18–100× 的棕榈/角豆
    /// 会被平方放大成数米到几十米的甩动。</summary>
    private static Shader BuildWindShader()
    {
        var code = @"
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, diffuse_lambert, specular_schlick_ggx;

uniform sampler2D baseTex : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D normTex : hint_normal, filter_linear_mipmap, repeat_enable;
uniform sampler2D specTex : filter_linear_mipmap, repeat_enable;
uniform bool useNormal = false;
uniform bool useSpec = false;
uniform vec2 windData = vec2(1.0, 1.0);

vec4 fakeCos(vec4 x) {
    vec4 tri = abs(fract(x + 0.5) * 2.0 - 1.0);
    return tri * tri * (3.0 - 2.0 * tri);
}

void vertex() {
    vec3 worldOrigin = MODEL_MATRIX[3].xyz;
    vec3 modelPos = clamp(fract(worldOrigin), vec3(0.4), vec3(1.0));
    float abswind = abs(windData.x) + abs(windData.y);

    vec3 wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    vec4 cosVec;
    cosVec.x = TIME * modelPos.x + wpos.x;
    cosVec.y = TIME * modelPos.z / 3.0 + worldOrigin.x;
    cosVec.z = TIME * abswind / 4.0 + wpos.z;
    cosVec = fakeCos(cosVec);

    float limit = clamp((VERTEX.x * VERTEX.z * VERTEX.y) / 3000.0, 0.0, 0.2);
    float diff = cosVec.x * limit;
    float diff2 = cosVec.y * clamp(VERTEX.y / 60.0, 0.0, 0.25);

    vec3 worldDisp = vec3(cosVec.z * limit * clamp(abswind, 1.2, 1.7));
    worldDisp.xz += vec2(diff) + diff2 * windData;
    // 与原版一致:worldDisp 加在缩放之后。零缩放实例(合批隐藏槽)跳过,避免 inverse 出 NaN。
    mat3 modelLin = mat3(MODEL_MATRIX);
    if (abs(determinant(modelLin)) > 1e-8)
        VERTEX += inverse(modelLin) * worldDisp;
}

void fragment() {
    vec4 tex = texture(baseTex, UV);
    ALBEDO = tex.rgb;
    ALPHA = tex.a;
    ALPHA_SCISSOR_THRESHOLD = 0.5;
    if (useNormal) {
        NORMAL_MAP = texture(normTex, UV).rgb;
    }
    if (useSpec) {
        ROUGHNESS = 1.0 - texture(specTex, UV).r;
    }
}
";
        return new Shader { Code = code };
    }

    private static Shader BuildPlayerColorShader()
    {
        var code = @"
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_lambert, specular_schlick_ggx;

uniform sampler2D baseTex : source_color;
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

    /// <summary>
    /// True for 0 A.D. objectcolor materials (objectcolor_norm_spec.xml — props
    /// heads). Same alpha-masked tint formula as player color, but tinted by the
    /// actor's &lt;color&gt; variant (hair color), not the team. Routing these to
    /// the StandardMaterial path would alpha-scissor the hair region away.
    /// </summary>
    public static bool IsObjectColorMaterial(string? materialName) =>
        !string.IsNullOrEmpty(materialName) &&
        materialName!.Contains("objectcolor", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for 0 A.D. wind materials (basic_trans_wind*.xml — 树木/灌木/谷物;
    /// basic_glow_wind、*_wind_grain 同族)。微风摆动着色器路径。
    /// </summary>
    public static bool IsWindMaterial(string? materialName) =>
        !string.IsNullOrEmpty(materialName) &&
        materialName!.Contains("wind", StringComparison.OrdinalIgnoreCase);

    public static Material Build(
        ImageTexture? baseTex,
        ImageTexture? normTex,
        ImageTexture? specTex,
        Color teamColor,
        string? materialName,
        Color? objectColor = null)
    {
        if (IsPlayerColorMaterial(materialName))
            return BuildPlayerColor(baseTex, normTex, specTex, teamColor);
        if (IsObjectColorMaterial(materialName))
            // Same mix formula as player color; white default = untinted when the
            // actor defines no <color> variants.
            return BuildPlayerColor(baseTex, normTex, specTex, objectColor ?? Colors.White);
        if (IsWindMaterial(materialName))
            return BuildWind(baseTex, normTex, specTex);
        return BuildStandard(baseTex, normTex, specTex, teamColor);
    }

    /// <summary>风摆材质:每实例一个 ShaderMaterial(贴图绑定不同),着色器共享。</summary>
    private static Material BuildWind(
        ImageTexture? baseTex,
        ImageTexture? normTex,
        ImageTexture? specTex)
    {
        var mat = new ShaderMaterial { Shader = _windShader.Value };
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
        var mat = new StandardMaterial3D
        {
            // C++ calculateShading 是 Lambert(texel×(sun·N·L+ambient));Godot 默认
            // Burley 更暗更灰,和原版鲜艳观感对不上。
            DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Lambert,
        };
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
        else
        {
            mat.SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled;
        }
        return mat;
    }
}
