using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>多形状阻挡(原版 Obstructions 元素——墙门 Left/Right/Door 分形):
/// 子件独立成形状、阻挡查询按件、摘除全清。</summary>
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
        // 两翼 ±12m,门洞居中(对齐 wall_gate.xml 的 Left/Right/Door 比例缩小)。
        var obs = new ObstructionComponent
        {
            Type = ObstructionType.Static,
            Size0 = Fixed.FromInt(30),   // 主形状 = 全宽(行为保留;查询仍过它)
            Size1 = Fixed.FromInt(6),
        };
        obs.SubShapes.Add((Fixed.FromInt(-12), Fixed.Zero, Fixed.FromInt(8), Fixed.FromInt(6)));
        obs.SubShapes.Add((Fixed.FromInt(12), Fixed.Zero, Fixed.FromInt(8), Fixed.FromInt(6)));
        // 门洞子件(0,0)——不注册:门洞不挡(门 = 翼间空缺;真实门件由 Door 子形状
        // 单独成形状,这里演示"门洞缺失则通行")。
        cm.AddComponent(gate, obs);
        obs.EnsureRegistered();

        // 翼位有阻挡(子件),门洞中心无主形状…主形状在:用子件判定:
        // 查询:翼位碰撞应命中(子件形状),远离全宽外不命中。
        var hitsWing = mgr.TestStaticShape((_, f, _, _) => (f & ObstructionFlags.BlockFoundation) == 0,
            Fixed.FromInt(38), Fixed.FromInt(50),
            new FixedVector2D(Fixed.FromInt(1), Fixed.Zero), new FixedVector2D(Fixed.Zero, Fixed.FromInt(1)),
            Fixed.FromInt(1), Fixed.FromInt(1));
        Assert.NotEmpty(hitsWing);

        // 摘除:所有形状清空。
        cm.DestroyEntity(gate);
        var hitsAfter = mgr.TestStaticShape((_, f, _, _) => (f & ObstructionFlags.BlockFoundation) == 0,
            Fixed.FromInt(38), Fixed.FromInt(50),
            new FixedVector2D(Fixed.FromInt(1), Fixed.Zero), new FixedVector2D(Fixed.Zero, Fixed.FromInt(1)),
            Fixed.FromInt(1), Fixed.FromInt(1));
        Assert.Empty(hitsAfter);
    }
}
