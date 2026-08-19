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

            object main = terrainSet[0], ff1 = terrainSet[1], ff2 = terrainSet[2];
            object tree1 = terrainSet[3], tree2 = terrainSet[4];

            var variants = new (object[] border, object[] interior)[]
            {
                (new object[] { ff2, main, tree1 }, new object[] { ff2, tree1 }),
                (new object[] { ff1, main, tree2 }, new object[] { ff1, tree2 }),
            };

            double numberOfTrees = treeCount;
            int numberOfForests = (int)Math.Floor(numberOfTrees /
                (RmgenLibrary.ScaleByMapSize(3, 6, map.GetSize()) * numPlayers * variants.Length));
            if (numberOfForests == 0)
                return;

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
    }
}
