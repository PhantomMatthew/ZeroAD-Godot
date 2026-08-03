using System.Linq;
using ZeroAD.Sim.AI.CommonApi;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>建造管理器（原版 petra/buildManager.js，191 行）。
/// 检查是否需要建造特定类型的建筑（简化的 IsMissing 方法）。
/// 完整版有 hasNeed/update 逻辑——此处骨架。</summary>
public sealed class BuildManager
{
    public bool HasNeed(GameState gameState, string buildingClass)
    {
        // 简化：检查是否已有该类建筑
        var existing = gameState.GetOwnStructures().Filter(e => e.HasClass(buildingClass));
        return !existing.HasEntities();
    }
}

/// <summary>研究管理器（原版 petra/researchManager.js，248 行）。
/// checkPhase: 检查是否应升级阶段（人口/worker 达阈值）。
/// update: 选择研究科技（population bonus / trade / wanted techs）。</summary>
public sealed class ResearchManager
{
    private readonly PetraConfig _config;

    public ResearchManager(PetraConfig config) => _config = config;

    /// <summary>检查阶段升级（原版 checkPhase，17-42 行）。
    /// 人口达 Economy.popPhase2/workPhase3/workPhase4 阈值时入队升级科技。</summary>
    public void CheckPhase(GameState gameState, QueueManager queues)
    {
        int phase = gameState.CurrentPhase();
        if (phase >= gameState.GetNumberOfPhases()) return;

        int pop = gameState.GetPopulation();
        int targetPop = phase == 0 ? _config.Economy.PopPhase2 :
                        phase == 1 ? _config.Economy.WorkPhase3 : _config.Economy.WorkPhase4;

        if (pop < targetPop) return;

        // 入队升级科技
        string nextPhase = gameState.GetPhaseName(phase + 1);
        if (!string.IsNullOrEmpty(nextPhase) && gameState.CanResearch(nextPhase))
            queues.AddPlan("majorTech", new ResearchPlan(gameState, nextPhase));
    }

    /// <summary>主更新（原版 update，163-248 行）。
    /// 简化版：检查人口加成科技 + 经济科技。</summary>
    public void Update(GameState gameState, QueueManager queues)
    {
        // 简化：每隔几 think 检查可用科技
        var available = gameState.FindAvailableTech();
        if (available.Count == 0) return;

        // 优先人口加成科技（PopBonus class）
        foreach (var tech in available)
        {
            if (!gameState.TechCatalog.Technologies.TryGetValue(tech, out var def)) continue;
            // 检查 modifications 是否含 Population/Bonus
            bool isPopTech = false;
            foreach (var mod in def.Modifications)
            {
                if (mod.Path != null && mod.Path.Contains("Population")) { isPopTech = true; break; }
            }
            if (isPopTech)
            {
                queues.AddPlan("majorTech", new ResearchPlan(gameState, tech));
                return;
            }
        }

        // 否则取第一个可用科技
        queues.AddPlan("minorTech", new ResearchPlan(gameState, available[0]));
    }
}

/// <summary>紧急管理器（原版 petra/emergencyManager.js，100 行）。
/// 检测紧急情况（人口/建筑/根节点低于阈值）并触发应急措施（暂停队列、训练紧急单位）。</summary>
public sealed class EmergencyManager
{
    private readonly PetraConfig _config;
    public bool Emergency { get; private set; }

    public EmergencyManager(PetraConfig config) => _config = config;

    /// <summary>主更新（原版 update，25-100 行）。
    /// 简化版：检查 worker 数是否低于阈值 → 标记紧急。</summary>
    public void Update(GameState gameState)
    {
        int workers = gameState.CountOwnEntitiesByRole("worker");
        int workersMin = _config.Economy.PopPhase2;
        Emergency = workers < workersMin * 0.3;  // 低于 30% → 紧急
    }
}

/// <summary>驻军管理器（原版 petra/garrisonManager.js，393 行）。
/// 管理单位进出驻军（受伤时驻入回血、危险时避难、攻击时出驻）。
/// 骨架版——核心 keepGarrisoned 决策移植，出驻逻辑标 TODO。</summary>
public sealed class GarrisonManager
{
    private readonly PetraConfig _config;

    public GarrisonManager(PetraConfig config) => _config = config;

    /// <summary>主更新（原版 update，29-296 行）。
    /// 简化版：检查每个驻军持有者，决定是否保持驻军或释放。</summary>
    public void Update(GameState gameState, AIEventBuffer events)
    {
        foreach (var holder in gameState.GetOwnStructures().Filter(e => e.GarrisonedCount > 0).Values())
        {
            // 简化：低血量单位保持驻军（回血）；满血 + 无威胁 → 释放
            foreach (var entId in GetGarrisonedEntities(gameState, holder))
            {
                var ent = gameState.GetEntityById(entId);
                if (ent == null) continue;
                if (ent.HealthLevel > _config.GarrisonHealthLevel.High)
                {
                    // 满血 → 释放（TODO: 下达 ungarrison 命令）
                }
            }
        }
    }

    /// <summary>判断单位是否应保持驻军（原版 keepGarrisoned，297-393 行）。
    /// 简化版：血量低或附近有敌人 → 保持。</summary>
    public bool KeepGarrisoned(GameState gameState, AIEntity ent, AIEntity holder)
    {
        if (ent.HealthLevel < _config.GarrisonHealthLevel.Medium) return true;
        // TODO: 检查附近是否有威胁
        return false;
    }

    private static System.Collections.Generic.List<uint> GetGarrisonedEntities(GameState gameState, AIEntity holder)
    {
        // 简化：从 GarrisonHolderComponent 读（需要 ComponentManager 查询）
        // 当前 AIEntity 不暴露 garrisoned 列表——返回空
        return new();
    }
}
