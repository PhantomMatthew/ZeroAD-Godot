using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>海军管理器（原版 petra/navalManager.js，924 行）。
/// 管理船只（渔船/战船/运输船）、海上贸易保护、运输调度。
/// 骨架版——update 结构 + checkEvents 移植，运输/海战标 TODO。</summary>
public sealed class NavalManager
{
    private readonly PetraConfig _config;
    public readonly List<TransportPlan> TransportPlans = new();
    public readonly Dictionary<uint, AIEntity> Ships = new();  // ship id → entity

    public NavalManager(PetraConfig config) => _config = config;

    /// <summary>事件检查（原版 checkEvents）。
    /// 处理新船建造/摧毁、运输请求。</summary>
    public void CheckEvents(GameState gameState, QueueManager queues, AIEventBuffer events)
    {
        foreach (var ev in events.Events)
        {
            if (ev.Type == AIEventType.ConstructionFinished || ev.Type == AIEventType.Create)
            {
                var ent = gameState.GetEntityById(ev.Entity);
                if (ent != null && ent.HasClass("Ship"))
                    Ships[ent.Id] = ent;
            }
            if (ev.Type == AIEventType.Destroy)
                Ships.Remove(ev.Entity);
        }
    }

    /// <summary>主更新（原版 navalManager.update）。
    /// 骨架版：更新船只集合 + 运输计划。</summary>
    public void Update(GameState gameState, QueueManager queues, AIEventBuffer events)
    {
        // 更新运输计划
        foreach (var plan in TransportPlans.ToList())
        {
            plan.Update(gameState);
            if (plan.State == TransportPlan.TransportState.Completed
                || plan.State == TransportPlan.TransportState.Failed)
                TransportPlans.Remove(plan);
        }
    }

    /// <summary>需要运输时创建 TransportPlan。</summary>
    public TransportPlan? CreateTransport(GameState gameState, uint unit, FixedVector2D destination)
    {
        // TODO: 完整版需 Accessibility.getTrajectTo 确认需要跨海
        var plan = new TransportPlan(unit, destination);
        TransportPlans.Add(plan);
        return plan;
    }
}

/// <summary>运输计划（原版 petra/transportPlan.js，753 行）。
/// 跨海运兵：登船 → 航行 → 下船。
/// 骨架版——状态机结构。</summary>
public sealed class TransportPlan
{
    public readonly uint Unit;
    public readonly FixedVector2D Destination;

    public enum TransportState { Boarding, Sailing, Unboarding, Completed, Failed }
    public TransportState State { get; private set; }

    public TransportPlan(uint unit, FixedVector2D destination)
    { Unit = unit; Destination = destination; State = TransportState.Boarding; }

    public void Update(GameState gameState)
    {
        // TODO: 完整状态机（Boarding→Sailing→Unboarding→Completed）
        // 简化版：无船可用时直接 Failed
        State = TransportState.Failed;
    }
}
