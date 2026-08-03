using System;
using System.Collections.Generic;
using System.IO;

namespace ZeroAD.Sim.Net
{
    /// <summary>录像元数据（header 中的对局信息）。镜像 SaveGameManager.SaveMeta，新增 commandDelay/engineVersion。</summary>
    public sealed record ReplayMeta(
        string MapPath,
        string MapType,
        bool Tutorial,
        uint LocalPlayerId,
        NetRole Role,
        int CommandDelay,
        IReadOnlyList<PlayerSlotSetup> Slots,
        long TimeUnix,
        string Description,
        string EngineVersion);

    /// <summary>录像文件格式：初始状态 + 命令流。
    /// 设计为确定性内核的"免费产物"——录制旁听已有的 NetTurnManager 命令批，
    /// 播放通过 InjectReplayBundle 注入预录制命令。零改动内核确定性逻辑。
    ///
    /// 文件布局：
    ///   magic "0ADREPL" (7 bytes ASCII)
    ///   version  uint32
    ///   header:  mapPath / mapType / tutorial / localPlayerId / role / commandDelay /
    ///            slots[] / timeUnix / description / engineVersion
    ///   initial-state payload: payloadLen(uint32) + payload bytes (SerializeSaveGame 输出)
    ///   command-stream: 0+ records of (turnNumber uint32, batchLen uint32, batch bytes)。
    /// </summary>
    public static class ReplayFile
    {
        public const string Magic = "0ADREPL";
        public const uint Version = 1;

        /// <summary>开始录制：写 magic + header + 初始状态，返回 ReplayWriter 供逐回合追加命令。</summary>
        public static ReplayWriter BeginRecording(Stream stream, ReplayMeta meta, ComponentManager cm)
        {
            var bw = new BinaryWriter(stream);
            WriteHeaderAndPayload(bw, meta, cm);
            return new ReplayWriter(bw);
        }

        /// <summary>打开录像：读 magic + header + 初始状态，返回 ReplayReader 供逐回合读命令。</summary>
        public static ReplayReader Open(Stream stream)
        {
            var br = new BinaryReader(stream);
            var meta = ReadHeader(br)
                ?? throw new InvalidDataException("Not a valid replay file (magic/version mismatch).");
            uint payloadLen = br.ReadUInt32();
            byte[] payload = br.ReadBytes(checked((int)payloadLen));
            if (payload.Length != payloadLen)
                throw new InvalidDataException("Truncated initial-state payload in replay.");
            return new ReplayReader(br, meta, payload);
        }

        /// <summary>仅读 header（浏览器列表用，不消费 payload/命令流）。
        /// 返回 null 表示 magic/version 不匹配。</summary>
        public static ReplayMeta? ReadHeader(Stream stream)
        {
            var br = new BinaryReader(stream);
            return ReadHeader(br);
        }

        // ── header 读写（镜像 SaveGameManager.ReadHeaderFromStream，新增 commandDelay/engineVersion）──

        private static ReplayMeta? ReadHeader(BinaryReader br)
        {
            for (int i = 0; i < Magic.Length; i++)
                if (br.ReadByte() != (byte)Magic[i])
                    return null;
            uint version = br.ReadUInt32();
            if (version != Version)
                return null;

            string mapPath = br.ReadString();
            string mapType = br.ReadString();
            bool tutorial = br.ReadByte() != 0;
            uint localPlayerId = br.ReadUInt32();
            var role = (NetRole)br.ReadByte();
            int commandDelay = br.ReadInt32();
            int slotCount = br.ReadByte();
            var slots = new List<PlayerSlotSetup>(slotCount);
            for (int i = 0; i < slotCount; i++)
            {
                var kind = (PlayerSlotKind)br.ReadByte();
                string civ = br.ReadString();
                int team = br.ReadInt32();
                slots.Add(new PlayerSlotSetup { PlayerId = i + 1, Kind = kind, Civ = civ, Team = team });
            }
            long timeUnix = br.ReadInt64();
            string description = br.ReadString();
            string engineVersion = br.ReadString();
            return new ReplayMeta(
                mapPath.Length == 0 ? string.Empty : mapPath,
                mapType, tutorial, localPlayerId, role, commandDelay,
                slots, timeUnix, description, engineVersion);
        }

