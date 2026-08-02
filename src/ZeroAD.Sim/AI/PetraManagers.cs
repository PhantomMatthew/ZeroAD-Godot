using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;

namespace ZeroAD.Sim.AI;

// 搬自原 godot/Scripts/PetraManagers.cs(Phase 2 内核驻留)。零 Godot 依赖。
//
// 所有 manager 经 NetCommand + NetTurnManager.SubmitAiCommand 下令(盖 _playerId——AI 的玩家槽)。
// SubmitAiCommand 是 AI 专用的本地锁步通道:命令永不进网络 outbox,各端确定性同生成,
// 故 AI 无需 _expectedPlayers 网络槽,MP 下不重复、不 OOS。时序与人类 SubmitLocalCommand 一致
// (currentTurn + commandDelay 执行)。
//
// _playerId 不在 ctor 注入——Configure(cm,net) 时 AIComponent.Entity 尚未 SetEntity,无法从
// OwnershipComponent 派生。改为 Update(snap, playerId) 入口赋值,查询代码仍用 _playerId 不变。
//
// 计时字段计 THINKS 不计秒(对齐原版):AIComponent 每 5 sim 回合调一次 Update,阈值已按原
// 0.5s/think 节拍换算(BuildManager 每 10 thinks ≈ 5s,等等)。计数器 internal 供 AIComponent 序列化。

public sealed class EconomyManager
{
    private readonly ComponentManager _cm;
    private readonly NetTurnManager _net;
    private uint _playerId;

    internal int _targetVillagers = 12;
    internal int _allocThinkCount;

    public EconomyManager(ComponentManager cm, NetTurnManager net) { _cm = cm; _net = net; }

    public void Update(AISnapshot snap, uint playerId)
    {
        _playerId = playerId;
        EnsureTraining(snap);
        AssignIdleVillagers(snap);
        ManageGatherRatios(snap);
    }

    private void EnsureTraining(AISnapshot snap)
    {
        if (snap.Villagers.Count >= _targetVillagers) return;
        if (snap.Player.Food < 50) return;

        foreach (var building in snap.Buildings)
        {
            var queue = _cm.QueryInterface<ProductionQueue>(building);
            var identity = _cm.QueryInterface<IdentityComponent>(building);
            if (queue == null || identity == null) continue;
            if (!identity.Name.Contains("Center") && !identity.Name.Contains("civil_centre")) continue;
            if (queue.QueueCount > 0) continue;

            // 数据驱动:从建筑可训练列表选首个村民(support_civilian),文明正确
            // (此前硬编码 units/spart/*,高卢 AI 出斯巴达兵)。
            string pick = "units/spart/support_civilian";
            foreach (var t in queue.GetTrainableEntities(_cm))
                if (t.Contains("support_civilian")) { pick = t; break; }
            _net.SubmitAiCommand(NetCommand.Train(_playerId, building.Value, pick));
            return;
        }
    }

    private void AssignIdleVillagers(AISnapshot snap)
    {
        foreach (var villager in snap.Villagers)
        {
            var gatherer = _cm.QueryInterface<ResourceGatherer>(villager);
            if (gatherer == null || gatherer.State != ResourceGatherer.GatherState.Idle) continue;

            ResourceType target = DetermineNeededResource(snap.Player);
            var resource = FindNearest(villager, target);
            if (resource.HasValue)
                _net.SubmitAiCommand(NetCommand.Gather(_playerId, villager.Value, resource.Value.Value));
        }
    }

    private ResourceType DetermineNeededResource(PlayerComponent player)
    {
        if (player.Food < 100) return ResourceType.Food;
        if (player.Wood < 150) return ResourceType.Wood;
        if (player.Metal < 50) return ResourceType.Metal;
        if (player.Stone < 50) return ResourceType.Stone;
        return ResourceType.Wood;
    }

    private void ManageGatherRatios(AISnapshot snap)
    {
        // 每 20 thinks ≈ 10s(原 _allocTimer >= 10f @ 0.5s/think)。
        if (++_allocThinkCount < 20) return;
        _allocThinkCount = 0;

        if (snap.Player.Food > 500 && snap.Player.Wood < 200)
        {
            for (int i = 0; i < snap.Villagers.Count / 3; i++)
            {
                var v = snap.Villagers[i];
                var r = FindNearest(v, ResourceType.Wood);
                if (r.HasValue) _net.SubmitAiCommand(NetCommand.Gather(_playerId, v.Value, r.Value.Value));
            }
        }
    }

    private EntityId? FindNearest(EntityId from, ResourceType type) =>
        AiUtils.FindNearest(_cm, from, e =>
        {
            var s = _cm.QueryInterface<ResourceSupply>(e);
            return s != null && !s.IsEmpty && s.Type == type;
        });
}

