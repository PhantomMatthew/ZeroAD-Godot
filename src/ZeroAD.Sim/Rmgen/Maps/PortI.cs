using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>fields_of_meroe.js（逐字移植）——尼罗河斜穿地图、两处瀑布浅滩、
    /// 库施村落与金字塔分布在沙丘/农田/河岸之间。fields_of_meroe 专属 biome
    /// 字段在本类内按 dry/rainy 两季补齐；环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class FieldsOfMeroeMap2 : StandardMap
    {
        protected override double HeightLand => 2;

        /// <summary>上游 fields_of_meroe.json SupportedBiomes = "fields_of_meroe/"。</summary>
        protected override IReadOnlyList<string> SupportedBiomes => BiomeLoader.FieldsOfMeroeBiomes;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            var biomeData = InitFieldsOfMeroeContext(rng, settings);
            var map = Map;

            IReadOnlyList<string> tMainDirt = biomeData.MainDirt;
            string tSecondaryDirt = biomeData.SecondaryDirt;
            string tDirt = biomeData.Dirt;
            const string tLush = "desert_grass_a";
            const string tSLush = "desert_grass_a_sand";
            const string tFarmland = "desert_farmland";
            const string tRoad = "savanna_tile_a";
            const string tRoadWild = "desert_city_tile";
            const string tRiverBank = "savanna_riparian_wet";
            const string tForestFloor = "savanna_forestfloor_b";

            string oBush = biomeData.Berry;
            const string oBaobab = "gaia/tree/baobab";
            const string oAcacia = "gaia/tree/acacia";
            const string oDatePalm = "gaia/tree/date_palm";
            const string oSDatePalm = "gaia/tree/cretan_date_palm_short";
            const string oGazelle = "gaia/fauna_gazelle";
            const string oGiraffe = "gaia/fauna_giraffe";
            const string oLion = "gaia/fauna_lion";
            const string oFish = "gaia/fish/generic";
            const string oHawk = "birds/buzzard";
            const string oStoneLarge = "gaia/rock/savanna_large";
            const string oStoneSmall = "gaia/rock/desert_small";
            const string oMetalLarge = "gaia/ore/savanna_large";
            const string oMetalSmall = "gaia/ore/desert_small";

            const string oHouse = "structures/kush/house";
            const string oFarmstead = "structures/kush/farmstead";
            const string oField = "structures/kush/field";
            const string oPyramid = "structures/kush/pyramid_small";
            const string oPyramidLarge = "structures/kush/pyramid_large";
            string oKushUnits = settings.Nomad ?
                "units/kush/support_civilian" :
                "units/kush/infantry_javelineer_merc_e";

            string? aRain = biomeData.Rain;
            string aBushA = biomeData.BushA;
            string aBushB = biomeData.BushB;
            var aBushes = new[] { aBushA, aBushB };
            const string aReeds = "actor|props/flora/reeds_pond_lush_a.xml";
            string aRockA = biomeData.Rock;
            const string aRockB = "actor|geology/shoreline_large.xml";
            const string aRockC = "actor|geology/shoreline_small.xml";

            var pForestP = new[] { tForestFloor + "|" + oAcacia, tForestFloor };

            double heightSeaGround = biomeData.SeaGround;
            const double heightReedsDepth = -2.5;
            const double heightCataract = -1;
            const double heightShore = 1;
            const double heightDunes = 11;
            const double heightOffsetBump = 1.4;
            const double heightOffsetBumpPassage = 4;

            var mapCenter = map.GetCenter();

            var kushVillageBuildings = new (string Template, RmgenVector2D Offset)[]
            {
                (oHouse, new RmgenVector2D(5, 5)),
                (oHouse, new RmgenVector2D(5, 0)),
                (oHouse, new RmgenVector2D(5, -5)),
                (oFarmstead, new RmgenVector2D(-5, 0)),
                (oField, new RmgenVector2D(-5, 5)),
                (oField, new RmgenVector2D(-5, -5)),
                (oPyramid, new RmgenVector2D(0, -5)),
            };

            var clKushiteVillages = new TileClass(MapSize);
            var clRiver = new TileClass(MapSize);
            var clShore = new TileClass(MapSize);
            var clDunes = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clRain = new TileClass(MapSize);
            var clCataract = new TileClass(MapSize);

            var riverTextures = new[]
            {
                new MeroeRiverTexture(RmgenLibrary.FractionToTiles(0, MapSize),
                    RmgenLibrary.FractionToTiles(0.04, MapSize), tLush, clShore),
                new MeroeRiverTexture(RmgenLibrary.FractionToTiles(0.04, MapSize),
                    RmgenLibrary.FractionToTiles(0.06, MapSize), tSLush, clShore),
            };

            const double riverAngle = SafeMath.PI / 5;
            var riverStart = new RmgenVector2D(RmgenLibrary.FractionToTiles(0.25, MapSize), MapSize);
            riverStart.RotateAround(riverAngle, mapCenter);
            var riverEnd = new RmgenVector2D(RmgenLibrary.FractionToTiles(0.25, MapSize), 0);
            riverEnd.RotateAround(riverAngle, mapCenter);
            PortIHelpers.PaintRiver(rng, map, riverStart, riverEnd,
                RmgenLibrary.ScaleByMapSize(12, 36, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 12, MapSize),
                heightSeaGround, heightShore,
                parallel: false, deviation: 1, meanderShort: 14, meanderLong: 18,
                waterFunc: (position, _height, _riverFraction) =>
                {
                    clRiver.Add(position);
                    TerrainFactory.CreateTerrain(tRiverBank).Place(map, rng, position);
                },
                landFunc: (position, shoreDist1, shoreDist2) =>
                {
                    foreach (var riv in riverTextures)
                        if (riv.Left < shoreDist1 && shoreDist1 < riv.Right ||
                            riv.Left < -shoreDist2 && -shoreDist2 < riv.Right)
                        {
                            riv.TileClass.Add(position);
                            TerrainFactory.CreateTerrain(riv.Terrain).Place(map, rng, position);
                        }
                });

            foreach (double x in new[]
            {
                RmgenLibrary.FractionToTiles(rng.RandFloat(0.15, 0.25), MapSize),
                RmgenLibrary.FractionToTiles(rng.RandFloat(0.75, 0.85), MapSize),
            })
            {
                double anglePassage = riverAngle + SafeMath.PI / 2 * rng.RandFloat(0.8, 1.2);
                var start = new RmgenVector2D(x, 0);
                start.RotateAround(anglePassage, mapCenter);
                var end = new RmgenVector2D(x, MapSize);
                end.RotateAround(anglePassage, mapCenter);

                var areaPassage = RmgenLibrary.CreateArea(
                    new PathPlacer(rng, 0, 1, 0, 0, double.PositiveInfinity)
                    {
                        Start = start,
                        End = end,
                        Width = RmgenLibrary.ScaleByMapSize(20, 30, MapSize),
                    },
                    new IPainter[]
                    {
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            heightCataract, 2),
                        new TileClassPainter(clCataract),
                    },
                    new HeightConstraint(map, double.NegativeInfinity, 0));

                if (areaPassage == null)
                    continue;

                var passageAreas = new[] { areaPassage };
                RmgenLibrary.CreateAreasInAreas(rng,
                    new ClumpPlacer(rng, 4, 0.4, 0.6, 0.5),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                            heightOffsetBumpPassage, 2, relative: true),
                    },
                    null,
                    RmgenLibrary.ScaleByMapSize(15, 30, MapSize),
                    20,
                    passageAreas);

                RmgenLibrary.CreateObjectGroupsByAreas(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, aReeds, 2, 4, 0, 1),
                    }, true),
                    0,
                    null,
                    RmgenLibrary.ScaleByMapSize(20, 50, MapSize),
                    20,
                    passageAreas);
            }

            var sortedPlayers = RmgenCommon.SortAllPlayers(rng, settings);
            var placement = PortIHelpers.PlayerPlacementRandom(rng, map, settings, sortedPlayers,
                RmgenLibrary.AvoidClasses(clRiver, 15, ClPlayer, 30));
            if (!placement.HasValue)
                throw new InvalidOperationException("fields_of_meroe: no valid player placement");
            var (playerIDs, playerPosition) = placement.Value;

            PortIHelpers.PlacePlayerBases(rng, map, settings, playerIDs, playerPosition,
                playerTileClass: null,
                cityPatchOuterTerrain: tRoadWild,
                cityPatchInnerTerrain: tRoad,
                cityPatchRadius: 10,
                cityPatchWidth: 3,
                cityPatchPainters: new IPainter[] { new TileClassPainter(ClPlayer) },
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = oBush,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneSmall, "stone_formation", tSecondaryDirt),
                    },
                    MinesGroupElements = new List<IGroupElement>
                    {
                        new RandomObject(rng, aBushes, 2, 4, 2, 3),
                    },
                    TreesTemplate = rng.PickRandom(new[] { oBaobab, oAcacia }),
                    TreesCount = 3,
                });

            var kushiteTownPositions = new List<RmgenVector2D>();
            for (int retryCount = 0; retryCount < RmgenLibrary.ScaleByMapSize(3, 10, MapSize); ++retryCount)
            {
                var coordinate = RmgenCommon.RandomCoordinate(rng, map, passableOnly: true);
                if (RmgenLibrary.AvoidClasses(ClPlayer, 40, ClForest, 5, clKushiteVillages, 50,
                        clRiver, 15).Allows(coordinate))
                {
                    kushiteTownPositions.Add(coordinate);
                    RmgenLibrary.CreateArea(
                        new ClumpPlacer(rng, 40, 0.6, 0.3, double.PositiveInfinity, coordinate),
                        new IPainter[]
                        {
                            new TerrainPainter(tRoad, rng),
                            new TileClassPainter(clKushiteVillages),
                        },
                        null);
                }
            }

            foreach (var coordinate in kushiteTownPositions)
            {
                foreach (var building in kushVillageBuildings)
                    map.PlaceEntityPassable(building.Template, 0,
                        RmgenVector2D.Add(coordinate, building.Offset), SafeMath.PI);

                RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, oKushUnits, 5, 7, 1, 2),
                    }, true, clKushiteVillages, coordinate),
                    0,
                    null);
            }

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oPyramidLarge, 1, 1, 0, 1),
                }, true, clKushiteVillages),
                0,
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 5, clKushiteVillages, 30, clRiver, 10),
                RmgenLibrary.ScaleByMapSize(1, 7, MapSize),
                200);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize), 0.3, 0.06, 1),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump, 2, relative: true),
                },
                new StaticConstraint(map,
                    RmgenLibrary.AvoidClasses(ClPlayer, 5, clKushiteVillages, 10, clRiver, 20)),
                RmgenLibrary.ScaleByMapSize(300, 800, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(5, 15, MapSize)), 0.5),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightDunes, 2),
                    new TileClassPainter(clDunes),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 3, clRiver, 20, clDunes, 10, clKushiteVillages, 10),
                RmgenLibrary.ScaleByMapSize(1, 3, MapSize) * NumPlayers * 3);

            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(400, 2000, 0.7, MapSize);
            GaiaEntities.CreateForests(rng, map,
                new object[] { tMainDirt[0], tForestFloor, tForestFloor, pForestP, pForestP },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 20, clDunes, 2, clRiver, 20,
                    clKushiteVillages, 10),
                ClForest,
                forestTrees,
                NumPlayers);

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
                        new LayeredPainter(new object[] { tSecondaryDirt, tDirt }, new[] { 1 }, rng),
                    },
                    RmgenLibrary.AvoidClasses(clDunes, 0, ClForest, 0, ClPlayer, 5, clRiver, 10),
                    RmgenLibrary.ScaleByMapSize(50, 90, MapSize));

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(30, 40, MapSize),
                RmgenLibrary.ScaleByMapSize(35, 50, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, size, 0.4, 0.6),
                    new IPainter[] { new TerrainPainter(tFarmland, rng) },
                    RmgenLibrary.AvoidClasses(clDunes, 3, ClForest, 3, ClPlayer, 5,
                        clKushiteVillages, 5, clRiver, 10),
                    RmgenLibrary.ScaleByMapSize(1, 10, MapSize));

            var stoneMineConstraint = RmgenLibrary.AvoidClasses(clRiver, 4, clCataract, 4,
                ClPlayer, 20, ClRock, 15, clKushiteVillages, 5, clDunes, 2, ClForest, 4);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                }, true, ClRock),
                0, stoneMineConstraint, RmgenLibrary.ScaleByMapSize(2, 8, MapSize), 50);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3, 0, 2 * SafeMath.PI, 1),
                }, true, ClRock),
                0, stoneMineConstraint, RmgenLibrary.ScaleByMapSize(2, 8, MapSize), 50);

            var metalMineConstraint = RmgenLibrary.AvoidClasses(clRiver, 4, clCataract, 4,
                ClPlayer, 20, ClRock, 10, ClMetal, 15, clKushiteVillages, 5, clDunes, 2, ClForest, 4);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oMetalSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                }, true, ClMetal),
                0, metalMineConstraint, RmgenLibrary.ScaleByMapSize(2, 8, MapSize), 50);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oMetalSmall, 2, 5, 1, 3, 0, 2 * SafeMath.PI, 1),
                }, true, ClMetal),
                0, metalMineConstraint, RmgenLibrary.ScaleByMapSize(2, 8, MapSize), 50);

            var herdConstraint = RmgenLibrary.AvoidClasses(ClForest, 0, clKushiteVillages, 10,
                ClPlayer, 5, clDunes, 1, clFood, 25, clRiver, 2, ClMetal, 4, ClRock, 4);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oGazelle, 4, 6, 1, 4) },
                    true, clFood),
                0, herdConstraint, 2 * NumPlayers, 50);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oGiraffe, 4, 6, 1, 4) },
                    true, clFood),
                0, herdConstraint, 2 * NumPlayers, 50);
            if (!settings.Nomad)
                RmgenLibrary.CreateObjectGroups(rng,
                    new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oLion, 2, 3, 0, 2) },
                        true, clFood),
                    0, herdConstraint, 3 * NumPlayers, 50);

            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(1, 3, MapSize); ++i)
                map.PlaceEntityAnywhere(oHawk, 0, mapCenter, rng.RandomAngle());

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oFish, 1, 2, 0, 1) },
                    true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clRiver, 4),
                    RmgenLibrary.AvoidClasses(clFood, 16, clCataract, 10),
                }),
                RmgenLibrary.ScaleByMapSize(15, 80, MapSize),
                50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oBaobab, oAcacia },
                RmgenLibrary.AvoidClasses(ClForest, 3, clFood, 1, clDunes, 1, ClPlayer, 1,
                    ClMetal, 6, ClRock, 6, clRiver, 15, clKushiteVillages, 15),
                ClForest,
                stragglerTrees);
            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oBaobab, oAcacia },
                RmgenLibrary.AvoidClasses(ClForest, 1, clFood, 1, clDunes, 3, ClPlayer, 1,
                    ClMetal, 6, ClRock, 6, clRiver, 15, clKushiteVillages, 15),
                ClForest,
                stragglerTrees * (settings.Nomad ? 3 : 1));
            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oDatePalm, oSDatePalm },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 5, clFood, 1),
                    RmgenLibrary.StayClasses(clShore, 2),
                }),
                ClForest,
                stragglerTrees * 10);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, aReeds, 3, 5, 0, 1) }, true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    new HeightConstraint(map, heightReedsDepth, heightShore),
                    RmgenLibrary.AvoidClasses(clCataract, 2),
                }),
                RmgenLibrary.ScaleByMapSize(500, 1000, MapSize),
                50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, aRockA, 2, 4, 0, 1) }, true),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, clDunes, 0, clRiver, 5,
                    clCataract, 5, ClMetal, 4, ClRock, 4),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize),
                50);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aRockB, 1, 2, 0, 1),
                    new ScatterObject(rng, aRockC, 1, 3, 0, 1),
                }, true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    new NearTileClassConstraint(clCataract, 5),
                    new HeightConstraint(map, double.NegativeInfinity, heightShore),
                }),
                RmgenLibrary.ScaleByMapSize(30, 50, MapSize),
                50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aBushB, 1, 2, 0, 1),
                    new ScatterObject(rng, aBushA, 1, 3, 0, 2),
                }, true),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, clDunes, 0, clRiver, 15,
                    ClMetal, 4, ClRock, 4),
                RmgenLibrary.ScaleByMapSize(50, 500, MapSize),
                50);

            if (aRain != null)
                RmgenLibrary.CreateObjectGroups(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, aRain, 1, 1, 1, 4),
                    }, true, clRain),
                    0,
                    RmgenLibrary.AvoidClasses(clRain, 5),
                    RmgenLibrary.ScaleByMapSize(60, 200, MapSize));

            return map.MakeExportable();
        }

        private MeroeBiomeData InitFieldsOfMeroeContext(RmgenRng rng, MapSettings settings)
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

            var biomeData = MeroeBiomeData.For(BiomeName);
            Map = new RandomMap(rng, MapSize, HeightLand, biomeData.MainDirt, settings.CircularMap);
            RmgenLibrary.CurrentMap = Map;

            ClPlayer = new TileClass(MapSize);
            ClHill = new TileClass(MapSize);
            ClForest = new TileClass(MapSize);
            ClDirt = new TileClass(MapSize);
            ClRock = new TileClass(MapSize);
            ClMetal = new TileClass(MapSize);
            ClBaseResource = new TileClass(MapSize);
            return biomeData;
        }

        private readonly record struct MeroeRiverTexture(
            double Left, double Right, string Terrain, TileClass TileClass);

        private sealed class MeroeBiomeData
        {
            public readonly IReadOnlyList<string> MainDirt;
            public readonly string SecondaryDirt;
            public readonly string Dirt;
            public readonly string Berry;
            public readonly string BushA;
            public readonly string BushB;
            public readonly string Rock;
            public readonly string? Rain;
            public readonly double SeaGround;

            private MeroeBiomeData(IReadOnlyList<string> mainDirt, string secondaryDirt, string dirt,
                string berry, string bushA, string bushB, string rock, string? rain, double seaGround)
            {
                MainDirt = mainDirt;
                SecondaryDirt = secondaryDirt;
                Dirt = dirt;
                Berry = berry;
                BushA = bushA;
                BushB = bushB;
                Rock = rock;
                Rain = rain;
                SeaGround = seaGround;
            }

            public static MeroeBiomeData For(string biomeName)
            {
                if (biomeName == "fields_of_meroe/rainy")
                    return new MeroeBiomeData(
                        new[] { "savanna_grass_b_wetseason", "savanna_shrubs_a_wetseason" },
                        "savanna_grass_a_wetseason",
                        "savanna_shrubs_a",
                        "gaia/fruit/berry_01",
                        "actor|props/flora/bush_desert_a.xml",
                        "actor|props/flora/bush_desert_a.xml",
                        "actor|geology/stone_granite_greek_med.xml",
                        "actor|particle/rain_shower.xml",
                        -5);

                return new MeroeBiomeData(
                    new[] { "savanna_dirt_b", "savanna_dirt_rocks_a" },
                    "savanna_dirt_a",
                    "savanna_dirt_rocks_c",
                    "gaia/fruit/berry_05",
                    "actor|props/flora/bush_desert_dry_a.xml",
                    "actor|props/flora/bush_desert_dry_a.xml",
                    "actor|geology/stone_desert_med.xml",
                    null,
                    -4);
            }
        }
    }

    /// <summary>latium.js（逐字移植）——两侧平行海湾夹出意大利半岛，按多层噪声
    /// 刷海岸、悬崖、林地和高地；玩家沿中线交错布置。环境设置与 placePlayersNomad
    /// 按既有移植约定省略。</summary>
    public sealed class LatiumMap2 : StandardMap
    {
        private const string tOceanDepths = "medit_sea_depths";
        private const string tOceanRockDeep = "medit_sea_coral_deep";
        private const string tOceanRockShallow = "medit_rocks_wet";
        private const string tOceanCoral = "medit_sea_coral_plants";
        private const string tBeachWet = "medit_sand_wet";
        private const string tBeachDry = "medit_sand";
        private const string tBeachGrass = "medit_rocks_grass";
        private const string tBeachCliff = "medit_dirt";
        private const string tCity = "medit_city_tile";
        private static readonly string[] tGrassDry =
            { "medit_grass_field_brown", "medit_grass_field_dry", "medit_grass_field_b" };
        private static readonly string[] tGrass =
            { "medit_grass_field_dry", "medit_grass_field_brown", "medit_grass_field_b" };
        private static readonly string[] tGrassShrubs = { "medit_grass_shrubs", "medit_grass_flowers" };
        private static readonly string[] tGrassRock = { "medit_rocks_grass" };
        private const string tDirt = "medit_dirt";
        private const string tDirtCliff = "medit_cliff_italia";
        private const string tGrassCliff = "medit_cliff_italia_grass";
        private static readonly string[] tCliff =
            { "medit_cliff_italia", "medit_cliff_italia", "medit_cliff_italia_grass" };
        private const string tForestFloor = "medit_grass_wild";

        private const string oBeech = "gaia/tree/euro_beech";
        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oCarob = "gaia/tree/carob";
        private const string oCypress1 = "gaia/tree/cypress";
        private const string oCypress2 = "gaia/tree/cypress";
        private const string oLombardyPoplar = "gaia/tree/poplar_lombardy";
        private const string oPalm = "gaia/tree/medit_fan_palm";
        private const string oPine = "gaia/tree/aleppo_pine";
        private const string oDeer = "gaia/fauna_deer";
        private const string oFish = "gaia/fish/generic";
        private const string oSheep = "gaia/fauna_sheep";
        private const string oStoneLarge = "gaia/rock/mediterranean_large";
        private const string oStoneSmall = "gaia/rock/mediterranean_small";
        private const string oMetalLarge = "gaia/ore/mediterranean_large";

        private const string aBushMedDry = "actor|props/flora/bush_medit_me_dry.xml";
        private const string aBushMed = "actor|props/flora/bush_medit_me.xml";
        private const string aBushSmall = "actor|props/flora/bush_medit_sm.xml";
        private const string aBushSmallDry = "actor|props/flora/bush_medit_sm_dry.xml";
        private const string aGrass = "actor|props/flora/grass_soft_large_tall.xml";
        private const string aGrassDry = "actor|props/flora/grass_soft_dry_large_tall.xml";
        private const string aRockLarge = "actor|geology/stone_granite_large.xml";
        private const string aRockMed = "actor|geology/stone_granite_med.xml";
        private const string aRockSmall = "actor|geology/stone_granite_small.xml";

        protected override double HeightLand => 0;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tGrass);
            var map = Map;

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

            const double heightSeaGround = -16;
            const double heightLand = 0;
            const double heightPlayer = 5;
            const double heightHill = 12;
            const double waterWidth = 0.1;

            var mapCenter = map.GetCenter();
            var clWater = new TileClass(MapSize);
            var clCliff = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);

            double startAngle = rng.RandBool() ? 0 : SafeMath.PI / 2;
            var playerPosition = PortIHelpers.PlayerPlacementLine(
                startAngle + SafeMath.PI / 2,
                mapCenter,
                RmgenLibrary.FractionToTiles(rng.RandFloat(0.42, 0.46), MapSize),
                NumPlayers,
                MapSize);

            double DistanceToPlayers(double x, double z)
            {
                double r = double.PositiveInfinity;
                for (int i = 0; i < NumPlayers; ++i)
                {
                    double dx = x - RmgenLibrary.TilesToFraction(playerPosition[i].X, MapSize);
                    double dz = z - RmgenLibrary.TilesToFraction(playerPosition[i].Y, MapSize);
                    r = Math.Min(r, SafeMath.Square(dx) + SafeMath.Square(dz));
                }
                return SafeMath.Sqrt(r);
            }

            double PlayerNearness(double x, double z)
            {
                double d = RmgenLibrary.FractionToTiles(DistanceToPlayers(x, z), MapSize);
                if (d < 13)
                    return 0;
                if (d < 19)
                    return (d - 13) / (19 - 13);
                return 1;
            }

            foreach (double x in new[] { 0.0, (double)MapSize })
            {
                var riverStart = new RmgenVector2D(x, MapSize);
                riverStart.RotateAround(startAngle, mapCenter);
                var riverEnd = new RmgenVector2D(x, 0);
                riverEnd.RotateAround(startAngle, mapCenter);
                PortIHelpers.PaintRiver(rng, map, riverStart, riverEnd,
                    2 * RmgenLibrary.FractionToTiles(waterWidth, MapSize),
                    16,
                    heightSeaGround,
                    heightLand,
                    parallel: true,
                    deviation: 0,
                    meanderShort: 0,
                    meanderLong: 0,
                    waterFunc: (position, _height, _riverFraction) => clWater.Add(position),
                    landFunc: null);
            }

            var noise0 = new Noise2D(rng, RmgenLibrary.ScaleByMapSize(4, 16, MapSize));
            var noise1 = new Noise2D(rng, RmgenLibrary.ScaleByMapSize(8, 32, MapSize));
            var noise2 = new Noise2D(rng, RmgenLibrary.ScaleByMapSize(15, 60, MapSize));
            var noise2a = new Noise2D(rng, RmgenLibrary.ScaleByMapSize(20, 80, MapSize));
            var noise2b = new Noise2D(rng, RmgenLibrary.ScaleByMapSize(35, 140, MapSize));
            var noise3 = new Noise2D(rng, RmgenLibrary.ScaleByMapSize(4, 16, MapSize));
            var noise4 = new Noise2D(rng, RmgenLibrary.ScaleByMapSize(6, 24, MapSize));
            var noise5 = new Noise2D(rng, RmgenLibrary.ScaleByMapSize(11, 44, MapSize));

            for (int ix = 0; ix <= MapSize; ++ix)
                for (int iz = 0; iz <= MapSize; ++iz)
                {
                    var position = new RmgenVector2D(ix, iz);
                    double x = ix / (MapSize + 1.0);
                    double z = iz / (MapSize + 1.0);
                    double pn = PlayerNearness(x, z);
                    double c = startAngle != 0 ? z : x;
                    double distToWater = clWater.Has(position) ? 0 : 0.5 - waterWidth - Math.Abs(c - 0.5);
                    double h = distToWater != 0 ?
                        heightHill * (1 - Math.Abs(c - 0.5) / (0.5 - waterWidth)) :
                        map.GetHeight(position);

                    double baseNoise =
                        16 * noise0.Get(x, z) +
                        8 * noise1.Get(x, z) +
                        4 * noise2.Get(x, z) -
                        (16 + 8 + 4) / 2.0;
                    if (baseNoise < 0)
                    {
                        baseNoise *= pn;
                        baseNoise *= Math.Max(0.1, distToWater / (0.5 - waterWidth));
                    }
                    double oldH = h;
                    h += baseNoise;

                    if (oldH > 0)
                        h += (0.4 * noise2a.Get(x, z) + 0.2 * noise2b.Get(x, z)) *
                            Math.Min(oldH / 10, 1);

                    if (h > -10)
                    {
                        double cliffNoise = (noise3.Get(x, z) + 0.5 * noise4.Get(x, z)) / 1.5;
                        if (h < 1)
                        {
                            double u = 1 - 0.3 * ((h - 1) / -10);
                            cliffNoise *= u;
                        }
                        cliffNoise += 0.05 * distToWater / (0.5 - waterWidth);
                        if (cliffNoise > 0.6)
                        {
                            double u = 0.8 * (cliffNoise - 0.6);
                            cliffNoise += u * noise5.Get(x, z);
                            cliffNoise /= 1 + u;
                        }
                        cliffNoise -= 0.59;
                        cliffNoise *= pn;
                        if (cliffNoise > 0)
                            h += 19 * Math.Min(cliffNoise, 0.045) / 0.045;
                    }
                    map.SetHeight(position, h);
                }

            var noise6 = new Noise2D(rng, RmgenLibrary.ScaleByMapSize(10, 40, MapSize));
            var noise7 = new Noise2D(rng, RmgenLibrary.ScaleByMapSize(20, 80, MapSize));
            var noise8 = new Noise2D(rng, RmgenLibrary.ScaleByMapSize(13, 52, MapSize));
            var noise9 = new Noise2D(rng, RmgenLibrary.ScaleByMapSize(26, 104, MapSize));
            var noise10 = new Noise2D(rng, RmgenLibrary.ScaleByMapSize(50, 200, MapSize));

            for (int ix = 0; ix < MapSize; ++ix)
                for (int iz = 0; iz < MapSize; ++iz)
                {
                    var position = new RmgenVector2D(ix, iz);
                    double x = ix / (MapSize + 1.0);
                    double z = iz / (MapSize + 1.0);
                    double pn = PlayerNearness(x, z);

                    double minH = double.PositiveInfinity;
                    double maxH = double.NegativeInfinity;
                    foreach (var vertex in RmgenGeometry.TileVertices)
                    {
                        double height = map.GetHeight(RmgenVector2D.Add(position, vertex));
                        minH = Math.Min(minH, height);
                        maxH = Math.Max(maxH, height);
                    }
                    double diffH = maxH - minH;

                    double minAdjHeight = minH;
                    if (maxH > 15)
                    {
                        int maxNx = Math.Min(ix + 2, MapSize);
                        int maxNz = Math.Min(iz + 2, MapSize);
                        for (int nx = Math.Max(ix - 1, 0); nx <= maxNx; ++nx)
                            for (int nz = Math.Max(iz - 1, 0); nz <= maxNz; ++nz)
                                minAdjHeight = Math.Min(minAdjHeight,
                                    map.GetHeight(new RmgenVector2D(nx, nz)));
                    }

                    object t = tGrass;

                    if (maxH < -12)
                        t = tOceanDepths;
                    else if (maxH < -8.8)
                        t = tOceanRockDeep;
                    else if (maxH < -4.7)
                        t = tOceanCoral;
                    else if (maxH < -2.8)
                        t = tOceanRockShallow;
                    else if (maxH < 0.9 && minH < 0.35)
                        t = tBeachWet;
                    else if (maxH < 1.5 && minH < 0.9)
                        t = tBeachDry;
                    else if (maxH < 2.3 && minH < 1.3)
                        t = tBeachGrass;

                    if (minH < 0)
                        clWater.Add(position);

                    if (diffH > 2.9 && minH > -7)
                    {
                        t = tCliff;
                        clCliff.Add(position);
                    }
                    else if (diffH > 2.5 && minH > -5 || maxH - minAdjHeight > 2.9 && minH > 0)
                    {
                        if (minH < -1)
                            t = tCliff;
                        else if (minH < 0.5)
                            t = tBeachCliff;
                        else
                            t = new object[] { tDirtCliff, tGrassCliff, tGrassCliff, tGrassRock, tCliff };

                        clCliff.Add(position);
                    }

                    if (minH >= 20)
                        clCliff.Add(position);

                    if (map.GetHeight(position) < 11 && diffH < 2 && minH > 1)
                    {
                        double forestNoise = (noise6.Get(x, z) + 0.5 * noise7.Get(x, z)) / 1.5 * pn - 0.59;
                        if (forestNoise > 0 && rng.RandBool())
                        {
                            if (minH < 11 && minH >= 4)
                            {
                                double typeNoise = noise10.Get(x, z);
                                if (typeNoise < 0.43 && forestNoise < 0.05)
                                    t = pPoplarForest;
                                else if (typeNoise < 0.63)
                                    t = pMainForest;
                                else
                                    t = pPineForest;

                                ClForest.Add(position);
                            }
                            else if (minH < 4)
                            {
                                t = pPalmForest;
                                ClForest.Add(position);
                            }
                        }
                    }

                    if (ReferenceEquals(t, tGrass))
                    {
                        double grassNoise = (noise8.Get(x, z) + 0.6 * noise9.Get(x, z)) / 1.6;
                        if (grassNoise < 0.3)
                            t = diffH > 1.2 ? tDirtCliff : tDirt;
                        else if (grassNoise < 0.34)
                        {
                            t = diffH > 1.2 ? tGrassCliff : tGrassDry;
                            if (diffH < 0.5 && rng.RandBool(0.02))
                                map.PlaceEntityAnywhere(aGrassDry, 0,
                                    RmgenLibrary.RandomPositionOnTile(rng, position), rng.RandomAngle());
                        }
                        else if (grassNoise > 0.61)
                            t = diffH > 1.2 ? tGrassRock : tGrassShrubs;
                        else if (diffH < 0.5 && rng.RandBool(0.02))
                            map.PlaceEntityAnywhere(aGrass, 0,
                                RmgenLibrary.RandomPositionOnTile(rng, position), rng.RandomAngle());
                    }

                    TerrainFactory.CreateTerrain(t).Place(map, rng, position);
                }

            PortIHelpers.PlacePlayerBases(rng, map, settings,
                PortIHelpers.PrimeSortAllPlayers(rng, settings),
                playerPosition,
                playerTileClass: ClPlayer,
                cityPatchOuterTerrain: tGrass,
                cityPatchInnerTerrain: tCity,
                cityPatchRadius: 11,
                cityPatchWidth: 4,
                cityPatchPainters: new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightPlayer, 2),
                },
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    ExtraBaseResourceConstraint = RmgenLibrary.AvoidClasses(clCliff, 4),
                    StartingAnimal = true,
                    BerriesTemplate = oBerryBush,
                    BerriesDistance = 9,
                    Mines = new()
                    {
                        (oMetalLarge, (string?)null, (object?)null),
                        (oStoneLarge, (string?)null, (object?)null),
                    },
                    TreesTemplate = oPalm,
                    TreesCount = 5,
                    TreesMinDist = 10,
                    TreesMaxDist = 11,
                });

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aBushSmall, 0, 2, 0, 2),
                    new ScatterObject(rng, aBushSmallDry, 0, 2, 0, 2),
                    new ScatterObject(rng, aBushMed, 0, 1, 0, 2),
                    new ScatterObject(rng, aBushMedDry, 0, 1, 0, 2),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 4, clCliff, 2),
                RmgenLibrary.ScaleByMapSize(9, 146, MapSize),
                50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aRockSmall, 0, 3, 0, 2),
                    new ScatterObject(rng, aRockMed, 0, 2, 0, 2),
                    new ScatterObject(rng, aRockLarge, 0, 1, 0, 2),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 2, clCliff, 1),
                RmgenLibrary.ScaleByMapSize(9, 146, MapSize),
                50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                }, true, ClRock),
                0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 4, ClPlayer, 40, ClRock, 60,
                    ClMetal, 10, clCliff, 3),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize),
                100);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3),
                }, true, ClRock),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 4, clWater, 1, ClPlayer, 40, ClRock, 30,
                    ClMetal, 10, clCliff, 3),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize),
                100);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oMetalLarge, 1, 1, 0, 2),
                }, true, ClMetal),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 4, clWater, 1, ClPlayer, 40, ClMetal, 50,
                    clCliff, 3),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize),
                100);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oCarob, oBeech, oLombardyPoplar, oLombardyPoplar, oPine },
                RmgenLibrary.AvoidClasses(clWater, 5, clCliff, 4, ClForest, 2, ClPlayer, 15,
                    ClMetal, 6, ClRock, 6),
                ClForest,
                RmgenLibrary.ScaleByMapSize(10, 190, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oCypress2, 1, 3, 0, 3),
                    new ScatterObject(rng, oCypress1, 0, 2, 0, 2),
                }, true),
                0,
                RmgenLibrary.AvoidClasses(clWater, 5, clCliff, 4, ClForest, 2, ClPlayer, 15,
                    ClMetal, 6, ClRock, 6),
                RmgenLibrary.ScaleByMapSize(5, 75, MapSize),
                50);

            var landFoodConstraint = RmgenLibrary.AvoidClasses(clWater, 5, ClForest, 2, clCliff, 1,
                ClPlayer, 20, ClMetal, 6, ClRock, 6, clFood, 8);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oSheep, 2, 4, 0, 2) },
                    true, clFood),
                0, landFoodConstraint, 3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oFish, 1, 1, 0, 1) },
                    true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 10),
                    RmgenLibrary.StayClasses(clWater, 4),
                    new HeightConstraint(map, double.NegativeInfinity, heightLand),
                }),
                RmgenLibrary.ScaleByMapSize(45, 65, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oDeer, 5, 7, 0, 4) },
                    true, clFood),
                0, landFoodConstraint, 3 * NumPlayers, 50);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oBerryBush, 5, 7, 0, 3) },
                    true, clFood),
                0, landFoodConstraint, 1.5 * NumPlayers, 100);

            return map.MakeExportable();
        }
    }

    /// <summary>extinct_volcano.js（逐字移植）——玩家起点在小火山平台上，中央火山
    /// 由五层同心 ClumpPlacer 塑出高锥与凹陷火山口；周边湖泊、火山灰、草斑、
    /// 矿点和持续降雨装饰构成洪水触发图的地形基础。extinct_volcano_triggers.js、
    /// 环境设置、Walls="towers" 起始墙和 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class ExtinctVolcanoMap2 : StandardMap
    {
        private const string tHillDark = "cliff volcanic light";
        private const string tHillMedium1 = "ocean_rock_a";
        private const string tHillMedium2 = "ocean_rock_b";
        private static readonly string[] tHillVeryDark = { "cliff volcanic coarse", "cave_walls" };
        private const string tRoad = "road1";
        private const string tRoadWild = "road1";
        private const string tForestFloor1 = tHillMedium1;
        private const string tForestFloor2 = tHillMedium2;
        private const string tGrassA = "cliff volcanic light";
        private const string tGrassB = "ocean_rock_a";
        private const string tGrassPatchBlend = "temp_grass_long_b";
        private static readonly string[] tGrassPatch = { "temp_grass_d", "temp_grass_clovers" };
        private const string tShoreBlend = "cliff volcanic light";
        private const string tShore = "ocean_rock_a";
        private const string tWater = "ocean_rock_b";

        private const string oTree = "gaia/tree/dead";
        private const string oTree2 = "gaia/tree/euro_beech";
        private const string oTree3 = "gaia/tree/oak";
        private const string oTree4 = "gaia/tree/oak_dead";
        private const string oBush = "gaia/tree/bush_temperate";
        private const string oFruitBush = "gaia/fruit/berry_01";
        private const string oRabbit = "gaia/fauna_rabbit";
        private const string oGoat = "gaia/fauna_goat";
        private const string oBear = "gaia/fauna_bear_brown";
        private const string oStoneLarge = "gaia/rock/temperate_large";
        private const string oStoneSmall = "gaia/rock/temperate_small";
        private const string oMetalLarge = "gaia/ore/temperate_large";
        private const string oTower = "structures/palisades_fort";

        private const string aRockLarge = "actor|geology/stone_granite_med.xml";
        private const string aRockMedium = "actor|geology/stone_granite_med.xml";
        private const string aBushMedium = "actor|props/flora/bush_tempe_me.xml";
        private const string aBushSmall = "actor|props/flora/bush_tempe_sm.xml";
        private const string aGrass = "actor|props/flora/grass_soft_large_tall.xml";
        private const string aGrassShort = "actor|props/flora/grass_soft_large.xml";
        private const string aRain = "actor|particle/rain_shower.xml";

        protected override double HeightLand => 1;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tHillMedium1);
            var map = Map;
            var mapCenter = map.GetCenter();

            var pForestD = new[]
            {
                tForestFloor1 + "|" + oTree,
                tForestFloor2 + "|" + oTree2,
                tForestFloor1,
            };
            var pForestP = new[]
            {
                tForestFloor1 + "|" + oTree3,
                tForestFloor2 + "|" + oTree4,
                tForestFloor1,
            };

            const double heightSeaGround = -4;
            const double heightHill = 18;
            const double heightPlayerHill = 25;

            var clFood = new TileClass(MapSize);
            var clWater = new TileClass(MapSize);
            var clGrass = new TileClass(MapSize);
            var clBumps = new TileClass(MapSize);
            var clTower = new TileClass(MapSize);
            var clRain = new TileClass(MapSize);

            double playerMountainSize = RmgenCommon.DefaultPlayerBaseRadius(MapSize);
            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng,
                map,
                settings,
                settings.PlayerPlacement,
                RmgenLibrary.FractionToTiles(0.35, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize),
                rng.RandomAngle());

            if (!settings.Nomad)
                for (int i = 0; i < NumPlayers; ++i)
                {
                    PortIHelpers.CreateMountain(rng, map,
                        heightPlayerHill,
                        (int)playerMountainSize,
                        (int)playerMountainSize,
                        (int)Math.Floor(RmgenLibrary.ScaleByMapSize(4, 10, MapSize)),
                        null,
                        (int)playerPosition[i].X,
                        (int)playerPosition[i].Y,
                        tHillDark,
                        ClPlayer,
                        14);

                    RmgenLibrary.CreateArea(
                        new ClumpPlacer(rng, RmgenGeometry.DiskArea(playerMountainSize),
                            0.95, 0.6, double.PositiveInfinity, playerPosition[i]),
                        new IPainter[]
                        {
                            new LayeredPainter(new object[] { tHillVeryDark, tHillMedium1 },
                                new double[] { playerMountainSize }, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                                heightPlayerHill, playerMountainSize),
                            new TileClassPainter(ClPlayer),
                        },
                        null);
                }

            PortIHelpers.PlacePlayerBases(rng, map, settings, playerIDs, playerPosition,
                playerTileClass: null,
                cityPatchOuterTerrain: tRoadWild,
                cityPatchInnerTerrain: tRoad,
                cityPatchRadius: null,
                cityPatchWidth: 1,
                cityPatchPainters: null,
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
                    TreesTemplate = oTree2,
                });

            PortIHelpers.CreateVolcano(rng, map, mapCenter, ClHill, tHillVeryDark,
                lavaTextures: null, smoke: false, relative: false);

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 5, 6, Math.Floor(RmgenLibrary.ScaleByMapSize(10, 14, MapSize)), 0.1),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tShoreBlend, tShore, tWater }, new[] { 1, 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaGround, 3),
                    new TileClassPainter(clWater),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 0, ClHill, 2, clWater, 12),
                SafeMath.Round(RmgenLibrary.ScaleByMapSize(6, 12, MapSize)));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, 10, 3, 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        RmgenLibrary.ScaleByMapSize(4, 10, MapSize), 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 0, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(50, 300, MapSize));
            RmgenLibrary.PaintTileClassBasedOnHeight(10, 100,
                HeightPlacer.Mode.ExcludeMinExcludeMax, clBumps);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 150, MapSize),
                    0.2, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tHillDark, tHillDark, tHillDark }, new[] { 2, 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightHill, 2),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 0, ClHill, 15, clWater, 2, ClBaseResource, 2),
                RmgenLibrary.ScaleByMapSize(2, 8, MapSize) * NumPlayers);

            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(1200, 3000, 0.7, MapSize);
            var forestTypes = new object[][]
            {
                new object[]
                {
                    new object[] { tGrassB, tGrassA, pForestD },
                    new object[] { tGrassB, pForestD },
                },
                new object[]
                {
                    new object[] { tGrassB, tGrassA, pForestP },
                    new object[] { tGrassB, pForestP },
                },
            };
            double size = forestTrees / (RmgenLibrary.ScaleByMapSize(4, 12, MapSize) * NumPlayers);
            double num = Math.Floor(size / forestTypes.Length);
            foreach (var type in forestTypes)
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, forestTrees / num, 0.1, 0.1, double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 4, ClForest, 10, ClHill, 0, clWater, 2),
                    num);

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 84, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 128, MapSize),
            })
                foreach (var type in new object[][]
                {
                    new object[] { tHillMedium1, tHillDark },
                    new object[] { tHillDark, tHillMedium2 },
                    new object[] { tHillMedium1, tHillMedium2 },
                })
                    RmgenLibrary.CreateAreas(rng,
                        new ClumpPlacer(rng, patchSize, 0.3, 0.06, 0.5),
                        new IPainter[]
                        {
                            new LayeredPainter(type, new[] { 1 }, rng),
                            new TileClassPainter(clGrass),
                        },
                        RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClHill, 0,
                            clBumps, 0, ClPlayer, 0),
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
                        new LayeredPainter(new object[] { tGrassPatchBlend, tGrassPatch }, new[] { 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClHill, 0, clGrass, 5,
                        clBumps, 0, ClPlayer, 0),
                    RmgenLibrary.ScaleByMapSize(3, 8, MapSize));

            var mineBumpConstraint = new AndConstraint(new IConstraint[]
            {
                RmgenLibrary.StayClasses(clBumps, 1),
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 0, ClRock, 15, ClHill, 0),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4),
                    new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4),
                }, true, ClRock),
                0, mineBumpConstraint, 100, 100);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3) },
                    true, ClRock),
                0, mineBumpConstraint, 100, 100);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) },
                    true, ClMetal),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clBumps, 1),
                    RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 0, ClMetal, 15,
                        ClRock, 10, ClHill, 0),
                }),
                100,
                100);

            if (!settings.Nomad)
                RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                    new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oTower, 1, 1, 0, 4) },
                        true, clTower),
                    0,
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.StayClasses(clBumps, 3),
                        RmgenLibrary.AvoidClasses(ClMetal, 5, ClRock, 5, ClHill, 0, clTower, 60,
                            ClPlayer, 10, ClForest, 2),
                    }),
                    500,
                    1);

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, aGrassShort, 1, 2, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aGrass, 2, 4, 0, 1.8),
                        new ScatterObject(rng, aGrassShort, 3, 6, 1.2, 2.5),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aBushMedium, 1, 2, 0, 2),
                        new ScatterObject(rng, aBushSmall, 2, 4, 0, 2),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(15, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(15, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(15, MapSize, settings.CircularMap),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clGrass, 0),
                    RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 0),
                }));

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
                    RmgenLibrary.ScaleByMapSize(15, 250, MapSize),
                    RmgenLibrary.ScaleByMapSize(15, 150, MapSize),
                },
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 0));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oRabbit, 5, 7, 2, 4) },
                    new IGroupElement[] { new ScatterObject(rng, oGoat, 3, 5, 2, 4) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(1, 6, MapSize) * NumPlayers,
                    RmgenLibrary.ScaleByMapSize(3, 10, MapSize) * NumPlayers,
                },
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClPlayer, 0, ClHill, 1, clFood, 20),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oBear, 1, 1, 0, 2) },
                },
                new double[] { 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClPlayer, 0, ClHill, 1, clFood, 20),
                    RmgenLibrary.StayClasses(ClForest, 2),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oFruitBush, 1, 2, 0, 4) },
                },
                new double[] { 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clGrass, 1),
                    RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClPlayer, 0, ClHill, 1, clFood, 10),
                }),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oTree, oTree2, oTree3, oTree4, oBush },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clGrass, 1),
                    RmgenLibrary.AvoidClasses(clWater, 5, ClForest, 1, ClHill, 1, ClPlayer, 0,
                        ClMetal, 4, ClRock, 4),
                }),
                ClForest,
                stragglerTrees);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oBush, 1, 3, 0, 3) },
                    true, ClForest),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clGrass, 3),
                    RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 1, ClPlayer, 0, ClMetal, 4, ClRock, 4),
                }),
                stragglerTrees);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, aRain, 2, 2, 1, 4) },
                    true, clRain),
                0,
                RmgenLibrary.AvoidClasses(clRain, 5),
                RmgenLibrary.ScaleByMapSize(80, 250, MapSize));

            return map.MakeExportable();
        }
    }

    internal static class PortIHelpers
    {
        public static void PaintRiver(RmgenRng rng, RandomMap map,
            RmgenVector2D start, RmgenVector2D end, double width, double fadeDist,
            double heightRiverbed, double heightLand, bool parallel, double deviation,
            double meanderShort, double meanderLong,
            Action<RmgenVector2D, double, double>? waterFunc,
            Action<RmgenVector2D, double, double>? landFunc,
            double? minHeight = null)
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
                            height += (heightLand - heightRiverbed) * (1 + shoreDist1 / fadeDist);
                        else if (shoreDist2 < fadeDist)
                            height += (heightLand - heightRiverbed) * (1 - shoreDist2 / fadeDist);

                        if (!minHeight.HasValue || height < minHeight.Value)
                            map.SetHeight(vecPoint, height);
                        waterFunc?.Invoke(vecPoint, height, riverFraction);
                    }
                    else
                    {
                        landFunc?.Invoke(vecPoint, shoreDist1, shoreDist2);
                    }
                }
        }

        public static List<RmgenVector2D> PlayerPlacementLine(double angle, RmgenVector2D center,
            double width, int numPlayers, int mapSize)
        {
            var playerPosition = new List<RmgenVector2D>();
            for (int i = 0; i < numPlayers; ++i)
            {
                var offset = new RmgenVector2D(
                    RmgenLibrary.FractionToTiles((i + 1.0) / (numPlayers + 1) - 0.5, mapSize),
                    width * (i % 2 - 0.5));
                offset.Rotate(angle);
                var position = RmgenVector2D.Add(center, offset);
                position.Round();
                playerPosition.Add(position);
            }
            return playerPosition;
        }

        public static List<int> PrimeSortAllPlayers(RmgenRng rng, MapSettings settings)
        {
            var playerIDs = RmgenCommon.SortAllPlayers(rng, settings);
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

        public static (List<int> playerIDs, List<RmgenVector2D> playerPosition)? PlayerPlacementRandom(
            RmgenRng rng, RandomMap map, MapSettings settings, List<int> playerIDs, IConstraint? constraints)
        {
            int numPlayers = RmgenCommon.GetNumPlayers(settings);
            var locations = new List<RmgenVector2D>();
            int attempts = 0;
            int resets = 0;

            var mapCenter = map.GetCenter();
            double playerMinDistSquared = SafeMath.Square(RmgenLibrary.FractionToTiles(0.25, map.GetSize()));
            double borderDistance = RmgenLibrary.FractionToTiles(0.08, map.GetSize());
            var area = RmgenLibrary.CreateArea(new MapBoundsPlacer(), (IPainter?)null, constraints);
            if (area == null)
                return null;

            for (int i = 0; i < numPlayers; ++i)
            {
                if (area.PointCount == 0)
                    return null;
                var position = rng.PickRandom(area.GetPoints());

                bool tooClose = false;
                foreach (var loc in locations)
                    if (loc.DistanceToSquared(position) < playerMinDistSquared)
                    {
                        tooClose = true;
                        break;
                    }

                if (tooClose ||
                    position.DistanceToSquared(mapCenter) > SafeMath.Square(mapCenter.X - borderDistance))
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

        public static void PlacePlayerBases(RmgenRng rng, RandomMap map, MapSettings settings,
            IReadOnlyList<int> playerIDs, IReadOnlyList<RmgenVector2D> playerPositions,
            TileClass? playerTileClass, object? cityPatchOuterTerrain, object? cityPatchInnerTerrain,
            double? cityPatchRadius, double cityPatchWidth, IReadOnlyList<IPainter>? cityPatchPainters,
            RmgenCommon.PlayerBaseOptions? options)
        {
            int numPlayers = RmgenCommon.GetNumPlayers(settings);
            for (int i = 0; i < numPlayers; ++i)
                PlacePlayerBase(rng, map, settings, playerIDs[i], playerPositions[i], playerTileClass,
                    cityPatchOuterTerrain, cityPatchInnerTerrain, cityPatchRadius, cityPatchWidth,
                    cityPatchPainters, options);
        }

        public static void CreateMountain(RmgenRng rng, RandomMap map, double maxHeight,
            int minRadius, int maxRadius, int numCircles, IConstraint? constraints,
            int x, int z, object? terrain, TileClass? tileClass, int fcc)
        {
            var position = new RmgenVector2D(x, z);
            var constraint = constraints ?? new NullConstraint();
            if (!map.InMapBounds(position) || !constraint.Allows(position))
                return;

            int mapSize = map.GetSize();
            var gotRet = new int[mapSize, mapSize];
            for (int i = 0; i < mapSize; ++i)
                for (int j = 0; j < mapSize; ++j)
                    gotRet[i, j] = -1;

            --mapSize;
            minRadius = Math.Max(1, Math.Min(minRadius, maxRadius));
            var edges = new List<(int X, int Z)> { (x, z) };
            var circles = new List<(int X, int Z, int Radius)>();

            for (int i = 0; i < numCircles; ++i)
            {
                bool badPoint = false;
                var center = rng.PickRandom(edges);
                int radius = rng.RandIntInclusive(minRadius, maxRadius);
                int sx = Math.Max(0, center.X - radius);
                int sz = Math.Max(0, center.Z - radius);
                int lx = Math.Min(center.X + radius, mapSize);
                int lz = Math.Min(center.Z + radius, mapSize);
                double radius2 = SafeMath.Square(radius);

                for (int ix = sx; ix <= lx; ++ix)
                {
                    for (int iz = sz; iz <= lz; ++iz)
                    {
                        var pos = new RmgenVector2D(ix, iz);
                        // 上游误把欧氏距离与 radius² 比较；这里保留以复现山体外形。
                        if (SafeMath.EuclidDistance2D(ix, iz, center.X, center.Z) > radius2 ||
                            !map.InMapBounds(pos))
                            continue;

                        if (!constraint.Allows(pos))
                        {
                            badPoint = true;
                            break;
                        }

                        int state = gotRet[ix, iz];
                        if (state == -1)
                            gotRet[ix, iz] = -2;
                        else if (state >= 0)
                        {
                            edges.RemoveAt(state);
                            gotRet[ix, iz] = -2;
                            for (int k = state; k < edges.Count; ++k)
                                --gotRet[edges[k].X, edges[k].Z];
                        }
                    }
                    if (badPoint)
                        break;
                }

                if (badPoint)
                    continue;

                circles.Add((center.X, center.Z, radius));
                for (int ix = sx; ix <= lx; ++ix)
                    for (int iz = sz; iz <= lz; ++iz)
                    {
                        if (gotRet[ix, iz] != -2 ||
                            fcc != 0 && (x - ix > fcc || ix - x > fcc || z - iz > fcc || iz - z > fcc) ||
                            ix > 0 && gotRet[ix - 1, iz] == -1 ||
                            iz > 0 && gotRet[ix, iz - 1] == -1 ||
                            ix < mapSize && gotRet[ix + 1, iz] == -1 ||
                            iz < mapSize && gotRet[ix, iz + 1] == -1)
                            continue;

                        edges.Add((ix, iz));
                        gotRet[ix, iz] = edges.Count - 1;
                    }
            }

            foreach (var circle in circles)
            {
                var circlePosition = new RmgenVector2D(circle.X, circle.Z);
                int sx = Math.Max(0, circle.X - circle.Radius);
                int sz = Math.Max(0, circle.Z - circle.Radius);
                int lx = Math.Min(circle.X + circle.Radius, mapSize);
                int lz = Math.Min(circle.Z + circle.Radius, mapSize);
                double clumpHeight = (double)circle.Radius / maxRadius * maxHeight * rng.RandFloat(0.8, 1.2);
                var terrainObj = terrain != null ? TerrainFactory.CreateTerrain(terrain) : null;

                for (int ix = sx; ix <= lx; ++ix)
                    for (int iz = sz; iz <= lz; ++iz)
                    {
                        var deltaPosition = new RmgenVector2D(ix, iz);
                        double distance = deltaPosition.DistanceTo(circlePosition);
                        double newHeight = rng.RandIntInclusive(0, 2) +
                            SafeMath.Round(2.0 / 3 * clumpHeight *
                                (SafeMath.Sin(SafeMath.PI * 2.0 / 3 * (3.0 / 4 - distance / circle.Radius)) + 0.5));

                        if (distance > circle.Radius)
                            continue;

                        if (map.GetHeight(deltaPosition) < newHeight)
                            map.SetHeight(deltaPosition, newHeight);
                        else if (map.GetHeight(deltaPosition) >= newHeight &&
                            map.GetHeight(position) < newHeight + 4)
                            map.SetHeight(deltaPosition, newHeight + 4);

                        terrainObj?.Place(map, rng, deltaPosition);
                        tileClass?.Add(deltaPosition);
                    }
            }
        }

        public static void CreateVolcano(RmgenRng rng, RandomMap map, RmgenVector2D position,
            TileClass tileClass, object terrainTexture, IReadOnlyList<string>? lavaTextures,
            bool smoke, bool relative)
        {
            var clLava = new TileClass(map.GetSize());
            var layers = new VolcanoLayer[]
            {
                new(RmgenGeometry.DiskArea(RmgenLibrary.ScaleByMapSize(18, 25, map.GetSize())),
                    15, tileClass, 3, null),
                new(RmgenGeometry.DiskArea(RmgenLibrary.ScaleByMapSize(16, 23, map.GetSize())),
                    25, new TileClass(map.GetSize()), 3, null),
                new(RmgenGeometry.DiskArea(RmgenLibrary.ScaleByMapSize(10, 15, map.GetSize())),
                    45, new TileClass(map.GetSize()), 3, null),
                new(RmgenGeometry.DiskArea(RmgenLibrary.ScaleByMapSize(8, 11, map.GetSize())),
                    62, new TileClass(map.GetSize()), 3, null),
                new(RmgenGeometry.DiskArea(RmgenLibrary.ScaleByMapSize(4, 6, map.GetSize())),
                    42, clLava, 1, lavaTextures),
            };

            for (int i = 0; i < layers.Length; ++i)
            {
                var lava = layers[i].LavaTextures;
                IPainter volcanoPainter = lava == null ?
                    new LayeredPainter(new object[] { terrainTexture, terrainTexture }, new[] { 3 }, rng) :
                    new LayeredPainter(new object[]
                    {
                        terrainTexture,
                        lava[0],
                        lava[1],
                        lava[2],
                    }, new[] { 1, 1, 1 }, rng);

                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, layers[i].Clumps, 0.7, 0.05,
                        double.PositiveInfinity, position),
                    new IPainter[]
                    {
                        volcanoPainter,
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            layers[i].Elevation, layers[i].Steepness, relative: relative),
                        new TileClassPainter(layers[i].TileClass),
                    },
                    i == 0 ? null : RmgenLibrary.StayClasses(layers[i - 1].TileClass, 1));
            }

            if (smoke)
            {
                int num = (int)Math.Floor(RmgenGeometry.DiskArea(
                    RmgenLibrary.ScaleByMapSize(3, 5, map.GetSize())));
                RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, "actor|particle/smoke.xml", num, num, 0, 7),
                    }, false, clLava, position),
                    0,
                    RmgenLibrary.StayClasses(tileClass, 1));
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

        private static void PlacePlayerBase(RmgenRng rng, RandomMap map, MapSettings settings,
            int playerID, RmgenVector2D playerPosition, TileClass? playerTileClass,
            object? cityPatchOuterTerrain, object? cityPatchInnerTerrain, double? cityPatchRadius,
            double cityPatchWidth, IReadOnlyList<IPainter>? cityPatchPainters,
            RmgenCommon.PlayerBaseOptions? options)
        {
            if (settings.Nomad)
                return;

            RmgenCommon.PlaceStartingEntities(map, playerPosition, playerID,
                RmgenCommon.GetStartingEntities(settings.DataRoot, RmgenCommon.GetCivCode(settings, playerID)),
                6,
                -SafeMath.PI / 4);

            if (playerTileClass != null)
                RmgenCommon.AddCivicCenterAreaToClass(map, playerPosition, playerTileClass);

            IConstraint baseResourceConstraint = options?.BaseResourceClass != null ?
                RmgenLibrary.AvoidClasses(options.BaseResourceClass, 4) :
                new NullConstraint();
            if (options?.ExtraBaseResourceConstraint != null)
                baseResourceConstraint = new AndConstraint(new IConstraint[]
                {
                    baseResourceConstraint,
                    options.ExtraBaseResourceConstraint,
                });

            var painters = new List<IPainter>();
            if (cityPatchOuterTerrain != null && cityPatchInnerTerrain != null)
                painters.Add(new LayeredPainter(new object[] { cityPatchOuterTerrain, cityPatchInnerTerrain },
                    new[] { cityPatchWidth }, rng));
            if (cityPatchPainters != null)
                painters.AddRange(cityPatchPainters);
            if (painters.Count != 0)
            {
                double radius = cityPatchRadius ?? RmgenCommon.DefaultPlayerBaseRadius(map.GetSize()) / 3;
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, Math.Floor(RmgenGeometry.DiskArea(radius)),
                        0.6, 0.3, double.PositiveInfinity, playerPosition),
                    painters,
                    null);
            }

            if (options == null)
                return;

            if (options.TreesTemplate != null)
                PlacePlayerBaseTrees(rng, map, options, playerPosition, baseResourceConstraint);
            if (options.Mines != null)
                PlacePlayerBaseMines(rng, map, options, playerPosition, baseResourceConstraint);
            if (options.BerriesTemplate != null)
                PlacePlayerBaseBerries(rng, options, playerPosition, baseResourceConstraint);
            if (options.StartingAnimal)
                PlacePlayerBaseStartingAnimal(rng, options, playerPosition, baseResourceConstraint);
        }

        private static void PlacePlayerBaseTrees(RmgenRng rng, RandomMap map,
            RmgenCommon.PlayerBaseOptions opt, RmgenVector2D playerPos, IConstraint constraint)
        {
            int num = opt.TreesCount ?? (int)Math.Floor(RmgenLibrary.ScaleByMapSize(7, 20, map.GetSize()));
            for (int x = 0; x < 30; ++x)
            {
                var off = new RmgenVector2D(0, rng.RandFloat(opt.TreesMinDist, opt.TreesMaxDist));
                off.Rotate(rng.RandomAngle());
                var position = RmgenVector2D.Add(off, playerPos);
                position.Round();
                if (RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, opt.TreesTemplate!, num, num,
                            opt.TreesMinDistGroup, opt.TreesMaxDistGroup),
                    }, false, opt.BaseResourceClass, position),
                    0,
                    constraint))
                    return;
            }
        }

        private static void PlacePlayerBaseMines(RmgenRng rng, RandomMap map,
            RmgenCommon.PlayerBaseOptions opt, RmgenVector2D playerPos, IConstraint constraint)
        {
            double angleBetweenMines = rng.RandFloat(opt.MinesMinAngle, opt.MinesMaxAngle);
            int mineCount = opt.Mines!.Count;
            for (int tries = 0; tries < 75; ++tries)
            {
                RmgenVector2D[]? pos = new RmgenVector2D[mineCount];
                double startAngle = rng.RandomAngle();
                for (int i = 0; i < mineCount; ++i)
                {
                    double angle = startAngle + angleBetweenMines * (i + (mineCount - 1) / 2.0);
                    var off = new RmgenVector2D(0, opt.MinesDistance);
                    off.Rotate(angle);
                    var p = RmgenVector2D.Add(off, playerPos);
                    p.Round();
                    pos[i] = p;
                    if (!map.ValidTilePassable(p) || !constraint.Allows(p))
                    {
                        pos = null;
                        break;
                    }
                }
                if (pos == null)
                    continue;

                for (int i = 0; i < mineCount; ++i)
                {
                    var type = opt.Mines[i];
                    if (type.Type == "stone_formation")
                    {
                        GaiaEntities.CreateStoneMineFormation(rng, map, pos[i],
                            type.Template, type.Terrain ?? "");
                        opt.BaseResourceClass?.Add(pos[i]);
                        continue;
                    }

                    var objs = new List<IGroupElement>
                    {
                        new ScatterObject(rng, type.Template, 1, 1, 0, 0),
                    };
                    if (opt.MinesGroupElements != null)
                        objs.AddRange(opt.MinesGroupElements);
                    RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(objs, true, opt.BaseResourceClass, pos[i]),
                        0,
                        null);
                }
                return;
            }
        }

        private static void PlacePlayerBaseBerries(RmgenRng rng,
            RmgenCommon.PlayerBaseOptions opt, RmgenVector2D playerPos, IConstraint constraint)
        {
            for (int tries = 0; tries < 30; ++tries)
            {
                var off = new RmgenVector2D(0, opt.BerriesDistance);
                off.Rotate(rng.RandomAngle());
                var position = RmgenVector2D.Add(off, playerPos);
                if (RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, opt.BerriesTemplate!, opt.BerriesMinCount,
                            opt.BerriesMaxCount, 1, 3),
                    }, true, opt.BaseResourceClass, position),
                    0,
                    constraint))
                    return;
            }
        }

        private static void PlacePlayerBaseStartingAnimal(RmgenRng rng,
            RmgenCommon.PlayerBaseOptions opt, RmgenVector2D playerPos, IConstraint constraint)
        {
            int count = opt.StartingAnimalCount ?? 5;
            for (int i = 0; i < opt.StartingAnimalGroupCount; ++i)
            {
                bool success = false;
                for (int tries = 0; tries < 30; ++tries)
                {
                    var off = new RmgenVector2D(0, opt.StartingAnimalDistance);
                    off.Rotate(rng.RandomAngle());
                    var position = RmgenVector2D.Add(off, playerPos);
                    if (RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(rng, opt.StartingAnimalTemplate,
                                opt.StartingAnimalMinGroupCount ?? count,
                                opt.StartingAnimalMaxGroupCount ?? count,
                                opt.StartingAnimalMinGroupDistance,
                                opt.StartingAnimalMaxGroupDistance),
                        }, true, opt.BaseResourceClass, position),
                        0,
                        constraint))
                    {
                        success = true;
                        break;
                    }
                }
                if (!success)
                    return;
            }
        }

        private readonly record struct VolcanoLayer(double Clumps, double Elevation,
            TileClass TileClass, double Steepness, IReadOnlyList<string>? LavaTextures);
    }
}
