using System.IO;
using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Tests;

/// <summary>UnitMotion waypoints 序列化(存档 v16):读档后单位沿原路标续走,
/// 与从不存档的演化逐位一致(哈希连续)。</summary>
public sealed class UnitMotionWaypointSerializationTests
{
    private static ComponentManager BuildMovingUnit(out EntityId unitOut)
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        var pos = cm.QueryInterface<PositionComponent>(e)!;
        pos.Position = new FixedVector3D(Fixed.Zero, Fixed.Zero, Fixed.Zero);
        pos.TurnRate = Fixed.FromInt(14);
        var motion = new UnitMotion();
        cm.AddComponent(e, motion);
        motion.Speed = Fixed.FromInt(8);
        // 直线目标(无寻路 → beeline 单路标)走几步后存——路标/进度在路上。
        motion.MoveToPoint(new FixedVector2D(Fixed.FromInt(20), Fixed.Zero));
        for (int i = 0; i < 5; i++) motion.Tick(0.1f);
        unitOut = e;
        return cm;
    }

    private static ComponentManager RoundTrip(ComponentManager cm)
    {
        var ms = new MemoryStream();
        cm.SerializeSaveGame(new BinarySerializer(new BinaryWriter(ms)));
        ms.Position = 0;
        var cm2 = new ComponentManager(42);
        cm2.Registry.AutoRegister(typeof(PositionComponent).Assembly);
        SimSystem.Init(cm2);
        cm2.DeserializeSaveGame(new BinaryDeserializer(new BinaryReader(ms)), _ => { });
        return cm2;
    }

    [Fact]
    public void Waypoints_SurviveRoundTrip()
    {
        var cm = BuildMovingUnit(out var unit);
        var motion = cm.QueryInterface<UnitMotion>(unit)!;
        Assert.True(motion.HasMoveTarget);

        var cm2 = RoundTrip(cm);
        var motion2 = cm2.QueryInterface<UnitMotion>(unit)!;
        Assert.True(motion2.HasMoveTarget, "move target should survive load");
        Assert.Equal(motion.TargetPos, motion2.TargetPos);

        // 续走:再拍 5 拍,两端位置逐位一致(演化不跳变)。
        // 注意 SimSystem 是静态别名:逐世界 Tick 前须 Init 回该世界(否则 A 的
        // Tick 会经静态 Sim 读到 B 的组件——双拍同一实体)。
        SimSystem.Init(cm);
        for (int i = 0; i < 5; i++) motion.Tick(0.1f);
        SimSystem.Init(cm2);
        for (int i = 0; i < 5; i++) motion2.Tick(0.1f);
        var p1 = cm.QueryInterface<PositionComponent>(unit)!.Position;
        var p2 = cm2.QueryInterface<PositionComponent>(unit)!.Position;
        Assert.Equal(p1.X, p2.X);
        Assert.Equal(p1.Z, p2.Z);
    }

    [Fact]
    public void LoadThenRun_MatchesUnsavedRun_HashContinuous()
    {
        // 哈希连续:存→读→跑 N 拍的状态哈希 == 不存连跑的状态哈希。
        var cmA = BuildMovingUnit(out _);
        var cmB = RoundTrip(cmA);
        SimSystem.Init(cmA);
        for (int i = 0; i < 20; i++)
            foreach (var e in cmA.AllEntities)
                cmA.QueryInterface<UnitMotion>(e)?.Tick(0.1f);
        SimSystem.Init(cmB);
        for (int i = 0; i < 20; i++)
            foreach (var e in cmB.AllEntities)
                cmB.QueryInterface<UnitMotion>(e)?.Tick(0.1f);
        Assert.Equal(cmA.ComputeStateHash(), cmB.ComputeStateHash());
    }

    [Fact]
    public void Load_ArrivalReachesSameDestination()
    {
        var cm = BuildMovingUnit(out var unit);
        var cm2 = RoundTrip(cm);
        SimSystem.Init(cm2);
        var motion2 = cm2.QueryInterface<UnitMotion>(unit)!;
        for (int i = 0; i < 600 && motion2.HasMoveTarget; i++) motion2.Tick(0.1f);
        var p = cm2.QueryInterface<PositionComponent>(unit)!.Position;
        Assert.True(System.Math.Abs(p.X.ToFloat() - 20f) < 1f,
            $"loaded unit should still arrive at x=20, got {p.X.ToFloat()}");
    }
}
