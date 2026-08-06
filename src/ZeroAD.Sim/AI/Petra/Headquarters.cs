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
    public ResearchManager ResearchManager;
    public AttackManager AttackManager;
    public TradeManager TradeManager;
    public EmergencyManager EmergencyManager;
    public DefenseManager DefenseManager;
    public GarrisonManager GarrisonManager;
    public NavalManager NavalManager;
    public DiplomacyManager DiplomacyManager;
    public VictoryManager VictoryManager;

    /// <summary>海图标记(原版 HQ.navalMap):首个水域区域 ≥ NavalMapMinWaterCells
    /// 即当海图运营(建码头/训船)。首次 Update 时从 Accessibility 计算,只增不减。</summary>
    public bool NavalMap { get; private set; }
    private bool _navalMapComputed;
    /// <summary>海图水域阈值(navcell 数;64×64 地图全水约 4096 格,取 200 =
    /// 一块像样的湖/海,小水洼不算)。</summary>
    private const int NavalMapMinWaterCells = 200;

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
        ResearchManager = new ResearchManager(config);
        AttackManager = new AttackManager(config);
        TradeManager = new TradeManager(config);
        EmergencyManager = new EmergencyManager(config);
        DefenseManager = new DefenseManager(config);
        GarrisonManager = new GarrisonManager(config);
        NavalManager = new NavalManager(config);
        DiplomacyManager = new DiplomacyManager(config);
        VictoryManager = new VictoryManager(config);
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

        // 核心经济循环（每4回合轮替）——回合号取 NetTurnManager.CurrentTurn(原版
        // playedTurn);Net 缺失的测试环境回落事件计数。
        bool hasActive = HasActiveBase(gameState);
        uint turn = gameState.Net?.CurrentTurn ?? (uint)gameState.Events.Events.Count;
        if (hasActive)
        {
            int turnMod = (int)(turn % 4);
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
        if (turn % 3 == 0)
        {
            ConstructTrainingBuildings(gameState);
            if (Config.Difficulty > DifficultyLevel.Sandbox) BuildDefenses(gameState);
        }

        // 子管理器更新
        EmergencyManager.Update(gameState);
        BasesManager.Update(gameState, events);
        // 科技管理(原版 researchManager:CheckPhase 在 Phasing==0 时,Update 每 think)。
        if (Phasing == 0)
            ResearchManager.CheckPhase(gameState, Queues);
        ResearchManager.Update(gameState, Queues);
        TradeManager.Update(gameState, events, Queues);
        // 进攻管理(原版门控:难度 > Sandbox 且(有活基地或不可造兵))
        if (Config.Difficulty > DifficultyLevel.Sandbox && (hasActive || !CanBuildUnits))
            AttackManager.Update(gameState, Queues, events);
        // 守家(原版顺序:tradeManager → garrisonManager → defenseManager):
        // 先驻军避险,再调空闲兵力回防——驻军消耗 idle 池,回防取其剩余。
        if (Config.Difficulty > DifficultyLevel.Sandbox && hasActive)
        {
            GarrisonManager.Update(gameState);
            DefenseManager.Update(gameState, events);
        }
        // 海军(原版 navalManager.update 门控:navalMap):首 Update 从 Accessibility
        // 判定海图(有 ≥200 格水域区域),海图才运营码头/船。
        if (!_navalMapComputed)
        {
            _navalMapComputed = true;
            NavalMap = (gameState.Accessibility?.LargestWaterRegionSize() ?? 0) >= NavalMapMinWaterCells;
        }
        if (NavalMap && hasActive)
            NavalManager.Update(gameState, Queues, events);
        // 外交/胜利(原版顺序压轴:diplomacyManager → victoryManager):
        // 贡品输送/LMS 背叛;奇迹建造/弑君护主。
        DiplomacyManager.Update(gameState, events);
        VictoryManager.Update(gameState, events, Queues);
        // NavalManager:海图由 Accessibility 判定(见 Update),已启用码头/训船闭环。

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

    // ── 建造/训练决策（原版 headquarters.js 同名方法,简化选址/门控版）──

    /// <summary>队列是否已有未启动计划(防每回合重复加单;原版查 queue 计数)。</summary>
    private bool HasPendingPlan(string queueName)
        => Queues.GetQueue(queueName)?.HasQueuedUnits == true;

    /// <summary>自有建筑按 class 计数。</summary>
    private static int CountOwnStructuresByClass(GameState gameState, string className)
        => gameState.GetOwnStructures().Filter(e => e.HasClass(className)).Length;

    private void TrainMoreWorkers(GameState gameState)
    {
        // 原版 trainMoreWorkers:worker 数未达 targetNumWorkers 时补训练;
        // 已排队的计入(防超训),villager 队列挂 support_civilian。
        int numWorkers = gameState.CountOwnEntitiesByRole("worker");
        if (numWorkers >= TargetNumWorkers) return;
        if (HasPendingPlan("villager")) return;
        var plan = new TrainingPlan(gameState, "units/{civ}/support_civilian",
            number: System.Math.Min(2, TargetNumWorkers - numWorkers));
        Queues.AddPlan("villager", plan);
    }

    private void BuildMoreHouses(GameState gameState)
    {
        // 原版 buildMoreHouses:剩余床位逼近缓冲(原版按 pop 规模 5-20)即建房;
        // house 队列非空时不重复加。
        int freeBeds = gameState.GetPopulationLimit() - gameState.GetPopulation();
        if (freeBeds > 8) return;
        if (HasPendingPlan("house")) return;
        Queues.AddPlan("house", new ConstructionPlan(gameState, "structures/{civ}/house"));
    }

    private void BuildFarmstead(GameState gameState)
    {
        // 原版 buildFarmstead:食物产能不足时建农场。简化门控:农场数 <
        // max(1, workers/5) 且(首农场必建或食物库存 < 400),队列不重复。
        int numWorkers = gameState.CountOwnEntitiesByRole("worker");
        int farms = CountOwnStructuresByClass(gameState, "Farmstead");
        int farmsWanted = System.Math.Max(1, numWorkers / 5);
        if (farms >= farmsWanted) return;
        if (farms >= 1 && gameState.GetResources().Food > 400) return;
        if (HasPendingPlan("field")) return;
        Queues.AddPlan("field", new ConstructionPlan(gameState, "structures/{civ}/farmstead"));
    }

    private void ManageCorral(GameState gameState)
    {
        // 原版 manageCorral:需要畜牧时建畜栏。门控:无畜栏且队列不重复。
        if (CountOwnStructuresByClass(gameState, "Corral") >= 1) return;
        if (HasPendingPlan("corral")) return;
        Queues.AddPlan("corral", new ConstructionPlan(gameState, "structures/{civ}/corral"));
    }

    private void CheckBaseExpansion(GameState gameState)
    {
        // 原版 checkBaseExpansion:town+ 且 CanExpand 时扩建新 CC(完整版用
        // findCCLocation 沿领土边界选址;简化版复用通用选址)。
        if (!CanExpand || CurrentPhase < 2) return;
        if (HasPendingPlan("economicBuilding")) return;
        int ccs = CountOwnStructuresByClass(gameState, "CivCentre");
        int ccsWanted = Config.Difficulty >= DifficultyLevel.Hard ? 3 : 2;
        if (ccs >= ccsWanted) return;
        Queues.AddPlan("economicBuilding",
            new ConstructionPlan(gameState, "structures/{civ}/civil_centre"));
    }

    private void CheckPhaseRequirements(GameState gameState)
    {
        // 正在升级时检查是否完成(科技研究本体由 ResearchManager(2.7)负责)
        if (gameState.IsResearched(gameState.GetPhaseName(Phasing)))
            Phasing = 0;
    }

    private void BuildMarket(GameState gameState)
    {
        // 原版:不能以物易物(CanBarter=false)时建市场换汇。
        if (CountOwnStructuresByClass(gameState, "Market") >= 1) return;
        if (HasPendingPlan("economicBuilding")) return;
        Queues.AddPlan("economicBuilding",
            new ConstructionPlan(gameState, "structures/{civ}/market"));
    }

    private void BuildForge(GameState gameState)
    {
        // 原版:建铁匠铺解锁攻防科技。
        if (CountOwnStructuresByClass(gameState, "Forge") >= 1) return;
        if (HasPendingPlan("economicBuilding")) return;
        Queues.AddPlan("economicBuilding",
            new ConstructionPlan(gameState, "structures/{civ}/forge"));
    }

    private void BuildTemple(GameState gameState)
    {
        // 原版:建神庙(治疗/宗教科技)。
        if (CountOwnStructuresByClass(gameState, "Temple") >= 1) return;
        if (HasPendingPlan("economicBuilding")) return;
        Queues.AddPlan("economicBuilding",
            new ConstructionPlan(gameState, "structures/{civ}/temple"));
    }

    private void BuildDefenses(GameState gameState)
    {
        // 原版:按 ExtraTowers(难度×防御性格)补防御塔。
        int towers = CountOwnStructuresByClass(gameState, "DefenseTower");
        if (towers >= 2 + ExtraTowers) return;
        if (HasPendingPlan("defenseBuilding")) return;
        Queues.AddPlan("defenseBuilding",
            new ConstructionPlan(gameState, "structures/{civ}/defense_tower"));
    }

    private void ConstructTrainingBuildings(GameState gameState)
    {
        // 原版:保证每基地至少 1 兵营。
        int barracks = CountOwnStructuresByClass(gameState, "Barracks");
        int bases = System.Math.Max(1, CountOwnStructuresByClass(gameState, "CivCentre"));
        if (barracks >= bases) return;
        if (HasPendingPlan("militaryBuilding")) return;
        Queues.AddPlan("militaryBuilding",
            new ConstructionPlan(gameState, "structures/{civ}/barracks"));
    }

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
