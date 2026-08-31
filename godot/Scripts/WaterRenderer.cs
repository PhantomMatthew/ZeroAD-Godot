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

    /// <summary>rmgen 生成图的水体参数(MapExport.Environment.Water)。
    /// 上游 Height 为 undefined 时按 SEA_LEVEL 兜底(与 ExportMap 的默认一致)。</summary>
    public static WaterSpec FromRmgen(ZeroAD.Sim.Rmgen.RmgenEnvironment env)
    {
        var w = env.Water;
        return new WaterSpec(
            (float)(w.Height ?? ZeroAD.Sim.Rmgen.RmgenConstants.SEA_LEVEL),
            new Color((float)w.Color.R, (float)w.Color.G, (float)w.Color.B),
            new Color((float)w.Tint.R, (float)w.Tint.G, (float)w.Tint.B),
            (float)w.Waviness, (float)w.Murkiness, (float)w.WindAngle, w.Type);
    }

    public static WaterSpec? LoadWaterFromXml(string xmlPath)
    {        if (!File.Exists(xmlPath)) return null;

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

    /// <summary>地形高度采样(建水面网格用;Main 在调用前已 Set)。</summary>
    public static System.Func<float, float, float>? TerrainHeight { get; set; }

    /// <summary>建水面网格:只覆盖地形低于水位的格子(洼地/湖盆),水面高度取 spec.Height。
    /// 此前是一整块 mapSize×1.5 的巨板——伸出地图外的悬边从高处/平视视角看是
    /// 一堵"黑墙"挡在天上(Gold Oasis 报告的遮挡物)。对齐 C++:只在地形下方画水。
    /// 无地形采样(旧调用/测试)时回退整图面板(不超出地图边界)。</summary>
    public static MeshInstance3D CreateWaterPlane(WaterSpec spec, float mapSize)
    {
        var material = CreateWaterMaterial(spec);
        const float cell = 4f;   // 与地形 tile 同步的网格粒度
        int n = Mathf.Max(1, (int)(mapSize / cell));

        if (TerrainHeight != null)
        {
            // 逐格判定:格子中心地形低于水位 → 该格画水。收集行段(连续格子)拼 quad,
            // 顶点数远少于逐格独立 quad。
            var verts = new System.Collections.Generic.List<float>();
            var indices = new System.Collections.Generic.List<int>();
            int rowVerts = n + 1;
            bool[,] wet = new bool[n, n];
            for (int tz = 0; tz < n; tz++)
                for (int tx = 0; tx < n; tx++)
                {
                    float cx = (tx + 0.5f) * cell, cz = (tz + 0.5f) * cell;
                    // 中心或任一角低于水位都画水——只看中心时,岸边半淹的 4m 格不铺水,
                    // 底下沙滩/水下贴图会露出一长条楼梯(爱琴海河岸)。
                    wet[tx, tz] = TerrainHeight(cx, cz) < spec.Height
                        || TerrainHeight(tx * cell, tz * cell) < spec.Height
                        || TerrainHeight((tx + 1) * cell, tz * cell) < spec.Height
                        || TerrainHeight(tx * cell, (tz + 1) * cell) < spec.Height
                        || TerrainHeight((tx + 1) * cell, (tz + 1) * cell) < spec.Height;
                }
            for (int tz = 0; tz < n; tz++)
                for (int tx = 0; tx < n; tx++)
                {
                    if (!wet[tx, tz]) continue;
                    // 该格 quad 的四个顶点(共享行顶点缓冲:顶点按 (tx,tz) 网格索引)
                    int i00 = tz * rowVerts + tx;
                    // 段式生成太复杂,直接逐格独立 quad(格子最多 256×256=6.5 万,
                    // 实际有水的只有洼地几百格——Gold Oasis 盆地 ~100 格)。
                    int baseIdx = verts.Count / 3;
                    void V(float x, float z) { verts.Add(x); verts.Add(spec.Height); verts.Add(z); }
                    V(tx * cell, tz * cell);
                    V((tx + 1) * cell, tz * cell);
                    V((tx + 1) * cell, (tz + 1) * cell);
                    V(tx * cell, (tz + 1) * cell);
                    indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 1);
                    indices.Add(baseIdx); indices.Add(baseIdx + 3); indices.Add(baseIdx + 2);
                }
            if (verts.Count == 0)
            {
                // 全图无洼地(水位低于所有地形):不画水。
                return new MeshInstance3D { Mesh = null!, Visible = false };
            }
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            for (int i = 0; i < verts.Count; i += 3)
                st.AddVertex(new Vector3(verts[i], verts[i + 1], verts[i + 2]));
            foreach (int idx in indices)
                st.AddIndex(idx);
            var mesh = st.Commit();
            return new MeshInstance3D { Mesh = mesh, MaterialOverride = material };
        }

        // 回退:整图面板(不超出地图)
        var plane = new PlaneMesh();
        plane.Size = new Vector2(mapSize, mapSize);
        plane.Material = material;
        return new MeshInstance3D
        {
            Mesh = plane,
            Position = new Vector3(mapSize * 0.5f, spec.Height, mapSize * 0.5f),
        };
    }

    private static global::Godot.Vector3[] ToVector3s(System.Collections.Generic.List<float> flat)
    {
        var arr = new global::Godot.Vector3[flat.Count / 3];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = new global::Godot.Vector3(flat[i * 3], flat[i * 3 + 1], flat[i * 3 + 2]);
        return arr;
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
