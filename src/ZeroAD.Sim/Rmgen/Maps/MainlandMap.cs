using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>mainland 地图生成器（逐字移植 maps/random/mainland.js，215 行）。
    /// 纯陆地地图：起伏 + 丘陵/山脉 + 森林 + 泥土/草地补丁 + 金属/石矿 + 食物 + 装饰物。
    /// 骨架版——核心流程移植，依赖未完整移植的辅助函数标 TODO。</summary>
    public static class MainlandMap
    {
        /// <summary>生成地图。返回 MapExport 供引擎消费。</summary>
        public static MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            const double heightLand = 3;
            const string tMainTerrain = "medit_grass_field";
            const string tCliff = "medit_cliff_aegean";
            const string tHill = "medit_rocks_grass";

            int mapSize = settings.Size;
            var map = new RandomMap(rng, mapSize, heightLand, tMainTerrain, settings.CircularMap);
            RmgenLibrary.CurrentMap = map;

            int numPlayers = RmgenCommon.GetNumPlayers(settings);

            // TileClass 创建
            var clPlayer = new TileClass(mapSize);
            var clHill = new TileClass(mapSize);
            var clForest = new TileClass(mapSize);
            var clDirt = new TileClass(mapSize);
            var clRock = new TileClass(mapSize);
            var clMetal = new TileClass(mapSize);

            // 玩家基地放置（骨架：均匀分布 CC）
            RmgenCommon.PlacePlayerBases(rng, map, settings, tMainTerrain, clPlayer);

            // 起伏
            RmgenCommon.CreateBumps(rng, map,
                RmgenLibrary.AvoidClasses(clPlayer, 20));

            // 丘陵/山脉
            if (rng.RandBool())
                RmgenCommon.CreateHills(rng, map, new[] { tCliff, tCliff, tHill },
                    RmgenLibrary.AvoidClasses(clPlayer, 20, clHill, 15), clHill,
                    count: (int)RmgenLibrary.ScaleByMapSize(3, 15, mapSize));
            else
                RmgenCommon.CreateMountains(rng, map, tCliff,
                    RmgenLibrary.AvoidClasses(clPlayer, 20, clHill, 15), clHill,
                    count: (int)RmgenLibrary.ScaleByMapSize(3, 15, mapSize));

            // 森林（骨架）
            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(
                500, 3000, 0.7, mapSize);
            // TODO: CreateDefaultForests（依赖 LayeredPainter + 未移植的 forest 变体逻辑）

            // 金属矿 + 石矿（骨架）
            // TODO: CreateBalancedMetalMines / CreateBalancedStoneMines

            // 食物 + 装饰物（骨架）
            // TODO: CreateFood / CreateDecoration / CreateStragglerTrees

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
