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
    private Image? _image;
    private ImageTexture? _texture;
    private int _gridSize;

    public FogWorldRenderer(SimBridge sim) => _sim = sim;

    /// <summary>Swap the terrain's material for the fog shader, copying its albedo
    /// setup. worldSize must match the sim-side RangeManager bounds (one fog texel
    /// per 4m LOS vertex).</summary>
    public void Attach(MeshInstance3D terrain, float worldSize)
    {
        var src = terrain.GetActiveMaterial(0) as StandardMaterial3D;
        var mat = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://Shaders/fog_terrain.gdshader")
        };
        if (src?.AlbedoTexture != null)
            mat.SetShaderParameter("albedo_texture", src.AlbedoTexture);
        else
            mat.SetShaderParameter("albedo_color", src?.AlbedoColor ?? new Color(0.4f, 0.6f, 0.25f));
        mat.SetShaderParameter("world_size", worldSize);

        _gridSize = _sim.Range.Los.VerticesPerSide;
        _image = Image.CreateEmpty(_gridSize, _gridSize, false, Image.Format.L8);
        _texture = ImageTexture.CreateFromImage(_image);
        mat.SetShaderParameter("fog_texture", _texture);

        terrain.MaterialOverride = mat;
    }

    /// <summary>Re-upload the fog texture from the current LOS grid. Per frame is fine
    /// (the builder reuses buffers; the upload is verticesPerSide² bytes).</summary>
    public void Update()
    {
        if (_image == null || _texture == null) return;
        int n = _sim.Range.Los.VerticesPerSide;
        if (n != _gridSize) return; // bounds changed without re-attach — keep the stale texture
        byte[] data = _builder.BuildBlurred(_sim.Range.Los, (int)_sim.LocalPlayerId);
        _image.SetData(n, n, false, Image.Format.L8, data);
        _texture.Update(_image);
    }
}
