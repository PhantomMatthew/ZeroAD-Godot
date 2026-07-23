using System;
using System.Collections.Generic;

namespace ZeroAD.Sim.Net
{
    public sealed class NetTurnManager
    {
        private readonly ComponentManager _cm;
        private readonly SimCommandExecutor _executor;
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
            _executor = new SimCommandExecutor(cm);
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
            // Command execution lives in SimCommandExecutor — the single shared entry point
            // so single-player (SimBridge.CommandX) and lockstep (this path) can never diverge.
            _executor.Apply(cmd);
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
