using System.Collections.Generic;
using Godot;
using ZeroAD.Sim;

namespace ZeroAD.Godot;

/// <summary>
/// 表现层位置插值器:消除 10Hz sim tick 造成的单位瞬移。内核 <c>PositionComponent</c>
/// 以固定 10Hz 更新(锁步确定性,绝不改动);本类在每个 sim tick 记录每单位的
/// prev(上一 tick 位置)/ curr(本 tick 位置),每渲染帧按 alpha =
/// <c>_simAccumulator / SimTickRate</c> 在两者间线性插值写入视觉节点 Position。
///
/// 这是标准 fixed-timestep 渲染模式:渲染帧落在两次 tick 之间,用累积器余数作
/// 插值因子,单位视觉上从 prev 平滑滑向 curr,而非每 100ms 跳一格。
///
/// 边界:新单位首次记录直接 snap(prev=curr),不从原点滑入;位移超过
/// <see cref="TeleportThresholdSq"/> 视为传送/寻路重置,snap 不插值;静止或锁步
/// stall 时 prev==curr,lerp 自然无漂移。对齐原版 C++ <c>CCmpVisualActor</c> 插值。
/// </summary>
public sealed class VisualInterpolator
{
    private struct Entry
    {
        public Node3D Node;
        public Vector3 Prev;
        public Vector3 Curr;
        /// <summary>本 tick 是否移动(Curr != Prev)。静止单位跳过每帧的 Position 写入——
        /// 写入即使值不变也会让引擎标脏重算全局变换/渲染脏区,几百个静止村民 ×60fps
        /// 是纯浪费(实测 sim-paused 与基线的帧时差里 ~5ms 来自这套写入的传播)。</summary>
        public bool Moving;
    }

    private readonly Dictionary<EntityId, Entry> _entries = new();
    private float _alpha;

    /// <summary>位移平方距离超过此值视为传送/路径重置,snap 而非插值(避免单位飞跨地图)。
    /// 5 单位(平方阈值 25):正常单 tick 移动远小于此,寻路重置/MoveToPoint 大跳远大于此。</summary>
    private const float TeleportThresholdSq = 25f;

    /// <summary>每 sim tick 后调用(在 <c>SyncVisuals</c> 内):记录 prev=上一 tick 的
    /// curr、curr=<paramref name="simPos"/>。首次见或大跳(>5 格)时 snap
    /// (prev=curr=simPos)并立即写入 <c>node.Position</c>。</summary>
    public void RecordTick(EntityId entity, Node3D node, Vector3 simPos)
    {
        if (!_entries.TryGetValue(entity, out var entry))
        {
            // First sighting: snap so newly spawned units don't slide in from the origin.
            _entries[entity] = new Entry { Node = node, Prev = simPos, Curr = simPos };
            node.Position = simPos;
            return;
        }

        entry.Node = node; // node ref may have been swapped (e.g. RebuildAllVisuals)
        float jumpSq = (simPos - entry.Curr).LengthSquared();
        if (jumpSq > TeleportThresholdSq)
        {
            // Teleport / path reset: snap, don't interpolate across the map.
            entry.Prev = simPos;
            entry.Curr = simPos;
            entry.Moving = false;
            node.Position = simPos;
        }
        else
        {
            entry.Prev = entry.Curr;
            entry.Curr = simPos;
            entry.Moving = entry.Curr != entry.Prev;
            // 停下的一拍:补写一次落定位置(此前插值可能停在中途),之后静止期不再写。
            if (!entry.Moving)
                node.Position = entry.Curr;
        }
        _entries[entity] = entry;
    }

    /// <summary>渲染插值因子。调用方(SimBridge._Process)每帧设为
    /// <c>_simAccumulator / SimTickRate</c>,内部 clamp 到 [0,1]。</summary>
    public void SetAlpha(float alpha) => _alpha = Mathf.Clamp(alpha, 0f, 1f);

    /// <summary>每渲染帧调用:将每单位视觉位置按 alpha 在 prev→curr 间插值写入
    /// <c>node.Position</c>。alpha=0 显示上一 tick 位置,alpha→1 趋近本 tick 位置。</summary>
    public void ApplyRenderPositions()
    {
        float a = _alpha;
        // Value iteration is fine: Entry.Node is a reference, mutating its Position
        // property does not require re-storing the dictionary entry.
        // 静止实体(Moving=false)跳过写入——见 Entry.Moving 注释。
        foreach (var entry in _entries.Values)
            if (entry.Moving)
                entry.Node.Position = entry.Prev.Lerp(entry.Curr, a);
    }

    /// <summary>单位被销毁时移除其插值条目,避免对已 QueueFree 的节点写 Position。</summary>
    public void Remove(EntityId entity) => _entries.Remove(entity);

    /// <summary>清空所有条目(存档加载重建视觉前调用)。</summary>
    public void Clear() => _entries.Clear();
}
