using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>外交管理器（原版 petra/diplomacyManager.js，589 行）。
/// 管理贡品、外交请求、背叛逻辑（最后一人站立模式）。
/// 本版:tributes 贡品闭环移植(每 30s:盈余 >200 且盟友 <20% → 送 0.3×差值;
/// 盟友濒危 → 直送 100),LMS 背叛(只剩两家盟友 → 反目);外交请求(chat.requestTribute)
/// 属聊天体系,不移植。</summary>
public sealed class DiplomacyManager
{
    private readonly PetraConfig _config;
    /// <summary>下次贡品回合(原版 nextTributeUpdate;回合 = 0.1s,300 回合 = 30s)。</summary>
    public uint NextTributeTurn = 900;   // 原版初始 90s
    /// <summary>贡品请求冷却(原版 nextTributeRequest:全局 90s / 单资源 240s;秒)。</summary>
    private readonly Dictionary<string, double> _nextTributeRequest = new();
    /// <summary>等待中的我方请求(原版 receivedDiplomacyRequests 的我方侧:
    /// 请求目标玩家 → 资源;盟友 TributeExchanged 到账即清)。</summary>
    private readonly Dictionary<int, string> _pendingRequests = new();
    public bool WaitingToBetray;
    /// <summary>背叛到的回合(原版 betrayLapseTime)。</summary>
    public uint BetrayAtTurn;

    /// <summary>HQ 反链(原版经 gameState.ai.HQ 回查 pickMostNeededResources)。</summary>
    public Headquarters? Hq;

    public DiplomacyManager(PetraConfig config, bool deserialized = false) => _config = config;

    /// <summary>主更新（原版 diplomacyManager.js:542-568:tributes + 背叛检查）。</summary>
    public void Update(GameState gameState, AIEventBuffer events)
    {
        CheckEvents(gameState, events);
        uint turn = gameState.Net?.CurrentTurn ?? 0;
        if (turn >= NextTributeTurn)
        {
            NextTributeTurn = turn + 300;   // 30s
            Tributes(gameState);
        }
        CheckBetrayal(gameState, turn);
    }

    /// <summary>贡品(原版 tributes):对每个未败盟友,donor(同盟共胜局或盟友实体
    /// 比我方少)时按盈余输送;两种口径:常规(>200 盈余、盟友 <20% → 0.3×我方-盟友)
    /// 与濒危救济(盟友人口 < min(30, 我方一半) 且我方 >500 且盟友 <100 → 直送 100)。</summary>
    private void Tributes(GameState gameState)
    {
        var cm = gameState.Cm;
        var us = cm.GetPlayerEntity(gameState.PlayerId);
        if (us == null) return;
        int ourPop = us.PopUsed;

        foreach (int allyId in gameState.GetAllies())
        {
            var allyPlayer = cm.GetPlayerEntity(allyId);
            if (allyPlayer == null || !allyPlayer.IsActive()) continue;
            bool donor = cm.EndGame.AlliedVictory
                || CountEntities(cm, allyId) < CountEntities(cm, gameState.PlayerId);
            if (!donor) continue;

            Send(gameState, allyId, ResourceType.Food, us.Food, allyPlayer.Food, allyPlayer.PopUsed, ourPop);
            Send(gameState, allyId, ResourceType.Wood, us.Wood, allyPlayer.Wood, allyPlayer.PopUsed, ourPop);
            Send(gameState, allyId, ResourceType.Stone, us.Stone, allyPlayer.Stone, allyPlayer.PopUsed, ourPop);
            Send(gameState, allyId, ResourceType.Metal, us.Metal, allyPlayer.Metal, allyPlayer.PopUsed, ourPop);
            // 请求-应答(原版 tributes 尾段):我方 0 库存 + 盟友盈余 >600 超出我方总量
            // + 该资源在最缺之列 → 发请求(90s 全局/240s 单资源冷却;盟友 AI 下拍应答)。
            RequestIfStarving(gameState, allyId, allyPlayer, us);
        }
    }

    /// <summary>原版:availableResources==0 && ally > total+600 && 最缺 → 请求贡品。</summary>
    private void RequestIfStarving(GameState gameState, int allyId,
        PlayerComponent allyPlayer, PlayerComponent us)
    {
        double now = gameState.ElapsedTime;
        if (_nextTributeRequest.TryGetValue("all", out double allCd) && now < allCd) return;
        foreach (var (type, ours, theirs) in new[]
        {
            (ResourceType.Food, us.Food, allyPlayer.Food),
            (ResourceType.Wood, us.Wood, allyPlayer.Wood),
            (ResourceType.Stone, us.Stone, allyPlayer.Stone),
            (ResourceType.Metal, us.Metal, allyPlayer.Metal),
        })
        {
            if (ours != 0) continue;
            int total = ours + theirs;
            if (theirs <= total + 600) continue;
            string res = type.ToString().ToLowerInvariant();
            if (_nextTributeRequest.TryGetValue(res, out double cd) && now < cd) continue;
            // 最缺校验(原版 pickMostNeededResources 循环)。
            var needed = Hq?.PickMostNeededResources(gameState);
            if (needed == null || !needed.Any(n => n.Type == res && n.Wanted > 0)) continue;
            _nextTributeRequest["all"] = now + 90;
            _nextTributeRequest[res] = now + 240;
            _pendingRequests[allyId] = res;
            // sim 内请求(替代原版 chat.requestTribute——AI 间同进程,事件即达,
            // 锁步确定):盟友 DiplomacyManager 应答。
            gameState.Cm.Events.RaiseTributeRequested(new Events.TributeRequestedEvent
            { FromPlayer = gameState.PlayerId, ToPlayer = allyId, ResourceType = res });
            return;
        }
    }

