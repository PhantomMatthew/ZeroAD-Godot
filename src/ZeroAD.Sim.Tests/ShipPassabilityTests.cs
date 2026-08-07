using System.IO;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using Xunit;

namespace ZeroAD.Sim.Tests;

// 船只水路通行(原版 UnitMotion/PassabilityClass):船按 Ship 水类寻路/判地形,
// 陆军按 Default 陆类——此前一律陆类,船在陆网格上无解永远卡岸,也无法在水面出生。
// 注意:船类净空 10m(贴近岸/障碍的水面对船不可通行),测试图须用宽水带。
public sealed class ShipPassabilityTests
{
    private const int Tiles = 32;                 // 32 地块 × 4m = 128m 见方
    private const float TileSize = 4f;
    // 中央纵向水带:地块 i∈[10,21] → 世界 x∈[40,88);船净空 10m 后可航带 ≈ x∈[50,78)。

    private static (ComponentManager Cm, PathfinderComponent Pf) SetupWaterBandWorld()
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        SimSystem.SetObstructionManager(new ObstructionManager(Tiles * (int)TileSize, TileSize));

        var terrain = new TerrainComponent();
        terrain.Configure(Tiles, TileSize);
        var grid = new TerrainClass[Tiles, Tiles];
        for (int i = 0; i < Tiles; i++)
            for (int j = 0; j < Tiles; j++)
                grid[i, j] = (i >= 10 && i <= 21) ? TerrainClass.Water : TerrainClass.Land;
        terrain.SetPassabilityGrid(grid);

