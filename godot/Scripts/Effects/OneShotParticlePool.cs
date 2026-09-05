using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot;

/// <summary>一次性粒子池(纯视觉,不进 sim):按 art/particles 定义名生成
/// GPUParticles3D(OneShot),生命周期结束回池复用。触发点:
/// 建筑摧毁(destruction_dust_*/destruction_smoke_*,对应原版 structures/destruction_
/// small/med/large 变体的粒子 props)、落水命中(water_splash——上游溅花只挂在
/// 瀑布 actor 上,命中落水是我们增补的表现层触发点,记录在案)。
/// 常驻循环粒子(建造扬尘 construction_dust)不走本池——直接挂在节点上开关 Emitting。</summary>
public sealed partial class OneShotParticlePool : Node
{
    private readonly Dictionary<string, Queue<GpuParticles3D>> _idle = new();
    private readonly List<Active> _active = new();

    private sealed class Active
    {
        public required GpuParticles3D Node;
        public required string DefName;
        public float Age;
        public float Ttl;
    }

    /// <summary>在 pos 放一发名为 defName 的粒子(art/particles/{defName}.xml)。
    /// 定义缺失(junction 未接)时静默跳过。</summary>
    public void Spawn(string defName, Vector3 pos, int amount = 48)
    {
        var def = EnvironmentParticles.LoadDef(defName);
        if (def == null) return;

        GpuParticles3D? node = null;
        if (_idle.TryGetValue(defName, out var q) && q.Count > 0)
            node = q.Dequeue();
        if (node == null)
        {
            node = EnvironmentParticles.Build(def, amount);
            if (node == null) return;
            node.OneShot = true;
            node.Emitting = false;
            node.Preprocess = 0;   // 一次性特效不预跑(Build 为常驻粒子设了 Preprocess)
            node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            AddChild(node);
        }

        node.Position = pos;
        node.Restart();
        _active.Add(new Active
        {
            Node = node, DefName = defName, Age = 0f, Ttl = def.LifetimeMax + 0.3f,
        });
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var a = _active[i];
            a.Age += dt;
            if (a.Age < a.Ttl) continue;
            a.Node.Emitting = false;
            if (!_idle.TryGetValue(a.DefName, out var q))
                _idle[a.DefName] = q = new Queue<GpuParticles3D>();
            q.Enqueue(a.Node);
            _active.RemoveAt(i);
        }
    }
}