    /// <summary>应答(原版 answerRequestTribute 语义):盟友求援 → 我方有盈余
    /// (>500 且对方 <100)即按 0.3×差值直送(不等下个 30s 贡品拍)。</summary>
    public void AnswerTributeRequest(GameState gameState, int fromPlayer, string resource)
    {
        if (!gameState.IsPlayerMutualAlly(fromPlayer)) return;
        var cm = gameState.Cm;
        var us = cm.GetPlayerEntity(gameState.PlayerId);
        var them = cm.GetPlayerEntity(fromPlayer);
        if (us == null || them == null || !them.IsActive()) return;
        var type = resource switch
        {
            "food" => ResourceType.Food, "wood" => ResourceType.Wood,
            "stone" => ResourceType.Stone, _ => ResourceType.Metal,
        };
        int ours = Res(us, type), theirs = Res(them, type);
        if (ours <= 500 || theirs >= 100) return;
        int amount = (int)(0.3 * (ours - theirs));
        if (amount <= 0) return;
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Tribute(
            (uint)gameState.PlayerId, fromPlayer, type, amount));
    }

    private static int Res(PlayerComponent p, ResourceType t) => t switch
    {
        ResourceType.Food => p.Food, ResourceType.Wood => p.Wood,
        ResourceType.Stone => p.Stone, _ => p.Metal,
    };

    private static void Send(GameState gameState, int allyId,
        ResourceType type, int ours, int theirs, int allyPop, int ourPop)
    {
        int amount = 0;
        if (ours > 200 && theirs < 0.2f * ours)
            amount = (int)(0.3f * ours) - theirs;                 // 常规输送
        else if (allyPop < System.Math.Min(30, 0.5f * ourPop) && ours > 500 && theirs < 100)
            amount = 100;                                          // 濒危救济
        if (amount <= 0) return;
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Tribute(
            (uint)gameState.PlayerId, allyId, type, amount));
    }

    private static int CountEntities(ComponentManager cm, int playerId)
        => SimSystem.Range?.GetEntitiesByPlayer(playerId).Count ?? 0;

    /// <summary>LMS 背叛(原版 betrayal 逻辑简化):LastManStanding(非同盟共胜)局,
    /// 全场只剩我们和盟友两家活跃 → 经过短暂延迟后反目(互设敌对),否则无法收官。</summary>
    private void CheckBetrayal(GameState gameState, uint turn)
    {
        var cm = gameState.Cm;
        if (cm.EndGame.AlliedVictory) return;
        var actives = new List<int>();
        foreach (int pid in cm.Players.GetNonGaiaPlayerIds())
        {
            var p = cm.Players.GetPlayerEntity(pid);
            if (p != null && p.IsActive()) actives.Add(pid);
        }
        if (actives.Count == 2 && actives.Contains(gameState.PlayerId)
            && gameState.IsPlayerMutualAlly(actives[0] == gameState.PlayerId ? actives[1] : actives[0]))
        {
            if (!WaitingToBetray)
            {
                WaitingToBetray = true;
                BetrayAtTurn = turn + 100;   // 10s 缓冲(原版 lapse)
            }
            if (turn >= BetrayAtTurn)
            {
                int other = actives[0] == gameState.PlayerId ? actives[1] : actives[0];
                gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.SetStance(
                    (uint)gameState.PlayerId, other, DiplomacyComponent.Enemy));
                WaitingToBetray = false;
            }
        }
        else
        {
            WaitingToBetray = false;
        }
    }

    /// <summary>事件订阅(原版 checkEvents + TributeExchanged 应答追踪;
    /// 挂 sim 事件总线——请求/到账是即时事件,非 think 缓冲)。</summary>
    public void Attach(GameState gameState)
    {
        gameState.Cm.Events.TributeRequested += e =>
        {
            if (e.ToPlayer == gameState.PlayerId) AnswerTributeRequest(gameState, e.FromPlayer, e.ResourceType);
        };
        gameState.Cm.Events.Tribute += e =>
        {
            // 到账清挂起(原版:waitingForTribute → wanted 递减,归零即清)。
            if (e.ToPlayerId == gameState.PlayerId
                && _pendingRequests.TryGetValue(e.FromPlayerId, out var res)
                && res == e.Type.ToString().ToLowerInvariant())
                _pendingRequests.Remove(e.FromPlayerId);
        };
    }

    private void CheckEvents(GameState gameState, AIEventBuffer events)
    {
        foreach (var ev in events.Events)
        {
            if (ev.Type == AIEventType.PlayerDefeated)
            {
                // 玩家被击败 → 背叛状态重估(下一拍 CheckBetrayal 自然处理)。
                WaitingToBetray = false;
                _pendingRequests.Remove(ev.IntParam);
            }
        }
    }
}

/// <summary>胜利管理器（原版 petra/victoryManager.js，771 行）。
/// 管理奇迹胜利条件、最终推进、消灭残敌 + 关键实体护卫/治疗者编排。
/// 本版:init 登记(奇迹/英雄/圣物,playedTurn==1 等价)+ 每 10 回合
/// (原版 playedTurn%10)按胜利条件驱动——wonder:无奇迹无在队即建(ConstructionPlan)
/// + 动工抽 10 个 worker 当建造者;regicide:英雄健康 → 站姿 aggressive,遇袭撤退
/// (garrisonEmergency:≤70% 驻防治疗建筑/<40% 就近强驻,驻不进则 passive 逃向
/// 最近同陆基地);capture_the_relic:强制 Raid 夺取 + 关键实体护卫编排
/// (manageCriticalEntGuards/assignGuardToCriticalEnt 逐字:worker 缺口回补、
/// 冠军→outOfPlan→士兵三趟、上限公式、陆区同区优先、跨海走 navalManager
/// 运输)。治疗者:manageCriticalEntHealers 训练(support_healer_b,saveResources/
/// 在队/无神庙/护卫池超 min(popMax/10,pop/4) 四门) + 新建治疗者即指派
/// (Create 事件接应,等价原版 TrainingFinished——上游 queues.healer 唯一
/// 生产者就是 victoryManager) + 既有 Healer 类空闲单位兜底配额。
/// 记录在案的差异:治疗者配额对所有关键实体生效(上游仅 regicide 英雄有
/// healersAssigned 桶);garrisonAttackedUnit 只查我方建筑(上游含盟友);
/// EntityRenamed 顺手迁移 assigned 集(上游只迁 guards 表,留陈旧 id)。</summary>
public sealed class VictoryManager
{
    private readonly PetraConfig _config;
    /// <summary>每关键实体治疗者配额(原版 healersPerCriticalEnt;构造即定)。</summary>
    private readonly int _healersPerCriticalEnt;
    /// <summary>init 已跑(原版 playedTurn==1 的 init;幂等,不骑缝)。</summary>
    private bool _inited;

    /// <summary>关键实体的护卫登记(原版 criticalEnts 的 {guardsAssigned,
    /// healersAssigned, guards})。</summary>
    public sealed class CriticalEntData
    {
        /// <summary>已被指派的军事护卫 id 集(含在途)。</summary>
        public readonly HashSet<uint> GuardsAssigned = new();
        /// <summary>已被指派的治疗者 id 集。</summary>
        public readonly HashSet<uint> HealersAssigned = new();
        /// <summary>在位护卫:id → "guard"/"healer"。</summary>
        public readonly Dictionary<uint, string> Guards = new();
        /// <summary>危急驻防旗(原版 garrisonEmergency:血量 <low 时就近强驻,
        /// 入舱即消)。</summary>
        public bool GarrisonEmergency;
    }

