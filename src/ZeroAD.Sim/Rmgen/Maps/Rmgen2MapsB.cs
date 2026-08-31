using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;
using E = ZeroAD.Sim.Rmgen.Common.Rmgen2Context.Element;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>empire.js（逐字移植）——每队两处据点（外圈 0.37、内圈 0.15，内圈整体旋转），
    /// 地形以巨型山脉/高原为主。</summary>
    public sealed class EmpireMap2 : Rmgen2Map
    {
        protected override double PickHeightLand(RmgenRng rng) => 2;

        protected override void GenerateRmgen2()
        {
            var c = Ctx;

            var teamsArray = RmgenCommon.GetTeamsArray(Rng, Settings);
            double startAngle = Rng.RandomAngle();

            CreateBasesAt("stronghold",
                RmgenLibrary.FractionToTiles(0.37, MapSize),
                RmgenLibrary.FractionToTiles(0.04, MapSize),
                startAngle, false);

            // 第二组据点：整体旋转后再放一次（每队因此有两处基地）
            double rotation = SafeMath.PI;
            if (teamsArray.Count == 2)
                rotation = SafeMath.PI / 2;
            if (teamsArray.Count == 4)
                rotation = 5.0 / 4 * SafeMath.PI;

            CreateBasesAt("stronghold",
                RmgenLibrary.FractionToTiles(0.15, MapSize),
                RmgenLibrary.FractionToTiles(0.04, MapSize),
                startAngle + rotation, false);

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddHills(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 5, c.ClHill, 15, c.ClMountain, 2,
                        c.ClPlateau, 5, c.ClPlayer, 20, c.ClValley, 2, c.ClWater, 2 },
                    Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMountains(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 20, c.ClMountain, 25, c.ClPlateau, 20,
                        c.ClPlayer, 20, c.ClValley, 10, c.ClWater, 15 },
                    Sizes = new[] { "huge" }, Mixes = new[] { "same", "similar" },
                    Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddPlateaus(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 20, c.ClMountain, 25, c.ClPlateau, 20,
                        c.ClPlayer, 40, c.ClValley, 10, c.ClWater, 15 },
                    Sizes = new[] { "huge" }, Mixes = new[] { "same", "similar" },
                    Amounts = new[] { "tons" },
                },
            }));

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 2, c.ClDirt, 5, c.ClForest, 2,
                        c.ClMountain, 2, c.ClPlateau, 2, c.ClPlayer, 12, c.ClWater, 3 },
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
                        c.ClMountain, 2, c.ClPlayer, 30, c.ClRock, 10, c.ClMetal, 20,
                        c.ClPlateau, 2, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 3,
                        c.ClMountain, 2, c.ClPlayer, 30, c.ClRock, 20, c.ClMetal, 10,
                        c.ClPlateau, 2, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 18,
                        c.ClMetal, 3, c.ClMountain, 5, c.ClPlateau, 2, c.ClPlayer, 20,
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

    /// <summary>stronghold.js（逐字移植）——高地起手（heightLand=30），
    /// 台地/丘陵/山脉/高原/谷地五件套洗牌，基地间凿通道。</summary>
    public sealed class StrongholdMap2 : Rmgen2Map
    {
        private const double HeightLandConst = 30;

        protected override double PickHeightLand(RmgenRng rng) => HeightLandConst;

        protected override void GenerateRmgen2()
        {
            var c = Ctx;

            double distance = RmgenLibrary.FractionToTiles(Rng.RandFloat(0.2, 0.35), MapSize);
            double groupedDistance = RmgenLibrary.FractionToTiles(Rng.RandFloat(0.05, 0.1), MapSize);
            double angle = Rng.RandomAngle();

            var playerPosition = CreateBasesAt("stronghold", distance, groupedDistance, angle, false);
            c.MarkPlayerAvoidanceArea(playerPosition, RmgenCommon.DefaultPlayerBaseRadius(MapSize));

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, bh) => c.AddBluffs(cs, s, d, f, bh),
                    BaseHeight = HeightLandConst,
                    Avoid = new object[] { c.ClBluff, 20, c.ClHill, 5, c.ClMountain, 20,
                        c.ClPlateau, 20, c.ClPlayer, 30, c.ClValley, 5, c.ClWater, 7 },
                    Sizes = new[] { "big", "huge" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddHills(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 5, c.ClHill, 15, c.ClMountain, 2,
                        c.ClPlateau, 2, c.ClPlayer, 20, c.ClValley, 2, c.ClWater, 2 },
                    Sizes = new[] { "normal", "big" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMountains(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 20, c.ClMountain, 25, c.ClPlateau, 20,
                        c.ClPlayer, 20, c.ClValley, 10, c.ClWater, 15 },
                    Sizes = new[] { "big", "huge" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddPlateaus(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 20, c.ClMountain, 25, c.ClPlateau, 25,
                        c.ClPlayer, 40, c.ClValley, 10, c.ClWater, 15 },
                    Sizes = new[] { "big", "huge" },
                },
                new E
                {
                    Func = (cs, s, d, f, bh) => c.AddValleys(cs, s, d, f, bh),
                    BaseHeight = HeightLandConst,
                    Avoid = new object[] { c.ClBluff, 5, c.ClHill, 5, c.ClMountain, 25,
                        c.ClPlateau, 10, c.ClPlayer, 40, c.ClValley, 15, c.ClWater, 10 },
                    Sizes = new[] { "normal", "big" },
                },
            }));

            if (!Settings.Nomad)
                c.CreateBluffsPassages(playerPosition);

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 2, c.ClDirt, 5, c.ClForest, 2,
                        c.ClMountain, 2, c.ClPlateau, 2, c.ClPlayer, 12, c.ClValley, 5, c.ClWater, 3 },
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
                        c.ClRock, 10, c.ClSpine, 2, c.ClWater, 3 },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 20, c.ClBluff, 5, c.ClForest, 2,
                        c.ClMetal, 2, c.ClMountain, 1, c.ClPlateau, 2, c.ClPlayer, 20,
                        c.ClRock, 2, c.ClSpine, 2, c.ClWater, 3 },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStragglerTrees(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 7,
                        c.ClMetal, 2, c.ClMountain, 1, c.ClPlateau, 2, c.ClPlayer, 12,
                        c.ClRock, 2, c.ClSpine, 2, c.ClWater, 5 },
                },
            }));
        }
    }
}
