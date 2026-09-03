using System;
using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;
using Xunit;

namespace ZeroAD.Sim.Tests;

// Tests for PlayerManager / WaterManager. These are standalone managers (not IComponent),
// so they're tested directly rather than through the component registry.
public sealed class ManagerTests
{
    [Fact]
    public void PlayerManager_AddAndGet_PlayerEntityByPlayerId()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent());
        cm.Players.AddPlayer(playerId: 1, playerEntity);

        Assert.Equal(playerEntity, cm.Players.GetPlayerEntityId(1));
        Assert.NotNull(cm.Players.GetPlayerEntity(1));
        Assert.Null(cm.Players.GetPlayerEntity(2)); // unregistered
        Assert.Contains(1, cm.Players.GetNonGaiaPlayerIds());
    }

    [Fact]
    public void PlayerManager_ApplyOwnershipPopChange_ChargesAndRefundsPop()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var playerEntity = cm.CreateEntity();
        cm.AddComponent(playerEntity, new PlayerComponent());
        cm.Players.AddPlayer(1, playerEntity);
        var player = cm.Players.GetPlayerEntity(1)!;

        var unit = cm.CreateEntity();
        cm.AddComponent(unit, new CostComponent { PopulationCost = 2 });

        // Grant ownership to player 1 → pop used increases.
        cm.Players.ApplyOwnershipPopChange(unit, oldOwner: -1, newOwner: 1);
        Assert.Equal(2, player.PopUsed);

        // Remove ownership (death) → pop used decreases.
        cm.Players.ApplyOwnershipPopChange(unit, oldOwner: 1, newOwner: -1);
        Assert.Equal(0, player.PopUsed);
    }

    [Fact]
    public void WaterManager_SetAndGet_GlobalHeight()
    {
        var water = new WaterManager();
        Assert.False(water.HasWater);

        water.SetWaterLevel(Fixed.FromFloat(5.5f));
        Assert.True(water.HasWater);
        // GetWaterLevel ignores coordinates (global height, matches CCmpWaterManager).
        Assert.Equal(Fixed.FromFloat(5.5f), water.GetWaterLevel(Fixed.Zero, Fixed.Zero));
        Assert.Equal(Fixed.FromFloat(5.5f), water.GetWaterLevel(Fixed.FromFloat(100f), Fixed.FromFloat(-50f)));
    }

    [Fact]
    public void WaterManager_SerializeRoundtrip_PreservesHeight()
    {
        var water = new WaterManager();
        water.SetWaterLevel(Fixed.FromFloat(3.25f));

        var captured = new List<(string, int)>();
        var s = new CapturingSerializer();
        water.Serialize(s);

        var d = new ReplayingDeserializer(s.Values);
        var restored = new WaterManager();
        restored.Deserialize(d);

        Assert.True(restored.HasWater);
        Assert.Equal(Fixed.FromFloat(3.25f), restored.WaterHeight);
    }

    [Fact]
    public void PlayerManager_SerializeRoundtrip_PreservesRegistry()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var gaia = cm.CreateEntity();
        var p1 = cm.CreateEntity();
        cm.Players.AddPlayer(0, gaia);
        cm.Players.AddPlayer(1, p1);

        var s = new CapturingSerializer();
        cm.Players.Serialize(s);

        // Fresh manager, same ComponentManager, deserialize the registry back.
        var restored = new PlayerManager(cm);
        var d = new ReplayingDeserializer(s.Values);
        restored.Deserialize(d);

        Assert.Equal(gaia, restored.GetPlayerEntityId(0));
        Assert.Equal(p1, restored.GetPlayerEntityId(1));
        Assert.Equal(2, restored.GetNumPlayers());
    }

    // Minimal capturing serializer — records (name,value) in order so a matching
    // ReplayingDeserializer can play them back. Sufficient for manager round-trip tests
    // where the real BinarySerializer/HashSerializer aren't needed.
    private sealed class CapturingSerializer : ISerializer
    {
        public readonly List<(string Name, int IntValue, bool IsBool, bool BoolValue)> Values = new();
        public void NumberU8(string n, byte v) => Values.Add((n, v, false, false));
        public void NumberI8(string n, sbyte v) => Values.Add((n, v, false, false));
        public void NumberU16(string n, ushort v) => Values.Add((n, v, false, false));
        public void NumberI16(string n, short v) => Values.Add((n, v, false, false));
        public void NumberU32(string n, uint v) => Values.Add((n, (int)v, false, false));
        public void NumberI32(string n, int v) => Values.Add((n, v, false, false));
        public void NumberU64(string n, ulong v) => Values.Add((n, (int)v, false, false));
        public void NumberI64(string n, long v) => Values.Add((n, (int)v, false, false));
        public void NumberFloat(string n, float v) => Values.Add((n, (int)v, false, false));
        public void NumberDouble(string n, double v) => Values.Add((n, (int)v, false, false));
        public void NumberFixed(string n, Fixed v) => Values.Add((n, v.InternalValue, false, false));
        public void Bool(string n, bool v) => Values.Add((n, 0, true, v));
        public void StringASCII(string n, string v) => Values.Add((n, v.Length, false, false));
        public void RawBytes(string n, ReadOnlySpan<byte> data) => Values.Add((n, data.Length, false, false));
    }

    private sealed class ReplayingDeserializer : IDeserializer
    {
        private readonly List<(string Name, int IntValue, bool IsBool, bool BoolValue)> _v;
        private int _i;
        public ReplayingDeserializer(List<(string Name, int IntValue, bool IsBool, bool BoolValue)> v) => _v = v;
        private (string, int, bool, bool) Next() => _v[_i++];
        public byte NumberU8(string n) => (byte)Next().Item2;
        public sbyte NumberI8(string n) => (sbyte)Next().Item2;
        public ushort NumberU16(string n) => (ushort)Next().Item2;
        public short NumberI16(string n) => (short)Next().Item2;
        public uint NumberU32(string n) => (uint)Next().Item2;
        public int NumberI32(string n) => Next().Item2;
        public ulong NumberU64(string n) => (ulong)Next().Item2;
        public long NumberI64(string n) => Next().Item2;
        public float NumberFloat(string n) => Next().Item2;
        public double NumberDouble(string n) => Next().Item2;
        public Fixed NumberFixed(string n) => Fixed.Zero.WithInternalValue(Next().Item2);
        public bool Bool(string n) => Next().Item4;
        public string StringASCII(string n) => "";
        public void RawBytes(string n, Span<byte> data) { }
    }
}
