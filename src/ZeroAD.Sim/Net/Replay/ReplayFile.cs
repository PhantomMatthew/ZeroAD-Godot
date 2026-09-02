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
        public const uint Version = 2;   // v2: AIComponent 增 HQ 尾段(初始状态载荷格式随存档 v12)

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

    /// <summary>逐回合追加命令批。turn 应单调递增；空回合也必须写以保持回放 turn 算术一致。
    /// 命令流写完后,Dispose 前调 WriteHashLog 写尾部哈希日志段(确定性回归验证用)。</summary>
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

        /// <summary>写尾部哈希日志段(命令流结束后、Dispose 前)。格式:标记字节 0xHA +
        /// count(uint32) + count × (turn uint32 + 16 字节 MD5)。录像回放时按此段校验
        /// 每个检查点回合的确定性状态哈希,捕捉 desync 回归。Version 不变——旧 reader
        /// 在 TryReadTurnBatch 遇到此标记字节会因 batchLen 异常而停止(命令流已读完,
        /// 流位置 >= 长度则正常返回 false),故向后兼容。</summary>
        public void WriteHashLog(IReadOnlyDictionary<uint, byte[]> hashes)
        {
            _bw.Write((byte)0xAD);  // 段标记(区别于命令流的 turn uint32 首字节)
            _bw.Write((uint)hashes.Count);
            foreach (var kv in hashes)
            {
                _bw.Write(kv.Key);
                if (kv.Value.Length != 16) throw new InvalidDataException("hash must be 16 bytes (MD5)");
                _bw.Write(kv.Value);
            }
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

        /// <summary>读下一条命令批。返回 false 表示命令流结束（录像播完或遇到哈希日志段标记）。
        /// 命令流结束后若存在尾部哈希日志段(标记 0xAD),留给 TryReadHashLog 读。</summary>
        public bool TryReadTurnBatch(out uint turn, out NetCommand[] commands)
        {
            turn = 0; commands = Array.Empty<NetCommand>();
            if (_disposed) return false;
            Stream s = _br.BaseStream;
            if (s.Position >= s.Length) return false;
            // 哈希日志段标记(0xAD):命令流到此结束,后续是哈希日志。停读命令,不消费标记。
            // 用 ReadByte 确定性判断(BinaryReader.PeekChar 受编码影响:0xAD 作为 char
            // 可能解码成软连字符,值不符);是标记则回退一字节,留给 TryReadHashLog 读。
            int b = _br.ReadByte();
            if (b == 0xAD) { s.Position -= 1; return false; }
            // 不是标记 → 是 turn uint32 的首字节(小端)。重建完整 turn:首字节 + 续读 3 字节。
            turn = (uint)b | ((uint)_br.ReadByte() << 8) | ((uint)_br.ReadByte() << 16) | ((uint)_br.ReadByte() << 24);
            uint batchLen = _br.ReadUInt32();
            byte[] batch = _br.ReadBytes(checked((int)batchLen));
            if (batch.Length != batchLen)
                throw new InvalidDataException($"Truncated command batch at turn {turn}.");
            commands = NetCommand.DeserializeBatch(batch);
            if (turn > _maxTurn) _maxTurn = turn;
            return true;
        }

        /// <summary>读尾部哈希日志段(命令流结束后)。无此段(旧录像)返回空字典。
        /// 回放驱动器据此在每个 HashCheckInterval 回合对比录制时存的状态哈希,
        /// 捕捉确定性回归(OOS)。必须在所有 TryReadTurnBatch 读完后调用。</summary>
        public Dictionary<uint, byte[]> TryReadHashLog()
        {
            var result = new Dictionary<uint, byte[]>();
            if (_disposed) return result;
            Stream s = _br.BaseStream;
            // 命令流可能已读完到末尾(旧录像无标记)或停在 0xAD 标记处(新录像)。
            if (s.Position >= s.Length) return result;  // 无哈希日志段
            if (_br.ReadByte() != 0xAD) return result;   // 非 0xAD → 旧格式或损坏,忽略
            uint count = _br.ReadUInt32();
            for (int i = 0; i < count; i++)
            {
                uint turn = _br.ReadUInt32();
                byte[] hash = _br.ReadBytes(16);
                if (hash.Length == 16) result[turn] = hash;
            }
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _br.Dispose();
        }
    }
}
