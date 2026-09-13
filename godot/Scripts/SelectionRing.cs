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
        Shape shape = Shape.Circle, float lineWidth = -1f)
    {
        

        var points = shape == Shape.Square ? SquarePoints(radius) : CirclePoints(radius);
        float width = lineWidth > 0f ? lineWidth : (shape == Shape.Square ? 0.5f : 0.35f);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        AppendOutlineBand(st, points, width);
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

    /// <summary>原版 CCmpSelectable DYNAMIC_QUAD:贴地 ellipse.png × mask 染玩家色。
    /// 参数是 C++ halfSize(圆 footprint = 半径;方 footprint = 宽/深的一半)。
    /// texRel/maskRel 相对 art/textures/selection/(单位 128x128,动物 128x256)。</summary>
    private static Shader? _ellipseShader;
    private static readonly Dictionary<string, ShaderMaterial> _ellipseMats = new();

    public static MeshInstance3D CreateEllipse(float halfX, float halfZ, Color playerColor,
        string? texRel = null, string? maskRel = null)
    {
        float hx = halfX > 0.05f ? halfX : 1.5f;
        float hz = halfZ > 0.05f ? halfZ : hx;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetColor(playerColor);
        // UV: C++ overlay quad 四角;Y 略抬避免 z-fight。
        void V(float x, float z, float u, float v)
        {
            st.SetColor(playerColor);
            st.SetUV(new Vector2(u, v));
            st.AddVertex(new Vector3(x, 0.08f, z));
        }
        V(-hx, -hz, 0f, 0f); V(hx, -hz, 1f, 0f); V(hx, hz, 1f, 1f);
        V(-hx, -hz, 0f, 0f); V(hx, hz, 1f, 1f); V(-hx, hz, 0f, 1f);
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, EllipseMat(texRel, maskRel));
        return new MeshInstance3D
        {
            Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    private static ShaderMaterial EllipseMat(string? texRel, string? maskRel)
    {
        string tex = string.IsNullOrEmpty(texRel) ? "128x128/ellipse.png" : texRel;
        string mask = string.IsNullOrEmpty(maskRel)
            ? tex.Replace("ellipse.png", "ellipse_mask.png")
            : maskRel;
        string key = tex + "|" + mask;
        if (_ellipseMats.TryGetValue(key, out var cached)) return cached;
        _ellipseShader ??= new Shader
        {
            Code = "shader_type spatial;\n"
                    + "render_mode unshaded, cull_disabled, depth_draw_never, fog_disabled, blend_mix;\n"
                    + "uniform sampler2D base_tex : source_color, filter_linear, repeat_disable;\n"
                    + "uniform sampler2D mask_tex : filter_linear, repeat_disable;\n"
                    + "void fragment() {\n"
                    + "  vec4 base = texture(base_tex, UV);\n"
                    + "  float m = texture(mask_tex, UV).r;\n"
                    + "  ALBEDO = mix(base.rgb, COLOR.rgb, m);\n"
                    + "  ALPHA = COLOR.a * base.a;\n"
                    + "}\n",
        };
        var mat = new ShaderMaterial { Shader = _ellipseShader };
        mat.SetShaderParameter("base_tex", LoadSelectionTex(tex));
        mat.SetShaderParameter("mask_tex", LoadSelectionTex(mask));
        _ellipseMats[key] = mat;
        return mat;
    }

    private static Texture2D LoadSelectionTex(string rel)
    {
        string norm = rel.Replace('\\', '/').Trim().TrimStart('/');
        string[] segs = norm.Split('/');
        var parts = new string[3 + segs.Length];
        parts[0] = "art";
        parts[1] = "textures";
        parts[2] = "selection";
        segs.CopyTo(parts, 3);
        string? path = RuntimePaths.FindPublicPath(parts);
        if (path == null && segs.Length == 1)
            path = RuntimePaths.FindPublicPath("art", "textures", "selection", "128x128", segs[0]);
        if (path != null)
        {
            var img = Image.LoadFromFile(path);
            if (img != null) return ImageTexture.CreateFromImage(img);
        }
        var fallback = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        fallback.Fill(Colors.White);
        return ImageTexture.CreateFromImage(fallback);
    }

    /// <summary>攻击射程圈(原版 RangeOverlay:选中带 RangeOverlay 的实体时显示;
    /// CC/箭塔的防御半径)。贴地沿地形起伏(每段采样高度),浅色细环。
    /// centerSimX/Z = 实体 sim 坐标;返回的节点挂在实体视觉下(局部坐标系=sim 系)。</summary>
    public static MeshInstance3D CreateRangeRing(float radius, float centerSimX, float centerSimZ,
        Color color)
    {
        const int segs = 96;
        const float width = 0.45f;
        float baseY = TerrainHeightService.Sample(centerSimX, centerSimZ);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < segs; i++)
        {
            float a0 = i * Mathf.Tau / segs;
            float a1 = (i + 1) * Mathf.Tau / segs;
            // 内外圈各采样地形(局部 Y = 采样高 − 实体基底高 + 抬高)。
            var v00 = RingPoint(centerSimX, centerSimZ, radius - width / 2, a0, baseY);
            var v01 = RingPoint(centerSimX, centerSimZ, radius - width / 2, a1, baseY);
            var v10 = RingPoint(centerSimX, centerSimZ, radius + width / 2, a0, baseY);
            var v11 = RingPoint(centerSimX, centerSimZ, radius + width / 2, a1, baseY);
            st.AddTriangleFan(new[] { v00, v10, v11 });
            st.AddTriangleFan(new[] { v00, v11, v01 });
        }
        var mesh = st.Commit();
        var instance = new MeshInstance3D { Mesh = mesh };
        mesh.SurfaceSetMaterial(0, MatFor(color));
        return instance;
    }

    private static Vector3 RingPoint(float cx, float cz, float r, float angle, float baseY)
    {
        float sx = cx + r * Mathf.Cos(angle);
        float sz = cz + r * Mathf.Sin(angle);
        float y = TerrainHeightService.Sample(sx, sz) - baseY + 0.08f;
        return new Vector3(sx - cx, y, sz - cz);
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

    // 原版 template_unit StatusBars 缺省;建筑/船/树由模板覆盖。
    public const float DefaultBarWidth = 2.0f;
    public const float DefaultBarHeight = 0.333f;

    /// <summary>原版 foreground_overlay.fs = <c>texture * colorMul</c> 无光照;
    /// 斜面高光画在 health_fg/bg.png 的纵向渐变里(1px 宽拉满条宽)。公告板 + 禁雾 +
    /// 无深度测试对齐 OverlayRenderer 前景 pass(C++ 血条不受距离雾冲洗成一片平色)。</summary>
    private static readonly Dictionary<string, StandardMaterial3D> _overlayMats = new();

    private static StandardMaterial3D OverlayMat(string file)
    {
        if (_overlayMats.TryGetValue(file, out var cached)) return cached;
        var mat = new StandardMaterial3D
        {
            AlbedoTexture = LoadStatusIcon(file),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = true,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            DisableFog = true,
            DisableReceiveShadows = true,
            VertexColorUseAsAlbedo = true,
            // 默认 LinearWithMipmaps 会把 1×N 斜面渐变平均成一块纯色(远距 RTS 镜头)。
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
            // fg 叠在同平面 bg 上;提高优先级避免 gl_compatibility 深度冲突把绿条吞掉。
            RenderPriority = file.EndsWith("_fg.png", System.StringComparison.Ordinal) ? 1 : 0,
        };
        _overlayMats[file] = mat;
        return mat;
    }

    private static Texture2D LoadStatusIcon(string file)
    {
        string? path = RuntimePaths.FindPublicPath(
            "art", "textures", "ui", "session", "icons", file);
        if (path != null)
        {
            var img = Image.LoadFromFile(path);
            if (img != null) return ImageTexture.CreateFromImage(img);
        }
        var fallback = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        fallback.Fill(Colors.White);
        return ImageTexture.CreateFromImage(fallback);
    }

    /// <summary>原版 OverlayRenderer 公告板四边形:局部 X=相机右,Y=相机上;
    /// UV 与 C++ 一致 ((x0,y0)→(0,1) … (x0,y1)→(0,0))。z 微偏让 fg 叠在 bg 前。</summary>
    private static void AddOverlayQuad(SurfaceTool st, float x0, float y0, float x1, float y1, Color color, float z = 0f)
    {
        st.SetColor(color);
        st.SetUV(new Vector2(0f, 1f)); st.AddVertex(new Vector3(x0, y0, z));
        st.SetUV(new Vector2(1f, 1f)); st.AddVertex(new Vector3(x1, y0, z));
        st.SetUV(new Vector2(1f, 0f)); st.AddVertex(new Vector3(x1, y1, z));
        st.SetUV(new Vector2(0f, 1f)); st.AddVertex(new Vector3(x0, y0, z));
        st.SetUV(new Vector2(1f, 0f)); st.AddVertex(new Vector3(x1, y1, z));
        st.SetUV(new Vector2(0f, 0f)); st.AddVertex(new Vector3(x0, y1, z));
    }

    private static MeshInstance3D FinishOverlayMesh(ArrayMesh mesh)
    {
        return new MeshInstance3D
        {
            Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    /// <summary>原版 StatusBars.AddBar:全宽 <c>{type}_bg.png</c> + 按 amount 裁切宽度的
    /// <c>{type}_fg.png</c>(UV 仍铺满,随宽度压扁)。type ∈ health/supply/pack/upgrade。</summary>
    public static MeshInstance3D CreateStatusBar(string type, float amount,
        float width = DefaultBarWidth, float height = DefaultBarHeight)
    {
        amount = Mathf.Clamp(amount, 0f, 1f);
        float w = width > 0f ? width : DefaultBarWidth;
        float h = height > 0f ? height : DefaultBarHeight;
        var mesh = new ArrayMesh();

        var bg = new SurfaceTool();
        bg.Begin(Mesh.PrimitiveType.Triangles);
        AddOverlayQuad(bg, -w / 2f, 0f, w / 2f, h, Colors.White);
        bg.Commit(mesh);
        mesh.SurfaceSetMaterial(0, OverlayMat(type + "_bg.png"));

        if (amount > 0.001f)
        {
            var fg = new SurfaceTool();
            fg.Begin(Mesh.PrimitiveType.Triangles);
            // 原版:x1 = width * (amount - 0.5) → 满血到 +w/2,空到 -w/2。
            AddOverlayQuad(fg, -w / 2f, 0f, w * (amount - 0.5f), h, Colors.White, 0.05f);
            fg.Commit(mesh);
            mesh.SurfaceSetMaterial(1, OverlayMat(type + "_fg.png"));
        }
        return FinishOverlayMesh(mesh);
    }

    public static MeshInstance3D CreateHealthBar(float healthFraction,
        float width = DefaultBarWidth, float height = DefaultBarHeight) =>
        CreateStatusBar("health", healthFraction, width, height);

    /// <summary>占领条(原版 StatusBars.AddCaptureBar):每段一张
    /// <c>capture_bar.png</c>,<c>colorMul</c>=玩家色(灰度贴图染成玩家色的斜面)。</summary>
    public static MeshInstance3D CreateCaptureBar(IReadOnlyList<(float Fraction, Color Color)> segments,
        float width = DefaultBarWidth, float height = DefaultBarHeight)
    {
        float w = width > 0f ? width : DefaultBarWidth;
        float h = height > 0f ? height : DefaultBarHeight;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float cursor = -w / 2f;
        foreach (var (frac, color) in segments)
        {
            if (frac <= 0f) continue;
            float segW = w * Mathf.Clamp(frac, 0f, 1f);
            AddOverlayQuad(st, cursor, 0f, cursor + segW, h, color);
            cursor += segW;
        }
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, OverlayMat("capture_bar.png"));
        return FinishOverlayMesh(mesh);
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
