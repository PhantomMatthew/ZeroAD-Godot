using System.Collections.Generic;
using ZeroAD.Sim.AI.CommonApi;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>总指挥部（原版 petra/headquarters.js，2458 行）。
/// Petra 的大脑——每回合编排所有管理器、做高层建造/训练/扩张决策。
///
/// update 主循环顺序（逐字移植 headquarters.js:2242-2350）：
///   emergencyManager → checkEvents → navalManager.checkEvents →
///   researchManager.checkPhase → 每4回合(trainWorkers/buildHouses/farmstead/corral) →
///   每5回合(research) → checkBaseExpansion → buildMarket/Forge/Temple/Wonder →
///   tradeManager → garrisonManager → defenseManager →
///   constructTrainingBuildings/buildDefenses → basesManager →
///   navalManager → attackManager → diplomacyManager → victoryManager → updateCaptureStrength
///
/// 子管理器（2.7 逐步实现）：buildManager/defenseManager/tradeManager/navalManager/
/// researchManager/diplomacyManager/garrisonManager/victoryManager/emergencyManager/attackManager。
/// 当前为骨架——update 主循环已就位，各决策方法标 TODO 逐步填充。</summary>
public sealed class Headquarters
{
    public readonly PetraConfig Config;
    public readonly QueueManager Queues;

    // 子管理器（2.7 逐步填充）
    public BasesManager BasesManager;
    // public AttackManager AttackManager;  // Phase 3
    // public DefenseManager DefenseManager;  // Phase 3
    // public BuildManager BuildManager;  // 2.7
    // public TradeManager TradeManager;  // Phase 3
    // public NavalManager NavalManager;  // Phase 3
    // public ResearchManager ResearchManager;  // 2.7
    // public DiplomacyManager DiplomacyManager;  // Phase 3
    // public GarrisonManager GarrisonManager;  // 2.7
    // public VictoryManager VictoryManager;  // Phase 3
    // public EmergencyManager EmergencyManager;  // 2.7

    public int Phasing;  // 0=无，>0=正在升级到 phase i
    public int CurrentPhase;
    public bool FirstBaseConfig;
    public int TargetNumWorkers;
    public double SupportRatio;
    public bool CanBarter;
    public bool CanBuildUnits = true;
    public bool CanExpand = true;
    public bool SaveResources;
    public bool NeedFarm;
    public bool NeedCorral;
    public bool NeedFish;

    // 建造计时
    public int FortStartTime = 180;
    public int TowerStartTime;
    public int TowerLapseTime;
    public int FortressStartTime;
    public int FortressLapseTime;
    public int ExtraTowers;
    public int ExtraFortresses;

    // 占领目标缓存
    public readonly Dictionary<uint, CapturableTarget> CapturableTargets = new();

    // 回合缓存
    private readonly Dictionary<string, object> _turnCache = new();
    public readonly Dictionary<string, int> LastFailedGather = new();

    public Headquarters(PetraConfig config)
    {
        Config = config;
        Queues = new QueueManager(config);
        BasesManager = new BasesManager(config);
        TargetNumWorkers = config.Economy.TargetNumWorkers;
        SupportRatio = config.Economy.SupportRatio;
        TowerLapseTime = config.Military.TowerLapseTime;
        FortressLapseTime = config.Military.FortressLapseTime;
        ExtraTowers = (int)System.Math.Round(System.Math.Min(config.Difficulty, 3) * config.Personality.Defensive);
        ExtraFortresses = (int)System.Math.Round(System.Math.Max(System.Math.Min(config.Difficulty - 1, 2), 0) * config.Personality.Defensive);
    }

    /// <summary>主更新（原版 headquarters.js:2242-2350）。每 think 回合调一次。</summary>
    public void Update(GameState gameState, AIEventBuffer events)
    {
        // 清回合缓存
        _turnCache.Clear();
        CurrentPhase = gameState.CurrentPhase();

        // 事件处理
        CheckEvents(gameState, events);

        // 阶段升级检查
        if (Phasing != 0)
            CheckPhaseRequirements(gameState);
        // else ResearchManager.CheckPhase(gameState);  // TODO: 2.7

        // 核心经济循环（每4回合轮替）
        bool hasActive = HasActiveBase(gameState);
        if (hasActive)
        {
            int turnMod = gameState.Events.Events.Count % 4;  // 简化：用事件数模4（原版用 playedTurn）
            if (turnMod == 0) TrainMoreWorkers(gameState);
            if (turnMod == 1) BuildMoreHouses(gameState);
            if (turnMod == 2 && (!SaveResources || CanBarter)) BuildFarmstead(gameState);
            if (turnMod == 3 && NeedCorral) ManageCorral(gameState);
            // if (turnMod % 5 == 1) ResearchManager.Update(gameState);  // TODO: 2.7
        }

        // 基地扩张（每10回合）
        if (!HasPotentialBase(gameState) || (CanExpand && CurrentPhase > 1))
            CheckBaseExpansion(gameState);

        // 建筑（每3回合，town+）
        if (CurrentPhase > 1)
        {
            if (!CanBarter) BuildMarket(gameState);
            if (!SaveResources) { BuildForge(gameState); BuildTemple(gameState); }
        }

        // 训练建筑 + 防御（每3回合）
        if (gameState.Events.Events.Count % 3 == 0)
        {
            ConstructTrainingBuildings(gameState);
            if (Config.Difficulty > DifficultyLevel.Sandbox) BuildDefenses(gameState);
        }

        // 子管理器更新
        BasesManager.Update(gameState, events);
        // DefenseManager.Update(gameState, events);  // Phase 3
        // GarrisonManager.Update(gameState, events);  // 2.7
        // TradeManager.Update(gameState, events);  // Phase 3
        // NavalManager.Update(gameState, events);  // Phase 3
        // if (Config.Difficulty > Sandbox && (hasActive || !CanBuildUnits))
        //     AttackManager.Update(gameState, events);  // Phase 3
        // DiplomacyManager.Update(gameState, events);  // Phase 3
        // VictoryManager.Update(gameState, events);  // Phase 3

        // 资源队列管理器
        Queues.Update(gameState);

        // 占领强度更新（每3秒）—— TODO: 用 gameState 时间做门控
    }

