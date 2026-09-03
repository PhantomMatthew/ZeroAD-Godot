using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;
using Xunit;

namespace ZeroAD.Sim.Tests.Pathfinding;

/// <summary>增量阻挡更新(P1)测试:ObstructionManager 脏区打点 → 网格补丁 +
/// 分层局部重连,与全量重建逐位等价(网格单元)且连通性查询等价(分层区域)。</summary>
public sealed class IncrementalGridUpdateTests
{
    private static TerrainTileInfo Land() => new(Fixed.Zero, Fixed.Zero, Fixed.Zero);

    private static TerrainTileInfo[,] FlatTerrain(int tiles)
    {
        var t = new TerrainTileInfo[tiles, tiles];
        for (int j = 0; j < tiles; j++)
            for (int i = 0; i < tiles; i++)
                t[i, j] = Land();
        return t;
    }

    private static ObstructionSquare Box(float x, float z, float hw, float hh,
        ObstructionFlags flags = ObstructionFlags.DefaultBlock) =>
        new(Fixed.FromFloat(x), Fixed.FromFloat(z),
            new FixedVector2D(Fixed.FromInt(1), Fixed.Zero),
            new FixedVector2D(Fixed.Zero, Fixed.FromInt(1)),
            Fixed.FromFloat(hw), Fixed.FromFloat(hh), flags);

    /// <summary>两格模型:Build 结果 = 地形膨胀 ∪ 形状自带 clearance 印戳,
    /// 与旧管线(印后整体膨胀)逐位等价的自证:直接对拍"两趟 Build"。</summary>
    [Fact]
    public void Build_TwoGridModel_MatchesReferenceSemantics()
    {
        // 参考语义:先地形膨胀,再形状(AABB+clearance)印戳——本测试与 PatchRect 共用
        // 同一 StampObstruction,等价性由增量对拍测试兜底;此处仅钉死 Build 基本盘:
        // 形状印戳覆盖 AABB+clearance,远处不受影响。
        var builder = new PassabilityGridBuilder();
        var terrain = FlatTerrain(8);   // 32x32 navcells
        builder.Build(terrain, 8, new[] { Box(16f, 16f, 2f, 2f) });
        var grid = builder.Grid!;
        var def = builder.Default;
        int clear = def.Clearance.ToIntRoundToInfinity();
        // 中心(16,16)→ navcell 16;印戳覆盖 [16-2-clear .. 16+2+clear]。
        Assert.False(PathfindingCore.IsPassable(grid.Get(16, 16), def.Mask));
        Assert.False(PathfindingCore.IsPassable(grid.Get(16 - 2 - clear, 16), def.Mask));
        Assert.True(PathfindingCore.IsPassable(grid.Get(16 - 2 - clear - 2, 16), def.Mask));
        // 地形基线无形状。
        Assert.True(PathfindingCore.IsPassable(builder.TerrainOnly!.Get(16, 16), def.Mask));
    }

    [Fact]
    public void PatchRect_RestoresThenRestamps_EqualsFullRebuild()
    {
        // 同一场景:全量 Build(A) vs Build(无新建筑) + PatchRect(脏区, 含新建筑)(B)。
        int tiles = 32;   // 128 navcells/side
        var baseline = new[] { Box(30f, 30f, 4f, 4f) };
        var withNew = new[] { Box(30f, 30f, 4f, 4f), Box(90f, 60f, 3f, 5f) };

        var a = new PassabilityGridBuilder();
        a.Build(FlatTerrain(tiles), tiles, withNew);

        var b = new PassabilityGridBuilder();
        b.Build(FlatTerrain(tiles), tiles, baseline);
        // 脏区 = 新形状 bbox + 最大 clearance 余量(与 ObstructionManager 打点同式)。
        int margin = b.MaxClearanceNavcells + 1;
        int i0 = PathfindingCore.WorldToNavcell(Fixed.FromFloat(90f - 5f)) - margin;
        int j0 = PathfindingCore.WorldToNavcell(Fixed.FromFloat(60f - 3f)) - margin;
        int i1 = PathfindingCore.WorldToNavcell(Fixed.FromFloat(90f + 5f)) + margin;
        int j1 = PathfindingCore.WorldToNavcell(Fixed.FromFloat(60f + 3f)) + margin;
        b.PatchRect(i0, j0, i1, j1, withNew);

        for (int j = 0; j < b.Grid!.H; j++)
            for (int i = 0; i < b.Grid.W; i++)
                Assert.Equal(a.Grid!.Get(i, j).Value, b.Grid.Get(i, j).Value);
    }

