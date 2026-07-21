using System;
using System.Collections.Generic;
using System.IO;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Net
{
    public enum NetCommandType : byte
    {
        Invalid = 0,
        Move = 1,
        Gather = 2,
        Attack = 3,
        Build = 4,
        Train = 5,
        TrainSoldier = 6,
    }

    public readonly struct NetCommand
    {
        public readonly uint Player;
        public readonly NetCommandType Type;
        public readonly uint EntityId;
        public readonly int IntParam1;
        public readonly int IntParam2;
        public readonly int FixedParam1;
        public readonly int FixedParam2;
        /// <summary>
        /// Template name for Train (e.g. "units/spart/infantry_spearman_b"). Carried alongside
        /// the command so both clients enqueue the exact same template — previously this was
        /// hardcoded to "villager"/"soldier" in ExecuteCommand, which diverged from the local
        /// SimBridge path and caused OOS on training.
        /// </summary>
        public readonly string TemplateName;

        public NetCommand(uint player, NetCommandType type, uint entityId = 0,
            int p1 = 0, int p2 = 0, int fp1 = 0, int fp2 = 0, string? templateName = null)
        {
            Player = player; Type = type; EntityId = entityId;
            IntParam1 = p1; IntParam2 = p2; FixedParam1 = fp1; FixedParam2 = fp2;
            TemplateName = templateName ?? "";
        }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream(48);
            using var bw = new BinaryWriter(ms);
            bw.Write(Player);
            bw.Write((byte)Type);
            bw.Write(EntityId);
            bw.Write(IntParam1);
            bw.Write(IntParam2);
            bw.Write(FixedParam1);
            bw.Write(FixedParam2);
            // Length-prefixed UTF8 (0 if no template). Keeps the wire format self-describing
            // without growing every command by a fixed buffer.
            byte[] tmplBytes = System.Text.Encoding.UTF8.GetBytes(TemplateName);
            bw.Write(tmplBytes.Length);
            bw.Write(tmplBytes);
            return ms.ToArray();
        }

        public static NetCommand Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            return new NetCommand(
                br.ReadUInt32(),
                (NetCommandType)br.ReadByte(),
                br.ReadUInt32(),
                br.ReadInt32(),
                br.ReadInt32(),
                br.ReadInt32(),
                br.ReadInt32(),
                br.ReadString());
        }

        public static NetCommand Move(uint player, uint entityId, Fixed x, Fixed z) =>
            new(player, NetCommandType.Move, entityId, 0, 0, x.InternalValue, z.InternalValue);

        public static NetCommand Gather(uint player, uint unitId, uint targetId) =>
            new(player, NetCommandType.Gather, unitId, (int)targetId);

        public static NetCommand Attack(uint player, uint attackerId, uint targetId) =>
            new(player, NetCommandType.Attack, attackerId, (int)targetId);

        public static NetCommand Build(uint player, uint builderId, int gx, int gz) =>
            new(player, NetCommandType.Build, builderId, gx, gz);

        /// <summary>
        /// Train command carrying the full template name. Use this for all training (villagers,
        /// soldiers, siege, ...). The legacy <see cref="TrainSoldier"/> factory is kept only for
        /// backward compatibility with older peers and routes through the same ExecuteCommand
        /// path keyed on template name.
        /// </summary>
        public static NetCommand Train(uint player, uint buildingId, string templateName) =>
            new(player, NetCommandType.Train, buildingId, templateName: templateName);

        public static NetCommand TrainSoldier(uint player, uint buildingId) =>
            new(player, NetCommandType.TrainSoldier, buildingId);
    }

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
                        var motion = _cm.QueryInterface<Components.UnitMotion>(entity);
                        var x = Fixed.Zero.WithInternalValue(cmd.FixedParam1);
                        var z = Fixed.Zero.WithInternalValue(cmd.FixedParam2);
                        motion?.MoveToPoint(new FixedVector2D(x, z));
                        break;
                    }
                case NetCommandType.Gather:
                    {
                        var target = new EntityId((uint)cmd.IntParam1);
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
                        break;
                    }
                case NetCommandType.Attack:
                    {
                        var target = new EntityId((uint)cmd.IntParam1);
                        var attack = _cm.QueryInterface<Components.AttackComponent>(entity);
                        attack?.AttackTarget(target);
                        break;
                    }
                case NetCommandType.Train:
                    {
                        // Route through the same sim entry point as SimBridge.CommandTrain so
                        // single-player and lockstep agree exactly on cost/limits/spawn. The
                        // template name travels with the command; if it's empty (older peer or
                        // TrainSoldier legacy), fall back to a sane default.
                        var queue = _cm.QueryInterface<Components.ProductionQueue>(entity);
                        string template = string.IsNullOrEmpty(cmd.TemplateName)
                            ? "units/spart/support_civilian"
                            : cmd.TemplateName;
                        queue?.EnqueueTraining(template, count: 1, _cm);
                        break;
                    }
                case NetCommandType.TrainSoldier:
                    {
                        // Legacy soldier command from older peers. Maps to the spearman template
                        // and otherwise takes the unified training path.
                        var queue = _cm.QueryInterface<Components.ProductionQueue>(entity);
                        queue?.EnqueueTraining("units/spart/infantry_spearman_b", count: 1, _cm);
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
