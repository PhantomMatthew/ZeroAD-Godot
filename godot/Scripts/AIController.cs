using Godot;
using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Godot;

public sealed partial class AIController : Node
{
	private SimBridge _sim = null!;
	private EntityId _playerEntity;
	private float _decisionTimer;
	private readonly List<EntityId> _aiUnits = new();
	private readonly List<EntityId> _aiBuildings = new();

	public void Init(SimBridge sim, EntityId playerEntity)
	{
		_sim = sim;
		_playerEntity = playerEntity;
	}

	public void RegisterAIUnit(EntityId entity) => _aiUnits.Add(entity);
	public void RegisterAIBuilding(EntityId entity) => _aiBuildings.Add(entity);

	public override void _Process(double delta)
	{
		if (_sim == null) return;
		_decisionTimer += (float)delta;
		if (_decisionTimer < 1.0f) return;
		_decisionTimer = 0;

		var player = _sim.Sim.QueryInterface<PlayerComponent>(_playerEntity);
		if (player == null) return;

		EnsureVillagersGathering(player);
		EnsureTraining(player);
		EnsureBuilding(player);
		EnsureMilitary(player);
	}

	private void EnsureVillagersGathering(PlayerComponent player)
	{
		foreach (var unit in _aiUnits.ToArray())
		{
			var gatherer = _sim.Sim.QueryInterface<ResourceGatherer>(unit);
			if (gatherer == null || gatherer.State != ResourceGatherer.GatherState.Idle) continue;

			var resource = FindNearestResource(unit, ResourceType.Wood);
			if (resource.HasValue)
			{
				var motion = _sim.Sim.QueryInterface<UnitMotion>(unit);
				if (motion != null)
					_sim.CommandGather(unit, resource.Value);
			}
		}
	}

	private void EnsureTraining(PlayerComponent player)
	{
		if (_aiUnits.Count >= 20) return;
		if (player.Food < 50) return;

		foreach (var building in _aiBuildings)
		{
			var queue = _sim.Sim.QueryInterface<ProductionQueue>(building);
			if (queue == null || queue.QueueCount > 0) continue;
			_sim.CommandTrain(building);
			return;
		}
	}

	private void EnsureBuilding(PlayerComponent player)
	{
		if (player.Wood < 150) return;
		if (_aiBuildings.Count >= 4) return;

		var builder = _aiUnits.Find(u =>
			_sim.Sim.QueryInterface<BuilderComponent>(u) != null);
		if (builder.Equals(default(EntityId))) return;

		var bpos = _sim.Sim.QueryInterface<PositionComponent>(_aiBuildings[0]);
		if (bpos == null) return;

		float bx = bpos.Position.X.ToFloat() + 40 + GD.Randf() * 20;
		float bz = bpos.Position.Z.ToFloat() + GD.Randf() * 20;

		var foundation = _sim.SpawnFoundation(bx, bz, "House", 8.0f);
		_sim.CommandBuild(builder, foundation);
		_aiBuildings.Add(foundation);
	}

	private void EnsureMilitary(PlayerComponent player)
	{
		var soldiers = _aiUnits.FindAll(u =>
			_sim.Sim.QueryInterface<AttackComponent>(u) != null);

		if (soldiers.Count >= 5)
		{
			var enemy = FindEnemy();
			if (enemy.HasValue)
			{
				foreach (var soldier in soldiers)
					_sim.CommandAttack(soldier, enemy.Value);
			}
		}
	}

	private EntityId? FindNearestResource(EntityId from, ResourceType type)
	{
		var fromPos = _sim.Sim.QueryInterface<PositionComponent>(from);
		if (fromPos == null) return null;

		float bestDist = float.MaxValue;
		EntityId? best = null;

		foreach (var kvp in GetAllEntityNodes())
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

	private EntityId? FindEnemy()
	{
		foreach (var kvp in GetAllEntityNodes())
		{
			var identity = _sim.Sim.QueryInterface<IdentityComponent>(kvp.Key);
			if (identity == null) continue;
			var attack = _sim.Sim.QueryInterface<AttackComponent>(kvp.Key);
			if (attack == null) continue;
			if (!_aiUnits.Contains(kvp.Key))
				return kvp.Key;
		}
		return null;
	}

	private Dictionary<EntityId, Node3D> GetAllEntityNodes()
	{
		var field = typeof(SimBridge).GetField("_entityNodes",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
		return (Dictionary<EntityId, Node3D>?)field?.GetValue(_sim) ?? new();
	}
}
