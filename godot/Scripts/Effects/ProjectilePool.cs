using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot;

/// <summary>飞行投射物对象池（箭矢）。纯视觉——伤害已由 DelayedDamage 瞬间结算，
/// 投射物只是装饰（匹配原版 CCmpProjectileManager：just graphical effects）。
///
/// 每次 Spawn(from, to) 取一个空闲 MeshInstance3D，沿抛物线从 from 飞到 to
/// （FlightDuration 秒），到达后隐藏回收到池。预创建 PoolSize 个节点避免运行时卡顿。</summary>
public sealed partial class ProjectilePool : Node
{
    private const int PoolSize = 20;
    private const float FlightDuration = 0.3f;
    private const float ArcHeight = 3.0f;   // 抛物线峰值高度

    private readonly List<MeshInstance3D> _pool = new();
    private readonly List<Active> _active = new();

    private struct Active
    {
        public MeshInstance3D Node;
        public Vector3 From;
        public Vector3 To;
        public float Age;
    }

    public override void _Ready()
    {
        // 预创建池：细长圆柱体模拟箭矢（不依赖未转换的美术资产）。
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.4f, 0.3f, 0.15f),  // 木褐色
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        var mesh = new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.04f, Height = 0.5f };
        mesh.Material = mat;

        for (int i = 0; i < PoolSize; i++)
        {
            var node = new MeshInstance3D { Mesh = mesh, Visible = false };
            AddChild(node);
            _pool.Add(node);
        }
    }

    /// <summary>从 from 发射投射物飞向 to（世界坐标）。仅在 IsRanged 时由 SimBridge 调用。</summary>
    public void Spawn(Vector3 from, Vector3 to)
    {
        MeshInstance3D? node = null;
        // 找空闲节点（Visible=false 的）。
        foreach (var n in _pool)
        {
            if (!n.Visible) { node = n; break; }
        }
        // 池耗尽：复用最老的（避免运行时分配）。
        if (node == null)
        {
            node = _active[0].Node;
            _active.RemoveAt(0);
        }
        node.Visible = true;
        node.Position = from;
        node.LookAt(new Vector3(to.X, from.Y, to.Z), Vector3.Up);  // 朝向目标（水平面）
        _active.Add(new Active { Node = node, From = from, To = to, Age = 0 });
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var a = _active[i];
            a.Age += dt;
            float t = a.Age / FlightDuration;
            if (t >= 1f)
            {
                a.Node.Visible = false;  // 回收
                _active.RemoveAt(i);
                continue;
            }
            // 抛物线：水平 Lerp(from,to,t) + 垂直 4t(1-t)×ArcHeight 的弧高。
            Vector3 pos = a.From.Lerp(a.To, t);
            pos.Y += ArcHeight * 4f * t * (1f - t);
            a.Node.Position = pos;
            // 朝向运动方向（下一帧的位置）。
            Vector3 next = a.From.Lerp(a.To, t + 0.05f);
            next.Y += ArcHeight * 4f * (t + 0.05f) * (1f - t - 0.05f);
            a.Node.LookAt(next);
            _active[i] = a;
        }
    }
}