    /// <summary>胜利关键实体(原版 victoryManager.criticalEnts;奇迹/圣物/国王——
    /// 防御分派与进攻征收都绕开它们)。</summary>
    public readonly Dictionary<uint, CriticalEntData> CriticalEnts = new();
    /// <summary>兼容性判定(DefenseManager 等用)。</summary>
    public bool IsCritical(uint id) => CriticalEnts.ContainsKey(id);
    /// <summary>已被盯上的 gaia 圣物(原版 victoryManager.targetedGaiaRelics;
    /// 防多队抢同一圣物)。</summary>
    public readonly HashSet<uint> TargetedGaiaRelics = new();
    /// <summary>护卫池(原版 guardEnts):id → 当前是否在位看护。</summary>
    private readonly Dictionary<uint, bool> _guardEnts = new();

    /// <summary>HQ 反链(圣物袭击要走 attackManager)。</summary>
    public Headquarters? Hq;

    public VictoryManager(PetraConfig config)
    {
        _config = config;
        _healersPerCriticalEnt = 2 + (int)System.Math.Round(config.Personality.Defensive * 2);
    }

    /// <summary>原版 init(playedTurn==1):登记开局即在的胜利关键实体——
    /// wonder:我方奇迹;regicide:我方英雄(Soldier 默认 aggressive,否则 passive);
    /// capture_the_relic:我方已持圣物。幂等:已登记的跳过(读档后重跑安全)。</summary>
    private void Init(GameState gameState)
    {
        _inited = true;
        var endGame = gameState.Cm.EndGame;
        if (endGame.HasCondition("wonder"))
            foreach (var wonder in gameState.GetOwnEntitiesByClass("Wonder").Values())
                if (!CriticalEnts.ContainsKey(wonder.Id))
                    RegisterCriticalEnt(gameState, wonder.Id);

        if (endGame.HasCondition("regicide"))
            foreach (var hero in gameState.GetOwnEntitiesByClass("Hero").Values())
            {
                string defaultStance = hero.HasClass("Soldier") ? "aggressive" : "passive";
                var ai = gameState.Cm.QueryInterface<UnitAIComponent>(hero.Entity);
                if (ai != null && ai.Stance != defaultStance)
                    gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.SetUnitStance(
                        (uint)gameState.PlayerId, hero.Id, defaultStance));
                if (!CriticalEnts.ContainsKey(hero.Id))
                    RegisterCriticalEnt(gameState, hero.Id);
            }

