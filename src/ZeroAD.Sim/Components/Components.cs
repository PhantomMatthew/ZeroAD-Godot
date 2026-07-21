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
    // Default lives on the field initializer (-1 = no owner), not OnInit, so callers using
    // `new OwnershipComponent { PlayerId = 2 }` keep their value. Previously OnInit reset this
    // to -1 after the object initializer ran, silently clobbering every caller's owner.
    public int PlayerId = -1;

    protected override void OnInit() { }

    public override void Serialize(ISerializer serializer) =>
        serializer.NumberI32("player", PlayerId);

    public override void Deserialize(IDeserializer deserializer) =>
        PlayerId = deserializer.NumberI32("player");

    public void HandleMessage(IMessage message) { }
}
