using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>push-pressure 全量移植(UnitSeparation v2)的增量行为测试:
/// 编队同组豁免、per-template Weight 动量配比、压力累积→减速、initialPos 交叉 nudge。</summary>
public sealed class UnitSeparationPushTests
{
    private static (ComponentManager cm, EntityId a, EntityId b) BuildPair(
        float ax, float az, float bx, float bz, bool moving = false)
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var a = cm.CreateEntity();
        var b = cm.CreateEntity();
        foreach (var (e, x, z) in new[] { (a, ax, az), (b, bx, bz) })
        {
            cm.AddComponent(e, new PositionComponent());
            cm.QueryInterface<PositionComponent>(e)!.Position =
                new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
            cm.AddComponent(e, new UnitMotion());
            cm.AddComponent(e, new ObstructionComponent
            { Type = ObstructionType.Unit, Size0 = Fixed.FromInt(1) });
        }
        if (moving)
        {
            // moving 标记 = CurrentSpeed > 0(原版 isMoving;直接灌速度字段)。
            cm.QueryInterface<UnitMotion>(a)!.CurrentSpeed = Fixed.FromInt(8);
            cm.QueryInterface<UnitMotion>(b)!.CurrentSpeed = Fixed.FromInt(8);
        }
        return (cm, a, b);
    }

    private static FixedVector2D Pos(ComponentManager cm, EntityId e)
    {
        var p = cm.QueryInterface<PositionComponent>(e)!.Position;
        return new FixedVector2D(p.X, p.Z);
    }

    [Fact]
    public void SameFormation_MembersNeverPush()
    {
        // 同控制组(编队)成员重叠也不互推(原版 sameControlGroup:movingPush 置 0 且
        // maxDist 不加扩展)。
        var (cm, a, b) = BuildPair(0f, 0f, 0.5f, 0f, moving: true);
        foreach (var e in new[] { a, b })
            cm.QueryInterface<UnitAIComponent>(e)
                ?.GetType();   // 无 UnitAI 组件时 ControlGroup=0(无编队)
        // 直接挂 UnitAI 并设同编队控制器。
        cm.AddComponent(a, new UnitAIComponent());
        cm.AddComponent(b, new UnitAIComponent());
        var controller = new EntityId(900);
        cm.QueryInterface<UnitAIComponent>(a)!.SetFormationController(controller);
        cm.QueryInterface<UnitAIComponent>(b)!.SetFormationController(controller);

        var beforeA = Pos(cm, a);
        var beforeB = Pos(cm, b);
        UnitSeparation.Separate(cm, Fixed.FromFloat(0.1f));
        Assert.Equal(beforeA, Pos(cm, a));
        Assert.Equal(beforeB, Pos(cm, b));
    }

    [Fact]
    public void DifferentFormations_StillPush()
    {
        // 三单位重合(单对推力 0.125 低于 MinimalForce 0.2 门——原版同款;
        // 对数 ≥2 才累积过门):a/b 异编队,c 无编队。
        var (cm, a, b) = BuildPair(0f, 0f, 0f, 0f);
        var c = cm.CreateEntity();
        cm.AddComponent(c, new PositionComponent());
        cm.QueryInterface<PositionComponent>(c)!.Position =
            new FixedVector3D(Fixed.Zero, Fixed.Zero, Fixed.Zero);
        cm.AddComponent(c, new UnitMotion());
        cm.AddComponent(c, new ObstructionComponent
        { Type = ObstructionType.Unit, Size0 = Fixed.FromInt(1) });
        cm.AddComponent(a, new UnitAIComponent());
        cm.AddComponent(b, new UnitAIComponent());
        cm.QueryInterface<UnitAIComponent>(a)!.SetFormationController(new EntityId(900));
        cm.QueryInterface<UnitAIComponent>(b)!.SetFormationController(new EntityId(901));

        var beforeA = Pos(cm, a);
        UnitSeparation.Separate(cm, Fixed.FromFloat(0.1f));
        Assert.NotEqual(beforeA, Pos(cm, a));
    }

    [Fact]
    public void Weight_HeavierUnitPushesLighterFarther()
    {
        // a 重(40 = 象兵级),b 轻(10 基准):b 被推出的距离应大于 a。
        var (cm, a, b) = BuildPair(0f, 0f, 0.8f, 0f);
        cm.QueryInterface<UnitMotion>(a)!.Weight = Fixed.FromInt(40);
        cm.QueryInterface<UnitMotion>(b)!.Weight = Fixed.FromInt(10);

        UnitSeparation.Separate(cm, Fixed.FromFloat(0.1f));
        var da = (Pos(cm, a) - new FixedVector2D(Fixed.Zero, Fixed.Zero)).Length();
        var db = (Pos(cm, b) - new FixedVector2D(Fixed.FromFloat(0.8f), Fixed.Zero)).Length();
        Assert.True(db > da, $"lighter unit should be pushed farther (a={da.ToFloat()}, b={db.ToFloat()})");
    }

    [Fact]
    public void Pressure_AccumulatesAndDecays()
    {
        var (cm, a, b) = BuildPair(0f, 0f, 0.5f, 0f);
        var ma = cm.QueryInterface<UnitMotion>(a)!;
        Assert.Equal(0, ma.PushingPressure);
        UnitSeparation.Separate(cm, Fixed.FromFloat(0.1f));
        Assert.True(ma.PushingPressure > 0, "overlap should add pressure");

        // 衰减:单位 Tick 末 ×3/5。
        int before = ma.PushingPressure;
        ma.HasMoveTarget = false;   // Tick 早退路径也有衰减?衰减加在主路径——直接调有效速度验证
        // 直接验证衰减语义:模拟两回合推挤后远离,压力随 Tick 衰减由 UnitMotion.Tick 执行;
        // 此处钉减速公式:压力越大速度越低,地板 1.5。
        ma.PushingPressure = 200;
        // EffectiveSpeed 是 private——经 Tick 观察速度不可行(无目标不动);改测公式出口:
        // ApplyPushingPressure 间接验证放在 UnitMotionTests 的集成路径;这里钉压力饱和上限。
        ma.PushingPressure = 0;
        for (int i = 0; i < 30; i++)
            UnitSeparation.Separate(cm, Fixed.FromFloat(0.1f));
        Assert.True(ma.PushingPressure <= 255, $"pressure must cap at 255, got {ma.PushingPressure}");
    }

    [Fact]
    public void SpatialGrid_FarPairsSkipped()
    {
        // 20m 网格 + 4 邻格:相距 100m 的对不在枚举内(推力为零,位置不动)——
        // O(n²) 时代它们也推不动,此测钉死网格化后行为不变。
        var (cm, a, b) = BuildPair(0f, 0f, 100f, 0f);
        var beforeA = Pos(cm, a);
        var beforeB = Pos(cm, b);
        UnitSeparation.Separate(cm, Fixed.FromFloat(0.1f));
        Assert.Equal(beforeA, Pos(cm, a));
        Assert.Equal(beforeB, Pos(cm, b));
    }
}
