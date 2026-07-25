using Godot;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

/// <summary>
/// Owns the world-space fog-of-war: uploads the local player's blurred LOS grid
/// (see <see cref="FogTextureBuilder"/>) to an L8 texture and drives the terrain's
/// fog shader. Attach() wraps the terrain mesh's material; Update() refreshes the
/// texture — call per frame, it's a 37KB upload at tutorial scale.
/// </summary>
public sealed class FogWorldRenderer
{
    private readonly SimBridge _sim;
    private readonly FogTextureBuilder _builder = new();
    private ShaderMaterial? _mat;
    private Image? _image;
    private ImageTexture? _texture;
    private int _gridSize;

    public FogWorldRenderer(SimBridge sim) => _sim = sim;

    /// <summary>Swap the terrain's material for the fog shader, copying its albedo
    /// setup. worldSize must match the sim-side RangeManager bounds (one fog texel
    /// per 4m LOS vertex). When the terrain already uses a fog-aware ShaderMaterial
    /// (terrain_splat.gdshader), keeps it and just refreshes the fog params.</summary>
    public void Attach(MeshInstance3D terrain, float worldSize)
    {
        if (terrain.GetActiveMaterial(0) is ShaderMaterial splat)
        {
            _mat = splat;
            _mat.SetShaderParameter("world_size", worldSize);
            EnsureTexture(_sim.Range.Los.VerticesPerSide);
            terrain.MaterialOverride = _mat;
            return;
        }

        var src = terrain.GetActiveMaterial(0) as StandardMaterial3D;
        _mat = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://Shaders/fog_terrain.gdshader")
        };
        if (src?.AlbedoTexture != null)
            _mat.SetShaderParameter("albedo_texture", src.AlbedoTexture);
        else
            _mat.SetShaderParameter("albedo_color", src?.AlbedoColor ?? new Color(0.4f, 0.6f, 0.25f));
        _mat.SetShaderParameter("world_size", worldSize);
        EnsureTexture(_sim.Range.Los.VerticesPerSide);
        terrain.MaterialOverride = _mat;
    }

    /// <summary>Re-upload the fog texture from the current LOS grid. Per frame is fine
    /// (the builder reuses buffers; the upload is verticesPerSide² bytes). Recreates the
    /// texture when RangeManager.SetBounds resized the grid after Attach (the PMP load
    /// order does exactly that), so the fog can never freeze on a stale grid.</summary>
    public void Update()
    {
        if (_mat == null || _image == null || _texture == null) return;
        int n = _sim.Range.Los.VerticesPerSide;
        if (n != _gridSize) EnsureTexture(n);
        byte[] data = _builder.BuildBlurred(_sim.Range.Los, (int)_sim.LocalPlayerId);
        _image.SetData(n, n, false, Image.Format.L8, data);
        _texture.Update(_image);
    }

    private void EnsureTexture(int n)
    {
        _gridSize = n;
        _image = Image.CreateEmpty(n, n, false, Image.Format.L8);
        _texture = ImageTexture.CreateFromImage(_image);
        _mat?.SetShaderParameter("fog_texture", _texture);
    }
}
