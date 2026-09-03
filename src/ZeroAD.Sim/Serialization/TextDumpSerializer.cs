using System;
using System.Text;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Serialization;

/// <summary>
/// Renders serialized state as "name = value" lines under [entity N] / [component T]
/// section headers. Fixed-point values dump their raw internal value in hex so a
/// plain `diff` of two peers' dumps pinpoints the diverging field exactly.
/// </summary>
public sealed class TextDumpSerializer : ISerializer, ISectionSerializer
{
    private readonly StringBuilder _sb = new();

    public void BeginSection(string name) => _sb.Append("\n[").Append(name).Append("]\n");

    private void Line(string name, string value) =>
        _sb.Append(name).Append(" = ").Append(value).Append('\n');

    public void NumberU8(string name, byte value) => Line(name, value.ToString());
    public void NumberI8(string name, sbyte value) => Line(name, value.ToString());
    public void NumberU16(string name, ushort value) => Line(name, value.ToString());
    public void NumberI16(string name, short value) => Line(name, value.ToString());
    public void NumberU32(string name, uint value) => Line(name, value.ToString());
    public void NumberI32(string name, int value) => Line(name, value.ToString());
    public void NumberU64(string name, ulong value) => Line(name, value.ToString());
    public void NumberI64(string name, long value) => Line(name, value.ToString());
    public void NumberFloat(string name, float value) =>
        Line(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public void NumberDouble(string name, double value) =>
        Line(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public void NumberFixed(string name, Fixed value) =>
        Line(name, "0x" + value.InternalValue.ToString("X8"));
    public void Bool(string name, bool value) => Line(name, value ? "1" : "0");
    public void StringASCII(string name, string value) => Line(name, value);
    public void RawBytes(string name, ReadOnlySpan<byte> data) =>
        Line(name, Convert.ToHexString(data));

    public override string ToString() => _sb.ToString();
}