        private static void WriteHeaderAndPayload(BinaryWriter bw, ReplayMeta meta, ComponentManager cm)
        {
            foreach (char c in Magic)
                bw.Write((byte)c);
            bw.Write(Version);
            bw.Write(meta.MapPath ?? string.Empty);
            bw.Write(meta.MapType ?? string.Empty);
            bw.Write((byte)(meta.Tutorial ? 1 : 0));
            bw.Write(meta.LocalPlayerId);
            bw.Write((byte)meta.Role);
            bw.Write(meta.CommandDelay);
            bw.Write((byte)meta.Slots.Count);
            foreach (var s in meta.Slots)
            {
                bw.Write((byte)s.Kind);
                bw.Write(s.Civ ?? string.Empty);
                bw.Write(s.Team);
            }
            bw.Write(meta.TimeUnix);
            bw.Write(meta.Description ?? string.Empty);
            bw.Write(meta.EngineVersion ?? string.Empty);

            // 初始状态 payload：先写长度前缀，再写 SerializeSaveGame 输出。
            // 用临时 MemoryStream 捕获 payload 以便前置长度（流的不可回退性使然）。
            long payloadPos = bw.BaseStream.Position;
            bw.Write(0u);  // 占位，稍后回填真实长度
            long start = bw.BaseStream.Position;
            cm.SerializeSaveGame(new Serialization.BinarySerializer(bw));
            long end = bw.BaseStream.Position;
            uint payloadLen = (uint)(end - start);
            bw.BaseStream.Position = payloadPos;
            bw.Write(payloadLen);
            bw.BaseStream.Position = end;
        }
    }

    /// <summary>逐回合追加命令批。turn 应单调递增；空回合也必须写以保持回放 turn 算术一致。</summary>
    public sealed class ReplayWriter : IDisposable
    {
        private readonly BinaryWriter _bw;
        private bool _disposed;

        internal ReplayWriter(BinaryWriter bw) => _bw = bw;

        public void WriteTurnBatch(uint turn, NetCommand[] commands)
        {
            byte[] batch = NetCommand.SerializeBatch(commands);
            _bw.Write(turn);
            _bw.Write((uint)batch.Length);
            _bw.Write(batch);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bw.Flush();
            _bw.Dispose();
        }
    }

    /// <summary>逐回合读命令批。InitialStatePayload 喂给 ComponentManager.DeserializeSaveGame。</summary>
    public sealed class ReplayReader : IDisposable
    {
        private readonly BinaryReader _br;
        private bool _disposed;
        private uint _maxTurn;

        public ReplayMeta Meta { get; }
        public byte[] InitialStatePayload { get; }
        /// <summary>录像已扫描到的最大回合数（UI 显示 "回合 N / M"）。随 TryRead 单调递增。</summary>
        public uint MaxTurnSeen => _maxTurn;

        internal ReplayReader(BinaryReader br, ReplayMeta meta, byte[] payload)
        {
            _br = br;
            Meta = meta;
            InitialStatePayload = payload;
            _maxTurn = 0;
        }

        /// <summary>读下一条命令批。返回 false 表示命令流结束（录像播完）。</summary>
        public bool TryReadTurnBatch(out uint turn, out NetCommand[] commands)
        {
            turn = 0; commands = Array.Empty<NetCommand>();
            if (_disposed) return false;
            Stream s = _br.BaseStream;
            if (s.Position >= s.Length) return false;
            turn = _br.ReadUInt32();
            uint batchLen = _br.ReadUInt32();
            byte[] batch = _br.ReadBytes(checked((int)batchLen));
            if (batch.Length != batchLen)
                throw new InvalidDataException($"Truncated command batch at turn {turn}.");
            commands = NetCommand.DeserializeBatch(batch);
            if (turn > _maxTurn) _maxTurn = turn;
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _br.Dispose();
        }
    }
}
