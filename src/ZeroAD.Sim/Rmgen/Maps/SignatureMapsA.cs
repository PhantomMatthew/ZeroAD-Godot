using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>islands.js（逐字移植）——海底起底，玩家各自小岛带码头，
    /// 其余大小岛屿散布全图；资源/森林/装饰限制在 clLand 岛陆上。
    /// TILE_CENTERED_HEIGHT_MAP、环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class IslandsMap2 : StandardMap
    {
        protected override double HeightLand => -5;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.Water, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightLand = 3;
            const double heightOffsetBump = 2;
            const double heightHill = 18;

            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clLand = new TileClass(MapSize);

            double playerIslandRadius = RmgenLibrary.ScaleByMapSize(20, 29, MapSize);
            var (playerIDs, playerPosition, playerAngle, _) = RmgenCommon.PlayerPlacementCircle(
                rng, map, NumPlayers, RmgenLibrary.FractionToTiles(0.35, MapSize));

            if (!settings.Nomad)
            {
                for (int i = 0; i < NumPlayers; ++i)
                {
                    RmgenLibrary.CreateArea(
                        new ClumpPlacer(rng, RmgenGeometry.DiskArea(playerIslandRadius),
                            0.8, 0.1, double.PositiveInfinity, playerPosition[i]),
                        new IPainter[]
                        {
                            new LayeredPainter(new object[] { biome.MainTerrain, biome.MainTerrain, biome.MainTerrain },
                                new[] { 1, 6 }, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                                heightLand, 6),
                            new TileClassPainter(clLand),
                            new TileClassPainter(ClPlayer),
                        },
                        null);

                    var dockLocation = RmgenCommon.FindLocationInDirectionBasedOnHeight(map,
                        playerPosition[i], mapCenter, -3, heightLand - 0.5, heightLand);
                    if (dockLocation.HasValue)
                        map.PlaceEntityPassable("skirmish/structures/default_dock", playerIDs[i],
                            dockLocation.Value, playerAngle[i] + SafeMath.PI);
                }
            }

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(4, 8, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(8, 14, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(25, 60, MapSize)),
                    0.07),
                new IPainter[]
                {
                    new TerrainPainter(biome.MainTerrain, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightLand, 6),
                    new TileClassPainter(clLand),
                },
                RmgenLibrary.AvoidClasses(clLand, RmgenLibrary.ScaleByMapSize(8, 12, MapSize)),
                RmgenLibrary.ScaleByMapSize(4, 14, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(4, 7, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(7, 10, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)),
                    0.07),
                new IPainter[]
                {
                    new TerrainPainter(biome.MainTerrain, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightLand, 6),
                    new TileClassPainter(clLand),
                },
                RmgenLibrary.AvoidClasses(clLand, RmgenLibrary.ScaleByMapSize(8, 12, MapSize)),
                RmgenLibrary.ScaleByMapSize(6, 54, MapSize));

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, 3,
                HeightPlacer.Mode.ExcludeMinExcludeMax, biome.Shore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -8, 1,
                HeightPlacer.Mode.ExcludeMinIncludeMax, biome.Water);

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                playerPosition, biome.RoadWild, biome.Road, playerIDs,
                cityPatchRadius: playerIslandRadius / 3,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = biome.FruitBush,
                    Mines = new() { (biome.MetalLarge, (string?)null, (object?)null),
                                    (biome.StoneLarge, (string?)null, (object?)null) },
                    Treasures = new() { ("gaia/treasure/wood", 14) },
                    TreesTemplate = biome.Tree1,
                    TreesCount = 5,
                    DecorativesTemplate = biome.GrassShort,
                });

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize),
                    0.3, 0.06, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump, 2, relative: true),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 0),
                    RmgenLibrary.StayClasses(clLand, 3),
                }),
                RmgenLibrary.ScaleByMapSize(20, 100, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, biome.Hill }, new[] { 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightHill, 2),
                    new TileClassPainter(ClHill),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 2, ClHill, 15),
                    RmgenLibrary.StayClasses(clLand, 0),
                }),
                RmgenLibrary.ScaleByMapSize(4, 13, MapSize));

            double forestTrees = biome.ForestProbability *
                RmgenLibrary.ScaleByMapSize(biome.TreesMin, biome.TreesMax, MapSize);
            double stragglerTrees = (1 - biome.ForestProbability) *
                RmgenLibrary.ScaleByMapSize(biome.TreesMin, biome.TreesMax, MapSize);
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
            var forestTypes = new object[][]
            {
                new object[] { new object[] { biome.ForestFloor2, biome.MainTerrain, pForest1 },
                               new object[] { biome.ForestFloor2, pForest1 } },
                new object[] { new object[] { biome.ForestFloor1, biome.MainTerrain, pForest2 },
                               new object[] { biome.ForestFloor1, pForest2 } },
            };

            if (BiomeName != "generic/savanna")
            {
                double forestSize = forestTrees /
                    (RmgenLibrary.ScaleByMapSize(3, 6, MapSize) * NumPlayers);
                double forestNum = Math.Floor(forestSize / forestTypes.Length);
                foreach (var type in forestTypes)
                    RmgenLibrary.CreateAreas(rng,
                        new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                            forestTrees / (forestNum * Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize))),
                            0.5),
                        new IPainter[]
                        {
                            new LayeredPainter(type, new[] { 2 }, rng),
                            new TileClassPainter(ClForest),
                        },
                        new AndConstraint(new IConstraint[]
                        {
                            RmgenLibrary.AvoidClasses(ClPlayer, 0, ClForest, 10, ClHill, 0),
                            RmgenLibrary.StayClasses(clLand, 6),
                        }),
                        forestNum);
            }

            double numberOfPatches = RmgenLibrary.ScaleByMapSize(15, 45, MapSize) *
                (BiomeName == "generic/savanna" ? 3 : 1);

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
                        RmgenLibrary.StayClasses(clLand, 6),
                    }),
                    numberOfPatches);

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
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 0),
                        RmgenLibrary.StayClasses(clLand, 6),
                    }),
                    numberOfPatches);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.StoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, biome.StoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                }, avoidSelf: true, tileClass: ClRock),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 0, ClRock, 10, ClHill, 1),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.StoneSmall, 2, 5, 1, 3),
                }, avoidSelf: true, tileClass: ClRock),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 0, ClRock, 10, ClHill, 1),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.MetalLarge, 1, 1, 0, 4),
                }, avoidSelf: true, tileClass: ClMetal),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 0, ClMetal, 10, ClRock, 5, ClHill, 1),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 1),
                }, avoidSelf: true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.RockLarge, 1, 2, 0, 1),
                    new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 2),
                }, avoidSelf: true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4) },
                },
                new double[] { 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 1, clFood, 20),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2) },
                },
                new double[] { 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 1, clFood, 20),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) },
                },
                new double[] { rng.RandIntInclusive(1, 4) * NumPlayers + 2 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 8, ClHill, 1, clFood, 20),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.Fish, 2, 3, 0, 2) },
                },
                new double[] { 40 * NumPlayers },
                RmgenLibrary.AvoidClasses(clLand, 4, ClForest, 2, ClPlayer, 2, ClHill, 2, clFood, 14),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClHill, 1, ClPlayer, 0, ClMetal, 6, ClRock, 6),
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                ClForest, stragglerTrees);

            int planetm = BiomeName == "generic/india" ? 8 : 1;
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
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                planetm * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

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
                planetm * RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.BushMedium, 1, 2, 0, 2),
                    new ScatterObject(rng, biome.BushSmall, 2, 4, 0, 2),
                }),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClHill, 1, ClPlayer, 1, ClDirt, 1),
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                planetm * RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            return map.MakeExportable();
        }
    }

    /// <summary>rivers.js（逐字移植）——圆形开局之间按敌对关系切河，所有河流汇入中央湖；
    /// 河道按 waterFunc 生成可涉浅滩并标记 clWater/clShallow。环境设置与 placePlayersNomad
    /// 按既有移植约定省略。</summary>
    public sealed class RiversMap2 : StandardMap
    {
        protected override double HeightLand => 1;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightSeaGround = -3;
            const double heightShallows = -1;
            const double heightLand = 1;

            string tShore = biome.Shore;
            string tWater = biome.Water;
            if (BiomeName == "generic/india")
            {
                tShore = "tropic_dirt_b_plants";
                tWater = "tropic_dirt_b";
            }

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clShallow = new TileClass(MapSize);

            var (playerIDs, playerPosition, _, startAngle) = RmgenCommon.PlayerPlacementCircle(
                rng, map, NumPlayers, RmgenLibrary.FractionToTiles(0.35, MapSize));

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                playerPosition, biome.RoadWild, biome.Road, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = biome.FruitBush,
                    Mines = new() { (biome.MetalLarge, (string?)null, (object?)null),
                                    (biome.StoneLarge, (string?)null, (object?)null) },
                    TreesTemplate = biome.Tree1,
                    TreesCount = 2,
                    DecorativesTemplate = biome.GrassShort,
                });

            RmgenLibrary.CreateArea(
                new ClumpPlacer(rng, RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.075, MapSize)),
                    0.7, 0.1, double.PositiveInfinity, mapCenter),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tShore, tWater }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaGround, 4),
                    new TileClassPainter(clWater),
                },
                null);

            int numRivers = settings.Nomad ? rng.RandIntInclusive(4, 8) : NumPlayers;
            var rivers = RmgenGeometry.DistributePointsOnCircle(numRivers,
                startAngle + SafeMath.PI / numRivers, RmgenLibrary.FractionToTiles(0.5, MapSize),
                mapCenter).points;
            for (int i = 0; i < numRivers; ++i)
            {
                if (settings.Nomad
                    ? rng.RandBool()
                    : RmgenCommon.AreAllies(settings, playerIDs[i], playerIDs[(i + 1) % NumPlayers]))
                    continue;

                double shallowLocation = rng.RandFloat(0.2, 0.7);
                double shallowWidth = rng.RandFloat(0.12, 0.21);

                PaintRiver(rng, map, rivers[i], mapCenter,
                    RmgenLibrary.ScaleByMapSize(10, 30, MapSize), 5,
                    heightSeaGround, heightLand,
                    parallel: true, deviation: 0, meanderShort: 10, meanderLong: 0,
                    minHeight: heightSeaGround,
                    waterFunc: (position, height, riverFraction) =>
                    {
                        clWater.Add(position);

                        bool isShallow = height < heightShallows &&
                            riverFraction > shallowLocation &&
                            riverFraction < shallowLocation + shallowWidth;

                        double newHeight = isShallow ? heightShallows : Math.Max(height, heightSeaGround);

                        if (map.GetHeight(position) < newHeight)
                            return;

                        map.SetHeight(position, newHeight);
                        map.SetTexture(position, height >= 0 ? tShore : tWater);

                        if (isShallow)
                            clShallow.Add(position);
                    });
            }

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
                    RmgenLibrary.ScaleByMapSize(3, 15, MapSize));
            else
                RmgenCommon.CreateMountains(rng, map, biome.Cliff,
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 15, clWater, 2), ClHill,
                    count: (int)Math.Ceiling(RmgenLibrary.ScaleByMapSize(3, 15, MapSize)));

            double forestTrees = biome.ForestProbability *
                RmgenLibrary.ScaleByMapSize(biome.TreesMin, biome.TreesMax, MapSize);
            double stragglerTrees = (1 - biome.ForestProbability) *
                RmgenLibrary.ScaleByMapSize(biome.TreesMin, biome.TreesMax, MapSize);
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
                    RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 12),
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
                    RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 12),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            GaiaEntities.CreateBalancedMetalMines(rng, map, NumPlayers,
                biome.MetalSmall, biome.MetalLarge, ClMetal,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1,
                    ClPlayer, RmgenLibrary.ScaleByMapSize(20, 35, MapSize), ClHill, 1));

            GaiaEntities.CreateBalancedStoneMines(rng, map, NumPlayers,
                biome.StoneSmall, biome.StoneLarge, ClRock,
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1,
                    ClPlayer, RmgenLibrary.ScaleByMapSize(20, 35, MapSize), ClHill, 1, ClMetal, 10));

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

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.Reeds, 1, 3, 0, 1) },
                    new IGroupElement[] { new ScatterObject(rng, biome.Lillies, 1, 2, 0, 1) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(800, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(800, MapSize, settings.CircularMap),
                },
                RmgenLibrary.StayClasses(clShallow, 0));

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
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) },
                },
                new double[] { 3 * NumPlayers },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.Fish, 2, 3, 0, 2) },
                },
                new double[] { 35 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 8),
                    RmgenLibrary.StayClasses(clWater, 6),
                }),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                RmgenLibrary.AvoidClasses(clWater, 5, ClForest, 7, ClHill, 1, ClPlayer, 12,
                    ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }

        private delegate void RiverWaterFunc(RmgenVector2D position, double height, double riverFraction);

        private static void PaintRiver(RmgenRng rng, RandomMap map,
            RmgenVector2D start, RmgenVector2D end, double width, double fadeDist,
            double heightRiverbed, double heightLandValue,
            bool parallel, double deviation, double meanderShort, double meanderLong,
            RiverWaterFunc? waterFunc = null, IConstraint? constraint = null, double? minHeight = null)
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

                        if (!minHeight.HasValue || height < minHeight.Value)
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
    }

    /// <summary>english_channel.js（逐字移植）——无 biome 的固定温带资源表；
    /// 主海峡横贯地图并用支流/浅滩撕开陆地，玩家分列于英法两岸。
    /// 环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class EnglishChannelMap2 : StandardMap
    {
        private const string tPrimary = "temp_grass_long";
        private static readonly string[] tGrass = { "temp_grass", "temp_grass", "temp_grass_d" };
        private const string tGrassDForest = "temp_plants_bog";
        private const string tGrassA = "temp_grass_plants";
        private const string tGrassB = "temp_plants_bog";
        private const string tGrassC = "temp_mud_a";
        private static readonly string[] tHill = { "temp_highlands", "temp_grass_long_b" };
        private static readonly string[] tCliff = { "temp_cliff_a", "temp_cliff_b" };
        private const string tRoad = "temp_road";
        private const string tRoadWild = "temp_road_overgrown";
        private const string tGrassPatchBlend = "temp_grass_long_b";
        private static readonly string[] tGrassPatch = { "temp_grass_d", "temp_grass_clovers" };
        private const string tShore = "temp_dirt_gravel";
        private const string tWater = "temp_dirt_gravel_b";

        private const string oBeech = "gaia/tree/euro_beech";
        private const string oPoplar = "gaia/tree/poplar";
        private const string oApple = "gaia/fruit/apple";
        private const string oOak = "gaia/tree/oak";
        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oDeer = "gaia/fauna_deer";
        private const string oFish = "gaia/fish/generic";
        private const string oGoat = "gaia/fauna_goat";
        private const string oBoar = "gaia/fauna_boar";
        private const string oStoneLarge = "gaia/rock/temperate_large";
        private const string oStoneSmall = "gaia/rock/temperate_small";
        private const string oMetalLarge = "gaia/ore/temperate_large";

        private const string aGrass = "actor|props/flora/grass_soft_large_tall.xml";
        private const string aGrassShort = "actor|props/flora/grass_soft_large.xml";
        private const string aRockLarge = "actor|geology/stone_granite_large.xml";
        private const string aRockMedium = "actor|geology/stone_granite_med.xml";
        private const string aBushMedium = "actor|props/flora/bush_medit_me_lush.xml";
        private const string aBushSmall = "actor|props/flora/bush_medit_sm_lush.xml";
        private const string aReeds = "actor|props/flora/reeds_pond_lush_a.xml";
        private const string aLillies = "actor|props/flora/water_lillies.xml";

        protected override double HeightLand => 1;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tPrimary);
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightSeaGround = -4;
            const double heightLand = 3;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clShallow = new TileClass(MapSize);

            var pForestD = new[] { tGrassDForest + "|" + oBeech, tGrassDForest };

            double startAngle = rng.RandomAngle();
            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementRiver(
                rng, map, settings, startAngle + SafeMath.PI / 2,
                RmgenLibrary.FractionToTiles(0.6, MapSize));

            RmgenCommon.PlacePlayerBases(rng, map, settings, tPrimary, ClPlayer, null,
                playerPosition, tRoadWild, tRoad, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = oBerryBush,
                    Mines = new() { (oMetalLarge, (string?)null, (object?)null),
                                    (oStoneLarge, (string?)null, (object?)null) },
                    TreesTemplate = oOak,
                    TreesCount = 2,
                    DecorativesTemplate = aGrassShort,
                });

            var riverStart = new RmgenVector2D(0, mapCenter.Y);
            riverStart.RotateAround(startAngle, mapCenter);
            var riverEnd = new RmgenVector2D(MapSize, mapCenter.Y);
            riverEnd.RotateAround(startAngle, mapCenter);
            PaintRiver(rng, map, riverStart, riverEnd,
                RmgenLibrary.FractionToTiles(0.25, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 10, MapSize),
                heightSeaGround, heightLand,
                parallel: false, deviation: 0, meanderShort: 20, meanderLong: 0,
                waterFunc: (position, height, _) =>
                {
                    map.SetTexture(position, height < -1.5 ? tWater : tShore);
                },
                landFunc: (position, _, _) =>
                {
                    map.SetHeight(position, heightLand + 0.1);
                });

            CreateTributaryRivers(startAngle,
                rng.RandIntInclusive(9, RmgenLibrary.ScaleByMapSize(13, 21, MapSize)),
                RmgenLibrary.ScaleByMapSize(10, 20, MapSize),
                heightSeaGround,
                new[] { -6.0, -1.5 },
                SafeMath.PI / 5,
                clWater,
                clShallow,
                RmgenLibrary.AvoidClasses(ClPlayer, 8, clBaseResource, 4));

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -5, 1,
                HeightPlacer.Mode.IncludeMinExcludeMax, tWater);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, heightLand,
                HeightPlacer.Mode.IncludeMinExcludeMax, tShore);
            RmgenLibrary.PaintTileClassBasedOnHeight(-6, 0.5,
                HeightPlacer.Mode.IncludeMinExcludeMax, clWater);

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, 2, 2,
                        relative: true),
                },
                RmgenLibrary.AvoidClasses(clWater, 5, ClPlayer, 20),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tCliff, tCliff, tHill },
                        new[] { 1, 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, 18, 2),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 15, clWater, 5),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers);

            double forestTrees = 0.7 * RmgenLibrary.ScaleByMapSize(500, 3000, MapSize);
            double stragglerTrees = (1 - 0.7) * RmgenLibrary.ScaleByMapSize(500, 3000, MapSize);
            GaiaEntities.CreateForests(rng, map,
                new object[] { tGrass, tGrassDForest, tGrassDForest, pForestD, pForestD },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 17, ClHill, 0, clWater, 6),
                ClForest, forestTrees, NumPlayers);

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
                            new object[] { tGrass, tGrassA },
                            tGrassB,
                            new object[] { tGrassB, tGrassC },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 6),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            // 上游把 createPatches 当成带 terrainWidths 的旧签名使用；按该意图分层复现。
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
                        new LayeredPainter(new object[] { tGrassPatchBlend, tGrassPatch },
                            new[] { 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 6),
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
                RmgenLibrary.AvoidClasses(clWater, 2, ClForest, 1, ClPlayer, 20, ClRock, 10, ClHill, 2),
                ClRock);

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) },
                },
                RmgenLibrary.AvoidClasses(clWater, 2, ClForest, 1, ClPlayer, 20, ClMetal, 10,
                    ClRock, 5, ClHill, 2),
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
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                },
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClPlayer, 0, ClHill, 0));

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, aReeds, 1, 3, 0, 1) },
                    new IGroupElement[] { new ScatterObject(rng, aLillies, 1, 2, 0, 1) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(800, 12800, MapSize),
                    RmgenLibrary.ScaleByMapSize(800, 12800, MapSize),
                },
                RmgenLibrary.StayClasses(clShallow, 0));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oDeer, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, oGoat, 2, 3, 0, 2) },
                    new IGroupElement[] { new ScatterObject(rng, oBoar, 2, 3, 0, 2) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers, 3 * NumPlayers },
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClPlayer, 20, ClHill, 0, clFood, 15),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oBerryBush, 5, 7, 0, 4) },
                },
                new double[] { rng.RandIntInclusive(1, 4) * NumPlayers + 2 },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oFish, 2, 3, 0, 2) },
                },
                new[] { RmgenLibrary.ScaleByMapSize(30, 45, MapSize) * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 6),
                    RmgenLibrary.StayClasses(clWater, 4),
                }),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oBeech, oPoplar, oApple },
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 1, ClHill, 1, ClPlayer, 8,
                    ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }

        private delegate void RiverWaterFunc(RmgenVector2D position, double height, double riverFraction);
        private delegate void RiverLandFunc(RmgenVector2D position, double shoreDist1, double shoreDist2);

        private void CreateTributaryRivers(double riverAngle, int riverCount, double riverWidth,
            double heightRiverbed, IReadOnlyList<double> heightRange, double maxAngle,
            TileClass tributaryRiverTileClass, TileClass shallowTileClass, IConstraint constraint)
        {
            const double waviness = 0.4;
            double smoothness = RmgenLibrary.ScaleByMapSize(3, 12, MapSize);
            const double offset = 0.1;
            const double tapering = 0.05;
            const double heightShallow = -2;

            var map = Map;
            var mapCenter = map.GetCenter();

            IConstraint riverConstraint = RmgenLibrary.AvoidClasses(tributaryRiverTileClass, 3);
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

        private static void PaintRiver(RmgenRng rng, RandomMap map,
            RmgenVector2D start, RmgenVector2D end, double width, double fadeDist,
            double heightRiverbed, double heightLandValue,
            bool parallel, double deviation, double meanderShort, double meanderLong,
            RiverWaterFunc? waterFunc = null, RiverLandFunc? landFunc = null,
            IConstraint? constraint = null, double? minHeight = null)
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
    }
}
