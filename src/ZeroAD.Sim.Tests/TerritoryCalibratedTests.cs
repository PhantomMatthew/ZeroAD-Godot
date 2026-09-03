using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;

namespace ZeroAD.Sim.Tests;

/// <summary>领土校准(上游成本加权洪泛模型)的增量行为:地形成本减速 + 百分比。</summary>
public sealed class TerritoryCalibratedTests
{
    private static EntityId AddInfluencer(ComponentManager cm, int owner, float x, float z,
        float radius, int weight, bool root)
    {
        var e = cm.CreateEntity();
        cm.AddComponent(e, new PositionComponent());
        cm.QueryInterface<PositionComponent>(e)!.Position =
            new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        cm.AddComponent(e, new OwnershipComponent { PlayerId = owner });
        cm.AddComponent(e, new TerritoryInfluenceComponent
        { Radius = Fixed.FromFloat(radius), Weight = weight, Root = root });
        cm.NotifyEntityCreated(e);
        return e;
    }

    [Fact]
    public void GetTerritoryPercentage_CountsConnectedPassable()
    {
        var cm = new ComponentManager(42);
        var tm = new TerritoryManager(cm, 64);
        Assert.Equal(0, tm.GetTerritoryPercentage(1));   // 无人

        AddInfluencer(cm, 1, 32, 32, radius: 24, weight: 10000, root: true);
        int pct = tm.GetTerritoryPercentage(1);
        Assert.True(pct > 0, $"connected root territory should score, got {pct}");
        Assert.True(pct < 50, $"single small CC can't cover half the map, got {pct}");
        Assert.Equal(0, tm.GetTerritoryPercentage(2));
    }

    [Fact]
    public void ImpassableTerrain_SlowsInfluenceSpread()
    {
        // 寻路网格:左半陆右半深水(不可通行);领土源放岸上。上游:不可通行格
        // cost=4 → 同 radius 下跨越不可通行带的波及显著短于陆地方向。
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var pf = new PathfinderComponent(cm);
        int tiles = 16;   // 64m
        var terrain = new TerrainTileInfo[tiles, tiles];
        for (int j = 0; j < tiles; j++)
            for (int i = 0; i < tiles; i++)
                terrain[i, j] = i < 8
                    ? new TerrainTileInfo(Fixed.Zero, Fixed.Zero, Fixed.Zero)          // 陆
                    : new TerrainTileInfo(Fixed.FromInt(5), Fixed.Zero, Fixed.Zero);   // 深水
        pf.RebuildGridFromTiles(terrain, tiles, System.Array.Empty<ObstructionSquare>());
        SimSystem.SetPathfinder(pf);

        var tm = new TerritoryManager(cm, 64);
        // 源在 (16,32)(陆上,距岸 2 瓦),radius 大(覆盖全图量级)。
        AddInfluencer(cm, 1, 16, 32, radius: 120, weight: 10000, root: true);

        // 陆地远端同纬度:(24,32) 陆方向 1 瓦 —— 有主。
        Assert.Equal(1, tm.GetOwner(Fixed.FromInt(24), Fixed.FromInt(32)));
        // 水带深处:(56,32) 距源 5 瓦,其中 4 瓦 cost=4 —— 等效 falloff 远超 radius 预算
        // (falloff=10000×8/120≈666/瓦;陆向可及 ~15 瓦,水向仅 ~3.75 瓦)→ 无主。
        Assert.Equal(0, tm.GetOwner(Fixed.FromInt(56), Fixed.FromInt(32)));
        // 无寻路网格的对照(均匀成本)下该点本可有主——在另一世界验证对照:
        SimSystem.SetPathfinder(null!);
        var cm2 = new ComponentManager(43);
        var tm2 = new TerritoryManager(cm2, 64);
        AddInfluencer(cm2, 1, 16, 32, radius: 120, weight: 10000, root: true);
        Assert.Equal(1, tm2.GetOwner(Fixed.FromInt(56), Fixed.FromInt(32)));
    }
}
