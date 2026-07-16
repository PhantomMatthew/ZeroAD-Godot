using Godot;
using System.IO;
using System.Xml.Linq;

namespace ZeroAD.Godot;

public static class WaterRenderer
{
    public static (float height, Color color)? LoadWaterFromXml(string xmlPath)
    {
        if (!File.Exists(xmlPath)) return null;

        try
        {
            var doc = XDocument.Load(xmlPath);
            var body = doc.Root?.Element("Environment")?.Element("Water")?.Element("WaterBody");
            if (body == null) return null;

            var heightEl = body.Element("Height");
            if (heightEl == null || !float.TryParse(heightEl.Value, out float height))
                return null;

            var colorEl = body.Element("Color");
            Color color = new(0.3f, 0.25f, 0.14f, 0.7f);
            if (colorEl != null)
            {
                float r = float.TryParse(colorEl.Attribute("r")?.Value, out var rv) ? rv : 0.3f;
                float g = float.TryParse(colorEl.Attribute("g")?.Value, out var gv) ? gv : 0.25f;
                float b = float.TryParse(colorEl.Attribute("b")?.Value, out var bv) ? bv : 0.14f;
                color = new Color(r, g, b, 0.75f);
            }

            return (height, color);
        }
        catch { return null; }
    }

    public static MeshInstance3D CreateWaterPlane(float height, Color color, float mapSize)
    {
        var plane = new PlaneMesh();
        plane.Size = new Vector2(mapSize * 1.5f, mapSize * 1.5f);
        plane.Material = CreateWaterMaterial(color);

        var instance = new MeshInstance3D
        {
            Mesh = plane,
            Position = new Vector3(mapSize * 0.5f, height, mapSize * 0.5f),
        };

        return instance;
    }

    private static StandardMaterial3D CreateWaterMaterial(Color color)
    {
        var mat = new StandardMaterial3D();
        mat.AlbedoColor = color;
        mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        mat.Metallic = 0.3f;
        mat.Roughness = 0.2f;
        mat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel;
        return mat;
    }
}
