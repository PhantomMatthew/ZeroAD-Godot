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
            _borderMesh = new MeshInstance3D
            {
                Name = "TerritoryBorders",
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            _borderMesh.MaterialOverride = mat;
            terrain.GetParent()?.AddChild(_borderMesh);

            _blinkMesh = new MeshInstance3D
            {
                Name = "TerritoryBordersBlink",
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            var blinkShader = new Shader
            {
                Code = "shader_type spatial; render_mode unshaded, cull_disabled;\n"
                    + "uniform vec4 blink_color : source_color = vec4(1.0);\n"
                    + "void fragment() { float a = 0.2 + 0.8 * abs(cos(TIME * 3.14159265));\n"
                    + "  ALBEDO = blink_color.rgb; ALPHA = blink_color.a * a; }",
            };
            var blinkMat = new ShaderMaterial { Shader = blinkShader };
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

    /// <summary>领土描边(上游 CTerritoryBoundaryCalculator 轮廓追踪 + 环带):
    /// 边界 = 追踪出的闭合环(角部连续,替代逐边 quad 的断角);环带沿环向两侧各
    /// 扩半宽,内顶点加小方帽接角;blink 环进独立脉冲网格(TIME 动画 alpha)。
    /// LOS 裁剪:两端格均未探索的段不画(战争迷雾不透敌方疆域线)。</summary>
    private void RebuildBorderMesh(byte[] owners, int n)
    {
        if (_borderMesh == null) return;
        const int cell = TerritoryManager.CellSize;
        const float halfW = 0.4f;

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

        var verts = new System.Collections.Generic.List<Vector3>();
        var colors = new System.Collections.Generic.List<Color>();
        var blinkVerts = new System.Collections.Generic.List<Vector3>();
        int blinkOwner = -1;

        foreach (var b in boundaries)
        {
            var sink = b.Blinking ? blinkVerts : verts;
            var col = SimBridge.GetPlayerColor(b.Owner);
            if (b.Blinking)
            {
                // 脉冲网格整体一个 blink_color(每环一色 —— 多 blink 主时取末个;
                // 闪烁区本来就稀有,实测无感;逐环分网格过碎)。
                blinkOwner = b.Owner;
            }
            if (!b.Blinking) col.A = 0.92f;
            int count = b.Points.Count;
            if (count < 2) continue;

            // 每点算相邻两边的平均法向(miter),环带顶点 = 点 ± 法向×halfW。
            var left = new Vector3[count];
            var right = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                var p = b.Points[i];
                var prev = b.Points[(i - 1 + count) % count];
                var next = b.Points[(i + 1) % count];
                float dx1 = p.X - prev.X, dz1 = p.Z - prev.Z;
                float dx2 = next.X - p.X, dz2 = next.Z - p.Z;
                // 每边法向(垂直单位):(-dz, dx)/len。
                float l1 = (float)System.Math.Sqrt(dx1 * dx1 + dz1 * dz1);
                float l2 = (float)System.Math.Sqrt(dx2 * dx2 + dz2 * dz2);
                float nx = 0, nz = 0;
                if (l1 > 0.001f) { nx += -dz1 / l1; nz += dx1 / l1; }
                if (l2 > 0.001f) { nx += -dz2 / l2; nz += dx2 / l2; }
                float nl = (float)System.Math.Sqrt(nx * nx + nz * nz);
                if (nl < 0.001f) { nx = 1; nz = 0; nl = 1; }
                nx = nx / nl * halfW; nz = nz / nl * halfW;

                float y1 = TerrainHeightService.Sample(p.X + nx, p.Z + nz) + 0.07f;
                float y2 = TerrainHeightService.Sample(p.X - nx, p.Z - nz) + 0.07f;
                left[i] = new Vector3(p.X + nx, y1, _worldSize - (p.Z + nz));
                right[i] = new Vector3(p.X - nx, y2, _worldSize - (p.Z - nz));
            }

            // 环带三角条(闭合):每段两三角;LOS 裁剪按段两端格。
            for (int i = 0; i < count; i++)
            {
                int j = (i + 1) % count;
                if (!ExploredAt(b.Points[i].X, b.Points[i].Z)
                    && !ExploredAt(b.Points[j].X, b.Points[j].Z))
                    continue;
                foreach (var v in new[] { left[i], right[i], right[j], left[i], right[j], left[j] })
                    sink.Add(v);
                if (!b.Blinking)
                    for (int k = 0; k < 6; k++) colors.Add(col);
            }
        }

        SetMesh(_borderMesh, verts, colors);
        if (_blinkMesh != null)
        {
            if (blinkOwner >= 0 && blinkVerts.Count > 0)
            {
                var c = SimBridge.GetPlayerColor(blinkOwner);
                c.A = 0.92f;
                (_blinkMesh.MaterialOverride as ShaderMaterial)
                    ?.SetShaderParameter("blink_color", c);
                var arr = new global::Godot.Collections.Array();
                arr.Resize((int)Mesh.ArrayType.Max);
                arr[(int)Mesh.ArrayType.Vertex] = blinkVerts.ToArray();
                var mesh = new ArrayMesh();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
                _blinkMesh.Mesh = mesh;
            }
            else
            {
                _blinkMesh.Mesh = null;
            }
        }
    }

    private static void SetMesh(MeshInstance3D node, System.Collections.Generic.List<Vector3> verts,
        System.Collections.Generic.List<Color> colors)
    {
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        var mesh = new ArrayMesh();
        if (verts.Count > 0)
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        node.Mesh = mesh;
    }


    /// <summary>与 SimBridge 单位调色板同源;超出 8 玩家的槽位补 gaia 灰。</summary>
    private static Color[] BuildPlayerColors()
    {
        var colors = new Color[MaxSlots];
        for (int i = 0; i < MaxSlots; i++) colors[i] = SimBridge.GetPlayerColor(i);
        return colors;
    }
}
