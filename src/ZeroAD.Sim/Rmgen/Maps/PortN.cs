using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>flood.js（336 行）——洪水图：玩家起始小岛、中央高地与周边低洼林地。
    /// flood_triggers.js 不在本批范围；脚本内为触发器准备的初始水下地形与实体照常生成。
    /// SupportedBiomes 为 flood.json 显式七项；环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class FloodMap2 : StandardMap
    {
        protected override double HeightLand => -2;

        /// <summary>上游 flood.json SupportedBiomes 显式七项。</summary>
        protected override IReadOnlyList<string> SupportedBiomes => BiomeLoader.SevenGenericBiomes;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.Water, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;

            var tMainTerrain = biome.MainTerrain;
            string tForestFloor1 = biome.ForestFloor1;
            string tForestFloor2 = biome.ForestFloor2;
            var tCliff = biome.Cliff;
            string tTier1Terrain = biome.Tier1Terrain;
            string tTier2Terrain = biome.Tier2Terrain;
            string tTier3Terrain = biome.Tier3Terrain;
            string tRoad = biome.Road;
            string tRoadWild = biome.RoadWild;
            string tTier4Terrain = biome.Tier4Terrain;
            string tShore = biome.Shore;

            object tHillLocal = biome.Hill;
            object tDirtLocal = biome.Dirt;
            if (BiomeName == "generic/temperate")
            {
                tDirtLocal = new[] { "medit_shrubs_a", "grass_field" };
                tHillLocal = new[] { "grass_field", "peat_temp" };
            }

            string oTree1 = biome.Tree1;
            string oTree2 = biome.Tree2;
            string oTree3 = biome.Tree3;
            string oTree4 = biome.Tree4;
            string oTree5 = biome.Tree5;
            string oFruitBush = biome.FruitBush;
            string oMainHuntableAnimal = biome.MainHuntableAnimal;
            string oFish = biome.Fish;
            string oSecondaryHuntableAnimal = biome.SecondaryHuntableAnimal;
            string oStoneLarge = biome.StoneLarge;
            string oMetalLarge = biome.MetalLarge;

            string aGrass = biome.Grass;
            string aGrassShort = biome.GrassShort;
            string aRockLarge = biome.RockLarge;
            string aRockMedium = biome.RockMedium;
            string aBushMedium = biome.BushMedium;
            string aBushSmall = biome.BushSmall;

            var pForest1 = new[]
            {
                tForestFloor2 + "|" + oTree1,
                tForestFloor2 + "|" + oTree2,
                tForestFloor2,
            };
            var pForest2 = new[]
            {
                tForestFloor1 + "|" + oTree4,
                tForestFloor1 + "|" + oTree5,
                tForestFloor1,
            };

            const double heightSeaGround = -2;
            const double heightLand = 6;
            const double heightStartingIslands = 2;
            const double shoreRadius = 6;

            var clMountain = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var mapCenter = map.GetCenter();

            var (playerIDs, playerPosition, _, _) = RmgenCommon.PlayerPlacementCircle(
                rng, map, NumPlayers, RmgenLibrary.FractionToTiles(0.38, MapSize));

            for (int i = 0; i < NumPlayers; ++i)
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng,
                        RmgenGeometry.DiskArea(1.4 * RmgenCommon.DefaultPlayerBaseRadius(MapSize)),
                        0.8, 0.1, double.PositiveInfinity, playerPosition[i]),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tShore, tMainTerrain },
                            new double[] { shoreRadius }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            heightStartingIslands, shoreRadius),
                        new TileClassPainter(ClHill),
                    },
                    null);

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                playerPosition, tRoadWild, tRoad, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = oFruitBush,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneLarge, (string?)null, (object?)null),
                    },
                    TreesTemplate = oTree2,
                    TreesCount = 50,
                    TreesMaxDist = 16,
                    TreesMaxDistGroup = 7,
                    DecorativesTemplate = aGrassShort,
                });

            RmgenLibrary.CreateArea(
                new ChainPlacer(rng, 6,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(10, 15, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(200, 300, MapSize)),
                    double.PositiveInfinity, mapCenter, 0,
                    new[] { (int)Math.Floor(RmgenLibrary.FractionToTiles(0.01, MapSize)) }),
                new IPainter[]
                {
                    // 上游给了两个 terrain 却传两个 width；第三层永远应继续用主地表。
                    new LayeredPainter(new object[] { tShore, tMainTerrain, tMainTerrain },
                        new double[] { shoreRadius, 100 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightLand, shoreRadius),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 40));

            for (int m = 0; m < rng.RandIntInclusive(20, 34); ++m)
            {
                int elevRand = rng.RandIntInclusive(6, 20);
                RmgenLibrary.CreateArea(
                    new ChainPlacer(rng, 7, 15,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(15, 20, MapSize)),
                        double.PositiveInfinity,
                        new RmgenVector2D(
                            RmgenLibrary.FractionToTiles(rng.RandFloat(0, 1), MapSize),
                            RmgenLibrary.FractionToTiles(rng.RandFloat(0, 1), MapSize)),
                        0, new[] { (int)Math.Floor(RmgenLibrary.FractionToTiles(0.01, MapSize)) }),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tDirtLocal, tHillLocal },
                            new double[] { Math.Floor(elevRand / 3.0), 40 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            elevRand, Math.Floor(elevRand / 3.0)),
                        new TileClassPainter(ClHill),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClBaseResource, 2, ClPlayer, 40),
                        RmgenLibrary.StayClasses(ClHill, 6),
                    }));
            }

            for (int m = 0; m < rng.RandIntInclusive(8, 17); ++m)
            {
                int elevRand = rng.RandIntInclusive(15, 29);
                RmgenLibrary.CreateArea(
                    new ChainPlacer(rng, 5, 8,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(15, 20, MapSize)),
                        double.PositiveInfinity,
                        new RmgenVector2D(rng.RandIntExclusive(0, MapSize),
                            rng.RandIntExclusive(0, MapSize)),
                        0, new[] { (int)Math.Floor(RmgenLibrary.FractionToTiles(0.01, MapSize)) }),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tCliff, tForestFloor2 },
                            new double[] { Math.Floor(elevRand / 3.0), 40 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                            elevRand, Math.Floor(elevRand / 3.0), relative: true),
                        new TileClassPainter(clMountain),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClBaseResource, 2, ClPlayer, 40),
                        RmgenLibrary.StayClasses(ClHill, 6),
                    }));
            }

            RmgenLibrary.CreateObjectGroup(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oMetalLarge, 3, 6, 25,
                        Math.Floor(RmgenLibrary.FractionToTiles(0.25, MapSize))),
                }, true, ClBaseResource, mapCenter),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClBaseResource, 20, ClPlayer, 40, clMountain, 4),
                    RmgenLibrary.StayClasses(ClHill, 10),
                }));

            RmgenLibrary.CreateObjectGroup(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneLarge, 3, 6, 25,
                        Math.Floor(RmgenLibrary.FractionToTiles(0.25, MapSize))),
                }, true, ClBaseResource, mapCenter),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClBaseResource, 20, ClPlayer, 40, clMountain, 4),
                    RmgenLibrary.StayClasses(ClHill, 10),
                }));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oFish, 2, 3, 0, 2),
                }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(ClHill, 10, clFood, 20),
                10 * NumPlayers, 60);

            double forestTreesMainIsland = biome.ForestProbability *
                RmgenLibrary.ScaleByMapSize(biome.TreesMin * 0.7, biome.TreesMax * 0.7, MapSize);
            double stragglerTreesMainIsland = (1 - biome.ForestProbability) *
                RmgenLibrary.ScaleByMapSize(biome.TreesMin * 0.7, biome.TreesMax * 0.7, MapSize);
            GaiaEntities.CreateForests(rng, map,
                new object[] { tMainTerrain, tForestFloor1, tForestFloor2, pForest1, pForest2 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 25, ClForest, 10, ClBaseResource, 3,
                        ClMetal, 6, ClRock, 6, clMountain, 2),
                    RmgenLibrary.StayClasses(ClHill, 6),
                }),
                ClForest, forestTreesMainIsland, NumPlayers);

            var treeTypes = new[] { oTree1, oTree2, oTree4, oTree3 };
            GaiaEntities.CreateStragglerTrees(rng, treeTypes,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClBaseResource, 2, ClMetal, 6, ClRock, 6,
                        clMountain, 2, ClPlayer, 25),
                    RmgenLibrary.StayClasses(ClHill, 6),
                }),
                ClForest, stragglerTreesMainIsland);

            int numb = BiomeName == "generic/savanna" ? 3 : 1;
            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        size, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new object[] { tMainTerrain, tTier1Terrain },
                            new object[] { tTier1Terrain, tTier2Terrain },
                            new object[] { tTier2Terrain, tTier3Terrain },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, clMountain, 0, ClDirt, 5, ClPlayer, 10),
                    numb * RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, heightStartingIslands,
                HeightPlacer.Mode.ExcludeMinExcludeMax, tMainTerrain);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightSeaGround, 1,
                HeightPlacer.Mode.IncludeMinIncludeMax, tTier1Terrain);

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        size, 0.5),
                    new IPainter[] { new TerrainPainter(tTier4Terrain, rng) },
                    RmgenLibrary.AvoidClasses(ClForest, 0, clMountain, 0, ClDirt, 5, ClPlayer, 10),
                    numb * RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oMainHuntableAnimal, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, oSecondaryHuntableAnimal, 2, 3, 0, 2) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, clMountain, 1,
                        clFood, 4, ClRock, 6, ClMetal, 6),
                    RmgenLibrary.StayClasses(ClHill, 6),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oFruitBush, 5, 7, 0, 4) },
                },
                new double[] { 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 15, clMountain, 1,
                        clFood, 4, ClRock, 6, ClMetal, 6),
                    RmgenLibrary.StayClasses(ClHill, 6),
                }),
                clFood);

            int planetm = BiomeName == "generic/india" ? 8 : 1;
            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                        new ScatterObject(rng, aRockMedium, 1, 3, 0, 2),
                    },
                    new IGroupElement[] { new ScatterObject(rng, aGrassShort, 2, 15, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aGrass, 2, 10, 0, 1.8),
                        new ScatterObject(rng, aGrassShort, 3, 10, 1.2, 2.5),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aBushMedium, 1, 5, 0, 2),
                        new ScatterObject(rng, aBushSmall, 2, 4, 0, 2),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, settings.CircularMap),
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                },
                RmgenLibrary.AvoidClasses(ClForest, 2, ClPlayer, 20, clMountain, 5,
                    clFood, 1, ClBaseResource, 2));

            double forestTreesSurrounding = biome.ForestProbability *
                RmgenLibrary.ScaleByMapSize(biome.TreesMin * 0.1, biome.TreesMax * 0.1, MapSize);
            GaiaEntities.CreateForests(rng, map,
                new object[] { tMainTerrain, tForestFloor1, tForestFloor2, pForest1, pForest2 },
                RmgenLibrary.AvoidClasses(ClPlayer, 30, ClHill, 10, clFood, 5),
                ClForest, forestTreesSurrounding, NumPlayers);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aGrassShort, 1, 2, 0, 1),
                }),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clMountain, 2, ClPlayer, 2, ClDirt, 0),
                    RmgenLibrary.StayClasses(ClHill, 8),
                }),
                planetm * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            return map.MakeExportable();
        }
    }

    /// <summary>rhine_marshlands.js（334 行）——莱茵沼泽：温带草地上反复链式切出泥水沼泽，
    /// 芦苇、水草、橡榉/橡树林和泥斑共同形成交错湿地。无 biome；环境设置与
    /// placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class RhineMarshlandsMap2 : StandardMap
    {
        private static readonly string[] tGrass = { "temp_grass", "temp_grass", "temp_grass_d" };
        private const string tForestFloor = "temp_plants_bog";
        private const string tGrassA = "temp_grass_plants";
        private const string tGrassB = "temp_plants_bog";
        private const string tMud = "temp_mud_a";
        private const string tRoad = "temp_road";
        private const string tRoadWild = "temp_road_overgrown";
        private const string tShoreBlend = "temp_grass_plants";
        private const string tShore = "temp_plants_bog";
        private const string tWater = "temp_mud_a";

        private const string oBeech = "gaia/tree/euro_beech";
        private const string oOak = "gaia/tree/oak";
        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oDeer = "gaia/fauna_deer";
        private const string oHorse = "gaia/fauna_horse";
        private const string oWolf = "gaia/fauna_wolf";
        private const string oRabbit = "gaia/fauna_rabbit";
        private const string oStoneLarge = "gaia/rock/temperate_large";
        private const string oStoneSmall = "gaia/rock/temperate_small";
        private const string oMetalLarge = "gaia/ore/temperate_large";

        private const string aGrass = "actor|props/flora/grass_soft_small_tall.xml";
        private const string aGrassShort = "actor|props/flora/grass_soft_large.xml";
        private const string aRockLarge = "actor|geology/stone_granite_med.xml";
        private const string aRockMedium = "actor|geology/stone_granite_med.xml";
        private const string aReeds = "actor|props/flora/reeds_pond_lush_a.xml";
        private const string aLillies = "actor|props/flora/water_lillies.xml";
        private const string aBushMedium = "actor|props/flora/bush_medit_me.xml";
        private const string aBushSmall = "actor|props/flora/bush_medit_sm.xml";

        protected override double HeightLand => 1;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tGrass);
            var map = Map;

            const double heightMarsh = -2;
            const double heightOffsetBumpWater = 1;
            const double heightOffsetBumpLand = 2;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, null, RmgenLibrary.FractionToTiles(0.35, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize), rng.RandomAngle());

            RmgenCommon.PlacePlayerBases(rng, map, settings, tGrass[0], ClPlayer, null,
                playerPosition, tRoadWild, tRoad, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = oBerryBush,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneLarge, (string?)null, (object?)null),
                    },
                    TreesTemplate = oBeech,
                    DecorativesTemplate = aGrassShort,
                });

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize),
                    0.3, 0.06, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBumpLand, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 13),
                RmgenLibrary.ScaleByMapSize(300, 800, MapSize));

            for (int i = 0; i < 7; ++i)
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(6, 12, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(15, 60, MapSize)), 0.8),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tShoreBlend, tShore, tWater },
                            new[] { 1, 1 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            heightMarsh, 3),
                        new TileClassPainter(clWater),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, clWater,
                        SafeMath.Round(RmgenLibrary.ScaleByMapSize(7, 16, MapSize) *
                            rng.RandFloat(0.8, 1.35))),
                    RmgenLibrary.ScaleByMapSize(4, 20, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aReeds, 5, 10, 0, 4),
                    new ScatterObject(rng, aLillies, 5, 10, 0, 4),
                }, true),
                0, RmgenLibrary.StayClasses(clWater, 1),
                RmgenLibrary.ScaleByMapSize(400, 2000, MapSize), 100);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize),
                    0.3, 0.06, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBumpWater, 2, relative: true),
                },
                RmgenLibrary.StayClasses(clWater, 2),
                RmgenLibrary.ScaleByMapSize(50, 100, MapSize));

            double forestTrees = 0.7 * RmgenLibrary.ScaleByMapSize(500, 2500, MapSize);
            double stragglerTrees = (1 - 0.7) * RmgenLibrary.ScaleByMapSize(500, 2500, MapSize);
            var pForestD = new[] { tForestFloor + "|" + oBeech, tForestFloor };
            var pForestP = new[] { tForestFloor + "|" + oOak, tForestFloor };
            var forestTypes = new[]
            {
                new object[] { new object[] { tForestFloor, tGrass, pForestD }, new object[] { tForestFloor, pForestD } },
                new object[] { new object[] { tForestFloor, tGrass, pForestP }, new object[] { tForestFloor, pForestP } },
            };
            double forestSize = forestTrees / (RmgenLibrary.ScaleByMapSize(3, 6, MapSize) * NumPlayers);
            double forestNum = Math.Floor(forestSize / forestTypes.Length);
            foreach (var type in forestTypes)
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        forestTrees / (forestNum * Math.Floor(RmgenLibrary.ScaleByMapSize(2, 4, MapSize))),
                        double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, clWater, 0, ClForest, 10),
                    forestNum);

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        size, double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tGrassA, tGrassB, tMud },
                            new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClDirt, 5, ClPlayer, 8),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                }, true, ClRock),
                0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 1, ClPlayer, 20, ClRock, 10),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3),
                }, true, ClRock),
                0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 1, ClPlayer, 20, ClRock, 10),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4),
                }, true, ClMetal),
                0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 1, ClPlayer, 20,
                    ClMetal, 10, ClRock, 5),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aRockMedium, 1, 3, 0, 1),
                }, true),
                0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                    new ScatterObject(rng, aRockMedium, 1, 3, 0, 2),
                }, true),
                0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oDeer, 5, 7, 0, 4) },
                    true, clFood),
                0, RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 20, clFood, 13),
                6 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oHorse, 1, 3, 0, 4) },
                    true, clFood),
                0, RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 20, clFood, 13),
                3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oRabbit, 5, 7, 0, 2) },
                    true, clFood),
                0, RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 20, clFood, 13),
                6 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oWolf, 1, 3, 0, 4) },
                    true, clFood),
                0, RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 20, clFood, 13),
                3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oBerryBush, 5, 7, 0, 4) },
                    true, clFood),
                0, RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, clFood, 10),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oOak, oBeech },
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 13, ClMetal, 6, ClRock, 6,
                    clWater, 0),
                ClForest, stragglerTrees);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aGrassShort, 1, 2, 0, 1),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 13, ClDirt, 0),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aGrass, 2, 4, 0, 1.8),
                    new ScatterObject(rng, aGrassShort, 3, 6, 1.2, 2.5),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClPlayer, 13, ClDirt, 1, ClForest, 0),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aBushMedium, 1, 2, 0, 2),
                    new ScatterObject(rng, aBushSmall, 2, 4, 0, 2),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClPlayer, 13, ClDirt, 1),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            return map.MakeExportable();
        }
    }

    /// <summary>northern_lights.js（307 行）——北极光：横贯冰海、雪岸、冰湖、小岛、
    /// 冰山和海象/北极狼。无 biome；环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class NorthernLightsMap2 : StandardMap
    {
        private static readonly string[] tSnowA = { "polar_snow_b" };
        private const string tSnowB = "polar_ice_snow";
        private const string tSnowC = "polar_ice";
        private const string tSnowD = "polar_snow_a";
        private const string tForestFloor = "polar_tundra_snow";
        private const string tCliff = "polar_snow_rocks";
        private static readonly string[] tSnowE = { "polar_snow_glacial" };
        private const string tRoad = "new_alpine_citytile";
        private const string tRoadWild = "new_alpine_citytile";
        private const string tShoreBlend = "alpine_shore_rocks_icy";
        private const string tShore = "alpine_shore_rocks";
        private const string tWater = "alpine_shore_rocks";

        private const string oPine = "gaia/tree/pine_w";
        private const string oStoneLarge = "gaia/rock/alpine_large";
        private const string oStoneSmall = "gaia/rock/alpine_small";
        private const string oMetalLarge = "gaia/ore/alpine_large";
        private const string oFish = "gaia/fish/generic";
        private const string oWalrus = "gaia/fauna_walrus";
        private const string oArcticWolf = "gaia/fauna_wolf_arctic";

        private const string aIceberg = "actor|props/special/eyecandy/iceberg.xml";

        protected override double HeightLand => 3;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tSnowA);
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightSeaGround = -5;
            const double heightLake = -4;
            const double heightLand = 3;
            const double heightHill = 25;

            var clWater = new TileClass(MapSize);
            var clIsland = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);

            double startAngle = rng.RandomAngle();

            var playerIDs = RmgenCommon.SortAllPlayers(rng, settings);
            var playerPosition = PlayerPlacementLine(map, NumPlayers, 0,
                new RmgenVector2D(mapCenter.X, RmgenLibrary.FractionToTiles(0.45, MapSize)),
                RmgenLibrary.FractionToTiles(0.2, MapSize));
            for (int i = 0; i < playerPosition.Count; ++i)
            {
                var pos = playerPosition[i];
                pos.RotateAround(startAngle, mapCenter);
                playerPosition[i] = pos;
            }

            RmgenCommon.PlacePlayerBases(rng, map, settings, tSnowA[0], ClPlayer, null,
                playerPosition, tRoadWild, tRoad, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneLarge, (string?)null, (object?)null),
                    },
                    TreesTemplate = oPine,
                    TreesCount = (int)RmgenLibrary.ScaleByMapSize(12, 30, MapSize),
                });

            var riverStart = new RmgenVector2D(0, MapSize);
            riverStart.RotateAround(startAngle, mapCenter);
            var riverEnd = new RmgenVector2D(MapSize, MapSize);
            riverEnd.RotateAround(startAngle, mapCenter);
            RmgenCommon.PaintRiver(rng, map, riverStart, riverEnd,
                2 * RmgenLibrary.FractionToTiles(0.31, MapSize), 8,
                heightSeaGround, heightLand,
                parallel: true, deviation: 0, meanderShort: 0, meanderLong: 0);

            RmgenLibrary.PaintTileClassBasedOnHeight(double.NegativeInfinity, 0.5,
                HeightPlacer.Mode.ExcludeMinExcludeMax, clWater);

            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(20, 120, MapSize); ++i)
            {
                var position = new RmgenVector2D(
                    RmgenLibrary.FractionToTiles(rng.RandFloat(0.1, 0.9), MapSize),
                    RmgenLibrary.FractionToTiles(rng.RandFloat(0.67, 0.74), MapSize));
                position.RotateAround(startAngle, mapCenter);
                position.Round();

                RmgenLibrary.CreateArea(
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(16, 30, MapSize)),
                        double.PositiveInfinity, position),
                    new IPainter[]
                    {
                        new TerrainPainter(tSnowA, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            heightLand, 3),
                        new TileClassUnPainter(clWater),
                    },
                    null);
            }

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.1),
                new IPainter[]
                {
                    new TerrainPainter(tSnowA, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightLand, 3),
                    new TileClassPainter(clIsland),
                    new TileClassUnPainter(clWater),
                },
                RmgenLibrary.StayClasses(clWater, 7),
                RmgenLibrary.ScaleByMapSize(10, 80, MapSize));

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -6, 1,
                HeightPlacer.Mode.IncludeMinExcludeMax, tWater);

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(5, 7, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(20, 50, MapSize)), 0.1),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tShoreBlend, tShore, tWater },
                        new[] { 1, 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightLake, 3),
                    new TileClassPainter(clWater),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, clWater, 20),
                SafeMath.Round(RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers));

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, 2.8,
                HeightPlacer.Mode.IncludeMinExcludeMax, tShoreBlend);
            RmgenLibrary.PaintTileClassBasedOnHeight(-6, 0.5,
                HeightPlacer.Mode.IncludeMinExcludeMax, clWater);

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.1),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tCliff, tSnowA }, new[] { 3 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightHill, 3),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 15, clWater, 2,
                    ClBaseResource, 2),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers);

            double forestTrees = 0.7 * RmgenLibrary.ScaleByMapSize(100, 625, MapSize);
            double stragglerTrees = (1 - 0.7) * RmgenLibrary.ScaleByMapSize(100, 625, MapSize);
            var pForestD = new[] { tForestFloor + "|" + oPine, tForestFloor, tForestFloor };
            var pForestS = new[] { tForestFloor + "|" + oPine, tForestFloor, tForestFloor, tForestFloor };
            var forestTypes = new[]
            {
                new object[] { new object[] { tSnowA, tSnowA, tSnowA, tSnowA, pForestD }, new object[] { tSnowA, tSnowA, tSnowA, pForestD } },
                new object[] { new object[] { tSnowA, tSnowA, tSnowA, tSnowA, pForestS }, new object[] { tSnowA, tSnowA, tSnowA, pForestS } },
            };
            double forestSize = forestTrees / (RmgenLibrary.ScaleByMapSize(3, 6, MapSize) * NumPlayers);
            double forestNum = Math.Floor(forestSize / forestTypes.Length);
            foreach (var type in forestTypes)
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        forestTrees / (forestNum * Math.Floor(RmgenLibrary.ScaleByMapSize(2, 4, MapSize))),
                        double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 20, ClHill, 0, clWater, 8),
                    forestNum);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aIceberg, 0, 2, 0, 4),
                }, true, ClRock),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClRock, 6),
                    RmgenLibrary.StayClasses(clWater, 4),
                }),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        size, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tSnowD, tSnowB, tSnowC },
                            new[] { 2, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 8, ClForest, 0, ClHill, 0,
                        ClPlayer, 20, ClDirt, 16),
                    RmgenLibrary.ScaleByMapSize(20, 80, MapSize));

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        size, 0.5),
                    new IPainter[]
                    {
                        new TerrainPainter(tSnowE, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 8, ClForest, 0, ClHill, 0,
                        ClPlayer, 20, ClDirt, 16),
                    RmgenLibrary.ScaleByMapSize(20, 80, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                }, true, ClRock),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20,
                    ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(8, 32, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3),
                }, true, ClRock),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20,
                    ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(8, 32, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4),
                }, true, ClMetal),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20,
                    ClMetal, 10, ClRock, 5, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(8, 32, MapSize), 100);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oPine },
                RmgenLibrary.AvoidClasses(clWater, 5, ClForest, 1, ClHill, 1,
                    ClPlayer, 12, ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oWalrus, 5, 7, 0, 4),
                }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20,
                    ClHill, 1, clFood, 20),
                3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oArcticWolf, 2, 3, 0, 2),
                }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20,
                    ClHill, 1, clFood, 20),
                3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oFish, 2, 3, 0, 2),
                }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 20),
                    RmgenLibrary.StayClasses(clWater, 6),
                }),
                25 * NumPlayers, 60);

            return map.MakeExportable();
        }

        private static List<RmgenVector2D> PlayerPlacementLine(RandomMap map, int numPlayers,
            double angle, RmgenVector2D center, double width)
        {
            var playerPosition = new List<RmgenVector2D>();
            for (int i = 0; i < numPlayers; ++i)
            {
                var offset = new RmgenVector2D(
                    RmgenLibrary.FractionToTiles((i + 1.0) / (numPlayers + 1) - 0.5, map.GetSize()),
                    width * (i % 2 - 0.5));
                offset.Rotate(angle);
                var pos = RmgenVector2D.Add(center, offset);
                pos.Round();
                playerPosition.Add(pos);
            }
            return playerPosition;
        }
    }

    /// <summary>fortress.js（307 行）——要塞：每名非游牧玩家开局获得铺装方院、资源箱和
    /// Spahbod 式城墙复合体。无 biome；环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class FortressMap2 : StandardMap
    {
        private static readonly string[] tGrass =
            { "temperate_grass_04", "temperate_grass_03", "temperate_grass_04" };
        private const string tForestFloor = "temperate_forestfloor_01";
        private const string tGrassA = "temperate_grass_05";
        private const string tGrassB = "temperate_grass_02";
        private const string tGrassC = "temperate_grass_mud_01";
        private const string tHill = "temperate_rocks_dirt_01";
        private static readonly string[] tCliff = { "temperate_cliff_01", "temperate_cliff_02" };
        private const string tRoad = "temperate_paving_03";
        private const string tGrassPatch = "temperate_grass_dirt_01";
        private const string tShore = "temperate_grass_mud_01";
        private const string tWater = "temperate_mud_01";

        private const string oBeech = "gaia/tree/euro_beech_aut";
        private const string oOak = "gaia/tree/oak_aut";
        private const string oPine = "gaia/tree/pine";
        private const string oDeer = "gaia/fauna_deer";
        private const string oFish = "gaia/fish/generic";
        private const string oSheep = "gaia/fauna_rabbit";
        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oStoneLarge = "gaia/rock/temperate_large";
        private const string oStoneSmall = "gaia/rock/temperate_small";
        private const string oMetalLarge = "gaia/ore/temperate_01";
        private const string oMetalSmall = "gaia/ore/temperate_small";
        private const string oFoodTreasure = "gaia/treasure/food_bin";
        private const string oWoodTreasure = "gaia/treasure/wood";
        private const string oStoneTreasure = "gaia/treasure/stone";
        private const string oMetalTreasure = "gaia/treasure/metal";

        private const string aGrass = "actor|props/flora/grass_soft_dry_small_tall.xml";
        private const string aGrassShort = "actor|props/flora/grass_soft_dry_large.xml";
        private const string aRockLarge = "actor|geology/stone_granite_med.xml";
        private const string aRockMedium = "actor|geology/stone_granite_med.xml";
        private const string aReeds = "actor|props/flora/reeds_pond_dry.xml";
        private const string aLillies = "actor|props/flora/water_lillies.xml";
        private const string aBushMedium = "actor|props/flora/bush_medit_me_dry.xml";
        private const string aBushSmall = "actor|props/flora/bush_medit_sm_dry.xml";

        protected override double HeightLand => 3;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tGrass);
            var map = Map;

            const double heightSeaGround = -4;
            const double playerAngle = -SafeMath.PI / 4;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);

            var treasures = new[]
            {
                (Template: oFoodTreasure, Count: 5),
                (Template: oWoodTreasure, Count: 5),
                (Template: oMetalTreasure, Count: 4),
                (Template: oStoneTreasure, Count: 2),
            };

            var (playerIDs, playerPosition, _, _) = RmgenCommon.PlayerPlacementCircle(
                rng, map, NumPlayers, RmgenLibrary.FractionToTiles(0.35, MapSize));

            for (int i = 0; i < NumPlayers; ++i)
            {
                if (settings.Nomad)
                    break;

                foreach (double dist in new[] { 6.0, 8.0 })
                {
                    var ents = RmgenCommon.GetStartingEntities(settings.DataRoot,
                        RmgenCommon.GetCivCode(settings, playerIDs[i]));
                    if (dist == 8)
                        ents = ents.Where(ent =>
                            ent.Template.Contains("female", StringComparison.Ordinal) ||
                            ent.Template.Contains("infantry", StringComparison.Ordinal)).ToList();
                    if (ents.Count > 0)
                        RmgenCommon.PlaceStartingEntities(map, playerPosition[i], playerIDs[i], ents, dist);
                }

                for (int j = 0; j < treasures.Length; ++j)
                {
                    var offset = new RmgenVector2D(10, 0);
                    offset.Rotate(-j * SafeMath.PI / 2 - playerAngle);
                    RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(rng, treasures[j].Template, treasures[j].Count,
                                treasures[j].Count, 0, 2),
                        }, false, ClBaseResource, RmgenVector2D.Add(playerPosition[i], offset)),
                        0, null);
                }

                string civ = RmgenCommon.GetCivCode(settings, playerIDs[i]);
                double tilesSize = civ == "cart" ? 23 : 21;
                var points = new List<RmgenVector2D>();
                for (int j = 0; j < 4; ++j)
                {
                    var point = new RmgenVector2D(tilesSize, 0);
                    point.Rotate(j * SafeMath.PI / 2 - playerAngle - SafeMath.PI / 4);
                    point.Add(playerPosition[i]);
                    points.Add(point);
                }
                RmgenLibrary.CreateArea(
                    new ConvexPolygonPlacer(points, double.PositiveInfinity),
                    new IPainter[]
                    {
                        new TerrainPainter(tRoad, rng),
                        new TileClassPainter(ClPlayer),
                    },
                    null);

                PlaceSpahbodFortress(map, playerPosition[i], civ, playerIDs[i], playerAngle);
            }

            int numLakes = (int)SafeMath.Round(RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers);
            var waterAreas = RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(100, 250, MapSize),
                    0.8, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tShore, tWater }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaGround, 3),
                    new TileClassPainter(clWater),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 7, clWater, 20),
                numLakes);

            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aReeds, 5, 10, 0, 4),
                    new ScatterObject(rng, aLillies, 0, 1, 0, 4),
                }, true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.BorderClasses(clWater, 3, 0),
                    RmgenLibrary.StayClasses(clWater, 1),
                }),
                numLakes, 100, waterAreas);

            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oFish, 1, 1, 0, 1),
                }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clWater, 4),
                    RmgenLibrary.AvoidClasses(clFood, 8),
                }),
                numLakes / 4.0, 50, waterAreas);

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        2, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 5),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tCliff, tCliff, tHill }, new[] { 1, 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, 18, 2),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 5, clWater, 5, ClHill, 15),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers);

            double forestTrees = 0.7 * RmgenLibrary.ScaleByMapSize(500, 2500, MapSize);
            double stragglerTrees = (1 - 0.7) * RmgenLibrary.ScaleByMapSize(500, 2500, MapSize);
            var pForestD = new[] { tForestFloor + "|" + oBeech, tForestFloor };
            var pForestO = new[] { tForestFloor + "|" + oOak, tForestFloor };
            var pForestP = new[] { tForestFloor + "|" + oPine, tForestFloor };
            var forestTypes = new[]
            {
                new object[] { new object[] { tForestFloor, tGrass, pForestD }, new object[] { tForestFloor, pForestD } },
                new object[] { new object[] { tForestFloor, tGrass, pForestO }, new object[] { tForestFloor, pForestO } },
                new object[] { new object[] { tForestFloor, tGrass, pForestP }, new object[] { tForestFloor, pForestP } },
            };
            double forestSize = forestTrees / (RmgenLibrary.ScaleByMapSize(3, 6, MapSize) * NumPlayers);
            double forestNum = Math.Floor(forestSize / forestTypes.Length);
            foreach (var type in forestTypes)
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        forestTrees / forestNum, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 5, clWater, 3, ClForest, 15, ClHill, 1),
                    forestNum);

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        size, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new object[] { tGrass, tGrassA },
                            new object[] { tGrassA, tGrassB },
                            new object[] { tGrassB, tGrassC },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClHill, 0,
                        ClDirt, 5, ClPlayer, 1),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        size, 0.5),
                    new IPainter[]
                    {
                        new TerrainPainter(tGrassPatch, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClHill, 0,
                        ClDirt, 5, ClPlayer, 1),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                        new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                    },
                    new IGroupElement[] { new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3) },
                },
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 1, ClPlayer, 5,
                    ClRock, 10, ClHill, 1),
                ClRock);

            GaiaEntities.CreateBalancedMetalMines(rng, map, NumPlayers,
                oMetalSmall, oMetalLarge, ClMetal,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 1, ClPlayer, 5,
                    ClMetal, 10, ClRock, 5, ClHill, 1));

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                        new ScatterObject(rng, aRockMedium, 1, 3, 0, 2),
                    },
                    new IGroupElement[] { new ScatterObject(rng, aGrassShort, 1, 2, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aGrass, 2, 4, 0, 1.8),
                        new ScatterObject(rng, aGrassShort, 3, 6, 1.2, 2.5),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aBushMedium, 1, 2, 0, 2),
                        new ScatterObject(rng, aBushSmall, 2, 4, 0, 2),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                },
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 1, ClHill, 0));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oSheep, 2, 3, 0, 2) },
                    new IGroupElement[] { new ScatterObject(rng, oDeer, 5, 7, 0, 4) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers },
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 6,
                    ClHill, 1, clFood, 20),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oBerryBush, 5, 7, 0, 4) },
                },
                new double[] { rng.RandIntInclusive(1, 4) * NumPlayers + 2 },
                RmgenLibrary.AvoidClasses(clWater, 2, ClForest, 0, ClPlayer, 6,
                    ClHill, 1, clFood, 10),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oOak, oBeech, oPine },
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 1, ClHill, 1,
                    ClPlayer, 1, ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }

        private static void PlaceSpahbodFortress(RandomMap map, RmgenVector2D centerPosition,
            string civ, int playerId, double orientation)
        {
            var wall = new[]
            {
                "gate", "tower", "long",
                "cornerIn", "long", "barracks", "tower", "long", "tower", "long",
                "cornerIn", "long", "stable", "tower", "gate", "tower", "long",
                "cornerIn", "long", "temple", "tower", "long", "tower", "long",
                "cornerIn", "long", "market", "tower",
            };
            var style = BuildStoneWallStyle(civ);
            var centerToFirstElement = GetCenterToFirstElement(
                GetWallAlignment(style, new RmgenVector2D(0, 0), wall, 0));
            var position = RmgenVector2D.Add(
                RmgenVector2D.Add(centerPosition,
                    Rotate(new RmgenVector2D(centerToFirstElement.X, 0), -orientation)),
                Rotate(new RmgenVector2D(centerToFirstElement.Y, 0).Perpendicular(), -orientation));
            PlaceWall(map, style, position, wall, playerId, orientation);
        }

        private sealed class WallStyleData
        {
            public readonly string Civ;
            public readonly Dictionary<string, WallElementData> Elements;
            public readonly double Overlap;

            public WallStyleData(string civ, Dictionary<string, WallElementData> elements, double overlap)
            {
                Civ = civ;
                Elements = elements;
                Overlap = overlap;
            }
        }

        private readonly struct WallElementData
        {
            public readonly string? TemplateName;
            public readonly double Angle;
            public readonly double Length;
            public readonly double Indent;
            public readonly double Bend;

            public WallElementData(string? templateName, double angle, double length, double indent, double bend)
            {
                TemplateName = templateName;
                Angle = angle;
                Length = length;
                Indent = indent;
                Bend = bend;
            }

            public WallElementData With(string? templateName = null, double? angle = null,
                double? length = null, double? indent = null, double? bend = null)
                => new(templateName ?? TemplateName, angle ?? Angle, length ?? Length,
                    indent ?? Indent, bend ?? Bend);
        }

        private static WallStyleData BuildStoneWallStyle(string civ)
        {
            string wallset = "structures/" + civ + "/wallset_stone";
            var stats = RmgenLibrary.Templates?.ExtractStats(wallset);
            if (stats == null || !stats.IsWallSet)
            {
                civ = "athen";
                wallset = "structures/athen/wallset_stone";
                stats = RmgenLibrary.Templates?.ExtractStats(wallset);
            }

            string TemplateOr(string? template, string fallback)
                => string.IsNullOrWhiteSpace(template) ? fallback : template.Replace("{civ}", civ);

            var elements = new Dictionary<string, WallElementData>(StringComparer.Ordinal)
            {
                ["tower"] = ReadyWallElement(TemplateOr(stats?.WallSetTower,
                    "structures/" + civ + "/wall_tower"), 7.0 / RmgenConstants.TERRAIN_TILE_SIZE),
                ["gate"] = ReadyWallElement(TemplateOr(stats?.WallSetGate,
                    "structures/" + civ + "/wall_gate"), 36.0 / RmgenConstants.TERRAIN_TILE_SIZE),
                ["long"] = ReadyWallElement(TemplateOr(stats?.WallSetLong,
                    "structures/" + civ + "/wall_long"), 36.0 / RmgenConstants.TERRAIN_TILE_SIZE),
                ["medium"] = ReadyWallElement(TemplateOr(stats?.WallSetMedium,
                    "structures/" + civ + "/wall_medium"), 24.0 / RmgenConstants.TERRAIN_TILE_SIZE),
                ["short"] = ReadyWallElement(TemplateOr(stats?.WallSetShort,
                    "structures/" + civ + "/wall_short"), 12.0 / RmgenConstants.TERRAIN_TILE_SIZE),
            };

            double minTowerOverlap = stats?.WallSetMinTowerOverlap ?? 0.05;
            return new WallStyleData(civ, elements, minTowerOverlap * elements["tower"].Length);
        }

        private static WallElementData ReadyWallElement(string templateName, double fallbackLength)
        {
            double length = fallbackLength;
            try
            {
                var stats = RmgenLibrary.Templates?.ExtractStats(templateName);
                if (stats != null && stats.WallPieceLength > 0)
                    length = stats.WallPieceLength / RmgenConstants.TERRAIN_TILE_SIZE;
            }
            catch (Exception)
            {
                // 模板缺失时沿用上游常见石墙尺寸兜底。
            }
            return new WallElementData(templateName, SafeMath.PI, length, 0, 0);
        }

        private static WallElementData GetWallElement(WallStyleData style, string element)
        {
            if (style.Elements.TryGetValue(element, out var direct))
                return direct;

            var ret = style.Elements.TryGetValue("tower", out var tower)
                ? tower
                : new WallElementData(null, 0, 0, 0, 0);

            switch (element)
            {
                case "cornerIn":
                    ret = ret.With(angle: ret.Angle + SafeMath.PI / 4,
                        length: 0, indent: ret.Length / 4, bend: SafeMath.PI / 2);
                    break;

                case "cornerOut":
                    ret = ret.With(angle: ret.Angle - SafeMath.PI / 4,
                        length: 0, indent: -ret.Length / 4, bend: -SafeMath.PI / 2);
                    break;

                default:
                    if (element.StartsWith("gap_", StringComparison.Ordinal))
                        ret = new WallElementData(null, 0,
                            double.Parse(element.Substring("gap_".Length),
                                System.Globalization.CultureInfo.InvariantCulture), 0, 0);
                    else if (element.StartsWith("turn_", StringComparison.Ordinal))
                        ret = new WallElementData(null, 0, 0, 0,
                            double.Parse(element.Substring("turn_".Length),
                                System.Globalization.CultureInfo.InvariantCulture) * SafeMath.PI);
                    else
                    {
                        string templateName = "structures/" + style.Civ + "/" + element;
                        bool exists = RmgenLibrary.Templates == null ||
                            RmgenLibrary.Templates.TemplateExists(templateName);
                        if (exists)
                            ret = ret.With(templateName: templateName, length: 0,
                                indent: ret.Length *
                                    (element == "outpost" ||
                                     element.EndsWith("_tower", StringComparison.Ordinal) ? -3 : 3.5));
                    }
                    break;
            }

            style.Elements[element] = ret;
            return ret;
        }

        private static List<(RmgenVector2D Position, string? TemplateName, double Angle)> GetWallAlignment(
            WallStyleData style, RmgenVector2D position, IReadOnlyList<string> wall, double orientation)
        {
            var alignment = new List<(RmgenVector2D, string?, double)>();
            var wallPosition = position;

            for (int i = 0; i < wall.Count; ++i)
            {
                var element = GetWallElement(style, wall[i]);
                alignment.Add((
                    RmgenVector2D.Sub(wallPosition,
                        Rotate(new RmgenVector2D(element.Indent, 0), -orientation)),
                    element.TemplateName,
                    orientation + element.Angle));

                if (i + 1 >= wall.Count)
                    continue;

                orientation += element.Bend;
                var nextElement = GetWallElement(style, wall[i + 1]);
                double distance = (element.Length + nextElement.Length) / 2 - style.Overlap;

                double indent = element.Indent;
                double bend = element.Bend;
                if (bend != 0 && indent != 0)
                {
                    distance += indent * SafeMath.Sin(bend);
                    wallPosition.Add(Rotate(new RmgenVector2D(indent, 0), -orientation));
                }

                wallPosition.Add(Rotate(new RmgenVector2D(distance, 0), -orientation).Perpendicular());
            }

            return alignment;
        }

        private static RmgenVector2D GetCenterToFirstElement(
            List<(RmgenVector2D Position, string? TemplateName, double Angle)> alignment)
        {
            var result = new RmgenVector2D(0, 0);
            foreach (var align in alignment)
                result.Sub(RmgenVector2D.Div(align.Position, alignment.Count));
            return result;
        }

        private static void PlaceWall(RandomMap map, WallStyleData style, RmgenVector2D position,
            IReadOnlyList<string> wall, int playerId, double orientation)
        {
            foreach (var align in GetWallAlignment(style, position, wall, orientation))
            {
                if (align.TemplateName == null || !map.InMapBounds(align.Position))
                    continue;
                var floored = align.Position;
                floored.Floor();
                map.PlaceEntityPassable(align.TemplateName, playerId, align.Position, align.Angle);
            }
        }

        private static RmgenVector2D Rotate(RmgenVector2D v, double angle)
        {
            v.Rotate(angle);
            return v;
        }
    }
}
