using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

[Component("Position", "Position")]
public sealed class PositionComponent : ComponentBase, IComponentMessageHandler
{
    public FixedVector3D Position;
    public FixedVector3D Rotation;

    protected override void OnInit()
    {
        Position = new FixedVector3D(Fixed.Zero, Fixed.Zero, Fixed.Zero);
        Rotation = new FixedVector3D(Fixed.Zero, Fixed.Zero, Fixed.Zero);
    }

    public override void Serialize(ISerializer serializer)
    {
        serializer.NumberFixed("x", Position.X);
        serializer.NumberFixed("y", Position.Y);
        serializer.NumberFixed("z", Position.Z);
        serializer.NumberFixed("rx", Rotation.X);
        serializer.NumberFixed("ry", Rotation.Y);
        serializer.NumberFixed("rz", Rotation.Z);
    }

    public override void Deserialize(IDeserializer deserializer)
    {
        var x = deserializer.NumberFixed("x");
        var y = deserializer.NumberFixed("y");
        var z = deserializer.NumberFixed("z");
        var rx = deserializer.NumberFixed("rx");
        var ry = deserializer.NumberFixed("ry");
        var rz = deserializer.NumberFixed("rz");
        Position = new FixedVector3D(x, y, z);
        Rotation = new FixedVector3D(rx, ry, rz);
    }

    public void HandleMessage(IMessage message) { }
}

[Component("Ownership", "Ownership")]
public sealed class OwnershipComponent : ComponentBase, IComponentMessageHandler
{
    public int PlayerId;

    protected override void OnInit() => PlayerId = -1;

    public override void Serialize(ISerializer serializer) =>
        serializer.NumberI32("player", PlayerId);

    public override void Deserialize(IDeserializer deserializer) =>
        PlayerId = deserializer.NumberI32("player");

    public void HandleMessage(IMessage message) { }
}
