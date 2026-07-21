using Godot;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

public sealed class EconomyManager
{
    private readonly SimBridge _sim;
    private readonly EntityId _player;
    private readonly List<EntityId> _units;

    private int _targetVillagers = 12;
    private float _allocTimer;

    public EconomyManager(SimBridge sim, EntityId player, List<EntityId> units)
    { _sim = sim; _player = player; _units = units; }

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

            _sim.CommandTrain(building);
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
                _sim.CommandGather(villager, resource.Value);
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
        _allocTimer += 0.5f;
        if (_allocTimer < 10f) return;
        _allocTimer = 0;

        if (snap.Player.Food > 500 && snap.Player.Wood < 200)
        {
            for (int i = 0; i < snap.Villagers.Count / 3; i++)
            {
                var v = snap.Villagers[i];
                var r = FindNearest(v, ResourceType.Wood);
                if (r.HasValue) _sim.CommandGather(v, r.Value);
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
    private readonly EntityId _player;
    private readonly List<EntityId> _buildings;
    private readonly List<EntityId> _units;

    private float _buildTimer;

    public BuildManager(SimBridge sim, EntityId player, List<EntityId> buildings, List<EntityId> units)
    { _sim = sim; _player = player; _buildings = buildings; _units = units; }

    public void Update(AISnapshot snap)
    {
        _buildTimer += 0.5f;
        if (_buildTimer < 5f) return;
        _buildTimer = 0;

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

        float bx = tcPos.Position.X.ToFloat() + 35 + GD.Randf() * 15;
        float bz = tcPos.Position.Z.ToFloat() + GD.Randf() * 15;

        var player = snap.Player;
        if (player.Wood < 100) return;
        player.Wood -= 100;

        var foundation = _sim.SpawnFoundation(bx, bz, name, 8.0f);
        _sim.CommandBuild(builder, foundation);
        _buildings.Add(foundation);
    }
}

public sealed class ResearchManager
{
    private readonly SimBridge _sim;
    private readonly EntityId _player;
    private readonly List<EntityId> _buildings;
    private float _researchTimer;

    public ResearchManager(SimBridge sim, EntityId player, List<EntityId> buildings)
    { _sim = sim; _player = player; _buildings = buildings; }

    public void Update(AISnapshot snap)
    {
        _researchTimer += 0.5f;
        if (_researchTimer < 15f) return;
        _researchTimer = 0;

        var techMgr = _sim.Sim.QueryInterface<TechnologyManager>(_player);
        if (techMgr == null) return;

        foreach (var building in snap.Buildings)
        {
            var researcher = _sim.Sim.QueryInterface<ResearcherComponent>(building);
            if (researcher == null || researcher.IsResearching) continue;

            string? tech = PickNextTech(snap, techMgr);
            if (tech != null)
                researcher.StartResearch(tech, techMgr, snap.Player);
        }
    }

    private string? PickNextTech(AISnapshot snap, TechnologyManager techMgr)
    {
        if (!techMgr.IsResearched("phase_town") && snap.Player.Wood >= 100)
            return "phase_town";
        if (!techMgr.IsResearched("gather_capacity") && snap.Player.Wood >= 50 && snap.Player.Food >= 50)
            return "gather_capacity";
        if (!techMgr.IsResearched("infantry_attack") && snap.Player.Metal >= 50)
            return "infantry_attack";
        if (!techMgr.IsResearched("gather_wood") && snap.Player.Wood >= 40 && snap.Player.Stone >= 40)
            return "gather_wood";
        if (!techMgr.IsResearched("infantry_armor") && snap.Player.Stone >= 50)
            return "infantry_armor";
        return null;
    }
}

public sealed class DefenseManager
{
    private readonly SimBridge _sim;
    private readonly EntityId _player;
    private readonly List<EntityId> _units;
    private readonly List<EntityId> _buildings;

    public DefenseManager(SimBridge sim, EntityId player, List<EntityId> units, List<EntityId> buildings)
    { _sim = sim; _player = player; _units = units; _buildings = buildings; }

    public void Update(AISnapshot snap)
    {
        if (snap.EnemyUnits.Count == 0) return;

        var threat = FindThreatNearBase(snap);
        if (threat == null) return;

        foreach (var soldier in snap.Soldiers)
        {
            var attack = _sim.Sim.QueryInterface<AttackComponent>(soldier);
            if (attack == null || attack.State == AttackComponent.AttackState.Attacking) continue;
            _sim.CommandAttack(soldier, threat.Value);
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
    private readonly EntityId _player;
    private readonly List<EntityId> _units;
    private float _attackTimer;
    private const int AttackWaveSize = 5;
    private const float AttackInterval = 20f;

    public AttackManager(SimBridge sim, EntityId player, List<EntityId> units)
    { _sim = sim; _player = player; _units = units; }

    public void Update(AISnapshot snap)
    {
        EnsureMilitaryProduction(snap);

        _attackTimer += 0.5f;
        if (_attackTimer < AttackInterval) return;
        if (snap.Soldiers.Count < AttackWaveSize) return;
        if (snap.EnemyBuildings.Count == 0 && snap.EnemyUnits.Count == 0) return;

        _attackTimer = 0;
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

            _sim.CommandTrainSoldier(building);
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
            _sim.CommandAttack(soldier, target.Value);
    }
}
