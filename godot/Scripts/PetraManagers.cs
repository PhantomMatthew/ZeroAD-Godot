using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;

namespace ZeroAD.Godot;

// All managers issue orders via NetCommand + SimBridge.SubmitCommand, stamping _playerId
// (the AI's player slot). This routes every decision through the lockstep queue →
// SimCommandExecutor, the single SP/MP chokepoint, so the AI can never diverge from a
// human player's command path. Timer fields count THINKS (not seconds): PetraAI calls
// Update once per think (~every 5 sim turns), so thresholds are scaled to preserve the
// original cadence (e.g. BuildManager fires every 10 thinks ≈ 5s, matching the old 0.5s
// frame-timer × 10).

public sealed class EconomyManager
{
    private readonly SimBridge _sim;
    private readonly uint _playerId;
    private readonly List<EntityId> _units;

    private int _targetVillagers = 12;
    private int _allocThinkCount;

    public EconomyManager(SimBridge sim, uint playerId, List<EntityId> units)
    { _sim = sim; _playerId = playerId; _units = units; }

    public void Update(AISnapshot snap)
    {
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
            var queue = _sim.Sim.QueryInterface<ProductionQueue>(building);
            var identity = _sim.Sim.QueryInterface<IdentityComponent>(building);
            if (queue == null || identity == null) continue;
            if (!identity.Name.Contains("Center") && !identity.Name.Contains("civil_centre")) continue;
            if (queue.QueueCount > 0) continue;

            _sim.SubmitCommand(NetCommand.Train(_playerId, building.Value, "units/spart/support_civilian"));
            return;
        }
    }

    private void AssignIdleVillagers(AISnapshot snap)
    {
        foreach (var villager in snap.Villagers)
        {
            var gatherer = _sim.Sim.QueryInterface<ResourceGatherer>(villager);
            if (gatherer == null || gatherer.State != ResourceGatherer.GatherState.Idle) continue;

            ResourceType target = DetermineNeededResource(snap.Player);
            var resource = FindNearest(villager, target);
            if (resource.HasValue)
                _sim.SubmitCommand(NetCommand.Gather(_playerId, villager.Value, resource.Value.Value));
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
        // Fires every 20 thinks ≈ 10s (was _allocTimer >= 10f at 0.5s/think).
        if (++_allocThinkCount < 20) return;
        _allocThinkCount = 0;

        if (snap.Player.Food > 500 && snap.Player.Wood < 200)
        {
            for (int i = 0; i < snap.Villagers.Count / 3; i++)
            {
                var v = snap.Villagers[i];
                var r = FindNearest(v, ResourceType.Wood);
                if (r.HasValue) _sim.SubmitCommand(NetCommand.Gather(_playerId, v.Value, r.Value.Value));
            }
        }
    }

    private EntityId? FindNearest(EntityId from, ResourceType type)
    {
        var fromPos = _sim.Sim.QueryInterface<PositionComponent>(from);
        if (fromPos == null) return null;
        return _sim.FindNearestEntity(from, e =>
        {
            var s = _sim.Sim.QueryInterface<ResourceSupply>(e);
            return s != null && !s.IsEmpty && s.Type == type;
        });
    }
}

public sealed class BuildManager
{
    private readonly SimBridge _sim;
    private readonly uint _playerId;
    private readonly List<EntityId> _buildings;
    private readonly List<EntityId> _units;

    private int _buildThinkCount;

    public BuildManager(SimBridge sim, uint playerId, List<EntityId> buildings, List<EntityId> units)
    { _sim = sim; _playerId = playerId; _buildings = buildings; _units = units; }

    public void Update(AISnapshot snap)
    {
        // Fires every 10 thinks ≈ 5s (was _buildTimer >= 5f at 0.5s/think).
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
            var identity = _sim.Sim.QueryInterface<IdentityComponent>(b);
            return identity != null && identity.Name.Contains(name);
        });

    private int CountBuildings(string name, AISnapshot snap) =>
        snap.Buildings.Count(b =>
        {
            var identity = _sim.Sim.QueryInterface<IdentityComponent>(b);
            return identity != null && identity.Name.Contains(name);
        });

    private void TryBuild(string name, AISnapshot snap)
    {
        var builder = snap.Villagers.FirstOrDefault(u =>
            _sim.Sim.QueryInterface<BuilderComponent>(u) != null);
        if (builder.Equals(default(EntityId))) return;

        var tc = snap.Buildings.FirstOrDefault(b =>
        {
            var identity = _sim.Sim.QueryInterface<IdentityComponent>(b);
            return identity != null && identity.Name.Contains("Center");
        });
        if (tc.Equals(default(EntityId))) return;

        var tcPos = _sim.Sim.QueryInterface<PositionComponent>(tc);
        if (tcPos == null) return;

        // Deterministic jitter from the kernel RNG (serialized into the OOS hash + save
        // state) — GD.Randf would diverge across MP peers and across save/reload.
        double jitterX = _sim.Sim.RNG.NextDouble() * 15.0;
        double jitterZ = _sim.Sim.RNG.NextDouble() * 15.0;
        float bx = tcPos.Position.X.ToFloat() + 35f + (float)jitterX;
        float bz = tcPos.Position.Z.ToFloat() + (float)jitterZ;

        // One lockstep command replaces the old 3-line bypass. SimCommandExecutor.ApplyBuild
        // charges the cost (CanAfford + Spend), spawns the foundation with owner=_playerId,
        // and assigns the builder — all at the execution turn. The foundation joins
        // _ownedBuildings on the next think via PetraAI.RebuildOwned (owner match).
        _sim.SubmitCommand(NetCommand.Build(_playerId, builder.Value,
            SimBridge.MapBuildNameToTemplate(name), Fixed.FromFloat(bx), Fixed.FromFloat(bz)));
    }
}