    [Fact]
    public void PatchRect_Removal_RestoresToBaseline()
    {
        int tiles = 16;
        var withBuilding = new[] { Box(32f, 32f, 4f, 4f) };
        var empty = System.Array.Empty<ObstructionSquare>();

        var a = new PassabilityGridBuilder();
        a.Build(FlatTerrain(tiles), tiles, empty);

        var b = new PassabilityGridBuilder();
        b.Build(FlatTerrain(tiles), tiles, withBuilding);
        int margin = b.MaxClearanceNavcells + 1;
        int i0 = PathfindingCore.WorldToNavcell(Fixed.FromFloat(32f - 4f)) - margin;
        int j0 = i0;
        int i1 = PathfindingCore.WorldToNavcell(Fixed.FromFloat(32f + 4f)) + margin;
        int j1 = i1;
        b.PatchRect(i0, j0, i1, j1, empty);

        for (int j = 0; j < b.Grid!.H; j++)
            for (int i = 0; i < b.Grid.W; i++)
                Assert.Equal(a.Grid!.Get(i, j).Value, b.Grid.Get(i, j).Value);
    }

    [Fact]
    public void HierUpdate_DirtyChunk_MatchesFullRecompute()
    {
        // 网格:开放平原 + 一堵竖墙把图分两半;增量打掉墙的一段(开门)后,
        // 连通性须与全量重建一致(两半重新连通)。
        int tiles = 32;   // 128 navcells → 2x2 chunks
        int nav = tiles * PathfindingCore.NavcellsPerTerrainTile;
        var terrain = FlatTerrain(tiles);

        // 墙:x=64 一线,hw 0.5,贯穿全图高。
        var wall = Box(64f, 64f, 0.5f, 64f);
        var wallWithGate = Box(64f, 64f, 0.5f, 64f);

        var full = new PassabilityGridBuilder();
        // 全量:墙上开洞 = 两段墙(下段 0..56,上段 72..128)。
        var gateSegments = new[]
        {
            Box(64f, 28f, 0.5f, 28f), Box(64f, 100f, 0.5f, 28f),
        };
        full.Build(terrain, tiles, gateSegments);
        var fullHier = new HierarchicalPathfinder();
        fullHier.Recompute(full.Grid!, full.UnitClasses);

        var inc = new PassabilityGridBuilder();
        inc.Build(FlatTerrain(tiles), tiles, new[] { wallWithGate });
        var incHier = new HierarchicalPathfinder();
        incHier.Recompute(inc.Grid!, inc.UnitClasses);

        // 增量:墙中段(28..72 中心 50)删除 → 脏区 → PatchRect + hier.Update。
        int margin = inc.MaxClearanceNavcells + 1;
        int i0 = PathfindingCore.WorldToNavcell(Fixed.FromFloat(63.5f)) - margin;
        int j0 = PathfindingCore.WorldToNavcell(Fixed.FromFloat(28f)) - margin;
        int i1 = PathfindingCore.WorldToNavcell(Fixed.FromFloat(64.5f)) + margin;
        int j1 = PathfindingCore.WorldToNavcell(Fixed.FromFloat(72f)) + margin;
        inc.PatchRect(i0, j0, i1, j1, gateSegments);
        incHier.Update(inc.Grid!, new[] { (i0, j0, i1, j1) }, inc.UnitClasses);

        // 网格逐位等价
        for (int j = 0; j < nav; j++)
            for (int i = 0; i < nav; i++)
                Assert.Equal(full.Grid!.Get(i, j).Value, inc.Grid!.Get(i, j).Value);

        // 连通性等价:墙左右各取一点,两侧 global region 关系(同/不同)与全量一致。
        var def = full.Default;
        uint fullL = fullHier.GetGlobalRegion(32, 64, def.Mask);
        uint fullR = fullHier.GetGlobalRegion(96, 64, def.Mask);
        uint incL = incHier.GetGlobalRegion(32, 64, def.Mask);
        uint incR = incHier.GetGlobalRegion(96, 64, def.Mask);
        Assert.NotEqual(0u, fullL);
        Assert.Equal(fullL == fullR, incL == incR);
        Assert.True(incL == incR, "gate opened: both halves must share a region");
    }

