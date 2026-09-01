using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>corsica.js（逐字移植）——科西嘉/撒丁双岛与中央海峡，海湾、沙滩、双层台地和通道按高度成形。
    /// 环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class CorsicaMap2 : StandardMap
    {
        private static readonly string[] tGrass = { "medit_grass_field", "medit_grass_field_b", "temp_grass_c" };
        private static readonly string[] tLushGrass = { "medit_grass_field", "medit_grass_field_a" };
        private static readonly string[] tSteepCliffs = { "temp_cliff_b", "temp_cliff_a" };
        private static readonly string[] tCliffs = { "temp_cliff_b", "medit_cliff_italia", "medit_cliff_italia_grass" };
        private static readonly string[] tHill =
        {
            "medit_cliff_italia_grass",
            "medit_cliff_italia_grass",
            "medit_grass_field",
            "medit_grass_field",
            "temp_grass",
        };
        private static readonly string[] tMountain = { "medit_cliff_italia_grass", "medit_cliff_italia" };
        private static readonly string[] tRoad = { "medit_city_tile", "medit_rocks_grass", "medit_grass_field_b" };
        private static readonly string[] tRoadWild = { "medit_rocks_grass", "medit_grass_field_b" };
        private static readonly string[] tShoreBlend = { "medit_sand_wet", "medit_rocks_wet" };
        private static readonly string[] tShore = { "medit_rocks", "medit_sand", "medit_sand" };
        private static readonly string[] tSandTransition = { "medit_sand", "medit_rocks_grass", "medit_rocks_grass", "medit_rocks_grass" };
        private static readonly string[] tVeryDeepWater = { "medit_sea_depths", "medit_sea_coral_deep" };
        private static readonly string[] tDeepWater = { "medit_sea_coral_deep", "tropic_ocean_coral" };
        private const string tCreekWater = "medit_sea_coral_plants";

        private const string ePine = "gaia/tree/aleppo_pine";
        private const string ePalmTall = "gaia/tree/cretan_date_palm_tall";
        private const string eFanPalm = "gaia/tree/medit_fan_palm";
        private const string eCypress = "gaia/tree/cypress";
        private const string eBush = "gaia/fruit/berry_01";
        private const string eFish = "gaia/fish/generic";
        private const string ePig = "gaia/fauna_pig";
        private const string eStoneMine = "gaia/rock/mediterranean_large";
        private const string eMetalMine = "gaia/ore/mediterranean_large";

        private const string aRock = "actor|geology/stone_granite_med.xml";
        private const string aLargeRock = "actor|geology/stone_granite_large.xml";
        private const string aBushA = "actor|props/flora/bush_medit_sm_lush.xml";
        private const string aBushB = "actor|props/flora/bush_medit_me_lush.xml";
        private const string aPlantA = "actor|props/flora/plant_medit_artichoke.xml";
        private const string aPlantB = "actor|props/flora/grass_tufts_a.xml";
        private const string aPlantC = "actor|props/flora/grass_soft_tuft_a.xml";
        private const string aStandingStone = "actor|props/special/eyecandy/standing_stones.xml";

        protected override double HeightLand => -8;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tVeryDeepWater);
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightCreeks = -5;
            const double heightBeaches = -1;
            const double heightMain = 5;
            const double heightOffsetMainRelief = 30;
            const double heightOffsetLevel1 = 9;
            const double heightOffsetLevel2 = 8;
            const double heightOffsetBumps = 2;
            const double heightOffsetAntiBumps = -5;

            var clIsland = new TileClass(MapSize);
            var clCreek = new TileClass(MapSize);
            var clWater = new TileClass(MapSize);
            var clCliffs = new TileClass(MapSize);
            var clFish = new TileClass(MapSize);
            var clForest = new TileClass(MapSize);
            var clShore = new TileClass(MapSize);
            var clPlayer = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clPassage = new TileClass(MapSize);
            var clSettlement = new TileClass(MapSize);

            double radiusBeach = RmgenLibrary.FractionToTiles(0.65, MapSize);
            double radiusCreeks = RmgenLibrary.FractionToTiles(0.60, MapSize);
            double radiusIsland = RmgenLibrary.FractionToTiles(0.46, MapSize);
            double radiusLevel1 = RmgenLibrary.FractionToTiles(0.40, MapSize);
            double radiusPlayer = RmgenLibrary.FractionToTiles(0.29, MapSize);
            double radiusLevel2 = RmgenLibrary.FractionToTiles(0.20, MapSize);

            double CreeksArea() => rng.RandBool()
                ? rng.RandFloat(10, 50)
                : RmgenLibrary.ScaleByMapSize(75, 100, MapSize) + rng.RandFloat(0, 20);

            double nbCreeks = RmgenLibrary.ScaleByMapSize(7, 9, MapSize);
            const int nbSubIsland = 5;
            double nbBeaches = RmgenLibrary.ScaleByMapSize(6, 10, MapSize);
            double nbPassagesLevel1 = RmgenLibrary.ScaleByMapSize(6, 8, MapSize);
            double nbPassagesLevel2 = RmgenLibrary.ScaleByMapSize(2, 4, MapSize);

            double swapAngle = rng.RandBool() ? SafeMath.PI / 2 : 0;
            var islandLocations = new List<RmgenVector2D>();
            foreach (var source in new[] { new RmgenVector2D(0.05, 0.05), new RmgenVector2D(0.95, 0.95) })
            {
                var location = source;
                location.Mult(MapSize);
                location.RotateAround(-swapAngle, mapCenter);
                islandLocations.Add(location);
            }

            for (int island = 0; island < 2; ++island)
            {
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, RmgenGeometry.DiskArea(radiusIsland), 1, 0.5,
                        double.PositiveInfinity, islandLocations[island]),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tCliffs, tGrass }, new[] { 2 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightMain, 0),
                        new TileClassPainter(clIsland),
                    },
                    null);

                for (int i = 0; i < nbSubIsland + 1; ++i)
                {
                    double angle = SafeMath.PI * (island + i / (nbSubIsland * 2.0)) + swapAngle;
                    var offset = new RmgenVector2D(radiusIsland, 0);
                    offset.Rotate(-angle);
                    var location = RmgenVector2D.Add(islandLocations[island], offset);
                    RmgenLibrary.CreateArea(
                        new ClumpPlacer(rng, RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.09, MapSize)),
                            0.6, 0.03, double.PositiveInfinity, location),
                        new IPainter[]
                        {
                            new LayeredPainter(new object[] { tCliffs, tGrass }, new[] { 2 }, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightMain, 1),
                            new TileClassPainter(clIsland),
                        },
                        null);
                }

                for (int i = 0; i < nbCreeks + 1; ++i)
                {
                    double angle = SafeMath.PI * (island + i * (1.0 / (nbCreeks * 2))) + swapAngle;
                    var offset = new RmgenVector2D(radiusCreeks, 0);
                    offset.Rotate(-angle);
                    var location = RmgenVector2D.Add(islandLocations[island], offset);
                    RmgenLibrary.CreateArea(
                        new ClumpPlacer(rng, CreeksArea(), 0.4, 0.01,
                            double.PositiveInfinity, location),
                        new IPainter[]
                        {
                            new TerrainPainter(tSteepCliffs, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightCreeks, 0),
                            new TileClassPainter(clCreek),
                        },
                        null);
                }

                for (int i = 0; i < nbBeaches + 1; ++i)
                {
                    double angle = SafeMath.PI * (island +
                        i / (nbBeaches * 2.5) + 1 / (nbBeaches * 6) +
                        rng.RandFloat(-1, 1) / (nbBeaches * 7)) + swapAngle;
                    var startOffset = new RmgenVector2D(radiusIsland, 0);
                    startOffset.Rotate(-angle);
                    var start = RmgenVector2D.Add(islandLocations[island], startOffset);
                    var endOffset = new RmgenVector2D(radiusBeach, 0);
                    endOffset.Rotate(-angle);
                    var end = RmgenVector2D.Add(islandLocations[island], endOffset);

                    RmgenLibrary.CreateArea(
                        new ClumpPlacer(rng, 130, 0.7, 0.8, double.PositiveInfinity,
                            RmgenVector2D.Div(RmgenVector2D.Add(start, RmgenVector2D.Mult(end, 3)), 4)),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightBeaches, 5),
                        null);

                    RmgenCommon.CreatePassage(rng, map, start, end, 18, 25, 4, tileClass: clShore);
                }

                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, RmgenGeometry.DiskArea(radiusIsland), 1, 0.2,
                        double.PositiveInfinity, islandLocations[island]),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetMainRelief, RmgenLibrary.FractionToTiles(0.45, MapSize), relative: true),
                    null);

                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, RmgenGeometry.DiskArea(radiusLevel1), 0.95, 0.02,
                        double.PositiveInfinity, islandLocations[island]),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetLevel1, 1, relative: true),
                    null);

                for (int i = 0; i <= nbPassagesLevel1; ++i)
                {
                    double angle = SafeMath.PI * (i / 7.0 + 1 / 9.0 + island) + swapAngle;
                    var startOffset = new RmgenVector2D(radiusLevel1 + 10, 0);
                    startOffset.Rotate(-angle);
                    var endOffset = new RmgenVector2D(radiusLevel1 - 4, 0);
                    endOffset.Rotate(-angle);
                    RmgenCommon.CreatePassage(rng, map,
                        RmgenVector2D.Add(islandLocations[island], startOffset),
                        RmgenVector2D.Add(islandLocations[island], endOffset),
                        20, 20, 2, tileClass: clPassage);
                }

                if (MapSize > 150)
                {
                    RmgenLibrary.CreateArea(
                        new ClumpPlacer(rng, RmgenGeometry.DiskArea(radiusLevel2), 0.98, 0.04,
                            double.PositiveInfinity, islandLocations[island]),
                        new IPainter[]
                        {
                            new LayeredPainter(new object[] { tCliffs, tGrass }, new[] { 2 }, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                                heightOffsetLevel2, 1, relative: true),
                        },
                        null);

                    for (int i = 0; i < nbPassagesLevel2; ++i)
                    {
                        double angle = SafeMath.PI *
                            (i / (2 * nbPassagesLevel2) + 1 / (4 * nbPassagesLevel2) + island) +
                            swapAngle;
                        var startOffset = new RmgenVector2D(radiusLevel2 + 3, 0);
                        startOffset.Rotate(-angle);
                        var endOffset = new RmgenVector2D(radiusLevel2 - 6, 0);
                        endOffset.Rotate(-angle);
                        RmgenCommon.CreatePassage(rng, map,
                            RmgenVector2D.Add(islandLocations[island], startOffset),
                            RmgenVector2D.Add(islandLocations[island], endOffset),
                            20, 20, 2, tileClass: clPassage);
                    }
                }
            }

            RmgenCommon.SortAllPlayers(rng, settings);
            var playerPosition = new List<RmgenVector2D>();
            for (int island = 0; island < 2; ++island)
            {
                int playersPerIsland = island == 0 ? (int)Math.Ceiling(NumPlayers / 2.0) : (int)Math.Floor(NumPlayers / 2.0);
                for (int i = 0; i < playersPerIsland; ++i)
                {
                    double playerAngle = SafeMath.PI * ((i + 0.5) / (2 * playersPerIsland) + island) + swapAngle;
                    var offset = new RmgenVector2D(radiusPlayer, 0);
                    offset.Rotate(-playerAngle);
                    var position = RmgenVector2D.Add(islandLocations[island], offset);
                    playerPosition.Add(position);
                    RmgenCommon.AddCivicCenterAreaToClass(map, position, clPlayer);
                }
            }

            RmgenCommon.PlacePlayerBases(rng, map, settings, tGrass[0], clSettlement, null,
                playerPosition, tRoadWild, tRoad, RmgenCommon.SortAllPlayers(rng, settings),
                cityPatchRadius: 6, cityPatchCoherence: 0.8,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    ExtraBaseResourceConstraint = RmgenLibrary.AvoidClasses(clPlayer, 2),
                    StartingAnimal = true,
                    BerriesTemplate = eBush,
                    Mines = new()
                    {
                        (eMetalMine, (string?)null, (object?)null),
                        (eStoneMine, (string?)null, (object?)null),
                    },
                });

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, 70, 0.6, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBumps, 3, relative: true),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clIsland, 2),
                    RmgenLibrary.AvoidClasses(clPlayer, 6, clPassage, 2),
                }),
                RmgenLibrary.ScaleByMapSize(20, 100, MapSize), 5);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, 120, 0.3, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetAntiBumps, 6, relative: true),
                },
                RmgenLibrary.AvoidClasses(clPlayer, 6, clPassage, 2, clIsland, 2),
                RmgenLibrary.ScaleByMapSize(20, 100, MapSize), 5);

            RmgenLibrary.PaintTileClassBasedOnHeight(double.NegativeInfinity, 0,
                HeightPlacer.Mode.ExcludeMinExcludeMax, clWater);

            for (int mapX = 0; mapX < MapSize; ++mapX)
                for (int mapZ = 0; mapZ < MapSize; ++mapZ)
                {
                    var position = new RmgenVector2D(mapX, mapZ);
                    object? terrain = GetCorsicaSardiniaTerrain(map, position, clWater, clShore,
                        clPassage, clSettlement);
                    if (terrain == null)
                        continue;

                    TerrainFactory.CreateTerrain(terrain).Place(map, rng, position);

                    if (ReferenceEquals(terrain, tCliffs) || ReferenceEquals(terrain, tSteepCliffs))
                        clCliffs.Add(position);
                }

            foreach (string mine in new[] { eMetalMine, eStoneMine })
                RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, mine, 1, 1, 0, 0),
                        new ScatterObject(rng, aBushB, 1, 1, 2, 2),
                        new ScatterObject(rng, aBushA, 0, 2, 1, 3),
                    }, true, clBaseResource),
                    0,
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.StayClasses(clIsland, 1),
                        RmgenLibrary.AvoidClasses(clWater, 3, clPlayer, 6, clBaseResource, 4,
                            clPassage, 2, clCliffs, 1),
                    }),
                    RmgenLibrary.ScaleByMapSize(6, 25, MapSize), 1000);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, 20, 0.3, 0.06, 0.5),
                new IPainter[]
                {
                    new TerrainPainter(tLushGrass, rng),
                    new TileClassPainter(clForest),
                },
                RmgenLibrary.AvoidClasses(clWater, 1, clPlayer, 6, clBaseResource, 3, clCliffs, 1),
                RmgenLibrary.ScaleByMapSize(10, 40, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, ePine, 3, 6, 1, 3),
                    new ScatterObject(rng, ePalmTall, 1, 3, 1, 3),
                    new ScatterObject(rng, eFanPalm, 0, 2, 0, 2),
                    new ScatterObject(rng, eCypress, 0, 1, 1, 2),
                }, true, clForest),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clIsland, 3),
                    RmgenLibrary.AvoidClasses(clWater, 1, clForest, 0, clPlayer, 3,
                        clBaseResource, 4, clPassage, 2, clCliffs, 2),
                }),
                RmgenLibrary.ScaleByMapSize(350, 2500, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aRock, 1, 3, 0, 1),
                    new ScatterObject(rng, aStandingStone, 0, 2, 0, 3),
                }, true),
                0,
                RmgenLibrary.AvoidClasses(clWater, 0, clForest, 0, clPlayer, 6,
                    clBaseResource, 4, clPassage, 2),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            var rocksGroup = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aLargeRock, 1, 2, 0, 1),
                new ScatterObject(rng, aRock, 1, 3, 0, 2),
            }, true);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng, rocksGroup, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, clForest, 0, clPlayer, 6,
                    clBaseResource, 4, clPassage, 2),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng, rocksGroup, 0,
                RmgenLibrary.BorderClasses(clWater, 5, 10),
                RmgenLibrary.ScaleByMapSize(100, 800, MapSize), 500);

            var plantGroups = new[]
            {
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aPlantA, 3, 7, 0, 3),
                    new ScatterObject(rng, aPlantB, 3, 6, 0, 3),
                    new ScatterObject(rng, aPlantC, 1, 4, 0, 4),
                }, true),
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aPlantB, 5, 20, 0, 5),
                    new ScatterObject(rng, aPlantC, 4, 10, 0, 4),
                }, true),
            };
            foreach (var group in plantGroups)
                RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                    RmgenLibrary.AvoidClasses(clWater, 0, clBaseResource, 4, clShore, 3),
                    RmgenLibrary.ScaleByMapSize(100, 600, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, ePig, 2, 4, 0, 3) }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, clBaseResource, 4, clPlayer, 6),
                RmgenLibrary.ScaleByMapSize(20, 100, MapSize), 50);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, eFish, 2, 3, 0, 2) },
                },
                new double[] { 50 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clCreek, 2, clShore, 3, clFish, 8),
                    RmgenLibrary.StayClasses(clWater, 3),
                }),
                clFish);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, eFish, 2, 3, 0, 2) },
                },
                new double[] { 70 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFish, 6),
                    RmgenLibrary.StayClasses(clWater, 3, clShore, 0),
                }),
                clFish);

            return map.MakeExportable();
        }

        private static object? GetCorsicaSardiniaTerrain(RandomMap map, RmgenVector2D position,
            TileClass clWater, TileClass clShore, TileClass clPassage, TileClass clSettlement)
        {
            bool isWater = clWater.CountMembersInRadius(position, 3) != 0;
            bool isShore = clShore.CountMembersInRadius(position, 2) != 0;
            bool isPassage = clPassage.CountMembersInRadius(position, 2) != 0;
            bool isSettlement = clSettlement.CountMembersInRadius(position, 2) != 0;

            if (isSettlement)
                return null;

            double height = map.GetHeight(position);
            double slope = map.GetSlope(position);

            if (height >= 0.5 && height < 1.5 && isShore)
                return tSandTransition;

            if (height >= 1 && !isWater)
            {
                if (isPassage)
                    return tGrass;

                if (slope >= 1.25)
                    return height > 25 ? tSteepCliffs : tCliffs;

                if (height < 17)
                    return tGrass;

                if (slope < 0.625)
                    return tHill;

                return tMountain;
            }

            if (slope >= 1.125)
                return tCliffs;

            if (height >= 1.5)
                return null;

            if (height >= -0.75)
                return tShore;

            if (height >= -3)
                return tShoreBlend;

            if (height >= -6)
                return tCreekWater;

            if (height > -10 && slope < 0.75)
                return tDeepWater;

            return null;
        }
    }

    /// <summary>river_archipelago.js（逐字移植）——平行湿地条带拼出河网群岛，玩家沿交错双线落位。
    /// Walls="towers"、环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class RiverArchipelagoMap2 : StandardMap
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
        private const string oPalm1 = "gaia/tree/palm_tropic";
        private const string oPalm2 = "gaia/tree/palm_tropical";
        private const string oStoneLarge = "gaia/rock/tropical_large";
        private const string oStoneSmall = "gaia/rock/tropical_small";
        private const string oMetalLarge = "gaia/ore/tropical_large";
        private const string oFish = "gaia/fish/generic";
        private const string oDeer = "gaia/fauna_deer";
        private const string oTiger = "gaia/fauna_tiger";
        private const string oBoar = "gaia/fauna_boar";
        private const string oPeacock = "gaia/fauna_peacock";
        private const string oBush = "gaia/fruit/berry_01";
        private const string oSpearman = "units/maur/infantry_spearman_b";
        private const string oArcher = "units/maur/infantry_archer_b";
        private const string oArcherElephant = "units/maur/elephant_archer_b";

        private const string aRockLarge = "actor|geology/stone_granite_large.xml";
        private const string aRockMedium = "actor|geology/stone_granite_med.xml";
        private const string aBush1 = "actor|props/flora/plant_tropic_a.xml";
        private const string aBush2 = "actor|props/flora/plant_lg.xml";
        private const string aBush3 = "actor|props/flora/plant_tropic_large.xml";

        protected override double HeightLand => -8;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tGrass);
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightWaterLevel = 0;
            const double heightShore = 1;
            const double heightShoreBlend = 2.8;
            const double heightLand = 3;
            const double heightHill = 25;

            var clPlayer = new TileClass(MapSize);
            var clPlayerTerritory = new TileClass(MapSize);
            var clHill = new TileClass(MapSize);
            var clForest = new TileClass(MapSize);
            var clWater = new TileClass(MapSize);
            var clDirt = new TileClass(MapSize);
            var clRock = new TileClass(MapSize);
            var clMetal = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clGaia = new TileClass(MapSize);

            var pForestD = new object[] { tForestFloor + "|" + oTree, tForestFloor };
            var pForestP1 = new object[] { tForestFloor + "|" + oPalm1, tForestFloor };
            var pForestP2 = new object[] { tForestFloor + "|" + oPalm2, tForestFloor };

            double startAngle = rng.RandomAngle();
            bool connectPlayers = rng.RandBool();
            double[][] stripWidthsLeft = connectPlayers
                ? new[]
                {
                    new[] { 0.03, 0.09 },
                    new[] { 0.14, 0.25 },
                    new[] { 0.36, 0.46 },
                }
                : new[]
                {
                    new[] { 0.0, 0.06 },
                    new[] { 0.12, 0.23 },
                    new[] { 0.33, 0.43 },
                };
            var stripWidthsRight = new List<double[]>();
            for (int i = stripWidthsLeft.Length - 1; i >= 0; --i)
                stripWidthsRight.Add(new[] { 1 - stripWidthsLeft[i][1], 1 - stripWidthsLeft[i][0] });

            var stripWidths = stripWidthsLeft.Concat(stripWidthsRight).ToArray();
            var clStrip = new TileClass[stripWidths.Length];

            for (int i = 0; i < stripWidths.Length; ++i)
            {
                clStrip[i] = new TileClass(MapSize);
                bool isPlayerStrip = i == 2 || i == 3;
                for (int j = 0; j < RmgenLibrary.ScaleByMapSize(20, 100, MapSize); ++j)
                {
                    var position = new RmgenVector2D(
                        rng.RandFloat(0, MapSize),
                        RmgenLibrary.FractionToTiles(rng.RandFloat(stripWidths[i][0], stripWidths[i][1]), MapSize));
                    position.RotateAround(startAngle, mapCenter);
                    position.Round();

                    RmgenLibrary.CreateArea(
                        new ChainPlacer(rng, 1,
                            Math.Floor(RmgenLibrary.ScaleByMapSize(3, connectPlayers && isPlayerStrip ? 8 : 7, MapSize)),
                            Math.Floor(RmgenLibrary.ScaleByMapSize(30, 60, MapSize)),
                            double.PositiveInfinity, position),
                        new IPainter[]
                        {
                            new TerrainPainter(tGrass, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightLand, 3),
                            new TileClassPainter(clStrip[i]),
                        },
                        null);
                }
            }

            var playerPosition = PlayerPlacementLine(map, NumPlayers, startAngle, mapCenter,
                RmgenLibrary.FractionToTiles(1 - stripWidthsLeft[2][0] - stripWidthsLeft[2][1], MapSize));
            var playerIDs = rng.RandBool()
                ? RmgenCommon.SortAllPlayers(rng, settings)
                : PrimeSortAllPlayers(rng, settings);

            double playerRadius = RmgenLibrary.ScaleByMapSize(15, 20, MapSize);
            for (int i = 0; i < NumPlayers; ++i)
                RmgenLibrary.CreateArea(
                    new ChainPlacer(rng, 1, 6, 40, 1, playerPosition[i], 0,
                        new[] { (int)Math.Floor(playerRadius) }),
                    new IPainter[]
                    {
                        new TerrainPainter(tGrass, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightLand, 4),
                        new TileClassPainter(clPlayerTerritory),
                    },
                    null);

            RmgenCommon.PlacePlayerBases(rng, map, settings, tGrass[0], clPlayer, null,
                playerPosition, tRoadWild, tRoad, playerIDs,
                cityPatchRadius: playerRadius / 3,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    ExtraBaseResourceConstraint = RmgenLibrary.StayClasses(clPlayerTerritory, 4),
                    StartingAnimal = true,
                    StartingAnimalTemplate = oPeacock,
                    BerriesTemplate = oBush,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneLarge, (string?)null, (object?)null),
                    },
                    TreesTemplate = oTree,
                    TreesCount = 40,
                });

            var areaWater = RmgenLibrary.CreateArea(
                new HeightPlacer(map, HeightPlacer.Mode.IncludeMinExcludeMax,
                    double.NegativeInfinity, heightWaterLevel),
                new IPainter[]
                {
                    new TerrainPainter(tWater, rng),
                    new TileClassPainter(clWater),
                },
                null);

            RmgenLibrary.CreateArea(
                new HeightPlacer(map, HeightPlacer.Mode.IncludeMinExcludeMax, heightWaterLevel, heightShore),
                new TerrainPainter(tShore, rng),
                null);

            RmgenLibrary.CreateArea(
                new HeightPlacer(map, HeightPlacer.Mode.IncludeMinExcludeMax, heightShore, heightShoreBlend),
                new TerrainPainter(tShoreBlend, rng),
                null);

            if (!settings.Nomad)
                for (int i = 0; i < 2; ++i)
                    for (int j = 0; j < RmgenLibrary.ScaleByMapSize(1, 8, MapSize); ++j)
                        RmgenLibrary.CreateObjectGroups(rng,
                            new ObjectGroup(new IGroupElement[]
                            {
                                new ScatterObject(rng, oSpearman, 8, 12, 2, 3),
                                new ScatterObject(rng, oArcher, 8, 12, 2, 3),
                                new ScatterObject(rng, oArcherElephant, 2, 3, 4, 5),
                            }, true, clGaia),
                            0,
                            new AndConstraint(new IConstraint[]
                            {
                                RmgenLibrary.AvoidClasses(clWater, 2, clForest, 1,
                                    clPlayerTerritory, 0, clHill, 1, clGaia, 15),
                                RmgenLibrary.StayClasses(clStrip[i == 0 ? 0 : stripWidths.Length - 1], 1),
                            }),
                            RmgenLibrary.ScaleByMapSize(5, 10, MapSize), 50);

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.1),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tCliff, tGrass }, new[] { 3 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightHill, 3),
                    new TileClassPainter(clHill),
                },
                RmgenLibrary.AvoidClasses(clPlayerTerritory, 0, clHill, 5, clGaia, 1, clWater, 2),
                RmgenLibrary.ScaleByMapSize(1, 5, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 2, 8, 4, 1),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, 4, 2,
                        relative: true),
                },
                RmgenLibrary.AvoidClasses(clPlayer, 8, clWater, 2),
                RmgenLibrary.ScaleByMapSize(20, 150, MapSize));

            double forestTrees = 0.7 * RmgenLibrary.ScaleByMapSize(1000, 4000, MapSize);
            double stragglerTrees = (1 - 0.7) * RmgenLibrary.ScaleByMapSize(1000, 4000, MapSize);
            var forestTypes = new[]
            {
                new object[] { new object[] { tGrass, tGrass, tGrass, tGrass, pForestD }, new object[] { tGrass, tGrass, tGrass, pForestD } },
                new object[] { new object[] { tGrass, tGrass, tGrass, tGrass, pForestP1 }, new object[] { tGrass, tGrass, tGrass, pForestP1 } },
                new object[] { new object[] { tGrass, tGrass, tGrass, tGrass, pForestP2 }, new object[] { tGrass, tGrass, tGrass, pForestP2 } },
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
                        new TileClassPainter(clForest),
                    },
                    RmgenLibrary.AvoidClasses(clPlayer, 12, clForest, 6, clHill, 0,
                        clGaia, 1, clWater, 2),
                    forestNum);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oTree, oPalm1, oPalm2 },
                RmgenLibrary.AvoidClasses(clWater, 5, clForest, 1, clHill, 1,
                    clPlayer, 8, clBaseResource, 4, clGaia, 1, clMetal, 4, clRock, 4),
                clForest,
                stragglerTrees);

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
                        new LayeredPainter(new object[] { tGrassC, tGrassA, tGrassB }, new[] { 2, 1 }, rng),
                        new TileClassPainter(clDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 8, clForest, 0, clHill, 0,
                        clGaia, 1, clPlayerTerritory, 0, clDirt, 16),
                    RmgenLibrary.ScaleByMapSize(20, 80, MapSize));

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
                        new TerrainPainter(tPlants, rng),
                        new TileClassPainter(clDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 8, clForest, 0, clHill, 0,
                        clGaia, 1, clPlayerTerritory, 0, clDirt, 16),
                    RmgenLibrary.ScaleByMapSize(20, 80, MapSize));

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4),
                    new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4),
                }, true, clRock),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, clForest, 1, clPlayerTerritory, 0,
                    clGaia, 1, clRock, 10, clHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3) }, true, clRock),
                0,
                RmgenLibrary.AvoidClasses(clWater, 4, clForest, 1, clPlayerTerritory, 0,
                    clGaia, 1, clRock, 10, clHill, 1),
                RmgenLibrary.ScaleByMapSize(2, 8, MapSize), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) }, true, clMetal),
                0,
                RmgenLibrary.AvoidClasses(clWater, 4, clForest, 1, clPlayerTerritory, 0,
                    clGaia, 1, clMetal, 10, clRock, 5, clHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) }, true),
                0,
                RmgenLibrary.AvoidClasses(clWater, 2, clForest, 1, clGaia, 1,
                    clPlayer, 8, clBaseResource, 4, clHill, 0),
                RmgenLibrary.ScaleByMapSize(50, 800, MapSize), 20);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                    new ScatterObject(rng, aRockMedium, 1, 3, 0, 2),
                }, true),
                0,
                RmgenLibrary.AvoidClasses(clWater, 2, clForest, 1, clGaia, 1,
                    clPlayer, 8, clBaseResource, 4, clHill, 0),
                RmgenLibrary.ScaleByMapSize(25, 400, MapSize), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, aBush1, 1, 2, 0, 1) }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 4, clHill, 2, clPlayer, 8,
                    clGaia, 1, clBaseResource, 4, clDirt, 0),
                RmgenLibrary.ScaleByMapSize(100, 500, MapSize));

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aBush2, 2, 4, 0, 1.8, -SafeMath.PI / 8, SafeMath.PI / 8),
                    new ScatterObject(rng, aBush1, 3, 6, 1.2, 2.5, -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 4, clHill, 2, clGaia, 1,
                    clPlayer, 8, clBaseResource, 4, clDirt, 1, clForest, 0),
                RmgenLibrary.ScaleByMapSize(100, 500, MapSize));

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aBush3, 1, 2, 0, 2),
                    new ScatterObject(rng, aBush2, 2, 4, 0, 2),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 4, clHill, 1, clPlayerTerritory, 0,
                    clGaia, 1, clDirt, 1),
                RmgenLibrary.ScaleByMapSize(100, 500, MapSize));

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oDeer, 5, 7, 0, 4) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 4, clForest, 0, clPlayerTerritory, 0,
                    clGaia, 1, clHill, 1, clFood, 20),
                3 * NumPlayers, 20);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oBoar, 2, 4, 0, 4) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 4, clForest, 0, clPlayerTerritory, 0,
                    clGaia, 1, clHill, 1, clRock, 4, clMetal, 4, clFood, 20),
                2 * NumPlayers, 20);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oTiger, 1, 1, 0, 4) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 4, clForest, 0, clPlayerTerritory, 0,
                    clGaia, 1, clHill, 1, clRock, 4, clMetal, 4, clFood, 20),
                2 * NumPlayers, 20);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oBush, 5, 7, 0, 4) }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 4, clForest, 0, clPlayerTerritory, 0,
                    clGaia, 1, clHill, 1, clRock, 4, clMetal, 4, clFood, 10),
                rng.RandIntInclusive(1, 4) * NumPlayers, 20);

            var waterAreas = areaWater != null ? new[] { areaWater } : Array.Empty<Area>();
            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                    { new ScatterObject(rng, oFish, 2, 3, 0, 2) }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 15),
                    RmgenLibrary.StayClasses(clWater, 4),
                }),
                RmgenLibrary.ScaleByMapSize(20, 100, MapSize), 20, waterAreas);

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
                var position = RmgenVector2D.Add(center, offset);
                position.Round();
                playerPosition.Add(position);
            }
            return playerPosition;
        }

        private static List<int> PrimeSortAllPlayers(RmgenRng rng, MapSettings settings)
            => PrimeSortPlayers(RmgenCommon.SortAllPlayers(rng, settings));

        private static List<int> PrimeSortPlayers(IReadOnlyList<int> playerIDs)
        {
            var prime = new List<int>();
            for (int i = 0; i < Math.Floor(playerIDs.Count / 2.0); ++i)
            {
                prime.Add(playerIDs[i]);
                prime.Add(playerIDs[playerIDs.Count - 1 - i]);
            }

            if (playerIDs.Count % 2 != 0)
                prime.Add(playerIDs[(int)Math.Floor(playerIDs.Count / 2.0)]);

            return prime;
        }
    }

    /// <summary>pyrenean_sierra.js（逐字移植）——随机角度的比利牛斯山脊横贯地图，双侧海岸和两处山口分割玩家。
    /// TILE_CENTERED_HEIGHT_MAP 在当前基础库未暴露；环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class PyreneanSierraMap2 : StandardMap
    {
        private static readonly string[] tGrassSpecific = { "new_alpine_grass_d", "new_alpine_grass_d", "new_alpine_grass_e" };
        private static readonly string[] tGrass = { "new_alpine_grass_d", "new_alpine_grass_b", "new_alpine_grass_e" };
        private static readonly string[] tGrassMidRange = { "new_alpine_grass_b", "alpine_grass_a" };
        private static readonly string[] tGrassHighRange = { "new_alpine_grass_a", "alpine_grass_a", "alpine_grass_rocky" };
        private static readonly string[] tHighRocks = { "alpine_cliff_b", "alpine_cliff_c", "alpine_cliff_c", "alpine_grass_rocky" };
        private static readonly string[] tSnowedRocks = { "alpine_cliff_b", "alpine_cliff_snow" };
        private static readonly string[] tTopSnow = { "alpine_snow_rocky", "alpine_snow_a" };
        private static readonly string[] tTopSnowOnly = { "alpine_snow_a" };
        private static readonly string[] tDirtyGrass = { "new_alpine_grass_d", "alpine_grass_d", "alpine_grass_c", "alpine_grass_b" };
        private static readonly string[] tLushGrass = { "new_alpine_grass_a", "new_alpine_grass_d" };
        private static readonly string[] tMidRangeCliffs = { "alpine_cliff_b", "alpine_cliff_c" };
        private static readonly string[] tHighRangeCliffs = { "alpine_mountainside", "alpine_cliff_snow" };
        private static readonly string[] tSand = { "beach_c", "beach_d" };
        private static readonly string[] tSandTransition = { "beach_scrub_50_" };
        private static readonly string[] tWater = { "sand_wet_a", "sand_wet_b", "sand_wet_b", "sand_wet_b" };
        private const string tGrassLandForest = "alpine_forrestfloor";
        private const string tGrassLandForest2 = "alpine_grass_d";
        private static readonly string[] tForestTransition = { "new_alpine_grass_d", "new_alpine_grass_b", "alpine_grass_d" };
        private const string tRoad = "new_alpine_citytile";
        private const string tRoadWild = "new_alpine_citytile";

        private const string oBeech = "gaia/tree/euro_beech";
        private const string oPine = "gaia/tree/aleppo_pine";
        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oDeer = "gaia/fauna_deer";
        private const string oFish = "gaia/fish/generic";
        private const string oRabbit = "gaia/fauna_rabbit";
        private const string oStoneLarge = "gaia/rock/alpine_large";
        private const string oStoneSmall = "gaia/rock/alpine_small";
        private const string oMetalLarge = "gaia/ore/alpine_large";
        private const string oMetalSmall = "gaia/ore/alpine_small";

        private const string aGrass = "actor|props/flora/grass_soft_small_tall.xml";
        private const string aGrassShort = "actor|props/flora/grass_soft_large.xml";
        private const string aRockLarge = "actor|geology/stone_granite_med.xml";
        private const string aRockMedium = "actor|geology/stone_granite_med.xml";
        private const string aBushMedium = "actor|props/flora/bush_medit_me.xml";
        private const string aBushSmall = "actor|props/flora/bush_medit_sm.xml";

        protected override double HeightLand => -100;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tGrass);
            var map = Map;
            var mapCenter = map.GetCenter();

            var pForestLand = new object[]
            {
                tGrassLandForest + "|" + oPine,
                tGrassLandForest + "|" + oBeech,
                tGrassLandForest2 + "|" + oPine,
                tGrassLandForest2 + "|" + oBeech,
                tGrassLandForest,
                tGrassLandForest2,
                tGrassLandForest2,
                tGrassLandForest2,
            };
            var pForestLandLight = new object[]
            {
                tGrassLandForest + "|" + oPine,
                tGrassLandForest + "|" + oBeech,
                tGrassLandForest2 + "|" + oPine,
                tGrassLandForest2 + "|" + oBeech,
                tGrassLandForest,
                tGrassLandForest2,
                tForestTransition,
                tGrassLandForest2,
                tGrassLandForest,
                tForestTransition,
                tGrassLandForest2,
                tForestTransition,
                tGrassLandForest2,
                tGrassLandForest2,
                tGrassLandForest2,
                tGrassLandForest2,
            };
            var pForestLandVeryLight = new object[]
            {
                tGrassLandForest2 + "|" + oPine,
                tGrassLandForest2 + "|" + oBeech,
                tForestTransition,
                tGrassLandForest2,
                tForestTransition,
                tForestTransition,
                tForestTransition,
                tGrassLandForest,
                tForestTransition,
                tGrassLandForest2,
                tForestTransition,
                tGrassLandForest2,
                tGrassLandForest2,
                tGrassLandForest2,
                tGrassLandForest2,
            };

            const double heightInit = -100;
            const double heightOcean = -22;
            const double heightWaterTerrain = -14;
            const double heightBase = -6;
            const double heightSand = -2;
            const double heightSandTransition = 0;
            const double heightGrass = 6;
            const double heightPyreneans = 15;
            const double heightGrassMidRange = 18;
            const double heightGrassHighRange = 30;
            double heightPassage = RmgenLibrary.ScaleByMapSize(25, 40, MapSize);
            double heightHighRocks = heightPassage + 5;
            double heightSnowedRocks = heightHighRocks + 10;
            double heightMountain = heightHighRocks + 20;
            const double heightOffsetHill = 7;
            const double heightOffsetHillRandom = 2;

            var clDirt = new TileClass(MapSize);
            var clRock = new TileClass(MapSize);
            var clMetal = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clPass = new TileClass(MapSize);
            var clPyrenneans = new TileClass(MapSize);
            var clPlayer = new TileClass(MapSize);
            var clHill = new TileClass(MapSize);
            var clForest = new TileClass(MapSize);
            var clWater = new TileClass(MapSize);

            double startAngle = rng.RandomAngle();
            double oceanAngle = startAngle + rng.RandFloat(-1, 1) * SafeMath.PI / 12;
            double mountainLength = RmgenLibrary.FractionToTiles(0.68, MapSize);
            double mountainWidth = RmgenLibrary.ScaleByMapSize(15, 55, MapSize);
            double mountainPeaks = 100 * RmgenLibrary.ScaleByMapSize(1, 10, MapSize);
            double mountainOffset = rng.RandFloat(-1, 1) * RmgenLibrary.ScaleByMapSize(1, 12, MapSize);
            double passageLength = RmgenLibrary.ScaleByMapSize(8, 50, MapSize);

            var terrainPerHeight = new[]
            {
                new TerrainHeightSpec(heightGrass, 5, tGrass, tMidRangeCliffs),
                new TerrainHeightSpec(heightGrassMidRange, 8, tGrassMidRange, tMidRangeCliffs),
                new TerrainHeightSpec(heightGrassHighRange, 8, tGrassHighRange, tMidRangeCliffs),
                new TerrainHeightSpec(heightHighRocks, 8, tHighRocks, tHighRangeCliffs),
                new TerrainHeightSpec(heightSnowedRocks, 7, tSnowedRocks, tHighRangeCliffs),
                new TerrainHeightSpec(double.PositiveInfinity, 6, tTopSnowOnly, tTopSnow),
            };

            var baseHeights = new double[MapSize][];
            double heightNoiseScale = RmgenLibrary.ScaleByMapSize(1, 3, MapSize);
            double trigNoiseScale = RmgenLibrary.ScaleByMapSize(5, 30, MapSize);
            for (int ix = 0; ix < MapSize; ++ix)
            {
                baseHeights[ix] = new double[MapSize];
                for (int iz = 0; iz < MapSize; ++iz)
                {
                    var position = new RmgenVector2D(ix, iz);
                    if (map.InMapBounds(position))
                    {
                        double height = heightBase + rng.RandFloat(-1, 1) + heightNoiseScale *
                            (SafeMath.Cos(ix / trigNoiseScale) + SafeMath.Sin(iz / trigNoiseScale));
                        map.SetHeight(position, height);
                        baseHeights[ix][iz] = height;
                    }
                    else
                        baseHeights[ix][iz] = heightInit;
                }
            }

            var playerIDs = RmgenCommon.SortAllPlayers(rng, settings);
            var playerPosition = PlayerPlacementArcs(settings, playerIDs, mapCenter,
                RmgenLibrary.FractionToTiles(0.35, MapSize), oceanAngle, 0.1 * SafeMath.PI, 0.9 * SafeMath.PI);

            RmgenCommon.PlacePlayerBases(rng, map, settings, tGrass[0], clPlayer, null,
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
                    TreesTemplate = oPine,
                    DecorativesTemplate = aGrassShort,
                });

            var mountainVec = new RmgenVector2D(mountainLength, 0);
            mountainVec.Rotate(-startAngle);
            var mountainStart = RmgenVector2D.Sub(mapCenter, RmgenVector2D.Div(mountainVec, 2));
            var mountainDirection = mountainVec.Clone();
            mountainDirection.Normalize();
            CreatePyreneans(rng, map, baseHeights, mountainPeaks, mountainWidth,
                mountainLength, mountainOffset, mountainStart, mountainDirection, startAngle, heightMountain);
            RmgenLibrary.PaintTileClassBasedOnHeight(heightPyreneans, double.PositiveInfinity,
                HeightPlacer.Mode.ExcludeMinExcludeMax, clPyrenneans);

            const double passageLocation = 0.35;
            var passageVec = mountainDirection.Perpendicular();
            passageVec.Mult(passageLength);
            foreach (double passLoc in new[] { passageLocation, 1 - passageLocation })
                foreach (int direction in new[] { 1, -1 })
                {
                    var passageStart = RmgenVector2D.Add(mountainStart, RmgenVector2D.Mult(mountainVec, passLoc));
                    var passageEnd = RmgenVector2D.Add(passageStart, RmgenVector2D.Mult(passageVec, direction));
                    RmgenCommon.CreatePassage(rng, map, passageStart, passageEnd,
                        7, 7, 2, tileClass: clPass, startHeight: heightPassage);
                }

            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new SmoothingPainter(1, 0.3, 1),
                new NearTileClassConstraint(clPyrenneans, 1));

            foreach (var ocean in RmgenGeometry.DistributePointsOnCircle(2, oceanAngle,
                RmgenLibrary.FractionToTiles(0.48, MapSize), mapCenter).points)
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.18, MapSize)),
                        0.9, 0.05, double.PositiveInfinity, ocean),
                    new IPainter[]
                    {
                        new ElevationPainter(heightOcean),
                        new TileClassPainter(clWater),
                    },
                    null);

            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new SmoothingPainter(5, 0.9, 1),
                new NearTileClassConstraint(clWater, 5));

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(60, 120, MapSize),
                    0.3, 0.06, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetHill, 4, relative: true, randomElevation: heightOffsetHillRandom),
                    new TerrainPainter(tGrassSpecific, rng),
                    new TileClassPainter(clHill),
                },
                RmgenLibrary.AvoidClasses(clWater, 5, clPlayer, 20, clBaseResource, 6, clPyrenneans, 2),
                RmgenLibrary.ScaleByMapSize(5, 35, MapSize));

            var forestTypes = new[] { new object[] { tForestTransition, pForestLandVeryLight, pForestLandLight, pForestLand } };
            double forestSize = RmgenLibrary.ScaleByMapSize(40, 115, MapSize) * SafeMath.PI;
            double forestNum = Math.Floor(RmgenLibrary.ScaleByMapSize(8, 40, MapSize) / forestTypes.Length);
            foreach (var type in forestTypes)
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, forestSize, 0.2, 0.1, double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(type,
                            new[]
                            {
                                RmgenLibrary.ScaleByMapSize(1, 2, MapSize),
                                RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                                RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                            }, rng),
                        new TileClassPainter(clForest),
                    },
                    RmgenLibrary.AvoidClasses(clPlayer, 20, clPyrenneans, 0, clForest, 7, clWater, 2),
                    forestNum);

            double loneTrees = RmgenLibrary.ScaleByMapSize(80, 400, MapSize);
            var group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, oPine, 1, 2, 1, 3),
                new ScatterObject(rng, oBeech, 1, 2, 1, 3),
            }, true, clForest);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, clForest, 1, clPlayer, 8, clPyrenneans, 1),
                loneTrees, 20);

            for (int i = 0; i < terrainPerHeight.Length; ++i)
                foreach (bool steep in new[] { false, true })
                    RmgenLibrary.CreateArea(
                        new MapBoundsPlacer(),
                        new TerrainPainter(steep ? terrainPerHeight[i].TerrainSteep : terrainPerHeight[i].TerrainGround, rng),
                        new AndConstraint(new IConstraint[]
                        {
                            new NearTileClassConstraint(clPyrenneans, 2),
                            new HeightConstraint(map, i > 0 ? terrainPerHeight[i - 1].MaxHeight : double.NegativeInfinity,
                                terrainPerHeight[i].MaxHeight),
                            steep
                                ? new SlopeConstraint(map, terrainPerHeight[i].Steepness, double.PositiveInfinity)
                                : new SlopeConstraint(map, double.NegativeInfinity, terrainPerHeight[i].Steepness),
                        }));

            for (int x = 0; x < MapSize; ++x)
                for (int z = 0; z < MapSize; ++z)
                {
                    var position = new RmgenVector2D(x, z);
                    double height = map.GetHeight(position);
                    double heightDiff = map.GetSlope(position);
                    object? terrainShore = GetShoreTerrain(position, height, heightDiff,
                        clWater, heightWaterTerrain, heightSand, heightSandTransition);
                    if (terrainShore != null)
                        TerrainFactory.CreateTerrain(terrainShore).Place(map, rng, position);
                }

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 20, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 40, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 60, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, size, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new TerrainPainter(tDirtyGrass, rng),
                        new TileClassPainter(clDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 3, clForest, 0, clPyrenneans, 5,
                        clHill, 0, clDirt, 5, clPlayer, 6),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 32, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 80, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, size, 0.3, 0.06, 0.5),
                    new IPainter[] { new TerrainPainter(tLushGrass, rng) },
                    RmgenLibrary.AvoidClasses(clWater, 3, clForest, 0, clPyrenneans, 5,
                        clHill, 0, clDirt, 5, clPlayer, 6),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, aGrassShort, 1, 2, 0, 1, -SafeMath.PI / 8, SafeMath.PI / 8) });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 2, clHill, 2, clPlayer, 5, clDirt, 0, clPyrenneans, 2),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize));
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.StayClasses(clDirt, 1),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 10);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aGrass, 2, 4, 0, 1.8, -SafeMath.PI / 8, SafeMath.PI / 8),
                new ScatterObject(rng, aGrassShort, 3, 6, 1.2, 2.5, -SafeMath.PI / 8, SafeMath.PI / 8),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, clHill, 2, clPlayer, 5, clDirt, 1,
                    clForest, 0, clPyrenneans, 2),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize));
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.StayClasses(clDirt, 1),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 10);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aBushMedium, 1, 2, 0, 2),
                new ScatterObject(rng, aBushSmall, 2, 4, 0, 2),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 2, clPlayer, 1, clPyrenneans, 1),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            GaiaEntities.CreateBalancedMetalMines(rng, map, NumPlayers,
                oMetalSmall, oMetalLarge, clMetal,
                RmgenLibrary.AvoidClasses(clWater, 2, clForest, 0,
                    clPlayer, RmgenLibrary.ScaleByMapSize(15, 25, MapSize), clPyrenneans, 3));

            GaiaEntities.CreateBalancedStoneMines(rng, map, NumPlayers,
                oStoneSmall, oStoneLarge, clRock,
                RmgenLibrary.AvoidClasses(clWater, 2, clForest, 0,
                    clPlayer, RmgenLibrary.ScaleByMapSize(15, 25, MapSize), clPyrenneans, 3, clMetal, 10));

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, clForest, 0, clPlayer, 0),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                new ScatterObject(rng, aRockMedium, 1, 3, 0, 2),
            }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 0, clForest, 0, clPlayer, 0),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, oDeer, 5, 7, 0, 4) }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, clForest, 0, clPlayer, 20,
                    clPyrenneans, 1, clFood, 15),
                3 * NumPlayers, 50);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, oRabbit, 2, 3, 0, 2) }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, clForest, 0, clPlayer, 20,
                    clPyrenneans, 1, clFood, 15),
                3 * NumPlayers, 50);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, oBerryBush, 5, 7, 0, 4) }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 3, clForest, 0, clPlayer, 20,
                    clPyrenneans, 1, clFood, 10),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, oFish, 2, 3, 0, 2) }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 10),
                    RmgenLibrary.StayClasses(clWater, 2),
                }),
                50 * NumPlayers, 60);

            return map.MakeExportable();
        }

        private static double Sigmoid(double x, double peakPosition)
            => 1 / (1 + SafeMath.Exp(x)) *
                (0.2 - Math.Max(0, Math.Abs(0.5 - peakPosition) - 0.3)) * 5;

        private static void CreatePyreneans(RmgenRng rng, RandomMap map, double[][] baseHeights,
            double mountainPeaks, double mountainWidth, double mountainLength, double mountainOffset,
            RmgenVector2D mountainStart, RmgenVector2D mountainDirection, double startAngle,
            double heightMountain)
        {
            for (int peak = 0; peak < mountainPeaks; ++peak)
            {
                double peakPosition = peak / mountainPeaks;
                double peakHeight = rng.RandFloat(0, 10);

                for (double distance = 0; distance < mountainWidth; distance += 1.0 / 3)
                {
                    double rest = 2 * (1 - distance / mountainWidth);
                    double sigmoidX =
                        -1 * (rest - 1.9) +
                        -4 *
                            (rest - rng.RandFloat(0.9, 1.1)) *
                            (rest - rng.RandFloat(0.9, 1.1)) *
                            (rest - rng.RandFloat(0.9, 1.1));

                    foreach (int direction in new[] { -1, 1 })
                    {
                        var peakBase = RmgenVector2D.Add(mountainStart,
                            RmgenVector2D.Mult(mountainDirection, peakPosition * mountainLength));
                        var mountainOffsetVec = new RmgenVector2D(mountainOffset, 0);
                        mountainOffsetVec.Rotate(-peakPosition * SafeMath.PI * 4);
                        var distanceVec = new RmgenVector2D(distance, 0);
                        distanceVec.Rotate(-startAngle - direction * SafeMath.PI / 2);
                        var pos = Sum(peakBase, mountainOffsetVec, distanceVec);
                        pos.Round();

                        map.SetHeight(pos,
                            baseHeights[(int)pos.X][(int)pos.Y] +
                            (heightMountain + peakHeight + rng.RandFloat(-9, 9)) *
                            Sigmoid(sigmoidX, peakPosition));
                    }
                }
            }
        }

        private static RmgenVector2D Sum(params RmgenVector2D[] vectors)
        {
            var result = new RmgenVector2D();
            foreach (var vector in vectors)
                result.Add(vector);
            return result;
        }

        private static object? GetShoreTerrain(RmgenVector2D position, double height, double heightDiff,
            TileClass clWater, double heightWaterTerrain, double heightSand, double heightSandTransition)
        {
            if (height <= heightWaterTerrain)
                return tWater;

            if (height <= heightSand && new NearTileClassConstraint(clWater, 2).Allows(position))
                return heightDiff < 2.5 ? tSand : tMidRangeCliffs;

            if (height <= heightSandTransition && new NearTileClassConstraint(clWater, 3).Allows(position))
                return heightDiff < 2.5 ? tSandTransition : tMidRangeCliffs;

            return null;
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
                if (eastIndex != -1)
                    result.Add(eastPosition[eastIndex]);
                else
                    result.Add(westPosition[west.IndexOf(playerID)]);
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
                .Select(teamID => playerIDs.Where(playerID => RmgenCommon.GetPlayerTeam(settings, playerID) == teamID).ToList())
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

        private readonly record struct TerrainHeightSpec(
            double MaxHeight, double Steepness, object TerrainGround, object TerrainSteep);
    }
}
