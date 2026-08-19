using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>arctic_summer.js（348 行）——极地盛夏：冰丘 + 冰湖 + 耐寒动物群。
    /// 无 biome（上游不 LoadLibrary("rmbiome")，全部地形/实体为脚本内联常量）。
    /// 环境设置（雾/PP/水色/天色）按既有移植约定省略；placePlayersNomad 未移植。</summary>
    public sealed class ArcticSummerMap : StandardMap
    {
        // 地形（上游内联常量）
        private static readonly string[] tPrimary = { "alpine_grass_rocky" };
        private const string tForestFloor = "alpine_grass";
        private static readonly string[] tCliff = { "polar_cliff_a", "polar_cliff_b", "polar_cliff_snow" };
        private const string tSecondary = "alpine_grass";
        private static readonly string[] tHalfSnow = { "polar_grass_snow", "ice_dirt" };
        private static readonly string[] tSnowLimited = { "polar_snow_rocks", "polar_ice" };
        private const string tDirt = "ice_dirt";
        private const string tShore = "alpine_shore_rocks";
        private const string tWater = "polar_ice_b";
        private const string tHill = "polar_ice_cracked";

        // 实体（上游内联常量）
        private const string oBush = "gaia/tree/bush_badlands";
        private const string oBush2 = "gaia/tree/bush_temperate";
        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oRabbit = "gaia/fauna_rabbit";
        private const string oMuskox = "gaia/fauna_muskox";
        private const string oDeer = "gaia/fauna_deer";
        private const string oWolf = "gaia/fauna_wolf";
        private const string oWhaleFin = "gaia/fauna_whale_fin";
        private const string oWhaleHumpback = "gaia/fauna_whale_humpback";
        private const string oFish = "gaia/fish/generic";
        private const string oStoneLarge = "gaia/rock/alpine_large";
        private const string oStoneSmall = "gaia/rock/alpine_small";
        private const string oMetalLarge = "gaia/ore/alpine_large";
        private const string aRockLarge = "actor|geology/stone_granite_med.xml";
        private const string aRockMedium = "actor|geology/stone_granite_med.xml";

        protected override double HeightLand => 2;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tPrimary[0]);
            var map = Map;

            const double heightSeaGround = -5;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);

            // playerPlacementByPattern 默认 "circle"（settings 无 PlayerPlacement 字段——
            // 上游 gamesetup 默认即 circle；randomAngle 抽数在 PlayerPlacementCircle 内）
            var (_, playerPosition, _, _) = RmgenCommon.PlayerPlacementCircle(
                rng, map, NumPlayers, RmgenLibrary.FractionToTiles(0.35, MapSize));

            RmgenCommon.PlacePlayerBases(rng, map, settings, tPrimary[0], ClPlayer, null,
                playerPosition, cityPatchOuterTerrain: tPrimary[0], cityPatchInnerTerrain: tSecondary);

            // ── 冰丘（上游 createHills 默认参数：minSize=1, maxSize=floor(scale(4,6)),
            // spread=floor(scale(16,40)), failFraction=0.5, elevation=18, smoothing=2）──
            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tPrimary, tCliff, tHill }, new[] { 1, 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, 18, 2),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 35, ClForest, 20, ClHill, 50, clWater, 2),
                RmgenLibrary.ScaleByMapSize(1, 240, MapSize));

            // ── 冰湖 ──
            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 8, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(40, 180, MapSize)), double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tShore, tWater }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, heightSeaGround, 5),
                    new TileClassPainter(clWater),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 15),
                RmgenLibrary.ScaleByMapSize(1, 20, MapSize));

            // ── 起伏（createBumps(constraint, scale(30,300), 1, 8, 4, 0, 3)）──
            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, 8, 4, 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, 3, 2,
                        relative: true),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 6, clWater, 2),
                RmgenLibrary.ScaleByMapSize(30, 300, MapSize));

            // ── 雪线刷漆 ──
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 4, 15,
                HeightPlacer.Mode.ExcludeMinExcludeMax, tCliff);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 15, 100,
                HeightPlacer.Mode.IncludeMinIncludeMax, tSnowLimited);

            // ── 森林（getTreeCounts(500, 3000, 0.7)）──
            double forestTrees = 0.7 * RmgenLibrary.ScaleByMapSize(500, 3000, MapSize);
            double stragglerTrees = (1 - 0.7) * RmgenLibrary.ScaleByMapSize(500, 3000, MapSize);
            var pForest = new[]
            {
                tForestFloor + "|" + oBush,
                tForestFloor + "|" + oBush2,
                tForestFloor,
            };
            GaiaEntities.CreateForests(rng, map,
                new object[] { tSecondary, tForestFloor, tForestFloor, pForest, pForest },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 14, ClHill, 20, clWater, 2),
                ClForest, forestTrees, NumPlayers);

            // ── 泥地分层斑块 ──
            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                        patchSize, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new object[] { tDirt, tHalfSnow },
                            new object[] { tHalfSnow, tSnowLimited },
                        }, new[] { 2 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClHill, 0, ClDirt, 5,
                        ClPlayer, 12),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            // ── 灌木斑块 + 草地斑块（上游两次 createPatches 参数相同）──
            for (int i = 0; i < 2; ++i)
                foreach (double patchSize in new[]
                {
                    RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                    RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
                })
                    RmgenLibrary.CreateAreas(rng,
                        new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                            patchSize, 0.5),
                        new IPainter[]
                        {
                            new TerrainPainter(tSecondary, rng),
                            new TileClassPainter(ClDirt),
                        },
                        RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClHill, 0, ClDirt, 5,
                            ClPlayer, 12),
                        RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            // ── 石矿/金属矿 ──
            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                        new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                    },
                    new IGroupElement[] { new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3) },
                },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20, ClRock, 18,
                    ClHill, 1),
                ClRock);

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][] { new IGroupElement[] { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) } },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20, ClMetal, 18,
                    ClRock, 5, ClHill, 1),
                ClMetal);

            // ── 装饰岩石（scaleByMapAreaAbsolute）──
            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                        new ScatterObject(rng, aRockMedium, 1, 3, 0, 1),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, settings.CircularMap),
                },
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 0));

            // ── 食物 ──
            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oWolf, 3, 5, 0, 3) },
                    new IGroupElement[] { new ScatterObject(rng, oRabbit, 6, 8, 0, 6) },
                    new IGroupElement[] { new ScatterObject(rng, oDeer, 3, 4, 0, 3) },
                    new IGroupElement[] { new ScatterObject(rng, oMuskox, 3, 4, 0, 3) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers, 3 * NumPlayers, 3 * NumPlayers },
                RmgenLibrary.AvoidClasses(clFood, 20, ClHill, 5, clWater, 5),
                clFood);

            // 鲸（避开食物 + 限水域）
            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oWhaleFin, 1, 1, 0, 3) },
                    new IGroupElement[] { new ScatterObject(rng, oWhaleHumpback, 1, 1, 0, 3) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 20),
                    RmgenLibrary.StayClasses(clWater, 6),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, oBerryBush, 5, 7, 0, 4) } },
                new double[] { rng.RandIntInclusive(1, 4) * NumPlayers + 2 },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, ClHill, 1,
                    clFood, 10),
                clFood);

            // 鱼群
            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, oFish, 2, 3, 0, 2) } },
                new double[] { 25 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 10),
                    RmgenLibrary.StayClasses(clWater, 6),
                }),
                clFood);

            // ── 散落树 ──
            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oBush, oBush2 },
                RmgenLibrary.AvoidClasses(clWater, 5, ClForest, 3, ClHill, 1, ClPlayer, 12,
                    ClMetal, 4, ClRock, 4),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }
    }
}
