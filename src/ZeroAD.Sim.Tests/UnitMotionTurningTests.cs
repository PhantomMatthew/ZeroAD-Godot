using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>UnitMotion 转向物理(原版 PerformMove L1285-1323)测试:
/// 大角度原地转向停走、小角度边走边减速(cos)、到站面向目标。</summary>
public sealed class UnitMotionTurningTests
{
    private static (ComponentManager cm, EntityId e, PositionComponent pos, UnitMotion motion) World()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        var pos = cm.QueryInterface<PositionComponent>(e)!;
        pos.Position = new FixedVector3D(Fixed.Zero, Fixed.Zero, Fixed.Zero);
        pos.TurnRate = Fixed.FromInt(14);
        cm.AddComponent(e, new UnitMotion());
        var motion = cm.QueryInterface<UnitMotion>(e)!;
        motion.Speed = Fixed.FromInt(8);
        return (cm, e, pos, motion);
    }

    [Fact]
    public void SharpTurn_RotatesInPlace_NoMovementUntilAligned()
    {
        var (_, _, pos, motion) = World();
        // 朝 +X 走(目标在 +X);初始面向 −X(π 背对)。
        pos.Rotation = new FixedVector3D(Fixed.Zero, Fixed.Pi, Fixed.Zero);
        motion.InstantTurnAngle = Fixed.FromFraction(3, 2);   // 1.5 rad
        motion.MoveToPoint(new FixedVector2D(Fixed.FromInt(20), Fixed.Zero));

        // 第一拍:偏差 π > 1.5 → 原地转向(0.1s × 14rad/s = 1.4rad),不走。
        motion.Tick(0.1f);
        Assert.Equal(Fixed.Zero, pos.Position.X);
        Assert.True(pos.Rotation.Y < Fixed.Pi - Fixed.FromFloat(0.5f),
            $"should have rotated toward target, got {pos.Rotation.Y.ToFloat()}");

        // 连拍至转完:之后开始走。
        for (int i = 0; i < 10; i++) motion.Tick(0.1f);
        Assert.True(pos.Position.X > Fixed.Zero, "aligned → moves");
        // 对齐后 yaw ≈ 朝 +X(atan2(dx,dz)=atan2(1,0)=π/2)。
        float yaw = pos.Rotation.Y.ToFloat();
        Assert.True(System.Math.Abs(yaw - System.Math.PI / 2) < 0.05f,
            $"final yaw should face +X (π/2), got {yaw}");
    }

    [Fact]
    public void ShallowTurn_SlowsByCosine()
    {
        var (_, _, pos, motion) = World();
        // 目标正前方稍偏 0.3 rad(<1.5 → 小角度):速度 ×cos(0.3)≈0.955。
        // 对照:完全正前方(偏差 0)→ 全速。
        var ahead = new FixedVector2D(Fixed.FromFloat(20f * 0.955f), Fixed.FromFloat(20f * 0.2965f));
        // 初始面向与目标偏差 0.3:直接给定目标方位,初始 yaw = 目标方位 + 0.3。
        motion.MoveToPoint(ahead);
        // MoveToPoint 无寻路(无 Pathfinder)→ 直线单路标;先算目标方位:
        var diff = ahead;   // from origin
        var targetAngle = Trig.Atan2Approx(diff.X, diff.Y);
        pos.Rotation = new FixedVector3D(Fixed.Zero,
            targetAngle + Fixed.FromFraction(3, 10), Fixed.Zero);
        float x0 = pos.Position.X.ToFloat(), z0 = pos.Position.Z.ToFloat();
        motion.Tick(0.1f);
        float moved = (pos.Position.X.ToFloat() - x0) * (pos.Position.X.ToFloat() - x0)
            + (pos.Position.Z.ToFloat() - z0) * (pos.Position.Z.ToFloat() - z0);
        float full = 8f * 0.1f;   // 全速一拍 0.8m
        float movedDist = (float)System.Math.Sqrt(moved);
        // cos(0.3)≈0.955 → 0.764 ± 容差(定点近似)。
        Assert.True(movedDist < full * 0.99f && movedDist > full * 0.90f,
            $"shallow turn should scale speed by cos(0.3)≈0.955, moved {movedDist} vs full {full}");
        // 且 yaw 已瞬对目标方位。
        float yawErr = System.Math.Abs(pos.Rotation.Y.ToFloat() - targetAngle.ToFloat());
        Assert.True(yawErr < 0.02f, $"yaw snapped to target bearing, err {yawErr}");
    }

    [Fact]
    public void Arrival_FacesTargetPoint()
    {
        var (_, _, pos, motion) = World();
        // 背对目标到达:yaw=π(面 −X),目标在 +X 0.5m(一步到)。
        pos.Rotation = new FixedVector3D(Fixed.Zero, Fixed.Pi, Fixed.Zero);
        motion.MoveToPoint(new FixedVector2D(Fixed.FromFraction(1, 2), Fixed.Zero));
        for (int i = 0; i < 5 && motion.HasMoveTarget; i++) motion.Tick(0.1f);
        Assert.False(motion.HasMoveTarget);
        float yaw = pos.Rotation.Y.ToFloat();
        Assert.True(System.Math.Abs(yaw - System.Math.PI / 2) < 0.05f,
            $"should face +X after arrival, got {yaw}");
    }

    [Fact]
    public void Ships_TurnAlmostInstantly_LargeInstantTurnAngle()
    {
        var (_, _, pos, motion) = World();
        // 船:InstantTurnAngle=10(>π 恒成立)→ 永走浅角分支:瞬对+全速(cos(π)=−1 钳 0?)。
        motion.InstantTurnAngle = Fixed.FromInt(10);
        pos.Rotation = new FixedVector3D(Fixed.Zero, Fixed.Pi, Fixed.Zero);
        motion.MoveToPoint(new FixedVector2D(Fixed.FromInt(20), Fixed.Zero));
        motion.Tick(0.1f);
        // 背对(π)但瞬对:cos(π)=−1 → 钳 0 → 本拍不走但角度已正(原版船不显停)。
        float yaw = pos.Rotation.Y.ToFloat();
        Assert.True(System.Math.Abs(yaw - System.Math.PI / 2) < 0.02f);
        // 第二拍:已对齐 → 全速走。
        float x0 = pos.Position.X.ToFloat();
        motion.Tick(0.1f);
        Assert.True(pos.Position.X.ToFloat() - x0 > 0.7f);
    }
}
