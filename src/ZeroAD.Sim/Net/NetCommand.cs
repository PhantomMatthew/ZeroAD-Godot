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
        Research = 6,
        SetRallyPoint = 7,
    }

    /// <summary>
    /// A player command travelling the lockstep network. Commands are the ONLY mutator of
    /// sim state in multiplayer: they are scheduled COMMAND_DELAY turns ahead, aggregated
    /// by the host into per-turn bundles, and applied by SimCommandExecutor at the same
    /// turn on every peer. The legacy TrainSoldier type was removed — Train carries the
    /// full template name and a count.
    /// </summary>
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
        /// Template name for Train/Build (entity template) or Research (technology id).
        /// Carried with the command so every peer resolves the exact same data.
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
            byte[] tmplBytes = System.Text.Encoding.UTF8.GetBytes(TemplateName);
            bw.Write(tmplBytes.Length);
            bw.Write(tmplBytes);
            return ms.ToArray();
        }

        public static NetCommand Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            uint player = br.ReadUInt32();
            var type = (NetCommandType)br.ReadByte();
            uint entityId = br.ReadUInt32();
            int p1 = br.ReadInt32();
            int p2 = br.ReadInt32();
            int fp1 = br.ReadInt32();
            int fp2 = br.ReadInt32();
            // Matches Serialize: raw int32 byte count + raw UTF8 bytes (NOT ReadString,
            // which expects a 7-bit-encoded length prefix and would misalign the stream).
            int tmplLen = br.ReadInt32();
            string templateName = System.Text.Encoding.UTF8.GetString(br.ReadBytes(tmplLen));
            return new NetCommand(player, type, entityId, p1, p2, fp1, fp2, templateName);
        }

        /// <summary>Length-prefixed batch framing for per-turn bundles and client batches.</summary>
        public static byte[] SerializeBatch(IReadOnlyList<NetCommand> commands)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(commands.Count);
            foreach (var cmd in commands)
            {
                byte[] payload = cmd.Serialize();
                bw.Write(payload.Length);
                bw.Write(payload);
            }
            return ms.ToArray();
        }

        public static NetCommand[] DeserializeBatch(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            int count = br.ReadInt32();
            var commands = new NetCommand[count];
            for (int i = 0; i < count; i++)
            {
                int len = br.ReadInt32();
                commands[i] = Deserialize(br.ReadBytes(len));
            }
            return commands;
        }

        public static NetCommand Move(uint player, uint entityId, Fixed x, Fixed z) =>
            new(player, NetCommandType.Move, entityId, 0, 0, x.InternalValue, z.InternalValue);

        /// <summary>Gather: IntParam1 = target supply entity id.</summary>
        public static NetCommand Gather(uint player, uint unitId, uint targetId) =>
            new(player, NetCommandType.Gather, unitId, (int)targetId);

        /// <summary>Attack: IntParam1 = target entity id; IntParam2 = allowCapture (0/1,
        /// 原版 cmd.allowCapture,GUI Ctrl+攻击)。</summary>
        public static NetCommand Attack(uint player, uint attackerId, uint targetId, bool allowCapture = false) =>
            new(player, NetCommandType.Attack, attackerId, (int)targetId, allowCapture ? 1 : 0);

        /// <summary>Build: EntityId = builder, TemplateName = full building template,
        /// FixedParam1/2 = world x/z. Cost charge + foundation spawn happen at execution.</summary>
        public static NetCommand Build(uint player, uint builderId, string template, Fixed x, Fixed z) =>
            new(player, NetCommandType.Build, builderId, 0, 0, x.InternalValue, z.InternalValue, template);

        /// <summary>Train: IntParam1 = count (batch training sends 5 as one command).</summary>
        public static NetCommand Train(uint player, uint buildingId, string templateName, int count = 1) =>
            new(player, NetCommandType.Train, buildingId, count, 0, 0, 0, templateName);

        /// <summary>Research: TemplateName = technology id.</summary>
        public static NetCommand Research(uint player, uint buildingId, string techName) =>
            new(player, NetCommandType.Research, buildingId, 0, 0, 0, 0, techName);

        /// <summary>SetRallyPoint: IntParam1 = target entity id (0 = clear).</summary>
        public static NetCommand SetRallyPoint(uint player, uint buildingId, uint targetEntityId) =>
            new(player, NetCommandType.SetRallyPoint, buildingId, (int)targetEntityId);
    }
}
