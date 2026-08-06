using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>防御管理器（原版 petra/defenseManager.js，991 行）。
/// 检测威胁、调兵回防、管理防御军。
/// 本版:IsDangerous(距建筑 80m)+ assignDefenders 真下攻击命令(经 SubmitAiCommand
/// 锁步通道),带指派簿记防重复下单;防御军编组(DefenseArmy 集结/合并)仍为骨架。</summary>
public sealed class DefenseManager
{
    private readonly PetraConfig _config;

    /// <summary>单个威胁最多派的防守人数(原版按威胁程度估算兵力,此处定值)。</summary>
    private const int MaxDefendersPerThreat = 4;
    /// <summary>同时响应的威胁上限(多于此时按距离建筑最近者优先)。</summary>
    private const int MaxActiveThreats = 3;

    // 威胁追踪
    public readonly List<uint> TargetList = new();  // 敌方进攻单位 id
    public readonly Dictionary<int, int> AttackedAllies = new();  // 被攻击的盟友 → 次数
    // 指派簿记:防守者 → 威胁(防每 think 重复下单;目标死亡/脱险即清)。
    private readonly Dictionary<uint, uint> _assignments = new();

    public DefenseManager(PetraConfig config) => _config = config;

    /// <summary>主更新（原版 defenseManager.js:28-990）。</summary>
    public void Update(GameState gameState, AIEventBuffer events)
    {
        CheckEvents(gameState, events);

        // 清理失效目标
        TargetList.RemoveAll(id => gameState.GetEntityById(id) == null);

        // 检查敌方单位威胁 + 调兵回防
        AssignDefenders(gameState);
    }

    private void CheckEvents(GameState gameState, AIEventBuffer events)
    {
        foreach (var ev in events.Events)
        {
            if (ev.Type == AIEventType.OwnershipChanged && ev.IntParam2 == gameState.PlayerId)
            {
                // 实体归我方 → 可能是刚训练/占领，忽略
            }
        }
    }

    /// <summary>判断敌方单位是否威胁我方（原版 isDangerous，96-398 行）。
    /// 简化版：距离我方建筑 < 80 单位 → 威胁。(阈值判定用米制距离——
    /// SquareDistance 的内部定点平方法只能排序。)</summary>
    public bool IsDangerous(GameState gameState, AIEntity enemy)
    {
        const float threatRangeSq = 80f * 80f;
        foreach (var bldg in gameState.GetOwnStructures().Values())
        {
            if (AIUtils3.SquareDistanceMeters(enemy.Position2D, bldg.Position2D) < threatRangeSq)
                return true;
        }
        return false;
    }

    /// <summary>调兵回防（原版 assignDefenders，399-544 行）。
    /// 威胁按"距我方最近建筑"升序(近的优先),每个威胁最多 4 名防守者;
    /// 从空闲可战单位中取最近者下 Attack 命令。已指派者不重下(目标死后其
    /// Attack 订单自行收工回 IDLE,簿记下轮清理)。</summary>
    private void AssignDefenders(GameState gameState)
    {
        // 簿记清理:防守者死了/目标死了或脱险 → 除名。
        foreach (var kv in _assignments.ToList())
        {
            var soldier = gameState.GetEntityById(kv.Key);
            var threat = gameState.GetEntityById(kv.Value);
            if (soldier == null || threat == null || !IsDangerous(gameState, threat))
                _assignments.Remove(kv.Key);
        }

        var enemies = gameState.GetEnemyUnits().Values()
            .Where(e => IsDangerous(gameState, e))
            .OrderBy(e => NearestOwnStructureDistSq(gameState, e))
            .ThenBy(e => e.Id)   // 确定性 tie-break
            .Take(MaxActiveThreats)
            .ToList();
        if (enemies.Count == 0) return;

        // 空闲可战且未指派的兵力。
        var idleSoldiers = gameState.GetOwnUnits().Values()
            .Where(e => e.CanAttack && e.IsIdle && !_assignments.ContainsKey(e.Id))
            .ToList();
        if (idleSoldiers.Count == 0) return;

        foreach (var enemy in enemies)
        {
            int assigned = _assignments.Values.Count(t => t == enemy.Id);
            while (assigned < MaxDefendersPerThreat)
            {
                var nearest = idleSoldiers
                    .OrderBy(s => AIUtils3.SquareDistance(s.Position2D, enemy.Position2D))
                    .ThenBy(s => s.Id)
                    .FirstOrDefault();
                if (nearest == null) return;   // 无兵可派
                idleSoldiers.Remove(nearest);
                _assignments[nearest.Id] = enemy.Id;
                gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Attack(
                    (uint)gameState.PlayerId, nearest.Id, enemy.Id));
                assigned++;
            }
        }
    }

    private static float NearestOwnStructureDistSq(GameState gameState, AIEntity enemy)
    {
        float best = float.MaxValue;
        foreach (var bldg in gameState.GetOwnStructures().Values())
        {
            float d = AIUtils3.SquareDistanceMeters(enemy.Position2D, bldg.Position2D);
            if (d < best) best = d;
        }
        return best;
    }
}

/// <summary>防御军（原版 petra/defenseArmy.js，660 行）。
/// 编组防御力量、追踪位置、合并/解散。
/// 骨架版——实体集合 + update 结构。</summary>
public sealed class DefenseArmy
{
    public readonly HashSet<uint> Entities = new();
    public FixedVector2D Position;

    public enum ArmyState { Gathering, Attacking, Disbanding }

    public ArmyState State { get; private set; }

    public void AddEntity(uint id) => Entities.Add(id);
    public void RemoveEntity(uint id) => Entities.Remove(id);

    /// <summary>更新（原版 defenseArmy.js:574-660）。
    /// 简化版：清理死单位 + 检查是否应解散。</summary>
    public void Update(GameState gameState)
    {
        // 清理死单位
        Entities.RemoveWhere(id => gameState.GetEntityById(id) == null);
        // 无单位 → 解散
        if (Entities.Count == 0) State = ArmyState.Disbanding;
    }

    /// <summary>合并两支军队（原版 merge）。</summary>
    public void Merge(DefenseArmy other)
    {
        foreach (var id in other.Entities) Entities.Add(id);
        other.Entities.Clear();
        other.State = ArmyState.Disbanding;
    }
}