public sealed class ResearchManager
{
    private readonly SimBridge _sim;
    private readonly uint _playerId;
    private readonly EntityId _playerEntity; // for TechnologyManager.CanResearch reads
    private readonly List<EntityId> _buildings;
    private int _researchThinkCount;

    public ResearchManager(SimBridge sim, uint playerId, EntityId playerEntity, List<EntityId> buildings)
    { _sim = sim; _playerId = playerId; _playerEntity = playerEntity; _buildings = buildings; }

    public void Update(AISnapshot snap)
    {
        // Fires every 30 thinks ≈ 15s (was _researchTimer >= 15f at 0.5s/think).
        if (++_researchThinkCount < 30) return;
        _researchThinkCount = 0;

        var techMgr = _sim.Sim.QueryInterface<TechnologyManager>(_playerEntity);
        if (techMgr == null) return;

        foreach (var building in snap.Buildings)
        {
            var researcher = _sim.Sim.QueryInterface<ResearcherComponent>(building);
            if (researcher == null || researcher.IsResearching) continue;

            string? tech = PickNextTech(snap, techMgr);
            if (tech != null)
                _sim.SubmitCommand(NetCommand.Research(_playerId, building.Value, tech));
        }
    }

    private string? PickNextTech(AISnapshot snap, TechnologyManager techMgr)
    {
        // 真实 JSON 科技名(数据驱动重写后)。CanResearch 已含前置/pair/重复判定;
        // 资源是否够由 SimCommandExecutor.ApplyResearch 的 CanAfford 把关(不够则本次放弃,15s 后重试)。
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
    private readonly SimBridge _sim;
    private readonly uint _playerId;
    private readonly List<EntityId> _units;
    private readonly List<EntityId> _buildings;

    public DefenseManager(SimBridge sim, uint playerId, List<EntityId> units, List<EntityId> buildings)
    { _sim = sim; _playerId = playerId; _units = units; _buildings = buildings; }

    public void Update(AISnapshot snap)
    {
        if (snap.EnemyUnits.Count == 0) return;

        var threat = FindThreatNearBase(snap);
        if (threat == null) return;

        foreach (var soldier in snap.Soldiers)
        {
            var attack = _sim.Sim.QueryInterface<AttackComponent>(soldier);
            if (attack == null || attack.State == AttackComponent.AttackState.Attacking) continue;
            _sim.SubmitCommand(NetCommand.Attack(_playerId, soldier.Value, threat.Value.Value));
        }
    }

    private EntityId? FindThreatNearBase(AISnapshot snap)
    {
        foreach (var building in snap.Buildings)
        {
            var bpos = _sim.Sim.QueryInterface<PositionComponent>(building);
            if (bpos == null) continue;

            foreach (var enemy in snap.EnemyUnits)
            {
                var epos = _sim.Sim.QueryInterface<PositionComponent>(enemy);
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
    private readonly SimBridge _sim;
    private readonly uint _playerId;
    private readonly List<EntityId> _units;
    private int _attackThinkCount;
    private const int AttackWaveSize = 5;
    private const int AttackIntervalThinks = 40; // ≈ 20s (was 20f at 0.5s/think)

    public AttackManager(SimBridge sim, uint playerId, List<EntityId> units)
    { _sim = sim; _playerId = playerId; _units = units; }

    public void Update(AISnapshot snap)
    {
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
            var queue = _sim.Sim.QueryInterface<ProductionQueue>(building);
            var identity = _sim.Sim.QueryInterface<IdentityComponent>(building);
            if (queue == null || identity == null) continue;
            if (!identity.Name.Contains("Barracks") && !identity.Name.Contains("Center")) continue;
            if (queue.QueueCount > 0) continue;

            _sim.SubmitCommand(NetCommand.Train(_playerId, building.Value, "units/spart/infantry_spearman_b"));
            return;
        }
    }

    private void LaunchAttack(AISnapshot snap)
    {
        EntityId? target = snap.EnemyBuildings.FirstOrDefault();
        if (target == null) target = snap.EnemyUnits.FirstOrDefault();
        if (target == null) return;

        var tpos = _sim.Sim.QueryInterface<PositionComponent>(target.Value);
        if (tpos == null) return;

        foreach (var soldier in snap.Soldiers)
            _sim.SubmitCommand(NetCommand.Attack(_playerId, soldier.Value, target.Value.Value));
    }
}
