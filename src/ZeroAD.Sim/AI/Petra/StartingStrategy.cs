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
    /// <summary>开局分析（原版 gameAnalysis，15-65 行 + assignStartingEntities 的区域标注段）。
    /// 在第一回合调用：海图判定(原版此处即定 navalMap,tradeManager.init 读)+
    /// 运营陆区标注(本单位所在陆区;海图时经海可达的大陆区)——HQ.LandRegions
    /// 是 FindMarketLocation/queueplanBuilding 的选址门。</summary>
    public static void GameAnalysis(Headquarters hq, GameState gameState)
    {
        hq.FirstBaseConfig = false;
        hq.EnsureNavalMap(gameState);   // 原版 gameAnalysis 即定 navalMap

        var acc = gameState.Accessibility;
        if (acc == null) return;

        // 原版 assignStartingEntities 的"小区纠偏"段:本单位所在陆区登记为运营区
        // (land > 1 = 非不可通行/非未分区)。
        foreach (var ent in gameState.GetOwnEntities().Values())
        {
            if (ent.Position2D == default) continue;
            ushort land = acc.LandRegionAt(
                ent.Position2D.X.ToFloat(), ent.Position2D.Y.ToFloat());
            if (land > 1) hq.LandRegions.Add(land);
        }

        // 原版 gameAnalysis 的大陆区段(startingStrategy.js:150-170 近似):
        // 海图时,主陆区经海可达且(陆区 >10% 全图 或 夹间海域 >20% 全图)的
        // 陆区(>320 格)也属运营区(跨海扩张选址放行)。
        if (hq.NavalMap && hq.LandRegions.Count > 0)
        {
            int total = acc.Width * acc.Height;
            int main = hq.LandRegions.OrderBy(id => id).First();
            for (int id = 2; id < acc.RegionCount; id++)
            {
                if (acc.GetRegionType(id) != "land" || hq.LandRegions.Contains(id)) continue;
                int size = acc.RegionSizeById(id);
                if (size <= 320) continue;   // 原版 cellArea(=1m²)×regionSize > 320
                ushort sea = NavalManager.GetSeaBetweenIndices(
                    gameState, (ushort)main, (ushort)id);
                if (sea == 0) continue;
                if (size > 0.1 * total || acc.RegionSizeById(sea) > 0.2 * total)
                    hq.LandRegions.Add(id);
            }
        }
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
