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
/// 本版:每 10 回合(原版 playedTurn%10)按胜利条件驱动——wonder:无奇迹无在队
/// 即建(ConstructionPlan);regicide:英雄健康 → 站姿 aggressive;capture_the_relic:
/// 强制 Raid 夺取 + 关键实体护卫编排(manageCriticalEntGuards/assignGuardToCriticalEnt
/// 逐字:worker 缺口回补、冠军优先士兵次之、上限公式、陆区同区优先、
/// 治疗者按 personality 配额)。</summary>
public sealed class VictoryManager
{
    private readonly PetraConfig _config;

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

    public VictoryManager(PetraConfig config) => _config = config;

    /// <summary>主更新（原版 victoryManager.js:594-770）。</summary>
    public void Update(GameState gameState, AIEventBuffer events, QueueManager queues)
    {
        CheckEvents(gameState, events);

        uint turn = gameState.Net?.CurrentTurn ?? 0;
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
            ManageCriticalEntGuards(gameState);
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
        int healersPerCriticalEnt = 2 + (int)System.Math.Round(_config.Personality.Defensive * 2);
        foreach (var (id, data) in CriticalEnts)
        {
            var criticalEnt = gameState.GetEntityById(id);
            if (criticalEnt == null) continue;

            int militaryCap = (criticalEnt.HasClass("Wonder") ? 10 : 4)
                + (int)System.Math.Round(_config.Personality.Defensive * 5);
            if (data.GuardsAssigned.Count < militaryCap)
            {
                // 同陆区优先两趟(原版 checkForSameAccess [true,false])。
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

            // 治疗者(原版 healersAssigned 配额;Healer 类空闲单位)。
            if (data.HealersAssigned.Count < healersPerCriticalEnt)
            {
                foreach (var entity in gameState.GetOwnEntitiesByClass("Healer").Values())
                {
                    if (data.HealersAssigned.Count >= healersPerCriticalEnt) break;
                    if (gameState.Metadata.GetObject(entity.Id, "plan") != null
                        || gameState.Metadata.GetObject(entity.Id, "transport") != null
                        || CriticalEnts.ContainsKey(entity.Id))
                        continue;
                    gameState.Metadata.Set(entity.Id, "plan", -2);
                    gameState.Metadata.Set(entity.Id, "role", WorkerRoles.RoleCriticalEntHealer);
                    if (AssignGuardToCriticalEnt(gameState, entity, id))
                        data.HealersAssigned.Add(entity.Id);
                }
            }
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

    /// <summary>原版 assignGuardToCriticalEnt:不可护卫(无 UnitAI/在运输)拒;
    /// 无指定目标 → 派给护卫最少的关键实体(治疗者按 healersAssigned,否则
    /// guardsAssigned);登 metadata guardedEnt + Guard 锁步令。</summary>
    public bool AssignGuardToCriticalEnt(GameState gameState, AIEntity guardEnt, uint? criticalEntId)
    {
        var ai = gameState.Cm.QueryInterface<UnitAIComponent>(new EntityId(guardEnt.Id));
        if (ai == null || gameState.Metadata.GetObject(guardEnt.Id, "transport") != null)
            return false;

        bool isHealer = guardEnt.HasClass("Healer");
        if (criticalEntId == null || !CriticalEnts.ContainsKey(criticalEntId.Value))
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
        }
        if (criticalEntId == null)
        {
            gameState.Metadata.Remove(guardEnt.Id, "guardedEnt");
            return false;
        }

        gameState.Metadata.Set(guardEnt.Id, "guardedEnt", criticalEntId.Value);
        gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Guard(
            (uint)gameState.PlayerId, guardEnt.Id, criticalEntId.Value));
        var data2 = CriticalEnts[criticalEntId.Value];
        (isHealer ? data2.HealersAssigned : data2.GuardsAssigned).Add(guardEnt.Id);
        data2.Guards[guardEnt.Id] = isHealer ? "healer" : "guard";
        _guardEnts[guardEnt.Id] = true;
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

    /// <summary>原版 removeCriticalEnt:释放全部护卫(治疗者回池、护卫清岗)。</summary>
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
        foreach (var ev in events.Events)
        {
            if (ev.Type == AIEventType.OwnershipChanged)
            {
                // 失去关键实体 → 摘除(护卫释放)。
                if (ev.IntParam == gameState.PlayerId && CriticalEnts.ContainsKey(ev.Entity))
                    RemoveCriticalEnt(gameState, ev.Entity);
                // 得到圣物/奇迹 → 登记。
                else if (ev.IntParam2 == gameState.PlayerId
                    && gameState.GetEntityById(ev.Entity) is { } ent
                    && ((gameState.Cm.EndGame.HasCondition("wonder") && ent.HasClass("Wonder"))
                        || (gameState.Cm.EndGame.HasCondition("capture_the_relic")
                            && ent.HasClass("Relic"))))
                    RegisterCriticalEnt(gameState, ev.Entity);
            }
        }
    }

    // ── 序列化(存档 v17 尾段)──
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
            CriticalEnts[cid] = data;
        }
        int tn = d.NumberI32("tgt_n");
        for (int i = 0; i < tn; i++) TargetedGaiaRelics.Add(d.NumberU32("tgt"));
        int gn = d.NumberI32("ge_n");
        for (int i = 0; i < gn; i++) _guardEnts[d.NumberU32("geid")] = d.Bool("ge");
    }
}
