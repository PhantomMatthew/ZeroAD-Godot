using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeroAD.Sim.Net
{
    public enum NetRole : byte
    {
        /// <summary>No network: local submissions aggregate synchronously. SP path.</summary>
        Standalone = 0,
        /// <summary>Aggregates per-turn batches from all players and produces bundles.</summary>
        Host = 1,
        /// <summary>Ships per-turn batches to the host; executes only received bundles.</summary>
        Client = 2,
    }

    /// <summary>
    /// Host-authoritative lockstep turn manager (one per peer).
    ///
    /// Command lifecycle:
    ///   SubmitLocalCommand → outbox → drained into a per-turn batch at AdvanceTurn
    ///   (key = currentTurn + commandDelay, event OnBatchDue) → host aggregates batches
    ///   from ALL expected players → ProduceBundle(turn) → OnTurnBundleReady → transport
    ///   broadcasts → ReceiveTurnBundle on every peer (the ONLY writer of _bundles
    ///   besides Standalone) → executed by AdvanceTurn when currentTurn reaches it.
    ///
    /// The turn barrier: CanAdvanceTurn() is false until the bundle for the upcoming
    /// turn has arrived, so the sim's advance is paced by the network, not the clock.
    /// Turns before commandDelay can never contain commands; HostBootstrap pre-produces
    /// them empty so the game can start.
    /// </summary>
    public sealed class NetTurnManager
    {
        private readonly ComponentManager _cm;
        private readonly SimCommandExecutor _executor;
        private readonly int _commandDelay;
        private readonly NetRole _role;
        private readonly uint _localPlayerId;
        private readonly HashSet<uint> _expectedPlayers;

        private uint _currentTurn;
        private readonly List<NetCommand> _outbox = new();
        private readonly Dictionary<uint, List<NetCommand>> _bundles = new();
        private readonly Dictionary<uint, Dictionary<uint, List<NetCommand>>> _incoming = new();

        private byte[]? _lastLocalHash;
        private uint _lastHashTurn;
        private readonly Dictionary<(uint turn, uint player), byte[]> _remoteHashes = new();
        private string? _oosError;

        public uint CurrentTurn => _currentTurn;
        public int CommandDelay => _commandDelay;
        public NetRole Role => _role;
        public uint LocalPlayerId => _localPlayerId;
        public bool HasOOS => _oosError != null;
        public string? OosError => _oosError;

        /// <summary>(turn, possibly-empty local batch) raised at every AdvanceTurn.
        /// Clients forward this to the host; the host self-ingests internally.</summary>
        public event Action<uint, NetCommand[]>? OnBatchDue;
        /// <summary>Host only: a complete per-turn bundle is ready for broadcast.</summary>
        public event Action<uint, NetCommand[]>? OnTurnBundleReady;
        /// <summary>Client only: ship this state hash to the host.</summary>
        public event Action<byte[]>? OnHashComputed;
        public event Action<uint, string>? OnOOSDetected;
        public event Action<uint>? OnTurnAdvanced;

        public NetTurnManager(ComponentManager cm, int commandDelay, uint localPlayerId,
            NetRole role, HashSet<uint> expectedPlayers)
        {
            _cm = cm;
            _executor = new SimCommandExecutor(cm);
            _commandDelay = Math.Max(1, commandDelay);
            _localPlayerId = localPlayerId;
            _role = role;
            _expectedPlayers = expectedPlayers;
        }

        public void SubmitLocalCommand(NetCommand cmd) => _outbox.Add(cmd);

        /// <summary>Host only: turns [0, commandDelay) can never contain commands, so
        /// their bundles are produced empty up front and the game can start immediately.</summary>
        public void HostBootstrap()
        {
            if (_role != NetRole.Host) return;
            for (uint turn = 0; turn < (uint)_commandDelay; turn++)
                ProduceBundle(turn, new Dictionary<uint, List<NetCommand>>());
        }

        public bool CanAdvanceTurn() =>
            _role == NetRole.Standalone || _bundles.ContainsKey(_currentTurn);

        public void AdvanceTurn()
        {
            // Drain the outbox into this turn's batch (possibly empty — the heartbeat
            // that lets the host complete aggregation for silent players).
            uint batchTurn = _currentTurn + (uint)_commandDelay;
            var batch = _outbox.ToArray();
            _outbox.Clear();
            OnBatchDue?.Invoke(batchTurn, batch);
            if (_role == NetRole.Standalone)
                _bundles[batchTurn] = new List<NetCommand>(batch);
            else if (_role == NetRole.Host)
                HostIngestBatch(_localPlayerId, batchTurn, batch);

            // Execute the bundle scheduled for this turn (absent/empty = no commands).
            if (_bundles.TryGetValue(_currentTurn, out var commands))
            {
                _bundles.Remove(_currentTurn);
                foreach (var cmd in commands)
                    _executor.Apply(cmd);
            }

            _currentTurn++;
            OnTurnAdvanced?.Invoke(_currentTurn);
            if (_currentTurn % 20 == 0)
                CheckOOS();
        }

        /// <summary>Host only: ingest one player's batch for a turn. When every
        /// expected player has reported, the bundle is produced. Duplicate batches
        /// from the same player for the same turn are ignored.</summary>
        public void HostIngestBatch(uint player, uint turn, NetCommand[] commands)
        {
            if (_role != NetRole.Host) return;
            if (!_incoming.TryGetValue(turn, out var perPlayer))
            {
                perPlayer = new Dictionary<uint, List<NetCommand>>();
                _incoming[turn] = perPlayer;
            }
            if (perPlayer.ContainsKey(player)) return;
            perPlayer[player] = new List<NetCommand>(commands);
            if (perPlayer.Count == _expectedPlayers.Count)
            {
                _incoming.Remove(turn);
                ProduceBundle(turn, perPlayer);
            }
        }

        private void ProduceBundle(uint turn, Dictionary<uint, List<NetCommand>> perPlayer)
        {
            // Deterministic order: ascending player id, in-batch order preserved.
            var bundle = new List<NetCommand>();
            foreach (uint pid in perPlayer.Keys.OrderBy(k => k))
                bundle.AddRange(perPlayer[pid]);
            OnTurnBundleReady?.Invoke(turn, bundle.ToArray());
        }

        /// <summary>The ONLY writer of execution slots in Host/Client mode. Called by
        /// the transport when a bundle arrives (host included, via CallLocal loopback).</summary>
        public void ReceiveTurnBundle(uint turn, NetCommand[] commands)
        {
            if (_role == NetRole.Standalone) return;
            _bundles[turn] = new List<NetCommand>(commands);
        }

        private void CheckOOS()
        {
            byte[] hash = _cm.ComputeStateHash();
            _lastLocalHash = hash;
            _lastHashTurn = _currentTurn;
            if (_role == NetRole.Client)
            {
                OnHashComputed?.Invoke(hash);
                return;
            }
            if (_role == NetRole.Host)
            {
                foreach (var kvp in _remoteHashes)
                    if (kvp.Key.turn == _currentTurn && !HashEquals(hash, kvp.Value))
                        SetOOS(_currentTurn);
            }
        }

        /// <summary>Host only: compare a client's state hash against the local one.
        /// Latches both directions (client hash may arrive before the host's own
        /// checkpoint fires, or after).</summary>
        public void HostReceiveRemoteHash(uint turn, uint player, byte[] hash)
        {
            if (_role != NetRole.Host) return;
            _remoteHashes[(turn, player)] = hash;
            if (_lastLocalHash != null && turn == _lastHashTurn && !HashEquals(_lastLocalHash, hash))
                SetOOS(turn);
        }

        private void SetOOS(uint turn)
        {
            if (_oosError != null) return;
            _oosError = $"OOS at turn {turn}: state hash mismatch";
            OnOOSDetected?.Invoke(turn, _oosError);
        }

        private static bool HashEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        public static string HashToString(byte[] hash) => Convert.ToHexString(hash);
    }
}
