using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;
using E = ZeroAD.Sim.Rmgen.Common.Rmgen2Context.Element;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>hells_pass.js（逐字移植）——按队数在图心放射状竖起数道高墙（spine），
    /// 队伍间只能从墙缝穿行，形成"地狱关隘"。</summary>
    public sealed class HellsPassMap2 : Rmgen2Map
    {
        private const double HeightLandConst = 1;
        private const double HeightBarrier = 30;

        protected override double PickHeightLand(RmgenRng rng) => HeightLandConst;

        protected override void GenerateRmgen2()
        {
            var c = Ctx;

            var teamsArray = RmgenCommon.GetTeamsArray(Rng, Settings);
            double startAngle = Rng.RandomAngle();

            CreateBasesAt("groupedLines",
                RmgenLibrary.FractionToTiles(0.2, MapSize),
                RmgenLibrary.FractionToTiles(0.08, MapSize),
                startAngle, false);

            PlaceBarriers(teamsArray.Count, startAngle);

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, bh) => c.AddBluffs(cs, s, d, f, bh),
                    BaseHeight = HeightLandConst,
                    Avoid = new object[] { c.ClBluff, 20, c.ClHill, 5, c.ClMountain, 20,
                        c.ClPlateau, 20, c.ClPlayer, 30, c.ClSpine, 15, c.ClValley, 5, c.ClWater, 7 },
                    Sizes = new[] { "normal", "big" }, Mixes = new[] { "varied" },
                    Amounts = new[] { "few" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddHills(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 5, c.ClHill, 15, c.ClMountain, 2,
                        c.ClPlateau, 5, c.ClPlayer, 20, c.ClSpine, 15, c.ClValley, 2, c.ClWater, 2 },
                    Sizes = new[] { "normal", "big" }, Mixes = new[] { "varied" },
                    Amounts = new[] { "few" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLakes(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 7, c.ClHill, 2, c.ClMountain, 15,
                        c.ClPlateau, 10, c.ClPlayer, 20, c.ClSpine, 15, c.ClValley, 10, c.ClWater, 25 },
                    Sizes = new[] { "big", "huge" }, Mixes = new[] { "varied", "unique" },
                    Amounts = new[] { "few" },
                },
            }));

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 2, c.ClDirt, 5, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlateau, 2, c.ClPlayer, 12, c.ClSpine, 5, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 2, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlateau, 2, c.ClPlayer, 12, c.ClSpine, 5, c.ClWater, 3 },
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
                        c.ClMetal, 20, c.ClSpine, 5, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 3,
                        c.ClMountain, 2, c.ClPlateau, 2, c.ClPlayer, 30, c.ClRock, 20,
                        c.ClMetal, 10, c.ClSpine, 5, c.ClWater, 3 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBerries, 5, c.ClBluff, 5, c.ClForest, 18,
                        c.ClMetal, 3, c.ClMountain, 5, c.ClPlateau, 5, c.ClPlayer, 20,
                        c.ClRock, 3, c.ClSpine, 5, c.ClWater, 2 },
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

        /// <summary>placeBarriers——放射状高墙 + 墙上装饰/道具。</summary>
        private void PlaceBarriers(int teamCount, double startAngle)
        {
            var c = Ctx;

            object spineTerrain = Biome.Dirt;
            if (BiomeName == "generic/arctic")
                spineTerrain = Biome.Tier1Terrain;
            if (BiomeName == "generic/alpine" || BiomeName == "generic/savanna")
                spineTerrain = Biome.Tier2Terrain;
            if (BiomeName == "generic/autumn")
                spineTerrain = Biome.Tier4Terrain;

            int spineCount = Settings.Nomad ? Rng.RandIntInclusive(1, 4) : teamCount;

            for (int i = 0; i < spineCount; ++i)
            {
                double mSize = 8;
                double mWaviness = 0.6;
                double mOffset = 0.5;
                double mTaper = -1.5;

                if (spineCount > 3 || MapSize <= 192)
                {
                    mWaviness = 0.2;
                    mOffset = 0.2;
                    mTaper = -1;
                }

                if (spineCount >= 5)
                {
                    mSize = 4;
                    mWaviness = 0.2;
                    mOffset = 0.2;
                    mTaper = -0.7;
                }

                double angle = startAngle + (i + 0.5) * 2 * SafeMath.PI / spineCount;

                var startOffset = new RmgenVector2D(RmgenLibrary.FractionToTiles(0.075, MapSize), 0);
                startOffset.Rotate(-angle);
                var endOffset = new RmgenVector2D(RmgenLibrary.FractionToTiles(0.42, MapSize), 0);
                endOffset.Rotate(-angle);

                RmgenLibrary.CreateArea(
                    new PathPlacer(Rng, mWaviness, 0.1, mOffset, mTaper)
                    {
                        Start = RmgenVector2D.Add(Ctx.MapCenter, startOffset),
                        End = RmgenVector2D.Add(Ctx.MapCenter, endOffset),
                        Width = RmgenLibrary.ScaleByMapSize(14, mSize, MapSize),
                    },
                    new IPainter[]
                    {
                        new LayeredPainter(new[] { (object)Biome.Cliff, spineTerrain },
                            new[] { 2 }, Rng),
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            HeightBarrier, 2),
                        new TileClassPainter(c.ClSpine),
                    },
                    RmgenLibrary.AvoidClasses(c.ClPlayer, 5, c.ClBaseResource, 5));
            }

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClBluff, 2, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlayer, 12, c.ClWater, 3 },
                    Stay = new object[] { c.ClSpine, 5 },
                    Sizes = new[] { "huge" }, Mixes = new[] { "unique" }, Amounts = new[] { "tons" },
                },
            });

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddProps(cs, s, d, f),
                    Avoid = new object[] { c.ClForest, 2, c.ClPlayer, 2, c.ClProp, 20, c.ClWater, 3 },
                    Stay = new object[] { c.ClSpine, 8 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "scarce" },
                },
            });
        }
    }

    /// <summary>lions_den.js（逐字移植）——图心一座高山，四周下沉成谷；
    /// 每名玩家一个"兽穴"平台，用斜坡道连向扩张点与邻居。</summary>
    public sealed class LionsDenMap2 : Rmgen2Map
    {
        private const double HeightValley = 0;
        private const double HeightPath = 10;
        private const double HeightDen = 15;
        private const double HeightDenTop = 50;

        private double _startAngle;

        protected override double PickHeightLand(RmgenRng rng) => HeightDenTop;

        /// <summary>基底是 tier2Terrain（山顶），不是 mainTerrain。</summary>
        protected override IReadOnlyList<string> BaseTerrainList => new[] { Biome.Tier2Terrain };

        protected override IReadOnlyList<string>? ExtraTileClasses => new[] { "step" };

        protected override void GenerateRmgen2()
        {
            var c = Ctx;
            var clStep = c.Cl("step");
            _startAngle = Rng.RandomAngle();

            CreateBasesAt("circle",
                RmgenLibrary.FractionToTiles(0.4, MapSize),
                RmgenLibrary.FractionToTiles(Rng.RandFloat(0.05, 0.1), MapSize),
                _startAngle, true);

            CreateSunkenTerrain();

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClDirt, 5, c.ClForest, 2,
                        c.ClMountain, 2, c.ClPlayer, 12, clStep, 5 },
                    Stay = new object[] { c.ClValley, 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClDirt, 5, c.ClForest, 2,
                        c.ClMountain, 2, c.ClPlayer, 12 },
                    Stay = new object[] { c.ClSettlement, 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddLayeredPatches(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClDirt, 5, c.ClForest, 2 },
                    Stay = new object[] { c.ClPlayer, 1 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClForest, 2 },
                    Stay = new object[] { c.ClPlayer, 1 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlayer, 12, clStep, 2 },
                    Stay = new object[] { c.ClValley, 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlayer, 12 },
                    Stay = new object[] { c.ClSettlement, 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "normal" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClForest, 2, c.ClMountain, 2,
                        c.ClPlayer, 12 },
                    Stay = new object[] { clStep, 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "scarce" },
                },
            });

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClBerries, 5, c.ClForest, 3,
                        c.ClPlayer, 30, c.ClRock, 10, c.ClMetal, 20 },
                    Stay = new object[] { c.ClSettlement, 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddMetal(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClBerries, 5, c.ClForest, 3,
                        c.ClPlayer, 10, c.ClRock, 10, c.ClMetal, 20, c.ClMountain, 5, clStep, 5 },
                    Stay = new object[] { c.ClValley, 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClBerries, 5, c.ClForest, 3,
                        c.ClPlayer, 30, c.ClRock, 20, c.ClMetal, 10 },
                    Stay = new object[] { c.ClSettlement, 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStone(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClBerries, 5, c.ClForest, 3,
                        c.ClPlayer, 10, c.ClRock, 20, c.ClMetal, 10, c.ClMountain, 5, clStep, 5 },
                    Stay = new object[] { c.ClValley, 7 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "same" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClBerries, 5, c.ClForest, 18,
                        c.ClMetal, 3, c.ClPlayer, 20, c.ClRock, 3 },
                    Stay = new object[] { c.ClSettlement, 7 },
                    Sizes = new[] { "normal", "big" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddForests(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClBerries, 3, c.ClForest, 18,
                        c.ClMetal, 3, c.ClMountain, 5, c.ClPlayer, 5, c.ClRock, 3, clStep, 1 },
                    Stay = new object[] { c.ClValley, 7 },
                    Sizes = new[] { "normal", "big" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
            }));

            c.AddElements(Shuffle(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddBerries(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClBerries, 30, c.ClForest, 5,
                        c.ClMetal, 10, c.ClPlayer, 20, c.ClRock, 10 },
                    Stay = new object[] { c.ClSettlement, 7 },
                    Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddBerries(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClBerries, 30, c.ClForest, 5,
                        c.ClMetal, 10, c.ClMountain, 5, c.ClPlayer, 10, c.ClRock, 10, clStep, 5 },
                    Stay = new object[] { c.ClValley, 7 },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 20, c.ClBaseResource, 5, c.ClForest, 0,
                        c.ClMetal, 1, c.ClPlayer, 20, c.ClRock, 1 },
                    Stay = new object[] { c.ClSettlement, 7 },
                    Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddAnimals(cs, s, d, f),
                    Avoid = new object[] { c.ClAnimals, 20, c.ClBaseResource, 5, c.ClForest, 0,
                        c.ClMetal, 1, c.ClMountain, 5, c.ClPlayer, 10, c.ClRock, 1, clStep, 5 },
                    Stay = new object[] { c.ClValley, 7 },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStragglerTrees(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClBerries, 5, c.ClForest, 7,
                        c.ClMetal, 3, c.ClPlayer, 12, c.ClRock, 3 },
                    Stay = new object[] { c.ClSettlement, 7 },
                    Amounts = new[] { "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStragglerTrees(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClBerries, 5, c.ClForest, 7,
                        c.ClMetal, 3, c.ClMountain, 5, c.ClPlayer, 10, c.ClRock, 3, clStep, 5 },
                    Stay = new object[] { c.ClValley, 7 },
                    Amounts = new[] { "normal", "many", "tons" },
                },
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddStragglerTrees(cs, s, d, f),
                    Avoid = new object[] { c.ClPlayer, 10, c.ClBaseResource, 5, c.ClBerries, 5,
                        c.ClForest, 3, c.ClMetal, 5, c.ClRock, 5 },
                    Stay = new object[] { c.ClPlayer, 1 },
                    Sizes = new[] { "huge" }, Mixes = new[] { "same" }, Amounts = new[] { "tons" },
                },
            }));

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClValley, 4, c.ClPlayer, 4,
                        c.ClSettlement, 4, clStep, 4 },
                    Stay = new object[] { c.ClLand, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "tons" },
                },
            });

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddProps(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClValley, 4, c.ClPlayer, 4,
                        c.ClSettlement, 4, clStep, 4 },
                    Stay = new object[] { c.ClLand, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "scarce" },
                },
            });

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddDecoration(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClPlayer, 4,
                        c.ClSettlement, 4, clStep, 4 },
                    Stay = new object[] { c.ClMountain, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "tons" },
                },
            });

            c.AddElements(new[]
            {
                new E
                {
                    Func = (cs, s, d, f, _) => c.AddProps(cs, s, d, f),
                    Avoid = new object[] { c.ClBaseResource, 5, c.ClPlayer, 4,
                        c.ClSettlement, 4, clStep, 4 },
                    Stay = new object[] { c.ClMountain, 2 },
                    Sizes = new[] { "normal" }, Mixes = new[] { "normal" }, Amounts = new[] { "scarce" },
                },
            });
        }

        /// <summary>createSunkenTerrain——中央谷 + 中央山 + 每玩家兽穴/扩张点/道路。</summary>
        private void CreateSunkenTerrain()
        {
            var c = Ctx;
            var clStep = c.Cl("step");
            var mapCenter = c.MapCenter;

            object baseTerrain = Biome.MainTerrain;
            object middle = Biome.Dirt;
            object lower = Biome.Tier2Terrain;
            object road = Biome.Road;

            if (BiomeName == "generic/arctic")
            {
                middle = Biome.Tier2Terrain;
                lower = Biome.Tier1Terrain;
            }

            if (BiomeName == "generic/alpine")
            {
                middle = Biome.Shore;
                lower = Biome.Tier4Terrain;
            }

            if (BiomeName == "generic/aegean")
            {
                middle = Biome.Tier1Terrain;
                lower = Biome.ForestFloor1;
            }

            if (BiomeName == "generic/savanna")
            {
                middle = Biome.Tier2Terrain;
                lower = Biome.Tier4Terrain;
            }

            if (BiomeName == "generic/india" || BiomeName == "generic/autumn")
                road = Biome.RoadWild;

            if (BiomeName == "generic/autumn")
                middle = Biome.Shore;

            double expSize = RmgenGeometry.DiskArea(
                RmgenLibrary.FractionToTiles(0.14, MapSize)) / NumPlayers;
            double expDist = 0.1 + NumPlayers / 200.0;
            double expAngle = 0.75;

            if (NumPlayers <= 2)
            {
                expSize = RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.075, MapSize));
                expAngle = 0.72;
            }

            double nRoad = 0.44;
            double nExp = 0.425;

            if (NumPlayers < 4)
            {
                nRoad = 0.42;
                nExp = 0.4;
            }

            // 中央谷
            RmgenLibrary.CreateArea(
                new DiskPlacer(RmgenLibrary.FractionToTiles(0.29, MapSize), mapCenter),
                new IPainter[]
                {
                    new LayeredPainter(new[] { (object)Biome.Cliff, lower }, new[] { 3 }, Rng),
                    new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                        HeightValley, 3),
                    new TileClassPainter(c.ClValley),
                },
                null);

            // 中央山
            RmgenLibrary.CreateArea(
                new DiskPlacer(RmgenLibrary.FractionToTiles(0.21, MapSize), mapCenter),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { Biome.Cliff, Biome.Tier2Terrain },
                        new[] { 3 }, Rng),
                    new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                        HeightDenTop, 3),
                    new TileClassPainter(c.ClMountain),
                },
                null);

            RmgenVector2D GetCoords(double distance, int playerID, double playerIDOffset)
            {
                double angle = _startAngle + (playerID + playerIDOffset) * 2 * SafeMath.PI / NumPlayers;
                var offset = new RmgenVector2D(RmgenLibrary.FractionToTiles(distance, MapSize), 0);
                offset.Rotate(-angle);
                var pos = RmgenVector2D.Add(mapCenter, offset);
                pos.Round();
                return pos;
            }

            for (int i = 0; i < NumPlayers; ++i)
            {
                var playerPosition = GetCoords(0.4, i, 0);

                // 玩家 → 扩张点的坡道
                var expansionPosition = GetCoords(expDist, i, expAngle);
                RmgenLibrary.CreateArea(
                    new PathPlacer(Rng, 0.7, 0.5, 0.1, -1)
                    {
                        Start = playerPosition, End = expansionPosition, Width = 12,
                    },
                    new IPainter[]
                    {
                        new LayeredPainter(new[] { (object)Biome.Cliff, middle, road },
                            new[] { 3, 4 }, Rng),
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            HeightPath, 3),
                        new TileClassPainter(clStep),
                    },
                    null);

                // 玩家 → 左右邻居的坡道
                foreach (double neighborOffset in new[] { -0.5, 0.5 })
                {
                    var neighborPosition = GetCoords(nRoad, i, neighborOffset);
                    var pathPosition = GetCoords(0.47, i, 0);
                    RmgenLibrary.CreateArea(
                        new PathPlacer(Rng, 0.4, 0.5, 0.1, -0.6)
                        {
                            Start = pathPosition, End = neighborPosition, Width = 19,
                        },
                        new IPainter[]
                        {
                            new LayeredPainter(new[] { (object)Biome.Cliff, middle, road },
                                new[] { 3, 6 }, Rng),
                            new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                                HeightPath, 3),
                            new TileClassPainter(clStep),
                        },
                        null);
                }

                // 兽穴
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng,
                        RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.1, MapSize)) /
                            (Settings.Nomad ? 2 : 1),
                        0.9, 0.3, double.PositiveInfinity, playerPosition),
                    new IPainter[]
                    {
                        new LayeredPainter(new[] { (object)Biome.Cliff, baseTerrain },
                            new[] { 3 }, Rng),
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            HeightDen, 3),
                        new TileClassPainter(c.ClValley),
                    },
                    null);

                // 扩张点
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, expSize, 0.9, 0.3, double.PositiveInfinity, expansionPosition),
                    new IPainter[]
                    {
                        new LayeredPainter(new[] { (object)Biome.Cliff, baseTerrain },
                            new[] { 3 }, Rng),
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            HeightDen, 3),
                        new TileClassPainter(c.ClSettlement),
                    },
                    RmgenLibrary.AvoidClasses(c.ClSettlement, 2));
            }

            // 玩家之间的扩张点
            for (int i = 0; i < NumPlayers; ++i)
            {
                var position = GetCoords(nExp, i, 0.5);
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(Rng, expSize, 0.9, 0.3, double.PositiveInfinity, position),
                    new IPainter[]
                    {
                        new LayeredPainter(new[] { (object)Biome.Cliff, lower }, new[] { 3 }, Rng),
                        new SmoothElevationPainter(Rng, SmoothElevationPainter.SmoothType.Solid,
                            HeightValley, 3),
                        new TileClassPainter(c.ClSettlement),
                    },
                    null);
            }
        }
    }
}
