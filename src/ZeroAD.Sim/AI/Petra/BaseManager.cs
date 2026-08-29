using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>Worker 角色/子角色常量（原版 petra/worker.js:19-35）。</summary>
public static class WorkerRoles
{
    public const string RoleAttack = "attack";
    public const string RoleTrader = "trader";
    public const string RoleSwitchToTrader = "switchToTrader";
    public const string RoleWorker = "worker";
    public const string RoleCriticalEntGuard = "criticalEntGuard";
    public const string RoleCriticalEntHealer = "criticalEntHealer";

    public const string SubroleDefender = "defender";
    public const string SubroleIdle = "idle";
    public const string SubroleBuilder = "builder";
    public const string SubroleCompleting = "completing";
    public const string SubroleWalking = "walking";
    public const string SubroleAttacking = "attacking";
    public const string SubroleGatherer = "gatherer";
    public const string SubroleHunter = "hunter";
    public const string SubroleFisher = "fisher";
    public const string SubroleGarrisoning = "garrisoning";
}

/// <summary>单个 worker 的行为控制器（原版 petra/worker.js，1153 行）。
/// 由 BaseManager 每 think 为每个 worker 调 Update。
/// 根据 metadata 的 subrole 分发到不同行为：gatherer/hunter/fisher/builder/completing。
/// 骨架版——核心分支结构移植，复杂依赖（transport/territory/dropsite）标 TODO。</summary>
public sealed class WorkerAI
{
    private readonly BaseManager _base;

    public WorkerAI(BaseManager b) => _base = b;

    /// <summary>驱动单个 worker（原版 worker.js:37-1153）。</summary>
    public void Update(GameState gameState, AIEntity ent)
    {
        if (!CanAct(gameState, ent)) return;

        var subrole = gameState.Metadata.GetObject(ent.Id, "subrole")?.ToString();
        if (subrole == null)
        {
            gameState.Metadata.Set(ent.Id, "subrole", WorkerRoles.SubroleIdle);
            _base.ReassignIdleWorkers(gameState, ent);
            return;
        }

        switch (subrole)
        {
            case WorkerRoles.SubroleGatherer:
            case WorkerRoles.SubroleHunter:
                UpdateGatherer(gameState, ent, subrole);
                break;
            case WorkerRoles.SubroleFisher:
                // TODO: 完整 fisher 逻辑（依赖 naval）
                break;
            case WorkerRoles.SubroleBuilder:
                UpdateBuilder(gameState, ent);
                break;
            case WorkerRoles.SubroleCompleting:
                UpdateCompleting(gameState, ent);
                break;
            case WorkerRoles.SubroleIdle:
                _base.ReassignIdleWorkers(gameState, ent);
                break;
        }
    }

    private static bool CanAct(GameState gameState, AIEntity ent)
    {
        // 无位置（驻军中）或被标记为 plan -2/-3 → 不动
        var pos = ent.Position;
        if (pos.X == Fixed.Zero && pos.Z == Fixed.Zero) return false;
        var plan = gameState.Metadata.GetObject(ent.Id, "plan");
        if (plan is int p && (p == -2 || p == -3)) return false;
        var transport = gameState.Metadata.GetObject(ent.Id, "transport");
        if (transport != null) return false;  // 等运输
        return true;
    }

    /// <summary>采集者/猎人的行为更新（原版 SUBROLE_GATHERER/HUNTER 分支）。
    /// 检查资源可达性、dropsite 可用性，必要时重新分配。</summary>
    private void UpdateGatherer(GameState gameState, AIEntity ent, string subrole)
    {
        // 检查当前资源是否仍可达
        var supplyId = gameState.Metadata.GetObject(ent.Id, "supply");
        if (supplyId != null)
        {
            var supply = gameState.GetEntityById((uint)supplyId);
            if (supply == null || supply.ResourceSupplyAmount <= 0)
            {
                // 资源耗尽 → 重新分配
                gameState.Metadata.Remove(ent.Id, "supply");
                gameState.Metadata.Set(ent.Id, "subrole", WorkerRoles.SubroleIdle);
                _base.ReassignIdleWorkers(gameState, ent);
                return;
            }
        }

        // 检查携带量满了 → 需要回投放站
        if (ent.ResourceCarrying > 0)
        {
            var carryType = ent.CarryType;
            var dropsites = gameState.GetOwnDropsites(carryType.ToString().ToLowerInvariant());
            if (!dropsites.HasEntities())
            {
                // 无 dropsite → 重新分配
                gameState.Metadata.Set(ent.Id, "subrole", WorkerRoles.SubroleIdle);
            }
            else
            {
                // 找最近投放站并下达 ReturnResource(原版 worker.update 的携带满回送)。
                var nearest = dropsites.FilterNearest(ent.Position2D, 1);
                if (nearest.HasEntities())
                    gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.ReturnResource(
                        (uint)gameState.PlayerId, (uint)ent.Id, (uint)nearest.Values().First().Id));
            }
        }
    }

    /// <summary>建造者行为（原版 SUBROLE_BUILDER 分支）。
    /// 检查目标地基是否存在，存在则继续建造/修复。</summary>
    private void UpdateBuilder(GameState gameState, AIEntity ent)
    {
        var foundationId = gameState.Metadata.GetObject(ent.Id, "target-foundation");
        if (foundationId == null) return;
        var target = gameState.GetEntityById((uint)foundationId);
        if (target == null || !target.IsFoundation)
        {
            // 地基完成或消失 → 回 idle
            gameState.Metadata.Remove(ent.Id, "target-foundation");
            gameState.Metadata.Set(ent.Id, "subrole", WorkerRoles.SubroleIdle);
            return;
        }
        // 继续建造(原版 builder 分支的 repair 命令;Repair 驱动建造进度)。
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Repair(
            (uint)gameState.PlayerId, (uint)ent.Id, (uint)foundationId));
    }

    /// <summary>完成中行为（原版 SUBROLE_COMPLETING 分支）。</summary>
    private void UpdateCompleting(GameState gameState, AIEntity ent)
    {
        // 建筑接近完成时切到此 subrole → 继续采集直到建筑完成
        gameState.Metadata.Set(ent.Id, "subrole", WorkerRoles.SubroleGatherer);
        UpdateGatherer(gameState, ent, WorkerRoles.SubroleGatherer);
    }
}

