using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot;

public static class SelectionRing
{
    private static readonly Dictionary<Color, StandardMaterial3D> _mats = new();

    private static StandardMaterial3D CreateRingMat(Color color)
    {
        var mat = new StandardMaterial3D();
        mat.AlbedoColor = color;
        mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        mat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        mat.NoDepthTest = true;
        return mat;
    }

    /// <summary>按颜色缓存材质——修复"首个被选实体的颜色污染全会话"
    /// (原 _ringMat ??= 一次性缓存,先选敌方后己方也全红)。</summary>
    private static StandardMaterial3D MatFor(Color color)
    {
        if (!_mats.TryGetValue(color, out var m))
        {
            m = CreateRingMat(color);
            _mats[color] = m;
        }
        return m;
    }

    public enum Shape { Circle, Square }

    /// <summary>
    /// Selection marker matching the original: units get a circular ring,
    /// buildings get a square outline around their footprint.
    /// 绘制为贴地三角带条(原版是带宽度贴图四边形;LineStrip 在 gl_compatibility
    /// 只有 1px 发丝线,且线宽不可控)。
    /// </summary>
    public static MeshInstance3D Create(float radius, Color friendlyColor, Color enemyColor,
        Shape shape = Shape.Circle)
    {
        

        var points = shape == Shape.Square ? SquarePoints(radius) : CirclePoints(radius);
        float lineWidth = shape == Shape.Square ? 0.5f : 0.35f;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        AppendOutlineBand(st, points, lineWidth);
        var mesh = st.Commit();
        var instance = new MeshInstance3D { Mesh = mesh };
        mesh.SurfaceSetMaterial(0, MatFor(friendlyColor));
        return instance;
    }

    /// <summary>按建筑实际 footprint 画矩形选择框(半宽/半深 + 带宽),替代固定半径正方形。</summary>
    public static MeshInstance3D CreateRect(float halfX, float halfZ, Color color, float lineWidth = 0.5f)
    {
        
        var points = new Vector3[]
        {
            new(-halfX, 0.1f, -halfZ),
            new(halfX, 0.1f, -halfZ),
            new(halfX, 0.1f, halfZ),
            new(-halfX, 0.1f, halfZ),
            new(-halfX, 0.1f, -halfZ),
        };
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        AppendOutlineBand(st, points, lineWidth);
        var mesh = st.Commit();
        var instance = new MeshInstance3D { Mesh = mesh };
        mesh.SurfaceSetMaterial(0, MatFor(color));
        return instance;
    }

    /// <summary>把闭合折线画成带宽度的贴地带条:每段一个四边形(内外各偏 width/2,
    /// 沿 XZ 平面法线),接缝处允许少量重叠(视觉无缝)。</summary>
    private static void AppendOutlineBand(SurfaceTool st, Vector3[] points, float width)
    {
        float half = width * 0.5f;
        for (int i = 0; i < points.Length - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            var dir = (b - a).Normalized();
            // XZ 平面内向右法线
            var n = new Vector3(-dir.Z, 0, dir.X) * half;
            var aOut = a + n; var aIn = a - n;
            var bOut = b + n; var bIn = b - n;
            st.AddVertex(aIn); st.AddVertex(aOut); st.AddVertex(bOut);
            st.AddVertex(aIn); st.AddVertex(bOut); st.AddVertex(bIn);
        }
    }

    private static Vector3[] SquarePoints(float half) => new Vector3[]
    {
        new(-half, 0.1f, -half),
        new(half, 0.1f, -half),
        new(half, 0.1f, half),
        new(-half, 0.1f, half),
        new(-half, 0.1f, -half),
    };

    private static Vector3[] CirclePoints(float radius)
    {
        const int segments = 32;
        var pts = new Vector3[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float a = i * Mathf.Tau / segments;
            pts[i] = new Vector3(Mathf.Cos(a) * radius, 0.1f, Mathf.Sin(a) * radius);
        }
        return pts;
    }

    public static MeshInstance3D CreateHealthBar(float healthFraction)
    {
        float w = 2f;
        float h = 0.3f;
        float greenW = w * Mathf.Clamp(healthFraction, 0f, 1f);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        st.SetColor(new Color(0.1f, 0.7f, 0.1f));
        st.AddVertex(new Vector3(-w / 2, 0, 0));
        st.AddVertex(new Vector3(-w / 2 + greenW, 0, 0));
        st.AddVertex(new Vector3(-w / 2 + greenW, h, 0));
        st.AddVertex(new Vector3(-w / 2, 0, 0));
        st.AddVertex(new Vector3(-w / 2 + greenW, h, 0));
        st.AddVertex(new Vector3(-w / 2, h, 0));

        if (greenW < w)
        {
            st.SetColor(new Color(0.7f, 0.1f, 0.1f));
            st.AddVertex(new Vector3(-w / 2 + greenW, 0, 0));
            st.AddVertex(new Vector3(w / 2, 0, 0));
            st.AddVertex(new Vector3(w / 2, h, 0));
            st.AddVertex(new Vector3(-w / 2 + greenW, 0, 0));
            st.AddVertex(new Vector3(w / 2, h, 0));
            st.AddVertex(new Vector3(-w / 2 + greenW, h, 0));
        }

        var mesh = st.Commit();
        var instance = new MeshInstance3D { Mesh = mesh };
        var mat = new StandardMaterial3D();
        mat.VertexColorUseAsAlbedo = true;
        mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        mat.NoDepthTest = true;
        mesh.SurfaceSetMaterial(0, mat);
        instance.Position = new Vector3(0, 4f, 0);
        return instance;
    }

