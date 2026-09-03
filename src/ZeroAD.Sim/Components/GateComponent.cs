using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components
{
    /// <summary>城门组件(原版 Gate.js 的最小移植):锁状态 + 阻挡联动。
    /// 原版:门解锁时友军通行(阻挡失活),上锁时全阻挡(激活)。
    /// 序列化 Locked——读档恢复锁态;阻挡失活状态由 Deserialize 联动恢复。</summary>
    public sealed class GateComponent : ComponentBase
    {
        /// <summary>上锁 = 阻挡通行(true);原版默认未锁(false,可通行)。</summary>
        public bool Locked;

        /// <summary>切换锁态(原版 gate 面板 lock/unlock 按钮):联动 Obstruction 活性
        /// 并重建寻路网格(静态阻挡形状变化须重烘焙)。</summary>
        public void SetLocked(ComponentManager cm, bool locked)
        {
            if (Locked == locked) return;
            Locked = locked;
            var obstruction = cm.QueryInterface<ObstructionComponent>(Entity);
            // SetActive → ObstructionManager 形状摘除/重挂 → 自动打脏;
            // 回合末 PathfinderComponent.UpdateGrid 增量重烘焙(不再 mid-turn 全量重建)。
            obstruction?.SetActive(locked);
        }

        public override void Serialize(ISerializer s) => s.Bool("locked", Locked);

        public override void Deserialize(IDeserializer d)
        {
            Locked = d.Bool("locked");
            // 阻挡活性随锁态恢复(读档路径不经 SetLocked)。
            if (Locked)
                SimSystem.GetComponent<ObstructionComponent>(Entity)?.SetActive(true);
        }
    }
}
