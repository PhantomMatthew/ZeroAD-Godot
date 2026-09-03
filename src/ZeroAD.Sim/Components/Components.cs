using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

[Component("Position", "Position")]
public sealed class PositionComponent : ComponentBase, IComponentMessageHandler
{
    public FixedVector3D Position;
    public FixedVector3D Rotation;
    /// <summary>原版 CCmpPosition::m_RotYSpeed(Position/TurnRate 模板,rad/s):
    /// sim 侧转向物理(UnitMotion)与表现层 yaw 平滑共用。装配自模板,随存档。</summary>
    public Fixed TurnRate = Fixed.FromInt(14);
    /// <summary>Port of CCmpPosition::m_InWorld:驻防/搭载时移出世界(false)——
    /// 离开空间索引与 LOS、不渲染、不参与范围查询。RangeManager 经 SetInWorld 同步。</summary>
    public bool InWorld = true;

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
        serializer.Bool("inWorld", InWorld);
        serializer.NumberFixed("turnRate", TurnRate);   // 存档 v15
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
        InWorld = deserializer.Bool("inWorld");
        TurnRate = deserializer.NumberFixed("turnRate");
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
