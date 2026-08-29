using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>mainland 地图生成器（逐字移植 maps/random/mainland.js，215 行）。
    /// 纯陆地地图：起伏 + 丘陵/山脉 + 森林 + 泥土/草地补丁 + 金属/石矿 + 食物 + 装饰物。
    /// biome 驱动(rmbiome/defaultbiome.json + generic/<name>.json 覆盖 + .js 随机分支)。</summary>
    public static class MainlandMap
    {
        /// <summary>生成地图。返回 MapExport 供引擎消费。</summary>
        public static MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            var biome = settings.BiomeData
                ?? BiomeLoader.Load(settings.DataRoot, "generic/temperate", rng);

            string tMainTerrain = biome.MainTerrain0;
            string tForestFloor1 = biome.ForestFloor1;
            string tForestFloor2 = biome.ForestFloor2;
            string tCliff = biome.Cliff.Count > 0 ? biome.Cliff[0] : "medit_cliff_aegean";
            string tTier1Terrain = biome.Tier1Terrain;
            string tTier2Terrain = biome.Tier2Terrain;
            string tTier3Terrain = biome.Tier3Terrain;
            string tHill = biome.Hill.Count > 0 ? biome.Hill[0] : "medit_rocks_grass";
            string tRoad = biome.Road;
            string tRoadWild = biome.RoadWild;
            string tTier4Terrain = biome.Tier4Terrain;

            string oTree1 = biome.Tree1;
            string oTree2 = biome.Tree2;
            string oTree3 = biome.Tree3;
            string oTree4 = biome.Tree4;
            string oTree5 = biome.Tree5;
            string oFruitBush = biome.FruitBush;
            string oMainHuntableAnimal = biome.MainHuntableAnimal;
            string oSecondaryHuntableAnimal = biome.SecondaryHuntableAnimal;
            string oStoneLarge = biome.StoneLarge;
            string oStoneSmall = biome.StoneSmall;
            string oMetalLarge = biome.MetalLarge;
            string oMetalSmall = biome.MetalSmall;

            string aGrass = biome.Grass;
            string aGrassShort = biome.GrassShort;
            string aRockLarge = biome.RockLarge;
            string aRockMedium = biome.RockMedium;
            string aBushMedium = biome.BushMedium;
            string aBushSmall = biome.BushSmall;

            const string terrainSeparator = "|";   // 原版 TERRAIN_SEPARATOR("|"):地形|实体混合 token

            int mapSize = settings.Size;
            const double heightLand = 3;
            var map = new RandomMap(rng, mapSize, heightLand, tMainTerrain, settings.CircularMap);
            RmgenLibrary.CurrentMap = map;

            int numPlayers = RmgenCommon.GetNumPlayers(settings);

            // TileClass 创建(原版 createTileClass 八个:player/hill/forest/dirt/rock/metal/food/baseResource)
            var clPlayer = new TileClass(mapSize);
            var clHill = new TileClass(mapSize);
            var clForest = new TileClass(mapSize);
            var clDirt = new TileClass(mapSize);
            var clRock = new TileClass(mapSize);
            var clMetal = new TileClass(mapSize);
            var clFood = new TileClass(mapSize);
            var clBaseResource = new TileClass(mapSize);

            // 玩家基地放置(原版 placePlayerBases:基地浆果/矿/树线落点互不重叠)。
            RmgenCommon.PlacePlayerBases(rng, map, settings, tMainTerrain, clPlayer, biome,
                new RmgenCommon.PlayerBaseOptions
                {
                    StartingAnimalTemplate = biome.StartingAnimal,
                    BerriesTemplate = oFruitBush,
                    Mines = new List<(string Template, string? Type, object? Terrain)>
                    {
                        (oMetalLarge, null, null),
                        (oStoneLarge, null, null),
                    },
                    TreesTemplate = oTree1,
                    TreesCount = 5,
                });

            // 起伏
            RmgenCommon.CreateBumps(rng, map,
                RmgenLibrary.AvoidClasses(clPlayer, 20));

            // 丘陵/山脉(原版 randBool 二选一)
            if (rng.RandBool())
                RmgenCommon.CreateHills(rng, map, new[] { tCliff, tCliff, tHill },
                    RmgenLibrary.AvoidClasses(clPlayer, 20, clHill, 15), clHill,
                    count: (int)RmgenLibrary.ScaleByMapSize(3, 15, mapSize));
            else
                RmgenCommon.CreateMountains(rng, map, tCliff,
                    RmgenLibrary.AvoidClasses(clPlayer, 20, clHill, 15), clHill,
                    count: (int)RmgenLibrary.ScaleByMapSize(3, 15, mapSize));

            // 森林(原版 getTreeCounts + createDefaultForests:
            // pForest1/pForest2 是 terrain|entity 混合 token,层刷地皮 + 树)。
            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(
                biome.TreesMin, biome.TreesMax, biome.ForestProbability, mapSize);
            var pForest1 = new object[]
            {
                tForestFloor2 + terrainSeparator + oTree1,
                tForestFloor2 + terrainSeparator + oTree2,
                tForestFloor2,
            };
            var pForest2 = new object[]
            {
                tForestFloor1 + terrainSeparator + oTree4,
                tForestFloor1 + terrainSeparator + oTree5,
                tForestFloor1,
            };
            RmgenCommon.CreateDefaultForests(rng, map,
                new object[] { tMainTerrain, tForestFloor1, tForestFloor2, pForest1, pForest2 },
                RmgenLibrary.AvoidClasses(clPlayer, 20, clForest, 18, clHill, 0),
                clForest, forestTrees);

            // 泥土斑块(原版 createLayeredPatches:分层刷地皮破单调)。
            RmgenCommon.CreateLayeredPatches(rng, map,
                new[] { RmgenLibrary.ScaleByMapSize(3, 6, mapSize),
                        RmgenLibrary.ScaleByMapSize(5, 10, mapSize),
                        RmgenLibrary.ScaleByMapSize(8, 21, mapSize) },
                new object[] { new object[] { tMainTerrain, tTier1Terrain },
                               new object[] { tTier1Terrain, tTier2Terrain },
                               new object[] { tTier2Terrain, tTier3Terrain } },
                new[] { 1, 1 },
                RmgenLibrary.AvoidClasses(clForest, 0, clHill, 0, clDirt, 5, clPlayer, 12),
                (int)RmgenLibrary.ScaleByMapSize(15, 45, mapSize),
                clDirt);

            // 草地斑块(原版 createPatches)。
            RmgenCommon.CreatePatches(rng, map,
                new[] { RmgenLibrary.ScaleByMapSize(2, 4, mapSize),
                        RmgenLibrary.ScaleByMapSize(3, 7, mapSize),
                        RmgenLibrary.ScaleByMapSize(5, 15, mapSize) },
                tTier4Terrain,
                RmgenLibrary.AvoidClasses(clForest, 0, clHill, 0, clDirt, 5, clPlayer, 12),
                (int)RmgenLibrary.ScaleByMapSize(15, 45, mapSize),
                clDirt);

            // 金属矿 + 石矿(原版 createBalancedMetalMines/createBalancedStoneMines:
            // 大矿远散 + 小矿簇 + 随机小矿点三档)。
            GaiaEntities.CreateBalancedMetalMines(rng, map, numPlayers,
                oMetalSmall, oMetalLarge, clMetal,
                RmgenLibrary.AvoidClasses(clForest, 1, clPlayer,
                    RmgenLibrary.ScaleByMapSize(20, 35, mapSize), clHill, 1));
            GaiaEntities.CreateBalancedStoneMines(rng, map, numPlayers,
                oStoneSmall, oStoneLarge, clRock,
                RmgenLibrary.AvoidClasses(clForest, 1, clPlayer,
                    RmgenLibrary.ScaleByMapSize(20, 35, mapSize), clHill, 1, clMetal, 10));

            // 装饰物(原版 createDecoration:岩石/草/灌木散布)。
            double planetm = 1;   // 原版 biome=india 时 8;biome 自选由 BiomeData 承载,此处按默认
            GaiaEntities.CreateDecoration(rng,
                new[]
                {
                    new IGroupElement[] { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) },
                    new IGroupElement[] { new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                                         new ScatterObject(rng, aRockMedium, 1, 3, 0, 2) },
                    new IGroupElement[] { new ScatterObject(rng, aGrassShort, 1, 2, 0, 1) },
                    new IGroupElement[] { new ScatterObject(rng, aGrass, 2, 4, 0, 1.8),
                                         new ScatterObject(rng, aGrassShort, 3, 6, 1.2, 2.5) },
                    new IGroupElement[] { new ScatterObject(rng, aBushMedium, 1, 2, 0, 2),
                                         new ScatterObject(rng, aBushSmall, 2, 4, 0, 2) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, mapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, mapSize, settings.CircularMap),
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, mapSize, settings.CircularMap),
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, mapSize, settings.CircularMap),
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, mapSize, settings.CircularMap),
                },
                RmgenLibrary.AvoidClasses(clForest, 0, clPlayer, 10, clHill, 0));

            // 食物(原版 createFood:主猎物 + 次猎物 + 浆果丛)。
            GaiaEntities.CreateFood(rng,
                new[]
                {
                    new IGroupElement[] { new ScatterObject(rng, oMainHuntableAnimal, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, oSecondaryHuntableAnimal, 2, 3, 0, 2) },
                },
                new double[] { 3 * numPlayers, 3 * numPlayers },
                RmgenLibrary.AvoidClasses(clForest, 0, clPlayer, 20, clHill, 1,
                    clMetal, 4, clRock, 4, clFood, 20),
                clFood);
            GaiaEntities.CreateFood(rng,
                new[] { new IGroupElement[] { new ScatterObject(rng, oFruitBush, 5, 7, 0, 4) } },
                new double[] { 3 * numPlayers },
                RmgenLibrary.AvoidClasses(clForest, 0, clPlayer, 20, clHill, 1,
                    clMetal, 4, clRock, 4, clFood, 10),
                clFood);

            // 散树(原版 createStragglerTrees:森林外的单棵树)。
            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oTree1, oTree2, oTree4, oTree3 },
                RmgenLibrary.AvoidClasses(clForest, 8, clHill, 1, clPlayer, 12,
                    clMetal, 6, clRock, 6, clFood, 1),
                clForest, stragglerTrees);

            return map.MakeExportable();
        }
    }

    /// <summary>continent 地图生成器（骨架）。
    /// 原版在 mainland 基础上加海洋生成。</summary>
    public static class ContinentMap
    {
        public static MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            // 骨架：复用 MainlandMap（完整版需海洋 + 海岸生成）
            return MainlandMap.Generate(rng, settings);
        }
    }
}
