using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>land_grab.js（逐字移植）——海底起底，玩家在弧形小岛上争夺同一大陆，
    /// 基地岛含码头，后续森林/矿/食物限大陆或岛屿对应 tileclass。环境设置与 placePlayersNomad
    /// 按既有移植约定省略。</summary>
    public sealed class LandGrabMap2 : StandardMap
    {
        protected override double HeightLand => -5;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.Water, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightLand = 3;
            const double heightHill = 18;
            const double heightOffsetBump = 2;

            var clFood = new TileClass(MapSize);
            var clLand = new TileClass(MapSize);
            var clIsland = new TileClass(MapSize);

            var pForest1 = new[]
            {
                biome.ForestFloor2 + "|" + biome.Tree1,
                biome.ForestFloor2 + "|" + biome.Tree2,
                biome.ForestFloor2,
            };
            var pForest2 = new[]
            {
                biome.ForestFloor1 + "|" + biome.Tree4,
                biome.ForestFloor1 + "|" + biome.Tree5,
                biome.ForestFloor1,
            };

            double startAngle = rng.RandomAngle();
            var playerIDs = RmgenCommon.SortAllPlayers(rng, settings);
            var (playerPosition, playerAngle) = PortLMapHelpers.PlayerPlacementCustomAngle(
                NumPlayers, RmgenLibrary.FractionToTiles(0.35, MapSize), mapCenter,
                i => startAngle - SafeMath.PI * (i + 1) / (NumPlayers + 1));

            for (int i = 0; i < NumPlayers; ++i)
            {
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, RmgenGeometry.DiskArea(RmgenCommon.DefaultPlayerBaseRadius(MapSize)),
                        0.8, 0.1, double.PositiveInfinity, playerPosition[i]),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { biome.Water, biome.Shore, biome.MainTerrain },
                            new[] { 1, 4 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightLand, 4),
                        new TileClassPainter(clIsland),
                        new TileClassPainter(settings.Nomad ? clLand : ClPlayer),
                    },
                    null);

                if (settings.Nomad)
                    continue;

                var dockLocation = RmgenCommon.FindLocationInDirectionBasedOnHeight(map,
                    playerPosition[i], mapCenter, -3, 2.6, 3);
                if (dockLocation.HasValue)
                    map.PlaceEntityPassable("skirmish/structures/default_dock", playerIDs[i],
                        dockLocation.Value, playerAngle[i] + SafeMath.PI);
            }

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome: null,
                playerPositions: playerPosition, cityPatchOuterTerrain: null, cityPatchInnerTerrain: null,
                playerIDs: playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = biome.FruitBush,
                    Mines = new()
                    {
                        (biome.MetalLarge, (string?)null, (object?)null),
                        (biome.StoneLarge, (string?)null, (object?)null),
                    },
                    Treasures = new() { ("gaia/treasure/wood", 14) },
                    TreesTemplate = biome.Tree1,
                    TreesCount = (int)RmgenLibrary.ScaleByMapSize(12, 30, MapSize),
                    DecorativesTemplate = biome.GrassShort,
                });

            var continentPosition = RmgenVector2D.Add(mapCenter,
                RotatedVector(0, RmgenLibrary.FractionToTiles(0.38, MapSize), -startAngle));
            continentPosition.Round();
            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.4, MapSize)),
                    0.8, 0.08, double.PositiveInfinity, continentPosition),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Water, biome.Shore, biome.MainTerrain },
                        new[] { 4, 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightLand, 4),
                    new TileClassPainter(clLand),
                },
                RmgenLibrary.AvoidClasses(clIsland, 8));

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(15, 80, MapSize),
                    0.2, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.MainTerrain, biome.MainTerrain }, new[] { 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightLand, 4),
                    new TileClassPainter(clLand),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.BorderClasses(clLand, 6, 3),
                    RmgenLibrary.AvoidClasses(clIsland, 8),
                }),
                RmgenLibrary.ScaleByMapSize(2, 15, MapSize) * 20,
                150);

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, 3,
                HeightPlacer.Mode.ExcludeMinExcludeMax, biome.Shore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -8, 1,
                HeightPlacer.Mode.ExcludeMinIncludeMax, biome.Water);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize),
                    0.3, 0.06, double.PositiveInfinity),
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
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 150, MapSize),
                    0.2, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, biome.Hill }, new[] { 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightHill, 2),
                    new TileClassPainter(ClHill),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clIsland, 10, ClHill, 15),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers);

            double biomeTreeTotal = RmgenLibrary.ScaleByMapSize(biome.TreesMin, biome.TreesMax, MapSize);
            double forestTrees = biome.ForestProbability * biomeTreeTotal;
            double stragglerTrees = (1 - biome.ForestProbability) * biomeTreeTotal;
            var forestTypes = new object[][]
            {
                new object[]
                {
                    new object[] { biome.ForestFloor2, biome.MainTerrain, pForest1 },
                    new object[] { biome.ForestFloor2, pForest1 },
                },
                new object[]
                {
                    new object[] { biome.ForestFloor1, biome.MainTerrain, pForest2 },
                    new object[] { biome.ForestFloor1, pForest2 },
                },
            };
            double forestSize = forestTrees / (RmgenLibrary.ScaleByMapSize(2, 8, MapSize) * NumPlayers) *
                (BiomeName == "generic/savanna" ? 2 : 1);
            double forestNum = Math.Floor(forestSize / forestTypes.Length);
            foreach (var type in forestTypes)
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, forestTrees / forestNum, 0.1, 0.1, double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClPlayer, 6, ClForest, 10, ClHill, 0),
                        RmgenLibrary.StayClasses(clLand, 7),
                    }),
                    forestNum);

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 84, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 128, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, patchSize, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new object[] { biome.MainTerrain, biome.Tier1Terrain },
                            new object[] { biome.Tier1Terrain, biome.Tier2Terrain },
                            new object[] { biome.Tier2Terrain, biome.Tier3Terrain },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5, clIsland, 0),
                        RmgenLibrary.StayClasses(clLand, 7),
                    }),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 32, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 80, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, patchSize, 0.3, 0.06, 0.5),
                    new IPainter[] { new TerrainPainter(biome.Tier4Terrain, rng) },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5, clIsland, 0),
                        RmgenLibrary.StayClasses(clLand, 7),
                    }),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.StoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, biome.StoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                }, true, ClRock),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClRock, 10, ClHill, 1),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, biome.StoneSmall, 2, 5, 1, 3) }, true, ClRock),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClRock, 10, ClHill, 1),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, biome.MetalLarge, 1, 1, 0, 4) }, true, ClMetal),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClMetal, 10, ClRock, 5, ClHill, 1),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 1) }, true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.RockLarge, 1, 2, 0, 1),
                    new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 2),
                }, true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4) }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, ClHill, 1, clFood, 20),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2) }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, ClHill, 1, clFood, 20),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 8, ClHill, 1, clFood, 20),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, biome.Fish, 2, 3, 0, 2) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clLand, 2, ClPlayer, 2, ClHill, 0, clFood, 14),
                60 * NumPlayers, 60);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClHill, 1, ClPlayer, 9, ClMetal, 6, ClRock, 6),
                    RmgenLibrary.StayClasses(clLand, 9),
                }),
                ClForest, stragglerTrees);

            int plantMultiplier = BiomeName == "generic/india" ? 8 : 1;
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.GrassShort, 1, 2, 0, 1,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClHill, 2, ClPlayer, 2, ClDirt, 0),
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                plantMultiplier * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.Grass, 2, 4, 0, 1.8,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                    new ScatterObject(rng, biome.GrassShort, 3, 6, 1.2, 2.5,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClHill, 2, ClPlayer, 2, ClDirt, 1, ClForest, 0),
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                plantMultiplier * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.BushMedium, 1, 2, 0, 2),
                    new ScatterObject(rng, biome.BushSmall, 2, 4, 0, 2),
                }),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClHill, 1, ClPlayer, 1, ClDirt, 1),
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                plantMultiplier * RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            return map.MakeExportable();
        }

        private static RmgenVector2D RotatedVector(double x, double y, double angle)
        {
            var vector = new RmgenVector2D(x, y);
            vector.Rotate(angle);
            return vector;
        }
    }

    /// <summary>hyrcanian_shores.js（逐字移植）——Caspian 海岸横穿地图，玩家沿岸排布，
    /// 后方高地、丘陵和两档森林共享同一陆地基底。环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class HyrcanianShoresMap2 : StandardMap
    {
        protected override double HeightLand => 1;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightSeaGround1 = -3;
            const double heightShore1 = -1.5;
            const double heightShore2 = 0;
            const double heightLand = 1;
            const double heightOffsetBump = 4;
            const double heightHill = 15;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clHighlands = new TileClass(MapSize);

            var pForestD = new[] { biome.ForestFloor2 + "|" + biome.Tree1, biome.ForestFloor2 };
            var pForestP = new[] { biome.ForestFloor1 + "|" + biome.Tree4, biome.ForestFloor1 };

            double waterPosition = RmgenLibrary.FractionToTiles(0.25, MapSize);
            double highlandsPosition = RmgenLibrary.FractionToTiles(0.75, MapSize);
            double startAngle = rng.RandomAngle();

            var playerIDs = RmgenCommon.SortAllPlayers(rng, settings);
            var playerPosition = PortLMapHelpers.PlayerPlacementLine(map, NumPlayers, startAngle,
                mapCenter, RmgenLibrary.FractionToTiles(0.2, MapSize));

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                playerPositions: playerPosition, cityPatchOuterTerrain: biome.RoadWild,
                cityPatchInnerTerrain: biome.Road, playerIDs: playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = biome.FruitBush,
                    Mines = new()
                    {
                        (biome.MetalLarge, (string?)null, (object?)null),
                        (biome.StoneLarge, (string?)null, (object?)null),
                    },
                    TreesTemplate = biome.Tree4,
                    TreesCount = 2,
                    DecorativesTemplate = biome.GrassShort,
                });

            var riverStart = new RmgenVector2D(0, MapSize);
            riverStart.RotateAround(startAngle, mapCenter);
            var riverEnd = new RmgenVector2D(MapSize, MapSize);
            riverEnd.RotateAround(startAngle, mapCenter);
            var waterTerrain = TerrainFactory.CreateTerrain(biome.Shore);
            var shoreTerrain = TerrainFactory.CreateTerrain(biome.ShoreBlend);
            PortLMapHelpers.PaintRiver(rng, map, riverStart, riverEnd,
                2 * waterPosition, RmgenLibrary.ScaleByMapSize(6, 25, MapSize),
                heightSeaGround1, heightLand,
                parallel: true, deviation: 0, meanderShort: 20, meanderLong: 0,
                waterFunc: (position, height, _) =>
                {
                    if (height < heightShore2)
                        clWater.Add(position);
                    (height < heightShore1 ? waterTerrain : shoreTerrain).Place(map, rng, position);
                });

            var highlandVertices = new List<RmgenVector2D>
            {
                new(0, MapSize - highlandsPosition),
                new(MapSize, MapSize - highlandsPosition),
                new(0, 0),
                new(MapSize, 0),
            };
            for (int i = 0; i < highlandVertices.Count; ++i)
            {
                var point = highlandVertices[i];
                point.RotateAround(startAngle, mapCenter);
                highlandVertices[i] = point;
            }
            RmgenLibrary.CreateArea(
                new ConvexPolygonPlacer(highlandVertices, double.PositiveInfinity),
                new TileClassPainter(clHighlands),
                null);

            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(60, 70, MapSize); ++i)
                RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.Fish, 2, 3, 0, 2),
                    }, true, clFood),
                    0,
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.StayClasses(clWater, 4),
                        RmgenLibrary.AvoidClasses(clFood, 8),
                    }),
                    NumPlayers,
                    100);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(10, 60, MapSize),
                    0.3, 0.06, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump, 3, relative: true),
                },
                RmgenLibrary.StayClasses(clHighlands, 1),
                RmgenLibrary.ScaleByMapSize(300, 600, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 150, MapSize),
                    0.2, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, biome.Hill }, new[] { 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightHill, 2),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, clWater, 5, ClHill, 15, clHighlands, 5),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers);

            double treeTotal = RmgenLibrary.ScaleByMapSize(1000, 3500, MapSize);
            double forestTrees = 0.85 * treeTotal;
            double stragglerTrees = 0.15 * treeTotal;
            const double highlandShare = 0.4;

            var mainlandForestTypes = new object[][]
            {
                new object[]
                {
                    new object[] { biome.ForestFloor2, biome.Tier1Terrain, pForestD },
                    new object[] { biome.ForestFloor2, pForestD },
                },
            };
            double mainlandForests = RmgenLibrary.ScaleByMapSize(20, 100, MapSize) /
                ((object[])mainlandForestTypes[0]).Length;
            foreach (var type in mainlandForestTypes)
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, forestTrees * (1.0 - highlandShare) / mainlandForests,
                        0.1, 0.1, double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, clWater, 3, ClForest, 10,
                        ClHill, 0, ClBaseResource, 3, clHighlands, 2),
                    mainlandForests);

            var highlandForestTypes = new object[][]
            {
                new object[]
                {
                    new object[] { biome.ForestFloor2, biome.Tier1Terrain, pForestP },
                    new object[] { biome.ForestFloor2, pForestP },
                },
            };
            double highlandForests = RmgenLibrary.ScaleByMapSize(8, 50, MapSize) /
                ((object[])highlandForestTypes[0]).Length;
            foreach (var type in highlandForestTypes)
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, forestTrees * highlandShare / highlandForests,
                        0.1, 0.1, double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClPlayer, 20, clWater, 3, ClForest, 10,
                            ClHill, 0, ClBaseResource, 3),
                        RmgenLibrary.StayClasses(clHighlands, 2),
                    }),
                    highlandForests,
                    30);

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 84, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 128, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, patchSize, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new object[] { biome.Tier1Terrain, biome.Tier2Terrain },
                            new object[] { biome.Tier2Terrain, biome.Tier3Terrain },
                            new object[] { biome.Tier3Terrain, biome.Tier4Terrain },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClHill, 0,
                        ClDirt, 5, ClPlayer, 4),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 32, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 80, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, patchSize, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { biome.Tier4Terrain, biome.Dirt }, new[] { 2 }, rng),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClHill, 0,
                        ClDirt, 5, ClPlayer, 6, ClBaseResource, 6),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.StoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, biome.StoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                }, true, ClRock),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 1, ClPlayer, 20, ClRock, 10, ClHill, 2),
                }),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, biome.StoneSmall, 2, 5, 1, 3) }, true, ClRock),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 1, ClPlayer, 20, ClRock, 10, ClHill, 2),
                }),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, biome.MetalLarge, 1, 1, 0, 4) }, true, ClMetal),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 1, ClPlayer, 20,
                        ClMetal, 10, ClRock, 5, ClHill, 2),
                }),
                RmgenLibrary.ScaleByMapSize(5, 20, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 1) }, true),
                0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.RockLarge, 1, 2, 0, 1),
                    new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 2),
                }, true),
                0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 20, ClHill, 0, clFood, 5),
                6 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, "gaia/fauna_goat", 2, 3, 0, 2) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 20, ClHill, 0, clFood, 20),
                3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 6, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, "gaia/fauna_boar", 2, 3, 0, 2) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 20, ClHill, 0, clFood, 20),
                2 * NumPlayers, 50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree3 },
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 1, ClHill, 1,
                    ClPlayer, 10, ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.GrassShort, 1, 2, 0, 1,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 2, ClHill, 2, ClPlayer, 2, ClDirt, 0),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.Grass, 2, 4, 0, 1.8,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                    new ScatterObject(rng, biome.GrassShort, 3, 6, 1.2, 2.5,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClHill, 2, ClPlayer, 2, ClDirt, 1, ClForest, 0),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.BushMedium, 1, 2, 0, 2),
                    new ScatterObject(rng, biome.BushSmall, 2, 4, 0, 2),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClHill, 1, ClPlayer, 1, ClDirt, 1),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            return map.MakeExportable();
        }
    }

    /// <summary>cappadocian_badlands.js（逐字移植）——沙漠荒地、中央绿洲和多级侵蚀岩丘，
    /// 使用上游显式 sahara/nubia/savanna biome 白名单。环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class CappadocianBadlandsMap2 : StandardMap
    {
        private static readonly string[] SupportedBiomeNames = { "sahara", "nubia", "savanna" };

        protected override double HeightLand => 10;

        protected override IReadOnlyList<string> SupportedBiomes => SupportedBiomeNames;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            var mapCenter = map.GetCenter();

            const string forestFloor = "desert_forestfloor_palms";
            const string berryBush = "gaia/fruit/grapes";
            const string camel = "gaia/fauna_camel";
            const string gazelle = "gaia/fauna_gazelle";
            const string giraffe = "gaia/fauna_giraffe";
            const string goat = "gaia/fauna_goat";
            const string wildebeest = "gaia/fauna_wildebeest";
            const string oasisTree = "gaia/tree/senegal_date_palm";
            const string bush2 = "actor|props/flora/bush_desert_dry_a.xml";
            const string bush4 = "actor|props/flora/plant_desert_a.xml";

            const double heightOffsetOasis = -11;
            const double heightOffsetHill1 = 16;
            const double heightOffsetHill2 = 16;
            const double heightOffsetHill3 = 16;
            const double heightOffsetBump = 2;

            var clHill1 = new TileClass(MapSize);
            var clOasis = new TileClass(MapSize);
            var clPatch = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);

            var bushes = new[] { biome.BushMedium, bush2, biome.BushSmall, bush4 };
            var pForest = new[]
            {
                forestFloor + "|" + biome.Tree1,
                forestFloor + "|" + biome.Tree2,
                forestFloor,
            };
            var pForestOasis = new[]
            {
                biome.ForestFloor2 + "|" + oasisTree,
                biome.ForestFloor2 + "|" + biome.Tree1,
                biome.ForestFloor2,
            };

            double oasisRadius = RmgenLibrary.ScaleByMapSize(14, 40, MapSize);
            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, null,
                RmgenLibrary.FractionToTiles(0.35, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize),
                rng.RandomAngle());

            if (!settings.Nomad)
                for (int i = 0; i < NumPlayers; ++i)
                    RmgenLibrary.CreateArea(
                        new ClumpPlacer(rng, RmgenGeometry.DiskArea(RmgenCommon.DefaultPlayerBaseRadius(MapSize)),
                            0.9, 0.5, double.PositiveInfinity, playerPosition[i]),
                        new TileClassPainter(ClPlayer),
                        null);

            if (!settings.Nomad)
                foreach (var position in playerPosition)
                    PortLMapHelpers.PaintCityPatch(rng, position, biome.RoadWild, biome.Road, 3, 10);

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome: null,
                playerPositions: playerPosition, cityPatchOuterTerrain: null, cityPatchInnerTerrain: null,
                playerIDs: playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = berryBush,
                    Mines = new()
                    {
                        (biome.MetalLarge, (string?)null, (object?)null),
                        (biome.StoneLarge, (string?)null, (object?)null),
                    },
                    TreesTemplate = biome.Tree1,
                });

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(40, 150, MapSize), 0.2, 0.1, 0),
                new IPainter[]
                {
                    new TerrainPainter(biome.Tier4Terrain, rng),
                    new TileClassPainter(clPatch),
                },
                RmgenLibrary.AvoidClasses(clPatch, 2, ClPlayer, 0),
                RmgenLibrary.ScaleByMapSize(5, 20, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(25, 100, MapSize), 0.2, 0.1, 0),
                new IPainter[]
                {
                    new TerrainPainter(new object[] { biome.Tier3Terrain, biome.Tier2Terrain }, rng),
                    new TileClassPainter(clPatch),
                },
                RmgenLibrary.AvoidClasses(clPatch, 2, ClPlayer, 0),
                RmgenLibrary.ScaleByMapSize(15, 50, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(25, 100, MapSize), 0.2, 0.1, 0),
                new IPainter[]
                {
                    new TerrainPainter(new object[] { biome.Dirt }, rng),
                    new TileClassPainter(clPatch),
                },
                RmgenLibrary.AvoidClasses(clPatch, 2, ClPlayer, 0),
                RmgenLibrary.ScaleByMapSize(15, 50, MapSize));

            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, RmgenGeometry.DiskArea(oasisRadius), 0.6, 0.15, 0, mapCenter),
                new IPainter[]
                {
                    new LayeredPainter(new object[]
                    {
                        new object[] { biome.Tier3Terrain, pForest },
                        new object[] { biome.ShoreBlend, pForestOasis },
                        biome.ShoreBlend,
                        biome.Shore,
                        biome.Water,
                    }, new[] { 2, 3, 1, 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetOasis, 8, relative: true),
                    new TileClassPainter(clOasis),
                },
                null);

            int num = (int)SafeMath.Round(SafeMath.PI * oasisRadius / 8);
            IConstraint constraint = new AndConstraint(new IConstraint[]
            {
                RmgenLibrary.BorderClasses(clOasis, 0, 3),
                RmgenLibrary.AvoidClasses(clOasis, 0),
            });
            for (int i = 0; i < num; ++i)
            {
                RmgenVector2D animalPosition;
                int radius = 0;
                double angle = 2 * SafeMath.PI / num * i;
                do
                {
                    var offset = new RmgenVector2D(radius, 0);
                    offset.Rotate(-angle);
                    animalPosition = RmgenVector2D.Add(mapCenter, offset);
                    animalPosition.Round();
                    ++radius;
                }
                while (!constraint.Allows(animalPosition) && radius < MapSize / 2);

                RmgenLibrary.CreateObjectGroup(
                    new RandomGroup(rng,
                        new IGroupElement[]
                        {
                            new ScatterObject(rng, giraffe, 2, 4, 0, 3),
                            new ScatterObject(rng, wildebeest, 3, 5, 0, 3),
                            new ScatterObject(rng, gazelle, 5, 7, 0, 3),
                        },
                        true, clFood, animalPosition),
                    0,
                    null);
            }

            constraint = new AndConstraint(new IConstraint[]
            {
                RmgenLibrary.BorderClasses(clOasis, 15, 0),
                RmgenLibrary.AvoidClasses(clFood, 5),
            });
            num = (int)SafeMath.Round(SafeMath.PI * oasisRadius / 16);
            for (int i = 0; i < num; ++i)
            {
                RmgenVector2D fishPosition;
                int radius = 0;
                double angle = 2 * SafeMath.PI / num * i;
                do
                {
                    var offset = new RmgenVector2D(radius, 0);
                    offset.Rotate(-angle);
                    fishPosition = RmgenVector2D.Add(mapCenter, offset);
                    ++radius;
                }
                while (!constraint.Allows(fishPosition) && radius < MapSize / 2);

                RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.Fish, 1, 1, 0, 1),
                    }, true, clFood, fishPosition),
                    0,
                    null);
            }

            var hillAreas = RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(50, 300, MapSize), 0.25, 0.1, 0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, biome.Tier3Terrain }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetHill1, 1, relative: true),
                    new TileClassPainter(clHill1),
                },
                RmgenLibrary.AvoidClasses(clOasis, 3, ClPlayer, 0, clHill1, 10),
                RmgenLibrary.ScaleByMapSize(10, 20, MapSize),
                100);

            hillAreas.AddRange(RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(25, 150, MapSize), 0.25, 0.1, 0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, biome.Tier3Terrain }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetHill2, 1, relative: true),
                    new TileClassPainter(clHill1),
                },
                RmgenLibrary.AvoidClasses(clOasis, 3, ClPlayer, 0, clHill1, 3),
                RmgenLibrary.ScaleByMapSize(15, 25, MapSize),
                100));

            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new RandomObject(rng, new[] { biome.RockMedium, bush2, biome.BushSmall }, 3, 8, 0, 2),
                }, true),
                0,
                RmgenLibrary.BorderClasses(clHill1, 0, 3),
                RmgenLibrary.ScaleByMapSize(40, 200, MapSize), 50,
                hillAreas);

            RmgenLibrary.CreateAreasInAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(25, 150, MapSize), 0.25, 0.1, 0),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, biome.Tier3Terrain }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetHill2, 1, relative: true),
                },
                RmgenLibrary.StayClasses(clHill1, 0),
                RmgenLibrary.ScaleByMapSize(15, 25, MapSize),
                50,
                hillAreas);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(12, 75, MapSize), 0.25, 0.1, 0),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, biome.Tier3Terrain }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetHill3, 1, relative: true),
                },
                RmgenLibrary.StayClasses(clHill1, 0),
                RmgenLibrary.ScaleByMapSize(15, 25, MapSize),
                50);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize), 0.3, 0.06, 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(clOasis, 0, ClPlayer, 0, clHill1, 2),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            double forestTotal = RmgenLibrary.ScaleByMapSize(500, 2500, MapSize);
            double forestTrees = 0.5 * forestTotal;
            double stragglerTrees = 0.5 * forestTotal;
            double defaultNumberOfForests = RmgenLibrary.ScaleByMapSize(8, 36, MapSize);
            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, forestTrees / defaultNumberOfForests, 0.15, 0.1, 0.5),
                new IPainter[]
                {
                    new TerrainPainter(new object[] { biome.Tier3Terrain, pForest }, rng),
                    new TileClassPainter(ClForest),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 1, clOasis, 10, ClForest, 10, clHill1, 1),
                defaultNumberOfForests,
                50);

            GaiaEntities.CreateBalancedMetalMines(rng, map, NumPlayers,
                biome.MetalSmall, biome.MetalLarge, ClMetal,
                RmgenLibrary.AvoidClasses(clOasis, 2, ClForest, 0,
                    ClPlayer, RmgenLibrary.ScaleByMapSize(15, 25, MapSize), clHill1, 1));

            GaiaEntities.CreateBalancedStoneMines(rng, map, NumPlayers,
                biome.StoneSmall, biome.StoneLarge, ClRock,
                RmgenLibrary.AvoidClasses(clOasis, 2, ClForest, 0,
                    ClPlayer, RmgenLibrary.ScaleByMapSize(15, 25, MapSize), clHill1, 1, ClMetal, 10));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, gazelle, 5, 7, 0, 4) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clOasis, 1, ClForest, 0, ClPlayer, 5, clHill1, 1, clFood, 10),
                RmgenLibrary.ScaleByMapSize(5, 20, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, goat, 2, 4, 0, 3) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clOasis, 1, ClForest, 0, ClPlayer, 5, clHill1, 1, clFood, 10),
                RmgenLibrary.ScaleByMapSize(5, 20, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, camel, 2, 4, 0, 2) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clOasis, 1, ClForest, 0, ClPlayer, 5, clHill1, 1, clFood, 10),
                RmgenLibrary.ScaleByMapSize(5, 20, MapSize), 50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2 },
                RmgenLibrary.AvoidClasses(clOasis, 1, ClForest, 0, clHill1, 1,
                    ClPlayer, 4, ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new RandomObject(rng, bushes, 2, 3, 0, 2),
                }),
                0,
                RmgenLibrary.AvoidClasses(clOasis, 1, clHill1, 1, ClPlayer, 0, ClForest, 0),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.RockMedium, 1, 2, 0, 2),
                }),
                0,
                RmgenLibrary.AvoidClasses(clOasis, 1, clHill1, 1, ClPlayer, 0, ClForest, 0),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize));

            return map.MakeExportable();
        }
    }

    /// <summary>kerala.js（逐字移植）——无 biome 的热带海岸，玩家沿岸线排布，
    /// 近海河带、岸滩修补和内陆山地/森林共同形成喀拉拉式布局。环境设置与 placePlayersNomad
    /// 按既有移植约定省略。</summary>
    public sealed class KeralMap2 : StandardMap
    {
        private static readonly string[] tGrass =
        {
            "tropic_grass_c",
            "tropic_grass_c",
            "tropic_grass_c",
            "tropic_grass_c",
            "tropic_grass_plants",
            "tropic_plants",
            "tropic_plants_b",
        };
        private const string tGrassA = "tropic_plants_c";
        private const string tGrassB = "tropic_plants_c";
        private const string tGrassC = "tropic_grass_c";
        private const string tForestFloor = "tropic_grass_plants";
        private static readonly string[] tCliff = { "tropic_cliff_a", "tropic_cliff_a", "tropic_cliff_a", "tropic_cliff_a_plants" };
        private const string tPlants = "tropic_plants";
        private const string tRoad = "tropic_citytile_a";
        private const string tRoadWild = "tropic_citytile_plants";
        private const string tShoreBlend = "tropic_beach_dry_plants";
        private const string tShore = "tropic_beach_dry";
        private const string tWater = "tropic_beach_wet";

        private const string oTree = "gaia/tree/toona";
        private const string oPalm = "gaia/tree/palm_tropic";
        private const string oStoneLarge = "gaia/rock/tropical_large";
        private const string oStoneSmall = "gaia/rock/tropical_small";
        private const string oMetalLarge = "gaia/ore/tropical_large";
        private const string oFish = "gaia/fish/generic";
        private const string oDeer = "gaia/fauna_deer";
        // 上游变量名 oSheep 实际指向老虎，照搬该怪癖。
        private const string oSheep = "gaia/fauna_tiger";
        private const string oBush = "gaia/fruit/berry_01";

        private const string aRockLarge = "actor|geology/stone_granite_large.xml";
        private const string aRockMedium = "actor|geology/stone_granite_med.xml";
        private const string aBush1 = "actor|props/flora/plant_tropic_a.xml";
        private const string aBush2 = "actor|props/flora/plant_lg.xml";
        private const string aBush3 = "actor|props/flora/plant_tropic_large.xml";

        protected override double HeightLand => 3;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tGrass);
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightSeaGround = -5;
            const double heightLand = 3;
            const double heightHill = 25;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);

            var pForestD = new[] { tForestFloor + "|" + oTree, tForestFloor };
            var pForestP = new[] { tForestFloor + "|" + oPalm, tForestFloor };

            double waterPosition = RmgenLibrary.FractionToTiles(0.31, MapSize);
            double playerPositionRadius = RmgenLibrary.FractionToTiles(0.55, MapSize);
            double mountainPosition = RmgenLibrary.FractionToTiles(0.69, MapSize);
            double startAngle = rng.RandomAngle();

            var playerIDs = RmgenCommon.SortAllPlayers(rng, settings);
            var playerPosition = PortLMapHelpers.PlayerPlacementLine(map, NumPlayers, 0,
                new RmgenVector2D(mapCenter.X, playerPositionRadius),
                RmgenLibrary.FractionToTiles(0.2, MapSize));
            for (int i = 0; i < playerPosition.Count; ++i)
            {
                var position = playerPosition[i];
                position.RotateAround(startAngle, mapCenter);
                playerPosition[i] = position;
            }

            RmgenCommon.PlacePlayerBases(rng, map, settings, tGrass[0], ClPlayer, biome: null,
                playerPositions: playerPosition, cityPatchOuterTerrain: tRoadWild,
                cityPatchInnerTerrain: tRoad, playerIDs: playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = oBush,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneLarge, (string?)null, (object?)null),
                    },
                    TreesTemplate = oTree,
                    TreesCount = (int)RmgenLibrary.ScaleByMapSize(12, 30, MapSize),
                    TreesMinDist = 12,
                    TreesMaxDist = 14,
                    TreesMinDistGroup = 1,
                    TreesMaxDistGroup = 3,
                });

            double coastAngle = startAngle - SafeMath.PI / 2;
            var riverStart = new RmgenVector2D(0, MapSize);
            riverStart.RotateAround(coastAngle, mapCenter);
            var riverEnd = new RmgenVector2D(0, 0);
            riverEnd.RotateAround(coastAngle, mapCenter);
            PortLMapHelpers.PaintRiver(rng, map, riverStart, riverEnd,
                2 * waterPosition, 8, heightSeaGround, heightLand,
                parallel: true, deviation: 0, meanderShort: 20, meanderLong: 0,
                waterFunc: (position, _, _) => clWater.Add(position));

            var mountainVertices = new List<RmgenVector2D>
            {
                new(mountainPosition, MapSize),
                new(mountainPosition, 0),
                new(MapSize, MapSize),
                new(MapSize, 0),
            };
            for (int i = 0; i < mountainVertices.Count; ++i)
            {
                var point = mountainVertices[i];
                point.RotateAround(coastAngle, mapCenter);
                mountainVertices[i] = point;
            }
            var areaMountains = RmgenLibrary.CreateArea(
                new ConvexPolygonPlacer(mountainVertices, double.PositiveInfinity),
                (IPainter?)null,
                null);

            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(20, 120, MapSize); ++i)
            {
                var position = new RmgenVector2D(
                    RmgenLibrary.FractionToTiles(rng.RandFloat(0.28, 0.34), MapSize),
                    RmgenLibrary.FractionToTiles(rng.RandFloat(0.1, 0.9), MapSize));
                position.RotateAround(coastAngle, mapCenter);
                position.Round();
                RmgenLibrary.CreateArea(
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(16, 30, MapSize)),
                        double.PositiveInfinity, position),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tGrass, tGrass }, new[] { 2 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightLand, 3),
                        new TileClassUnPainter(clWater),
                    },
                    null);
            }

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -6, 1,
                HeightPlacer.Mode.IncludeMinExcludeMax, tWater);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, 2.8,
                HeightPlacer.Mode.IncludeMinExcludeMax, tShoreBlend);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 0, 1,
                HeightPlacer.Mode.IncludeMinExcludeMax, tShore);
            RmgenLibrary.PaintTileClassBasedOnHeight(-6, 0.5,
                HeightPlacer.Mode.IncludeMinExcludeMax, clWater);

            var mountainAreas = areaMountains != null ? new[] { areaMountains } : Array.Empty<Area>();
            RmgenLibrary.CreateAreasInAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.1),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tCliff, tGrass }, new[] { 3 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightHill, 3),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 5, clWater, 2, ClBaseResource, 2),
                RmgenLibrary.ScaleByMapSize(5, 100, MapSize),
                3,
                mountainAreas);

            double treeTotal = RmgenLibrary.ScaleByMapSize(1000, 6000, MapSize);
            double forestTrees = 0.7 * treeTotal;
            double stragglerTrees = 0.3 * treeTotal;
            var forestTypes = new object[][]
            {
                new object[]
                {
                    new object[] { tGrass, tGrass, tGrass, tGrass, pForestD },
                    new object[] { tGrass, tGrass, tGrass, pForestD },
                },
                new object[]
                {
                    new object[] { tGrass, tGrass, tGrass, tGrass, pForestP },
                    new object[] { tGrass, tGrass, tGrass, pForestP },
                },
            };
            double forestSize = forestTrees / (RmgenLibrary.ScaleByMapSize(3, 6, MapSize) * NumPlayers);
            double forestNum = Math.Floor(forestSize / forestTypes.Length);
            foreach (var type in forestTypes)
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        forestTrees / (forestNum * Math.Floor(RmgenLibrary.ScaleByMapSize(2, 4, MapSize))),
                        0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 10, ClHill, 0, clWater, 8),
                    forestNum);

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        patchSize, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tGrassC, tGrassA, tGrassB }, new[] { 2, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 8, ClForest, 0, ClHill, 0,
                        ClPlayer, 12, ClDirt, 16),
                    RmgenLibrary.ScaleByMapSize(20, 80, MapSize));

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        patchSize, 0.5),
                    new IPainter[]
                    {
                        new TerrainPainter(tPlants, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 8, ClForest, 0, ClHill, 0,
                        ClPlayer, 12, ClDirt, 16),
                    RmgenLibrary.ScaleByMapSize(20, 80, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                }, true, ClRock),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3) }, true, ClRock),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) }, true, ClMetal),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20, ClMetal, 10, ClRock, 5, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) }, true),
                0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 0),
                3 * RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                    new ScatterObject(rng, aRockMedium, 1, 3, 0, 2),
                }, true),
                0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 0),
                3 * RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aBush1, 1, 2, 0, 1,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 2, ClHill, 2, ClPlayer, 2, ClDirt, 0),
                8 * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aBush2, 2, 4, 0, 1.8,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                    new ScatterObject(rng, aBush1, 3, 6, 1.2, 2.5,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClHill, 2, ClPlayer, 2, ClDirt, 1, ClForest, 0),
                8 * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aBush3, 1, 2, 0, 2),
                    new ScatterObject(rng, aBush2, 2, 4, 0, 2),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClHill, 1, ClPlayer, 1, ClDirt, 1),
                8 * RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oTree, oPalm },
                RmgenLibrary.AvoidClasses(clWater, 5, ClForest, 1, ClHill, 1,
                    ClPlayer, 12, ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oDeer, 5, 7, 0, 4) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 10, ClHill, 1, clFood, 20),
                3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oBush, 5, 7, 0, 4) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 6, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oSheep, 2, 3, 0, 2) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 22, ClHill, 1, clFood, 20),
                3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oFish, 2, 3, 0, 2) }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 14),
                    RmgenLibrary.StayClasses(clWater, 6),
                }),
                50 * NumPlayers, 60);

            return map.MakeExportable();
        }
    }

    file static class PortLMapHelpers
    {
        public static (List<RmgenVector2D> positions, List<double> angles) PlayerPlacementCustomAngle(
            int numPlayers, double radius, RmgenVector2D center, Func<int, double> playerAngleFunc)
        {
            var playerPosition = new List<RmgenVector2D>();
            var playerAngle = new List<double>();
            for (int i = 0; i < numPlayers; ++i)
            {
                double angle = playerAngleFunc(i);
                playerAngle.Add(angle);
                var offset = new RmgenVector2D(radius, 0);
                offset.Rotate(-angle);
                var position = RmgenVector2D.Add(center, offset);
                position.Round();
                playerPosition.Add(position);
            }

            return (playerPosition, playerAngle);
        }

        public static List<RmgenVector2D> PlayerPlacementLine(RandomMap map, int numPlayers,
            double angle, RmgenVector2D center, double width)
        {
            var playerPosition = new List<RmgenVector2D>();
            for (int i = 0; i < numPlayers; ++i)
            {
                var offset = new RmgenVector2D(
                    RmgenLibrary.FractionToTiles((i + 1.0) / (numPlayers + 1) - 0.5, map.GetSize()),
                    width * (i % 2 - 0.5));
                offset.Rotate(angle);
                var position = RmgenVector2D.Add(center, offset);
                position.Round();
                playerPosition.Add(position);
            }

            return playerPosition;
        }

        public static void PaintCityPatch(RmgenRng rng, RmgenVector2D position,
            object outerTerrain, object innerTerrain, double width, double radius)
            => RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, Math.Floor(RmgenGeometry.DiskArea(radius)),
                    0.6, 0.3, double.PositiveInfinity, position),
                new LayeredPainter(new object[] { outerTerrain, innerTerrain }, new[] { width }, rng),
                null);

        public static void PaintRiver(RmgenRng rng, RandomMap map,
            RmgenVector2D start, RmgenVector2D end, double width, double fadeDist,
            double heightRiverbed, double heightLandValue,
            bool parallel, double deviation, double meanderShort, double meanderLong,
            Action<RmgenVector2D, double, double>? waterFunc)
        {
            int mapSize = map.GetSize();
            double meanderShortTiles = RmgenLibrary.FractionToTiles(
                meanderShort / RmgenLibrary.ScaleByMapSize(35, 160, mapSize), mapSize);
            double meanderLongTiles = RmgenLibrary.FractionToTiles(
                meanderLong / RmgenLibrary.ScaleByMapSize(35, 100, mapSize), mapSize);

            double seed1 = rng.RandFloat(2, 3);
            double seed2 = rng.RandFloat(2, 3);
            double startingAngle1 = rng.RandFloat(0, 1);
            double startingAngle2 = rng.RandFloat(0, 1);

            double RiverCurve(double riverFraction, double riverStartAngle, double seed) =>
                meanderShortTiles * RndRiver(riverStartAngle +
                    RmgenLibrary.FractionToTiles(riverFraction, mapSize) / 128, seed) +
                meanderLongTiles * RndRiver(riverStartAngle +
                    RmgenLibrary.FractionToTiles(riverFraction, mapSize) / 256, seed);

            double riverLength = start.DistanceTo(end);
            var unitVecRiver = RmgenVector2D.Sub(start, end);
            unitVecRiver.Normalize();
            var unitVecPerpendicular = unitVecRiver.Perpendicular();

            double riverMinX = Math.Min(start.X, end.X);
            double riverMinZ = Math.Min(start.Y, end.Y);
            double riverMaxX = Math.Max(start.X, end.X);
            double riverMaxZ = Math.Max(start.Y, end.Y);

            for (int ix = 0; ix < mapSize; ++ix)
                for (int iz = 0; iz < mapSize; ++iz)
                {
                    var vecPoint = new RmgenVector2D(ix, iz);
                    double distanceToRiver = RmgenGeometry.DistanceOfPointFromLine(start, end, vecPoint);
                    var river = RmgenVector2D.Sub(vecPoint,
                        RmgenVector2D.Mult(unitVecPerpendicular, distanceToRiver));

                    if (river.X < riverMinX || river.X > riverMaxX ||
                        river.Y < riverMinZ || river.Y > riverMaxZ)
                        continue;

                    double riverFraction = river.DistanceTo(start) / riverLength;
                    double riverCurve1 = RiverCurve(riverFraction, startingAngle1, seed1);
                    double riverCurve2 = parallel ? riverCurve1 : RiverCurve(riverFraction, startingAngle2, seed2);
                    double dev = deviation * rng.RandFloat(-1, 1);

                    double shoreDist1 = riverCurve1 + distanceToRiver - dev - width / 2;
                    double shoreDist2 = riverCurve2 + distanceToRiver - dev + width / 2;

                    if (shoreDist1 < 0 && shoreDist2 > 0)
                    {
                        double height = heightRiverbed;
                        if (shoreDist1 > -fadeDist)
                            height += (heightLandValue - heightRiverbed) * (1 + shoreDist1 / fadeDist);
                        else if (shoreDist2 < fadeDist)
                            height += (heightLandValue - heightRiverbed) * (1 - shoreDist2 / fadeDist);

                        map.SetHeight(vecPoint, height);
                        waterFunc?.Invoke(vecPoint, height, riverFraction);
                    }
                }
        }

        private static double RndRiver(double f, double seed)
        {
            double rndRw = seed;
            for (int i = 0; i <= f; ++i)
                rndRw = 10 * (rndRw % 1);

            double rndRr = f % 1;
            double retVal = ((int)Math.Floor(f) % 2 != 0 ? -1 : 1) * rndRr * (rndRr - 1);

            int rndRe = (int)Math.Floor(rndRw) % 5;
            if (rndRe == 0)
                retVal *= 2.3 * (rndRr - 0.5) * (rndRr - 0.5);
            else if (rndRe == 1)
                retVal *= 2.6 * (rndRr - 0.3) * (rndRr - 0.7);
            else if (rndRe == 2)
                retVal *= 22 * (rndRr - 0.2) * (rndRr - 0.3) * (rndRr - 0.3) * (rndRr - 0.8);
            else if (rndRe == 3)
                retVal *= 180 * (rndRr - 0.2) * (rndRr - 0.2) * (rndRr - 0.4) *
                    (rndRr - 0.6) * (rndRr - 0.6) * (rndRr - 0.8);
            else if (rndRe == 4)
                retVal *= 2.6 * (rndRr - 0.5) * (rndRr - 0.7);

            return retVal;
        }
    }
}
