using Godot;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

/// <summary>
/// Owns the world-space fog-of-war: uploads the local player's blurred LOS grid
/// (see <see cref="FogTextureBuilder"/>) to an L8 texture and drives the terrain's
/// fog shader. Attach() wraps the terrain mesh's material; Update() refreshes the
/// texture, gated on <see cref="RangeManager.LosVersion"/> so it only rebuilds on
/// turns where the sim recomputed visibility — call it per frame, it self-skips.
/// </summary>
public sealed class FogWorldRenderer
{
    private readonly SimBridge _sim;
    private readonly FogTextureBuilder _builder = new();
    private ShaderMaterial? _mat;
    private Image? _image;
    private ImageTexture? _texture;
    private int _gridSize;
    private int _lastVersion = -1;   // last RangeManager.LosVersion we uploaded; -1 forces a rebuild

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

    /// <summary>Re-upload the fog texture from the current LOS grid, gated on
    /// <see cref="RangeManager.LosVersion"/>: the LOS grid only changes when the sim's
    /// per-turn visibility pass recomputed it, so the BuildBlurred + texture upload runs
    /// only on those turns, not every render frame. Recreates the texture when
    /// RangeManager.SetBounds resized the grid after Attach (the PMP load order does
    /// exactly that), so the fog can never freeze on a stale grid.</summary>
    public void Update()
    {
        if (_mat == null || _image == null || _texture == null) return;
        int n = _sim.Range.Los.VerticesPerSide;
        if (n != _gridSize) EnsureTexture(n);
        if (_sim.Range.LosVersion == _lastVersion) return;
        _lastVersion = _sim.Range.LosVersion;
        byte[] data = _builder.BuildBlurred(_sim.Range.Los, (int)_sim.LocalPlayerId);
        _image.SetData(n, n, false, Image.Format.L8, data);
        _texture.Update(_image);
    }

    private void EnsureTexture(int n)
    {
        _gridSize = n;
        _lastVersion = -1;   // texture recreated → force next Update to repopulate it
        _image = Image.CreateEmpty(n, n, false, Image.Format.L8);
        _texture = ImageTexture.CreateFromImage(_image);
        _mat?.SetShaderParameter("fog_texture", _texture);
    }
}
