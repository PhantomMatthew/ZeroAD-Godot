using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

// 阻挡缓释(UnitMotion):A. 不可达目标沿直线钳到最远合法点(不直线穿墙);
// B. 卡死看门狗侧绕。需要一个带墙的真实寻路网格。
public sealed class ObstructionMitigationTests
{
    private const int Tiles = 32;                 // 32 地块 × 4m = 128m 见方
    private const float TileSize = 4f;

    private sealed class World
    {
        public required ComponentManager Cm;
        public required PathfinderComponent Pf;
    }

    /// <summary>128×128m 全陆地网格;x=32m 处一堵纵向静障碍墙(z 0..48,南侧留口)。
    /// (世界 128m:图外缘 12 navcell 边带不盖住 z=56 的目标点——16 地块时代无此问题。)</summary>
    private static World SetupWalledWorld()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var obstructions = new ObstructionManager(Tiles * (int)TileSize, TileSize);
        SimSystem.SetObstructionManager(obstructions);

        var terrain = new TerrainComponent();
        terrain.Configure(Tiles, TileSize);
        var grid = new TerrainClass[Tiles, Tiles];
        for (int i = 0; i < Tiles; i++)
            for (int j = 0; j < Tiles; j++)
                grid[i, j] = TerrainClass.Land;
        terrain.SetPassabilityGrid(grid);

        // 墙:x≈32,z 从 0 到 48(2m 宽,分段 4m 长)。旗标 = 真实墙体的 DefaultBlock
        // (pathfinding 类位图只印 BlockPathfinding 旗——原版 Rasterize 同款过滤;
        // 此前省略该旗只因旧构建器旗盲全印)。
        var wallOwner = cm.CreateEntity();
        for (int z = 2; z <= 46; z += 4)
        {
            obstructions.AddStaticShape(wallOwner,
                Fixed.FromInt(32), Fixed.FromInt(z),
                new FixedVector2D(Fixed.Zero, Fixed.FromInt(1)),   // u = +z 方向
                new FixedVector2D(Fixed.FromInt(1), Fixed.Zero),   // v = +x
                Fixed.FromInt(2), Fixed.FromInt(1),
                ObstructionFlags.BlockMovement | ObstructionFlags.BlockFoundation
                    | ObstructionFlags.BlockPathfinding, 1, 1);
        }

