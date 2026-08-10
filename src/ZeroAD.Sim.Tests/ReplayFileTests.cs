using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Tests;

/// <summary>录像文件格式测试（纯内核，无 Godot）。覆盖 header/payload/命令流的往返正确性。
/// 设计要点：录制 = 初始状态(SerializeSaveGame) + 命令流(SerializeBatch)。
/// 这两个底层序列化已被 SaveLoadRoundTripTests 和 NetLockstepTests 验证；本测试聚焦于
/// ReplayFile 的 framing 层：magic/version/header 字段/payload 长度前缀/命令记录 framing。</summary>
public class ReplayFileTests
{
    private static ComponentManager MakeMinimalWorld()
    {
        var cm = new ComponentManager(rngSeed: 42);
        cm.Registry.AutoRegister(typeof(PositionComponent).Assembly);
        // 给几个实体挂组件，让 SerializeSaveGame 有非平凡内容可序列化（而非空世界）。
        for (int i = 0; i < 3; i++)
        {
            var e = cm.CreateEntity();
            cm.AddComponent(e, new PositionComponent());
        }
        return cm;
    }

    private static ReplayMeta SampleMeta(int commandDelay = 2) => new(
        MapPath: "maps/scenarios/arcadia",
        MapType: "singleplayer",
        Tutorial: false,
        LocalPlayerId: 1,
        Role: NetRole.Standalone,
        CommandDelay: commandDelay,
        Slots: new List<PlayerSlotSetup>
        {
            new() { PlayerId = 1, Kind = PlayerSlotKind.Human, Civ = "athen", Team = -1 },
            new() { PlayerId = 2, Kind = PlayerSlotKind.AI, Civ = "spart", Team = -1 },
        },
        TimeUnix: 1700000000L,
        Description: "Test match",
        EngineVersion: "0.29.0");

    private static byte[] WriteReplay(ReplayMeta meta, ComponentManager cm, IEnumerable<(uint turn, NetCommand[] batch)> records)
    {
        using var ms = new MemoryStream();
        using (var rec = ReplayFile.BeginRecording(ms, meta, cm))
            foreach (var (turn, batch) in records)
                rec.WriteTurnBatch(turn, batch);
        return ms.ToArray();
    }

    [Fact]
    public void RoundTrip_Header_Payload_CommandStream_AllMatch()
    {
        var cm = MakeMinimalWorld();
        var meta = SampleMeta();
        // 捕获期望的初始状态 payload（独立序列化，用于对比）
        byte[] expectedPayload;
        using (var pms = new MemoryStream())
        {
            cm.SerializeSaveGame(new BinarySerializer(new BinaryWriter(pms)));
            expectedPayload = pms.ToArray();
        }

        var cmds = new[]
        {
            (0u, Array.Empty<NetCommand>()),                      // commandDelay 内的空回合
            (1u, new[] { NetCommand.Move(player: 1, entityId: 1, Fixed.FromInt(10), Fixed.FromInt(20)) }),
            (2u, new[] { NetCommand.Train(player: 1, buildingId: 5, templateName: "units/athen/support_citizen") }),
        };
        byte[] file = WriteReplay(meta, cm, cmds);

        using var reader = ReplayFile.Open(new MemoryStream(file));
        // header
        Assert.Equal(meta.MapPath, reader.Meta.MapPath);
        Assert.Equal(meta.MapType, reader.Meta.MapType);
        Assert.False(reader.Meta.Tutorial);
        Assert.Equal(meta.LocalPlayerId, reader.Meta.LocalPlayerId);
        Assert.Equal(NetRole.Standalone, reader.Meta.Role);
        Assert.Equal(meta.CommandDelay, reader.Meta.CommandDelay);
        Assert.Equal(2, reader.Meta.Slots.Count);
        Assert.Equal("athen", reader.Meta.Slots[0].Civ);
        Assert.Equal(PlayerSlotKind.AI, reader.Meta.Slots[1].Kind);
        Assert.Equal(meta.TimeUnix, reader.Meta.TimeUnix);
        Assert.Equal(meta.Description, reader.Meta.Description);
        Assert.Equal(meta.EngineVersion, reader.Meta.EngineVersion);
        // initial-state payload 逐位一致
        Assert.Equal(expectedPayload, reader.InitialStatePayload);
        // 命令流逐条一致
        var read = new List<(uint turn, NetCommand[] batch)>();
        while (reader.TryReadTurnBatch(out uint t, out var b))
            read.Add((t, b));
        Assert.Equal(3, read.Count);
        Assert.Equal(0u, read[0].turn); Assert.Empty(read[0].batch);
        Assert.Equal(1u, read[1].turn);
        Assert.Single(read[1].batch);
        Assert.Equal(NetCommandType.Move, read[1].batch[0].Type);
        Assert.Equal(2u, read[2].turn);
        Assert.Equal(NetCommandType.Train, read[2].batch[0].Type);
        Assert.Equal("units/athen/support_citizen", read[2].batch[0].TemplateName);
    }

