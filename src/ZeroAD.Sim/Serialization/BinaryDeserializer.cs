using System;
using System.IO;

namespace ZeroAD.Sim.Serialization;

/// <summary>
/// Binary deserializer — mirror of <see cref="BinarySerializer"/>. Reads back
/// exactly what BinarySerializer wrote (little-endian numbers, length-prefixed
/// strings, pre-sized raw bytes). Used by the save/load system to restore a
/// game state previously written via <see cref="StateDump"/> or SaveGameManager.
/// </summary>
public sealed class BinaryDeserializer : IDeserializer
{
    private readonly BinaryReader _reader;

    public BinaryDeserializer(BinaryReader reader) => _reader = reader;

    /// <summary>Current stream position (diagnostic only).</summary>
    public long Position => _reader.BaseStream.Position;

    public byte NumberU8(string name) => _reader.ReadByte();
    public sbyte NumberI8(string name) => _reader.ReadSByte();
    public ushort NumberU16(string name) => _reader.ReadUInt16();
    public short NumberI16(string name) => _reader.ReadInt16();
    public uint NumberU32(string name) => _reader.ReadUInt32();
    public int NumberI32(string name) => _reader.ReadInt32();
    public ZeroAD.Sim.Maths.Fixed NumberFixed(string name) =>
        ZeroAD.Sim.Maths.Fixed.Zero.WithInternalValue(_reader.ReadInt32());
    public bool Bool(string name) => _reader.ReadBoolean();
    public string StringASCII(string name) => _reader.ReadString();
    public void RawBytes(string name, Span<byte> data) =>
        _reader.Read(data);
}
