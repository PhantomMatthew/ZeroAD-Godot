using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>门自动开关(Gate.js 全量)测试:盟友接近开门/离开关门/锁定不开/
/// 关门阻挡重试/编队控制组切换/炮塔站姿。</summary>
public sealed class GateAndStanceTests
{
    private static (ComponentManager cm, EntityId gate) BuildGate()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var mgr = new ObstructionManager();
        SimSystem.SetObstructionManager(mgr);
        SimSystem.SetRangeManager(new RangeManager(cm, Fixed.FromInt(512), Fixed.FromInt(512)));

        var gate = cm.CreateEntity();
        cm.AddComponent(gate, new PositionComponent());
        cm.QueryInterface<PositionComponent>(gate)!.Position =
            new FixedVector3D(Fixed.FromInt(50), Fixed.Zero, Fixed.FromInt(50));
        cm.AddComponent(gate, new OwnershipComponent { PlayerId = 1 });
        cm.AddComponent(gate, new GateComponent());
        // CreateEntity 不发 EntityCreated——RangeManager 靠它注册;测试手工补发
        // (位置已就位,OnEntityCreated 一次读对)。
        var obs = new ObstructionComponent
        {
            Type = ObstructionType.Static,
            Size0 = Fixed.FromInt(10),
            Size1 = Fixed.FromInt(4),
        };
        cm.AddComponent(gate, obs);
        cm.NotifyEntityCreated(gate);
        obs.EnsureRegistered();
        // 初始未锁关门:移动挡/寻路放。
        obs.SetDisableBlockMovementPathfinding(false, true);
        return (cm, gate);
    }

    private static EntityId AddSoldier(ComponentManager cm, float x, float z, int owner = 1)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new UnitAIComponent());
        cm.NotifyEntityCreated(e);   // RangeManager 注册(位置已就位)
        return e;
    }

    // GetObstruction 不带 flags(默认 None)——直接读组件的 EffectiveFlags(注册即用此值)。
    private static ObstructionFlags Flags(ComponentManager cm, EntityId gate)
        => cm.QueryInterface<ObstructionComponent>(gate)!.EffectiveFlags();

    [Fact]
    public void AllyInRange_GateOpens()
    {
        var (cm, gate) = BuildGate();
        var g = cm.QueryInterface<GateComponent>(gate)!;
        Assert.False(g.Opened);
        AddSoldier(cm, 55f, 50f);   // 5m 处,PassRange 20 内
        g.OperateGate(cm);
        Assert.True(g.Opened);
        var flags = Flags(cm, gate);
        Assert.False(flags.HasFlag(ObstructionFlags.BlockMovement));
        Assert.False(flags.HasFlag(ObstructionFlags.BlockPathfinding));
    }

    [Fact]
    public void AllyLeaves_GateCloses_MovementBlocked_PathfindingOpen()
    {
        var (cm, gate) = BuildGate();
        var g = cm.QueryInterface<GateComponent>(gate)!;
        var soldier = AddSoldier(cm, 55f, 50f);
        g.OperateGate(cm);
        Assert.True(g.Opened);
        // 走开(移出 20m)。
        var oldPos = cm.QueryInterface<PositionComponent>(soldier)!.Position;
        cm.QueryInterface<PositionComponent>(soldier)!.Position =
            new FixedVector3D(Fixed.FromInt(200), Fixed.Zero, Fixed.FromInt(200));
        SimSystem.NotifyPositionChanged(soldier,
            new FixedVector2D(oldPos.X, oldPos.Z),
            new FixedVector2D(Fixed.FromInt(200), Fixed.FromInt(200)));
        g.OperateGate(cm);
        Assert.False(g.Opened);
        var flags = Flags(cm, gate);
        Assert.True(flags.HasFlag(ObstructionFlags.BlockMovement));
        Assert.False(flags.HasFlag(ObstructionFlags.BlockPathfinding));   // 未锁关门:寻路放行
    }

    [Fact]
    public void LockedGate_NeverAutoOpens()
    {
        var (cm, gate) = BuildGate();
        var g = cm.QueryInterface<GateComponent>(gate)!;
        g.SetLocked(cm, true);
        AddSoldier(cm, 55f, 50f);
        g.OperateGate(cm);
        Assert.False(g.Opened);
        var flags = Flags(cm, gate);
        Assert.True(flags.HasFlag(ObstructionFlags.BlockMovement));
        Assert.True(flags.HasFlag(ObstructionFlags.BlockPathfinding));
    }

    [Fact]
    public void EnemyInRange_GateStaysClosed()
    {
        var (cm, gate) = BuildGate();
        var g = cm.QueryInterface<GateComponent>(gate)!;
        AddSoldier(cm, 55f, 50f, owner: 2);   // 敌军
        g.OperateGate(cm);
        Assert.False(g.Opened);
    }

    [Fact]
    public void PackedSiege_DoesNotHoldGateOpen()
    {
        var (cm, gate) = BuildGate();
        var g = cm.QueryInterface<GateComponent>(gate)!;
        var siege = AddSoldier(cm, 55f, 50f);
        cm.AddComponent(siege, new PackComponent { Packed = true });
        g.OperateGate(cm);
        Assert.False(g.Opened);
    }

    [Fact]
    public void BlockedDoorway_StaysOpen()
    {
        var (cm, gate) = BuildGate();
        var g = cm.QueryInterface<GateComponent>(gate)!;
        var soldier = AddSoldier(cm, 55f, 50f);
        g.OperateGate(cm);
        Assert.True(g.Opened);
        // 门洞里塞一个 BlockConstruction 静态件(如在建地基),士兵离场 → 关不上。
        var blocker = cm.CreateEntity();
        cm.AddComponent(blocker, new PositionComponent());
        cm.QueryInterface<PositionComponent>(blocker)!.Position =
            new FixedVector3D(Fixed.FromInt(50), Fixed.Zero, Fixed.FromInt(50));
        cm.AddComponent(blocker, new ObstructionComponent
        {
            Type = ObstructionType.Static,
            Size0 = Fixed.FromInt(2),
            Size1 = Fixed.FromInt(2),
            Flags = ObstructionFlags.DefaultBlock,
        });
        cm.NotifyEntityCreated(blocker);
        cm.QueryInterface<ObstructionComponent>(blocker)!.EnsureRegistered();
        var oldPos = cm.QueryInterface<PositionComponent>(soldier)!.Position;
        cm.QueryInterface<PositionComponent>(soldier)!.Position =
            new FixedVector3D(Fixed.FromInt(200), Fixed.Zero, Fixed.FromInt(200));
        SimSystem.NotifyPositionChanged(soldier,
            new FixedVector2D(oldPos.X, oldPos.Z),
            new FixedVector2D(Fixed.FromInt(200), Fixed.FromInt(200)));
        g.OperateGate(cm);
        Assert.True(g.Opened);   // 门洞被占 → 保持开
    }

    [Fact]
    public void FormationMembership_SwitchesObstructionControlGroup()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        SimSystem.SetObstructionManager(new ObstructionManager());
        var unit = cm.CreateEntity();
        cm.AddComponent(unit, new PositionComponent());
        cm.QueryInterface<PositionComponent>(unit)!.Position =
            new FixedVector3D(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(5));
        cm.AddComponent(unit, new UnitAIComponent());
        cm.AddComponent(unit, new ObstructionComponent
        { Type = ObstructionType.Unit, Size0 = Fixed.FromInt(1) });
        var obs = cm.QueryInterface<ObstructionComponent>(unit)!;
        obs.EnsureRegistered();
        Assert.Equal(unit.Value, obs.ControlGroup);   // 默认自身

        var controller = new EntityId(900);
        cm.QueryInterface<UnitAIComponent>(unit)!.SetFormationController(controller);
        Assert.Equal(900u, obs.ControlGroup);

        cm.QueryInterface<UnitAIComponent>(unit)!.UnsetFormationController();
        Assert.Equal(unit.Value, obs.ControlGroup);
    }

    [Fact]
    public void TurretStance_ForcesStandground_AndRestores()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var unit = cm.CreateEntity();
        cm.AddComponent(unit, new PositionComponent());
        var ai = new UnitAIComponent();
        cm.AddComponent(unit, ai);

        ai.SetStance("aggressive", cm);
        ai.SetTurretStance(cm);
        Assert.True(ai.IsTurret);
        Assert.Equal("standground", ai.Stance);

        ai.ResetTurretStance(cm);
        Assert.False(ai.IsTurret);
        Assert.Equal("aggressive", ai.Stance);
    }

    [Fact]
    public void TurretStance_AlreadyStandground_KeepsNoBackup()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var unit = cm.CreateEntity();
        cm.AddComponent(unit, new PositionComponent());
        var ai = new UnitAIComponent();
        cm.AddComponent(unit, ai);
        ai.SetStance("standground", cm);
        ai.SetTurretStance(cm);
        Assert.Equal("standground", ai.Stance);
        ai.ResetTurretStance(cm);
        Assert.Equal("standground", ai.Stance);   // 无还原(本就 standground)
    }
}
