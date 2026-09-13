using Godot;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Godot;

/// <summary>
/// 领土地面 overlay(对齐原版领地边界渲染):把 <see cref="TerritoryManager"/> 的 4m 格
/// 编码成 RGBA8 纹理(R=owner id,G=blink 标志)喂给地形 shader,shader 侧做邻格差分画
/// 玩家色边界 + 未连通区域闪烁(TIME,纯表现)。Attach() 复用雾已挂的 ShaderMaterial
/// (splat 或 fog 回退);Update() 按 <see cref="TerritoryManager.Version"/> 门控重建,
/// 网格不变时零上传。调色板与单位着色同源(SimBridge.GetPlayerColor)。
/// </summary>
public sealed class TerritoryWorldRenderer
{
    private const int MaxSlots = 17;   // gaia + 16 玩家,与 LosGrid.MaxPlayers 对齐

    private readonly SimBridge _sim;
    private ShaderMaterial? _mat;
    private Image? _image;
    private ImageTexture? _texture;
    private int _gridSize;
    private int _lastVersion = -1;
    private int _lastLosVersion = -1;
    private byte[] _buf = System.Array.Empty<byte>();
    private MeshInstance3D? _borderMesh;
    private MeshInstance3D? _blinkMesh;
    private float _worldSize;

    public TerritoryWorldRenderer(SimBridge sim) => _sim = sim;

