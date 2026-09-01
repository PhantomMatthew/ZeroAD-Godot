using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>danubius.js（逐字移植，1048 行）——斜穿全图的大河、河中岛链、
    /// 两岸高卢村寨/祭祀点以及触发器用巡逻/登陆/船只生成点。环境设置、
    /// placePlayersNomad 按既有移植约定省略；开头 day 抽数仍保留以匹配生成顺序。</summary>
    public sealed class DanubiusMap2 : StandardMap
    {
        private TileClass ClFood = null!;

        private const double BuildingOrientation = -SafeMath.PI / 4;
        private const int SmallMapSize = 192;
        private const int MediumMapSize = 256;
        private const int NormalMapSize = 320;
        private const double ShorelineDistance = 15;
        private const double HeightSeaGround = -3;
        private const double HeightShore = 1;
        private const double HeightLandConst = 3;
        private const double HeightPath = 1.5;
        private const double HeightIsland = 6;

        private const string TriggerPointShipSpawn = "trigger/trigger_point_A";
        private const string TriggerPointShipPatrol = "trigger/trigger_point_B";
        private const string TriggerPointShipUnloadLeft = "trigger/trigger_point_C";
        private const string TriggerPointShipUnloadRight = "trigger/trigger_point_D";
        private const string TriggerPointLandPatrolLeft = "trigger/trigger_point_E";
        private const string TriggerPointLandPatrolRight = "trigger/trigger_point_F";
        private const string TriggerPointCCAttackerPatrolLeft = "trigger/trigger_point_G";
        private const string TriggerPointCCAttackerPatrolRight = "trigger/trigger_point_H";
        private const string TriggerPointRiverDirection = "trigger/trigger_point_I";

        private const string TRoad = "steppe_river_rocks";
        private const string TCliff = "temp_cliff_a";
        private const string TForestFloor = "temp_forestfloor_aut";
        private const string TGrass = "medit_shrubs_golden";
        private const string TGrass2 = "grass_mediterranean_dry_1024test";
        private const string TGrass3 = "medit_grass_field_b";
        private const string TShore = "temp_dirt_gravel_b";
        private const string TWater = "steppe_river_rocks_wet";
        private const string TSeaDepths = "medit_sea_depths";

        private const string OBerryBush = "gaia/fruit/berry_01";
        private const string ODeer = "gaia/fauna_deer";
        private const string OFish = "gaia/fish/generic";
        private const string OSheep = "gaia/fauna_sheep";
        private const string OGoat = "gaia/fauna_goat";
        private const string OWolf = "gaia/fauna_wolf";
        private const string OHawk = "birds/buzzard";
        private const string ORabbit = "gaia/fauna_rabbit";
        private const string OBoar = "gaia/fauna_boar";
        private const string OBear = "gaia/fauna_bear_brown";
        private const string OStoneLarge = "gaia/rock/temperate_large";
        private const string OStoneRuins = "gaia/ruins/standing_stone";
        private const string OMetalLarge = "gaia/ore/mediterranean_large";
        private const string OApple = "gaia/fruit/apple";
        private const string OAcacia = "gaia/tree/acacia";
        private const string OOak = "gaia/tree/oak_aut";
        private const string OOak2 = "gaia/tree/oak_aut_new";
        private const string OOak3 = "gaia/tree/oak_dead";
        private const string OOak4 = "gaia/tree/oak";
        private const string OPopolar = "gaia/tree/poplar_lombardy";
        private const string OBeech = "gaia/tree/euro_beech_aut";
        private const string OBeech2 = "gaia/tree/euro_beech";

        private const string OCivicCenter = "structures/gaul/civil_centre";
        private const string OTower = "structures/gaul/defense_tower";
        private const string OOutpost = "structures/gaul/outpost";
        private const string OTemple = "uncapturable|structures/gaul/temple";
        private const string OTavern = "uncapturable|structures/gaul/tavern";
        private const string OHouse = "uncapturable|structures/gaul/house";
        private const string OLongHouse = "uncapturable|structures/celt_longhouse";
        private const string OHut = "uncapturable|structures/celt_hut";
        private const string OSentryTower = "uncapturable|structures/gaul/sentry_tower";
        private const string OWatchTower = "uncapturable|structures/palisades_watchtower";
        private const string OPalisadeTallSpikes = "uncapturable|structures/palisades_spikes_tall";
        private const string OPalisadeAngleSpikes = "uncapturable|structures/palisades_spike_angle";
        private const string OPalisadeCurve = "uncapturable|structures/palisades_curve";
        private const string OPalisadeShort = "uncapturable|structures/palisades_short";
        private const string OPalisadeMedium = "uncapturable|structures/palisades_medium";
        private const string OPalisadeLong = "uncapturable|structures/palisades_long";
        private const string OPalisadeGate = "uncapturable|structures/palisades_gate";
        private const string OPalisadePillar = "uncapturable|structures/palisades_tower";

        private const string OCivilian = "units/gaul/support_civilian";
        private const string OHealer = "units/gaul/support_healer_b";
        private const string OSkirmisher = "units/gaul/infantry_javelineer_b";
        private const string ONakedFanatic = "units/gaul/champion_fanatic";

        private const string ABush1 = "actor|props/flora/bush_tempe_sm.xml";
        private const string ABush2 = "actor|props/flora/bush_tempe_me.xml";
        private const string ABush3 = "actor|props/flora/bush_tempe_la.xml";
        private const string ABush4 = "actor|props/flora/bush_tempe_me.xml";
        private const string ARock1 = "actor|geology/stone_granite_med.xml";
        private const string ARock2 = "actor|geology/stone_granite_boulder.xml";
        private const string ARock3 = "actor|geology/stone_granite_greek_boulder.xml";
        private const string ARock4 = "actor|geology/stonemine_alpine_a.xml";
        private const string AFerns = "actor|props/flora/ferns.xml";
        private const string ABucket = "actor|props/structures/celts/blacksmith_bucket";
        private const string ABarrel = "actor|props/structures/gauls/storehouse_barrel_b";
        private const string ATartan = "actor|props/structures/celts/tartan_a";
        private const string AWheel = "actor|props/special/eyecandy/wheel_laying";
        private const string AWell = "actor|props/special/eyecandy/well_1_b";
        private const string AWoodcord = "actor|props/special/eyecandy/woodcord";
        private const string AWaterLog = "actor|props/flora/water_log.xml";
        private const string ACampfire = "actor|props/special/eyecandy/campfire";
        private const string ABench = "actor|props/special/eyecandy/bench_1";
        private const string ARug = "actor|props/special/eyecandy/rug_stand_iber";

        private static readonly string[] TPrimary =
        {
            "temp_grass_aut", "temp_grass_plants_aut", "temp_grass_c_aut", "temp_grass_d_aut",
        };

        private static readonly string[] TIsland =
        {
            "temp_grass_long_b_aut", "temp_grass_plants_aut", "temp_forestfloor_aut",
        };

        private static readonly string[] OTreasures =
        {
            "gaia/treasure/food_barrel", "gaia/treasure/food_bin", "gaia/treasure/stone",
            "gaia/treasure/wood", "gaia/treasure/metal",
        };

        private static readonly string[] TreeTypes =
        {
            OOak, OOak2, OOak3, OOak4, OBeech, OBeech2, OAcacia,
        };

        private static readonly string[] PForest1 =
        {
            TForestFloor,
            TForestFloor + "|" + OOak,
            TForestFloor + "|" + OOak2,
            TForestFloor + "|" + OOak3,
            TForestFloor + "|" + OOak4,
            TForestFloor,
        };

        private static readonly string[] PForest2 =
        {
            TForestFloor,
            TForestFloor + "|" + OPopolar,
            TForestFloor + "|" + OBeech,
            TForestFloor + "|" + OBeech2,
            TForestFloor + "|" + OAcacia,
            TForestFloor,
        };

        protected override double HeightLand => HeightLandConst;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            rng.RandBool(2.0 / 3); // day 只影响环境表；这里保留上游生成前抽数。
            InitContextNoBiome(rng, settings, TPrimary);
            var map = Map;
            var mapCenter = map.GetCenter();
            double startAngle = rng.RandomAngle();
            double waterWidth = RmgenLibrary.FractionToTiles(0.3, MapSize);
            int gallicCCTreasureCount = rng.RandIntInclusive(8, 12);
            int randomTreasureCount = rng.RandIntInclusive(0, RmgenLibrary.ScaleByMapSize(0, 2, MapSize));

            var clWater = new TileClass(MapSize);
            var clLand = new[] { new TileClass(MapSize), new TileClass(MapSize) };
            var clPatrolPointSiegeEngine = new[] { new TileClass(MapSize), new TileClass(MapSize) };
            var clPatrolPointSoldier = new[] { new TileClass(MapSize), new TileClass(MapSize) };
            var clShore = new[] { new TileClass(MapSize), new TileClass(MapSize) };
            var clShoreUngarrisonPoint = new[] { new TileClass(MapSize), new TileClass(MapSize) };
            var clShip = new TileClass(MapSize);
            var clShipPatrol = new TileClass(MapSize);
            var clIsland = new TileClass(MapSize);
            var clTreasure = new TileClass(MapSize);
            var clWaterLog = new TileClass(MapSize);
            var clGauls = new TileClass(MapSize);
            var clTower = new TileClass(MapSize);
            var clOutpost = new TileClass(MapSize);
            var clPath = new TileClass(MapSize);
            var clRitualPlace = new TileClass(MapSize);
            ClFood = new TileClass(MapSize);

            bool gallicCC = MapSize >= SmallMapSize;
            if (gallicCC)
                PlaceGallicVillages(rng, map, settings, mapCenter, startAngle, waterWidth,
                    gallicCCTreasureCount, clGauls, clPath, clRitualPlace);

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementRiver(rng, map, settings,
                startAngle, RmgenLibrary.FractionToTiles(0.6, MapSize));
            RmgenCommon.PlacePlayerBases(rng, map, settings, TPrimary[0], ClPlayer, null,
                playerPosition, TShore, TRoad, playerIDs,
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
                    MinesDistance = RmgenLibrary.ScaleByMapSize(9, 14, MapSize),
                    TreesTemplate = OOak,
                    TreesCount = 20,
                    TreesMinDist = 10,
                    TreesMaxDist = 14,
                    DecorativesTemplate = ABush1,
                });

            var riverStart = new RmgenVector2D(mapCenter.X, MapSize);
            riverStart.RotateAround(startAngle, mapCenter);
            var riverEnd = new RmgenVector2D(mapCenter.X, 0);
            riverEnd.RotateAround(startAngle, mapCenter);
            PaintDanubiusRiver(rng, map, riverStart, riverEnd, waterWidth,
                RmgenLibrary.ScaleByMapSize(6, 25, MapSize), HeightSeaGround, HeightLandConst,
                true, 0, 30, 0,
                waterFunc: (position, height, _) =>
                {
                    var origPos = position;
                    origPos.RotateAround(-startAngle, mapCenter);
                    if (height > 0 && height < 1 &&
                        origPos.Y > ShorelineDistance && origPos.Y < MapSize - ShorelineDistance)
                        clShore[origPos.X < mapCenter.X ? 0 : 1].Add(position);
                },
                landFunc: (position, shoreDist1, shoreDist2) =>
                {
                    if (shoreDist1 > 0)
                        clLand[0].Add(position);
                    if (shoreDist2 < 0)
                        clLand[1].Add(position);
                });

            RmgenLibrary.PaintTileClassBasedOnHeight(double.NegativeInfinity, 0.7,
                HeightPlacer.Mode.ExcludeMinExcludeMax, clWater);

            var areasLand = new List<Area>
            {
                EnsureArea(map, RmgenLibrary.CreateArea(new MapBoundsPlacer(), (IPainter?)null,
                    RmgenLibrary.StayClasses(clLand[0], 0))),
                EnsureArea(map, RmgenLibrary.CreateArea(new MapBoundsPlacer(), (IPainter?)null,
                    RmgenLibrary.StayClasses(clLand[1], 0))),
            };

            var areasWater = new List<Area>
            {
                EnsureArea(map, RmgenLibrary.CreateArea(new MapBoundsPlacer(), (IPainter?)null,
                    new HeightConstraint(map, double.NegativeInfinity, HeightLandConst))),
            };

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, double.NegativeInfinity, HeightShore,
                HeightPlacer.Mode.ExcludeMinExcludeMax, TWater);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, HeightShore, HeightLandConst,
                HeightPlacer.Mode.ExcludeMinExcludeMax, TShore);

            CreateBumps(rng, map,
                RmgenLibrary.AvoidClasses(ClPlayer, 6, clWater, 2, clPath, 1, clGauls, 1),
                RmgenLibrary.ScaleByMapSize(30, 300, MapSize), 1, 8, 4, 0, 3);

            if (rng.RandBool())
                CreateHills(rng, map, new object[] { TCliff, TCliff, TCliff },
                    RmgenLibrary.AvoidClasses(ClPlayer, 18, ClHill, 20, clWater, 2,
                        clGauls, 5, clPath, 1),
                    ClHill, RmgenLibrary.ScaleByMapSize(3, 15, MapSize));
            else
                RmgenCommon.CreateMountains(rng, map, TCliff,
                    RmgenLibrary.AvoidClasses(ClPlayer, 18, ClHill, 20, clWater, 2,
                        clGauls, 5, clPath, 1),
                    ClHill, (int)Math.Ceiling(RmgenLibrary.ScaleByMapSize(3, 15, MapSize)));

            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(500, 3000, 0.7, MapSize);
            GaiaEntities.CreateForests(rng, map,
                new object[] { TForestFloor, TForestFloor, TForestFloor, PForest1, PForest2 },
                RmgenLibrary.AvoidClasses(ClPlayer, 16, ClForest, 17, clWater, 5,
                    ClHill, 2, clGauls, 5, clPath, 1),
                ClForest, forestTrees, NumPlayers);

            CreateLayeredPatches(rng, map,
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                    RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
                },
                new object[]
                {
                    new[] { TGrass, TGrass2 },
                    new[] { TGrass2, TGrass3 },
                    new[] { TGrass3, TGrass },
                },
                new[] { 1, 1 },
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, clWater, 2,
                    ClDirt, 2, ClHill, 1, clGauls, 5, clPath, 1),
                RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            var areaIslands = RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(3, 4, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(4, 8, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(50, 80, MapSize)),
                    0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { TWater, TShore, TIsland }, new[] { 2, 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, HeightIsland, 4),
                    new TileClassPainter(clIsland),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clIsland, 30),
                    RmgenLibrary.StayClasses(clWater, 10),
                }),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers);

            CreateBumps(rng, map, RmgenLibrary.StayClasses(clIsland, 2),
                RmgenLibrary.ScaleByMapSize(50, 400, MapSize), 1, 8, 4, 0, 3);

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -20, -3,
                HeightPlacer.Mode.IncludeMinIncludeMax, TSeaDepths);

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, OMetalLarge, 1, 1, 0, 4) },
                    true, ClMetal),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClMetal, 50, ClRock, 10),
                    RmgenLibrary.StayClasses(clIsland, 5),
                }),
                RmgenLibrary.ScaleByMapSize(3, 10, MapSize), 20, areaIslands);

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, OStoneLarge, 1, 1, 0, 4) },
                    true, ClRock),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClMetal, 10, ClRock, 50),
                    RmgenLibrary.StayClasses(clIsland, 5),
                }),
                RmgenLibrary.ScaleByMapSize(3, 10, MapSize), 20, areaIslands);

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, OTower, 1, 1, 0, 4) },
                    true, clTower),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClMetal, 4, ClRock, 4, clTower, 20),
                    RmgenLibrary.StayClasses(clIsland, 7),
                }),
                RmgenLibrary.ScaleByMapSize(3, 10, MapSize), 20, areaIslands);

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, OOutpost, 1, 1, 0, 4) },
                    true, clOutpost),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClMetal, 4, ClRock, 4, clTower, 5, clOutpost, 20),
                    RmgenLibrary.StayClasses(clIsland, 7),
                }),
                RmgenLibrary.ScaleByMapSize(3, 10, MapSize), 20, areaIslands);

            PlaceDanubiusLandMinesAndRuins(rng, map, areasLand, clWater, clGauls, clPath);
            PlaceDanubiusDecorations(rng, settings, clWater, clIsland, clGauls, clPath);
            PlaceDanubiusFood(rng, settings, clWater, clIsland, clGauls, clPath,
                clTower, clOutpost, stragglerTrees);
            PlaceDanubiusTreasuresAndVillageProps(rng, areasLand, clWater, clGauls, clPath,
                clTreasure, randomTreasureCount);
            PlaceDanubiusTriggerPoints(rng, map, mapCenter, startAngle, areasLand, areasWater,
                clWater, clIsland, clGauls, clPath, clShore, clShoreUngarrisonPoint, clShip,
                clShipPatrol, clPatrolPointSiegeEngine, clPatrolPointSoldier, clWaterLog, gallicCC);

            return map.MakeExportable();
        }

        private void PlaceGallicVillages(RmgenRng rng, RandomMap map, MapSettings settings,
            RmgenVector2D mapCenter, double startAngle, double waterWidth, int treasureCount,
            TileClass clGauls, TileClass clPath, TileClass clRitualPlace)
        {
            const double gaulCityRadius = 12;
            double gaulCityBorderDistance = MapSize < MediumMapSize ? 10 : 18;
            bool addCelticRitual = rng.RandBool(0.9);
            var villageStyle = DanubiusVillageStyle(MapSize >= NormalMapSize ?
                (settings.Nomad ? OSentryTower : OTower) : OWatchTower);
            var spikesStyle = DanubiusSpikesStyle();
            var villageFortress = DanubiusVillageFortress();
            var spikesFortress = DanubiusSpikesFortress();
            var ritualParticipants = CreateRitualParticipants();

            for (int i = 0; i < 2; ++i)
            {
                var civicCenterPosition = new RmgenVector2D(
                    i == 0 ? gaulCityBorderDistance : MapSize - gaulCityBorderDistance,
                    mapCenter.Y);
                civicCenterPosition.RotateAround(startAngle, mapCenter);

                if (addCelticRitual)
                    PlaceCelticRitual(rng, map, mapCenter, startAngle, waterWidth,
                        gaulCityRadius, i, civicCenterPosition, clPath, clRitualPlace,
                        ritualParticipants);

                map.PlaceEntityPassable(OCivicCenter, 0, civicCenterPosition,
                    startAngle + BuildingOrientation + SafeMath.PI * 3 / 2 * i);

                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, RmgenGeometry.DiskArea(gaulCityRadius),
                        0.6, 0.3, double.PositiveInfinity, civicCenterPosition),
                    new IPainter[]
                    {
                        new TerrainPainter(TShore, rng),
                        new TileClassPainter(clGauls),
                    },
                    null);

                PlaceCustomFortress(map, villageStyle, civicCenterPosition, villageFortress,
                    0, startAngle + SafeMath.PI, null);
                PlaceCustomFortress(map, spikesStyle, civicCenterPosition, spikesFortress,
                    0, startAngle + SafeMath.PI, null);

                for (int j = 0; j < treasureCount; ++j)
                {
                    var off = new RmgenVector2D(rng.RandFloat(-0.8, 0.8) * gaulCityRadius, 0);
                    off.Rotate(rng.RandomAngle());
                    map.PlaceEntityPassable(rng.PickRandom(OTreasures), 0,
                        RmgenVector2D.Add(civicCenterPosition, off), rng.RandomAngle());
                }
            }
        }

        private void PlaceCelticRitual(RmgenRng rng, RandomMap map, RmgenVector2D mapCenter,
            double startAngle, double waterWidth, double gaulCityRadius, int side,
            RmgenVector2D civicCenterPosition, TileClass clPath, TileClass clRitualPlace,
            IReadOnlyList<RitualParticipant> ritualParticipants)
        {
            var meetingPlacePosition = new RmgenVector2D(
                side == 0 ? waterWidth : MapSize - waterWidth,
                mapCenter.Y + RmgenLibrary.FractionToTiles(rng.RandFloat(0.1, 0.4), MapSize) *
                    (rng.RandBool() ? 1 : -1));
            meetingPlacePosition.RotateAround(startAngle, mapCenter);
            double mRadius = RmgenLibrary.ScaleByMapSize(4, 6, MapSize);

            var gateOffset = new RmgenVector2D(gaulCityRadius * (side == 0 ? 1 : -1), 0);
            gateOffset.Rotate(startAngle);
            var pathStart = RmgenVector2D.Add(civicCenterPosition, gateOffset);
            RmgenLibrary.CreateArea(
                new PathPlacer(rng, 0.4, 4, 0.2, 0.05)
                {
                    Start = pathStart,
                    End = meetingPlacePosition,
                    Width = 4,
                },
                new IPainter[]
                {
                    new LayeredPainter(new object[] { TShore, TRoad }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        HeightPath, 4),
                    new TileClassPainter(clPath),
                },
                null);

            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, RmgenGeometry.DiskArea(mRadius), 0.6, 0.3,
                    double.PositiveInfinity, meetingPlacePosition),
                new IPainter[]
                {
                    new TerrainPainter(TShore, rng),
                    new TileClassPainter(clPath),
                    new TileClassPainter(clRitualPlace),
                },
                null);

            map.PlaceEntityAnywhere(ACampfire, 0, meetingPlacePosition, rng.RandomAngle());
            foreach (var participants in ritualParticipants)
            {
                var (positions, angles) = RmgenGeometry.DistributePointsOnCircle(
                    participants.Count, startAngle, participants.Radius * mRadius, meetingPlacePosition);
                for (int j = 0; j < positions.Count; ++j)
                    map.PlaceEntityPassable(rng.PickRandom(participants.Templates), 0,
                        positions[j], angles[j] + participants.Angle);
            }
        }

        private void PlaceDanubiusLandMinesAndRuins(RmgenRng rng, RandomMap map,
            IReadOnlyList<Area> areasLand, TileClass clWater, TileClass clGauls, TileClass clPath)
        {
            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, OMetalLarge, 1, 1, 0, 4) },
                    true, ClMetal),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 4, ClBaseResource, 20, ClMetal, 50,
                    ClRock, 20, clWater, 4, ClHill, 4, clGauls, 5, clPath, 5),
                RmgenLibrary.ScaleByMapSize(4, 20, MapSize), 50, areasLand);

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, OStoneLarge, 1, 1, 0, 4) },
                    true, ClRock),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 4, ClBaseResource, 20, ClMetal, 20,
                    ClRock, 50, clWater, 4, ClHill, 4, clGauls, 5, clPath, 5),
                RmgenLibrary.ScaleByMapSize(4, 20, MapSize), 50, areasLand);

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, OStoneRuins, 1, 1, 0, 4) },
                    true, ClRock),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 2, ClPlayer, 12, ClMetal, 6,
                    ClRock, 25, clWater, 4, ClHill, 4, clGauls, 5, clPath, 1),
                RmgenLibrary.ScaleByMapSize(2, 10, MapSize), 20, areasLand);
        }

        private void PlaceDanubiusDecorations(RmgenRng rng, MapSettings settings,
            TileClass clWater, TileClass clIsland, TileClass clGauls, TileClass clPath)
        {
            for (int i = 0; i < 2; ++i)
                GaiaEntities.CreateDecoration(rng,
                    new IGroupElement[][]
                    {
                        new IGroupElement[] { new ScatterObject(rng, ARock1, 1, 1, 0, 1) },
                        new IGroupElement[] { new ScatterObject(rng, ARock2, 1, 1, 0, 1) },
                        new IGroupElement[] { new ScatterObject(rng, ARock3, 1, 1, 0, 1) },
                        new IGroupElement[] { new ScatterObject(rng, ARock4, 1, 1, 0, 1) },
                        new IGroupElement[] { new ScatterObject(rng, ABush1, 1, 3, 0, 2) },
                        new IGroupElement[] { new ScatterObject(rng, ABush2, 1, 2, 0, 1) },
                        new IGroupElement[] { new ScatterObject(rng, ABush3, 1, 3, 0, 2) },
                        new IGroupElement[] { new ScatterObject(rng, ABush4, 1, 2, 0, 1) },
                        new IGroupElement[] { new ScatterObject(rng, AFerns, 2, 5, 2, 4) },
                    },
                    new[]
                    {
                        RmgenLibrary.ScaleByMapAreaAbsolute(5, MapSize, settings.CircularMap),
                        RmgenLibrary.ScaleByMapAreaAbsolute(5, MapSize, settings.CircularMap),
                        RmgenLibrary.ScaleByMapAreaAbsolute(5, MapSize, settings.CircularMap),
                        RmgenLibrary.ScaleByMapAreaAbsolute(5, MapSize, settings.CircularMap),
                        RmgenLibrary.ScaleByMapAreaAbsolute(5, MapSize, settings.CircularMap),
                        RmgenLibrary.ScaleByMapAreaAbsolute(5, MapSize, settings.CircularMap),
                        RmgenLibrary.ScaleByMapAreaAbsolute(5, MapSize, settings.CircularMap),
                        RmgenLibrary.ScaleByMapAreaAbsolute(5, MapSize, settings.CircularMap),
                        RmgenLibrary.ScaleByMapSize(20, 80, MapSize),
                    },
                    i == 0 ?
                        RmgenLibrary.AvoidClasses(clWater, 4, ClForest, 1, ClPlayer, 16,
                            ClRock, 4, ClMetal, 4, ClHill, 4, clGauls, 5, clPath, 1) :
                        new AndConstraint(new IConstraint[]
                        {
                            RmgenLibrary.StayClasses(clIsland, 4),
                            RmgenLibrary.AvoidClasses(ClForest, 1, ClRock, 4, ClMetal, 4),
                        }));
        }

        private void PlaceDanubiusFood(RmgenRng rng, MapSettings settings,
            TileClass clWater, TileClass clIsland, TileClass clGauls, TileClass clPath,
            TileClass clTower, TileClass clOutpost, int stragglerTrees)
        {
            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, OFish, 2, 3, 0, 2) },
                },
                new[] { 20 * RmgenLibrary.ScaleByMapSize(5, 20, MapSize) },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clIsland, 2, ClFood, 10, clPath, 1),
                    RmgenLibrary.StayClasses(clWater, 5),
                }),
                ClFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, OSheep, 5, 5, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, OGoat, 5, 5, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, ORabbit, 5, 8, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, ODeer, 4, 6, 0, 2) },
                    new IGroupElement[] { new ScatterObject(rng, OHawk, 1, 1, 0, 4) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                },
                RmgenLibrary.AvoidClasses(clIsland, 2, ClFood, 10, clWater, 5,
                    ClPlayer, 16, ClHill, 2, clGauls, 5, clPath, 1),
                ClFood);

            if (!settings.Nomad)
                GaiaEntities.CreateFood(rng,
                    new IGroupElement[][]
                    {
                        new IGroupElement[] { new ScatterObject(rng, OWolf, 1, 3, 0, 4) },
                        new IGroupElement[] { new ScatterObject(rng, OBoar, 1, 1, 0, 4) },
                        new IGroupElement[] { new ScatterObject(rng, OBear, 1, 1, 0, 4) },
                    },
                    new[]
                    {
                        RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                        RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                        RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    },
                    RmgenLibrary.AvoidClasses(clIsland, 2, ClFood, 10, clWater, 5,
                        ClPlayer, 24, ClHill, 2, clGauls, 5, clPath, 1),
                    ClFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, OApple, 3, 5, 4, 7) },
                    new IGroupElement[] { new ScatterObject(rng, OBerryBush, 4, 6, 0, 4) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                },
                RmgenLibrary.AvoidClasses(clWater, 5, ClForest, 2, ClPlayer, 16,
                    ClHill, 4, ClFood, 10, ClMetal, 4, ClRock, 4, clGauls, 5, clPath, 1),
                ClFood);

            GaiaEntities.CreateStragglerTrees(rng, TreeTypes,
                RmgenLibrary.AvoidClasses(ClForest, 2, clWater, 8, ClPlayer, 16,
                    ClMetal, 4, ClRock, 4, ClFood, 1, ClHill, 2, clGauls, 5, clPath, 5),
                ClForest, stragglerTrees);

            GaiaEntities.CreateStragglerTrees(rng, TreeTypes,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clIsland, 4),
                    RmgenLibrary.AvoidClasses(ClMetal, 4, ClRock, 4, clTower, 4, clOutpost, 4),
                }),
                ClForest, stragglerTrees * 7);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, OSheep, 4, 6, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, OGoat, 4, 6, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, ORabbit, 5, 8, 0, 4) },
                },
                new[]
                {
                    10 * RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    10 * RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                    10 * RmgenLibrary.ScaleByMapSize(5, 20, MapSize),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClRock, 4, ClMetal, 4, ClFood, 3,
                        ClForest, 1, clOutpost, 2, clTower, 2),
                    RmgenLibrary.StayClasses(clIsland, 4),
                }),
                ClFood);
        }

        private void PlaceDanubiusTreasuresAndVillageProps(RmgenRng rng, IReadOnlyList<Area> areasLand,
            TileClass clWater, TileClass clGauls, TileClass clPath, TileClass clTreasure,
            int randomTreasureCount)
        {
            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, rng.PickRandom(OTreasures), 1, 1, 0, 2),
                }, true, clTreasure),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 15, ClHill, 1,
                    clWater, 5, ClFood, 1, ClRock, 4, ClMetal, 4, clTreasure, 10,
                    clGauls, 5),
                randomTreasureCount, 50, areasLand);

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, ABucket, 1, 1, 0, 1) },
                    new IGroupElement[] { new ScatterObject(rng, ABarrel, 1, 1, 0, 1) },
                    new IGroupElement[] { new ScatterObject(rng, ATartan, 3, 3, 4, 4,
                        SafeMath.PI / 4, SafeMath.PI / 2) },
                    new IGroupElement[] { new ScatterObject(rng, AWheel, 2, 4, 1, 2) },
                    new IGroupElement[] { new ScatterObject(rng, AWell, 1, 1, 0, 2) },
                    new IGroupElement[] { new ScatterObject(rng, AWoodcord, 1, 2, 2, 2,
                        SafeMath.PI / 2, SafeMath.PI / 2) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(2, 10, MapSize),
                    RmgenLibrary.ScaleByMapSize(2, 10, MapSize),
                    RmgenLibrary.ScaleByMapSize(2, 10, MapSize),
                    RmgenLibrary.ScaleByMapSize(2, 10, MapSize),
                    RmgenLibrary.ScaleByMapSize(3, 4, MapSize),
                    RmgenLibrary.ScaleByMapSize(2, 10, MapSize),
                },
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClBaseResource, 5,
                    ClHill, 1, ClFood, 1, clWater, 5, ClRock, 4, ClMetal, 4, clGauls, 5,
                    clPath, 1));
        }

        private void PlaceDanubiusTriggerPoints(RmgenRng rng, RandomMap map, RmgenVector2D mapCenter,
            double startAngle, IReadOnlyList<Area> areasLand, IReadOnlyList<Area> areasWater,
            TileClass clWater, TileClass clIsland, TileClass clGauls, TileClass clPath,
            IReadOnlyList<TileClass> clShore, IReadOnlyList<TileClass> clShoreUngarrisonPoint,
            TileClass clShip, TileClass clShipPatrol,
            IReadOnlyList<TileClass> clPatrolPointSiegeEngine,
            IReadOnlyList<TileClass> clPatrolPointSoldier, TileClass clWaterLog, bool gallicCC)
        {
            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, TriggerPointShipSpawn, 1, 1, 0, 0) },
                    true, clShip),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clShip, 5, clIsland, 4),
                    RmgenLibrary.StayClasses(clWater, 10),
                }),
                RmgenLibrary.ScaleByMapSize(10, 75, MapSize), 10, areasWater);

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, TriggerPointShipPatrol, 1, 1, 0, 0) },
                    true, clShipPatrol),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clShipPatrol, 5, clIsland, 3),
                    RmgenLibrary.StayClasses(clWater, 4),
                }),
                RmgenLibrary.ScaleByMapSize(20, 150, MapSize), 10, areasWater);

            for (int i = 0; i < 2; ++i)
            {
                var areaShore = new List<Area>
                {
                    EnsureArea(map, RmgenLibrary.CreateArea(new MapBoundsPlacer(), (IPainter?)null,
                        RmgenLibrary.StayClasses(clShore[i], 0))),
                };
                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, i == 0 ? TriggerPointShipUnloadLeft :
                            TriggerPointShipUnloadRight, 1, 1, 0, 0),
                    }, true, clShoreUngarrisonPoint[i]),
                    0,
                    RmgenLibrary.AvoidClasses(clShoreUngarrisonPoint[i], 4),
                    RmgenLibrary.ScaleByMapSize(60, 200, MapSize), 20, areaShore);
            }

            var directionOffset = new RmgenVector2D(0, 1);
            directionOffset.Rotate(startAngle);
            map.PlaceEntityAnywhere(TriggerPointRiverDirection, 0,
                RmgenVector2D.Add(mapCenter, directionOffset), rng.RandomAngle());

            for (int i = 0; i < 2; ++i)
                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, i == 0 ? TriggerPointLandPatrolLeft :
                            TriggerPointLandPatrolRight, 1, 1, 0, 0),
                    }, true, clPatrolPointSiegeEngine[i]),
                    0,
                    RmgenLibrary.AvoidClasses(clWater, 5, ClForest, 3, ClHill, 3,
                        ClFood, 1, ClRock, 5, ClMetal, 5, ClPlayer, 10, clGauls, 5,
                        clPatrolPointSiegeEngine[i], 5),
                    RmgenLibrary.ScaleByMapSize(20, 150, MapSize), 10,
                    new[] { areasLand[i] });

            if (gallicCC)
                for (int i = 0; i < 2; ++i)
                    RmgenLibrary.CreateObjectGroupsByAreas(rng,
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(rng, i == 0 ? TriggerPointCCAttackerPatrolLeft :
                                TriggerPointCCAttackerPatrolRight, 1, 1, 0, 0),
                        }, true, clPatrolPointSoldier[i]),
                        0,
                        RmgenLibrary.AvoidClasses(clWater, 5, ClHill, 3, ClFood, 1,
                            ClRock, 4, ClMetal, 4, ClPlayer, 15, clGauls, 0,
                            clPatrolPointSoldier[i], 5),
                        RmgenLibrary.ScaleByMapSize(20, 150, MapSize), 20,
                        new[] { areasLand[i] });

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, AWaterLog, 1, 1, 0, 0, startAngle, startAngle),
                }, true, clWaterLog),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clShip, 3, clIsland, 4),
                    RmgenLibrary.StayClasses(clWater, 4),
                }),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize), 10, areasWater);
        }

        private static void CreateBumps(RmgenRng rng, RandomMap map, IConstraint constraint,
            double count, double minSize, double maxSize, double spread, double failFraction, double elevation)
            => RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, minSize, maxSize, spread, failFraction),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        elevation, 2, relative: true),
                },
                constraint, count);

        private static void CreateHills(RmgenRng rng, RandomMap map, object[] terrainSet,
            IConstraint constraint, TileClass tileClass, double count)
            => RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, map.GetSize())),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, map.GetSize())), 0.5),
                new IPainter[]
                {
                    new LayeredPainter(terrainSet, new[] { 1, 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, 18, 2),
                    new TileClassPainter(tileClass),
                },
                constraint, count);

        private static void CreateLayeredPatches(RmgenRng rng, RandomMap map,
            IReadOnlyList<double> sizes, object[] terrains, int[] widths, IConstraint constraint,
            double count, TileClass tileClass)
        {
            foreach (double size in sizes)
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, map.GetSize())),
                        size, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(terrains, widths, rng),
                        new TileClassPainter(tileClass),
                    },
                    constraint, count);
        }

        private delegate void RiverWaterFunc(RmgenVector2D position, double height, double riverFraction);
        private delegate void RiverLandFunc(RmgenVector2D position, double shoreDist1, double shoreDist2);

        private static void PaintDanubiusRiver(RmgenRng rng, RandomMap map,
            RmgenVector2D start, RmgenVector2D end, double width, double fadeDist,
            double heightRiverbed, double heightLandValue, bool parallel, double deviation,
            double meanderShort, double meanderLong,
            RiverWaterFunc? waterFunc, RiverLandFunc? landFunc)
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

            double RiverCurve(double riverFraction, double curveStartAngle, double seed) =>
                meanderShortTiles * RndRiver(curveStartAngle +
                    RmgenLibrary.FractionToTiles(riverFraction, mapSize) / 128, seed) +
                meanderLongTiles * RndRiver(curveStartAngle +
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
                    else
                        landFunc?.Invoke(vecPoint, shoreDist1, shoreDist2);
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

        private static Area EnsureArea(RandomMap map, Area? area)
            => area ?? new Area(map, new List<RmgenVector2D>());

        private static IReadOnlyList<RitualParticipant> CreateRitualParticipants()
            => new[]
            {
                new RitualParticipant(0.6, new[] { OCivilian }, 9, SafeMath.PI),
                new RitualParticipant(0.8, new[] { OSkirmisher, OHealer, ONakedFanatic }, 15,
                    SafeMath.PI),
                new RitualParticipant(1, new[] { ABench }, 10, SafeMath.PI / 2),
                new RitualParticipant(1.1, new[] { OGoat }, 7, 0),
                new RitualParticipant(1.2, new[] { ARug }, 8, SafeMath.PI),
            };

        private static DanubiusWallStyle DanubiusVillageStyle(string defenseTowerTemplate)
            => new(new Dictionary<string, WallBuilder.WallElement>
            {
                ["house"] = new(OHouse, SafeMath.PI, 0, 4, 0),
                ["hut"] = new(OHut, SafeMath.PI, 0, 4, 0),
                ["longhouse"] = new(OLongHouse, SafeMath.PI, 0, 4, 0),
                ["tavern"] = new(OTavern, SafeMath.PI * 3 / 2, 0, 4, 0),
                ["temple"] = new(OTemple, SafeMath.PI * 3 / 2, 0, 4, 0),
                ["defense_tower"] = new(defenseTowerTemplate, SafeMath.PI / 2, 0, 4, 0),
                ["pillar"] = ReadyPalisade(OPalisadePillar, 3, SafeMath.PI, 0, 0),
                ["gate"] = ReadyPalisade(OPalisadeGate, 14, SafeMath.PI, 0, 0),
                ["long"] = ReadyPalisade(OPalisadeLong, 14, SafeMath.PI, 0, 0),
                ["medium"] = ReadyPalisade(OPalisadeMedium, 9, SafeMath.PI, 0, 0),
                ["short"] = ReadyPalisade(OPalisadeShort, 4, SafeMath.PI, 0, 0),
                ["cornerIn"] = ReadyPalisade(OPalisadeCurve, 8, SafeMath.PI * 0.75, 2.8,
                    SafeMath.PI * 0.5),
            }, 0.05);

        private static DanubiusWallStyle DanubiusSpikesStyle()
            => new(new Dictionary<string, WallBuilder.WallElement>
            {
                ["spikes_tall"] = ReadyPalisade(OPalisadeTallSpikes, 11, SafeMath.PI * 0.5, 0, 0),
                ["spike_single"] = ReadyPalisade(OPalisadeAngleSpikes, 3, SafeMath.PI * 1.5, -0.7, 0),
            }, 0);

        private static WallBuilder.WallElement ReadyPalisade(string templateName,
            double lengthMetres, double angle, double indentMetres, double bend)
            => new(templateName, angle, lengthMetres / RmgenConstants.TERRAIN_TILE_SIZE,
                indentMetres / RmgenConstants.TERRAIN_TILE_SIZE, bend);

        private static WallBuilder.Fortress DanubiusVillageFortress()
        {
            string[] side =
            {
                "gate", "pillar", "hut", "long", "long", "cornerIn", "defense_tower",
                "long", "temple", "long", "pillar", "house", "long", "short", "pillar",
                "gate", "pillar", "longhouse", "long", "long", "cornerIn", "defense_tower",
                "long", "tavern", "long", "pillar",
            };
            return new WallBuilder.Fortress("Geto-Dacian Tribal Confederation",
                side.Concat(side).ToArray());
        }

        private static WallBuilder.Fortress DanubiusSpikesFortress()
        {
            var palisadeCorner = new[] { "turn_0.25", "spike_single", "turn_0.25" };
            var palisadeGate = new[] { "spike_single", "gap_3.6", "spike_single" };
            var palisadeWallShort = Repeat("spikes_tall", 3);
            var palisadeWallLong = Repeat("spikes_tall", 5);
            var sideShort = palisadeGate.Concat(palisadeWallShort).Concat(palisadeCorner)
                .Concat(palisadeWallShort);
            var sideLong = palisadeGate.Concat(palisadeWallShort).Concat(palisadeCorner)
                .Concat(palisadeWallLong);
            return new WallBuilder.Fortress("Spikes Of The Geto-Dacian Tribal Confederation",
                sideLong.Concat(sideShort).Concat(sideLong).Concat(sideShort).ToArray());
        }

        private static string[] Repeat(string value, int count)
        {
            var result = new string[count];
            for (int i = 0; i < count; ++i)
                result[i] = value;
            return result;
        }

        private static void PlaceCustomFortress(RandomMap map, DanubiusWallStyle style,
            RmgenVector2D centerPosition, WallBuilder.Fortress fortress, int playerId,
            double orientation, IConstraint? constraints)
        {
            var centerToFirstElement = fortress.CenterToFirstElement ??
                GetCenterToFirstElement(GetWallAlignment(style, new RmgenVector2D(0, 0),
                    fortress.Wall, 0));
            var a = Rotate(new RmgenVector2D(centerToFirstElement.X, 0), -orientation);
            var b = Rotate(new RmgenVector2D(centerToFirstElement.Y, 0).Perpendicular(), -orientation);
            var position = RmgenVector2D.Add(RmgenVector2D.Add(centerPosition, a), b);
            PlaceWall(map, style, position, fortress.Wall, playerId, orientation, constraints);
        }

        private static void PlaceWall(RandomMap map, DanubiusWallStyle style,
            RmgenVector2D position, IReadOnlyList<string> wall, int playerId,
            double orientation, IConstraint? constraints)
        {
            var constraint = constraints ?? new NullConstraint();
            foreach (var align in GetWallAlignment(style, position, wall, orientation))
            {
                if (align.templateName == null || !map.InMapBounds(align.position))
                    continue;
                var floored = align.position;
                floored.Floor();
                if (constraint.Allows(floored))
                    map.PlaceEntityPassable(align.templateName, playerId, align.position, align.angle);
            }
        }

        private static List<(RmgenVector2D position, string? templateName, double angle)> GetWallAlignment(
            DanubiusWallStyle style, RmgenVector2D position, IReadOnlyList<string> wall,
            double orientation)
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
                if (element.Bend != 0 && element.Indent != 0)
                {
                    distance += element.Indent * SafeMath.Sin(element.Bend);
                    wallPosition.Add(Rotate(new RmgenVector2D(element.Indent, 0), -orientation));
                }
                wallPosition.Add(Rotate(new RmgenVector2D(distance, 0), -orientation).Perpendicular());
            }
            return alignment;
        }

        private static WallBuilder.WallElement GetWallElement(DanubiusWallStyle style, string element)
        {
            if (style.Elements.TryGetValue(element, out var wallElement))
                return wallElement;
            if (element.StartsWith("turn_", StringComparison.Ordinal))
                return new WallBuilder.WallElement(null, 0, 0, 0,
                    double.Parse(element.Substring("turn_".Length),
                        System.Globalization.CultureInfo.InvariantCulture) * SafeMath.PI);
            if (element.StartsWith("gap_", StringComparison.Ordinal))
                return new WallBuilder.WallElement(null, 0,
                    double.Parse(element.Substring("gap_".Length),
                        System.Globalization.CultureInfo.InvariantCulture), 0, 0);
            return new WallBuilder.WallElement(null, 0, 0, 0, 0);
        }

        private static RmgenVector2D GetCenterToFirstElement(
            IReadOnlyList<(RmgenVector2D position, string? templateName, double angle)> alignment)
        {
            var result = new RmgenVector2D(0, 0);
            foreach (var align in alignment)
                result.Sub(RmgenVector2D.Div(align.position, alignment.Count));
            return result;
        }

        private static RmgenVector2D Rotate(RmgenVector2D v, double angle)
        {
            v.Rotate(angle);
            return v;
        }

        private sealed class DanubiusWallStyle
        {
            public readonly Dictionary<string, WallBuilder.WallElement> Elements;
            public readonly double Overlap;

            public DanubiusWallStyle(Dictionary<string, WallBuilder.WallElement> elements, double overlap)
            {
                Elements = elements;
                Overlap = overlap;
            }
        }

        private readonly struct RitualParticipant
        {
            public readonly double Radius;
            public readonly IReadOnlyList<string> Templates;
            public readonly int Count;
            public readonly double Angle;

            public RitualParticipant(double radius, IReadOnlyList<string> templates, int count, double angle)
            {
                Radius = radius;
                Templates = templates;
                Count = count;
                Angle = angle;
            }
        }
    }

    /// <summary>elephantine.js（逐字移植，653 行）——真实高度图上的尼罗河心岛：
    /// 中央岛多边形、外围河道、岸线湿地梯度、神庙/金字塔城区与库施守军。
    /// TILE_CENTERED_HEIGHT_MAP 无 C# 等价已忽略；环境设置与 placePlayersNomad 按约定省略。</summary>
    public sealed class ElephantineMap2 : StandardMap
    {
        private TileClass ClFood = null!;

        private const double HeightSeaGround = -6;
        private const double HeightWaterLevel = 0;
        private const double HeightShore = 0.5;
        private const double MinHeight = -1;
        private const double MaxHeight = 2;
        private const double RiverAngle = 0.22 * SafeMath.PI;

        private const string TWater = "desert_sand_wet";
        private const string TRoad = "savanna_tile_a_red";
        private const string TRoadIsland = "savanna_tile_a";
        private const string TRoadWildIsland = "savanna_dirt_rocks_a";
        private const string TForestFloorLand = "savanna_forestfloor_b_red";

        private const string OAcacia = "gaia/tree/acacia";
        private const string OStoneLarge = "gaia/rock/savanna_large";
        private const string OStoneSmall = "gaia/rock/desert_small";
        private const string OMetalLarge = "gaia/ore/savanna_large";
        private const string OMetalSmall = "gaia/ore/desert_small";
        private const string OBerryBush = "gaia/fruit/berry_05";
        private const string OGazelle = "gaia/fauna_gazelle";
        private const string ORhino = "gaia/fauna_rhinoceros_white";
        private const string OWarthog = "gaia/fauna_boar";
        private const string OGiraffe = "gaia/fauna_giraffe";
        private const string OGiraffeInfant = "gaia/fauna_giraffe_infant";
        private const string OElephant = "gaia/fauna_elephant_african_bush";
        private const string OElephantInfant = "gaia/fauna_elephant_african_infant";
        private const string OLion = "gaia/fauna_lion";
        private const string OLioness = "gaia/fauna_lioness";
        private const string OCrocodile = "gaia/fauna_crocodile_nile";
        private const string OFish = "gaia/fish/tilapia";
        private const string OHawk = "birds/buzzard";
        private const string OWonder = "structures/ptol/wonder";
        private const string OPyramid = "structures/kush/pyramid_large";

        private static readonly string[] TPrimary =
        {
            "savanna_dirt_rocks_a_red", "savanna_dirt_a_red", "savanna_dirt_b_red",
        };

        private static readonly string[] TDirt =
        {
            "new_savanna_dirt_c", "new_savanna_dirt_d", "savanna_dirt_b_red",
            "savanna_dirt_plants_cracked",
        };

        private static readonly string[] TGrass =
        {
            "savanna_shrubs_a_wetseason", "alpine_grass_b_wild", "medit_shrubs_a",
            "steppe_grass_green_a",
        };

        private static readonly string[] OPalms =
        {
            "gaia/tree/cretan_date_palm_tall", "gaia/tree/cretan_date_palm_short",
            "gaia/tree/palm_tropic", "gaia/tree/date_palm",
            "gaia/tree/senegal_date_palm", "gaia/tree/medit_fan_palm",
        };

        private static readonly string[] OTreasure =
        {
            "gaia/treasure/food_barrel", "gaia/treasure/food_bin", "gaia/treasure/wood",
            "gaia/treasure/metal", "gaia/treasure/stone",
        };

        private static readonly string[] OTemples =
        {
            "structures/kush/temple_amun", "structures/kush/temple",
        };

        private static readonly string[] OTowers =
        {
            "uncapturable|structures/kush/sentry_tower",
            "uncapturable|structures/kush/sentry_tower",
            "uncapturable|structures/kush/defense_tower",
        };

        private static readonly string[] AStatues =
        {
            "actor|props/structures/kushites/statue_bird.xml",
            "actor|props/structures/kushites/statue_lion.xml",
            "actor|props/structures/kushites/statue_ram.xml",
        };

        private static readonly string[] ABushesShoreline =
        {
            "actor|props/flora/ferns.xml",
            "actor|props/flora/ferns.xml",
            "actor|props/flora/ferns.xml",
            "actor|props/flora/ferns.xml",
            "actor|props/flora/bush.xml",
            "actor|props/flora/bush_medit_la.xml",
            "actor|props/flora/bush_medit_la_lush.xml",
            "actor|props/flora/bush_medit_me_lush.xml",
            "actor|props/flora/bush_medit_sm.xml",
            "actor|props/flora/bush_medit_sm_lush.xml",
            "actor|props/flora/bush_tempe_la_lush.xml",
        };

        private static readonly string[] ABushesDesert =
        {
            "actor|props/flora/bush_dry_a.xml",
            "actor|props/flora/bush_medit_la_dry.xml",
            "actor|props/flora/bush_medit_me_dry.xml",
            "actor|props/flora/bush_medit_sm.xml",
            "actor|props/flora/bush_medit_sm_dry.xml",
            "actor|props/flora/bush_tempe_me_dry.xml",
            "actor|props/flora/grass_soft_dry_large_tall.xml",
            "actor|props/flora/grass_soft_dry_small_tall.xml",
        };

        private static readonly string[] KushHeroesFallback =
        {
            "units/kush/hero_amanirenas", "units/kush/hero_amanirenas_infantry",
            "units/kush/hero_arakamani", "units/kush/hero_harsiotef", "units/kush/hero_nastasen",
        };

        private static readonly string[] KushUnitsFallback =
        {
            "units/kush/cavalry_javelineer_a", "units/kush/cavalry_javelineer_b",
            "units/kush/cavalry_javelineer_e", "units/kush/cavalry_javelineer_merc_a",
            "units/kush/cavalry_javelineer_merc_b", "units/kush/cavalry_javelineer_merc_e",
            "units/kush/cavalry_spearman_a", "units/kush/cavalry_spearman_b",
            "units/kush/cavalry_spearman_e", "units/kush/champion_cavalry",
            "units/kush/champion_elephant", "units/kush/champion_infantry_amun",
            "units/kush/champion_infantry_apedemak", "units/kush/champion_infantry_archer",
            "units/kush/infantry_archer_a", "units/kush/infantry_archer_b",
            "units/kush/infantry_archer_e", "units/kush/infantry_javelineer_merc_a",
            "units/kush/infantry_javelineer_merc_b", "units/kush/infantry_javelineer_merc_e",
            "units/kush/infantry_maceman_merc_a", "units/kush/infantry_maceman_merc_b",
            "units/kush/infantry_maceman_merc_e", "units/kush/infantry_pikeman_a",
            "units/kush/infantry_pikeman_b", "units/kush/infantry_pikeman_e",
            "units/kush/infantry_spearman_a", "units/kush/infantry_spearman_b",
            "units/kush/infantry_spearman_e", "units/kush/infantry_swordsman_a",
            "units/kush/infantry_swordsman_b", "units/kush/infantry_swordsman_e",
            "units/kush/support_healer_a", "units/kush/support_healer_b",
            "units/kush/support_healer_e",
        };

        protected override double HeightLand => 0;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            string tForestFloorIsland = rng.PickRandom(TGrass);
            InitContextNoBiome(rng, settings, TPrimary);
            var map = Map;
            var mapCenter = map.GetCenter();
            double riverWidthBorder = RmgenLibrary.FractionToTiles(0.27, MapSize);
            double riverWidthCenter = RmgenLibrary.FractionToTiles(0.35, MapSize);
            double heightOffsetPath = -settings.Size / 80.0;

            var clWater = new TileClass(MapSize);
            var clIsland = new TileClass(MapSize);
            var clCliff = new TileClass(MapSize);
            var clTemple = new TileClass(MapSize);
            var clTower = new TileClass(MapSize);
            var clStatue = new TileClass(MapSize);
            var clSoldier = new TileClass(MapSize);
            var clTreasure = new TileClass(MapSize);
            var clPath = new TileClass(MapSize);
            ClFood = new TileClass(MapSize);

            IConstraint AvoidCollisions() => RmgenLibrary.AvoidClasses(ClPlayer, 15, clWater, 1,
                ClForest, 1, ClRock, 4, ClMetal, 4, ClFood, 6, clPath, 1, clTemple, 11,
                clCliff, 0, clStatue, 2, clSoldier, 3, clTower, 2, clTreasure, 1);

            LoadElephantineHeightmap();

            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        HeightSeaGround, 4),
                    new TileClassPainter(clWater),
                },
                new HeightConstraint(map, double.NegativeInfinity, HeightWaterLevel));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothingPainter(1, RmgenLibrary.ScaleByMapSize(0.1, 0.5, MapSize), 1),
                null);

            var islandVertices = new List<RmgenVector2D>
            {
                new(mapCenter.X - riverWidthBorder / 2, MapSize),
                new(mapCenter.X - riverWidthBorder / 2, 0),
                new(mapCenter.X - riverWidthCenter / 2, mapCenter.Y),
                new(mapCenter.X + riverWidthCenter / 2, mapCenter.Y),
                new(mapCenter.X + riverWidthBorder / 2, MapSize),
                new(mapCenter.X + riverWidthBorder / 2, 0),
            };
            for (int i = 0; i < islandVertices.Count; ++i)
            {
                var v = islandVertices[i];
                v.RotateAround(RiverAngle, mapCenter);
                islandVertices[i] = v;
            }

            var areaIsland = EnsureArea(map, RmgenLibrary.CreateArea(
                new ConvexPolygonPlacer(islandVertices, double.PositiveInfinity),
                new TileClassPainter(clIsland),
                RmgenLibrary.AvoidClasses(ClPlayer, 0, clWater, 0)));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TerrainPainter(TGrass, rng),
                RmgenLibrary.StayClasses(clIsland, 0));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TerrainPainter(TWater, rng),
                new HeightConstraint(map, double.NegativeInfinity, HeightShore));

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementRiver(rng, map, settings,
                RiverAngle, RmgenLibrary.FractionToTiles(0.62, MapSize));
            RmgenCommon.PlacePlayerBases(rng, map, settings, TPrimary[0], ClPlayer, null,
                playerPosition, TRoad, TRoad, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    ExtraBaseResourceConstraint = RmgenLibrary.AvoidClasses(clWater, 4),
                    StartingAnimal = true,
                    BerriesTemplate = OBerryBush,
                    Mines = new List<(string, string?, object?)>
                    {
                        (OMetalLarge, null, null),
                        (OStoneLarge, null, null),
                    },
                    TreesTemplate = OAcacia,
                    TreesCount = 2,
                    DecorativesTemplate = rng.PickRandom(ABushesDesert),
                });

            var groupTemple = CreateObjectGroupsByAreasCaptured(rng, map,
                new ObjectGroup(new IGroupElement[]
                {
                    new RandomObject(rng, MapSize >= 320 ? new[] { OWonder } : OTemples,
                        1, 1, 0, 1, RiverAngle, RiverAngle),
                }, true, clTemple),
                0,
                RmgenLibrary.StayClasses(clIsland, RmgenLibrary.ScaleByMapSize(10, 20, MapSize)),
                1, 200, new[] { areaIsland });

            var groupPyramid = CreateObjectGroupsByAreasCaptured(rng, map,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, OPyramid, 1, 1, 0, 1, RiverAngle, RiverAngle),
                }, true, clTemple),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clIsland, RmgenLibrary.ScaleByMapSize(10, 20, MapSize)),
                    RmgenLibrary.AvoidClasses(clTemple, RmgenLibrary.ScaleByMapSize(20, 50, MapSize)),
                    AvoidCollisions(),
                }),
                1, 200, new[] { areaIsland });

            var cityCenters = new List<CityCenter>();
            if (groupTemple.Count > 0 && groupTemple[0].Count > 0)
                cityCenters.Add(new CityCenter(groupTemple[0][0].Position, 10));
            if (groupPyramid.Count > 0 && groupPyramid[0].Count > 0)
                cityCenters.Add(new CityCenter(groupPyramid[0][0].Position, 6));

            var areaCityPatch = new List<Area>();
            foreach (var cityCenter in cityCenters)
            {
                var area = RmgenLibrary.CreateArea(
                    new DiskPlacer(cityCenter.Radius, cityCenter.Pos),
                    new LayeredPainter(new object[] { TRoadWildIsland, TRoadIsland }, new[] { 2 }, rng),
                    RmgenLibrary.StayClasses(clIsland, 2));
                if (area != null)
                    areaCityPatch.Add(area);
            }

            if (cityCenters.Count == 2)
                RmgenLibrary.CreateArea(
                    new PathPlacer(rng, 0.3, 4, 0.2, 0.05)
                    {
                        Start = cityCenters[0].Pos,
                        End = cityCenters[1].Pos,
                        Width = 4,
                    },
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { TRoadWildIsland, TRoadIsland }, new[] { 1 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                            heightOffsetPath, 4, relative: true),
                        new TileClassPainter(clPath),
                    },
                    null);

            CreateBumps(rng, map,
                RmgenLibrary.AvoidClasses(ClPlayer, 10, clWater, 2, clTemple, 10, clPath, 1),
                RmgenLibrary.ScaleByMapSize(10, 500, MapSize), 1, 8, 4, 0.2, 3);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TileClassPainter(clCliff),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clWater, 2),
                    new SlopeConstraint(map, 2, double.PositiveInfinity),
                }));

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
                        new ScatterObject(rng, OStoneSmall, 2, 5, 1, 3, 0, 2 * SafeMath.PI, 1),
                    },
                },
                RmgenLibrary.AvoidClasses(clWater, 4, clCliff, 4, ClPlayer, 20, ClRock, 10,
                    clPath, 1),
                ClRock, RmgenLibrary.ScaleByMapSize(6, 24, MapSize));

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, OMetalSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                        new ScatterObject(rng, OMetalLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, OMetalSmall, 2, 5, 1, 3, 0, 2 * SafeMath.PI, 1),
                    },
                },
                RmgenLibrary.AvoidClasses(clWater, 4, clCliff, 4, ClPlayer, 20, ClMetal, 10,
                    ClRock, 5, clPath, 1),
                ClMetal, RmgenLibrary.ScaleByMapSize(6, 24, MapSize));

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new RandomObject(rng, OTowers, 1, 1, 0, 1) },
                    true, clTower),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clIsland, RmgenLibrary.ScaleByMapSize(4, 8, MapSize)),
                    new NearTileClassConstraint(clTemple, 25),
                    RmgenLibrary.AvoidClasses(clTower, 12, ClPlayer, 30),
                    AvoidCollisions(),
                }),
                RmgenLibrary.ScaleByMapSize(4, 12, MapSize), 200);

            var pForestPalmsLand = ForestWithPalms(TForestFloorLand);
            var pForest2Land = new[] { TForestFloorLand, TForestFloorLand + "|" + OAcacia, TForestFloorLand };
            var pForestPalmsIsland = ForestWithPalms(tForestFloorIsland);
            var pForest2Island = new[] { tForestFloorIsland, tForestFloorIsland + "|" + OAcacia,
                tForestFloorIsland };
            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(400, 3000, 0.7, MapSize);

            GaiaEntities.CreateForests(rng, map,
                new object[] { TForestFloorLand, TForestFloorLand, TForestFloorLand,
                    pForestPalmsLand, pForest2Land },
                new AndConstraint(new IConstraint[]
                {
                    AvoidCollisions(),
                    RmgenLibrary.AvoidClasses(clIsland, 0, ClPlayer, 20, ClForest, 18,
                        clWater, 2),
                }),
                ClForest, forestTrees / 2.0, NumPlayers);

            GaiaEntities.CreateForests(rng, map,
                new object[] { tForestFloorIsland, tForestFloorIsland, tForestFloorIsland,
                    pForestPalmsIsland, pForest2Island },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clIsland, 0),
                    RmgenLibrary.AvoidClasses(ClForest, 15, clWater, 2),
                    AvoidCollisions(),
                }),
                ClForest, forestTrees / 2.0, NumPlayers);

            CreatePatches(rng, map,
                new[] { RmgenLibrary.ScaleByMapSize(5, 15, MapSize) }, TDirt,
                RmgenLibrary.AvoidClasses(clWater, 0, clIsland, 0, ClForest, 0, ClDirt, 5,
                    ClPlayer, 12),
                RmgenLibrary.ScaleByMapSize(5, 30, MapSize), ClDirt);

            PlaceElephantineStructuresAndFauna(rng, map, settings, areaIsland, areaCityPatch,
                clWater, clIsland, clTemple, clTower, clStatue, clSoldier, clTreasure,
                clPath, AvoidCollisions, stragglerTrees);

            return map.MakeExportable();
        }

        private void PlaceElephantineStructuresAndFauna(RmgenRng rng, RandomMap map,
            MapSettings settings, Area areaIsland, IReadOnlyList<Area> areaCityPatch,
            TileClass clWater, TileClass clIsland, TileClass clTemple, TileClass clTower,
            TileClass clStatue, TileClass clSoldier, TileClass clTreasure, TileClass clPath,
            Func<IConstraint> avoidCollisions, int stragglerTrees)
        {
            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[] { new RandomObject(rng, AStatues, 1, 1, 0, 1) },
                    true, clStatue),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clIsland, RmgenLibrary.ScaleByMapSize(8, 24, MapSize)),
                    new NearTileClassConstraint(clTemple, 10),
                    avoidCollisions(),
                }),
                RmgenLibrary.ScaleByMapSize(2, 10, MapSize), 400, new[] { areaIsland });

            RmgenLibrary.CreateObjectGroupsByAreas(rng,
                new ObjectGroup(new IGroupElement[] { new RandomObject(rng, OTreasure, 1, 2, 0, 1) },
                    true, clTreasure),
                0, avoidCollisions(), RmgenLibrary.ScaleByMapSize(4, 10, MapSize), 100,
                areaCityPatch);

            var kushHeroes = FindKushHeroes(settings.DataRoot);
            var kushUnits = FindKushUnits(settings.DataRoot);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new RandomObject(rng, kushHeroes, 1, 1, 0, 1) },
                    true, clSoldier),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clIsland, RmgenLibrary.ScaleByMapSize(2, 24, MapSize)),
                    new NearTileClassConstraint(clTemple, 14),
                    avoidCollisions(),
                }),
                1, 500);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new RandomObject(rng, kushUnits, 1, 1, 0, 1) },
                    true, clSoldier),
                0,
                new AndConstraint(new IConstraint[]
                {
                    new StaticConstraint(map,
                        RmgenLibrary.StayClasses(clIsland, RmgenLibrary.ScaleByMapSize(2, 24, MapSize)),
                        new NearTileClassConstraint(clTemple, 20)),
                    avoidCollisions(),
                }),
                RmgenLibrary.ScaleByMapSize(12, 60, MapSize), 200);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, OBerryBush, 3, 5, 1, 2) },
                    true, ClFood),
                0, avoidCollisions(), RmgenLibrary.ScaleByMapSize(4, 12, MapSize), 250);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, ORhino, 1, 1, 0, 1) },
                    true, ClFood),
                0, avoidCollisions(), RmgenLibrary.ScaleByMapSize(2, 10, MapSize), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, OWarthog, 1, 1, 0, 1) },
                    true, ClFood),
                0, avoidCollisions(), RmgenLibrary.ScaleByMapSize(2, 10, MapSize), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, OGazelle, 5, 7, 2, 4) },
                    true, ClFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clIsland, 1),
                    avoidCollisions(),
                }),
                RmgenLibrary.ScaleByMapSize(2, 10, MapSize), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, OGiraffe, 2, 3, 2, 4),
                    new ScatterObject(rng, OGiraffeInfant, 2, 3, 2, 4),
                }, true, ClFood),
                0, avoidCollisions(), RmgenLibrary.ScaleByMapSize(2, 10, MapSize), 50);

            if (!settings.Nomad)
                RmgenLibrary.CreateObjectGroups(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, OLion, 1, 2, 2, 4),
                        new ScatterObject(rng, OLioness, 2, 3, 2, 4),
                    }, true, ClFood),
                    0, avoidCollisions(), RmgenLibrary.ScaleByMapSize(2, 10, MapSize), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, OElephant, 2, 3, 2, 4),
                    new ScatterObject(rng, OElephantInfant, 2, 3, 2, 4),
                }, true, ClFood),
                0, avoidCollisions(), RmgenLibrary.ScaleByMapSize(2, 10, MapSize), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, OCrocodile, 2, 3, 3, 5) },
                    true, ClFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    new NearTileClassConstraint(clWater, 3),
                    avoidCollisions(),
                }),
                RmgenLibrary.ScaleByMapSize(2, 10, MapSize), 50);

            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(0, 2, MapSize); ++i)
                map.PlaceEntityAnywhere(OHawk, 0, map.GetCenter(), rng.RandomAngle());

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, OFish, 1, 2, 0, 1) },
                    true, ClFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clWater, 2),
                    RmgenLibrary.AvoidClasses(ClFood, 12),
                }),
                RmgenLibrary.ScaleByMapSize(125, 200, MapSize), 50);

            GaiaEntities.CreateStragglerTrees(rng, new[] { OAcacia }, avoidCollisions(), ClForest,
                stragglerTrees);

            GaiaEntities.CreateDecoration(rng, BushGroups(rng, ABushesDesert, 0, 3, 2, 4),
                RandomScaledCounts(rng, ABushesDesert.Count(), 20, 150),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clIsland, 0),
                    avoidCollisions(),
                }));

            var bushesIslands = ABushesShoreline.Concat(new[]
            {
                "actor|props/flora/foliagebush.xml",
                "actor|props/flora/foliagebush.xml",
                "actor|props/flora/foliagebush.xml",
            }).ToArray();
            GaiaEntities.CreateDecoration(rng, BushGroups(rng, bushesIslands, 0, 4, 2, 4),
                RandomScaledCounts(rng, bushesIslands.Length, 20, 150),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clIsland, 0),
                    avoidCollisions(),
                }));

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, "actor|geology/stone_savanna_med.xml", 0, 4, 2, 4),
                    },
                },
                new[] { RmgenLibrary.ScaleByMapSize(80, 500, MapSize) },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clIsland, 0),
                    avoidCollisions(),
                }));

            GaiaEntities.CreateDecoration(rng, BushGroups(rng, ABushesShoreline, 0, 3, 2, 4),
                RepeatedCount(ABushesShoreline.Length, RmgenLibrary.ScaleByMapSize(200, 1000, MapSize)),
                new AndConstraint(new IConstraint[]
                {
                    new HeightConstraint(map, HeightWaterLevel, HeightShore),
                    avoidCollisions(),
                }));
        }

        private void LoadElephantineHeightmap()
        {
            string? path = Settings.DataRoot != null
                ? Path.Combine(Settings.DataRoot, "maps", "random", "elephantine.png")
                : null;

            float[][] heightmap;
            if (path != null && File.Exists(path))
                heightmap = HeightmapLoader.ConvertHeightmap1Dto2D(
                    HeightmapLoader.LoadHeightmapImage(path));
            else
                heightmap = FallbackElephantineHeightmap();

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new HeightmapPainter(Map, heightmap, MinHeight, MaxHeight), null);
        }

        private static float[][] FallbackElephantineHeightmap()
        {
            const int n = 513;
            var hm = new float[n][];
            for (int x = 0; x < n; ++x)
            {
                hm[x] = new float[n];
                for (int y = 0; y < n; ++y)
                {
                    double dx = Math.Abs(x - (n - 1) / 2.0) / (n / 2.0);
                    double dy = Math.Abs(y - (n - 1) / 2.0) / (n / 2.0);
                    double island = Math.Max(0, 1 - dx * 1.8) * (0.7 + 0.3 * (1 - dy));
                    hm[x][y] = (float)(Math.Max(0, island) * 0xFFFF);
                }
            }
            return hm;
        }

        private static void CreateBumps(RmgenRng rng, RandomMap map, IConstraint constraint,
            double count, double minSize, double maxSize, double spread, double failFraction, double elevation)
            => RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, minSize, maxSize, spread, failFraction),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        elevation, 2, relative: true),
                },
                constraint, count);

        private static void CreatePatches(RmgenRng rng, RandomMap map,
            IReadOnlyList<double> sizes, object terrain, IConstraint constraint,
            double count, TileClass tileClass)
        {
            foreach (double size in sizes)
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, map.GetSize())),
                        size, 0.5),
                    new IPainter[]
                    {
                        new TerrainPainter(terrain, rng),
                        new TileClassPainter(tileClass),
                    },
                    constraint, count);
        }

        private static List<List<RmgenEntity>> CreateObjectGroupsByAreasCaptured(RmgenRng rng,
            RandomMap map, ICenteredObjectGroup group, int player, IConstraint? constraint,
            double amount, int retryFactor, IReadOnlyList<Area> areas)
        {
            var nonEmpty = areas.Where(area => area.PointCount > 0).ToList();
            var results = new List<List<RmgenEntity>>();
            if (nonEmpty.Count == 0)
                return results;

            double maxFail = amount * retryFactor;
            int bad = 0;
            var actualConstraint = constraint ?? new NullConstraint();
            while (results.Count < amount && bad <= maxFail)
            {
                group.SetCenterPosition(rng.PickRandom(rng.PickRandom(nonEmpty).GetPoints()));
                int before = map.Entities.Count;
                if (RmgenLibrary.CreateObjectGroup(group, player, actualConstraint))
                    results.Add(map.Entities.GetRange(before, map.Entities.Count - before));
                else
                    ++bad;
            }
            return results;
        }

        private static string[] ForestWithPalms(string forestFloor)
        {
            var result = new List<string> { forestFloor };
            foreach (string palm in OPalms)
                result.Add(forestFloor + "|" + palm);
            result.Add(forestFloor);
            return result.ToArray();
        }

        private static IGroupElement[][] BushGroups(RmgenRng rng, IReadOnlyList<string> bushes,
            double minCount, double maxCount, double minDistance, double maxDistance)
        {
            var result = new IGroupElement[bushes.Count][];
            for (int i = 0; i < bushes.Count; ++i)
                result[i] = new IGroupElement[]
                {
                    new ScatterObject(rng, bushes[i], minCount, maxCount, minDistance, maxDistance),
                };
            return result;
        }

        private double[] RandomScaledCounts(RmgenRng rng, int count, double min, double max)
        {
            var result = new double[count];
            for (int i = 0; i < count; ++i)
                result[i] = RmgenLibrary.ScaleByMapSize(min, max, MapSize) * rng.RandIntInclusive(1, 3);
            return result;
        }

        private static double[] RepeatedCount(int count, double value)
        {
            var result = new double[count];
            for (int i = 0; i < count; ++i)
                result[i] = value;
            return result;
        }

        private static Area EnsureArea(RandomMap map, Area? area)
            => area ?? new Area(map, new List<RmgenVector2D>());

        private static string[] FindKushHeroes(string? dataRoot)
        {
            string? templateRoot = ResolveTemplatesRoot(dataRoot);
            if (templateRoot == null)
                return KushHeroesFallback;
            string kushRoot = Path.Combine(templateRoot, "units", "kush");
            if (!Directory.Exists(kushRoot))
                return KushHeroesFallback;

            var result = Directory.EnumerateFiles(kushRoot, "hero_*.xml", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => "units/kush/" + Path.GetRelativePath(kushRoot, p)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/')
                    .Replace(".xml", "", StringComparison.Ordinal))
                .ToArray();
            return result.Length == 0 ? KushHeroesFallback : result;
        }

        private static string[] FindKushUnits(string? dataRoot)
        {
            string? templateRoot = ResolveTemplatesRoot(dataRoot);
            if (templateRoot == null)
                return KushUnitsFallback;
            string kushRoot = Path.Combine(templateRoot, "units", "kush");
            if (!Directory.Exists(kushRoot))
                return KushUnitsFallback;

            var wanted = new HashSet<string>(new[] { "Soldier", "Healer", "Female" },
                StringComparer.Ordinal);
            var result = new List<string>();
            foreach (string file in Directory.EnumerateFiles(kushRoot, "*.xml", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.Ordinal))
            {
                string templateName = "units/kush/" + Path.GetFileNameWithoutExtension(file);
                if (templateName.StartsWith("units/kush/hero_", StringComparison.Ordinal))
                    continue;
                if (TemplateHasVisibleClass(templateRoot, templateName, wanted, new HashSet<string>()))
                    result.Add(templateName);
            }
            return result.Count == 0 ? KushUnitsFallback : result.ToArray();
        }

        private static string? ResolveTemplatesRoot(string? dataRoot)
        {
            if (dataRoot == null)
                return null;
            string nested = Path.Combine(dataRoot, "simulation", "templates");
            if (Directory.Exists(nested))
                return nested;
            if (Directory.Exists(Path.Combine(dataRoot, "units")))
                return dataRoot;
            return null;
        }

        private static bool TemplateHasVisibleClass(string templateRoot, string templateName,
            HashSet<string> wanted, HashSet<string> seen)
        {
            templateName = templateName.Contains('|', StringComparison.Ordinal) ?
                templateName.Substring(templateName.LastIndexOf('|') + 1) : templateName;
            if (!seen.Add(templateName))
                return false;

            string path = Path.Combine(templateRoot, templateName.Replace('/', Path.DirectorySeparatorChar) + ".xml");
            if (!File.Exists(path))
                return false;

            try
            {
                var doc = XDocument.Load(path);
                var root = doc.Root;
                string? parent = root?.Attribute("parent")?.Value;
                if (parent != null && TemplateHasVisibleClass(templateRoot, parent, wanted, seen))
                    return true;

                string? visible = root?.Element("Identity")?.Element("VisibleClasses")?.Value;
                if (visible == null)
                    return false;
                return visible.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(wanted.Contains);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private readonly struct CityCenter
        {
            public readonly RmgenVector2D Pos;
            public readonly double Radius;

            public CityCenter(RmgenVector2D pos, double radius)
            {
                Pos = pos;
                Radius = radius;
            }
        }
    }
}