    // ── 基地状态查询 ──

    public bool HasActiveBase(GameState gameState)
        => BasesManager.HasActiveBase(gameState);

    public bool HasPotentialBase(GameState gameState)
        => BasesManager.HasPotentialBase(gameState);

    // ── 事件处理（原版 checkEvents 简化版）──

    private void CheckEvents(GameState gameState, AIEventBuffer events)
    {
        foreach (var ev in events.Events)
        {
            switch (ev.Type)
            {
                case AIEventType.TrainingFinished:
                case AIEventType.ConstructionFinished:
                case AIEventType.OwnershipChanged:
                    // BasesManager 处理这些事件（分配 role/base/access metadata）
                    // 完整版在这里更新新实体的 metadata
                    break;
                case AIEventType.Destroy:
                    // 清理死亡实体的 metadata
                    gameState.Metadata.RemoveAll(ev.Entity);
                    break;
            }
        }
    }

    // ── 建造/训练决策（骨架——完整逻辑逐步填充）──

    private void TrainMoreWorkers(GameState gameState)
    {
        // 原版 trainMoreWorkers：检查 worker 数 vs targetNumWorkers，按 supportRatio
        // 决定训练 villager 还是 citizenSoldier，加入 villager/citizenSoldier 队列。
        int numWorkers = gameState.CountOwnEntitiesByRole("worker");
        if (numWorkers >= TargetNumWorkers) return;
        // 简化：加入 villager 训练计划
        var civ = gameState.GetPlayerCiv();
        // TODO: 精确版需检查 trainer 可用性 + pop cap + supportRatio 分配
        // Queues.AddPlan("villager", new TrainingPlan(gameState, $"units/{civ}/support_civilian", ...));
    }

    private void BuildMoreHouses(GameState gameState)
    {
        // 原版 buildMoreHouses：pop 接近上限时加入 house 建设计划
        int pop = gameState.GetPopulation();
        int popLimit = gameState.GetPopulationLimit();
        if (popLimit - pop > 5) return;  // 还有空间
        // TODO: Queues.AddPlan("house", new ConstructionPlan(gameState, $"structures/{civ}/house"));
    }

    private void BuildFarmstead(GameState gameState)
    {
        // 原版 buildFarmstead：食物不足时建农场
        // TODO: 完整选址逻辑
    }

    private void ManageCorral(GameState gameState) { /* TODO */ }

    private void CheckBaseExpansion(GameState gameState)
    {
        // 原版 checkBaseExpansion：资源充足 + 人口达阈值时建新 CC
        // TODO: findCCLocation（依赖 territory map + obstruction map 的完整版）
    }

    private void CheckPhaseRequirements(GameState gameState)
    {
        // 正在升级时检查是否完成
        if (gameState.IsResearched(gameState.GetPhaseName(Phasing)))
            Phasing = 0;
    }

    private void BuildMarket(GameState gameState) { /* TODO */ }
    private void BuildForge(GameState gameState) { /* TODO */ }
    private void BuildTemple(GameState gameState) { /* TODO */ }
    private void BuildDefenses(GameState gameState) { /* TODO */ }
    private void ConstructTrainingBuildings(GameState gameState) { /* TODO */ }

    /// <summary>占领目标信息（capturableTargets 缓存项）。</summary>
    public sealed class CapturableTarget
    {
        public double Strength;
        public HashSet<uint> Ents = new();
    }
}

/// <summary>基地管理器（原版 petra/basesManager.js，809 行）。
/// 管理多个基地的生命周期：创建/销毁/资源分配。
/// 当前骨架——完整逻辑（base anchor/resource balancing）逐步填充。</summary>
public sealed class BasesManager
{
    public readonly PetraConfig Config;
    public readonly List<BaseManager> Bases = new();
    private int _nextBaseId = 1;

    public BasesManager(PetraConfig config) => Config = config;

    public void Update(GameState gameState, AIEventBuffer events)
    {
        // 清理死基地 + 更新活基地
        Bases.RemoveAll(b =>
        {
            if (b.AnchorId == null) return b.Buildings.Count == 0;
            var ent = gameState.GetEntityById(b.AnchorId.Value);
            return ent == null || ent.IsDead;
        });
        foreach (var b in Bases)
            b.Update(gameState, events);
    }

    public bool HasActiveBase(GameState gameState)
    {
        foreach (var b in Bases)
            if (b.AnchorId != null)
            {
                var ent = gameState.GetEntityById(b.AnchorId.Value);
                if (ent != null && !ent.IsDead && ent.Owner == gameState.PlayerId)
                    return true;
            }
        return Bases.Count > 0;
    }

    public bool HasPotentialBase(GameState gameState)
        => HasActiveBase(gameState);

    public BaseManager CreateBase(GameState gameState, uint anchorId)
    {
        var b = new BaseManager(gameState, this, _nextBaseId++);
        b.AnchorId = anchorId;
        Bases.Add(b);
        return b;
    }
}
