using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot;

/// <summary>命中特效对象池。纯视觉——订阅 AttackLandedEvent 触发。
/// 不依赖 CPUParticles3D（gl_compatibility 下类型解析问题）；用简化 MeshInstance3D
/// 做爆发式特效：一个球体从大缩到小并淡出，模拟受击迸溅。
/// 普通命中：灰色扬尘球；击杀：红色血雾球（更大更久）。</summary>
public sealed partial class ImpactEffectPool : Node
{
    private const int PoolSize = 16;
    private const float EffectDuration = 0.4f;
    private const float KillEffectDuration = 0.6f;

    private static readonly StandardMaterial3D DustMat = MakeMat(new Color(0.6f, 0.55f, 0.45f));
    private static readonly StandardMaterial3D BloodMat = MakeMat(new Color(0.7f, 0.08f, 0.05f));

    private readonly List<MeshInstance3D> _pool = new();
    private readonly List<Active> _active = new();

    private struct Active
    {
        public MeshInstance3D Node;
        public float Age;
        public float Duration;
        public float StartScale;
    }

    public override void _Ready()
    {
        var sphere = new SphereMesh { Radius = 0.3f, Height = 0.6f };
        for (int i = 0; i < PoolSize; i++)
        {
            var node = new MeshInstance3D { Mesh = sphere, Visible = false };
            AddChild(node);
            _pool.Add(node);
        }
    }

    /// <summary>在某位置生成命中特效。isKill=true 用血雾（更大更久），否则扬尘。</summary>
    public void Spawn(Vector3 pos, bool isKill)
    {
        MeshInstance3D? node = null;
        foreach (var n in _pool)
        {
            if (!n.Visible) { node = n; break; }
        }
        if (node == null && _active.Count > 0)
        {
            node = _active[0].Node;
            _active.RemoveAt(0);
        }
        if (node == null) return;

        node.MaterialOverride = isKill ? BloodMat : DustMat;
        node.Position = pos;
        node.Scale = Vector3.One * (isKill ? 1.5f : 1.0f);
        node.Visible = true;
        _active.Add(new Active
        {
            Node = node,
            Age = 0,
            Duration = isKill ? KillEffectDuration : EffectDuration,
            StartScale = isKill ? 1.5f : 1.0f,
        });
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var a = _active[i];
            a.Age += dt;
            float t = a.Age / a.Duration;
            if (t >= 1f)
            {
                a.Node.Visible = false;
                _active.RemoveAt(i);
                continue;
            }
            // 迸溅：从小扩大再收缩（ease-out），配合透明度淡出。
            float scale = a.StartScale * (1f + t * 1.5f) * (1f - t * 0.5f);
            a.Node.Scale = Vector3.One * scale;
            // 透明度淡出（StandardMaterial3D 的 Transparency 须设为 Alpha）。
            if (a.Node.MaterialOverride is StandardMaterial3D mat)
            {
                var c = mat.AlbedoColor;
                mat.AlbedoColor = new Color(c.R, c.G, c.B, 1f - t);
            }
            _active[i] = a;
        }
    }

    private static StandardMaterial3D MakeMat(Color color)
    {
        var mat = new StandardMaterial3D
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
        };
        return mat;
    }
}
