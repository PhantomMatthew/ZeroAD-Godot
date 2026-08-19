using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Common
{
    /// <summary>gaia_entities.js 忠实移植（createMines/createFood/createDecoration/createStragglerTrees）。
    /// 与 RmgenCommon 里的简化版并存——简化版供已移植旧图使用不得改动；
    /// 新移植地图用本类，重试参数与 RNG 消耗顺序与上游一致。
    /// 注意上游 objects 参数是"数组的数组"（每种资源一组 SimpleObject）。</summary>
    public static class GaiaEntities
    {
        /// <summary>createMines(objects, constraint, tileClass, count)——逐组放置矿藏。
        /// count=0 表示上游缺省 scaleByMapSize(4, 16)（浮点数量由 retryPlacing 等效 ceil）。</summary>
        public static void CreateMines(RmgenRng rng, RandomMap map,
            IReadOnlyList<IReadOnlyList<IGroupElement>> objects, IConstraint? constraint,
            TileClass? tileClass, double count = 0)
        {
            foreach (var obj in objects)
                RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                    new ObjectGroup(obj, avoidSelf: true, tileClass: tileClass),
                    0, constraint,
                    count > 0 ? count : RmgenLibrary.ScaleByMapSize(4, 16, map.GetSize()),
                    70);
        }

        /// <summary>createFood(objects, counts, constraint, tileClass)——retryFactor=50。</summary>
        public static void CreateFood(RmgenRng rng,
            IReadOnlyList<IReadOnlyList<IGroupElement>> objects, IReadOnlyList<double> counts,
            IConstraint? constraint, TileClass? tileClass)
        {
            for (int i = 0; i < objects.Count; ++i)
                RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                    new ObjectGroup(objects[i], avoidSelf: true, tileClass: tileClass),
                    0, constraint, counts[i], 50);
        }

        /// <summary>createDecoration(objects, counts, constraint)——retryFactor=5，不标 TileClass。</summary>
        public static void CreateDecoration(RmgenRng rng,
            IReadOnlyList<IReadOnlyList<IGroupElement>> objects, IReadOnlyList<double> counts,
            IConstraint? constraint)
        {
            for (int i = 0; i < objects.Count; ++i)
                RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                    new ObjectGroup(objects[i], avoidSelf: true),
                    0, constraint, counts[i], 5);
        }

        /// <summary>createStragglerTrees(templateNames, constraint, tileClass, treeCount, retryFactor)。
        /// 每种树 Math.floor(treeCount / 种数) 组，每组 1 棵、散布半径 0..3。</summary>
        public static void CreateStragglerTrees(RmgenRng rng,
            IReadOnlyList<string> templateNames, IConstraint? constraint, TileClass tileClass,
            double treeCount, int retryFactor = 10)
        {
            foreach (string templateName in templateNames)
                RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                    new ObjectGroup(
                        new IGroupElement[] { new ScatterObject(rng, templateName, 1, 1, 0, 3) },
                        avoidSelf: true, tileClass: tileClass),
                    0, constraint, Math.Floor(treeCount / templateNames.Count), retryFactor);
        }

        /// <summary>createForests（gaia_entities.js 数字 treeCount 版）——
        /// 总树数拆 numberOfForests（floor(treeCount / (scaleByMapSize(3,6) * 玩家数 * 2))），
        /// 两种森林变体（border 三层 / interior 两层，LayeredPainter widths=[2]）。
        /// terrainSet = [mainTerrain, forestFloor1, forestFloor2, tree1, tree2]，
        /// 元素可为 string 或 string[]（"ff|tree" 混合名单）。</summary>
        public static void CreateForests(RmgenRng rng, RandomMap map,
            IReadOnlyList<object> terrainSet, IConstraint? constraint, TileClass tileClass,
            double treeCount, int numPlayers, int retryFactor = 10)
        {
            if (treeCount == 0)
                return;

            double numberOfForests = Math.Floor(treeCount /
                (RmgenLibrary.ScaleByMapSize(3, 6, map.GetSize()) * numPlayers * 2));
            CreateForestsCore(rng, map, terrainSet, constraint, tileClass,
                treeCount, numberOfForests, retryFactor);
        }

        /// <summary>createForests（对象 treeCount 版：nbForests + treesPerForest 分别给定）——
        /// numberOfTrees = nbForests * treesPerForest（两者均可浮点）。</summary>
        public static void CreateForests(RmgenRng rng, RandomMap map,
            IReadOnlyList<object> terrainSet, IConstraint? constraint, TileClass tileClass,
            double numberOfForests, double treesPerForest, int retryFactor)
            => CreateForestsCore(rng, map, terrainSet, constraint, tileClass,
                numberOfForests * treesPerForest, numberOfForests, retryFactor);

        /// <summary>createDefaultForests——g_DefaultNumberOfForests = scaleByMapSize(8, 36)。</summary>
        public static void CreateDefaultForests(RmgenRng rng, RandomMap map,
            IReadOnlyList<object> terrainSet, IConstraint? constraint, TileClass tileClass,
            double totalNumberOfTrees)
        {
            double numberOfForests = RmgenLibrary.ScaleByMapSize(8, 36, map.GetSize());
            CreateForestsCore(rng, map, terrainSet, constraint, tileClass,
                totalNumberOfTrees, numberOfForests, 10);
        }

        private static void CreateForestsCore(RmgenRng rng, RandomMap map,
            IReadOnlyList<object> terrainSet, IConstraint? constraint, TileClass tileClass,
            double numberOfTrees, double numberOfForests, int retryFactor)
        {
            if (numberOfForests == 0)
                return;

            object main = terrainSet[0], ff1 = terrainSet[1], ff2 = terrainSet[2];
            object tree1 = terrainSet[3], tree2 = terrainSet[4];

            var variants = new (object[] border, object[] interior)[]
            {
                (new object[] { ff2, main, tree1 }, new object[] { ff2, tree1 }),
                (new object[] { ff1, main, tree2 }, new object[] { ff1, tree2 }),
            };

            foreach (var v in variants)
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, map.GetSize())),
                        numberOfTrees / numberOfForests, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { v.border, v.interior }, new[] { 2 }, rng),
                        new TileClassPainter(tileClass),
                    },
                    constraint, numberOfForests, retryFactor);
        }

        /// <summary>createBalancedMines——大矿远散 + 小矿簇 + 随机小矿点三档，
        /// randomness∈(0,1) 时三个数量各乘一次 randFloat(1±r) 再 Math.round（3 次抽数）。
        /// 注意走非 deprecated 的 createObjectGroups（失败重试）。</summary>
        public static void CreateBalancedMines(RmgenRng rng, RandomMap map,
            string oSmall, string oLarge, TileClass clMine, IConstraint constraint,
            double largeCount, double smallCount, double randomSmallCount, double randomness)
        {
            int mapSize = map.GetSize();
            if (randomness > 0 && randomness < 1)
            {
                largeCount = SafeMath.Round(largeCount * rng.RandFloat(1 - randomness, 1 + randomness));
                smallCount = SafeMath.Round(smallCount * rng.RandFloat(1 - randomness, 1 + randomness));
                randomSmallCount = SafeMath.Round(randomSmallCount * rng.RandFloat(1 - randomness, 1 + randomness));
            }

            // 大矿彼此远散
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oLarge, 1, 1, 0, 1) },
                    avoidSelf: true, tileClass: clMine),
                0, new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clMine, RmgenLibrary.ScaleByMapSize(25, 50, mapSize)),
                    constraint,
                }),
                largeCount, 100);

            // 小矿簇
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oSmall, 2, 3, 0, 2) },
                    avoidSelf: true, tileClass: clMine),
                0, new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clMine, RmgenLibrary.ScaleByMapSize(18, 35, mapSize)),
                    constraint,
                }),
                smallCount, 50);

            // 随机小矿点（偶发形成好矿点）
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oSmall, 1, 2, 0, 2) },
                    avoidSelf: true, tileClass: clMine),
                0, new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clMine, 5),
                    constraint,
                }),
                randomSmallCount, 50);
        }

        /// <summary>createBalancedMetalMines（counts=1.0, randomness=0.05 缺省）。</summary>
        public static void CreateBalancedMetalMines(RmgenRng rng, RandomMap map, int numPlayers,
            string oSmall, string oLarge, TileClass clMine, IConstraint constraint,
            double counts = 1.0, double randomness = 0.05)
            => CreateBalancedMines(rng, map, oSmall, oLarge, clMine, constraint,
                Math.Max(RmgenLibrary.ScaleByMapSize(1, 9, map.GetSize()),
                    numPlayers * 1.8 - 0.8) * counts,
                RmgenLibrary.ScaleByMapSize(4, 12, map.GetSize()) * counts,
                RmgenLibrary.ScaleByMapSize(1, 8, map.GetSize()) * counts,
                randomness);

        /// <summary>createBalancedStoneMines（石矿总量略少于金属矿）。</summary>
        public static void CreateBalancedStoneMines(RmgenRng rng, RandomMap map, int numPlayers,
            string oSmall, string oLarge, TileClass clMine, IConstraint constraint,
            double counts = 1.0, double randomness = 0.05)
            => CreateBalancedMines(rng, map, oSmall, oLarge, clMine, constraint,
                Math.Max(RmgenLibrary.ScaleByMapSize(1, 9, map.GetSize()),
                    numPlayers * 1.25) * counts,
                RmgenLibrary.ScaleByMapSize(1, 8, map.GetSize()) * counts,
                RmgenLibrary.ScaleByMapSize(1, 8, map.GetSize()) * counts,
                randomness);
    }
}
