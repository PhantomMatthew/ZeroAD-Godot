using Godot;

namespace ZeroAD.Godot;

/// <summary>
/// 地图 &lt;Environment&gt; 光照段端口(SunColor/SunElevation/SunRotation/AmbientColor/FogColor
/// + Postproc Brightness/Contrast/Saturation)。
/// 太阳方向公式对齐 CLightEnv::CalculateSunDirection:
///   dir = normalize((1−sinE)·sinR, −sinE, (1−sinE)·cosR)
/// 该方向在 sim(C++ 世界)空间;我们的世界视觉经 _worldRoot z 镜像,故施加时 z 取反。
/// </summary>
public sealed record MapEnvironment(
    Color SunColor, float SunElevation, float SunRotation,
    Color AmbientColor, Color FogColor, float FogFactor, float FogMax,
    float Brightness = 0f, float Contrast = 1f, float Saturation = 0.99f,
    /// <summary>SkySet 名(原版 art/environments 的 <SkySet>name</SkySet>;
    /// art/textures/skies/{name}/ 5 面贴图)。空 = 无天空盒(程序化天空兜底)。</summary>
    string SkySet = "")
{
    /// <summary>无 XML 时的回退:数值取教程图同款(东南天太阳),比硬编码 euler 更接近 C++。</summary>
    public static readonly MapEnvironment Default = new(
        new Color(0.74902f, 0.74902f, 0.74902f), 0.681087f, -0.638136f,
        new Color(0.501961f, 0.501961f, 0.501961f),
        new Color(0.8f, 0.8f, 0.894118f), 0.0f, 1.0f);

    public static MapEnvironment? LoadFromXml(string xmlPath)
    {
        if (!System.IO.File.Exists(xmlPath)) return null;
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(xmlPath);
            var env = doc.Root?.Element("Environment");
            if (env == null) return null;

            Color sun = ReadColor(env.Element("SunColor"), Default.SunColor);
            float elev = ReadAngle(env.Element("SunElevation"), Default.SunElevation);
            float rot = ReadAngle(env.Element("SunRotation"), Default.SunRotation);
            Color amb = ReadColor(env.Element("AmbientColor"), Default.AmbientColor);
            var fogEl = env.Element("Fog");
            Color fog = ReadColor(fogEl?.Element("FogColor"), Default.FogColor);
            // 原版 fog.h:density=FogFactor, maxFog=FogThickness(远处最少保留的本色比例)。
            float fogFactor = ReadFloat(fogEl?.Element("FogFactor"), Default.FogFactor);
            float fogMax = ReadFloat(fogEl?.Element("FogThickness"), Default.FogMax);
            // CLightEnv 后处理(hdr.fs):color+=brightness; (color-0.5)*contrast+0.5; mix(luma,s)。
            var post = env.Element("Postproc");
            float brightness = ReadFloat(post?.Element("Brightness"), Default.Brightness);
            float contrast = ReadFloat(post?.Element("Contrast"), Default.Contrast);
            float saturation = ReadFloat(post?.Element("Saturation"), Default.Saturation);
            // SkySet(原版 <SkySet>name</SkySet> → art/textures/skies/{name}/)。
            string skySet = env.Element("SkySet")?.Value.Trim() ?? "";
            return new MapEnvironment(sun, elev, rot, amb, fog, fogFactor, fogMax,
                brightness, contrast, saturation, skySet);
        }
        catch (System.Exception e)
        {
            ZeroAD.Sim.Diag.Err("Map", $"MapEnvironment.LoadFromXml failed: {e.Message}");
            return null;
        }
    }

    private static Color ReadColor(System.Xml.Linq.XElement? el, Color fallback)
    {
        if (el == null) return fallback;
        float r = Attr(el, "r", fallback.R), g = Attr(el, "g", fallback.G), b = Attr(el, "b", fallback.B);
        return new Color(r, g, b);
    }

    private static float ReadAngle(System.Xml.Linq.XElement? el, float fallback) =>
        el == null ? fallback : Attr(el, "angle", fallback);

    private static float ReadFloat(System.Xml.Linq.XElement? el, float fallback) =>
        float.TryParse(el?.Value.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;

    private static float Attr(System.Xml.Linq.XElement el, string name, float fallback) =>
        float.TryParse(el.Attribute(name)?.Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;

    /// <summary>
    /// C++ 把 XML 色当 texel 乘数直接绑进 shader(不经 sRGB→线性)。
    /// Godot 的 LightColor / AmbientLightColor 按 sRGB 再解码,直接塞 XML 值会
    /// 把 0.75 太阳 / 0.50 环境光压成 ~0.52 / ~0.22,整场景发暗发灰。
    /// LinearToSrgb 预编码,解码后着色器看到的就是地图值。
    /// </summary>
    private static Color AsCppLight(Color xml) => xml.LinearToSrgb();

    /// <summary>施加到场景灯与环境。太阳方向按 C++ 公式算出(sim 空间)后 z 取反(视觉镜像)。</summary>
    public void Apply(DirectionalLight3D light, global::Godot.Environment env)
    {
        float sinE = Mathf.Sin(SunElevation);
        float scale = 1f - sinE;
        var dirSim = new Vector3(
            scale * Mathf.Sin(SunRotation),
            -sinE,
            scale * Mathf.Cos(SunRotation)).Normalized();
        var dirVis = new Vector3(dirSim.X, dirSim.Y, -dirSim.Z);

        // DirectionalLight3D 沿自身 −Z 照射;LookAtFromPosition 把 −Z 对准方向。
        light.LookAtFromPosition(Vector3.Zero, dirVis, Vector3.Up);
        light.LightColor = AsCppLight(SunColor);
        // 强度写在颜色里(C++ 无独立 energy;教程图太阳 0.749 + 环境 0.502)。
        light.LightEnergy = 1.0f;

        // C++ calculateShading: albedo*(sun*N·L*shadow + ambient)。平光环境色,
        // 不混天空。Godot 默认 AmbientLightSkyContribution=1 会让 AmbientLightColor
        // 完全失效(无 Sky 时环境光≈0,只剩太阳,画面发暗且不鲜艳)。
        env.AmbientLightSource = global::Godot.Environment.AmbientSource.Color;
        env.AmbientLightSkyContribution = 0f;
        env.AmbientLightColor = AsCppLight(AmbientColor);
        env.AmbientLightEnergy = 1.0f;
        env.ReflectedLightSource = global::Godot.Environment.ReflectionSource.Disabled;
        env.TonemapMode = global::Godot.Environment.ToneMapper.Linear;
        env.TonemapExposure = 1f;
        env.FogLightColor = AsCppLight(FogColor);
        // 雾密度对齐原版 fog.h:exp2(-(density·z)²·log2e)·(1-maxFog)+maxFog——
        // C++ 按"世界距离米"平方衰减,Godot FogDensity 按深度线性衰减,数学上不可直通;
        // 经验换算 FogFactor×0.44 在 z=100..500m 区间与 C++ 本色占比最接近
        // (0.0025×0.44=0.0011:z=100 本色 90% vs C++ 95%,z=350 68% vs 54%)。
        // FogMax(远处最少本色)Godot 无对应字段,以密度主项近似。density=0(原版默认)即关雾。
        env.FogDensity = FogFactor > 0f ? FogFactor * 0.44f : 0.0001f;

        // hdr.fs: color += brightness; (color-0.5)*contrast+0.5; mix(luma, color, sat)。
        // Godot AdjustmentBrightness 是乘数,1+brightness 近似原版加法项(地图值约 0±0.02)。
        env.AdjustmentEnabled = true;
        env.AdjustmentBrightness = 1f + Brightness;
        env.AdjustmentContrast = Contrast;
        env.AdjustmentSaturation = Saturation;

        // 后处理对齐原版选项(PORTING-GAPS §7):
        // bloom(原版 PostprocManager 的高斯模糊 bloom;Godot Glow 同效)
        // + HQ 上采样(MSAA 3D 2x/4x,原版 HQ 选项)+ sharpness(原版
        // sharpness 后处理;Godot 无直接字段,AdjustmentContrast 微调近似)。
        bool bloom = Options.OptionsApplier.GetBool("bloom", true);
        env.GlowEnabled = bloom;
        if (bloom)
        {
            env.GlowIntensity = 0.4f;
            env.GlowStrength = 0.9f;
            env.GlowBloom = 0.1f;
            env.GlowBlendMode = global::Godot.Environment.GlowBlendModeEnum.Additive;
        }
        // HQ 上采样(MSAA 3D 2x/4x,原版 HQ 选项;MSAA 是 Viewport 属性
        // 非 Environment——Main 建世界后 ApplyViewport 施加)。
        // sharpness 后处理(原版:锐化滤镜;Godot 无直接字段,AdjustmentContrast
        // 微抬近似——原版 sharpness 默认小正值)。
        float sharpness = Options.OptionsApplier.GetFloat("sharpness", 0f);
        if (sharpness != 0f)
            env.AdjustmentContrast *= 1f + sharpness * 0.5f;

        // 天空盒(原版 <SkySet>:art/textures/skies/{name}/ 5 面贴图装载;
        // 无贴图走程序化天空兜底——原版 C++ SkyBox 的背景替代)。
        SkyBox.Apply(env, SkySet.Length > 0 ? SkyBox.Load(SkySet) : SkyBox.CreateProcedural());
    }

    /// <summary>HQ 上采样施加到视口(MSAA 是 Viewport 属性非 Environment;
    /// Main 建世界后调用,2x/4x = 原版 HQ 选项)。</summary>
    public static void ApplyViewport(Viewport viewport)
    {
        string hq = Options.OptionsApplier.GetString("upscale", "off");
        viewport.Msaa3D = hq switch
        {
            "2x" => Viewport.Msaa.Msaa2X,
            "4x" => Viewport.Msaa.Msaa4X,
            _ => Viewport.Msaa.Disabled,
        };
    }
}