    /// <summary>Procedural rally-point flag fallback: a thin dark pole with a player-coloured
    /// quad at the top. Used only when the real <c>{civ}_waypoint_flag</c> actor fails to
    /// instantiate (e.g. art not converted). The returned Node3D's origin sits at ground
    /// level — raise it by setting Position.Y to the sampled terrain height.</summary>
    public static Node3D CreateRallyFlag(Color color)
    {
        const float poleHeight = 3f;
        var root = new Node3D();

        var pole = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.06f, Height = poleHeight },
            Position = new Vector3(0, poleHeight * 0.5f, 0),
        };
        pole.MaterialOverride = FlagMat(new Color(0.05f, 0.05f, 0.05f));
        root.AddChild(pole);

        const float flagW = 1.2f;
        const float flagH = 0.8f;
        var flag = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(flagW, flagH) },
            Position = new Vector3(flagW * 0.5f, poleHeight - flagH * 0.5f, 0),
        };
        flag.MaterialOverride = FlagMat(color);
        root.AddChild(flag);

        return root;
    }

    private static StandardMaterial3D FlagMat(Color color)
    {
        var mat = new StandardMaterial3D();
        mat.AlbedoColor = color;
        mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        mat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        mat.NoDepthTest = true;
        return mat;
    }

    private static StandardMaterial3D? _lineMat;

    /// <summary>Rally line material: the original <c>rallypoint_line.png</c> tiled along the
    /// path, transparent, unshaded, drawn over terrain (no depth test) — a flat ground decal
    /// matching <c>CCmpRallyPointRenderer</c>. Repeat is enabled so UV &gt; 1 tiles the strip.</summary>
    private static StandardMaterial3D LineMat()
    {
        if (_lineMat != null) return _lineMat;
        var mat = new StandardMaterial3D();
        mat.AlbedoTexture = ResourceLoader.Load<Texture2D>("res://assets/textures/misc/rallypoint_line.png");
        mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        mat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        mat.NoDepthTest = true;
        mat.TextureRepeat = true;
        _lineMat = mat;
        return mat;
    }

    /// <summary>Rally-point path line: a flat textured ribbon laid on the ground along the
    /// waypoints from the building to the rally (对齐原版 CCmpRallyPointRenderer's textured
    /// strip). <paramref name="points"/> are world-space, terrain-height-sampled, in travel
    /// order (building → rally). The texture tiles along the length; the strip width is fixed.
    /// Returns a MeshInstance3D (empty mesh if fewer than 2 points).</summary>
    public static MeshInstance3D CreateRallyLine(IReadOnlyList<Vector3> points)
    {
        var instance = new MeshInstance3D();
        if (points == null || points.Count < 2) return instance;

        const float halfWidth = 0.6f;
        const float tileLength = 3f;     // world units per one texture tile along the path

        // Left/right edge vertices, offset perpendicular to each segment's direction (XZ plane).
        var left = new List<Vector3>(points.Count);
        var right = new List<Vector3>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 p = points[i];
            Vector3 dir = i < points.Count - 1 ? points[i + 1] - p : p - points[i - 1];
            Vector3 d = new(dir.X, 0, dir.Z);
            float len = d.Length();
            d = len < 0.0001f ? new Vector3(0, 0, 1) : d / len;
            Vector3 perp = new Vector3(-d.Z, 0f, d.X) * halfWidth;
            left.Add(p + perp);
            right.Add(p - perp);
        }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        float v = 0f;
        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 seg = points[i + 1] - points[i];
            float segLen = new Vector3(seg.X, 0, seg.Z).Length();
            float v0 = v / tileLength;
            float v1 = (v + segLen) / tileLength;
            v += segLen;

            Vector3 lb = left[i], lt = left[i + 1], rb = right[i], rt = right[i + 1];
            // Two triangles per segment quad (UV.x across width 0..1, UV.y along length tiles).
            st.SetUV(new Vector2(0, v0)); st.AddVertex(lb);
            st.SetUV(new Vector2(1, v0)); st.AddVertex(rb);
            st.SetUV(new Vector2(0, v1)); st.AddVertex(lt);

            st.SetUV(new Vector2(1, v0)); st.AddVertex(rb);
            st.SetUV(new Vector2(1, v1)); st.AddVertex(rt);
            st.SetUV(new Vector2(0, v1)); st.AddVertex(lt);
        }

        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, LineMat());
        instance.Mesh = mesh;
        return instance;
    }
}
