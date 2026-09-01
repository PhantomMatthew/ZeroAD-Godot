using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>migration.js（逐字移植）——玩家小岛与大陆隔海相望；环境设置表驱动，游牧放置未移植。</summary>
    public sealed class MigrationMap2 : StandardMap
    {
        protected override double HeightLand => -5;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, -5, biome.Water, Settings.CircularMap);

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
            var tHill = biome.Hill;
            string tTier4Terrain = biome.Tier4Terrain;
            string tShore = biome.Shore;
            string tWater = biome.Water;

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
            string oStoneSmall = biome.StoneSmall;
            string oMetalLarge = biome.MetalLarge;
            const string oWoodTreasure = "gaia/treasure/wood";

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

            const double heightLand = 3;
            const double heightHill = 18;
            const double heightOffsetBump = 2;

            var mapCenter = map.GetCenter();
            var clForestIsland = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clIslandHunt = new TileClass(MapSize);
            var clLand = new TileClass(MapSize);
            var clIsland = new TileClass(MapSize);

            double startAngle = rng.RandomAngle();
            string pattern = settings.PlayerPlacement;
            double teamDist = pattern switch
            {
                "river" => 0.65,
                "stronghold" => 0.44,
                _ => 0.42,
            };
            double playerDist = pattern switch
            {
                "stronghold" => 0.06,
                _ => 0.1,
            };

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, pattern,
                RmgenLibrary.FractionToTiles(teamDist, MapSize),
                RmgenLibrary.FractionToTiles(playerDist, MapSize),
                startAngle);

            for (int i = 0; i < NumPlayers; ++i)
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng,
                        RmgenGeometry.DiskArea(RmgenCommon.DefaultPlayerBaseRadius(MapSize) * 1.75),
                        0.8, 0.1, double.PositiveInfinity, playerPosition[i]),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tWater, tShore, tMainTerrain }, new[] { 1, 4 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightLand, 4),
                        new TileClassPainter(clIsland),
                        new TileClassPainter(settings.Nomad ? clLand : ClPlayer),
                    },
                    null);

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, null,
                playerPosition, playerIDs: playerIDs,
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
                    TreesTemplate = oTree1,
                    TreesCount = (int)RmgenLibrary.ScaleByMapSize(12, 30, MapSize),
                    DecorativesTemplate = aGrassShort,
                });

            var continentPosition = RmgenVector2D.Add(mapCenter,
                new RmgenVector2D(0, RmgenLibrary.FractionToTiles(0, MapSize)));
            continentPosition.Round();
            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.30, MapSize)),
                    0.8, 0.08, double.PositiveInfinity, continentPosition),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tWater, tShore, tMainTerrain }, new[] { 4, 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightLand, 4),
                    new TileClassPainter(clLand),
                },
                RmgenLibrary.AvoidClasses(clIsland, 22));

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(10, 65, MapSize), 0.2, 0.1,
                    double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tMainTerrain, tMainTerrain }, new[] { 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightLand, 4),
                    new TileClassPainter(clLand),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.BorderClasses(clLand, 6, 3),
                    RmgenLibrary.AvoidClasses(clIsland, 16),
                }),
                RmgenLibrary.ScaleByMapSize(2, 15, MapSize) * 20,
                150);

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, 3,
                HeightPlacer.Mode.ExcludeMinExcludeMax, tShore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -8, 1,
                HeightPlacer.Mode.ExcludeMinIncludeMax, tWater);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize), 0.3, 0.06,
                    double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump, 2, relative: true),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clIsland, 10),
                    RmgenLibrary.StayClasses(clLand, 3),
                }),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 150, MapSize), 0.2, 0.1,
                    double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tCliff, tHill }, new[] { 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightHill, 2),
                    new TileClassPainter(ClHill),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clIsland, 10, ClHill, 50),
                    RmgenLibrary.StayClasses(clLand, 12),
                }),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers);

            {
                var (forestTrees, _) = GetBiomeTreeCounts(biome, MapSize, 1);
                var types = new[]
                {
                    new object[] { new object[] { tForestFloor2, tMainTerrain, pForest1 }, new object[] { tForestFloor2, pForest1 } },
                    new object[] { new object[] { tForestFloor1, tMainTerrain, pForest2 }, new object[] { tForestFloor1, pForest2 } },
                };
                double size = forestTrees / (RmgenLibrary.ScaleByMapSize(2, 8, MapSize) * NumPlayers) *
                    (BiomeName == "generic/savanna" ? 2 : 1);
                int num = (int)Math.Floor(size / types.Length);
                foreach (var type in types)
                    RmgenLibrary.CreateAreas(rng,
                        new ClumpPlacer(rng, forestTrees / num, 0.1, 0.1, double.PositiveInfinity),
                        new IPainter[]
                        {
                            new LayeredPainter(type, new[] { 2 }, rng),
                            new TileClassPainter(ClForest),
                        },
                        new AndConstraint(new IConstraint[]
                        {
                            RmgenLibrary.AvoidClasses(ClForest, 10, ClHill, 2),
                            RmgenLibrary.StayClasses(clLand, 7),
                        }),
                        num);
            }

            foreach (double dirtClumpSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 84, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 128, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, dirtClumpSize, 0.3, 0.06, 0.5),
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
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5, clIsland, 0),
                        RmgenLibrary.StayClasses(clLand, 7),
                    }),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            foreach (double grassClumpSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 32, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 80, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, grassClumpSize, 0.3, 0.06, 0.5),
                    new IPainter[] { new TerrainPainter(tTier4Terrain, rng) },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5, clIsland, 0),
                        RmgenLibrary.StayClasses(clLand, 7),
                    }),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            var group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClRock, 18, ClHill, 1),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                RmgenLibrary.ScaleByMapSize(26, 30, MapSize), 800);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) },
                true, ClMetal);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClMetal, 18, ClRock, 8, ClHill, 1),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                RmgenLibrary.ScaleByMapSize(28, 32, MapSize), 800);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3) },
                true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClMetal, 6, ClRock, 12, ClHill, 1),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                RmgenLibrary.ScaleByMapSize(25, 30, MapSize), 800);

            var (islandForestTrees, stragglerTrees) = GetBiomeTreeCounts(biome, MapSize, 1);
            var islandTypes = new[]
            {
                new object[] { new object[] { tForestFloor2, tMainTerrain, pForest1 }, new object[] { tForestFloor2, pForest1 } },
                new object[] { new object[] { tForestFloor1, tMainTerrain, pForest2 }, new object[] { tForestFloor1, pForest2 } },
            };
            double islandSize = islandForestTrees / (RmgenLibrary.ScaleByMapSize(2, 8, MapSize) * NumPlayers) *
                (BiomeName == "generic/savanna" ? 2 : 3);
            int islandNum = (int)Math.Floor(islandSize / islandTypes.Length);
            foreach (var type in islandTypes)
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, islandForestTrees / islandNum, 0.1, 0.1, double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(clForestIsland),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClBaseResource, 11, clForestIsland, 6, ClHill, 0),
                        RmgenLibrary.StayClasses(clIsland, 5),
                    }),
                    islandNum);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                new ScatterObject(rng, aRockMedium, 1, 3, 0, 2),
            }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, oMainHuntableAnimal, 5, 7, 0, 4) }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, ClHill, 1, clFood, 20),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                3 * NumPlayers, 50);

            if (BiomeName == "generic/savanna")
            {
                group = new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oMainHuntableAnimal, 5, 7, 0, 4) }, true, clIslandHunt);
                RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClBaseResource, 6, clIslandHunt, 58),
                        RmgenLibrary.StayClasses(clIsland, 12),
                    }),
                    NumPlayers * 20, 400);
            }

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, oSecondaryHuntableAnimal, 2, 3, 0, 2) }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, ClHill, 1, clFood, 20),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                3 * NumPlayers, 50);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oFruitBush, 5, 7, 0, 4) },
                true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 8, ClHill, 1, clFood, 20),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oFish, 2, 3, 0, 2) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clLand, 2, ClPlayer, 2, ClHill, 0, clFood, 10),
                RmgenLibrary.ScaleByMapSize(500, 700, MapSize), 500);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oTree1, oTree2, oTree4, oTree3 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClHill, 1, ClPlayer, 9, ClMetal, 6, ClRock, 6),
                    RmgenLibrary.StayClasses(clLand, 9),
                }),
                ClForest, stragglerTrees);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oTree1, oTree2, oTree4, oTree3 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clForestIsland, 1, ClBaseResource, 8),
                    RmgenLibrary.StayClasses(clIsland, 5),
                }),
                ClForest, stragglerTrees);

            int planetm = BiomeName == "generic/india" ? 8 : 1;
            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, aGrassShort, 1, 2, 0, 1, -SafeMath.PI / 8, SafeMath.PI / 8) });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClHill, 2, ClPlayer, 2, ClDirt, 0),
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                planetm * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aGrass, 2, 4, 0, 1.8, -SafeMath.PI / 8, SafeMath.PI / 8),
                new ScatterObject(rng, aGrassShort, 3, 6, 1.2, 2.5, -SafeMath.PI / 8, SafeMath.PI / 8),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClHill, 2, ClPlayer, 2, ClDirt, 1, ClForest, 0),
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                planetm * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aBushMedium, 1, 2, 0, 2),
                new ScatterObject(rng, aBushSmall, 2, 4, 0, 2),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClHill, 1, ClPlayer, 1, ClDirt, 1),
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                planetm * RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            _ = oWoodTreasure;
            return map.MakeExportable();
        }

        private static (double forestTrees, double stragglerTrees) GetBiomeTreeCounts(
            BiomeSet biome, int mapSize, double multiplier)
        {
            double scaled = RmgenLibrary.ScaleByMapSize(
                biome.TreesMin * multiplier, biome.TreesMax * multiplier, mapSize);
            return (biome.ForestProbability * scaled, (1 - biome.ForestProbability) * scaled);
        }
    }

    /// <summary>botswanan_haven.js（逐字移植）——湿季草原、水洼沼泽和沿水植被梯度；环境设置表驱动，游牧放置未移植。</summary>
    public sealed class BotswananHavenMap2 : StandardMap
    {
        protected override double HeightLand => 3;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            const string tGrassA = "savanna_shrubs_a_wetseason";
            const string tGrassB = "savanna_shrubs_a";
            const string tCliff = "savanna_cliff_a";
            const string tHill = "savanna_grass_a_wetseason";
            const string tMud = "savanna_mud_a";
            const string tShoreBlend = "savanna_grass_b_wetseason";
            const string tShore = "savanna_riparian_wet";
            const string tWater = "savanna_mud_a";
            const string tCityTile = "savanna_tile_a";

            const string oBush = "gaia/tree/bush_temperate";
            const string oBaobab = "gaia/tree/baobab";
            const string oToona = "gaia/tree/toona";
            const string oBerryBush = "gaia/fruit/berry_01";
            const string oGazelle = "gaia/fauna_gazelle";
            const string oZebra = "gaia/fauna_zebra";
            const string oWildebeest = "gaia/fauna_wildebeest";
            const string oLion = "gaia/fauna_lion";
            const string oRhino = "gaia/fauna_rhinoceros_white";
            const string oCrocodile = "gaia/fauna_crocodile_nile";
            const string oElephant = "gaia/fauna_elephant_north_african";
            const string oElephantInfant = "gaia/fauna_elephant_african_infant";
            const string oLioness = "gaia/fauna_lioness";
            const string oRabbit = "gaia/fauna_rabbit";
            const string oStoneLarge = "gaia/rock/temperate_large";
            const string oStoneSmall = "gaia/rock/savanna_small";
            const string oMetalLarge = "gaia/ore/savanna_large";

            const string aGrass = "actor|props/flora/grass_field_lush_tall.xml";
            const string aGrass2 = "actor|props/flora/grass_tropic_field_tall.xml";
            const string aGrassShort = "actor|props/flora/grass_soft_large.xml";
            const string aRockLarge = "actor|geology/stone_granite_med.xml";
            const string aRockMedium = "actor|geology/stone_granite_med.xml";
            const string aReeds = "actor|props/flora/reeds_pond_lush_a.xml";
            const string aReeds2 = "actor|props/flora/reeds_pond_lush_b.xml";
            const string aLillies = "actor|props/flora/water_lillies.xml";
            const string aBushMedium = "actor|props/flora/bush_tropic_b.xml";
            const string aBushSmall = "actor|props/flora/bush_tropic_a.xml";
            const string aShrub = "actor|props/flora/shrub_tropic_plant_flower.xml";
            const string aFlower = "actor|props/flora/flower_bright.xml";
            const string aPalm = "actor|props/flora/shrub_fanpalm.xml";

            const double heightMarsh = -2;
            const double heightHillTop = 15;
            const double heightOffsetBump1 = 2;
            const double heightOffsetBump2 = 1;

            InitContextNoBiome(rng, settings, tShoreBlend);
            var map = Map;
            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, settings.PlayerPlacement,
                RmgenLibrary.FractionToTiles(0.35, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize),
                rng.RandomAngle());

            RmgenCommon.PlacePlayerBases(rng, map, settings, tShoreBlend, ClPlayer, null,
                playerPosition, tCityTile, tCityTile, playerIDs,
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
                    TreesTemplate = oBaobab,
                    DecorativesTemplate = aGrassShort,
                });

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize), 0.6, 0.1,
                    double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump1, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 13),
                RmgenLibrary.ScaleByMapSize(300, 800, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tCliff, tHill }, new[] { 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightHillTop, 2),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 15, clWater, 0),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers * 3);

            for (int i = 0; i < 2; ++i)
            {
                double waterSpacing = SafeMath.Round(
                    RmgenLibrary.ScaleByMapSize(7, 16, MapSize) * rng.RandFloat(0.8, 1.35));
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(6, 12, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(15, 60, MapSize)), 0.8),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tShoreBlend, tShore, tWater }, new[] { 1, 1 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightMarsh, 3),
                        new TileClassPainter(clWater),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 25, clWater, waterSpacing),
                    RmgenLibrary.ScaleByMapSize(4, 20, MapSize));
            }

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aReeds, 20, 40, 0, 4),
                    new ScatterObject(rng, aReeds2, 20, 40, 0, 4),
                    new ScatterObject(rng, aLillies, 10, 30, 0, 4),
                }, true),
                0, RmgenLibrary.StayClasses(clWater, 1),
                RmgenLibrary.ScaleByMapSize(400, 1000, MapSize), 100);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize), 0.3, 0.06,
                    double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump2, 2, relative: true),
                },
                RmgenLibrary.StayClasses(clWater, 2),
                RmgenLibrary.ScaleByMapSize(50, 100, MapSize));

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 2, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 6, MapSize)),
                        size, double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tGrassA, tGrassB, tMud }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 1, ClHill, 0, ClDirt, 5, ClPlayer, 8),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            var group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4),
                new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClPlayer, 20, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3) },
                true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClPlayer, 20, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) },
                true, ClMetal);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClPlayer, 20, ClMetal, 10, ClRock, 5, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClPlayer, 1),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                new ScatterObject(rng, aRockMedium, 1, 3, 0, 2),
            }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oLion, 0, 1, 0, 4),
                new ScatterObject(rng, oLioness, 2, 3, 0, 4),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClPlayer, 20, clFood, 11, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 12, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oZebra, 4, 6, 0, 4) },
                true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 13),
                3 * NumPlayers, 50);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oWildebeest, 2, 4, 0, 4) },
                true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 13),
                3 * NumPlayers, 50);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oCrocodile, 2, 3, 0, 4) },
                true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 13),
                    RmgenLibrary.StayClasses(clWater, 3),
                }),
                5 * NumPlayers, 200);

            foreach (var fauna in new (string template, double min, double max, double dist, double count)[]
            {
                (oGazelle, 4, 6, 4, 3 * NumPlayers),
                (oRabbit, 6, 8, 2, 6 * NumPlayers),
                (oRhino, 1, 1, 2, 3 * NumPlayers),
            })
            {
                group = new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, fauna.template, fauna.min, fauna.max, 0, fauna.dist) }, true, clFood);
                RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                    RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 13),
                    fauna.count, 50);
            }

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oElephant, 2, 3, 0, 4),
                new ScatterObject(rng, oElephantInfant, 1, 1, 0, 4),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 13),
                3 * NumPlayers, 50);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oBerryBush, 5, 7, 0, 4) },
                true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oToona, oBaobab, oBush, oBush },
                RmgenLibrary.AvoidClasses(ClForest, 1, clWater, 1, ClHill, 1, ClPlayer, 13, ClMetal, 4, ClRock, 4),
                ClForest, RmgenLibrary.ScaleByMapSize(60, 500, MapSize));

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, aGrassShort, 1, 2, 0, 1, -SafeMath.PI / 8, SafeMath.PI / 8) });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 13, ClDirt, 0),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aGrass, 2, 4, 0, 1.8, -SafeMath.PI / 8, SafeMath.PI / 8),
                new ScatterObject(rng, aGrassShort, 3, 6, 1.2, 2.5, -SafeMath.PI / 8, SafeMath.PI / 8),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClPlayer, 13, ClDirt, 1, ClForest, 0),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aBushMedium, 1, 2, 0, 2),
                new ScatterObject(rng, aBushSmall, 2, 4, 0, 2),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClPlayer, 13, ClDirt, 1),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, aShrub, 1, 1, 0, 2) });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClPlayer, 13, ClDirt, 1),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, aPalm, 1, 3, 0, 2) });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 12, ClDirt, 1),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aFlower, 0, 6, 0, 2),
                new ScatterObject(rng, aGrass2, 2, 5, 0, 2),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClHill, 1, ClPlayer, 13, ClDirt, 1),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            return map.MakeExportable();
        }
    }

    /// <summary>wall_demo.js（逐字移植）——墙体样式展示；私有墙构建器补足当前 WallBuilder 未覆盖的样式表。</summary>
    public sealed class WallDemoMap2 : StandardMap
    {
        protected override double HeightLand => 0;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, "grass1");
            var map = Map;
            int mapSize = MapSize;

            const double distToMapBorder = 5;
            const double distToOtherWalls = 10;
            double buildableMapSize = mapSize - 2 * distToMapBorder;
            var position = new RmgenVector2D(distToMapBorder, distToMapBorder);
            const int playerID = 0;
            var wallStyles = LoadWallStyles(settings.DataRoot);
            int wallStyleCount = wallStyles.Count;

            for (int styleIndex = 0; styleIndex < wallStyleCount; ++styleIndex)
            {
                var pos = RmgenVector2D.Add(position,
                    new RmgenVector2D(styleIndex * buildableMapSize / wallStyleCount, 0));
                var wall = new[]
                {
                    "start", "long", "tower", "tower", "tower", "medium", "outpost", "medium",
                    "cornerOut", "medium", "cornerIn", "medium", "house", "end", "entryTower",
                    "start", "short", "barracks", "gate", "tower", "medium", "fort", "medium", "end",
                };
                double orientation = SafeMath.PI / 16 * SafeMath.Sin(styleIndex * SafeMath.PI / 4);
                PlaceWall(map, wallStyles[styleIndex], pos, wall, playerID, orientation, null);
            }

            position.Y += 80 + distToOtherWalls;

            const double fortressRadius = 15;
            var fortresses = CreateDefaultFortressTypes();
            for (int styleIndex = 0; styleIndex < wallStyleCount; ++styleIndex)
            {
                double orientation = styleIndex * SafeMath.PI / 32;
                var pos = Sum(position,
                    new RmgenVector2D(1, 1).Scaled(fortressRadius),
                    new RmgenVector2D(styleIndex * buildableMapSize / wallStyleCount, 0));
                map.PlaceEntityPassable("structures/obelisk", playerID, pos, orientation);
                PlaceCustomFortress(map, wallStyles[styleIndex], pos, fortresses["tiny"], playerID, orientation, null);
            }

            position.Y += 2 * fortressRadius + distToOtherWalls;

            double radius = Math.Min((mapSize - position.Y - distToOtherWalls) / 3,
                (buildableMapSize / wallStyleCount - distToOtherWalls) / 2);
            for (int styleIndex = 0; styleIndex < wallStyleCount; ++styleIndex)
            {
                var pos = Sum(position, new RmgenVector2D(radius, radius),
                    new RmgenVector2D(styleIndex * buildableMapSize / wallStyleCount, 0));
                map.PlaceEntityPassable("structures/obelisk", playerID, pos, 0);
                PlaceGenericFortress(rng, map, wallStyles[styleIndex], pos, radius, playerID, 0.5, 3, 100, null);
            }

            position.Y += 2 * radius + distToOtherWalls;

            radius = Math.Min((mapSize - position.Y - distToOtherWalls) / 3,
                (buildableMapSize / wallStyleCount - distToOtherWalls) / 2);
            for (int styleIndex = 0; styleIndex < wallStyleCount; ++styleIndex)
            {
                var center = Sum(position, new RmgenVector2D(1, 1).Scaled(radius),
                    new RmgenVector2D(styleIndex * buildableMapSize / wallStyleCount, 0));
                var wallPart = new[] { "tower", "medium", "house" };
                double orientation = styleIndex * SafeMath.PI / 16;
                double maxAngle = SafeMath.PI / 2 * (styleIndex % 3 + 2);
                map.PlaceEntityPassable("structures/obelisk", playerID, center, orientation);
                PlaceCircularWall(map, wallStyles[styleIndex], center, radius, wallPart, playerID,
                    orientation, maxAngle, null, 0, null);
            }

            position.Y += 2 * radius + distToOtherWalls;

            radius = Math.Min((mapSize - position.Y - distToOtherWalls) / 2,
                (buildableMapSize / wallStyleCount - distToOtherWalls) / 2);
            for (int styleIndex = 0; styleIndex < wallStyleCount; ++styleIndex)
            {
                var centerPosition = Sum(position, new RmgenVector2D(1, 1).Scaled(radius),
                    new RmgenVector2D(styleIndex * buildableMapSize / wallStyleCount, 0));
                double orientation = styleIndex * SafeMath.PI / 16;
                int numCorners = styleIndex % 6 + 3;
                map.PlaceEntityPassable("structures/obelisk", playerID, centerPosition, orientation);
                PlacePolygonalWall(map, wallStyles[styleIndex], centerPosition, radius,
                    new[] { "medium", "tower" }, "tower", playerID, orientation, numCorners, true, null);
            }

            position.Y += 2 * radius + distToOtherWalls;

            radius = Math.Min((mapSize - position.Y - distToOtherWalls) / 2,
                (buildableMapSize / wallStyleCount - distToOtherWalls) / 2);
            for (int styleIndex = 0; styleIndex < wallStyleCount; ++styleIndex)
            {
                var centerPosition = Sum(position, new RmgenVector2D(1, 1).Scaled(radius),
                    new RmgenVector2D(styleIndex * buildableMapSize / wallStyleCount, 0));
                double orientation = styleIndex * SafeMath.PI / 16;
                int numCorners = styleIndex % 6 + 3;
                map.PlaceEntityPassable("structures/obelisk", playerID, centerPosition, orientation);
                PlaceIrregularPolygonalWall(rng, map, wallStyles[styleIndex], centerPosition, radius,
                    "tower", playerID, orientation, numCorners, 0.5, true, null, null);
            }

            position.Y += 2 * radius + distToOtherWalls;

            double maxWallLength = mapSize - position.Y - distToMapBorder - distToOtherWalls;
            int numWallsPerStyle = (int)Math.Floor(buildableMapSize / distToOtherWalls / wallStyleCount);
            for (int styleIndex = 0; styleIndex < wallStyleCount; ++styleIndex)
                for (int wallIndex = 0; wallIndex < numWallsPerStyle; ++wallIndex)
                {
                    double offsetX = (styleIndex * numWallsPerStyle + wallIndex) * buildableMapSize /
                        wallStyleCount / numWallsPerStyle;
                    var start = RmgenVector2D.Add(position, new RmgenVector2D(offsetX, 0));
                    double offsetY = (wallIndex + 1) * maxWallLength / numWallsPerStyle;
                    var end = RmgenVector2D.Add(position, new RmgenVector2D(offsetX, offsetY));
                    PlaceLinearWall(map, wallStyles[styleIndex], start, end,
                        new[] { "tower", "medium" }, playerID, true, null);
                }

            return map.MakeExportable();
        }

        private struct DemoWallElement
        {
            public string? TemplateName;
            public double Angle;
            public double Length;
            public double Indent;
            public double Bend;

            public DemoWallElement(string? templateName, double angle, double length, double indent, double bend)
            {
                TemplateName = templateName;
                Angle = angle;
                Length = length;
                Indent = indent;
                Bend = bend;
            }
        }

        private sealed class DemoWallStyle
        {
            public readonly string Key;
            public readonly string FirstCiv;
            public readonly string? TemplateRoot;
            public readonly HashSet<string> KnownCivs;
            public readonly Dictionary<string, DemoWallElement> Elements = new(StringComparer.Ordinal);
            public readonly List<DemoWallElement> Curves = new();
            public double Overlap;

            public DemoWallStyle(string key, string firstCiv, string? templateRoot, HashSet<string> knownCivs)
            {
                Key = key;
                FirstCiv = firstCiv;
                TemplateRoot = templateRoot;
                KnownCivs = knownCivs;
            }

            public bool TemplateExists(string templateName)
            {
                if (TemplateRoot == null)
                    return true;
                return File.Exists(Path.Combine(TemplateRoot, templateName + ".xml"));
            }
        }

        private sealed class DemoFortress
        {
            public readonly List<string> Wall;
            public RmgenVector2D? CenterToFirstElement;

            public DemoFortress(IEnumerable<string> wall, RmgenVector2D? centerToFirstElement = null)
            {
                Wall = new List<string>(wall);
                CenterToFirstElement = centerToFirstElement;
            }
        }

        private static List<DemoWallStyle> LoadWallStyles(string? dataRoot)
        {
            if (dataRoot == null)
                return FallbackWallStyles();

            string civsDir = Path.Combine(dataRoot, "simulation", "data", "civs");
            string templateRoot = Path.Combine(dataRoot, "simulation", "templates");
            if (!Directory.Exists(civsDir) || !Directory.Exists(templateRoot))
                return FallbackWallStyles();

            var civInfos = new List<(string code, List<string> wallSets)>();
            var knownCivs = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(civsDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    string code = doc.RootElement.TryGetProperty("Code", out var codeElement)
                        ? codeElement.GetString() ?? Path.GetFileNameWithoutExtension(file)
                        : Path.GetFileNameWithoutExtension(file);
                    knownCivs.Add(code);
                    var wallSets = new List<string>();
                    if (doc.RootElement.TryGetProperty("WallSets", out var wallSetElement))
                        foreach (var item in wallSetElement.EnumerateArray())
                        {
                            string? value = item.GetString();
                            if (!string.IsNullOrEmpty(value))
                                wallSets.Add(value);
                        }
                    civInfos.Add((code, wallSets));
                }
                catch (Exception)
                {
                    // 单个文明数据异常时跳过，保持展示图可生成。
                }
            }

            string firstCiv = civInfos.Count > 0 ? civInfos[0].code : "athen";
            var styles = new List<DemoWallStyle>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (code, wallSets) in civInfos)
                foreach (string wallSetPath in wallSets)
                {
                    string baseName = Path.GetFileName(wallSetPath);
                    string[] parts = baseName.Split('_');
                    if (parts.Length < 2)
                        continue;
                    string styleKey = wallSetPath.Split('/').Contains(code, StringComparer.Ordinal)
                        ? code + "/" + parts[1]
                        : parts[1];
                    if (!seen.Add(styleKey))
                        continue;
                    styles.Add(LoadWallSet(templateRoot, wallSetPath, styleKey, code, firstCiv, knownCivs));
                }

            return styles.Count == 0 ? FallbackWallStyles() : styles;
        }

        private static DemoWallStyle LoadWallSet(string templateRoot, string wallSetPath, string styleKey,
            string civ, string firstCiv, HashSet<string> knownCivs)
        {
            var style = new DemoWallStyle(styleKey, firstCiv, templateRoot, knownCivs);
            double minTowerOverlap = 0.05;
            foreach (var root in LoadTemplateChain(templateRoot, wallSetPath, civ))
            {
                var wallSet = root.Element("WallSet");
                if (wallSet == null)
                    continue;
                if (TryReadDouble(wallSet.Element("MinTowerOverlap"), out double overlap))
                    minTowerOverlap = overlap;
                var templates = wallSet.Element("Templates");
                if (templates == null)
                    continue;
                foreach (var entry in templates.Elements())
                {
                    string? value = entry.Value.Trim();
                    if (string.IsNullOrEmpty(value))
                        continue;
                    string tag = entry.Name.LocalName;
                    if (tag == "WallCurves")
                    {
                        foreach (string curvePath in SplitTemplateList(value))
                            style.Curves.Add(LoadWallElement(templateRoot, curvePath, civ));
                        continue;
                    }
                    string? key = tag switch
                    {
                        "Tower" => "tower",
                        "Gate" => "gate",
                        "Fort" => "fort",
                        "WallLong" => "long",
                        "WallMedium" => "medium",
                        "WallShort" => "short",
                        "WallEnd" => "end",
                        _ => null,
                    };
                    if (key != null)
                        style.Elements[key] = LoadWallElement(templateRoot, value, civ);
                }
            }

            EnsureFallbackElements(style, civ);
            style.Overlap = minTowerOverlap * style.Elements["tower"].Length;
            return style;
        }

        private static IEnumerable<string> SplitTemplateList(string value)
            => value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        private static List<XElement> LoadTemplateChain(string templateRoot, string templateName, string civ)
        {
            var chain = new List<XElement>();
            string current = templateName.Replace("{civ}", civ, StringComparison.Ordinal);
            for (int depth = 0; depth < 16 && !string.IsNullOrEmpty(current); ++depth)
            {
                string path = Path.Combine(templateRoot, current + ".xml");
                if (!File.Exists(path))
                    break;
                try
                {
                    var doc = XDocument.Load(path);
                    if (doc.Root == null)
                        break;
                    chain.Insert(0, new XElement(doc.Root));
                    current = doc.Root.Attribute("parent")?.Value ?? "";
                    current = current.Replace("{civ}", civ, StringComparison.Ordinal);
                }
                catch (Exception)
                {
                    break;
                }
            }
            return chain;
        }

        private static DemoWallElement LoadWallElement(string templateRoot, string templateName, string civ)
        {
            var chain = LoadTemplateChain(templateRoot, templateName, civ);
            if (chain.Count == 0)
                return FallbackElement(templateName, civ);

            bool hasWallPiece = false;
            double length = 0;
            double orientation = 1;
            double indent = 0;
            double bend = 0;
            foreach (var root in chain)
            {
                var wallPiece = root.Element("WallPiece");
                if (wallPiece == null)
                    continue;
                hasWallPiece = true;
                if (TryReadDouble(wallPiece.Element("Length"), out double len))
                    length = len;
                if (TryReadDouble(wallPiece.Element("Orientation"), out double ori))
                    orientation = ori;
                if (TryReadDouble(wallPiece.Element("Indent"), out double ind))
                    indent = ind;
                if (TryReadDouble(wallPiece.Element("Bend"), out double ben))
                    bend = ben;
            }

            if (hasWallPiece)
                return new DemoWallElement(templateName.Replace("{civ}", civ, StringComparison.Ordinal),
                    orientation * SafeMath.PI,
                    length / RmgenConstants.TERRAIN_TILE_SIZE,
                    indent / RmgenConstants.TERRAIN_TILE_SIZE,
                    bend * SafeMath.PI);

            double width = 0;
            foreach (var root in chain)
            {
                var obstruction = root.Element("Obstruction");
                if (obstruction == null)
                    continue;
                var stat = obstruction.Element("Static");
                if (stat?.Attribute("width") != null &&
                    double.TryParse(stat.Attribute("width")!.Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double staticWidth))
                    width = staticWidth;
                var obstructions = obstruction.Element("Obstructions");
                if (obstructions != null)
                {
                    double sumWidth = 0;
                    foreach (var part in obstructions.Elements())
                        if (part.Attribute("width") != null &&
                            double.TryParse(part.Attribute("width")!.Value, NumberStyles.Float,
                                CultureInfo.InvariantCulture, out double partWidth))
                            sumWidth += partWidth;
                    if (sumWidth > 0)
                        width = sumWidth;
                }
            }

            return new DemoWallElement(templateName.Replace("{civ}", civ, StringComparison.Ordinal),
                SafeMath.PI, width / RmgenConstants.TERRAIN_TILE_SIZE, 0, 0);
        }

        private static bool TryReadDouble(XElement? element, out double value)
        {
            value = 0;
            return element != null &&
                double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static void EnsureFallbackElements(DemoWallStyle style, string civ)
        {
            var fallback = MakeFallbackStyle(style.Key, civ, style.FirstCiv, style.KnownCivs);
            foreach (var pair in fallback.Elements)
                if (!style.Elements.ContainsKey(pair.Key))
                    style.Elements[pair.Key] = pair.Value;
            if (style.Curves.Count == 0)
                style.Curves.AddRange(fallback.Curves);
        }

        private static List<DemoWallStyle> FallbackWallStyles()
        {
            var keys = new[]
            {
                "palisade", "achae/stone", "athen/stone", "brit/stone", "cart/short", "cart/stone",
                "gaul/stone", "germ/stone", "han/stone", "iber/stone", "kush/stone", "mace/stone",
                "maur/stone", "ptol/stone", "rome/stone", "rome/siege", "sele/stone", "spart/stone",
            };
            var civs = new HashSet<string>(keys.Where(k => k.Contains('/', StringComparison.Ordinal))
                .Select(k => k.Split('/')[0]), StringComparer.Ordinal);
            civs.Add("achae");
            return keys.Select(k => MakeFallbackStyle(k, k.Contains('/', StringComparison.Ordinal) ? k.Split('/')[0] : "achae",
                "achae", civs)).ToList();
        }

        private static DemoWallStyle MakeFallbackStyle(string key, string civ, string firstCiv,
            HashSet<string> knownCivs)
        {
            var style = new DemoWallStyle(key, firstCiv, null, knownCivs);
            bool palisade = key == "palisade";
            bool cartShort = key == "cart/short";
            bool romanSiege = key == "rome/siege";
            string prefix = palisade ? "structures/palisades" :
                romanSiege ? "structures/rome/siege_wall" :
                cartShort ? "structures/cart/s_wall" : "structures/" + civ + "/wall";

            if (palisade)
            {
                style.Elements["tower"] = new DemoWallElement("structures/palisades_tower", SafeMath.PI, 0.75, 0, 0);
                style.Elements["gate"] = new DemoWallElement("structures/palisades_gate", SafeMath.PI, 3.5, 0, 0);
                style.Elements["fort"] = new DemoWallElement("structures/palisades_fort", SafeMath.PI, 2, 0, 0);
                style.Elements["long"] = new DemoWallElement("structures/palisades_long", SafeMath.PI, 3.5, 0, 0);
                style.Elements["medium"] = new DemoWallElement("structures/palisades_medium", SafeMath.PI, 2.25, 0, 0);
                style.Elements["short"] = new DemoWallElement("structures/palisades_short", SafeMath.PI, 1, 0, 0);
                style.Elements["end"] = new DemoWallElement("structures/palisades_end", 1.5 * SafeMath.PI, 0.2, 0, 0);
                style.Curves.Add(new DemoWallElement("structures/palisades_curve", 0.75 * SafeMath.PI, 2, 0.7, 0.5 * SafeMath.PI));
            }
            else
            {
                style.Elements["tower"] = new DemoWallElement(prefix + "_tower", SafeMath.PI, 1.5, 0, 0);
                style.Elements["gate"] = new DemoWallElement(prefix + "_gate", SafeMath.PI, 9, 0, 0);
                style.Elements["long"] = new DemoWallElement(prefix + "_long", SafeMath.PI, 9, 0, 0);
                style.Elements["medium"] = new DemoWallElement(prefix + "_medium", SafeMath.PI, 6, 0, 0);
                style.Elements["short"] = new DemoWallElement(prefix + "_short", SafeMath.PI, 3, 0, 0);
                if (romanSiege)
                    style.Elements["fort"] = new DemoWallElement("structures/rome/army_camp", SafeMath.PI, 29.5 / 4, 2, 0);
            }
            style.Overlap = 0.05 * style.Elements["tower"].Length;
            return style;
        }

        private static DemoWallElement FallbackElement(string templateName, string civ)
        {
            string lower = templateName.ToLowerInvariant();
            if (lower.Contains("palisades", StringComparison.Ordinal))
                return MakeFallbackStyle("palisade", civ, civ, new HashSet<string> { civ }).Elements[
                    lower.Contains("gate", StringComparison.Ordinal) ? "gate" :
                    lower.Contains("medium", StringComparison.Ordinal) ? "medium" :
                    lower.Contains("short", StringComparison.Ordinal) ? "short" :
                    lower.Contains("long", StringComparison.Ordinal) ? "long" : "tower"];
            string key = lower.Contains("gate", StringComparison.Ordinal) ? "gate" :
                lower.Contains("medium", StringComparison.Ordinal) ? "medium" :
                lower.Contains("short", StringComparison.Ordinal) ? "short" :
                lower.Contains("long", StringComparison.Ordinal) ? "long" : "tower";
            return MakeFallbackStyle(civ + "/stone", civ, civ, new HashSet<string> { civ }).Elements[key];
        }

        private static Dictionary<string, DemoFortress> CreateDefaultFortressTypes()
        {
            var result = new Dictionary<string, DemoFortress>(StringComparer.Ordinal);
            void Add(string type, string[] wall)
            {
                var full = new List<string>();
                for (int i = 0; i < 4; ++i)
                    full.AddRange(wall);
                result[type] = new DemoFortress(full);
            }

            Add("tiny", new[] { "gate", "tower", "short", "cornerIn", "short", "tower" });
            Add("small", new[] { "gate", "tower", "medium", "cornerIn", "medium", "tower" });
            Add("medium", new[] { "gate", "tower", "long", "cornerIn", "long", "tower" });
            Add("normal", new[] { "gate", "tower", "medium", "cornerIn", "medium", "cornerOut", "medium", "cornerIn", "medium", "tower" });
            Add("large", new[] { "gate", "tower", "long", "cornerIn", "long", "cornerOut", "long", "cornerIn", "long", "tower" });
            Add("veryLarge", new[] { "gate", "tower", "medium", "cornerIn", "medium", "cornerOut", "long", "cornerIn", "long", "cornerOut", "medium", "cornerIn", "medium", "tower" });
            Add("giant", new[] { "gate", "tower", "long", "cornerIn", "long", "cornerOut", "long", "cornerIn", "long", "cornerOut", "long", "cornerIn", "long", "tower" });
            return result;
        }

        private static DemoWallElement GetWallElement(DemoWallStyle style, string element)
        {
            if (style.Elements.TryGetValue(element, out var found))
                return found;

            var ret = style.Elements.TryGetValue("tower", out var tower)
                ? tower
                : new DemoWallElement(null, 0, 0, 0, 0);
            const double quarterBendFactor = 0.5;
            double quarterBend = SafeMath.PI * quarterBendFactor;
            string rawCiv = style.Key.Split('/')[0];
            bool hasRawCiv = style.KnownCivs.Contains(rawCiv);

            switch (element)
            {
                case "cornerIn":
                    foreach (var curve in style.Curves)
                        if (curve.Bend == quarterBend)
                            ret = curve;
                    if (ret.Bend != quarterBend)
                    {
                        ret.Angle += SafeMath.PI / 4;
                        ret.Indent = ret.Length / 4;
                        ret.Length = 0;
                        ret.Bend = SafeMath.PI / 2;
                    }
                    break;

                case "cornerOut":
                    foreach (var curve in style.Curves)
                        if (curve.Bend == quarterBend)
                        {
                            ret = curve;
                            ret.Angle += SafeMath.PI / 2;
                            ret.Indent -= ret.Indent * 2;
                        }
                    if (ret.Bend != quarterBend)
                    {
                        ret.Angle -= SafeMath.PI / 4;
                        ret.Indent = -ret.Length / 4;
                        ret.Length = 0;
                    }
                    ret.Bend = -SafeMath.PI / 2;
                    break;

                case "entry":
                    ret.TemplateName = null;
                    ret.Length = GetWallElement(style, "gate").Length;
                    break;

                case "entryTower":
                    ret.TemplateName = hasRawCiv ? "structures/" + rawCiv + "/defense_tower" : "structures/palisades_watchtower";
                    ret.Indent = ret.Length * -3;
                    ret.Length = GetWallElement(style, "gate").Length;
                    break;

                case "entryFort":
                    ret = style.Elements.TryGetValue("fort", out var fort) ? fort : ret;
                    ret.Angle -= SafeMath.PI;
                    ret.Length *= 1.5;
                    ret.Indent = ret.Length;
                    break;

                case "start":
                    if (style.Elements.TryGetValue("end", out var endElement))
                    {
                        ret = endElement;
                        ret.Angle += SafeMath.PI;
                    }
                    break;

                case "end":
                    if (style.Elements.TryGetValue("end", out endElement))
                        ret = endElement;
                    break;

                default:
                    if (element.StartsWith("gap_", StringComparison.Ordinal))
                    {
                        ret.TemplateName = null;
                        ret.Angle = 0;
                        ret.Length = double.Parse(element.Substring(4), CultureInfo.InvariantCulture);
                    }
                    else if (element.StartsWith("turn_", StringComparison.Ordinal))
                    {
                        ret.TemplateName = null;
                        ret.Bend = double.Parse(element.Substring(5), CultureInfo.InvariantCulture) * SafeMath.PI;
                        ret.Length = 0;
                    }
                    else
                    {
                        string civ = hasRawCiv ? rawCiv : style.FirstCiv;
                        string templateName = "structures/" + civ + "/" + element;
                        bool assumeCommon = style.TemplateRoot == null &&
                            (element == "outpost" || element == "house" || element == "barracks" ||
                             element.EndsWith("_tower", StringComparison.Ordinal));
                        if (style.TemplateExists(templateName) || assumeCommon)
                        {
                            ret.Indent = ret.Length * (element == "outpost" ||
                                element.EndsWith("_tower", StringComparison.Ordinal) ? -3 : 3.5);
                            ret.TemplateName = templateName;
                            ret.Length = 0;
                        }
                    }
                    break;
            }

            style.Elements[element] = ret;
            return ret;
        }

        private static List<(RmgenVector2D position, string? templateName, double angle)> GetWallAlignment(
            DemoWallStyle style, RmgenVector2D position, IReadOnlyList<string> wall, double orientation)
        {
            var alignment = new List<(RmgenVector2D, string?, double)>();
            var wallPosition = position;
            for (int i = 0; i < wall.Count; ++i)
            {
                var element = GetWallElement(style, wall[i]);
                alignment.Add((RmgenVector2D.Sub(wallPosition,
                    Rotated(new RmgenVector2D(element.Indent, 0), -orientation)),
                    element.TemplateName, orientation + element.Angle));

                if (i + 1 >= wall.Count)
                    continue;

                orientation += element.Bend;
                var nextElement = GetWallElement(style, wall[i + 1]);
                double distance = (element.Length + nextElement.Length) / 2 - style.Overlap;
                if (element.Bend != 0 && element.Indent != 0)
                {
                    distance += element.Indent * SafeMath.Sin(element.Bend);
                    wallPosition.Add(Rotated(new RmgenVector2D(element.Indent, 0), -orientation));
                }
                wallPosition.Add(Rotated(new RmgenVector2D(distance, 0), -orientation).Perpendicular());
            }
            return alignment;
        }

        private static RmgenVector2D GetCenterToFirstElement(
            List<(RmgenVector2D position, string? templateName, double angle)> alignment)
        {
            var result = new RmgenVector2D(0, 0);
            foreach (var align in alignment)
                result.Sub(RmgenVector2D.Div(align.position, alignment.Count));
            return result;
        }

        private static void PlaceWall(RandomMap map, DemoWallStyle style, RmgenVector2D position,
            IReadOnlyList<string> wall, int playerId, double orientation, IConstraint? constraints)
        {
            var constraint = constraints ?? new NullConstraint();
            foreach (var align in GetWallAlignment(style, position, wall, orientation))
                PlaceWallEntity(map, align.templateName, playerId, align.position, align.angle, constraint);
        }

        private static void PlaceCustomFortress(RandomMap map, DemoWallStyle style, RmgenVector2D centerPosition,
            DemoFortress fortress, int playerId, double orientation, IConstraint? constraints)
        {
            var centerToFirstElement = fortress.CenterToFirstElement ??
                GetCenterToFirstElement(GetWallAlignment(style, new RmgenVector2D(0, 0), fortress.Wall, 0));
            var position = Sum(centerPosition,
                Rotated(new RmgenVector2D(centerToFirstElement.X, 0), -orientation),
                Rotated(new RmgenVector2D(centerToFirstElement.Y, 0).Perpendicular(), -orientation));
            PlaceWall(map, style, position, fortress.Wall, playerId, orientation, constraints);
        }

        private static void PlaceLinearWall(RandomMap map, DemoWallStyle style, RmgenVector2D startPosition,
            RmgenVector2D targetPosition, IReadOnlyList<string> wallPart, int playerId,
            bool endWithFirst, IConstraint? constraints)
        {
            double totalLength = startPosition.DistanceTo(targetPosition);
            double wallPartLength = GetWallLength(style, wallPart);
            if (wallPart.Count == 0 || wallPartLength == 0)
                return;

            int numParts = (int)Math.Ceiling(totalLength / wallPartLength);
            if (endWithFirst)
                numParts = (int)Math.Ceiling((totalLength - GetWallElement(style, wallPart[0]).Length) / wallPartLength);

            double scaleFactor = totalLength / (numParts * wallPartLength);
            if (endWithFirst)
                scaleFactor = totalLength / (numParts * wallPartLength + GetWallElement(style, wallPart[0]).Length);

            double wallAngle = GetAngle(startPosition, targetPosition);
            double placeAngle = wallAngle - SafeMath.PI / 2;
            var position = startPosition;
            var constraint = constraints ?? new NullConstraint();
            for (int partIndex = 0; partIndex < numParts; ++partIndex)
                foreach (string elementName in wallPart)
                {
                    var wallElement = GetWallElement(style, elementName);
                    double wallLength = (wallElement.Length - style.Overlap) / 2;
                    var dist = Rotated(new RmgenVector2D(scaleFactor * wallLength, 0), -wallAngle);
                    position.Add(dist);
                    var place = RmgenVector2D.Add(position,
                        Rotated(new RmgenVector2D(0, wallElement.Indent), -wallAngle));
                    PlaceWallEntity(map, wallElement.TemplateName, playerId, place,
                        placeAngle + wallElement.Angle, constraint);
                    position.Add(dist);
                }

            if (endWithFirst)
            {
                var wallElement = GetWallElement(style, wallPart[0]);
                double wallLength = (wallElement.Length - style.Overlap) / 2;
                position.Add(Rotated(new RmgenVector2D(scaleFactor * wallLength, 0), -wallAngle));
                PlaceWallEntity(map, wallElement.TemplateName, playerId, position,
                    placeAngle + wallElement.Angle, constraint);
            }
        }

        private static void PlaceCircularWall(RandomMap map, DemoWallStyle style, RmgenVector2D center,
            double radius, IReadOnlyList<string> wallPart, int playerId, double orientation,
            double maxAngle, bool? endWithFirst, double maxBendOff, IConstraint? constraints)
        {
            _ = maxBendOff;
            if (radius == 0 || wallPart.Count == 0)
                return;
            bool closeWithFirst = endWithFirst ?? maxAngle < SafeMath.PI * 2 - 0.001;
            double totalLength = maxAngle * radius;
            double wallPartLength = GetWallLength(style, wallPart);
            if (wallPartLength == 0)
                return;
            int numParts = (int)Math.Ceiling(totalLength / wallPartLength);
            if (closeWithFirst)
                numParts = (int)Math.Ceiling((totalLength - GetWallElement(style, wallPart[0]).Length) / wallPartLength);

            double scaleFactor = totalLength / (numParts * wallPartLength);
            if (closeWithFirst)
                scaleFactor = totalLength / (numParts * wallPartLength + GetWallElement(style, wallPart[0]).Length);

            var constraint = constraints ?? new NullConstraint();
            double actualAngle = orientation;
            var position = RmgenVector2D.Add(center, Rotated(new RmgenVector2D(radius, 0), -actualAngle));
            for (int partIndex = 0; partIndex < numParts; ++partIndex)
                foreach (string elementName in wallPart)
                {
                    var wallElement = GetWallElement(style, elementName);
                    double addAngle = scaleFactor * (wallElement.Length - style.Overlap) / radius;
                    var target = RmgenVector2D.Add(center,
                        Rotated(new RmgenVector2D(radius, 0), -actualAngle - addAngle));
                    var place = Average(position, target);
                    double placeAngle = actualAngle + addAngle / 2;
                    place.Sub(Rotated(new RmgenVector2D(wallElement.Indent, 0), -placeAngle));
                    PlaceWallEntity(map, wallElement.TemplateName, playerId, place,
                        placeAngle + wallElement.Angle, constraint);
                    actualAngle += addAngle;
                    position = RmgenVector2D.Add(center, Rotated(new RmgenVector2D(radius, 0), -actualAngle));
                }

            if (closeWithFirst)
            {
                var wallElement = GetWallElement(style, wallPart[0]);
                double addAngle = scaleFactor * wallElement.Length / radius;
                var target = RmgenVector2D.Add(center,
                    Rotated(new RmgenVector2D(radius, 0), -actualAngle - addAngle));
                var place = Average(position, target);
                double placeAngle = actualAngle + addAngle / 2;
                PlaceWallEntity(map, wallElement.TemplateName, playerId, place,
                    placeAngle + wallElement.Angle, constraint);
            }
        }

        private static void PlacePolygonalWall(RandomMap map, DemoWallStyle style, RmgenVector2D centerPosition,
            double radius, IReadOnlyList<string> wallPart, string cornerWallElement, int playerId,
            double orientation, int numCorners, bool skipFirstWall, IConstraint? constraints)
        {
            var constraint = constraints ?? new NullConstraint();
            double angleAdd = SafeMath.PI * 2 / numCorners;
            double angleStart = orientation - angleAdd / 2;
            var corners = new List<RmgenVector2D>();
            for (int i = 0; i < numCorners; ++i)
                corners.Add(RmgenVector2D.Add(centerPosition,
                    Rotated(new RmgenVector2D(radius, 0), -angleStart - i * angleAdd)));

            for (int i = 0; i < numCorners; ++i)
            {
                double angleToCorner = GetAngle(corners[i], centerPosition);
                var corner = GetWallElement(style, cornerWallElement);
                PlaceWallEntity(map, corner.TemplateName, playerId, corners[i], angleToCorner, constraint);
                if (skipFirstWall && i == 0)
                    continue;

                double cornerLength = corner.Length / 2;
                double cornerAngle = angleToCorner + angleAdd / 2;
                int targetCorner = (i + 1) % numCorners;
                var cornerPosition = Rotated(new RmgenVector2D(cornerLength, 0), -cornerAngle).Perpendicular();
                PlaceLinearWall(map, style,
                    RmgenVector2D.Sub(corners[i], cornerPosition),
                    RmgenVector2D.Add(corners[targetCorner], cornerPosition),
                    wallPart, playerId, true, constraints);
            }
        }

        private static void PlaceIrregularPolygonalWall(RmgenRng rng, RandomMap map, DemoWallStyle style,
            RmgenVector2D centerPosition, double radius, string cornerWallElement, int playerId,
            double orientation, int numCorners, double irregularity, bool skipFirstWall,
            IReadOnlyList<IReadOnlyList<string>>? wallPartsAssortment, IConstraint? constraints)
        {
            wallPartsAssortment ??= BuildDefaultWallPartAssortment(radius);
            double angleToCover = SafeMath.PI * 2;
            var angleAddList = new List<double>();
            for (int i = 0; i < numCorners; ++i)
            {
                double angleAdd = angleToCover / (numCorners - i) * (1 + rng.RandFloat(-irregularity, irregularity));
                angleAddList.Add(angleAdd);
                angleToCover -= angleAdd;
            }

            var corners = new List<RmgenVector2D>();
            double angleActual = orientation - angleAddList[0] / 2;
            for (int i = 0; i < numCorners; ++i)
            {
                corners.Add(RmgenVector2D.Add(centerPosition,
                    Rotated(new RmgenVector2D(radius, 0), -angleActual)));
                if (i < numCorners - 1)
                    angleActual += angleAddList[i + 1];
            }

            var wallPartLengths = new List<double>();
            double maxWallPartLength = 0;
            foreach (var wallPart in wallPartsAssortment)
            {
                double length = GetWallLength(style, wallPart);
                wallPartLengths.Add(length);
                if (length > maxWallPartLength)
                    maxWallPartLength = length;
            }
            if (maxWallPartLength == 0)
                return;

            var wallPartList = new List<IReadOnlyList<string>>();
            for (int i = 0; i < numCorners; ++i)
            {
                IReadOnlyList<string> bestWallPart = Array.Empty<string>();
                double bestWallLength = double.PositiveInfinity;
                int targetCorner = (i + 1) % numCorners;
                double wallLength = corners[i].DistanceTo(corners[targetCorner]);
                double numWallParts = Math.Ceiling(wallLength / maxWallPartLength);
                for (int partIndex = 0; partIndex < wallPartsAssortment.Count; ++partIndex)
                {
                    double linearWallLength = numWallParts * wallPartLengths[partIndex];
                    if (linearWallLength < bestWallLength && linearWallLength > wallLength)
                    {
                        bestWallPart = wallPartsAssortment[partIndex];
                        bestWallLength = linearWallLength;
                    }
                }
                wallPartList.Add(bestWallPart);
            }

            var constraint = constraints ?? new NullConstraint();
            var cornerElement = GetWallElement(style, cornerWallElement);
            for (int i = 0; i < numCorners; ++i)
            {
                double angleToCorner = GetAngle(corners[i], centerPosition);
                PlaceWallEntity(map, cornerElement.TemplateName, playerId, corners[i], angleToCorner, constraint);
                if (skipFirstWall && i == 0)
                    continue;

                double cornerLength = cornerElement.Length / 2;
                int targetCorner = (i + 1) % numCorners;
                double startAngle = angleToCorner + angleAddList[i] / 2;
                double targetAngle = angleToCorner + angleAddList[targetCorner] / 2;
                var startAdjust = Rotated(new RmgenVector2D(cornerLength, 0).Perpendicular(), -startAngle);
                var targetAdjust = Rotated(new RmgenVector2D(cornerLength, 0), -targetAngle - SafeMath.PI / 2);
                PlaceLinearWall(map, style,
                    RmgenVector2D.Sub(corners[i], startAdjust),
                    RmgenVector2D.Add(corners[targetCorner], targetAdjust),
                    wallPartList[i], playerId, false, constraints);
            }
        }

        private static List<IReadOnlyList<string>> BuildDefaultWallPartAssortment(double radius)
        {
            var result = new List<IReadOnlyList<string>>
            {
                new[] { "short" },
                new[] { "medium" },
                new[] { "long" },
                new[] { "gate", "tower", "short" },
            };
            var centeredWallPart = new List<string> { "gate" };
            result.Add(new List<string>(centeredWallPart));
            foreach (var assortmentOriginal in new[]
            {
                new List<string> { "tower", "long" },
                new List<string> { "tower", "medium" },
            })
            {
                var wallPart = new List<string>(centeredWallPart);
                for (int j = 0; j < radius; ++j)
                {
                    if (j % 2 == 0)
                    {
                        var next = new List<string>(wallPart);
                        next.AddRange(assortmentOriginal);
                        wallPart = next;
                    }
                    else
                    {
                        var reversed = new List<string>(assortmentOriginal);
                        reversed.Reverse();
                        reversed.AddRange(wallPart);
                        wallPart = reversed;
                    }
                    result.Add(new List<string>(wallPart));
                }
            }
            return result;
        }

        private static void PlaceGenericFortress(RmgenRng rng, RandomMap map, DemoWallStyle style,
            RmgenVector2D center, double radius, int playerId, double irregularity,
            int gateOccurence, int maxTries, IConstraint? constraints)
        {
            if (radius <= 0)
                return;
            double startAngle = rng.RandomAngle();
            var actualOff = Rotated(new RmgenVector2D(radius, 0), -startAngle);
            double actualAngle = startAngle;
            double pointDistance = GetWallLength(style, new[] { "long", "tower" });
            if (pointDistance <= 0)
                return;

            int tries = 0;
            List<RmgenVector2D>? bestPointDerivation = null;
            double minOverlap = 1000;
            while (tries < maxTries && minOverlap > style.Overlap)
            {
                var pointDerivation = new List<RmgenVector2D>();
                while (true)
                {
                    double indent = rng.RandFloat(-irregularity * pointDistance, irregularity * pointDistance);
                    var tmp = Rotated(new RmgenVector2D(radius + indent, 0),
                        -actualAngle - pointDistance / radius);
                    double tmpAngle = GetAngle(actualOff, tmp);
                    actualOff.Add(Rotated(new RmgenVector2D(pointDistance, 0), -tmpAngle));
                    actualAngle = GetAngle(new RmgenVector2D(0, 0), actualOff);
                    pointDerivation.Add(actualOff);
                    double distanceToTarget = pointDerivation[0].DistanceTo(actualOff);
                    int numPoints = pointDerivation.Count;
                    if (numPoints > 3 && distanceToTarget < pointDistance)
                    {
                        double overlap = pointDistance - pointDerivation[^1].DistanceTo(pointDerivation[0]);
                        if (overlap < minOverlap)
                        {
                            minOverlap = overlap;
                            bestPointDerivation = pointDerivation;
                        }
                        break;
                    }
                    if (pointDerivation.Count > 512)
                        break;
                }
                ++tries;
            }

            if (bestPointDerivation == null || bestPointDerivation.Count == 0)
                return;

            var constraint = constraints ?? new NullConstraint();
            for (int pointIndex = 0; pointIndex < bestPointDerivation.Count; ++pointIndex)
            {
                var start = RmgenVector2D.Add(center, bestPointDerivation[pointIndex]);
                var target = RmgenVector2D.Add(center, bestPointDerivation[(pointIndex + 1) % bestPointDerivation.Count]);
                double angle = GetAngle(start, target);
                var element = GetWallElement(style, (pointIndex + 1) % gateOccurence == 0 ? "gate" : "long");
                if (element.TemplateName != null)
                {
                    var pos = RmgenVector2D.Add(start,
                        Rotated(new RmgenVector2D(start.DistanceTo(target) / 2, 0), -angle));
                    PlaceWallEntity(map, element.TemplateName, playerId, pos,
                        angle - SafeMath.PI / 2 + element.Angle, constraint);
                }

                start = RmgenVector2D.Add(center,
                    bestPointDerivation[(pointIndex + bestPointDerivation.Count - 1) % bestPointDerivation.Count]);
                angle = GetAngle(start, target);
                var tower = GetWallElement(style, "tower");
                var towerPos = RmgenVector2D.Add(center, bestPointDerivation[pointIndex]);
                PlaceWallEntity(map, tower.TemplateName, playerId, towerPos,
                    angle - SafeMath.PI / 2 + tower.Angle, constraint);
            }
        }

        private static double GetWallLength(DemoWallStyle style, IReadOnlyList<string> wall)
        {
            double length = 0;
            foreach (string element in wall)
                length += GetWallElement(style, element).Length - style.Overlap;
            return length;
        }

        private static void PlaceWallEntity(RandomMap map, string? templateName, int playerId,
            RmgenVector2D position, double angle, IConstraint constraint)
        {
            if (templateName == null || !map.InMapBounds(position))
                return;
            var floored = position;
            floored.Floor();
            if (constraint.Allows(floored))
                map.PlaceEntityPassable(templateName, playerId, position, angle);
        }

        private static RmgenVector2D Rotated(RmgenVector2D vector, double angle)
        {
            vector.Rotate(angle);
            return vector;
        }

        private static RmgenVector2D Sum(params RmgenVector2D[] vectors)
        {
            var result = new RmgenVector2D(0, 0);
            foreach (var vector in vectors)
                result.Add(vector);
            return result;
        }

        private static RmgenVector2D Average(RmgenVector2D a, RmgenVector2D b)
            => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

        private static double GetAngle(RmgenVector2D a, RmgenVector2D b)
            => SafeMath.Atan2(b.Y - a.Y, b.X - a.X);
    }

    /// <summary>sahel_watering_holes.js（逐字移植）——环形玩家间放射状水路、水洼和浅滩迁徙点；环境设置表驱动，游牧放置未移植。</summary>
    public sealed class SahelWateringHolesMap2 : StandardMap
    {
        protected override double HeightLand => 3;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            const string tGrass = "savanna_grass_a";
            const string tForestFloor = "savanna_forestfloor_a";
            const string tCliff = "savanna_cliff_b";
            const string tDirtRocksA = "savanna_dirt_rocks_c";
            const string tDirtRocksB = "savanna_dirt_rocks_a";
            const string tDirtRocksC = "savanna_dirt_rocks_b";
            const string tHill = "savanna_cliff_a";
            const string tRoad = "savanna_tile_a_red";
            const string tRoadWild = "savanna_tile_a_red";
            const string tGrassPatch = "savanna_grass_b";
            const string tShore = "savanna_riparian_bank";
            const string tWater = "savanna_riparian_wet";

            const string oBaobab = "gaia/tree/baobab";
            const string oFig = "gaia/fruit/date";
            const string oBerryBush = "gaia/fruit/berry_01";
            const string oWildebeest = "gaia/fauna_wildebeest";
            const string oFish = "gaia/fish/generic";
            const string oGazelle = "gaia/fauna_gazelle";
            const string oElephant = "gaia/fauna_elephant_african_bush";
            const string oGiraffe = "gaia/fauna_giraffe";
            const string oZebra = "gaia/fauna_zebra";
            const string oStoneLarge = "gaia/rock/desert_large";
            const string oStoneSmall = "gaia/rock/savanna_small";
            const string oMetalLarge = "gaia/ore/savanna_large";

            const string aGrass = "actor|props/flora/grass_savanna.xml";
            const string aGrassShort = "actor|props/flora/grass_medit_field.xml";
            const string aRockLarge = "actor|geology/stone_savanna_med.xml";
            const string aRockMedium = "actor|geology/stone_savanna_med.xml";
            const string aBushMedium = "actor|props/flora/bush_desert_dry_a.xml";
            const string aBushSmall = "actor|props/flora/bush_dry_a.xml";

            var pForest = new[]
            {
                tForestFloor + "|" + oBaobab,
                tForestFloor + "|" + oBaobab,
                tForestFloor,
            };

            const double heightSeaGround = -4;
            const double heightShallows = -2;
            const double heightHillTop = 35;
            const double heightOffsetBump = 2;

            InitContextNoBiome(rng, settings, tGrass);
            var map = Map;
            var mapCenter = map.GetCenter();
            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clShallows = new TileClass(MapSize);

            var (playerIDs, playerPosition, _, startAngle) = RmgenCommon.PlayerPlacementCircle(
                rng, map, NumPlayers, RmgenLibrary.FractionToTiles(0.35, MapSize));

            RmgenCommon.PlacePlayerBases(rng, map, settings, tGrass, ClPlayer, null,
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
                    TreesTemplate = oBaobab,
                    TreesCount = 5,
                    DecorativesTemplate = aGrassShort,
                });

            var riverStart = RmgenGeometry.DistributePointsOnCircle(NumPlayers,
                startAngle + SafeMath.PI / NumPlayers,
                RmgenLibrary.FractionToTiles(0.15, MapSize), mapCenter).points;
            var riverEnd = RmgenGeometry.DistributePointsOnCircle(NumPlayers,
                startAngle + SafeMath.PI / NumPlayers,
                RmgenLibrary.FractionToTiles(0.49, MapSize), mapCenter).points;

            for (int i = 0; i < NumPlayers; ++i)
            {
                int neighborID = (i + 1) % NumPlayers;
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, RmgenGeometry.DiskArea(RmgenLibrary.ScaleByMapSize(5, 30, MapSize)),
                        0.95, 0.6, double.PositiveInfinity, riverStart[i]),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightSeaGround, 4),
                        new TileClassPainter(clWater),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 5));

                RmgenLibrary.CreateArea(
                    new PathPlacer(rng, 0.2, 3 * RmgenLibrary.ScaleByMapSize(1, 4, MapSize), 0.2, 0.05)
                    {
                        Start = riverStart[i],
                        End = riverEnd[i],
                        Width = RmgenLibrary.ScaleByMapSize(10, 50, MapSize),
                    },
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tShore, tWater, tWater }, new[] { 1, 3 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightSeaGround, 4),
                        new TileClassPainter(clWater),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 5));

                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, RmgenGeometry.DiskArea(RmgenLibrary.ScaleByMapSize(5, 22, MapSize)),
                        0.95, 0.6, double.PositiveInfinity, riverEnd[i]),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightSeaGround, 4),
                        new TileClassPainter(clWater),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 5));

                RmgenCommon.CreatePassage(rng, map, playerPosition[i], playerPosition[neighborID],
                    10, 10, 4, tileClass: clShallows,
                    constraints: new HeightConstraint(map, double.NegativeInfinity, heightShallows),
                    startHeight: heightShallows, endHeight: heightShallows);

                var shallowPosition = Average(new[] { playerPosition[i], playerPosition[neighborID] });
                shallowPosition.Round();
                foreach (var objectSpec in new IGroupElement[]
                {
                    new ScatterObject(rng, oWildebeest, 5, 6, 0, 4),
                    new ScatterObject(rng, oElephant, 2, 3, 0, 4),
                })
                    RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(new[] { objectSpec }, true, clFood, shallowPosition), 0, null);
            }

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -6, 2,
                HeightPlacer.Mode.IncludeMinExcludeMax, tWater);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize), 0.3, 0.06,
                    double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 20),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 150, MapSize), 0.2, 0.1,
                    double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tGrass, tCliff, tHill }, new[] { 1, 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightHillTop, 3),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 15, clWater, 3),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers);

            double forestTrees = 0.02 * RmgenLibrary.ScaleByMapSize(160, 900, MapSize);
            double stragglerTrees = (1 - 0.02) * RmgenLibrary.ScaleByMapSize(160, 900, MapSize);
            var types = new[]
            {
                new object[] { new object[] { tForestFloor, tGrass, pForest }, new object[] { tForestFloor, pForest } },
            };
            double forestSize = forestTrees / (0.5 * RmgenLibrary.ScaleByMapSize(2, 8, MapSize) * NumPlayers);
            int num = (int)Math.Floor(forestSize / types.Length);
            foreach (var type in types)
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, forestTrees / num, 0.1, 0.1, double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 10, ClHill, 0, clWater, 2),
                    num);

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 84, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 128, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, size, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new object[] { tGrass, tDirtRocksA },
                            new object[] { tDirtRocksA, tDirtRocksB },
                            new object[] { tDirtRocksB, tDirtRocksC },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClHill, 0, ClPlayer, 20),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 32, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 80, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, size, 0.3, 0.06, 0.5),
                    new IPainter[] { new TerrainPainter(tGrassPatch, rng) },
                    RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClHill, 0, ClPlayer, 20),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            var group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3) },
                true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) },
                true, ClMetal);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20, ClMetal, 10, ClRock, 5, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                new ScatterObject(rng, aRockMedium, 1, 3, 0, 2),
            }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            foreach (var fauna in new (string template, double min, double max, double dist)[]
            {
                (oWildebeest, 5, 7, 4),
                (oGazelle, 2, 3, 2),
                (oElephant, 2, 3, 2),
                (oGiraffe, 2, 3, 2),
                (oZebra, 2, 3, 2),
            })
            {
                group = new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, fauna.template, fauna.min, fauna.max, 0, fauna.dist) }, true, clFood);
                RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                    RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 5),
                    3 * NumPlayers, 50);
            }

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oFish, 2, 3, 0, 2) },
                true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 14),
                    RmgenLibrary.StayClasses(clWater, 6),
                }),
                35 * NumPlayers, 60);

            group = new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oBerryBush, 5, 7, 0, 4) },
                true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oBaobab, oBaobab, oBaobab, oFig },
                RmgenLibrary.AvoidClasses(clWater, 5, ClForest, 1, ClHill, 1, ClPlayer, 12, ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            const int planetm = 4;
            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, aGrassShort, 1, 2, 0, 1, -SafeMath.PI / 8, SafeMath.PI / 8) });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 2, ClHill, 2, ClPlayer, 2),
                planetm * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aGrass, 2, 4, 0, 1.8, -SafeMath.PI / 8, SafeMath.PI / 8),
                new ScatterObject(rng, aGrassShort, 3, 6, 1.2, 2.5, -SafeMath.PI / 8, SafeMath.PI / 8),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClHill, 2, ClPlayer, 2, ClForest, 0),
                planetm * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aBushMedium, 1, 2, 0, 2),
                new ScatterObject(rng, aBushSmall, 2, 4, 0, 2),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 2, ClHill, 1, ClPlayer, 1),
                planetm * RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            return map.MakeExportable();
        }

        private static RmgenVector2D Average(IReadOnlyList<RmgenVector2D> points)
        {
            double x = 0;
            double y = 0;
            foreach (var point in points)
            {
                x += point.X;
                y += point.Y;
            }
            return new RmgenVector2D(x / points.Count, y / points.Count);
        }
    }

    internal static class PortKVectorExtensions
    {
        public static RmgenVector2D Scaled(this RmgenVector2D vector, double scale)
        {
            vector.Mult(scale);
            return vector;
        }
    }
}
