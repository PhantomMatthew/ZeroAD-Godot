using System;
using System.IO;
using System.Security.Cryptography;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Serialization;

/// <summary>
/// Serializer interface — simplified C# translation of <c>ISerializer</c>.
/// Field names are ignored in binary/hash mode (used only by debug serializer).
/// All multi-byte values are written little-endian for cross-platform consistency.
/// </summary>
public interface ISerializer
{
    void NumberU8(string name, byte value);
    void NumberI8(string name, sbyte value);
    void NumberU16(string name, ushort value);
    void NumberI16(string name, short value);
    void NumberU32(string name, uint value);
    void NumberI32(string name, int value);
    void NumberFixed(string name, Fixed value);
    void Bool(string name, bool value);
    void StringASCII(string name, string value);
    void RawBytes(string name, ReadOnlySpan<byte> data);
}

public interface IDeserializer
{
    byte NumberU8(string name);
    sbyte NumberI8(string name);
    ushort NumberU16(string name);
    short NumberI16(string name);
    uint NumberU32(string name);
    int NumberI32(string name);
    Fixed NumberFixed(string name);
    bool Bool(string name);
    string StringASCII(string name);
    void RawBytes(string name, Span<byte> data);
}

internal static class LittleEndian
{
    public static byte[] Bytes(ushort v) => new[] { (byte)v, (byte)(v >> 8) };
    public static byte[] Bytes(short v) => Bytes((ushort)v);
    public static byte[] Bytes(uint v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };
    public static byte[] Bytes(int v) => Bytes((uint)v);
}

/// <summary>
/// Hash serializer for OOS detection. Feeds all serialized data into MD5.
/// Matches the design of <c>CHashSerializer</c> — not cryptographically strong,
/// but fast and sufficient for detecting unintended state divergence.
/// </summary>
public sealed class HashSerializer : ISerializer
{
    private readonly IncrementalMD5 _md5 = new();

    public byte[] ComputeHash() => _md5.ComputeHash();

    public void NumberU8(string name, byte value) => _md5.Update(new[] { value });
    public void NumberI8(string name, sbyte value) => _md5.Update(new[] { (byte)value });
    public void NumberU16(string name, ushort value) => _md5.Update(LittleEndian.Bytes(value));
    public void NumberI16(string name, short value) => _md5.Update(LittleEndian.Bytes(value));
    public void NumberU32(string name, uint value) => _md5.Update(LittleEndian.Bytes(value));
    public void NumberI32(string name, int value) => _md5.Update(LittleEndian.Bytes(value));
    public void NumberFixed(string name, Fixed value) => NumberI32(name, value.InternalValue);
    public void Bool(string name, bool value) => _md5.Update(new[] { (byte)(value ? 1 : 0) });
    public void StringASCII(string name, string value) => RawBytes(name, System.Text.Encoding.ASCII.GetBytes(value));
    public void RawBytes(string name, ReadOnlySpan<byte> data) => _md5.Update(data);
}

/// <summary>
/// Binary serializer for save/load and network state transfer.
/// </summary>
public sealed class BinarySerializer : ISerializer
{
    private readonly BinaryWriter _writer;

    public BinarySerializer(BinaryWriter writer) => _writer = writer;

    public void NumberU8(string name, byte value) => _writer.Write(value);
    public void NumberI8(string name, sbyte value) => _writer.Write(value);
    public void NumberU16(string name, ushort value) => _writer.Write(value);
    public void NumberI16(string name, short value) => _writer.Write(value);
    public void NumberU32(string name, uint value) => _writer.Write(value);
    public void NumberI32(string name, int value) => _writer.Write(value);
    public void NumberFixed(string name, Fixed value) => _writer.Write(value.InternalValue);
    public void Bool(string name, bool value) => _writer.Write(value);
    public void StringASCII(string name, string value) => _writer.Write(value);
    public void RawBytes(string name, ReadOnlySpan<byte> data) => _writer.Write(data);
}

internal sealed class IncrementalMD5
{
    private readonly MD5 _md5 = MD5.Create();

    public void Update(ReadOnlySpan<byte> data)
    {
        byte[] array = data.ToArray();
        _md5.TransformBlock(array, 0, array.Length, null, 0);
    }

    public byte[] ComputeHash()
    {
        _md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return _md5.Hash ?? throw new InvalidOperationException("MD5 hash computation failed");
    }
}