public sealed class BuildManager
{
    private readonly ComponentManager _cm;
    private readonly NetTurnManager _net;
    private uint _playerId;

    internal int _buildThinkCount;

    public BuildManager(ComponentManager cm, NetTurnManager net) { _cm = cm; _net = net; }

    public void Update(AISnapshot snap, uint playerId)
    {
        _playerId = playerId;
        // 每 10 thinks ≈ 5s(原 _buildTimer >= 5f @ 0.5s/think)。
        if (++_buildThinkCount < 10) return;
        _buildThinkCount = 0;

        if (snap.Player.Wood < 150) return;

        if (snap.Player.PopUsed >= snap.Player.PopulationLimit - 4)
            TryBuild("House", snap);
        else if (!HasBuilding("Barracks", snap) && snap.Player.Wood >= 200)
            TryBuild("Barracks", snap);
        else if (CountBuildings("House", snap) < 3 && snap.Player.Wood >= 100)
            TryBuild("House", snap);
    }

    private bool HasBuilding(string name, AISnapshot snap) =>
        snap.Buildings.Any(b =>
        {
            var identity = _cm.QueryInterface<IdentityComponent>(b);
            return identity != null && identity.Name.Contains(name);
        });

    private int CountBuildings(string name, AISnapshot snap) =>
        snap.Buildings.Count(b =>
        {
            var identity = _cm.QueryInterface<IdentityComponent>(b);
            return identity != null && identity.Name.Contains(name);
        });

    private void TryBuild(string name, AISnapshot snap)
    {
        var builder = snap.Villagers.FirstOrDefault(u =>
            _cm.QueryInterface<BuilderComponent>(u) != null);
        if (builder.Equals(default(EntityId))) return;

        var tc = snap.Buildings.FirstOrDefault(b =>
        {
            var identity = _cm.QueryInterface<IdentityComponent>(b);
            return identity != null && identity.Name.Contains("Center");
        });
        if (tc.Equals(default(EntityId))) return;

        var tcPos = _cm.QueryInterface<PositionComponent>(tc);
        if (tcPos == null) return;

        // 内核 Rand48 抖动(序列化进 OOS hash + 存档)——GD.Randf 会在 MP 对端/读档后发散。
        double jitterX = _cm.RNG.NextDouble() * 15.0;
        double jitterZ = _cm.RNG.NextDouble() * 15.0;
        float bx = tcPos.Position.X.ToFloat() + 35f + (float)jitterX;
        float bz = tcPos.Position.Z.ToFloat() + (float)jitterZ;

        // 单锁步命令替代旧 3 连。SimCommandExecutor.ApplyBuild 在执行回合扣费(CanAfford+Spend)、
        // 生地基(owner=_playerId)、派建造者。地基下回合 think 经 AIComponent.RebuildOwned 入 owned。
        _net.SubmitAiCommand(NetCommand.Build(_playerId, builder.Value,
            AiUtils.MapBuildNameToTemplate(name), Fixed.FromFloat(bx), Fixed.FromFloat(bz)));
    }
}

public sealed class ResearchManager
{
    private readonly ComponentManager _cm;
    private readonly NetTurnManager _net;
    private uint _playerId;

    internal int _researchThinkCount;

    public ResearchManager(ComponentManager cm, NetTurnManager net) { _cm = cm; _net = net; }

    public void Update(AISnapshot snap, uint playerId)
    {
        _playerId = playerId;
        // 每 30 thinks ≈ 15s(原 _researchTimer >= 15f @ 0.5s/think)。
        if (++_researchThinkCount < 30) return;
        _researchThinkCount = 0;

        // TechnologyManager 挂在玩家实体上——从 _playerId 派生(原 PetraAI 注入 _playerEntity 字段,改派生)。
        var playerEntity = _cm.GetPlayerEntityId((int)_playerId);
        var techMgr = playerEntity.HasValue
            ? _cm.QueryInterface<TechnologyManager>(playerEntity.Value) : null;
        if (techMgr == null) return;

        foreach (var building in snap.Buildings)
        {
            var researcher = _cm.QueryInterface<ResearcherComponent>(building);
            if (researcher == null || researcher.IsResearching) continue;

            string? tech = PickNextTech(techMgr);
            if (tech != null)
                _net.SubmitAiCommand(NetCommand.Research(_playerId, building.Value, tech));
        }
    }

    private string? PickNextTech(TechnologyManager techMgr)
    {
        // 真实 JSON 科技名。CanResearch 含前置/pair/重复判定;资源够否由 ApplyResearch 的 CanAfford 把关。
        if (techMgr.CanResearch("phase_town_generic")) return "phase_town_generic";
        if (techMgr.CanResearch("gather_capacity_wheelbarrow")) return "gather_capacity_wheelbarrow";
        if (techMgr.CanResearch("gather_lumbering_sharpaxes")) return "gather_lumbering_sharpaxes";
        if (techMgr.CanResearch("soldier_attack_ranged_01")) return "soldier_attack_ranged_01";
        if (techMgr.CanResearch("soldier_resistance_pierce_01")) return "soldier_resistance_pierce_01";
        return null;
    }
}

