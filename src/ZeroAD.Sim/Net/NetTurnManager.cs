using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Net
{
    public sealed class NetTurnManager
    {
        private readonly ComponentManager _cm;
        private readonly int _commandDelay;

        private readonly List<Dictionary<uint, List<NetCommand>>> _turnSlots = new();
        private uint _currentTurn;
        private readonly uint _localPlayerId;

        private readonly Dictionary<uint, byte[]> _pendingHashes = new();
        private uint _lastHashTurn;
        private byte[]? _lastLocalHash;
        private string? _oosError;

        public uint CurrentTurn => _currentTurn;
        public bool HasOOS => _oosError != null;
        public string? OosError => _oosError;

        public event Action<uint, List<NetCommand>>? OnCommandsReady;
        public event Action<byte[]>? OnHashComputed;
        public event Action<uint, string>? OnOOSDetected;
        public event Action<uint>? OnTurnAdvanced;

        public NetTurnManager(ComponentManager cm, int commandDelay, uint localPlayerId)
        {
            _cm = cm;
            _commandDelay = Math.Max(1, commandDelay);
            _localPlayerId = localPlayerId;
            for (int i = 0; i <= _commandDelay; i++)
                _turnSlots.Add(new Dictionary<uint, List<NetCommand>>());
        }

        public void SubmitLocalCommand(NetCommand cmd)
        {
            int slotIndex = _commandDelay;
            if (!_turnSlots[slotIndex].TryGetValue(cmd.Player, out var list))
            {
                list = new List<NetCommand>();
                _turnSlots[slotIndex][cmd.Player] = list;
            }
            list.Add(cmd);
        }

        public void ReceiveRemoteCommands(uint player, uint turn, NetCommand[] commands)
        {
            int slotIndex = (int)(turn - _currentTurn);
            if (slotIndex < 0 || slotIndex >= _turnSlots.Count) return;

            if (!_turnSlots[slotIndex].TryGetValue(player, out var list))
            {
                list = new List<NetCommand>();
                _turnSlots[slotIndex][player] = list;
            }
            list.AddRange(commands);
        }

        public bool IsTurnReady(HashSet<uint> expectedPlayers)
        {
            var currentSlot = _turnSlots[0];
            foreach (uint pid in expectedPlayers)
                if (!currentSlot.ContainsKey(pid))
                    return false;
            return true;
        }

        public void AdvanceTurn(HashSet<uint> expectedPlayers)
        {
            var currentSlot = _turnSlots[0];

            var allCommands = new List<NetCommand>();
            foreach (var kvp in currentSlot)
            {
                if (expectedPlayers.Contains(kvp.Key))
                    allCommands.AddRange(kvp.Value);
            }

            allCommands.Sort((a, b) => a.Player.CompareTo(b.Player));
            OnCommandsReady?.Invoke(_currentTurn, allCommands);

            foreach (var cmd in allCommands)
                ExecuteCommand(cmd);

            _turnSlots.RemoveAt(0);
            _turnSlots.Add(new Dictionary<uint, List<NetCommand>>());
            _currentTurn++;
            OnTurnAdvanced?.Invoke(_currentTurn);

            if (_currentTurn % 20 == 0)
                CheckOOS();
        }

        private void ExecuteCommand(NetCommand cmd)
        {
            var entity = new EntityId(cmd.EntityId);
            switch (cmd.Type)
            {
                case NetCommandType.Move:
                    {
                        var x = Fixed.Zero.WithInternalValue(cmd.FixedParam1);
                        var z = Fixed.Zero.WithInternalValue(cmd.FixedParam2);
                        // Route through UnitAI when present so lockstep agrees with single-player;
                        // fall back to direct UnitMotion for legacy entities.
                        var ai = _cm.QueryInterface<Components.UnitAIComponent>(entity);
                        if (ai != null)
                            ai.Walk(new FixedVector2D(x, z));
                        else
                            _cm.QueryInterface<Components.UnitMotion>(entity)?.MoveToPoint(new FixedVector2D(x, z));
                        break;
                    }
                case NetCommandType.Gather:
                    {
                        var target = new EntityId((uint)cmd.IntParam1);
                        var ai = _cm.QueryInterface<Components.UnitAIComponent>(entity);
                        if (ai != null)
                        {
                            ai.Gather(target);
                        }
                        else
                        {
                            var motion = _cm.QueryInterface<Components.UnitMotion>(entity);
                            var gatherer = _cm.QueryInterface<Components.ResourceGatherer>(entity);
                            var supply = _cm.QueryInterface<Components.ResourceSupply>(target);
                            var supplyPos = _cm.QueryInterface<Components.PositionComponent>(target);
                            if (gatherer != null && supply != null && supplyPos != null && motion != null)
                            {
                                gatherer.TargetSupply = target;
                                gatherer.CarryType = supply.Type;
                                gatherer.State = Components.ResourceGatherer.GatherState.MovingToResource;
                                motion.MoveToPoint(new FixedVector2D(supplyPos.Position.X, supplyPos.Position.Z));
                            }
                        }
                        break;
                    }
                case NetCommandType.Attack:
                    {
                        var target = new EntityId((uint)cmd.IntParam1);
                        var ai = _cm.QueryInterface<Components.UnitAIComponent>(entity);
                        if (ai != null)
                            ai.Attack(target);
                        else
                            _cm.QueryInterface<Components.AttackComponent>(entity)?.AttackTarget(target);
                        break;
                    }
                case NetCommandType.Train:
                    {
                        // Route through the same sim entry point as SimBridge.CommandTrain so
                        // single-player and lockstep agree exactly on cost/limits/spawn. The
                        // template name travels with the command; if it's empty (older peer),
                        // fall back to a sane default.
                        var queue = _cm.QueryInterface<Components.ProductionQueue>(entity);
                        string template = string.IsNullOrEmpty(cmd.TemplateName)
                            ? "units/spart/support_civilian"
                            : cmd.TemplateName;
                        queue?.EnqueueTraining(template, count: 1, _cm);
                        break;
                    }
            }
        }

        private void CheckOOS()
        {
            byte[] hash = _cm.ComputeStateHash();
            _lastLocalHash = hash;
            _lastHashTurn = _currentTurn;
            OnHashComputed?.Invoke(hash);
        }

        public void ReceiveRemoteHash(uint turn, byte[] remoteHash)
        {
            if (_lastLocalHash == null) return;
            if (turn != _lastHashTurn) return;

            if (!HashEquals(_lastLocalHash, remoteHash))
            {
                _oosError = $"OOS at turn {turn}: local hash differs from remote";
                OnOOSDetected?.Invoke(turn, _oosError);
            }
        }

        private static bool HashEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        public static string HashToString(byte[] hash) =>
            Convert.ToHexString(hash);
    }
}
