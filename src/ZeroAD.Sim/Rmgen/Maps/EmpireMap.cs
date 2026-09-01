using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>empire.js(255 行,逐字移植)——帝国:"stronghold" 布置双重下发(外圈 0.37 +
    /// 内圈 0.15,按队伍数旋转角),每玩家两座城邦(原版 json 描述原话:"each player will
    /// start with two civic centers")。地形走 rmgen2 声明式管线(hills/mountains/plateaus)。</summary>
    public sealed class EmpireMap : StandardMap
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

            var tc = new TileClassSet(mapSize);
            var gaia = new Rmgen2Gaia(rng, map, biome, BiomeName, tc, settings);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(), new TileClassPainter(tc["land"]), null);

            var teamsArray = RmgenCommon.GetTeamsArray(settings);
            double startAngle = rng.RandomAngle();

            var (ids1, pos1) = RmgenCommon.PlayerPlacementByPattern(rng, map, settings, "stronghold",
                RmgenLibrary.FractionToTiles(0.37, mapSize), startAngle, groupedDistance: RmgenLibrary.FractionToTiles(0.04, mapSize));
            Rmgen2Setup.CreateBases(rng, map, settings, tc, biome, BiomeName, ids1, pos1);

            double rotation = Math.PI;
            if (teamsArray.Count == 2) rotation = Math.PI / 2;
            if (teamsArray.Count == 4) rotation = 5.0 / 4 * Math.PI;

            var (ids2, pos2) = RmgenCommon.PlayerPlacementByPattern(rng, map, settings, "stronghold",
                RmgenLibrary.FractionToTiles(0.15, mapSize), startAngle + rotation,
                groupedDistance: RmgenLibrary.FractionToTiles(0.04, mapSize));
            Rmgen2Setup.CreateBases(rng, map, settings, tc, biome, BiomeName, ids2, pos2);

            Rmgen2Setup.AddElements(rng, Rmgen2Setup.Shuffle(rng, new List<Rmgen2Setup.GaiaElement>
            {
                new()
                {
                    Func = gaia.AddHills,
                    Avoid = new object[]
                    {
                        tc["bluff"], 5, tc["hill"], 15, tc["mountain"], 2, tc["plateau"], 5,
                        tc["player"], 20, tc["valley"], 2, tc["water"], 2,
                    },
                    Sizes = Rmgen2Setup.AllSizes, Mixes = Rmgen2Setup.AllMixes, Amounts = new[] { "tons" },
                },
                new()
                {
                    Func = gaia.AddMountains,
                    Avoid = new object[]
                    {
                        tc["bluff"], 20, tc["mountain"], 25, tc["plateau"], 20, tc["player"], 20,
                        tc["valley"], 10, tc["water"], 15,
                    },
                    Sizes = new[] { "huge" }, Mixes = new[] { "same", "similar" }, Amounts = new[] { "tons" },
                },
                new()
                {
                    Func = gaia.AddPlateaus,
                    Avoid = new object[]
                    {
                        tc["bluff"], 20, tc["mountain"], 25, tc["plateau"], 20, tc["player"], 40,
                        tc["valley"], 10, tc["water"], 15,
                    },
                    Sizes = new[] { "huge" }, Mixes = new[] { "same", "similar" }, Amounts = new[] { "tons" },
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
                        tc["player"], 30, tc["rock"], 10, tc["metal"], 20, tc["plateau"], 2, tc["water"], 3,
                    },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddStone,
                    Avoid = new object[]
                    {
                        tc["berries"], 5, tc["bluff"], 5, tc["forest"], 3, tc["mountain"], 2,
                        tc["player"], 30, tc["rock"], 20, tc["metal"], 10, tc["plateau"], 2, tc["water"], 3,
                    },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = Rmgen2Setup.AllAmounts,
                },
                new()
                {
                    Func = gaia.AddForests,
                    Avoid = new object[]
                    {
                        tc["berries"], 5, tc["bluff"], 5, tc["forest"], 18, tc["metal"], 3,
                        tc["mountain"], 5, tc["plateau"], 2, tc["player"], 20, tc["rock"], 3, tc["water"], 2,
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
