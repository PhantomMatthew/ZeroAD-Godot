using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>进攻管理器（原版 petra/attackManager.js，867 行）。
/// 本端口:按类型分桶(Rush/Raid/Attack/HugeAttack)的进攻生命周期 + 发起轮换 +
/// getEnemyPlayer 目标玩家选择 + defeated 追踪 + outOfPlan 回收池 + 轰炸补丁事件
/// + 海图换面(attackPlansEncounteredWater 双端:建计划失败/隔水失败置旗 →
/// NavalManager 消费提最低运输船数;上游只写不读为死旗,消费端为我们所接,
/// 记录在案)。</summary>
public sealed class AttackManager
{
    private readonly PetraConfig _config;

    /// <summary>HQ 反链(HQ 构造注入;getEnemyPlayer/raid finder 要用)。</summary>
    public Headquarters? Hq;

    /// <summary>各类型筹备中/进行中进攻(原版 upcomingAttacks/startedAttacks 按类型分桶)。</summary>
    public readonly Dictionary<string, List<AttackPlan>> UpcomingAttacks = new()
    {
        [AttackPlan.TypeRush] = new(), [AttackPlan.TypeRaid] = new(),
        [AttackPlan.TypeDefault] = new(), [AttackPlan.TypeHugeAttack] = new(),
    };
    public readonly Dictionary<string, List<AttackPlan>> StartedAttacks = new()
    {
        [AttackPlan.TypeRush] = new(), [AttackPlan.TypeRaid] = new(),
        [AttackPlan.TypeDefault] = new(), [AttackPlan.TypeHugeAttack] = new(),
    };

    /// <summary>离开计划的单位回收池(原版 outOfPlan;assignUnits 优先回收)。</summary>
    public readonly HashSet<uint> OutOfPlan = new();
    /// <summary>攻城游击(原版 bombingAttacks:目标建筑 → 攻击中的攻城器 id 集;
    /// 临时骚扰,不进正式进攻计划)。</summary>
    public readonly Dictionary<uint, HashSet<uint>> BombingAttacks = new();

    /// <summary>已败玩家(原版 defeated;checkEvents 维护)。</summary>
    private readonly HashSet<int> _defeated = new();
    /// <summary>当前重点敌(原版 currentEnemyPlayer:非 Huge 进攻保持集火)。</summary>
    private int? _currentEnemyPlayer;

    private int _totalNumber;
    private int _rushNumber;
    /// <summary>原版 attackPlansEncounteredWater(hack 旗):陆攻计划因目标不可达
    /// (隔水)失败而置位;NavalManager 消费 → 提最低运输船数(海图换面)。
    /// 上游 master 只写不读(死旗);我们把消费端接上(记录在案)。</summary>
    public bool AttackPlansEncounteredWater;
    /// <summary>已发起的 rush 数(原版 rushNumber;HQ 人口规划的 alpha 门用)。</summary>
    public int RushNumber => _rushNumber;
    /// <summary>rush 次数上限(原版 maxRushes;难度相关)。</summary>
    public int MaxRushesCount => MaxRushes;
    private int _attackNumber;
    /// <summary>rush 规模表(原版 rushSize 随 rushNumber 递增)。</summary>
    private static readonly int[] RushSizes = { 6, 10, 14 };
    /// <summary>setRushes 覆盖(原版 attackManager.setRushes,开局木量充足时的
    /// rush 收窄);null = 用难度默认。</summary>
    private int? _maxRushesOverride;
    private int[]? _rushSizesOverride;

    /// <summary>原版 setRushes:性格进取度 × 允许值收窄 rush 上限与规模。</summary>
    public void SetRushes(int allowed)
    {
        if (_config.Personality.Aggressive > _config.PersonalityCut.Strong && allowed > 2)
        {
            _maxRushesOverride = 3;
            _rushSizesOverride = new[] { 16, 20, 24 };
        }
        else if (_config.Personality.Aggressive > _config.PersonalityCut.Medium && allowed > 1)
        {
            _maxRushesOverride = 2;
            _rushSizesOverride = new[] { 18, 22 };
        }
        else if (_config.Personality.Aggressive > _config.PersonalityCut.Weak && allowed > 0)
        {
            _maxRushesOverride = 1;
            _rushSizesOverride = new[] { 20 };
        }
    }
    /// <summary>rush 上限(原版 maxRushes:难度驱动;Easy 0 / Medium 1 / Hard+ 2)。</summary>
    private int MaxRushes => _maxRushesOverride ?? (_config.Difficulty <= DifficultyLevel.Easy ? 0
        : _config.Difficulty <= DifficultyLevel.Medium ? 1 : 2);

