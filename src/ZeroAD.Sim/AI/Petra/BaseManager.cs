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
                    // 原版 worker.update:gatherer 无 supply 或 supply 已消失 → startGathering 重选。
                    var supplyRef = gameState.Metadata.GetObject(ent.Id, "supply");
                    uint supplyId = supplyRef is int s0 ? (uint)s0
                        : supplyRef is uint s1 ? s1 : 0;
                    if (supplyId == 0 || gameState.GetEntityById(supplyId) == null)
                    {
                        gameState.Metadata.Remove(ent.Id, "supply");
                        _base.StartGathering(gameState, ent);
                    }
                    else
                        UpdateGatherer(gameState, ent, subrole);
                    break;
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
            var supply = gameState.GetEntityById(supplyId is uint su ? su
                : supplyId is int si ? (uint)si : 0);
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
        var target = gameState.GetEntityById(foundationId is uint fu ? fu
            : foundationId is int fi ? (uint)fi : 0);
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

    // ── dropsite 分层补给(原版 baseManager.dropsiteSupplies;worker.startGathering 的核心数据):
    /// 每资源类型(food/wood/stone/metal)按距本基地任一 dropsite 的平方距离分三层
    /// (nearby < max/16,medium < max/4,faraway ≤ max),层内按距离升序。增量维护:
    /// 新 dropsite 入列时扫全图 supply 归入;supply 死亡在使用点剔除。</summary>
    public sealed class SupplyRef
    {
        public uint DropsiteId;
        public uint SupplyId;
        public float Dist;   // 平方距离(排序用)
        public SupplyRef(uint dropsiteId, uint supplyId, float dist)
        { DropsiteId = dropsiteId; SupplyId = supplyId; Dist = dist; }
    }
    public sealed class SupplyTiers
    {
        public readonly List<SupplyRef> Nearby = new();
        public readonly List<SupplyRef> Medium = new();
        public readonly List<SupplyRef> Faraway = new();
        public IEnumerable<SupplyRef> All()
        { foreach (var r in Nearby) yield return r;
          foreach (var r in Medium) yield return r;
          foreach (var r in Faraway) yield return r; }
    }
    public readonly Dictionary<string, SupplyTiers> DropsiteSupplies = new()
    {
        ["food"] = new(), ["wood"] = new(), ["stone"] = new(), ["metal"] = new(),
    };
    /// <summary>已入列的 dropsite 实体集(原版 dropsites{})。</summary>
    private readonly HashSet<uint> _dropsites = new();
    /// <summary>分配瞬间的拥塞记账(原版 TC gatherer:本次 think 内已指派人数,
    /// 避免同一轮把全部 idle worker 压到同一 supply)。</summary>
    private readonly Dictionary<uint, int> _tcGatherers = new();
    public void AddTCGatherer(uint supplyId) =>
        _tcGatherers[supplyId] = _tcGatherers.GetValueOrDefault(supplyId) + 1;
    public void RemoveTCGatherer(uint supplyId)
    { if (_tcGatherers.TryGetValue(supplyId, out int n) && n > 1) _tcGatherers[supplyId] = n - 1;
      else _tcGatherers.Remove(supplyId); }
    public int GetTCGatherer(uint supplyId) => _tcGatherers.GetValueOrDefault(supplyId);

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

        // 新 dropsite 入列(原版 assignResourceToDropsite:基地建设事件驱动;
        // 此处每 think 检测 Buildings 集合差分,等价幂等)。
        foreach (var b in Buildings)
        {
            if (_dropsites.Contains(b.Id)) continue;
            if (string.IsNullOrEmpty(b.Template.ResourceDropsiteTypes)) continue;
            AssignResourceToDropsite(gameState, b);
        }
        // 死 dropsite 清理(原版 removeDropsite)。
        foreach (var dp in _dropsites.ToList())
            if (gameState.GetEntityById(dp) == null)
                RemoveDropsite(gameState, dp);

        // 更新每个 worker
        foreach (var ent in Workers.ToList())
            _workerAI.Update(gameState, ent);

        // TC 记账仅存活一轮(原版 TC gatherer 是分配瞬态)。
        _tcGatherers.Clear();
    }

    /// <summary>原版 assignResourceToDropsite:dropsite 接纳的各资源类型,
    /// 全图 supply(剔除 Animal/Field——移动资源与农田另行处理)按可达性
    /// (同 landmass)+ 距离分层归入,层内按距离升序。</summary>
    private void AssignResourceToDropsite(GameState gameState, AIEntity dropsite)
    {
        if (!_dropsites.Add(dropsite.Id)) return;
        if (dropsite.Position2D == default) return;
        ushort accessIndex = AccessIndex
            ?? gameState.Accessibility?.GetAccessValue(
                dropsite.Position2D.X.ToFloat(), dropsite.Position2D.Y.ToFloat()) ?? (ushort)0;

        float maxDist = MaxDistResourceSquare;
        foreach (var type in dropsite.Template.ResourceDropsiteTypes!.Split(' ',
            System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (!DropsiteSupplies.TryGetValue(type, out var tiers)) continue;
            foreach (var supply in gameState.GetResourceSupplies(type).Values())
            {
                if (supply.Position2D == default) continue;
                if (supply.HasClass("Animal") || supply.HasClass("Field")) continue;
                if (gameState.Accessibility != null
                    && gameState.Accessibility.GetAccessValue(
                        supply.Position2D.X.ToFloat(), supply.Position2D.Y.ToFloat()) != accessIndex)
                    continue;
                float dx = supply.Position2D.X.ToFloat() - dropsite.Position2D.X.ToFloat();
                float dz = supply.Position2D.Y.ToFloat() - dropsite.Position2D.Y.ToFloat();
                float dist = dx * dx + dz * dz;
                if (dist >= maxDist) continue;
                var list = dist < maxDist / 16 ? tiers.Nearby
                    : dist < maxDist / 4 ? tiers.Medium : tiers.Faraway;
                list.Add(new SupplyRef(dropsite.Id, supply.Id, dist));
            }
            tiers.Nearby.Sort((a, b) => a.Dist.CompareTo(b.Dist));
            tiers.Medium.Sort((a, b) => a.Dist.CompareTo(b.Dist));
            tiers.Faraway.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        }
    }

    /// <summary>原版 removeDropsite:剔除该 dropsite 名下全部 supply 引用。</summary>
    private void RemoveDropsite(GameState gameState, uint dropsiteId)
    {
        _dropsites.Remove(dropsiteId);
        foreach (var tiers in DropsiteSupplies.Values)
            foreach (var list in new[] { tiers.Nearby, tiers.Medium, tiers.Faraway })
                list.RemoveAll(r => r.DropsiteId == dropsiteId
                    || gameState.GetEntityById(r.SupplyId) == null);
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

    /// <summary>重新分配 idle worker（原版 reassignIdleWorkers 移植）:
    /// 按 HQ.pickMostNeededResources 的需求序分配 gather-type(逐 canGather 过滤 +
    /// lastFailedGather 20s 冷却 + 非 food 枯竭类跳过);Support+Elephant 只当 builder;
    /// FastMoving 可猎者 → hunter;FishingBoat → fisher。</summary>
    public void ReassignIdleWorkers(GameState gameState, AIEntity? specific = null)
    {
        var idleWorkers = specific != null ? new List<AIEntity> { specific } :
            Workers.Where(w => gameState.Metadata.GetObject(w.Id, "subrole")?.ToString() == WorkerRoles.SubroleIdle).ToList();

        foreach (var worker in idleWorkers)
        {
            if (worker.Position2D == default) continue;   // 驻军中等出营
            if (worker.HasClass("Support") && worker.HasClass("Elephant")) continue;

            if (worker.HasClass("Worker"))
            {
                // 紧急修 anchor(原版:needsRepair 且修理者 <2;assignToFoundations 管正式的)。
                if (worker.IsBuilder && AnchorId != null
                    && gameState.GetEntityById(AnchorId.Value) is { } anchor
                    && anchor.NeedsRepair
                    && CountTargetFoundation(gameState, AnchorId.Value) < 2)
                {
                    gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Repair(
                        (uint)gameState.PlayerId, (uint)worker.Id, AnchorId.Value));
                    continue;
                }
                if (!worker.IsGatherer) continue;
                var hq = BasesManagerHq(gameState);
                // 需求序(原版 pickMostNeededResources;无 HQ 注入的测试环境降级固定序)。
                var neededList = hq?.PickMostNeededResources(gameState)
                    .Select(n => (n.Type, n.Wanted, n.Current)).ToList()
                    ?? new List<(string Type, double Wanted, double Current)>
                    { ("food", 1, 0), ("wood", 1, 0), ("stone", 1, 0), ("metal", 1, 0) };
                foreach (var needed in neededList)
                {
                    if (!worker.CanGather(needed.Type)) continue;
                    if (hq != null && hq.LastFailedGather.TryGetValue(needed.Type, out int failedTurn)
                        && gameState.ElapsedTime - failedTurn * 0.1 < 20)
                        continue;
                    gameState.Metadata.Set(worker.Id, "subrole", WorkerRoles.SubroleGatherer);
                    gameState.Metadata.Set(worker.Id, "gather-type", needed.Type);
                    StartGathering(gameState, worker);
                    break;
                }
            }
            else if (worker.HasClass("FastMoving") && worker.CanGather("food") && worker.CanAttack)
            {
                // 骑兵类无 Worker 标签的可猎单位(原版 FastMoving → hunter 分支)。
                gameState.Metadata.Set(worker.Id, "subrole", WorkerRoles.SubroleHunter);
                gameState.Metadata.Set(worker.Id, "gather-type", "food");
                StartGathering(gameState, worker);
            }
            else if (worker.HasClass("FishingBoat"))
                gameState.Metadata.Set(worker.Id, "subrole", WorkerRoles.SubroleFisher);
        }
    }

    private static int CountTargetFoundation(GameState gameState, uint foundationId)
    {
        int n = 0;
        foreach (var e in gameState.GetOwnUnits().Values())
            if (gameState.Metadata.GetObject(e.Id, "target-foundation") is uint tf && tf == foundationId)
                n++;
        return n;
    }

    /// <summary>BasesManager → HQ 反链(原版 base.basesManager.HQ;无注入的测试环境
    /// 返回 null,调用方降级为固定 food→wood→stone→metal 序)。</summary>
    private Headquarters? BasesManagerHq(GameState gameState) =>
        BasesManager.HqResolver?.Invoke(gameState);

    /// <summary>原版 worker.startGathering 全量移植(无 naval 运输段——无船图恒不可达):
    /// 宝藏优先 → food 先猎 → 本基地 nearby →(food:田)→ medium → 他基地(同陆)
    /// nearby/medium → 助建 dropsite 地基 → faraway(food 需不可建田/畜栏才远行)→ 记失败冷却。
    /// 找到即下令(Gather/Repair)并返回 true。</summary>
    public bool StartGathering(GameState gameState, AIEntity worker)
    {
        // 1) 宝藏(原版 gatherTreasure:最近的可见宝藏)。
        if (worker.IsTreasureCollector || worker.HasClass("Unit"))
        {
            var treasures = gameState.GetTreasureSupplies();
            if (treasures.HasEntities())
            {
                var nearest = treasures.FilterNearest(worker.Position2D, 1);
                if (nearest.HasEntities())
                {
                    var t = nearest.Values().First();
                    gameState.Metadata.Set(worker.Id, "supply", t.Id);
                    gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Gather(
                        (uint)gameState.PlayerId, (uint)worker.Id, t.Id));
                    return true;
                }
            }
        }

        string resource = gameState.Metadata.GetObject(worker.Id, "gather-type")?.ToString() ?? "food";

        // 2) food → 先打猎(原版 startHunting:猎物近且可猎时优先于种田)。
        if (resource == "food" && TryStartHunting(gameState, worker))
            return true;

        ushort entAccess = gameState.Accessibility?.GetAccessValue(
            worker.Position2D.X.ToFloat(), worker.Position2D.Y.ToFloat()) ?? (ushort)0;
        ushort baseAccess = AccessIndex ?? entAccess;

        // 3) 本基地(同陆):nearby →(food:就近田)→ medium。
        if (baseAccess == entAccess)
        {
            if (FindSupplyInTier(gameState, worker, DropsiteSupplies[resource].Nearby)) return true;
            if (resource == "food" && TryGatherNearestField(gameState, worker, ID)) return true;
            if (FindSupplyInTier(gameState, worker, DropsiteSupplies[resource].Medium)) return true;
        }

        // 4) 他基地(同陆):nearby →(food:田)→ medium;采到即转籍(原版 setMetadata base)。
        foreach (var b in BasesManager.Bases)
        {
            if (b.ID == ID) continue;
            ushort bAccess = b.AccessIndex ?? 0;
            if (bAccess != entAccess) continue;
            if (FindSupplyInTier(gameState, worker, b.DropsiteSupplies[resource].Nearby))
            { gameState.Metadata.Set(worker.Id, "base", b.ID); return true; }
            if (resource == "food" && TryGatherNearestField(gameState, worker, b.ID))
            { gameState.Metadata.Set(worker.Id, "base", b.ID); return true; }
            if (FindSupplyInTier(gameState, worker, b.DropsiteSupplies[resource].Medium))
            { gameState.Metadata.Set(worker.Id, "base", b.ID); return true; }
        }

        // 5) 助建 dropsite 地基(同陆;原版:无点可采时帮建投放站)。
        if (worker.IsBuilder)
            foreach (var f in gameState.GetOwnFoundations().Values())
            {
                if (f.Position2D == default) continue;
                if (gameState.Accessibility != null
                    && gameState.Accessibility.GetAccessValue(
                        f.Position2D.X.ToFloat(), f.Position2D.Y.ToFloat()) != entAccess)
                    continue;
                var builtTypes = f.Template.ResourceDropsiteTypes;
                if (builtTypes == null || !builtTypes.Split(' ').Contains(resource)) continue;
                gameState.Metadata.Set(worker.Id, "target-foundation", f.Id);
                gameState.Metadata.Set(worker.Id, "subrole", WorkerRoles.SubroleBuilder);
                gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Repair(
                    (uint)gameState.PlayerId, (uint)worker.Id, f.Id));
                return true;
            }

        // 6) faraway(food 仅在不能种田/畜栏时远行——原版 allowDistantFood 判定)。
        var hq2 = BasesManagerHq(gameState);
        bool allowDistant = resource != "food" || hq2 == null
            || (!hq2.CanBuild(gameState, "structures/{civ}/field")
                && !hq2.CanBuild(gameState, "structures/{civ}/corral"));
        if (allowDistant)
        {
            if (baseAccess == entAccess
                && FindSupplyInTier(gameState, worker, DropsiteSupplies[resource].Faraway))
                return true;
            foreach (var b in BasesManager.Bases)
            {
                if (b.ID == ID || (b.AccessIndex ?? 0) != entAccess) continue;
                if (FindSupplyInTier(gameState, worker, b.DropsiteSupplies[resource].Faraway))
                { gameState.Metadata.Set(worker.Id, "base", b.ID); return true; }
            }
        }

        // 7) 无点可采 → 记失败冷却(原版 lastFailedGather;20s 内 reassign 不再试该类)。
        if (hq2 != null) hq2.LastFailedGather[resource] = (int)(gameState.ElapsedTime * 10);
        gameState.Metadata.Set(worker.Id, "subrole", WorkerRoles.SubroleIdle);
        return false;
    }

    /// <summary>原版 findSupply:层内按距离序取首个合格 supply——剔除死条目;
    /// 过滤:采集速率表无此 subtype / 拥塞(grain 豁免,人均剩余 <30)/
    /// 敌领土 / 不可达冷却。命中即设 supply 元数据 + 下令 + TC 记账。</summary>
    private bool FindSupplyInTier(GameState gameState, AIEntity worker, List<SupplyRef> tier)
    {
        if (tier.Count == 0) return false;
        var gatherRates = worker.Template.ResourceGatherRates();
        var territory = SimSystem.Territory;
        for (int i = 0; i < tier.Count; i++)
        {
            var ent = gameState.GetEntityById(tier[i].SupplyId);
            if (ent == null) { tier.RemoveAt(i--); continue; }   // 枯竭/死亡剔除(原版同款)
            var supply = gameState.Cm.QueryInterface<ZeroAD.Sim.Components.ResourceSupply>(ent.Entity);
            if (supply == null || supply.Amount <= 0) continue;
            string supplyType = supply.GenericType + "." + supply.SpecificType;
            if (!gatherRates.ContainsKey(supplyType)) continue;   // 这工人不会采这 subtype

            // 拥塞:grain(农田)豁免;人均剩余 <30 → 不加人(原版同款)。
            int nbGatherers = CountGatherersAt(gameState, ent.Id) + GetTCGatherer(ent.Id);
            if (supply.SpecificType != "grain" && nbGatherers > 0
                && supply.Amount / (1 + nbGatherers) < 30)
                continue;

            // 敌领土拒绝(原版 territoryMap.getOwner != 0 且非盟友——玩家自身视为盟友)。
            if (territory != null && ent.Position2D != default)
            {
                int owner = territory.GetOwner(ent.Position2D.X, ent.Position2D.Y);
                if (owner != 0 && owner != gameState.PlayerId && !gameState.IsPlayerAlly(owner))
                    continue;
            }

            AddTCGatherer(ent.Id);
            gameState.Metadata.Set(worker.Id, "supply", ent.Id);
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Gather(
                (uint)gameState.PlayerId, (uint)worker.Id, ent.Id));
            return true;
        }
        return false;
    }

    /// <summary>food 分流:就近可采农田(原版 gatherNearestField——本基地名下的 Field)。</summary>
    private bool TryGatherNearestField(GameState gameState, AIEntity worker, int baseId)
    {
        AIEntity? best = null;
        float bestDist = float.MaxValue;
        foreach (var f in gameState.GetResourceSupplies("food").Values())
        {
            if (!f.HasClass("Field") || f.Position2D == default) continue;
            if (gameState.Metadata.GetObject(f.Id, "base") is int fb && fb != baseId) continue;
            var supply = gameState.Cm.QueryInterface<ZeroAD.Sim.Components.ResourceSupply>(f.Entity);
            if (supply == null || supply.Amount <= 0) continue;
            if (!gatherRatesContain(worker, supply)) continue;
            float dx = f.Position2D.X.ToFloat() - worker.Position2D.X.ToFloat();
            float dz = f.Position2D.Y.ToFloat() - worker.Position2D.Y.ToFloat();
            float d = dx * dx + dz * dz;
            if (d < bestDist) { bestDist = d; best = f; }
        }
        if (best == null) return false;
        gameState.Metadata.Set(worker.Id, "supply", best.Id);
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Gather(
            (uint)gameState.PlayerId, (uint)worker.Id, best.Id));
        return true;

        static bool gatherRatesContain(AIEntity w, ZeroAD.Sim.Components.ResourceSupply s)
            => w.Template.ResourceGatherRates().ContainsKey(s.GenericType + "." + s.SpecificType);
    }

    /// <summary>food 先猎(原版 startHunting 简化版:最近可猎动物;
    /// 距离过远仅 FastMoving——原版"only FastMoving should hunt faraway")。</summary>
    private bool TryStartHunting(GameState gameState, AIEntity worker)
    {
        var huntable = gameState.GetHuntableSupplies();
        if (!huntable.HasEntities()) return false;
        var nearest = huntable.FilterNearest(worker.Position2D, 1);
        if (!nearest.HasEntities()) return false;
        var prey = nearest.Values().First();
        float dx = prey.Position2D.X.ToFloat() - worker.Position2D.X.ToFloat();
        float dz = prey.Position2D.Y.ToFloat() - worker.Position2D.Y.ToFloat();
        // 原版远猎限 FastMoving(>90m 平方 8100 阈值近似)。
        if (dx * dx + dz * dz > 90f * 90f && !worker.HasClass("FastMoving")) return false;
        gameState.Metadata.Set(worker.Id, "supply", prey.Id);
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Gather(
            (uint)gameState.PlayerId, (uint)worker.Id, prey.Id));
        return true;
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