        if (endGame.HasCondition("capture_the_relic"))
            foreach (var relic in gameState.GetStructures().Values())
                if (relic.HasClass("Relic") && relic.Owner == gameState.PlayerId
                    && !CriticalEnts.ContainsKey(relic.Id))
                    RegisterCriticalEnt(gameState, relic.Id);
    }

    /// <summary>主更新（原版 victoryManager.js:592-668）。</summary>
    public void Update(GameState gameState, AIEventBuffer events, QueueManager queues)
    {
        uint turn = gameState.Net?.CurrentTurn ?? 0;
        // 原版:等一回合让触发器脚本先生成关键实体(regicide 英雄)。
        if (!_inited && turn > 0)
            Init(gameState);

        CheckEvents(gameState, events);

        if (turn % 10 != 0) return;

        var endGame = gameState.Cm.EndGame;
        if (!endGame.HasCondition("wonder") && !endGame.HasCondition("regicide")
            && !endGame.HasCondition("capture_the_relic"))
            return;

        // 奇迹胜利(原版 HQ.buildWonder):无奇迹且无在队计划 → 建。
        if (endGame.HasCondition("wonder")
            && !gameState.GetOwnEntitiesByClass("Wonder").HasEntities()
            && queues.GetQueue("wonder")?.HasQueuedUnits != true)
        {
            queues.AddPlan("wonder",
                new ConstructionPlan(gameState, "structures/{civ}/wonder"));
        }

        // 弑君护主(原版:英雄血量健康 → aggressive 站姿让其自由迎敌)。
        if (endGame.HasCondition("regicide")
            && endGame.RegicideHeroes.TryGetValue(gameState.PlayerId, out var hero))
        {
            var health = gameState.Cm.QueryInterface<HealthComponent>(hero);
            var ai = gameState.Cm.QueryInterface<UnitAIComponent>(hero);
            if (health != null && ai != null && ai.Stance != "aggressive"
                && (float)health.Current / health.Max > 0.8f)
            {
                gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.SetUnitStance(
                    (uint)gameState.PlayerId, hero.Value, "aggressive"));
            }
        }

        // 圣物胜利编排(原版 victoryManager capture_the_relic 段全量):
        // 无圣物在手 → 强制 Raid 夺取;在手 → 护卫/治疗者编排。
        if (endGame.HasCondition("capture_the_relic"))
            UpdateRelicHunt(gameState);

        // 护卫编排(关键实体非空才值得跑;原版 manageCriticalEntGuards 每 think)。
        if (CriticalEnts.Count > 0)
        {
            ManageCriticalEntGuards(gameState);
            // 治疗者训练(原版 manageCriticalEntHealers 仅 regicide 段调用;
            // 本版对所有关键实体配额开放——与治疗者指派面一致,记录在案)。
            ManageCriticalEntHealers(gameState, queues);
        }
    }

    // ── 关键实体护卫编排(原版 victoryManager.js:360-560 逐字) ──

    /// <summary>原版 manageCriticalEntGuards:worker 缺口回补(<20 → 释放民兵护卫回
    /// 采集岗),然后逐关键实体按上限派护卫(冠军优先,士兵次之,同陆区优先)——
    /// 治疗者同框架按 personality 配额。</summary>
    private void ManageCriticalEntGuards(GameState gameState)
    {
        int numWorkers = gameState.CountOwnEntitiesByRole(WorkerRoles.RoleWorker);
        if (numWorkers < 20)
        {
            foreach (var data in CriticalEnts.Values)
            {
                foreach (var guardId in data.Guards.Keys.ToArray())
                {
                    var guardEnt = gameState.GetEntityById(guardId);
                    if (guardEnt == null || !guardEnt.HasClass("CitizenSoldier")
                        || gameState.Metadata.GetObject(guardId, "role")?.ToString()
                            != WorkerRoles.RoleCriticalEntGuard)
                        continue;

                    gameState.Cm.QueryInterface<UnitAIComponent>(new EntityId(guardId))
                        ?.RemoveGuard(gameState.Cm);
                    gameState.Metadata.Set(guardId, "plan", -1);
                    gameState.Metadata.Remove(guardId, "role");
                    _guardEnts.Remove(guardId);
                    data.GuardsAssigned.Add(guardId);
                    gameState.Metadata.Remove(guardId, "guardedEnt");

                    if (++numWorkers >= 20) break;
                }
                if (numWorkers >= 20) break;
            }
        }

        const int minWorkers = 25;
        const int deltaWorkers = 3;
        foreach (var (id, data) in CriticalEnts)
        {
            var criticalEnt = gameState.GetEntityById(id);
            if (criticalEnt == null) continue;

            int militaryCap = (criticalEnt.HasClass("Wonder") ? 10 : 4)
                + (int)System.Math.Round(_config.Personality.Defensive * 5);
            if (data.GuardsAssigned.Count < militaryCap)
            {
                // 同陆区优先两趟(原版 checkForSameAccess [true,false]);
                // 每趟:冠军 → outOfPlan 回收池 → 士兵(原版 393-461 三段)。
                foreach (bool sameAccess in new[] { true, false })
                {
                    foreach (var entity in gameState.GetOwnEntitiesByClass("Champion").Values())
                    {
                        if (!TryAssignMilitaryGuard(gameState, entity, criticalEnt, sameAccess))
                            continue;
                        data.GuardsAssigned.Add(entity.Id);
                        if (data.GuardsAssigned.Count >= militaryCap) break;
                    }
                    if (data.GuardsAssigned.Count >= militaryCap
                        || numWorkers <= minWorkers + deltaWorkers * data.GuardsAssigned.Count)
                        break;

                    // outOfPlan 回收池(原版 attackManager.outOfPlan 趟:离队单位优先转护卫)。
                    if (Hq != null)
                        foreach (var entId in Hq.AttackManager.OutOfPlan.OrderBy(x => x).ToArray())
                        {
                            var entity = gameState.GetEntityById(entId);
                            if (entity == null
                                || !TryAssignMilitaryGuard(gameState, entity, criticalEnt, sameAccess))
                                continue;
                            numWorkers--;
                            data.GuardsAssigned.Add(entity.Id);
                            Hq.AttackManager.OutOfPlan.Remove(entId);
                            if (data.GuardsAssigned.Count >= militaryCap
                                || numWorkers <= minWorkers + deltaWorkers * data.GuardsAssigned.Count)
                                break;
                        }
                    if (data.GuardsAssigned.Count >= militaryCap
                        || numWorkers <= minWorkers + deltaWorkers * data.GuardsAssigned.Count)
                        break;

                    foreach (var entity in gameState.GetOwnEntitiesByClass("Soldier").Values())
                    {
                        if (!TryAssignMilitaryGuard(gameState, entity, criticalEnt, sameAccess))
                            continue;
                        numWorkers--;
                        data.GuardsAssigned.Add(entity.Id);
                        if (data.GuardsAssigned.Count >= militaryCap
                            || numWorkers <= minWorkers + deltaWorkers * data.GuardsAssigned.Count)
                            break;
                    }
                    if (data.GuardsAssigned.Count >= militaryCap
                        || numWorkers <= minWorkers + deltaWorkers * data.GuardsAssigned.Count)
                        break;
                }
            }

            // 治疗者兜底(本版新增面,上游治疗者仅经训练+TrainingFinished 指派;
            // 这里把既有空闲 Healer 类单位也按 personality 配额征召)。
            if (data.HealersAssigned.Count < _healersPerCriticalEnt)
            {
                foreach (var entity in gameState.GetOwnEntitiesByClass("Healer").Values())
                {
                    if (data.HealersAssigned.Count >= _healersPerCriticalEnt) break;
                    if (gameState.Metadata.GetObject(entity.Id, "plan") != null
                        || gameState.Metadata.GetObject(entity.Id, "transport") != null
                        || CriticalEnts.ContainsKey(entity.Id))
                        continue;
                    gameState.Metadata.Set(entity.Id, "plan", -2);
                    gameState.Metadata.Set(entity.Id, "role", WorkerRoles.RoleCriticalEntHealer);
                    if (AssignGuardToCriticalEnt(gameState, entity, id))
                        data.HealersAssigned.Add(entity.Id);
                    else
                    {
                        // 指派失败(无位置等)→ 回滚标记,下轮重试(防卡死)。
                        gameState.Metadata.Remove(entity.Id, "plan");
                        gameState.Metadata.Remove(entity.Id, "role");
                    }
                }
            }
        }
    }

    /// <summary>原版 manageCriticalEntHealers:关键实体治疗者缺额时经 healer 队列
    /// 训练补员(support_healer_b)。门控:saveResources / 队列非空 / 无建成神庙 /
    /// 护卫池规模超 min(popMax/10, pop/4) 皆停;每次 think 至多下一单。</summary>
    private void ManageCriticalEntHealers(GameState gameState, QueueManager queues)
    {
        if (Hq == null || Hq.SaveResources) return;
        if (queues.GetQueue("healer")?.HasQueuedUnits == true) return;
        if (!gameState.GetOwnEntitiesByClass("Temple").Values().Any(e => !e.IsFoundation))
            return;
        if (_guardEnts.Count > System.Math.Min(
                gameState.GetPopulationMax() / 10, gameState.GetPopulation() / 4))
            return;

        foreach (var (id, data) in CriticalEnts.OrderBy(kv => kv.Key))
        {
            if (data.HealersAssigned.Count >= _healersPerCriticalEnt) continue;
            queues.AddPlan("healer", new TrainingPlan(gameState,
                "units/{civ}/support_healer_b",
                new Dictionary<string, object>
                { ["role"] = WorkerRoles.RoleCriticalEntHealer, ["base"] = 0 }, 1, 1));
            return;
        }
    }

    /// <summary>原版 tryAssignMilitaryGuard:有计划在身/在运输/自身是关键实体 → 跳过;
    /// 同陆区趟要求同 access。</summary>
    private bool TryAssignMilitaryGuard(GameState gameState, AIEntity guardEnt,
        AIEntity criticalEnt, bool checkForSameAccess)
    {
        if (gameState.Metadata.GetObject(guardEnt.Id, "plan") != null
            || gameState.Metadata.GetObject(guardEnt.Id, "transport") != null
            || CriticalEnts.ContainsKey(guardEnt.Id))
            return false;
        if (checkForSameAccess)
        {
            var pf = SimSystem.Pathfinder;
            if (pf != null && guardEnt.Position2D != default && criticalEnt.Position2D != default
                && pf.GetLandRegion(criticalEnt.Position2D.X, criticalEnt.Position2D.Y)
                    != pf.GetLandRegion(guardEnt.Position2D.X, guardEnt.Position2D.Y))
                return false;
        }

        if (!AssignGuardToCriticalEnt(gameState, guardEnt, criticalEnt.Id))
            return false;
        gameState.Metadata.Set(guardEnt.Id, "plan", -2);
        gameState.Metadata.Set(guardEnt.Id, "role", WorkerRoles.RoleCriticalEntGuard);
        return true;
    }

    /// <summary>原版 assignGuardToCriticalEnt 逐字:不可护卫(无 UnitAI/在运输)拒;
    /// 指定目标已非关键实体 → 转派自选并清 guardedEnt;无指定目标 → 派给(对应桶)
    /// 护卫最少的关键实体;关键实体/护卫无位置 → 记入池(guardEnts=false)待
    /// UnGarrison 重试;同陆区 → 下 Guard 令 + 登记在位(建筑类关键实体顺手把
    /// 护卫 base 切过去);异陆区 → navalManager.requireTransport 订运输,
    /// 到位由 UnGarrison 事件重派。</summary>
    public bool AssignGuardToCriticalEnt(GameState gameState, AIEntity guardEnt, uint? criticalEntId)
    {
        var ai = gameState.Cm.QueryInterface<UnitAIComponent>(new EntityId(guardEnt.Id));
        if (ai == null || gameState.Metadata.GetObject(guardEnt.Id, "transport") != null)
            return false;

        if (criticalEntId is { } requested && !CriticalEnts.ContainsKey(requested))
        {
            criticalEntId = null;
            gameState.Metadata.Remove(guardEnt.Id, "guardedEnt");
        }

        bool isHealer = gameState.Metadata.GetObject(guardEnt.Id, "role")?.ToString()
            == WorkerRoles.RoleCriticalEntHealer || guardEnt.HasClass("Healer");
        if (criticalEntId == null)
        {
            // 派给(对应桶)护卫最少的关键实体。
            uint? best = null;
            int min = int.MaxValue;
            foreach (var (cid, data) in CriticalEnts.OrderBy(kv => kv.Key))
            {
                int count = (isHealer ? data.HealersAssigned : data.GuardsAssigned).Count;
                if (count < min) { min = count; best = cid; }
            }
            criticalEntId = best;
            if (criticalEntId is { } picked)
                (isHealer ? CriticalEnts[picked].HealersAssigned
                    : CriticalEnts[picked].GuardsAssigned).Add(guardEnt.Id);
        }
        if (criticalEntId == null)
        {
            gameState.Metadata.Remove(guardEnt.Id, "guardedEnt");
            return false;
        }

        var criticalEnt = gameState.GetEntityById(criticalEntId.Value);
        if (criticalEnt == null || criticalEnt.Position2D == default || guardEnt.Position2D == default)
        {
            // 无位置(驻军中等)→ 记池待 UnGarrison 重试(原版 guardEnts.set(id,false))。
            _guardEnts[guardEnt.Id] = false;
            return false;
        }

        gameState.Metadata.Set(guardEnt.Id, "guardedEnt", criticalEntId.Value);
        ushort guardAccess = EntityExtend.GetLandAccess(gameState, guardEnt);
        ushort criticalAccess = EntityExtend.GetLandAccess(gameState, criticalEnt);
        if (guardAccess == criticalAccess)
        {
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Guard(
                (uint)gameState.PlayerId, guardEnt.Id, criticalEntId.Value));
            var data2 = CriticalEnts[criticalEntId.Value];
            (isHealer ? data2.HealersAssigned : data2.GuardsAssigned).Add(guardEnt.Id);
            data2.Guards[guardEnt.Id] = isHealer ? "healer" : "guard";
            // 护卫换籍到关键实体的基地(原版:Structure 且有 base 元数据时)。
            if (criticalEnt.HasClass("Structure")
                && gameState.Metadata.TryGet(criticalEnt.Id, "base", out var b) && b != null)
                gameState.Metadata.Set(guardEnt.Id, "base", b);
        }
        else
        {
            // 异陆 → 订运输(原版 requireTransport;到位由 UnGarrison 事件重派)。
            Hq?.NavalManager.RequireTransport(
                gameState, guardEnt, guardAccess, criticalAccess, criticalEnt.Position2D);
        }
        _guardEnts[guardEnt.Id] = guardAccess == criticalAccess;
        return true;
    }

    // ── 圣物胜利编排 ──

    /// <summary>原版圣物编排:自由 gaia 圣物 → 强制 Raid(uniqueTarget);
    /// 我方持圣物 → 登记关键实体(护卫编排由 ManageCriticalEntGuards 接管)。</summary>
    private void UpdateRelicHunt(GameState gameState)
    {
        var hq = Hq;
        // 清理失效标记
        TargetedGaiaRelics.RemoveWhere(id =>
        {
            var e = gameState.GetEntityById(id);
            return e == null || e.Owner == gameState.PlayerId;
        });
        // 我方持有中 → 登记为关键实体(护卫/治疗者由 manageCriticalEntGuards 编排)。
        var held = FindRelic(gameState, gameState.PlayerId);
        if (held.HasValue)
        {
            if (!CriticalEnts.ContainsKey(held.Value))
                RegisterCriticalEnt(gameState, held.Value);
            return;
        }

        // 无圣物 → 找未被盯的 gaia 圣物起 Raid(强制、uniqueTarget)。
        if (hq == null) return;
        foreach (var relic in gameState.GetStructures().Values()
            .Where(e => e.HasClass("Relic") && e.Owner == 0 && e.Position2D != default)
            .OrderBy(e => e.Id))
        {
            if (TargetedGaiaRelics.Contains(relic.Id)) continue;
            TargetedGaiaRelics.Add(relic.Id);
            hq.AttackManager.StartRelicRaid(gameState, relic);
            return;   // 一次只抢一个(原版 targetedGaiaRelics 约束)
        }
    }

    /// <summary>原版 criticalEnts.set:登记 + 即刻尝试护卫指派。</summary>
    public void RegisterCriticalEnt(GameState gameState, uint entId)
    {
        CriticalEnts[entId] = new CriticalEntData();
        // 原版:登记后立刻从护卫池补一指派(在位护卫空闲时)。
        var ent = gameState.GetEntityById(entId);
        if (ent == null) return;
        foreach (var (guardId, isGuarding) in _guardEnts.OrderBy(kv => kv.Key).ToList())
        {
            if (isGuarding) continue;
            var guardEnt = gameState.GetEntityById(guardId);
            if (guardEnt != null)
                AssignGuardToCriticalEnt(gameState, guardEnt, entId);
            break;   // 原版每事件最多一次指派
        }
    }

    /// <summary>原版 removeCriticalEnt:释放全部护卫(治疗者回池待重派、护卫清岗出池)。</summary>
    public void RemoveCriticalEnt(GameState gameState, uint entId)
    {
        if (!CriticalEnts.TryGetValue(entId, out var data)) return;
        foreach (var (guardId, role) in data.Guards)
        {
            var guardEnt = gameState.GetEntityById(guardId);
            if (guardEnt == null) continue;
            if (role == "healer")
                _guardEnts[guardId] = false;
            else
            {
                gameState.Metadata.Set(guardId, "plan", -1);
                gameState.Metadata.Remove(guardId, "role");
                _guardEnts.Remove(guardId);
            }
            gameState.Metadata.Remove(guardId, "guardedEnt");
            gameState.Cm.QueryInterface<UnitAIComponent>(new EntityId(guardId))
                ?.RemoveGuard(gameState.Cm);
        }
        CriticalEnts.Remove(entId);
    }

    /// <summary>按属主找圣物(原版 getRelic:Relic 类 + 属主)。</summary>
    private static uint? FindRelic(GameState gameState, int owner)
    {
        foreach (var e in gameState.GetStructures().Values())
            if (e.HasClass("Relic") && e.Owner == owner && e.Position2D != default)
                return e.Id;
        return null;
    }

    private void CheckEvents(GameState gameState, AIEventBuffer events)
    {
        var endGame = gameState.Cm.EndGame;
        foreach (var ev in events.Events)
        {
            switch (ev.Type)
            {
                case AIEventType.Create:
                    // 奇迹动工(原版 Create 段):我方奇迹地基 → 抽 10 个 worker 当建造者
                    // (原版 bulkPickWorkers 跨基地抽人;本版直挑 worker 角色空闲者,
                    // base 换籍略,记录在案)。
                    if (endGame.HasCondition("wonder") && ev.IntParam == gameState.PlayerId
                        && gameState.GetEntityById(ev.Entity) is { } foundation
                        && foundation.IsFoundation && foundation.HasClass("Wonder"))
                    {
                        int assigned = 0;
                        foreach (var worker in gameState.GetOwnUnits().Values().OrderBy(w => w.Id))
                        {
                            if (assigned >= 10) break;
                            if (gameState.Metadata.GetObject(worker.Id, "role")?.ToString()
                                != WorkerRoles.RoleWorker) continue;
                            gameState.Metadata.Set(worker.Id, "subrole", WorkerRoles.SubroleBuilder);
                            gameState.Metadata.Set(worker.Id, "target-foundation", ev.Entity);
                            assigned++;
                        }
                    }
                    // 新训治疗者接应(原版 TrainingFinished 段:role=criticalEntHealer
                    // 即指派;上游 queues.healer 唯一生产者是本管理器,故凡缺额时新造
                    // Healer 即补。Create 等价 TrainingFinished——本内核训练出货即 Spawn)。
                    if (ev.IntParam == gameState.PlayerId && CriticalEnts.Count > 0
                        && gameState.GetEntityById(ev.Entity) is { } newborn
                        && newborn.IsUnit && newborn.HasClass("Healer")
                        && gameState.Metadata.GetObject(newborn.Id, "plan") == null
                        && CriticalEnts.Values.Any(d2 => d2.HealersAssigned.Count < _healersPerCriticalEnt))
                    {
                        gameState.Metadata.Set(newborn.Id, "plan", -2);
                        gameState.Metadata.Set(newborn.Id, "role", WorkerRoles.RoleCriticalEntHealer);
                        if (!AssignGuardToCriticalEnt(gameState, newborn, null))
                        {
                            gameState.Metadata.Remove(newborn.Id, "plan");
                            gameState.Metadata.Remove(newborn.Id, "role");
                        }
                    }
                    break;

                case AIEventType.ConstructionFinished:
                    // 奇迹建成 → 登记关键实体(原版 ConstructionFinished 段)。
                    if (endGame.HasCondition("wonder") && ev.IntParam == gameState.PlayerId
                        && gameState.GetEntityById(ev.Entity) is { } built
                        && built.HasClass("Wonder") && !CriticalEnts.ContainsKey(built.Id))
                        RegisterCriticalEnt(gameState, built.Id);
                    break;

                case AIEventType.Attacked:
                    // 弑君护主(原版 Attacked 段):关键实体血量 ≤high → 撤退/驻防。
                    if (endGame.HasCondition("regicide"))
                        OnCriticalEntAttacked(gameState, ev.Entity);
                    break;

                case AIEventType.Garrison:
                    OnCriticalEntGarrisoned(gameState, ev.Entity, (uint)ev.IntParam);
                    break;

                case AIEventType.UnGarrison:
                    OnGuardUnGarrisoned(gameState, ev.Entity);
                    break;

                case AIEventType.EntityRenamed:
                    // 护卫换名迁移(原版 EntityRenamed 段;本版顺手迁 assigned 集)。
                    if (_guardEnts.Remove((uint)ev.Entity, out bool wasGuarding))
                        _guardEnts[(uint)ev.IntParam] = wasGuarding;
                    foreach (var data in CriticalEnts.Values)
                    {
                        if (data.Guards.Remove((uint)ev.Entity, out var grole))
                            data.Guards[(uint)ev.IntParam] = grole;
                        if (data.GuardsAssigned.Remove((uint)ev.Entity))
                            data.GuardsAssigned.Add((uint)ev.IntParam);
                        if (data.HealersAssigned.Remove((uint)ev.Entity))
                            data.HealersAssigned.Add((uint)ev.IntParam);
                    }
                    break;

                case AIEventType.Destroy:
                    // 关键实体没了 → 摘除(护卫释放);护卫没了 → 各表清账(原版 Destroy 段)。
                    if (CriticalEnts.ContainsKey(ev.Entity))
                    {
                        RemoveCriticalEnt(gameState, ev.Entity);
                        break;
                    }
                    if (!_guardEnts.Remove(ev.Entity)) break;
                    foreach (var data in CriticalEnts.Values)
                        if (data.Guards.Remove(ev.Entity))
                        {
                            data.HealersAssigned.Remove(ev.Entity);
                            data.GuardsAssigned.Remove(ev.Entity);
                        }
                    break;

                case AIEventType.OwnershipChanged:
                    // 失去关键实体 → 摘除(护卫释放)。
                    if (ev.IntParam == gameState.PlayerId && CriticalEnts.ContainsKey(ev.Entity))
                        RemoveCriticalEnt(gameState, ev.Entity);
                    // 得到圣物/奇迹 → 登记;圣物得手即撤向最近基地(原版同款)。
                    else if (ev.IntParam2 == gameState.PlayerId
                        && gameState.GetEntityById(ev.Entity) is { } ent
                        && ((endGame.HasCondition("wonder") && ent.HasClass("Wonder"))
                            || (endGame.HasCondition("capture_the_relic")
                                && ent.HasClass("Relic"))))
                    {
                        RegisterCriticalEnt(gameState, ev.Entity);
                        if (ent.HasClass("Relic"))
                            PickCriticalEntRetreatLocation(gameState, ent, false);
                    }
                    break;
            }
        }
    }

    /// <summary>原版 regicide Attacked 段:血量 ≤high 且非应急计划在身 → 撤出战斗
    /// (停手/离计划/离军),≤low 升级危急驻防;已在应急计划且血量仍 <low →
    /// 换更近的驻防点(原版含 cancelGarrison——本内核 GarrisonManager 无预约
    /// 簿记,略,记录在案)。</summary>
    private void OnCriticalEntAttacked(GameState gameState, uint targetId)
    {
        if (!CriticalEnts.TryGetValue(targetId, out var hero)) return;
        var target = gameState.GetEntityById(targetId);
        if (target == null || target.Position2D == default) return;
        float health = target.HealthLevel;
        if (health > _config.GarrisonHealthLevel.High) return;

        var planMeta = gameState.Metadata.GetObject(targetId, "plan");
        int plan = planMeta is int p ? p : planMeta is uint pu ? (int)pu : -1;
        bool inEmergency = planMeta != null && (plan == -2 || plan == -3);
        if (!inEmergency)
        {
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Stop(
                (uint)gameState.PlayerId, targetId));
            if (planMeta != null && plan >= 0)
                Hq?.AttackManager.GetPlan(plan)?.RemoveUnit(gameState, target, true);
            var armyMeta = gameState.Metadata.GetObject(targetId, "PartOfArmy");
            int armyId = armyMeta is int a ? a : armyMeta is uint au ? (int)au : 0;
            if (armyId != 0)
                Hq?.DefenseManager.GetArmy(armyId)?.RemoveOwn(gameState, targetId);

            hero.GarrisonEmergency = health < _config.GarrisonHealthLevel.Low;
            PickCriticalEntRetreatLocation(gameState, target, hero.GarrisonEmergency);
        }
        else if (health < _config.GarrisonHealthLevel.Low && !hero.GarrisonEmergency)
        {
            PickCriticalEntRetreatLocation(gameState, target, true);
            hero.GarrisonEmergency = true;
        }
    }

    /// <summary>原版 Garrison 段:关键实体入舱 → 危急旗消;持有者是可以跑的船 →
    /// 护卫全体下岗(在原地待命回池);否则护卫移驻守军点旁(原版 moveToRange
    /// radius..radius+5;本版直取持有者位置,障碍收敛由 UnitAI 兜底,记录在案)。</summary>
    private void OnCriticalEntGarrisoned(GameState gameState, uint entId, uint holderId)
    {
        if (!CriticalEnts.TryGetValue(entId, out var data)) return;
        data.GarrisonEmergency = false;
        var holderEnt = gameState.GetEntityById(holderId);
        if (holderEnt == null) return;

        if (holderEnt.HasClass("Ship"))
        {
            foreach (var guardId in data.Guards.Keys.ToArray())
            {
                var guardEnt = gameState.GetEntityById(guardId);
                if (guardEnt == null) continue;
                gameState.Cm.QueryInterface<UnitAIComponent>(new EntityId(guardId))
                    ?.RemoveGuard(gameState.Cm);
                _guardEnts[guardId] = false;
            }
            data.Guards.Clear();
            return;
        }

        foreach (var guardId in data.Guards.Keys.ToArray())
        {
            var guardEnt = gameState.GetEntityById(guardId);
            if (guardEnt == null || guardEnt.Position2D == default) continue;
            // 非士兵的应急计划(-2/-3)治疗者可能正因低血在跑驻防,不打扰(原版同款)。
            var planMeta = gameState.Metadata.GetObject(guardId, "plan");
            if (!guardEnt.HasClass("Soldier")
                && (planMeta is int p && (p == -2 || p == -3)))
                continue;
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Move(
                (uint)gameState.PlayerId, guardId,
                holderEnt.Position2D.X, holderEnt.Position2D.Y));
        }
    }

    /// <summary>原版 UnGarrison 段:护卫经运输到达目标陆区(出舱)→ 按 guardedEnt
    /// 重试指派;关键实体(英雄)出舱 → 把池中未到位的护卫补派给它
    /// (上限沿用 healersPerCriticalEnt,原版同款怪口径)。</summary>
    private void OnGuardUnGarrisoned(GameState gameState, uint entId)
    {
        bool isGuard = _guardEnts.TryGetValue(entId, out bool isGuarding);
        if (!isGuard && !CriticalEnts.ContainsKey(entId)) return;
        var ent = gameState.GetEntityById(entId);
        if (ent == null) return;

        var roleMeta = gameState.Metadata.GetObject(entId, "role")?.ToString();
        if ((roleMeta == WorkerRoles.RoleCriticalEntHealer
                || roleMeta == WorkerRoles.RoleCriticalEntGuard)
            && isGuard && !isGuarding)
        {
            var guardedMeta = gameState.Metadata.GetObject(entId, "guardedEnt");
            uint? guardedEnt = guardedMeta is uint gu ? gu
                : guardedMeta is int gi && gi > 0 ? (uint)gi : null;
            AssignGuardToCriticalEnt(gameState, ent, guardedEnt);
            return;
        }

        if (!CriticalEnts.TryGetValue(entId, out var data)) return;
        foreach (var (guardId, guarding) in _guardEnts.OrderBy(kv => kv.Key).ToArray())
        {
            if (data.Guards.Count >= _healersPerCriticalEnt) break;
            if (guarding) continue;
            var guardEnt = gameState.GetEntityById(guardId);
            if (guardEnt != null)
                AssignGuardToCriticalEnt(gameState, guardEnt, entId);
        }
    }

    /// <summary>原版 pickCriticalEntRetreatLocation:先试驻防(garrisonAttackedUnit
    /// 语义:非危急只进 BuffHeal 建筑,危急就近强驻——满员则逐出首位腾位);
    /// 驻不进(无 plan=-3 标记)→ 非圣物改 passive 站姿 + 逃向最近同陆基地锚点。</summary>
    private void PickCriticalEntRetreatLocation(GameState gameState, AIEntity criticalEnt, bool emergency)
    {
        if (GarrisonAttackedUnit(gameState, criticalEnt, emergency))
            return;   // 已下驻防令(plan=-3 由 GarrisonAttackedUnit 登)

        if (CriticalEnts.TryGetValue(criticalEnt.Id, out var data) && data.GarrisonEmergency)
            data.GarrisonEmergency = false;

        if (!criticalEnt.HasClass("Relic"))
        {
            var ai = gameState.Cm.QueryInterface<UnitAIComponent>(criticalEnt.Entity);
            if (ai != null && ai.Stance != "passive")
                gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.SetUnitStance(
                    (uint)gameState.PlayerId, criticalEnt.Id, "passive"));
        }

        // 最近同陆基地(原版 getBestBase + access 一致才逃)。
        if (Hq == null || criticalEnt.Position2D == default) return;
        ushort access = EntityExtend.GetLandAccess(gameState, criticalEnt);
        BaseManager? bestBase = null;
        float bestDist = float.MaxValue;
        foreach (var b in Hq.BasesManager.Bases)
        {
            if (b.AnchorId == null || b.AccessIndex != access) continue;
            var anchor = gameState.GetEntityById(b.AnchorId.Value);
            if (anchor == null || anchor.Position2D == default) continue;
            float dx = anchor.Position2D.X.ToFloat() - criticalEnt.Position2D.X.ToFloat();
            float dz = anchor.Position2D.Y.ToFloat() - criticalEnt.Position2D.Y.ToFloat();
            float dist = dx * dx + dz * dz;
            if (dist < bestDist) { bestDist = dist; bestBase = b; }
        }
        if (bestBase?.AnchorId is { } anchorId
            && gameState.GetEntityById(anchorId) is { } bestAnchor)
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Move(
                (uint)gameState.PlayerId, criticalEnt.Id,
                bestAnchor.Position2D.X, bestAnchor.Position2D.Y));
    }

    /// <summary>原版 defenseManager.garrisonAttackedUnit 的紧凑移植:最近的我方可驻
    /// 建筑(非危急须 BuffHeal>0 的治疗建筑;原版含盟友建筑,本版只查我方,
    /// 记录在案),类许可 + 血量过弹出阈 + 同陆区;危急且满员 → 先逐出首位腾位。
    /// 成功 → 下 Garrison 令 + plan=-3 应急标记,返回 true。</summary>
    private bool GarrisonAttackedUnit(GameState gameState, AIEntity unit, bool emergency)
    {
        ushort unitAccess = EntityExtend.GetLandAccess(gameState, unit);
        AIEntity? nearest = null;
        float bestDist = float.MaxValue;
        foreach (var ent in gameState.GetOwnStructures().Values().OrderBy(e => e.Id))
        {
            var holder = gameState.Cm.QueryInterface<GarrisonHolderComponent>(ent.Entity);
            if (holder == null) continue;
            if (!emergency && holder.BuffHeal <= 0) continue;
            if (!holder.IsAllowedToGarrison(gameState.Cm, unit.Entity)) continue;
            int capacity = holder.GetCapacity(gameState.Cm);
            if (holder.OccupiedSlots(gameState.Cm) >= capacity
                && (!emergency || holder.Entities.Count == 0))
                continue;
            if (!holder.HasEnoughHealth(gameState.Cm)) continue;
            if (ent.Position2D == default
                || EntityExtend.GetLandAccess(gameState, ent) != unitAccess) continue;
            float dx = ent.Position2D.X.ToFloat() - unit.Position2D.X.ToFloat();
            float dz = ent.Position2D.Y.ToFloat() - unit.Position2D.Y.ToFloat();
            float dist = dx * dx + dz * dz;
            if (dist >= bestDist) continue;
            bestDist = dist;
            nearest = ent;
        }
        if (nearest == null) return false;

        var nearestHolder = gameState.Cm.QueryInterface<GarrisonHolderComponent>(nearest.Entity)!;
        if (emergency && nearestHolder.OccupiedSlots(gameState.Cm)
                >= nearestHolder.GetCapacity(gameState.Cm)
            && nearestHolder.Entities.Count > 0)
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Ungarrison(
                (uint)gameState.PlayerId, nearest.Id, (int)nearestHolder.Entities[0].Value));

        gameState.Metadata.Set(unit.Id, "plan", -3);
        gameState.Metadata.Set(unit.Id, "subrole", WorkerRoles.SubroleGarrisoning);
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Garrison(
            (uint)gameState.PlayerId, unit.Id, nearest.Id));
        return true;
    }

    // ── 序列化(存档 v21 尾段:GarrisonEmergency 危急旗)──
    public void Serialize(Serialization.ISerializer s)
    {
        s.NumberI32("crit_n", CriticalEnts.Count);
        foreach (var kv in CriticalEnts.OrderBy(kv => kv.Key))
        {
            s.NumberU32("cid", kv.Key);
            s.NumberI32("ga_n", kv.Value.GuardsAssigned.Count);
            foreach (uint g in kv.Value.GuardsAssigned.OrderBy(x => x)) s.NumberU32("ga", g);
            s.NumberI32("ha_n", kv.Value.HealersAssigned.Count);
            foreach (uint h in kv.Value.HealersAssigned.OrderBy(x => x)) s.NumberU32("ha", h);
            s.NumberI32("gu_n", kv.Value.Guards.Count);
            foreach (var g in kv.Value.Guards.OrderBy(kv2 => kv2.Key))
            {
                s.NumberU32("gid", g.Key);
                s.StringASCII("grole", g.Value);
            }
            s.Bool("gem", kv.Value.GarrisonEmergency);   // 存档 v21
        }
        s.NumberI32("tgt_n", TargetedGaiaRelics.Count);
        foreach (uint t in TargetedGaiaRelics.OrderBy(x => x)) s.NumberU32("tgt", t);
        s.NumberI32("ge_n", _guardEnts.Count);
        foreach (var kv in _guardEnts.OrderBy(kv => kv.Key))
        {
            s.NumberU32("geid", kv.Key);
            s.Bool("ge", kv.Value);
        }
    }

    public void Deserialize(Serialization.IDeserializer d)
    {
        int cn = d.NumberI32("crit_n");
        for (int i = 0; i < cn; i++)
        {
            uint cid = d.NumberU32("cid");
            var data = new CriticalEntData();
            int ga = d.NumberI32("ga_n");
            for (int j = 0; j < ga; j++) data.GuardsAssigned.Add(d.NumberU32("ga"));
            int ha = d.NumberI32("ha_n");
            for (int j = 0; j < ha; j++) data.HealersAssigned.Add(d.NumberU32("ha"));
            int gu = d.NumberI32("gu_n");
            for (int j = 0; j < gu; j++) data.Guards[d.NumberU32("gid")] = d.StringASCII("grole");
            if (Serialization.SaveFormat.LoadedVersion >= 21)
                data.GarrisonEmergency = d.Bool("gem");
            CriticalEnts[cid] = data;
        }
        int tn = d.NumberI32("tgt_n");
        for (int i = 0; i < tn; i++) TargetedGaiaRelics.Add(d.NumberU32("tgt"));
        int gn = d.NumberI32("ge_n");
        for (int i = 0; i < gn; i++) _guardEnts[d.NumberU32("geid")] = d.Bool("ge");
    }
}
