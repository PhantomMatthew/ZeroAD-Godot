using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>canyon.js（340 行）——中央环形湖、放射峡谷与中心岛。
    /// 上游 TILE_CENTERED_HEIGHT_MAP 标志在当前 RandomMap 基础库中未暴露；
    /// Walls="towers"、placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class CanyonMap2 : StandardMap
    {
        protected override double HeightLand => 3;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;

            const double heightSeaGround = -4;
            const double heightShallow = -2;
            const double heightLandValue = 3;
            const double heightRing = 4;
            const double heightHillValue = 20;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);

            var mapCenter = map.GetCenter();
            double radiusPlayers = RmgenLibrary.FractionToTiles(0.35, MapSize);
            double radiusCentralLake = RmgenLibrary.FractionToTiles(0.27, MapSize);
            double radiusCentralRingLand = RmgenLibrary.FractionToTiles(0.21, MapSize);
            double radiusCentralWaterRing = RmgenLibrary.FractionToTiles(0.17, MapSize);
            double radiusCentralIsland = RmgenLibrary.FractionToTiles(0.14, MapSize);
            double radiusCentralHill = RmgenLibrary.FractionToTiles(0.12, MapSize);

            var (playerIDs, playerPosition, _, startAngle) =
                RmgenCommon.PlayerPlacementCircle(rng, map, NumPlayers, radiusPlayers);

            int split = 1;
            if (MapSize == 128 && NumPlayers <= 2)
                split = 2;
            else if (MapSize == 192 && NumPlayers <= 3)
                split = 2;
            else if (MapSize == 256)
            {
                if (NumPlayers <= 3)
                    split = 3;
                else if (NumPlayers == 4)
                    split = 2;
            }
            else if (MapSize == 320)
            {
                if (NumPlayers <= 3)
                    split = 3;
                else if (NumPlayers == 4)
                    split = 2;
            }
            else if (MapSize == 384)
            {
                if (NumPlayers <= 3)
                    split = 4;
                else if (NumPlayers == 4)
                    split = 3;
                else if (NumPlayers == 5)
                    split = 2;
            }
            else if (MapSize == 448)
            {
                if (NumPlayers <= 2)
                    split = 5;
                else if (NumPlayers <= 4)
                    split = 4;
                else if (NumPlayers == 5)
                    split = 3;
                else if (NumPlayers == 6)
                    split = 2;
            }

            RmgenLibrary.CreateArea(
                new DiskPlacer(radiusCentralLake, mapCenter),
                new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                    heightSeaGround, 4),
                null);

            for (int m = 0; m < NumPlayers * split; ++m)
            {
                double angle = startAngle + (m + 0.5) * 2 * SafeMath.PI / (NumPlayers * split);
                var position1 = RadialPoint(mapCenter, RmgenLibrary.FractionToTiles(0.15, MapSize), angle);
                var position2 = RadialPoint(mapCenter, RmgenLibrary.FractionToTiles(0.6, MapSize), angle);
                RmgenLibrary.CreateArea(
                    new PathPlacer(rng, 0, RmgenLibrary.ScaleByMapSize(3, 9, MapSize), 0.2, 0.05)
                    {
                        Start = position1,
                        End = position2,
                        Width = RmgenLibrary.ScaleByMapSize(14, 40, MapSize),
                    },
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaGround, 4),
                    RmgenLibrary.AvoidClasses(ClPlayer, 5));
            }

            for (int m = 0; m < NumPlayers * split; ++m)
            {
                double angle = startAngle + m * 2 * SafeMath.PI / (NumPlayers * split);
                var position1 = RadialPoint(mapCenter, RmgenLibrary.FractionToTiles(0.05, MapSize), angle);
                var position2 = RadialPoint(mapCenter, RmgenLibrary.FractionToTiles(0.49, MapSize), angle);
                RmgenLibrary.CreateArea(
                    new PathPlacer(rng, 0, RmgenLibrary.ScaleByMapSize(3, 9, MapSize), 0.2, 0.05)
                    {
                        Start = position1,
                        End = position2,
                        Width = RmgenLibrary.ScaleByMapSize(10, 40, MapSize),
                    },
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightLandValue, 4),
                    null);
            }

            RmgenLibrary.CreateArea(
                new DiskPlacer(radiusCentralRingLand, mapCenter),
                new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightRing, 4),
                null);
            RmgenLibrary.CreateArea(
                new DiskPlacer(radiusCentralWaterRing, mapCenter),
                new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightShallow, 3),
                null);
            RmgenLibrary.CreateArea(
                new DiskPlacer(radiusCentralIsland, mapCenter),
                new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightRing, 3),
                null);
            RmgenLibrary.CreateArea(
                new DiskPlacer(radiusCentralHill, mapCenter),
                new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                    heightHillValue, 8),
                null);

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -6, 1,
                HeightPlacer.Mode.IncludeMinExcludeMax, biome.Water);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, 2,
                HeightPlacer.Mode.IncludeMinExcludeMax, biome.Shore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 2, 21,
                HeightPlacer.Mode.IncludeMinExcludeMax, biome.MainTerrain);
            RmgenLibrary.PaintTileClassBasedOnHeight(-6, 0.5,
                HeightPlacer.Mode.IncludeMinExcludeMax, clWater);

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                playerPosition, biome.RoadWild, biome.Road, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    ExtraBaseResourceConstraint = RmgenLibrary.AvoidClasses(clWater, 2),
                    StartingAnimal = true,
                    BerriesTemplate = biome.FruitBush,
                    Mines = new() { (biome.MetalLarge, (string?)null, (object?)null),
                                    (biome.StoneLarge, (string?)null, (object?)null) },
                    TreesTemplate = biome.Tree1,
                    TreesCount = 2,
                    DecorativesTemplate = biome.GrassShort,
                });

            if (rng.RandBool())
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { biome.MainTerrain, biome.Cliff, biome.MainTerrain },
                            new[] { 1, 2 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, 18, 2),
                        new TileClassPainter(ClHill),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 15, clWater, 2),
                    RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers);
            else
                RmgenCommon.CreateMountains(rng, map, biome.Cliff,
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 15, clWater, 2),
                    ClHill,
                    count: (int)SafeMath.Ceil(RmgenLibrary.ScaleByMapSize(1, 4, MapSize) *
                        NumPlayers));

            var (forestTreesInt, stragglerTreesInt) = RmgenCommon.GetTreeCounts(
                biome.TreesMin, biome.TreesMax, biome.ForestProbability, MapSize);
            double forestTrees = forestTreesInt;
            double stragglerTrees = stragglerTreesInt;
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
            GaiaEntities.CreateForests(rng, map,
                new object[] { biome.MainTerrain, biome.ForestFloor1, biome.ForestFloor2, pForest1, pForest2 },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 17, ClHill, 0, clWater, 2),
                ClForest, forestTrees, NumPlayers);

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
                        new LayeredPainter(new object[]
                        {
                            new object[] { biome.MainTerrain, biome.Tier1Terrain },
                            new object[] { biome.Tier1Terrain, biome.Tier2Terrain },
                            new object[] { biome.Tier2Terrain, biome.Tier3Terrain },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClHill, 0, ClDirt, 5,
                        ClPlayer, 12),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        patchSize, 0.5),
                    new IPainter[] { new TerrainPainter(biome.Tier4Terrain, rng), new TileClassPainter(ClDirt) },
                    RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClHill, 0, ClDirt, 5,
                        ClPlayer, 12),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.StoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                        new ScatterObject(rng, biome.StoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                    },
                    new IGroupElement[] { new ScatterObject(rng, biome.StoneSmall, 2, 5, 1, 3) },
                },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20, ClRock, 10,
                    ClHill, 1),
                ClRock);

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][] { new IGroupElement[] { new ScatterObject(rng, biome.MetalLarge, 1, 1, 0, 4) } },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20, ClMetal, 10,
                    ClRock, 5, ClHill, 1),
                ClMetal);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, biome.Fish, 1, 1, 0, 3) }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clWater, 8),
                    RmgenLibrary.AvoidClasses(clFood, 14),
                }),
                RmgenLibrary.ScaleByMapSize(400, 2000, MapSize), 100);

            int planetm = BiomeName == "generic/india" ? 8 : 1;
            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.RockLarge, 1, 2, 0, 1),
                        new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 2),
                    },
                    new IGroupElement[] { new ScatterObject(rng, biome.GrassShort, 1, 2, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.Grass, 2, 4, 0, 1.8),
                        new ScatterObject(rng, biome.GrassShort, 3, 6, 1.2, 2.5),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.BushMedium, 1, 2, 0, 2),
                        new ScatterObject(rng, biome.BushSmall, 2, 4, 0, 2),
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
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 0));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, ClHill, 1,
                    clFood, 20),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) } },
                new double[] { 3 * NumPlayers },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, ClHill, 1,
                    clFood, 10),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                RmgenLibrary.AvoidClasses(clWater, 5, ClForest, 7, ClHill, 1, ClPlayer, 12,
                    ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }

        private static RmgenVector2D RadialPoint(RmgenVector2D center, double radius, double angle)
        {
            var offset = new RmgenVector2D(radius, 0);
            offset.Rotate(-angle);
            return RmgenVector2D.Add(center, offset);
        }
    }

    /// <summary>gear.js（397 行）——高原中刻出互连齿轮状峡谷低地。
    /// placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class GearMap2 : StandardMap
    {
        private const double InitialHeight = 30;

        protected override double HeightLand => InitialHeight;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, InitialHeight, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;

            const double heightLandValue = 3;
            const double heightHillValue = 30;

            var clHill2 = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clLand = new TileClass(MapSize);
            var mapCenter = map.GetCenter();

            double playerCanyonRadius = RmgenLibrary.ScaleByMapSize(18, 32, MapSize);
            var (playerIDs, playerPosition, _, _) = RmgenCommon.PlayerPlacementCircle(
                rng, map, NumPlayers, RmgenLibrary.FractionToTiles(0.35, MapSize));

            for (int i = 0; i < NumPlayers; ++i)
                for (int j = 1; j <= 2; ++j)
                    RmgenLibrary.CreateArea(
                        new ClumpPlacer(rng, RmgenGeometry.DiskArea(playerCanyonRadius / j),
                            0.65, 0.1, double.PositiveInfinity, playerPosition[i]),
                        new IPainter[]
                        {
                            new TerrainPainter(biome.MainTerrain, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                                heightLandValue, 2),
                            new TileClassPainter(j == 1 || settings.Nomad ? clLand : ClPlayer),
                        },
                        null);

            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.16, MapSize)),
                    0.7, 0.1, double.PositiveInfinity, mapCenter),
                new IPainter[]
                {
                    new TerrainPainter(biome.MainTerrain, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightLandValue, 3),
                    new TileClassPainter(clLand),
                },
                null);

            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, 150, 0.6, 0.3, double.PositiveInfinity, mapCenter),
                new TileClassPainter(ClHill),
                null);

            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(9, 16, MapSize); ++i)
                RmgenLibrary.CreateArea(
                    new PathPlacer(rng, 0.4, 3 * RmgenLibrary.ScaleByMapSize(1, 4, MapSize),
                        0.1, 0)
                    {
                        Start = new RmgenVector2D(
                            rng.RandIntExclusive(1, MapSize),
                            rng.RandIntExclusive(1, MapSize)),
                        End = new RmgenVector2D(
                            rng.RandIntExclusive(1, MapSize),
                            rng.RandIntExclusive(1, MapSize)),
                        Width = RmgenLibrary.ScaleByMapSize(11, 16, MapSize),
                    },
                    new IPainter[]
                    {
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            heightHillValue, 3),
                        new TileClassPainter(clHill2),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 6, clHill2, 3, ClHill, 2));

            for (int g = 0; g < RmgenLibrary.ScaleByMapSize(5, 30, MapSize); ++g)
            {
                var position = new RmgenVector2D(
                    rng.RandIntInclusive(1, MapSize - 1),
                    rng.RandIntInclusive(1, MapSize - 1));

                var newarea = RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.06, MapSize)),
                        0.7, 0.1, double.PositiveInfinity, position),
                    new IPainter[]
                    {
                        new TerrainPainter(biome.MainTerrain, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            heightLandValue, 3),
                        new TileClassPainter(clLand),
                    },
                    RmgenLibrary.AvoidClasses(clLand, 6));

                if (newarea == null)
                    continue;

                var distances = new List<double>();
                double d1 = 9999;
                double d2 = 9999;
                int p1 = -1;
                int p2 = 0;

                for (int i = 0; i < NumPlayers; ++i)
                    distances.Add(position.DistanceTo(playerPosition[i]));

                for (int a = 0; a < NumPlayers; ++a)
                {
                    if (d1 >= distances[a])
                    {
                        d2 = d1;
                        d1 = distances[a];
                        p2 = p1;
                        p1 = a;
                    }
                    else if (d2 >= distances[a])
                    {
                        d2 = distances[a];
                        p2 = a;
                    }
                }

                foreach (int playerIndex in new[] { p1, p2 })
                {
                    if (playerIndex < 0 || playerIndex >= playerPosition.Count)
                        continue;

                    RmgenLibrary.CreateArea(
                        new PathPlacer(rng, 0.4, RmgenLibrary.ScaleByMapSize(3, 12, MapSize),
                            0.1, 0.1)
                        {
                            Start = position,
                            End = playerPosition[playerIndex],
                            Width = RmgenLibrary.ScaleByMapSize(11, 17, MapSize),
                        },
                        new IPainter[]
                        {
                            new TerrainPainter(biome.MainTerrain, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                                heightLandValue, 3),
                            new TileClassPainter(clLand),
                        },
                        null);
                }
            }

            for (int i = 0; i < NumPlayers; ++i)
            {
                int neighbor = i + 1 < NumPlayers ? i + 1 : 0;
                foreach (var position in new[] { playerPosition[neighbor], mapCenter })
                    RmgenLibrary.CreateArea(
                        new PathPlacer(rng, 0.4, 3 * RmgenLibrary.ScaleByMapSize(1, 4, MapSize),
                            0.1, 0)
                        {
                            Start = playerPosition[i],
                            End = position,
                            Width = RmgenLibrary.ScaleByMapSize(8, 13, MapSize),
                        },
                        new IPainter[]
                        {
                            new LayeredPainter(new object[] { biome.RoadWild, biome.Road }, new[] { 1 }, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                                heightLandValue, 2),
                            new TileClassPainter(clLand),
                            new TileClassPainter(ClHill),
                        },
                        null);
            }

            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, 150, 0.6, 0.3, double.PositiveInfinity, mapCenter),
                new TerrainPainter(biome.Road, rng),
                null);

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                playerPosition, biome.Road, biome.Road, playerIDs,
                cityPatchRadius: playerCanyonRadius / 3,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = biome.FruitBush,
                    Mines = new() { (biome.MetalLarge, (string?)null, (object?)null),
                                    (biome.StoneLarge, (string?)null, (object?)null) },
                    MinesDistance = 11,
                    TreesTemplate = biome.Tree1,
                    DecorativesTemplate = biome.GrassShort,
                });

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 3.1, 29,
                HeightPlacer.Mode.ExcludeMinExcludeMax, biome.Cliff);
            RmgenLibrary.PaintTileClassBasedOnHeight(3.1, 32,
                HeightPlacer.Mode.ExcludeMinExcludeMax, clHill2);

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        2, 2, relative: true),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 2),
                    RmgenLibrary.StayClasses(clLand, 2),
                }),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, biome.Cliff, biome.Hill },
                        new[] { 1, 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, 18, 2),
                    new TileClassPainter(ClHill),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 2, ClHill, 8, clHill2, 8),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                RmgenLibrary.ScaleByMapSize(10, 40, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, biome.Cliff, biome.MainTerrain },
                        new[] { 1, 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, 40, 2),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(clLand, 1, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(20, 150, MapSize));

            var (forestTreesInt, stragglerTreesInt) = RmgenCommon.GetTreeCounts(
                biome.TreesMin, biome.TreesMax, biome.ForestProbability, MapSize);
            double forestTrees = forestTreesInt;
            double stragglerTrees = stragglerTreesInt;
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
            GaiaEntities.CreateForests(rng, map,
                new object[] { biome.MainTerrain, biome.ForestFloor1, biome.ForestFloor2, pForest1, pForest2 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 1, ClForest, 15, ClHill, 1, clHill2, 0),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                ClForest, forestTrees, NumPlayers);

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
                        RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5,
                            ClPlayer, 4, clHill2, 0),
                        RmgenLibrary.StayClasses(clLand, 3),
                    }),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        patchSize, 0.5),
                    new IPainter[] { new TerrainPainter(biome.Tier4Terrain, rng), new TileClassPainter(ClDirt) },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5,
                            ClPlayer, 4, clHill2, 0),
                        RmgenLibrary.StayClasses(clLand, 3),
                    }),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.StoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                        new ScatterObject(rng, biome.StoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                    },
                    new IGroupElement[] { new ScatterObject(rng, biome.StoneSmall, 2, 5, 1, 3) },
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 3, ClRock, 10, ClHill, 1,
                        clHill2, 1),
                    RmgenLibrary.StayClasses(clLand, 2),
                }),
                ClRock);

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][] { new IGroupElement[] { new ScatterObject(rng, biome.MetalLarge, 1, 1, 0, 4) } },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 3, ClMetal, 10, ClRock, 5,
                        ClHill, 1, clHill2, 1),
                    RmgenLibrary.StayClasses(clLand, 2),
                }),
                ClMetal);

            int planetm = BiomeName == "generic/india" ? 8 : 1;
            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.RockLarge, 1, 2, 0, 1),
                        new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 2),
                    },
                    new IGroupElement[] { new ScatterObject(rng, biome.GrassShort, 1, 2, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.Grass, 2, 4, 0, 1.8),
                        new ScatterObject(rng, biome.GrassShort, 3, 6, 1.2, 2.5),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.BushMedium, 1, 2, 0, 2),
                        new ScatterObject(rng, biome.BushSmall, 2, 4, 0, 2),
                    },
                },
                new[]
                {
                    3 * RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, settings.CircularMap),
                    3 * RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, settings.CircularMap),
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                },
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, biome.Tree, 1, 1, 0, 1) }, true),
                0,
                RmgenLibrary.AvoidClasses(clLand, 5),
                RmgenLibrary.ScaleByMapSize(200, 800, MapSize), 50);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 4, ClHill, 1, clFood, 20,
                        clHill2, 1),
                    RmgenLibrary.StayClasses(clLand, 3),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) } },
                new double[] { 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 4, ClHill, 1, clFood, 10,
                        clHill2, 1),
                    RmgenLibrary.StayClasses(clLand, 3),
                }),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClHill, 1, ClPlayer, 9,
                        ClMetal, 6, ClRock, 6, clHill2, 1),
                    RmgenLibrary.StayClasses(clLand, 3),
                }),
                ClForest, stragglerTrees);

            for (int i = 0; i < rng.RandIntInclusive(3, 8); ++i)
                foreach (string template in new[] { "gaia/treasure/food_bin", "gaia/treasure/wood" })
                {
                    var offset = new RmgenVector2D(rng.RandFloat(0, 7), 0);
                    offset.Rotate(rng.RandomAngle());
                    map.PlaceEntityPassable(template, 0, RmgenVector2D.Add(mapCenter, offset),
                        rng.RandomAngle());
                }

            return map.MakeExportable();
        }
    }

    /// <summary>coast_range.js（357 行）——海岸大陆一侧堆出沿岸山脉与资源高原。
    /// placePlayersNomad 与水体环境设置按既有移植约定省略。</summary>
    public sealed class CoastRangeMap2 : StandardMap
    {
        private const double HeightSeaGroundValue = -5;

        protected override double HeightLand => HeightSeaGroundValue;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightSeaGroundValue, biome.Water, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;

            const double heightLandValue = 3;

            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clLand = new TileClass(MapSize);
            var clMountain = new TileClass(MapSize);
            var clNotMountain = new TileClass(MapSize);
            _ = RmgenLibrary.FractionToTiles(0.85, MapSize);

            var mapCenter = map.GetCenter();
            string pattern = settings.PlayerPlacement;
            var teams = RmgenCommon.GetTeamsArray(rng, settings);
            double startAngle = 0;
            if (pattern == "stronghold" || pattern == "river")
            {
                if (teams.Count != 2)
                    throw new InvalidOperationException("Too many teams for " + pattern + ", use circle or make two teams.");
                startAngle = 1.600;
            }
            if (pattern == "circle")
                startAngle = 2.600;

            var continentOffset = new RmgenVector2D(0, RmgenLibrary.FractionToTiles(0.10, MapSize));
            continentOffset.Rotate(startAngle);
            var continentPosition = RmgenVector2D.Add(mapCenter, continentOffset);
            continentPosition.Round();

            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.50, MapSize)),
                    0.98, 0.15, double.PositiveInfinity, continentPosition),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Water, biome.Shore, biome.MainTerrain },
                        new[] { 4, 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightLandValue, 4),
                    new TileClassPainter(clLand),
                },
                null);

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 3, 4,
                HeightPlacer.Mode.IncludeMinIncludeMax, biome.MainTerrain);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, 3,
                HeightPlacer.Mode.ExcludeMinExcludeMax, biome.Shore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -8, 1,
                HeightPlacer.Mode.ExcludeMinIncludeMax, biome.Water);

            double playerAngle = pattern == "circle"
                ? 2.435721 + 3.461476 / SafeMath.Pow(2, NumPlayers / 1.376771)
                : startAngle;
            double teamDist = pattern switch
            {
                "river" => 0.35,
                _ => 0.30,
            };
            double playerDist = pattern switch
            {
                "river" => 0.8,
                "stronghold" => 0.11,
                _ => 0.13,
            };

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, pattern,
                RmgenLibrary.FractionToTiles(teamDist, MapSize),
                RmgenLibrary.FractionToTiles(playerDist, MapSize),
                playerAngle);

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                playerPosition, biome.RoadWild, biome.Road, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = biome.FruitBush,
                    Mines = new() { (biome.MetalLarge, (string?)null, (object?)null),
                                    (biome.StoneLarge, (string?)null, (object?)null) },
                    TreesTemplate = biome.Tree1,
                    TreesCount = 2,
                    DecorativesTemplate = biome.GrassShort,
                });

            for (int m = 0; m < rng.RandIntInclusive(20, 34); ++m)
            {
                int elevRand = rng.RandIntInclusive(4, 12);
                RmgenLibrary.CreateArea(
                    new ChainPlacer(
                        rng,
                        7,
                        15,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(15, 20, MapSize)),
                        double.PositiveInfinity,
                        new RmgenVector2D(
                            RmgenLibrary.FractionToTiles(rng.RandFloat(0, 1), MapSize),
                            RmgenLibrary.FractionToTiles(rng.RandFloat(0, 1), MapSize)),
                        0,
                        new[] { (int)Math.Floor(RmgenLibrary.FractionToTiles(0.01, MapSize)) }),
                    new IPainter[]
                    {
                        new LooseLayeredPainter(new object[] { biome.Hill, biome.MainTerrain },
                            new double[] { Math.Floor(elevRand / 3.0), 40 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            elevRand, Math.Floor(elevRand / 3.0)),
                        new TileClassPainter(ClHill),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClPlayer, 16),
                        RmgenLibrary.StayClasses(clLand, 28),
                    }));
            }

            var nonMountainOffset = new RmgenVector2D(0, RmgenLibrary.FractionToTiles(0.10, MapSize));
            nonMountainOffset.Rotate(startAngle);
            nonMountainOffset.Rotate(SafeMath.PI);
            var nonMountainPosition = RmgenVector2D.Add(mapCenter, nonMountainOffset);
            nonMountainPosition.Round();
            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.48, MapSize)),
                    0.98, 0.15, double.PositiveInfinity, nonMountainPosition),
                new TileClassPainter(clNotMountain),
                null);

            for (int m = 0; m < rng.RandIntInclusive(120, 240); ++m)
            {
                int elevRand = rng.RandIntInclusive(18, 22);
                RmgenLibrary.CreateArea(
                    new ChainPlacer(
                        rng,
                        24,
                        28,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(5, 30, MapSize)),
                        double.PositiveInfinity,
                        new RmgenVector2D(
                            RmgenLibrary.FractionToTiles(rng.RandFloat(0, 1), MapSize),
                            RmgenLibrary.FractionToTiles(rng.RandFloat(0, 1), MapSize)),
                        0,
                        new[] { (int)Math.Floor(RmgenLibrary.FractionToTiles(0.01, MapSize)) }),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { biome.Hill, biome.MainTerrain, biome.Cliff },
                            new double[] { Math.Floor(elevRand / 3.0), 40 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            elevRand, rng.RandIntInclusive(18, 30)),
                        new TileClassPainter(clMountain),
                    },
                    RmgenLibrary.AvoidClasses(clNotMountain, 2, clMountain, 3));
            }

            for (int m = 0; m < rng.RandIntInclusive(100, 180); ++m)
            {
                int elevRand = rng.RandIntInclusive(24, 38);
                RmgenLibrary.CreateArea(
                    new ChainPlacer(
                        rng,
                        6,
                        18,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(8, 15, MapSize)),
                        double.PositiveInfinity,
                        new RmgenVector2D(
                            RmgenLibrary.FractionToTiles(rng.RandFloat(0, 1), MapSize),
                            RmgenLibrary.FractionToTiles(rng.RandFloat(0, 1), MapSize)),
                        0,
                        new[] { (int)Math.Floor(RmgenLibrary.FractionToTiles(0.01, MapSize)) }),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { biome.Cliff, biome.Hill, biome.MainTerrain },
                            new double[] { Math.Floor(elevRand / 3.0), 40 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            elevRand, rng.RandIntInclusive(30, 40)),
                        new TileClassPainter(clMountain),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(clBaseResource, 2),
                        RmgenLibrary.StayClasses(clMountain, 2),
                    }));
            }

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        2, 2, relative: true),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 10),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            var (forestTreesInt, stragglerTreesInt) = RmgenCommon.GetTreeCounts(
                biome.TreesMin, biome.TreesMax, biome.ForestProbability, MapSize);
            double forestTrees = forestTreesInt;
            double stragglerTrees = stragglerTreesInt;
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
            GaiaEntities.CreateDefaultForests(rng, map,
                new object[] { biome.MainTerrain, biome.ForestFloor1, biome.ForestFloor2, pForest1, pForest2 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 17, clMountain, 8,
                        clBaseResource, 2),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                ClForest, forestTrees);

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
                        RmgenLibrary.AvoidClasses(ClForest, 0, ClDirt, 5, ClPlayer, 12),
                        RmgenLibrary.StayClasses(clLand, 5),
                    }),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        patchSize, 0.5),
                    new IPainter[] { new TerrainPainter(biome.Tier4Terrain, rng), new TileClassPainter(ClDirt) },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClForest, 0, ClDirt, 5, ClPlayer, 12),
                        RmgenLibrary.StayClasses(clLand, 5),
                    }),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            GaiaEntities.CreateBalancedMetalMines(rng, map, NumPlayers,
                biome.MetalSmall, biome.MetalLarge, ClMetal,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clMountain, 4),
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 4, ClMetal, 6, ClRock, 6),
                }));

            GaiaEntities.CreateBalancedStoneMines(rng, map, NumPlayers,
                biome.StoneSmall, biome.StoneLarge, ClRock,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clMountain, 4),
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 4, ClMetal, 6, ClRock, 6),
                }));

            GaiaEntities.CreateBalancedStoneMines(rng, map, NumPlayers,
                biome.StoneSmall, biome.StoneSmall, ClRock,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clLand, 8),
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 16, clMountain, 8,
                        ClRock, 10),
                }));

            GaiaEntities.CreateBalancedStoneMines(rng, map, NumPlayers,
                biome.MetalSmall, biome.MetalSmall, ClMetal,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clLand, 8),
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 16, clMountain, 8,
                        ClMetal, 10, ClRock, 10),
                }));

            int planetm = BiomeName == "generic/india" ? 8 : 1;
            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.RockLarge, 1, 2, 0, 1),
                        new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 2),
                    },
                    new IGroupElement[] { new ScatterObject(rng, biome.GrassShort, 1, 2, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.Grass, 2, 4, 0, 1.8),
                        new ScatterObject(rng, biome.GrassShort, 3, 6, 1.2, 2.5),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.BushMedium, 1, 2, 0, 2),
                        new ScatterObject(rng, biome.BushSmall, 2, 4, 0, 2),
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
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0),
                    RmgenLibrary.StayClasses(clLand, 5),
                }));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2) },
                },
                new double[] { 4 * NumPlayers, 4 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, clFood, 20, clMountain, 4),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) } },
                new double[] { 5 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, clFood, 10, clMountain, 2),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, biome.Fish, 2, 3, 0, 2) } },
                new double[] { 70 * NumPlayers },
                RmgenLibrary.AvoidClasses(clLand, 2, clFood, 7),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 7, ClPlayer, 9, ClMetal, 6, ClRock, 6),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }

        /// <summary>复现上游 LayeredPainter 对多余 widths 的宽松行为。</summary>
        private sealed class LooseLayeredPainter : IPainter
        {
            private readonly List<ITerrain> _terrains = new();
            private readonly double[] _widths;
            private readonly RmgenRng _rng;

            public LooseLayeredPainter(IReadOnlyList<object> terrains, double[] widths, RmgenRng rng)
            {
                foreach (var terrain in terrains)
                    _terrains.Add(TerrainFactory.CreateTerrain(terrain));
                _widths = widths;
                _rng = rng;
            }

            public void Paint(Area area)
            {
                var map = RmgenLibrary.CurrentMap;
                var dist = new Dictionary<(int, int), int>();
                var queue = new Queue<(int, int)>();
                foreach (var p in area.GetPoints())
                {
                    var pt = ((int)p.X, (int)p.Y);
                    bool border = !area.Contains(new RmgenVector2D(pt.Item1 + 1, pt.Item2))
                        || !area.Contains(new RmgenVector2D(pt.Item1 - 1, pt.Item2))
                        || !area.Contains(new RmgenVector2D(pt.Item1, pt.Item2 + 1))
                        || !area.Contains(new RmgenVector2D(pt.Item1, pt.Item2 - 1));
                    if (border)
                    {
                        dist[pt] = 1;
                        queue.Enqueue(pt);
                    }
                }

                while (queue.Count > 0)
                {
                    var cur = queue.Dequeue();
                    int d = dist[cur] + 1;
                    foreach (var nb in new[]
                    {
                        (cur.Item1 + 1, cur.Item2), (cur.Item1 - 1, cur.Item2),
                        (cur.Item1, cur.Item2 + 1), (cur.Item1, cur.Item2 - 1),
                    })
                    {
                        if (dist.ContainsKey(nb))
                            continue;
                        if (!area.Contains(new RmgenVector2D(nb.Item1, nb.Item2)))
                            continue;
                        dist[nb] = d;
                        queue.Enqueue(nb);
                    }
                }

                foreach (var p in area.GetPoints())
                {
                    var pt = ((int)p.X, (int)p.Y);
                    int distance = dist.TryGetValue(pt, out int dd) ? dd : int.MaxValue;
                    double width = 0;
                    int i = 0;
                    for (; i < _widths.Length; ++i)
                    {
                        width += _widths[i];
                        if (width >= distance)
                            break;
                    }
                    _terrains[i].Place(map, _rng, p);
                }
            }
        }
    }
}
