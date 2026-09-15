using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>多形状阻挡摘除:CLUSTER 子件独立 tag,DestroyEntity 全清。</summary>
public sealed class MultiShapeObstructionTests
{
    [Fact]
    public void SubShapes_RegisterSeparately_BlockIndependently()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var mgr = new ObstructionManager();
        SimSystem.SetObstructionManager(mgr);

        var gate = cm.CreateEntity();
        cm.AddComponent(gate, new PositionComponent());
        cm.QueryInterface<PositionComponent>(gate)!.Position =
            new FixedVector3D(Fixed.FromInt(50), Fixed.Zero, Fixed.FromInt(50));
        var obs = new ObstructionComponent
        {
            Type = ObstructionType.Static,
            Size0 = Fixed.FromInt(36),
            Size1 = Fixed.FromInt(6),
            Flags = ObstructionFlags.DefaultBlock,
        };
        obs.SubShapes.Add(("Door", Fixed.Zero, Fixed.Zero, Fixed.FromInt(15), Fixed.FromInt(6)));
        obs.SubShapes.Add(("Left", Fixed.FromInt(-12), Fixed.Zero, Fixed.FromInt(8), Fixed.FromInt(6)));
        obs.SubShapes.Add(("Right", Fixed.FromInt(12), Fixed.Zero, Fixed.FromInt(8), Fixed.FromInt(6)));
        cm.AddComponent(gate, obs);
        obs.EnsureRegistered();

        ObstructionShapeFilter movement = (_, f, _, _) => (f & ObstructionFlags.BlockMovement) == 0;
        Assert.NotEmpty(mgr.TestUnitShape(movement,
            Fixed.FromInt(38), Fixed.FromInt(50), Fixed.FromInt(1)));
        Assert.NotEmpty(mgr.TestUnitShape(movement,
            Fixed.FromInt(50), Fixed.FromInt(50), Fixed.FromInt(1)));

        cm.DestroyEntity(gate);
        Assert.Empty(mgr.TestUnitShape(movement,
            Fixed.FromInt(38), Fixed.FromInt(50), Fixed.FromInt(1)));
        Assert.Empty(mgr.TestUnitShape(movement,
            Fixed.FromInt(50), Fixed.FromInt(50), Fixed.FromInt(1)));
    }
}
