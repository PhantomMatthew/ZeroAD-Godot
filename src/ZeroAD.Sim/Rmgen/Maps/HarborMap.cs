using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>harbor.js(429 行,逐字移植)——港湾:中央湖 + (大图)四个玩家旁小海港 +
    /// 放射状山脊(spine,PathPlacer 蜿蜒),rmgen2 声明式管线铺资源。</summary>
    public sealed class HarborMap : StandardMap
    {
        protected override double HeightLand => 2;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            int mapSize = MapSize;
            var mapCenter = map.GetCenter();
            const double heightSeaGround = -18;
            const double heightOffsetHarbor = -11;

            var tc = new TileClassSet(mapSize);
            var gaia = new Rmgen2Gaia(rng, map, biome, BiomeName, tc, settings);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(), new TileClassPainter(tc["land"]), null);

            double startAngle = rng.RandomAngle();
            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(rng, map, settings,
                "circle", RmgenLibrary.FractionToTiles(0.38, mapSize), startAngle);
            Rmgen2Setup.CreateBases(rng, map, settings, tc, biome, BiomeName, playerIDs, playerPosition);

            // ── 中央湖 ──
            RmgenLibrary.CreateArea(
                new ChainPlacer(rng, 2, Math.Floor(RmgenLibrary.ScaleByMapSize(2, 12, mapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(35, 160, mapSize)), double.PositiveInfinity,
                    mapCenter, 0, new[] { (int)Math.Floor(RmgenLibrary.FractionToTiles(0.2, mapSize)) }),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Shore, biome.Water }, new[] { 1.0 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightSeaGround, 10),
                    new TileClassPainter(tc["water"]),
                },
                RmgenLibrary.AvoidClasses(tc["player"], 20));

            // ── 港湾(大图专属)──
            if (mapSize >= 192)
                foreach (var position in playerPosition)
                {
                    var off = RmgenVector2D.Sub(mapCenter, position);
                    off = RmgenVector2D.Div(off, 2.5);
                    off.Round();
                    var harborPosition = RmgenVector2D.Add(position, off);
                    RmgenLibrary.CreateArea(
                        new ClumpPlacer(rng, 1200, 0.5, 0.5, double.PositiveInfinity, harborPosition),
                        new IPainter[]
                        {
                            new LayeredPainter(new object[] { biome.Shore, biome.Water }, new[] { 2.0 }, rng),
                            new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                                heightOffsetHarbor, 3, relative: true),
                            new TileClassPainter(tc["water"]),
                        },
                        RmgenLibrary.AvoidClasses(tc["player"], 15, tc["hill"], 1));
                }

            // ── 放射状山脊(spine)──
            bool smallSpines = mapSize <= 192;
            double spineSize = smallSpines ? 0.02 : 0.5;
            double spineTapering = smallSpines ? -0.1 : -1.4;
            double heightOffsetSpine = smallSpines ? 20 : 35;

            object spineTile = biome.Dirt;
            if (BiomeName == "generic/arctic") spineTile = biome.Tier1Terrain;
            if (BiomeName == "generic/alpine" || BiomeName == "generic/savanna") spineTile = biome.Tier2Terrain;
            if (BiomeName == "generic/autumn") spineTile = biome.Tier4Terrain;

            int split = 1;
            if (NumPlayers <= 3 || (mapSize >= 320 && NumPlayers <= 4)) split = 2;

            for (int i = 0; i < NumPlayers * split; ++i)
            {
                double tang = startAngle + (i + 0.5) * 2 * Math.PI / (NumPlayers * split);
                var v1 = new RmgenVector2D(RmgenLibrary.FractionToTiles(0.12, mapSize), 0);
                v1.Rotate(-tang);
                var start = RmgenVector2D.Add(mapCenter, v1);
                var v2 = new RmgenVector2D(RmgenLibrary.FractionToTiles(0.4, mapSize), 0);
                v2.Rotate(-tang);
                var end = RmgenVector2D.Add(mapCenter, v2);

                RmgenLibrary.CreateArea(
                    new PathPlacer(rng, 0.6, 0.1, 0.4, spineTapering)
                    { Start = start, End = end, Width = RmgenLibrary.ScaleByMapSize(14, spineSize, mapSize) },
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { biome.Cliff, spineTile }, new[] { 3.0 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                            heightOffsetSpine, 3, relative: true),
                        new TileClassPainter(tc["spine"]),
                    },
                    RmgenLibrary.AvoidClasses(tc["player"], 5));
            }

            Rmgen2Setup.AddElements(rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddDecoration,
                    Avoid = new object[]
                    {
                        tc["bluff"], 2, tc["forest"], 2, tc["mountain"], 2, tc["player"], 12, tc["water"], 3,
                    },
                    Stay = new object[] { tc["spine"], 5 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
            });
            Rmgen2Setup.AddElements(rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddProps,
                    Avoid = new object[] { tc["forest"], 2, tc["player"], 2, tc["prop"], 20, tc["water"], 3 },
                    Stay = new object[] { tc["spine"], 8 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "scarce" },
                },
            });

            Rmgen2Setup.AddElements(rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddFish,
                    Avoid = new object[]
                    {
                        tc["fish"], 12, tc["hill"], 8, tc["mountain"], 8, tc["player"], 8, tc["spine"], 4,
                    },
                    Stay = new object[] { tc["water"], 7 },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = new[] { "tons" },
                },
            });

            Rmgen2Setup.AddElements(rng, Rmgen2Setup.Shuffle(rng, new List<Rmgen2Setup.GaiaElement>
            {
                new()
                {
                    Func = gaia.AddHills,
                    Avoid = new object[]
                    {
                        tc["bluff"], 5, tc["hill"], 15, tc["mountain"], 2, tc["plateau"], 5,
                        tc["player"], 20, tc["spine"], 5, tc["valley"], 2, tc["water"], 2,
                    },
                    Sizes = new[] { "tiny", "small" }, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddMountains,
                    Avoid = new object[]
                    {
                        tc["bluff"], 20, tc["mountain"], 25, tc["plateau"], 20, tc["player"], 20,
                        tc["spine"], 20, tc["valley"], 10, tc["water"], 15,
                    },
                    Sizes = new[] { "small" }, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddPlateaus,
                    Avoid = new object[]
                    {
                        tc["bluff"], 20, tc["mountain"], 25, tc["plateau"], 20, tc["player"], 40,
                        tc["spine"], 20, tc["valley"], 10, tc["water"], 15,
                    },
                    Sizes = new[] { "small" }, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddBluffs,
                    BaseHeight = HeightLand,
                    Avoid = new object[]
                    {
                        tc["bluff"], 20, tc["mountain"], 25, tc["plateau"], 20, tc["player"], 40,
                        tc["spine"], 20, tc["valley"], 10, tc["water"], 15,
                    },
                    Sizes = new[] { "normal" }, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
            }));

            Rmgen2Setup.AddElements(rng, Rmgen2Setup.Shuffle(rng, new List<Rmgen2Setup.GaiaElement>
            {
                new()
                {
                    Func = gaia.AddMetal,
                    Avoid = new object[]
                    {
                        tc["berries"], 5, tc["bluff"], 5, tc["forest"], 3, tc["mountain"], 2,
                        tc["plateau"], 2, tc["player"], 30, tc["rock"], 10, tc["spine"], 5,
                        tc["metal"], 20, tc["water"], 3,
                    },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "normal", "many" },
                },
                new()
                {
                    Func = gaia.AddStone,
                    Avoid = new object[]
                    {
                        tc["berries"], 5, tc["bluff"], 5, tc["forest"], 3, tc["mountain"], 2,
                        tc["plateau"], 2, tc["player"], 30, tc["rock"], 20, tc["spine"], 5,
                        tc["metal"], 10, tc["water"], 3,
                    },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "normal", "many" },
                },
                new()
                {
                    Func = gaia.AddForests,
                    Avoid = new object[]
                    {
                        tc["berries"], 5, tc["bluff"], 5, tc["forest"], 8, tc["metal"], 3,
                        tc["mountain"], 5, tc["plateau"], 5, tc["player"], 20, tc["rock"], 3,
                        tc["spine"], 5, tc["water"], 2,
                    },
                    Sizes = new[] { "normal" }, Mixes = new[] { "similar" }, Amounts = new[] { "many" },
                },
            }));

            Rmgen2Setup.AddElements(rng, Rmgen2Setup.Shuffle(rng, new List<Rmgen2Setup.GaiaElement>
            {
                new()
                {
                    Func = gaia.AddBerries,
                    Avoid = new object[]
                    {
                        tc["berries"], 30, tc["bluff"], 5, tc["forest"], 5, tc["metal"], 10,
                        tc["mountain"], 2, tc["plateau"], 2, tc["player"], 20, tc["rock"], 10,
                        tc["spine"], 2, tc["water"], 3,
                    },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddAnimals,
                    Avoid = new object[]
                    {
                        tc["animals"], 20, tc["bluff"], 5, tc["forest"], 2, tc["metal"], 2,
                        tc["mountain"], 1, tc["plateau"], 2, tc["player"], 20, tc["rock"], 2,
                        tc["spine"], 2, tc["water"], 3,
                    },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddStragglerTrees,
                    Avoid = new object[]
                    {
                        tc["berries"], 5, tc["bluff"], 5, tc["forest"], 7, tc["metal"], 2,
                        tc["mountain"], 1, tc["plateau"], 2, tc["player"], 12, tc["rock"], 2,
                        tc["spine"], 2, tc["water"], 5,
                    },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
            }));

            Rmgen2Setup.AddElements(rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddLayeredPatches,
                    Avoid = new object[]
                    {
                        tc["bluff"], 2, tc["dirt"], 5, tc["forest"], 2, tc["mountain"], 2,
                        tc["plateau"], 2, tc["player"], 12, tc["spine"], 5, tc["water"], 3,
                    },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddDecoration,
                    Avoid = new object[]
                    {
                        tc["bluff"], 2, tc["forest"], 2, tc["mountain"], 2, tc["plateau"], 2,
                        tc["player"], 12, tc["spine"], 5, tc["water"], 3,
                    },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
            });

            return map.MakeExportable();
        }
    }
}
