using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>进攻管理器（原版 petra/attackManager.js，867 行）。
/// 本端口:按类型分桶(Rush/Raid/Attack/HugeAttack)的进攻生命周期 + 发起轮换 +
/// getEnemyPlayer 目标玩家选择 + defeated 追踪 + outOfPlan 回收池 + 轰炸补丁事件。
/// 原版 bombingAttacks/海图换面(attackPlansEncounteredWater)未移植。</summary>
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
    /// <summary>已败玩家(原版 defeated;checkEvents 维护)。</summary>
    private readonly HashSet<int> _defeated = new();
    /// <summary>当前重点敌(原版 currentEnemyPlayer:非 Huge 进攻保持集火)。</summary>
    private int? _currentEnemyPlayer;

    private int _totalNumber;
    private int _rushNumber;
    private int _attackNumber;
    /// <summary>rush 规模表(原版 rushSize 随 rushNumber 递增)。</summary>
    private static readonly int[] RushSizes = { 6, 10, 14 };
    /// <summary>rush 上限(原版 maxRushes:难度驱动;Easy 0 / Medium 1 / Hard+ 2)。</summary>
    private int MaxRushes => _config.Difficulty <= DifficultyLevel.Easy ? 0
        : _config.Difficulty <= DifficultyLevel.Medium ? 1 : 2;

    public AttackManager(PetraConfig config) => _config = config;

    public bool IsDefeated(int playerId) => _defeated.Contains(playerId);

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
                rushTargetSize: RushSizes[Math.Min(_rushNumber, RushSizes.Length - 1)]);
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
}
