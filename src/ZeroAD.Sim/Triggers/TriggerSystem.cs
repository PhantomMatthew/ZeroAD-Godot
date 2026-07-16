using System;
using System.Collections.Generic;

namespace ZeroAD.Sim.Triggers
{
    public enum TriggerEventType
    {
        OnTimer,
        OnUnitDied,
        OnEntityEnteredArea,
        OnPlayerDefeated,
        OnBuildingConstructed,
        OnTurn,
    }

    public readonly struct TriggerCondition
    {
        public readonly TriggerEventType Type;
        public readonly uint? EntityId;
        public readonly float? X;
        public readonly float? Z;
        public readonly float? Radius;
        public readonly int? TurnNumber;
        public readonly float? TimerSeconds;

        public TriggerCondition(TriggerEventType type,
            uint? entityId = null, float? x = null, float? z = null,
            float? radius = null, int? turnNumber = null, float? timerSeconds = null)
        {
            Type = type; EntityId = entityId; X = x; Z = z;
            Radius = radius; TurnNumber = turnNumber; TimerSeconds = timerSeconds;
        }
    }

    public sealed class Trigger
    {
        public string Name { get; }
        public TriggerCondition Condition { get; }
        public Action<TriggerSystem> Action { get; }
        public bool IsOneShot { get; }
        public bool HasFired { get; internal set; }

        public Trigger(string name, TriggerCondition condition, Action<TriggerSystem> action, bool oneShot = true)
        {
            Name = name; Condition = condition; Action = action; IsOneShot = oneShot;
        }
    }

    public sealed class TriggerSystem
    {
        private readonly List<Trigger> _triggers = new();
        private readonly ComponentManager _cm;
        private float _elapsedTime;
        private int _currentTurn;
        private readonly Dictionary<uint, bool> _entityDeathChecked = new();
        private readonly List<(float x, float z, float radius, HashSet<uint> inside)> _areaTrackers = new();

        public string VictoryMessage { get; private set; } = "";
        public string DefeatMessage { get; private set; } = "";
        public bool IsGameOver { get; private set; }

        public IReadOnlyList<Trigger> Triggers => _triggers;

        public TriggerSystem(ComponentManager cm) => _cm = cm;

        public void AddTrigger(Trigger trigger) => _triggers.Add(trigger);

        public Trigger AddTimer(string name, float seconds, Action<TriggerSystem> action, bool oneShot = true)
        {
            var t = new Trigger(name,
                new TriggerCondition(TriggerEventType.OnTimer, timerSeconds: seconds),
                action, oneShot);
            _triggers.Add(t);
            return t;
        }

        public Trigger AddOnTurn(string name, int turn, Action<TriggerSystem> action)
        {
            var t = new Trigger(name,
                new TriggerCondition(TriggerEventType.OnTurn, turnNumber: turn),
                action);
            _triggers.Add(t);
            return t;
        }

        public Trigger AddOnUnitDied(string name, uint entityId, Action<TriggerSystem> action)
        {
            var t = new Trigger(name,
                new TriggerCondition(TriggerEventType.OnUnitDied, entityId: entityId),
                action);
            _triggers.Add(t);
            return t;
        }

        public Trigger AddOnAreaEnter(string name, float x, float z, float radius, Action<TriggerSystem> action)
        {
            var t = new Trigger(name,
                new TriggerCondition(TriggerEventType.OnEntityEnteredArea, x: x, z: z, radius: radius),
                action, false);
            _triggers.Add(t);
            _areaTrackers.Add((x, z, radius, new HashSet<uint>()));
            return t;
        }

        public void SetVictory(string message)
        {
            VictoryMessage = message;
            IsGameOver = true;
        }

        public void SetDefeat(string message)
        {
            DefeatMessage = message;
            IsGameOver = true;
        }

        public void Tick(float dt, int turn)
        {
            if (IsGameOver) return;

            _elapsedTime += dt;
            _currentTurn = turn;

            CheckTimerTriggers();
            CheckTurnTriggers();
            CheckUnitDeath();
            CheckAreaEnters();
            CheckVictoryDefeat();
        }

        private void CheckTimerTriggers()
        {
            foreach (var trigger in _triggers)
            {
                if (trigger.HasFired && trigger.IsOneShot) continue;
                if (trigger.Condition.Type != TriggerEventType.OnTimer) continue;

                if (_elapsedTime >= trigger.Condition.TimerSeconds)
                {
                    trigger.Action(this);
                    if (trigger.IsOneShot) trigger.HasFired = true;
                }
            }
        }

        private void CheckTurnTriggers()
        {
            foreach (var trigger in _triggers)
            {
                if (trigger.HasFired && trigger.IsOneShot) continue;
                if (trigger.Condition.Type != TriggerEventType.OnTurn) continue;

                if (_currentTurn >= trigger.Condition.TurnNumber)
                {
                    trigger.Action(this);
                    if (trigger.IsOneShot) trigger.HasFired = true;
                }
            }
        }

        private void CheckUnitDeath()
        {
            foreach (var trigger in _triggers)
            {
                if (trigger.HasFired && trigger.IsOneShot) continue;
                if (trigger.Condition.Type != TriggerEventType.OnUnitDied) continue;
                if (trigger.Condition.EntityId == null) continue;

                uint eid = trigger.Condition.EntityId.Value;
                if (_entityDeathChecked.ContainsKey(eid)) continue;

                var health = _cm.QueryInterface<Components.HealthComponent>(new EntityId(eid));
                if (health != null && health.IsDead)
                {
                    trigger.Action(this);
                    if (trigger.IsOneShot) trigger.HasFired = true;
                    _entityDeathChecked[eid] = true;
                }
            }
        }

        private void CheckAreaEnters()
        {
            foreach (var trigger in _triggers)
            {
                if (trigger.HasFired && trigger.IsOneShot) continue;
                if (trigger.Condition.Type != TriggerEventType.OnEntityEnteredArea) continue;
                if (trigger.Condition.X == null || trigger.Condition.Z == null || trigger.Condition.Radius == null)
                    continue;

                float cx = trigger.Condition.X.Value;
                float cz = trigger.Condition.Z.Value;
                float r = trigger.Condition.Radius.Value;

                foreach (var eid in _cm.AllEntities)
                {
                    var pos = _cm.QueryInterface<Components.PositionComponent>(eid);
                    if (pos == null) continue;

                    float dx = pos.Position.X.ToFloat() - cx;
                    float dz = pos.Position.Z.ToFloat() - cz;
                    if (dx * dx + dz * dz < r * r)
                    {
                        trigger.Action(this);
                        if (trigger.IsOneShot) trigger.HasFired = true;
                        break;
                    }
                }
            }
        }

        private void CheckVictoryDefeat()
        {
            int alivePlayers = 0;
            foreach (var eid in _cm.AllEntities)
            {
                if (_cm.QueryInterface<Components.PlayerComponent>(eid) != null)
                {
                    bool hasBuildings = false;
                    foreach (var eid2 in _cm.AllEntities)
                    {
                        var identity = _cm.QueryInterface<Components.IdentityComponent>(eid2);
                        if (identity != null && identity.IsBuilding)
                        { hasBuildings = true; break; }
                    }
                    if (hasBuildings) alivePlayers++;
                }
            }

            if (alivePlayers <= 1)
            {
                SetVictory("Conquest Victory!");
            }
        }
    }
}