        var pf = new PathfinderComponent(cm);
        pf.SetTerrain(terrain);
        pf.RebuildGrid();
        SimSystem.SetPathfinder(pf);
        return new World { Cm = cm, Pf = pf };
    }

    private static EntityId MakeWalker(ComponentManager cm, float x, float z)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        var motion = new UnitMotion();
        cm.AddComponent(e, motion);
        motion.Speed = Fixed.FromInt(8);
        return e;
    }

    [Fact]
    public void GridBuilt_WallBlocksStraightLine()
    {
        var w = SetupWalledWorld();
        Assert.NotNull(w.Pf.PassabilityGrid);
        // 直线穿墙应为 false(墙在 x=32,z 0..48;从 (8,24) 到 (56,24) 必穿)。
        var pc = w.Pf.DefaultClass.Mask;
        Assert.False(w.Pf.CheckMovement(
            new FixedVector2D(Fixed.FromInt(16), Fixed.FromInt(24)),
            new FixedVector2D(Fixed.FromInt(56), Fixed.FromInt(24)), pc));
        // 绕南侧缺口(56,56)应可达。
        Assert.True(w.Pf.CheckMovement(
            new FixedVector2D(Fixed.FromInt(16), Fixed.FromInt(56)),
            new FixedVector2D(Fixed.FromInt(56), Fixed.FromInt(56)), pc));
    }

    [Fact]
    public void UnreachableGoal_ClampsToFarthestReachable_DoesNotGhostThroughWall()
    {
        var w = SetupWalledWorld();
        var walker = MakeWalker(w.Cm, 16, 24);
        var motion = w.Cm.QueryInterface<UnitMotion>(walker)!;
        var goal = new FixedVector2D(Fixed.FromInt(56), Fixed.FromInt(24));   // 墙后

        motion.MoveToPoint(goal);

        // 若长程求解已路由绕口(分层寻路会找南侧缺口),单位最终应到墙另一侧——
        // 两种结果都合法;关键断言:任何时刻不穿越墙体线段(x≈32 且 z<48)。
        // 若路径为空,则目标应被钳到墙前(TargetPos.x < 32)。
        for (int i = 0; i < 400 && motion.HasMoveTarget; i++)
        {
            motion.Tick(0.1f);
            var p = w.Cm.QueryInterface<PositionComponent>(walker)!.Position;
            float px = p.X.ToFloat(), pz = p.Z.ToFloat();
            bool insideWall = px > 30.5f && px < 33.5f && pz < 48f;
            Assert.False(insideWall, $"walker ghosted into wall at ({px:F1},{pz:F1})");
        }
        var final = w.Cm.QueryInterface<PositionComponent>(walker)!.Position;
        float fx = final.X.ToFloat();
        // 到达(绕行成功)或钳在墙前(x ≤ 32)——唯独不许"穿墙后在墙内"。
        Assert.True(fx <= 32.5f || fx > 33.5f, $"walker ended inside wall band: x={fx:F1}");
    }

    [Fact]
    public void StuckWatchdog_SidestepsThenResumes()
    {
        var w = SetupWalledWorld();
        var walker = MakeWalker(w.Cm, 16, 56);   // 开阔地(南半无墙)
        var motion = w.Cm.QueryInterface<UnitMotion>(walker)!;
        var goal = new FixedVector2D(Fixed.FromInt(56), Fixed.FromInt(56));
        motion.MoveToPoint(goal);

        // 冻结(Speed=0)→ 看门狗窗口 0.6s 位移为 0 → 触发一次侧绕。
        motion.Speed = Fixed.Zero;
        for (int i = 0; i < 8; i++) motion.Tick(0.1f);   // 0.8s > 0.6s 窗口

        // 恢复速度:先走侧绕点(偏出直线 ≥2m),再续程到目标。
        motion.Speed = Fixed.FromInt(8);
        float maxDeviation = 0f;
        for (int i = 0; i < 400 && motion.HasMoveTarget; i++)
        {
            motion.Tick(0.1f);
            var p = w.Cm.QueryInterface<PositionComponent>(walker)!.Position;
            // 起点 (8,56) → 目标 (56,56) 是水平线:偏差 = |z - 56|。
            float dev = System.MathF.Abs(p.Z.ToFloat() - 56f);
            if (dev > maxDeviation) maxDeviation = dev;
        }
        var final = w.Cm.QueryInterface<PositionComponent>(walker)!.Position;
        Assert.True(maxDeviation >= 2f, $"expected a sidestep detour, max deviation {maxDeviation:F2}m");
        Assert.Equal(56f, final.X.ToFloat(), 0);
        Assert.Equal(56f, final.Z.ToFloat(), 0);
    }

    [Fact]
    public void ClearGoal_StraightBeelineUnchanged()
    {
        // 回归护栏:无障碍直线目标照常直达(缓释不改变正常路径)。
        var w = SetupWalledWorld();
        var walker = MakeWalker(w.Cm, 16, 56);
        var motion = w.Cm.QueryInterface<UnitMotion>(walker)!;
        motion.MoveToPoint(new FixedVector2D(Fixed.FromInt(56), Fixed.FromInt(56)));

        float maxDeviation = 0f;
        for (int i = 0; i < 400 && motion.HasMoveTarget; i++)
        {
            motion.Tick(0.1f);
            var p = w.Cm.QueryInterface<PositionComponent>(walker)!.Position;
            float dev = System.MathF.Abs(p.Z.ToFloat() - 56f);
            if (dev > maxDeviation) maxDeviation = dev;
        }
        var final = w.Cm.QueryInterface<PositionComponent>(walker)!.Position;
        Assert.True(maxDeviation < 1.5f, $"beeline deviated {maxDeviation:F2}m unexpectedly");
        Assert.Equal(56f, final.X.ToFloat(), 0);
    }

    // --- 编队控制器绕障跳跃(原版 UnitAI.js AttemptObstructionMitigation:6786-6838)---

    private static EntityId MakeFormationMember(ComponentManager cm, float x, float z)
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new UnitMotion());
        return e;
    }

    [Fact]
    public void FormationController_Stuck_JumpsToMemberClosestToDestination_ThenCooldown()
    {
        // 裸世界(无寻路/无障碍):落点校验两段都跳过,专测跳跃决策 + 5s 冷却。
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var controller = cm.CreateEntity();
        var ctrlPos = new PositionComponent();
        cm.AddComponent(controller, ctrlPos);
        ctrlPos.Position = new FixedVector3D(Fixed.Zero, Fixed.Zero, Fixed.Zero);
        var ctrlMotion = new UnitMotion();
        cm.AddComponent(controller, ctrlMotion);
        var ai = new UnitAIComponent();
        cm.AddComponent(controller, ai);
        ai.InitAsFormationController();
        var formation = new FormationComponent { Shape = "square", RequiredMemberCount = 2 };
        cm.AddComponent(controller, formation);

        // 成员:近目标者 (90,0),远者 (0,10);SetMembers → 控制器跳到质心 (45,5)。
        var memberNear = MakeFormationMember(cm, 90f, 0f);
        var memberFar = MakeFormationMember(cm, 0f, 10f);
        formation.SetMembers(cm, new List<EntityId> { memberNear, memberFar });

        // 锁死控制器移动(Speed=0 → 看门狗 0.6s 窗口零位移 → IsStuckThisLeg)。
        // ComputeMotionParameters 只在 SetMembers 时跑,后置清零不被覆写。
        ctrlMotion.Speed = Fixed.Zero;
        var dest = new FixedVector2D(Fixed.FromInt(100), Fixed.Zero);
        ai.Walk(dest);
        ai.Tick(0.1f, cm);   // 派单 → FORMATIONCONTROLLER.WALKING(Enter 重排 + MoveToPoint)
        Assert.Equal("FORMATIONCONTROLLER.WALKING", ai.FsmStateName);

        for (int i = 0; i < 8; i++) ctrlMotion.Tick(0.1f);   // 0.8s 零位移
        Assert.True(ctrlMotion.IsStuckThisLeg);

        ai.Tick(0.1f, cm);   // Timer → 绕障跳跃(成员 (90,0) 比控制器近 >2m)
        Assert.Equal(90f, ctrlPos.Position.X.ToFloat(), 3);
        Assert.Equal(0f, ctrlPos.Position.Z.ToFloat(), 3);
        Assert.False(ctrlMotion.IsStuckThisLeg);   // 跳后重解路径,信号清零

        // 冷却:把远成员挪到正目标点(距 0 < 控制器距 10 − 2,本该再跳),
        // 但 5s 冷却内不得再跳。
        cm.QueryInterface<PositionComponent>(memberFar)!.Position =
            new FixedVector3D(Fixed.FromInt(100), Fixed.Zero, Fixed.Zero);
        // 跳跃后看门狗锚点仍是跳前位置:首个窗口量到"位移 45m"(假恢复),
        // 第二窗口才重新检出卡死——故需 >0.6+0.6s。
        for (int i = 0; i < 14; i++) ctrlMotion.Tick(0.1f);
        Assert.True(ctrlMotion.IsStuckThisLeg);
        ai.Tick(0.1f, cm);
        Assert.Equal(90f, ctrlPos.Position.X.ToFloat(), 3);
    }
}
