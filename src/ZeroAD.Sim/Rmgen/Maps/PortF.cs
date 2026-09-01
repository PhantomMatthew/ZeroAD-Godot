using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>jebel_barkal.js（逐字移植）——纳帕塔圣山：读取 jebel_barkal.pmp 起伏，
    /// 尼罗河与灌渠切出肥沃带，山脚布置金字塔和库施城，山顶守军/宝藏按触发难度缩放。
    /// TILE_CENTERED_HEIGHT_MAP 按本仓约定忽略；jebel_barkal_triggers.js 与 placePlayersNomad 不在本类移植范围。</summary>
    public sealed class JebelBarkalMap2 : StandardMap
    {
        private const string tSand = "desert_sand_dunes_100";
        private static readonly string[] tHilltop = { "new_savanna_dirt_c", "new_savanna_dirt_d" };
        private static readonly string[] tHillGround = { "savanna_dirt_rocks_a", "savanna_dirt_rocks_b", "savanna_dirt_rocks_c" };
        private static readonly string[] tHillCliff = { "savanna_cliff_a_red", "savanna_cliff_b_red" };
        private const string tRoadDesert = "savanna_tile_a";
        private const string tRoadFertileLand = "savanna_tile_a";
        private const string tWater = "desert_sand_wet";
        private static readonly string[] tGrass =
        {
            "savanna_shrubs_a_wetseason", "alpine_grass_b_wild", "medit_shrubs_a", "steppe_grass_green_a",
        };
        private const string tGrassTransition1 = "desert_grass_a";
        private const string tGrassTransition2 = "steppe_grass_dirt_66";
        private const string tPath = "road2";
        private const string tPathWild = "road_med";

        private const string oAcacia = "gaia/tree/acacia";
        private const string oPalmPath = "gaia/tree/cretan_date_palm_tall";
        private static readonly string[] oPalms =
        {
            "gaia/tree/cretan_date_palm_tall",
            "gaia/tree/cretan_date_palm_short",
            "gaia/tree/palm_tropic",
            "gaia/tree/date_palm",
            "gaia/tree/senegal_date_palm",
            "gaia/tree/medit_fan_palm",
        };
        private const string oBerryBushGrapes = "gaia/fruit/grapes";
        private const string oBerryBushDesert = "gaia/fruit/berry_05";
        private const string oStoneLargeDesert = "gaia/rock/desert_large";
        private const string oStoneSmallDesert = "gaia/rock/desert_small";
        private const string oMetalLargeDesert = "gaia/ore/desert_large";
        private const string oMetalSmallDesert = "gaia/ore/desert_small";
        private const string oStoneLargeFertileLand = "gaia/rock/desert_large";
        private const string oStoneSmallFertileLand = "gaia/rock/greece_small";
        private const string oMetalLargeFertileLand = "gaia/ore/desert_large";
        private const string oMetalSmallFertileLand = "gaia/ore/temperate_small";
        private const string oFoodTreasureBin = "gaia/treasure/food_bin";
        private const string oFoodTreasureCrate = "gaia/treasure/food_crate";
        private const string oFoodTreasureJars = "gaia/treasure/food_jars";
        private const string oWoodTreasure = "gaia/treasure/wood";
        private const string oStoneTreasure = "gaia/treasure/stone";
        private const string oMetalTreasure = "gaia/treasure/metal";
        private static readonly string[] oTreasuresHill = { oWoodTreasure, oStoneTreasure, oMetalTreasure };
        private static readonly string[] oTreasuresCity =
        {
            oFoodTreasureBin, oFoodTreasureCrate, oFoodTreasureJars, oWoodTreasure, oStoneTreasure, oMetalTreasure,
        };
        private const string oGiraffe = "gaia/fauna_giraffe";
        private const string oGiraffeInfant = "gaia/fauna_giraffe_infant";
        private const string oGazelle = "gaia/fauna_gazelle";
        private const string oRhino = "gaia/fauna_rhinoceros_white";
        private const string oWarthog = "gaia/fauna_boar";
        private const string oElephant = "gaia/fauna_elephant_african_bush";
        private const string oElephantInfant = "gaia/fauna_elephant_african_infant";
        private const string oLion = "gaia/fauna_lion";
        private const string oLioness = "gaia/fauna_lioness";
        private const string oCrocodile = "gaia/fauna_crocodile_nile";
        private const string oFish = "gaia/fish/tilapia";
        private const string oHawk = "birds/buzzard";
        private const string oTempleApedemak = "structures/kush/temple";
        private const string oTempleAmun = "structures/kush/temple_amun";
        private const string oPyramidLarge = "structures/kush/pyramid_large";
        private const string oPyramidSmall = "structures/kush/pyramid_small";
        private const string oWonderPtol = "structures/ptol/wonder";
        private const string oFortress = "structures/kush/fortress";
        private const string oHouse = "structures/kush/house";
        private const string oMarket = "structures/kush/market";
        private const string oForge = "structures/kush/forge";
        private const string oBlemmyeCamp = "structures/kush/camp_blemmye";
        private const string oNobaCamp = "structures/kush/camp_noba";
        private const string oCivicCenter = "structures/kush/civil_centre";
        private const string oBarracks = "structures/kush/barracks";
        private const string oStable = "structures/kush/stable";
        private const string oElephantStable = "structures/kush/elephant_stable";
        private const string oWallMedium = "structures/kush/wall_medium";
        private const string oWallGate = "structures/kush/wall_gate";
        private const string oWallTower = "structures/kush/wall_tower";
        private const string oPalisadeMedium = "structures/palisades_medium";
        private const string oPalisadeGate = "structures/palisades_gate";
        private const string oPalisadeTower = "structures/palisades_tower";
        private const string oKushCitizenArcher = "units/kush/infantry_archer_b";
        private const string oKushHealer = "units/kush/support_healer_b";
        private const string oKushChampionArcher = "units/kush/champion_infantry_archer";
        private static readonly string[] oKushChampions =
        {
            oKushChampionArcher,
            "units/kush/champion_infantry_amun",
            "units/kush/champion_infantry_apedemak",
        };
        private static readonly string[] oPtolSiege =
        {
            "units/ptol/siege_lithobolos_unpacked", "units/ptol/siege_polybolos_unpacked",
        };
        private const string oTriggerPointCityPath = "trigger/trigger_point_A";
        private const string oTriggerPointAttackerPatrol = "trigger/trigger_point_B";

        private const double minHeightSource = 3;
        private const double maxHeightSource = 800;
        private const int pmpPatchSize = 16;
        private const uint pmpMagic = 0x504D5350;
        private const double wallOverlap = 0.05;

        private double _heightWaterLevel;

        protected override double HeightLand => 0;

        protected internal override void ApplyExtraEnvironment(RmgenEnvironment env, RmgenRng rng)
        {
            env.SetWaterHeight(_heightWaterLevel + RmgenConstants.SEA_LEVEL);
            env.SetSunRotation(SafeMath.PI / 2 * rng.RandFloat(-1, 1));
        }

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tSand);
            var map = Map;
            int mapSize = MapSize;
            int difficulty = GetDifficulty(settings);
            string oTower = mapSize >= 256 && difficulty >= 3 ?
                "structures/kush/defense_tower" : "structures/kush/sentry_tower";

            string aPalmPath = RmgenLibrary.ActorTemplate("flora/trees/palm_cretan_date_tall");
            string aRock = RmgenLibrary.ActorTemplate("geology/stone_savanna_med");
            string aHandcart = RmgenLibrary.ActorTemplate("props/special/eyecandy/handcart_1");
            string aPlotFence = RmgenLibrary.ActorTemplate("props/special/common/plot_fence");
            string aStatueKush = RmgenLibrary.ActorTemplate("props/special/eyecandy/statues_kush");
            var aStatues = new[]
            {
                RmgenLibrary.ActorTemplate("props/structures/kushites/statue_pedestal_rectangular"),
                RmgenLibrary.ActorTemplate("props/structures/kushites/statue_pedestal_rectangular_lion"),
            };
            var aBushesFertileLand = new[]
            {
                "props/flora/shrub_spikes",
                "props/flora/shrub_spikes",
                "props/flora/shrub_spikes",
                "props/flora/ferns",
                "props/flora/ferns",
                "props/flora/ferns",
                "props/flora/shrub_tropic_plant_a",
                "props/flora/shrub_tropic_plant_b",
                "props/flora/shrub_tropic_plant_flower",
                "props/flora/foliagebush",
                "props/flora/bush",
                "props/flora/bush_medit_la",
                "props/flora/bush_medit_la_lush",
                "props/flora/bush_medit_me_lush",
                "props/flora/bush_medit_sm",
                "props/flora/bush_medit_sm_lush",
                "props/flora/bush_tempe_la_lush",
            }.Select(RmgenLibrary.ActorTemplate).ToList();
            var aBushesCity = new[]
            {
                "props/flora/bush_dry_a",
                "props/flora/bush_medit_la_dry",
                "props/flora/bush_medit_me_dry",
                "props/flora/bush_medit_sm",
                "props/flora/bush_medit_sm_dry",
            }.Select(RmgenLibrary.ActorTemplate).ToList();
            var aBushesDesert = new[]
            {
                "props/flora/bush_tempe_me_dry",
                "props/flora/grass_soft_dry_large_tall",
                "props/flora/grass_soft_dry_small_tall",
            }.Select(RmgenLibrary.ActorTemplate).Concat(aBushesCity).ToList();
            var aWaterDecoratives = new[]
            {
                RmgenLibrary.ActorTemplate("props/flora/reeds_pond_lush_a"),
            };

            string tForestFloorFertile = rng.PickRandom(tGrass);
            var pForestPalms = new List<string> { tForestFloorFertile };
            pForestPalms.AddRange(oPalms.Select(tree => tForestFloorFertile + TerrainFactory.TerrainSeparator + tree));
            pForestPalms.Add(tForestFloorFertile);

            double HeightScale(double num) => num * mapSize / 320.0;
            double FractionToTiles(double fraction) => RmgenLibrary.FractionToTiles(fraction, mapSize);
            double ScaleByMapSize(double min, double max) => RmgenLibrary.ScaleByMapSize(min, max, mapSize);
            RmgenVector2D mapCenter = map.GetCenter();
            double mapLeft = 0;
            double mapRight = mapSize;
            double mapTop = mapSize;
            double mapBottom = 0;

            var clHill = new TileClass(mapSize);
            var clCliff = new TileClass(mapSize);
            var clDesert = new TileClass(mapSize);
            var clFertileLand = new TileClass(mapSize);
            var clWater = new TileClass(mapSize);
            var clIrrigationCanal = new TileClass(mapSize);
            var clPassage = new TileClass(mapSize);
            var clPlayer = new TileClass(mapSize);
            var clBaseResource = new TileClass(mapSize);
            var clFood = new TileClass(mapSize);
            var clForest = new TileClass(mapSize);
            var clRock = new TileClass(mapSize);
            var clMetal = new TileClass(mapSize);
            var clTreasure = new TileClass(mapSize);
            var clCity = new TileClass(mapSize);
            var clPath = new TileClass(mapSize);
            var clPathStatues = new TileClass(mapSize);
            var clPathCrossing = new TileClass(mapSize);
            var clStatue = new TileClass(mapSize);
            var clWall = new TileClass(mapSize);
            var clGate = new TileClass(mapSize);
            var clRoad = new TileClass(mapSize);
            var clTriggerPointCityPath = new TileClass(mapSize);
            var clTriggerPointMap = new TileClass(mapSize);
            var clSoldier = new TileClass(mapSize);
            var clTower = new TileClass(mapSize);
            var clFortress = new TileClass(mapSize);
            var clTemple = new TileClass(mapSize);
            var clRitualPlace = new TileClass(mapSize);
            var clPyramid = new TileClass(mapSize);
            var clHouse = new TileClass(mapSize);
            var clForge = new TileClass(mapSize);
            var clStable = new TileClass(mapSize);
            var clElephantStable = new TileClass(mapSize);
            var clCivicCenter = new TileClass(mapSize);
            var clBarracks = new TileClass(mapSize);
            var clBlemmyeCamp = new TileClass(mapSize);
            var clNobaCamp = new TileClass(mapSize);
            var clMarket = new TileClass(mapSize);
            var clDecorative = new TileClass(mapSize);

            const double riverAngle = 0.05 * SafeMath.PI;
            double hillRadius = ScaleByMapSize(40, 120);
            var positionPyramids = new RmgenVector2D(FractionToTiles(0.15), FractionToTiles(0.75));
            const double pathWidth = 4;
            const double pathWidthCenter = 10;
            const double pathWidthSecondary = 6;
            string? placeNapataWall = mapSize < 192 || difficulty < 2 ? null :
                difficulty < 3 ? "napata_palisade" : "napata_wall";

            var layoutFertileLandTextures = new[]
            {
                new FertileTexture(FractionToTiles(0), FractionToTiles(0.04),
                    TerrainFactory.CreateTerrain(tGrassTransition1), clFertileLand),
                new FertileTexture(FractionToTiles(0.04), FractionToTiles(0.08),
                    TerrainFactory.CreateTerrain(tGrassTransition2), clDesert),
            };

            var layoutKushTemples = BuildTempleLayout(mapSize, ScaleByMapSize);
            var layoutKushCity = BuildKushCity(difficulty, oTower, clHouse, clFortress, clPath,
                clCivicCenter, clElephantStable, clStable, clBarracks, clTower, clMarket, clForge,
                clNobaCamp, clBlemmyeCamp);

            LoadJebelHeightmap();

            double heightDesert = map.GetHeight(mapCenter);
            double heightFertileLand = heightDesert - HeightScale(2);
            double heightShoreline = heightFertileLand - HeightScale(0.5);
            double heightWaterLevel = _heightWaterLevel = heightFertileLand - HeightScale(3);
            double heightPassage = heightWaterLevel - HeightScale(1.5);
            double heightIrrigationCanal = heightWaterLevel - HeightScale(4);
            double heightSeaGround = heightWaterLevel - HeightScale(8);
            double heightHill = heightDesert + HeightScale(4);
            double heightHilltop = heightHill + HeightScale(90);
            double heightHillArchers = (heightHilltop + heightHill) / 2;
            double heightOffsetPath = HeightScale(-2.5);
            double heightOffsetRoad = HeightScale(-1.5);
            double heightOffsetWalls = HeightScale(2.5);
            double heightOffsetStatue = HeightScale(2.5);

            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new ElevationPainter(heightDesert),
                new HeightConstraint(map, double.NegativeInfinity, heightDesert));

            double widthFertileLand = FractionToTiles(0.33);
            var fertileGrassTerrain = TerrainFactory.CreateTerrain(tGrass);
            PaintRiverExt(rng, map,
                RotateAround(new RmgenVector2D(mapLeft, mapBottom), -riverAngle, mapCenter),
                RotateAround(new RmgenVector2D(mapRight, mapBottom), -riverAngle, mapCenter),
                2 * widthFertileLand, 8, heightFertileLand, heightDesert,
                parallel: true, deviation: 0, meanderShort: 40, meanderLong: 0,
                waterFunc: (position, _, _) =>
                {
                    fertileGrassTerrain.Place(map, rng, position);
                    clFertileLand.Add(position);
                },
                landFunc: (position, shoreDist1, shoreDist2) =>
                {
                    foreach (var riv in layoutFertileLandTextures)
                        if (riv.Left < +shoreDist1 && +shoreDist1 < riv.Right ||
                            riv.Left < -shoreDist2 && -shoreDist2 < riv.Right)
                        {
                            riv.TileClass.Add(position);
                            riv.Terrain.Place(map, rng, position);
                        }
                });

            PaintRiverExt(rng, map,
                RotateAround(new RmgenVector2D(mapLeft, mapBottom), -riverAngle, mapCenter),
                RotateAround(new RmgenVector2D(mapRight, mapBottom), -riverAngle, mapCenter),
                FractionToTiles(0.2), 4, heightSeaGround, heightFertileLand,
                parallel: true, deviation: 0, meanderShort: 40, meanderLong: 0);

            var playerIDs = RmgenCommon.SortAllPlayers(rng, settings);
            var playerPosition = PlayerPlacementArcs(settings, playerIDs, mapCenter,
                FractionToTiles(0.38), riverAngle - 0.5 * SafeMath.PI,
                0.05 * SafeMath.PI, 0.55 * SafeMath.PI);

            if (!settings.Nomad)
                foreach (var position in playerPosition)
                    RmgenCommon.AddCivicCenterAreaToClass(map, position, clPlayer);

            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new IPainter[]
                {
                    new TileClassPainter(clWater),
                    new TileClassUnPainter(clFertileLand),
                },
                new HeightConstraint(map, double.NegativeInfinity, heightWaterLevel));

            var avoidWater = Static(map, RmgenLibrary.AvoidClasses(clWater, 0));
            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new TileClassPainter(clDesert),
                All(
                    new HeightConstraint(map, double.NegativeInfinity, heightHill),
                    avoidWater,
                    RmgenLibrary.AvoidClasses(clFertileLand, 0)));

            var stayDesert = Static(map, RmgenLibrary.StayClasses(clDesert, 0));
            var stayFertileLand = Static(map, RmgenLibrary.StayClasses(clFertileLand, 0));

            var irrigationCanalAreas = new List<Area>();
            for (int i = 0; i < 30; ++i)
            {
                double x = FractionToTiles(rng.RandFloat(0, 1));
                var area = RmgenLibrary.CreateArea(
                    NewPath(
                        RotateAround(new RmgenVector2D(x, mapBottom), -riverAngle, mapCenter),
                        RotateAround(new RmgenVector2D(x, mapTop), -riverAngle, mapCenter),
                        3, 0, 10, 0.1, 0.01, double.PositiveInfinity),
                    (IPainter?)null,
                    RmgenLibrary.AvoidClasses(clDesert, 2));
                if (area != null)
                    irrigationCanalAreas.Add(area);
            }

            var irrigationCanalLocations = new List<double>();
            foreach (var area in irrigationCanalAreas)
            {
                var points = area.GetPoints();
                var avoidCanals = RmgenLibrary.AvoidClasses(
                    clPlayer, ScaleByMapSize(8, 13),
                    clIrrigationCanal, ScaleByMapSize(15, 25));
                if (points.Count == 0 || points.Any(point => !avoidCanals.Allows(point)))
                    continue;

                var canalLocation = rng.PickRandom(points);
                canalLocation.RotateAround(riverAngle, mapCenter);
                irrigationCanalLocations.Add(canalLocation.X);
                RmgenLibrary.CreateArea(
                    new MapBoundsPlacer(),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            heightIrrigationCanal, 1),
                        new TileClassPainter(clIrrigationCanal),
                    },
                    All(
                        new StayAreasConstraint(new[] { area }),
                        new HeightConstraint(map, heightIrrigationCanal, heightDesert)));
            }

            int previousPassageY = rng.RandIntInclusive(0, widthFertileLand);
            var areasPassages = new List<Area>();
            irrigationCanalLocations.Sort();
            for (int i = 0; i < irrigationCanalLocations.Count; ++i)
            {
                double previous = i == 0 ? mapLeft : irrigationCanalLocations[i - 1];
                double next = i == irrigationCanalLocations.Count - 1 ? mapRight :
                    irrigationCanalLocations[i + 1];
                double x1 = (irrigationCanalLocations[i] + previous) / 2;
                double x2 = (irrigationCanalLocations[i] + next) / 2;
                double y = 0;

                for (int tries = 0; tries < 100; ++tries)
                {
                    y = (previousPassageY +
                        rng.RandIntInclusive(0.2 * widthFertileLand, 0.8 * widthFertileLand)) %
                        widthFertileLand;

                    var pos = RotateAround(new RmgenVector2D((x1 + x2) / 2, y), -riverAngle, mapCenter);
                    pos.Round();

                    if (map.ValidTilePassable(pos) &&
                        RmgenLibrary.AvoidClasses(clDesert, 12).Allows(pos) &&
                        new HeightConstraint(map, heightIrrigationCanal, heightFertileLand).Allows(pos))
                        break;
                }

                var area = RmgenLibrary.CreateArea(
                    NewPath(
                        RotateAround(new RmgenVector2D(x1, y), -riverAngle, mapCenter),
                        RotateAround(new RmgenVector2D(x2, y), -riverAngle, mapCenter),
                        10, 0, 1, 0, 0, double.PositiveInfinity),
                    new IPainter[]
                    {
                        new ElevationPainter(heightPassage),
                        new TileClassPainter(clPassage),
                    },
                    All(
                        new HeightConstraint(map, double.NegativeInfinity, heightPassage),
                        RmgenLibrary.StayClasses(clFertileLand, 2)));

                if (area == null || area.PointCount == 0)
                    continue;

                previousPassageY = (int)y;
                areasPassages.Add(area);
            }

            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new TileClassPainter(clHill),
                new HeightConstraint(map, heightHill, double.PositiveInfinity));
            var areaWater = RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new TileClassPainter(clWater),
                new HeightConstraint(map, double.NegativeInfinity, heightWaterLevel));
            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new TerrainPainter(tWater, rng),
                new HeightConstraint(map, double.NegativeInfinity, heightShoreline));
            var areaHill = RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new TerrainPainter(tHillGround, rng),
                new HeightConstraint(map, heightHill, double.PositiveInfinity));
            var areaHilltop = RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new TerrainPainter(tHilltop, rng),
                All(
                    new HeightConstraint(map, heightHilltop, double.PositiveInfinity),
                    new SlopeConstraint(map, double.NegativeInfinity, 2)));

            if (!settings.Nomad)
                for (int i = 0; i < NumPlayers; ++i)
                {
                    bool isDesert = clDesert.Has(playerPosition[i]);
                    RmgenCommon.PlaceSinglePlayerBase(map, rng, settings,
                        playerIDs[i], playerPosition[i], clPlayer,
                        isDesert ? tRoadDesert : tRoadFertileLand,
                        isDesert ? tRoadDesert : tRoadFertileLand,
                        new RmgenCommon.PlayerBaseOptions
                        {
                            BaseResourceClass = clBaseResource,
                            ExtraBaseResourceConstraint = RmgenLibrary.AvoidClasses(clPlayer, 4, clWater, 4),
                            StartingAnimal = true,
                            StartingAnimalTemplate = oGazelle,
                            StartingAnimalDistance = 15,
                            StartingAnimalMinGroupDistance = 2,
                            StartingAnimalMaxGroupDistance = 4,
                            StartingAnimalMinGroupCount = 2,
                            StartingAnimalMaxGroupCount = 3,
                            BerriesTemplate = isDesert ? oBerryBushDesert : oBerryBushGrapes,
                            Mines = new()
                            {
                                (isDesert ? oMetalLargeDesert : oMetalLargeFertileLand, (string?)null, (object?)null),
                                (isDesert ? oStoneLargeDesert : oStoneLargeFertileLand, (string?)null, (object?)null),
                            },
                            TreesTemplate = isDesert ? oAcacia : rng.PickRandom(oPalms),
                            TreesCount = (int)ScaleByMapSize(isDesert ? 5 : 15, isDesert ? 10 : 30),
                            Treasures = new()
                            {
                                (oWoodTreasure, isDesert ? 4 : 0),
                                (oStoneTreasure, isDesert ? 1 : 0),
                                (oMetalTreasure, isDesert ? 1 : 0),
                            },
                            DecorativesTemplate = isDesert ? aRock : rng.PickRandom(aBushesFertileLand),
                        });
                }

            var areaPyramids = RmgenLibrary.CreateArea(
                new DiskPlacer(ScaleByMapSize(5, 14), positionPyramids),
                (IPainter?)null,
                null);
            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new RandomObject(rng, new[] { oPyramidLarge, oPyramidSmall },
                        ScaleByMapSize(1, 6), ScaleByMapSize(2, 8),
                        ScaleByMapSize(6, 8), ScaleByMapSize(6, 14),
                        SafeMath.PI * 1.35, SafeMath.PI * 1.5, ScaleByMapSize(6, 8)),
                }, true, clPyramid),
                0, null, 1, 50, Areas(areaPyramids));

            var gridCenter = new RmgenVector2D(0, FractionToTiles(0.3));
            gridCenter.Rotate(-riverAngle);
            gridCenter.Add(mapCenter);
            gridCenter.Round();
            double gridMaxAngle = Math.Min(ScaleByMapSize(1.0 / 3, 1), 2.0 / 3) * SafeMath.PI;
            double gridStartAngle = -SafeMath.PI / 2 - gridMaxAngle / 2 + riverAngle;
            double GridRadius(int y) => hillRadius + 18 * y;

            int gridPointsX = layoutKushTemples.Count;
            int gridPointsY = (int)Math.Floor(ScaleByMapSize(2, 5));
            int gridPointXCenter = (int)Math.Floor(gridPointsX / 2.0);
            int gridPointYCenter = (int)Math.Floor(gridPointsY / 2.0);

            var cityGridPosition = new List<List<RmgenVector2D>>();
            var cityGridAngle = new List<List<double>>();
            for (int y = 0; y < gridPointsY; ++y)
            {
                var distributed = RmgenGeometry.DistributePointsOnCircularSegment(
                    gridPointsX, gridMaxAngle, gridStartAngle, GridRadius(y), gridCenter);
                cityGridPosition.Add(distributed.points);
                cityGridAngle.Add(distributed.angles);
            }

            for (int y = 0; y < cityGridPosition.Count; ++y)
                for (int x = 0; x < cityGridPosition[y].Count; ++x)
                {
                    var rounded = cityGridPosition[y][x];
                    rounded.Round();
                    cityGridPosition[y][x] = rounded;
                    RmgenLibrary.CreateArea(
                        new DiskPlacer(pathWidth, rounded),
                        new IPainter[]
                        {
                            new TileClassPainter(clPath),
                            new TileClassPainter(clPathCrossing),
                        },
                        null);
                }

            var areasCityPaths = new List<Area>();
            for (int y = 0; y < gridPointsY; ++y)
                for (int x = 1; x < gridPointsX; ++x)
                {
                    double width = y == gridPointYCenter ? pathWidthSecondary : pathWidth;
                    AddArea(areasCityPaths, RmgenLibrary.CreateArea(
                        NewPath(cityGridPosition[y][x - 1], cityGridPosition[y][x],
                            width, 0, 8, 0, 0, double.PositiveInfinity),
                        new TileClassPainter(clPath),
                        null));
                }

            for (int y = 1; y < gridPointsY; ++y)
                for (int x = 0; x < gridPointsX; ++x)
                {
                    double width = Math.Abs(x - gridPointXCenter) == 0 ? pathWidthCenter :
                        Math.Abs(x - gridPointXCenter) == 1 ? pathWidthSecondary : pathWidth;
                    AddArea(areasCityPaths, RmgenLibrary.CreateArea(
                        NewPath(cityGridPosition[y - 1][x], cityGridPosition[y][x],
                            width, 0, 8, 0, 0, double.PositiveInfinity),
                        new TileClassPainter(clPath),
                        null));
                }

            var entitiesTemples = new List<RmgenEntity>();
            var templePosition = new List<RmgenVector2D>();
            for (int i = 0; i < layoutKushTemples.Count; ++i)
            {
                int x = i + (gridPointsX - layoutKushTemples.Count) / 2;
                var offset = layoutKushTemples[i].PathOffset;
                offset.Rotate(-SafeMath.PI / 2 - cityGridAngle[0][x]);
                var position = RmgenVector2D.Add(cityGridPosition[0][x], offset);
                templePosition.Add(position);
                var entity = map.PlaceEntityPassable(layoutKushTemples[i].Template, 0,
                    position, cityGridAngle[0][x]);
                if (entity != null)
                    entitiesTemples.Add(entity);
            }

            RmgenLibrary.CreateArea(
                new EntitiesObstructionPlacer(entitiesTemples, 0, double.PositiveInfinity),
                new TileClassPainter(clTemple),
                null);
            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new ElevationBlendingPainter(heightDesert, 0.8),
                new NearTileClassConstraint(clTemple, 0));
            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new IPainter[]
                {
                    new TerrainPainter(tHillCliff, rng),
                    new TileClassPainter(clCliff),
                },
                All(
                    RmgenLibrary.StayClasses(clHill, 0),
                    new SlopeConstraint(map, 2, double.PositiveInfinity)));
            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new TerrainPainter(tPathWild, rng),
                All(
                    new NearTileClassConstraint(clTemple, 1),
                    RmgenLibrary.AvoidClasses(clPath, 0, clCliff, 1)));

            double statueCount = ScaleByMapSize(10, 40);
            var centralPathStart = cityGridPosition[0][gridPointXCenter];
            double centralPathLength = centralPathStart.DistanceTo(
                cityGridPosition[gridPointsY - 1][gridPointXCenter]);
            double centralPathAngle = cityGridAngle[0][gridPointXCenter];
            for (int i = 0; i < 2; ++i)
                for (int stat = 0; stat < statueCount; ++stat)
                {
                    var start = new RmgenVector2D(0, pathWidthCenter * 3.0 / 4 * (i - 0.5));
                    start.Rotate(centralPathAngle);
                    start.Add(centralPathStart);
                    var position = new RmgenVector2D(centralPathLength, 0);
                    position.Mult(stat / statueCount);
                    position.Rotate(-centralPathAngle);
                    position.Add(start);
                    position.Add(new RmgenVector2D(0.5, 0.5));

                    if (!RmgenLibrary.AvoidClasses(clPathCrossing, 2).Allows(position))
                        continue;

                    map.PlaceEntityPassable(rng.PickRandom(aStatues), 0, position,
                        centralPathAngle - SafeMath.PI * (i + 0.5));
                    AddRounded(clPathStatues, position);
                }

            double centralChampionsCount = ScaleByMapSize(2, 40);
            for (int i = 0; i < 2; ++i)
                for (int champ = 0; champ < centralChampionsCount; ++champ)
                {
                    var start = new RmgenVector2D(0, pathWidthCenter * 0.5 * (i - 0.5));
                    start.Rotate(-centralPathAngle);
                    start.Add(centralPathStart);
                    var position = new RmgenVector2D(centralPathLength, 0);
                    position.Mult(champ / centralChampionsCount);
                    position.Rotate(-centralPathAngle);
                    position.Add(start);
                    position.Add(new RmgenVector2D(0.5, 0.5));

                    if (!RmgenLibrary.AvoidClasses(clPathCrossing, 2).Allows(position))
                        continue;

                    map.PlaceEntityPassable(rng.PickRandom(oKushChampions), 0, position,
                        centralPathAngle - SafeMath.PI * (i - 0.5));
                    AddRounded(clPathStatues, position);
                }

            foreach (int x in new[] { gridPointXCenter - 1, gridPointXCenter + 1 })
            {
                map.PlaceEntityAnywhere(aStatueKush, 0, cityGridPosition[gridPointYCenter][x],
                    cityGridAngle[gridPointYCenter][x]);
                clPathStatues.Add(cityGridPosition[gridPointYCenter][x]);
            }

            var ritualPosition = Average(new[]
            {
                templePosition[(int)Math.Floor(templePosition.Count / 2.0) - 1],
                templePosition[(int)Math.Ceiling(templePosition.Count / 2.0) - 1],
                cityGridPosition[0][gridPointXCenter],
                cityGridPosition[0][gridPointXCenter - 1],
            });
            ritualPosition.Round();

            double ritualAngle =
                (cityGridAngle[0][gridPointXCenter] + cityGridAngle[0][gridPointXCenter - 1]) / 2 +
                SafeMath.PI / 2;
            map.PlaceEntityPassable(aStatueKush, 0, ritualPosition, ritualAngle - SafeMath.PI / 2);
            RmgenLibrary.CreateArea(
                new DiskPlacer(ScaleByMapSize(4, 6), ritualPosition),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tPathWild, tPath }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetPath, 2, relative: true),
                    new TileClassPainter(clRitualPlace),
                },
                RmgenLibrary.AvoidClasses(clCliff, 1));
            var statueGround = RmgenVector2D.Add(new RmgenVector2D(-1, -1), ritualPosition);
            RmgenLibrary.CreateArea(
                new DiskPlacer(0, statueGround),
                new ElevationPainter(heightDesert + heightOffsetStatue),
                null);

            var healers = RmgenGeometry.DistributePointsOnCircularSegment(
                (int)SafeMath.Round(ScaleByMapSize(2, 10)), SafeMath.PI, ritualAngle,
                ScaleByMapSize(2, 3), ritualPosition);
            for (int i = 0; i < healers.points.Count; ++i)
                map.PlaceEntityPassable(oKushHealer, 0, healers.points[i], healers.angles[i] + SafeMath.PI);

            var ritualStatues = RmgenGeometry.DistributePointsOnCircularSegment(
                (int)SafeMath.Round(ScaleByMapSize(4, 8)), SafeMath.PI, ritualAngle,
                ScaleByMapSize(3, 4), ritualPosition);
            for (int i = 0; i < ritualStatues.points.Count; ++i)
                map.PlaceEntityPassable(rng.PickRandom(aStatues), 0,
                    ritualStatues.points[i], ritualStatues.angles[i] + SafeMath.PI);

            var palmPosition = RmgenGeometry.DistributePointsOnCircularSegment(
                (int)SafeMath.Round(ScaleByMapSize(6, 16)), SafeMath.PI, ritualAngle,
                ScaleByMapSize(4, 5), ritualPosition).points;
            foreach (var position in palmPosition)
                if (RmgenLibrary.AvoidClasses(clTemple, 1).Allows(position))
                    map.PlaceEntityPassable(oPalmPath, 0, position, rng.RandomAngle());

            var areaPaths = RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tPathWild, tPath }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetPath, 1, relative: true),
                },
                RmgenLibrary.StayClasses(clPath, 0));

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oTriggerPointCityPath, 1, 1, 0, 0),
                }, true, clTriggerPointCityPath),
                0,
                All(
                    RmgenLibrary.AvoidClasses(clTriggerPointCityPath, 8),
                    RmgenLibrary.StayClasses(clPathCrossing, 2)),
                ScaleByMapSize(20, 100), 30, Areas(areaPaths));

            for (int y = 1; y < gridPointsY; ++y)
                for (int x = 1; x < gridPointsX; ++x)
                    RmgenLibrary.CreateArea(
                        new ConvexPolygonPlacer(
                            new[]
                            {
                                cityGridPosition[y - 1][x - 1],
                                cityGridPosition[y - 1][x],
                                cityGridPosition[y][x - 1],
                                cityGridPosition[y][x],
                            },
                            double.PositiveInfinity),
                        new IPainter[]
                        {
                            new TerrainPainter(tRoadDesert, rng),
                            new CityPainter(rng, layoutKushCity,
                                (-cityGridAngle[y][x - 1] - cityGridAngle[y][x]) / 2, 0),
                            new TileClassPainter(clCity),
                        },
                        Static(map, RmgenLibrary.AvoidClasses(clPath, 0)));

            List<RmgenEntity>? entitiesGates = null;
            if (placeNapataWall != null)
            {
                double wallGridMaxAngleSummand = ScaleByMapSize(0.04, 0.03) * SafeMath.PI;
                double wallGridStartAngle = gridStartAngle - wallGridMaxAngleSummand / 2;
                double wallGridRadiusFront = GridRadius(gridPointsY - 1) + pathWidth - 1;
                double wallGridMaxAngleFront = gridMaxAngle + wallGridMaxAngleSummand;
                var entitiesWalls = PlaceCircularWall(map, gridCenter, wallGridRadiusFront,
                    new[] { "tower", "short", "tower", "gate", "tower", "medium", "tower", "short" },
                    placeNapataWall, 0, wallGridStartAngle, wallGridMaxAngleFront, true, null);

                double wallGridRadiusBack = hillRadius - ScaleByMapSize(15, 25);
                double wallGridMaxAngleBack = gridMaxAngle + wallGridMaxAngleSummand;
                var wallGridPositionFront = RmgenGeometry.DistributePointsOnCircularSegment(
                    gridPointsX, wallGridMaxAngleBack, wallGridStartAngle, wallGridRadiusFront, gridCenter).points;
                var wallGridPositionBack = RmgenGeometry.DistributePointsOnCircularSegment(
                    gridPointsX, wallGridMaxAngleBack, wallGridStartAngle, wallGridRadiusBack, gridCenter).points;
                var wallGridPosition = new List<RmgenVector2D> { wallGridPositionFront[0] };
                wallGridPosition.AddRange(wallGridPositionBack);
                wallGridPosition.Add(wallGridPositionFront[^1]);
                for (int x = 1; x < wallGridPosition.Count; ++x)
                    entitiesWalls.AddRange(PlaceLinearWall(map,
                        wallGridPosition[x - 1],
                        wallGridPosition[x],
                        new[] { "tower", "gate", "tower", "short", "tower", "short", "tower" },
                        placeNapataWall,
                        0,
                        false,
                        RmgenLibrary.AvoidClasses(clHill, 0, clTemple, 0)));

                RmgenLibrary.CreateArea(
                    new EntitiesObstructionPlacer(entitiesWalls, 0, double.PositiveInfinity),
                    new TileClassPainter(clWall),
                    null);

                // 上游用库施石门后缀筛门，木栅栏分支因此得不到 gate；照搬该细节。
                entitiesGates = entitiesWalls.Where(entity =>
                    entity.TemplateName.EndsWith(oWallGate, StringComparison.Ordinal)).ToList();
                RmgenLibrary.CreateArea(
                    new EntitiesObstructionPlacer(entitiesGates, 0, double.PositiveInfinity),
                    new TileClassPainter(clGate),
                    null);

                RmgenLibrary.CreateArea(
                    new MapBoundsPlacer(),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                            heightOffsetWalls, 2, relative: true),
                        new TerrainPainter(tPathWild, rng),
                    },
                    All(
                        new NearTileClassConstraint(clWall, 1),
                        RmgenLibrary.AvoidClasses(clCliff, 0)));

                foreach (var entity in entitiesGates)
                    RmgenLibrary.CreateArea(
                        new DiskPlacer(pathWidth, entity.Position),
                        new IPainter[]
                        {
                            new LayeredPainter(new object[] { tPathWild, tPath }, new[] { 1 }, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                                heightOffsetPath, 2, relative: true),
                        },
                        All(
                            RmgenLibrary.AvoidClasses(clCliff, 0, clPath, 0, clCity, 0),
                            new NearTileClassConstraint(clPath, pathWidth + 1)));
            }

            List<RmgenVector2D> roadStartLocations;
            if (entitiesGates != null)
                roadStartLocations = RmgenCommon.ShuffleArray(rng,
                    entitiesGates.Select(entity => entity.Position).ToList());
            else
            {
                var starts = new List<RmgenVector2D>();
                starts.AddRange(cityGridPosition.Select(gridPos => gridPos[0]));
                starts.AddRange(cityGridPosition.Select(gridPos => gridPos[^1]));
                starts.AddRange(cityGridPosition[^1]);
                roadStartLocations = RmgenCommon.ShuffleArray(rng, starts);
            }

            IConstraint roadConstraint = Static(map,
                stayDesert,
                RmgenLibrary.AvoidClasses(clHill, 0, clCity, 0, clPyramid, 6, clPlayer, 16));
            var areaCityPaths = new Area(map, areasCityPaths.SelectMany(area => area.GetPoints()).ToList());
            var areaRoads = new List<Area>();
            foreach (var roadStart in roadStartLocations)
            {
                if (areaRoads.Count >= ScaleByMapSize(2, 5))
                    break;

                var closestPoint = areaCityPaths.GetClosestPointTo(roadStart);
                if (closestPoint == null)
                    continue;

                roadConstraint = Static(map, roadConstraint, RmgenLibrary.AvoidClasses(clRoad, 20));
                for (int tries = 0; tries < 30; ++tries)
                {
                    var roadOffset = new RmgenVector2D(0, 3.0 / 4 * mapSize);
                    roadOffset.Rotate(closestPoint.Value.AngleTo(roadStart));
                    var area = RmgenLibrary.CreateArea(
                        NewPath(
                            RmgenVector2D.Add(closestPoint.Value, roadOffset),
                            roadStart,
                            ScaleByMapSize(5, 3),
                            0.1, 5, 0.5, 0, 0),
                        new TileClassPainter(clRoad),
                        roadConstraint);

                    if (area != null && area.PointCount > 0)
                    {
                        areaRoads.Add(area);
                        break;
                    }
                }
            }

            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetRoad, 1, relative: true),
                    new LayeredPainter(new object[] { tPathWild, tPath }, new[] { 1 }, rng),
                },
                All(
                    RmgenLibrary.StayClasses(clRoad, 0),
                    RmgenLibrary.AvoidClasses(clPath, 0)));

            var areaRoadPalms = RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                (IPainter?)null,
                All(
                    new NearTileClassConstraint(clRoad, 1),
                    RmgenLibrary.AvoidClasses(clRoad, 0, clPath, 1, clWall, 4, clGate, 4)));
            if (areaRoadPalms != null && areaRoadPalms.PointCount > 0)
            {
                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, oPalmPath, 1, 1, 0, 0),
                    }, true, clForest),
                    0, RmgenLibrary.AvoidClasses(clForest, 2, clGate, 7),
                    ScaleByMapSize(40, 250), 20, Areas(areaRoadPalms));

                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new RandomObject(rng, aBushesCity, 1, 1, 0, 0),
                    }, true, clForest),
                    0, RmgenLibrary.AvoidClasses(clForest, 1),
                    ScaleByMapSize(40, 200), 20, Areas(areaRoadPalms));
            }

            var areaCityBushes = RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                (IPainter?)null,
                All(
                    new NearTileClassConstraint(clPath, 1),
                    RmgenLibrary.AvoidClasses(
                        clPath, 0,
                        clRoad, 0,
                        clPyramid, 20,
                        clRitualPlace, 8,
                        clTemple, 3,
                        clWall, 3,
                        clTower, 1,
                        clFortress, 1,
                        clHouse, 1,
                        clForge, 1,
                        clElephantStable, 1,
                        clStable, 1,
                        clCivicCenter, 1,
                        clBarracks, 1,
                        clBlemmyeCamp, 1,
                        clNobaCamp, 1,
                        clMarket, 1)));

            var areaCityPalms = RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                (IPainter?)null,
                All(
                    new StayAreasConstraint(Areas(areaCityBushes)),
                    RmgenLibrary.AvoidClasses(clElephantStable, 3)));

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aPalmPath, 1, 1, 0, 0),
                }, true, clForest),
                0, RmgenLibrary.AvoidClasses(clForest, 3),
                ScaleByMapSize(40, 400), 15, Areas(areaCityPalms));

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new RandomObject(rng, aBushesCity, 1, 1, 0, 0),
                }, true, clForest),
                0, RmgenLibrary.AvoidClasses(clForest, 1),
                ScaleByMapSize(20, 200), 15, Areas(areaCityBushes));

            if (placeNapataWall != null)
            {
                var areaWallPalms = RmgenLibrary.CreateArea(
                    new MapBoundsPlacer(),
                    (IPainter?)null,
                    Static(map,
                        new NearTileClassConstraint(clWall, 2),
                        RmgenLibrary.AvoidClasses(clPath, 1, clRoad, 1, clWall, 1, clGate, 3,
                            clTemple, 2, clHill, 6)));
                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, oPalmPath, 1, 1, 0, 0),
                    }, true, clForest),
                    0, RmgenLibrary.AvoidClasses(clForest, 2),
                    ScaleByMapSize(40, 250), 50, Areas(areaWallPalms));
            }

            CreateBumpsCustom(rng, map,
                Static(map, RmgenLibrary.AvoidClasses(
                    clPlayer, 6,
                    clCity, 0,
                    clWater, 2,
                    clHill, 0,
                    clPath, 0,
                    clRoad, 0,
                    clTemple, 4,
                    clPyramid, 8,
                    clWall, 0,
                    clGate, 4)),
                ScaleByMapSize(30, 300), 1, 8, 4, 0, 3);

            var nearWater = new NearTileClassConstraint(clWater, 3);
            var avoidCollisionsNomad = All(
                Static(map, RmgenLibrary.AvoidClasses(
                    clCliff, 0, clHill, 0, clPlayer, 15, clWater, 1, clPath, 2, clRitualPlace, 10,
                    clTemple, 4, clPyramid, 7, clCity, 4, clWall, 4, clGate, 8)),
                RmgenLibrary.AvoidClasses(clForest, 1, clRock, 4, clMetal, 4, clFood, 2, clSoldier, 1, clTreasure, 1));
            IConstraint avoidCollisions = All(
                avoidCollisionsNomad,
                Static(map, RmgenLibrary.AvoidClasses(clRoad, 6, clFood, 6)));

            var areaDesert = RmgenLibrary.CreateArea(new MapBoundsPlacer(), (IPainter?)null, stayDesert);
            var areaFertileLand = RmgenLibrary.CreateArea(new MapBoundsPlacer(), (IPainter?)null, stayFertileLand);

            GaiaEntities.CreateForests(rng, map,
                new object[] { tForestFloorFertile, tForestFloorFertile, tForestFloorFertile, pForestPalms, pForestPalms },
                All(
                    stayFertileLand,
                    RmgenLibrary.AvoidClasses(clForest, 15),
                    Static(map, RmgenLibrary.AvoidClasses(clWater, 2), avoidCollisions)),
                clForest,
                ScaleByMapSize(250, 2000),
                NumPlayers);

            var avoidCollisionsMines = new IConstraint[]
            {
                RmgenLibrary.AvoidClasses(clRock, 10, clMetal, 10),
                Static(map, RmgenLibrary.AvoidClasses(
                    clWater, 4, clCliff, 4, clCity, 4, clRitualPlace, 10, clPlayer, 20, clForest, 4,
                    clPyramid, 6, clTemple, 4, clPath, 4, clRoad, 4, clGate, 8)),
            };
            var mineObjectsPerBiome = new[]
            {
                new MineObjectsPerBiome(
                    MineObjects(rng, oMetalSmallDesert, oMetalLargeDesert),
                    MineObjects(rng, oMetalSmallFertileLand, oMetalLargeFertileLand),
                    clMetal),
                new MineObjectsPerBiome(
                    MineObjects(rng, oStoneSmallDesert, oStoneLargeDesert),
                    MineObjects(rng, oStoneSmallFertileLand, oStoneLargeFertileLand),
                    clRock),
            };

            for (int i = 0; i < ScaleByMapSize(6, 22); ++i)
            {
                var mineObjectsBiome = rng.PickRandom(mineObjectsPerBiome);
                foreach (var objects in new[] { mineObjectsBiome.Desert.Large, mineObjectsBiome.Desert.Small })
                    RmgenLibrary.CreateObjectGroupsByAreas(rng,
                        new ObjectGroup(objects, true, mineObjectsBiome.TileClass),
                        0,
                        All(avoidCollisionsMines.Concat(new[]
                        {
                            RmgenLibrary.AvoidClasses(clFertileLand, 12, mineObjectsBiome.TileClass, 15),
                        })),
                        1, 60, Areas(areaDesert));
            }

            double fertileMineCount = settings.Nomad ? ScaleByMapSize(6, 16) : ScaleByMapSize(0, 8);
            for (int i = 0; i < fertileMineCount; ++i)
            {
                var mineObjectsBiome = rng.PickRandom(mineObjectsPerBiome);
                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(mineObjectsBiome.FertileLand.Small, true, mineObjectsBiome.TileClass),
                    0,
                    All(avoidCollisionsMines.Concat(new[]
                    {
                        RmgenLibrary.AvoidClasses(clDesert, 5, clMetal, 15, clRock, 15,
                            mineObjectsBiome.TileClass, 20),
                    })),
                    1, 80, Areas(areaFertileLand));
            }

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oTriggerPointAttackerPatrol, 1, 1, 0, 0),
                }, true, clTriggerPointMap),
                0,
                RmgenLibrary.AvoidClasses(
                    clCity, 8,
                    clCliff, 4,
                    clHill, 4,
                    clWater, 0,
                    clWall, 2,
                    clForest, 1,
                    clRock, 4,
                    clMetal, 4,
                    clTriggerPointMap, 15),
                ScaleByMapSize(20, 100), 30);

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oBerryBushGrapes, 4, 6, 1, 2),
                }, true, clFood),
                0, avoidCollisions, ScaleByMapSize(3, 15), 50, Areas(areaFertileLand));

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oRhino, 1, 1, 0, 1),
                }, true, clFood),
                0, avoidCollisions, ScaleByMapSize(2, 10), 50, Areas(areaDesert));

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oWarthog, 1, 1, 0, 1),
                }, true, clFood),
                0, avoidCollisions, ScaleByMapSize(2, 10), 50, Areas(areaFertileLand));

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oGiraffe, 2, 3, 2, 4),
                    new ScatterObject(rng, oGiraffeInfant, 2, 3, 2, 4),
                }, true, clFood),
                0, avoidCollisions, ScaleByMapSize(2, 10), 50);

            // 上游 createObjectGroups 的多余 areas 实参会被 JS 忽略；这里同样全图尝试。
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oGazelle, 5, 7, 2, 4),
                }, true, clFood),
                0, avoidCollisions, ScaleByMapSize(2, 10), 50);

            if (!settings.Nomad)
                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, oLion, 1, 2, 2, 4),
                        new ScatterObject(rng, oLioness, 2, 3, 2, 4),
                    }, true, clFood),
                    0,
                    All(avoidCollisions, RmgenLibrary.AvoidClasses(clPlayer, 20)),
                    ScaleByMapSize(2, 10), 50, Areas(areaDesert));

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oElephant, 2, 3, 2, 4),
                    new ScatterObject(rng, oElephantInfant, 2, 3, 2, 4),
                }, true, clFood),
                0, avoidCollisions, ScaleByMapSize(2, 10), 50, Areas(areaDesert));

            if (!settings.Nomad)
                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, oCrocodile, 2, 3, 3, 5),
                    }, true, clFood),
                    0, All(nearWater, avoidCollisions),
                    ScaleByMapSize(1, 6), 50, Areas(areaFertileLand));

            var areaIrrigationCanalTrees = RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                (IPainter?)null,
                All(
                    nearWater,
                    RmgenLibrary.AvoidClasses(clPassage, 3),
                    avoidCollisions));

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new RandomObject(rng, oPalms, 1, 1, 1, 1),
                }, true, clForest),
                0, RmgenLibrary.AvoidClasses(clForest, 1),
                ScaleByMapSize(100, 600), 50, Areas(areaIrrigationCanalTrees));

            GaiaEntities.CreateStragglerTrees(rng, oPalms,
                All(stayFertileLand, avoidCollisions), clForest, ScaleByMapSize(50, 400), 200);
            GaiaEntities.CreateStragglerTrees(rng, new[] { oAcacia },
                All(stayDesert, avoidCollisions), clForest, ScaleByMapSize(50, 400), 200);

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new RandomObject(rng, new[] { oKushCitizenArcher, oKushChampionArcher },
                        ScaleByMapSize(4, 10), ScaleByMapSize(6, 20), 1, 4),
                }, true, clSoldier),
                0,
                Static(map, RmgenLibrary.AvoidClasses(clCliff, 1), new NearTileClassConstraint(clCliff, 5)),
                ScaleByMapSize(1, 5) / 3 * difficulty, 250, Areas(areaHilltop));

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new RandomObject(rng, new[] { oKushCitizenArcher, oKushChampionArcher }, 1, 1, 1, 3),
                }, true, clSoldier),
                0,
                Static(map,
                    new HeightConstraint(map, heightHillArchers, heightHilltop),
                    RmgenLibrary.AvoidClasses(clCliff, 1, clSoldier, 1),
                    new NearTileClassConstraint(clCliff, 5)),
                ScaleByMapSize(8, 100) / 3 * difficulty, 250, Areas(areaHill));

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new RandomObject(rng, oPtolSiege, 1, 1, 1, 3),
                }, true, clSoldier),
                0,
                Static(map,
                    new NearTileClassConstraint(clCliff, 5),
                    RmgenLibrary.AvoidClasses(clCliff, 1, clSoldier, 1)),
                ScaleByMapSize(1, 6) / 3 * difficulty, 250, Areas(areaHilltop));

            var avoidCollisionsPyramids = Static(map,
                avoidCollisions,
                new NearTileClassConstraint(clPyramid, 10));
            if (!settings.Nomad)
            {
                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, oKushCitizenArcher, 1, 1, 1, 1),
                    }, true, clSoldier),
                    0, avoidCollisionsPyramids, ScaleByMapSize(3, 8), 250, Areas(areaPyramids));

                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new RandomObject(rng, oTreasuresHill, 1, 1, 2, 2),
                    }, true, clTreasure),
                    0, avoidCollisionsPyramids, ScaleByMapSize(1, 10), 250, Areas(areaPyramids));
            }

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new RandomObject(rng, oTreasuresHill, 1, 1, 2, 2),
                }, true, clTreasure),
                0, RmgenLibrary.AvoidClasses(clCliff, 1, clTreasure, 1),
                ScaleByMapSize(8, 35), 250, Areas(areaHilltop));

            var pathBorderConstraint = All(
                Static(map, new NearTileClassConstraint(clCity, 1)),
                RmgenLibrary.AvoidClasses(clTreasure, 2, clStatue, 10, clPathStatues, 4, clWall, 2, clForest, 1));
            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new RandomObject(rng, oTreasuresCity, 1, 1, 0, 2),
                }, true, clTreasure),
                0, pathBorderConstraint, ScaleByMapSize(2, 60), 500, Areas(areaPaths));

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aHandcart, 1, 1, 1, 1),
                }, true, clDecorative),
                0, All(pathBorderConstraint, RmgenLibrary.AvoidClasses(clDecorative, 10)),
                ScaleByMapSize(0, 5), 250, Areas(areaPaths));

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aPlotFence, 1, 1, 1, 1),
                }, true, clDecorative),
                0,
                Static(map, avoidCollisions, RmgenLibrary.AvoidClasses(clWater, 6, clDecorative, 10)),
                ScaleByMapSize(1, 10), 250, Areas(areaFertileLand));

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oFish, 3, 4, 2, 3),
                }, true, clFood),
                0,
                All(Static(map, RmgenLibrary.StayClasses(clWater, 6)), RmgenLibrary.AvoidClasses(clFood, 12)),
                ScaleByMapSize(20, 120), 50);

            avoidCollisions = Static(map, avoidCollisions);
            GaiaEntities.CreateDecoration(rng,
                aBushesDesert.Select(bush => (IReadOnlyList<IGroupElement>)new IGroupElement[]
                {
                    new ScatterObject(rng, bush, 0, 3, 2, 4),
                }).ToList(),
                aBushesDesert.Select(_ => ScaleByMapSize(20, 120) * rng.RandIntInclusive(1, 3)).ToList(),
                All(stayDesert, avoidCollisions));

            GaiaEntities.CreateDecoration(rng,
                aBushesFertileLand.Select(bush => (IReadOnlyList<IGroupElement>)new IGroupElement[]
                {
                    new ScatterObject(rng, bush, 0, 4, 2, 4),
                }).ToList(),
                aBushesFertileLand.Select(_ => ScaleByMapSize(20, 120) * rng.RandIntInclusive(1, 3)).ToList(),
                All(stayFertileLand, avoidCollisions));

            GaiaEntities.CreateDecoration(rng,
                new IReadOnlyList<IGroupElement>[]
                {
                    new IGroupElement[] { new ScatterObject(rng, aRock, 0, 4, 2, 4) },
                },
                new[] { ScaleByMapSize(80, 500) },
                All(stayDesert, avoidCollisions));

            GaiaEntities.CreateDecoration(rng,
                aBushesFertileLand.Select(bush => (IReadOnlyList<IGroupElement>)new IGroupElement[]
                {
                    new ScatterObject(rng, bush, 0, 3, 2, 4),
                }).ToList(),
                aBushesFertileLand.Select(_ => ScaleByMapSize(100, 800)).ToList(),
                All(new HeightConstraint(map, heightWaterLevel, heightShoreline), avoidCollisions));

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new RandomObject(rng, aWaterDecoratives, 2, 4, 1, 2),
                }, true),
                0,
                Static(map, new NearTileClassConstraint(clFertileLand, 4)),
                ScaleByMapSize(50, 400), 20, Areas(areaWater));

            foreach (var area in areasPassages)
                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new RandomObject(rng, aWaterDecoratives, 2, 4, 1, 2),
                    }, true),
                    0, null, 15, 20, Areas(area));

            for (int i = 0; i < ScaleByMapSize(0, 2); ++i)
                map.PlaceEntityAnywhere(oHawk, 0, mapCenter, rng.RandomAngle());

            return map.MakeExportable();

            void LoadJebelHeightmap()
            {
                string? path = settings.DataRoot != null
                    ? Path.Combine(settings.DataRoot, "maps", "random", "jebel_barkal.pmp")
                    : null;

                float[][] heightmap;
                if (path != null && File.Exists(path))
                {
                    try
                    {
                        heightmap = HeightmapLoader.ConvertHeightmap1Dto2D(LoadPmpHeight(path));
                    }
                    catch (Exception)
                    {
                        // PMP 缺失或解码失败时用确定性合成高度图，保证无资源环境也能生成。
                        heightmap = FallbackJebelHeightmap();
                    }
                }
                else
                {
                    // PMP 缺失或解码失败时用确定性合成高度图，保证无资源环境也能生成。
                    heightmap = FallbackJebelHeightmap();
                }

                var translated = TranslateHeightmap(
                    new RmgenVector2D(-12, ScaleByMapSize(-12, -25)),
                    heightmap);
                RmgenLibrary.CreateArea(
                    new MapBoundsPlacer(),
                    new HeightmapPainter(map, translated, minHeightSource, maxHeightSource),
                    null);
            }
        }

        private static List<TempleLayout> BuildTempleLayout(int mapSize, Func<double, double, double> scaleByMapSize)
        {
            var all = new List<TempleLayout>
            {
                new(oTempleApedemak, new RmgenVector2D(0, 9), 320),
                new(oTempleApedemak, new RmgenVector2D(0, 9), 0),
                new(oTempleAmun, new RmgenVector2D(0, 12), 256),
                new(oWonderPtol, new RmgenVector2D(0, scaleByMapSize(9, 14)), 0),
                new(oTempleAmun, new RmgenVector2D(0, 12), 256),
                new(oTempleApedemak, new RmgenVector2D(0, 9), 320),
                new(oTempleApedemak, new RmgenVector2D(0, 9), 0),
            };
            return all.Where(temple => mapSize >= temple.MinMapSize).ToList();
        }

        private static List<CityPainter.CityTemplate> BuildKushCity(int difficulty, string oTower,
            TileClass clHouse, TileClass clFortress, TileClass clPath, TileClass clCivicCenter,
            TileClass clElephantStable, TileClass clStable, TileClass clBarracks, TileClass clTower,
            TileClass clMarket, TileClass clForge, TileClass clNobaCamp, TileClass clBlemmyeCamp)
        {
            var result = new List<CityPainter.CityTemplate>();
            Add("uncapturable|" + oHouse, "Very Easy", null, clHouse);
            Add(oFortress, "Medium",
                All(RmgenLibrary.AvoidClasses(clFortress, 25), new NearTileClassConstraint(clPath, 8)),
                clFortress);
            Add(oCivicCenter, "Easy",
                All(RmgenLibrary.AvoidClasses(clCivicCenter, 60), new NearTileClassConstraint(clPath, 8)),
                clCivicCenter);
            Add(oElephantStable, "Easy", RmgenLibrary.AvoidClasses(clElephantStable, 10), clElephantStable);
            Add(oStable, "Easy", RmgenLibrary.AvoidClasses(clStable, 20), clStable);
            Add(oBarracks, "Easy", RmgenLibrary.AvoidClasses(clBarracks, 12), clBarracks);
            Add(oTower, "Easy", RmgenLibrary.AvoidClasses(clTower, 17), clTower);
            Add("uncapturable|" + oMarket, "Very Easy", RmgenLibrary.AvoidClasses(clMarket, 15), clMarket);
            Add("uncapturable|" + oForge, "Very Easy", RmgenLibrary.AvoidClasses(clForge, 30), clForge);
            Add(oNobaCamp, "Easy", RmgenLibrary.AvoidClasses(clNobaCamp, 30), clNobaCamp);
            Add(oBlemmyeCamp, "Easy", RmgenLibrary.AvoidClasses(clBlemmyeCamp, 30), clBlemmyeCamp);
            return result;

            void Add(string templateName, string difficultyName, IConstraint? constraint, TileClass tileClass)
            {
                if (difficulty < DifficultyByName(difficultyName))
                    return;
                result.Add(new CityPainter.CityTemplate
                {
                    TemplateName = templateName,
                    Constraint = constraint,
                    Painter = new TileClassPainter(tileClass),
                });
            }
        }

        private static int GetDifficulty(MapSettings settings)
        {
            _ = settings;
            return 3;
        }

        private static int DifficultyByName(string name)
            => name switch
            {
                "Very Easy" => 1,
                "Easy" => 2,
                "Medium" => 3,
                "Hard" => 4,
                "Very Hard" => 5,
                _ => 3,
            };

        private static void CreateBumpsCustom(RmgenRng rng, RandomMap map, IConstraint constraint,
            double count, double minSize, double maxSize, double spread, double failFraction, double elevation)
            => RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, minSize, maxSize, spread, failFraction),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        elevation, 2, relative: true),
                },
                constraint, count);

        private static void PaintRiverExt(RmgenRng rng, RandomMap map,
            RmgenVector2D start, RmgenVector2D end, double width, double fadeDist,
            double heightRiverbed, double heightLand, bool parallel, double deviation,
            double meanderShort, double meanderLong,
            Action<RmgenVector2D, double, double>? waterFunc = null,
            Action<RmgenVector2D, double, double>? landFunc = null,
            IConstraint? constraint = null, double? minHeight = null)
        {
            int mapSize = map.GetSize();
            double meanderShortT = RmgenLibrary.FractionToTiles(
                meanderShort / RmgenLibrary.ScaleByMapSize(35, 160, mapSize), mapSize);
            double meanderLongT = RmgenLibrary.FractionToTiles(
                meanderLong / RmgenLibrary.ScaleByMapSize(35, 100, mapSize), mapSize);

            double seed1 = rng.RandFloat(2, 3);
            double seed2 = rng.RandFloat(2, 3);
            double startingAngle1 = rng.RandFloat(0, 1);
            double startingAngle2 = rng.RandFloat(0, 1);

            double RiverCurve(double riverFraction, double startAngle, double seed) =>
                meanderShortT * RndRiver(startAngle + RmgenLibrary.FractionToTiles(riverFraction, mapSize) / 128, seed) +
                meanderLongT * RndRiver(startAngle + RmgenLibrary.FractionToTiles(riverFraction, mapSize) / 256, seed);

            double riverLength = start.DistanceTo(end);
            var unitVecRiver = RmgenVector2D.Sub(start, end);
            unitVecRiver.Normalize();
            var unitVecPerpendicular = unitVecRiver.Perpendicular();

            double riverMinX = Math.Min(start.X, end.X);
            double riverMinZ = Math.Min(start.Y, end.Y);
            double riverMaxX = Math.Max(start.X, end.X);
            double riverMaxZ = Math.Max(start.Y, end.Y);
            var effectiveConstraint = constraint ?? new NullConstraint();

            for (int ix = 0; ix < mapSize; ++ix)
                for (int iz = 0; iz < mapSize; ++iz)
                {
                    var vecPoint = new RmgenVector2D(ix, iz);
                    if (!effectiveConstraint.Allows(vecPoint))
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
                            height += (heightLand - heightRiverbed) * (1 + shoreDist1 / fadeDist);
                        else if (shoreDist2 < fadeDist)
                            height += (heightLand - heightRiverbed) * (1 - shoreDist2 / fadeDist);

                        if (minHeight == null || height < minHeight.Value)
                            map.SetHeight(vecPoint, height);
                        waterFunc?.Invoke(vecPoint, height, riverFraction);
                    }
                    else
                    {
                        landFunc?.Invoke(vecPoint, shoreDist1, shoreDist2);
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

        private PathPlacer NewPath(RmgenVector2D start, RmgenVector2D end, double width,
            double waviness, double smoothness, double offset, double tapering, double failFraction)
            => new(Rng, waviness, smoothness, offset, tapering, failFraction)
            {
                Start = start,
                End = end,
                Width = width,
            };

        private static RmgenVector2D RotateAround(RmgenVector2D value, double angle, RmgenVector2D center)
        {
            value.RotateAround(angle, center);
            return value;
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

        private static void AddRounded(TileClass tileClass, RmgenVector2D position)
        {
            position.Round();
            tileClass.Add(position);
        }

        private static void AddArea(List<Area> areas, Area? area)
        {
            if (area != null)
                areas.Add(area);
        }

        private static List<Area> Areas(params Area?[] areas)
            => areas.Where(area => area != null).Select(area => area!).ToList();

        private static IConstraint All(params IConstraint[] constraints)
            => new AndConstraint(constraints);

        private static IConstraint All(IEnumerable<IConstraint> constraints)
            => new AndConstraint(constraints);

        private static IConstraint Static(RandomMap map, params IConstraint[] constraints)
            => new StaticConstraint(map, constraints);

        private static MineObjectSet MineObjects(RmgenRng rng, string templateSmall, string templateLarge)
            => new(
                new List<IGroupElement>
                {
                    new ScatterObject(rng, templateSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, templateLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                },
                new List<IGroupElement>
                {
                    new ScatterObject(rng, templateSmall, 3, 4, 1, 3, 0, 2 * SafeMath.PI, 1),
                });

        private static float[][] TranslateHeightmap(RmgenVector2D offset, float[][] heightmap)
        {
            offset.Round();
            double defaultHeight = double.PositiveInfinity;
            for (int x = 0; x < heightmap.Length; ++x)
                for (int y = 0; y < heightmap[x].Length; ++y)
                    defaultHeight = Math.Min(defaultHeight, heightmap[x][y]);

            var source = new float[heightmap.Length][];
            for (int x = 0; x < heightmap.Length; ++x)
                source[x] = (float[])heightmap[x].Clone();

            for (int x = 0; x < heightmap.Length; ++x)
                for (int y = 0; y < heightmap[x].Length; ++y)
                {
                    int sx = x + (int)offset.X;
                    int sy = y + (int)offset.Y;
                    heightmap[x][y] = sx >= 0 && sx < source.Length &&
                        sy >= 0 && sy < source[sx].Length ?
                            source[sx][sy] : (float)defaultHeight;
                }

            return heightmap;
        }

        private static ushort[] LoadPmpHeight(string path)
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs);
            uint magic = reader.ReadUInt32();
            if (magic != pmpMagic)
                throw new InvalidDataException("PMP magic");

            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            int patchesPerSide = reader.ReadInt32();
            int verticesPerSide = patchesPerSide * pmpPatchSize + 1;
            int heightmapSize = verticesPerSide * verticesPerSide;
            byte[] raw = reader.ReadBytes(heightmapSize * 2);
            if (raw.Length != heightmapSize * 2)
                throw new EndOfStreamException();

            var heightmap = new ushort[heightmapSize];
            for (int i = 0; i < heightmapSize; ++i)
                heightmap[i] = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));
            return heightmap;
        }

        private static float[][] FallbackJebelHeightmap()
        {
            const int n = 321;
            var hm = new float[n][];
            for (int x = 0; x < n; ++x)
            {
                hm[x] = new float[n];
                for (int y = 0; y < n; ++y)
                {
                    double nx = x / (double)(n - 1);
                    double ny = y / (double)(n - 1);
                    double dx = nx - 0.18;
                    double dy = ny - 0.78;
                    double radial = SafeMath.Sqrt(dx * dx + dy * dy) / 0.34;
                    double hill = Math.Max(0, 1 - radial);
                    double river = Math.Max(0, 1 - Math.Abs(ny - 0.12) / 0.18);
                    double value = 0.16 + 0.78 * hill * hill - 0.08 * river;
                    hm[x][y] = (float)(Math.Max(0, Math.Min(1, value)) * 0xFFFF);
                }
            }
            return hm;
        }

        private static List<RmgenEntity> PlaceCircularWall(RandomMap map, RmgenVector2D center,
            double radius, IReadOnlyList<string> wallPart, string style, int playerId,
            double orientation, double maxAngle, bool endWithFirst, IConstraint? constraints)
        {
            var totalLength = maxAngle * radius;
            double wallPartLength = GetWallLength(style, wallPart);
            int numParts = (int)Math.Ceiling(totalLength / wallPartLength);
            if (endWithFirst)
                numParts = (int)Math.Ceiling((totalLength - GetWallElement(style, wallPart[0]).Length) / wallPartLength);

            double scaleFactor = totalLength / (numParts * wallPartLength);
            if (endWithFirst)
                scaleFactor = totalLength / (numParts * wallPartLength + GetWallElement(style, wallPart[0]).Length);

            var entities = new List<RmgenEntity>();
            var constraint = constraints != null ? Static(map, constraints) : (IConstraint)new NullConstraint();
            double actualAngle = orientation;
            var position = RmgenVector2D.Add(center, Rotated(new RmgenVector2D(radius, 0), -actualAngle));

            for (int partIndex = 0; partIndex < numParts; ++partIndex)
                foreach (string wallName in wallPart)
                {
                    var wallEle = GetWallElement(style, wallName);
                    double addAngle = scaleFactor * (wallEle.Length - wallOverlap) / radius;
                    var target = RmgenVector2D.Add(center,
                        Rotated(new RmgenVector2D(radius, 0), -actualAngle - addAngle));
                    var place = Average(new[] { position, target });
                    double placeAngle = actualAngle + addAngle / 2;
                    place.Sub(Rotated(new RmgenVector2D(wallEle.Indent, 0), -placeAngle));

                    if (wallEle.TemplateName != null && map.InMapBounds(place) &&
                        constraint.Allows(Floored(place)))
                    {
                        var entity = map.PlaceEntityPassable(wallEle.TemplateName, playerId,
                            place, placeAngle + wallEle.Angle);
                        if (entity != null)
                            entities.Add(entity);
                    }

                    actualAngle += addAngle;
                    position = RmgenVector2D.Add(center, Rotated(new RmgenVector2D(radius, 0), -actualAngle));
                }

            if (endWithFirst)
            {
                var wallEle = GetWallElement(style, wallPart[0]);
                double addAngle = scaleFactor * wallEle.Length / radius;
                var target = RmgenVector2D.Add(center,
                    Rotated(new RmgenVector2D(radius, 0), -actualAngle - addAngle));
                var place = Average(new[] { position, target });
                double placeAngle = actualAngle + addAngle / 2;
                if (wallEle.TemplateName != null && map.InMapBounds(place) && constraint.Allows(Floored(place)))
                {
                    var entity = map.PlaceEntityPassable(wallEle.TemplateName, playerId,
                        place, placeAngle + wallEle.Angle);
                    if (entity != null)
                        entities.Add(entity);
                }
            }

            return entities;
        }

        private static List<RmgenEntity> PlaceLinearWall(RandomMap map,
            RmgenVector2D startPosition, RmgenVector2D targetPosition, IReadOnlyList<string> wallPart,
            string style, int playerId, bool endWithFirst, IConstraint? constraints)
        {
            double totalLength = startPosition.DistanceTo(targetPosition);
            double wallPartLength = GetWallLength(style, wallPart);
            int numParts = (int)Math.Ceiling(totalLength / wallPartLength);
            if (endWithFirst)
                numParts = (int)Math.Ceiling((totalLength - GetWallElement(style, wallPart[0]).Length) / wallPartLength);

            double scaleFactor = totalLength / (numParts * wallPartLength);
            if (endWithFirst)
                scaleFactor = totalLength / (numParts * wallPartLength + GetWallElement(style, wallPart[0]).Length);

            double wallAngle = SafeMath.Atan2(targetPosition.Y - startPosition.Y, targetPosition.X - startPosition.X);
            double placeAngle = wallAngle - SafeMath.PI / 2;
            var entities = new List<RmgenEntity>();
            var position = startPosition;
            var constraint = constraints != null ? Static(map, constraints) : (IConstraint)new NullConstraint();

            for (int partIndex = 0; partIndex < numParts; ++partIndex)
                foreach (string wallName in wallPart)
                {
                    var wallEle = GetWallElement(style, wallName);
                    double wallLength = (wallEle.Length - wallOverlap) / 2;
                    var dist = Rotated(new RmgenVector2D(scaleFactor * wallLength, 0), -wallAngle);
                    position.Add(dist);
                    var place = RmgenVector2D.Add(position,
                        Rotated(new RmgenVector2D(0, wallEle.Indent), -wallAngle));

                    if (wallEle.TemplateName != null && map.InMapBounds(place) &&
                        constraint.Allows(Floored(place)))
                    {
                        var entity = map.PlaceEntityPassable(wallEle.TemplateName, playerId,
                            place, placeAngle + wallEle.Angle);
                        if (entity != null)
                            entities.Add(entity);
                    }

                    position.Add(dist);
                }

            if (endWithFirst)
            {
                var wallEle = GetWallElement(style, wallPart[0]);
                double wallLength = (wallEle.Length - wallOverlap) / 2;
                position.Add(Rotated(new RmgenVector2D(scaleFactor * wallLength, 0), -wallAngle));
                if (wallEle.TemplateName != null && map.InMapBounds(position) &&
                    constraint.Allows(Floored(position)))
                {
                    var entity = map.PlaceEntityPassable(wallEle.TemplateName, playerId,
                        position, placeAngle + wallEle.Angle);
                    if (entity != null)
                        entities.Add(entity);
                }
            }

            return entities;
        }

        private static WallElement GetWallElement(string style, string element)
        {
            bool palisade = style == "napata_palisade";
            return element switch
            {
                "short" or "medium" => palisade
                    ? new WallElement("uncapturable|" + oPalisadeMedium, SafeMath.PI, 9.0 / RmgenConstants.TERRAIN_TILE_SIZE, 0)
                    : new WallElement("uncapturable|" + oWallMedium, SafeMath.PI, 24.0 / RmgenConstants.TERRAIN_TILE_SIZE, 0),
                "tower" => palisade
                    ? new WallElement("uncapturable|" + oPalisadeTower, SafeMath.PI, 3.0 / RmgenConstants.TERRAIN_TILE_SIZE, 0)
                    : new WallElement("uncapturable|" + oWallTower, SafeMath.PI, 8.0 / RmgenConstants.TERRAIN_TILE_SIZE, 0),
                "gate" => palisade
                    ? new WallElement("uncapturable|" + oPalisadeGate, SafeMath.PI, 14.0 / RmgenConstants.TERRAIN_TILE_SIZE, 0)
                    : new WallElement("uncapturable|" + oWallGate, SafeMath.PI, 36.0 / RmgenConstants.TERRAIN_TILE_SIZE, 0),
                _ => new WallElement(null, 0, 0, 0),
            };
        }

        private static double GetWallLength(string style, IReadOnlyList<string> wall)
        {
            double length = 0;
            foreach (string element in wall)
                length += GetWallElement(style, element).Length - wallOverlap;
            return length;
        }

        private static RmgenVector2D Rotated(RmgenVector2D value, double angle)
        {
            value.Rotate(angle);
            return value;
        }

        private static RmgenVector2D Floored(RmgenVector2D value)
        {
            value.Floor();
            return value;
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

        private readonly record struct FertileTexture(double Left, double Right, ITerrain Terrain, TileClass TileClass);
        private readonly record struct TempleLayout(string Template, RmgenVector2D PathOffset, int MinMapSize);
        private readonly record struct MineObjectSet(List<IGroupElement> Large, List<IGroupElement> Small);
        private readonly record struct MineObjectsPerBiome(MineObjectSet Desert, MineObjectSet FertileLand, TileClass TileClass);
        private readonly record struct WallElement(string? TemplateName, double Angle, double Length, double Indent);
    }

    /// <summary>new_rms_test.js（逐字移植）——最小测试图：春草平地 + 环形玩家出生点。
    /// 上游末尾 placePlayersNomad 按本仓既有约定不移植。</summary>
    public sealed class NewRmsTestMap2 : StandardMap
    {
        protected override double HeightLand => 0;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, "grass1_spring");
            var map = Map;

            if (!settings.Nomad)
            {
                var placement = RmgenCommon.PlayerPlacementCircle(rng, map, NumPlayers,
                    RmgenLibrary.FractionToTiles(0.39, MapSize));
                RmgenCommon.PlacePlayerBases(rng, map, settings, "grass1_spring",
                    new TileClass(MapSize), null, placement.playerPosition, playerIDs: placement.playerIDs);
            }

            return map.MakeExportable();
        }
    }
}
