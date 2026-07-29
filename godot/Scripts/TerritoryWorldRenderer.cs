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

    public TerritoryWorldRenderer(SimBridge sim) => _sim = sim;

    /// <summary>挂到地形当前 ShaderMaterial 上(须在 FogWorld.Attach 之后调用,雾已保证
    /// 地形是 fog 感知的 shader)。CreateFlat 等无 shader 材质路径直接跳过(不画领土)。</summary>
    public void Attach(MeshInstance3D terrain, float worldSize)
    {
        _mat = terrain.GetActiveMaterial(0) as ShaderMaterial;
        if (_mat == null) return;
        _mat.SetShaderParameter("player_colors", BuildPlayerColors());
        EnsureTexture(_sim.Territory.GridWidth);
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
        for (int cz = 0; cz < n; cz++)
            for (int cx = 0; cx < n; cx++)
            {
                var x = Fixed.FromInt(cx * TerritoryManager.CellSize + TerritoryManager.CellSize / 2);
                var z = Fixed.FromInt(cz * TerritoryManager.CellSize + TerritoryManager.CellSize / 2);
                int i = (cz * n + cx) * 4;
                _buf[i] = (byte)tm.GetOwner(x, z);
                _buf[i + 1] = tm.IsTerritoryBlinking(x, z) ? (byte)255 : (byte)0;
                _buf[i + 2] = 0;
                _buf[i + 3] = 255;
            }
        _image.SetData(n, n, false, Image.Format.Rgba8, _buf);
        _texture.Update(_image);
    }

    private void EnsureTexture(int n)
    {
        _gridSize = n;
        _image = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        _texture = ImageTexture.CreateFromImage(_image);
        _mat?.SetShaderParameter("territory_texture", _texture);
        _mat?.SetShaderParameter("territory_cells", (float)n);
    }

    /// <summary>与 SimBridge 单位调色板同源;超出 8 玩家的槽位补 gaia 灰。</summary>
    private static Color[] BuildPlayerColors()
    {
        var colors = new Color[MaxSlots];
        for (int i = 0; i < MaxSlots; i++) colors[i] = SimBridge.GetPlayerColor(i);
        return colors;
    }
}
