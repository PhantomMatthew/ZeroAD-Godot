using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>lions_den.js(584 行,逐字移植)——狮穴:中央高丘(50)+ 沉降中央谷地(0),
    /// 每玩家一个"穴"(den)+ 与相邻玩家/扩张点的路径(PathPlacer 挖低),
    /// 玩家间独立扩张点;rmgen2 声明式管线铺资源(区分 valley/settlement/step 三区)。</summary>
    public sealed class LionsDenMap : StandardMap
    {
        // topTerrain(tier2Terrain)是基准贴图,不是走 biome.MainTerrain——覆盖 CreateMap。
        protected override double HeightLand => 50;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.Tier2Terrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            int mapSize = MapSize;
            var mapCenter = map.GetCenter();
            int numPlayers = NumPlayers;
            double startAngle = rng.RandomAngle();

            object topTerrain = biome.Tier2Terrain;
            const double heightValley = 0, heightPath = 10, heightDen = 15, heightHill = 50;

            var tc = new TileClassSet(mapSize, new[] { "step" });
            var gaia = new Rmgen2Gaia(rng, map, biome, BiomeName, tc, settings);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(), new TileClassPainter(tc["land"]), null);

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(rng, map, settings,
                "circle", RmgenLibrary.FractionToTiles(0.4, mapSize), startAngle,
                groupedDistance: RmgenLibrary.FractionToTiles(rng.RandFloat(0.05, 0.1), mapSize));
            Rmgen2Setup.CreateBases(rng, map, settings, tc, biome, BiomeName, playerIDs, playerPosition);

            // ── createSunkenTerrain ──
            object middle = biome.Dirt, lower = biome.Tier2Terrain;
            object road = biome.Road;
            if (BiomeName == "generic/arctic") { middle = biome.Tier2Terrain; lower = biome.Tier1Terrain; }
            if (BiomeName == "generic/alpine") { middle = biome.Shore; lower = biome.Tier4Terrain; }
            if (BiomeName == "generic/aegean") { middle = biome.Tier1Terrain; lower = biome.ForestFloor1; }
            if (BiomeName == "generic/savanna") { middle = biome.Tier2Terrain; lower = biome.Tier4Terrain; }
            if (BiomeName == "generic/india" || BiomeName == "generic/autumn") road = biome.RoadWild;
            if (BiomeName == "generic/autumn") middle = biome.Shore;

            double expSize = RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.14, mapSize)) / numPlayers;
            double expDist = 0.1 + numPlayers / 200.0;
            double expAngle = 0.75;
            if (numPlayers <= 2)
            {
                expSize = RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.075, mapSize));
                expAngle = 0.72;
            }

            double nRoad = 0.44, nExp = 0.425;
            if (numPlayers < 4) { nRoad = 0.42; nExp = 0.4; }

            RmgenLibrary.CreateArea(
                new DiskPlacer(RmgenLibrary.FractionToTiles(0.29, mapSize), mapCenter),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, lower }, new[] { 3.0 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightValley, 3),
                    new TileClassPainter(tc["valley"]),
                }, null);

            RmgenLibrary.CreateArea(
                new DiskPlacer(RmgenLibrary.FractionToTiles(0.21, mapSize), mapCenter),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, topTerrain }, new[] { 3.0 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightHill, 3),
                    new TileClassPainter(tc["mountain"]),
                }, null);

            RmgenVector2D GetCoords(double distance, int playerIdx, double playerIdOffset)
            {
                double angle = startAngle + (playerIdx + playerIdOffset) * 2 * Math.PI / numPlayers;
                var v = new RmgenVector2D(RmgenLibrary.FractionToTiles(distance, mapSize), 0);
                v.Rotate(-angle);
                var p = RmgenVector2D.Add(mapCenter, v);
                p.Round();
                return p;
            }

            for (int i = 0; i < numPlayers; ++i)
            {
                var playerPos = GetCoords(0.4, i, 0);

                var expansionPosition = GetCoords(expDist, i, expAngle);
                RmgenLibrary.CreateArea(
                    new PathPlacer(rng, 0.7, 0.5, 0.1, -1) { Start = playerPos, End = expansionPosition, Width = 12 },
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { biome.Cliff, middle, road }, new[] { 3.0, 4.0 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightPath, 3),
                        new TileClassPainter(tc["step"]),
                    }, null);

                foreach (double neighborOffset in new[] { -0.5, 0.5 })
                {
                    var neighborPosition = GetCoords(nRoad, i, neighborOffset);
                    var pathPosition = GetCoords(0.47, i, 0);
                    RmgenLibrary.CreateArea(
                        new PathPlacer(rng, 0.4, 0.5, 0.1, -0.6)
                        { Start = pathPosition, End = neighborPosition, Width = 19 },
                        new IPainter[]
                        {
                            new LayeredPainter(new object[] { biome.Cliff, middle, road }, new[] { 3.0, 6.0 }, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightPath, 3),
                            new TileClassPainter(tc["step"]),
                        }, null);
                }

                // Den
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng,
                        RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.1, mapSize)) / (settings.Nomad ? 2 : 1),
                        0.9, 0.3, double.PositiveInfinity, playerPos),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { biome.Cliff, (object)biome.MainTerrain }, new[] { 3.0 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightDen, 3),
                        new TileClassPainter(tc["valley"]),
                    }, null);

                // Expansion
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, expSize, 0.9, 0.3, double.PositiveInfinity, expansionPosition),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { biome.Cliff, (object)biome.MainTerrain }, new[] { 3.0 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightDen, 3),
                        new TileClassPainter(tc["settlement"]),
                    },
                    RmgenLibrary.AvoidClasses(tc["settlement"], 2));
            }

            for (int i = 0; i < numPlayers; ++i)
            {
                var position = GetCoords(nExp, i, 0.5);
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, expSize, 0.9, 0.3, double.PositiveInfinity, position),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { biome.Cliff, lower }, new[] { 3.0 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightValley, 3),
                        new TileClassPainter(tc["settlement"]),
                    }, null);
            }

            // ── addElements(区分 valley/settlement/player/step/mountain 五区)──
            Rmgen2Setup.AddElements(rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddLayeredPatches,
                    Avoid = new object[]
                    {
                        tc["baseResource"], 5, tc["dirt"], 5, tc["forest"], 2, tc["mountain"], 2,
                        tc["player"], 12, tc["step"], 5,
                    },
                    Stay = new object[] { tc["valley"], 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddLayeredPatches,
                    Avoid = new object[] { tc["baseResource"], 5, tc["dirt"], 5, tc["forest"], 2, tc["player"], 12 },
                    Stay = new object[] { tc["settlement"], 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddLayeredPatches,
                    Avoid = new object[] { tc["baseResource"], 5, tc["dirt"], 5, tc["forest"], 2 },
                    Stay = new object[] { tc["player"], 1 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddDecoration,
                    Avoid = new object[] { tc["baseResource"], 5, tc["forest"], 2 },
                    Stay = new object[] { tc["player"], 1 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddDecoration,
                    Avoid = new object[]
                    {
                        tc["baseResource"], 5, tc["forest"], 2, tc["mountain"], 2, tc["player"], 12, tc["step"], 2,
                    },
                    Stay = new object[] { tc["valley"], 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddDecoration,
                    Avoid = new object[] { tc["baseResource"], 5, tc["forest"], 2, tc["mountain"], 2, tc["player"], 12 },
                    Stay = new object[] { tc["settlement"], 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddDecoration,
                    Avoid = new object[] { tc["baseResource"], 5, tc["forest"], 2, tc["mountain"], 2, tc["player"], 12 },
                    Stay = new object[] { tc["step"], 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "scarce" },
                },
            });

            Rmgen2Setup.AddElements(rng, Rmgen2Setup.Shuffle(rng, new List<Rmgen2Setup.GaiaElement>
            {
                new()
                {
                    Func = gaia.AddMetal,
                    Avoid = new object[]
                    {
                        tc["baseResource"], 5, tc["berries"], 5, tc["forest"], 3, tc["player"], 30,
                        tc["rock"], 10, tc["metal"], 20,
                    },
                    Stay = new object[] { tc["settlement"], 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                new()
                {
                    Func = gaia.AddMetal,
                    Avoid = new object[]
                    {
                        tc["baseResource"], 5, tc["berries"], 5, tc["forest"], 3, tc["player"], 10,
                        tc["rock"], 10, tc["metal"], 20, tc["mountain"], 5, tc["step"], 5,
                    },
                    Stay = new object[] { tc["valley"], 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddStone,
                    Avoid = new object[]
                    {
                        tc["baseResource"], 5, tc["berries"], 5, tc["forest"], 3, tc["player"], 30,
                        tc["rock"], 20, tc["metal"], 10,
                    },
                    Stay = new object[] { tc["settlement"], 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                new()
                {
                    Func = gaia.AddStone,
                    Avoid = new object[]
                    {
                        tc["baseResource"], 5, tc["berries"], 5, tc["forest"], 3, tc["player"], 10,
                        tc["rock"], 20, tc["metal"], 10, tc["mountain"], 5, tc["step"], 5,
                    },
                    Stay = new object[] { tc["valley"], 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddForests,
                    Avoid = new object[]
                    {
                        tc["baseResource"], 5, tc["berries"], 5, tc["forest"], 18, tc["metal"], 3,
                        tc["player"], 20, tc["rock"], 3,
                    },
                    Stay = new object[] { tc["settlement"], 7 },
                    Sizes = new[] { "normal", "big" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                new()
                {
                    Func = gaia.AddForests,
                    Avoid = new object[]
                    {
                        tc["baseResource"], 5, tc["berries"], 3, tc["forest"], 18, tc["metal"], 3,
                        tc["mountain"], 5, tc["player"], 5, tc["rock"], 3, tc["step"], 1,
                    },
                    Stay = new object[] { tc["valley"], 7 },
                    Sizes = new[] { "normal", "big" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
            }));

            Rmgen2Setup.AddElements(rng, Rmgen2Setup.Shuffle(rng, new List<Rmgen2Setup.GaiaElement>
            {
                new()
                {
                    Func = gaia.AddBerries,
                    Avoid = new object[]
                    {
                        tc["baseResource"], 5, tc["berries"], 30, tc["forest"], 5, tc["metal"], 10,
                        tc["player"], 20, tc["rock"], 10,
                    },
                    Stay = new object[] { tc["settlement"], 7 },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = new[] { "tons" },
                },
                new()
                {
                    Func = gaia.AddBerries,
                    Avoid = new object[]
                    {
                        tc["baseResource"], 5, tc["berries"], 30, tc["forest"], 5, tc["metal"], 10,
                        tc["mountain"], 5, tc["player"], 10, tc["rock"], 10, tc["step"], 5,
                    },
                    Stay = new object[] { tc["valley"], 7 },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddAnimals,
                    Avoid = new object[]
                    {
                        tc["animals"], 20, tc["baseResource"], 5, tc["forest"], 0, tc["metal"], 1,
                        tc["player"], 20, tc["rock"], 1,
                    },
                    Stay = new object[] { tc["settlement"], 7 },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = new[] { "tons" },
                },
                new()
                {
                    Func = gaia.AddAnimals,
                    Avoid = new object[]
                    {
                        tc["animals"], 20, tc["baseResource"], 5, tc["forest"], 0, tc["metal"], 1,
                        tc["mountain"], 5, tc["player"], 10, tc["rock"], 1, tc["step"], 5,
                    },
                    Stay = new object[] { tc["valley"], 7 },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddStragglerTrees,
                    Avoid = new object[]
                    {
                        tc["baseResource"], 5, tc["berries"], 5, tc["forest"], 7, tc["metal"], 3,
                        tc["player"], 12, tc["rock"], 3,
                    },
                    Stay = new object[] { tc["settlement"], 7 },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = new[] { "tons" },
                },
                new()
                {
                    Func = gaia.AddStragglerTrees,
                    Avoid = new object[]
                    {
                        tc["baseResource"], 5, tc["berries"], 5, tc["forest"], 7, tc["metal"], 3,
                        tc["mountain"], 5, tc["player"], 10, tc["rock"], 3, tc["step"], 5,
                    },
                    Stay = new object[] { tc["valley"], 7 },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = new[] { "normal", "many", "tons" },
                },
                new()
                {
                    Func = gaia.AddStragglerTrees,
                    Avoid = new object[]
                    {
                        tc["player"], 10, tc["baseResource"], 5, tc["berries"], 5, tc["forest"], 3,
                        tc["metal"], 5, tc["rock"], 5,
                    },
                    Stay = new object[] { tc["player"], 1 },
                    Sizes = new[] { "huge" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
            }));

            Rmgen2Setup.AddElements(rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddDecoration,
                    Avoid = new object[]
                    {
                        tc["baseResource"], 5, tc["valley"], 4, tc["player"], 4, tc["settlement"], 4, tc["step"], 4,
                    },
                    Stay = new object[] { tc["land"], 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "tons" },
                },
            });
            Rmgen2Setup.AddElements(rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddProps,
                    Avoid = new object[]
                    {
                        tc["baseResource"], 5, tc["valley"], 4, tc["player"], 4, tc["settlement"], 4, tc["step"], 4,
                    },
                    Stay = new object[] { tc["land"], 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "scarce" },
                },
            });
            Rmgen2Setup.AddElements(rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddDecoration,
                    Avoid = new object[] { tc["baseResource"], 5, tc["player"], 4, tc["settlement"], 4, tc["step"], 4 },
                    Stay = new object[] { tc["mountain"], 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "tons" },
                },
            });
            Rmgen2Setup.AddElements(rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddProps,
                    Avoid = new object[] { tc["baseResource"], 5, tc["player"], 4, tc["settlement"], 4, tc["step"], 4 },
                    Stay = new object[] { tc["mountain"], 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "scarce" },
                },
            });

            return map.MakeExportable();
        }
    }
}
