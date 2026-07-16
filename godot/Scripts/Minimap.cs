using Godot;
using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

public sealed partial class Minimap : Control
{
    private readonly SimBridge _sim;
    private readonly Main _main;
    private ImageTexture _texture = null!;
    private Image _image = null!;

    private const int MapSize = 128;

    public Minimap(SimBridge sim, Main main)
    {
        _sim = sim;
        _main = main;
    }

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(MapSize, MapSize);
        _image = Image.CreateEmpty(MapSize, MapSize, false, Image.Format.Rgba8);
        _texture = ImageTexture.CreateFromImage(_image);
    }

    public override void _Process(double delta)
    {
        _image.Fill(new Color(0.15f, 0.2f, 0.12f));

        float worldSize = _sim.Obstructions.GridSize * _sim.Obstructions.CellSize;

        foreach (var kvp in GetAllEntityNodes())
        {
            var node = kvp.Value;
            float nx = node.Position.X / worldSize;
            float nz = node.Position.Z / worldSize;

            int px = (int)(nx * MapSize);
            int pz = (int)(nz * MapSize);

            if (px < 0 || px >= MapSize || pz < 0 || pz >= MapSize) continue;

            var identity = _sim.Sim.QueryInterface<IdentityComponent>(kvp.Key);
            var health = _sim.Sim.QueryInterface<HealthComponent>(kvp.Key);

            Color color;
            if (identity != null && !identity.IsUnit && identity.Name == "Tree")
                color = new Color(0.1f, 0.6f, 0.1f);
            else if (identity != null && identity.IsBuilding)
                color = new Color(0.6f, 0.5f, 0.4f);
            else
                color = new Color(0.8f, 0.7f, 0.3f);

            if (health != null && health.HealthFraction < 0.5f)
                color = new Color(0.9f, 0.3f, 0.1f);

            DrawDot(px, pz, color, identity?.IsBuilding == true ? 3 : 2);
        }

        _texture.Update(_image);
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawTextureRect(_texture, new Rect2(Vector2.Zero, MapSize, MapSize), false);
        DrawRect(new Rect2(Vector2.Zero, MapSize, MapSize), new Color(1, 1, 1, 0.3f), false, 1);

        var selected = _main.SelectedEntities;
        foreach (var eid in selected)
        {
            foreach (var kvp in GetAllEntityNodes())
            {
                if (kvp.Key != eid) continue;
                var node = kvp.Value;
                float worldSize = _sim.Obstructions.GridSize * _sim.Obstructions.CellSize;
                int px = (int)(node.Position.X / worldSize * MapSize);
                int pz = (int)(node.Position.Z / worldSize * MapSize);
                DrawCircle(new Vector2(px, pz), 4, new Color(0.2f, 1f, 0.2f));
            }
        }
    }

    private void DrawDot(int px, int pz, Color color, int size)
    {
        for (int dz = -size / 2; dz <= size / 2; dz++)
            for (int dx = -size / 2; dx <= size / 2; dx++)
            {
                int x = px + dx, z = pz + dz;
                if (x >= 0 && x < MapSize && z >= 0 && z < MapSize)
                    _image.SetPixel(x, z, color);
            }
    }

    private IReadOnlyDictionary<EntityId, Node3D> GetAllEntityNodes() => _sim.EntityNodes;
}
