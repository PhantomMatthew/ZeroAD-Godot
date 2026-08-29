using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>撒哈拉的绿洲地图（逐字移植 maps/random/saharan_oases.js,258 行)。
    /// 沙漠地形 + 玩家旁绿洲(凹陷水域+棕榈林)+ 矿/食物/宝物环绕绿洲。</summary>
    public sealed class SaharanOasesMap : StandardMap
    {
        protected override double HeightLand => 1;
        protected override string BaseTerrain => "desert_sand_dunes_100";

        /// <summary>生成地图。返回 MapExport 供引擎消费。</summary>
        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            // 地形(原版 saharan_oases.js 的 t* 常量,沙漠硬编不走 biome)。
            const string tPrimary = "desert_sand_dunes_100";
            const string tCity = "desert_city_tile";
            const string tCityPlaza = "desert_city_tile_plaza";
            const string tFineSand = "desert_sand_smooth";
            const string tDirt1 = "desert_dirt_rough_2";
            const string tSandDunes = "desert_sand_dunes_50";
            const string tDirt2 = "desert_dirt_rough";
            const string tDirtCracks = "desert_dirt_cracks";
            const string tShore = "desert_shore_stones";
            const string tWaterDeep = "desert_shore_stones_wet";
            const string tLush = "desert_grass_a";
            const string tSLush = "desert_grass_a_sand";

            const string oGrapeBush = "gaia/fruit/grapes";
            const string oCamel = "gaia/fauna_camel";
            const string oGazelle = "gaia/fauna_gazelle";
            const string oGoat = "gaia/fauna_goat";
            const string oStoneLarge = "gaia/rock/badlands_large";
            const string oStoneSmall = "gaia/rock/desert_small";
            const string oMetalLarge = "gaia/ore/desert_large";
            const string oDatePalm = "gaia/tree/date_palm";
            const string oSDatePalm = "gaia/tree/cretan_date_palm_short";
            const string oWoodTreasure = "gaia/treasure/wood";
            const string oFoodTreasure = "gaia/treasure/food_bin";

            const string aBush1 = "actor|props/flora/bush_desert_a.xml";
            const string aBush2 = "actor|props/flora/bush_desert_dry_a.xml";
            const string aBush3 = "actor|props/flora/bush_medit_sm_dry.xml";
            const string aBush4 = "actor|props/flora/plant_desert_a.xml";
            const string aDecorativeRock = "actor|geology/stone_desert_med.xml";

            const string terrainSeparator = "|";
            var pForest = new object[]
            {
                tLush + terrainSeparator + oDatePalm,
                tLush + terrainSeparator + oSDatePalm,
                tLush,
            };

            const double heightLand = 1;
            const double heightOffsetOasis = -3;

            int mapSize = settings.Size;
            var map = new RandomMap(rng, mapSize, heightLand, tPrimary, settings.CircularMap);
            RmgenLibrary.CurrentMap = map;

            int numPlayers = RmgenCommon.GetNumPlayers(settings);
            var mapCenter = map.GetCenter();

            // TileClass(原版九个)。
            var clPlayer = new TileClass(mapSize);
            var clForest = new TileClass(mapSize);
            var clWater = new TileClass(mapSize);
            var clDirt = new TileClass(mapSize);
            var clRock = new TileClass(mapSize);
            var clMetal = new TileClass(mapSize);
            var clFood = new TileClass(mapSize);
            var clBaseResource = new TileClass(mapSize);
            var clTreasure = new TileClass(mapSize);

            // 玩家布置(原版 playerPlacementCircle)。
            var (playerIDs, playerPosition, playerAngle, startAngle) =
                RmgenCommon.PlayerPlacementCircle(rng, map, numPlayers,
                    RmgenLibrary.ScaleByMapSize(0.35 * mapSize, 0.35 * mapSize, mapSize));

            // 玩家基地(原版 placePlayerBases:城市贴图 city/cityPlaza,
            // 浆果葡萄丛,矿大金属/大石头,树短枣椰,装饰 desert bush)。
            RmgenCommon.PlacePlayerBases(rng, map, settings, tPrimary, clPlayer, null,
                playerPosition, tCityPlaza, tCity, playerIDs, options:
                new RmgenCommon.PlayerBaseOptions
                {
                    StartingAnimalTemplate = "gaia/fauna_chicken",
                    BerriesTemplate = oGrapeBush,
                    Mines = new List<(string Template, string? Type, object? Terrain)>
                    {
                        (oMetalLarge, null, null),
                        (oStoneLarge, null, null),
                    },
                    TreesTemplate = oSDatePalm,
                    TreesCount = 5,
                });

            // 绿洲(原版:玩家位置对角 oasisRadius 处 ClumpPlacer 凹陷水域+棕榈林)。
            double oasisRadius = RmgenLibrary.ScaleByMapSize(0.19 * mapSize, 0.22 * mapSize, mapSize);
            for (int i = 0; i < numPlayers; ++i)
            {
                var off = new RmgenVector2D(oasisRadius, 0);
                off.Rotate(-playerAngle[i]);
                var position = RmgenVector2D.Add(mapCenter, off);
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng,
                        (int)(RmgenLibrary.ScaleByMapSize(16, 60, mapSize) * 0.185),
                        0.6, 0.15, 0, position),
                    new IPainter[]
                    {
                        new LayeredPainter(
                            new object[] { tSLush, new object[] { tLush, pForest },
                                           new object[] { tLush, pForest }, tShore, tShore, tWaterDeep },
                            new[] { 2, 2, 1, 3, 1 }, rng),
                        new SmoothElevationPainter(rng,
                            SmoothElevationPainter.SmoothType.Blurry,
                            heightOffsetOasis, 10, relative: true),
                        new TileClassPainter(clWater),
                    },
                    null);
            }

            // 草地斑块(原版三层 ClumpPlacer + LayeredPainter 分层刷地皮)。
            foreach (var size in new[] { RmgenLibrary.ScaleByMapSize(3, 48, mapSize),
                                         RmgenLibrary.ScaleByMapSize(5, 84, mapSize),
                                         RmgenLibrary.ScaleByMapSize(8, 128, mapSize) })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, (int)size, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(
                            new object[] { new object[] { tDirt1, tSandDunes },
                                           new object[] { tSandDunes, tDirt2 },
                                           new object[] { tDirt2, tDirt1 } },
                            new[] { 1, 1 }, rng),
                        new TileClassPainter(clDirt),
                    },
                    RmgenLibrary.AvoidClasses(clForest, 0, clPlayer, 0, clWater, 1, clDirt, 5),
                    (int)RmgenLibrary.ScaleByMapSize(15, 45, mapSize));

            // 泥土斑块(原版三层 ClumpPlacer + LayeredPainter)。
            foreach (var size in new[] { RmgenLibrary.ScaleByMapSize(3, 48, mapSize),
                                         RmgenLibrary.ScaleByMapSize(5, 84, mapSize),
                                         RmgenLibrary.ScaleByMapSize(8, 128, mapSize) })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, (int)size, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(
                            new object[] { new object[] { tDirt2, tDirtCracks },
                                           new object[] { tDirt2, tFineSand },
                                           new object[] { tDirtCracks, tFineSand } },
                            new[] { 1, 1 }, rng),
                        new TileClassPainter(clDirt),
                    },
                    RmgenLibrary.AvoidClasses(clForest, 0, clDirt, 5, clPlayer, 0, clWater, 1),
                    (int)RmgenLibrary.ScaleByMapSize(15, 45, mapSize));

            // 石矿(原版 ObjectGroup 整组试放:小矿 0-2 + 大矿 1)。
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                }, avoidSelf: true, tileClass: clRock),
                0, RmgenLibrary.AvoidClasses(clForest, 1, clPlayer, 26, clRock, 10, clWater, 1),
                2 * RmgenLibrary.ScaleByMapSize(4, 16, mapSize), 100);

            // 小石场(原版:小矿 2-5)。
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3),
                }, avoidSelf: true, tileClass: clRock),
                0, RmgenLibrary.AvoidClasses(clForest, 1, clPlayer, 26, clRock, 10, clWater, 1),
                2 * RmgenLibrary.ScaleByMapSize(4, 16, mapSize), 100);

            // 金属矿(原版:大矿 1)。
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4),
                }, avoidSelf: true, tileClass: clMetal),
                0, RmgenLibrary.AvoidClasses(clForest, 1, clPlayer, 26, clMetal, 10,
                    clRock, 5, clWater, 1),
                2 * RmgenLibrary.ScaleByMapSize(4, 16, mapSize), 100);

            // 小装饰岩石(原版)。
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aDecorativeRock, 1, 3, 0, 1),
                }, avoidSelf: true),
                0, RmgenLibrary.AvoidClasses(clWater, 1, clForest, 0, clPlayer, 0),
                RmgenLibrary.ScaleByMapSize(16, 262, mapSize), 50);

            // 灌木(原版:desert dry bush ×3 种散布)。
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aBush2, 1, 2, 0, 1),
                    new ScatterObject(rng, aBush1, 1, 3, 0, 2),
                    new ScatterObject(rng, aBush4, 1, 2, 0, 1),
                    new ScatterObject(rng, aBush3, 1, 3, 0, 2),
                }, avoidSelf: true),
                0, RmgenLibrary.AvoidClasses(clWater, 1, clPlayer, 0),
                RmgenLibrary.ScaleByMapSize(10, 100, mapSize), 50);

            // 矿点小岩石(原版 stayClasses:矿区内散布)。
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aDecorativeRock, 1, 3, 0, 1),
                }, avoidSelf: true),
                0, RmgenLibrary.StayClasses(clRock, 0),
                5 * RmgenLibrary.ScaleByMapSize(16, 262, mapSize), 50);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aDecorativeRock, 1, 3, 0, 1),
                }, avoidSelf: true),
                0, RmgenLibrary.StayClasses(clMetal, 0),
                5 * RmgenLibrary.ScaleByMapSize(16, 262, mapSize), 50);

            // 瞪羚/山羊/骆驼/宝物(原版:borderClasses 水边散布)。
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oGazelle, 5, 7, 0, 4),
                }, avoidSelf: true, tileClass: clFood),
                0, RmgenLibrary.BorderClasses(clWater, 8, 5),
                6 * RmgenLibrary.ScaleByMapSize(5, 20, mapSize), 50);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oGoat, 2, 4, 0, 3),
                }, avoidSelf: true, tileClass: clFood),
                0, RmgenLibrary.BorderClasses(clWater, 8, 5),
                5 * RmgenLibrary.ScaleByMapSize(5, 20, mapSize), 50);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oFoodTreasure, 1, 1, 0, 2),
                }, avoidSelf: true, tileClass: clTreasure),
                0, RmgenLibrary.BorderClasses(clWater, 8, 5),
                3 * RmgenLibrary.ScaleByMapSize(5, 20, mapSize), 50);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oWoodTreasure, 1, 1, 0, 2),
                }, avoidSelf: true, tileClass: clTreasure),
                0, RmgenLibrary.BorderClasses(clWater, 8, 5),
                3 * RmgenLibrary.ScaleByMapSize(5, 20, mapSize), 50);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oCamel, 2, 4, 0, 2),
                }, avoidSelf: true, tileClass: clFood),
                0, RmgenLibrary.BorderClasses(clWater, 14, 5),
                5 * RmgenLibrary.ScaleByMapSize(5, 20, mapSize), 50);

            return map.MakeExportable();
        }
    }
}