    public AttackManager(PetraConfig config) => _config = config;

    public bool IsDefeated(int playerId) => _defeated.Contains(playerId);

    /// <summary>按计划名查计划(原版 getPlan;两桶全扫)。</summary>
    public AttackPlan? GetPlan(int name)
    {
        foreach (var list in new[] { UpcomingAttacks, StartedAttacks })
            foreach (var plans in list.Values)
                foreach (var p in plans)
                    if (p.Name == name) return p;
        return null;
    }

    /// <summary>筹备中的指定类型进攻(原版 getAttackInPreparation)。</summary>
    public AttackPlan? GetAttackInPreparation(string type) =>
        UpcomingAttacks[type].FirstOrDefault(p => p.State == AttackPlan.AttackState.Unstarted);

    // ── 主更新(原版 attackManager.update:先更新既有,再发起新的)──

    public void Update(GameState gameState, QueueManager queues, AIEventBuffer events)
    {
        CheckEvents(gameState, events);

        var unexecuted = new Dictionary<string, int>
        {
            [AttackPlan.TypeRush] = 0, [AttackPlan.TypeRaid] = 0,
            [AttackPlan.TypeDefault] = 0, [AttackPlan.TypeHugeAttack] = 0,
        };

        foreach (var type in UpcomingAttacks.Keys)
        {
            for (int i = 0; i < UpcomingAttacks[type].Count; i++)
            {
                var attack = UpcomingAttacks[type][i];
                attack.WireQueues(queues);
                if (attack.Paused) continue;
                var step = attack.UpdatePreparation(gameState, queues, this);
                if (step == AttackPlan.PreparationResult.KeepGoing)
                {
                    if (attack.State == AttackPlan.AttackState.Unstarted)
                        unexecuted[type]++;
                }
                else if (step == AttackPlan.PreparationResult.Failed)
                {
                    // 隔水失败(Overseas=0 且目标与自己不同陆区)→ 置海图旗。
                    if (attack.Overseas == 0 && attack.TargetPos is { } tpos)
                    {
                        var pf = SimSystem.Pathfinder;
                        var myPos = gameState.GetOwnStructures().Values()
                            .FirstOrDefault(e2 => e2.Position2D != default);
                        if (pf != null && myPos != null)
                        {
                            uint myRegion = pf.GetLandRegion(myPos.Position2D.X, myPos.Position2D.Y);
                            uint tgtRegion = pf.GetLandRegion(tpos.X, tpos.Y);
                            if (myRegion != 0 && tgtRegion != 0 && myRegion != tgtRegion)
                                AttackPlansEncounteredWater = true;
                        }
                    }
                    // 建计划即败(选不到/够不到目标;原版 attackPlan.failed → 置旗
                    // 的 hack 语义,attackManager.js:406)——消费端 NavalManager 会复核
                    // 确有海外敌才提船数。
                    else if (attack.Overseas == 0 && attack.FailedNoTarget)
                        AttackPlansEncounteredWater = true;
                    attack.Abort(gameState, this, queues);
                    UpcomingAttacks[type].RemoveAt(i--);
                }
                else   // Start
                {
                    if (attack.StartAttack(gameState))
                        StartedAttacks[type].Add(attack);
                    else
                        attack.Abort(gameState, this, queues);
                    UpcomingAttacks[type].RemoveAt(i--);
                }
            }
        }

        foreach (var type in StartedAttacks.Keys)
        {
            for (int i = 0; i < StartedAttacks[type].Count; i++)
            {
                var attack = StartedAttacks[type][i];
                if (attack.Paused) continue;
                if (!attack.Update(gameState, this))
                {
                    attack.Abort(gameState, this, queues);
                    StartedAttacks[type].RemoveAt(i--);
                }
            }
        }

        // ── 发起轮换(原版 update 尾部,顺序:rush → attack/huge → raid)──
        int barracksNb = gameState.GetOwnEntitiesByClass("Barracks")
            .Filter(e => !e.IsFoundation).Length;

        // Rush:有兵营且 rush 配额未用完。
        if (_rushNumber < MaxRushes && barracksNb >= 1
            && unexecuted[AttackPlan.TypeRush] == 0)
        {
            var plan = new AttackPlan(gameState, _totalNumber, AttackPlan.TypeRush, _config,
                rushTargetSize: (_rushSizesOverride ?? RushSizes)[
                Math.Min(_rushNumber, (_rushSizesOverride ?? RushSizes).Length - 1)]);
            plan.Init(gameState, queues);
            plan.SetInitialRallyPoint(gameState);
            UpcomingAttacks[AttackPlan.TypeRush].Add(plan);
            _totalNumber++;
            _rushNumber++;
        }
        // Attack/HugeAttack:无筹备中的进攻 + 进行中 < min(2, 1+popMax/100)
        // + (无进行中 或 人口余量 >12);门控:有兵营且(town+ 或在研 town),或无基地可扩。
        else if (unexecuted[AttackPlan.TypeDefault] == 0
            && unexecuted[AttackPlan.TypeHugeAttack] == 0
            && StartedAttacks[AttackPlan.TypeDefault].Count
                + StartedAttacks[AttackPlan.TypeHugeAttack].Count
                < Math.Min(2, 1 + (int)Math.Round(gameState.GetPopulationMax() / 100.0))
            && (StartedAttacks[AttackPlan.TypeDefault].Count
                    + StartedAttacks[AttackPlan.TypeHugeAttack].Count == 0
                || gameState.GetPopulationMax() - gameState.GetPopulation() > 12))
        {
            bool canAggress = barracksNb >= 1
                && (gameState.CurrentPhase() > 1
                    || gameState.IsResearching(gameState.GetPhaseName(2)));
            if (canAggress || Hq == null || !Hq.HasPotentialBase(gameState))
            {
                // 前两次普通进攻,之后且已有 Huge 进行中 → 仍普通;否则升级 Huge。
                string type = _attackNumber < 2
                    || StartedAttacks[AttackPlan.TypeHugeAttack].Count > 0
                    ? AttackPlan.TypeDefault : AttackPlan.TypeHugeAttack;
                var plan = new AttackPlan(gameState, _totalNumber, type, _config);
                plan.Init(gameState, queues);
                plan.SetInitialRallyPoint(gameState);
                UpcomingAttacks[type].Add(plan);
                _totalNumber++;
                _attackNumber++;
            }
        }

        // 攻城游击(原版 update 尾段):闲置远程攻城器骚扰敌建筑(难度 > VeryEasy,
        // 每 5 回合一次)。
        if (_config.Difficulty > DifficultyLevel.VeryEasy
            && (gameState.Net?.CurrentTurn ?? 0) % 5 == 0)
            AssignBombers(gameState);

        // Raid:defenseManager.targetList 有敌地基目标时发起(原版同款)。
        if (unexecuted[AttackPlan.TypeRaid] == 0 && Hq != null
            && Hq.DefenseManager.TargetList.Count > 0)
        {
            var plan = new AttackPlan(gameState, _totalNumber, AttackPlan.TypeRaid, _config);
            plan.Init(gameState, queues);
            plan.SetInitialRallyPoint(gameState);
            UpcomingAttacks[AttackPlan.TypeRaid].Add(plan);
            _totalNumber++;
        }
    }