    [Fact]
    public void HierUpdate_WallCloses_RegionSplits()
    {
        // 反向:开放图 → 增量加墙 → 两半断开。
        int tiles = 32;
        var terrain = FlatTerrain(tiles);
        var wall = Box(64f, 64f, 0.5f, 64f);

        var inc = new PassabilityGridBuilder();
        inc.Build(terrain, tiles, System.Array.Empty<ObstructionSquare>());
        var hier = new HierarchicalPathfinder();
        hier.Recompute(inc.Grid!, inc.UnitClasses);
        var def = inc.Default;
        Assert.Equal(hier.GetGlobalRegion(32, 64, def.Mask), hier.GetGlobalRegion(96, 64, def.Mask));

        int margin = inc.MaxClearanceNavcells + 1;
        int i0 = PathfindingCore.WorldToNavcell(Fixed.FromFloat(63.5f)) - margin;
        int j0 = 0;
        int i1 = PathfindingCore.WorldToNavcell(Fixed.FromFloat(64.5f)) + margin;
        int j1 = PathfindingCore.WorldToNavcell(Fixed.FromFloat(128f)) + margin;
        inc.PatchRect(i0, j0, i1, j1, new[] { wall });
        hier.Update(inc.Grid!, new[] { (i0, j0, i1, j1) }, inc.UnitClasses);

        uint l = hier.GetGlobalRegion(32, 64, def.Mask);
        uint r = hier.GetGlobalRegion(96, 64, def.Mask);
        Assert.NotEqual(0u, l);
        Assert.NotEqual(0u, r);
        Assert.NotEqual(l, r);
    }

    [Fact]
    public void ObstructionManager_MarksDirty_OnAddMoveRemove()
    {
        var mgr = new ObstructionManager();
        Assert.False(mgr.HasPathfinderDirtiness);

        // 无 BlockPathfinding/BlockFoundation 旗 → 不打脏(移动单位不刷屏)。
        var unitTag = mgr.AddUnitShape(new EntityId(5), Fixed.FromInt(10), Fixed.FromInt(10),
            Fixed.FromInt(1), ObstructionFlags.BlockMovement, 0);
        Assert.False(mgr.HasPathfinderDirtiness);

        var staticTag = mgr.AddStaticShape(new EntityId(9), Fixed.FromInt(40), Fixed.FromInt(40),
            new FixedVector2D(Fixed.FromInt(1), Fixed.Zero), new FixedVector2D(Fixed.Zero, Fixed.FromInt(1)),
            Fixed.FromInt(4), Fixed.FromInt(4), ObstructionFlags.DefaultBlock, 0, 0);
        Assert.True(mgr.HasPathfinderDirtiness);
        var dirty = mgr.TakePathfinderDirtiness();
        Assert.Single(dirty);
        Assert.False(mgr.HasPathfinderDirtiness);   // 取走即清

        mgr.MoveShape(staticTag, Fixed.FromInt(80), Fixed.FromInt(80),
            new FixedVector2D(Fixed.FromInt(1), Fixed.Zero), new FixedVector2D(Fixed.Zero, Fixed.FromInt(1)));
        dirty = mgr.TakePathfinderDirtiness();
        Assert.Equal(2, dirty.Count);   // 旧位 + 新位

        mgr.RemoveShape(staticTag);
        Assert.Single(mgr.TakePathfinderDirtiness());
        _ = unitTag;
    }
}
