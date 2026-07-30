using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

// Formation — port of Formation.js (编队控制器组件) + UnitAI 的
// FORMATIONCONTROLLER/FORMATIONMEMBER 子树。控制器是虚拟实体(无 Health/Cost/Obstruction),
// 按模板队形参数(square/triangle + 分隔/排序)计算成员偏移,ArrangeFormation 给成员发
// FormationWalk 指令;成员逐回合跟踪"控制器位置+旋转后的偏移"(原版 MoveToFormationOffset)。
// 对齐要点:类组合笛卡尔生成("" 占位 + Unsorted 兜底)、行布局左右交替、零均值居中、
// 逆优先级 splice 分配 + TakeClosestOffset 最近空位、速度=最慢成员×SpeedMultiplier、
// 成员数低于 RequiredMemberCount 解散(毁控制器)。散射(scatter)/编队光环/编队作战/
// 双编队合并不在本期范围(见 Formation.cs 头注)。
public sealed class FormationTests
{
    private static EntityId MakeMember(ComponentManager cm, int player = 1, float x = 0f, float z = 0f,
        string classes = "Melee Infantry", float speed = 3f, bool withAI = true)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        var motion = new UnitMotion();
        cm.AddComponent(e, motion);
        motion.Speed = Fixed.FromFloat(speed);
        var id = new IdentityComponent();
        cm.AddComponent(e, id);
        id.Classes.AddRange(classes.Split(' '));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = player });
        var obs = new ObstructionComponent { Type = ObstructionType.Unit };
        cm.AddComponent(e, obs);
        obs.Size0 = Fixed.FromFloat(1f);   // 半径 1 → footprint 2×2
        if (withAI) cm.AddComponent(e, new UnitAIComponent());
        return e;
    }

    private static EntityId MakeController(ComponentManager cm, float x = 0f, float z = 0f,
        string shape = "square", int required = 2, params string[] sortingClassLevels)
    {
        SimSystem.Init(cm);
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        var motion = new UnitMotion();
        cm.AddComponent(e, motion);
        var ai = new UnitAIComponent();
        cm.AddComponent(e, ai);
        ai.InitAsFormationController();
        var f = new FormationComponent { Shape = shape, RequiredMemberCount = required };
        cm.AddComponent(e, f);
        f.SortingClasses.AddRange(sortingClassLevels);
        return e;
    }

    private static void TickAll(ComponentManager cm, float dt, params EntityId[] entities)
    {
        foreach (var e in entities)
        {
            cm.QueryInterface<UnitMotion>(e)?.Tick(dt);
            cm.QueryInterface<UnitAIComponent>(e)?.Tick(dt, cm);
        }
    }

    [Fact]
    public void GenerateAllMatchingClassCombinations_CartesianWithPlaceholder()
    {
        // 原版 GenerateAllMatchingClassCombinations:每层末尾加 "" 占位,笛卡尔积按
        // reduce 顺序(首层变化最慢),"+" 连接,末尾兜底 "Unsorted"。
        var combos = FormationComponent.GenerateAllMatchingClassCombinations(
            new[] { "Melee Ranged", "Elephant Infantry" });

        Assert.Equal(new[]
        {
            "Melee+Elephant", "Melee+Infantry", "Melee",
            "Ranged+Elephant", "Ranged+Infantry", "Ranged",
            "Elephant", "Infantry", "",
            "Unsorted",
        }, combos);
    }

    [Fact]
    public void GetMemberClassCombinations_PicksFirstMatch_OrUnsorted()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var ctrl = MakeController(cm, sortingClassLevels: new[] { "Melee Ranged", "Infantry Cavalry" });
        var f = cm.QueryInterface<FormationComponent>(ctrl)!;

        var meleeInf = MakeMember(cm, classes: "Melee Infantry");
        var meleeOnly = MakeMember(cm, classes: "Melee Champion", x: 5f);
        var exotic = MakeMember(cm, classes: "Hero Elephant", x: 9f);

        Assert.Equal("Melee+Infantry", f.GetMemberClassCombinations(cm, meleeInf));
        Assert.Equal("Melee", f.GetMemberClassCombinations(cm, meleeOnly));
        Assert.Equal("Unsorted", f.GetMemberClassCombinations(cm, exotic));
    }

    [Fact]
    public void ComputeFormationOffsets_SquareGrid_ZeroCentered()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var ctrl = MakeController(cm);
        var f = cm.QueryInterface<FormationComponent>(ctrl)!;

        // 4 成员(半径 1 → 分隔 2×2),放在理想格点上让最近位分配逐一对应。
        var members = new[]
        {
            MakeMember(cm, x: -1f, z: 1f), MakeMember(cm, x: 1f, z: 1f),
            MakeMember(cm, x: -1f, z: -1f), MakeMember(cm, x: 1f, z: -1f),
        };
        var active = new List<EntityId>(members);
        var positions = new List<FixedVector2D>
        {
            new(Fixed.FromFloat(-1f), Fixed.FromFloat(1f)),
            new(Fixed.FromFloat(1f), Fixed.FromFloat(1f)),
            new(Fixed.FromFloat(-1f), Fixed.FromFloat(-1f)),
            new(Fixed.FromFloat(1f), Fixed.FromFloat(-1f)),
        };

        var offsets = f.ComputeFormationOffsets(cm, active, positions);

        Assert.Equal(4, offsets.Count);
        Assert.Equal(2, f.MaxRowsUsed);
        Assert.Equal(new[] { 2, 2 }, f.MaxColumnsUsed);
        // 零均值居中:四个格点 (±1, ±1);前排(z>0)为第 1 行。
        foreach (var o in offsets)
        {
            Assert.Equal(1f, System.MathF.Abs(o.X), 3);
            Assert.Equal(1f, System.MathF.Abs(o.Z), 3);
            Assert.Equal(o.Z > 0 ? 1 : 2, o.Row);
        }
        // 每个成员都拿到了偏移,且不重复。
        var assigned = new HashSet<EntityId>();
        foreach (var o in offsets) Assert.True(assigned.Add(o.Ent));
    }

    [Fact]
    public void ComputeFormationOffsets_TriangleRows()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var ctrl = MakeController(cm, shape: "triangle");
        var f = cm.QueryInterface<FormationComponent>(ctrl)!;

        // 5 成员:三角非 shift 行宽 1/3/5 → 第三行只剩 1 人截断。
        var active = new List<EntityId>();
        var positions = new List<FixedVector2D>();
        for (int i = 0; i < 5; i++)
        {
            active.Add(MakeMember(cm, x: i * 2f, z: 0f));
            positions.Add(new FixedVector2D(Fixed.FromFloat(i * 2f), Fixed.Zero));
        }

        var offsets = f.ComputeFormationOffsets(cm, active, positions);

        Assert.Equal(5, offsets.Count);
        Assert.Equal(3, f.MaxRowsUsed);
        Assert.Equal(new[] { 1, 3, 1 }, f.MaxColumnsUsed);
        // 行号分布:第 1 行 1 人、第 2 行 3 人、第 3 行 1 人。
        int r1 = 0, r2 = 0, r3 = 0;
        foreach (var o in offsets)
        {
            if (o.Row == 1) r1++;
            else if (o.Row == 2) r2++;
            else if (o.Row == 3) r3++;
        }
        Assert.Equal(1, r1);
        Assert.Equal(3, r2);
        Assert.Equal(1, r3);
    }

    [Fact]
    public void ComputeFormationOffsets_SortsMeleeFrontRangedBack()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var ctrl = MakeController(cm, sortingClassLevels: new[] { "Melee Ranged" });
        var f = cm.QueryInterface<FormationComponent>(ctrl)!;

        var melee1 = MakeMember(cm, classes: "Melee", x: -1f, z: 1f);
        var melee2 = MakeMember(cm, classes: "Melee", x: 1f, z: 1f);
        var ranged1 = MakeMember(cm, classes: "Ranged", x: -1f, z: -1f);
        var ranged2 = MakeMember(cm, classes: "Ranged", x: 1f, z: -1f);
        var active = new List<EntityId> { melee1, melee2, ranged1, ranged2 };
        var positions = new List<FixedVector2D>
        {
            new(Fixed.FromFloat(-1f), Fixed.FromFloat(1f)),
            new(Fixed.FromFloat(1f), Fixed.FromFloat(1f)),
            new(Fixed.FromFloat(-1f), Fixed.FromFloat(-1f)),
            new(Fixed.FromFloat(1f), Fixed.FromFloat(-1f)),
        };

        var offsets = f.ComputeFormationOffsets(cm, active, positions);

        // 默认排序(无 SortingOrder):偏移按行生成,逆优先级 splice 让最具体的
        // 组合(Melee)拿到前排(第 1 行),Ranged 拿后排。
        foreach (var o in offsets)
        {
            var id = cm.QueryInterface<IdentityComponent>(o.Ent)!;
            if (id.Classes.Contains("Melee")) Assert.Equal(1, o.Row);
            else Assert.Equal(2, o.Row);
        }
    }

    [Fact]
    public void SetMembers_LinksController_Centers_SetsSpeed()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var ctrl = MakeController(cm);
        var f = cm.QueryInterface<FormationComponent>(ctrl)!;
        var m1 = MakeMember(cm, x: 0f, z: 0f, speed: 3f);
        var m2 = MakeMember(cm, x: 6f, z: 0f, speed: 4f);
        var m3 = MakeMember(cm, x: 0f, z: 6f, speed: 5f);

        f.SetMembers(cm, new List<EntityId> { m1, m2, m3 });

        Assert.Equal(3, f.GetMemberCount());
        foreach (var m in new[] { m1, m2, m3 })
            Assert.Equal(ctrl, cm.QueryInterface<UnitAIComponent>(m)!.FormationController);
        // 控制器移到成员质心 (2,2)。
        var pos = cm.QueryInterface<PositionComponent>(ctrl)!;
        Assert.Equal(2f, pos.Position.X.ToFloat(), 3);
        Assert.Equal(2f, pos.Position.Z.ToFloat(), 3);
        // 编队速度 = 最慢成员 × SpeedMultiplier(1)。
        Assert.Equal(3f, cm.QueryInterface<UnitMotion>(ctrl)!.Speed.ToFloat(), 3);
    }

    [Fact]
    public void RemoveMembers_BelowRequired_Disbands()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var ctrl = MakeController(cm, required: 2);
        var f = cm.QueryInterface<FormationComponent>(ctrl)!;
        var m1 = MakeMember(cm);
        var m2 = MakeMember(cm, x: 4f);
        f.SetMembers(cm, new List<EntityId> { m1, m2 });

        f.RemoveMembers(cm, new List<EntityId> { m1 });

        // 低于 RequiredMemberCount → 控制器销毁;幸存者脱队回 INDIVIDUAL.IDLE。
        Assert.Null(cm.QueryInterface<FormationComponent>(ctrl));
        var survivor = cm.QueryInterface<UnitAIComponent>(m2)!;
        Assert.Null(survivor.FormationController);
        Assert.Equal("INDIVIDUAL.IDLE", survivor.FsmStateName);
    }

    [Fact]
    public void ArrangeFormation_IssuesFormationWalkOrders()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var ctrl = MakeController(cm);
        var f = cm.QueryInterface<FormationComponent>(ctrl)!;
        var members = new[]
        {
            MakeMember(cm, x: -1f, z: 1f), MakeMember(cm, x: 1f, z: 1f),
            MakeMember(cm, x: -1f, z: -1f), MakeMember(cm, x: 1f, z: -1f),
        };
        f.SetMembers(cm, new List<EntityId>(members));
        f.ArrangeFormation(cm, moveCenter: true, force: true, variant: null);

        Assert.NotNull(f.Offsets);
        Assert.Equal(4, f.Offsets!.Count);
        Assert.Equal(2f, f.Width, 3);
        Assert.Equal(2f, f.Depth, 3);
        Assert.Empty(f.FinishedEntities);   // force → ResetFinishedEntities
        foreach (var m in members)
        {
            var order = cm.QueryInterface<UnitAIComponent>(m)!.CurrentOrder;
            Assert.Equal("FormationWalk", order?.Type);
            Assert.Equal(ctrl, order?.Target);
        }
    }

    [Fact]
    public void MemberWalk_ReachesOffset_MarkedFinished()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var ctrl = MakeController(cm);
        var f = cm.QueryInterface<FormationComponent>(ctrl)!;
        var m1 = MakeMember(cm, x: -3f, z: 3f);
        var m2 = MakeMember(cm, x: 3f, z: -3f);
        f.SetMembers(cm, new List<EntityId> { m1, m2 });
        f.ArrangeFormation(cm, moveCenter: true, force: true, variant: null);

        for (int i = 0; i < 400 && !f.AreAllMembersFinished(); i++)
            TickAll(cm, 0.1f, m1, m2);

        Assert.True(f.AreAllMembersFinished());
        foreach (var m in new[] { m1, m2 })
        {
            var ai = cm.QueryInterface<UnitAIComponent>(m)!;
            Assert.Equal("FORMATIONMEMBER.IDLE", ai.FsmStateName);
            // 成员停在分到的世界偏移 1m 吸附容差内(到位判定 = 距目标 ≤1m)。
            var offset = f.Offsets!.Single(o => o.Ent == m);
            var cpos = cm.QueryInterface<PositionComponent>(ctrl)!.Position;
            var pos = cm.QueryInterface<PositionComponent>(m)!.Position;
            float odx = pos.X.ToFloat() - (cpos.X.ToFloat() + offset.X);
            float odz = pos.Z.ToFloat() - (cpos.Z.ToFloat() + offset.Z);
            Assert.True(odx * odx + odz * odz <= 1.1f,
                $"m{m.Value} at ({pos.X.ToFloat():F2},{pos.Z.ToFloat():F2}), offset ({offset.X:F2},{offset.Z:F2})");
        }
    }

    [Fact]
    public void ControllerWalk_MovesWholeFormation()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var ctrl = MakeController(cm);
        var f = cm.QueryInterface<FormationComponent>(ctrl)!;
        var members = new[]
        {
            MakeMember(cm, x: -1f, z: 1f, speed: 4f), MakeMember(cm, x: 1f, z: 1f, speed: 4f),
            MakeMember(cm, x: -1f, z: -1f, speed: 4f), MakeMember(cm, x: 1f, z: -1f, speed: 4f),
        };
        f.SetMembers(cm, new List<EntityId>(members));
        var ctrlAi = cm.QueryInterface<UnitAIComponent>(ctrl)!;

        ctrlAi.Walk(new FixedVector2D(Fixed.FromInt(30), Fixed.Zero));
        ctrlAi.Tick(0.1f, cm);
        Assert.Equal("FORMATIONCONTROLLER.WALKING", ctrlAi.FsmStateName);
        // 控制器入态即重排:成员收到 FormationWalk。
        foreach (var m in members)
            Assert.Equal("FormationWalk", cm.QueryInterface<UnitAIComponent>(m)!.CurrentOrder?.Type);

        var all = new List<EntityId> { ctrl };
        all.AddRange(members);
        for (int i = 0; i < 1200 && !f.AreAllMembersFinished(); i++)
            TickAll(cm, 0.1f, all.ToArray());

        Assert.True(f.AreAllMembersFinished(),
            $"finished {f.FinishedEntities.Count}/{f.Members.Count}; ctrl state={ctrlAi.FsmStateName}; " +
            string.Join("; ", members.Select(m =>
            {
                var ai = cm.QueryInterface<UnitAIComponent>(m)!;
                var p = cm.QueryInterface<PositionComponent>(m)!.Position;
                return $"m{m.Value}@{p.X.ToFloat():F1},{p.Z.ToFloat():F1} {ai.FsmStateName} order={ai.CurrentOrder?.Type}";
            })));
        Assert.True(ctrlAi.IsIdle,
            $"ctrl state={ctrlAi.FsmStateName} order={ctrlAi.CurrentOrder?.Type} " +
            $"pos={cm.QueryInterface<PositionComponent>(ctrl)!.Position.X.ToFloat():F1}," +
            $"{cm.QueryInterface<PositionComponent>(ctrl)!.Position.Z.ToFloat():F1} " +
            $"motion={cm.QueryInterface<UnitMotion>(ctrl)!.HasMoveTarget}");
        // 控制器抵达目标附近,成员收敛到控制器周围(队形宽 2)。
        var cpos = cm.QueryInterface<PositionComponent>(ctrl)!.Position;
        Assert.Equal(30f, cpos.X.ToFloat(), 0);
        Assert.Equal(0f, cpos.Z.ToFloat(), 0);
        foreach (var m in members)
        {
            var p = cm.QueryInterface<PositionComponent>(m)!.Position;
            float dx = p.X.ToFloat() - cpos.X.ToFloat(), dz = p.Z.ToFloat() - cpos.Z.ToFloat();
            Assert.True(dx * dx + dz * dz < 9f, $"member {m.Value} at ({p.X.ToFloat()},{p.Z.ToFloat()}) too far from controller");
        }
    }

    [Fact]
    public void RemoveMembers_AboveRequired_InvalidatesOffsets()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var ctrl = MakeController(cm, required: 2);
        var f = cm.QueryInterface<FormationComponent>(ctrl)!;
        var m1 = MakeMember(cm);
        var m2 = MakeMember(cm, x: 4f);
        var m3 = MakeMember(cm, x: 0f, z: 4f);
        f.SetMembers(cm, new List<EntityId> { m1, m2, m3 });
        f.ArrangeFormation(cm, moveCenter: true, force: true, variant: null);
        Assert.NotNull(f.Offsets);

        f.RemoveMembers(cm, new List<EntityId> { m1 });

        Assert.NotNull(cm.QueryInterface<FormationComponent>(ctrl));   // 未解散
        Assert.Equal(2, f.GetMemberCount());
        Assert.Null(f.Offsets);   // 队形几何作废,下次 ArrangeFormation 重算
        var gone = cm.QueryInterface<UnitAIComponent>(m1)!;
        Assert.Null(gone.FormationController);
        Assert.Equal("INDIVIDUAL.IDLE", gone.FsmStateName);
    }

    [Fact]
    public void GetRealOffsetPositions_RotatesWithController()
    {
        var cm = new ComponentManager(rngSeed: 1);
        var ctrl = MakeController(cm, x: 10f, z: 10f);
        var f = cm.QueryInterface<FormationComponent>(ctrl)!;
        cm.QueryInterface<PositionComponent>(ctrl)!.Rotation =
            new FixedVector3D(Fixed.Zero, Fixed.FromFloat((float)(System.Math.PI / 2)), Fixed.Zero);

        var real = f.GetRealOffsetPositions(cm, new List<FormationComponent.FormationOffset>
        {
            new() { X = 1f, Z = 0f },
            new() { X = 0f, Z = 2f },
        });

        // 旋转 π/2:(x,z) → (z, −x)。偏移 (1,0) → (0,−1);(0,2) → (2,0)。
        Assert.Equal(10f, real[0].X, 1);
        Assert.Equal(9f, real[0].Z, 1);
        Assert.Equal(12f, real[1].X, 1);
        Assert.Equal(10f, real[1].Z, 1);
    }

    [Fact]
    public void RoundTrip_ConfigAndRuntime()
    {
        var f = new FormationComponent
        {
            Shape = "triangle",
            RequiredMemberCount = 3,
            SpeedMultiplier = 1.2f,
            MaxTurningAngle = 0.785f,
            SortingOrder = "fillToTheCenter",
            ShiftRows = true,
            UnitSeparationWidthMultiplier = 1.5f,
            UnitSeparationDepthMultiplier = 0.5f,
            Sloppiness = 0.3f,
            WidthDepthRatio = 2f,
            MinColumns = 2,
            MaxColumns = 8,
            MaxRows = 4,
            CenterGap = 1f,
            FormationSeparation = 4.5f,
            MaxRowsUsed = 3,
            Width = 12f,
            Depth = 6f,
        };
        f.SortingClasses.Add("Melee Ranged");
        f.SortingClasses.Add("Infantry Cavalry");
        f.Members.Add(new EntityId(10));
        f.Members.Add(new EntityId(11));
        f.FinishedEntities.Add(new EntityId(10));
        f.TwinFormations.Add(new EntityId(20));
        f.MaxColumnsUsed.Add(3);
        f.MaxColumnsUsed.Add(1);
        f.Offsets = new List<FormationComponent.FormationOffset>
        {
            new() { Ent = new EntityId(10), X = 1.5f, Z = -2.5f, Row = 1, Column = 2 },
            new() { Ent = new EntityId(11), X = -1f, Z = 2f, Row = 2, Column = 1 },
        };

        var s1 = new CapturingSerializer();
        f.Serialize(s1);
        var back = new FormationComponent();
        back.Deserialize(new ReplayingDeserializer(s1));
        var s2 = new CapturingSerializer();
        back.Serialize(s2);
        Assert.Equal(s1.Fields, s2.Fields);   // 全字段流逐位一致

        Assert.Equal("triangle", back.Shape);
        Assert.Equal(3, back.RequiredMemberCount);
        Assert.Equal(1.2f, back.SpeedMultiplier, 3);
        Assert.Equal(0.785f, back.MaxTurningAngle, 3);
        Assert.Equal("fillToTheCenter", back.SortingOrder);
        Assert.True(back.ShiftRows);
        Assert.Equal(1.5f, back.UnitSeparationWidthMultiplier, 3);
        Assert.Equal(0.5f, back.UnitSeparationDepthMultiplier, 3);
        Assert.Equal(0.3f, back.Sloppiness, 3);
        Assert.Equal(2f, back.WidthDepthRatio, 3);
        Assert.Equal(2, back.MinColumns);
        Assert.Equal(8, back.MaxColumns);
        Assert.Equal(4, back.MaxRows);
        Assert.Equal(1f, back.CenterGap, 3);
        Assert.Equal(4.5f, back.FormationSeparation, 3);
        Assert.Equal(3, back.MaxRowsUsed);
        Assert.Equal(new[] { 3, 1 }, back.MaxColumnsUsed);
        Assert.Equal(12f, back.Width, 3);
        Assert.Equal(6f, back.Depth, 3);
        Assert.Equal(new[] { 10u, 11u }, Ids(back.Members));
        Assert.Equal(new[] { 10u }, Ids(back.FinishedEntities));
        Assert.Equal(new[] { 20u }, Ids(back.TwinFormations));
        Assert.Equal(new[] { "Melee Ranged", "Infantry Cavalry" }, back.SortingClasses);
        Assert.NotNull(back.Offsets);
        Assert.Equal(2, back.Offsets!.Count);
        Assert.Equal(10u, back.Offsets[0].Ent.Value);
        Assert.Equal(1.5f, back.Offsets[0].X, 3);
        Assert.Equal(-2.5f, back.Offsets[0].Z, 3);
        Assert.Equal(1, back.Offsets[0].Row);
        Assert.Equal(2, back.Offsets[0].Column);
    }

    [Fact]
    public void RealTemplate_Box_ParsesAndAssembles()
    {
        // 真实模板(special/formations/box.xml,parent=template_formation):解析 +
        // SpawnEntity 装配出控制器(UnitAI 控制器态 + Formation 配置,无 Health/Cost)。
        const string templatesRoot = "../../../binaries/data/mods/public/simulation/templates";
        if (!System.IO.Directory.Exists(templatesRoot)) return;   // 数据树未拉取则跳过
        var cm = new ComponentManager(rngSeed: 1,
            templates: new Content.TemplateLoader(templatesRoot));
        SimSystem.Init(cm);

        var stats = cm.Templates!.ExtractStats("special/formations/box");
        Assert.True(stats.HasFormation);
        Assert.Equal(4, stats.FormationRequiredMemberCount);
        Assert.Equal("square", stats.FormationShape);
        Assert.Equal(0.785f, stats.FormationMaxTurningAngle, 3);
        Assert.Equal("fillToTheCenter", stats.FormationSortingOrder);
        Assert.Equal(new[] { "Melee Ranged" }, stats.FormationSortingClasses);
        // 父模板 template_formation 继承的缺省值。
        Assert.Equal(1f, stats.FormationSpeedMultiplier, 3);
        Assert.Equal(1f, stats.FormationWidthDepthRatio, 3);

        var ctrl = cm.SpawnEntity("special/formations/box", 5f, 5f, ownerPlayerId: 1);
        var f = cm.QueryInterface<FormationComponent>(ctrl);
        Assert.NotNull(f);
        Assert.Equal(4, f!.RequiredMemberCount);
        Assert.Equal("square", f.Shape);
        Assert.Equal(new[] { "Melee Ranged" }, f.SortingClasses);
        var ai = cm.QueryInterface<UnitAIComponent>(ctrl);
        Assert.NotNull(ai);
        Assert.True(ai!.IsFormationController);
        Assert.Equal("FORMATIONCONTROLLER.IDLE", ai.FsmStateName);
        // 控制器是虚拟实体:无 Health/Cost/Obstruction。
        Assert.Null(cm.QueryInterface<HealthComponent>(ctrl));
        Assert.Null(cm.QueryInterface<CostComponent>(ctrl));
        Assert.Null(cm.QueryInterface<ObstructionComponent>(ctrl));
    }

    private static uint[] Ids(List<EntityId> list)
    {
        var arr = new uint[list.Count];
        for (int i = 0; i < list.Count; i++) arr[i] = list[i].Value;
        return arr;
    }
}
