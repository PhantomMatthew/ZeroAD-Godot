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
    private Texture2D? _bgTexture;
    private Texture2D? _circleMask;

    private const int MapSize = 200;

    public Minimap(SimBridge sim, Main main)
    {
        _sim = sim;
        _main = main;
        CustomMinimumSize = new Vector2(MapSize, MapSize);
    }

    public override void _Ready()
    {
        _image = Image.CreateEmpty(MapSize, MapSize, false, Image.Format.Rgba8);
        _texture = ImageTexture.CreateFromImage(_image);
        _bgTexture = LoadTex("background_circle_spart.png");
        _circleMask = LoadTex("minimap_circle_modern.png");
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            Vector2 local = mb.Position;
            float worldSize = _sim.Obstructions.GridSize * _sim.Obstructions.CellSize;
            float wx = local.X / MapSize * worldSize;
            float wz = local.Y / MapSize * worldSize;
            float h = TerrainHeightService.Sample(wx, wz);
            _main.SetCameraFocus(new Vector3(wx, h, wz));
        }
    }

    public override void _Process(double delta)
    {
        _image.Fill(new Color(0.12f, 0.15f, 0.08f));

        float worldSize = _sim.Obstructions.GridSize * _sim.Obstructions.CellSize;
        if (worldSize <= 0) { _texture.Update(_image); QueueRedraw(); return; }

        foreach (var kvp in GetAllEntityNodes())
        {
            var node = kvp.Value;
            int px = (int)(node.Position.X / worldSize * MapSize);
            int pz = (int)(node.Position.Z / worldSize * MapSize);

            if (px < 0 || px >= MapSize || pz < 0 || pz >= MapSize) continue;

            // Read identity/health/owner via the GuiInterface facade instead of inline
            // QueryInterface calls, so the query surface stays consolidated.
            var st = _sim.Gui.GetEntityState(kvp.Key);
            bool isBuilding = st?.IsBuilding ?? false;
            bool isUnit = st?.IsUnit ?? false;
            int ownerPlayerId = st?.OwnerPlayerId ?? -1;
            float healthFraction = st?.HealthFraction ?? 1f;
            string name = st?.Name ?? "";

            Color color;
            if (isBuilding || isUnit)
            {
                color = ownerPlayerId == 1
                    ? new Color(0.08f, 0.22f, 0.58f)
                    : new Color(0.72f, 0.06f, 0.06f);
            }
            else if (name.Contains("Tree"))
                color = new Color(0.1f, 0.45f, 0.1f);
            else
                color = new Color(0.6f, 0.6f, 0.4f);

            if (healthFraction < 0.5f)
                color = new Color(0.9f, 0.3f, 0.1f);

            DrawDot(px, pz, color, isBuilding ? 3 : 2);
        }

        _texture.Update(_image);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_bgTexture != null)
            DrawTextureRect(_bgTexture, new Rect2(Vector2.Zero, MapSize, MapSize), false);

        DrawTextureRect(_texture, new Rect2(Vector2.Zero, MapSize, MapSize), false);

        float worldSize = _sim.Obstructions.GridSize * _sim.Obstructions.CellSize;
        if (worldSize <= 0) return;

        DrawViewCone(worldSize);

        var selected = _main.SelectedEntities;
        foreach (var eid in selected)
        {
            foreach (var kvp in GetAllEntityNodes())
            {
                if (kvp.Key != eid) continue;
                int px = (int)(kvp.Value.Position.X / worldSize * MapSize);
                int pz = (int)(kvp.Value.Position.Z / worldSize * MapSize);
                DrawCircle(new Vector2(px, pz), 4, new Color(0.2f, 1f, 0.2f));
            }
        }

        DrawPlayerMarker(worldSize);

        if (_circleMask != null)
            DrawTextureRect(_circleMask, new Rect2(Vector2.Zero, MapSize, MapSize), false);
    }

    private void DrawViewCone(float worldSize)
    {
        var cam = _main.GetCameraFocus();
        if (cam == null) return;
        int cx = (int)(cam.Value.X / worldSize * MapSize);
        int cz = (int)(cam.Value.Z / worldSize * MapSize);
        float yaw = _main.GetCameraYaw();

        Vector2 center = new(cx, cz);
        float coneLen = 40f;
        float halfAngle = 0.5f;

        Vector2 left = center + new Vector2(Mathf.Sin(yaw - halfAngle), Mathf.Cos(yaw - halfAngle)) * coneLen;
        Vector2 right = center + new Vector2(Mathf.Sin(yaw + halfAngle), Mathf.Cos(yaw + halfAngle)) * coneLen;

        DrawLine(center, left, new Color(1f, 0.2f, 0.2f, 0.6f), 2f);
        DrawLine(center, right, new Color(1f, 0.2f, 0.2f, 0.6f), 2f);
        DrawLine(left, right, new Color(1f, 0.2f, 0.2f, 0.6f), 2f);
    }

    private void DrawPlayerMarker(float worldSize)
    {
        foreach (var kvp in GetAllEntityNodes())
        {
            var identity = _sim.Sim.QueryInterface<IdentityComponent>(kvp.Key);
            var owner = _sim.Sim.QueryInterface<OwnershipComponent>(kvp.Key);
            if (identity == null || !identity.IsBuilding || owner == null || owner.PlayerId != 1)
                continue;
            if (!identity.TemplateName.Contains("civil_centre") && !identity.TemplateName.Contains("civic_centre"))
                continue;
            int px = (int)(kvp.Value.Position.X / worldSize * MapSize);
            int pz = (int)(kvp.Value.Position.Z / worldSize * MapSize);
            DrawRect(new Rect2(px - 4, pz - 4, 8, 8), new Color(0.08f, 0.22f, 0.58f, 0.8f), false, 1.5f);
            break;
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

    private static Texture2D? LoadTex(string file)
    {
        string path = ProjectSettings.GlobalizePath($"res://assets/ui/{file}");
        if (!System.IO.File.Exists(path)) return null;
        var img = Image.LoadFromFile(path);
        return img != null ? ImageTexture.CreateFromImage(img) : null;
    }
}