        var pf = new PathfinderComponent(cm);
        pf.SetTerrain(terrain);
        pf.RebuildGrid();
        SimSystem.SetPathfinder(pf);
        return (cm, pf);
    }

    private static EntityId MakeWalker(ComponentManager cm, float x, float z, string passClass = "default")
    {
        var e = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(e, pos);
        pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        var motion = new UnitMotion();
        cm.AddComponent(e, motion);
        motion.Speed = Fixed.FromInt(8);
        motion.PassClassName = passClass;
        return e;
    }

    [Fact]
    public void LandUnit_NeverCrossesWater_StopsAtReachableShore()
    {
        var (cm, _) = SetupWaterBandWorld();
        var walker = MakeWalker(cm, 16, 64);   // 西岸
        var motion = cm.QueryInterface<UnitMotion>(walker)!;
        // 目标在东岸,水带贯通全图 → 长程给"最近可达点"路径;单位走完必须停在
        // 路径末端,绝不瞬移到原目标(此前末路标瞬移 TargetPos,陆军直接渡水)。
        motion.MoveToPoint(new FixedVector2D(Fixed.FromInt(112), Fixed.FromInt(64)));

        for (int i = 0; i < 1200 && motion.HasMoveTarget; i++)
        {
            motion.Tick(0.1f);
            float px = cm.QueryInterface<PositionComponent>(walker)!.Position.X.ToFloat();
            Assert.True(px < 40f, $"land unit entered water band: x={px:F1}");
        }
        float fx = cm.QueryInterface<PositionComponent>(walker)!.Position.X.ToFloat();
        Assert.True(fx < 40f, $"land unit ended in water: x={fx:F1}");
        Assert.True(fx > 16f, $"land unit should advance toward the goal: x={fx:F1}");
    }

    [Fact]
    public void Ship_CrossesWaterFreely_AlongBand()
    {
        var (cm, _) = SetupWaterBandWorld();
        var ship = MakeWalker(cm, 64, 16, passClass: "ship");   // 可航带内
        var motion = cm.QueryInterface<UnitMotion>(ship)!;
        // 同在水带内的目标:船应可达(陆类则因起点不可通行而寸步难行)。
        motion.MoveToPoint(new FixedVector2D(Fixed.FromInt(64), Fixed.FromInt(112)));

        for (int i = 0; i < 1500 && motion.HasMoveTarget; i++)
            motion.Tick(0.1f);

        var final = cm.QueryInterface<PositionComponent>(ship)!.Position;
        Assert.False(motion.HasMoveTarget);
        Assert.True(System.MathF.Abs(final.Z.ToFloat() - 112f) <= 1.5f,
            $"ship should reach the far end of the band: z={final.Z.ToFloat():F1}");
        Assert.True(System.MathF.Abs(final.X.ToFloat() - 64f) <= 1.5f,
            $"ship drifted off course: x={final.X.ToFloat():F1}");
    }

    [Fact]
    public void Ship_ClampsAtShoreline_WhenOrderedOntoLand()
    {
        var (cm, _) = SetupWaterBandWorld();
        var ship = MakeWalker(cm, 64, 64, passClass: "ship");
        var motion = cm.QueryInterface<UnitMotion>(ship)!;
        // 目标在西岸陆地:船最多到船类水线(岸 40m + 净空 10m ≈ x=50),永不上岸。
        motion.MoveToPoint(new FixedVector2D(Fixed.FromInt(16), Fixed.FromInt(64)));

        for (int i = 0; i < 800 && motion.HasMoveTarget; i++)
        {
            motion.Tick(0.1f);
            float px = cm.QueryInterface<PositionComponent>(ship)!.Position.X.ToFloat();
            Assert.True(px >= 49f, $"ship ran aground on land: x={px:F1}");
        }
        float fx = cm.QueryInterface<PositionComponent>(ship)!.Position.X.ToFloat();
        Assert.True(fx >= 49f && fx < 88f, $"ship should stop at the ship waterline: x={fx:F1}");
    }

    [Fact]
    public void CheckUnitPlacement_ShipNeedsWater_LandNeedsLand()
    {
        var (_, pf) = SetupWaterBandWorld();
        var water = Fixed.FromInt(64);
        var land = Fixed.FromInt(16);
        var z = Fixed.FromInt(64);
        var clearance = Fixed.FromInt(1);

        Assert.Equal(PlacementResult.Success,
            pf.CheckUnitPlacement(water, z, clearance, passClass: "ship"));
        Assert.Equal(PlacementResult.FailTerrain,
            pf.CheckUnitPlacement(water, z, clearance));                    // 陆军下水被拒
        Assert.Equal(PlacementResult.Success,
            pf.CheckUnitPlacement(land, z, clearance));
        Assert.Equal(PlacementResult.FailTerrain,
            pf.CheckUnitPlacement(land, z, clearance, passClass: "ship"));  // 船上岸被拒
    }

    [Fact]
    public void PickSpawnPoint_ShipClass_FindsWaterSlot()
    {
        var (cm, _) = SetupWaterBandWorld();
        // 码头近似:水带中央的持有者,环搜出生点。
        var dock = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(dock, pos);
        pos.Position = new FixedVector3D(Fixed.FromInt(64), Fixed.Zero, Fixed.FromInt(64));
        var fp = new FootprintComponent { MaxSpawnDistance = Fixed.FromInt(24) };
        cm.AddComponent(dock, fp);

        var spawn = fp.PickSpawnPoint(Fixed.FromInt(1), "ship");
        Assert.True(spawn.X.ToFloat() >= 0, "no water spawn point found for ship class");
        // 出生点必须落在水带内(通过船类放置检查)。
        Assert.True(spawn.X.ToFloat() >= 40f && spawn.X.ToFloat() <= 88f,
            $"ship spawn not on water: x={spawn.X.ToFloat():F1}");
    }

    [Fact]
    public void RealTemplate_ShipClass_Extracted_AndWired()
    {
        const string rel = "binaries/data/mods/public/simulation/templates";
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, rel)))
            dir = dir.Parent;
        if (dir == null) return;   // 数据树未拉取则跳过

        var cm = new ComponentManager(rngSeed: 1,
            templates: new Content.TemplateLoader(Path.Combine(dir.FullName, rel)));
        SimSystem.Init(cm);

        // template_unit_ship 显式 ship;athen 战舰经父链继承。
        Assert.Equal("ship", cm.Templates!.ExtractStats("template_unit_ship").PassabilityClass);
        Assert.Equal("ship", cm.Templates.ExtractStats("units/athen/ship_ram").PassabilityClass);
        // 陆军默认 default。
        Assert.Equal("default",
            cm.Templates.ExtractStats("units/spart/infantry_spearman_b").PassabilityClass);

        // 装配接线:SpawnEntity 出的船,UnitMotion.PassClassName = "ship"。
        var ship = cm.SpawnEntity("units/athen/ship_ram", 64, 64, ownerPlayerId: 1);
        Assert.Equal("ship", cm.QueryInterface<UnitMotion>(ship)!.PassClassName);
        var hoplite = cm.SpawnEntity("units/spart/infantry_spearman_b", 16, 16, ownerPlayerId: 1);
        Assert.Equal("default", cm.QueryInterface<UnitMotion>(hoplite)!.PassClassName);
    }
}
