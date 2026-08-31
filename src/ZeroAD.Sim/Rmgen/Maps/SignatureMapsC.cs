using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>cycladic_archipelago.js（417 行）——基底为深海，外环玩家/中立岛与中心小岛群。
    /// 环境设置、伊比利亚起始塔墙与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class CycladicArchipelagoMap2 : StandardMap
    {
        private const string tOceanRockDeep = "medit_sea_coral_deep";
        private const string tOceanCoral = "medit_sea_coral_plants";
        private const string tBeachWet = "medit_sand_wet";
        private const string tBeachDry = "medit_sand";
        private static readonly string[] tBeach =
            { "medit_rocks_grass", "medit_sand", "medit_rocks_grass_shrubs" };
        private static readonly string[] tBeachBlend =
            { "medit_rocks_grass", "medit_rocks_grass_shrubs" };
        private const string tCity = "medit_city_tile";
        private static readonly string[] tGrassDry =
            { "medit_grass_field_dry", "medit_grass_field_b" };
        private static readonly string[] tGrass =
            { "medit_rocks_grass", "medit_rocks_grass", "medit_dirt", "medit_rocks_grass_shrubs" };
        private const string tGrassShrubs = "medit_shrubs";
        private static readonly string[] tCliffShrubs =
            { "medit_cliff_aegean_shrubs", "medit_cliff_italia_grass", "medit_cliff_italia" };
        private static readonly string[] tCliff =
            { "medit_cliff_italia", "medit_cliff_italia", "medit_cliff_italia_grass" };
        private const string tForestFloor = "medit_forestfloor_a";
        private const string tWater = "medit_sea_depths";

        private const string oBeech = "gaia/tree/euro_beech";
        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oCarob = "gaia/tree/carob";
        private const string oCypress1 = "gaia/tree/cypress";
        private const string oCypress2 = "gaia/tree/cypress";
        private const string oLombardyPoplar = "gaia/tree/poplar_lombardy";
        private const string oPalm = "gaia/tree/medit_fan_palm";
        private const string oPine = "gaia/tree/aleppo_pine";
        private const string oDateT = "gaia/tree/cretan_date_palm_tall";
        private const string oDateS = "gaia/tree/cretan_date_palm_short";
        private const string oDeer = "gaia/fauna_deer";
        private const string oFish = "gaia/fish/generic";
        private const string oWhale = "gaia/fauna_whale_humpback";
        private const string oStoneLarge = "gaia/rock/mediterranean_large";
        private const string oStoneSmall = "gaia/rock/mediterranean_small";
        private const string oMetalLarge = "gaia/ore/mediterranean_large";
        private const string oShipwreck = "gaia/treasure/shipwreck";
        private const string oShipDebris = "gaia/treasure/shipwreck_debris";

        private const string aRockLarge = "actor|geology/stone_granite_large.xml";
        private const string aRockMed = "actor|geology/stone_granite_med.xml";
        private const string aRockSmall = "actor|geology/stone_granite_small.xml";

        protected override double HeightLand => -5;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tWater);
            var map = Map;

            const double heightHill = 12;
            const double heightOffsetBump = 2;

            var clCoral = new TileClass(MapSize);
            var clIsland = new TileClass(MapSize);
            var clCity = new TileClass(MapSize);
            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);

            var pPalmForest = new object[] { tForestFloor + "|" + oPalm, tGrass };
            var pPineForest = new object[] { tForestFloor + "|" + oPine, tGrass };
            var pPoplarForest = new object[] { tForestFloor + "|" + oLombardyPoplar, tGrass };
            var pMainForest = new object[]
            {
                tForestFloor + "|" + oCarob,
                tForestFloor + "|" + oBeech,
                tGrass,
                tGrass,
            };

            var startingPlaces = new[]
            {
                new[] { 0 },
                new[] { 0, 3 },
                new[] { 0, 2, 4 },
                new[] { 0, 1, 3, 4 },
                new[] { 0, 1, 2, 3, 4 },
                new[] { 0, 1, 2, 3, 4, 5 },
            };

            double startAngle = rng.RandomAngle();
            double islandRadius = RmgenLibrary.ScaleByMapSize(15, 40, MapSize);
            int islandCount = Math.Max(6, NumPlayers);
            var islandPosition = RmgenGeometry.DistributePointsOnCircle(
                islandCount, startAngle, RmgenLibrary.FractionToTiles(0.39, MapSize), map.GetCenter()).points;
            for (int i = 0; i < islandPosition.Count; ++i)
            {
                var rounded = islandPosition[i];
                rounded.Round();
                islandPosition[i] = rounded;
            }

            double centralIslandRadius = RmgenLibrary.ScaleByMapSize(15, 30, MapSize);
            int centralIslandCount = (int)Math.Floor(RmgenLibrary.ScaleByMapSize(1, 4, MapSize));
            var centralIslandPosition = new List<RmgenVector2D>();
            for (int i = 0; i < NumPlayers; ++i)
            {
                var offset = new RmgenVector2D(
                    RmgenLibrary.FractionToTiles(rng.RandFloat(0.1, 0.16), MapSize), 0);
                offset.Rotate(-startAngle - SafeMath.PI *
                    (i * 2.0 / centralIslandCount + rng.RandFloat(-1, 1) / 8));
                var position = RmgenVector2D.Add(map.GetCenter(), offset);
                position.Round();
                centralIslandPosition.Add(position);
            }

            var areas = new List<Area>();
            int nPlayer = 0;
            var playerPosition = new List<RmgenVector2D>();

            for (int i = 0; i < islandCount; ++i)
            {
                bool isPlayerIsland = NumPlayers >= 6 ||
                    (NumPlayers > 0 && nPlayer < startingPlaces[NumPlayers - 1].Length &&
                        i == startingPlaces[NumPlayers - 1][nPlayer]);
                if (isPlayerIsland)
                {
                    playerPosition.Add(islandPosition[i]);
                    ++nPlayer;
                }

                CreateCycladicArchipelagoIsland(rng, islandPosition[i],
                    isPlayerIsland ? ClPlayer : clIsland, islandRadius,
                    RmgenLibrary.ScaleByMapSize(1, 5, MapSize), clCoral, areas);
            }

            foreach (var position in centralIslandPosition)
                CreateCycladicArchipelagoIsland(rng, position, clIsland,
                    centralIslandRadius, 2, clCoral, areas);

            RmgenCommon.PlacePlayerBases(rng, map, settings, tGrass[0], clCity, null,
                playerPosition, tGrass, tCity, RmgenCommon.SortAllPlayers(rng, settings),
                cityPatchRadius: 6,
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
                    TreesTemplate = oPalm,
                    TreesCount = 2,
                });

            RmgenLibrary.CreateAreasInAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 60, MapSize),
                    0.3, 0.06, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump, 3, relative: true),
                },
                RmgenLibrary.AvoidClasses(clCity, 0),
                RmgenLibrary.ScaleByMapSize(25, 75, MapSize), 15, areas);

            RmgenLibrary.CreateAreasInAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 150, MapSize),
                    0.2, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tCliff, tCliffShrubs }, new[] { 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightHill, 2),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(clCity, 15, ClHill, 15),
                RmgenLibrary.ScaleByMapSize(5, 30, MapSize), 15, areas);

            RmgenLibrary.PaintTileClassBasedOnHeight(double.NegativeInfinity, 0,
                HeightPlacer.Mode.ExcludeMinExcludeMax, clWater);

            var forestTypes = new[]
            {
                new object[] { new object[] { tForestFloor, tGrass, pPalmForest }, new object[] { tForestFloor, pPalmForest } },
                new object[] { new object[] { tForestFloor, tGrass, pPineForest }, new object[] { tForestFloor, pPineForest } },
                new object[] { new object[] { tForestFloor, tGrass, pPoplarForest }, new object[] { tForestFloor, pPoplarForest } },
                new object[] { new object[] { tForestFloor, tGrass, pMainForest }, new object[] { tForestFloor, pMainForest } },
            };
            foreach (var type in forestTypes)
                RmgenLibrary.CreateAreasInAreas(rng,
                    new ClumpPlacer(rng, rng.RandIntInclusive(6, 17), 0.1, 0.1,
                        double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    RmgenLibrary.AvoidClasses(clCity, 1, clWater, 3, ClForest, 3,
                        ClHill, 1, ClBaseResource, 4),
                    RmgenLibrary.ScaleByMapSize(10, 64, MapSize), 20, areas);

            var group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 1, ClHill, 1,
                    ClPlayer, 5, ClRock, 6),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 200, areas);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3) }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 1, ClHill, 1,
                    ClPlayer, 5, ClRock, 6),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 200, areas);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) }, true, ClMetal);
            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 1, ClHill, 1,
                    ClPlayer, 5, ClMetal, 6, ClRock, 6),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 200, areas);

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 32, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 80, MapSize),
            })
                RmgenLibrary.CreateAreasInAreas(rng,
                    new ClumpPlacer(rng, size, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tBeachBlend, tGrassShrubs },
                            new[] { 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 3, ClHill, 0, ClDirt, 6,
                        clCity, 0, ClBaseResource, 4),
                    RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 20, areas);

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 32, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 80, MapSize),
            })
                RmgenLibrary.CreateAreasInAreas(rng,
                    new ClumpPlacer(rng, size, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tGrassDry }, Array.Empty<int>(), rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 3, ClHill, 0, ClDirt, 6,
                        clCity, 0, ClBaseResource, 4),
                    RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 20, areas);

            foreach (string tree in new[]
            {
                oCarob, oBeech, oLombardyPoplar, oLombardyPoplar, oPine,
            })
                RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, tree, 1, 1, 0, 1),
                    }, true, ClForest),
                    0,
                    RmgenLibrary.AvoidClasses(clWater, 2, ClForest, 2, clCity, 3,
                        ClBaseResource, 4, ClRock, 6, ClMetal, 6, ClPlayer, 1, ClHill, 1),
                    RmgenLibrary.ScaleByMapSize(2, 38, MapSize), 50, areas);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oCypress2, 1, 3, 0, 3),
                new ScatterObject(rng, oCypress1, 0, 2, 0, 2),
            }, true);
            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 2, ClForest, 2, clCity, 3,
                    ClBaseResource, 4, ClRock, 6, ClMetal, 6, ClPlayer, 1, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(5, 75, MapSize), 50, areas);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oDateS, 1, 3, 0, 3),
                new ScatterObject(rng, oDateT, 0, 2, 0, 2),
            }, true);
            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 2, ClForest, 1, clCity, 0,
                    ClBaseResource, 4, ClRock, 6, ClMetal, 6, ClPlayer, 1, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(5, 75, MapSize), 50, areas);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aRockSmall, 0, 3, 0, 2),
                new ScatterObject(rng, aRockMed, 0, 2, 0, 2),
                new ScatterObject(rng, aRockLarge, 0, 1, 0, 2),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, clCity, 0),
                RmgenLibrary.ScaleByMapSize(30, 180, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oDeer, 5, 7, 0, 4),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 5, ClForest, 1, ClHill, 1,
                    clCity, 10, ClMetal, 6, ClRock, 4, clFood, 8),
                3 * NumPlayers, 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oBerryBush, 5, 7, 0, 3),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 2, ClForest, 1, ClHill, 1,
                    clCity, 10, ClMetal, 6, ClRock, 4, clFood, 8),
                1.5 * NumPlayers, 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oFish, 1, 1, 0, 3),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clWater, 1),
                    RmgenLibrary.AvoidClasses(clFood, 8),
                }),
                RmgenLibrary.ScaleByMapSize(100, 250, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oWhale, 1, 1, 0, 3),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clWater, 1),
                    RmgenLibrary.AvoidClasses(clFood, 8, ClPlayer, 4, clIsland, 4),
                }),
                RmgenLibrary.ScaleByMapSize(10, 40, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oShipwreck, 1, 1, 0, 3),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clWater, 1),
                    RmgenLibrary.AvoidClasses(clFood, 8),
                }),
                RmgenLibrary.ScaleByMapSize(6, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oShipDebris, 1, 2, 0, 4),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clWater, 1),
                    RmgenLibrary.AvoidClasses(clFood, 8),
                }),
                RmgenLibrary.ScaleByMapSize(10, 20, MapSize), 100);

            return map.MakeExportable();
        }

        private void CreateCycladicArchipelagoIsland(RmgenRng rng, RmgenVector2D position,
            TileClass tileClass, double radius, double coralRadius, TileClass clCoral,
            List<Area> areas)
        {
            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, RmgenGeometry.DiskArea(radius + coralRadius),
                    0.7, 0.1, double.PositiveInfinity, position),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tOceanRockDeep, tOceanCoral },
                        new[] { 5 }, rng),
                    new TileClassPainter(clCoral),
                },
                RmgenLibrary.AvoidClasses(clCoral, 0, ClPlayer, 0));

            var area = RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, RmgenGeometry.DiskArea(radius),
                    0.7, 0.1, double.PositiveInfinity, position),
                new IPainter[]
                {
                    new LayeredPainter(new object[]
                    {
                        tOceanCoral,
                        tBeachWet,
                        tBeachDry,
                        tBeach,
                        tBeachBlend,
                        tGrass,
                    }, new[] { 1, 3, 1, 1, 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, 3, 5),
                    new TileClassPainter(tileClass),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 0));
            if (area != null)
                areas.Add(area);
        }
    }

    /// <summary>corinthian_isthmus.js（382 行）——一条深水海峡切开地图，并以浅滩/陆桥连通两岸。
    /// 上游 TILE_CENTERED_HEIGHT_MAP 标志在当前 RandomMap 基础库中未暴露；
    /// 环境设置、伊比利亚起始塔墙与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class CorinthianIsthmusMap2 : StandardMap
    {
        protected override double HeightLand => 3;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitCorinthianContext(rng, settings, out string oCypress);
            var biome = Biome;
            var map = Map;

            string tCity = biome.Road;
            string tCityPlaza = biome.RoadWild;
            var tHill = biome.Hill;
            var tMainDirt = biome.Dirt;
            var tCliff = biome.Cliff;
            string tForestFloor = biome.ForestFloor1;
            var tGrass = biome.MainTerrain;
            string tGrassSand50 = biome.Tier1Terrain;
            string tGrassSand25 = biome.Tier3Terrain;
            var tDirt = biome.Dirt;
            var tDirt2 = biome.Dirt;
            string tDirt3 = biome.Tier2Terrain;
            var tDirtCracks = biome.Dirt;
            string tShore = biome.Shore;
            string tWater = biome.Water;

            string oBerryBush = biome.FruitBush;
            string oDeer = biome.MainHuntableAnimal;
            string oFish = biome.Fish;
            string oSheep = biome.SecondaryHuntableAnimal;
            const string oGoat = "gaia/fauna_goat";
            string oStoneSmall = biome.StoneSmall;
            string oMetalLarge = biome.MetalLarge;
            string oMetalSmall = biome.MetalSmall;
            string oDatePalm = biome.Tree1;
            string oSDatePalm = biome.Tree2;
            string oCarob = biome.Tree3;
            string oFanPalm = biome.Tree4;
            string oPoplar = biome.Tree5;

            string aBush1 = biome.BushSmall;
            string aBush2 = biome.BushMedium;
            string aBush3 = biome.GrassShort;
            string aBush4 = biome.Tree;
            string aDecorativeRock = biome.RockMedium;
            string aLillies = biome.Lillies;
            string aReeds = biome.Reeds;

            var pForest = new object[]
            {
                tForestFloor,
                tForestFloor + "|" + oCarob,
                tForestFloor + "|" + oDatePalm,
                tForestFloor + "|" + oSDatePalm,
                tForestFloor,
            };

            const double heightSeaGround = -7;
            const double heightShallow = -0.8;
            const double heightLand = 3;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clGrass = new TileClass(MapSize);
            var clPassageway = new TileClass(MapSize);
            var clShallow = new TileClass(MapSize);

            var mapCenter = map.GetCenter();
            double riverAngle = rng.RandomAngle();
            double riverWidth = RmgenLibrary.ScaleByMapSize(20, 90, MapSize);
            var riverStart = new RmgenVector2D(mapCenter.X, 0);
            riverStart.RotateAround(riverAngle, mapCenter);
            var riverEnd = new RmgenVector2D(mapCenter.X, MapSize);
            riverEnd.RotateAround(riverAngle, mapCenter);

            RmgenLibrary.CreateArea(
                new PathPlacer(rng, 0.2, RmgenLibrary.ScaleByMapSize(0.3, 1, MapSize),
                    0.04, 0.01)
                {
                    Start = riverStart,
                    End = riverEnd,
                    Width = riverWidth,
                },
                new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                    heightSeaGround, 4),
                null);

            foreach (var point in new[] { riverStart, riverEnd })
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, RmgenGeometry.DiskArea(riverWidth / 2),
                        0.95, 0.6, double.PositiveInfinity, point),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaGround, 4),
                    null);

            RmgenLibrary.PaintTileClassBasedOnHeight(heightSeaGround - 1, 0.5,
                HeightPlacer.Mode.IncludeMinExcludeMax, clWater);

            double fraction = rng.RandFloat(0.38, 0.62);
            var middlePoint = RmgenVector2D.Add(RmgenVector2D.Mult(riverStart, fraction),
                RmgenVector2D.Mult(riverEnd, 1 - fraction));
            var passageStart = new RmgenVector2D(middlePoint.X, middlePoint.Y - riverWidth * 1.1);
            passageStart.RotateAround(riverAngle, middlePoint);
            var passageEnd = new RmgenVector2D(middlePoint.X, middlePoint.Y + riverWidth * 1.1);
            passageEnd.RotateAround(riverAngle, middlePoint);
            passageStart.RotateAround(SafeMath.PI / 2, middlePoint);
            passageEnd.RotateAround(SafeMath.PI / 2, middlePoint);

            double passageWidth = RmgenLibrary.ScaleByMapSize(15, 40, MapSize);
            RmgenLibrary.CreateArea(
                new PathPlacer(rng, 0.2, RmgenLibrary.ScaleByMapSize(0.2, 0.4, MapSize),
                    0.1, 0.01, 100.0)
                {
                    Start = passageStart,
                    End = passageEnd,
                    Width = passageWidth * 2,
                },
                new MultiPainter(new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightShallow, 3),
                    new TileClassPainter(clShallow),
                }),
                new OrConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clWater, 0),
                    RmgenLibrary.BorderClasses(clWater, 0, 3),
                }));

            RmgenLibrary.CreateArea(
                new PathPlacer(rng, 0.5, RmgenLibrary.ScaleByMapSize(0.2, 0.4, MapSize),
                    0.1, 0.01)
                {
                    Start = passageStart,
                    End = passageEnd,
                    Width = passageWidth,
                },
                new MultiPainter(new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightLand, 3),
                    new TileClassPainter(clPassageway),
                }),
                null);

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightSeaGround - 1, 1,
                HeightPlacer.Mode.IncludeMinExcludeMax, tWater);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, 2,
                HeightPlacer.Mode.IncludeMinExcludeMax, tShore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 2, 5,
                HeightPlacer.Mode.IncludeMinExcludeMax, tGrass);

            clWater = new TileClass(MapSize);
            RmgenLibrary.PaintTileClassBasedOnHeight(heightSeaGround - 1, 0.5,
                HeightPlacer.Mode.IncludeMinExcludeMax, clWater);

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementRiver(
                rng, map, settings, riverAngle, RmgenLibrary.FractionToTiles(0.6, MapSize));
            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                playerPosition, tCityPlaza, tCity, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = oBerryBush,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneSmall, (string?)null, (object?)null),
                        (oStoneSmall, (string?)null, (object?)null),
                    },
                    TreesTemplate = oCarob,
                    TreesCount = 2,
                    DecorativesTemplate = aBush1,
                });

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        2, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 20),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            double forestTrees = 0.7 * RmgenLibrary.ScaleByMapSize(500, 3000, MapSize);
            double stragglerTrees = (1 - 0.7) * RmgenLibrary.ScaleByMapSize(500, 3000, MapSize);
            GaiaEntities.CreateDefaultForests(rng, map,
                new object[] { tForestFloor, tForestFloor, pForest, pForest, pForest },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, clPassageway, 5, ClForest, 17,
                    clWater, 2, ClBaseResource, 3),
                ClForest, forestTrees);

            if (rng.RandBool())
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tGrass, tCliff, tHill },
                            new[] { 1, 2 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, 18, 2),
                        new TileClassPainter(ClHill),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, clPassageway, 10, ClForest, 1,
                        ClHill, 15, clWater, 3),
                    RmgenLibrary.ScaleByMapSize(3, 15, MapSize));
            else
                RmgenCommon.CreateMountains(rng, map, tCliff,
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, clPassageway, 10, ClForest, 1,
                        ClHill, 15, clWater, 3),
                    ClHill, count: (int)RmgenLibrary.ScaleByMapSize(3, 15, MapSize));

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
                            new object[] { tGrass, tGrassSand50 },
                            new object[] { tGrassSand50, tGrassSand25 },
                            new object[] { tGrassSand25, tGrass },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, clGrass, 2, ClPlayer, 10,
                        clWater, 2, ClDirt, 2, ClHill, 1),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

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
                            tDirt3,
                            tDirt2,
                            new object[] { tDirt, tMainDirt },
                            new object[] { tDirtCracks, tMainDirt },
                        }, new[] { 1, 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClDirt, 2, ClPlayer, 10,
                        clWater, 2, clGrass, 2, ClHill, 1),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 1, 4, 0, 4),
                }, true, ClRock),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 4, ClPlayer, 15, ClRock, 6,
                        clWater, 0, ClHill, 4),
                    RmgenLibrary.StayClasses(clPassageway, 2),
                }),
                RmgenLibrary.ScaleByMapSize(4, 15, MapSize), 80);

            GaiaEntities.CreateBalancedMetalMines(rng, map, NumPlayers,
                oMetalSmall, oMetalLarge, ClMetal,
                RmgenLibrary.AvoidClasses(clPassageway, 1, clWater, 0, ClForest, 0,
                    ClPlayer, RmgenLibrary.ScaleByMapSize(15, 25, MapSize),
                    ClHill, 1, ClRock, 10),
                0.9);

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, aDecorativeRock, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aBush2, 1, 2, 0, 1),
                        new ScatterObject(rng, aBush1, 1, 3, 0, 2),
                        new ScatterObject(rng, aBush4, 1, 2, 0, 1),
                        new ScatterObject(rng, aBush3, 1, 3, 0, 2),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapSize(40, 360, MapSize),
                },
                RmgenLibrary.AvoidClasses(clWater, 2, ClForest, 0, ClPlayer, 5,
                    ClBaseResource, 6, ClHill, 1, ClRock, 6, ClMetal, 6));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, oFish, 2, 3, 0, 2) } },
                new[] { 25 * RmgenLibrary.ScaleByMapSize(15, 20, MapSize) },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 10, clShallow, 2),
                    RmgenLibrary.StayClasses(clWater, 4),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oSheep, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, oGoat, 2, 4, 0, 3) },
                    new IGroupElement[] { new ScatterObject(rng, oDeer, 2, 4, 0, 2) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                },
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, clPassageway, 0,
                    ClBaseResource, 6, clWater, 1, clFood, 10, ClHill, 1, ClRock, 6, ClMetal, 6),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, oBerryBush, 5, 7, 0, 4) } },
                new double[] { 3 * NumPlayers },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, clPassageway, 0,
                    ClPlayer, 20, ClHill, 1, clFood, 10, ClRock, 6, ClMetal, 6),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oDatePalm, oSDatePalm, oCarob, oFanPalm, oPoplar, oCypress },
                RmgenLibrary.AvoidClasses(ClForest, 1, clWater, 2, ClPlayer, 8,
                    ClBaseResource, 6, ClMetal, 6, ClRock, 6, ClHill, 1),
                ClForest, stragglerTrees);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aDecorativeRock, 1, 2, 0, 1),
                    new ScatterObject(rng, aReeds, 0, 4, 0, 1),
                }, true, ClRock),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clShallow, 2),
                    RmgenLibrary.AvoidClasses(clPassageway, 0),
                }),
                RmgenLibrary.ScaleByMapSize(30, 100, MapSize), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aLillies, 1, 2, 0, 1),
                }, true, ClRock),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clShallow, 2),
                    RmgenLibrary.AvoidClasses(clPassageway, 0),
                }),
                RmgenLibrary.ScaleByMapSize(6, 36, MapSize), 50);

            return map.MakeExportable();
        }

        private void InitCorinthianContext(RmgenRng rng, MapSettings settings, out string oCypress)
        {
            Rng = rng;
            Settings = settings;
            MapSize = settings.Size;
            NumPlayers = RmgenCommon.GetNumPlayers(settings);

            if (settings.BiomeData != null)
            {
                Biome = settings.BiomeData;
                BiomeName = "";
            }
            else
            {
                string picked = rng.PickRandom(SupportedBiomes);
                BiomeName = picked.Contains('/') ? picked : "generic/" + picked;
                Biome = BiomeLoader.Load(settings.DataRoot, picked, rng);
            }

            oCypress = rng.PickRandom(new[]
            {
                Biome.Tree1, Biome.Tree2, Biome.Tree3, Biome.Tree4, Biome.Tree5,
            });

            Map = new RandomMap(rng, MapSize, HeightLand, Biome.Hill, settings.CircularMap);
            RmgenLibrary.CurrentMap = Map;
            ClPlayer = new TileClass(MapSize);
            ClHill = new TileClass(MapSize);
            ClForest = new TileClass(MapSize);
            ClDirt = new TileClass(MapSize);
            ClRock = new TileClass(MapSize);
            ClMetal = new TileClass(MapSize);
            ClBaseResource = new TileClass(MapSize);
        }
    }

    /// <summary>dodecanese.js（503 行）——随机玩家岛、众多小岛、火山、桥梁与海岛资源链。
    /// 上游 TILE_CENTERED_HEIGHT_MAP 标志在当前 RandomMap 基础库中未暴露；
    /// 环境设置、伊比利亚起始塔墙与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class DodecaneseMap2 : StandardMap
    {
        private const string tCity = "medit_city_pavement";
        private const string tCityPlaza = "medit_city_pavement";
        private static readonly string[] tHill =
        {
            "medit_grass_shrubs",
            "medit_rocks_grass_shrubs",
            "medit_rocks_shrubs",
            "medit_rocks_grass",
            "medit_shrubs",
        };
        private const string tMainDirt = "medit_dirt";
        private const string tCliff = "medit_cliff_aegean";
        private const string tForestFloor = "medit_grass_wild";
        private static readonly string[] tPrimary =
        {
            "medit_grass_shrubs",
            "medit_grass_wild",
            "medit_rocks_grass_shrubs",
            "medit_dirt_b",
            "medit_plants_dirt",
            "medit_grass_flowers",
        };
        private const string tDirt = "medit_dirt_b";
        private const string tDirt2 = "medit_rocks_grass";
        private const string tDirt3 = "medit_rocks_shrubs";
        private const string tDirtCracks = "medit_dirt_c";
        private const string tShoreLower = "medit_sand_wet";
        private const string tShoreUpper = "medit_sand";
        private const string tCoralsLower = "medit_sea_coral_deep";
        private const string tCoralsUpper = "medit_sea_coral_plants";
        private const string tWater = "medit_sea_depths";
        private const string tLavaOuter = "LavaTest06";
        private const string tLavaInner = "LavaTest05";

        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oDeer = "gaia/fauna_deer";
        private const string oFish = "gaia/fish/generic";
        private const string oSheep = "gaia/fauna_sheep";
        private const string oGoat = "gaia/fauna_goat";
        private const string oRabbit = "gaia/fauna_rabbit";
        private const string oStoneLarge = "gaia/rock/mediterranean_large";
        private const string oStoneSmall = "gaia/rock/mediterranean_small";
        private const string oMetalLarge = "gaia/ore/mediterranean_large";
        private const string oMetalSmall = "gaia/ore/mediterranean_small";
        private const string oDatePalm = "gaia/tree/cretan_date_palm_short";
        private const string oSDatePalm = "gaia/tree/cretan_date_palm_tall";
        private const string oCarob = "gaia/tree/carob";
        private const string oFanPalm = "gaia/tree/medit_fan_palm";
        private const string oPoplar = "gaia/tree/poplar_lombardy";
        private const string oCypress = "gaia/tree/cypress";
        private const string oBush = "gaia/tree/bush_temperate";

        private static readonly string aBush1 = RmgenLibrary.ActorTemplate("props/flora/bush_medit_sm");
        private static readonly string aBush2 = RmgenLibrary.ActorTemplate("props/flora/bush_medit_me");
        private static readonly string aBush3 = RmgenLibrary.ActorTemplate("props/flora/bush_medit_la");
        private static readonly string aBush4 = RmgenLibrary.ActorTemplate("props/flora/bush_medit_me");
        private static readonly string aDecorativeRock = RmgenLibrary.ActorTemplate("geology/stone_granite_med");
        private static readonly string aBridge =
            RmgenLibrary.ActorTemplate("props/special/eyecandy/bridge_edge_wooden");
        private static readonly string aSmokeBig = RmgenLibrary.ActorTemplate("particle/smoke_volcano");
        private static readonly string aSmokeSmall = RmgenLibrary.ActorTemplate("particle/smoke_curved");

        protected override double HeightLand => -8;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tWater);
            var map = Map;

            var pForest1 = new object[]
            {
                tForestFloor,
                tForestFloor + "|" + oCarob,
                tForestFloor + "|" + oDatePalm,
                tForestFloor + "|" + oSDatePalm,
                tForestFloor,
            };
            var pForest2 = new object[]
            {
                tForestFloor,
                tForestFloor + "|" + oFanPalm,
                tForestFloor + "|" + oPoplar,
                tForestFloor + "|" + oCypress,
            };

            const double heightCoralsLower = -6;
            const double heightCoralsUpper = -4;
            const double heightSeaBump = -2.5;
            const double heightShoreLower = -2;
            const double heightBridge = -0.5;
            const double heightShoreUpper = 1;
            const double heightLand = 3;
            const double heightOffsetBump = 2;
            const double heightHill = 8;
            const double heightVolano = 25;

            var clIsland = new TileClass(MapSize);
            var clWater = new TileClass(MapSize);
            var clPlayerIsland = new TileClass(MapSize);
            var clShore = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clGrass = new TileClass(MapSize);
            var clVolcano = new TileClass(MapSize);
            var clBridge = new TileClass(MapSize);

            double playerIslandRadius = RmgenLibrary.ScaleByMapSize(20, 29, MapSize);
            const double bridgeLength = 16;
            double maxBridges = RmgenLibrary.ScaleByMapSize(2, 12, MapSize);

            var sortedPlayers = RmgenCommon.SortAllPlayers(rng, settings);
            var randomPlacement = PlayerPlacementRandom(rng, map, settings, sortedPlayers, null);
            if (!randomPlacement.HasValue)
                throw new InvalidOperationException("Could not place Dodecanese players.");
            var (playerIDs, playerPosition) = randomPlacement.Value;

            foreach (var position in playerPosition)
            {
                var painters = new List<IPainter>
                {
                    new TerrainPainter(tPrimary, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightLand, 4),
                    new TileClassPainter(clIsland),
                };
                if (!settings.Nomad)
                    painters.Add(new TileClassPainter(clPlayerIsland));

                RmgenLibrary.CreateArea(
                    new ChainPlacer(rng, 2, 6, RmgenLibrary.ScaleByMapSize(15, 50, MapSize),
                        double.PositiveInfinity, position, 0,
                        new[] { (int)Math.Floor(playerIslandRadius) }),
                    painters,
                    null);
            }

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 6, Math.Floor(RmgenLibrary.ScaleByMapSize(8, 10, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(10, 35, MapSize)), 0.2),
                new IPainter[]
                {
                    new TerrainPainter(tPrimary, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightLand, 4),
                    new TileClassPainter(clIsland),
                },
                RmgenLibrary.AvoidClasses(clIsland, 6),
                RmgenLibrary.ScaleByMapSize(25, 80, MapSize));

            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new TileClassPainter(clWater),
                new HeightConstraint(map, double.NegativeInfinity, heightShoreLower));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaBump, 3),
                },
                RmgenLibrary.AvoidClasses(clIsland, 2),
                RmgenLibrary.ScaleByMapSize(10, 50, MapSize));

            var areasVolcano = RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenGeometry.DiskArea(
                    RmgenLibrary.ScaleByMapSize(4, 8, MapSize)), 0.5, 0.5, 0.1),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tLavaOuter, tLavaInner }, new[] { 4 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightVolano, 6),
                    new TileClassPainter(clVolcano),
                },
                new AndConstraint(new IConstraint[]
                {
                    new NearTileClassConstraint(clIsland, 8),
                    RmgenLibrary.AvoidClasses(ClHill, 5, clPlayerIsland, 0),
                }),
                1, 200);

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        2, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(clWater, 0, ClPlayer, 10, clVolcano, 0),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize),
                    0.3, 0.06, 1),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump, 3, relative: true),
                },
                RmgenLibrary.AvoidClasses(clWater, 2, clVolcano, 0, ClPlayer, 10),
                RmgenLibrary.ScaleByMapSize(20, 200, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tCliff, tHill }, new[] { 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightHill, 2),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(clWater, 1, ClPlayer, 12, clVolcano, 0, ClHill, 15),
                RmgenLibrary.ScaleByMapSize(4, 13, MapSize));

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, double.NegativeInfinity, heightCoralsLower,
                HeightPlacer.Mode.IncludeMinExcludeMax, tWater);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightCoralsLower, heightCoralsUpper,
                HeightPlacer.Mode.IncludeMinExcludeMax, tCoralsLower);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightCoralsUpper, heightShoreLower,
                HeightPlacer.Mode.IncludeMinExcludeMax, tCoralsUpper);

            var areaShoreline = RmgenLibrary.CreateArea(
                new HeightPlacer(map, HeightPlacer.Mode.IncludeMinExcludeMax,
                    heightShoreLower, heightShoreUpper),
                new IPainter[]
                {
                    new TerrainPainter(tShoreLower, rng),
                    new TileClassPainter(clShore),
                },
                RmgenLibrary.AvoidClasses(clVolcano, 0));

            RmgenLibrary.CreateArea(
                new HeightPlacer(map, HeightPlacer.Mode.IncludeMinExcludeMax,
                    heightShoreUpper, heightLand),
                new TerrainPainter(tShoreUpper, rng),
                RmgenLibrary.AvoidClasses(clVolcano, 0));

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
                            tDirt3,
                            tDirt2,
                            new object[] { tDirt, tMainDirt },
                            new object[] { tDirtCracks, tMainDirt },
                        }, new[] { 1, 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 4, clVolcano, 2, ClForest, 1,
                        ClDirt, 2, clGrass, 2, ClHill, 1),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            RmgenCommon.PlacePlayerBases(rng, map, settings, tPrimary[0], ClPlayer, null,
                playerPosition, tCityPlaza, tCity, playerIDs,
                cityPatchRadius: playerIslandRadius / 4,
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
                    DecorativesTemplate = aBush1,
                });

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
                RmgenLibrary.AvoidClasses(clWater, 4, clVolcano, 4, clPlayerIsland, 0,
                    ClBaseResource, 4, ClForest, 3, ClMetal, 4, ClRock, 4),
                ClRock, RmgenLibrary.ScaleByMapSize(4, 16, MapSize));

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, oMetalSmall, 0, 1, 0, 4),
                        new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4),
                    },
                    new IGroupElement[] { new ScatterObject(rng, oMetalSmall, 2, 5, 1, 3) },
                },
                RmgenLibrary.AvoidClasses(clWater, 4, clPlayerIsland, 0, clVolcano, 4,
                    ClBaseResource, 4, ClForest, 3, ClMetal, 4, ClRock, 4),
                ClMetal, RmgenLibrary.ScaleByMapSize(4, 16, MapSize));

            double forestTrees = 0.7 * RmgenLibrary.ScaleByMapSize(800, 4000, MapSize);
            double stragglerTrees = (1 - 0.7) * RmgenLibrary.ScaleByMapSize(800, 4000, MapSize);
            GaiaEntities.CreateForests(rng, map,
                new object[] { tForestFloor, tForestFloor, tForestFloor, pForest1, pForest2 },
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 4, clVolcano, 2, ClForest, 1,
                    ClBaseResource, 4, ClMetal, 4, ClRock, 4),
                ClForest, forestTrees, NumPlayers, 200);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oSheep, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, oGoat, 2, 4, 0, 3) },
                    new IGroupElement[] { new ScatterObject(rng, oDeer, 2, 4, 0, 2) },
                    new IGroupElement[] { new ScatterObject(rng, oRabbit, 3, 9, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, oBerryBush, 3, 5, 0, 4) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    3 * NumPlayers,
                },
                RmgenLibrary.AvoidClasses(clWater, 1, ClPlayer, 15, clVolcano, 4,
                    ClBaseResource, 4, ClHill, 2, ClMetal, 4, ClRock, 4),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, oFish, 2, 3, 0, 2) } },
                new double[] { 35 * NumPlayers },
                RmgenLibrary.AvoidClasses(clIsland, 2, clFood, 8, clVolcano, 2),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oPoplar, oCypress, oFanPalm, oDatePalm, oSDatePalm },
                RmgenLibrary.AvoidClasses(clWater, 1, clVolcano, 4, ClPlayer, 12,
                    ClForest, 1, ClMetal, 4, ClRock, 4),
                ClForest, stragglerTrees, 200);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oBush, 3, 5, 0, 4),
                }, true),
                0,
                RmgenLibrary.AvoidClasses(clWater, 1, clVolcano, 4, ClPlayer, 5,
                    ClForest, 1, ClBaseResource, 4, ClMetal, 4, ClRock, 4),
                RmgenLibrary.ScaleByMapSize(20, 50, MapSize));

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, aDecorativeRock, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aBush2, 1, 2, 0, 1),
                        new ScatterObject(rng, aBush1, 1, 3, 0, 2),
                        new ScatterObject(rng, aBush4, 1, 2, 0, 1),
                        new ScatterObject(rng, aBush3, 1, 3, 0, 2),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapSize(40, 360, MapSize),
                },
                RmgenLibrary.AvoidClasses(clWater, 4, ClPlayer, 5, clVolcano, 4,
                    ClForest, 1, ClBaseResource, 4, ClRock, 4, ClMetal, 4, ClHill, 1));

            CreateBridges(rng, map, areaShoreline, clWater, clShore, clBridge,
                bridgeLength, maxBridges, heightBridge);

            if (areasVolcano.Count != 0)
            {
                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, aSmokeBig, 1, 1, 0, 4),
                    }, false),
                    0, RmgenLibrary.StayClasses(clVolcano, 6),
                    RmgenLibrary.ScaleByMapSize(4, 12, MapSize), 20, areasVolcano);

                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, aSmokeSmall, 2, 2, 0, 4),
                    }, false),
                    0, RmgenLibrary.StayClasses(clVolcano, 4),
                    RmgenLibrary.ScaleByMapSize(4, 12, MapSize), 20, areasVolcano);
            }

            return map.MakeExportable();
        }

        private static (List<int> playerIDs, List<RmgenVector2D> playerPosition)? PlayerPlacementRandom(
            RmgenRng rng, RandomMap map, MapSettings settings, List<int> playerIDs,
            IConstraint? constraints)
        {
            int numPlayers = RmgenCommon.GetNumPlayers(settings);
            var locations = new List<RmgenVector2D>();
            int attempts = 0;
            int resets = 0;

            var mapCenter = map.GetCenter();
            double playerMinDistSquared =
                SafeMath.Square(RmgenLibrary.FractionToTiles(0.25, map.GetSize()));
            double borderDistance = RmgenLibrary.FractionToTiles(0.08, map.GetSize());
            var area = RmgenLibrary.CreateArea(new MapBoundsPlacer(), (IPainter?)null, constraints);
            if (area == null || area.PointCount == 0)
                return null;

            for (int i = 0; i < numPlayers; ++i)
            {
                var position = rng.PickRandom(area.GetPoints());
                bool tooClose = false;
                foreach (var loc in locations)
                    if (loc.DistanceToSquared(position) < playerMinDistSquared)
                    {
                        tooClose = true;
                        break;
                    }

                if (tooClose ||
                    position.DistanceToSquared(mapCenter) >
                    SafeMath.Square(mapCenter.X - borderDistance))
                {
                    --i;
                    ++attempts;

                    if (attempts > 500)
                    {
                        locations = new List<RmgenVector2D>();
                        i = -1;
                        attempts = 0;
                        ++resets;

                        if (resets % 25 == 0)
                            playerMinDistSquared *= 0.95;

                        if (resets == 500)
                            return null;
                    }
                    continue;
                }

                if (locations.Count == i)
                    locations.Add(position);
                else
                    locations[i] = position;
            }

            return RmgenCommon.GroupPlayersByArea(rng, settings, playerIDs, locations);
        }

        private void CreateBridges(RmgenRng rng, RandomMap map, Area? areaShoreline,
            TileClass clWater, TileClass clShore, TileClass clBridge,
            double bridgeLength, double maxBridges, double heightBridge)
        {
            if (areaShoreline == null)
                return;

            int bridges = 0;
            foreach (var bridgeStart in RmgenCommon.ShuffleArray(rng, areaShoreline.GetPoints()))
            {
                if (new NearTileClassConstraint(clBridge, bridgeLength * 8).Allows(bridgeStart))
                    continue;

                for (int direction = 0; direction < 4; ++direction)
                {
                    double bridgeAngle = direction * SafeMath.PI / 2;
                    var bridgeDirection = new RmgenVector2D(1, 0);
                    bridgeDirection.Rotate(bridgeAngle);
                    var areaOffset = new RmgenVector2D(1, 1);
                    var bridgeOffset = new RmgenVector2D(
                        direction % 2 != 0 ? 2 : 0,
                        direction % 2 != 0 ? 0 : 2);
                    var bridgeCenter1 = RmgenVector2D.Add(bridgeStart,
                        RmgenVector2D.Mult(bridgeDirection, bridgeLength / 2));
                    var bridgeCenter2 = RmgenVector2D.Add(bridgeCenter1, bridgeOffset);
                    if (RmgenLibrary.AvoidClasses(clWater, 0).Allows(bridgeCenter1) &&
                        RmgenLibrary.AvoidClasses(clWater, 0).Allows(bridgeCenter2))
                        continue;

                    var bridgeEnd1 = RmgenVector2D.Add(bridgeStart,
                        RmgenVector2D.Mult(bridgeDirection, bridgeLength));
                    var bridgeEnd2 = RmgenVector2D.Add(bridgeEnd1, bridgeOffset);
                    if (RmgenLibrary.AvoidClasses(clShore, 0).Allows(bridgeEnd1) &&
                        RmgenLibrary.AvoidClasses(clShore, 0).Allows(bridgeEnd2))
                        continue;

                    var bridgePerpendicular = bridgeDirection.Perpendicular();
                    var bridgeP = RmgenVector2D.Mult(bridgePerpendicular, bridgeLength / 2);
                    bridgeP.Round();
                    if (RmgenLibrary.AvoidClasses(clWater, 0).Allows(
                            RmgenVector2D.Add(bridgeCenter1, bridgeP)) ||
                        RmgenLibrary.AvoidClasses(clWater, 0).Allows(
                            RmgenVector2D.Sub(bridgeCenter2, bridgeP)))
                        continue;

                    ++bridges;
                    double bridgeOrientation = direction % 2 != 0 ? 0 : SafeMath.PI / 2;
                    if (direction % 2 != 0)
                    {
                        bridgeCenter1.Y += 0.25;
                        bridgeCenter2.Y -= 0.25;
                    }
                    else
                    {
                        bridgeCenter1.X += 0.25;
                        bridgeCenter2.X -= 0.25;
                    }

                    map.PlaceEntityAnywhere(aBridge, 0, bridgeCenter1, bridgeOrientation);
                    map.PlaceEntityAnywhere(aBridge, 0, bridgeCenter2,
                        bridgeOrientation + SafeMath.PI);

                    RmgenLibrary.CreateArea(
                        RectFrom(RmgenVector2D.Sub(bridgeStart, areaOffset),
                            RmgenVector2D.Add(bridgeEnd1, areaOffset)),
                        new IPainter[]
                        {
                            new ElevationPainter(heightBridge),
                            new TileClassPainter(clBridge),
                        },
                        null);

                    foreach (var center in new[] { bridgeStart, bridgeEnd2 })
                        RmgenLibrary.CreateArea(
                            new DiskPlacer(2, center),
                            new SmoothingPainter(1, 1, 1),
                            null);

                    break;
                }

                if (bridges >= maxBridges)
                    break;
            }
        }

        private static RectPlacer RectFrom(RmgenVector2D start, RmgenVector2D end)
        {
            int x1 = (int)SafeMath.Floor(Math.Min(start.X, end.X));
            int y1 = (int)SafeMath.Floor(Math.Min(start.Y, end.Y));
            int x2 = (int)SafeMath.Floor(Math.Max(start.X, end.X));
            int y2 = (int)SafeMath.Floor(Math.Max(start.Y, end.Y));
            return new RectPlacer(x1, y1, x2, y2);
        }
    }
}
