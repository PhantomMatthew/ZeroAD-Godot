using System.Linq;
using ZeroAD.Sim.AI.CommonApi;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>开局策略（原版 petra/startingStrategy.js，546 行）。
/// gameAnalysis: 分析开局（区域/建筑/资源）。
/// buildFirstBase: 从 StartEntities 建第一个基地。
/// configFirstBase: 配置首基地（分配初始 worker）。
/// 骨架版——核心结构移植，复杂依赖标 TODO。</summary>
public static class StartingStrategy
{
    /// <summary>开局分析（原版 gameAnalysis，15-65 行）。
    /// 在第一回合调用：分析地形可达性 + 结构 + 区域，为后续决策奠基。</summary>
    public static void GameAnalysis(Headquarters hq, GameState gameState)
    {
        // 原版调 regionAnalysis（Accessibility 分析每个实体的区域 ID）+
        // structureAnalysis（统计已有建筑）+ 设置 turnCache。
        // 骨架版：仅标记 FirstBaseConfig=false
        hq.FirstBaseConfig = false;
    }

    /// <summary>建第一个基地（原版 buildFirstBase，224-340 行）。
    /// 从 StartEntities 创建 BaseManager，分配初始单位。
    /// 在第一回合 GameAnalysis 后调用。</summary>
    public static void BuildFirstBase(Headquarters hq, GameState gameState)
    {
        if (hq.FirstBaseConfig) return;

        // 找 CC（CivCentre 类）作为基地 anchor
        var cc = gameState.GetOwnStructures().Filter(e => e.HasClass("CivCentre"));
        if (!cc.HasEntities())
        {
            // 无 CC → 用第一个建筑作 anchor
            cc = gameState.GetOwnStructures();
            if (!cc.HasEntities()) return;
        }

        var anchor = cc.Values().First();
        if (anchor == null) return;

        // 创建基地
        var baseMgr = hq.BasesManager.CreateBase(gameState, anchor.Id);
        baseMgr.AccessIndex = EntityExtend.GetLandAccess(gameState, anchor);

        // 分配所有初始单位到此基地
        foreach (var ent in gameState.GetOwnUnits().Values())
        {
            baseMgr.AssignEntity(gameState, ent);
            gameState.Metadata.Set(ent.Id, "role", WorkerRoles.RoleWorker);
            gameState.Metadata.Set(ent.Id, "subrole", WorkerRoles.SubroleIdle);
        }

        // 分配所有初始建筑
        foreach (var ent in gameState.GetOwnStructures().Values())
            baseMgr.AssignEntity(gameState, ent);

        hq.FirstBaseConfig = true;
    }

    /// <summary>配置首基地（原版 configFirstBase，425-546 行）。
    /// 设置初始采集目标、派遣初始 worker 到最近资源。
    /// 骨架版：触发 ReassignIdleWorkers 让 BaseManager 自动分配。</summary>
    public static void ConfigFirstBase(Headquarters hq, GameState gameState)
    {
        // 首基地的 worker 全部设 idle → BaseManager 下次 Update 时自动分配
        foreach (var ent in gameState.GetOwnUnits().Values())
            gameState.Metadata.Set(ent.Id, "subrole", WorkerRoles.SubroleIdle);
    }
}