    // ── 事件(原版 checkEvents:玩家战败标记)──

    // ── 攻击请求(原版 checkEvents 的 AttackRequest 段):盟友请求攻某敌——
    // 筹备中的计划改指目标玩家,可动兵力 >12 即强推(forceStart);答复发起方
    /// 经 sim 事件(原版 chat.answerAttackRequest;AI 间同进程)。</summary>
    private void OnAttackRequest(GameState gameState, int sourcePlayer, int targetPlayer)
    {
        if (!gameState.IsPlayerAlly(sourcePlayer) || !gameState.IsPlayerEnemy(targetPlayer))
            return;
        int available = 0;
        int? other = null;
        foreach (var plan in UpcomingAttacks.Values.SelectMany(l => l))
        {
            if (plan.State == AttackPlan.AttackState.Completing)
            {
                if (plan.TargetPlayer == targetPlayer)
                    available += plan.UnitCollection.Count;
                else if (plan.TargetPlayer != null && plan.TargetPlayer != targetPlayer)
                    other = plan.TargetPlayer;
                continue;
            }
            plan.TargetPlayer = targetPlayer;   // 筹备中计划改指(原版同款)
            if (plan.UnitCollection.Count > 2)
                available += plan.UnitCollection.Count;
        }
        if (available > 12)
        {
            foreach (var plan in UpcomingAttacks.Values.SelectMany(l => l))
            {
                if (plan.State == AttackPlan.AttackState.Completing
                    || plan.TargetPlayer != targetPlayer || plan.UnitCollection.Count < 3)
                    continue;
                plan.ForceStartImmediate(gameState);   // 立即推(原版 forceStart)
            }
        }
        // 答复发起方(原版 attackAnswer chat;我们走 sim 事件,AI 侧记录即可)。
        gameState.Cm.Events.RaiseAttackAnswered(new Events.AttackAnsweredEvent
        {
            SourcePlayer = gameState.PlayerId,
            TargetPlayer = targetPlayer,
            Accepted = available > 12,
        });
    }

