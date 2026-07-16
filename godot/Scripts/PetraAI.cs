using Godot;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

public sealed partial class PetraAI : Node
{
    private SimBridge _sim = null!;
    private EntityId _playerEntity;

    private EconomyManager _economy = null!;
    private BuildManager _build = null!;
    private ResearchManager _research = null!;
    private DefenseManager _defense = null!;
    private AttackManager _attack = null!;

    private float _thinkTimer;
    private const float ThinkInterval = 0.5f;

    private readonly List<EntityId> _ownedUnits = new();
    private readonly List<EntityId> _ownedBuildings = new();

    public void Init(SimBridge sim, EntityId playerEntity)
    {
        _sim = sim;
        _playerEntity = playerEntity;
        _economy = new EconomyManager(sim, playerEntity, _ownedUnits);
        _build = new BuildManager(sim, playerEntity, _ownedBuildings, _ownedUnits);
        _research = new ResearchManager(sim, playerEntity, _ownedBuildings);
        _defense = new DefenseManager(sim, playerEntity, _ownedUnits, _ownedBuildings);
        _attack = new AttackManager(sim, playerEntity, _ownedUnits);
    }

    public void RegisterUnit(EntityId e) => _ownedUnits.Add(e);
    public void RegisterBuilding(EntityId e) => _ownedBuildings.Add(e);

    public override void _Process(double delta)
    {
        if (_sim == null || _sim.Sim == null) return;

        _thinkTimer += (float)delta;
        if (_thinkTimer < ThinkInterval) return;
        _thinkTimer = 0;

        CleanupDead();

        var player = _sim.Sim.QueryInterface<PlayerComponent>(_playerEntity);
        if (player == null) return;

        var snapshot = new AISnapshot
        {
            Player = player,
            Villagers = _ownedUnits.Where(u => _sim.Sim.QueryInterface<ResourceGatherer>(u) != null).ToList(),
            Soldiers = _ownedUnits.Where(u => _sim.Sim.QueryInterface<AttackComponent>(u) != null).ToList(),
            Buildings = _ownedBuildings.ToList(),
            EnemyUnits = FindEnemyUnits(),
            EnemyBuildings = FindEnemyBuildings(),
        };

        _economy.Update(snapshot);
        _build.Update(snapshot);
        _research.Update(snapshot);
        _defense.Update(snapshot);
        _attack.Update(snapshot);
    }

    private void CleanupDead()
    {
        _ownedUnits.RemoveAll(u =>
        {
            var h = _sim.Sim.QueryInterface<HealthComponent>(u);
            return h != null && h.IsDead;
        });
        _ownedBuildings.RemoveAll(b =>
        {
            var h = _sim.Sim.QueryInterface<HealthComponent>(b);
            return h != null && h.IsDead;
        });
    }

    private List<EntityId> FindEnemyUnits()
    {
        var result = new List<EntityId>();
        foreach (var kvp in _sim.EntityNodes)
        {
            if (_ownedUnits.Contains(kvp.Key) || _ownedBuildings.Contains(kvp.Key)) continue;
            var identity = _sim.Sim.QueryInterface<IdentityComponent>(kvp.Key);
            var attack = _sim.Sim.QueryInterface<AttackComponent>(kvp.Key);
            if (identity != null && identity.IsUnit && attack != null)
                result.Add(kvp.Key);
        }
        return result;
    }

    private List<EntityId> FindEnemyBuildings()
    {
        var result = new List<EntityId>();
        foreach (var kvp in _sim.EntityNodes)
        {
            if (_ownedBuildings.Contains(kvp.Key)) continue;
            var identity = _sim.Sim.QueryInterface<IdentityComponent>(kvp.Key);
            if (identity != null && identity.IsBuilding)
                result.Add(kvp.Key);
        }
        return result;
    }
}

public sealed class AISnapshot
{
    public PlayerComponent Player = null!;
    public List<EntityId> Villagers = new();
    public List<EntityId> Soldiers = new();
    public List<EntityId> Buildings = new();
    public List<EntityId> EnemyUnits = new();
    public List<EntityId> EnemyBuildings = new();
}