/// <summary>基地管理器（原版 petra/baseManager.js，1234 行）。
/// 管理单个基地的 worker/building/dropsite，驱动经济运转。
/// 每 think 由 BasesManager.Update 调用各 base 的 Update。</summary>
public sealed class BaseManager
{
    public readonly int ID;
    public readonly PetraConfig Config;
    public readonly BasesManager BasesManager;

    // anchor（CC）状态
    public uint? AnchorId;
    public ushort? AccessIndex;
    public bool Constructing;
    public int NeededDefenders;

    // 最大资源搜索距离（平方）
    public int MaxDistResourceSquare = 360 * 360;

    // 实体集合（每 think 重建）
    public List<AIEntity> Units = new();
    public List<AIEntity> Workers = new();
    public List<AIEntity> Buildings = new();

    private readonly WorkerAI _workerAI;

    public BaseManager(GameState gameState, BasesManager basesManager, int id)
    {
        Config = basesManager.Config;
        BasesManager = basesManager;
        ID = id;
        NeededDefenders = Config.Difficulty > DifficultyLevel.Easy ? 3 + 2 * (Config.Difficulty - 3) : 0;
        _workerAI = new WorkerAI(this);
    }

    /// <summary>主更新（原版 baseManager.js:1057-1234）。</summary>
    public void Update(GameState gameState, AIEventBuffer events)
    {
        // 重建实体集合（按 base metadata 过滤）
        RebuildCollections(gameState);

        if (AnchorId == null)
        {
            // 无 anchor：如果有建筑则继续，否则重分配所有实体到其它 base
            if (Buildings.Count == 0)
            {
                foreach (var ent in Units)
                    gameState.Metadata.Set(ent.Id, "base", 0);  // 回到 baseless
                return;
            }
        }

        // 分配 idle worker 到任务
        AssignRolelessUnits(gameState);
        ReassignIdleWorkers(gameState);

        // 更新每个 worker
        foreach (var ent in Workers.ToList())
            _workerAI.Update(gameState, ent);
    }

    /// <summary>重建实体集合（按 metadata "base" == this.ID 过滤）。</summary>
    private void RebuildCollections(GameState gameState)
    {
        Units.Clear(); Workers.Clear(); Buildings.Clear();
        foreach (var ent in gameState.GetOwnUnits().Values())
        {
            var baseId = gameState.Metadata.GetObject(ent.Id, "base");
            if (baseId != null && (int)baseId == ID)
            {
                Units.Add(ent);
                var role = gameState.Metadata.GetObject(ent.Id, "role")?.ToString();
                if (role == WorkerRoles.RoleWorker) Workers.Add(ent);
            }
        }
        foreach (var ent in gameState.GetOwnStructures().Values())
        {
            var baseId = gameState.Metadata.GetObject(ent.Id, "base");
            if (baseId != null && (int)baseId == ID)
                Buildings.Add(ent);
        }
    }

    /// <summary>分配无角色单位（原版 assignRolelessUnits）。
    /// 简化版：全部设为 worker 角色。</summary>
    public void AssignRolelessUnits(GameState gameState)
    {
        foreach (var ent in Units)
        {
            var role = gameState.Metadata.GetObject(ent.Id, "role")?.ToString();
            if (role == null)
            {
                gameState.Metadata.Set(ent.Id, "role", WorkerRoles.RoleWorker);
                gameState.Metadata.Set(ent.Id, "subrole", WorkerRoles.SubroleIdle);
                Workers.Add(ent);
            }
        }
    }

