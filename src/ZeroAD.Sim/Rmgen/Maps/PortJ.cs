using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>island_stronghold.js（逐字移植）——队伍围绕小岛据点紧邻开局，外海追加大小岛、鱼群、鲸鱼与沉船。
    /// placePlayersNomad 与环境设置按既有移植约定省略。</summary>
    public sealed class IslandStrongholdMap2 : StandardMap
    {
        private const int InitialMineDistance = 14;
        private const int InitialTrees = 50;
        private const string WhaleTemplate = "gaia/fauna_whale_humpback";
        private const string ShipwreckTemplate = "gaia/treasure/shipwreck";
        private const string ShipDebrisTemplate = "gaia/treasure/shipwreck_debris";
        private const string ObeliskTemplate = "structures/obelisk";

        protected override double HeightLand => -10;

        /// <summary>上游 island_stronghold.json SupportedBiomes 显式 7 项。</summary>
        protected override IReadOnlyList<string> SupportedBiomes => BiomeLoader.SevenGenericBiomes;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.Water, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;

            const double heightSeaGround = -10;
            const double heightLandValue = 3;
            const double heightHillValue = 18;

            var clFood = new TileClass(MapSize);
            var clLand = new TileClass(MapSize);

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

            var mapCenter = map.GetCenter();
            double startAngle = rng.RandomAngle();
            var teams = RmgenCommon.GetTeamsArray(rng, settings);
            int numTeams = teams.Count;
            var teamPosition = RmgenGeometry.DistributePointsOnCircle(
                numTeams, startAngle, RmgenLibrary.FractionToTiles(0.3, MapSize), mapCenter).points;
            double teamRadius = RmgenLibrary.FractionToTiles(0.05, MapSize);

            for (int i = 0; i < teams.Count; ++i)
            {
                if (settings.Nomad)
                    continue;

                var (playerPosition, playerAngle) = RmgenGeometry.DistributePointsOnCircle(
                    teams[i].Count, startAngle + 2 * SafeMath.PI / teams[i].Count, teamRadius, teamPosition[i]);
                for (int p = 0; p < playerPosition.Count; ++p)
                {
                    var rounded = playerPosition[p];
                    rounded.Round();
                    playerPosition[p] = rounded;
                }

                for (int p = 0; p < teams[i].Count; ++p)
                {
                    RmgenCommon.AddCivicCenterAreaToClass(map, playerPosition[p], ClPlayer);
                    RmgenLibrary.CreateArea(
                        new ChainPlacer(rng, 2,
                            Math.Floor(RmgenLibrary.ScaleByMapSize(5, 11, MapSize)),
                            Math.Floor(RmgenLibrary.ScaleByMapSize(60, 250, MapSize)),
                            double.PositiveInfinity,
                            playerPosition[p],
                            double.PositiveInfinity,
                            new[] { (int)Math.Floor(RmgenCommon.DefaultPlayerBaseRadius(MapSize) * 3 / 4) }),
                        new IPainter[]
                        {
                            new TerrainPainter(biome.MainTerrain, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                                heightLandValue, 2),
                            new TileClassPainter(clLand),
                        },
                        null);

                    RmgenCommon.PlaceStartingEntities(map, playerPosition[p], teams[i][p],
                        RmgenCommon.GetStartingEntities(settings.DataRoot,
                            RmgenCommon.GetCivCode(settings, teams[i][p])),
                        6, -SafeMath.PI / 4);
                }

                double mineAngle = rng.RandFloat(-1, 1) * SafeMath.PI / teams[i].Count;
                var mines = new (string Template, double Angle)[]
                {
                    (biome.MetalLarge, mineAngle),
                    (biome.StoneLarge, mineAngle + SafeMath.PI / 4),
                };

                for (int p = 0; p < teams[i].Count; ++p)
                    foreach (var mine in mines)
                    {
                        var offset = new RmgenVector2D(InitialMineDistance, 0);
                        offset.Rotate(-playerAngle[p] - mine.Angle);
                        var position = RmgenVector2D.Add(playerPosition[p], offset);
                        RmgenLibrary.CreateObjectGroup(
                            new ObjectGroup(new IGroupElement[]
                            {
                                new ScatterObject(rng, mine.Template, 1, 1, 0, 4),
                            }, true, ClBaseResource, position),
                            0,
                            new AndConstraint(new IConstraint[]
                            {
                                RmgenLibrary.AvoidClasses(ClBaseResource, 4, ClPlayer, 4),
                                RmgenLibrary.StayClasses(clLand, 5),
                            }));
                    }

                for (int p = 0; p < teams[i].Count; ++p)
                {
                    const int tries = 10;
                    for (int x = 0; x < tries; ++x)
                    {
                        double treeAngle = playerAngle[p] +
                            rng.RandFloat(-1, 1) * 2 * SafeMath.PI / teams[i].Count;
                        var offset = new RmgenVector2D(16, 0);
                        offset.Rotate(-treeAngle);
                        var treePosition = RmgenVector2D.Add(playerPosition[p], offset);
                        treePosition.Round();
                        if (RmgenLibrary.CreateObjectGroup(
                            new ObjectGroup(new IGroupElement[]
                            {
                                new ScatterObject(rng, biome.Tree2, InitialTrees, InitialTrees, 0, 7),
                            }, true, ClBaseResource, treePosition),
                            0,
                            new AndConstraint(new IConstraint[]
                            {
                                RmgenLibrary.AvoidClasses(ClBaseResource, 4, ClPlayer, 4),
                                RmgenLibrary.StayClasses(clLand, 4),
                            })))
                            break;
                    }
                }

                for (int p = 0; p < teams[i].Count; ++p)
                    PlaceIslandStrongholdBerries(rng, biome.FruitBush, playerPosition[p], ClBaseResource);

                for (int p = 0; p < teams[i].Count; ++p)
                    PlaceIslandStrongholdStartingAnimal(rng, playerPosition[p], ClBaseResource);
            }

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(4, 8, MapSize) * (settings.Nomad ? 2 : 1)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(8, 16, MapSize) * (settings.Nomad ? 2 : 1)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(25, 60, MapSize)),
                    0.07, null, RmgenLibrary.ScaleByMapSize(30, 70, MapSize)),
                new IPainter[]
                {
                    new TerrainPainter(biome.MainTerrain, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightLandValue, 6),
                    new TileClassPainter(clLand),
                },
                RmgenLibrary.AvoidClasses(clLand, 3, ClPlayer, 3),
                RmgenLibrary.ScaleByMapSize(4, 14, MapSize) * (settings.Nomad ? 2 : 1),
                1);

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(4, 7, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(7, 10, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)),
                    0.07, null, RmgenLibrary.ScaleByMapSize(22, 40, MapSize)),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.MainTerrain, biome.MainTerrain },
                        new[] { 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightLandValue, 6),
                    new TileClassPainter(clLand),
                },
                RmgenLibrary.AvoidClasses(clLand, 3, ClPlayer, 3),
                RmgenLibrary.ScaleByMapSize(6, 55, MapSize),
                1);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(), new SmoothingPainter(1, 0.8, 5), null);
            RmgenLibrary.UnPaintTileClassBasedOnHeight(-10, 10,
                HeightPlacer.Mode.IncludeMinIncludeMax, clLand);
            RmgenLibrary.PaintTileClassBasedOnHeight(0, 5,
                HeightPlacer.Mode.IncludeMinIncludeMax, clLand);

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        2, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.MetalLarge, 1, 1, 0, 4) },
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 40, ClRock, 20),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                ClMetal);

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.StoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                        new ScatterObject(rng, biome.StoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                    },
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 40, ClMetal, 20),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                ClRock);

            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(
                biome.TreesMin, biome.TreesMax, biome.ForestProbability, MapSize);
            GaiaEntities.CreateForests(rng, map,
                new object[] { biome.MainTerrain, biome.ForestFloor1, biome.ForestFloor2, pForest1, pForest2 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 10, ClForest, 20, ClBaseResource, 5,
                        ClRock, 6, ClMetal, 6),
                    RmgenLibrary.StayClasses(clLand, 3),
                }),
                ClForest, forestTrees, NumPlayers);

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, biome.Hill }, new[] { 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightHillValue, 2),
                    new TileClassPainter(ClHill),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClBaseResource, 20, ClHill, 15, ClRock, 6, ClMetal, 6),
                    RmgenLibrary.StayClasses(clLand, 0),
                }),
                RmgenLibrary.ScaleByMapSize(4, 13, MapSize));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(), new SmoothingPainter(1, 0.8, 3), null);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 10, ClPlayer, 20, ClMetal, 6,
                        ClRock, 6, ClHill, 1),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                ClForest, stragglerTrees);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, ClHill, 1,
                        ClRock, 6, ClMetal, 6),
                    RmgenLibrary.StayClasses(clLand, 2),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) },
                },
                new double[] { 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 15, ClHill, 1,
                        clFood, 4, ClRock, 6, ClMetal, 6),
                    RmgenLibrary.StayClasses(clLand, 2),
                }),
                clFood);

            if (BiomeName == "generic/sahara")
                RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, ObeliskTemplate, 1, 1, 0, 1),
                    }, true),
                    0,
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClBaseResource, 0, ClHill, 0, ClRock, 0,
                            ClMetal, 0, clFood, 0),
                        RmgenLibrary.StayClasses(clLand, 1),
                    }),
                    RmgenLibrary.ScaleByMapSize(3, 8, MapSize), 1000);

            int dirtMultiplier = BiomeName == "generic/savanna" ? 3 : 1;
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
                        RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 0),
                        RmgenLibrary.StayClasses(clLand, 4),
                    }),
                    dirtMultiplier * RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        patchSize, 0.5),
                    new IPainter[] { new TerrainPainter(biome.Tier4Terrain, rng) },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 0),
                        RmgenLibrary.StayClasses(clLand, 4),
                    }),
                    dirtMultiplier * RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 1),
                }, true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0),
                    RmgenLibrary.StayClasses(clLand, 2),
                }),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.RockLarge, 1, 2, 0, 1),
                    new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 2),
                }, true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0),
                    RmgenLibrary.StayClasses(clLand, 2),
                }),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.Fish, 2, 3, 0, 2),
                }, true, clFood),
                0,
                RmgenLibrary.AvoidClasses(clLand, 4, clFood, 20),
                25 * NumPlayers, 60);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, WhaleTemplate, 1, 1, 0, 3),
                }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clLand, 4),
                    RmgenLibrary.AvoidClasses(clFood, 8),
                }),
                RmgenLibrary.ScaleByMapSize(5, 20, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, ShipwreckTemplate, 1, 1, 0, 1),
                }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clLand, 4),
                    RmgenLibrary.AvoidClasses(clFood, 8),
                }),
                RmgenLibrary.ScaleByMapSize(12, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, ShipDebrisTemplate, 1, 1, 0, 1),
                }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clLand, 4),
                    RmgenLibrary.AvoidClasses(clFood, 8),
                }),
                RmgenLibrary.ScaleByMapSize(10, 20, MapSize), 100);

            int planetMultiplier = BiomeName == "generic/india" ? 8 : 1;
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.GrassShort, 1, 2, 0, 1,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClHill, 2, ClPlayer, 2, ClDirt, 0),
                    RmgenLibrary.StayClasses(clLand, 3),
                }),
                planetMultiplier * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.Grass, 2, 4, 0, 1.8,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                    new ScatterObject(rng, biome.GrassShort, 3, 6, 1.2, 2.5,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClHill, 2, ClPlayer, 2, ClDirt, 1, ClForest, 0),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                planetMultiplier * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, 2,
                HeightPlacer.Mode.ExcludeMinExcludeMax, biome.Shore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightSeaGround, 1,
                HeightPlacer.Mode.IncludeMinIncludeMax, biome.Water);

            return map.MakeExportable();
        }

        private static void PlaceIslandStrongholdBerries(RmgenRng rng, string template,
            RmgenVector2D playerPosition, TileClass baseResourceClass)
        {
            // 上游直接调用 helper 时把 baseResourceConstraint 放进 args 但未传第二参数；这里照搬为无约束。
            for (int tries = 0; tries < 30; ++tries)
            {
                var offset = new RmgenVector2D(0, 12);
                offset.Rotate(rng.RandomAngle());
                if (RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, template, 5, 5, 1, 3),
                    }, true, baseResourceClass, RmgenVector2D.Add(offset, playerPosition)),
                    0, null))
                    return;
            }
        }

        private static void PlaceIslandStrongholdStartingAnimal(RmgenRng rng,
            RmgenVector2D playerPosition, TileClass baseResourceClass)
        {
            // 同上，args.baseResourceConstraint 在原脚本中实际未生效。
            for (int i = 0; i < 2; ++i)
            {
                bool success = false;
                for (int tries = 0; tries < 30; ++tries)
                {
                    var offset = new RmgenVector2D(0, 9);
                    offset.Rotate(rng.RandomAngle());
                    if (RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(rng, "gaia/fauna_chicken", 5, 5, 0, 2),
                        }, true, baseResourceClass, RmgenVector2D.Add(offset, playerPosition)),
                        0, null))
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

    /// <summary>snowflake_searocks.js（逐字移植）——雪花状岛链：玩家岛、中心岛、邻接岛和窄路按矩阵连接。
    /// TILE_CENTERED_HEIGHT_MAP、Walls="towers"、placePlayersNomad 与环境设置按既有移植约定省略。</summary>
    public sealed class SnowflakeSearocksMap2 : StandardMap
    {
        protected override double HeightLand => -5;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.Water, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;

            const double heightIsland = 20;
            var clFood = new TileClass(MapSize);
            var clLand = new TileClass(MapSize);

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

            double playerIslandRadius = RmgenLibrary.ScaleByMapSize(15, 30, MapSize);
            var (playerIDs, playerPosition, playerAngle, startAngle) =
                RmgenCommon.PlayerPlacementCircle(rng, map, NumPlayers,
                    RmgenLibrary.FractionToTiles(0.35, MapSize));

            var state = new SnowflakeState(NumPlayers);
            if (MapSize <= 128)
                CreateSnowflakeSearockTiny(rng, state, map.GetCenter(), playerIslandRadius,
                    heightIsland, clLand, biome.MainTerrain);
            else if (MapSize <= 192)
                CreateSnowflakeSearockWithoutCenter(rng, state, map.GetCenter(), startAngle,
                    playerIslandRadius, heightIsland, clLand, biome.MainTerrain);
            else if (MapSize <= 256)
            {
                if (NumPlayers < 6)
                    CreateSnowflakeSearockWithCenter(rng, state, map.GetCenter(), startAngle,
                        playerIslandRadius, heightIsland, clLand, biome.MainTerrain,
                        0.41, 0.49, 0.26, 1);
                else
                    CreateSnowflakeSearockWithoutCenter(rng, state, map.GetCenter(), startAngle,
                        playerIslandRadius, heightIsland, clLand, biome.MainTerrain);
            }
            else if (MapSize <= 320)
            {
                if (NumPlayers < 8)
                    CreateSnowflakeSearockWithCenter(rng, state, map.GetCenter(), startAngle,
                        playerIslandRadius, heightIsland, clLand, biome.MainTerrain,
                        0.41, 0.49, 0.26, 1);
                else
                    CreateSnowflakeSearockWithoutCenter(rng, state, map.GetCenter(), startAngle,
                        playerIslandRadius, heightIsland, clLand, biome.MainTerrain);
            }
            else if (NumPlayers < 6)
                CreateSnowflakeSearockWithCenter(rng, state, map.GetCenter(), startAngle,
                    playerIslandRadius, heightIsland, clLand, biome.MainTerrain,
                    0.41, 0.49, 0.24, 1);
            else
                CreateSnowflakeSearockWithCenter(rng, state, map.GetCenter(), startAngle,
                    playerIslandRadius, heightIsland, clLand, biome.MainTerrain,
                    0.41, 0.36, 0.28, 0.81);

            for (int i = 0; i < NumPlayers; ++i)
            {
                state.IslandPos[i] = playerPosition[i];
                CreateSnowflakeIsland(rng, state, i, 1, settings.Nomad ? clLand : ClPlayer,
                    playerIslandRadius, heightIsland, biome.MainTerrain);
            }

            for (int i = 0; i < state.NumIslands; ++i)
                for (int j = 0; j < state.NumIslands; ++j)
                    if (state.IsConnected[i, j] != 0)
                        RmgenLibrary.CreateArea(
                            new PathPlacer(rng, 0, 1, 0, 0, double.PositiveInfinity)
                            {
                                Start = state.IslandPos[i],
                                End = state.IslandPos[j],
                                Width = 11,
                            },
                            new IPainter[]
                            {
                                new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                                    heightIsland, 2),
                                new TerrainPainter(biome.MainTerrain, rng),
                                new TileClassPainter(clLand),
                            },
                            null);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TerrainPainter(biome.Cliff, rng),
                new SlopeConstraint(map, 2, double.PositiveInfinity));

            if (!settings.Nomad)
                RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                    playerPosition, biome.RoadWild, biome.Road, playerIDs,
                    options: new RmgenCommon.PlayerBaseOptions
                    {
                        BaseResourceClass = ClBaseResource,
                        ExtraBaseResourceConstraint = RmgenLibrary.StayClasses(ClPlayer, 4),
                        StartingAnimal = true,
                        BerriesTemplate = biome.FruitBush,
                        BerriesDistance = playerIslandRadius - 4,
                        Mines = new()
                        {
                            (biome.MetalLarge, (string?)null, (object?)null),
                            (biome.StoneLarge, (string?)null, (object?)null),
                        },
                        MinesDistance = playerIslandRadius - 4,
                        TreesTemplate = biome.Tree1,
                        TreesCount = (int)RmgenLibrary.ScaleByMapSize(10, 50, MapSize),
                        TreesMinDist = 11,
                        TreesMaxDist = 11,
                        DecorativesTemplate = biome.GrassShort,
                    });

            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(
                biome.TreesMin, biome.TreesMax, biome.ForestProbability, MapSize);
            var forestTypes = new object[][]
            {
                new object[]
                {
                    new object[] { biome.ForestFloor2, biome.MainTerrain, pForest1 },
                    new object[] { biome.ForestFloor2, pForest1 },
                },
                new object[]
                {
                    new object[] { biome.ForestFloor1, biome.MainTerrain, pForest2 },
                    new object[] { biome.ForestFloor1, pForest2 },
                },
            };
            double forestSize = forestTrees / (RmgenLibrary.ScaleByMapSize(2, 8, MapSize) * NumPlayers) *
                (BiomeName == "generic/savanna" ? 2 : 1);
            double forestAmount = Math.Floor(forestSize / forestTypes.Length);
            if (forestAmount != 0)
                foreach (var type in forestTypes)
                    RmgenLibrary.CreateAreas(rng,
                        new ClumpPlacer(rng, forestTrees / forestAmount, 0.1, 0.1,
                            double.PositiveInfinity),
                        new IPainter[]
                        {
                            new LayeredPainter(type, new[] { 2 }, rng),
                            new TileClassPainter(ClForest),
                        },
                        new AndConstraint(new IConstraint[]
                        {
                            RmgenLibrary.AvoidClasses(ClPlayer, 6, ClForest, 10),
                            RmgenLibrary.StayClasses(clLand, 4),
                        }),
                        forestAmount);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.StoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, biome.StoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                }, true, ClRock),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClRock, 10),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                5 * RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.StoneSmall, 2, 5, 1, 3),
                }, true, ClRock),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClRock, 10),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                5 * RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.MetalLarge, 1, 1, 0, 4),
                }, true, ClMetal),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClMetal, 10, ClRock, 5),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                5 * RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 84, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 128, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, patchSize, 0.3, 0.06, 0.5),
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
                RmgenLibrary.ScaleByMapSize(2, 32, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 80, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, patchSize, 0.3, 0.06, 0.5),
                    new IPainter[] { new TerrainPainter(biome.Tier4Terrain, rng) },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClForest, 0, ClDirt, 5, ClPlayer, 12),
                        RmgenLibrary.StayClasses(clLand, 5),
                    }),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 1),
                }, true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.RockLarge, 1, 2, 0, 1),
                    new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 2),
                }, true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4),
                }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, clFood, 20),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2),
                }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, clFood, 20),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4),
                }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, clFood, 20),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                3 * NumPlayers, 50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 9, ClMetal, 6, ClRock, 6),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                ClForest, stragglerTrees);

            int planetMultiplier = BiomeName == "generic/india" ? 8 : 1;
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.GrassShort, 1, 2, 0, 1,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 2, ClDirt, 0),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                planetMultiplier * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.Grass, 2, 4, 0, 1.8,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                    new ScatterObject(rng, biome.GrassShort, 3, 6, 1.2, 2.5,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 2, ClDirt, 1, ClForest, 0),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                planetMultiplier * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.BushMedium, 1, 2, 0, 2),
                    new ScatterObject(rng, biome.BushSmall, 2, 4, 0, 2),
                }),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 1, ClDirt, 1),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                planetMultiplier * RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            return map.MakeExportable();
        }

        private sealed class SnowflakeState
        {
            public int NumIslands;
            public int[,] IsConnected;
            public RmgenVector2D[] IslandPos;

            public SnowflakeState(int numPlayers)
            {
                int capacity = 4 * numPlayers + 1;
                IsConnected = new int[capacity, capacity];
                IslandPos = new RmgenVector2D[capacity];
            }

            public void Init(int numIslands)
            {
                NumIslands = numIslands;
                IsConnected = new int[numIslands, numIslands];
                IslandPos = new RmgenVector2D[numIslands];
            }
        }

        private void CreateSnowflakeIsland(RmgenRng rng, SnowflakeState state, int islandID,
            double size, TileClass tileClass, double playerIslandRadius, double heightIsland,
            IReadOnlyList<string> islandTerrain)
        {
            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, size * RmgenGeometry.DiskArea(playerIslandRadius),
                    0.95, 0.6, double.PositiveInfinity, state.IslandPos[islandID]),
                new IPainter[]
                {
                    new TerrainPainter(islandTerrain, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightIsland, 2),
                    new TileClassPainter(tileClass),
                },
                null);
        }

        private void CreateSnowflakeIslandAtRadialLocation(RmgenRng rng, SnowflakeState state,
            RmgenVector2D mapCenter, double startAngle, double playerIslandRadius,
            double heightIsland, TileClass clLand, IReadOnlyList<string> islandTerrain,
            int playerID, int islandID, int playerIDOffset, double distFromCenter,
            double islandRadius)
        {
            double angle = startAngle + (playerID * 2 + playerIDOffset) * SafeMath.PI / NumPlayers;
            var offset = new RmgenVector2D(RmgenLibrary.FractionToTiles(distFromCenter, MapSize), 0);
            offset.Rotate(-angle);
            var position = RmgenVector2D.Add(mapCenter, offset);
            position.Round();
            state.IslandPos[islandID] = position;
            CreateSnowflakeIsland(rng, state, islandID, islandRadius, clLand, playerIslandRadius,
                heightIsland, islandTerrain);
        }

        private void CreateSnowflakeSearockWithCenter(RmgenRng rng, SnowflakeState state,
            RmgenVector2D mapCenter, double startAngle, double playerIslandRadius,
            double heightIsland, TileClass clLand, IReadOnlyList<string> islandTerrain,
            double tertiaryIslandDist, double tertiaryIslandRadius,
            double islandBetweenPlayersDist, double islandBetweenPlayersRadius)
        {
            const double islandBetweenPlayerAndCenterDist = 0.16;
            const double islandBetweenPlayerAndCenterRadius = 0.81;
            const double centralIslandRadius = 0.36;

            int islandIDCenter = 4 * NumPlayers;
            state.Init(islandIDCenter + 1);
            state.IslandPos[islandIDCenter] = mapCenter;
            CreateSnowflakeIsland(rng, state, islandIDCenter, centralIslandRadius, clLand,
                playerIslandRadius, heightIsland, islandTerrain);

            for (int playerID = 0; playerID < NumPlayers; ++playerID)
            {
                int playerIDNeighbor = playerID + 1 < NumPlayers ? playerID + 1 : 0;
                int islandIDPlayer = playerID;
                int islandIDPlayerNeighbor = playerIDNeighbor;
                int islandIDBetweenPlayers = playerID + NumPlayers;
                int islandIDBetweenPlayerAndCenter = playerID + 2 * NumPlayers;
                int islandIDBetweenPlayerAndCenterNeighbor = playerIDNeighbor + 2 * NumPlayers;
                int islandIDTertiary = playerID + 3 * NumPlayers;

                state.IsConnected[islandIDBetweenPlayers, islandIDPlayer] = 1;
                state.IsConnected[islandIDBetweenPlayers, islandIDPlayerNeighbor] = 1;
                CreateSnowflakeIslandAtRadialLocation(rng, state, mapCenter, startAngle,
                    playerIslandRadius, heightIsland, clLand, islandTerrain,
                    playerID, islandIDBetweenPlayers, 1, islandBetweenPlayersDist,
                    islandBetweenPlayersRadius);

                state.IsConnected[islandIDBetweenPlayerAndCenter, islandIDPlayer] = 1;
                state.IsConnected[islandIDBetweenPlayerAndCenter, islandIDCenter] = 1;
                state.IsConnected[islandIDBetweenPlayerAndCenter,
                    islandIDBetweenPlayerAndCenterNeighbor] = 1;
                CreateSnowflakeIslandAtRadialLocation(rng, state, mapCenter, startAngle,
                    playerIslandRadius, heightIsland, clLand, islandTerrain,
                    playerID, islandIDBetweenPlayerAndCenter, 0,
                    islandBetweenPlayerAndCenterDist, islandBetweenPlayerAndCenterRadius);

                state.IsConnected[islandIDTertiary, islandIDBetweenPlayers] = 1;
                CreateSnowflakeIslandAtRadialLocation(rng, state, mapCenter, startAngle,
                    playerIslandRadius, heightIsland, clLand, islandTerrain,
                    playerID, islandIDTertiary, 1, tertiaryIslandDist, tertiaryIslandRadius);
            }
        }

        private void CreateSnowflakeSearockWithoutCenter(RmgenRng rng, SnowflakeState state,
            RmgenVector2D mapCenter, double startAngle, double playerIslandRadius,
            double heightIsland, TileClass clLand, IReadOnlyList<string> islandTerrain)
        {
            const double islandBetweenPlayerAndCenterDist = 0.16;
            const double islandBetweenPlayerAndCenterRadius = 0.81;

            state.Init(2 * NumPlayers);
            for (int playerID = 0; playerID < NumPlayers; ++playerID)
            {
                int playerIDNeighbor = playerID + 1 < NumPlayers ? playerID + 1 : 0;
                int islandIDPlayer = playerID;
                int islandIDPlayerNeighbor = playerIDNeighbor;
                int islandIDInFrontOfPlayer = playerID + NumPlayers;
                int islandIDInFrontOfPlayerNeighbor = playerIDNeighbor + NumPlayers;

                state.IsConnected[islandIDPlayer, islandIDPlayerNeighbor] = 1;
                state.IsConnected[islandIDPlayer, islandIDInFrontOfPlayer] = 1;
                state.IsConnected[islandIDInFrontOfPlayer, islandIDInFrontOfPlayerNeighbor] = 1;

                CreateSnowflakeIslandAtRadialLocation(rng, state, mapCenter, startAngle,
                    playerIslandRadius, heightIsland, clLand, islandTerrain,
                    playerID, islandIDInFrontOfPlayer, 0,
                    islandBetweenPlayerAndCenterDist, islandBetweenPlayerAndCenterRadius);
            }
        }

        private void CreateSnowflakeSearockTiny(RmgenRng rng, SnowflakeState state,
            RmgenVector2D mapCenter, double playerIslandRadius, double heightIsland,
            TileClass clLand, IReadOnlyList<string> islandTerrain)
        {
            state.Init(NumPlayers + 1);
            int islandIDCenter = NumPlayers;
            state.IslandPos[islandIDCenter] = mapCenter;
            CreateSnowflakeIsland(rng, state, NumPlayers, 1, clLand, playerIslandRadius,
                heightIsland, islandTerrain);

            for (int playerID = 0; playerID < NumPlayers; ++playerID)
                state.IsConnected[playerID, islandIDCenter] = 1;
        }
    }

    /// <summary>lower_nubia.js（逐字移植）——真实高度图合成尼罗河谷、水阈值修正湖泊，沙漠高地包围河岸绿洲。
    /// TILE_CENTERED_HEIGHT_MAP、placePlayersNomad 与环境设置按既有移植约定省略。</summary>
    public sealed class LowerNubiaMap2 : StandardMap
    {
        private const string SandTerrain = "desert_sand_dunes_100";
        private static readonly string[] PlateauTerrain = { "savanna_dirt_a", "savanna_dirt_b" };
        private const string NilePlantsTerrain = "desert_plants_a";
        private static readonly string[] CliffUpperTerrain =
        {
            "medit_cliff_italia", "medit_cliff_italia", "medit_cliff_italia_grass",
        };
        private const string RoadTerrain = "savanna_tile_a";
        private const string WaterTerrain = "desert_sand_wet";

        private const string AcaciaTemplate = "gaia/tree/acacia";
        private const string TreeDeadTemplate = "gaia/tree/dead";
        private const string BushBadlandsTemplate = "gaia/tree/bush_badlands";
        private const string BerryBushTemplate = "gaia/fruit/berry_05";
        private static readonly string[] PalmTemplates =
        {
            "gaia/tree/cretan_date_palm_tall",
            "gaia/tree/cretan_date_palm_short",
            "gaia/tree/palm_tropic",
            "gaia/tree/date_palm",
            "gaia/tree/senegal_date_palm",
            "gaia/tree/medit_fan_palm",
        };
        private const string StoneLargeTemplateName = "gaia/rock/savanna_large";
        private const string StoneSmallTemplateName = "gaia/rock/desert_small";
        private const string MetalLargeTemplateName = "gaia/ore/savanna_large";
        private const string MetalSmallTemplateName = "gaia/ore/desert_small";
        private const string WoodTreasureTemplate = "gaia/treasure/wood";
        private const string GazelleTemplate = "gaia/fauna_gazelle";
        private const string ElephantTemplate = "gaia/fauna_elephant_african_bush";
        private const string ElephantInfantTemplate = "gaia/fauna_elephant_african_infant";
        private const string LionTemplate = "gaia/fauna_lion";
        private const string LionessTemplate = "gaia/fauna_lioness";
        private const string HawkTemplate = "birds/buzzard";
        private const string PyramidTemplate = "structures/kush/pyramid_large";
        private const int PngPatchSize = 16;

        protected override double HeightLand => 0;
        protected override string BaseTerrain => SandTerrain;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, SandTerrain);
            var map = Map;
            var mapCenter = map.GetCenter();

            string rockActor = RmgenLibrary.ActorTemplate("geology/stone_savanna_med");
            var bushActors = new[]
            {
                RmgenLibrary.ActorTemplate("props/flora/bush_dry_a"),
                RmgenLibrary.ActorTemplate("props/flora/bush_medit_la_dry"),
                RmgenLibrary.ActorTemplate("props/flora/bush_medit_me_dry"),
                RmgenLibrary.ActorTemplate("props/flora/bush_medit_sm"),
                RmgenLibrary.ActorTemplate("props/flora/bush_medit_sm_dry"),
                RmgenLibrary.ActorTemplate("props/flora/bush_tempe_me_dry"),
                RmgenLibrary.ActorTemplate("props/flora/grass_soft_dry_large_tall"),
                RmgenLibrary.ActorTemplate("props/flora/grass_soft_dry_small_tall"),
            };

            double heightScale = MapSize / 320.0;
            double heightSeaGround = -3 * heightScale;
            double heightWaterLevel = 0 * heightScale;
            double heightNileForests = 15 * heightScale;
            double heightPlateau2 = 38 * heightScale;
            const double minHeight = -3;
            const double maxHeight = 150;

            var clWater = new TileClass(MapSize);
            var clCliff = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clPyramid = new TileClass(MapSize);
            var clPassage = new TileClass(MapSize);

            float[][] heightmapCombined = LoadLowerNubiaHeightmap(minHeight);
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new HeightmapPainter(map, heightmapCombined, minHeight, maxHeight), null);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaGround, 3),
                    new TileClassPainter(clWater),
                },
                new HeightConstraint(map, double.NegativeInfinity, heightSeaGround));

            double riverAngle = SafeMath.PI * 3 / 4;
            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(8, 15, MapSize); ++i)
            {
                double x = RmgenLibrary.FractionToTiles(rng.RandFloat(0, 1), MapSize);
                var start = new RmgenVector2D(x, 0);
                start.RotateAround(riverAngle, mapCenter);
                var end = new RmgenVector2D(x, MapSize);
                end.RotateAround(riverAngle, mapCenter);
                RmgenLibrary.CreateArea(
                    new PathPlacer(rng, 0.2, 5, 0.2, 0, double.PositiveInfinity)
                    {
                        Start = start,
                        End = end,
                        Width = RmgenLibrary.ScaleByMapSize(5, 7, MapSize),
                    },
                    new IPainter[]
                    {
                        new ElevationBlendingPainter(heightNileForests, 0.5),
                        new SmoothingPainter(2, 1, 2),
                        new TileClassPainter(clPassage),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        new NearTileClassConstraint(clWater, 4),
                        RmgenLibrary.AvoidClasses(clPassage,
                            RmgenLibrary.ScaleByMapSize(15, 25, MapSize)),
                    }));
            }

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new SmoothingPainter(1, RmgenLibrary.ScaleByMapSize(0.5, 1, MapSize), 1), null);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(), new TileClassPainter(clWater),
                new HeightConstraint(map, double.NegativeInfinity, heightSeaGround));
            RmgenLibrary.CreateArea(new MapBoundsPlacer(), new TileClassPainter(clCliff),
                new SlopeConstraint(map, 2, double.PositiveInfinity));
            RmgenLibrary.CreateArea(new MapBoundsPlacer(), new TerrainPainter(WaterTerrain, rng),
                new HeightConstraint(map, double.NegativeInfinity, heightWaterLevel));
            RmgenLibrary.CreateArea(new MapBoundsPlacer(), new TerrainPainter(PlateauTerrain, rng),
                new HeightConstraint(map, heightPlateau2, double.PositiveInfinity));

            var playerIDs = new List<int>();
            var playerPosition = new List<RmgenVector2D>();
            if (!settings.Nomad)
            {
                var placement = PlayerPlacementRandomWithIds(rng, map, settings,
                    RmgenCommon.SortAllPlayers(rng, settings),
                    RmgenLibrary.AvoidClasses(clWater, RmgenLibrary.ScaleByMapSize(8, 12, MapSize),
                        clCliff, RmgenLibrary.ScaleByMapSize(8, 12, MapSize)));
                if (placement.HasValue)
                {
                    playerIDs = placement.Value.playerIDs;
                    playerPosition = placement.Value.playerPosition;
                    foreach (var position in playerPosition)
                        RmgenLibrary.CreateArea(
                            new ClumpPlacer(rng,
                                RmgenGeometry.DiskArea(RmgenCommon.DefaultPlayerBaseRadius(MapSize) * 0.8),
                                0.95, 0.6, double.PositiveInfinity, position),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                                map.GetHeight(position), 6),
                            null);
                }
            }

            string decorative = rng.PickRandom(bushActors);
            if (!settings.Nomad && playerIDs.Count == NumPlayers)
                RmgenCommon.PlacePlayerBases(rng, map, settings, SandTerrain, ClPlayer, null,
                    playerPosition, RoadTerrain, RoadTerrain, playerIDs,
                    options: new RmgenCommon.PlayerBaseOptions
                    {
                        BaseResourceClass = ClBaseResource,
                        ExtraBaseResourceConstraint = RmgenLibrary.AvoidClasses(clCliff, 0, clWater, 0),
                        StartingAnimal = true,
                        StartingAnimalTemplate = GazelleTemplate,
                        StartingAnimalDistance = 18,
                        StartingAnimalMinGroupDistance = 2,
                        StartingAnimalMaxGroupDistance = 4,
                        StartingAnimalMinGroupCount = 2,
                        StartingAnimalMaxGroupCount = 3,
                        BerriesTemplate = BerryBushTemplate,
                        Mines = new()
                        {
                            (MetalLargeTemplateName, (string?)null, (object?)null),
                            (StoneLargeTemplateName, (string?)null, (object?)null),
                        },
                        TreesTemplate = AcaciaTemplate,
                        TreesCount = (int)RmgenLibrary.ScaleByMapSize(3, 12, MapSize),
                        TreesMinDistGroup = 2,
                        TreesMaxDistGroup = 6,
                        TreesMinDist = 15,
                        TreesMaxDist = 16,
                        Treasures = new() { (WoodTreasureTemplate, 14) },
                        DecorativesTemplate = decorative,
                    });

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TerrainPainter(NilePlantsTerrain, rng),
                new AndConstraint(new IConstraint[]
                {
                    new SlopeConstraint(map, 2, double.PositiveInfinity),
                    new NearTileClassConstraint(clWater, 2),
                }));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TerrainPainter(CliffUpperTerrain, rng),
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
                        new ScatterObject(rng, StoneSmallTemplateName, 0, 2, 0, 4, 0,
                            2 * SafeMath.PI, 1),
                        new ScatterObject(rng, StoneLargeTemplateName, 1, 1, 0, 4, 0,
                            2 * SafeMath.PI, 4),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, StoneSmallTemplateName, 3, 6, 1, 3, 0,
                            2 * SafeMath.PI, 1),
                    },
                },
                RmgenLibrary.AvoidClasses(clWater, 4, clCliff, 4, ClPlayer, 20, ClRock, 10),
                ClRock,
                RmgenLibrary.ScaleByMapSize(10, 30, MapSize));

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, MetalSmallTemplateName, 0, 2, 0, 4, 0,
                            2 * SafeMath.PI, 1),
                        new ScatterObject(rng, MetalLargeTemplateName, 1, 1, 0, 4, 0,
                            2 * SafeMath.PI, 4),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, MetalSmallTemplateName, 3, 6, 1, 3, 0,
                            2 * SafeMath.PI, 1),
                    },
                },
                RmgenLibrary.AvoidClasses(clWater, 4, clCliff, 4, ClPlayer, 20,
                    ClMetal, 10, ClRock, 5),
                ClMetal,
                RmgenLibrary.ScaleByMapSize(10, 30, MapSize));

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, PyramidTemplate, 1, 1, 1, 1),
                }, true, clPyramid),
                0,
                new AndConstraint(new IConstraint[]
                {
                    new NearTileClassConstraint(clWater, 10),
                    RmgenLibrary.AvoidClasses(clWater, 6, clCliff, 6, ClPlayer, 40,
                        ClMetal, 6, ClRock, 6),
                }),
                1, 500);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new RandomObject(rng, PalmTemplates, 1, 2, 1, 1),
                }, true, ClForest),
                0,
                new AndConstraint(new IConstraint[]
                {
                    new NearTileClassConstraint(clWater, RmgenLibrary.ScaleByMapSize(1, 8, MapSize)),
                    new HeightConstraint(map, heightNileForests, double.PositiveInfinity),
                    RmgenLibrary.AvoidClasses(
                        clWater, 0,
                        clCliff, 0,
                        ClForest, 1,
                        ClPlayer, 12,
                        ClBaseResource, 5,
                        ClMetal, 4,
                        ClRock, 4,
                        clPyramid, 6),
                }),
                RmgenLibrary.ScaleByMapSize(100, 1000, MapSize), 200);

            IConstraint avoidCollisions = RmgenLibrary.AvoidClasses(
                ClPlayer, 12,
                ClBaseResource, 5,
                clWater, 1,
                ClForest, 1,
                ClRock, 4,
                ClMetal, 4,
                clFood, 6,
                clCliff, 0,
                clPyramid, 6);

            var stragglerTreeObjects = new IGroupElement[][]
            {
                new IGroupElement[]
                {
                    new ScatterObject(rng, AcaciaTemplate, 1, 1, 0, 0),
                    new ScatterObject(rng, BushBadlandsTemplate, 0, 1, 2, 2),
                },
                new IGroupElement[]
                {
                    new ScatterObject(rng, TreeDeadTemplate, 1, 1, 0, 0),
                    new ScatterObject(rng, BushBadlandsTemplate, 0, 1, 2, 2),
                },
            };
            foreach (var objects in stragglerTreeObjects)
                RmgenLibrary.CreateObjectGroups(rng,
                    new ObjectGroup(objects, true, ClForest),
                    0,
                    new AndConstraint(new IConstraint[]
                    {
                        avoidCollisions,
                        RmgenLibrary.AvoidClasses(clWater, 10, ClForest, 4),
                    }),
                    RmgenLibrary.ScaleByMapSize(10, 180, MapSize), 10);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, GazelleTemplate, 5, 7, 2, 4),
                }, true, clFood),
                0, avoidCollisions, RmgenLibrary.ScaleByMapSize(2, 10, MapSize), 50);

            if (!settings.Nomad)
                RmgenLibrary.CreateObjectGroups(rng,
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, LionTemplate, 1, 2, 2, 4),
                        new ScatterObject(rng, LionessTemplate, 2, 3, 2, 4),
                    }, true, clFood),
                    0, avoidCollisions, RmgenLibrary.ScaleByMapSize(2, 10, MapSize), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, ElephantTemplate, 2, 3, 2, 4),
                    new ScatterObject(rng, ElephantInfantTemplate, 2, 3, 2, 4),
                }, true, clFood),
                0, avoidCollisions, RmgenLibrary.ScaleByMapSize(2, 10, MapSize), 50);

            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(0, 2, MapSize); ++i)
                map.PlaceEntityAnywhere(HawkTemplate, 0, mapCenter, rng.RandomAngle());

            var bushDecorationObjects = new List<IReadOnlyList<IGroupElement>>();
            var bushDecorationCounts = new List<double>();
            foreach (string bush in bushActors)
            {
                bushDecorationObjects.Add(new IGroupElement[]
                {
                    new ScatterObject(rng, bush, 0, 3, 2, 4),
                });
                bushDecorationCounts.Add(RmgenLibrary.ScaleByMapSize(100, 800, MapSize) *
                    rng.RandIntInclusive(1, 3));
            }
            GaiaEntities.CreateDecoration(rng, bushDecorationObjects, bushDecorationCounts,
                new AndConstraint(new IConstraint[]
                {
                    new NearTileClassConstraint(clWater, 2),
                    new HeightConstraint(map, heightWaterLevel, double.PositiveInfinity),
                    RmgenLibrary.AvoidClasses(ClForest, 0),
                }));

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, rockActor, 0, 4, 2, 4) },
                },
                new double[] { RmgenLibrary.ScaleByMapSize(100, 600, MapSize) },
                RmgenLibrary.AvoidClasses(clWater, 0));

            return map.MakeExportable();
        }

        private (List<int> playerIDs, List<RmgenVector2D> playerPosition)? PlayerPlacementRandomWithIds(
            RmgenRng rng, RandomMap map, MapSettings settings, List<int> playerIDs,
            IConstraint? constraints)
        {
            var locations = new List<RmgenVector2D>();
            int attempts = 0;
            int resets = 0;
            var mapCenter = map.GetCenter();
            double playerMinDistSquared = SafeMath.Square(RmgenLibrary.FractionToTiles(0.25, MapSize));
            double borderDistance = RmgenLibrary.FractionToTiles(0.08, MapSize);
            var area = RmgenLibrary.CreateArea(new MapBoundsPlacer(), (IPainter?)null,
                constraints == null ? null : new AndConstraint(new[] { constraints }));
            if (area == null)
                return null;

            for (int i = 0; i < NumPlayers; ++i)
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

        private float[][] LoadLowerNubiaHeightmap(double minHeight)
        {
            if (Settings.DataRoot != null)
            {
                string basePath = Path.Combine(Settings.DataRoot, "maps", "random");
                string landPath = Path.Combine(basePath, "lower_nubia_heightmap.png");
                string landThresholdPath = Path.Combine(basePath, "lower_nubia_land_threshold.png");
                string waterThresholdPath = Path.Combine(basePath, "lower_nubia_water_threshold.png");
                try
                {
                    if (File.Exists(landPath) && File.Exists(landThresholdPath) &&
                        File.Exists(waterThresholdPath))
                    {
                        var heightmapLand = ConvertHeightmap1Dto2D(LoadHeightmapImageCompat(landPath));
                        var heightmapLandThreshold = ConvertHeightmap1Dto2D(
                            LoadHeightmapImageCompat(landThresholdPath));
                        var heightmapWaterThreshold = ConvertHeightmap1Dto2D(
                            LoadHeightmapImageCompat(waterThresholdPath));
                        int n = heightmapLand.Length;
                        var combined = new float[n][];
                        for (int x = 0; x < n; ++x)
                        {
                            combined[x] = new float[n];
                            for (int y = 0; y < n; ++y)
                                combined[x][y] = heightmapLandThreshold[x][y] != 0 ||
                                    heightmapWaterThreshold[x][y] != 0 ? heightmapLand[x][y] : (float)minHeight;
                        }
                        return combined;
                    }
                }
                catch (Exception)
                {
                    // 高度图不可读时使用确定性回退。
                }
            }

            return FallbackLowerNubiaHeightmap(minHeight);
        }

        private static float[][] FallbackLowerNubiaHeightmap(double minHeight)
        {
            const int n = 513;
            var hm = new float[n][];
            for (int x = 0; x < n; ++x)
            {
                hm[x] = new float[n];
                for (int y = 0; y < n; ++y)
                {
                    double nx = (x - (n - 1) / 2.0) / (n - 1);
                    double ny = (y - (n - 1) / 2.0) / (n - 1);
                    double river = Math.Abs(nx + ny * 0.35);
                    double plateau = Math.Min(1, river * 5 + Math.Abs(nx - ny) * 0.4);
                    hm[x][y] = river < 0.045
                        ? (float)(0xFFFF * 0.14)
                        : (float)(minHeight + 0xFFFF * Math.Max(0, plateau));
                }
            }
            return hm;
        }

        private static ushort[] LoadHeightmapImageCompat(string path)
        {
            var (pixels, width, height) = DecodeGrayPng(File.ReadAllBytes(path));
            int tileSize = Math.Min(width, height);
            tileSize -= tileSize % PngPatchSize;
            var heightmap = new ushort[(tileSize + 1) * (tileSize + 1)];
            for (int y = 0; y < tileSize + 1; ++y)
                for (int x = 0; x < tileSize + 1; ++x)
                {
                    int offset = Math.Min(y, tileSize - 1) * width + Math.Min(x, tileSize - 1);
                    heightmap[(tileSize - y) * (tileSize + 1) + x] = (ushort)(256 * pixels[offset]);
                }
            return heightmap;
        }

        private static float[][] ConvertHeightmap1Dto2D(ushort[] heightmap)
        {
            int hmSize = (int)SafeMath.Sqrt(heightmap.Length);
            var result = new float[hmSize][];
            for (int x = 0; x < hmSize; ++x)
            {
                result[x] = new float[hmSize];
                for (int y = 0; y < hmSize; ++y)
                    result[x][y] = heightmap[y * hmSize + x];
            }
            return result;
        }

        private static (byte[] pixels, int width, int height) DecodeGrayPng(byte[] data)
        {
            if (data.Length < 8 || data[0] != 0x89 || data[1] != 0x50)
                throw new InvalidDataException("not a PNG");

            int width = 0;
            int height = 0;
            int bitDepth = 0;
            var idat = new MemoryStream();
            int pos = 8;
            while (pos + 8 <= data.Length)
            {
                int length = ReadBE32(data, pos);
                string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
                if (type == "IHDR")
                {
                    width = ReadBE32(data, pos + 8);
                    height = ReadBE32(data, pos + 12);
                    bitDepth = data[pos + 16];
                    int colorType = data[pos + 17];
                    int interlace = data[pos + 20];
                    if (colorType != 0 || interlace != 0 ||
                        bitDepth != 1 && bitDepth != 2 && bitDepth != 4 && bitDepth != 8)
                        throw new InvalidDataException(
                            $"unsupported PNG: bitDepth={bitDepth} colorType={colorType} interlace={interlace}");
                }
                else if (type == "IDAT")
                    idat.Write(data, pos + 8, length);
                else if (type == "IEND")
                    break;

                pos += 12 + length;
            }

            byte[] raw;
            using (var zs = new ZLibStream(new MemoryStream(idat.ToArray()), CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                zs.CopyTo(ms);
                raw = ms.ToArray();
            }

            int rowBytes = (width * bitDepth + 7) / 8;
            int stride = rowBytes + 1;
            var unfiltered = new byte[rowBytes * height];
            for (int y = 0; y < height; ++y)
            {
                int rowStart = y * stride;
                int filter = raw[rowStart];
                for (int x = 0; x < rowBytes; ++x)
                {
                    int cur = raw[rowStart + 1 + x];
                    int left = x > 0 ? unfiltered[y * rowBytes + x - 1] : 0;
                    int up = y > 0 ? unfiltered[(y - 1) * rowBytes + x] : 0;
                    int upLeft = x > 0 && y > 0 ? unfiltered[(y - 1) * rowBytes + x - 1] : 0;
                    int val = filter switch
                    {
                        0 => cur,
                        1 => cur + left,
                        2 => cur + up,
                        3 => cur + (left + up) / 2,
                        4 => cur + Paeth(left, up, upLeft),
                        _ => throw new InvalidDataException("bad PNG filter " + filter),
                    };
                    unfiltered[y * rowBytes + x] = (byte)(val & 0xFF);
                }
            }

            var pixels = new byte[width * height];
            int maxSample = (1 << bitDepth) - 1;
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                {
                    int sample;
                    if (bitDepth == 8)
                        sample = unfiltered[y * rowBytes + x];
                    else
                    {
                        int bit = x * bitDepth;
                        int packed = unfiltered[y * rowBytes + bit / 8];
                        int shift = 8 - bitDepth - bit % 8;
                        sample = (packed >> shift) & maxSample;
                    }
                    pixels[y * width + x] = (byte)(sample * 255 / maxSample);
                }

            return (pixels, width, height);
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static int ReadBE32(byte[] d, int o)
            => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
    }
}
