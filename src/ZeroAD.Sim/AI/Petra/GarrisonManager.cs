using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>驻军管理器（原版 petra/garrisonManager.js，393 行的核心闭环移植）。
/// 原版职责:受威胁时把平民/士兵塞进附近建筑(民居/市政中心收平民,防御塔收兵),
/// 威胁解除后放出复工;带 BuffHeal 的建筑收伤员。本版覆盖该核心闭环:
///   - 威胁判定:敌军单位距持有者 ≤ 40m(简化原版的攻击通知+领土判定)。
///   - 召集:威胁在 → 30m 内空闲平民/士兵 → 有空位即下 Garrison 命令(每次每建筑 ≤4)。
///   - 疏散:威胁消 → 对"本管理器塞入的"驻军下 Ungarrison(全部)放回复工;
///     玩家手动驻军不碰(簿记只记自己塞的)。
/// 未移植(记录在案):治疗驻军(BuffHeal 回血编排)、驻防偏好权重(弓手优先进塔)、
/// 占领中的建筑锁定、Javelin 骑兵上骆驼等特殊规则。</summary>
public sealed class GarrisonManager
{
    private readonly PetraConfig _config;

    private const float ThreatRange = 40f;
    private const float MusterRange = 30f;
    private const int MaxPerHolderPerUpdate = 4;

    // 防御驻军簿记:holder → 本管理器塞入的单位集合(死单位/自离单位逐轮清理)。
    private readonly Dictionary<uint, HashSet<uint>> _defenseGarrisons = new();

    public GarrisonManager(PetraConfig config) => _config = config;

    /// <summary>主更新(原版 update;由 HQ 在 tradeManager 之后调用)。</summary>
    public void Update(GameState gameState)
    {
        var cm = gameState.Cm;
        // 簿记清理:holder 没了/单位没了或已不在舱内 → 除名。
        foreach (var kv in _defenseGarrisons.ToList())
        {
            var holderEnt = gameState.GetEntityById(kv.Key);
            var holderCmp = holderEnt != null
                ? cm.QueryInterface<GarrisonHolderComponent>(holderEnt.Entity) : null;
            if (holderCmp == null)
            {
                _defenseGarrisons.Remove(kv.Key);
                continue;
            }
            kv.Value.RemoveWhere(id => !holderCmp.Entities.Contains(new EntityId(id)));
            if (kv.Value.Count == 0)
                _defenseGarrisons.Remove(kv.Key);
        }

        var enemies = gameState.GetEnemyUnits().Values().ToList();
        foreach (var bldg in gameState.GetOwnStructures().Values()
                     .OrderBy(b => b.Id))   // 确定性遍历
        {
            var holder = cm.QueryInterface<GarrisonHolderComponent>(bldg.Entity);
            if (holder == null) continue;

            bool threatened = enemies.Any(e =>
                AIUtils3.SquareDistanceMeters(e.Position2D, bldg.Position2D) < ThreatRange * ThreatRange);

            if (threatened)
                MusterInto(gameState, bldg, holder);
            else
                Evacuate(gameState, bldg);
        }
    }

    /// <summary>召集(原版 garrison 分支):威胁在且有邻兵 → 有空位即塞。
    /// 平民(无攻击件)与士兵皆可;已在编队/驻防/炮塔/有命令在身的跳过(原版只动 idle)。</summary>
    private void MusterInto(GameState gameState, AIEntity bldg, GarrisonHolderComponent holder)
    {
        var cm = gameState.Cm;
        int capacity = holder.GetCapacity(cm);
        int free = capacity - holder.OccupiedSlots(cm);
        if (free <= 0) return;

        int used = 0;
        var candidates = gameState.GetOwnUnits().Values()
            .Where(u => u.IsIdle)
            .Where(u => AIUtils3.SquareDistanceMeters(u.Position2D, bldg.Position2D)
                        < MusterRange * MusterRange)
            .OrderBy(u => AIUtils3.SquareDistanceMeters(u.Position2D, bldg.Position2D))
            .ThenBy(u => u.Id);
        foreach (var u in candidates)
        {
            if (used >= MaxPerHolderPerUpdate || free <= 0) break;
            var ai = cm.QueryInterface<UnitAIComponent>(u.Entity);
            if (ai == null || ai.IsGarrisoned || ai.IsTurret || ai.FormationController != null)
                continue;
            if (!holder.IsAllowedToGarrison(cm, u.Entity)) continue;
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Garrison(
                (uint)gameState.PlayerId, u.Id, bldg.Id));
            if (!_defenseGarrisons.TryGetValue(bldg.Id, out var set))
                _defenseGarrisons[bldg.Id] = set = new HashSet<uint>();
            set.Add(u.Id);
            used++;
            free--;
        }
    }

    /// <summary>疏散(原版 ungarrison 分支):威胁解除 → 放出本管理器塞入的驻军。</summary>
    private void Evacuate(GameState gameState, AIEntity bldg)
    {
        if (!_defenseGarrisons.TryGetValue(bldg.Id, out var set) || set.Count == 0) return;
        var holder = gameState.Cm.QueryInterface<GarrisonHolderComponent>(bldg.Entity);
        if (holder == null) return;
        // 逐个点名放出(Ungarrison unitId 参数;-1=全部会误放玩家手动驻军)。
        foreach (var id in set.ToList())
        {
            if (!holder.Entities.Contains(new EntityId(id)))
            {
                set.Remove(id);
                continue;
            }
            gameState.SubmitCommand(ZeroAD.Sim.Net.NetCommand.Ungarrison(
                (uint)gameState.PlayerId, bldg.Id, (int)id));
            set.Remove(id);
        }
        if (set.Count == 0)
            _defenseGarrisons.Remove(bldg.Id);
    }
}
