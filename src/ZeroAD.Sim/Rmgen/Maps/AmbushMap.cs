using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>ambush.js(250 行,逐字移植)——伏击走廊:可通行陡坡高地(bluffs)环绕基地,
    /// 中心走廊连接玩家;走 rmgen2 声明式管线(addElements + addBluffs/addHills/...,
    /// 定义于 <see cref="Rmgen2Gaia"/>/<see cref="Rmgen2Setup"/>)。
    /// Nomad 分支(placePlayersNomad)未移植——同既有 P0 简化约定。
    /// playerbaseTypes[...].walls(伊比利亚开局城墙)未移植,详见 Rmgen2Setup.CreateBase 注释。</summary>
    public sealed class AmbushMap : StandardMap
    {
        protected override double HeightLand => 2;

        /// <summary>上游 new RandomMap(heightLand, g_Terrains.mainTerrain)——用完整
        /// mainTerrain 名单(逐图块 pickRandom),不是单一 MainTerrain0。</summary>
        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            int mapSize = MapSize;

            var tc = new TileClassSet(mapSize, new[] { "bluffsPassage", "nomadArea" });
            var gaia = new Rmgen2Gaia(rng, map, biome, BiomeName, tc, settings);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(), new TileClassPainter(tc["land"]), null);

            double distance = RmgenLibrary.FractionToTiles(rng.RandFloat(0.25, 0.35), mapSize);
            double angle = rng.RandomAngle();
            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, settings.PlayerPlacement, distance, angle);
            Rmgen2Setup.CreateBases(rng, map, settings, tc, biome, BiomeName, playerIDs, playerPosition);

            if (!settings.Nomad)
                gaia.MarkPlayerAvoidanceArea(playerPosition, RmgenCommon.DefaultPlayerBaseRadius(mapSize));

            Rmgen2Setup.AddElements(rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddBluffs,
                    Avoid = new object[] { tc["bluffIgnore"], 0 },
                    Sizes = new[] { "normal", "big", "huge" },
                    Mixes = new[] { "same" },
                    Amounts = new[] { "many" },
                },
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddHills,
                    Avoid = new object[] { tc["bluff"], 5, tc["hill"], 15, tc["player"], 20 },
                    Sizes = new[] { "normal", "big" },
                    Mixes = new[] { "normal" },
                    Amounts = new[] { "tons" },
                },
            });

            if (!settings.Nomad)
                gaia.CreateBluffsPassages(playerPosition);

            Rmgen2Setup.AddElements(rng, new[]
            {
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddLayeredPatches,
                    Avoid = new object[]
                    {
                        tc["bluff"], 2, tc["bluffsPassage"], 4, tc["dirt"], 5,
                        tc["forest"], 2, tc["mountain"], 2, tc["player"], 12, tc["water"], 3,
                    },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new Rmgen2Setup.GaiaElement
                {
                    Func = gaia.AddDecoration,
                    Avoid = new object[]
                    {
                        tc["bluff"], 2, tc["bluffsPassage"], 4, tc["forest"], 2,
                        tc["mountain"], 2, tc["player"], 12, tc["water"], 3,
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
                        tc["bluffsPassage"], 4, tc["berries"], 5, tc["forest"], 3, tc["mountain"], 2,
                        tc["player"], 30, tc["rock"], 10, tc["metal"], 20, tc["water"], 3,
                    },
                    Stay = new object[] { tc["bluff"], 5 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                new()
                {
                    Func = gaia.AddStone,
                    Avoid = new object[]
                    {
                        tc["bluffsPassage"], 4, tc["berries"], 5, tc["forest"], 3, tc["mountain"], 2,
                        tc["player"], 30, tc["rock"], 20, tc["metal"], 10, tc["water"], 3,
                    },
                    Stay = new object[] { tc["bluff"], 5 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                new()
                {
                    // Forests on bluffs
                    Func = gaia.AddForests,
                    Avoid = new object[]
                    {
                        tc["bluffsPassage"], 4, tc["forest"], 6, tc["metal"], 3, tc["mountain"], 5,
                        tc["player"], 20, tc["rock"], 3, tc["water"], 2,
                    },
                    Stay = new object[] { tc["bluff"], 5 },
                    Sizes = new[] { "big" }, Mixes = new[] { "normal" }, Amounts = new[] { "tons" },
                },
                new()
                {
                    // Forests on mainland
                    Func = gaia.AddForests,
                    Avoid = new object[]
                    {
                        tc["bluffsPassage"], 4, tc["bluff"], 10, tc["forest"], 10, tc["metal"], 3,
                        tc["mountain"], 5, tc["player"], 20, tc["rock"], 3, tc["water"], 2,
                    },
                    Sizes = new[] { "small" }, Mixes = new[] { "same" }, Amounts = new[] { "normal" },
                },
            }));

            Rmgen2Setup.AddElements(rng, Rmgen2Setup.Shuffle(rng, new List<Rmgen2Setup.GaiaElement>
            {
                new()
                {
                    Func = gaia.AddBerries,
                    Avoid = new object[]
                    {
                        tc["bluff"], 5, tc["bluffsPassage"], 4, tc["forest"], 5, tc["metal"], 10,
                        tc["mountain"], 2, tc["player"], 20, tc["rock"], 10, tc["water"], 3,
                    },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "few" },
                },
                new()
                {
                    Func = gaia.AddAnimals,
                    Avoid = new object[]
                    {
                        tc["bluff"], 5, tc["bluffsPassage"], 4, tc["forest"], 2, tc["metal"], 2,
                        tc["mountain"], 1, tc["player"], 12, tc["rock"], 2, tc["water"], 3,
                    },
                    Sizes = new[] { "small" }, Mixes = new[] { "similar" }, Amounts = new[] { "few" },
                },
                new()
                {
                    Func = gaia.AddStragglerTrees,
                    Avoid = new object[]
                    {
                        tc["berries"], 5, tc["bluff"], 5, tc["bluffsPassage"], 4, tc["forest"], 7,
                        tc["metal"], 2, tc["mountain"], 1, tc["player"], 12, tc["rock"], 2, tc["water"], 5,
                    },
                    Sizes = new[] { "tiny" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
            }));

            return map.MakeExportable();
        }
    }
}
