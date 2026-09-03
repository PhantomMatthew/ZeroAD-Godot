using Godot;
using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

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

    // C++ 小地图约定(CMiniMap::WorldSpaceToMiniMapSpace):x→右,z→上(z 大=北=屏顶)。
    // 本类所有"世界坐标→像素"都走这两个助手,勿内联展开。
    private static int Px(float wx, float worldSize) => (int)(wx / worldSize * MapSize);
    private static int Pz(float wz, float worldSize) => MapSize - 1 - (int)(wz / worldSize * MapSize);

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
            float worldSize = _sim.Terrain.MapSize * _sim.Terrain.TileSize;
            float wx = local.X / MapSize * worldSize;
            // 对齐 C++ 小地图:z 大=北=屏顶(CMiniMap::GetMouseWorldCoordinates 自底向上量 py)。
            float wz = (MapSize - local.Y) / MapSize * worldSize;
            float h = TerrainHeightService.Sample(wx, wz);
            _main.SetCameraFocus(new Vector3(wx, h, wz));
        }
    }

    public override void _Process(double delta)
    {
        _image.Fill(new Color(0.12f, 0.15f, 0.08f));

        float worldSize = _sim.Terrain.MapSize * _sim.Terrain.TileSize;
        if (worldSize <= 0) { _texture.Update(_image); QueueRedraw(); return; }

        BlendTerritory(worldSize);

        int lp = (int)_sim.LocalPlayerId;
        // 批量快照(回合缓存):每帧 × 每实体的 GetEntityState 逐调用分配消除。
        foreach (var dot in _sim.Gui.GetMinimapEntities())
        {
            var eid = new EntityId(dot.Id);
            // Fog-of-war: entities hidden from the local player don't appear at all;
            // fogged stand-ins (mirages/structures) draw dimmed.
            var vis = _sim.Range.GetLosVisibility(eid, lp);
            if (vis == LosVisibility.Hidden) continue;

            int px = Px(dot.X, worldSize);
            int pz = Pz(dot.Z, worldSize);

            if (px < 0 || px >= MapSize || pz < 0 || pz >= MapSize) continue;

            bool isBuilding = dot.IsBuilding;
            bool isUnit = dot.IsUnit;
            int ownerPlayerId = dot.OwnerPlayerId;
            float healthFraction = dot.HealthFraction;
            string name = dot.Name;

            Color color;
            if (isBuilding || isUnit)
            {
                color = ownerPlayerId == lp
                    ? new Color(0.08f, 0.22f, 0.58f)
                    : new Color(0.72f, 0.06f, 0.06f);
            }
            else if (name.Contains("Tree"))
                color = new Color(0.1f, 0.45f, 0.1f);
            else
                color = new Color(0.6f, 0.6f, 0.4f);

            if (healthFraction < 0.5f)
                color = new Color(0.9f, 0.3f, 0.1f);

            if (vis == LosVisibility.Fogged)
                color = color.Darkened(0.5f);

            DrawDot(px, pz, color, isBuilding ? 3 : 2);
        }

        BlendFog(lp);

        _texture.Update(_image);
        QueueRedraw();
    }

    private readonly FogTextureBuilder _fogBuilder = new();

    /// <summary>Darken the whole map by the blurred fog texture: unexplored → black,
    /// explored → half bright, visible → full. Soft edges from the 7-tap binomial.
    /// Operates on the raw RGBA buffer — two marshalled copies per frame instead of
    /// 80k per-pixel interop calls.</summary>
    private void BlendFog(int player)
    {
        byte[] fog = _fogBuilder.BuildBlurred(_sim.Range.Los, player);
        int fn = _sim.Range.Los.VerticesPerSide;
        if (fn <= 1) return;
        byte[] rgba = _image.GetData();
        for (int pz = 0; pz < MapSize; pz++)
        {
            int fj = Mathf.Min((MapSize - 1 - pz) * fn / MapSize, fn - 1);
            for (int px = 0; px < MapSize; px++)
            {
                int bright = fog[fj * fn + Mathf.Min(px * fn / MapSize, fn - 1)];
                if (bright >= 252) continue;
                int o = (pz * MapSize + px) * 4;
                rgba[o] = (byte)(rgba[o] * bright / 255);
                rgba[o + 1] = (byte)(rgba[o + 1] * bright / 255);
                rgba[o + 2] = (byte)(rgba[o + 2] * bright / 255);
            }
        }
        _image.SetData(MapSize, MapSize, false, Image.Format.Rgba8, rgba);
    }

    /// <summary>Territory tint under the entity dots(对齐原版小地图领土着色):owner 色
    /// 半透明填充,未连通(blinking)区域随时间脉冲。与 BlendFog 同套路:整图 raw buffer
    /// 一次 GetData/SetData,避免 40k 次 SetPixel。闪烁相位用墙钟,纯表现不进模拟。</summary>
    private void BlendTerritory(float worldSize)
    {
        var tm = _sim.Territory;
        if (tm == null || tm.GridWidth <= 0) return;
        float blink = 0.55f + 0.45f * Mathf.Sin(Time.GetTicksMsec() / 1000f * 4f);
        byte[] rgba = _image.GetData();
        for (int pz = 0; pz < MapSize; pz++)
        {
            float wz = ((MapSize - 1 - pz) + 0.5f) / MapSize * worldSize;
            var fz = Fixed.FromFloat(wz);
            for (int px = 0; px < MapSize; px++)
            {
                var fx = Fixed.FromFloat((px + 0.5f) / MapSize * worldSize);
                int owner = tm.GetOwner(fx, fz);
                if (owner <= 0) continue;
                Color c = SimBridge.GetPlayerColor(owner);
                float a = 0.35f * (tm.IsTerritoryBlinking(fx, fz) ? blink : 1f);
                int cr = (int)(c.R * 255), cg = (int)(c.G * 255), cb = (int)(c.B * 255);
                int o = (pz * MapSize + px) * 4;
                rgba[o] = (byte)(rgba[o] + (cr - rgba[o]) * a);
                rgba[o + 1] = (byte)(rgba[o + 1] + (cg - rgba[o + 1]) * a);
                rgba[o + 2] = (byte)(rgba[o + 2] + (cb - rgba[o + 2]) * a);
            }
        }
        _image.SetData(MapSize, MapSize, false, Image.Format.Rgba8, rgba);
    }

    public override void _Draw()
    {
        if (_bgTexture != null)
            DrawTextureRect(_bgTexture, new Rect2(Vector2.Zero, MapSize, MapSize), false);

        DrawTextureRect(_texture, new Rect2(Vector2.Zero, MapSize, MapSize), false);

        float worldSize = _sim.Terrain.MapSize * _sim.Terrain.TileSize;
        if (worldSize <= 0) return;

        DrawViewCone(worldSize);

        var selected = _main.SelectedEntities;
        foreach (var eid in selected)
        {
            foreach (var kvp in GetAllEntityNodes())
            {
                if (kvp.Key != eid) continue;
                int px = Px(kvp.Value.Position.X, worldSize);
                int pz = Pz(kvp.Value.Position.Z, worldSize);
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
        int cx = Px(cam.Value.X, worldSize);
        int cz = Pz(cam.Value.Z, worldSize);
        float yaw = _main.GetCameraYaw();

        Vector2 center = new(cx, cz);
        float coneLen = 40f;
        float halfAngle = 0.5f;

        // sim 视线方向 = (−sin yaw, +cos yaw)(见 RTSCamera 镜像注释);像素 y 向下=z 减小,
        // 故像素向量 = (−sin, −cos)。
        Vector2 left = center + new Vector2(-Mathf.Sin(yaw - halfAngle), -Mathf.Cos(yaw - halfAngle)) * coneLen;
        Vector2 right = center + new Vector2(-Mathf.Sin(yaw + halfAngle), -Mathf.Cos(yaw + halfAngle)) * coneLen;

        DrawLine(center, left, new Color(1f, 0.2f, 0.2f, 0.6f), 2f);
        DrawLine(center, right, new Color(1f, 0.2f, 0.2f, 0.6f), 2f);
        DrawLine(left, right, new Color(1f, 0.2f, 0.2f, 0.6f), 2f);
    }

    private void DrawPlayerMarker(float worldSize)
    {
        // 桥:GetCivilCentrePosition(单趟桥侧扫描,替代每帧内联双查)。
        var cc = _sim.Gui.GetCivilCentrePosition(1);
        if (cc == null) return;
        int px = Px(cc.Value.X, worldSize);
        int pz = Pz(cc.Value.Z, worldSize);
        DrawRect(new Rect2(px - 4, pz - 4, 8, 8), new Color(0.08f, 0.22f, 0.58f, 0.8f), false, 1.5f);
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
