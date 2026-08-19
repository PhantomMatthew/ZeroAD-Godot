using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>aegean_sea.js（347 行）——爱琴海：中央海峡把玩家分两列（playerPlacementRiver），
    /// 海中散布珊瑚岛（clIsland 上的矿/树密度极高）。
    /// 无 biome（全部内联常量）；基底贴图 tHill 名单逐图块 pickRandom（RandomMap 名单构造器）。
    /// 环境设置（天色/水色/雾/PP）与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class AegeanSeaMap : StandardMap
    {
        private const string tCity = "medit_city_pavement";
        private const string tCityPlaza = "medit_city_pavement";
        private static readonly string[] tHill =
        {
            "medit_grass_shrubs", "medit_rocks_grass_shrubs", "medit_rocks_shrubs",
            "medit_rocks_grass", "medit_shrubs",
        };
        private const string tMainDirt = "medit_dirt";
        private const string tCliff = "medit_cliff_aegean";
        private const string tForestFloor = "medit_grass_shrubs";
        private const string tGrass = "medit_grass_field";
        private const string tGrassSand50 = "medit_grass_field_a";
        private const string tGrassSand25 = "medit_grass_field_b";
        private const string tDirt = "medit_dirt_b";
        private const string tDirt2 = "medit_rocks_grass";
        private const string tDirt3 = "medit_rocks_shrubs";
        private const string tDirtCracks = "medit_dirt_c";
        private const string tShoreUpper = "medit_sand";
        private const string tShoreLower = "medit_sand_wet";
        private const string tCoralsUpper = "medit_sea_coral_plants";
        private const string tCoralsLower = "medit_sea_coral_deep";
        private const string tSeaDepths = "medit_sea_depths";

        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oDeer = "gaia/fauna_deer";
        private const string oFish = "gaia/fish/generic";
        private const string oSheep = "gaia/fauna_sheep";
        private const string oGoat = "gaia/fauna_goat";
        private const string oStoneLarge = "gaia/rock/mediterranean_large";
        private const string oStoneSmall = "gaia/rock/mediterranean_small";
        private const string oMetalLarge = "gaia/ore/mediterranean_large";
        private const string oDatePalm = "gaia/tree/cretan_date_palm_short";
        private const string oSDatePalm = "gaia/tree/cretan_date_palm_tall";
        private const string oCarob = "gaia/tree/carob";
        private const string oFanPalm = "gaia/tree/medit_fan_palm";
        private const string oPoplar = "gaia/tree/poplar_lombardy";
        private const string oCypress = "gaia/tree/cypress";

        private const string aBush1 = "actor|props/flora/bush_medit_sm.xml";
        private const string aBush2 = "actor|props/flora/bush_medit_me.xml";
        private const string aBush3 = "actor|props/flora/bush_medit_la.xml";
        private const string aBush4 = "actor|props/flora/bush_medit_me.xml";
        private const string aDecorativeRock = "actor|geology/stone_granite_med.xml";

        protected override double HeightLand => 1;   // heightShore

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tHill);
            var map = Map;

            const double heightSeaGround = -3;
            const double heightSeaBump = -2.5;
            const double heightCorralsLower = -2;
            const double heightCorralsUpper = -1.5;
            const double heightShore = 1;
            const double heightLand = 2;
            const double heightIsland = 6;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clGrass = new TileClass(MapSize);
            var clIsland = new TileClass(MapSize);

            var mapCenter = map.GetCenter();
            int mapSize = MapSize;

            double startAngle = rng.RandomAngle();

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementRiver(
                rng, map, settings, startAngle, RmgenLibrary.FractionToTiles(0.6, mapSize));
            RmgenCommon.PlacePlayerBases(rng, map, settings, tHill[0], ClPlayer, null,
                playerPosition, tCityPlaza, tCity, playerIDs);

            // ── 中央海峡（paintRiver：双正弦蜿蜒，deviation=0 仍逐格抽数）──
            var riverStart = new RmgenVector2D(mapCenter.X, mapSize);   // mapBounds.top
            riverStart.RotateAround(startAngle, mapCenter);
            var riverEnd = new RmgenVector2D(mapCenter.X, 0);           // mapBounds.bottom
            riverEnd.RotateAround(startAngle, mapCenter);
            RmgenCommon.PaintRiver(rng, map, riverStart, riverEnd,
                RmgenLibrary.FractionToTiles(0.35, mapSize),
                RmgenLibrary.ScaleByMapSize(6, 25, mapSize),
                heightSeaGround, heightLand,
                parallel: false, deviation: 0, meanderShort: 20, meanderLong: 0);

            RmgenLibrary.PaintTileClassBasedOnHeight(double.NegativeInfinity, 0.7,
                HeightPlacer.Mode.ExcludeMinExcludeMax, clWater);

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, double.NegativeInfinity, heightShore,
                HeightPlacer.Mode.ExcludeMinExcludeMax, tShoreLower);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightShore, heightLand,
                HeightPlacer.Mode.ExcludeMinExcludeMax, tShoreUpper);

            // ── 起伏（createBumps 默认参数）──
            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, mapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, mapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, 2, 2,
                        relative: true),
                },
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 20),
                RmgenLibrary.ScaleByMapSize(100, 200, mapSize));

            // ── 森林 ──
            double forestTrees = 0.7 * RmgenLibrary.ScaleByMapSize(500, 3000, mapSize);
            double stragglerTrees = (1 - 0.7) * RmgenLibrary.ScaleByMapSize(500, 3000, mapSize);
            var pForest = new[]
            {
                tForestFloor,
                tForestFloor + "|" + oCarob,
                tForestFloor + "|" + oDatePalm,
                tForestFloor + "|" + oSDatePalm,
                tForestFloor,
            };
            GaiaEntities.CreateForests(rng, map,
                new object[] { tForestFloor, tForestFloor, tForestFloor, pForest, pForest },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 17, clWater, 2, clBaseResource, 3),
                ClForest, forestTrees, NumPlayers);

            // ── 丘陵或山脉（randBool 二选一）──
            if (rng.RandBool())
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, mapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, mapSize)), 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tGrass, tCliff, tHill }, new[] { 1, 2 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, 18, 2),
                        new TileClassPainter(ClHill),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 1, ClHill, 15, clWater, 3),
                    RmgenLibrary.ScaleByMapSize(3, 15, mapSize));
            else
                RmgenCommon.CreateMountains(rng, map, tCliff,
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 1, ClHill, 15, clWater, 3),
                    ClHill, count: (int)RmgenLibrary.ScaleByMapSize(3, 15, mapSize));

            // ── 草地分层斑块 ──
            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 6, mapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, mapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, mapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, mapSize)),
                        patchSize, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new object[] { tGrass, tGrassSand50 },
                            new object[] { tGrassSand50, tGrassSand25 },
                            new object[] { tGrassSand25, tGrass },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, clGrass, 2, ClPlayer, 10, clWater, 2,
                        ClDirt, 2, ClHill, 1),
                    RmgenLibrary.ScaleByMapSize(15, 45, mapSize));

            // ── 泥地分层斑块 ──
            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 6, mapSize),
                RmgenLibrary.ScaleByMapSize(5, 10, mapSize),
                RmgenLibrary.ScaleByMapSize(8, 21, mapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, mapSize)),
                        patchSize, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            tDirt3, tDirt2,
                            new object[] { tDirt, tMainDirt },
                            new object[] { tDirtCracks, tMainDirt },
                        }, new[] { 1, 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClDirt, 2, ClPlayer, 10, clWater, 2,
                        clGrass, 2, ClHill, 1),
                    RmgenLibrary.ScaleByMapSize(15, 45, mapSize));

            // ── 海底隆起 ──
            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, mapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, mapSize)), 0.5),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightSeaBump, 3),
                },
                RmgenLibrary.StayClasses(clWater, 6),
                RmgenLibrary.ScaleByMapSize(10, 50, mapSize));

            // ── 岛屿 ──
            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, mapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(30, 80, mapSize)), 0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tShoreLower, tShoreUpper, tHill },
                        new[] { 2, 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightIsland, 4),
                    new TileClassPainter(clIsland),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 8, ClForest, 1, clIsland, 15),
                    RmgenLibrary.StayClasses(clWater, 6),
                }),
                RmgenLibrary.ScaleByMapSize(1, 4, mapSize) * NumPlayers);

            // ── 海沟/珊瑚按高度刷漆 ──
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, double.NegativeInfinity, heightSeaGround,
                HeightPlacer.Mode.IncludeMinIncludeMax, tSeaDepths);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightSeaGround, heightCorralsLower,
                HeightPlacer.Mode.ExcludeMinIncludeMax, tCoralsLower);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightCorralsLower, heightCorralsUpper,
                HeightPlacer.Mode.ExcludeMinIncludeMax, tCoralsUpper);

            // ── 岛上矿 ──
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
                RmgenLibrary.StayClasses(clIsland, 4), ClRock);

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][] { new IGroupElement[] { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) } },
                RmgenLibrary.StayClasses(clIsland, 4), ClMetal);

            // ── 陆地矿 ──
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
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClRock, 10, clWater, 1, ClHill, 1),
                ClRock);

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][] { new IGroupElement[] { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) } },
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClMetal, 10, ClRock, 5,
                    clWater, 1, ClHill, 1),
                ClMetal);

            // ── 装饰 ──
            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, aDecorativeRock, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aBush2, 1, 2, 0, 1),
                        new ScatterObject(rng, aBush1, 1, 3, 0, 2),
                        new ScatterObject(rng, aBush4, 1, 2, 0, 1),
                        new ScatterObject(rng, aBush3, 1, 3, 0, 2),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, mapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapSize(40, 360, mapSize),
                },
                RmgenLibrary.AvoidClasses(clWater, 2, ClForest, 0, ClPlayer, 0, ClHill, 1));

            // ── 食物 ──
            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, oFish, 2, 3, 0, 2) } },
                new[] { 25 * RmgenLibrary.ScaleByMapSize(15, 20, mapSize) },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clIsland, 2, clFood, 10),
                    RmgenLibrary.StayClasses(clWater, 5),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oSheep, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, oGoat, 2, 4, 0, 3) },
                    new IGroupElement[] { new ScatterObject(rng, oDeer, 2, 4, 0, 2) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(5, 20, mapSize),
                    RmgenLibrary.ScaleByMapSize(5, 20, mapSize),
                    RmgenLibrary.ScaleByMapSize(5, 20, mapSize),
                },
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 8, clBaseResource, 4, clWater, 1,
                    clFood, 10, ClHill, 1),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, oBerryBush, 5, 7, 0, 4) } },
                new double[] { 3 * NumPlayers },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10),
                clFood);

            // ── 散落树（陆地 + 岛上 10 倍密度）──
            var types = new[] { oDatePalm, oSDatePalm, oCarob, oFanPalm, oPoplar, oCypress };
            GaiaEntities.CreateStragglerTrees(rng, types,
                RmgenLibrary.AvoidClasses(ClForest, 1, clWater, 2, ClPlayer, 12, ClMetal, 6, ClHill, 1),
                ClForest, stragglerTrees);
            GaiaEntities.CreateStragglerTrees(rng, types,
                RmgenLibrary.StayClasses(clIsland, 4),
                ClForest, stragglerTrees * 10);

            return map.MakeExportable();
        }
    }
}
