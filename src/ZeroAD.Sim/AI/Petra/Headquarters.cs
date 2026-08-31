using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

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
        BasesManager.HqResolver = _ => this;   // worker 分配的需求序回查(原版 basesManager.HQ)
        ResearchManager = new ResearchManager(config);
        AttackManager = new AttackManager(config);
        AttackManager.Hq = this;   // 原版 attackManager 经 gameState.ai.HQ 回查(getEnemyPlayer 等)
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
        var prof = ProfSw;

        // 事件处理
        long t0 = prof.ElapsedMilliseconds;
        CheckEvents(gameState, events);
        long t1 = prof.ElapsedMilliseconds;

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
        long t2 = prof.ElapsedMilliseconds;

        // 基地扩张(原版每 think 调 checkBaseExpansion,门控全在函数内——CC 全灭重建/
        // 升代暂缓/单位数超基地承载;外层不再预过滤)。
        CheckBaseExpansion(gameState);
        long t3 = prof.ElapsedMilliseconds;

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
        long t4 = prof.ElapsedMilliseconds;

        // 子管理器更新
        EmergencyManager.Update(gameState);
        long t5 = prof.ElapsedMilliseconds;
        BasesManager.Update(gameState, events);
        long t6 = prof.ElapsedMilliseconds;
        // 科技管理(原版 researchManager:CheckPhase 在 Phasing==0 时,Update 每 think)。
        if (Phasing == 0)
            ResearchManager.CheckPhase(gameState, Queues);
        ResearchManager.Update(gameState, Queues);
        long t7 = prof.ElapsedMilliseconds;
        TradeManager.Update(gameState, events, Queues);
        long t8 = prof.ElapsedMilliseconds;
        // 进攻管理(原版门控:难度 > Sandbox 且(有活基地或不可造兵))
        if (Config.Difficulty > DifficultyLevel.Sandbox && (hasActive || !CanBuildUnits))
            AttackManager.Update(gameState, Queues, events);
        long t9 = prof.ElapsedMilliseconds;
        // 守家(原版顺序:tradeManager → garrisonManager → defenseManager):
        // 先驻军避险,再调空闲兵力回防——驻军消耗 idle 池,回防取其剩余。
        if (Config.Difficulty > DifficultyLevel.Sandbox && hasActive)
        {
            GarrisonManager.Update(gameState);
            DefenseManager.Update(gameState, events);
        }
        long t10 = prof.ElapsedMilliseconds;
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
        // 贡品输送/LMS 背叛;奇迹建造/奇迹建造/弑君护主。
        DiplomacyManager.Update(gameState, events);
        VictoryManager.Update(gameState, events, Queues);
        long t11 = prof.ElapsedMilliseconds;
        // NavalManager:海图由 Accessibility 判定(见 Update),已启用码头/训船闭环。

        // 资源队列管理器
        Queues.Update(gameState);
        long t12 = prof.ElapsedMilliseconds;

        ProfEvents += t1 - t0; ProfEcon += t2 - t1; ProfExpansion += t3 - t2; ProfBuild += t4 - t3;
        ProfEmergency += t5 - t4; ProfBases += t6 - t5; ProfResearch += t7 - t6; ProfTrade += t8 - t7;
        ProfAttack += t9 - t8; ProfDefense += t10 - t9; ProfNavalDiploVictory += t11 - t10; ProfQueues += t12 - t11;

        // 占领强度更新（每3秒）—— TODO: 用 gameState 时间做门控
    }

    /// <summary>性能探针:HQ 各阶段耗时(SimBridge 聚合打印后清零)。</summary>
    public static long ProfEvents, ProfEcon, ProfExpansion, ProfBuild, ProfEmergency, ProfBases,
        ProfResearch, ProfTrade, ProfAttack, ProfDefense, ProfNavalDiploVictory, ProfQueues;
    public static readonly System.Diagnostics.Stopwatch ProfSw = System.Diagnostics.Stopwatch.StartNew();

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
        // 原版 checkBaseExpansion(headquarters.js:1522-1557)逐字条件:
        if (HasPendingPlan("civilCentre")) return;
        // 1) CC 全灭 → 立即重建首基地
        if (!HasPotentialBase(gameState))
        {
            BuildNewBase(gameState, PickExpansionResource(gameState));
            return;
        }
        // 2)(原版 buildManager.numberMissingRoom>1 即扩建——我们的 buildManager 无
        //    房间追踪,跳过该条件)
        // 3) 已计划升代 → 暂缓扩张
        if (Phasing != 0) return;
        // 4) 单位数超基地承载(侵略性性格调节阈值)或囤资源策略下 >50 → 扩张
        int activeBases = NumActiveBases(gameState);
        int numUnits = gameState.GetOwnUnits().Length;
        double numvar = 10 * (1 - Config.Personality.Aggressive);
        if (numUnits > activeBases * (65 + numvar + (10 + numvar) * (activeBases - 1))
            || SaveResources && numUnits > 50)
            BuildNewBase(gameState, PickExpansionResource(gameState));
    }

    /// <summary>活跃基地数(原版 numActiveBases:anchor 活着的基地)。</summary>
    public int NumActiveBases(GameState gameState)
    {
        int n = 0;
        foreach (var b in BasesManager.Bases)
            if (b.AnchorId != null && gameState.GetEntityById(b.AnchorId.Value) is { IsDead: false })
                n++;
        return n;
    }

    /// <summary>扩张资源选择(原版 buildNewBase 调用点按最缺资源扩张)。</summary>
    private string PickExpansionResource(GameState gameState)
    {
        var needed = PickMostNeededResources(gameState);
        return needed.Count > 0 ? needed[0].Type : "wood";
    }

    /// <summary>原版 buildNewBase(headquarters.js:1558-1595)逐字门控:
    /// phase1 且未在研 phase2 → 不扩;CC 地基/队列已有 → 不重复;
    /// 有本文明 CC 且可建军营殖民地 → 用 military_colony(原版优先——可释放特定单位/科技),
    /// 否则 civil_centre;落成 ConstructionPlan(base=-1 + resource)进 civilCentre 队列。</summary>
    public bool BuildNewBase(GameState gameState, string resource)
    {
        if (HasPotentialBase(gameState) && CurrentPhase == 1
            && !gameState.IsResearching(gameState.GetPhaseName(2)))
            return false;
        if (gameState.GetOwnFoundations().Filter(e => e.HasClass("CivCentre")).HasEntities())
            return false;
        if (HasPendingPlan("civilCentre")) return false;

        // 本文明 CC 存在性(原版:必须有 civ 自己的 CC,殖民地才放行专属内容)。
        string ownCc = gameState.ApplyCiv("structures/{civ}/civil_centre");
        bool hasOwnCC = gameState.GetOwnStructures().Values()
            .Any(e => e.HasClass("CivCentre") && e.Template.TemplateName == ownCc);

        string template;
        if (hasOwnCC && CanBuild(gameState, "structures/{civ}/military_colony"))
            template = "structures/{civ}/military_colony";
        else if (CanBuild(gameState, "structures/{civ}/civil_centre"))
            template = "structures/{civ}/civil_centre";
        else if (!hasOwnCC && CanBuild(gameState, "structures/{civ}/military_colony"))
            template = "structures/{civ}/military_colony";
        else
            return false;

        Queues.AddPlan("civilCentre",
            new ConstructionPlan(gameState, template,
                new Dictionary<string, object> { ["base"] = -1, ["resource"] = resource }));
        return true;
    }

    /// <summary>原版 findEconomicCCLocation(headquarters.js:668-868)的网格扫描移植:
    /// 领土图逐格(无主 + 可达陆区 + 可放置)评分:
    ///   val = (2×目标资源密度 + Σ其它非食物密度) × norm
    ///   norm = CC 距离门(120² 拒任何 CC / 200² 拒盟友 CC / 250² 盟友近减分;
    ///         410² 超远拒(可达)/360-reduce² 远减分/500² 不可达远减分;
    ///         我方 dropsite 60² 拒/80² 减分;地图边界减半;危险位拒)
    /// 资源密度图 = ccResourceMaps 等价物:静态 supply 按量加权放射 splat(线性衰减 24m)。
    /// bestVal < cut(60) → 放弃(返回 null)。确定性:全扫描无随机。</summary>
    public static FixedVector2D? FindEconomicCCLocation(GameState gameState, string templatePath,
        string resource, PetraConfig config)
    {
        var territory = SimSystem.Territory;
        if (territory == null || gameState.Accessibility == null) return null;
        int width = territory.GridWidth;
        const int cellSize = 4;   // TerritoryManager.CellSize

        // 模板足迹(障碍半径)。
        var template = gameState.GetTemplate(gameState.ApplyCiv(templatePath));
        if (template == null) return null;
        float halfW = template.GetFloat("Footprint/Square/@width") / 2f;
        float halfD = template.GetFloat("Footprint/Square/@depth") / 2f;
        float halfSize = Math.Max(halfW, halfD);
        if (halfSize <= 0) halfSize = template.GetFloat("Footprint/Circle/@radius");
        if (halfSize <= 0) halfSize = 8f;

        // 资源密度图(food 除外项按原版只作背景加分;目标资源双倍权重)。
        var resMaps = BuildCcResourceMaps(gameState, width, cellSize, resource);

        // CC / dropsite 清单(原版 allCCs + own non-CC dropsites)。
        var ccList = gameState.GetStructures().Values()
            .Where(e => e.HasClass("CivCentre") && e.Position2D != default)
            .Select(e => (Pos: e.Position2D, Ally: gameState.IsPlayerAlly(e.Owner),
                Access: gameState.Accessibility.GetAccessValue(
                    e.Position2D.X.ToFloat(), e.Position2D.Y.ToFloat())))
            .ToList();
        var dpList = gameState.GetOwnStructures().Values()
            .Where(e => !string.IsNullOrEmpty(e.Template.ResourceDropsiteTypes)
                && !e.HasClass("CivCentre") && e.Position2D != default)
            .Select(e => e.Position2D)
            .ToList();

        double reduce = (template.HasClass("Colony") ? 30 : 0) + 30 * config.Personality.Defensive;
        float nearbyRejected = 120 * 120;
        float nearbyAllyRejected = 200 * 200;
        float nearbyAllyDisfavored = 250 * 250;
        float maxAccessRejected = 410 * 410;
        float maxAccessDisfavored = (360 - (float)reduce) * (360 - (float)reduce);
        float maxNoAccessDisfavored = 500 * 500;
        double cut = 60;

        int bestIdx = -1;
        double bestVal = double.MinValue;
        var pathfinder = SimSystem.Pathfinder;
        for (int j = 0; j < width * width; j++)
        {
            if (territory.OwnerGrid[j] != 0) continue;   // 只在无主领土扩张
            float px = cellSize * (j % width + 0.5f);
            float pz = cellSize * (j / width + 0.5f);
            // 可达陆区(regionSize>0;0 = 不可达/水域)
            ushort region = gameState.Accessibility.GetAccessValue(px, pz);
            if (gameState.Accessibility.GetRegionSize(region, false) <= 0) continue;

            double norm = 0.5;
            // CC 距离门
            float minDist = float.MaxValue;
            bool accessible = false;
            bool reject = false;
            foreach (var cc in ccList)
            {
                float dx = cc.Pos.X.ToFloat() - px, dz = cc.Pos.Y.ToFloat() - pz;
                float dist = dx * dx + dz * dz;
                if (dist < nearbyRejected) { reject = true; break; }
                if (cc.Ally)
                {
                    if (dist < nearbyAllyRejected) { reject = true; break; }
                    if (dist < nearbyAllyDisfavored) norm *= 0.5;
                    if (dist < minDist) minDist = dist;
                    if (cc.Access == region) accessible = true;
                }
            }
            if (reject) continue;
            if (ccList.Count > 0)
            {
                if (accessible && minDist > maxAccessRejected) continue;
                if (minDist > maxAccessDisfavored)
                {
                    if (!accessible)
                        norm *= minDist > maxNoAccessDisfavored ? 0.5 : 0.8;
                    else
                        norm *= 0.5;
                }
            }
            // dropsite 邻近排斥(原版 3600 拒/6400 减分)。
            foreach (var dp in dpList)
            {
                float dx = dp.X.ToFloat() - px, dz = dp.Y.ToFloat() - pz;
                float dist = dx * dx + dz * dz;
                if (dist < 3600) { reject = true; break; }
                if (dist < 6400) norm *= 0.5;
            }
            if (reject) continue;

            // 地图边界减分(borderMap 简化:离世界缘 <2 格)。
            if (j % width < 2 || j / width < 2 || j % width >= width - 2 || j / width >= width - 2)
                norm *= 0.5;

            double val = 2 * resMaps.Target[j];
            foreach (var kv in resMaps.Others) val += kv.Value[j];
            val *= norm;
            if (val <= bestVal) continue;
            // 危险位拒(敌防御半径内)。
            if (headquartersDanger(gameState, px, pz, halfSize)) continue;
            // 可放置校验(真实障碍+地形;SimSystem.Pathfinder 即原版 obstruction 图等价)。
            if (pathfinder != null)
            {
                var pr = pathfinder.CheckBuildingPlacement(
                    Fixed.FromFloat(px), Fixed.FromFloat(pz),
                    Fixed.FromFloat(halfW), Fixed.FromFloat(halfD));
                if (pr != PlacementResult.Success) continue;
            }
            bestVal = val;
            bestIdx = j;
        }
        if (bestIdx < 0 || bestVal < cut) return null;
        return new FixedVector2D(
            Fixed.FromFloat(cellSize * (bestIdx % width + 0.5f)),
            Fixed.FromFloat(cellSize * (bestIdx / width + 0.5f)));
    }

    /// <summary>危险位判定的静态壳(IsDangerousLocation 的静态版——本方法不进 HQ 实例)。</summary>
    private static bool headquartersDanger(GameState gameState, float px, float pz, float halfSize)
    {
        float radius = halfSize + 40f;   // 原版 isDangerousLocation 的半径近似
        foreach (var e in gameState.GetEnemyStructures().Values())
        {
            if (!e.HasDefensiveFire || e.Position2D == default) continue;
            float dx = e.Position2D.X.ToFloat() - px;
            float dz = e.Position2D.Y.ToFloat() - pz;
            if (dx * dx + dz * dz < radius * radius) return true;
        }
        return false;
    }

    /// <summary>ccResourceMaps 等价物:静态 supply(剔除 Animal/Field/枯竭)按
    /// min(amount,1000)/1000 权重,24m 半径线性衰减 splat 进领土格。目标资源单列,
    /// 其余(wood/stone/metal)合并(food 原版明确不计)。</summary>
    private static (float[] Target, Dictionary<string, float[]> Others) BuildCcResourceMaps(
        GameState gameState, int width, int cellSize, string targetResource)
    {
        var target = new float[width * width];
        var others = new Dictionary<string, float[]>
        { ["wood"] = new float[width * width], ["stone"] = new float[width * width],
          ["metal"] = new float[width * width] };
        const float radius = 24f;
        int rCells = (int)(radius / cellSize) + 1;
        foreach (var type in new[] { "food", "wood", "stone", "metal" })
        {
            if (type == "food") continue;   // 原版 CC 选址不计食物(田随基地建)
            foreach (var supply in gameState.GetResourceSupplies(type).Values())
            {
                if (supply.Position2D == default) continue;
                if (supply.HasClass("Animal") || supply.HasClass("Field")) continue;
                var comp = gameState.Cm.QueryInterface<ResourceSupply>(supply.Entity);
                if (comp == null || comp.Amount <= 0) continue;
                float weight = Math.Min(comp.Amount, 1000) / 1000f;
                float sx = supply.Position2D.X.ToFloat(), sz = supply.Position2D.Y.ToFloat();
                int cx = (int)(sx / cellSize), cz = (int)(sz / cellSize);
                var map = type == targetResource ? target : others[type];
                for (int dz = -rCells; dz <= rCells; dz++)
                    for (int dx = -rCells; dx <= rCells; dx++)
                    {
                        int gx = cx + dx, gz = cz + dz;
                        if (gx < 0 || gz < 0 || gx >= width || gz >= width) continue;
                        float d = (float)Math.Sqrt((dx * dx + dz * dz) * cellSize * cellSize);
                        if (d > radius) continue;
                        map[gz * width + gx] += weight * (1f - d / radius);
                    }
            }
        }
        return (target, others);
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

    // ── 采集速率编排(原版 headquarters.js:616-668;worker 分配的资源需求驱动)──

    /// <summary>期望采集速率(原版 GetWantedGatherRates,带 turnCache)。</summary>
    public Dictionary<string, double> GetWantedGatherRates(GameState gameState)
    {
        if (_turnCache.TryGetValue("wantedRates", out var cached))
            return (Dictionary<string, double>)cached;
        var rates = Queues.WantedGatherRates(gameState);
        _turnCache["wantedRates"] = rates;
        return rates;
    }

    /// <summary>当前采集速率(原版 GetCurrentGatherRates → 各 base addGatherRates 之和):
    /// 按 worker 的 gather-type 元数据,累加其模板该 generic 类的最高 subtype 速率。</summary>
    public Dictionary<string, double> GetCurrentGatherRates(GameState gameState)
    {
        var rates = new Dictionary<string, double>
        { ["wood"] = 0, ["food"] = 0, ["stone"] = 0, ["metal"] = 0 };
        foreach (var ent in gameState.GetOwnUnits().Values())
        {
            var role = gameState.Metadata.GetObject(ent.Id, "role")?.ToString();
            if (role != WorkerRoles.RoleWorker) continue;
            var subrole = gameState.Metadata.GetObject(ent.Id, "subrole")?.ToString();
            if (subrole != WorkerRoles.SubroleGatherer && subrole != WorkerRoles.SubroleHunter
                && subrole != WorkerRoles.SubroleFisher) continue;
            var type = gameState.Metadata.GetObject(ent.Id, "gather-type")?.ToString();
            if (type == null || !rates.ContainsKey(type)) continue;
            // 该 generic 类的最高 subtype 速率(原版 gatherRates[supplyType] 的近似——
            // 实际采集的 subtype 在 supply 元数据里,取 max 高估不多)。
            double best = 0;
            foreach (var kv in ent.Template.ResourceGatherRates())
                if (kv.Key.StartsWith(type + ".", StringComparison.Ordinal) && kv.Value > best)
                    best = kv.Value;
            rates[type] += best;
        }
        return rates;
    }

    /// <summary>最缺资源排序(原版 pickMostNeededResources 逐字移植):
    /// wanted vs current 采集速率比排序;wanted=0 且 current=0 的沉底。</summary>
    public List<(string Type, double Wanted, double Current)> PickMostNeededResources(
        GameState gameState, IReadOnlyList<string>? allowedResources = null)
    {
        var wanted = GetWantedGatherRates(gameState);
        var current = GetCurrentGatherRates(gameState);
        var allowed = allowedResources is { Count: > 0 }
            ? allowedResources : new List<string> { "wood", "food", "stone", "metal" };

        var needed = allowed
            .Select(r => (Type: r, Wanted: wanted.GetValueOrDefault(r), Current: current.GetValueOrDefault(r)))
            .ToList();
        needed.Sort((a, b) =>
        {
            if (a.Current < a.Wanted && b.Current < b.Wanted)
            {
                if (a.Current > 0 && b.Current > 0)
                    return (b.Wanted / b.Current).CompareTo(a.Wanted / a.Current);
                if (a.Current > 0) return 1;
                if (b.Current > 0) return -1;
                return b.Wanted.CompareTo(a.Wanted);
            }
            if (a.Current < a.Wanted || a.Wanted > 0 && b.Wanted == 0) return -1;
            if (b.Current < b.Wanted || b.Wanted > 0 && a.Wanted == 0) return 1;
            return (a.Current - a.Wanted - b.Current + b.Wanted).CompareTo(0.0);
        });
        return needed;
    }

    /// <summary>可建模板(原版 canBuild 简化:模板存在且可建造;科技门由 CanStart 兜底)。</summary>
    public bool CanBuild(GameState gameState, string template)
    {
        string resolved = gameState.ApplyCiv(template);
        return gameState.Templates.TemplateExists(resolved)
            && gameState.FindBuilder(resolved).HasEntities();
    }

    // ── 最优可训单位(原版 findBestTrainableUnit;headquarters.js:519-582)──

    /// <summary>按类过滤可训模板,interest 加权评分取最高(value/cost 性价比):
    /// strength = 攻击强度(dps 加权和);siegeStrength = 对 Structure(Bonuses 乘子);
    /// speed = 行速;costsResource = 含该资源成本则 ×weight(稀缺加重);
    /// canGather = 有采集能力则 ×weight。类含 Hero 不排除;否则排除 Hero+SiegeTower。</summary>
    public static string? FindBestTrainableUnit(GameState gameState, string[] classes,
        (string Interest, double Weight)[] interests)
    {
        string clsStr = string.Join(' ', classes);
        bool hero = classes.Contains("Hero");
        var units = gameState.FindTrainableUnits(clsStr, hero ? "" : "Hero SiegeTower");
        if (units.Count == 0) return null;

        // 动态 costsResource 惩罚(原版:领土内剩余 <800 且现有 <800 时加重)。
        var remaining = GetTotalResourceLevel(gameState);
        var available = gameState.GetResources();
        var parameters = interests
            .Select(i => (i.Interest, i.Weight, (string?)null)).ToList();
        foreach (var type in new[] { "wood", "food", "stone", "metal" })
        {
            int avail = ResOf(available, type);
            if (avail > 800) continue;
            if (remaining.GetValueOrDefault(type) > 800) continue;
            double costWeight = remaining.GetValueOrDefault(type) > 400 ? 0.6 : 0.2;
            // 原版 Rush/Attack 槽自带 costsResource 项(无资源名 = 全类);此处加资源名维度。
            int idx = parameters.FindIndex(x => x.Item1 == "costsResource");
            if (idx >= 0)
                parameters[idx] = ("costsResource", Math.Min(parameters[idx].Item2, costWeight), type);
            else
                parameters.Add(("costsResource", costWeight, type));
        }

        units.Sort((a, b) =>
        {
            double aValue = ScoreUnit(a.def, parameters);
            double bValue = ScoreUnit(b.def, parameters);
            int aCost = 1 + a.def.CostWood + a.def.CostFood + a.def.CostStone + a.def.CostMetal;
            int bCost = 1 + b.def.CostWood + b.def.CostFood + b.def.CostStone + b.def.CostMetal;
            return (-aValue / aCost).CompareTo(-bValue / bCost);   // 性价比降序
        });
        return units[0].template;
    }

    private static double ScoreUnit(AITemplate t,
        List<(string Interest, double Weight, string? Resource)> parameters)
    {
        double value = 0.1;
        foreach (var p in parameters)
        {
            switch (p.Interest)
            {
                case "strength":
                    value += GetMaxStrength(t, null) * p.Weight;
                    break;
                case "siegeStrength":
                    value += GetMaxStrength(t, "Structure") * p.Weight;
                    break;
                case "speed":
                    value += t.GetFloat("UnitMotion/Speed") * p.Weight;
                    break;
                case "costsResource":
                    if (p.Resource != null && ResOf(t, p.Resource) > 0)
                        value *= p.Weight;
                    break;
                case "canGather":
                    // 原版查 wood.tree(任意采集能力的代理)。
                    if (t.ResourceGatherRates().ContainsKey("wood.tree"))
                        value *= p.Weight;
                    break;
            }
        }
        return value;
    }

    /// <summary>原版 getMaxStrength(petra/entityExtend.js:17-70)移植:
    /// 每攻击类型(跳 Slaughter):伤害 × 类型权重均值 + 射程×0.0125
    /// + repeat/100000 - prepare/100000;againstClass 走 Bonuses 乘子(无匹配按 1)。</summary>
    private static double GetMaxStrength(AITemplate t, string? againstClass)
    {
        double strength = 0;
        foreach (var type in new[] { "Melee", "Ranged", "Capture", "Charge" })
        {
            bool any = false;
            int dmgCount = 0;
            double dmgSum = 0;
            foreach (var dmg in new[] { "Hack", "Pierce", "Crush", "Fire" })
            {
                float v = t.GetFloat($"Attack/{type}/Damage/{dmg}");
                if (v <= 0) continue;
                any = true;
                if (againstClass != null)
                    v *= (float)GetMultiplierAgainst(t, type, againstClass);
                dmgSum += v;
                dmgCount++;
            }
            if (!any) continue;
            // 原版:各伤害型 × importance / 类型数(默认等权 → 均值)。
            strength += dmgSum / Math.Max(dmgCount, 1);
            strength += t.GetFloat($"Attack/{type}/MaxRange") * 0.0125;
            strength += t.GetFloat($"Attack/{type}/RepeatTime") / 100000;
            strength -= t.GetFloat($"Attack/{type}/PrepareTime") / 100000;
        }
        return strength;
    }

    /// <summary>攻击加成乘子(原版 getMultiplierAgainst:Attack/{type}/Bonuses 下
    /// Classes 含目标类的项取乘)。XML 缺失/无匹配 → 1。</summary>
    private static double GetMultiplierAgainst(AITemplate t, string attackType, string againstClass)
    {
        var bonuses = t.Node.GetChild("Attack").GetChild(attackType).GetChild("Bonuses");
        if (!bonuses.IsOk) return 1;
        double mult = 1;
        foreach (var (name, bonus) in bonuses.Children)
        {
            if (name.StartsWith('@')) continue;
            var cls = bonus.GetChild("Classes");
            if (!cls.IsOk || !cls.Value.Contains(againstClass)) continue;
            var m = bonus.GetChild("Multiplier");
            if (m.IsOk) mult *= m.ToFixed().ToFloat();
        }
        return mult;
    }

    /// <summary>全图剩余资源估值(原版 getTotalResourceLevel 简化:全部静态 supply 余量求和)。</summary>
    public static Dictionary<string, double> GetTotalResourceLevel(GameState gameState)
    {
        var totals = new Dictionary<string, double>
        { ["wood"] = 0, ["food"] = 0, ["stone"] = 0, ["metal"] = 0 };
        foreach (var type in new[] { "wood", "food", "stone", "metal" })
            foreach (var s in gameState.GetResourceSupplies(type).Values())
            {
                var comp = gameState.Cm.QueryInterface<ResourceSupply>(s.Entity);
                if (comp != null) totals[type] += comp.Amount;
            }
        return totals;
    }

    private static int ResOf(ResourcesManager r, string type) => type switch
    { "wood" => r.Wood, "food" => r.Food, "stone" => r.Stone, _ => r.Metal };

    private static int ResOf(AITemplate t, string type) => type switch
    {
        "wood" => t.CostWood, "food" => t.CostFood, "stone" => t.CostStone, _ => t.CostMetal,
    };

    /// <summary>危险位置判定(原版 isDangerousLocation 简化版:半径内有敌防御火力建筑)。</summary>
    public bool IsDangerousLocation(GameState gameState, FixedVector2D pos, float radius)
    {
        foreach (var e in gameState.GetEnemyStructures().Values())
        {
            if (!e.HasDefensiveFire || e.Position2D == default) continue;
            float dx = e.Position2D.X.ToFloat() - pos.X.ToFloat();
            float dz = e.Position2D.Y.ToFloat() - pos.Y.ToFloat();
            if (dx * dx + dz * dz < radius * radius) return true;
        }
        return false;
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

    /// <summary>基地 → HQ 反链(原版 base.basesManager.HQ;worker 分配要查
    /// pickMostNeededResources/lastFailedGather)。HQ 构造时注入。</summary>
    public Func<GameState, Headquarters?>? HqResolver;

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