    /// <summary>挂到地形当前 ShaderMaterial 上(须在 FogWorld.Attach 之后调用,雾已保证
    /// 地形是 fog 感知的 shader)。CreateFlat 等无 shader 材质路径直接跳过(不画领土)。</summary>
    public void Attach(MeshInstance3D terrain, float worldSize)
    {
        _worldSize = worldSize;
        _mat = terrain.GetActiveMaterial(0) as ShaderMaterial;
        if (_mat == null) return;
        _mat.SetShaderParameter("player_colors", BuildPlayerColors());
        EnsureTexture(_sim.Territory.GridWidth);
        // 领土描边网格(C++ TerritoryBoundary 的轮廓环带):挂地形同级,世界坐标重建。
        // 闪烁环(未连通/衰变区)单独网格 + TIME 脉冲 alpha(原版 renderer 的
        // 0.2+0.8|cos(t·π)| 动画,TerritoryManager 渲染段)。
        if (_borderMesh == null)
        {
            // 原版 overlayline.fs:color = mix(base.rgb, playerColor, mask.r), alpha = playerAlpha·base.a。
            // base = territory_border.png(横向剖面:内侧淡黄微光 → 深色细脊 → 玩家色带 → 深色外缘),
            // mask = territory_border_mask.png(色带段 r=1)。玩家色走顶点 COLOR;blink 版乘
            // 0.2+0.8|cos(t·π)|(CCmpTerritoryManager::Interpolate)。
            var shader = new Shader
            {
                Code = "shader_type spatial; render_mode unshaded, cull_disabled, depth_draw_never;\n"
                    + "uniform sampler2D base_tex : source_color, filter_linear, repeat_disable;\n"
                    + "uniform sampler2D mask_tex : filter_linear, repeat_disable;\n"
                    + "uniform bool blinking = false;\n"
                    + "void fragment() {\n"
                    + "  vec4 base = texture(base_tex, UV);\n"
                    + "  float m = texture(mask_tex, UV).r;\n"
                    + "  float a = blinking ? 0.2 + 0.8 * abs(cos(TIME * 3.14159265)) : 1.0;\n"
                    + "  ALBEDO = mix(base.rgb, COLOR.rgb, m);\n"
                    + "  ALPHA = COLOR.a * base.a * a; }",
            };
            var (baseTex, maskTex) = LoadBorderTextures();

            _borderMesh = new MeshInstance3D
            {
                Name = "TerritoryBorders",
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            var mat = new ShaderMaterial { Shader = shader };
            mat.SetShaderParameter("base_tex", baseTex);
            mat.SetShaderParameter("mask_tex", maskTex);
            mat.SetShaderParameter("blinking", false);
            _borderMesh.MaterialOverride = mat;
            terrain.GetParent()?.AddChild(_borderMesh);

            _blinkMesh = new MeshInstance3D
            {
                Name = "TerritoryBordersBlink",
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            var blinkMat = new ShaderMaterial { Shader = shader };
            blinkMat.SetShaderParameter("base_tex", baseTex);
            blinkMat.SetShaderParameter("mask_tex", maskTex);
            blinkMat.SetShaderParameter("blinking", true);
            _blinkMesh.MaterialOverride = blinkMat;
            terrain.GetParent()?.AddChild(_blinkMesh);
        }
        _lastVersion = -1;   // 强制下次 Update 全量重建
    }

    /// <summary>按 Version 门控重建领土纹理;网格尺寸变化(SetBounds)时自愈重建。
    /// LOS 版本也参与门控——边线网格按已探索格裁剪,探索推进时须重画。</summary>
    public void Update()
    {
        if (_mat == null || _image == null || _texture == null) return;
        var tm = _sim.Territory;
        int n = tm.GridWidth;
        if (n != _gridSize) EnsureTexture(n);
        int losVersion = _sim.Range.LosVersion;
        if (tm.Version == _lastVersion && losVersion == _lastLosVersion) return;
        _lastVersion = tm.Version;
        _lastLosVersion = losVersion;

        if (_buf.Length != n * n * 4) _buf = new byte[n * n * 4];
        var owners = new byte[n * n];
        for (int cz = 0; cz < n; cz++)
            for (int cx = 0; cx < n; cx++)
            {
                var x = Fixed.FromInt(cx * TerritoryManager.CellSize + TerritoryManager.CellSize / 2);
                var z = Fixed.FromInt(cz * TerritoryManager.CellSize + TerritoryManager.CellSize / 2);
                owners[cz * n + cx] = (byte)tm.GetOwner(x, z);
            }

        // 边界微光(对齐 C++ TerritoryTexture.GenerateBitmap):无主/边界格置 192,
        // 四向扫描 max(a-32, cur) 衰减,再把仍满 192 的格删成 0(边界本身留出细缝,
        // 由描边网格补线)。结果进 B 通道,shader 直接采。
        var glow = ComputeGlow(owners, n);

        for (int cz = 0; cz < n; cz++)
            for (int cx = 0; cx < n; cx++)
            {
                var x = Fixed.FromInt(cx * TerritoryManager.CellSize + TerritoryManager.CellSize / 2);
                var z = Fixed.FromInt(cz * TerritoryManager.CellSize + TerritoryManager.CellSize / 2);
                int i = (cz * n + cx) * 4;
                _buf[i] = owners[cz * n + cx];
                _buf[i + 1] = tm.IsTerritoryBlinking(x, z) ? (byte)255 : (byte)0;
                _buf[i + 2] = glow[cz * n + cx];
                _buf[i + 3] = 255;
            }
        _image.SetData(n, n, false, Image.Format.Rgba8, _buf);
        _texture.Update(_image);

        RebuildBorderMesh(owners, n);
    }

    /// <summary>C++ TerritoryTexture.GenerateBitmap 逐位移植:种子 192 + 四向 32/格衰减 +
    /// 删满值格(边界留缝)。返回每格微光 alpha(0..192)。</summary>
    private static byte[] ComputeGlow(byte[] owners, int n)
    {
        const int Seed = 192, Falloff = 32;
        var a = new int[n * n];
        for (int cz = 0; cz < n; cz++)
            for (int cx = 0; cx < n; cx++)
            {
                int own = owners[cz * n + cx];
                bool border = own == 0
                    || (cx > 0 && owners[cz * n + cx - 1] != own)
                    || (cx < n - 1 && owners[cz * n + cx + 1] != own)
                    || (cz > 0 && owners[(cz - 1) * n + cx] != own)
                    || (cz < n - 1 && owners[(cz + 1) * n + cx] != own);
                a[cz * n + cx] = border ? Seed : 0;
            }
        // 行扫描(左右/右左)+ 列扫描(上下/下上)
        for (int z = 0; z < n; z++)
        {
            int cur = 0;
            for (int x = 0; x < n; x++) { cur = System.Math.Max(cur - Falloff, a[z * n + x]); a[z * n + x] = cur; }
            cur = 0;
            for (int x = n - 1; x >= 0; x--) { cur = System.Math.Max(cur - Falloff, a[z * n + x]); a[z * n + x] = cur; }
        }
        for (int x = 0; x < n; x++)
        {
            int cur = 0;
            for (int z = 0; z < n; z++) { cur = System.Math.Max(cur - Falloff, a[z * n + x]); a[z * n + x] = cur; }
            cur = 0;
            for (int z = n - 1; z >= 0; z--) { cur = System.Math.Max(cur - Falloff, a[z * n + x]); a[z * n + x] = cur; }
        }
        var glow = new byte[n * n];
        for (int i = 0; i < n * n; i++)
            glow[i] = a[i] == Seed ? (byte)0 : (byte)a[i];   // 满值格(原始边界/无主)删除
        return glow;
    }


    private void EnsureTexture(int n)
    {
        _gridSize = n;
        _image = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        _texture = ImageTexture.CreateFromImage(_image);
        _mat?.SetShaderParameter("territory_texture", _texture);
        _mat?.SetShaderParameter("territory_cells", (float)n);
    }

    /// <summary>原版 territorymanager.xml:BorderThickness 0.75(半宽,线全宽 1.5m)、
    /// BorderSeparation 0.85(向领土内侧平移,相邻两家边线不重叠)。</summary>
    private const float BorderThickness = 0.75f;
    private const float BorderSeparation = 0.85f;
    private const float OverlayVOffset = 0.2f;   // 原版 OverlayRenderer::OVERLAY_VOFFSET

    /// <summary>加载原版边线贴图(data/mods/mod/art/textures/misc);缺失时退回 1×1 白/白
    /// (纯玩家色实线)。</summary>
    private static (Texture2D Base, Texture2D Mask) LoadBorderTextures()
    {
        Texture2D Fallback(Color c)
        {
            var img = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            img.Fill(c);
            return ImageTexture.CreateFromImage(img);
        }
        string? root = RuntimePaths.FindBinariesRoot();
        if (root == null) return (Fallback(Colors.White), Fallback(Colors.White));
        string dir = System.IO.Path.Combine(root, "data", "mods", "mod", "art", "textures", "misc");
        Texture2D Load(string file, Color fallback)
        {
            string p = System.IO.Path.Combine(dir, file);
            if (!System.IO.File.Exists(p)) return Fallback(fallback);
            var img = Image.LoadFromFile(p);
            return img == null ? Fallback(fallback) : ImageTexture.CreateFromImage(img);
        }
        return (Load("territory_border.png", Colors.White), Load("territory_border_mask.png", Colors.White));
    }

    /// <summary>原版 SimRender::SmoothPointsAverage(闭合):三点均值平滑。</summary>
    private static (float X, float Z)[] SmoothPointsAverage(System.Collections.Generic.IReadOnlyList<(float X, float Z)> pts)
    {
        int n = pts.Count;
        var outPts = new (float X, float Z)[n];
        if (n < 2) { for (int i = 0; i < n; i++) outPts[i] = pts[i]; return outPts; }
        for (int i = 0; i < n; i++)
        {
            var a = pts[(i - 1 + n) % n]; var b = pts[i]; var c = pts[(i + 1) % n];
            outPts[i] = ((a.X + b.X + c.X) / 3f, (a.Z + b.Z + c.Z) / 3f);
        }
        return outPts;
    }

    /// <summary>原版 SimRender::InterpolatePointsRNS(闭合,segmentSamples=4):GPG4 非均匀
    /// 三次样条重采样,并沿切向左法向(领土内侧)平移 offset。</summary>
    private static System.Collections.Generic.List<(float X, float Z)> InterpolatePointsRNS(
        (float X, float Z)[] pts, float offset, int segmentSamples = 4)
    {
        int n = pts.Length;
        var result = new System.Collections.Generic.List<(float X, float Z)>(n * segmentSamples);
        if (n < 1) return result;
        static Vector2 Norm(Vector2 v) => v.LengthSquared() > 1e-12f ? v.Normalized() : Vector2.Zero;
        for (int i = 0; i < n; i++)
        {
            var p0 = new Vector2(pts[(i - 1 + n) % n].X, pts[(i - 1 + n) % n].Z);
            var p1 = new Vector2(pts[i].X, pts[i].Z);
            var p2 = new Vector2(pts[(i + 1) % n].X, pts[(i + 1) % n].Z);
            var p3 = new Vector2(pts[(i + 2) % n].X, pts[(i + 2) % n].Z);
            float l1 = (p2 - p1).Length();
            var s0 = Norm(p1 - p0); var s1 = Norm(p2 - p1); var s2 = Norm(p3 - p2);
            var v1 = Norm(s0 + s1) * l1;
            var v2 = Norm(s1 + s2) * l1;
            var a0 = p1 * 2 + p2 * -2 + v1 + v2;
            var a1 = p1 * -3 + p2 * 3 + v1 * -2 + v2 * -1;
            var a2 = v1;
            var a3 = p1;
            for (int s = 0; s < segmentSamples; s++)
            {
                float t = s / (float)segmentSamples;
                var p = a0 * (t * t * t) + a1 * (t * t) + a2 * t + a3;
                var dp = Norm(a0 * (3 * t * t) + a1 * (2 * t) + a2);
                p += new Vector2(dp.Y * -offset, dp.X * offset);
                result.Add((p.X, p.Y));
            }
        }
        return result;
    }

    /// <summary>领土描边(上游 CCmpTerritoryManager::UpdateBoundaryLines + CTexturedLineRData):
    /// 轮廓环 → 三点平滑 → RNS 样条重采样并向内平移 BorderSeparation → 沿环带铺
    /// 贴图线(半宽 BorderThickness,U 横跨 0..1,内侧 0 / 外侧 1;V 逐点 0/1 交替);
    /// blink 环进独立脉冲网格。LOS 裁剪:两端格均未探索的段不画。</summary>
    private void RebuildBorderMesh(byte[] owners, int n)
    {
        if (_borderMesh == null) return;
        const int cell = TerritoryManager.CellSize;
        const float halfW = BorderThickness;
        float waterY = _sim.Sim.Water.WaterHeight.ToFloat();

        var packed = _sim.Territory.GetBoundaryGridSnapshot();
        var boundaries = TerritoryBoundaryCalculator.ComputeBoundaries(packed, n, cell);

        // LOS 门控:格级已探索表。
        var los = _sim.Range.Los;
        int lp = (int)_sim.LocalPlayerId;
        var explored = new bool[n * n];
        for (int cz = 0; cz < n; cz++)
            for (int cx = 0; cx < n; cx++)
            {
                var (vi, vj) = los.WorldToVertex(
                    Fixed.FromInt(cx * cell + cell / 2), Fixed.FromInt(cz * cell + cell / 2));
                explored[cz * n + cx] = los.IsExplored(lp, vi, vj);
            }
        bool ExploredAt(float wx, float wz)
        {
            int cx = (int)(wx / cell), cz = (int)(wz / cell);
            if (cx < 0 || cz < 0 || cx >= n || cz >= n) return false;
            return explored[cz * n + cx];
        }

        var solid = new RibbonSink();
        var blink = new RibbonSink();

        foreach (var b in boundaries)
        {
            if (b.Points.Count < 2) continue;
            var sink = b.Blinking ? blink : solid;
            // 原版 SBoundaryLine.color = cmpPlayer->GetDisplayedColor()(alpha 1;blink 由 shader 脉冲)。
            var col = SimBridge.GetPlayerColor(b.Owner);
            col.A = 1f;

            // 原版点处理:三点均值平滑 → RNS 样条重采样(每段 4 采样)+ 向内平移 BorderSeparation。
            var pts = InterpolatePointsRNS(SmoothPointsAverage(b.Points), BorderSeparation);
            int count = pts.Count;
            if (count < 2) continue;

            // 逐点角平分线(CTexturedLineRData::Update):b = (s0+s1)×up,再缩放使其在
            // 当前段法向上的投影 = 半宽(斜接,线宽恒定)。左(内侧)U=0,右(外侧)U=1。
            var left = new Vector3[count];
            var right = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                var p = pts[i];
                var prev = pts[(i - 1 + count) % count];
                var next = pts[(i + 1) % count];
                var s0 = new Vector2(p.X - prev.X, p.Z - prev.Z);
                var s1 = new Vector2(next.X - p.X, next.Z - p.Z);
                if (s0.LengthSquared() > 1e-10f) s0 = s0.Normalized();
                if (s1.LengthSquared() > 1e-10f) s1 = s1.Normalized();
                // 左法向 (-dz, dx)。
                var n1 = new Vector2(-s1.Y, s1.X);
                var bis = new Vector2(-(s0.Y + s1.Y), s0.X + s1.X);
                float l = bis.Dot(n1);
                bis = System.Math.Abs(l) > 1e-6f ? bis * (halfW / l) : n1 * halfW;

                float lx = p.X + bis.X, lz = p.Z + bis.Y;
                float rx = p.X - bis.X, rz = p.Z - bis.Y;
                float ly = System.Math.Max(TerrainHeightService.Sample(lx, lz), waterY) + OverlayVOffset;
                float ry = System.Math.Max(TerrainHeightService.Sample(rx, rz), waterY) + OverlayVOffset;
                left[i] = new Vector3(lx, ly, _worldSize - lz);
                right[i] = new Vector3(rx, ry, _worldSize - rz);
            }

            // 环带三角条(闭合):每段两三角;V 逐点 0/1 交替(原版 v = 1 - v);
            // LOS 裁剪按段两端格。
            for (int i = 0; i < count; i++)
            {
                int j = (i + 1) % count;
                if (!ExploredAt(pts[i].X, pts[i].Z) && !ExploredAt(pts[j].X, pts[j].Z))
                    continue;
                float vi = i & 1, vj = j & 1;
                sink.Quad(left[i], right[i], right[j], left[j], vi, vj, col);
            }
        }

        solid.Apply(_borderMesh);
        if (_blinkMesh != null) blink.Apply(_blinkMesh);
    }

    /// <summary>贴图线顶点缓冲:位置 + UV(U 横跨 0 内/1 外,V 沿线 0/1)+ 玩家色。</summary>
    private sealed class RibbonSink
    {
        private readonly System.Collections.Generic.List<Vector3> _verts = new();
        private readonly System.Collections.Generic.List<Vector2> _uvs = new();
        private readonly System.Collections.Generic.List<Color> _colors = new();

        public void Quad(Vector3 li, Vector3 ri, Vector3 rj, Vector3 lj, float vi, float vj, Color col)
        {
            Add(li, 0f, vi, col); Add(ri, 1f, vi, col); Add(rj, 1f, vj, col);
            Add(li, 0f, vi, col); Add(rj, 1f, vj, col); Add(lj, 0f, vj, col);
        }

        private void Add(Vector3 p, float u, float v, Color c)
        {
            _verts.Add(p); _uvs.Add(new Vector2(u, v)); _colors.Add(c);
        }

        public void Apply(MeshInstance3D node)
        {
            if (_verts.Count == 0) { node.Mesh = null; return; }
            var arrays = new global::Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = _verts.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = _uvs.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            node.Mesh = mesh;
        }
    }


    /// <summary>与 SimBridge 单位调色板同源;超出 8 玩家的槽位补 gaia 灰。</summary>
    private static Color[] BuildPlayerColors()
    {
        var colors = new Color[MaxSlots];
        for (int i = 0; i < MaxSlots; i++) colors[i] = SimBridge.GetPlayerColor(i);
        return colors;
    }
}
