using Godot;
using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

public sealed partial class AIDirector : Node
{
    private SimBridge _sim = null!;
    private EntityId _playerEntity;
    private float _decisionTimer;

    private readonly List<EntityId> _aiUnits = new();
    private readonly List<EntityId> _aiBuildings = new();

    private int _targetVillagerCount = 8;
    private int _targetSoldierCount = 5;
    private float _aggressionTimer;

    public void Init(SimBridge sim, EntityId playerEntity)
    {
        _sim = sim;
        _playerEntity = playerEntity;
    }

    public void RegisterUnit(EntityId e) => _aiUnits.Add(e);
    public void RegisterBuilding(EntityId e) => _aiBuildings.Add(e);

    public override void _Process(double delta)
    {
        if (_sim == null) return;
        _decisionTimer += (float)delta;
        if (_decisionTimer < 0.5f) return;
        _decisionTimer = 0;

        var player = _sim.Sim.QueryInterface<PlayerComponent>(_playerEntity);
        if (player == null) return;

        ManageEconomy(player);
        ManageMilitary(player);
        ManageConstruction(player);
    }

    private void ManageEconomy(PlayerComponent player)
    {
        int villagers = 0;
        int soldiers = 0;
        int idleVillagers = 0;

        foreach (var unit in _aiUnits.ToArray())
        {
            var gatherer = _sim.Sim.QueryInterface<ResourceGatherer>(unit);
            var attack = _sim.Sim.QueryInterface<AttackComponent>(unit);
            var health = _sim.Sim.QueryInterface<HealthComponent>(unit);
            if (health != null && health.IsDead)
            {
                _aiUnits.Remove(unit);
                continue;
            }

            if (gatherer != null)
            {
                villagers++;
                if (gatherer.State == ResourceGatherer.GatherState.Idle)
                {
                    idleVillagers++;
                    AssignGathering(unit, player);
                }
            }
            if (attack != null) soldiers++;
        }

        if (villagers < _targetVillagerCount && player.Food >= 50)
        {
            foreach (var building in _aiBuildings)
            {
                var queue = _sim.Sim.QueryInterface<ProductionQueue>(building);
                if (queue == null || queue.QueueCount > 0) continue;
                var identity = _sim.Sim.QueryInterface<IdentityComponent>(building);
                if (identity == null || !identity.Name.Contains("Center") && !identity.Name.Contains("Barracks")) continue;

                if (identity.Name.Contains("Center"))
                {
                    _sim.CommandTrain(building);
                    return;
                }
            }
        }
    }

    private void AssignGathering(EntityId villager, PlayerComponent player)
    {
        ResourceType target = ResourceType.Wood;
        if (player.Food < 50) target = ResourceType.Food;
        else if (player.Wood < 100) target = ResourceType.Wood;
        else if (player.Stone < 50) target = ResourceType.Stone;
        else if (player.Metal < 50) target = ResourceType.Metal;

        var resource = FindNearestResource(villager, target);
        if (resource.HasValue)
            _sim.CommandGather(villager, resource.Value);
    }

    private void ManageMilitary(PlayerComponent player)
    {
        _aggressionTimer += 0.5f;

        int soldiers = 0;
        var army = new List<EntityId>();

        foreach (var unit in _aiUnits)
        {
            var attack = _sim.Sim.QueryInterface<AttackComponent>(unit);
            var health = _sim.Sim.QueryInterface<HealthComponent>(unit);
            if (attack != null && (health == null || !health.IsDead))
            {
                soldiers++;
                army.Add(unit);
            }
        }

        if (_aggressionTimer > 15f && army.Count >= _targetSoldierCount)
        {
            _aggressionTimer = 0;
            var enemy = FindEnemyTarget();
            if (enemy.HasValue)
            {
                foreach (var soldier in army)
                    _sim.CommandAttack(soldier, enemy.Value);
            }
        }

        if (soldiers < _targetSoldierCount && player.Food >= 80)
        {
            foreach (var building in _aiBuildings)
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
    }

    private void ManageConstruction(PlayerComponent player)
    {
        if (player.Wood < 200) return;

        bool needsHouse = player.PopUsed >= player.PopulationLimit - 3;
        bool needsBarracks = !_aiBuildings.Exists(b =>
        {
            var identity = _sim.Sim.QueryInterface<IdentityComponent>(b);
            return identity != null && identity.Name.Contains("Barracks");
        });

        if (!needsHouse && !needsBarracks) return;

        var builder = _aiUnits.Find(u =>
        {
            var health = _sim.Sim.QueryInterface<HealthComponent>(u);
            return _sim.Sim.QueryInterface<BuilderComponent>(u) != null
                && (health == null || !health.IsDead);
        });
        if (builder.Equals(default(EntityId))) return;

        var bpos = _sim.Sim.QueryInterface<PositionComponent>(_aiBuildings[0]);
        if (bpos == null) return;

        float bx = bpos.Position.X.ToFloat() + 30 + GD.Randf() * 20;
        float bz = bpos.Position.Z.ToFloat() + GD.Randf() * 20;

        string name = needsBarracks ? "Barracks" : "House";
        var foundation = _sim.SpawnFoundationDirect(bx, bz, name, 8.0f);
        _sim.OrderRepairDirect(builder, foundation);
        _aiBuildings.Add(foundation);
    }

    private EntityId? FindNearestResource(EntityId from, ResourceType type)
    {
        var fromPos = _sim.Sim.QueryInterface<PositionComponent>(from);
        if (fromPos == null) return null;

        float bestDist = float.MaxValue;
        EntityId? best = null;

        foreach (var kvp in GetEntityNodes())
        {
            var supply = _sim.Sim.QueryInterface<ResourceSupply>(kvp.Key);
            if (supply == null || supply.IsEmpty || supply.Type != type) continue;
            var pos = _sim.Sim.QueryInterface<PositionComponent>(kvp.Key);
            if (pos == null) continue;

            float dx = pos.Position.X.ToFloat() - fromPos.Position.X.ToFloat();
            float dz = pos.Position.Z.ToFloat() - fromPos.Position.Z.ToFloat();
            float dist = dx * dx + dz * dz;
            if (dist < bestDist) { bestDist = dist; best = kvp.Key; }
        }
        return best;
    }

    private EntityId? FindEnemyTarget()
    {
        foreach (var kvp in GetEntityNodes())
        {
            if (_aiUnits.Contains(kvp.Key) || _aiBuildings.Contains(kvp.Key)) continue;
            var identity = _sim.Sim.QueryInterface<IdentityComponent>(kvp.Key);
            if (identity == null) continue;
            if (identity.Name == "Tree") continue;
            return kvp.Key;
        }
        return null;
    }

    private IReadOnlyDictionary<EntityId, Node3D> GetEntityNodes() => _sim.EntityNodes;
}
