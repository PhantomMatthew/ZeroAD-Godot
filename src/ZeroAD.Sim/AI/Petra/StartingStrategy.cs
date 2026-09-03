using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;

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

    /// <summary>配置首基地（原版 configFirstBase，425-546 行的资源分析段全量）。
    /// 开局食物/木材评估 → needFarm/needFish/needCorral/saveResources/maxFields/
    /// setRushes 联动;worker 置 idle 由 BaseManager 自动分配。
    /// 与原版的差异记录:startingSize 取寻路网格面积(无网格按大图处理——跳过小图分支)。</summary>
    public static void ConfigFirstBase(Headquarters hq, GameState gameState)
    {
        // 首基地的 worker 全部设 idle → BaseManager 下次 Update 时自动分配
        foreach (var ent in gameState.GetOwnUnits().Values())
            gameState.Metadata.Set(ent.Id, "subrole", WorkerRoles.SubroleIdle);

        // 地图面积(原版 startingSize:accessibility 陆格数 × 格面积;
        // 取寻路网格 navcell 数 = 平方米近似;无网格(无头测试)→ 大图)。
        double startingSize = double.MaxValue;
        var pf = SimSystem.Pathfinder;
        if (pf?.PassabilityGrid != null)
        {
            double side = pf.NavcellsPerSide;
            startingSize = side * side;
        }

        // 食物评估(原版 470-485):开局食物 <800 → 小图 needFish(码头人口门 1)/
        // 大图 needFarm。
        double startingFood = gameState.GetResources().Food
            + Headquarters.GetTotalResourceLevel(gameState)["food"];
        if (startingFood < 800)
        {
            if (startingSize < 25000)
            {
                hq.NeedFish = true;
                hq.Config.Economy.PopForDock = 1;
            }
            else
            {
                hq.NeedFarm = true;
            }
        }

        // 木材评估(原版 486-516):<6000 → saveResources + popPhase2×0.75
        // (早出二阶好扩张);<2000 且需田 → 畜栏替田(田耗木大);>8500 → setRushes
        // (木量充裕才冲;停战太久不冲)。
        double startingWood = gameState.GetResources().Wood
            + Headquarters.GetTotalResourceLevel(gameState)["wood"];
        if (startingWood < 6000)
        {
            hq.SaveResources = true;
            hq.Config.Economy.PopPhase2 = (int)(0.75 * hq.Config.Economy.PopPhase2);
            if (startingWood < 2000 && hq.NeedFarm)
            {
                hq.NeedCorral = true;
                hq.NeedFarm = false;
            }
        }
        if (startingWood > 8500 && hq.CanBuildUnits)
        {
            int allowed = (int)System.Math.Ceiling((startingWood - 8500) / 3000);
            if (gameState.Cm.EndGame.CeasefireActive)
            {
                float remaining = gameState.Cm.EndGame.CeasefireRemaining;
                if (remaining > 900)
                    allowed = 0;
                else if (remaining > 600 && allowed > 1)
                    allowed = 1;
            }
            hq.AttackManager.SetRushes(allowed);
        }

        // 小图 maxFields(原版:startingSize<25000 → 1(且 needCorral);<60000 → 2)。
        if (startingSize < 25000)
        {
            hq.MaxFields = 1;
            hq.NeedCorral = true;
        }
        else if (startingSize < 60000)
        {
            hq.MaxFields = 2;
        }
    }
}
