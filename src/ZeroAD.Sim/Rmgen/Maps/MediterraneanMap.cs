using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;
using E = ZeroAD.Sim.Rmgen.Common.Rmgen2Context.Element;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>mediterranean.js（逐字移植，488 行）——以 GEBCO 真实海拔（mediterranean.png）
    /// 为底的地中海：五个气候带（北欧/西欧/东欧/南欧/非洲）各用一套 biome 铺地与放资源，
    /// 玩家随机落在陆地上。高度图缺失时回退确定性径向渐变（测试/数据缺失环境）。
    /// placePlayersNomad 与环境设置（水色/天色/雾/PP）按既有移植约定省略。</summary>
    public sealed class MediterraneanMap : Rmgen2Map
    {
        private const string TWater = "medit_sand_wet";
        private static readonly string[] TSnowedRocks = { "alpine_cliff_b", "alpine_cliff_snow" };

        private double _heightSeaGround, _heightWaterLevel, _heightShoreline, _heightSnow;

        /// <summary>上游 setBiome("generic/aegean") 作为默认 biome（各气候带再各自覆盖）。</summary>
        protected override string? ForcedBiome => "generic/aegean";

        /// <summary>上游 new RandomMap(heightWaterLevel, ...)，heightWaterLevel = 0 * scale = 0。</summary>
        protected override double PickHeightLand(RmgenRng rng) => 0;

        protected override IReadOnlyList<string>? ExtraTileClasses => new[]
        {
            "autumn", "desert", "medit", "polar", "steppe", "temp", "shoreline",
            "africa", "northern_europe", "southern_europe", "western_europe", "eastern_europe",
        };

        /// <summary>land 类由高度/圆盘决定，不是全图刷。</summary>
        protected override bool PaintLandClass => false;

        private sealed class ClimateZone
        {
            public TileClass TileClass = null!;
            public RmgenVector2D Position1, Position2;
            public string BiomeId = "";
            public IConstraint Constraint = new NullConstraint();
            public BiomeSet Biome = null!;
            public Rmgen2Context Ctx = null!;
        }

        protected override void GenerateRmgen2()
        {
            var c = Ctx;

            double heightScale = MapSize / 320.0;
            _heightSeaGround = -6 * heightScale;
            _heightWaterLevel = 0 * heightScale;
            _heightShoreline = 0.5 * heightScale;
            _heightSnow = 10 * heightScale;

            LoadMediterraneanHeightmap();

            // 上游 mapBounds：left/bottom = 0，right/top = mapSize
            double boundsLeft = 0, boundsBottom = 0, boundsRight = MapSize, boundsTop = MapSize;

            var northernTopLeft = new RmgenVector2D(
                RmgenLibrary.FractionToTiles(0.3, MapSize),
                RmgenLibrary.FractionToTiles(0.7, MapSize));
            var westernTopLeft = new RmgenVector2D(
                RmgenLibrary.FractionToTiles(0.7, MapSize),
                RmgenLibrary.FractionToTiles(0.47, MapSize));
            double africaTop = RmgenLibrary.FractionToTiles(0.33, MapSize);

            var climateZones = new List<ClimateZone>
            {
                new()
                {
                    TileClass = c.Cl("northern_europe"),
                    Position1 = new RmgenVector2D(northernTopLeft.X, boundsTop),
                    Position2 = new RmgenVector2D(boundsRight, northernTopLeft.Y),
                    BiomeId = "generic/arctic",
                },
                new()
                {
                    TileClass = c.Cl("western_europe"),
                    Position1 = new RmgenVector2D(boundsLeft, boundsTop),
                    Position2 = westernTopLeft,
                    BiomeId = "generic/temperate",
                    Constraint = RmgenLibrary.AvoidClasses(c.Cl("northern_europe"), 0),
                },
                new()
                {
                    TileClass = c.Cl("eastern_europe"),
                    Position1 = new RmgenVector2D(westernTopLeft.X, boundsTop),
                    Position2 = new RmgenVector2D(boundsRight, westernTopLeft.Y),
                    BiomeId = "generic/autumn",
                    Constraint = RmgenLibrary.AvoidClasses(c.Cl("northern_europe"), 0),
                },
                new()
                {
                    TileClass = c.Cl("southern_europe"),
                    Position1 = new RmgenVector2D(boundsLeft, africaTop),
                    Position2 = new RmgenVector2D(boundsRight, westernTopLeft.Y),
                    BiomeId = "generic/aegean",
                },
                new()
                {
                    TileClass = c.Cl("africa"),
                    Position1 = new RmgenVector2D(boundsLeft, africaTop),
                    Position2 = new RmgenVector2D(boundsRight, boundsBottom),
                    BiomeId = "generic/sahara",
                },
            };

            foreach (var zone in climateZones)
            {
                zone.Biome = BiomeLoader.Load(Settings.DataRoot, zone.BiomeId, Rng);
                zone.Ctx = c.WithBiome(zone.Biome, zone.BiomeId);
            }

            // 降海平面
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                    _heightSeaGround, 2),
                new HeightConstraint(Map, double.NegativeInfinity, _heightWaterLevel));

            // 高度图平滑
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothingPainter(1, RmgenLibrary.ScaleByMapSize(0.3, 0.8, MapSize), 1),
                null);

            // 水标记
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TileClassPainter(c.ClWater),
                new HeightConstraint(Map, double.NegativeInfinity, _heightWaterLevel));

            // 陆标记（图心半径 0.5 图宽的圆盘内、非水）
            RmgenLibrary.CreateArea(
                new DiskPlacer(RmgenLibrary.FractionToTiles(0.5, MapSize), c.MapCenter),
                new TileClassPainter(c.ClLand),
                RmgenLibrary.AvoidClasses(c.ClWater, 0));

            // 气候带标记 + 铺地
            foreach (var zone in climateZones)
            {
                RmgenLibrary.CreateArea(RectOf(zone),
                    new TileClassPainter(zone.TileClass),
                    zone.Constraint);

                RmgenLibrary.CreateArea(RectOf(zone),
                    new TerrainPainter(zone.Biome.MainTerrain, Rng),
                    new AndConstraint(new IConstraint[]
                    {
                        new HeightConstraint(Map, _heightWaterLevel, double.PositiveInfinity),
                        zone.Constraint,
                    }));
            }

            // 气候带边界模糊
            foreach (var zone in climateZones)
                RmgenCommon.CreateLayeredPatches(Rng, Map,
                    new[]
                    {
                        RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                        RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                        RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
                    },
                    new object[]
                    {
                        new object[] { zone.Biome.MainTerrain, zone.Biome.Tier1Terrain },
                        new object[] { zone.Biome.Tier1Terrain, zone.Biome.Tier2Terrain },
                        new object[] { zone.Biome.Tier2Terrain, zone.Biome.Tier3Terrain },
                    },
                    new[] { 1, 1 },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(c.ClForest, 2, c.ClWater, 2, c.ClMountain, 2,
                            c.ClDirt, 5, c.ClPlayer, 8),
                        RmgenLibrary.BorderClasses(zone.TileClass, 3, 3),
                    }),
                    (int)RmgenLibrary.ScaleByMapSize(20, 60, MapSize),
                    c.ClDirt);

            // 玩家布置：随机落点 + 基地区压平 + 逐玩家按所在气候带建基地
            if (!Settings.Nomad)
            {
                double baseRadius = RmgenCommon.DefaultPlayerBaseRadius(MapSize);
                var placement = RmgenCommon.PlayerPlacementRandom(Rng, Map, Settings,
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(c.ClMountain, 5),
                        RmgenLibrary.StayClasses(c.ClLand,
                            RmgenLibrary.ScaleByMapSize(8, 25, MapSize)),
                    }));

                if (placement.HasValue)
                {
                    var (playerIDs, playerPosition) = placement.Value;
                    for (int i = 0; i < NumPlayers; ++i)
                    {
                        var zoneCtx = ZoneAt(climateZones, playerPosition[i]).Ctx;

                        RmgenLibrary.CreateArea(
                            new ClumpPlacer(Rng, RmgenGeometry.DiskArea(baseRadius * 0.8),
                                0.95, 0.6, double.PositiveInfinity, playerPosition[i]),
                            new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                                Map.GetHeight(playerPosition[i]), 6),
                            null);

                        // createBase(playerID, position, mapSize >= 384)
                        zoneCtx.CreateBase(playerIDs[i], playerPosition[i], MapSize >= 384);
                    }
                }
            }

            // 逐气候带：岸线 / 崖壁 / 资源
            foreach (var zone in climateZones)
            {
                var zc = zone.Ctx;
                var zb = zone.Biome;

                RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                    new IPainter[]
                    {
                        new TerrainPainter(zb.Shore, Rng),
                        new TileClassPainter(c.Cl("shoreline")),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.StayClasses(zone.TileClass, 0),
                        new HeightConstraint(Map, double.NegativeInfinity, _heightShoreline),
                    }));

                RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                    new IPainter[]
                    {
                        new TerrainPainter(zb.Cliff, Rng),
                        new TileClassPainter(c.ClMountain),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.StayClasses(zone.TileClass, 0),
                        RmgenLibrary.AvoidClasses(c.ClWater, 2),
                        new SlopeConstraint(Map, 2, double.PositiveInfinity),
                    }));

                zc.AddElements(new[]
                {
                    new E
                    {
                        Func = (cs, s, d, f, _) => zc.AddMetal(cs, s, d, f),
                        Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 2,
                            c.ClPlayer, 30, c.ClRock, 10, c.ClMetal, 25, c.ClWater, 4 },
                        Stay = new object[] { zone.TileClass, 0 },
                        Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                        Amounts = new[] { "many" },
                    },
                    new E
                    {
                        Func = (cs, s, d, f, _) => zc.AddStone(cs, s, d, f),
                        Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 2,
                            c.ClPlayer, 30, c.ClRock, 10, c.ClMetal, 25, c.ClWater, 4 },
                        Stay = new object[] { zone.TileClass, 0 },
                        Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                        Amounts = new[] { "many" },
                    },
                    new E
                    {
                        Func = (cs, s, d, f, _) => zc.AddForests(cs, s, d, f),
                        Avoid = new object[] { c.ClBerries, 3, c.ClForest, 15, c.ClMetal, 3,
                            c.ClMountain, 2, c.ClPlayer, 12, c.ClRock, 2, c.ClWater, 2 },
                        Stay = new object[] { zone.TileClass, 0 },
                        Sizes = new[] { "normal" }, Mixes = new[] { "normal" },
                        Amounts = new[] { "normal" },
                    },
                    new E
                    {
                        Func = (cs, s, d, f, _) => zc.AddSmallMetal(cs, s, d, f),
                        Avoid = new object[] { c.ClBerries, 5, c.ClForest, 3, c.ClMountain, 2,
                            c.ClPlayer, 30, c.ClRock, 10, c.ClMetal, 15, c.ClWater, 4 },
                        Stay = new object[] { zone.TileClass, 0 },
                        Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                        Amounts = new[] { "few", "normal", "many" },
                    },
                    new E
                    {
                        Func = (cs, s, d, f, _) => zc.AddBerries(cs, s, d, f),
                        Avoid = new object[] { c.ClBerries, 30, c.ClForest, 2, c.ClMetal, 4,
                            c.ClMountain, 2, c.ClPlayer, 20, c.ClRock, 4, c.ClWater, 2 },
                        Stay = new object[] { zone.TileClass, 0 },
                        Sizes = new[] { "normal" }, Mixes = new[] { "normal" },
                        Amounts = new[] { "many" },
                    },
                    new E
                    {
                        Func = (cs, s, d, f, _) => zc.AddAnimals(cs, s, d, f),
                        Avoid = new object[] { c.ClAnimals, 10, c.ClForest, 1, c.ClMetal, 2,
                            c.ClMountain, 1, c.ClPlayer, 15, c.ClRock, 2, c.ClWater, 3 },
                        Stay = new object[] { zone.TileClass, 0 },
                        Sizes = new[] { "normal" }, Mixes = new[] { "normal" },
                        Amounts = new[] { "many" },
                    },
                    new E
                    {
                        Func = (cs, s, d, f, _) => zc.AddAnimals(cs, s, d, f),
                        Avoid = new object[] { c.ClAnimals, 10, c.ClForest, 1, c.ClMetal, 2,
                            c.ClMountain, 1, c.ClPlayer, 15, c.ClRock, 2, c.ClWater, 1 },
                        Stay = new object[] { zone.TileClass, 0 },
                        Sizes = new[] { "small" }, Mixes = new[] { "normal" },
                        Amounts = new[] { "tons" },
                    },
                    new E
                    {
                        Func = (cs, s, d, f, _) => zc.AddStragglerTrees(cs, s, d, f),
                        Avoid = new object[] { c.ClBerries, 5, c.ClForest, 5, c.ClMetal, 2,
                            c.ClMountain, 1, c.ClPlayer, 12, c.ClRock, 2, c.ClWater, 3 },
                        Stay = new object[] { zone.TileClass, 0 },
                        Sizes = new[] { "normal" }, Mixes = new[] { "normal" },
                        // 上游写的 "some" 不在量词表里，pickAmount 回退 normal——照搬
                        Amounts = new[] { "some" },
                    },
                    new E
                    {
                        Func = (cs, s, d, f, _) => zc.AddLayeredPatches(cs, s, d, f),
                        Avoid = new object[] { c.ClDirt, 5, c.ClForest, 2, c.ClMountain, 2,
                            c.ClPlayer, 12, c.ClWater, 3 },
                        Stay = new object[] { zone.TileClass, 0 },
                        Sizes = new[] { "normal" }, Mixes = new[] { "normal" },
                        Amounts = new[] { "tons" },
                    },
                    new E
                    {
                        Func = (cs, s, d, f, _) => zc.AddDecoration(cs, s, d, f),
                        Avoid = new object[] { c.ClForest, 2, c.ClMountain, 2, c.ClPlayer, 12,
                            c.ClWater, 4 },
                        Stay = new object[] { zone.TileClass, 0 },
                        Sizes = new[] { "small" }, Mixes = new[] { "same" },
                        Amounts = new[] { "normal" },
                    },
                });
            }

            // 水面刷漆
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TerrainPainter(TWater, Rng),
                new HeightConstraint(Map, double.NegativeInfinity, _heightWaterLevel));

            // 高山雪线
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TerrainPainter(TSnowedRocks, Rng),
                new AndConstraint(new IConstraint[]
                {
                    new HeightConstraint(Map, _heightSnow, double.PositiveInfinity),
                    RmgenLibrary.AvoidClasses(c.Cl("africa"), 0, c.Cl("southern_europe"), 0,
                        c.ClPlayer, 6),
                }));

            // 鱼群（上游临时改 g_Gaia.fish）
            var fishBiome = Biome.Clone();
            fishBiome.Fish = "gaia/fish/generic";
            var fishCtx = c.WithBiome(fishBiome, BiomeName);
            fishCtx.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => fishCtx.AddFish(cs, s, d, f),
                    Avoid = new object[] { c.ClFish, 10 },
                    Stay = new object[] { c.ClWater, 4 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "similar" },
                    Amounts = new[] { "many" },
                },
            });

            // 鲸鱼（同上，换模板）
            var whaleBiome = Biome.Clone();
            whaleBiome.Fish = "gaia/fauna_whale_fin";
            var whaleCtx = c.WithBiome(whaleBiome, BiomeName);
            whaleCtx.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => whaleCtx.AddFish(cs, s, d, f),
                    Avoid = new object[] { c.ClFish, 2, c.Cl("desert"), 50, c.Cl("steppe"), 50 },
                    Stay = new object[] { c.ClWater, 7 },
                    Sizes = new[] { "small" }, Mixes = new[] { "same" },
                    Amounts = new[] { "scarce" },
                },
            });
        }

        private static ClimateZone ZoneAt(List<ClimateZone> zones, RmgenVector2D position)
        {
            foreach (var zone in zones)
                if (zone.TileClass.Has(position))
                    return zone;
            // 上游 climateZones.find(...) 找不到会崩；本版退回南欧带（默认 aegean）
            return zones[3];
        }

        private static RectPlacer RectOf(ClimateZone zone)
            => new((int)zone.Position1.X, (int)zone.Position1.Y,
                (int)zone.Position2.X, (int)zone.Position2.Y);

        /// <summary>g_Map.LoadHeightmapImage("mediterranean.png", 0, 40)。
        /// 缺失时回退确定性径向渐变（扫雷/模板测试的 dataRoot 不含 maps/）。</summary>
        private void LoadMediterraneanHeightmap()
        {
            string? path = Settings.DataRoot != null
                ? System.IO.Path.Combine(Settings.DataRoot, "maps", "random", "mediterranean.png")
                : null;

            float[][] heightmap;
            if (path != null && System.IO.File.Exists(path))
                heightmap = HeightmapLoader.ConvertHeightmap1Dto2D(
                    HeightmapLoader.LoadHeightmapImage(path));
            else
                heightmap = FallbackHeightmap();

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new HeightmapPainter(Map, heightmap, 0, 40), null);
        }

        private static float[][] FallbackHeightmap()
        {
            const int n = 721;
            var hm = new float[n][];
            for (int x = 0; x < n; ++x)
            {
                hm[x] = new float[n];
                for (int y = 0; y < n; ++y)
                {
                    // 中心高四周低的海岸近似
                    double dx = x - (n - 1) / 2.0;
                    double dy = y - (n - 1) / 2.0;
                    double v = 0.7 - SafeMath.Sqrt(dx * dx + dy * dy) / (n / 2.0) * 0.8;
                    hm[x][y] = (float)(Math.Max(0, v) * 0xFFFF);
                }
            }
            return hm;
        }
    }
}
