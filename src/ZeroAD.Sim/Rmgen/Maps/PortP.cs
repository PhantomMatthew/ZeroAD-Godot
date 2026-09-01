using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>india.js（283 行）——印度季风：半干湖/河床、湿岸、鳄鱼与象群。
    /// 无 biome（上游不 LoadLibrary("rmbiome")）。环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class IndiaMap2 : StandardMap
    {
        private const string tGrass1 = "savanna_grass_a";
        private const string tDirt1 = "savanna_dirt_a";
        private const string tDirt4 = "savanna_dirt_b";
        private const string tShore = "savanna_riparian_bank";
        private const string tWater = "savanna_riparian_wet";

        private const string oTree = "gaia/tree/palm_tropic";
        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oRabbit = "gaia/fauna_rabbit";
        private const string oTiger = "gaia/fauna_tiger";
        private const string oCrocodile = "gaia/fauna_crocodile_nile";
        private const string oFish = "gaia/fish/generic";
        private const string oElephant = "gaia/fauna_elephant_asian";
        private const string oElephantInfant = "gaia/fauna_elephant_asian_infant";
        private const string oBoar = "gaia/fauna_boar";
        private const string oStoneSmall = "gaia/rock/savanna_small";
        private const string oMetalLarge = "gaia/ore/savanna_large";

        private const string aBush = "actor|props/flora/bush_medit_sm_dry.xml";
        private const string aRock = "actor|geology/stone_savanna_med.xml";

        protected override double HeightLand => 1;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tGrass1);
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightSeaGround = -3;
            const double heightShore = 3;
            const double heightOffsetBump = 2;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, null, RmgenLibrary.FractionToTiles(0.35, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize), rng.RandomAngle());

            RmgenCommon.PlacePlayerBases(rng, map, settings, tGrass1, ClPlayer, null,
                playerPosition, playerIDs: playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = oBerryBush,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneSmall, "stone_formation", tDirt1),
                    },
                    TreesTemplate = oTree,
                    TreesCount = (int)RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                    TreesMinDist = 13,
                    TreesMaxDist = 15,
                    TreesMinDistGroup = 4,
                    TreesMaxDistGroup = 6,
                });

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize),
                    0.5, 0.08, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 13),
                RmgenLibrary.ScaleByMapSize(300, 800, MapSize));

            RmgenLibrary.CreateArea(
                new ChainPlacer(rng, 2, Math.Floor(RmgenLibrary.ScaleByMapSize(2, 16, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(35, 200, MapSize)),
                    double.PositiveInfinity, mapCenter, 0,
                    new[] { (int)Math.Floor(RmgenLibrary.ScaleByMapSize(15, 40, MapSize)) }),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaGround, 4),
                    new TileClassPainter(clWater),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 2));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 2, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    3, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightShore, 4),
                    new TileClassUnPainter(clWater),
                },
                RmgenLibrary.BorderClasses(clWater, 4, 7),
                RmgenLibrary.ScaleByMapSize(12, 130, MapSize) * 2, 150);

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 2.4, 3.4,
                HeightPlacer.Mode.IncludeMinIncludeMax, tGrass1);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, 2.4,
                HeightPlacer.Mode.ExcludeMinExcludeMax, tShore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -8, 1,
                HeightPlacer.Mode.ExcludeMinIncludeMax, tWater);
            RmgenLibrary.PaintTileClassBasedOnHeight(-6, 0,
                HeightPlacer.Mode.IncludeMinExcludeMax, clWater);

            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(12, 30, MapSize); ++i)
            {
                var position = new RmgenVector2D(
                    rng.RandIntInclusive(1, MapSize - 1),
                    rng.RandIntInclusive(1, MapSize - 1));
                if (RmgenLibrary.AvoidClasses(ClPlayer, 30, ClRock, 25, clWater, 10).Allows(position))
                {
                    GaiaEntities.CreateStoneMineFormation(rng, map, position, oStoneSmall, tDirt4);
                    ClRock.Add(position);
                }
            }

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) },
                    true, ClMetal),
                0,
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClMetal, 10, ClRock, 8, clWater, 4),
                RmgenLibrary.ScaleByMapSize(2, 12, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, aRock, 1, 3, 0, 3) },
                    true),
                0,
                RmgenLibrary.AvoidClasses(ClPlayer, 7, clWater, 1),
                RmgenLibrary.ScaleByMapSize(200, 1200, MapSize), 1);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oBoar, 1, 2, 0, 4) },
                    true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClPlayer, 20, clFood, 11),
                RmgenLibrary.ScaleByMapSize(4, 12, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oTiger, 2, 2, 0, 4) },
                    true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClPlayer, 20, clFood, 11),
                RmgenLibrary.ScaleByMapSize(4, 12, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oCrocodile, 2, 4, 0, 4) },
                    true, clFood),
                0,
                RmgenLibrary.StayClasses(clWater, 1),
                RmgenLibrary.ScaleByMapSize(4, 12, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oElephant, 2, 4, 0, 4),
                    new ScatterObject(rng, oElephantInfant, 1, 2, 0, 4),
                }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClPlayer, 20, clFood, 11),
                RmgenLibrary.ScaleByMapSize(4, 12, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oRabbit, 5, 6, 0, 4) },
                    true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClPlayer, 20, clFood, 11),
                RmgenLibrary.ScaleByMapSize(4, 12, MapSize), 50);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, oFish, 2, 3, 0, 2) } },
                new double[] { 40 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 10),
                    RmgenLibrary.StayClasses(clWater, 2),
                }),
                clFood);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oBerryBush, 5, 7, 0, 4) },
                    true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClPlayer, 20, clFood, 12, ClRock, 4, ClMetal, 4),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oTree, 1, 7, 0, 3) },
                    true, ClForest),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClMetal, 4, ClRock, 4, clWater, 1),
                RmgenLibrary.ScaleByMapSize(70, 500, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aBush, 2, 4, 0, 1.8, -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClPlayer, 2, ClForest, 0),
                RmgenLibrary.ScaleByMapSize(100, 1200, MapSize));

            return map.MakeExportable();
        }
    }

    /// <summary>polar_sea.js（281 行）——极地海：中央冻结海面、小冰湖、冰山与狼触发点。
    /// sibling polar_sea_triggers.js 不在本批范围；生成脚本中的 trigger_point_A 已移植。
    /// 环境设置与 Nomad 重新布置/补宝因 placePlayersNomad 未移植按既有约定省略。</summary>
    public sealed class PolarSeaMap2 : StandardMap
    {
        private static readonly string[] tPrimary = { "alpine_snow_01" };
        private const string tSecondary = "alpine_snow_02";
        private const string tShore = "alpine_ice_01";
        private const string tWater = "alpine_ice_01";

        private const string oArcticFox = "gaia/fauna_fox_arctic";
        private const string oArcticWolf = "gaia/fauna_wolf_arctic_violent";
        private const string oMuskox = "gaia/fauna_muskox";
        private const string oWalrus = "gaia/fauna_walrus";
        private const string oWhaleFin = "gaia/fauna_whale_fin";
        private const string oWhaleHumpback = "gaia/fauna_whale_humpback";
        private const string oFish = "gaia/fish/generic";
        private const string oStoneLarge = "gaia/rock/polar_01";
        private const string oStoneSmall = "gaia/rock/alpine_small";
        private const string oMetalLarge = "gaia/ore/polar_01";
        private const string oWoodTreasure = "gaia/treasure/wood";
        private const string oMarket = "skirmish/structures/default_market";

        private const string aRockLarge = "actor|geology/stone_granite_med.xml";
        private const string aRockMedium = "actor|geology/stone_granite_med.xml";
        private const string aIceberg = "actor|props/special/eyecandy/iceberg.xml";

        private const double BuildingOrientation = -SafeMath.PI / 4;

        protected override double HeightLand => 2;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tPrimary);
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightSeaGround = -10;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clArcticWolf = new TileClass(MapSize);

            var (playerIDs, playerPosition, _, _) = RmgenCommon.PlayerPlacementCircle(
                rng, map, NumPlayers, RmgenLibrary.FractionToTiles(0.35, MapSize));

            if (!settings.Nomad)
                for (int i = 0; i < NumPlayers; ++i)
                {
                    var offset = new RmgenVector2D(12, 0);
                    offset.Rotate(rng.RandomAngle());
                    var marketPos = RmgenVector2D.Add(playerPosition[i], offset);
                    marketPos.Round();
                    map.PlaceEntityPassable(oMarket, playerIDs[i], marketPos, BuildingOrientation);
                    RmgenCommon.AddCivicCenterAreaToClass(map, marketPos, clBaseResource);
                }

            var treasures = new List<(string Template, int Count)>
            {
                (oWoodTreasure, settings.Nomad ? 16 : 14),
            };

            RmgenCommon.PlacePlayerBases(rng, map, settings, tPrimary[0], ClPlayer, null,
                playerPosition, tSecondary, tSecondary, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    StartingAnimal = true,
                    StartingAnimalTemplate = oMuskox,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneLarge, (string?)null, (object?)null),
                    },
                    Treasures = treasures,
                });

            RmgenLibrary.CreateArea(
                new ChainPlacer(rng, 2, Math.Floor(RmgenLibrary.ScaleByMapSize(5, 16, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(35, 200, MapSize)),
                    double.PositiveInfinity, mapCenter, 0,
                    new[] { (int)Math.Floor(RmgenLibrary.FractionToTiles(0.17, MapSize)) }),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tShore, tWater }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaGround, 4),
                    new TileClassPainter(clWater),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(2, 4, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(20, 140, MapSize)), 0.7),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tShore, tWater }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaGround, 5),
                    new TileClassPainter(clWater),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20),
                RmgenLibrary.ScaleByMapSize(10, 16, MapSize), 1);

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, 2, 2,
                        relative: true),
                },
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 20),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tSecondary, tSecondary, tSecondary },
                        new[] { 1, 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, 18, 2),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 35),
                RmgenLibrary.ScaleByMapSize(20, 240, MapSize));

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(10, 20, MapSize),
                RmgenLibrary.ScaleByMapSize(20, 30, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        patchSize, 0.5),
                    new IPainter[]
                    {
                        new TerrainPainter(tSecondary, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 3, ClDirt, 5, ClPlayer, 12),
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
                RmgenLibrary.AvoidClasses(clWater, 3, ClPlayer, 20, ClRock, 18, ClHill, 2),
                ClRock);

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) } },
                RmgenLibrary.AvoidClasses(clWater, 3, ClPlayer, 20, ClMetal, 18, ClRock, 5, ClHill, 2),
                ClMetal);

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                        new ScatterObject(rng, aRockMedium, 1, 3, 0, 2),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, settings.CircularMap),
                },
                RmgenLibrary.AvoidClasses(clWater, 0, ClPlayer, 0));

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, aIceberg, 1, 1, 1, 1) } },
                new[] { RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, settings.CircularMap) },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clWater, 4),
                    RmgenLibrary.AvoidClasses(ClHill, 2),
                }));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oArcticFox, 1, 2, 0, 3) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, settings.Nomad ? oArcticFox : oArcticWolf, 4, 6, 0, 4),
                    },
                    new IGroupElement[] { new ScatterObject(rng, oWalrus, 2, 3, 0, 2) },
                    new IGroupElement[] { new ScatterObject(rng, oMuskox, 2, 3, 0, 2) },
                },
                new double[] { 3 * NumPlayers, 5 * NumPlayers, 5 * NumPlayers, 12 * NumPlayers },
                RmgenLibrary.AvoidClasses(ClPlayer, 35, clFood, 16, clWater, 2, ClMetal, 4,
                    ClRock, 4, ClHill, 2),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oWhaleFin, 1, 2, 0, 2) },
                    new IGroupElement[] { new ScatterObject(rng, oWhaleHumpback, 1, 2, 0, 2) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(1, 6, MapSize) * 3,
                    RmgenLibrary.ScaleByMapSize(1, 6, MapSize) * 3,
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 20, ClHill, 5),
                    RmgenLibrary.StayClasses(clWater, 6),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, oFish, 2, 3, 0, 2) } },
                new double[] { 100 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 12, ClHill, 5),
                    RmgenLibrary.StayClasses(clWater, 6),
                }),
                clFood);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, "trigger/trigger_point_A", 1, 1, 0, 0) },
                    true, clArcticWolf),
                0,
                RmgenLibrary.AvoidClasses(clWater, 2, ClMetal, 4, ClRock, 4, ClPlayer, 15,
                    ClHill, 2, clArcticWolf, 20),
                1000, 100);

            return map.MakeExportable();
        }
    }

    /// <summary>lake.js（278 行）——大型中央湖：先用 clPlayer 保护出生点，再挖湖与锯齿岸线。
    /// 使用 rmbiome；环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class LakeMap2 : StandardMap
    {
        protected override double HeightLand => 3;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightSeaGround = -3;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);

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

            string pattern = settings.PlayerPlacement;
            double teamDist = pattern == "river" ? 0.55 : 0.35;
            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, pattern, RmgenLibrary.FractionToTiles(teamDist, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize), rng.RandomAngle());

            for (int i = 0; i < NumPlayers; ++i)
                RmgenCommon.AddCivicCenterAreaToClass(map, playerPosition[i], ClPlayer);

            RmgenLibrary.CreateArea(
                new ChainPlacer(rng, 2, Math.Floor(RmgenLibrary.ScaleByMapSize(5, 16, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(35, 200, MapSize)),
                    double.PositiveInfinity, mapCenter, 0,
                    new[] { (int)Math.Floor(RmgenLibrary.FractionToTiles(0.2, MapSize)) }),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaGround, 4),
                    new TileClassPainter(clWater),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 2, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    3, double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, biome.Hill }, new[] { 2 }, rng),
                    new TileClassUnPainter(clWater),
                },
                RmgenLibrary.BorderClasses(clWater, 4, 7),
                RmgenLibrary.ScaleByMapSize(12, 130, MapSize) * 2, 150);

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 2.4, 3.4,
                HeightPlacer.Mode.IncludeMinIncludeMax, biome.MainTerrain);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, 2.4,
                HeightPlacer.Mode.ExcludeMinExcludeMax, biome.Shore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -8, 1,
                HeightPlacer.Mode.ExcludeMinIncludeMax, biome.Water);
            RmgenLibrary.PaintTileClassBasedOnHeight(-6, 0,
                HeightPlacer.Mode.IncludeMinExcludeMax, clWater);

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0,
                new TileClass(MapSize), biome, playerPosition, biome.RoadWild, biome.Road, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = biome.FruitBush,
                    Mines = new()
                    {
                        (biome.MetalLarge, (string?)null, (object?)null),
                        (biome.StoneLarge, (string?)null, (object?)null),
                    },
                    TreesTemplate = biome.Tree1,
                    TreesCount = 5,
                    DecorativesTemplate = biome.GrassShort,
                });

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, 2, 2,
                        relative: true),
                },
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 20),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            if (rng.RandBool())
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { biome.MainTerrain, biome.Cliff, biome.Hill },
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
                    count: (int)Math.Ceiling(RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers));

            double forestTrees = biome.ForestProbability *
                RmgenLibrary.ScaleByMapSize(biome.TreesMin, biome.TreesMax, MapSize);
            double stragglerTrees = (1 - biome.ForestProbability) *
                RmgenLibrary.ScaleByMapSize(biome.TreesMin, biome.TreesMax, MapSize);
            GaiaEntities.CreateDefaultForests(rng, map,
                new object[] { biome.MainTerrain, biome.ForestFloor1, biome.ForestFloor2, pForest1, pForest2 },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 17, ClHill, 0, clWater, 2),
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
                    new IPainter[]
                    {
                        new TerrainPainter(biome.Tier4Terrain, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClHill, 0, ClDirt, 5,
                        ClPlayer, 12),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            GaiaEntities.CreateBalancedMetalMines(rng, map, NumPlayers,
                biome.MetalSmall, biome.MetalLarge, ClMetal,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer,
                    RmgenLibrary.ScaleByMapSize(20, 35, MapSize), ClHill, 1));

            GaiaEntities.CreateBalancedStoneMines(rng, map, NumPlayers,
                biome.StoneSmall, biome.StoneLarge, ClRock,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer,
                    RmgenLibrary.ScaleByMapSize(20, 35, MapSize), ClHill, 1, ClMetal, 10));

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
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 20),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) } },
                new double[] { 3 * NumPlayers },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, biome.Fish, 2, 3, 0, 2) } },
                new double[] { 65 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 12),
                    RmgenLibrary.StayClasses(clWater, 4),
                }),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                RmgenLibrary.AvoidClasses(clWater, 5, ClForest, 7, ClHill, 1, ClPlayer, 12,
                    ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }
    }

    /// <summary>anatolian_plateau.js（274 行）——安纳托利亚高原：steppe 基底、多层草斑和疏林。
    /// 无 biome（上游全部常量内联）。环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class AnatolianPlateauMap2 : StandardMap
    {
        private static readonly string[] tPrimary = { "steppe_grass_a", "steppe_grass_c", "steppe_grass_d" };
        private static readonly string[] tGrass =
            { "steppe_grass_a", "steppe_grass_b", "steppe_grass_c", "steppe_grass_d" };
        private const string tForestFloor = "steppe_grass_c";
        private const string tGrassA = "steppe_grass_b";
        private const string tGrassB = "steppe_grass_c";
        private static readonly string[] tGrassC = { "steppe_grass_b", "steppe_grass_c", "steppe_grass_d" };
        private const string tGrassD = "steppe_grass_a";
        private static readonly string[] tDirt = { "steppe_dirt_a", "steppe_dirt_b" };
        private const string tRoad = "road_stones";
        private const string tRoadWild = "road_stones";

        private const string oPoplar = "gaia/tree/poplar_lombardy";
        private const string oBush = "gaia/tree/bush_temperate";
        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oRabbit = "gaia/fauna_rabbit";
        private const string oAnimal = "gaia/fauna_sheep";
        private const string oSheep = "gaia/fauna_sheep";
        private const string oStoneLarge = "gaia/rock/mediterranean_large";
        private const string oStoneSmall = "gaia/rock/mediterranean_small";
        private const string oMetalLarge = "gaia/ore/mediterranean_large";

        private const string aGrass = "actor|props/flora/grass_soft_small_tall.xml";
        private const string aGrassShort = "actor|props/flora/grass_soft_large.xml";
        private const string aRockLarge = "actor|geology/stone_granite_med.xml";
        private const string aRockMedium = "actor|geology/stone_granite_med.xml";
        private const string aBushMedium = "actor|props/flora/bush_medit_me.xml";
        private const string aBushSmall = "actor|props/flora/bush_medit_sm.xml";

        protected override double HeightLand => 1;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tPrimary);
            var map = Map;

            const double heightOffsetBump = 2;

            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var pForest = new[]
            {
                tForestFloor + "|" + oPoplar,
                tForestFloor,
            };

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, null, RmgenLibrary.FractionToTiles(0.35, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize), rng.RandomAngle());

            RmgenCommon.PlacePlayerBases(rng, map, settings, tPrimary[0], ClPlayer, null,
                playerPosition, tRoadWild, tRoad, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    StartingAnimal = true,
                    StartingAnimalTemplate = oAnimal,
                    BerriesTemplate = oBerryBush,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneLarge, (string?)null, (object?)null),
                    },
                    TreesTemplate = oPoplar,
                    DecorativesTemplate = aGrassShort,
                });

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0.5),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 13),
                RmgenLibrary.ScaleByMapSize(300, 800, MapSize));

            double forestTrees = 0.65 * RmgenLibrary.ScaleByMapSize(220, 1000, MapSize);
            double stragglerTrees = (1 - 0.65) * RmgenLibrary.ScaleByMapSize(220, 1000, MapSize);
            var forestTypes = new object[][]
            {
                new object[]
                {
                    new object[] { tForestFloor, tGrass, pForest },
                    new object[] { tForestFloor, pForest },
                },
            };
            double size = forestTrees / (RmgenLibrary.ScaleByMapSize(2, 8, MapSize) * NumPlayers);
            double num = 4 * Math.Floor(size / forestTypes.Length);
            foreach (var type in forestTypes)
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(2, 3, MapSize)),
                        4, double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 13, ClForest, 20, ClHill, 1),
                    num);

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(5, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(6, 84, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 128, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        patchSize, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new object[] { tGrass, tGrassA, tGrassC },
                            new object[] { tGrass, tGrassA, tGrassC },
                            new object[] { tGrass, tGrassA, tGrassC },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 2, ClPlayer, 10),
                    RmgenLibrary.ScaleByMapSize(50, 70, MapSize));

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(5, 32, MapSize),
                RmgenLibrary.ScaleByMapSize(6, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(7, 80, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        patchSize, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tGrassD, tDirt }, new[] { 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 2, ClPlayer, 10),
                    RmgenLibrary.ScaleByMapSize(50, 90, MapSize));

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(10, 60, MapSize),
                RmgenLibrary.ScaleByMapSize(15, 90, MapSize),
                RmgenLibrary.ScaleByMapSize(20, 120, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        patchSize, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tGrassB, tGrassA }, new[] { 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClHill, 0, ClPlayer, 8),
                    RmgenLibrary.ScaleByMapSize(30, 90, MapSize));

            var group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3) }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) }, true, ClMetal);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClMetal, 10, ClRock, 5, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(2, 8, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                new ScatterObject(rng, aRockMedium, 1, 3, 0, 2),
            }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, oRabbit, 5, 7, 0, 4) }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, ClHill, 1, clFood, 20),
                6 * NumPlayers, 50);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, oBerryBush, 5, 7, 0, 4) }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 20),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, oSheep, 2, 3, 0, 2) }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, ClHill, 1, clFood, 20),
                3 * NumPlayers, 50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oBush, oPoplar },
                RmgenLibrary.AvoidClasses(ClForest, 1, ClHill, 1, ClPlayer, 13, ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aGrass, 2, 4, 0, 1.8, -SafeMath.PI / 8, SafeMath.PI / 8),
                new ScatterObject(rng, aGrassShort, 3, 6, 1.2, 2.5, -SafeMath.PI / 8, SafeMath.PI / 8),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClHill, 2, ClPlayer, 10, ClDirt, 1, ClForest, 0),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aBushMedium, 1, 2, 0, 2),
                new ScatterObject(rng, aBushSmall, 2, 4, 0, 2),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClHill, 1, ClPlayer, 1, ClDirt, 1),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            return map.MakeExportable();
        }
    }
}
