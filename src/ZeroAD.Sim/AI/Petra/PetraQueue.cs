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
        // 合并同类型 unit 计划（maxMerge 内);attackPlan 槽位标记("special")不同不并——
        // 合并会把别的编组槽的在排数算错(原版同款按 metadata 区分)。
        if (plan.Category == "unit")
        {
            foreach (var existing in Plans)
            {
                if (existing.Type != plan.Type) continue;
                if (existing.Metadata.GetValueOrDefault("special")?.ToString()
                    != plan.Metadata.GetValueOrDefault("special")?.ToString()) continue;
                if (existing.Metadata.GetValueOrDefault("plan")?.ToString()
                    != plan.Metadata.GetValueOrDefault("plan")?.ToString()) continue;
                if (existing.Number + plan.Number <= (existing as TrainingPlan)?.MaxMerge)
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

    /// <summary>序列化(原版 queue.js;计划全量,顺序 = 队列序)。</summary>
    public void Serialize(Serialization.ISerializer s)
    {
        s.Bool("paused", Paused);
        s.NumberI32("count", Plans.Count);
        foreach (var plan in Plans)
            plan.Serialize(s);
    }

    public void Deserialize(Serialization.IDeserializer d, GameState gameState)
    {
        Paused = d.Bool("paused");
        int count = d.NumberI32("count");
        Plans.Clear();
        for (int i = 0; i < count; i++)
            Plans.Add(QueuePlan.Deserialize(d, gameState));
    }

    /// <summary>按元数据键值计数在排单位(原版 countQueuedUnitsWithMetadata;
    /// attackPlan 的 "special" 槽位标记经此计入编组进度)。</summary>
    public int CountQueuedUnitsWithMetadata(string key, object value)
        => Plans.Where(p => p.Metadata.TryGetValue(key, out var v) && Equals(v, value))
            .Sum(p => p.Number);
}
