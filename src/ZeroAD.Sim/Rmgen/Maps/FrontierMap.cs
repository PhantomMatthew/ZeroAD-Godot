using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>frontier.js(299 行,逐字移植)——边境:随机基准海拔(偏低)决定地形要素菜单
    /// (低海拔加湖泊、高海拔加河谷),其余走 rmgen2 声明式管线。</summary>
    public sealed class FrontierMap : StandardMap
    {
        // HeightLand 由 randElevation 动态决定,Generate 内直接构造 RandomMap,不用基类默认值。
        protected override double HeightLand => 3;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            Rng = rng;
            Settings = settings;
            MapSize = settings.Size;
            NumPlayers = RmgenCommon.GetNumPlayers(settings);

            if (settings.BiomeData != null) { Biome = settings.BiomeData; BiomeName = ""; }
            else
            {
                string picked = rng.PickRandom(SupportedBiomes);
                BiomeName = picked.Contains('/') ? picked : "generic/" + picked;
                Biome = BiomeLoader.Load(settings.DataRoot, picked, rng);
            }
            var biome = Biome;

            // 随机基准海拔(偏低倾向):randIntInclusive(0,29),≥25 时再抽 randIntInclusive(1,4)。
            double randElevation = rng.RandIntInclusive(0, 29);
            if (randElevation < 25)
                randElevation = rng.RandIntInclusive(1, 4);

            var map = new RandomMap(rng, MapSize, randElevation, biome.MainTerrain, settings.CircularMap);
            Map = map;
            RmgenLibrary.CurrentMap = map;
            int mapSize = MapSize;

            var tc = new TileClassSet(mapSize);
            var gaia = new Rmgen2Gaia(rng, map, biome, BiomeName, tc, settings);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(), new TileClassPainter(tc["land"]), null);

            if (!settings.Nomad)
            {
                double distance = RmgenLibrary.FractionToTiles(rng.RandFloat(0.2, 0.35), mapSize);
                double angle = rng.RandomAngle();
                var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                    rng, map, settings, settings.PlayerPlacement, distance, angle);
                Rmgen2Setup.CreateBases(rng, map, settings, tc, biome, BiomeName, playerIDs, playerPosition);
            }

            var features = new List<Rmgen2Setup.GaiaElement>
            {
                new()
                {
                    Func = gaia.AddBluffs,
                    BaseHeight = randElevation,
                    Avoid = new object[]
                    {
                        tc["bluff"], 20, tc["hill"], 10, tc["mountain"], 20, tc["plateau"], 15,
                        tc["player"], 30, tc["valley"], 5, tc["water"], 7,
                    },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddHills,
                    Avoid = new object[]
                    {
                        tc["bluff"], 5, tc["hill"], 15, tc["mountain"], 2, tc["plateau"], 15,
                        tc["player"], 20, tc["valley"], 2, tc["water"], 2,
                    },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddMountains,
                    Avoid = new object[]
                    {
                        tc["bluff"], 20, tc["mountain"], 25, tc["plateau"], 15, tc["player"], 20,
                        tc["valley"], 10, tc["water"], 15,
                    },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddPlateaus,
                    Avoid = new object[]
                    {
                        tc["bluff"], 20, tc["mountain"], 25, tc["plateau"], 25, tc["player"], 40,
                        tc["valley"], 10, tc["water"], 15,
                    },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
            };

            if (randElevation < 4)
                features.Add(new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddLakes,
                    Avoid = new object[]
                    {
                        tc["bluff"], 7, tc["hill"], 2, tc["mountain"], 15, tc["plateau"], 10,
                        tc["player"], 20, tc["valley"], 10, tc["water"], 25,
                    },
                    Sizes = new[] { "small" }, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                });

            if (randElevation > 20)
                features.Add(new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddValleys,
                    BaseHeight = randElevation,
                    Avoid = new object[]
                    {
                        tc["bluff"], 5, tc["hill"], 5, tc["mountain"], 25, tc["plateau"], 20,
                        tc["player"], 40, tc["valley"], 15, tc["water"], 10,
                    },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                });

            Rmgen2Setup.AddElements(rng, Rmgen2Setup.Shuffle(rng, features));

            Rmgen2Setup.AddElements(rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddLayeredPatches,
                    Avoid = new object[]
                    {
                        tc["bluff"], 2, tc["dirt"], 5, tc["forest"], 2, tc["mountain"], 2,
                        tc["plateau"], 2, tc["player"], 12, tc["water"], 3,
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
                        tc["mountain"], 2, tc["plateau"], 2, tc["player"], 20, tc["rock"], 10, tc["water"], 3,
                    },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddAnimals,
                    Avoid = new object[]
                    {
                        tc["animals"], 20, tc["bluff"], 5, tc["forest"], 2, tc["metal"], 2,
                        tc["mountain"], 1, tc["plateau"], 2, tc["player"], 20, tc["rock"], 2, tc["water"], 3,
                    },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddStragglerTrees,
                    Avoid = new object[]
                    {
                        tc["berries"], 5, tc["bluff"], 5, tc["forest"], 7, tc["metal"], 2,
                        tc["mountain"], 1, tc["plateau"], 2, tc["player"], 12, tc["rock"], 2, tc["water"], 5,
                    },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = Rmgen2Setup.AllAmounts,
                },
            }));

            return map.MakeExportable();
        }
    }
}