public sealed class DefenseManager
{
    private readonly ComponentManager _cm;
    private readonly NetTurnManager _net;
    private uint _playerId;

    public DefenseManager(ComponentManager cm, NetTurnManager net) { _cm = cm; _net = net; }

    public void Update(AISnapshot snap, uint playerId)
    {
        _playerId = playerId;
        if (snap.EnemyUnits.Count == 0) return;

        var threat = FindThreatNearBase(snap);
        if (threat == null) return;

        foreach (var soldier in snap.Soldiers)
        {
            var attack = _cm.QueryInterface<AttackComponent>(soldier);
            if (attack == null || attack.State == AttackComponent.AttackState.Attacking) continue;
            _net.SubmitAiCommand(NetCommand.Attack(_playerId, soldier.Value, threat.Value.Value, allowCapture: true));
        }
    }

    private EntityId? FindThreatNearBase(AISnapshot snap)
    {
        foreach (var building in snap.Buildings)
        {
            var bpos = _cm.QueryInterface<PositionComponent>(building);
            if (bpos == null) continue;

            foreach (var enemy in snap.EnemyUnits)
            {
                var epos = _cm.QueryInterface<PositionComponent>(enemy);
                if (epos == null) continue;

                float dx = epos.Position.X.ToFloat() - bpos.Position.X.ToFloat();
                float dz = epos.Position.Z.ToFloat() - bpos.Position.Z.ToFloat();
                if (dx * dx + dz * dz < 40 * 40)
                    return enemy;
            }
        }
        return null;
    }
}

public sealed class AttackManager
{
    private readonly ComponentManager _cm;
    private readonly NetTurnManager _net;
    private uint _playerId;

    internal int _attackThinkCount;
    private const int AttackWaveSize = 5;
    private const int AttackIntervalThinks = 40; // ≈ 20s(原 20f @ 0.5s/think)

    public AttackManager(ComponentManager cm, NetTurnManager net) { _cm = cm; _net = net; }

    public void Update(AISnapshot snap, uint playerId)
    {
        _playerId = playerId;
        EnsureMilitaryProduction(snap);

        if (++_attackThinkCount < AttackIntervalThinks) return;
        if (snap.Soldiers.Count < AttackWaveSize) return;
        if (snap.EnemyBuildings.Count == 0 && snap.EnemyUnits.Count == 0) return;

        _attackThinkCount = 0;
        LaunchAttack(snap);
    }

    private void EnsureMilitaryProduction(AISnapshot snap)
    {
        if (snap.Soldiers.Count >= 10) return;
        if (snap.Player.Food < 80) return;

        foreach (var building in snap.Buildings)
        {
            var queue = _cm.QueryInterface<ProductionQueue>(building);
            var identity = _cm.QueryInterface<IdentityComponent>(building);
            if (queue == null || identity == null) continue;
            if (!identity.Name.Contains("Barracks") && !identity.Name.Contains("Center")) continue;
            if (queue.QueueCount > 0) continue;

            // 数据驱动:从建筑可训练列表选首个战斗单位(非 support 项)。
            string pick = "units/spart/infantry_spearman_b";
            foreach (var t in queue.GetTrainableEntities(_cm))
                if (!t.Contains("support_")) { pick = t; break; }
            _net.SubmitAiCommand(NetCommand.Train(_playerId, building.Value, pick));
            return;
        }
    }

    private void LaunchAttack(AISnapshot snap)
    {
        EntityId? target = snap.EnemyBuildings.FirstOrDefault();
        if (target == null) target = snap.EnemyUnits.FirstOrDefault();
        if (target == null) return;

        var tpos = _cm.QueryInterface<PositionComponent>(target.Value);
        if (tpos == null) return;

        foreach (var soldier in snap.Soldiers)
            _net.SubmitAiCommand(NetCommand.Attack(_playerId, soldier.Value, target.Value.Value, allowCapture: true));
    }
}

/// <summary>AI 每 think 的世界快照(对齐原 PetraAI.AISnapshot)。AIComponent.Tick 构造后传给 5 manager。</summary>
public sealed class AISnapshot
{
    public PlayerComponent Player = null!;
    public List<EntityId> Villagers = new();
    public List<EntityId> Soldiers = new();
    public List<EntityId> Buildings = new();
    public List<EntityId> EnemyUnits = new();
    public List<EntityId> EnemyBuildings = new();
}
