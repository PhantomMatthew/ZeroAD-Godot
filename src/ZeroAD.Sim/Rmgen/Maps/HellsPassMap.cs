using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>hells_pass.js(350 行,逐字移植)——地狱通道:"groupedLines" 布置(回退 circle)+
    /// 队伍数条放射状峭壁屏障(PathPlacer),rmgen2 声明式管线铺地形/资源。</summary>
    public sealed class HellsPassMap : StandardMap
    {
        protected override double HeightLand => 1;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            int mapSize = MapSize;
            var mapCenter = map.GetCenter();
            const double heightBarrier = 30;

            var tc = new TileClassSet(mapSize);
            var gaia = new Rmgen2Gaia(rng, map, biome, BiomeName, tc, settings);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(), new TileClassPainter(tc["land"]), null);

            var teamsArray = RmgenCommon.GetTeamsArray(settings);
            double startAngle = rng.RandomAngle();
            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(rng, map, settings,
                "groupedLines", RmgenLibrary.FractionToTiles(0.2, mapSize), startAngle,
                groupedDistance: RmgenLibrary.FractionToTiles(0.08, mapSize));
            Rmgen2Setup.CreateBases(rng, map, settings, tc, biome, BiomeName, playerIDs, playerPosition);

            // ── 放射状屏障(placeBarriers)──
            object spineTerrain = biome.Dirt;
            if (BiomeName == "generic/arctic") spineTerrain = biome.Tier1Terrain;
            if (BiomeName == "generic/alpine" || BiomeName == "generic/savanna") spineTerrain = biome.Tier2Terrain;
            if (BiomeName == "generic/autumn") spineTerrain = biome.Tier4Terrain;

            int spineCount = settings.Nomad ? rng.RandIntInclusive(1, 4) : teamsArray.Count;
            for (int i = 0; i < spineCount; ++i)
            {
                double mSize = 8, mWaviness = 0.6, mOffset = 0.5, mTaper = -1.5;
                if (spineCount > 3 || mapSize <= 192) { mWaviness = 0.2; mOffset = 0.2; mTaper = -1; }
                if (spineCount >= 5) { mSize = 4; mWaviness = 0.2; mOffset = 0.2; mTaper = -0.7; }

                double angle = startAngle + (i + 0.5) * 2 * Math.PI / spineCount;
                var v1 = new RmgenVector2D(RmgenLibrary.FractionToTiles(0.075, mapSize), 0);
                v1.Rotate(-angle);
                var start = RmgenVector2D.Add(mapCenter, v1);
                var v2 = new RmgenVector2D(RmgenLibrary.FractionToTiles(0.42, mapSize), 0);
                v2.Rotate(-angle);
                var end = RmgenVector2D.Add(mapCenter, v2);

                RmgenLibrary.CreateArea(
                    new PathPlacer(rng, mWaviness, 0.1, mOffset, mTaper)
                    { Start = start, End = end, Width = RmgenLibrary.ScaleByMapSize(14, mSize, mapSize) },
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { biome.Cliff, spineTerrain }, new[] { 2.0 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, heightBarrier, 2),
                        new TileClassPainter(tc["spine"]),
                    },
                    RmgenLibrary.AvoidClasses(tc["player"], 5, tc["baseResource"], 5));
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
                    Sizes = new[] { "huge" }, Mixes = new[] { "unique" }, Amounts = new[] { "tons" },
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

            Rmgen2Setup.AddElements(rng, Rmgen2Setup.Shuffle(rng, new List<Rmgen2Setup.GaiaElement>
            {
                new()
                {
                    Func = gaia.AddBluffs,
                    BaseHeight = HeightLand,
                    Avoid = new object[]
                    {
                        tc["bluff"], 20, tc["hill"], 5, tc["mountain"], 20, tc["plateau"], 20,
                        tc["player"], 30, tc["spine"], 15, tc["valley"], 5, tc["water"], 7,
                    },
                    Sizes = new[] { "normal", "big" }, Mixes = new[] { "varied" }, Amounts = new[] { "few" },
                },
                new()
                {
                    Func = gaia.AddHills,
                    Avoid = new object[]
                    {
                        tc["bluff"], 5, tc["hill"], 15, tc["mountain"], 2, tc["plateau"], 5,
                        tc["player"], 20, tc["spine"], 15, tc["valley"], 2, tc["water"], 2,
                    },
                    Sizes = new[] { "normal", "big" }, Mixes = new[] { "varied" }, Amounts = new[] { "few" },
                },
                new()
                {
                    Func = gaia.AddLakes,
                    Avoid = new object[]
                    {
                        tc["bluff"], 7, tc["hill"], 2, tc["mountain"], 15, tc["plateau"], 10,
                        tc["player"], 20, tc["spine"], 15, tc["valley"], 10, tc["water"], 25,
                    },
                    Sizes = new[] { "big", "huge" }, Mixes = new[] { "varied", "unique" }, Amounts = new[] { "few" },
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

            Rmgen2Setup.AddElements(rng, Rmgen2Setup.Shuffle(rng, new List<Rmgen2Setup.GaiaElement>
            {
                new()
                {
                    Func = gaia.AddMetal,
                    Avoid = new object[]
                    {
                        tc["berries"], 5, tc["bluff"], 5, tc["forest"], 3, tc["mountain"], 2,
                        tc["plateau"], 2, tc["player"], 30, tc["rock"], 10, tc["metal"], 20,
                        tc["spine"], 5, tc["water"], 3,
                    },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddStone,
                    Avoid = new object[]
                    {
                        tc["berries"], 5, tc["bluff"], 5, tc["forest"], 3, tc["mountain"], 2,
                        tc["plateau"], 2, tc["player"], 30, tc["rock"], 20, tc["metal"], 10,
                        tc["spine"], 5, tc["water"], 3,
                    },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddForests,
                    Avoid = new object[]
                    {
                        tc["berries"], 5, tc["bluff"], 5, tc["forest"], 18, tc["metal"], 3,
                        tc["mountain"], 5, tc["plateau"], 5, tc["player"], 20, tc["rock"], 3,
                        tc["spine"], 5, tc["water"], 2,
                    },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes,
                    Amounts = new[] { "few", "normal", "many", "tons" },
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

            return map.MakeExportable();
        }
    }
}
