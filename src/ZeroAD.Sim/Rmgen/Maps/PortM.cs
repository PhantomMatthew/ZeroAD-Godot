using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>persian_highlands.js（逐字移植）——干旱中央高原、外围岩山和富矿谷地。
    /// 使用 rmbiome/persian_highlands/ 图专属 biome；环境来自 biome JSON，
    /// placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class PersianHighlandsMap2 : StandardMap
    {
        protected override double HeightLand => 10;

        /// <summary>上游 persian_highlands.json SupportedBiomes = "persian_highlands/"。</summary>
        protected override IReadOnlyList<string> SupportedBiomes => BiomeLoader.PersianHighlandsBiomes;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            var mapCenter = map.GetCenter();

            var tDirtMain = biome.MainTerrain;
            string tCity = biome.Road;
            var tCliff = biome.Cliff;
            var biomeExtras = GetPersianHighlandsExtras(BiomeName);
            object tLakebed1 = biomeExtras.Lakebed1;
            object tLakebed2 = biomeExtras.Lakebed2;
            string tForestFloor = biome.ForestFloor1;
            string tRocky = biome.Tier1Terrain;
            string tRocks = biome.Tier2Terrain;
            string tGrass = biome.Tier3Terrain;

            string oOak = biome.Tree1;
            string oGrapesBush = biome.FruitBush;
            string oCamel = biome.MainHuntableAnimal;
            string oSheep = biome.SecondaryHuntableAnimal;
            string oGoat = biomeExtras.ThirdHuntableAnimal;
            string oStoneLarge = biome.StoneLarge;
            string oStoneSmall = biome.StoneSmall;
            string oMetalLarge = biome.MetalLarge;

            string aDecorativeRock = biome.RockMedium;
            string[] aBushes = biomeExtras.Bushes;

            var pForestO = new object[]
            {
                tForestFloor + "|" + oOak,
                tForestFloor + "|" + oOak,
                tForestFloor,
                tDirtMain,
                tDirtMain,
            };

            const double heightOffsetValley = -10;

            var clPatch = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clCP = new TileClass(MapSize);

            string pattern = settings.PlayerPlacement;
            double teamDist = pattern == "river" ? 0.50 : 0.35;
            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, pattern,
                RmgenLibrary.FractionToTiles(teamDist, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize),
                rng.RandomAngle());

            var mineBushElements = new List<IGroupElement>();
            foreach (string bush in RmgenCommon.ShuffleArray(rng, aBushes))
                mineBushElements.Add(new ScatterObject(rng, bush, 1, 1, 3, 4));

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                playerPosition, tCity, tCity, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = oGrapesBush,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneLarge, (string?)null, (object?)null),
                    },
                    MinesGroupElements = mineBushElements,
                    TreesTemplate = oOak,
                    TreesCount = 3,
                });

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(20, 45, MapSize)), 0),
                new IPainter[]
                {
                    new TerrainPainter(tRocky, rng),
                    new TileClassPainter(clPatch),
                },
                RmgenLibrary.AvoidClasses(clPatch, 2, ClPlayer, 0),
                RmgenLibrary.ScaleByMapSize(5, 20, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(15, 40, MapSize)), 0),
                new IPainter[]
                {
                    new TerrainPainter(new[] { tRocky, tRocks }, rng),
                    new TileClassPainter(clPatch),
                },
                RmgenLibrary.AvoidClasses(clPatch, 2, ClPlayer, 4),
                RmgenLibrary.ScaleByMapSize(15, 50, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(15, 40, MapSize)), 0),
                new IPainter[]
                {
                    new TerrainPainter(new[] { tGrass }, rng),
                    new TileClassPainter(clPatch),
                },
                RmgenLibrary.AvoidClasses(clPatch, 2, ClPlayer, 4),
                RmgenLibrary.ScaleByMapSize(15, 50, MapSize));

            RmgenLibrary.CreateArea(
                new ChainPlacer(rng, 2,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(5, 13, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(35, 200, MapSize)),
                    double.PositiveInfinity, mapCenter, 0,
                    new[] { (int)Math.Floor(RmgenLibrary.ScaleByMapSize(18, 68, MapSize)) }),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tLakebed2, tLakebed1 }, new[] { 6 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetValley, 8, relative: true),
                    new TileClassPainter(clCP),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 18));

            RmgenCommon.CreateMountains(rng, map, tCliff,
                RmgenLibrary.AvoidClasses(ClPlayer, 7, clCP, 5, ClHill,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(18, 25, MapSize))),
                ClHill,
                count: (int)RmgenLibrary.ScaleByMapSize(20, 80, MapSize),
                maxHeight: Math.Floor(RmgenLibrary.ScaleByMapSize(40, 60, MapSize)),
                minRadius: (int)Math.Floor(RmgenLibrary.ScaleByMapSize(3, 4, MapSize)),
                maxRadius: (int)Math.Floor(RmgenLibrary.ScaleByMapSize(6, 12, MapSize)),
                numCircles: (int)Math.Floor(RmgenLibrary.ScaleByMapSize(4, 10, MapSize)));

            double forestTrees = 0.7 * RmgenLibrary.ScaleByMapSize(500, 2500, MapSize);
            double stragglerTrees = (1 - 0.7) * RmgenLibrary.ScaleByMapSize(500, 2500, MapSize);
            var types = new[]
            {
                new object[]
                {
                    new object[] { tDirtMain, tForestFloor, pForestO },
                    new object[] { tForestFloor, pForestO },
                },
                new object[]
                {
                    new object[] { tDirtMain, tForestFloor, pForestO },
                    new object[] { tForestFloor, pForestO },
                },
            };
            double forestSize = forestTrees /
                (RmgenLibrary.ScaleByMapSize(3, 6, MapSize) * NumPlayers);
            double num = Math.Floor(forestSize / types.Length);
            foreach (var type in types)
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(1, 2, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)),
                        Math.Floor(forestSize /
                            Math.Floor(RmgenLibrary.ScaleByMapSize(8, 3, MapSize))),
                        double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 6, ClForest, 10, ClHill, 1, clCP, 1),
                    num);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                    new RandomObject(rng, aBushes, 2, 4, 0, 2),
                }, true, ClRock),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClRock, 10, ClHill, 1, clCP, 1),
                RmgenLibrary.ScaleByMapSize(2, 8, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3),
                    new RandomObject(rng, aBushes, 2, 4, 0, 2),
                }, true, ClRock),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClRock, 10, ClHill, 1, clCP, 1),
                RmgenLibrary.ScaleByMapSize(2, 8, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4),
                    new RandomObject(rng, aBushes, 2, 4, 0, 2),
                }, true, ClMetal),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClMetal, 10, ClRock, 5,
                    ClHill, 1, clCP, 1),
                RmgenLibrary.ScaleByMapSize(2, 8, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                    new RandomObject(rng, aBushes, 2, 4, 0, 2),
                }, true, ClRock),
                0, RmgenLibrary.StayClasses(clCP, 6),
                5 * RmgenLibrary.ScaleByMapSize(5, 30, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3),
                    new RandomObject(rng, aBushes, 2, 4, 0, 2),
                }, true, ClRock),
                0, RmgenLibrary.StayClasses(clCP, 6),
                5 * RmgenLibrary.ScaleByMapSize(5, 30, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4),
                    new RandomObject(rng, aBushes, 2, 4, 0, 2),
                }, true, ClMetal),
                0, RmgenLibrary.StayClasses(clCP, 6),
                5 * RmgenLibrary.ScaleByMapSize(5, 30, MapSize), 50);

            var group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aDecorativeRock, 1, 3, 0, 1),
            }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aBushes[1], 1, 2, 0, 1),
                new ScatterObject(rng, aBushes[0], 1, 3, 0, 2),
            }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oGoat, 5, 7, 0, 4),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 1, ClHill, 1, clFood, 20, clCP, 2),
                3 * NumPlayers, 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oSheep, 2, 3, 0, 2),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 1, ClHill, 1, clFood, 20, clCP, 2),
                3 * NumPlayers, 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oGrapesBush, 5, 7, 0, 4),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10, clCP, 2),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oCamel, 2, 3, 0, 2),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.StayClasses(clCP, 2),
                3 * NumPlayers, 50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oOak },
                RmgenLibrary.AvoidClasses(ClForest, 1, ClHill, 1, ClPlayer, 1,
                    ClBaseResource, 6, ClMetal, 6, ClRock, 6, clCP, 2),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }

        private static PersianHighlandsExtras GetPersianHighlandsExtras(string biomeName)
        {
            if (biomeName == "persian_highlands/summer")
                return new PersianHighlandsExtras(
                    new[] { "desert_lakebed_dry_b", "desert_lakebed_dry" },
                    new[]
                    {
                        "desert_lakebed_dry_b", "desert_lakebed_dry",
                        "desert_shore_stones", "desert_shore_stones",
                    },
                    "gaia/fauna_goat",
                    new[]
                    {
                        "actor|props/flora/bush_desert_a.xml",
                        "actor|props/flora/bush_desert_dry_a.xml",
                        "actor|props/flora/bush_dry_a.xml",
                        "actor|props/flora/plant_desert_a.xml",
                    });

            return new PersianHighlandsExtras(
                new[] { "desert_lakebed_dry_b", "desert_lakebed_dry" },
                "desert_grass_a_sand",
                "gaia/fauna_goat",
                new[]
                {
                    "actor|props/flora/bush_desert_a.xml",
                    "actor|props/flora/bush_desert_dry_a.xml",
                    "actor|props/flora/bush_dry_a.xml",
                    "actor|props/flora/plant_desert_a.xml",
                });
        }

        private readonly struct PersianHighlandsExtras
        {
            public readonly object Lakebed1;
            public readonly object Lakebed2;
            public readonly string ThirdHuntableAnimal;
            public readonly string[] Bushes;

            public PersianHighlandsExtras(object lakebed1, object lakebed2,
                string thirdHuntableAnimal, string[] bushes)
            {
                Lakebed1 = lakebed1;
                Lakebed2 = lakebed2;
                ThirdHuntableAnimal = thirdHuntableAnimal;
                Bushes = bushes;
            }
        }
    }

    /// <summary>phoenician_levant.js（逐字移植）——黎凡特海岸：玩家沿海岸线排布，
    /// 宽阔海域、塞浦路斯小岛、海鱼和地中海灌木。环境设置与 placePlayersNomad
    /// 按既有移植约定省略。</summary>
    public sealed class PhoenicianLevantMap2 : StandardMap
    {
        private const string tCity = "medit_city_pavement";
        private const string tCityPlaza = "medit_city_pavement";
        private static readonly string[] tHill =
        {
            "medit_dirt",
            "medit_dirt_b",
            "medit_dirt_c",
            "medit_rocks_grass",
            "medit_rocks_grass",
        };
        private const string tMainDirt = "medit_dirt";
        private const string tCliff = "medit_cliff_aegean";
        private const string tForestFloor = "medit_rocks_shrubs";
        private const string tGrass = "medit_rocks_grass";
        private const string tRocksShrubs = "medit_rocks_shrubs";
        private const string tRocksGrass = "medit_rocks_grass";
        private const string tDirt = "medit_dirt_b";
        private const string tDirtB = "medit_dirt_c";
        private const string tShore = "medit_sand";
        private const string tWater = "medit_sand_wet";

        private const string oGrapeBush = "gaia/fruit/grapes";
        private const string oDeer = "gaia/fauna_deer";
        private const string oFish = "gaia/fish/generic";
        private const string oSheep = "gaia/fauna_sheep";
        private const string oGoat = "gaia/fauna_goat";
        private const string oStoneLarge = "gaia/rock/mediterranean_large";
        private const string oStoneSmall = "gaia/rock/mediterranean_small";
        private const string oMetalLarge = "gaia/ore/mediterranean_large";
        private const string oDatePalm = "gaia/tree/cretan_date_palm_short";
        private const string oSDatePalm = "gaia/tree/cretan_date_palm_tall";
        private const string oCarob = "gaia/tree/carob";
        private const string oFanPalm = "gaia/tree/medit_fan_palm";
        private const string oPoplar = "gaia/tree/poplar_lombardy";
        private const string oCypress = "gaia/tree/cypress";

        private const string aBush1 = "actor|props/flora/bush_medit_sm.xml";
        private const string aBush2 = "actor|props/flora/bush_medit_me.xml";
        private const string aBush3 = "actor|props/flora/bush_medit_la.xml";
        private const string aBush4 = "actor|props/flora/bush_medit_me.xml";
        private const string aDecorativeRock = "actor|geology/stone_granite_med.xml";

        protected override double HeightLand => 1;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tHill);
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightSeaGround = -3;
            const double heightShore = -1.5;
            const double heightLand = 1;
            const double heightIsland = 6;
            const double heightHill = 15;
            const double heightOffsetBump = 2;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clGrass = new TileClass(MapSize);
            var clIsland = new TileClass(MapSize);

            var pForest = new[]
            {
                tForestFloor + "|" + oDatePalm,
                tForestFloor + "|" + oSDatePalm,
                tForestFloor + "|" + oCarob,
                tForestFloor,
                tForestFloor,
            };

            double startAngle = rng.RandIntInclusive(0, 3) * SafeMath.PI / 2;
            var playerIDs = RmgenCommon.SortAllPlayers(rng, settings);
            var playerPosition = PlayerPlacementLine(map, NumPlayers, SafeMath.PI / 2,
                new RmgenVector2D(RmgenLibrary.FractionToTiles(0.76, MapSize), mapCenter.Y),
                RmgenLibrary.FractionToTiles(0.2, MapSize));
            for (int i = 0; i < playerPosition.Count; ++i)
            {
                var position = playerPosition[i];
                position.RotateAround(startAngle, mapCenter);
                playerPosition[i] = position;
            }

            RmgenCommon.PlacePlayerBases(rng, map, settings, tHill[0], ClPlayer, null,
                playerPosition, tCityPlaza, tCity, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = oGrapeBush,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneLarge, (string?)null, (object?)null),
                    },
                    TreesTemplate = oCarob,
                    TreesCount = 2,
                    DecorativesTemplate = aBush1,
                });

            var riverStart = new RmgenVector2D(0, MapSize);
            riverStart.RotateAround(startAngle, mapCenter);
            var riverEnd = new RmgenVector2D(0, 0);
            riverEnd.RotateAround(startAngle, mapCenter);
            RmgenCommon.PaintRiver(rng, map, riverStart, riverEnd, MapSize,
                RmgenLibrary.ScaleByMapSize(6, 25, MapSize),
                heightSeaGround, heightLand,
                parallel: true, deviation: 0, meanderShort: 20, meanderLong: 0);

            RmgenLibrary.PaintTileClassBasedOnHeight(double.NegativeInfinity, heightLand,
                HeightPlacer.Mode.ExcludeMinExcludeMax, clWater);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, double.NegativeInfinity, heightShore,
                HeightPlacer.Mode.ExcludeMinExcludeMax, tWater);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightShore, heightLand,
                HeightPlacer.Mode.ExcludeMinExcludeMax, tShore);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize),
                    0.3, 0.06, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 20),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

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
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 1, ClHill, 15, clWater, 0),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers * 3);

            double forestTrees = 0.5 * RmgenLibrary.ScaleByMapSize(500, 2500, MapSize);
            double stragglerTrees = (1 - 0.5) * RmgenLibrary.ScaleByMapSize(500, 2500, MapSize);
            double num = RmgenLibrary.ScaleByMapSize(10, 42, MapSize);
            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                    forestTrees / (num * Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize))), 0.5),
                new IPainter[]
                {
                    new TerrainPainter(new object[] { tForestFloor, pForest }, rng),
                    new TileClassPainter(ClForest),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 10, clWater, 1, ClHill, 1,
                    clBaseResource, 3),
                num, 50);

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        size, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new[] { tGrass, tRocksShrubs },
                            new[] { tRocksShrubs, tRocksGrass },
                            new[] { tRocksGrass, tGrass },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, clGrass, 5, ClPlayer, 10,
                        clWater, 4, ClDirt, 5, ClHill, 1),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        size, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new[] { tDirt, tDirtB },
                            new[] { tDirt, tMainDirt },
                            new[] { tDirtB, tMainDirt },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClDirt, 5, ClPlayer, 10,
                        clWater, 4, clGrass, 5, ClHill, 1),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng,
                    RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.08, MapSize)),
                    0.2, 0.1, 0.01),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tShore, tHill }, new[] { 12 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightIsland, 8),
                    new TileClassPainter(clIsland),
                    new TileClassUnPainter(clWater),
                },
                RmgenLibrary.StayClasses(clWater, 8),
                1, 100);

            var mines = new IGroupElement[][]
            {
                new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                },
                new IGroupElement[] { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) },
                new IGroupElement[] { new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3) },
            };
            foreach (var mine in mines)
                RmgenLibrary.CreateObjectGroups(rng,
                    new ObjectGroup(mine, true,
                        ReferenceEquals(mine, mines[1]) ? ClMetal : ClRock),
                    0,
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.StayClasses(clIsland, 9),
                        RmgenLibrary.AvoidClasses(ClForest, 1, ClRock, 8, ClMetal, 8),
                    }),
                    RmgenLibrary.ScaleByMapSize(4, 16, MapSize));

            var group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClRock, 10, clWater, 3, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClRock, 10, clWater, 3, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4),
            }, true, ClMetal);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClMetal, 10, ClRock, 5,
                    clWater, 3, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aDecorativeRock, 1, 3, 0, 1),
            }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClPlayer, 0, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aBush2, 1, 2, 0, 1),
                new ScatterObject(rng, aBush1, 1, 3, 0, 2),
                new ScatterObject(rng, aBush4, 1, 2, 0, 1),
                new ScatterObject(rng, aBush3, 1, 3, 0, 2),
            }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClPlayer, 0, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(40, 360, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oFish, 1, 3, 2, 6),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clIsland, 2, clFood, 10),
                    RmgenLibrary.StayClasses(clWater, 5),
                }),
                20 * RmgenLibrary.ScaleByMapSize(15, 20, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oSheep, 5, 7, 0, 4),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 7, clWater, 3, clFood, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(5, 20, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oGoat, 2, 4, 0, 3),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 7, clWater, 3, clFood, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(5, 20, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oDeer, 2, 4, 0, 2),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 7, clWater, 3, clFood, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(5, 20, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oGrapeBush, 5, 7, 0, 4),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 15, ClHill, 1, clFood, 7),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oDatePalm, oSDatePalm, oCarob, oFanPalm, oPoplar, oCypress },
                RmgenLibrary.AvoidClasses(ClForest, 0, clWater, 4, ClPlayer, 8, ClMetal, 6, ClHill, 1),
                ClForest, stragglerTrees);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oDatePalm, oSDatePalm, oCarob, oFanPalm, oPoplar, oCypress },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clIsland, 9),
                    RmgenLibrary.AvoidClasses(ClRock, 4, ClMetal, 4),
                }),
                ClForest, 3 * stragglerTrees);

            return map.MakeExportable();
        }

        private static List<RmgenVector2D> PlayerPlacementLine(RandomMap map, int numPlayers,
            double angle, RmgenVector2D center, double width)
        {
            var playerPosition = new List<RmgenVector2D>();
            for (int i = 0; i < numPlayers; ++i)
            {
                var offset = new RmgenVector2D(
                    RmgenLibrary.FractionToTiles((i + 1.0) / (numPlayers + 1) - 0.5,
                        map.GetSize()),
                    width * (i % 2 - 0.5));
                offset.Rotate(angle);
                var position = RmgenVector2D.Add(center, offset);
                position.Round();
                playerPosition.Add(position);
            }

            return playerPosition;
        }
    }

    /// <summary>scythian_rivulet.js（逐字移植）——雪原小河、支流、浅滩与稀疏针叶林。
    /// setWindAngle(startAngle) 依赖局部变量，保留在 ApplyExtraEnvironment；其余环境设置
    /// 与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class ScythianRivuletMap2 : StandardMap
    {
        private const string tMainTerrain = "alpine_snow_a";
        private const string tTier1Terrain = "snow rough";
        private const string tTier2Terrain = "snow_01";
        private const string tTier3Terrain = "snow rocks";
        private const string tForestFloor1 = "alpine_forrestfloor_snow";
        private const string tForestFloor2 = "polar_snow_rocks";
        private static readonly string[] tCliff = { "alpine_cliff_a", "alpine_cliff_b" };
        private const string tHill = "alpine_snow_glacial";
        private const string tRoad = "new_alpine_citytile";
        private const string tRoadWild = "alpine_snow_rocky";
        private const string tShore = "alpine_shore_rocks_icy";
        private const string tWater = "polar_ice_b";

        private const string oTreeDead = "gaia/tree/dead";
        private const string oOak = "gaia/tree/oak_dead";
        private const string oPine = "gaia/tree/pine";
        private const string oGrapes = "gaia/fruit/grapes";
        private const string oBush = "gaia/tree/bush_badlands";
        private const string oDeer = "gaia/fauna_deer";
        private const string oRabbit = "gaia/fauna_rabbit";
        private const string oWolf1 = "gaia/fauna_wolf";
        private const string oWolf2 = "gaia/fauna_wolf_arctic";
        private const string oHawk = "birds/buzzard";
        private const string oFish = "gaia/fish/generic";
        private const string oStoneLarge = "gaia/rock/alpine_large";
        private const string oStoneSmall = "gaia/rock/alpine_small";
        private const string oMetalLarge = "gaia/ore/alpine_large";

        private const string aRockLarge = "actor|geology/stone_granite_large.xml";
        private const string aRockMedium = "actor|geology/stone_granite_med.xml";
        private const string aBushMedium = "actor|props/flora/plant_desert_a.xml";
        private const string aBushSmall = "actor|props/flora/bush_desert_a.xml";
        private const string aReeds = "actor|props/flora/reeds_pond_lush_a.xml";
        private const string aOutpostPalisade = "actor|props/structures/britons/outpost_palisade.xml";
        private const string aWorkshopChariot = "actor|props/structures/britons/workshop_chariot_01.xml";

        private double _startAngle;

        protected override double HeightLand => 1;

        protected internal override void ApplyExtraEnvironment(RmgenEnvironment env, RmgenRng rng)
            => env.SetWindAngle(_startAngle);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tMainTerrain);
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightSeaGround = -2;
            const double heightShoreLower = 0.7;
            const double heightShoreUpper = 1;
            const double heightLand = 2;
            const double heightSnowline = 12;
            const double heightOffsetLargeBumps = 4;

            var clRiver = new TileClass(MapSize);
            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clShallowsFlora = new TileClass(MapSize);

            double riverWidth = RmgenLibrary.FractionToTiles(0.1, MapSize);
            double startAngle = _startAngle = rng.RandomAngle();
            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementRiver(
                rng, map, settings, startAngle, RmgenLibrary.FractionToTiles(0.6, MapSize));

            if (!settings.Nomad)
                foreach (var position in playerPosition)
                    RmgenCommon.AddCivicCenterAreaToClass(map, position, ClPlayer);

            var riverStart = new RmgenVector2D(mapCenter.X, MapSize);
            riverStart.RotateAround(startAngle, mapCenter);
            var riverEnd = new RmgenVector2D(mapCenter.X, 0);
            riverEnd.RotateAround(startAngle, mapCenter);
            RmgenCommon.PaintRiver(rng, map, riverStart, riverEnd, riverWidth,
                RmgenLibrary.ScaleByMapSize(3, 14, MapSize),
                heightSeaGround, heightLand,
                parallel: false, deviation: 6, meanderShort: 40, meanderLong: 20);

            RmgenLibrary.PaintTileClassBasedOnHeight(double.NegativeInfinity, heightShoreUpper,
                HeightPlacer.Mode.ExcludeMinExcludeMax, clRiver);

            CreateTributaryRivers(startAngle + SafeMath.PI / 2,
                4, 10, heightSeaGround,
                new[] { double.NegativeInfinity, heightSeaGround },
                SafeMath.PI / 5,
                clWater, null,
                RmgenLibrary.AvoidClasses(ClPlayer, 4));

            RmgenCommon.PlacePlayerBases(rng, map, settings, tMainTerrain, ClPlayer, null,
                playerPosition, tRoadWild, tRoad, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    ExtraBaseResourceConstraint = RmgenLibrary.AvoidClasses(clWater, 4),
                    StartingAnimal = true,
                    StartingAnimalTemplate = oDeer,
                    StartingAnimalDistance = 18,
                    StartingAnimalMinGroupDistance = 2,
                    StartingAnimalMaxGroupDistance = 4,
                    StartingAnimalMinGroupCount = 2,
                    StartingAnimalMaxGroupCount = 3,
                    BerriesTemplate = oGrapes,
                    BerriesMinCount = 3,
                    BerriesMaxCount = 3,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneLarge, (string?)null, (object?)null),
                    },
                    TreesTemplate = oTreeDead,
                    TreesCount = 10,
                    DecorativesTemplate = aBushSmall,
                    DecorativesMinDist = 10,
                    DecorativesMaxDist = 12,
                });

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(15, 60, MapSize)), 0.8),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaGround, 3),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20),
                RmgenLibrary.ScaleByMapSize(6, 20, MapSize));

            CreateDefaultBumps(rng, RmgenLibrary.AvoidClasses(ClPlayer, 2));

            var terrainConstraint = RmgenLibrary.AvoidClasses(ClPlayer, 20, clWater, 1,
                ClHill, 15, clRiver, 10);
            if (rng.RandBool())
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tCliff, tCliff, tHill },
                            new[] { 1, 2 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            18, 2),
                        new TileClassPainter(ClHill),
                    },
                    terrainConstraint,
                    RmgenLibrary.ScaleByMapSize(3, 15, MapSize));
            else
                RmgenCommon.CreateMountains(rng, map, tCliff, terrainConstraint, ClHill,
                    count: (int)RmgenLibrary.ScaleByMapSize(3, 15, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize),
                    0.3, 0.06, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetLargeBumps, 3, relative: true),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 2),
                RmgenLibrary.ScaleByMapSize(100, 800, MapSize));

            CreateDefaultBumps(rng, RmgenLibrary.AvoidClasses(ClPlayer, 20));

            RmgenLibrary.PaintTileClassBasedOnHeight(double.NegativeInfinity, heightShoreUpper,
                HeightPlacer.Mode.ExcludeMinExcludeMax, clWater);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, double.NegativeInfinity, heightShoreUpper,
                HeightPlacer.Mode.ExcludeMinExcludeMax, tWater);
            // 上游 min/max 顺序相反，保持无效果的刷漆调用。
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightShoreUpper, heightShoreLower,
                HeightPlacer.Mode.ExcludeMinExcludeMax, tShore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightSnowline, double.PositiveInfinity,
                HeightPlacer.Mode.ExcludeMinExcludeMax, tMainTerrain);

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        size, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new[] { tMainTerrain, tTier1Terrain },
                            new[] { tTier1Terrain, tTier2Terrain },
                            new[] { tTier2Terrain, tTier3Terrain },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClHill, 2, ClDirt, 5, ClPlayer, 12,
                        clWater, 5, ClForest, 4),
                    RmgenLibrary.ScaleByMapSize(25, 55, MapSize));

            double forestTrees = 0.7 * RmgenLibrary.ScaleByMapSize(200, 1200, MapSize);
            double stragglerTrees = (1 - 0.7) * RmgenLibrary.ScaleByMapSize(200, 1200, MapSize);
            var pForest1 = new[]
            {
                tForestFloor2 + "|" + oTreeDead,
                tForestFloor2 + "|" + oOak,
                tForestFloor2,
            };
            var pForest2 = new[]
            {
                tForestFloor1 + "|" + oTreeDead,
                tForestFloor1,
            };
            GaiaEntities.CreateForests(rng, map,
                new object[] { tForestFloor1, tForestFloor2, tForestFloor1, pForest1, pForest2 },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, clWater, 2, ClHill, 2, ClForest, 12),
                ClForest, forestTrees, NumPlayers);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oTreeDead, oOak, oPine, oBush },
                RmgenLibrary.AvoidClasses(ClPlayer, 17, clWater, 2, ClHill, 2,
                    ClForest, 1, clRiver, 4),
                ClForest, stragglerTrees);

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
                RmgenLibrary.AvoidClasses(ClForest, 4, clWater, 1, ClPlayer, 20, ClRock, 15, ClHill, 1),
                ClRock);

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) },
                },
                RmgenLibrary.AvoidClasses(ClForest, 4, clWater, 1, ClPlayer, 20, ClMetal, 15,
                    ClRock, 5, ClHill, 1),
                ClMetal);

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aBushSmall, 1, 2, 0, 1),
                        new ScatterObject(rng, aBushMedium, 1, 3, 0, 2),
                        new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapSize(40, 360, MapSize),
                },
                RmgenLibrary.AvoidClasses(clWater, 2, ClForest, 0, ClPlayer, 20, ClHill, 1));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oHawk, 1, 1, 0, 3) },
                    new IGroupElement[] { new ScatterObject(rng, oWolf1, 4, 6, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, oWolf2, 4, 8, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, oDeer, 4, 6, 0, 2) },
                    new IGroupElement[] { new ScatterObject(rng, oRabbit, 1, 3, 4, 6) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(3, 10, MapSize),
                    RmgenLibrary.ScaleByMapSize(3, 10, MapSize),
                    RmgenLibrary.ScaleByMapSize(3, 10, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                },
                RmgenLibrary.AvoidClasses(clWater, 3, ClPlayer, 20, ClHill, 1, clFood, 10),
                null);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oFish, 1, 2, 0, 2) },
                },
                new double[] { 12 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 8, ClForest, 1, ClHill, 4),
                    RmgenLibrary.StayClasses(clWater, 2),
                }),
                clFood);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aReeds, 6, 14, 1, 5),
                }, false, clShallowsFlora),
                0,
                new AndConstraint(new IConstraint[]
                {
                    new HeightConstraint(map, -1, 0),
                    RmgenLibrary.AvoidClasses(clShallowsFlora, 25),
                }),
                20 * RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 80);

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, aOutpostPalisade, 1, 1, 0, 1) },
                    new IGroupElement[] { new ScatterObject(rng, aWorkshopChariot, 1, 1, 0, 1) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(2, 7, MapSize),
                    RmgenLibrary.ScaleByMapSize(2, 7, MapSize),
                },
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClBaseResource, 5,
                    ClHill, 4, clFood, 4, clWater, 5, ClRock, 9, ClMetal, 9));

            return map.MakeExportable();
        }

        private void CreateDefaultBumps(RmgenRng rng, IConstraint constraint)
            => RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        2, 2, relative: true),
                },
                constraint,
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

        private void CreateTributaryRivers(double riverAngle, int riverCount, double riverWidth,
            double heightRiverbed, IReadOnlyList<double> heightRange, double maxAngle,
            TileClass tributaryRiverTileClass, TileClass? shallowTileClass, IConstraint constraint)
        {
            const double waviness = 0.4;
            double smoothness = RmgenLibrary.ScaleByMapSize(3, 12, MapSize);
            const double offset = 0.1;
            const double tapering = 0.05;
            const double heightShallow = -2;

            var map = Map;
            var mapCenter = map.GetCenter();

            IConstraint riverConstraint = RmgenLibrary.AvoidClasses(tributaryRiverTileClass, 3);
            if (shallowTileClass != null)
                riverConstraint = new AndConstraint(new IConstraint[]
                {
                    riverConstraint,
                    RmgenLibrary.AvoidClasses(shallowTileClass, 2),
                });

            for (int i = 0; i < riverCount; ++i)
            {
                var searchCenter = new RmgenVector2D(
                    RmgenLibrary.FractionToTiles(Rng.RandFloat(tapering, 1 - tapering), MapSize),
                    mapCenter.Y);
                double sign = Rng.RandBool() ? 1 : -1;
                var distanceVec = new RmgenVector2D(0, sign * tapering);

                var searchStart = RmgenVector2D.Add(searchCenter, distanceVec);
                searchStart.RotateAround(riverAngle, mapCenter);
                var searchEnd = RmgenVector2D.Sub(searchCenter, distanceVec);
                searchEnd.RotateAround(riverAngle, mapCenter);

                var startLocation = RmgenCommon.FindLocationInDirectionBasedOnHeight(map,
                    searchStart, searchEnd, heightRange[0], heightRange[1], 4);
                if (!startLocation.HasValue)
                    continue;

                var start = startLocation.Value;
                start.Round();
                var endOffset = new RmgenVector2D(MapSize, 0);
                endOffset.Rotate(riverAngle -
                    sign * Rng.RandFloat(maxAngle, 2 * SafeMath.PI - maxAngle));
                var end = RmgenVector2D.Add(mapCenter, endOffset);
                end.Round();

                var area = RmgenLibrary.CreateArea(
                    new PathPlacer(Rng, waviness, smoothness, offset, tapering)
                    {
                        Start = start,
                        End = end,
                        Width = riverWidth,
                    },
                    new IPainter[]
                    {
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            heightRiverbed, 4),
                        new TileClassPainter(tributaryRiverTileClass),
                    },
                    new AndConstraint(new IConstraint[] { constraint, riverConstraint }));

                if (area == null)
                    continue;

                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(riverWidth / 2),
                        0.95, 0.6, double.PositiveInfinity, end),
                    new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                        heightRiverbed, 3),
                    constraint);
            }

            if (shallowTileClass == null)
                return;

            foreach (double z in new[] { 0.25, 0.75 })
            {
                var start = new RmgenVector2D(0, RmgenLibrary.FractionToTiles(z, MapSize));
                start.RotateAround(riverAngle, mapCenter);
                var end = new RmgenVector2D(MapSize, RmgenLibrary.FractionToTiles(z, MapSize));
                end.RotateAround(riverAngle, mapCenter);

                RmgenCommon.CreatePassage(Rng, map, start, end,
                    RmgenLibrary.ScaleByMapSize(8, 12, MapSize),
                    RmgenLibrary.ScaleByMapSize(8, 12, MapSize),
                    2, tileClass: shallowTileClass,
                    constraints: new HeightConstraint(map, double.NegativeInfinity, heightShallow),
                    startHeight: heightShallow, endHeight: heightShallow);
            }
        }
    }

    /// <summary>lorraine_plain.js（逐字移植）——温带河流平原：横贯主河、浅滩、
    /// 支流、湿地森林与草地。Walls="towers"、环境设置与 placePlayersNomad
    /// 按既有移植约定省略。</summary>
    public sealed class LorrainePlainMap2 : StandardMap
    {
        private const string tPrimary = "temp_grass_long";
        private static readonly string[] tGrass = { "temp_grass", "temp_grass", "temp_grass_d" };
        private const string tGrassPForest = "temp_plants_bog";
        private const string tGrassDForest = "temp_plants_bog";
        private const string tGrassA = "temp_grass_plants";
        private const string tGrassB = "temp_plants_bog";
        private const string tGrassC = "temp_mud_a";
        private const string tRoad = "temp_road";
        private const string tRoadWild = "temp_road_overgrown";
        private const string tGrassPatchBlend = "temp_grass_long_b";
        private static readonly string[] tGrassPatch = { "temp_grass_d", "temp_grass_clovers" };
        private const string tWater = "temp_mud_a";

        private const string oBeech = "gaia/tree/euro_beech";
        private const string oOak = "gaia/tree/oak";
        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oDeer = "gaia/fauna_deer";
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

        protected override double HeightLand => 3;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tPrimary);
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightSeaGround = -4;
            const double heightShallows = -2;
            const double heightOffsetBump = 2;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clShallow = new TileClass(MapSize);

            var pForestB = new[] { tGrassDForest + "|" + oBeech, tGrassDForest };
            var pForestO = new[] { tGrassPForest + "|" + oOak, tGrassPForest };
            var pForestR = new[]
            {
                tGrassDForest + "|" + oBeech,
                tGrassDForest,
                tGrassDForest + "|" + oOak,
                tGrassDForest,
                tGrassDForest,
                tGrassDForest,
            };

            double shallowWidth = RmgenLibrary.ScaleByMapSize(8, 12, MapSize);
            double startAngle = rng.RandomAngle();

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementRiver(rng, map, settings,
                startAngle + SafeMath.PI / 2, RmgenLibrary.FractionToTiles(0.5, MapSize));
            RmgenCommon.PlacePlayerBases(rng, map, settings, tPrimary, ClPlayer, null,
                playerPosition, tRoadWild, tRoad, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = oBerryBush,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneLarge, (string?)null, (object?)null),
                    },
                    TreesTemplate = oOak,
                    TreesCount = 3,
                    DecorativesTemplate = aGrassShort,
                });

            var riverPositions = new List<RmgenVector2D>
            {
                new(1, mapCenter.Y),
                new(MapSize - 1, mapCenter.Y),
            };
            for (int i = 0; i < riverPositions.Count; ++i)
            {
                var position = riverPositions[i];
                position.RotateAround(startAngle, mapCenter);
                riverPositions[i] = position;
            }

            RmgenLibrary.CreateArea(
                new PathPlacer(rng, 0.5, RmgenLibrary.ScaleByMapSize(0.5, 2, MapSize),
                    0.1, 0.01)
                {
                    Start = riverPositions[0],
                    End = riverPositions[1],
                    Width = RmgenLibrary.ScaleByMapSize(10, 20, MapSize),
                },
                new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                    heightSeaGround, 4),
                RmgenLibrary.AvoidClasses(ClPlayer, 4));

            foreach (var riverPosition in riverPositions)
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng,
                        RmgenGeometry.DiskArea(RmgenLibrary.ScaleByMapSize(5, 10, MapSize)),
                        0.95, 0.6, double.PositiveInfinity, riverPosition),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaGround, 2),
                    RmgenLibrary.AvoidClasses(ClPlayer, 8));

            int shallows = rng.RandIntInclusive(3, RmgenLibrary.ScaleByMapSize(4, 6, MapSize));
            for (int i = 0; i <= shallows; ++i)
            {
                double location = RmgenLibrary.FractionToTiles(rng.RandFloat(0.15, 0.85), MapSize);
                var start = new RmgenVector2D(location, MapSize);
                start.RotateAround(startAngle, mapCenter);
                var end = new RmgenVector2D(location, 0);
                end.RotateAround(startAngle, mapCenter);

                RmgenCommon.CreatePassage(rng, map, start, end,
                    shallowWidth, shallowWidth, 2,
                    tileClass: clShallow,
                    constraints: new HeightConstraint(map, double.NegativeInfinity, heightShallows),
                    startHeight: heightShallows, endHeight: heightShallows);
            }

            CreateTributaryRivers(startAngle,
                rng.RandIntInclusive(9, RmgenLibrary.ScaleByMapSize(13, 21, MapSize)),
                RmgenLibrary.ScaleByMapSize(10, 20, MapSize),
                heightSeaGround,
                new[] { -6.0, -1.5 },
                SafeMath.PI / 5,
                clWater,
                clShallow,
                RmgenLibrary.AvoidClasses(ClPlayer, 3, clBaseResource, 4));

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -5, 1,
                HeightPlacer.Mode.IncludeMinExcludeMax, tWater);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, 2,
                HeightPlacer.Mode.IncludeMinExcludeMax, pForestR);
            RmgenLibrary.PaintTileClassBasedOnHeight(-6, 0.5,
                HeightPlacer.Mode.IncludeMinExcludeMax, clWater);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize),
                    0.3, 0.06, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 15),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            double forestTrees = 0.7 * RmgenLibrary.ScaleByMapSize(500, 2500, MapSize);
            double stragglerTrees = (1 - 0.7) * RmgenLibrary.ScaleByMapSize(500, 2500, MapSize);
            GaiaEntities.CreateForests(rng, map,
                new object[] { tGrass, tGrassDForest, tGrassPForest, pForestB, pForestO },
                RmgenLibrary.AvoidClasses(ClPlayer, 15, clWater, 3, ClForest, 16, ClHill, 1),
                ClForest, forestTrees, NumPlayers);

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        size, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new object[] { tGrass, tGrassA },
                            tGrassB,
                            new[] { tGrassB, tGrassC },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClHill, 0,
                        ClDirt, 5, ClPlayer, 6),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        size, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tGrassPatchBlend, tGrassPatch },
                            new[] { 1 }, rng),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClHill, 0,
                        ClDirt, 5, ClPlayer, 6),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            var group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 1, ClPlayer, 15, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 1, ClPlayer, 15, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4),
            }, true, ClMetal);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 1, ClPlayer, 15, ClMetal, 10,
                    ClRock, 5, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aRockMedium, 1, 3, 0, 1),
            }, true);
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

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oDeer, 5, 7, 0, 4),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 15, ClHill, 1, clFood, 20),
                3 * NumPlayers, 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oRabbit, 2, 3, 0, 2),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 15, ClHill, 1, clFood, 20),
                3 * NumPlayers, 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oBerryBush, 5, 7, 0, 4),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 15, ClHill, 1, clFood, 10),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oOak, oBeech },
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 7, ClHill, 1, ClPlayer, 5,
                    ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aGrassShort, 1, 2, 0, 1, -SafeMath.PI / 8, SafeMath.PI / 8),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 2, ClHill, 2, ClPlayer, 2, ClDirt, 0),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aGrass, 2, 4, 0, 1.8, -SafeMath.PI / 8, SafeMath.PI / 8),
                new ScatterObject(rng, aGrassShort, 3, 6, 1.2, 2.5, -SafeMath.PI / 8, SafeMath.PI / 8),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClHill, 2, ClPlayer, 2, ClDirt, 1, ClForest, 0),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aBushMedium, 1, 2, 0, 2),
                new ScatterObject(rng, aBushSmall, 2, 4, 0, 2),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClHill, 1, ClPlayer, 1, ClDirt, 1),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aLillies, 1, 2, 0, 2),
                new ScatterObject(rng, aReeds, 2, 4, 0, 2),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.StayClasses(clShallow, 1),
                60 * RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 80);

            return map.MakeExportable();
        }

        private void CreateTributaryRivers(double riverAngle, int riverCount, double riverWidth,
            double heightRiverbed, IReadOnlyList<double> heightRange, double maxAngle,
            TileClass tributaryRiverTileClass, TileClass? shallowTileClass, IConstraint constraint)
        {
            const double waviness = 0.4;
            double smoothness = RmgenLibrary.ScaleByMapSize(3, 12, MapSize);
            const double offset = 0.1;
            const double tapering = 0.05;
            const double heightShallow = -2;

            var map = Map;
            var mapCenter = map.GetCenter();

            IConstraint riverConstraint = RmgenLibrary.AvoidClasses(tributaryRiverTileClass, 3);
            if (shallowTileClass != null)
                riverConstraint = new AndConstraint(new IConstraint[]
                {
                    riverConstraint,
                    RmgenLibrary.AvoidClasses(shallowTileClass, 2),
                });

            for (int i = 0; i < riverCount; ++i)
            {
                var searchCenter = new RmgenVector2D(
                    RmgenLibrary.FractionToTiles(Rng.RandFloat(tapering, 1 - tapering), MapSize),
                    mapCenter.Y);
                double sign = Rng.RandBool() ? 1 : -1;
                var distanceVec = new RmgenVector2D(0, sign * tapering);

                var searchStart = RmgenVector2D.Add(searchCenter, distanceVec);
                searchStart.RotateAround(riverAngle, mapCenter);
                var searchEnd = RmgenVector2D.Sub(searchCenter, distanceVec);
                searchEnd.RotateAround(riverAngle, mapCenter);

                var startLocation = RmgenCommon.FindLocationInDirectionBasedOnHeight(map,
                    searchStart, searchEnd, heightRange[0], heightRange[1], 4);
                if (!startLocation.HasValue)
                    continue;

                var start = startLocation.Value;
                start.Round();
                var endOffset = new RmgenVector2D(MapSize, 0);
                endOffset.Rotate(riverAngle -
                    sign * Rng.RandFloat(maxAngle, 2 * SafeMath.PI - maxAngle));
                var end = RmgenVector2D.Add(mapCenter, endOffset);
                end.Round();

                var area = RmgenLibrary.CreateArea(
                    new PathPlacer(Rng, waviness, smoothness, offset, tapering)
                    {
                        Start = start,
                        End = end,
                        Width = riverWidth,
                    },
                    new IPainter[]
                    {
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            heightRiverbed, 4),
                        new TileClassPainter(tributaryRiverTileClass),
                    },
                    new AndConstraint(new IConstraint[] { constraint, riverConstraint }));

                if (area == null)
                    continue;

                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(riverWidth / 2),
                        0.95, 0.6, double.PositiveInfinity, end),
                    new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                        heightRiverbed, 3),
                    constraint);
            }

            if (shallowTileClass == null)
                return;

            foreach (double z in new[] { 0.25, 0.75 })
            {
                var start = new RmgenVector2D(0, RmgenLibrary.FractionToTiles(z, MapSize));
                start.RotateAround(riverAngle, mapCenter);
                var end = new RmgenVector2D(MapSize, RmgenLibrary.FractionToTiles(z, MapSize));
                end.RotateAround(riverAngle, mapCenter);

                RmgenCommon.CreatePassage(Rng, map, start, end,
                    RmgenLibrary.ScaleByMapSize(8, 12, MapSize),
                    RmgenLibrary.ScaleByMapSize(8, 12, MapSize),
                    2, tileClass: shallowTileClass,
                    constraints: new HeightConstraint(map, double.NegativeInfinity, heightShallow),
                    startHeight: heightShallow, endHeight: heightShallow);
            }
        }
    }
}