    [Fact]
    public void EmptyTurnBatches_PreserveTurnArithmetic()
    {
        // 连续空批（沉默回合）必须按序保留，否则回放 turn 算术会错位。
        var cm = MakeMinimalWorld();
        var meta = SampleMeta(commandDelay: 1);
        var cmds = new[]
        {
            (0u, Array.Empty<NetCommand>()),
            (1u, Array.Empty<NetCommand>()),
            (2u, Array.Empty<NetCommand>()),
            (3u, Array.Empty<NetCommand>()),
        };
        byte[] file = WriteReplay(meta, cm, cmds);

        using var reader = ReplayFile.Open(new MemoryStream(file));
        for (uint expected = 0; expected <= 3; expected++)
        {
            Assert.True(reader.TryReadTurnBatch(out uint turn, out var batch));
            Assert.Equal(expected, turn);
            Assert.Empty(batch);
        }
        Assert.False(reader.TryReadTurnBatch(out _, out _)); // 流结束
        Assert.Equal(3u, reader.MaxTurnSeen);
    }

    [Fact]
    public void TryReadTurnBatch_ReturnsFalse_AtEndOfStream()
    {
        var cm = MakeMinimalWorld();
        var emptyRecords = System.Array.Empty<(uint turn, NetCommand[] batch)>();
        byte[] file = WriteReplay(SampleMeta(), cm, emptyRecords);

        using var reader = ReplayFile.Open(new MemoryStream(file));
        // 无命令记录 → 立即返回 false
        Assert.False(reader.TryReadTurnBatch(out _, out _));
    }

    [Fact]
    public void ReadHeader_DoesNotConsume_PayloadOrCommands()
    {
        // 浏览器列表只需 header；ReadHeader 不能消费 payload（否则后续 Open 会失败）。
        var cm = MakeMinimalWorld();
        var meta = SampleMeta();
        byte[] file = WriteReplay(meta, cm, new[] { (0u, Array.Empty<NetCommand>()) });

        using var headerStream = new MemoryStream(file);
        var headerMeta = ReplayFile.ReadHeader(headerStream);
        Assert.NotNull(headerMeta);
        Assert.Equal(meta.MapPath, headerMeta!.MapPath);
        Assert.Equal(meta.CommandDelay, headerMeta.CommandDelay);
        // ReadHeader 只读了 header，Position 应停在 payload 长度前缀之前（magic+version+header 之后）
        // 关键验证：能从同一字节流重新 Open（独立流，证明文件完整可读）
        using var fullStream = new MemoryStream(file);
        using var reader = ReplayFile.Open(fullStream);
        Assert.Equal(expectedPayloadSizeFor(cm), reader.InitialStatePayload.Length);
        Assert.True(reader.TryReadTurnBatch(out _, out _)); // 命令记录可读
    }

    private static int expectedPayloadSizeFor(ComponentManager cm)
    {
        using var ms = new MemoryStream();
        cm.SerializeSaveGame(new BinarySerializer(new BinaryWriter(ms)));
        return (int)ms.Length;
    }

    [Fact]
    public void Open_RejectsWrongMagic()
    {
        using var ms = new MemoryStream();
        // leaveOpen: true —— BinaryWriter 默认 dispose 会关闭底层 MemoryStream，导致后续 Position 失败。
        var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write("WRONGMG"u8.ToArray());
        bw.Write(ReplayFile.Version);
        bw.Flush();
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => ReplayFile.Open(ms));
    }

    [Fact]
    public void Open_RejectsWrongVersion()
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        foreach (char c in ReplayFile.Magic) bw.Write((byte)c);
        bw.Write(uint.MaxValue); // 不匹配的版本
        bw.Flush();
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => ReplayFile.Open(ms));
    }

    [Fact]
    public void HashLog_RoundTrip_PreservesCheckpoints()
    {
        // 录制:写命令流 + 尾部哈希日志段;读回:命令流读完 → 读哈希日志 → 逐条一致。
        var cm = MakeMinimalWorld();
        var meta = SampleMeta();
        var cmds = new[]
        {
            (0u, Array.Empty<NetCommand>()),
            (1u, new[] { NetCommand.Move(1, 1, Fixed.FromInt(5), Fixed.FromInt(6)) }),
        };
        // 模拟两个校验点的哈希(实际由 ReplayRecorder 在 OnTurnAdvanced 存入)。
        var hashes = new Dictionary<uint, byte[]>
        {
            [20] = cm.ComputeStateHash(),
            [40] = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 },
        };

        using var ms = new MemoryStream();
        using (var rec = ReplayFile.BeginRecording(ms, meta, cm))
        {
            foreach (var (turn, batch) in cmds)
                rec.WriteTurnBatch(turn, batch);
            rec.WriteHashLog(hashes);
        }

        using var reader = ReplayFile.Open(new MemoryStream(ms.ToArray()));
        // 先读完命令流
        int batchCount = 0;
        while (reader.TryReadTurnBatch(out _, out _)) batchCount++;
        Assert.Equal(2, batchCount);
        // 再读哈希日志段
        var readHashes = reader.TryReadHashLog();
        Assert.Equal(2, readHashes.Count);
        Assert.Equal(hashes[20], readHashes[20]);
        Assert.Equal(hashes[40], readHashes[40]);
    }

    [Fact]
    public void HashLog_AbsentInOldReplay_ReturnsEmpty()
    {
        // 旧录像(无 WriteHashLog 调用)读哈希日志 → 空字典,不报错(向后兼容)。
        var cm = MakeMinimalWorld();
        byte[] file = WriteReplay(SampleMeta(), cm, new[] { (0u, Array.Empty<NetCommand>()) });
        using var reader = ReplayFile.Open(new MemoryStream(file));
        while (reader.TryReadTurnBatch(out _, out _)) { }
        var hashes = reader.TryReadHashLog();
        Assert.Empty(hashes);
    }
}
