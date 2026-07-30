using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

// Turretable + TurretHolder — ports of Turretable.js / TurretHolder.js。
// 炮塔点:远程兵上城墙/哨塔,占命名炮塔点后位置随持有者可动(UpdatePosition 跟拍,
// 对齐原版 Position.SetTurretParent),留在世界内可作战; obstruction 停用避免干扰寻路;
// 持有者被毁按 EjectOrKill 逐出/同灭。与 Garrison 共用 GARRISON 子树(原版同,
// 指令以 Type 区分,对应原版 order.data.garrison 标志)。
public sealed class TurretTests
{
    private static EntityId MakeUnit(ComponentManager cm, int player = 1, float x = 0f, float z = 0f,
        string classes = "Archer", bool withAI = false)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        var motion = new UnitMotion();
        cm.AddComponent(e, motion);
        motion.Speed = Fixed.FromFloat(3f);
        var id = new IdentityComponent();
        cm.AddComponent(e, id);
        id.Classes.AddRange(classes.Split(' '));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        cm.AddComponent(e, new TurretableComponent());
        var obs = new ObstructionComponent { Type = ObstructionType.Unit };
        cm.AddComponent(e, obs);
        var health = new HealthComponent();
        cm.AddComponent(e, health);
        health.Current = 100; health.Max = 100;
        if (withAI) cm.AddComponent(e, new UnitAIComponent());
        return e;
    }

    private static EntityId MakeWall(ComponentManager cm, int player = 1, float x = 0f, float z = 0f,
        float loadingRange = 2f, params (string name, float ox, float oz, string classes, bool ejectable)[] points)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        var id = new IdentityComponent();
        cm.AddComponent(e, id);
        id.Classes.Add("Structure");
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        var th = new TurretHolderComponent { LoadingRange = loadingRange };
        cm.AddComponent(e, th);
        foreach (var (name, ox, oz, classes, ejectable) in points)
            th.TurretPoints.Add(new TurretHolderComponent.TurretPoint
            {
                Name = name,
                OffsetX = ox,
                OffsetZ = oz,
                AllowedClasses = classes,
                Ejectable = ejectable,
            });
        return e;
    }

    private static void AddPlayer(ComponentManager cm, int playerId)
    {
        var pe = cm.CreateEntity();
        var pc = new PlayerComponent();
        cm.AddComponent(pe, pc);
        pc.Wood = 0; pc.Food = 0; pc.Stone = 0; pc.Metal = 0;
        cm.AddComponent(pe, new DiplomacyComponent());
        cm.Players.AddPlayer(playerId, pe);
    }

    [Fact]
    public void CanOccupy_ClassDiplomacyVacancy_Matrix()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        AddPlayer(cm, 2);
        var wall = MakeWall(cm, 1, 0f, 0f, 2f, ("p1", 1f, 0f, "Archer", true));
        var th = cm.QueryInterface<TurretHolderComponent>(wall)!;
        var archer = MakeUnit(cm, classes: "Archer");
        var spearman = MakeUnit(cm, classes: "Spearman", x: 5f);

        Assert.True(th.CanOccupy(cm, archer));                    // 空点+类别+同主 → 可
        Assert.False(th.CanOccupy(cm, spearman));                 // 类别不符 → 拒

        cm.QueryInterface<TurretableComponent>(archer)!.OccupyTurret(cm, wall);
        Assert.False(th.CanOccupy(cm, MakeUnit(cm, classes: "Archer", x: 9f))); // 已占 → 拒

        // 敌对墙(无外交 = 非互盟)→ 拒;同队 seed 后 → 可。
        var wall2 = MakeWall(cm, 2, 50f, 0f, 2f, ("p1", 1f, 0f, "", true));
        var th2 = cm.QueryInterface<TurretHolderComponent>(wall2)!;
        var unit = MakeUnit(cm, classes: "Archer", x: 20f);
        Assert.False(th2.CanOccupy(cm, unit));
        cm.Players.SeedDiplomacyFromTeams(new System.Collections.Generic.Dictionary<int, int> { [1] = 0, [2] = 0 });
        Assert.True(th2.CanOccupy(cm, unit));
    }

    [Fact]
    public void OccupyTurret_SetsState_AndSnapsToPoint()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var wall = MakeWall(cm, 1, 6f, 0f, 2f, ("p1", 1f, 0f, "", true), ("p2", -1f, 0f, "", true));
        var th = cm.QueryInterface<TurretHolderComponent>(wall)!;
        var unit = MakeUnit(cm, withAI: true);
        var tb = cm.QueryInterface<TurretableComponent>(unit)!;

        Assert.True(tb.OccupyTurret(cm, wall));

        Assert.Equal(wall, tb.Holder);
        Assert.Equal("p1", tb.TurretPointName);                   // 首个空点
        Assert.Equal(unit, th.TurretPoints[0].Entity);
        Assert.True(cm.QueryInterface<UnitAIComponent>(unit)!.IsTurret);   // SetTurretStance
        Assert.False(cm.QueryInterface<ObstructionComponent>(unit)!.Active); // 障碍停用
        // 位置跟拍:持有者(6,0) + 偏移(1,0),旋转 0 → (7,0)。
        var pos = cm.QueryInterface<PositionComponent>(unit)!;
        Assert.True(pos.InWorld);                                 // 炮塔兵留世界内
        Assert.Equal(7f, pos.Position.X.ToFloat(), 2);
        Assert.Equal(0f, pos.Position.Z.ToFloat(), 2);

        Assert.False(tb.OccupyTurret(cm, wall));                  // 已在点 → 拒
    }

    [Fact]
    public void OccupyTurret_PicksFirstFreePoint()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var wall = MakeWall(cm, 1, 0f, 0f, 2f, ("p1", 1f, 0f, "", true), ("p2", -1f, 0f, "", true));
        var th = cm.QueryInterface<TurretHolderComponent>(wall)!;
        th.TurretPoints[0].Entity = MakeUnit(cm, classes: "Archer", x: 30f);   // p1 已占

        var unit = MakeUnit(cm);
        Assert.True(cm.QueryInterface<TurretableComponent>(unit)!.OccupyTurret(cm, wall));
        Assert.Equal("p2", cm.QueryInterface<TurretableComponent>(unit)!.TurretPointName);
    }

    [Fact]
    public void LeaveTurret_RestoresState_AndWalksToRally()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var wall = MakeWall(cm, 1, 0f, 0f, 2f, ("p1", 1f, 0f, "", true));
        var rally = new RallyPointComponent();
        cm.AddComponent(wall, rally);
        rally.Set(new FixedVector2D(Fixed.FromInt(15), Fixed.FromInt(15)));
        var unit = MakeUnit(cm, withAI: true);
        var tb = cm.QueryInterface<TurretableComponent>(unit)!;
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;

        tb.OccupyTurret(cm, wall);
        Assert.True(tb.LeaveTurret(cm));

        Assert.Null(tb.Holder);
        Assert.Equal("", tb.TurretPointName);
        Assert.Null(cm.QueryInterface<TurretHolderComponent>(wall)!.TurretPoints[0].Entity);
        Assert.True(cm.QueryInterface<ObstructionComponent>(unit)!.Active);   // 障碍恢复
        Assert.False(ai.IsTurret);                                            // ResetTurretStance
        var pos = cm.QueryInterface<PositionComponent>(unit)!;
        Assert.NotEqual(1f, pos.Position.X.ToFloat(), 1);                     // 落出点位
        Assert.Equal("Walk", ai.CurrentOrder?.Type);                          // 集结点指令
    }

    [Fact]
    public void LeaveTurret_NonEjectable_RequiresForced()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var wall = MakeWall(cm, 1, 0f, 0f, 2f, ("p1", 1f, 0f, "", false));     // 点不可逐
        var unit = MakeUnit(cm);
        var tb = cm.QueryInterface<TurretableComponent>(unit)!;

        tb.OccupyTurret(cm, wall);
        Assert.False(tb.LeaveTurret(cm));                                     // 非强制 → 拒
        Assert.True(tb.IsTurreted);
        Assert.True(tb.LeaveTurret(cm, forced: true));                        // 强制 → 可
        Assert.False(tb.IsTurreted);
    }

    [Fact]
    public void UpdatePosition_FollowsHolder_WithRotation()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var wall = MakeWall(cm, 1, 10f, 10f, 2f, ("p1", 1f, 0f, "", true));
        var unit = MakeUnit(cm, x: 8f, z: 10f);
        var tb = cm.QueryInterface<TurretableComponent>(unit)!;
        tb.OccupyTurret(cm, wall);

        // 平移持有者:单位随行。
        var holderPos = cm.QueryInterface<PositionComponent>(wall)!;
        holderPos.Position = new FixedVector3D(Fixed.FromInt(20), Fixed.Zero, Fixed.FromInt(20));
        tb.UpdatePosition(cm);
        var pos = cm.QueryInterface<PositionComponent>(unit)!;
        Assert.Equal(21f, pos.Position.X.ToFloat(), 2);
        Assert.Equal(20f, pos.Position.Z.ToFloat(), 2);

        // 旋转 π/2:偏移 (1,0) → (0,−1)。
        holderPos.Rotation = new FixedVector3D(Fixed.Zero, Fixed.FromFloat((float)(System.Math.PI / 2)), Fixed.Zero);
        tb.UpdatePosition(cm);
        Assert.Equal(20f, pos.Position.X.ToFloat(), 1);
        Assert.Equal(19f, pos.Position.Z.ToFloat(), 1);
    }

    [Fact]
    public void EjectOrKill_LeavesEjectables_KillsNonTurretable()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var wall = MakeWall(cm, 1, 0f, 0f, 2f, ("p1", 1f, 0f, "", true), ("p2", -1f, 0f, "", true));
        var th = cm.QueryInterface<TurretHolderComponent>(wall)!;
        var archer = MakeUnit(cm, classes: "Archer");
        cm.QueryInterface<TurretableComponent>(archer)!.OccupyTurret(cm, wall);
        // 直塞进点、无 Turretable 件的占位者(原版 EjectOrKill 的击杀路径)。
        var squatter = cm.CreateEntity();
        cm.AddComponent(squatter, new PositionComponent());
        var sqHealth = new HealthComponent();
        cm.AddComponent(squatter, sqHealth);
        sqHealth.Current = 100; sqHealth.Max = 100;
        th.TurretPoints[1].Entity = squatter;

        th.EjectOrKill(cm, th.GetEntities());

        // 可逐者离点存活;占位者击杀(Health=0,等 RemoveDeadEntities)。
        Assert.Null(cm.QueryInterface<TurretableComponent>(archer)!.Holder);
        Assert.True(cm.QueryInterface<HealthComponent>(archer)!.Current > 0);
        Assert.Equal(0, cm.QueryInterface<HealthComponent>(squatter)!.Current);
        Assert.Null(th.TurretPoints[0].Entity);
        Assert.Null(th.TurretPoints[1].Entity);
    }

    [Fact]
    public void UnitAI_OccupyTurretOrder_ApproachesThenOccupies()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var wall = MakeWall(cm, 1, 6f, 0f, 2f, ("p1", 1f, 0f, "", true));
        var unit = MakeUnit(cm, withAI: true);
        var tb = cm.QueryInterface<TurretableComponent>(unit)!;
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;

        ai.OccupyTurret(wall);
        ai.Tick(0.1f, cm);
        Assert.EndsWith("GARRISON.APPROACHING", ai.FsmStateName);   // 与驻军共用子树(原版同)

        for (int i = 0; i < 300 && !tb.IsTurreted; i++)
        {
            cm.QueryInterface<UnitMotion>(unit)?.Tick(0.1f);
            ai.Tick(0.1f, cm);
        }

        Assert.True(tb.IsTurreted);
        Assert.True(ai.IsTurret);
        Assert.EndsWith("IDLE", ai.FsmStateName);
        // Tick 不冻结(炮塔兵可作战):再 Tick 不抛。
        ai.Tick(0.1f, cm);
    }

    [Fact]
    public void UnitAI_OccupyTurretRejected_WhenFull()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var wall = MakeWall(cm, 1, 1f, 0f, 2f, ("p1", 1f, 0f, "", true));
        var th = cm.QueryInterface<TurretHolderComponent>(wall)!;
        th.TurretPoints[0].Entity = MakeUnit(cm, classes: "Archer", x: 30f);   // 唯一点已占

        var unit = MakeUnit(cm, withAI: true);
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;
        ai.OccupyTurret(wall);
        ai.Tick(0.1f, cm);
        ai.Tick(0.1f, cm);

        Assert.EndsWith("IDLE", ai.FsmStateName);
        Assert.False(cm.QueryInterface<TurretableComponent>(unit)!.IsTurreted);
    }

    [Fact]
    public void UnitAI_GarrisonRejected_WhileTurreted()
    {
        var cm = new ComponentManager(rngSeed: 1);
        AddPlayer(cm, 1);
        var wall = MakeWall(cm, 1, 0f, 0f, 2f, ("p1", 1f, 0f, "", true));
        var unit = MakeUnit(cm, withAI: true);
        var tb = cm.QueryInterface<TurretableComponent>(unit)!;
        var ai = cm.QueryInterface<UnitAIComponent>(unit)!;
        tb.OccupyTurret(cm, wall);

        // 原版 UnitAI.Garrison:IsTurret → 拒收驻军指令。
        var hold = MakeWall(cm, 1, 30f, 0f, 2f, ("q", 0f, 0f, "", true));
        cm.AddComponent(hold, new GarrisonHolderComponent { Max = 4 });
        cm.QueryInterface<GarrisonHolderComponent>(hold)!.AllowedClasses.Add("Archer");
        ai.Garrison(hold);
        ai.Tick(0.1f, cm);
        ai.Tick(0.1f, cm);

        Assert.EndsWith("IDLE", ai.FsmStateName);
        Assert.Null(cm.QueryInterface<GarrisonableComponent>(unit)?.Holder);
        Assert.True(tb.IsTurreted);                               // 炮塔状态未受影响
    }

    [Fact]
    public void RoundTrip_PreservesHolderPointsAndOccupants()
    {
        var th = new TurretHolderComponent { LoadingRange = 3.5f, Pickup = true };
        th.TurretPoints.Add(new TurretHolderComponent.TurretPoint
        {
            Name = "archer-left",
            OffsetX = 1.5f,
            OffsetY = 9f,
            OffsetZ = 1f,
            AllowedClasses = "Archer Javelineer",
            Angle = 1.5707f,
            Entity = new EntityId(11),
            Ejectable = true,
        });
        th.TurretPoints.Add(new TurretHolderComponent.TurretPoint { Name = "archer-right", Entity = null, Ejectable = false });

        var ms = new System.IO.MemoryStream();
        th.Serialize(new Serialization.BinarySerializer(new System.IO.BinaryWriter(ms)));
        ms.Position = 0;
        var back = new TurretHolderComponent();
        back.Deserialize(new Serialization.BinaryDeserializer(new System.IO.BinaryReader(ms)));

        Assert.Equal(3.5f, back.LoadingRange, 3);
        Assert.True(back.Pickup);
        Assert.Equal(2, back.TurretPoints.Count);
        var p0 = back.TurretPoints[0];
        Assert.Equal("archer-left", p0.Name);
        Assert.Equal(1.5f, p0.OffsetX, 3);
        Assert.Equal(9f, p0.OffsetY, 3);
        Assert.Equal(1f, p0.OffsetZ, 3);
        Assert.Equal("Archer Javelineer", p0.AllowedClasses);
        Assert.Equal(1.5707f, p0.Angle!.Value, 3);
        Assert.Equal(new EntityId(11), p0.Entity);
        Assert.True(p0.Ejectable);
        Assert.Equal("archer-right", back.TurretPoints[1].Name);
        Assert.Null(back.TurretPoints[1].Entity);
        Assert.False(back.TurretPoints[1].Ejectable);

        var tb = new TurretableComponent { Holder = new EntityId(9), Ejectable = false, TurretPointName = "tower-top" };
        var ms2 = new System.IO.MemoryStream();
        tb.Serialize(new Serialization.BinarySerializer(new System.IO.BinaryWriter(ms2)));
        ms2.Position = 0;
        var tbBack = new TurretableComponent();
        tbBack.Deserialize(new Serialization.BinaryDeserializer(new System.IO.BinaryReader(ms2)));
        Assert.Equal(new EntityId(9), tbBack.Holder);
        Assert.False(tbBack.Ejectable);
        Assert.Equal("tower-top", tbBack.TurretPointName);
    }
}
