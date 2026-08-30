using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot;

/// <summary>战场贴花(原版 actors/props/units/blood_*.xml 的 decal 语义:
/// 击杀/重击落地血斑,贴地不动、缓慢消融)。贴花 = 地面投射的四边形
/// (MeshInstance3D + StandardMaterial3D,billboard 关、贴地 Y 偏移),
/// 按 decay 秒淡出后回收(原版 CCmpDecay 的消融节奏)。
///
/// 与 ImpactEffectPool(命中瞬时血雾球)互补:命中瞬间的迸溅在池里,
/// 残留血斑在本系统(击杀时地面留 decal)。</summary>
public sealed partial class BattleDecals : Node
{
    private const int PoolSize = 24;
    private const float DecaySeconds = 45f;   // 原版 CCmpDecay 的消融节奏近似
    private static readonly string[] BloodTextures =
    {
        "blood_01.dds", "blood_02.dds", "blood_03.dds", "blood_05.dds",
    };
    /// <summary>炮击弹坑/建筑毁坏贴花纹理(原版 eyecandy/impact_decal 与
    /// decal_destruct 的 decals;攻城命中/建筑被毁时落,比血斑大、消融更久)。</summary>
    private static readonly string[] ImpactTextures =
    {
        "decal_campfire.png", "decal_destruct_large.png", "decal_destruct_llong.png",
    };

    private readonly List<MeshInstance3D> _pool = new();
    private readonly List<(MeshInstance3D node, float age)> _active = new();
    private static readonly List<Texture2D> _textures = new();
    private static readonly List<Texture2D> _impactTextures = new();
    private static bool _texturesLoaded;
    private int _nextTexture;

    public override void _Ready()
    {
        LoadTextures();
        for (int i = 0; i < PoolSize; i++)
        {
            var node = new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = new Vector2(2.5f, 2.5f) },
                Visible = false,
            };
            AddChild(node);
            _pool.Add(node);
        }
    }

    private static void LoadTextures()
    {
        if (_texturesLoaded) return;
        _texturesLoaded = true;
        string projRoot = ProjectSettings.GlobalizePath("res://");
        foreach (var up in new[] { "..", "../.." })
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(projRoot, up,
                "binaries", "data", "mods", "public", "art", "textures", "skins", "props"));
            if (!System.IO.Directory.Exists(dir)) continue;
            foreach (var name in BloodTextures)
            {
                string path = System.IO.Path.Combine(dir, name);
                if (!System.IO.File.Exists(path)) continue;
                var img = Image.LoadFromFile(path);
                if (img != null) _textures.Add(ImageTexture.CreateFromImage(img));
            }
            foreach (var name in ImpactTextures)
            {
                string path = System.IO.Path.Combine(dir, name);
                if (!System.IO.File.Exists(path)) continue;
                var img = Image.LoadFromFile(path);
                if (img != null) _impactTextures.Add(ImageTexture.CreateFromImage(img));
            }
            return;
        }
    }

    /// <summary>击杀/重击落地血斑(原版 blood_*.xml 的 decal 触发语义;
    /// 随机纹理轮转 + 随机朝向,贴地消融)。</summary>
    public void Spawn(Vector3 pos) => SpawnDecal(pos, _textures, 45f);

    /// <summary>炮击弹坑/建筑毁坏贴花(原版 eyecandy/impact_decal 与
    /// decal_destruct 的 decal 语义:攻城命中/建筑被毁时落,比血斑大、
    /// 消融更久 90s)。</summary>
    public void SpawnImpact(Vector3 pos) => SpawnDecal(pos, _impactTextures, 90f);

    private void SpawnDecal(Vector3 pos, List<Texture2D> textures, float decaySeconds)
    {
        MeshInstance3D? node = null;
        foreach (var n in _pool)
        {
            if (!n.Visible) { node = n; break; }
        }
        if (node == null && _active.Count > 0)
        {
            node = _active[0].node;
            _active.RemoveAt(0);
        }
        if (node == null) return;

        if (textures.Count > 0)
        {
            var mat = new StandardMaterial3D
            {
                AlbedoTexture = textures[_nextTexture % textures.Count],
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(1, 1, 1, 0.9f),
            };
            _nextTexture++;
            if (node.Mesh is QuadMesh quad)
                quad.Material = mat;
        }
        node.Position = pos + Vector3.Up * 0.02f;
        node.Rotation = new Vector3(-Mathf.Pi / 2, (float)(_nextTexture * 1.618f % (Mathf.Pi * 2)), 0);
        node.Scale = Vector3.One;
        node.Visible = true;
        _active.Add((node, 0f));
    }

    public override void _Process(double delta)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var (node, age) = _active[i];
            age += (float)delta;
            // 消融(原版 CCmpDecay:线性淡出 + 缩小,45s 后回收)。
            float t = age / DecaySeconds;
            if (t >= 1f)
            {
                node.Visible = false;
                _active.RemoveAt(i);
                continue;
            }
            node.Scale = Vector3.One * (1f - t * 0.4f);
            if (node.Mesh is QuadMesh { Material: StandardMaterial3D mat })
                mat.AlbedoColor = new Color(1, 1, 1, 0.9f * (1f - t));
            _active[i] = (node, age);
        }
    }
}
