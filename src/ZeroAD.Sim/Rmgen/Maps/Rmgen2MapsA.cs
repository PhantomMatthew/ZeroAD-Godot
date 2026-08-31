using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;
using E = ZeroAD.Sim.Rmgen.Common.Rmgen2Context.Element;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>frontier.js（逐字移植）——每局随机基准海拔，据此决定是否长湖泊/谷地；
    /// 台地/丘陵/山脉/高原四件套洗牌后铺开。</summary>
    public sealed class FrontierMap2 : Rmgen2Map
    {
        private double _randElevation;

        /// <summary>上游：randIntInclusive(0,29)，&lt;25 时重抽 randIntInclusive(1,4)——
        /// 即"大概率低地、小概率高原"。</summary>
        protected override double PickHeightLand(RmgenRng rng)
        {
            double randElevation = rng.RandIntInclusive(0, 29);
            if (randElevation < 25)
                randElevation = rng.RandIntInclusive(1, 4);
            _randElevation = randElevation;
            return randElevation;
        }

        protected override void GenerateRmgen2()
        {
            var c = Ctx;

            if (!Settings.Nomad)
                CreateBasesByPattern(0.2, 0.35);

            var features = new List<E>
            {
                new()
                {
                    Func = (cs, s, d, f, bh) => c.AddBluffs(cs, s, d, f, bh),
                    BaseHeight = _randElevation,
                    Avoid = new object[] { c.ClBluff, 20, c.ClHill, 10, c.ClMountain, 20,
                        c.ClPlateau, 15, c.ClPlayer, 30, c.ClValley, 5, c.ClWater, 7 },
                },
                new()
                {
                    Func = (cs, s, d, f, _) => c.AddHills(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 5, c.ClHill, 15, c.ClMountain, 2,
                        c.ClPlateau, 15, c.ClPlayer, 20, c.ClValley, 2, c.ClWater, 2 },
                },
                new()
                {
                    Func = (cs, s, d, f, _) => c.AddMountains(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 20, c.ClMountain, 25, c.ClPlateau, 15,
                        c.ClPlayer, 20, c.ClValley, 10, c.ClWater, 15 },
                },
                new()
                {
                    Func = (cs, s, d, f, _) => c.AddPlateaus(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 20, c.ClMountain, 25, c.ClPlateau, 25,
                        c.ClPlateau, 25, c.ClPlayer, 40, c.ClValley, 10, c.ClWater, 15 },
                },
            };

            if (_randElevation < 4)
                features.Add(new E
                {
                    Func = (cs, s, d, f, _) => c.AddLakes(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 7, c.ClHill, 2, c.ClMountain, 15,
                        c.ClPlateau, 10, c.ClPlayer, 20, c.ClValley, 10, c.ClWater, 25 },
                    Sizes = new[] { "small" },
                });

            if (_randElevation > 20)
                features.Add(new E
                {
                    Func = (cs, s, d, f, bh) => c.AddValleys(cs, s, d, f, bh),
                    BaseHeight = _randElevation,
                    Avoid = new object[] { c.ClBluff, 5, c.ClHill, 5, c.ClMountain, 25,
                        c.ClPlateau, 20, c.ClPlayer, 40, c.ClValley, 15, c.ClWater, 10 },
                });

            c.AddElements(Shuffle(features));

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 2, c.ClDirt, 5, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlateau, 2, c.ClPlayer, 12, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 2, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlateau, 2, c.ClPlayer, 12, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
            });

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 3,
                        c.ClMountain, 2, c.ClPlateau, 2, c.ClPlayer, 30, c.ClRock, 10,
                        c.ClMetal, 20, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 3,
                        c.ClMountain, 2, c.ClPlateau, 2, c.ClPlayer, 30, c.ClRock, 20,
                        c.ClMetal, 10, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 18,
                        c.ClMetal, 3, c.ClMountain, 5, c.ClPlateau, 5, c.ClPlayer, 20,
                        c.ClRock, 3, c.ClWater, 2 },
                    Amounts = new[] { "few", "normal", "many", "tons" },
                },
            }));

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddBerries(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 30, c.ClBluff, 5, c.ClForest, 5,
                        c.ClMetal, 10, c.ClMountain, 2, c.ClPlateau, 2, c.ClPlayer, 20,
                        c.ClRock, 10, c.ClWater, 3 },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 20, c.ClBluff, 5, c.ClForest, 2,
                        c.ClMetal, 2, c.ClMountain, 1, c.ClPlateau, 2, c.ClPlayer, 20,
                        c.ClRock, 2, c.ClWater, 3 },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStragglerTrees(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 7,
                        c.ClMetal, 2, c.ClMountain, 1, c.ClPlateau, 2, c.ClPlayer, 12,
                        c.ClRock, 2, c.ClWater, 5 },
                },
            }));
        }
    }

    /// <summary>ambush.js（逐字移植）——满图台地（bluff）+ 从每个基地凿出的通道，
    /// 台地上/平原上各一套森林参数，形成"埋伏走廊"地形。</summary>
    public sealed class AmbushMap2 : Rmgen2Map
    {
        private const double HeightLandConst = 2;

        protected override double PickHeightLand(RmgenRng rng) => HeightLandConst;

        protected override IReadOnlyList<string> ExtraTileClasses
            => new[] { "bluffsPassage", "nomadArea" };

        protected override void GenerateRmgen2()
        {
            var c = Ctx;
            var bluffsPassage = c.Cl("bluffsPassage");

            var playerPosition = CreateBasesByPattern(0.25, 0.35);

            if (!Settings.Nomad)
                c.MarkPlayerAvoidanceArea(playerPosition,
                    RmgenCommon.DefaultPlayerBaseRadius(MapSize));

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, bh) => c.AddBluffs(cs, s, d, f, bh),
                    BaseHeight = HeightLandConst,
                    Avoid = new object[] { c.ClBluffIgnore, 0 },
                    Sizes = new[] { "normal", "big", "huge" },
                    Mixes = new[] { "same" },
                    Amounts = new[] { "many" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddHills(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 5, c.ClHill, 15, c.ClPlayer, 20 },
                    Sizes = new[] { "normal", "big" },
                    Mixes = new[] { "normal" },
                    Amounts = new[] { "tons" },
                },
            });

            if (!Settings.Nomad)
                c.CreateBluffsPassages(playerPosition);

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 2, bluffsPassage, 4, c.ClDirt, 5,
                        c.ClForest, 2, c.ClMountain, 2, c.ClPlayer, 12, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 2, bluffsPassage, 4, c.ClForest, 2,
                        c.ClMountain, 2, c.ClPlayer, 12, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
            });

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMetal(cs, s, d, f),
                    Avoid = new object[] { bluffsPassage, 4, c.ClBerries, 5, c.ClForest, 3,
                        c.ClMountain, 2, c.ClPlayer, 30, c.ClRock, 10, c.ClMetal, 20, c.ClWater, 3 },
                    Stay = new object[] { c.ClBluff, 5 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { bluffsPassage, 4, c.ClBerries, 5, c.ClForest, 3,
                        c.ClMountain, 2, c.ClPlayer, 30, c.ClRock, 20, c.ClMetal, 10, c.ClWater, 3 },
                    Stay = new object[] { c.ClBluff, 5 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                // 台地上的森林
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { bluffsPassage, 4, c.ClForest, 6, c.ClMetal, 3,
                        c.ClMountain, 5, c.ClPlayer, 20, c.ClRock, 3, c.ClWater, 2 },
                    Stay = new object[] { c.ClBluff, 5 },
                    Sizes = new[] { "big" }, Mixes = new[] { "normal" }, Amounts = new[] { "tons" },
                },
                // 平原上的森林
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { bluffsPassage, 4, c.ClBluff, 10, c.ClForest, 10,
                        c.ClMetal, 3, c.ClMountain, 5, c.ClPlayer, 20, c.ClRock, 3, c.ClWater, 2 },
                    Sizes = new[] { "small" }, Mixes = new[] { "same" }, Amounts = new[] { "normal" },
                },
            }));

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddBerries(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 5, bluffsPassage, 4, c.ClForest, 5,
                        c.ClMetal, 10, c.ClMountain, 2, c.ClPlayer, 20, c.ClRock, 10, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "few" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 5, bluffsPassage, 4, c.ClForest, 2,
                        c.ClMetal, 2, c.ClMountain, 1, c.ClPlayer, 12, c.ClRock, 2, c.ClWater, 3 },
                    Sizes = new[] { "small" }, Mixes = new[] { "similar" }, Amounts = new[] { "few" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStragglerTrees(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, bluffsPassage, 4,
                        c.ClForest, 7, c.ClMetal, 2, c.ClMountain, 1, c.ClPlayer, 12,
                        c.ClRock, 2, c.ClWater, 5 },
                    Sizes = new[] { "tiny" }, Mixes = new[] { "same" }, Amounts = new[] { "many" },
                },
            }));
        }
    }
}
