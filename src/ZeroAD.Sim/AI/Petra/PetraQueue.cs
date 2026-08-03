using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>生产队列（原版 petra/queue.js，166 行）。
/// 持有按优先级排序的 QueuePlan 列表。QueueManager 管理 N 个命名 Queue。</summary>
public sealed class PetraQueue
{
    public readonly List<QueuePlan> Plans = new();
    public bool Paused;

    public void AddPlan(QueuePlan? plan)
    {
        if (plan == null) return;
        // 合并同类型 unit 计划（maxMerge 内）
        if (plan.Category == "unit")
        {
            foreach (var existing in Plans)
            {
                if (existing.Type == plan.Type && existing.Number + plan.Number <= (existing as TrainingPlan)?.MaxMerge)
                {
                    existing.AddItem(plan.Number);
                    return;
                }
            }
        }
        else if (plan.Category == "technology")
        {
            // 同科技不重复入队
            if (Plans.Any(p => p.Type == plan.Type)) return;
        }
        Plans.Add(plan);
    }

    /// <summary>检查队列：移除无效计划。</summary>
    public void Check(GameState gameState)
    {
        while (Plans.Count > 0)
        {
            if (!Plans[0].IsInvalid(gameState)) return;
            Plans.RemoveAt(0);
        }
    }

    public QueuePlan? GetNext() => Plans.Count > 0 ? Plans[0] : null;

    public bool StartNext(GameState gameState)
    {
        if (Plans.Count > 0)
        {
            var plan = Plans[0];
            Plans.RemoveAt(0);
            plan.Start(gameState);
            return true;
        }
        return false;
    }

    /// <summary>队列全部计划的总成本。</summary>
    public ResourcesManager QueueCost()
    {
        var total = new ResourcesManager();
        foreach (var plan in Plans)
            total.Add(plan.GetCost());
        return total;
    }

    public int Length => Plans.Count;
    public bool HasQueuedUnits => Plans.Count > 0;

    public int CountQueuedUnits() => Plans.Sum(p => p.Number);

    public int CountQueuedUnitsWithClass(string cls)
        => Plans.Where(p => p.Category == "unit").Sum(p => p.Number);
}
