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
    /// 优先级:人口加成(Population/Bonus) > 贸易(Trader 速度/增益) >
    /// wanted techs(解锁冠军/采集率/射程;原版 researchWantedTechs) >
    /// 第一个可用科技。</summary>
    public void Update(GameState gameState, QueueManager queues)
    {
        var available = gameState.FindAvailableTech();
        if (available.Count == 0) return;

        // 1. 人口加成科技(原版 researchPopulationBonus:含 Population/Bonus 修改)。
        foreach (var tech in available)
        {
            if (!gameState.TechCatalog.Technologies.TryGetValue(tech, out var def)) continue;
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

        // 2. 贸易科技(原版 researchTradeBonus:affects 含 Trader + 修改 UnitMotion/
        // WalkSpeed 或 Trader/GainMultiplier)。当前 AI 有市场才研(贸易经理活跃判)。
        bool hasMarket = gameState.GetOwnStructures().Filter(e => e.HasClass("Market")).HasEntities();
        if (hasMarket)
        {
            foreach (var tech in available)
            {
                if (!gameState.TechCatalog.Technologies.TryGetValue(tech, out var def)) continue;
                bool isTradeTech = false;
                foreach (var mod in def.Modifications)
                {
                    if (mod.Path != null && (mod.Path.Contains("UnitMotion/WalkSpeed")
                        || mod.Path.Contains("Trader/GainMultiplier")))
                    {
                        isTradeTech = true;
                        break;
                    }
                }
                if (isTradeTech)
                {
                    queues.AddPlan("minorTech", new ResearchPlan(gameState, tech));
                    return;
                }
            }
        }

        // 3. wanted techs(原版 researchWantedTechs:采集率/射程/解锁冠军)。
        // phase1 时检查资源余量(原版 costMax > 10×numWorkers 跳过——养不起不研)。
        int phase = gameState.CurrentPhase();
        foreach (var tech in available)
        {
            if (!gameState.TechCatalog.Technologies.TryGetValue(tech, out var def)) continue;
            if (tech.StartsWith("unlock_champion", System.StringComparison.Ordinal))
            {
                queues.AddPlan("majorTech", new ResearchPlan(gameState, tech));
                return;
            }
            bool isWanted = false;
            foreach (var mod in def.Modifications)
            {
                if (mod.Path == null) continue;
                if (mod.Path.StartsWith("ResourceGatherer/Rates/", System.StringComparison.Ordinal)
                    || mod.Path.StartsWith("ResourceGatherer/Capacities", System.StringComparison.Ordinal)
                    || mod.Path == "Attack/Ranged/MaxRange")
                {
                    isWanted = true;
                    break;
                }
            }
            if (isWanted)
            {
                if (phase == 1)
                {
                    var avail = gameState.GetResources();
                    int numWorkers = gameState.CountOwnEntitiesByRole("worker");
                    int costMax = System.Math.Max(0, System.Math.Max(
                        def.Food - avail.Food, System.Math.Max(
                            def.Wood - avail.Wood, System.Math.Max(
                                def.Stone - avail.Stone, def.Metal - avail.Metal))));
                    if (10 * numWorkers < costMax) continue;   // 原版:养不起不研
                }
                queues.AddPlan("minorTech", new ResearchPlan(gameState, tech));
                return;
            }
        }

        // 4. 否则取第一个可用科技(原版兜底)。
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

/// <summary>驻军管理器已独立成件:见 GarrisonManager.cs(核心闭环移植——威胁塞人/
/// 安全放出,含 Ungarrison 命令下达)。原骨架(keepGarrisoned 决策,命令未接线)废弃。</summary>
