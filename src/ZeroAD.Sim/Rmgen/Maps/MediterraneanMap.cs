using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>地中海地图（原版 maps/random/mediterranean.js,488 行——
    /// 高度图驱动:GEBCO 地形图 + 多气候区生物群系铺地)。降海平面 +
    /// 平滑高度图 + 水/陆标记 + 气候区铺生物群系。高度图缺失时回退
    /// 确定性径向渐变(测试/数据缺失环境)。</summary>
    public sealed class MediterraneanMap : StandardMap
    {
    protected override double HeightLand => 0;
        protected override string BaseTerrain => "medit_sand_wet";

        /// <summary>生成地图。返回 MapExport 供引擎消费。</summary>
        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            // 高度图(原版 LoadHeightmapImage("mediterranean.png", 0, 40) +
            // convertHeightmap1Dto2D;缺失回退确定性径向渐变——扫雷/模板测试
            // 的 dataRoot 不含 maps/)。
            float[][] heightmap = LoadMediterraneanHeightmap(settings.DataRoot);

            // 高度基准(原版 heightScale = Size/320)。
            int mapSize = settings.Size;
            double heightScale = mapSize / 320.0;
            double heightSeaGround = -6 * heightScale;
            double heightWaterLevel = 0 * heightScale;
            double heightShoreline = 0.5 * heightScale;

            var map = new RandomMap(rng, mapSize, heightWaterLevel,
                "medit_sand_wet", settings.CircularMap);
            RmgenLibrary.CurrentMap = map;
            var mapCenter = map.GetCenter();

            // TileClass(原版 g_TileClasses 八个气候区 + water/land/mountain/shoreline)。
            var clWater = new TileClass(mapSize);
            var clLand = new TileClass(mapSize);
            var clMountain = new TileClass(mapSize);
            var clShoreline = new TileClass(mapSize);
            var clNorthernEurope = new TileClass(mapSize);
            var clWesternEurope = new TileClass(mapSize);
            var clEasternEurope = new TileClass(mapSize);
            var clSouthernEurope = new TileClass(mapSize);
            var clAfrica = new TileClass(mapSize);
            var clPlayer = new TileClass(mapSize);
            var clForest = new TileClass(mapSize);
            var clDirt = new TileClass(mapSize);
            var clRock = new TileClass(mapSize);
            var clMetal = new TileClass(mapSize);

            // 高度图写入(原版 TILE_CENTERED_HEIGHT_MAP=true 的逐点刷)。
            int hmSize = heightmap.Length;
            for (int x = 0; x < mapSize; x++)
                for (int y = 0; y < mapSize; y++)
                {
                    int hx = Math.Min(x * hmSize / mapSize, hmSize - 1);
                    int hy = Math.Min(y * hmSize / mapSize, hmSize - 1);
                    map.SetHeight(new RmgenVector2D(x, y),
                        heightSeaGround + heightmap[hx][hy] / 0xFFFF * (40 * heightScale));
                }

            // 气候区(原版 climateZones 五带:北欧/西欧/东欧/南欧/非洲)。
            var bounds = new { Left = 0, Right = mapSize, Top = mapSize, Bottom = 0 };
            double northernTopLeftX = RmgenLibrary.FractionToTiles(0.3, mapSize);
            double northernTopLeftY = RmgenLibrary.FractionToTiles(0.7, mapSize);
            double westernTopLeftX = RmgenLibrary.FractionToTiles(0.7, mapSize);
            double westernTopLeftY = RmgenLibrary.FractionToTiles(0.47, mapSize);
            double africaTop = RmgenLibrary.FractionToTiles(0.33, mapSize);

            var climateZones = new[]
            {
                (TileClass: clNorthernEurope, P1: new RmgenVector2D(northernTopLeftX, bounds.Top),
                 P2: new RmgenVector2D(bounds.Right, northernTopLeftY), Biome: "generic/arctic",
                 Constraint: (IConstraint?)null),
                (TileClass: clWesternEurope, P1: new RmgenVector2D(bounds.Left, bounds.Top),
                 P2: new RmgenVector2D(westernTopLeftX, westernTopLeftY), Biome: "generic/temperate",
                 Constraint: (IConstraint?)RmgenLibrary.AvoidClasses(clNorthernEurope, 0)),
                (TileClass: clEasternEurope, P1: new RmgenVector2D(westernTopLeftX, bounds.Top),
                 P2: new RmgenVector2D(bounds.Right, westernTopLeftY), Biome: "generic/autumn",
                 Constraint: (IConstraint?)RmgenLibrary.AvoidClasses(clNorthernEurope, 0)),
                (TileClass: clSouthernEurope, P1: new RmgenVector2D(bounds.Left, africaTop),
                 P2: new RmgenVector2D(bounds.Right, westernTopLeftY), Biome: "generic/aegean",
                 Constraint: (IConstraint?)null),
                (TileClass: clAfrica, P1: new RmgenVector2D(bounds.Left, africaTop),
                 P2: new RmgenVector2D(bounds.Right, bounds.Bottom), Biome: "generic/sahara",
                 Constraint: (IConstraint?)null),
            };

            // 降海平面(原版:MapBoundsPlacer + SmoothElevationPainter ELEVATION_SET)。
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothElevationPainter(rng,
                    SmoothElevationPainter.SmoothType.Blurry, heightSeaGround, 2),
                new HeightConstraint(map, double.NegativeInfinity, heightWaterLevel));

            // 平滑高度图(原版:SmoothingPainter)。
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothingPainter(1,
                    RmgenLibrary.ScaleByMapSize(0.3, 0.8, mapSize), 1),
                null);

            // 水标记(原版:MapBoundsPlacer + TileClassPainter,水位以下)。
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TileClassPainter(clWater),
                new HeightConstraint(map, double.NegativeInfinity, heightWaterLevel));

            // 陆标记(原版:DiskPlacer 中心半图径,避水)。
            RmgenLibrary.CreateArea(
                new DiskPlacer(RmgenLibrary.FractionToTiles(0.5, mapSize), mapCenter),
                new TileClassPainter(clLand),
                RmgenLibrary.AvoidClasses(clWater, 0));

            // 气候区铺生物群系(原版:每区 RectPlacer 标类 + 地形刷漆)。
            foreach (var zone in climateZones)
            {
                var biome = BiomeLoader.Load(settings.DataRoot, zone.Biome, rng);
                RmgenLibrary.CreateArea(
                    new RectPlacer((int)zone.P1.X, (int)zone.P1.Y,
                        (int)zone.P2.X, (int)zone.P2.Y),
                    new TileClassPainter(zone.TileClass),
                    zone.Constraint);
                RmgenLibrary.CreateArea(
                    new RectPlacer((int)zone.P1.X, (int)zone.P1.Y,
                        (int)zone.P2.X, (int)zone.P2.Y),
                    new TerrainPainter(biome.MainTerrain0, rng),
                    new AndConstraint(
                        new HeightConstraint(map, heightWaterLevel, double.PositiveInfinity),
                        zone.Constraint ?? new NullConstraint()));
            }

            // 生物群系边界模糊(原版:分层斑块破气候区硬边)。
            foreach (var zone in climateZones)
            {
                var biome = BiomeLoader.Load(settings.DataRoot, zone.Biome, rng);
                RmgenCommon.CreateLayeredPatches(rng, map,
                    new[] { RmgenLibrary.ScaleByMapSize(3, 6, mapSize),
                            RmgenLibrary.ScaleByMapSize(5, 10, mapSize),
                            RmgenLibrary.ScaleByMapSize(8, 21, mapSize) },
                    new object[] { new object[] { biome.MainTerrain0, biome.Tier1Terrain },
                                   new object[] { biome.Tier1Terrain, biome.Tier2Terrain },
                                   new object[] { biome.Tier2Terrain, biome.Tier3Terrain } },
                    new[] { 1, 1 },
                    new AndConstraint(
                        RmgenLibrary.AvoidClasses(clForest, 2, clWater, 2, clMountain, 2,
                            clDirt, 5, clPlayer, 8),
                        RmgenLibrary.BorderClasses(zone.TileClass, 3, 3)),
                    (int)RmgenLibrary.ScaleByMapSize(20, 60, mapSize),
                    clDirt);
            }

            // 玩家布置(原版:非 Nomad 时 playerPlacementRandom 随机布置,
            // 每玩家 CC 区压平 + createBase)。
            if (!settings.Nomad)
            {
                var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementRandom(
                    rng, map, settings,
                    new AndConstraint(
                        RmgenLibrary.AvoidClasses(clMountain, 5),
                        RmgenLibrary.StayClasses(clLand,
                            RmgenLibrary.ScaleByMapSize(8, 25, mapSize)))) ?? (null!, null!);
                if (playerIDs != null)
                {
                    for (int i = 0; i < RmgenCommon.GetNumPlayers(settings); ++i)
                    {
                        // CC 区压平(原版:ClumpPlacer + SmoothElevationPainter 到玩家高度)。
                        RmgenLibrary.CreateArea(
                            new ClumpPlacer(rng,
                                (int)(RmgenCommon.DefaultPlayerBaseRadius(mapSize) * 0.8),
                                0.95, 0.6, double.PositiveInfinity, playerPosition[i]),
                            new SmoothElevationPainter(rng,
                                SmoothElevationPainter.SmoothType.Solid,
                                map.GetHeight(playerPosition[i]), 6),
                            null);
                    }
                    // 基地区按所在气候区 biome 刷(原版:setBiome 按 zone 所在);
                    // 显式位置版 PlacePlayerBases 一次全玩家(位置列表按序配对——
                    // 原版每玩家 zoneBiome 不同,按 optionsFactory 逐玩家换基地贴图)。
                    RmgenCommon.PlacePlayerBases(rng, map, settings,
                        BiomeLoader.Load(settings.DataRoot, "generic/aegean", rng).MainTerrain0,
                        clPlayer, null, playerPosition, null, null, playerIDs,
                        optionsFactory: pid =>
                        {
                            int idx = playerIDs.IndexOf(pid);
                            var pos = playerPosition[idx >= 0 ? idx : 0];
                            var zone = climateZones[0];
                            foreach (var z in climateZones)
                                if (z.TileClass.Has(pos)) { zone = z; break; }
                            return new RmgenCommon.PlayerBaseOptions();
                        });
                }
            }

            return map.MakeExportable();
        }

        /// <summary>高度图加载(mediterranean.png;缺失回退确定性径向渐变——
        /// 扫雷/模板测试的 dataRoot 不含 maps/)。</summary>
        private static float[][] LoadMediterraneanHeightmap(string? dataRoot)
        {
            string? path = dataRoot != null
                ? System.IO.Path.Combine(dataRoot, "maps", "random", "mediterranean.png")
                : null;
            if (path != null && System.IO.File.Exists(path))
                return HeightmapLoader.ConvertHeightmap1Dto2D(
                    HeightmapLoader.LoadHeightmapImage(path));

            const int n = 721;
            var hm = new float[n][];
            for (int x = 0; x < n; ++x)
            {
                hm[x] = new float[n];
                for (int y = 0; y < n; ++y)
                {
                    // 确定性径向渐变(测试/数据缺失环境;中心高四周低的海岸近似)。
                    double dx = x - (n - 1) / 2.0;
                    double dy = y - (n - 1) / 2.0;
                    double v = 0.7 - SafeMath.Sqrt(dx * dx + dy * dy) / (n / 2.0) * 0.8;
                    hm[x][y] = (float)(SafeMath.Max(0, v) * 0xFFFF);
                }
            }
            return hm;
        }
    }
}
