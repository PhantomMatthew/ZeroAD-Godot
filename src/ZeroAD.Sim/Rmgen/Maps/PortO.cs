using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>PortO 批次共享的小型 rmgen-common 包装：保留浮点 amount 与 ELEVATION_MODIFY 语义。</summary>
    internal static class PortOMapHelpers
    {
        public static void CreateBumps(RmgenRng rng, RandomMap map, IConstraint constraint,
            double? count = null, double? minSize = null, double? maxSize = null,
            double? spread = null, double failFraction = 0, double elevation = 2)
        {
            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng,
                    minSize ?? 1,
                    maxSize ?? Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, map.GetSize())),
                    spread ?? Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, map.GetSize())),
                    failFraction),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        elevation, 2, relative: true),
                },
                constraint,
                count ?? RmgenLibrary.ScaleByMapSize(100, 200, map.GetSize()));
        }

        public static void CreateHills(RmgenRng rng, RandomMap map,
            IReadOnlyList<object> terrainSet, IConstraint constraint, TileClass tileClass,
            double? count = null, double? minSize = null, double? maxSize = null,
            double? spread = null, double failFraction = 0.5, double elevation = 18,
            double elevationSmoothing = 2)
        {
            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng,
                    minSize ?? 1,
                    maxSize ?? Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, map.GetSize())),
                    spread ?? Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, map.GetSize())),
                    failFraction),
                new IPainter[]
                {
                    new LayeredPainter(terrainSet, new[] { 1, elevationSmoothing }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        elevation, elevationSmoothing),
                    new TileClassPainter(tileClass),
                },
                constraint,
                count ?? RmgenLibrary.ScaleByMapSize(1, 4, map.GetSize()));
        }

        public static void CreateLayeredPatches(RmgenRng rng, RandomMap map,
            IReadOnlyList<double> sizes, IReadOnlyList<object> terrains, double[] widths,
            IConstraint constraint, double count, TileClass? tileClass, double failFraction = 0.5)
        {
            foreach (double size in sizes)
            {
                var painters = new List<IPainter>
                {
                    new LayeredPainter(terrains, widths, rng),
                };
                if (tileClass != null)
                    painters.Add(new TileClassPainter(tileClass));

                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, map.GetSize())),
                        size, failFraction),
                    painters, constraint, count);
            }
        }

        public static void CreatePatches(RmgenRng rng, RandomMap map,
            IReadOnlyList<double> sizes, object terrain, IConstraint constraint,
            double count, TileClass tileClass, double failFraction = 0.5)
        {
            foreach (double size in sizes)
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, map.GetSize())),
                        size, failFraction),
                    new IPainter[]
                    {
                        new TerrainPainter(terrain, rng),
                        new TileClassPainter(tileClass),
                    },
                    constraint, count);
        }
    }

    /// <summary>guadalquivir_river.js（304 行）——水底基底上抬出半岛，再沿旋转轴切出 Guadalquivir 河。</summary>
    public sealed class GuadalquivirRiverMap2 : StandardMap
    {
        private static readonly string[] TGrass = { "medit_grass_field_a", "medit_grass_field_b" };
        private const string TForestFloorC = "medit_plants_dirt";
        private const string TForestFloorP = "medit_grass_shrubs";
        private const string TGrassA = "medit_grass_field_b";
        private const string TGrassB = "medit_grass_field_brown";
        private const string TGrassC = "medit_grass_field_dry";
        private const string TRoad = "medit_city_tile";
        private const string TRoadWild = "medit_city_tile";
        private const string TGrassPatch = "medit_grass_shrubs";
        private const string TShore = "sand_grass_25";
        private const string TWater = "medit_sand_wet";

        private const string OPoplar = "gaia/tree/poplar";
        private const string OApple = "gaia/fruit/apple";
        private const string OCarob = "gaia/tree/carob";
        private const string OBerryBush = "gaia/fruit/berry_01";
        private const string ODeer = "gaia/fauna_deer";
        private const string OFish = "gaia/fish/generic";
        private const string OSheep = "gaia/fauna_sheep";
        private const string OStoneLarge = "gaia/rock/mediterranean_large";
        private const string OStoneSmall = "gaia/rock/mediterranean_small";
        private const string OMetalLarge = "gaia/ore/mediterranean_large";

        private const string AGrass = "actor|props/flora/grass_soft_large_tall.xml";
        private const string AGrassShort = "actor|props/flora/grass_soft_large.xml";
        private const string AReeds = "actor|props/flora/reeds_pond_lush_a.xml";
        private const string ALillies = "actor|props/flora/water_lillies.xml";
        private const string ARockLarge = "actor|geology/stone_granite_large.xml";
        private const string ARockMedium = "actor|geology/stone_granite_med.xml";
        private const string ABushMedium = "actor|props/flora/bush_medit_me.xml";
        private const string ABushSmall = "actor|props/flora/bush_medit_sm.xml";

        private const double HeightSeaGround = -3;
        private const double HeightShallow = -1.5;
        private const double HeightShore = 2;
        private const double HeightLandValue = 3;

        protected override double HeightLand => HeightSeaGround;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, TWater);
            var map = Map;
            var mapCenter = map.GetCenter();

            var clFood = new TileClass(MapSize);
            var clLand = new TileClass(MapSize);
            var clRiver = new TileClass(MapSize);
            var clShallow = new TileClass(MapSize);

            var pForestP = new[]
            {
                TForestFloorP + TerrainFactory.TerrainSeparator + OPoplar,
                TForestFloorP,
            };
            var pForestC = new[]
            {
                TForestFloorC + TerrainFactory.TerrainSeparator + OCarob,
                TForestFloorC,
            };

            double startAngle = rng.RandomAngle();
            var continentCenter = new RmgenVector2D(
                RmgenLibrary.FractionToTiles(0.5, MapSize),
                RmgenLibrary.FractionToTiles(0.7, MapSize));
            continentCenter.RotateAround(startAngle, mapCenter);
            continentCenter.Round();

            RmgenLibrary.CreateArea(
                new ChainPlacer(rng, 2,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(5, 12, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(60, 700, MapSize)),
                    double.PositiveInfinity, continentCenter, 0,
                    new[] { (int)Math.Floor(RmgenLibrary.FractionToTiles(0.49, MapSize)) }),
                new IPainter[]
                {
                    new TerrainPainter(TGrass, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        HeightLandValue, 4),
                    new TileClassPainter(clLand),
                },
                null);

            var playerIDs = RmgenCommon.SortAllPlayers(rng, settings);
            var playerPosition = PlayerPlacementArcs(settings, playerIDs, continentCenter,
                RmgenLibrary.FractionToTiles(0.35, MapSize),
                -startAngle - 0.5 * SafeMath.PI, 0, 0.65 * SafeMath.PI);

            RmgenCommon.PlacePlayerBases(rng, map, settings, TGrass[0], ClPlayer, null,
                playerPosition,
                cityPatchOuterTerrain: TRoadWild,
                cityPatchInnerTerrain: TRoad,
                playerIDs: playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = OBerryBush,
                    Mines = new List<(string, string?, object?)>
                    {
                        (OMetalLarge, null, null),
                        (OStoneLarge, null, null),
                    },
                    TreesTemplate = OPoplar,
                    TreesCount = 2,
                    DecorativesTemplate = AGrassShort,
                });

            var riverStart = new RmgenVector2D(mapCenter.X, 0);
            riverStart.RotateAround(startAngle, mapCenter);
            var riverEnd = new RmgenVector2D(mapCenter.X, MapSize);
            riverEnd.RotateAround(startAngle, mapCenter);

            PaintRiver(rng, map,
                riverStart, riverEnd,
                RmgenLibrary.FractionToTiles(0.07, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 12, MapSize),
                HeightSeaGround, HeightShore,
                parallel: true, deviation: 1, meanderShort: 12, meanderLong: 0,
                constraint: RmgenLibrary.StayClasses(clLand, 0),
                waterFunc: (position, height, z) =>
                {
                    clRiver.Add(position);
                    TerrainFactory.CreateTerrain(TWater).Place(map, rng, position);

                    if (height < HeightShallow &&
                        (z > 0.3 && z < 0.4 || z > 0.5 && z < 0.6 || z > 0.7 && z < 0.8))
                    {
                        map.SetHeight(position, HeightShallow);
                        clShallow.Add(position);
                    }
                });

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, 3,
                HeightPlacer.Mode.ExcludeMinExcludeMax, TShore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -8, 1,
                HeightPlacer.Mode.ExcludeMinIncludeMax, TWater);

            PortOMapHelpers.CreateBumps(rng, map, new AndConstraint(new IConstraint[]
            {
                RmgenLibrary.AvoidClasses(ClPlayer, 20, clRiver, 1),
                RmgenLibrary.StayClasses(clLand, 3),
            }));

            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(500, 3000, 0.7, MapSize);
            GaiaEntities.CreateForests(rng, map,
                new object[] { TGrass, TForestFloorP, TForestFloorC, pForestC, pForestP },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 17, clRiver, 1),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                ClForest, forestTrees, NumPlayers);

            PortOMapHelpers.CreateLayeredPatches(rng, map,
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                    RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
                },
                new object[]
                {
                    new object[] { TGrass, TGrassA },
                    new object[] { TGrassA, TGrassB },
                    new object[] { TGrassB, TGrassC },
                },
                new[] { 1.0, 1.0 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClDirt, 3, ClPlayer, 8, clRiver, 1),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            PortOMapHelpers.CreatePatches(rng, map,
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                    RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
                },
                TGrassPatch,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClDirt, 3, ClPlayer, 8, clRiver, 1),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, OStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                        new ScatterObject(rng, OStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, OStoneSmall, 2, 5, 1, 3),
                    },
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClRock, 10, clRiver, 1),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                ClRock);

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, OMetalLarge, 1, 1, 0, 4) },
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClMetal, 10, ClRock, 5,
                        clRiver, 1),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                ClMetal);

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, ARockMedium, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, ARockLarge, 1, 2, 0, 1),
                        new ScatterObject(rng, ARockMedium, 1, 3, 0, 2),
                    },
                    new IGroupElement[] { new ScatterObject(rng, AGrassShort, 1, 2, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, AGrass, 2, 4, 0, 1.8),
                        new ScatterObject(rng, AGrassShort, 3, 6, 1.2, 2.5),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, ABushMedium, 1, 2, 0, 2),
                        new ScatterObject(rng, ABushSmall, 2, 4, 0, 2),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, Settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, Settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, Settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, Settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, Settings.CircularMap),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 1, ClDirt, 1, clRiver, 1),
                    RmgenLibrary.StayClasses(clLand, 6),
                }));

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, AReeds, 1, 3, 0, 1) },
                    new IGroupElement[] { new ScatterObject(rng, ALillies, 1, 2, 0, 1) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(800, MapSize, Settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(800, MapSize, Settings.CircularMap),
                },
                RmgenLibrary.StayClasses(clShallow, 0));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, ODeer, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, OSheep, 2, 3, 0, 2) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, clFood, 20, clRiver, 1),
                    RmgenLibrary.StayClasses(clLand, 3),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, OBerryBush, 5, 7, 0, 4) },
                },
                new double[] { rng.RandIntInclusive(1, 4) * NumPlayers + 2 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, clFood, 10, clRiver, 1),
                    RmgenLibrary.StayClasses(clLand, 3),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, OFish, 2, 3, 0, 2) },
                },
                new double[] { 40 * NumPlayers },
                RmgenLibrary.AvoidClasses(clLand, 2, clFood, 8),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { OPoplar, OCarob, OApple },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 9, ClMetal, 6, ClRock, 6,
                        clRiver, 1),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }

        private delegate void RiverWaterFunc(RmgenVector2D position, double height, double riverFraction);

        private static void PaintRiver(RmgenRng rng, RandomMap map,
            RmgenVector2D start, RmgenVector2D end, double width, double fadeDist,
            double heightRiverbed, double heightLandValue,
            bool parallel, double deviation, double meanderShort, double meanderLong,
            RiverWaterFunc? waterFunc = null, IConstraint? constraint = null)
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

            double RiverCurve(double riverFraction, double startAngle, double seed) =>
                meanderShortTiles * RndRiver(startAngle +
                    RmgenLibrary.FractionToTiles(riverFraction, mapSize) / 128, seed) +
                meanderLongTiles * RndRiver(startAngle +
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
                    if (constraint != null && !constraint.Allows(vecPoint))
                        continue;

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

        private static List<RmgenVector2D> PlayerPlacementArcs(MapSettings settings,
            IReadOnlyList<int> playerIDs, RmgenVector2D center, double radius,
            double mapAngle, double startAngle, double endAngle)
        {
            var (east, west) = PartitionPlayers(settings, playerIDs);
            var eastPosition = PlayerPlacementArc(east, center, radius, mapAngle + startAngle, mapAngle + endAngle);
            var westPosition = PlayerPlacementArc(west, center, radius, mapAngle - startAngle, mapAngle - endAngle);
            var result = new List<RmgenVector2D>();

            foreach (int playerID in playerIDs)
            {
                int eastIndex = east.IndexOf(playerID);
                result.Add(eastIndex != -1
                    ? eastPosition[eastIndex]
                    : westPosition[west.IndexOf(playerID)]);
            }

            return result;
        }

        private static List<RmgenVector2D> PlayerPlacementArc(IReadOnlyList<int> playerIDs,
            RmgenVector2D center, double radius, double startAngle, double endAngle)
        {
            var points = RmgenGeometry.DistributePointsOnCircularSegment(
                playerIDs.Count + 2, endAngle - startAngle, startAngle, radius, center).points;
            var result = new List<RmgenVector2D>();
            for (int i = 1; i < points.Count - 1; ++i)
            {
                var point = points[i];
                point.Round();
                result.Add(point);
            }
            return result;
        }

        private static (List<int> east, List<int> west) PartitionPlayers(MapSettings settings,
            IReadOnlyList<int> playerIDs)
        {
            var teamIDs = new List<int>();
            foreach (int playerID in playerIDs)
            {
                int team = RmgenCommon.GetPlayerTeam(settings, playerID);
                if (!teamIDs.Contains(team))
                    teamIDs.Add(team);
            }

            var teams = teamIDs
                .Select(teamID => playerIDs.Where(playerID =>
                    RmgenCommon.GetPlayerTeam(settings, playerID) == teamID).ToList())
                .ToList();

            int gaiaIndex = teamIDs.IndexOf(-1);
            if (gaiaIndex != -1)
            {
                var unteamed = teams[gaiaIndex];
                teams.RemoveAt(gaiaIndex);
                foreach (int playerID in unteamed)
                    teams.Add(new List<int> { playerID });
            }

            if (teams.Count == 1)
            {
                int idx = (int)Math.Floor(teams[0].Count / 2.0);
                teams = new List<List<int>>
                {
                    teams[0].Skip(idx).ToList(),
                    teams[0].Take(idx).ToList(),
                };
            }

            teams = teams.OrderByDescending(team => team.Count).ToList();

            var east = new List<int>();
            var west = new List<int>();
            foreach (var team in teams)
            {
                if (east.Count > west.Count)
                    west.AddRange(team);
                else
                    east.AddRange(team);
            }

            return (east, west);
        }
    }

    /// <summary>syria.js（302 行）——沙漠基底、玩家绿洲草斑、丘陵与稀疏棕榈林。</summary>
    public sealed class SyriaMap2 : StandardMap
    {
        private static readonly string[] TMainDirt = { "desert_dirt_rocks_1", "desert_dirt_cracks" };
        private const string TForestFloor1 = "forestfloor_dirty";
        private const string TForestFloor2 = "desert_forestfloor_palms";
        private const string TGrassSands = "desert_grass_a_sand";
        private const string TGrass = "desert_grass_a";
        private const string TSecondaryDirt = "medit_dirt_dry";
        private static readonly string[] TCliff = { "desert_cliff_persia_1", "desert_cliff_persia_2" };
        private static readonly string[] THill =
            { "desert_dirt_rocks_1", "desert_dirt_rocks_2", "desert_dirt_rocks_3" };
        private static readonly string[] TDirt = { "desert_dirt_rough", "desert_dirt_rough_2" };
        private const string TRoad = "desert_shore_stones";
        private const string TRoadWild = "desert_grass_a_stones";

        private const string OTamarix = "gaia/tree/tamarix";
        private const string OPalm = "gaia/tree/date_palm";
        private const string OPine = "gaia/tree/aleppo_pine";
        private const string OBush = "gaia/fruit/grapes";
        private const string OCamel = "gaia/fauna_camel";
        private const string OGazelle = "gaia/fauna_gazelle";
        private const string OLion = "gaia/fauna_lion";
        private const string OStoneLarge = "gaia/rock/desert_large";
        private const string OStoneSmall = "gaia/rock/desert_small";
        private const string OMetalLarge = "gaia/ore/desert_large";

        private const string ARock = "actor|geology/stone_desert_med.xml";
        private const string ABushA = "actor|props/flora/bush_desert_dry_a.xml";
        private const string ABushB = "actor|props/flora/bush_desert_dry_a.xml";

        private const double HeightLandValue = 1;
        private const double HeightHillValue = 22;
        private const double HeightOffsetBump = 2;

        protected override double HeightLand => HeightLandValue;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, TMainDirt);
            var map = Map;

            var clFood = new TileClass(MapSize);
            var clGrass = new TileClass(MapSize);

            var aBushes = new[] { ABushA, ABushB };
            var pForestP = new[]
            {
                TForestFloor2 + TerrainFactory.TerrainSeparator + OPalm,
                TForestFloor2,
            };
            var pForestT = new[]
            {
                TForestFloor1 + TerrainFactory.TerrainSeparator + OTamarix,
                TForestFloor2,
            };

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, settings.PlayerPlacement,
                RmgenLibrary.FractionToTiles(0.35, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize),
                rng.RandomAngle());

            for (int i = 0; i < NumPlayers; ++i)
            {
                if (!settings.Nomad)
                    RmgenLibrary.CreateArea(
                        new ClumpPlacer(rng,
                            RmgenGeometry.DiskArea(RmgenCommon.DefaultPlayerBaseRadius(MapSize)),
                            0.9, 0.5, double.PositiveInfinity, playerPosition[i]),
                        new TileClassPainter(ClPlayer), null);

                RmgenLibrary.CreateArea(
                    new ChainPlacer(rng, 2,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(5, 12, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(25, 60, MapSize)) /
                            (settings.Nomad ? 2 : 1),
                        double.PositiveInfinity, playerPosition[i], 0,
                        new[] { (int)Math.Floor(RmgenLibrary.ScaleByMapSize(16, 30, MapSize)) }),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { TGrassSands, TGrass }, new[] { 3.0 }, rng),
                        new TileClassPainter(clGrass),
                    },
                    null);
            }

            RmgenCommon.PlacePlayerBases(rng, map, settings, TMainDirt[0], ClPlayer, null,
                playerPosition,
                cityPatchOuterTerrain: TRoadWild,
                cityPatchInnerTerrain: TRoad,
                playerIDs: playerIDs,
                cityPatchRadius: 10,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = OBush,
                    Mines = new List<(string, string?, object?)>
                    {
                        (OMetalLarge, null, null),
                        (OStoneLarge, null, null),
                    },
                    MinesGroupElements = new List<IGroupElement>
                    {
                        new RandomObject(rng, aBushes, 2, 4, 2, 3),
                    },
                    TreesTemplate = rng.PickRandom(new[] { OPalm, OTamarix }),
                    TreesCount = 3,
                });

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize),
                    0.3, 0.06, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        HeightOffsetBump, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 13),
                RmgenLibrary.ScaleByMapSize(300, 800, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)),
                    0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { TCliff, THill }, new[] { 2.0 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        HeightHillValue, 2),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 3, clGrass, 1, ClHill, 10),
                RmgenLibrary.ScaleByMapSize(1, 3, MapSize) * NumPlayers * 3);

            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(400, 2000, 0.7, MapSize);
            var forestTypes = new[]
            {
                new object[]
                {
                    new object[] { TMainDirt, TForestFloor2, pForestP },
                    new object[] { TForestFloor2, pForestP },
                },
                new object[]
                {
                    new object[] { TMainDirt, TForestFloor1, pForestT },
                    new object[] { TForestFloor1, pForestT },
                },
            };
            double forestSize = (double)forestTrees /
                (RmgenLibrary.ScaleByMapSize(3, 6, MapSize) * NumPlayers);
            double num = Math.Floor(forestSize / forestTypes.Length);

            foreach (var type in forestTypes)
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        forestTrees / (num * Math.Floor(RmgenLibrary.ScaleByMapSize(2, 4, MapSize))),
                        0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2.0 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 1, clGrass, 1, ClForest, 10, ClHill, 1),
                    num);

            IConstraint patchConstraint =
                RmgenLibrary.AvoidClasses(ClHill, 0, ClForest, 0, ClPlayer, 8, clGrass, 1);

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
                        new LayeredPainter(new object[] { TSecondaryDirt, TDirt }, new[] { 1.0 }, rng),
                    },
                    patchConstraint,
                    RmgenLibrary.ScaleByMapSize(50, 90, MapSize));

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(6, 30, MapSize),
                RmgenLibrary.ScaleByMapSize(10, 50, MapSize),
                RmgenLibrary.ScaleByMapSize(16, 70, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        size, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { TSecondaryDirt, TDirt }, new[] { 1.0 }, rng),
                    },
                    patchConstraint,
                    RmgenLibrary.ScaleByMapSize(30, 90, MapSize));

            IConstraint mineConstraint =
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClRock, 10, ClHill, 1, clGrass, 1);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, OStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, OStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                    new RandomObject(rng, aBushes, 2, 4, 0, 2),
                }, true, ClRock),
                0, mineConstraint, RmgenLibrary.ScaleByMapSize(2, 8, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, OStoneSmall, 2, 5, 1, 3),
                    new RandomObject(rng, aBushes, 2, 4, 0, 2),
                }, true, ClRock),
                0, mineConstraint, RmgenLibrary.ScaleByMapSize(2, 8, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, OMetalLarge, 1, 1, 0, 4),
                    new RandomObject(rng, aBushes, 2, 4, 0, 2),
                }, true, ClMetal),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClMetal, 10, ClRock, 5,
                    ClHill, 1, clGrass, 1),
                RmgenLibrary.ScaleByMapSize(2, 8, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, ARock, 1, 3, 0, 1),
                }, true),
                0, RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, ABushB, 1, 2, 0, 1),
                    new ScatterObject(rng, ABushA, 1, 3, 0, 2),
                }, true),
                0, RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(50, 500, MapSize), 50);

            IConstraint huntConstraint =
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 1, ClHill, 1, clFood, 20, clGrass, 2);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, OGazelle, 5, 7, 0, 4),
                }, true, clFood),
                0, huntConstraint, 3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, OLion, 2, 3, 0, 2),
                }, true, clFood),
                0, huntConstraint, 3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, OCamel, 2, 3, 0, 2),
                }, true, clFood),
                0, huntConstraint, 3 * NumPlayers, 50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { OPalm, OTamarix, OPine },
                RmgenLibrary.AvoidClasses(ClForest, 1, ClHill, 1, ClPlayer, 1, ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { OPalm, OTamarix, OPine },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClHill, 1, ClPlayer, 1, ClMetal, 6,
                        ClRock, 6),
                    RmgenLibrary.StayClasses(clGrass, 3),
                }),
                ClForest, stragglerTrees * (settings.Nomad ? 3 : 1));

            return map.MakeExportable();
        }
    }

    /// <summary>gulf_of_bothnia.js（297 行）——图专属季节 biome，旋转三段 ChainPlacer 切出海湾。</summary>
    public sealed class GulfOfBothniaMap2 : StandardMap
    {
        protected override IReadOnlyList<string> SupportedBiomes => BiomeLoader.GulfOfBothniaBiomes;
        protected override double HeightLand => GetBiomeExtras().LandHeight;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, GetBiomeExtras().LandHeight, biome.MainTerrain, Settings.CircularMap);

        protected internal override void ApplyExtraEnvironment(RmgenEnvironment env, RmgenRng rng)
        {
            // 图专属 biome 的 Environment 字段不走 MapEnvironments 表；这里按 JSON 直接写入导出值。
            GetBiomeExtras().ApplyEnvironment(env);
        }

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            var mapCenter = map.GetCenter();
            var extras = GetBiomeExtras();

            var clLake = new TileClass(MapSize);
            var clWater = extras.IsLakeFrozen ? new TileClass(MapSize) : clLake;
            var clFood = new TileClass(MapSize);

            var pForest1 = new[]
            {
                biome.ForestFloor1 + TerrainFactory.TerrainSeparator + biome.Tree1,
                biome.ForestFloor1 + TerrainFactory.TerrainSeparator + biome.Tree2,
                biome.ForestFloor1,
            };
            var pForest2 = new[]
            {
                biome.ForestFloor1 + TerrainFactory.TerrainSeparator + biome.Tree4,
                biome.ForestFloor1 + TerrainFactory.TerrainSeparator + biome.Tree5,
                biome.ForestFloor1,
            };

            double startAngle = rng.RandomAngle();
            var playerIDs = RmgenCommon.SortAllPlayers(rng, settings);
            var playerPosition = new List<RmgenVector2D>();
            for (int i = 0; i < NumPlayers; ++i)
            {
                double angle = startAngle + 1.0 / 3.0 * SafeMath.PI *
                    (1 + 2 * (NumPlayers == 1 ? 1 : 2.0 * i / (NumPlayers - 1)));
                var offset = new RmgenVector2D(RmgenLibrary.FractionToTiles(0.35, MapSize), 0);
                offset.Rotate(-angle);
                var position = RmgenVector2D.Add(mapCenter, offset);
                position.Round();
                playerPosition.Add(position);
            }

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                playerPosition,
                cityPatchOuterTerrain: biome.RoadWild,
                cityPatchInnerTerrain: biome.Road,
                playerIDs: playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = biome.FruitBush,
                    Mines = new List<(string, string?, object?)>
                    {
                        (biome.MetalLarge, null, null),
                        (biome.StoneLarge, null, null),
                    },
                    TreesTemplate = biome.Tree1,
                    TreesCount = 2,
                    DecorativesTemplate = biome.RockMedium,
                });

            var gulfLakePositions = new[]
            {
                new { NumCircles = 200.0, X = RmgenLibrary.FractionToTiles(0, MapSize),
                    Radius = RmgenLibrary.FractionToTiles(0.175, MapSize) },
                new { NumCircles = 120.0, X = RmgenLibrary.FractionToTiles(0.3, MapSize),
                    Radius = RmgenLibrary.FractionToTiles(0.2, MapSize) },
                new { NumCircles = 100.0, X = RmgenLibrary.FractionToTiles(0.5, MapSize),
                    Radius = RmgenLibrary.FractionToTiles(0.225, MapSize) },
            };

            foreach (var gulfLake in gulfLakePositions)
            {
                var gulfOffset = new RmgenVector2D(gulfLake.X, 0);
                gulfOffset.Rotate(-startAngle);
                var position = RmgenVector2D.Add(mapCenter, gulfOffset);
                position.Round();

                RmgenLibrary.CreateArea(
                    new ChainPlacer(rng, 2,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(5, 16, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(35, gulfLake.NumCircles, MapSize)),
                        double.PositiveInfinity,
                        position,
                        0,
                        new[] { (int)Math.Floor(gulfLake.Radius) }),
                    new IPainter[]
                    {
                        new TerrainPainter(biome.MainTerrain, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            extras.SeaGroundHeight, 4),
                        new TileClassPainter(clLake),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, RmgenLibrary.ScaleByMapSize(20, 28, MapSize)));
            }

            if (extras.IsLakeFrozen)
            {
                var areas = RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, 4,
                        RmgenLibrary.ScaleByMapSize(16, 40, MapSize), 0.3),
                    new IPainter[]
                    {
                        new ElevationPainter(-6),
                        new TileClassPainter(clWater),
                    },
                    RmgenLibrary.StayClasses(clLake, 2),
                    RmgenLibrary.ScaleByMapSize(10, 40, MapSize));

                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.Fish, 1, 2, 0, 2),
                    }, true, clFood),
                    0,
                    RmgenLibrary.StayClasses(clWater, 1),
                    2 * RmgenLibrary.ScaleByMapSize(extras.FishMin, extras.FishMax, MapSize),
                    20,
                    areas);
            }

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, extras.ShoreHeight, extras.LandHeight,
                HeightPlacer.Mode.ExcludeMinExcludeMax, biome.Shore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, double.NegativeInfinity, extras.ShoreHeight,
                HeightPlacer.Mode.ExcludeMinIncludeMax, biome.Water);

            PortOMapHelpers.CreateBumps(rng, map, RmgenLibrary.AvoidClasses(clLake, 2, ClPlayer, 10));

            if (rng.RandBool())
                PortOMapHelpers.CreateHills(rng, map,
                    new object[] { biome.MainTerrain, biome.Cliff, biome.Cliff },
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 15, clLake, 0),
                    ClHill,
                    RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers,
                    failFraction: 0.5,
                    elevation: 18,
                    elevationSmoothing: 4);
            else
                RmgenCommon.CreateMountains(rng, map, biome.Cliff,
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 15, clLake, 0),
                    ClHill,
                    (int)(RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(20, 40, MapSize)));

            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(500, 3000, 0.7, MapSize);
            GaiaEntities.CreateDefaultForests(rng, map,
                new object[] { biome.ForestFloor1, biome.ForestFloor1, biome.ForestFloor1,
                    pForest1, pForest2 },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 16, ClHill, 0, clLake, 2),
                ClForest, forestTrees);

            IConstraint patchConstraint =
                RmgenLibrary.AvoidClasses(clLake, 6, ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 12);

            PortOMapHelpers.CreateLayeredPatches(rng, map,
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                    RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
                },
                new object[]
                {
                    new object[] { biome.MainTerrain, biome.Tier1Terrain },
                    new object[] { biome.Tier1Terrain, biome.Tier2Terrain },
                    new object[] { biome.Tier2Terrain, biome.Tier3Terrain },
                },
                new[] { 1.0, 1.0 },
                patchConstraint,
                RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            PortOMapHelpers.CreatePatches(rng, map,
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                    RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
                },
                biome.Tier2Terrain,
                patchConstraint,
                RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            GaiaEntities.CreateBalancedMetalMines(rng, map, NumPlayers,
                biome.MetalSmall, biome.MetalLarge, ClMetal,
                RmgenLibrary.AvoidClasses(clLake, 2, ClForest, 0, ClPlayer,
                    RmgenLibrary.ScaleByMapSize(15, 25, MapSize), ClHill, 1),
                randomness: 0.9);

            GaiaEntities.CreateBalancedStoneMines(rng, map, NumPlayers,
                biome.StoneSmall, biome.StoneLarge, ClRock,
                RmgenLibrary.AvoidClasses(clLake, 2, ClForest, 0, ClPlayer,
                    RmgenLibrary.ScaleByMapSize(15, 25, MapSize), ClHill, 1, ClMetal, 10),
                randomness: 0.9);

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
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, Settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, Settings.CircularMap),
                    RmgenLibrary.ScaleByMapSize(extras.BushMin, extras.BushMax, MapSize),
                    RmgenLibrary.ScaleByMapSize(extras.BushMin, extras.BushMax, MapSize),
                    RmgenLibrary.ScaleByMapSize(extras.BushMin, extras.BushMax, MapSize),
                },
                RmgenLibrary.AvoidClasses(clLake, 0, ClForest, 0, ClPlayer, 5, ClHill, 0,
                    ClBaseResource, 5));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                        { new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4) },
                    new IGroupElement[]
                        { new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(extras.HuntMin / 2, extras.HuntMax / 2, MapSize),
                    RmgenLibrary.ScaleByMapSize(extras.HuntMin / 2, extras.HuntMax / 2, MapSize),
                },
                RmgenLibrary.AvoidClasses(clLake, 3, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 20),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(extras.BerriesMin, extras.BerriesMax, MapSize),
                },
                RmgenLibrary.AvoidClasses(clLake, 3, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10),
                clFood);

            if (!extras.IsLakeFrozen)
                GaiaEntities.CreateFood(rng,
                    new IGroupElement[][]
                    {
                        new IGroupElement[] { new ScatterObject(rng, biome.Fish, 2, 3, 0, 2) },
                    },
                    new[]
                    {
                        4 * RmgenLibrary.ScaleByMapSize(extras.FishMin, extras.FishMax, MapSize),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(clFood, 12),
                        RmgenLibrary.StayClasses(clWater, 2),
                    }),
                    clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree3 },
                RmgenLibrary.AvoidClasses(clLake, 3, ClForest, 1, ClHill, 1, ClPlayer, 12,
                    ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }

        private GulfBiomeExtras GetBiomeExtras()
            => BiomeName switch
            {
                "gulf_of_bothnia/frozen_lake" => GulfBiomeExtras.FrozenLake,
                "gulf_of_bothnia/winter" => GulfBiomeExtras.Winter,
                _ => GulfBiomeExtras.LateSpring,
            };

        private readonly record struct GulfBiomeExtras(
            bool IsLakeFrozen,
            double SeaGroundHeight,
            double ShoreHeight,
            double LandHeight,
            double FishMin,
            double FishMax,
            double BushMin,
            double BushMax,
            double HuntMin,
            double HuntMax,
            double BerriesMin,
            double BerriesMax,
            Action<RmgenEnvironment> ApplyEnvironment)
        {
            public static readonly GulfBiomeExtras FrozenLake = new(
                true, 0.05, 0.4, 0.6,
                6, 25, 13, 50, 10, 80, 2, 10,
                env =>
                {
                    env.SkySet = "stormy";
                    env.SunColor = new RmgenColor(0.99866, 0.9995, 1.08284);
                    env.SunElevation = 0.462494;
                    env.SunRotation = -1.70047;
                    env.AmbientColor = new RmgenColor(0.334118, 0.332157, 0.334118);
                    env.Water.Type = "lake";
                    env.Water.Color = new RmgenColor(0.0784314, 0.237059, 0.299608);
                    env.Water.Tint = new RmgenColor(0.471, 0.75, 0.501961);
                    env.Water.Murkiness = 0.97;
                    env.Water.Waviness = 3;
                    env.Fog.FogThickness = 0.000005;
                    env.Fog.FogFactor = 0.002;
                    env.Fog.FogColor = new RmgenColor(0.8, 0.8, 0.8);
                    env.Postproc.PostprocEffect = "hdr";
                    env.Postproc.Brightness = 0.015625;
                    env.Postproc.Saturation = 0.96;
                    env.Postproc.Contrast = 0.98;
                    env.Postproc.Bloom = 0.16;
                });

            public static readonly GulfBiomeExtras LateSpring = new(
                false, -3, 1, 3,
                20, 100, 5, 50, 5, 40, 2, 10,
                env =>
                {
                    env.SkySet = "stormy";
                    env.SunColor = new RmgenColor(1.00866, 0.9595, 1.00284);
                    env.SunElevation = 0.689049;
                    env.SunRotation = -0.842871;
                    env.AmbientColor = new RmgenColor(0.319608, 0.394118, 0.503922);
                    env.Water.Type = "lake";
                    env.Water.Color = new RmgenColor(0.154, 0.31, 0.31);
                    env.Water.Tint = new RmgenColor(0.133, 0.725, 0.855);
                    env.Water.Murkiness = 0.94;
                    env.Water.Waviness = 5;
                    env.Fog.FogThickness = 0.00005313;
                    env.Fog.FogFactor = 0.00105664;
                    env.Fog.FogColor = new RmgenColor(0.8, 0.8, 0.9);
                    env.Postproc.PostprocEffect = "hdr";
                    env.Postproc.Brightness = 0.0041797;
                    env.Postproc.Saturation = 0.98;
                    env.Postproc.Contrast = 1;
                    env.Postproc.Bloom = 0.14;
                });

            public static readonly GulfBiomeExtras Winter = new(
                false, -3, 1, 3,
                5, 30, 5, 50, 2, 30, 10, 50,
                env =>
                {
                    env.SkySet = "stormy";
                    env.SunColor = new RmgenColor(0.74866, 0.7495, 0.67284);
                    env.SunElevation = 0.502494;
                    env.SunRotation = -0.926047;
                    env.AmbientColor = new RmgenColor(0.464706, 0.476471, 0.519608);
                    env.Water.Type = "lake";
                    env.Water.Color = new RmgenColor(0.024, 0.162, 0.182);
                    env.Water.Tint = new RmgenColor(0.133, 0.725, 0.855);
                    env.Water.Murkiness = 0.94;
                    env.Water.Waviness = 7;
                    env.Postproc.PostprocEffect = "hdr";
                    env.Postproc.Brightness = 0.015625;
                    env.Postproc.Saturation = 0.96;
                    env.Postproc.Contrast = 0.98;
                    env.Postproc.Bloom = 0.16;
                });
        }
    }

    /// <summary>cantabrian_highlands.js（297 行）——玩家先立于山台，湖泊/森林/矿物围绕高地散布。</summary>
    public sealed class CantabrianHighlandsMap2 : StandardMap
    {
        private const double HeightSeaGround = -7;
        private const double HeightLandValue = 3;
        private const double HeightHillValue = 20;

        protected override double HeightLand => HeightLandValue;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLandValue, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;

            var tGrass = new[] { biome.Tier1Terrain, biome.Tier2Terrain };
            var pForestD = new[]
            {
                biome.ForestFloor2 + TerrainFactory.TerrainSeparator + biome.Tree1,
                biome.ForestFloor2 + TerrainFactory.TerrainSeparator + biome.Tree2,
                biome.ForestFloor2,
            };
            var pForestP = new[]
            {
                biome.ForestFloor1 + TerrainFactory.TerrainSeparator + biome.Tree4,
                biome.ForestFloor1 + TerrainFactory.TerrainSeparator + biome.Tree5,
                biome.ForestFloor1,
            };

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);

            double playerHillRadius = RmgenCommon.DefaultPlayerBaseRadius(MapSize) /
                (settings.Nomad ? 1.5 : 1);
            var startingPlacement = DetailedPlayerPlacementByPattern(rng, map, settings,
                settings.PlayerPlacement,
                RmgenLibrary.FractionToTiles(0.35, MapSize),
                RmgenLibrary.FractionToTiles(0.02, MapSize) + 7,
                0);

            var hillPositions = startingPlacement.TeamPosition ?? startingPlacement.PlayerPosition;
            var hillAngles = startingPlacement.TeamAngle ?? startingPlacement.PlayerAngle;
            for (int i = 0; i < hillPositions.Count; ++i)
            {
                double hillRadius = (startingPlacement.StrongholdRadius?[i] ?? 0) + playerHillRadius;
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, RmgenGeometry.DiskArea(hillRadius),
                        0.95, 0.6, double.PositiveInfinity, hillPositions[i]),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { biome.Cliff, biome.Hill }, new[] { 2.0 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            HeightHillValue, 2),
                        new TileClassPainter(ClPlayer),
                    },
                    null);

                double angle = hillAngles[i] + SafeMath.PI *
                    (1 + rng.RandFloat(-1, 1) * RmgenLibrary.FractionToTiles(0.005, MapSize) / 8);

                var startOffset = new RmgenVector2D(
                    hillRadius + 5 + RmgenLibrary.FractionToTiles(0.02, MapSize), 0);
                startOffset.Rotate(-angle);
                var endOffset = new RmgenVector2D(hillRadius - 3, 0);
                endOffset.Rotate(-angle);
                RmgenCommon.CreatePassage(rng, map,
                    RmgenVector2D.Add(hillPositions[i], startOffset),
                    RmgenVector2D.Add(hillPositions[i], endOffset),
                    10, 10, 2,
                    terrain: biome.Hill,
                    edgeTerrain: biome.Cliff,
                    tileClass: ClPlayer);
            }

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                startingPlacement.PlayerPosition,
                cityPatchOuterTerrain: biome.RoadWild,
                cityPatchInnerTerrain: biome.Road,
                playerIDs: startingPlacement.PlayerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = biome.FruitBush,
                    Mines = new List<(string, string?, object?)>
                    {
                        (biome.MetalLarge, null, null),
                        (biome.StoneLarge, null, null),
                    },
                    TreesTemplate = biome.Tree1,
                    TreesCount = 2,
                    DecorativesTemplate = biome.GrassShort,
                });

            double numLakes = SafeMath.Round(RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers);
            var waterAreas = RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(100, 250, MapSize),
                    0.8, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.ShoreBlend, biome.Shore, biome.Water },
                        new[] { 1.0, 1.0 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        HeightSeaGround, 6),
                    new TileClassPainter(clWater),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 2, clWater, 20),
                numLakes);

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.Reeds, 5, 10, 0, 4),
                    new ScatterObject(rng, biome.Lillies, 0, 1, 0, 4),
                }, true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.BorderClasses(clWater, 3, 0),
                    RmgenLibrary.StayClasses(clWater, 1),
                }),
                numLakes, 100, waterAreas);

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.Fish, 1, 1, 0, 1),
                }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clWater, 4),
                    RmgenLibrary.AvoidClasses(clFood, 8),
                }),
                numLakes / 4, 50, waterAreas);

            PortOMapHelpers.CreateBumps(rng, map,
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 0));

            PortOMapHelpers.CreateHills(rng, map,
                new object[] { biome.Cliff, biome.Cliff, biome.Hill },
                RmgenLibrary.AvoidClasses(ClPlayer, 2, clWater, 5, ClHill, 15),
                ClHill,
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers);

            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(
                biome.TreesMin, biome.TreesMax, biome.ForestProbability, MapSize);
            GaiaEntities.CreateDefaultForests(rng, map,
                new object[] { tGrass, biome.ForestFloor2, biome.ForestFloor1, pForestP, pForestD },
                RmgenLibrary.AvoidClasses(ClPlayer, 1, clWater, 3, ClForest, 17, ClHill, 1),
                ClForest, forestTrees);

            IConstraint patchConstraint =
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 0);

            PortOMapHelpers.CreateLayeredPatches(rng, map,
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                    RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
                },
                new object[]
                {
                    new object[] { tGrass, biome.Tier2Terrain },
                    new object[] { biome.Tier2Terrain, biome.Tier3Terrain },
                    new object[] { biome.Tier3Terrain, biome.Tier4Terrain },
                },
                new[] { 1.0, 1.0 },
                patchConstraint,
                RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            PortOMapHelpers.CreateLayeredPatches(rng, map,
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                    RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
                },
                new object[] { biome.Tier2Terrain, biome.Tier1Terrain },
                new[] { 1.0 },
                patchConstraint,
                RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            GaiaEntities.CreateBalancedMetalMines(rng, map, NumPlayers,
                biome.MetalSmall, biome.MetalLarge, ClMetal,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer,
                    RmgenLibrary.ScaleByMapSize(15, 25, MapSize), ClHill, 1));

            GaiaEntities.CreateBalancedStoneMines(rng, map, NumPlayers,
                biome.StoneSmall, biome.StoneLarge, ClRock,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer,
                    RmgenLibrary.ScaleByMapSize(15, 25, MapSize), ClHill, 1, ClMetal, 10));

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
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, Settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, Settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, Settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, Settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, Settings.CircularMap),
                },
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 0));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2) },
                    new IGroupElement[] { new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers },
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 1, ClHill, 1, clFood, 20),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) },
                },
                new double[] { rng.RandIntInclusive(1, 4) * NumPlayers + 2 },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 1, ClHill, 1, ClPlayer, 1,
                    ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }

        private static StartingPlacement DetailedPlayerPlacementByPattern(RmgenRng rng, RandomMap map,
            MapSettings settings, string? patternName, double distance, double groupedDistance,
            double angle, RmgenVector2D? center = null)
        {
            patternName ??= settings.PlayerPlacement;
            if (patternName == "stronghold")
                return PlaceStronghold(map, RmgenCommon.GetTeamsArray(rng, settings), distance,
                    groupedDistance * 1.4, angle);

            if (patternName == "groupedLines")
            {
                var line = RmgenCommon.PlaceLine(map, RmgenCommon.GetTeamsArray(rng, settings),
                    distance, groupedDistance, angle);
                return new StartingPlacement(line.playerIDs, line.playerPosition,
                    Enumerable.Repeat(angle, line.playerPosition.Count).ToList());
            }

            if (patternName == "river")
            {
                var river = RmgenCommon.PlayerPlacementRiver(rng, map, settings, angle, distance, center);
                return new StartingPlacement(river.playerIDs, river.playerPosition,
                    Enumerable.Repeat(angle, river.playerPosition.Count).ToList());
            }

            if (patternName == "randomGroup")
            {
                var random = RmgenCommon.PlayerPlacementRandom(rng, map, settings, null);
                if (random.HasValue)
                    return new StartingPlacement(random.Value.playerIDs, random.Value.playerPosition,
                        Enumerable.Repeat(angle, random.Value.playerPosition.Count).ToList());
            }

            var circle = RmgenCommon.PlayerPlacementCircle(rng, map, RmgenCommon.GetNumPlayers(settings),
                distance, angle, center);
            return new StartingPlacement(circle.playerIDs, circle.playerPosition, circle.playerAngle);
        }

        private static StartingPlacement PlaceStronghold(RandomMap map,
            IReadOnlyList<List<int>> teamsArray, double distance, double groupedDistance,
            double startAngle)
        {
            var mapCenter = map.GetCenter();
            var playerIDs = new List<int>();
            var playerPosition = new List<RmgenVector2D>();
            var teamPositions = new List<RmgenVector2D>();

            var strongholdRadius = teamsArray.Select(team => team.Count == 1
                ? 0
                : groupedDistance / 2 / SafeMath.Sin(SafeMath.PI / team.Count)).ToList();
            double distanceBetweenStrongholds =
                (distance * 2 * SafeMath.PI - 2 * strongholdRadius.Sum()) / strongholdRadius.Count;

            var relativeTeamAngles = strongholdRadius.Select((r1, i) =>
                (distanceBetweenStrongholds +
                    strongholdRadius[(i - 1 + strongholdRadius.Count) % strongholdRadius.Count] + r1) /
                distance).ToList();

            var teamAngles = new List<double>();
            for (int i = 0; i < relativeTeamAngles.Count; ++i)
                teamAngles.Add((i == 0 ? startAngle : teamAngles[^1]) + relativeTeamAngles[i]);

            for (int i = 0; i < teamsArray.Count; ++i)
            {
                var teamOffset = new RmgenVector2D(distance * 0.8, 0);
                teamOffset.Rotate(-teamAngles[i]);
                var teamPosition = RmgenVector2D.Add(mapCenter, teamOffset);

                for (int p = 0; p < teamsArray[i].Count; ++p)
                {
                    double angle = startAngle + (p + 1) * 2 * SafeMath.PI / teamsArray[i].Count;
                    playerIDs.Add(teamsArray[i][p]);
                    var offset = new RmgenVector2D(strongholdRadius[i], 0);
                    offset.Rotate(-angle);
                    var position = RmgenVector2D.Add(teamPosition, offset);
                    position.Round();
                    playerPosition.Add(position);
                }

                teamPositions.Add(teamPosition);
            }

            return new StartingPlacement(playerIDs, playerPosition,
                Enumerable.Repeat(startAngle, playerPosition.Count).ToList(),
                teamPositions, teamAngles, strongholdRadius);
        }

        private sealed record StartingPlacement(
            List<int> PlayerIDs,
            List<RmgenVector2D> PlayerPosition,
            List<double> PlayerAngle,
            List<RmgenVector2D>? TeamPosition = null,
            List<double>? TeamAngle = null,
            List<double>? StrongholdRadius = null);
    }
}
