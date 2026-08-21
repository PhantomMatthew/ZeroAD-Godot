using Godot;
using System.IO;
using System.Xml.Linq;

namespace ZeroAD.Godot;

public static class WaterRenderer
{
    /// <summary>地图 XML 的水体参数(Environment/Water/WaterBody;原版 WaterManager 字段)。</summary>
    public sealed record WaterSpec(
        float Height, Color Color, Color Tint,
        float Waviness, float Murkiness, float WindAngle, string Type);

    public static WaterSpec? LoadWaterFromXml(string xmlPath)
    {
        if (!File.Exists(xmlPath)) return null;

        try
        {
            var doc = XDocument.Load(xmlPath);
            var body = doc.Root?.Element("Environment")?.Element("Water")?.Element("WaterBody");
            if (body == null) return null;

            var heightEl = body.Element("Height");
            if (heightEl == null || !float.TryParse(heightEl.Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float height))
                return null;

            Color ParseColor(XElement? el, Color dflt)
            {
                if (el == null) return dflt;
                float F(string n, float d) => float.TryParse(el.Attribute(n)?.Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
                return new Color(F("r", dflt.R), F("g", dflt.G), F("b", dflt.B));
            }
            float F2(string elName, float d) => float.TryParse(body.Element(elName)?.Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;

            var color = ParseColor(body.Element("Color"), new Color(0.3f, 0.25f, 0.14f));
            var tint = ParseColor(body.Element("Tint"), color);
            float waviness = F2("Waviness", 6f);
            float murkiness = F2("Murkiness", 0.9f);
            float windAngle = F2("WindAngle", 0f);
            string type = body.Element("Type")?.Value.Trim() ?? "lake";
            if (type.Length == 0) type = "lake";

            return new WaterSpec(height, color, tint, waviness, murkiness, windAngle, type);
        }
        catch { return null; }
    }

    public static MeshInstance3D CreateWaterPlane(float height, Color color, float mapSize) =>
        CreateWaterPlane(new WaterSpec(height, color, color, 6f, 0.9f, 0f, "lake"), mapSize);

    public static MeshInstance3D CreateWaterPlane(WaterSpec spec, float mapSize)
    {
        var plane = new PlaneMesh();
        plane.Size = new Vector2(mapSize * 1.5f, mapSize * 1.5f);
        plane.Material = CreateWaterMaterial(spec);

        var instance = new MeshInstance3D
        {
            Mesh = plane,
            Position = new Vector3(mapSize * 0.5f, spec.Height, mapSize * 0.5f),
        };

        return instance;
    }

    private static readonly System.Lazy<Shader> _waterShader =
        new(() => GD.Load<Shader>("res://Shaders/water.gdshader"));

    private static Material CreateWaterMaterial(WaterSpec spec)
    {
        // dev 钩子:ZEROAD_WATER_PLAIN=1 用纯半透明 StandardMaterial(隔离自定义
        // shader 问题——排查"水面全黑"时用)。
        if (System.Environment.GetEnvironmentVariable("ZEROAD_WATER_PLAIN") == "1")
        {
            return new StandardMaterial3D
            {
                AlbedoColor = new Color(spec.Color.R, spec.Color.G, spec.Color.B, 0.75f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
                Roughness = 0.15f,
            };
        }
        var mat = new ShaderMaterial { Shader = _waterShader.Value };
        mat.SetShaderParameter("water_color", spec.Color);
        mat.SetShaderParameter("water_tint", spec.Tint);
        mat.SetShaderParameter("murkiness", spec.Murkiness);
        mat.SetShaderParameter("waviness", spec.Waviness);
        mat.SetShaderParameter("wind_angle", spec.WindAngle);
        // 水波法线序列帧(原版 art/textures/animated/water/<type>/normal00XX.png,
        // 取两帧错相;junction 直读)。缺失则波纹退化为微闪。
        var na = LoadWaterNormal(spec.Type, 1);
        var nb = LoadWaterNormal(spec.Type, 2);
        if (na != null) mat.SetShaderParameter("normal_a", na);
        if (nb != null) mat.SetShaderParameter("normal_b", nb);
        return mat;
    }

    private static Texture2D? LoadWaterNormal(string type, int index)
    {
        string projRoot = ProjectSettings.GlobalizePath("res://");
        foreach (var up in new[] { "..", "../.." })
        {
            string p = Path.GetFullPath(Path.Combine(projRoot, up,
                "binaries", "data", "mods", "public", "art", "textures", "animated", "water",
                type, $"normal{index:D4}.png"));
            if (!File.Exists(p)) continue;
            var img = Image.LoadFromFile(p);
            if (img != null) return ImageTexture.CreateFromImage(img);
        }
        return null;
    }
}
