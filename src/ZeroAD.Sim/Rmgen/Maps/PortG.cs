using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>unknown.js（逐字移植）——元地图：按上游 Object.values 顺序随机选择
    /// Archipelago/Continent/CentralSea/Isthmus/CentralRiverLand/CentralRiverNaval/
    /// RiversAndLake/EdgeSeas/Gulf/Lakes/Passes/Lowlands/Mainland 子布局。
    /// 环境设置由 MapEnvironments 表驱动处理；Walls 与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class UnknownMap2 : StandardMap
    {
        private const double HeightSeaGroundConst = -5;
        private const double HeightLandConst = 3;
        private const double HeightCliffConst = 3.12;
        private const double HeightHillConst = 18;
        private const double HeightOffsetBump = 2;
        private const string WoodTreasure = "gaia/treasure/wood";

        private TileClass _clPlayerTerritory = null!;
        private TileClass _clWater = null!;
        private TileClass _clFood = null!;
        private TileClass _clPeninsulaSteam = null!;
        private TileClass _clLand = null!;
        private TileClass _clShallow = null!;
        private List<int> _playerIDs = null!;
        private List<RmgenVector2D> _playerPosition = null!;
        private RmgenVector2D _mapCenter;
        private bool _startingTreasures;

        protected override double HeightLand => HeightSeaGroundConst;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightSeaGroundConst, biome.Water, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            _mapCenter = map.GetCenter();

            _clPlayerTerritory = new TileClass(MapSize);
            _clWater = new TileClass(MapSize);
            _clFood = new TileClass(MapSize);
            _clPeninsulaSteam = new TileClass(MapSize);
            _clLand = new TileClass(MapSize);
            _clShallow = new TileClass(MapSize);

            _playerIDs = RmgenCommon.SortAllPlayers(rng, settings);
            _playerPosition = new List<RmgenVector2D>();
            _startingTreasures = false;

            RunRandomLandscape();

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, HeightCliffConst, 40,
                HeightPlacer.Mode.IncludeMinExcludeMax, biome.Cliff);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, HeightLandConst, HeightCliffConst,
                HeightPlacer.Mode.IncludeMinExcludeMax, biome.MainTerrain);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, HeightLandConst,
                HeightPlacer.Mode.IncludeMinExcludeMax, biome.Shore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -8, 1,
                HeightPlacer.Mode.ExcludeMinIncludeMax, biome.Water);

            RmgenLibrary.UnPaintTileClassBasedOnHeight(0, HeightCliffConst,
                HeightPlacer.Mode.IncludeMinExcludeMax, _clWater);
            RmgenLibrary.UnPaintTileClassBasedOnHeight(-6, 0,
                HeightPlacer.Mode.IncludeMinExcludeMax, _clLand);

            RmgenLibrary.PaintTileClassBasedOnHeight(-6, 0,
                HeightPlacer.Mode.IncludeMinExcludeMax, _clWater);
            RmgenLibrary.PaintTileClassBasedOnHeight(0, HeightCliffConst,
                HeightPlacer.Mode.IncludeMinExcludeMax, _clLand);
            RmgenLibrary.PaintTileClassBasedOnHeight(HeightCliffConst, 40,
                HeightPlacer.Mode.IncludeMinExcludeMax, ClHill);

            if (_playerPosition.Count >= NumPlayers)
                RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                    _playerPosition, biome.RoadWild, biome.Road, _playerIDs,
                    options: new RmgenCommon.PlayerBaseOptions
                    {
                        BaseResourceClass = ClBaseResource,
                        StartingAnimal = true,
                        BerriesTemplate = biome.FruitBush,
                        Mines = new()
                        {
                            (biome.MetalLarge, (string?)null, (object?)null),
                            (biome.StoneLarge, (string?)null, (object?)null),
                        },
                        Treasures = new() { (WoodTreasure, _startingTreasures ? 14 : 0) },
                        TreesTemplate = biome.Tree1,
                        DecorativesTemplate = biome.GrassShort,
                    });

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, S(20, 50), 0.3, 0.06, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        HeightOffsetBump, 2, relative: true),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(_clWater, 2, ClPlayer, 10),
                    RmgenLibrary.StayClasses(_clLand, 3),
                }),
                rng.RandIntInclusive(0, S(1, 2) * 200));

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, S(20, 150), 0.2, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, biome.Hill }, new[] { 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        HeightHillConst, 2),
                    new TileClassPainter(ClHill),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 15, ClHill, rng.RandIntInclusive(6, 18)),
                    RmgenLibrary.StayClasses(_clLand, 0),
                }),
                rng.RandIntInclusive(0, S(4, 8)) * rng.RandIntInclusive(1, S(4, 9)));

            double treeCount = S(biome.TreesMin, biome.TreesMax);
            double numForest = biome.ForestProbability * treeCount;
            double numStragglers = (1 - biome.ForestProbability) * treeCount;
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

            object[][] types =
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
            double forestSize = numForest / (S(2, 8) * NumPlayers);
            double forestNum = Math.Floor(forestSize / types.Length);
            foreach (var type in types)
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, numForest / forestNum, 0.1, 0.1,
                        double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest,
                            rng.RandIntInclusive(5, 15), ClHill, 2),
                        RmgenLibrary.StayClasses(_clLand, 4),
                    }),
                    forestNum);

            double patchCount = (BiomeName == "generic/savanna" ? 3 : 1) * S(15, 45);
            foreach (double patchSize in new[] { S(3, 48), S(5, 84), S(8, 128) })
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
                        RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 2, ClDirt, 5, ClPlayer, 7),
                        RmgenLibrary.StayClasses(_clLand, 4),
                    }),
                    patchCount);

            foreach (double patchSize in new[] { S(2, 32), S(3, 48), S(5, 80) })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, patchSize, 0.3, 0.06, 0.5),
                    new IPainter[] { new TerrainPainter(biome.Tier4Terrain, rng) },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 2, ClDirt, 5, ClPlayer, 7),
                        RmgenLibrary.StayClasses(_clLand, 4),
                    }),
                    patchCount);

            CreateObjectGroupsDeprecated(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.StoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, biome.StoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                }, true, ClRock),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClRock, 10, ClHill, 2),
                    RmgenLibrary.StayClasses(_clLand, 3),
                }),
                rng.RandIntInclusive(S(2, 9), S(9, 40)), 100);

            CreateObjectGroupsDeprecated(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.StoneSmall, 2, 5, 1, 3),
                }, true, ClRock),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClRock, 10, ClHill, 2),
                    RmgenLibrary.StayClasses(_clLand, 3),
                }),
                rng.RandIntInclusive(S(2, 9), S(9, 40)), 100);

            CreateObjectGroupsDeprecated(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.MetalLarge, 1, 1, 0, 4),
                }, true, ClMetal),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClMetal, 10, ClRock, 5,
                        ClHill, 2),
                    RmgenLibrary.StayClasses(_clLand, 3),
                }),
                rng.RandIntInclusive(S(2, 9), S(9, 40)), 100);

            CreateObjectGroupsDeprecated(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 1),
                }, true),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(_clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 2),
                    RmgenLibrary.StayClasses(_clLand, 3),
                }),
                S(16, 262), 50);

            CreateObjectGroupsDeprecated(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.RockLarge, 1, 2, 0, 1),
                    new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 2),
                }, true),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(_clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 2),
                    RmgenLibrary.StayClasses(_clLand, 3),
                }),
                S(8, 131), 50);

            CreateObjectGroupsDeprecated(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4),
                }, true, _clFood),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(_clWater, 0, ClForest, 0, ClPlayer, 8, ClHill, 2,
                        _clFood, 20),
                    RmgenLibrary.StayClasses(_clLand, 2),
                }),
                rng.RandIntInclusive(NumPlayers + 3, 5 * NumPlayers + 4), 50);

            CreateObjectGroupsDeprecated(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4),
                }, true, _clFood),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(_clWater, 0, ClForest, 0, ClPlayer, 8, ClHill, 2,
                        _clFood, 20),
                    RmgenLibrary.StayClasses(_clLand, 2),
                }),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            CreateObjectGroupsDeprecated(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2),
                }, true, _clFood),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(_clWater, 0, ClForest, 0, ClPlayer, 8, ClHill, 2,
                        _clFood, 20),
                    RmgenLibrary.StayClasses(_clLand, 2),
                }),
                rng.RandIntInclusive(NumPlayers + 3, 5 * NumPlayers + 4), 50);

            CreateObjectGroupsDeprecated(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.Fish, 2, 3, 0, 2),
                }, true, _clFood),
                RmgenLibrary.AvoidClasses(_clLand, 4, ClForest, 0, ClPlayer, 0, ClHill, 2,
                    _clFood, 20),
                rng.RandIntInclusive(15, 40) * NumPlayers, 60);

            var stragglerTypes = new[] { biome.Tree1, biome.Tree2, biome.Tree3, biome.Tree4 };
            double stragglerNum = Math.Floor(numStragglers / stragglerTypes.Length);
            foreach (string type in stragglerTypes)
                CreateObjectGroupsDeprecated(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, type, 1, 1, 0, 3),
                    }, true, ClForest),
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(_clWater, 1, ClForest, 1, ClHill, 2, ClPlayer, 0,
                            ClMetal, 6, ClRock, 6, ClBaseResource, 6),
                        RmgenLibrary.StayClasses(_clLand, 4),
                    }),
                    stragglerNum);

            int planetm = BiomeName == "generic/india" ? 8 : 1;

            CreateObjectGroupsDeprecated(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.GrassShort, 1, 2, 0, 1,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(_clWater, 2, ClHill, 2, ClPlayer, 2, ClDirt, 0),
                    RmgenLibrary.StayClasses(_clLand, 3),
                }),
                planetm * S(13, 200));

            CreateObjectGroupsDeprecated(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.Grass, 2, 4, 0, 1.8,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                    new ScatterObject(rng, biome.GrassShort, 3, 6, 1.2, 2.5,
                        -SafeMath.PI / 8, SafeMath.PI / 8),
                }),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(_clWater, 3, ClHill, 2, ClPlayer, 2, ClDirt, 1,
                        ClForest, 0),
                    RmgenLibrary.StayClasses(_clLand, 3),
                }),
                planetm * S(13, 200));

            CreateObjectGroupsDeprecated(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.Lillies, 1, 2, 0, 2),
                    new ScatterObject(rng, biome.Reeds, 2, 4, 0, 2),
                }),
                RmgenLibrary.StayClasses(_clShallow, 1),
                60 * S(13, 200), 80);

            CreateObjectGroupsDeprecated(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biome.BushMedium, 1, 2, 0, 2),
                    new ScatterObject(rng, biome.BushSmall, 2, 4, 0, 2),
                }),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(_clWater, 1, ClHill, 2, ClPlayer, 1, ClDirt, 1),
                    RmgenLibrary.StayClasses(_clLand, 3),
                }),
                planetm * S(13, 200), 50);

            return map.MakeExportable();
        }

        private void RunRandomLandscape()
        {
            switch (Rng.RandIntExclusive(0, 13))
            {
                case 0: UnknownArchipelago(); break;
                case 1: UnknownContinent(); break;
                case 2: UnknownCentralSeaOrIsthmus(false); break;
                case 3: UnknownCentralSeaOrIsthmus(true); break;
                case 4: UnknownCentralRiver(true); break;
                case 5: UnknownCentralRiver(false); break;
                case 6: UnknownRiversAndLake(); break;
                case 7: UnknownEdgeSeas(); break;
                case 8: UnknownGulf(); break;
                case 9: UnknownLakes(); break;
                case 10: UnknownPasses(); break;
                case 11: UnknownLowlands(); break;
                default: UnknownMainland(); break;
            }
        }

        private void UnknownCentralSeaOrIsthmus(bool isthmus)
        {
            const double waterHeight = -3;
            double startAngle = Rng.RandomAngle();
            var (riverStart, riverEnd) = CentralRiverCoordinates(startAngle);

            PortGMapHelpers.PaintRiver(Rng, Map, riverStart, riverEnd,
                F(S(0.27, 0.42) + Rng.RandFloat(0, 0.08)),
                S(3, 12), waterHeight, HeightLandConst,
                parallel: false, deviation: 0, meanderShort: 20, meanderLong: 0,
                waterFunc: (position, height, _) =>
                {
                    if (height < 0)
                        _clWater.Add(position);
                },
                landFunc: (position, _, _) =>
                {
                    Map.SetHeight(position, 3.1);
                    _clLand.Add(position);
                });

            if (!Settings.Nomad)
            {
                (_playerIDs, _playerPosition) = RmgenCommon.PlayerPlacementRiver(
                    Rng, Map, Settings, startAngle + SafeMath.PI / 2, F(0.6));
                MarkPlayerArea(large: false);
            }

            if (isthmus)
            {
                var (isthmusStart, isthmusEnd) =
                    CentralRiverCoordinates(startAngle + SafeMath.PI / 2);
                RmgenLibrary.CreateArea(
                    PortGMapHelpers.Path(Rng, isthmusStart, isthmusEnd,
                        S(Rng.RandIntInclusive(16, 24), Rng.RandIntInclusive(100, 140)),
                        0.5, 3 * S(1, 4), 0.1, 0.01),
                    new IPainter[]
                    {
                        LandElevationPainter(),
                        new TileClassPainter(_clLand),
                        new TileClassUnPainter(_clWater),
                    },
                    null);
            }

            CreateExtensionsOrIslands();
        }

        private void UnknownCentralRiver(bool shallows)
        {
            const double waterHeight = -4;
            const double heightShallow = -2;

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new ElevationPainter(HeightLandConst), null);

            double startAngle = Rng.RandomAngle();
            if (!Settings.Nomad)
            {
                (_playerIDs, _playerPosition) = RmgenCommon.PlayerPlacementRiver(
                    Rng, Map, Settings, startAngle + SafeMath.PI / 2, F(0.5));
                MarkPlayerArea(large: true);
            }

            var (coord1, coord2) = CentralRiverCoordinates(startAngle);
            RmgenLibrary.CreateArea(
                PortGMapHelpers.Path(Rng, coord1, coord2, S(14, 24), 0.5, S(3, 12), 0.1, 0.01),
                new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                    waterHeight, 4),
                RmgenLibrary.AvoidClasses(_clPlayerTerritory, 4));

            foreach (var coord in new[] { coord1, coord2 })
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(S(5, 10)), 0.95, 0.6,
                        double.PositiveInfinity, coord),
                    new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                        waterHeight, 2),
                    RmgenLibrary.AvoidClasses(_clPlayerTerritory, 8));

            if (shallows)
            {
                for (int i = 0; ; ++i)
                {
                    if (i > Rng.RandIntInclusive(1, S(4, 8)))
                        break;

                    double location = F(Rng.RandFloat(0.15, 0.85));
                    var start = new RmgenVector2D(location, MapSize);
                    start.RotateAround(startAngle, _mapCenter);
                    var end = new RmgenVector2D(location, 0);
                    end.RotateAround(startAngle, _mapCenter);
                    RmgenCommon.CreatePassage(Rng, Map, start, end,
                        S(8, 12), S(8, 12), 2,
                        tileClass: _clShallow,
                        constraints: new HeightConstraint(Map, double.NegativeInfinity, heightShallow),
                        startHeight: heightShallow, endHeight: heightShallow);
                }
            }

            if (Rng.RandBool(2.0 / 3))
                CreateTributaryRivers(startAngle,
                    Rng.RandIntInclusive(8, S(12, 16)),
                    S(10, 20), -4, new[] { -6.0, -1.5 }, SafeMath.PI / 5,
                    _clWater, _clShallow, RmgenLibrary.AvoidClasses(_clPlayerTerritory, 3));
        }

        private void UnknownArchipelago()
        {
            _startingTreasures = true;

            var (pIDs, islandPosition, _, _) =
                RmgenCommon.PlayerPlacementCircle(Rng, Map, NumPlayers, F(0.35));
            if (!Settings.Nomad)
            {
                _playerIDs = pIDs;
                _playerPosition = islandPosition;
                MarkPlayerArea(large: true);
            }

            double islandSize = RmgenGeometry.DiskArea(S(17, 29));
            for (int i = 0; i < NumPlayers; ++i)
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, islandSize, 0.8, 0.1,
                        double.PositiveInfinity, islandPosition[i]),
                    LandElevationPainter(), null);

            switch (Rng.RandIntInclusive(1, Settings.Nomad ? 2 : 3))
            {
                case 1:
                    RmgenLibrary.CreateAreas(Rng,
                        new ClumpPlacer(Rng, islandSize * Rng.RandFloat(0.8, 1.2),
                            0.8, 0.1, double.PositiveInfinity),
                        new IPainter[] { LandElevationPainter(), new TileClassPainter(_clLand) },
                        null,
                        S(2, 5) * Rng.RandIntInclusive(8, 14));

                    RmgenLibrary.CreateAreas(Rng,
                        new ClumpPlacer(Rng, S(15, 80), 0.2, 0.1, double.PositiveInfinity),
                        new IPainter[]
                        {
                            new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                                HeightLandConst, 4),
                            new TileClassPainter(_clLand),
                        },
                        RmgenLibrary.BorderClasses(_clLand, 6, 3),
                        S(12, 130) * 2, 150);
                    break;

                case 2:
                    RmgenLibrary.CreateAreas(Rng,
                        new ClumpPlacer(Rng, islandSize * Rng.RandFloat(0.6, 1.4),
                            0.8, 0.1, Rng.RandFloat(0.0, 0.2)),
                        new IPainter[] { LandElevationPainter(), new TileClassPainter(_clLand) },
                        RmgenLibrary.AvoidClasses(_clLand, 3, _clPlayerTerritory, 3),
                        S(6, 10) * Rng.RandIntInclusive(8, 14));

                    RmgenLibrary.CreateAreas(Rng,
                        new ClumpPlacer(Rng, islandSize * Rng.RandFloat(0.3, 0.7),
                            0.8, 0.1, 0.07),
                        new IPainter[]
                        {
                            new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                                HeightLandConst, 6),
                            new TileClassPainter(_clLand),
                        },
                        RmgenLibrary.AvoidClasses(_clLand, 3, _clPlayerTerritory, 3),
                        S(2, 6) * Rng.RandIntInclusive(6, 15), 25);
                    break;

                default:
                    RmgenLibrary.CreateAreas(Rng,
                        new ClumpPlacer(Rng, islandSize * Rng.RandFloat(0.8, 1.2),
                            0.8, 0.1, double.PositiveInfinity),
                        new IPainter[] { LandElevationPainter(), new TileClassPainter(_clLand) },
                        RmgenLibrary.AvoidClasses(_clLand, Rng.RandIntInclusive(8, 16),
                            _clPlayerTerritory, 3),
                        S(2, 5) * Rng.RandIntInclusive(8, 14));
                    break;
            }
        }

        private void UnknownContinent()
        {
            const double waterHeight = -5;

            if (!Settings.Nomad)
            {
                (_playerIDs, _playerPosition, _, _) =
                    RmgenCommon.PlayerPlacementCircle(Rng, Map, NumPlayers, F(0.25));
                MarkPlayerArea(large: false);

                for (int i = 0; i < NumPlayers; ++i)
                    RmgenLibrary.CreateArea(
                        new ChainPlacer(Rng, 2, Math.Floor(S(5, 9)), Math.Floor(S(5, 20)),
                            double.PositiveInfinity, _playerPosition[i], 0,
                            new[] { (int)Math.Floor(S(23, 50)) }),
                        new IPainter[] { LandElevationPainter(), new TileClassPainter(_clLand) },
                        null);
            }

            RmgenLibrary.CreateArea(
                new ClumpPlacer(Rng, RmgenGeometry.DiskArea(F(0.38)), 0.9, 0.09,
                    double.PositiveInfinity, _mapCenter),
                new IPainter[] { LandElevationPainter(), new TileClassPainter(_clLand) },
                null);

            if (Rng.RandBool(1.0 / 3))
            {
                double angle = Rng.RandomAngle();
                var offset1 = new RmgenVector2D(F(0.25), 0);
                offset1.Rotate(-angle);
                var peninsulaPosition1 = RmgenVector2D.Add(_mapCenter, offset1);
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(F(0.38)), 0.9, 0.09,
                        double.PositiveInfinity, peninsulaPosition1),
                    new IPainter[] { LandElevationPainter(), new TileClassPainter(_clLand) },
                    null);

                var offset2 = new RmgenVector2D(F(0.35), 0);
                offset2.Rotate(-angle);
                var peninsulaPosition2 = RmgenVector2D.Add(_mapCenter, offset2);
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(F(0.33)), 0.9, 0.01,
                        double.PositiveInfinity, peninsulaPosition2),
                    new TileClassPainter(_clPeninsulaSteam),
                    null);
            }

            CreateShoreJaggedness(waterHeight, _clLand, 7);
        }

        private void UnknownRiversAndLake()
        {
            const double waterHeight = -4;
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new ElevationPainter(HeightLandConst), null);

            double startAngle;
            if (!Settings.Nomad)
            {
                (_playerIDs, _playerPosition, _, startAngle) =
                    RmgenCommon.PlayerPlacementCircle(Rng, Map, NumPlayers, F(0.35));
                MarkPlayerArea(large: false);
            }
            else
                startAngle = Rng.RandomAngle();

            bool lake = Rng.RandBool(3.0 / 4);
            if (lake)
            {
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(F(0.17)), 0.7, 0.1,
                        double.PositiveInfinity, _mapCenter),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            waterHeight, 4),
                        new TileClassPainter(_clWater),
                    },
                    null);

                CreateShoreJaggedness(waterHeight, _clWater, 3);
            }

            foreach (var river in RmgenGeometry.DistributePointsOnCircle(
                         NumPlayers, startAngle + SafeMath.PI / NumPlayers, F(0.5), _mapCenter).points)
            {
                RmgenLibrary.CreateArea(
                    PortGMapHelpers.Path(Rng, _mapCenter, river, S(14, 24), 0.4,
                        3 * S(1, 3), 0.2, 0.05),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            waterHeight, 4),
                        new TileClassPainter(_clWater),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 5));

                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(S(4, 22)), 0.95, 0.6,
                        double.PositiveInfinity, river),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            waterHeight, 0),
                        new TileClassPainter(_clWater),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 5));
            }

            RmgenLibrary.CreateArea(
                new ClumpPlacer(Rng, RmgenGeometry.DiskArea(F(0.04)), 0.7, 0.1,
                    double.PositiveInfinity, _mapCenter),
                new IPainter[]
                {
                    new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                        waterHeight, 4),
                    new TileClassPainter(_clWater),
                },
                null);

            if (!Settings.Nomad && lake && Rng.RandBool(2.0 / 3))
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(F(0.05)), 0.7, 0.1,
                        double.PositiveInfinity, _mapCenter),
                    new IPainter[] { LandElevationPainter(), new TileClassPainter(_clWater) },
                    null);
        }

        private void UnknownEdgeSeas()
        {
            const double waterHeight = -4;
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new ElevationPainter(HeightLandConst), null);

            double startAngle = Rng.RandomAngle();
            if (!Settings.Nomad)
            {
                _playerIDs = RmgenCommon.SortAllPlayers(Rng, Settings);
                _playerPosition = PlayerPlacementLine(startAngle + SafeMath.PI / 2,
                    _mapCenter, F(0.2));
                MarkPlayerArea(large: false);
            }

            var sides = Rng.PickRandom(new[]
            {
                new[] { 0.0 },
                new[] { SafeMath.PI },
                new[] { 0.0, SafeMath.PI },
            });

            foreach (double side in sides)
            {
                var start = new RmgenVector2D(0, MapSize);
                start.RotateAround(side + startAngle, _mapCenter);
                var end = new RmgenVector2D(0, 0);
                end.RotateAround(side + startAngle, _mapCenter);
                PortGMapHelpers.PaintRiver(Rng, Map, start, end,
                    S(80, Rng.RandFloat(270, 320)),
                    S(2, 8), waterHeight, HeightLandConst,
                    parallel: true, deviation: 0, meanderShort: 20, meanderLong: 0);
            }

            CreateExtensionsOrIslands();
            RmgenLibrary.PaintTileClassBasedOnHeight(0, HeightCliffConst,
                HeightPlacer.Mode.IncludeMinExcludeMax, _clLand);
            CreateShoreJaggedness(waterHeight, _clLand, 7, inwards: false);
        }

        private void UnknownGulf()
        {
            const double waterHeight = -3;
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new ElevationPainter(HeightLandConst), null);

            double startAngle = Rng.RandomAngle();
            if (!Settings.Nomad)
            {
                _playerPosition = PlayerPlacementCustomAngle(F(0.35), _mapCenter, i =>
                    startAngle + 2.0 / 3 * SafeMath.PI *
                    (-1 + (NumPlayers == 1 ? 1 : 2.0 * i / (NumPlayers - 1))));
                MarkPlayerArea(large: true);
            }

            foreach (var gulfPart in new[] { (radius: 0.16, distance: 0.0),
                         (radius: 0.2, distance: 0.2), (radius: 0.22, distance: 0.49) })
            {
                double radius = F(gulfPart.radius);
                double distance = F(gulfPart.distance);
                var offset = new RmgenVector2D(distance, 0);
                offset.Rotate(-startAngle);
                var position = RmgenVector2D.Sub(_mapCenter, offset);
                position.Round();
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(radius), 0.7, 0.05,
                        double.PositiveInfinity, position),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            waterHeight, 4),
                        new TileClassPainter(_clWater),
                    },
                    RmgenLibrary.AvoidClasses(_clPlayerTerritory,
                        RmgenCommon.DefaultPlayerBaseRadius(MapSize)));
            }
        }

        private void UnknownLakes()
        {
            const double waterHeight = -5;
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new ElevationPainter(HeightLandConst), null);

            if (!Settings.Nomad)
            {
                (_playerIDs, _playerPosition, _, _) =
                    RmgenCommon.PlayerPlacementCircle(Rng, Map, NumPlayers, F(0.35));
                MarkPlayerArea(large: true);
            }

            IConstraint lakeConstraint = RmgenLibrary.AvoidClasses(_clPlayerTerritory, 12);
            if (Rng.RandBool())
                lakeConstraint = new AndConstraint(new IConstraint[]
                {
                    lakeConstraint,
                    RmgenLibrary.AvoidClasses(_clWater, 8),
                });

            RmgenLibrary.CreateAreas(Rng,
                new ClumpPlacer(Rng, S(160, 700), 0.2, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                        waterHeight, 5),
                    new TileClassPainter(_clWater),
                },
                lakeConstraint, S(5, 16));
        }

        private void UnknownPasses()
        {
            const double heightMountain = 24;
            const double waterHeight = -4;

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new ElevationPainter(HeightLandConst), null);

            double startAngle;
            if (!Settings.Nomad)
            {
                (_playerIDs, _playerPosition, _, startAngle) =
                    RmgenCommon.PlayerPlacementCircle(Rng, Map, NumPlayers, F(0.35));
                MarkPlayerArea(large: false);
            }
            else
                startAngle = Rng.RandomAngle();

            foreach (var mountain in RmgenGeometry.DistributePointsOnCircle(
                         NumPlayers, startAngle + SafeMath.PI / NumPlayers, F(0.5), _mapCenter).points)
            {
                RmgenLibrary.CreateArea(
                    PortGMapHelpers.Path(Rng, _mapCenter, mountain, S(14, 24), 0.4,
                        3 * S(1, 3), 0.2, 0.05),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            heightMountain, 1),
                        new TileClassPainter(_clWater),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 5));

                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(S(4, 22)), 0.95, 0.6,
                        double.PositiveInfinity, mountain),
                    new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                        heightMountain, 0),
                    RmgenLibrary.AvoidClasses(ClPlayer, 5));
            }

            if (NumPlayers > 1)
            {
                List<RmgenVector2D>? passes = null;
                if (NumPlayers == 2)
                    passes = RmgenGeometry.DistributePointsOnCircle(
                        NumPlayers * 3, startAngle, F(0.35), _mapCenter).points;

                for (int i = 0; i < NumPlayers; ++i)
                {
                    RmgenVector2D start;
                    RmgenVector2D end;
                    if (NumPlayers != 2)
                    {
                        start = _playerPosition[i];
                        end = _playerPosition[(i + 1) % NumPlayers];
                    }
                    else
                    {
                        start = passes![3 * i + 1];
                        end = passes[3 * i + 2];
                    }

                    RmgenLibrary.CreateArea(
                        PortGMapHelpers.Path(Rng, start, end, S(14, 24), 0.4,
                            3 * S(1, 3), 0.2, 0.05),
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            HeightLandConst, 2),
                        null);
                }
            }

            if (Rng.RandBool(2.0 / 5))
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(F(0.1)), 0.7, 0.1,
                        double.PositiveInfinity, _mapCenter),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            waterHeight, 3),
                        new TileClassPainter(_clWater),
                    },
                    null);
            else
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(F(0.05)), 0.7, 0.1,
                        double.PositiveInfinity, _mapCenter),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            heightMountain, 4),
                        new TileClassPainter(_clWater),
                    },
                    null);
        }

        private void UnknownLowlands()
        {
            const double heightMountain = 30;
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new ElevationPainter(heightMountain), null);

            double startAngle;
            if (!Settings.Nomad)
            {
                (_playerIDs, _playerPosition, _, startAngle) =
                    RmgenCommon.PlayerPlacementCircle(Rng, Map, NumPlayers, F(0.35));
                MarkPlayerArea(large: false);
            }
            else
                startAngle = Rng.RandomAngle();

            int valleys = NumPlayers;
            if (MapSize >= 128 && NumPlayers <= 2 ||
                MapSize >= 192 && NumPlayers <= 3 ||
                MapSize >= 320 && NumPlayers <= 4 ||
                MapSize >= 384 && NumPlayers <= 5 ||
                MapSize >= 448 && NumPlayers <= 6)
            {
                valleys *= 2;
            }

            foreach (var valley in RmgenGeometry.DistributePointsOnCircle(
                         valleys, startAngle, F(0.35), _mapCenter).points)
            {
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, RmgenGeometry.DiskArea(S(18, 32)), 0.65, 0.1,
                        double.PositiveInfinity, valley),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            HeightLandConst, 2),
                        new TileClassPainter(_clLand),
                    },
                    null);

                RmgenLibrary.CreateArea(
                    PortGMapHelpers.Path(Rng, _mapCenter, valley, S(14, 24), 0.4,
                        3 * S(1, 3), 0.2, 0.05),
                    new IPainter[] { LandElevationPainter(), new TileClassPainter(_clWater) },
                    null);
            }

            RmgenLibrary.CreateArea(
                new ClumpPlacer(Rng, RmgenGeometry.DiskArea(F(0.18)), 0.7, 0.1,
                    double.PositiveInfinity, _mapCenter),
                new IPainter[] { LandElevationPainter(), new TileClassPainter(_clWater) },
                null);
        }

        private void UnknownMainland()
        {
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new ElevationPainter(HeightLandConst), null);

            if (!Settings.Nomad)
            {
                (_playerIDs, _playerPosition, _, _) =
                    RmgenCommon.PlayerPlacementCircle(Rng, Map, NumPlayers, F(0.35));
                MarkPlayerArea(large: false);
            }
        }

        private (RmgenVector2D start, RmgenVector2D end) CentralRiverCoordinates(double angle)
        {
            var start = new RmgenVector2D(1, _mapCenter.Y);
            start.RotateAround(angle, _mapCenter);
            var end = new RmgenVector2D(MapSize - 1, _mapCenter.Y);
            end.RotateAround(angle, _mapCenter);
            return (start, end);
        }

        private void CreateShoreJaggedness(double waterHeight, TileClass borderClass,
            double shoreDist, bool inwards = true)
        {
            for (int i = 0; i < 2; ++i)
            {
                if (i == 0 && !inwards)
                    continue;

                RmgenLibrary.CreateAreas(Rng,
                    new ChainPlacer(Rng, 2, Math.Floor(S(4, 6)), 15,
                        double.PositiveInfinity),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            i != 0 ? HeightLandConst : waterHeight, 4),
                        i != 0 ? new TileClassPainter(_clLand) : new TileClassUnPainter(_clLand),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        RmgenLibrary.AvoidClasses(ClPlayer, 20, _clPeninsulaSteam, 20),
                        RmgenLibrary.BorderClasses(borderClass, shoreDist, shoreDist),
                    }),
                    S(7, 130) * 2, 150);
            }
        }

        private void CreateExtensionsOrIslands()
        {
            int rnd = Rng.RandIntInclusive(1, 3);
            if (rnd == 1)
            {
                int radius = Rng.RandIntInclusive(S(8, 15), S(15, 23));
                RmgenLibrary.CreateAreas(Rng,
                    new ClumpPlacer(Rng, SafeMath.Square(radius), 0.8, 0.1,
                        Rng.RandFloat(0, 0.2)),
                    new IPainter[] { LandElevationPainter(), new TileClassPainter(_clLand) },
                    RmgenLibrary.AvoidClasses(_clLand, 3, ClPlayer, 3),
                    S(2, 5) * Rng.RandIntInclusive(8, 14));
            }
            else if (rnd == 2)
            {
                RmgenLibrary.CreateAreas(Rng,
                    new ChainPlacer(Rng, Math.Floor(S(4, 7)), Math.Floor(S(7, 10)),
                        Math.Floor(S(16, 40)), 0.07),
                    new IPainter[] { LandElevationPainter(), new TileClassPainter(_clLand) },
                    null,
                    S(2, 5) * Rng.RandIntInclusive(8, 14));
            }
        }

        private void MarkPlayerArea(bool large)
        {
            foreach (var position in _playerPosition)
            {
                RmgenCommon.AddCivicCenterAreaToClass(Map, position, ClPlayer);

                if (large)
                    RmgenLibrary.CreateArea(
                        new ClumpPlacer(Rng, RmgenGeometry.DiskArea(S(17, 29) / 3),
                            0.6, 0.3, double.PositiveInfinity, position),
                        new TileClassPainter(_clPlayerTerritory),
                        null);
            }
        }

        private List<RmgenVector2D> PlayerPlacementLine(double angle, RmgenVector2D center, double width)
        {
            var playerPosition = new List<RmgenVector2D>();
            for (int i = 0; i < NumPlayers; ++i)
            {
                var offset = new RmgenVector2D(
                    F((i + 1.0) / (NumPlayers + 1) - 0.5),
                    width * (i % 2 - 0.5));
                offset.Rotate(angle);
                var position = RmgenVector2D.Add(center, offset);
                position.Round();
                playerPosition.Add(position);
            }
            return playerPosition;
        }

        private List<RmgenVector2D> PlayerPlacementCustomAngle(double radius, RmgenVector2D center,
            Func<int, double> playerAngleFunc)
        {
            var playerPosition = new List<RmgenVector2D>();
            for (int i = 0; i < NumPlayers; ++i)
            {
                double playerAngle = playerAngleFunc(i);
                var offset = new RmgenVector2D(radius, 0);
                offset.Rotate(-playerAngle);
                var position = RmgenVector2D.Add(center, offset);
                position.Round();
                playerPosition.Add(position);
            }
            return playerPosition;
        }

        private void CreateTributaryRivers(double riverAngle, int riverCount, double riverWidth,
            double heightRiverbed, IReadOnlyList<double> heightRange, double maxAngle,
            TileClass tributaryRiverTileClass, TileClass shallowTileClass, IConstraint constraint)
        {
            const double waviness = 0.4;
            double smoothness = S(3, 12);
            const double offset = 0.1;
            const double tapering = 0.05;
            const double heightShallow = -2;

            IConstraint riverConstraint = new AndConstraint(new IConstraint[]
            {
                RmgenLibrary.AvoidClasses(tributaryRiverTileClass, 3),
                RmgenLibrary.AvoidClasses(shallowTileClass, 2),
            });

            for (int i = 0; i < riverCount; ++i)
            {
                var searchCenter = new RmgenVector2D(F(Rng.RandFloat(tapering, 1 - tapering)),
                    _mapCenter.Y);
                double sign = Rng.RandBool() ? 1 : -1;
                var distanceVec = new RmgenVector2D(0, sign * tapering);

                var searchStart = RmgenVector2D.Add(searchCenter, distanceVec);
                searchStart.RotateAround(riverAngle, _mapCenter);
                var searchEnd = RmgenVector2D.Sub(searchCenter, distanceVec);
                searchEnd.RotateAround(riverAngle, _mapCenter);

                var startLocation = RmgenCommon.FindLocationInDirectionBasedOnHeight(Map,
                    searchStart, searchEnd, heightRange[0], heightRange[1], 4);
                if (!startLocation.HasValue)
                    continue;

                var start = startLocation.Value;
                start.Round();
                var endOffset = new RmgenVector2D(MapSize, 0);
                endOffset.Rotate(riverAngle -
                    sign * Rng.RandFloat(maxAngle, 2 * SafeMath.PI - maxAngle));
                var end = RmgenVector2D.Add(_mapCenter, endOffset);
                end.Round();

                var area = RmgenLibrary.CreateArea(
                    PortGMapHelpers.Path(Rng, start, end, riverWidth, waviness, smoothness,
                        offset, tapering),
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
                var start = new RmgenVector2D(0, F(z));
                start.RotateAround(riverAngle, _mapCenter);
                var end = new RmgenVector2D(MapSize, F(z));
                end.RotateAround(riverAngle, _mapCenter);

                RmgenCommon.CreatePassage(Rng, Map, start, end, S(8, 12), S(8, 12), 2,
                    tileClass: shallowTileClass,
                    constraints: new HeightConstraint(Map, double.NegativeInfinity, heightShallow),
                    startHeight: heightShallow, endHeight: heightShallow);
            }
        }

        private IPainter LandElevationPainter()
            => new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                HeightLandConst, 4);

        private void CreateObjectGroupsDeprecated(ICenteredObjectGroup group, IConstraint? constraint,
            double amount, int retryFactor = 10)
            => RmgenLibrary.CreateObjectGroupsDeprecated(Rng, group, 0, constraint, amount, retryFactor);

        private double F(double fraction) => RmgenLibrary.FractionToTiles(fraction, MapSize);

        private double S(double min, double max) => RmgenLibrary.ScaleByMapSize(min, max, MapSize);
    }

    /// <summary>the_nile.js（逐字移植）——尼罗河纵贯地图，paintRiver 回调刷湿岸、
    /// 绿洲渐变、河边植物；环境设置由 MapEnvironments 表驱动处理，placePlayersNomad 省略。</summary>
    public sealed class TheNileMap2 : StandardMap
    {
        private const string TPrimary = "desert_sand_dunes_100";
        private const string TCity = "desert_city_tile";
        private const string TCityPlaza = "desert_city_tile_plaza";
        private const string TFineSand = "desert_sand_smooth";
        private const string TForestFloor = "desert_forestfloor_palms";
        private const string TGrass = "desert_dirt_rough_2";
        private const string TGrassSand50 = "desert_sand_dunes_50";
        private const string TGrassSand25 = "desert_dirt_rough";
        private const string TDirt = "desert_dirt_rough";
        private const string TDirtCracks = "desert_dirt_cracks";
        private const string TShore = "desert_sand_wet";
        private const string TLush = "desert_grass_a";
        private const string TSLush = "desert_grass_a_sand";
        private const string TSDry = "desert_plants_b";

        private const string OBerryBush = "gaia/fruit/berry_01";
        private const string OCamel = "gaia/fauna_camel";
        private const string OGazelle = "gaia/fauna_gazelle";
        private const string OGoat = "gaia/fauna_goat";
        private const string OFish = "gaia/fish/tilapia";
        private const string OStoneLarge = "gaia/rock/badlands_large";
        private const string OStoneSmall = "gaia/rock/desert_small";
        private const string OMetalLarge = "gaia/ore/desert_large";
        private const string ODatePalm = "gaia/tree/date_palm";
        private const string OSDatePalm = "gaia/tree/cretan_date_palm_short";
        private const string EObelisk = "structures/obelisk";
        private const string EPyramid = "gaia/ruins/pyramid_minor";
        private const string OWoodTreasure = "gaia/treasure/wood";
        private const string OFoodTreasure = "gaia/treasure/food_bin";

        private const string ABush1 = "actor|props/flora/bush_desert_a.xml";
        private const string ABush2 = "actor|props/flora/bush_desert_dry_a.xml";
        private const string ABush3 = "actor|props/flora/bush_medit_sm_dry.xml";
        private const string ABush4 = "actor|props/flora/plant_desert_a.xml";
        private const string ADecorativeRock = "actor|geology/stone_desert_med.xml";
        private const string AReeds = "actor|props/flora/reeds_pond_lush_a.xml";
        private const string ALillies = "actor|props/flora/water_lillies.xml";

        private const double HeightLandConst = 1;
        private const double HeightShore = 2;
        private const double HeightPonds = -7;
        private const double HeightSeaGround = -3;
        private const double HeightOffsetBump = 2;

        protected override double HeightLand => HeightLandConst;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, TPrimary);
            var map = Map;
            var mapCenter = map.GetCenter();

            string aPlants = MapSize < 256 ?
                "actor|props/flora/grass_tropical.xml" :
                "actor|props/flora/grass_tropic_field_tall.xml";

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clGrass = new TileClass(MapSize);
            var clDesert = new TileClass(MapSize);
            var clPond = new TileClass(MapSize);
            var clShore = new TileClass(MapSize);
            var clTreasure = new TileClass(MapSize);

            var pForest = new[]
            {
                TForestFloor + "|" + ODatePalm,
                TForestFloor + "|" + OSDatePalm,
                TForestFloor,
            };

            double startAngle = rng.RandomAngle();
            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementRiver(
                rng, map, settings, startAngle, F(0.4));

            RmgenCommon.PlacePlayerBases(rng, map, settings, TPrimary, ClPlayer, null,
                playerPosition, TCityPlaza, TCity, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = OBerryBush,
                    Mines = new()
                    {
                        (OMetalLarge, (string?)null, (object?)null),
                        (OStoneLarge, (string?)null, (object?)null),
                    },
                    TreesTemplate = ODatePalm,
                    TreesCount = 2,
                    DecorativesTemplate = ABush1,
                });

            var riverTextures = new[]
            {
                new RiverTextureBand(F(0), F(0.04), TLush, clShore),
                new RiverTextureBand(F(0.04), F(0.06), TSLush, clShore),
                new RiverTextureBand(F(0.06), F(0.09), TSDry, clShore),
                new RiverTextureBand(F(0.25), F(0.5), null, clDesert),
            };

            int plantFrequency = 2;
            int plantID = 0;
            var riverStart = new RmgenVector2D(mapCenter.X, MapSize);
            riverStart.RotateAround(startAngle, mapCenter);
            var riverEnd = new RmgenVector2D(mapCenter.X, 0);
            riverEnd.RotateAround(startAngle, mapCenter);
            PortGMapHelpers.PaintRiver(rng, map, riverStart, riverEnd,
                F(0.1), S(3, 12), HeightSeaGround, HeightShore,
                parallel: true, deviation: 0.5, meanderShort: 12, meanderLong: 50,
                waterFunc: (position, height, _) =>
                {
                    clWater.Add(position);
                    map.SetTexture(position, TShore);

                    if (height <= -0.2 || height >= 0.1)
                        return;

                    if (plantID % plantFrequency == 0)
                    {
                        plantID = 0;
                        map.PlaceEntityAnywhere(aPlants, 0, position, rng.RandomAngle());
                    }
                    ++plantID;
                },
                landFunc: (position, shoreDist1, shoreDist2) =>
                {
                    foreach (var riverTexture in riverTextures)
                        if (riverTexture.Left < shoreDist1 && shoreDist1 < riverTexture.Right ||
                            riverTexture.Left < -shoreDist2 && -shoreDist2 < riverTexture.Right)
                        {
                            riverTexture.Marker.Add(position);
                            if (riverTexture.Terrain != null)
                                map.SetTexture(position, riverTexture.Terrain);
                        }
                });

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, S(20, 50), 0.3, 0.06, double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        HeightOffsetBump, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 6),
                S(100, 200));

            int numLakes = (int)SafeMath.Round(S(1, 4) * NumPlayers / 2);
            var waterAreas = RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, S(2, 5) * 50, 0.8, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new TerrainPainter(TShore, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        HeightPonds, 4),
                    new TileClassPainter(clPond),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 25, clWater, 20, clPond, 10),
                numLakes);

            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, AReeds, 1, 3, 0, 1),
                }, true),
                0, RmgenLibrary.StayClasses(clPond, 1), numLakes, 100, waterAreas);

            RmgenLibrary.CreateObjectGroupsByAreasDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, ALillies, 1, 3, 0, 1),
                }, true),
                0, RmgenLibrary.StayClasses(clPond, 1), numLakes, 100, waterAreas);

            double forestTrees = 0.5 * S(700, 3500);
            double stragglerTrees = (1 - 0.5) * S(700, 3500);
            double num = S(10, 30);
            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, forestTrees / num, 0.15, 0.1, 0.5),
                new IPainter[]
                {
                    new TerrainPainter(new object[] { pForest, TForestFloor }, rng),
                    new TileClassPainter(ClForest),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 19, ClForest, 4, clWater, 1, clDesert, 5,
                    clPond, 2, ClBaseResource, 3),
                num, 50);

            foreach (double size in new[] { S(3, 48), S(5, 84), S(8, 128) })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, size, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new object[] { TGrass, TGrassSand50 },
                            new object[] { TGrassSand50, TGrassSand25 },
                            new object[] { TGrassSand25, TGrass },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, clGrass, 5, ClPlayer, 10,
                        clWater, 1, ClDirt, 5, clShore, 1, clPond, 1),
                    S(15, 45));

            foreach (double size in new[] { S(3, 48), S(5, 84), S(8, 128) })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, size, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new object[] { TDirt, TDirtCracks },
                            new object[] { TDirt, TFineSand },
                            new object[] { TDirtCracks, TFineSand },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClDirt, 5, ClPlayer, 10,
                        clWater, 1, clGrass, 5, clShore, 1, clPond, 1),
                    S(15, 45));

            var group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, OStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                new ScatterObject(rng, OStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClRock, 10,
                    clWater, 1, clPond, 1),
                S(4, 16), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, OStoneSmall, 2, 5, 1, 3),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClRock, 10,
                    clWater, 1, clPond, 1),
                S(4, 16), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, OMetalLarge, 1, 1, 0, 4),
            }, true, ClMetal);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClMetal, 10, ClRock, 5,
                    clWater, 1, clPond, 1),
                S(4, 16), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, OStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                new ScatterObject(rng, OStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClRock, 10,
                        clWater, 1, clPond, 1),
                    RmgenLibrary.StayClasses(clDesert, 3),
                }),
                S(6, 20), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, OStoneSmall, 2, 5, 1, 3),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClRock, 10,
                        clWater, 1, clPond, 1),
                    RmgenLibrary.StayClasses(clDesert, 3),
                }),
                S(6, 20), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, OMetalLarge, 1, 1, 0, 4),
            }, true, ClMetal);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClMetal, 10, ClRock, 5,
                        clWater, 1, clPond, 1),
                    RmgenLibrary.StayClasses(clDesert, 3),
                }),
                S(6, 20), 100);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, ADecorativeRock, 1, 3, 0, 1),
            }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClForest, 0, ClPlayer, 0, clPond, 1),
                S(16, 262), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, ABush2, 1, 2, 0, 1),
                new ScatterObject(rng, ABush1, 1, 3, 0, 2),
                new ScatterObject(rng, ABush4, 1, 2, 0, 1),
                new ScatterObject(rng, ABush3, 1, 3, 0, 2),
            }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(clWater, 1, ClPlayer, 0, clPond, 1),
                S(20, 180), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, OGazelle, 5, 7, 0, 4),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, clWater, 1, clFood, 10,
                    clDesert, 5, clPond, 1),
                3 * S(5, 20), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, OGoat, 2, 4, 0, 3),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, clWater, 1, clFood, 10,
                    clDesert, 5, clPond, 1),
                3 * S(5, 20), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, OFoodTreasure, 1, 1, 0, 2),
            }, true, clTreasure);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, clWater, 1, clFood, 2,
                    clDesert, 5, clTreasure, 6, clPond, 1),
                3 * S(5, 20), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, OWoodTreasure, 1, 1, 0, 2),
            }, true, clTreasure);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, clWater, 1, clFood, 2,
                    clDesert, 5, clTreasure, 6, clPond, 1),
                3 * S(5, 20), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, OCamel, 2, 4, 0, 2),
            }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, clWater, 1, clFood, 10,
                    clDesert, 5, clTreasure, 2, clPond, 1),
                3 * S(5, 20), 50);

            GaiaEntities.CreateStragglerTrees(rng, new[] { ODatePalm, OSDatePalm },
                RmgenLibrary.AvoidClasses(ClForest, 0, clWater, 1, ClPlayer, 20, ClMetal, 6,
                    clDesert, 1, clTreasure, 2, clPond, 1),
                ClForest, stragglerTrees / 2);

            GaiaEntities.CreateStragglerTrees(rng, new[] { ODatePalm, OSDatePalm },
                RmgenLibrary.AvoidClasses(ClForest, 0, clWater, 1, ClPlayer, 20, ClMetal, 6,
                    clTreasure, 2),
                ClForest, stragglerTrees / 10);

            GaiaEntities.CreateStragglerTrees(rng, new[] { ODatePalm, OSDatePalm },
                RmgenLibrary.BorderClasses(clPond, 1, 4),
                ClForest, stragglerTrees);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, EObelisk, 1, 1, 0, 1),
            }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clWater, 4, ClForest, 3, ClPlayer, 20,
                        ClMetal, 6, ClRock, 6, clPond, 4, clTreasure, 2),
                    RmgenLibrary.StayClasses(clDesert, 3),
                }),
                S(5, 30), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, EPyramid, 1, 1, 0, 1),
            }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clWater, 7, ClForest, 6, ClPlayer, 20,
                        ClMetal, 5, ClRock, 5, clPond, 7, clTreasure, 2),
                    RmgenLibrary.StayClasses(clDesert, 3),
                }),
                S(2, 6), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, OFish, 1, 2, 0, 1),
                }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clWater, 4),
                    RmgenLibrary.AvoidClasses(clFood, 12),
                }),
                S(60, 80), 100);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, OFish, 1, 2, 0, 1),
                }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clPond, 3),
                    RmgenLibrary.AvoidClasses(clFood, 4),
                }),
                S(30, 30), 50);

            return map.MakeExportable();
        }

        private readonly record struct RiverTextureBand(double Left, double Right, string? Terrain,
            TileClass Marker);

        private double F(double fraction) => RmgenLibrary.FractionToTiles(fraction, MapSize);

        private double S(double min, double max) => RmgenLibrary.ScaleByMapSize(min, max, MapSize);
    }

    internal static class PortGMapHelpers
    {
        public static PathPlacer Path(RmgenRng rng, RmgenVector2D start, RmgenVector2D end,
            double width, double waviness, double smoothness, double offset, double tapering,
            double failFraction = 0)
            => new(rng, waviness, smoothness, offset, tapering, failFraction)
            {
                Start = start,
                End = end,
                Width = width,
            };

        public static void PaintRiver(RmgenRng rng, RandomMap map,
            RmgenVector2D start, RmgenVector2D end, double width, double fadeDist,
            double heightRiverbed, double heightLandValue,
            bool parallel, double deviation, double meanderShort, double meanderLong,
            Action<RmgenVector2D, double, double>? waterFunc = null,
            Action<RmgenVector2D, double, double>? landFunc = null)
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
                    double distanceToRiver =
                        RmgenGeometry.DistanceOfPointFromLine(start, end, vecPoint);
                    var river = RmgenVector2D.Sub(vecPoint,
                        RmgenVector2D.Mult(unitVecPerpendicular, distanceToRiver));

                    if (river.X < riverMinX || river.X > riverMaxX ||
                        river.Y < riverMinZ || river.Y > riverMaxZ)
                        continue;

                    double riverFraction = river.DistanceTo(start) / riverLength;
                    double riverCurve1 = RiverCurve(riverFraction, startingAngle1, seed1);
                    double riverCurve2 = parallel ? riverCurve1 :
                        RiverCurve(riverFraction, startingAngle2, seed2);
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
                retVal *= 22 * (rndRr - 0.2) * (rndRr - 0.3) *
                    (rndRr - 0.3) * (rndRr - 0.8);
            else if (rndRe == 3)
                retVal *= 180 * (rndRr - 0.2) * (rndRr - 0.2) * (rndRr - 0.4) *
                    (rndRr - 0.6) * (rndRr - 0.6) * (rndRr - 0.8);
            else if (rndRe == 4)
                retVal *= 2.6 * (rndRr - 0.5) * (rndRr - 0.7);

            return retVal;
        }
    }
}
