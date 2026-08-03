using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>防御管理器（原版 petra/defenseManager.js，991 行）。
/// 检测威胁、调兵回防、管理防御军。
/// 骨架版——update 结构 + IsDangerous 移植，assignDefenders 标 TODO。</summary>
public sealed class DefenseManager
{
    private readonly PetraConfig _config;

    // 威胁追踪
    public readonly List<uint> TargetList = new();  // 敌方进攻单位 id
    public readonly Dictionary<int, int> AttackedAllies = new();  // 被攻击的盟友 → 次数

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
    /// 简化版：距离我方建筑 < 80 单位 → 威胁。</summary>
    public bool IsDangerous(GameState gameState, AIEntity enemy)
    {
        const double threatRangeSq = 80 * 80;
        foreach (var bldg in gameState.GetOwnStructures().Values())
        {
            if (AIUtils3.SquareDistance(enemy.Position2D, bldg.Position2D) < threatRangeSq)
                return true;
        }
        return false;
    }

    /// <summary>调兵回防（原版 assignDefenders，399-544 行）。
    /// 简化版：威胁在我方领土时，调最近 idle 兵力防御。</summary>
    private void AssignDefenders(GameState gameState)
    {
        var enemies = gameState.GetEnemyUnits().Values()
            .Where(e => IsDangerous(gameState, e)).ToList();
        if (enemies.Count == 0) return;

        // 找 idle 兵力
        var soldiers = gameState.GetOwnUnits().Values()
            .Where(e => e.CanAttack && e.IsIdle).ToList();
        if (soldiers.Count == 0) return;

        // 简化：最近兵攻击最近威胁
        foreach (var enemy in enemies)
        {
            var nearest = soldiers
                .OrderBy(s => AIUtils3.SquareDistance(s.Position2D, enemy.Position2D))
                .FirstOrDefault();
            if (nearest == null) continue;
            // TODO: 下达 attack 命令（需 NetCommand.Attack + SubmitAiCommand）
        }
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
