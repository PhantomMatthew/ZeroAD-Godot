using System;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;
using Xunit;

namespace ZeroAD.Sim.Tests;

public sealed class NetCommandCodecTests
{
    [Fact]
    public void Batch_RoundTrips_AllCommandTypes()
    {
        var commands = new[]
        {
            NetCommand.Move(1, 10, Fixed.FromFloat(3.5f), Fixed.FromFloat(-7.25f)),
            NetCommand.Gather(1, 10, 55),
            NetCommand.Attack(2, 11, 66),
            NetCommand.Attack(2, 16, 88, allowCapture: true),   // IntParam2 载 allowCapture
            NetCommand.Build(1, 12, "structures/spart/house", Fixed.FromFloat(100f), Fixed.FromFloat(64f), Fixed.FromFloat(MathF.PI * 3f / 4f)),
            NetCommand.Train(2, 13, "units/spart/infantry_spearman_b", count: 5),
            NetCommand.Research(1, 14, "phase_town_generic"),
            NetCommand.SetRallyPoint(2, 15, 77),
            NetCommand.FocusFire(1, 18, 99, queued: true, pushFront: false),   // IntParam2 载 queued/pushFront
            NetCommand.FocusFire(2, 19, 100, queued: false, pushFront: true),
        };

        byte[] data = NetCommand.SerializeBatch(commands);
        var decoded = NetCommand.DeserializeBatch(data);

        Assert.Equal(commands.Length, decoded.Length);
        for (int i = 0; i < commands.Length; i++)
        {
            Assert.Equal(commands[i].Player, decoded[i].Player);
            Assert.Equal(commands[i].Type, decoded[i].Type);
            Assert.Equal(commands[i].EntityId, decoded[i].EntityId);
            Assert.Equal(commands[i].IntParam1, decoded[i].IntParam1);
            Assert.Equal(commands[i].IntParam2, decoded[i].IntParam2);
            Assert.Equal(commands[i].FixedParam1, decoded[i].FixedParam1);
            Assert.Equal(commands[i].FixedParam2, decoded[i].FixedParam2);
            Assert.Equal(commands[i].TemplateName, decoded[i].TemplateName);
        }
    }

    [Fact]
    public void Batch_RoundTrips_Empty()
    {
        var decoded = NetCommand.DeserializeBatch(NetCommand.SerializeBatch(System.Array.Empty<NetCommand>()));
        Assert.Empty(decoded);
    }

    [Fact]
    public void Train_CarriesCount()
    {
        var cmd = NetCommand.Train(1, 42, "units/spart/support_civilian", count: 5);
        Assert.Equal(5, cmd.IntParam1);
        Assert.Equal("units/spart/support_civilian", cmd.TemplateName);
    }

    [Fact]
    public void Build_CarriesTemplateAndWorldPosition()
    {
        var cmd = NetCommand.Build(2, 9, "structures/spart/barracks", Fixed.FromFloat(12.5f), Fixed.FromFloat(99f), Fixed.FromFloat(MathF.PI));
        Assert.Equal("structures/spart/barracks", cmd.TemplateName);
        Assert.Equal(9u, cmd.EntityId);
        Assert.Equal(Fixed.FromFloat(12.5f).InternalValue, cmd.FixedParam1);
        Assert.Equal(Fixed.FromFloat(99f).InternalValue, cmd.FixedParam2);
        // IntParam1 载 yaw 弧度(原版 cmd.angle;对齐 placement.js DEFAULT_ANGLE)。
        Assert.Equal(Fixed.FromFloat(MathF.PI).InternalValue, cmd.IntParam1);
    }
}
