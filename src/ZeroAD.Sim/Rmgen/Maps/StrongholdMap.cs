using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>stronghold.js(282 行,逐字移植)——堡垒:"stronghold" 布置(队伍聚成堡垒圆环),
    /// 全套地形要素(bluffs/hills/mountains/plateaus/valleys)乱序下发。</summary>
    public sealed class StrongholdMap : StandardMap
    {
        protected override double HeightLand => 30;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            int mapSize = MapSize;
            double heightLand = HeightLand;

            var tc = new TileClassSet(mapSize);
            var gaia = new Rmgen2Gaia(rng, map, biome, BiomeName, tc, settings);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(), new TileClassPainter(tc["land"]), null);

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(rng, map, settings,
                "stronghold", RmgenLibrary.FractionToTiles(rng.RandFloat(0.2, 0.35), mapSize),
                rng.RandomAngle(), groupedDistance: RmgenLibrary.FractionToTiles(rng.RandFloat(0.05, 0.1), mapSize));
            Rmgen2Setup.CreateBases(rng, map, settings, tc, biome, BiomeName, playerIDs, playerPosition);
            gaia.MarkPlayerAvoidanceArea(playerPosition, RmgenCommon.DefaultPlayerBaseRadius(mapSize));

            Rmgen2Setup.AddElements(rng, Rmgen2Setup.Shuffle(rng, new List<Rmgen2Setup.GaiaElement>
            {
                new()
                {
                    Func = gaia.AddBluffs,
                    BaseHeight = heightLand,
                    Avoid = new object[]
                    {
                        tc["bluff"], 20, tc["hill"], 5, tc["mountain"], 20, tc["plateau"], 20,
                        tc["player"], 30, tc["valley"], 5, tc["water"], 7,
                    },
                    Sizes = new[] { "big", "huge" }, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddHills,
                    Avoid = new object[]
                    {
                        tc["bluff"], 5, tc["hill"], 15, tc["mountain"], 2, tc["plateau"], 2,
                        tc["player"], 20, tc["valley"], 2, tc["water"], 2,
                    },
                    Sizes = new[] { "normal", "big" }, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddMountains,
                    Avoid = new object[]
                    {
                        tc["bluff"], 20, tc["mountain"], 25, tc["plateau"], 20, tc["player"], 20,
                        tc["valley"], 10, tc["water"], 15,
                    },
                    Sizes = new[] { "big", "huge" }, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddPlateaus,
                    Avoid = new object[]
                    {
                        tc["bluff"], 20, tc["mountain"], 25, tc["plateau"], 25, tc["player"], 40,
                        tc["valley"], 10, tc["water"], 15,
                    },
                    Sizes = new[] { "big", "huge" }, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddValleys,
                    BaseHeight = heightLand,
                    Avoid = new object[]
                    {
                        tc["bluff"], 5, tc["hill"], 5, tc["mountain"], 25, tc["plateau"], 10,
                        tc["player"], 40, tc["valley"], 15, tc["water"], 10,
                    },
                    Sizes = new[] { "normal", "big" }, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
            }));

            if (!settings.Nomad)
                gaia.CreateBluffsPassages(playerPosition);

            Rmgen2Setup.AddElements(rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddLayeredPatches,
                    Avoid = new object[]
                    {
                        tc["bluff"], 2, tc["dirt"], 5, tc["forest"], 2, tc["mountain"], 2,
                        tc["plateau"], 2, tc["player"], 12, tc["valley"], 5, tc["water"], 3,
                    },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddDecoration,
                    Avoid = new object[]
                    {
                        tc["bluff"], 2, tc["forest"], 2, tc["mountain"], 2, tc["plateau"], 2,
                        tc["player"], 12, tc["water"], 3,
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
                        tc["plateau"], 2, tc["player"], 30, tc["rock"], 10, tc["metal"], 20, tc["water"], 3,
                    },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddStone,
                    Avoid = new object[]
                    {
                        tc["berries"], 5, tc["bluff"], 5, tc["forest"], 3, tc["mountain"], 2,
                        tc["plateau"], 2, tc["player"], 30, tc["rock"], 20, tc["metal"], 10, tc["water"], 3,
                    },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddForests,
                    Avoid = new object[]
                    {
                        tc["berries"], 5, tc["bluff"], 5, tc["forest"], 18, tc["metal"], 3,
                        tc["mountain"], 5, tc["plateau"], 5, tc["player"], 20, tc["rock"], 3, tc["water"], 2,
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