    /// <summary>重新分配 idle worker（原版 reassignIdleWorkers）。
    /// 简化版：找到最近的资源点分配。</summary>
    public void ReassignIdleWorkers(GameState gameState, AIEntity? specific = null)
    {
        var idleWorkers = specific != null ? new List<AIEntity> { specific } :
            Workers.Where(w => gameState.Metadata.GetObject(w.Id, "subrole")?.ToString() == WorkerRoles.SubroleIdle).ToList();

        foreach (var worker in idleWorkers)
        {
            AssignWorkerToResource(gameState, worker);
        }
    }

    /// <summary>分配 worker 到资源点（原版 worker.startGathering 的 findSupply 评分版）:
    /// 候选 supply 按 价值×剩余量/(1+已在场采集者数) 评分,过滤 敌领土/拥塞(人均剩余
    /// <30)/枯竭。同型内取最高分。</summary>
    private void AssignWorkerToResource(GameState gameState, AIEntity worker)
    {
        // 按当前最缺资源选类型（简化：优先 food → wood → stone → metal）
        var resources = gameState.GetResources();
        string preferredType = "food";
        if (resources.Food > resources.Wood) preferredType = "wood";
        else if (resources.Wood > resources.Stone) preferredType = "stone";

        ZeroAD.Sim.AI.CommonApi.EntityCollection supplies = gameState.GetResourceSupplies(preferredType);
        if (!supplies.HasEntities()) return;

        var territory = SimSystem.Territory;
        var workerPos = worker.Position2D;
        uint bestId = 0;
        AIEntity? best = null;
        float bestScore = float.MinValue;
        foreach (var supply in supplies.Values())
        {
            if (supply.Position2D == default) continue;
            var supplyComp = gameState.Cm.QueryInterface<ZeroAD.Sim.Components.ResourceSupply>(new ZeroAD.Sim.EntityId(supply.Id));
            int amount = supplyComp?.Amount ?? 0;
            if (amount <= 0) continue;   // 枯竭

            // 敌领土排除(原版:territoryOwner != 0 且非盟友 → 拒)。
            if (territory != null)
            {
                int owner = territory.GetOwner(supply.Position2D.X, supply.Position2D.Y);
                if (owner != 0 && owner != gameState.PlayerId) continue;
            }

            // 拥塞控制(原版:人均剩余 <30 → 不加采集者;农场除外——原版
            // "except for farms",food 类豁免拥塞门槛)。在场采集者数从全图
            // 采集者 TargetSupply 统计(原版 resourceSupplyNumGatherers 近似)。
            int numGatherers = CountGatherersAt(gameState, supply.Id);
            if (preferredType != "food" && amount / (1 + numGatherers) < 30)
                continue;

            // 评分:剩余量优先 + 近优先(平方距离惩罚;原版 findSupply 的性价比近似)。
            float dx = supply.Position2D.X.ToFloat() - workerPos.X.ToFloat();
            float dz = supply.Position2D.Y.ToFloat() - workerPos.Y.ToFloat();
            float score = amount * 1.0f - (dx * dx + dz * dz) * 0.001f;
            if (score > bestScore || best == null && score == bestScore)
            {
                bestScore = score;
                best = supply;
                bestId = supply.Id;
            }
        }
        if (best == null) return;
        var chosen = best;

        // 分配：设 subrole + supply metadata
        gameState.Metadata.Set(worker.Id, "subrole", WorkerRoles.SubroleGatherer);
        gameState.Metadata.Set(worker.Id, "supply", chosen.Id);
        gameState.Metadata.Set(worker.Id, "gather-type", preferredType);
        // 下达 gather 命令(NetCommand.Gather → UnitAI GATHER 订单;AI 锁步通道,
        // 与人手同路径同延迟)。此前只分配元数据不下令 → AI 工人分配后永不动。
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Gather(
            (uint)gameState.PlayerId, (uint)worker.Id, chosen.Id));
    }

    /// <summary>该 supply 上的在场采集者数(原版 resourceSupplyNumGatherers 近似:
    /// 全图采集者 TargetSupply 指向该实体的计数)。</summary>
    private static int CountGatherersAt(GameState gameState, uint supplyId)
    {
        int count = 0;
        foreach (var e in gameState.GetOwnUnits().Values())
        {
            var gatherer = gameState.Cm.QueryInterface<ZeroAD.Sim.Components.ResourceGatherer>(new ZeroAD.Sim.EntityId(e.Id));
            if (gatherer?.TargetSupply?.Value == supplyId) count++;
        }
        return count;
    }

    /// <summary>分配实体到此 base（原版 assignEntity）。</summary>
    public void AssignEntity(GameState gameState, AIEntity ent)
    {
        gameState.Metadata.Set(ent.Id, "base", ID);
    }
}
