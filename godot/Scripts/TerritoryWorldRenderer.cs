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
    private byte[] _buf = System.Array.Empty<byte>();
    private MeshInstance3D? _borderMesh;
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
        // 领土描边网格(C++ TerritoryBoundary 的细线):挂地形同级,世界坐标重建。
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
        }
        _lastVersion = -1;   // 强制下次 Update 全量重建
    }

    /// <summary>按 Version 门控重建领土纹理;网格尺寸变化(SetBounds)时自愈重建。</summary>
    public void Update()
    {
        if (_mat == null || _image == null || _texture == null) return;
        var tm = _sim.Territory;
        int n = tm.GridWidth;
        if (n != _gridSize) EnsureTexture(n);
        if (tm.Version == _lastVersion) return;
        _lastVersion = tm.Version;

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

    /// <summary>领土描边网格(C++ TerritoryBoundary):沿异主格界画贴地四边形条,
    /// 顶点色 = 属主玩家色。双色边界(对齐原版):两异主格共边时,各画半宽带、各用
    /// 己方玩家色,合起来就是一条左半 A 色、右半 B 色的双色线(原版为每个 owner 独立
    /// 生成边界环,叠加成同效)。gaia(owner 0)侧不画(只画有主侧)。</summary>
    private void RebuildBorderMesh(byte[] owners, int n)
    {
        if (_borderMesh == null) return;
        const int cell = TerritoryManager.CellSize;   // 4m
        const float halfW = 0.4f;
        var verts = new System.Collections.Generic.List<Vector3>();
        var colors = new System.Collections.Generic.List<Color>();

        // 画半宽带:边在 (simX0,simZ0)-(simX1,simZ1),owner 色条偏向 dirX/dirZ 侧
        // (法向正方向 = owner 己方格),宽 halfW。gaia(owner 0)跳过。
        void EmitHalfEdge(float simX0, float simZ0, float simX1, float simZ1,
            float dirX, float dirZ, int owner)
        {
            if (owner <= 0) return;   // gaia 不画
            var c = SimBridge.GetPlayerColor(owner);
            c.A = 0.92f;
            Vector3 P(float sx, float sz, float ox, float oz)
            {
                float y = TerrainHeightService.Sample(sx + ox, sz + oz) + 0.07f;
                return new Vector3(sx + ox, y, _worldSize - (sz + oz));
            }
            // 从边线(ox=0)向 owner 侧偏 halfW(ox=dirX*halfW)
            var a0 = P(simX0, simZ0, 0, 0);
            var a1 = P(simX0, simZ0, dirX * halfW, dirZ * halfW);
            var b0 = P(simX1, simZ1, 0, 0);
            var b1 = P(simX1, simZ1, dirX * halfW, dirZ * halfW);
            foreach (var v in new[] { a0, a1, b1, a0, b1, b0 }) { verts.Add(v); colors.Add(c); }
        }

        for (int cz = 0; cz < n; cz++)
            for (int cx = 0; cx < n; cx++)
            {
                int own = owners[cz * n + cx];
                // +x 邻边:own 在左(cx 格),nb 在右(cx+1 格)。边线 x=(cx+1)*cell。
                if (cx + 1 < n)
                {
                    int nb = owners[cz * n + cx + 1];
                    if (nb != own)
                    {
                        float ex = (cx + 1) * cell;
                        // own 侧偏 -x(法向指向 own 格),nb 侧偏 +x。
                        EmitHalfEdge(ex, cz * cell, ex, (cz + 1) * cell, -1f, 0f, own);
                        EmitHalfEdge(ex, cz * cell, ex, (cz + 1) * cell, 1f, 0f, nb);
                    }
                }
                // +z 邻边:own 在上(cz 格),nb 在下(cz+1 格)。边线 z=(cz+1)*cell。
                if (cz + 1 < n)
                {
                    int nb = owners[(cz + 1) * n + cx];
                    if (nb != own)
                    {
                        float ez = (cz + 1) * cell;
                        // own 侧偏 -z,nb 侧偏 +z。
                        EmitHalfEdge(cx * cell, ez, (cx + 1) * cell, ez, 0f, -1f, own);
                        EmitHalfEdge(cx * cell, ez, (cx + 1) * cell, ez, 0f, 1f, nb);
                    }
                }
            }

        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        var mesh = new ArrayMesh();
        if (verts.Count > 0)
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _borderMesh.Mesh = mesh;
    }

    /// <summary>与 SimBridge 单位调色板同源;超出 8 玩家的槽位补 gaia 灰。</summary>
    private static Color[] BuildPlayerColors()
    {
        var colors = new Color[MaxSlots];
        for (int i = 0; i < MaxSlots; i++) colors[i] = SimBridge.GetPlayerColor(i);
        return colors;
    }
}
