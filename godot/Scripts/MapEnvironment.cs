using Godot;

namespace ZeroAD.Godot;

/// <summary>
/// 地图 &lt;Environment&gt; 光照段端口(SunColor/SunElevation/SunRotation/AmbientColor/FogColor)。
/// 太阳方向公式对齐 CLightEnv::CalculateSunDirection:
///   dir = normalize((1−sinE)·sinR, −sinE, (1−sinE)·cosR)
/// 该方向在 sim(C++ 世界)空间;我们的世界视觉经 _worldRoot z 镜像,故施加时 z 取反。
/// </summary>
public sealed record MapEnvironment(
    Color SunColor, float SunElevation, float SunRotation,
    Color AmbientColor, Color FogColor)
{
    /// <summary>无 XML 时的回退:数值取教程图同款(东南天太阳),比硬编码 euler 更接近 C++。</summary>
    public static readonly MapEnvironment Default = new(
        new Color(0.74902f, 0.74902f, 0.74902f), 0.681087f, -0.638136f,
        new Color(0.501961f, 0.501961f, 0.501961f),
        new Color(0.8f, 0.8f, 0.894118f));

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
            Color fog = ReadColor(env.Element("Fog")?.Element("FogColor"), Default.FogColor);
            return new MapEnvironment(sun, elev, rot, amb, fog);
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"MapEnvironment.LoadFromXml failed: {e.Message}");
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

    private static float Attr(System.Xml.Linq.XElement el, string name, float fallback) =>
        float.TryParse(el.Attribute(name)?.Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;

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
        light.LightColor = SunColor;

        // AmbientColor 对齐(C++ 是平光环境色);雾色取地图值,密度模型不同保持现有。
        env.AmbientLightSource = global::Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor = AmbientColor;
        env.FogLightColor = FogColor;
    }
}
