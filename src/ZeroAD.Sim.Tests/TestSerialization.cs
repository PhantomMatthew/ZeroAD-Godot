using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Tests;

/// <summary>Captures every written field (bytes preserved) so a ReplayingDeserializer
/// can feed them back — the repo has no production binary deserializer yet.</summary>
internal sealed class CapturingSerializer : ISerializer
{
    public readonly List<object?> Fields = new();
    public void NumberU8(string n, byte v) => Fields.Add(v);
    public void NumberI8(string n, sbyte v) => Fields.Add(v);
    public void NumberU16(string n, ushort v) => Fields.Add(v);
    public void NumberI16(string n, short v) => Fields.Add(v);
    public void NumberU32(string n, uint v) => Fields.Add(v);
    public void NumberU64(string n, ulong v) => Fields.Add(v);
    public void NumberI64(string n, long v) => Fields.Add(v);
    public void NumberFloat(string n, float v) => Fields.Add(v);
    public void NumberDouble(string n, double v) => Fields.Add(v);
    public void NumberI32(string n, int v) => Fields.Add(v);
    public void NumberFixed(string n, Fixed v) => Fields.Add(v.InternalValue);
    public void Bool(string n, bool v) => Fields.Add(v);
    public void StringASCII(string n, string v) => Fields.Add(v);
    public void RawBytes(string n, ReadOnlySpan<byte> data) => Fields.Add(data.ToArray());
}

internal sealed class ReplayingDeserializer : IDeserializer
{
    private readonly List<object?> _f;
    private int _i;
    public ReplayingDeserializer(CapturingSerializer s) => _f = s.Fields;
    private T Next<T>() => (T)_f[_i++]!;
    public byte NumberU8(string n) => Next<byte>();
    public sbyte NumberI8(string n) => Next<sbyte>();
    public ushort NumberU16(string n) => Next<ushort>();
    public short NumberI16(string n) => Next<short>();
    public uint NumberU32(string n) => Next<uint>();
    public ulong NumberU64(string n) => Next<ulong>();
    public long NumberI64(string n) => Next<long>();
    public float NumberFloat(string n) => Next<float>();
    public double NumberDouble(string n) => Next<double>();
    public int NumberI32(string n) => Next<int>();
    public Fixed NumberFixed(string n) => Fixed.Zero.WithInternalValue(Next<int>());
    public bool Bool(string n) => Next<bool>();
    public string StringASCII(string n) => Next<string>();
    public void RawBytes(string n, Span<byte> data) => Next<byte[]>().CopyTo(data);
}
