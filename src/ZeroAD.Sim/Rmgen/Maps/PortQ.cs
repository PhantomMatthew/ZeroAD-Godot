using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>deep_forest.js（264 行，逐字移植）——密林迷宫：随机主地表、清窄道路、
    /// 环形扩张矿点和逐图块密度森林。环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class DeepForestMap2 : StandardMap
    {
        private const string TemplateStone = "gaia/rock/temperate_small";
        private const string TemplateStoneMine = "gaia/rock/temperate_large";
        private const string TemplateMetalMine = "gaia/ore/temperate_large";
        private const string TemplateTemple = "gaia/ruins/unfinished_greek_temple";

        private static readonly string[] TerrainPrimary =
        {
            "temp_grass",
            "temp_grass_b",
            "temp_grass_c",
            "temp_grass_d",
            "temp_grass_long_b",
            "temp_grass_clovers_2",
            "temp_grass_mossy",
            "temp_grass_plants",
        };

        private static readonly string[] TerrainWood =
        {
            "temp_grass_mossy|gaia/tree/oak",
            "temp_forestfloor_pine|gaia/tree/pine",
            "temp_mud_plants|gaia/tree/dead",
            "temp_plants_bog|gaia/tree/oak_large",
            "temp_dirt_gravel_plants|gaia/tree/aleppo_pine",
            "temp_forestfloor_autumn|gaia/tree/carob",
        };

        private static readonly string[] TerrainWoodBorder =
        {
            "temp_grass_plants|gaia/tree/euro_beech",
            "temp_grass_mossy|gaia/tree/poplar",
            "temp_grass_mossy|gaia/tree/poplar_lombardy",
            "temp_grass_long|gaia/tree/bush_temperate",
            "temp_mud_plants|gaia/tree/bush_temperate",
            "temp_mud_plants|gaia/tree/bush_badlands",
            "temp_grass_long|gaia/fruit/apple",
            "temp_grass_clovers|gaia/fruit/berry_01",
            "temp_grass_clovers_2|gaia/fruit/grapes",
            "temp_grass_plants|gaia/fauna_deer",
            "temp_grass_long_b|gaia/fauna_rabbit",
            "temp_grass_plants",
        };

        private static readonly string[] TerrainBase = { "temp_dirt_gravel", "temp_grass_b" };
        private static readonly string[] TerrainBaseBorder =
            { "temp_grass_b", "temp_grass_b", "temp_grass", "temp_grass_c", "temp_grass_mossy" };
        private static readonly string[] TerrainBaseCenter =
            { "temp_dirt_gravel", "temp_dirt_gravel", "temp_grass_b" };
        private static readonly string[] TerrainPath = { "temp_road", "temp_road_overgrown", "temp_grass_b" };
        private static readonly string[] TerrainHill =
            { "temp_highlands", "temp_highlands", "temp_highlands", "temp_dirt_gravel_b", "temp_cliff_a" };
        private static readonly string[] TerrainHillBorder =
        {
            "temp_highlands",
            "temp_highlands",
            "temp_highlands",
            "temp_dirt_gravel_b",
            "temp_dirt_gravel_plants",
            "temp_highlands",
            "temp_highlands",
            "temp_highlands",
            "temp_dirt_gravel_b",
            "temp_dirt_gravel_plants",
            "temp_highlands",
            "temp_highlands",
            "temp_highlands",
            "temp_cliff_b",
            "temp_dirt_gravel_plants",
            "temp_highlands",
            "temp_highlands",
            "temp_highlands",
            "temp_cliff_b",
            "temp_dirt_gravel_plants",
            "temp_highlands|gaia/fauna_goat",
        };

        protected override double HeightLand => 0;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, TerrainPrimary);
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightPath = -2;
            const double heightOffsetRandomPath = 1;
            const double baseRadius = 20;

            double mapRadius = MapSize / 2.0;
            double minPlayerRadius = Math.Min(mapRadius - 1.5 * baseRadius, 5.0 / 8 * mapRadius);
            double maxPlayerRadius = Math.Min(mapRadius - baseRadius, 3.0 / 4 * mapRadius);
            var playerPosition = new List<RmgenVector2D>();
            var playerAngle = new List<double>();
            double playerAngleStart = rng.RandomAngle();
            double playerAngleAddAvrg = 2 * SafeMath.PI / NumPlayers;
            double playerAngleMaxOff = playerAngleAddAvrg / 4;

            double radiusEC = Math.Max(mapRadius / 8, baseRadius / 2);
            double resourceRadius = RmgenLibrary.FractionToTiles(1.0 / 3, MapSize);
            string[] resourcePerPlayer = { TemplateStone, TemplateMetalMine };

            double maxTreeDensity = Math.Min(256 * (192 + 8 * NumPlayers) / SafeMath.Square(MapSize), 1);
            const double bushChance = 1.0 / 3;

            var playerIDs = RmgenCommon.SortAllPlayers(rng, settings);
            for (int i = 0; i < NumPlayers; ++i)
            {
                double angle = (playerAngleStart + i * playerAngleAddAvrg +
                    rng.RandFloat(0, playerAngleMaxOff)) % (2 * SafeMath.PI);
                playerAngle.Add(angle);
                var offset = new RmgenVector2D(rng.RandFloat(minPlayerRadius, maxPlayerRadius), 0);
                offset.Rotate(-angle);
                var pos = RmgenVector2D.Add(mapCenter, offset);
                pos.Round();
                playerPosition.Add(pos);
            }

            PlaceDeepForestPlayerBases(rng, map, settings, playerIDs, playerPosition, baseRadius);

            bool pathBlending = NumPlayers <= 4;
            var clPath = new TileClass(MapSize);
            for (int i = 0; i < NumPlayers + (pathBlending ? 1 : 0); ++i)
                for (int j = pathBlending ? 0 : i + 1; j < NumPlayers + 1; ++j)
                {
                    var pathStart = i < NumPlayers ? playerPosition[i] : mapCenter;
                    var pathEnd = j < NumPlayers ? playerPosition[j] : mapCenter;
                    RmgenLibrary.CreateArea(
                        new RandomPathPlacer(rng, pathStart, pathEnd, 1.25, baseRadius / 2, pathBlending),
                        new IPainter[]
                        {
                            new TerrainPainter(TerrainPath, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                                heightPath, 2, randomElevation: heightOffsetRandomPath),
                            new TileClassPainter(clPath),
                        },
                        RmgenLibrary.AvoidClasses(ClBaseResource, 4));
                }

            for (int i = 0; i < NumPlayers; ++i)
                for (int rIndex = 0; rIndex < resourcePerPlayer.Length; ++rIndex)
                {
                    double angleDist = NumPlayers > 1
                        ? (playerAngle[(i + 1) % NumPlayers] - playerAngle[i] + 2 * SafeMath.PI) %
                            (2 * SafeMath.PI)
                        : 2 * SafeMath.PI;
                    double angle = playerAngle[i] + angleDist * (rIndex + 1) /
                        (resourcePerPlayer.Length + 1);
                    var offset = new RmgenVector2D(resourceRadius, 0);
                    offset.Rotate(-angle);
                    var position = RmgenVector2D.Add(mapCenter, offset);
                    position.Round();

                    map.PlaceEntityPassable(resourcePerPlayer[rIndex], 0, position, rng.RandomAngle());
                    RmgenLibrary.CreateArea(
                        new ClumpPlacer(rng, 40, 0.5, 0.125, double.PositiveInfinity, position),
                        new IPainter[]
                        {
                            new LayeredPainter(new object[] { TerrainHillBorder, TerrainHill }, new[] { 1 }, rng),
                            new ElevationPainter(rng.RandFloat(1, 2)),
                            new TileClassPainter(ClHill),
                        },
                        null);
                }

            map.PlaceEntityPassable(TemplateTemple, 0, mapCenter, rng.RandomAngle());
            ClBaseResource.Add(mapCenter);

            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, SafeMath.Square(radiusEC), 0.5, 0.125,
                    double.PositiveInfinity, mapCenter),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { TerrainHillBorder, TerrainHill },
                        new[] { radiusEC / 4 }, rng),
                    new ElevationPainter(rng.RandFloat(1, 2)),
                    new TileClassPainter(ClHill),
                },
                null);

            for (int x = 0; x < MapSize; ++x)
                for (int z = 0; z < MapSize; ++z)
                {
                    var position = new RmgenVector2D(x, z);
                    double radius = mapCenter.DistanceTo(RmgenVector2D.Add(position, new RmgenVector2D(0.5, 0.5)));
                    double minDistToSL = MapSize;
                    for (int i = 0; i < NumPlayers; ++i)
                        minDistToSL = Math.Min(minDistToSL, position.DistanceTo(playerPosition[i]));

                    double tDensFactSL = Math.Max(Math.Min((minDistToSL - baseRadius) / baseRadius, 1), 0);
                    double tDensFactRad = Math.Abs((resourceRadius - radius) / resourceRadius);
                    double tDensFactEC = Math.Max(Math.Min((radius - radiusEC) / radiusEC, 1), 0);
                    double tDensActual = maxTreeDensity * tDensFactSL * tDensFactRad * tDensFactEC;

                    if (rng.RandBool(tDensActual) && map.ValidTile(position))
                    {
                        bool border = tDensActual < rng.RandFloat(0, bushChance * maxTreeDensity);
                        if (RmgenLibrary.AvoidClasses(clPath, 1, ClHill, border ? 0 : 1).Allows(position))
                        {
                            TerrainFactory.CreateTerrain(border ? TerrainWoodBorder : TerrainWood)
                                .Place(map, rng, position);
                            map.SetHeight(position, rng.RandFloat(0, 1));
                            ClForest.Add(position);
                        }
                    }

                    double hVarMiddleHill = RmgenLibrary.FractionToTiles(1.0 / 64, MapSize) *
                        (1 + SafeMath.Cos(3.0 / 2 * SafeMath.PI * radius / mapRadius));
                    double hVarHills = 5 * (1 + SafeMath.Sin(x / 10.0) * SafeMath.Sin(z / 10.0));
                    map.SetHeight(position, map.GetHeight(position) + hVarMiddleHill + hVarHills + 1);
                }

            return map.MakeExportable();
        }

        private void PlaceDeepForestPlayerBases(RmgenRng rng, RandomMap map, MapSettings settings,
            IReadOnlyList<int> playerIDs, IReadOnlyList<RmgenVector2D> playerPosition, double baseRadius)
        {
            if (settings.Nomad)
                return;

            for (int i = 0; i < NumPlayers; ++i)
                PlaceDeepForestPlayerBase(rng, map, settings, playerIDs[i], playerPosition[i], baseRadius);
        }

        private void PlaceDeepForestPlayerBase(RmgenRng rng, RandomMap map, MapSettings settings,
            int playerID, RmgenVector2D playerPosition, double baseRadius)
        {
            string civ = RmgenCommon.GetCivCode(settings, playerID);
            RmgenCommon.PlaceStartingEntities(map, playerPosition, playerID,
                RmgenCommon.GetStartingEntities(settings.DataRoot, civ), 6, -SafeMath.PI / 4);

            var baseResourceConstraint = RmgenLibrary.AvoidClasses(ClBaseResource, 4);
            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, Math.Floor(RmgenGeometry.DiskArea(0.8 * baseRadius)),
                    0.6, 1.0 / 8, double.PositiveInfinity, playerPosition),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { TerrainBaseBorder, TerrainBase, TerrainBaseCenter },
                        new[] { baseRadius / 4, baseRadius / 4 }, rng),
                    new TileClassPainter(ClPlayer),
                },
                null);

            PlaceBaseTrees(rng, playerPosition, "gaia/tree/oak_large", 2,
                11, 13, 0, 5, baseResourceConstraint);
            PlaceBaseMines(rng, map, playerPosition,
                new (string Template, string? Type, object? Terrain)[]
                {
                    (TemplateMetalMine, null, null),
                    (TemplateStoneMine, null, null),
                },
                SafeMath.PI / 2, SafeMath.PI, 12, baseResourceConstraint);
            PlaceBaseBerries(rng, playerPosition, "gaia/fruit/grapes",
                2, 2, 12, 5, 8, baseResourceConstraint);
            PlaceBaseStartingAnimal(rng, playerPosition, baseResourceConstraint);
        }

        private void PlaceBaseStartingAnimal(RmgenRng rng, RmgenVector2D playerPosition,
            IConstraint baseResourceConstraint)
        {
            for (int i = 0; i < 2; ++i)
            {
                bool success = false;
                for (int tries = 0; tries < 30; ++tries)
                {
                    var offset = new RmgenVector2D(0, 9);
                    offset.Rotate(rng.RandomAngle());
                    var position = RmgenVector2D.Add(offset, playerPosition);
                    if (RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(rng, "gaia/fauna_chicken", 5, 5, 0, 2),
                        }, true, ClBaseResource, position),
                        0, baseResourceConstraint))
                    {
                        success = true;
                        break;
                    }
                }

                if (!success)
                    return;
            }
        }

        private void PlaceBaseBerries(RmgenRng rng, RmgenVector2D playerPosition,
            string template, int minCount, int maxCount, double distance, double minDist,
            double maxDist, IConstraint baseResourceConstraint)
        {
            for (int tries = 0; tries < 30; ++tries)
            {
                var offset = new RmgenVector2D(0, distance);
                offset.Rotate(rng.RandomAngle());
                var position = RmgenVector2D.Add(offset, playerPosition);
                if (RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, template, minCount, maxCount, minDist, maxDist),
                    }, true, ClBaseResource, position),
                    0, baseResourceConstraint))
                    return;
            }
        }

        private void PlaceBaseMines(RmgenRng rng, RandomMap map, RmgenVector2D playerPosition,
            IReadOnlyList<(string Template, string? Type, object? Terrain)> mines,
            double minAngle, double maxAngle, double distance, IConstraint baseResourceConstraint)
        {
            double angleBetweenMines = rng.RandFloat(minAngle, maxAngle);
            int mineCount = mines.Count;

            for (int tries = 0; tries < 75; ++tries)
            {
                var pos = new RmgenVector2D[mineCount];
                bool valid = true;
                double startAngle = rng.RandomAngle();
                for (int i = 0; i < mineCount; ++i)
                {
                    double angle = startAngle + angleBetweenMines * (i + (mineCount - 1) / 2.0);
                    var offset = new RmgenVector2D(0, distance);
                    offset.Rotate(angle);
                    var p = RmgenVector2D.Add(offset, playerPosition);
                    p.Round();
                    pos[i] = p;
                    if (!map.ValidTilePassable(p) || !baseResourceConstraint.Allows(p))
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                    continue;

                for (int i = 0; i < mineCount; ++i)
                {
                    var mine = mines[i];
                    if (mine.Type == "stone_formation")
                    {
                        GaiaEntities.CreateStoneMineFormation(rng, map, pos[i], mine.Template,
                            mine.Terrain ?? "");
                        ClBaseResource.Add(pos[i]);
                        continue;
                    }

                    RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(rng, mine.Template, 1, 1, 0, 0),
                        }, true, ClBaseResource, pos[i]),
                        0, null);
                }
                return;
            }
        }

        private void PlaceBaseTrees(RmgenRng rng, RmgenVector2D playerPosition,
            string template, int count, double minDist, double maxDist, double minDistGroup,
            double maxDistGroup, IConstraint baseResourceConstraint)
        {
            int num = (int)Math.Floor((double)count);
            for (int tries = 0; tries < 30; ++tries)
            {
                var offset = new RmgenVector2D(0, rng.RandFloat(minDist, maxDist));
                offset.Rotate(rng.RandomAngle());
                var position = RmgenVector2D.Add(offset, playerPosition);
                position.Round();

                if (RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, template, num, num, minDistGroup, maxDistGroup),
                    }, false, ClBaseResource, position),
                    0, baseResourceConstraint))
                    return;
            }
        }
    }

    /// <summary>sahel.js（257 行，逐字移植）——干旱稀树草原：水洼、猴面包树、
    /// 大群草食动物和狮群。环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class SahelMap2 : StandardMap
    {
        private const string TPrimary = "savanna_grass_a";
        private const string TGrass2 = "savanna_grass_b";
        private const string TGrass3 = "savanna_shrubs_a";
        private const string TDirt1 = "savanna_dirt_rocks_a";
        private const string TDirt2 = "savanna_dirt_rocks_b";
        private const string TDirt3 = "savanna_dirt_rocks_c";
        private const string TDirt4 = "savanna_dirt_b";
        private const string TCityTiles = "savanna_tile_a";
        private const string TShore = "savanna_riparian_bank";
        private const string TWater = "savanna_riparian_wet";

        private const string OBaobab = "gaia/tree/baobab";
        private const string OBerryBush = "gaia/fruit/berry_05";
        private const string OGazelle = "gaia/fauna_gazelle";
        private const string OGiraffe = "gaia/fauna_giraffe";
        private const string OGiraffeInfant = "gaia/fauna_giraffe_infant";
        private const string OElephant = "gaia/fauna_elephant_african_bush";
        private const string OElephantInfant = "gaia/fauna_elephant_african_infant";
        private const string OLion = "gaia/fauna_lion";
        private const string OLioness = "gaia/fauna_lioness";
        private const string OZebra = "gaia/fauna_zebra";
        private const string OStoneSmall = "gaia/rock/savanna_small";
        private const string OMetalLarge = "gaia/ore/savanna_large";

        private const string ABush = "actor|props/flora/bush_medit_sm_dry.xml";
        private const string ARock = "actor|geology/stone_savanna_med.xml";

        protected override double HeightLand => 1;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, TPrimary);
            var map = Map;

            const double heightSeaGround = -5;
            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, null,
                RmgenLibrary.FractionToTiles(0.35, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize),
                rng.RandomAngle());

            RmgenCommon.PlacePlayerBases(rng, map, settings, TPrimary, ClPlayer, null,
                playerPosition, TCityTiles, TCityTiles, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = OBerryBush,
                    Mines = new List<(string, string?, object?)>
                    {
                        (OMetalLarge, null, null),
                        (OStoneSmall, "stone_formation", TDirt4),
                    },
                    TreesTemplate = OBaobab,
                    TreesCount = (int)Math.Floor(RmgenLibrary.ScaleByMapSize(2, 7, MapSize)),
                    TreesMinDistGroup = 2,
                    TreesMaxDistGroup = 7,
                });

            foreach (string patch in new[] { TGrass2, TGrass3 })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 6, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(10, 20, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(15, 60, MapSize)),
                        double.PositiveInfinity),
                    new IPainter[] { new TerrainPainter(patch, rng) },
                    RmgenLibrary.AvoidClasses(ClPlayer, 10),
                    RmgenLibrary.ScaleByMapSize(5, 20, MapSize));

            foreach (double size in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
            })
                foreach (string patch in new[] { TDirt1, TDirt2, TDirt3 })
                    RmgenLibrary.CreateAreas(rng,
                        new ChainPlacer(rng, 1,
                            Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                            size, double.PositiveInfinity),
                        new IPainter[] { new TerrainPainter(patch, rng) },
                        RmgenLibrary.AvoidClasses(ClPlayer, 12),
                        RmgenLibrary.ScaleByMapSize(4, 15, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(20, 60, MapSize)),
                    double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { TShore, TWater }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaGround, 7),
                    new TileClassPainter(clWater),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 24),
                RmgenLibrary.ScaleByMapSize(1, 3, MapSize));

            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(12, 30, MapSize); ++i)
            {
                var position = new RmgenVector2D(
                    rng.RandIntExclusive(0, MapSize),
                    rng.RandIntExclusive(0, MapSize));
                if (RmgenLibrary.AvoidClasses(ClPlayer, 30, ClRock, 25, clWater, 10)
                    .Allows(position))
                {
                    GaiaEntities.CreateStoneMineFormation(rng, map, position, OStoneSmall, TDirt4);
                    ClRock.Add(position);
                }
            }

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, OMetalLarge, 1, 1, 0, 4),
                }, true, ClMetal),
                0,
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClMetal, 10, ClRock, 8, clWater, 4),
                RmgenLibrary.ScaleByMapSize(2, 8, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, ARock, 1, 3, 0, 3),
                }, true),
                0,
                RmgenLibrary.AvoidClasses(ClPlayer, 7, clWater, 1),
                RmgenLibrary.ScaleByMapSize(200, 1200, MapSize), 1);

            CreateSahelFood(rng, clWater, clFood);

            GaiaEntities.CreateStragglerTrees(rng, new[] { OBaobab },
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClMetal, 6,
                    ClRock, 7, clWater, 1),
                ClForest, RmgenLibrary.ScaleByMapSize(70, 500, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, ABush, 2, 4, 0, 1.8, -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClPlayer, 2, ClForest, 0),
                RmgenLibrary.ScaleByMapSize(100, 1200, MapSize));

            return map.MakeExportable();
        }

        private void CreateSahelFood(RmgenRng rng, TileClass clWater, TileClass clFood)
        {
            foreach (var spec in new (IGroupElement[] Objects, double Amount)[]
            {
                (new IGroupElement[] { new ScatterObject(rng, OGazelle, 5, 7, 0, 4) },
                    RmgenLibrary.ScaleByMapSize(4, 12, MapSize)),
                (new IGroupElement[] { new ScatterObject(rng, OZebra, 5, 7, 0, 4) },
                    RmgenLibrary.ScaleByMapSize(4, 12, MapSize)),
                (new IGroupElement[]
                    {
                        new ScatterObject(rng, OGiraffe, 2, 4, 0, 4),
                        new ScatterObject(rng, OGiraffeInfant, 0, 2, 0, 4),
                    },
                    RmgenLibrary.ScaleByMapSize(4, 12, MapSize)),
                (new IGroupElement[]
                    {
                        new ScatterObject(rng, OElephant, 2, 4, 0, 4),
                        new ScatterObject(rng, OElephantInfant, 0, 2, 0, 4),
                    },
                    RmgenLibrary.ScaleByMapSize(4, 12, MapSize)),
                (new IGroupElement[]
                    {
                        new ScatterObject(rng, OLion, 0, 1, 0, 4),
                        new ScatterObject(rng, OLioness, 2, 3, 0, 4),
                    },
                    RmgenLibrary.ScaleByMapSize(4, 12, MapSize)),
            })
                RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                    new ObjectGroup(spec.Objects, true, clFood),
                    0,
                    RmgenLibrary.AvoidClasses(clWater, 1, ClPlayer, 20, clFood, 11),
                    spec.Amount, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, OBerryBush, 5, 7, 0, 4),
                }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clWater, 3, ClPlayer, 20, clFood, 12, ClRock, 7,
                    ClMetal, 6),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);
        }
    }

    /// <summary>survivalofthefittest.js（255 行，逐字移植）——中央低地竞技场、
    /// 玩家通道、寻宝平民和触发点；同名触发脚本不在本次范围。</summary>
    public sealed class SurvivalOfTheFittestMap2 : StandardMap
    {
        private const string AWaypointFlag = "actor|props/special/common/waypoint_flag_factions.xml";
        private const string OTreasureSeeker =
            "nonbuilder|undeletable|skirmish/units/default_support_civilian";
        private const string OObstruction = "obstructors/placement_24x24";
        private const string TriggerPointAttacker = "trigger/trigger_point_A";
        private static readonly string[] TriggerPointTreasures =
        {
            "trigger/trigger_point_B",
            "trigger/trigger_point_C",
            "trigger/trigger_point_D",
        };

        protected override double HeightLand => 30;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightLandLocal = 3;
            const double heightHillTop = 30;

            var clLand = new TileClass(MapSize);
            var clCivilians = new TileClass(MapSize);

            var pForest1 = new[]
            {
                biome.ForestFloor2 + TerrainFactory.TerrainSeparator + biome.Tree1,
                biome.ForestFloor2 + TerrainFactory.TerrainSeparator + biome.Tree2,
                biome.ForestFloor2,
            };
            var pForest2 = new[]
            {
                biome.ForestFloor1 + TerrainFactory.TerrainSeparator + biome.Tree4,
                biome.ForestFloor1 + TerrainFactory.TerrainSeparator + biome.Tree5,
                biome.ForestFloor1,
            };

            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.15, MapSize)),
                    0.7, 0.1, double.PositiveInfinity, mapCenter),
                new IPainter[]
                {
                    new TerrainPainter(biome.MainTerrain, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightLandLocal, 3),
                    new TileClassPainter(clLand),
                },
                null);

            var (playerIDs, playerPosition, playerAngle, startAngle) =
                RmgenCommon.PlayerPlacementCircle(rng, map, NumPlayers,
                    RmgenLibrary.FractionToTiles(0.3, MapSize));
            var halfway = RoundedCirclePoints(NumPlayers, startAngle,
                RmgenLibrary.FractionToTiles(0.375, MapSize), mapCenter);
            var attacker = RoundedCirclePoints(NumPlayers, startAngle,
                RmgenLibrary.FractionToTiles(0.45, MapSize), mapCenter);
            var passage = RmgenGeometry.DistributePointsOnCircle(NumPlayers,
                startAngle + SafeMath.PI / NumPlayers,
                RmgenLibrary.FractionToTiles(0.5, MapSize), mapCenter).points;

            for (int i = 0; i < NumPlayers; ++i)
            {
                var civEntities = RmgenCommon.GetStartingEntities(settings.DataRoot,
                    RmgenCommon.GetCivCode(settings, playerIDs[i]))
                    .Where(ent => ent.Template.Contains("civil_centre", StringComparison.Ordinal) ||
                                  ent.Template.Contains("infantry", StringComparison.Ordinal))
                    .ToList();
                RmgenCommon.PlaceStartingEntities(map, playerPosition[i], playerIDs[i], civEntities);

                PortQHelpers.PlacePlayerBaseDecoratives(rng, map, playerPosition[i],
                    biome.GrassShort, ClBaseResource);

                var pathPlacer = new PathPlacer(rng, 0.4,
                    RmgenLibrary.ScaleByMapSize(3, 9, MapSize), 0.2, 0.05)
                {
                    Start = mapCenter,
                    End = passage[i],
                    Width = RmgenLibrary.ScaleByMapSize(14, 24, MapSize),
                };
                RmgenLibrary.CreateArea(
                    pathPlacer,
                    new IPainter[]
                    {
                        new TerrainPainter(biome.MainTerrain, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            heightLandLocal, 4),
                    },
                    null);

                var civilianLocation = RmgenCommon.FindLocationInDirectionBasedOnHeight(
                    map, playerPosition[i], mapCenter, -3, 3.5, 3)!.Value;
                civilianLocation.Round();
                clCivilians.Add(civilianLocation);
                map.PlaceEntityPassable(OTreasureSeeker, playerIDs[i], civilianLocation,
                    playerAngle[i] + SafeMath.PI);

                map.PlaceEntityAnywhere(AWaypointFlag, 0, attacker[i], SafeMath.PI / 2);
                map.PlaceEntityPassable(TriggerPointAttacker, playerIDs[i], attacker[i],
                    SafeMath.PI / 2);

                RmgenCommon.AddCivicCenterAreaToClass(map, playerPosition[i], ClPlayer);
                ClPlayer.Add(attacker[i]);
                ClPlayer.Add(halfway[i]);
            }

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightLandLocal + 0.12, heightHillTop - 1,
                HeightPlacer.Mode.IncludeMinExcludeMax, biome.Cliff);
            RmgenLibrary.PaintTileClassBasedOnHeight(heightLandLocal + 0.12, heightHillTop - 1,
                HeightPlacer.Mode.IncludeMinExcludeMax, ClHill);

            IConstraint landConstraint = new StaticConstraint(map, RmgenLibrary.StayClasses(clLand, 5));

            foreach (string triggerPointTreasure in TriggerPointTreasures)
                RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, triggerPointTreasure, 1, 1, 0, 0),
                    }, true, clCivilians),
                    0,
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClPlayer, 5, ClHill, 5),
                        landConstraint,
                    }),
                    RmgenLibrary.ScaleByMapSize(40, 140, MapSize), 100);

            PortQHelpers.CreateBumps(rng, MapSize, landConstraint);

            var hillConstraint = new AndConstraint(new IConstraint[]
            {
                RmgenLibrary.AvoidClasses(ClHill, 5),
                new StaticConstraint(map,
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClBaseResource, 3, clCivilians, 5)),
            });
            if (rng.RandBool())
                PortQHelpers.CreateHills(rng, MapSize,
                    new object[] { biome.MainTerrain, biome.Cliff, biome.Hill },
                    new AndConstraint(new IConstraint[] { hillConstraint, landConstraint }),
                    ClHill, RmgenLibrary.ScaleByMapSize(10, 60, MapSize) * NumPlayers);
            else
                RmgenCommon.CreateMountains(rng, map, biome.Cliff,
                    new AndConstraint(new IConstraint[] { hillConstraint, landConstraint }),
                    ClHill,
                    count: (int)Math.Ceiling(RmgenLibrary.ScaleByMapSize(10, 60, MapSize) * NumPlayers));

            PortQHelpers.CreateHills(rng, MapSize,
                new object[] { biome.Cliff, biome.Cliff, biome.Hill },
                new AndConstraint(new IConstraint[]
                {
                    hillConstraint,
                    RmgenLibrary.AvoidClasses(clLand, 5),
                }),
                ClHill, RmgenLibrary.ScaleByMapSize(15, 90, MapSize) * NumPlayers,
                elevation: 55);

            double scaledTrees = RmgenLibrary.ScaleByMapSize(biome.TreesMin, biome.TreesMax, MapSize);
            double forestTrees = biome.ForestProbability * scaledTrees;
            double stragglerTrees = (1 - biome.ForestProbability) * scaledTrees;
            GaiaEntities.CreateForests(rng, map,
                new object[] { biome.MainTerrain, biome.ForestFloor1, biome.ForestFloor2, pForest1, pForest2 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 5),
                    new StaticConstraint(map,
                        RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 0,
                            ClBaseResource, 2, clCivilians, 5),
                        RmgenLibrary.StayClasses(clLand, 4)),
                }),
                ClForest, forestTrees, NumPlayers);

            var patchConstraint = new AndConstraint(new IConstraint[]
            {
                RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5,
                    ClPlayer, 12, clCivilians, 5),
                landConstraint,
            });
            PortQHelpers.CreateLayeredPatches(rng, MapSize,
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
                new[] { 1, 1 }, patchConstraint,
                RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            PortQHelpers.CreatePatches(rng, MapSize,
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                    RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
                },
                biome.Tier4Terrain, patchConstraint,
                RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            double planetm = BiomeName == "generic/india" ? 8 : 1;
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
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                    landConstraint,
                }));

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 7, ClHill, 1, ClPlayer, 9),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                ClForest, stragglerTrees);

            double maxHeight = (heightHillTop + heightLandLocal) / 2;
            for (int x = 0; x < MapSize; x += 6)
                for (int y = 0; y < MapSize; y += 6)
                {
                    var vec = new RmgenVector2D(x, y);
                    if (map.GetHeight(vec) < maxHeight)
                        map.PlaceEntityAnywhere(OObstruction, 0, vec, SafeMath.PI / 2);
                }

            return map.MakeExportable();
        }

        private static List<RmgenVector2D> RoundedCirclePoints(int count, double startAngle,
            double radius, RmgenVector2D center)
        {
            var result = RmgenGeometry.DistributePointsOnCircle(count, startAngle, radius, center).points;
            for (int i = 0; i < result.Count; ++i)
            {
                var point = result[i];
                point.Round();
                result[i] = point;
            }
            return result;
        }
    }

    /// <summary>foothills.js（254 行，逐字移植）——滚动山脊：两轮随机 ChainPlacer
    /// 山丘叠加，随后按通用 biome 生成森林、矿、猎物和装饰。placePlayersNomad 省略。</summary>
    public sealed class FoothillsMap2 : StandardMap
    {
        protected override double HeightLand => 3;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            var clFood = new TileClass(MapSize);

            var pForest1 = new[]
            {
                biome.ForestFloor2 + TerrainFactory.TerrainSeparator + biome.Tree1,
                biome.ForestFloor2 + TerrainFactory.TerrainSeparator + biome.Tree2,
                biome.ForestFloor2,
            };
            var pForest2 = new[]
            {
                biome.ForestFloor1 + TerrainFactory.TerrainSeparator + biome.Tree4,
                biome.ForestFloor1 + TerrainFactory.TerrainSeparator + biome.Tree5,
                biome.ForestFloor1,
            };

            string pattern = settings.PlayerPlacement;
            double circleTeamDist = rng.RandFloat(0.33, 0.42);
            double teamDist = pattern switch
            {
                "circle" => circleTeamDist,
                "river" => 0.47,
                "stronghold" => 0.33,
                _ => double.NaN,
            };

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, pattern,
                RmgenLibrary.FractionToTiles(teamDist, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize),
                rng.RandomAngle());

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                playerPosition, biome.RoadWild, biome.Road, playerIDs,
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
                    TreesCount = 5,
                });

            int firstHillCount = rng.RandIntInclusive(40, 90);
            for (int m = 0; m < firstHillCount; ++m)
            {
                int elevRand = rng.RandIntInclusive(6, 12);
                RmgenLibrary.CreateArea(
                    new ChainPlacer(rng, 12, 28,
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
                            new[] { Math.Floor(elevRand / 5.0), 40 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            elevRand, rng.RandIntInclusive(18, 35)),
                        new TileClassPainter(ClHill),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 34, ClHill, 8));
            }

            int secondHillCount = rng.RandIntInclusive(60, 100);
            for (int m = 0; m < secondHillCount; ++m)
            {
                int elevRand = rng.RandIntInclusive(14, 36);
                RmgenLibrary.CreateArea(
                    new ChainPlacer(rng, 10, 20,
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
                            new[] { Math.Floor(elevRand / 8.0), 40 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            elevRand, rng.RandIntInclusive(18, 25)),
                        new TileClassPainter(ClHill),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClBaseResource, 2, ClPlayer, 30),
                        RmgenLibrary.StayClasses(ClHill, 1),
                    }));
            }

            double scaledTrees = RmgenLibrary.ScaleByMapSize(biome.TreesMin, biome.TreesMax, MapSize);
            double forestTrees = biome.ForestProbability * scaledTrees;
            double stragglerTrees = (1 - biome.ForestProbability) * scaledTrees;
            GaiaEntities.CreateDefaultForests(rng, map,
                new object[] { biome.MainTerrain, biome.ForestFloor1, biome.ForestFloor2, pForest1, pForest2 },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 18),
                ClForest, forestTrees);

            IConstraint patchConstraint = RmgenLibrary.AvoidClasses(ClForest, 0, ClDirt, 5, ClPlayer, 12);
            PortQHelpers.CreateLayeredPatches(rng, MapSize,
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
                new[] { 1, 1 }, patchConstraint,
                RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            PortQHelpers.CreatePatches(rng, MapSize,
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                    RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
                },
                biome.Tier4Terrain, patchConstraint,
                RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            GaiaEntities.CreateBalancedMetalMines(rng, map, NumPlayers,
                biome.MetalSmall, biome.MetalLarge, ClMetal,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer,
                    RmgenLibrary.ScaleByMapSize(20, 35, MapSize)));

            GaiaEntities.CreateBalancedStoneMines(rng, map, NumPlayers,
                biome.StoneSmall, biome.StoneLarge, ClRock,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer,
                    RmgenLibrary.ScaleByMapSize(20, 35, MapSize), ClMetal, 10));

            double planetm = BiomeName == "generic/india" ? 8 : 1;
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
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers },
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, ClMetal, 4,
                    ClRock, 4, clFood, 20),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) },
                },
                new double[] { 3 * NumPlayers },
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, ClMetal, 4,
                    ClRock, 4, clFood, 10),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                RmgenLibrary.AvoidClasses(ClForest, 8, ClPlayer, 12, ClMetal, 6,
                    ClRock, 6, clFood, 1),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }
    }

    internal static class PortQHelpers
    {
        public static void CreateBumps(RmgenRng rng, int mapSize, IConstraint constraint)
        {
            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, mapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, mapSize)),
                    0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        2, 2, relative: true),
                },
                constraint,
                RmgenLibrary.ScaleByMapSize(100, 200, mapSize));
        }

        public static void CreateHills(RmgenRng rng, int mapSize, object[] terrainSet,
            IConstraint constraint, TileClass tileClass, double count,
            double? minSize = null, double? maxSize = null, double? spread = null,
            double failFraction = 0.5, double elevation = 18, double elevationSmoothing = 2)
        {
            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng,
                    minSize ?? 1,
                    maxSize ?? Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, mapSize)),
                    spread ?? Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, mapSize)),
                    failFraction),
                new IPainter[]
                {
                    new LayeredPainter(terrainSet, new[] { 1, elevationSmoothing }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        elevation, elevationSmoothing),
                    new TileClassPainter(tileClass),
                },
                constraint, count);
        }

        public static void CreateLayeredPatches(RmgenRng rng, int mapSize, double[] sizes,
            object[] terrains, int[] terrainWidths, IConstraint constraint, double count,
            TileClass tileClass, double failFraction = 0.5)
        {
            foreach (double size in sizes)
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, mapSize)),
                        size, failFraction),
                    new IPainter[]
                    {
                        new LayeredPainter(terrains, terrainWidths, rng),
                        new TileClassPainter(tileClass),
                    },
                    constraint, count);
        }

        public static void CreatePatches(RmgenRng rng, int mapSize, double[] sizes,
            object terrain, IConstraint constraint, double count, TileClass tileClass,
            double failFraction = 0.5)
        {
            foreach (double size in sizes)
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, mapSize)),
                        size, failFraction),
                    new IPainter[]
                    {
                        new TerrainPainter(terrain, rng),
                        new TileClassPainter(tileClass),
                    },
                    constraint, count);
        }

        public static void PlacePlayerBaseDecoratives(RmgenRng rng, RandomMap map,
            RmgenVector2D playerPosition, string template, TileClass baseResourceClass)
        {
            var baseResourceConstraint = RmgenLibrary.AvoidClasses(baseResourceClass, 4);
            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(2, 5, map.GetSize()); ++i)
            {
                bool success = false;
                for (int tries = 0; tries < 30; ++tries)
                {
                    var offset = new RmgenVector2D(0, rng.RandIntInclusive(8, 11));
                    offset.Rotate(rng.RandomAngle());
                    var position = RmgenVector2D.Add(offset, playerPosition);
                    position.Round();
                    if (RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(rng, template, 2, 5, 0, 1),
                        }, false, baseResourceClass, position),
                        0, baseResourceConstraint))
                    {
                        success = true;
                        break;
                    }
                }

                if (!success)
                    return;
            }
        }
    }
}