    /// <summary>事件订阅(攻击请求走 sim 事件总线,原版 events.AttackRequest)。</summary>
    public void Attach(GameState gameState)
    {
        gameState.Cm.Events.AttackRequested += e =>
            OnAttackRequest(gameState, e.SourcePlayer, e.TargetPlayer);
    }

    private void CheckEvents(GameState gameState, AIEventBuffer events)
    {
        foreach (var ev in events.Events)
            if (ev.Type == AIEventType.PlayerDefeated && ev.IntParam > 0)
            {
                // 战败者 id 在 IntParam(AIEventBuffer 的 PlayerDefeated 载荷)。
                _defeated.Add(ev.IntParam);
                if (_currentEnemyPlayer == ev.IntParam)
                    _currentEnemyPlayer = null;
            }
    }

    // ── 攻城游击(原版 assignBombers 逐字逻辑)──

    /// <summary>闲置远程攻城器(BoltShooter/StoneThrower)骚扰射程内敌建筑:
    /// 清理失效/跑偏 → 每个闲攻城器找最近可打建筑,超程则推进到射程边
    /// (领土安全校验:安全圈不深入敌领,射程点须我领+同陆);同目标 ≤4 人。</summary>
    private void AssignBombers(GameState gameState)
    {
        // 清理:目标死了/不再敌 → 整张撕;单位死了/不再我/单已指 → 摘除。
        foreach (var (targetId, unitIds) in BombingAttacks.ToList())
        {
            var target = gameState.GetEntityById(targetId);
            if (target == null || !gameState.IsPlayerEnemy(target.Owner))
            {
                BombingAttacks.Remove(targetId);
                continue;
            }
            foreach (var entId in unitIds.ToList())
            {
                var ent = gameState.GetEntityById(entId);
                // 原版:最后订单仍指目标且非应急计划 → 保留在册。
                if (!(ent != null && ent.Owner == gameState.PlayerId
                    && ent.UnitAIOrderTarget is { } t2 && t2.Value == targetId
                    && (gameState.Metadata.GetObject(entId, "plan") is not int pl
                        || pl == -1)))
                    unitIds.Remove(entId);
            }
            if (unitIds.Count == 0)
                BombingAttacks.Remove(targetId);
        }

        var territory = SimSystem.Territory;
        foreach (var ent in gameState.GetOwnUnits().Values()
            .Where(e => e.HasClass("BoltShooter") || e.HasClass("StoneThrower"))
            .OrderBy(e => e.Id))
        {
            if (ent.Position2D == default || !ent.IsIdle) continue;
            float range = ent.Template.GetFloat("Attack/Ranged/MaxRange");
            if (range <= 0) continue;
            var planMeta = gameState.Metadata.GetObject(ent.Id, "plan");
            if (planMeta is int pm && (pm == -2 || pm == -3)) continue;
            if (planMeta is int pp && pp != -1)
            {
                var subrole = gameState.Metadata.GetObject(ent.Id, "subrole")?.ToString();
                if (subrole is WorkerRoles.SubroleCompleting or WorkerRoles.SubroleWalking
                    or WorkerRoles.SubroleAttacking) continue;
            }
            if (BombingAttacks.Values.Any(u => u.Contains(ent.Id))) continue;

            ushort entAccess = gameState.Accessibility?.GetAccessValue(
                ent.Position2D.X.ToFloat(), ent.Position2D.Y.ToFloat()) ?? (ushort)0;
            foreach (var structure in gameState.GetEnemyStructures().Values()
                .OrderBy(st => st.Id))
            {
                if (!ent.CanAttackTarget(structure)) continue;
                if (structure.Position2D == default) continue;
                // 田:有人采且地主为敌才打(原版 Field 分支)。
                if (structure.HasClass("Field") && territory != null)
                {
                    int owner = territory.GetOwner(
                        structure.Position2D.X, structure.Position2D.Y);
                    if (!gameState.IsPlayerEnemy(owner)) continue;
                }

                float ex = ent.Position2D.X.ToFloat(), ez = ent.Position2D.Y.ToFloat();
                float sx = structure.Position2D.X.ToFloat(), sz = structure.Position2D.Y.ToFloat();
                float dist = MathF.Sqrt((ex - sx) * (ex - sx) + (ez - sz) * (ez - sz));

                float moveX = ex, moveZ = ez;
                bool needMove = dist > range;
                if (needMove)
                {
                    // 原版:安全圈(足迹半径+30)不能深入敌领;射程点须我领+同陆。
                    float safety = ObstructionRadiusOf(structure) + 30;
                    float tx = sx + (ex - sx) * safety / dist;
                    float tz = sz + (ez - sz) * safety / dist;
                    int tOwner = territory?.GetOwner(
                        Maths.Fixed.FromFloat(tx), Maths.Fixed.FromFloat(tz)) ?? 0;
                    if (tOwner != 0 && gameState.IsPlayerEnemy(tOwner)) continue;
                    tx = sx + (ex - sx) * range / dist;
                    tz = sz + (ez - sz) * range / dist;
                    if (territory != null && territory.GetOwner(
                        Maths.Fixed.FromFloat(tx), Maths.Fixed.FromFloat(tz)) != gameState.PlayerId)
                        continue;
                    if (gameState.Accessibility != null
                        && gameState.Accessibility.GetAccessValue(tx, tz) != entAccess)
                        continue;
                    moveX = tx; moveZ = tz;
                }

                if (!BombingAttacks.TryGetValue(structure.Id, out var attacking))
                    BombingAttacks[structure.Id] = attacking = new HashSet<uint>();
                if (attacking.Count > 4) continue;   // 原版:同目标至多 4 台
                attacking.Add(ent.Id);
                if (needMove)
                    gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Move(
                        (uint)gameState.PlayerId, ent.Id,
                        Maths.Fixed.FromFloat(moveX), Maths.Fixed.FromFloat(moveZ)));
                gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Attack(
                    (uint)gameState.PlayerId, ent.Id, structure.Id));
                break;
            }
        }
    }

    /// <summary>建筑障碍半径(Obstruction Static 宽深取大之半;原版 footprintRadius 近似)。</summary>
    private static float ObstructionRadiusOf(AIEntity ent)
    {
        float w = ent.Template.GetFloat("Obstruction/Static/@width");
        float d = ent.Template.GetFloat("Obstruction/Static/@depth");
        return Math.Max(w, d) / 2f;
    }

    /// <summary>圣物袭击(原版 victoryManager 的 forced Raid:uniqueTarget 圣物,
    /// 少量快单位强推——forced Raid 跳过常规编组等待)。</summary>
    public void StartRelicRaid(GameState gameState, AIEntity relic)
    {
        var plan = new AttackPlan(gameState, _totalNumber, AttackPlan.TypeRaid, _config)
        {
            UniqueTargetId = relic.Id,
        };
        _totalNumber++;
        plan.Init(gameState, Hq != null ? Hq.Queues : new QueueManager(_config));
        plan.SetInitialRallyPoint(gameState);
        plan.Target = relic.Id;
        plan.TargetPlayer = relic.Owner;
        plan.TargetPos = relic.Position2D;
        // 立即进入 Completing(集结 20s,Raid 原版 maxCompletingTime 同款)——
        // 人员由 UpdatePreparation 的 assignUnits 每轮补充,到点即推。
        plan.ForceStartImmediate(gameState);
        UpcomingAttacks[AttackPlan.TypeRaid].Add(plan);
    }

    // ── 目标玩家选择(原版 getEnemyPlayer 逐字逻辑)──

    public int? GetEnemyPlayer(GameState gameState, AttackPlan attack)
    {
        // (奇迹/圣物胜利条件的偏好玩家未移植——胜利管理器深化时补。)

        var veto = new HashSet<int>(_defeated);
        // Rush 不挑防守过强的敌(>6 防御建筑,原版"iberians 条款")。
        if (attack.Type == AttackPlan.TypeRush)
            foreach (int i in gameState.GetEnemies())
            {
                if (veto.Contains(i)) continue;
                int enemyDefense = gameState.GetEnemyStructures().Values()
                    .Count(e => e.Owner == i
                        && (e.HasClass("Tower") || e.HasClass("WallTower") || e.HasClass("Fortress")));
                if (enemyDefense > 6) veto.Add(i);
            }

        // 非 Huge:保持当前目标(原版 currentEnemyPlayer 粘性——其有实体即续打)。
        if (attack.Type != AttackPlan.TypeHugeAttack)
        {
            if (attack.TargetPlayer == null && _currentEnemyPlayer != null
                && !_defeated.Contains(_currentEnemyPlayer.Value)
                && gameState.IsPlayerEnemy(_currentEnemyPlayer.Value)
                && HasAnyEntity(gameState, _currentEnemyPlayer.Value))
                return _currentEnemyPlayer;

            // 同陆最近敌 CC(原版:我方每座 CC 找同陆最近敌 CC,取全局最近)。
            int? ccmin = null;
            float distmin = float.MaxValue;
            var allCcs = gameState.GetStructures().Values()
                .Where(e => e.HasClass("CivCentre") && e.Position2D != default)
                .ToList();
            foreach (var ourcc in allCcs.Where(e => e.Owner == gameState.PlayerId))
            {
                ushort access = gameState.Accessibility?.GetAccessValue(
                    ourcc.Position2D.X.ToFloat(), ourcc.Position2D.Y.ToFloat()) ?? (ushort)0;
                foreach (var enemycc in allCcs)
                {
                    if (enemycc.Owner == gameState.PlayerId || veto.Contains(enemycc.Owner)) continue;
                    if (!gameState.IsPlayerEnemy(enemycc.Owner)) continue;
                    if (gameState.Accessibility != null
                        && gameState.Accessibility.GetAccessValue(
                            enemycc.Position2D.X.ToFloat(), enemycc.Position2D.Y.ToFloat()) != access)
                        continue;
                    float dx = ourcc.Position2D.X.ToFloat() - enemycc.Position2D.X.ToFloat();
                    float dz = ourcc.Position2D.Y.ToFloat() - enemycc.Position2D.Y.ToFloat();
                    float dist = dx * dx + dz * dz;
                    if (dist < distmin) { distmin = dist; ccmin = enemycc.Owner; }
                }
            }
            if (ccmin != null)
            {
                if (attack.TargetPlayer == null) _currentEnemyPlayer = ccmin;
                return ccmin;
            }
        }

        // 最强敌(实体计数;有 CC +500)。
        int? enemyPlayer = null;
        int max = 0;
        foreach (int i in gameState.GetEnemies())
        {
            if (veto.Contains(i)) continue;
            int count = 0;
            bool hasCc = false;
            foreach (var e in gameState.GetEntities(i).Values())
            {
                count++;
                if (e.HasClass("CivCentre")) hasCc = true;
            }
            if (hasCc) count += 500;
            if (count == 0 || count < max) continue;
            max = count;
            enemyPlayer = i;
        }
        if (attack.TargetPlayer == null) _currentEnemyPlayer = enemyPlayer;
        return enemyPlayer;
    }

    private static bool HasAnyEntity(GameState gameState, int playerId)
        => gameState.GetEntities(playerId).HasEntities();

    // ── 序列化(原版 attackManager.js Serialize;计数器/回收池/计划全量)──
    public void Serialize(Serialization.ISerializer s)
    {
        s.NumberI32("total", _totalNumber);
        s.NumberI32("rush", _rushNumber);
        s.NumberI32("attack", _attackNumber);
        s.NumberI32("currentEnemy", _currentEnemyPlayer ?? -1);
        s.NumberI32("defeated", _defeated.Count);
        foreach (var p in _defeated.OrderBy(p => p)) s.NumberI32("d", p);
        s.NumberI32("outOfPlan", OutOfPlan.Count);
        foreach (var id in OutOfPlan.OrderBy(id => id)) s.NumberU32("o", id);
        foreach (var (bucket, list) in new[]
            { ("up", UpcomingAttacks), ("st", StartedAttacks) })
        {
            s.StringASCII("bucket", bucket);
            int total = list.Values.Sum(v => v.Count);
            s.NumberI32("plans", total);
            foreach (var type in new[] { AttackPlan.TypeRush, AttackPlan.TypeRaid,
                AttackPlan.TypeDefault, AttackPlan.TypeHugeAttack })
                foreach (var plan in list[type])
                    plan.Serialize(s);
        }
    }

    /// <summary>重建(写序逐位一致;计划经 AttackPlan.Deserialize 全量还原,
    /// 队列由 HQ 的 QueueManager 序列另行恢复——plan_* 队列在其中)。</summary>
    public void Deserialize(Serialization.IDeserializer d, GameState gameState)
    {
        _totalNumber = d.NumberI32("total");
        _rushNumber = d.NumberI32("rush");
        _attackNumber = d.NumberI32("attack");
        _currentEnemyPlayer = d.NumberI32("currentEnemy") is { } c && c >= 0 ? c : null;
        int defeated = d.NumberI32("defeated");
        for (int i = 0; i < defeated; i++) _defeated.Add(d.NumberI32("d"));
        int outOfPlan = d.NumberI32("outOfPlan");
        for (int i = 0; i < outOfPlan; i++) OutOfPlan.Add(d.NumberU32("o"));
        foreach (var list in new[] { UpcomingAttacks, StartedAttacks })
        {
            string bucket = d.StringASCII("bucket");
            int plans = d.NumberI32("plans");
            for (int i = 0; i < plans; i++)
            {
                var plan = AttackPlan.Deserialize(d, gameState, _config);
                // 读回时队列引用重挂(HQ.Queues 由 QueueManager.Deserialize 先行恢复)。
                if (Hq != null) plan.WireQueues(Hq.Queues);
                list[plan.Type].Add(plan);
            }
        }
    }

    /// <summary>防御军转攻(原版 switchDefenseToAttack):为指定目标起 uniqueTarget
    /// 进攻计划并立即启动(绕过筹备——原版直推 startedAttacks),军队同陆成员
    /// 全部转隶(240m 内)。</summary>
    public bool SwitchDefenseToAttack(GameState gameState, AIEntity target, int armyId)
    {
        if (target.Position2D == default) return false;
        var plan = new AttackPlan(gameState, _totalNumber, AttackPlan.TypeDefault, _config)
        {
            UniqueTargetId = target.Id,
        };
        _totalNumber++;
        // 原版直推 startedAttacks 不走筹备,队列注册走 HQ 的 QueueManager
        // (Init 注册 plan_* 三条;RemoveQueues 收尾要用同表)。
        plan.Init(gameState, Hq != null ? Hq.Queues : new QueueManager(_config));
        plan.SetInitialRallyPoint(gameState);
        // 原版:直推 startedAttacks 并 forceStart。我们走:目标已定 → Completing
        // 立即超时路径(集结 0s)→ 下轮 UpdatePreparation 返回 Start → StartAttack。
        plan.Target = target.Id;
        plan.TargetPos = target.Position2D;
        plan.ForceStartImmediate(gameState);
        StartedAttacks[AttackPlan.TypeDefault].Add(plan);

        // 军队成员转隶(原版:army.ownEntities 中同陆者 setMetadata plan)。
        var army = Hq?.DefenseManager.GetArmy(armyId);
        if (army != null)
            foreach (var id in army.OwnEntities.ToList())
            {
                var ent = gameState.GetEntityById(id);
                if (ent == null || ent.Position2D == default) continue;
                gameState.Metadata.Set(id, "plan", plan.Name);
                plan.UnitCollection.Add(id);
            }
        return true;
    }
}
