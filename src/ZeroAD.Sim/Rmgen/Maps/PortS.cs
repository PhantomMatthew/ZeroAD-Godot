using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>continent.js（逐字移植，291 行）——整图起手是海（基底 water、高度 -5），
    /// 先在图心用一条巨型 ChainPlacer 抬出一整块大陆，再保证每个玩家脚下有陆地，
    /// 最后按高度分带刷 主地形/岸线/水面。
    ///
    /// 注意：本图与 mainland.js 只有开头这段"造大陆"不同，后半段资源流程一致——
    /// 但此前它被错当成 mainland 的同义词（ContinentMap2 只覆盖参数、跑基类 Generate），
    /// 于是整张图退化成没有海的普通大陆图。
    ///
    /// placePlayersNomad 按既有移植约定省略；环境设置（setWaterWaviness/setWaterType）
    /// 已由 MapEnvironments 表驱动施加。</summary>
    public sealed class ContinentMap2 : StandardMap
    {
        private const double HeightSeaGround = -5;
        private const double HeightLandConst = 3;

        protected override double HeightLand => HeightLandConst;

        /// <summary>上游 new RandomMap(heightSeaGround, tWater)——基底是水，不是主地形。</summary>
        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightSeaGround, biome.Water, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;
            var mapCenter = map.GetCenter();

            var clFood = new TileClass(MapSize);
            var clLand = new TileClass(MapSize);

            string tMainTerrain = biome.MainTerrain0;
            var pForest1 = new[]
            {
                biome.ForestFloor2 + TerrainFactory.TerrainSeparator + biome.Tree1,
                biome.ForestFloor2 + TerrainFactory.TerrainSeparator + biome.Tree2,
                biome.ForestFloor2,
            };
            var pForest2 = new[]
            {
                biome.ForestFloor1 + TerrainFactory.TerrainSeparator + biome.Tree4,
                biome.ForestFloor1 + TerrainFactory.TerrainSeparator + biome.Tree5,
                biome.ForestFloor1,
            };

            // ── 造大陆（图心一条巨链，queue 首圆半径 = floor(fractionToTiles(0.33))）──
            RmgenLibrary.CreateArea(
                new ChainPlacer(rng, 2,
                    Math.Floor(RmgenLibrary.ScaleByMapSize(5, 12, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(60, 700, MapSize)),
                    double.PositiveInfinity, mapCenter, 0,
                    new[] { (int)Math.Floor(RmgenLibrary.FractionToTiles(0.33, MapSize)) }),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        HeightLandConst, 4),
                    new TileClassPainter(clLand),
                },
                null);

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, null,
                RmgenLibrary.FractionToTiles(0.25, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize),
                rng.RandomAngle());

            // ── 保证每个玩家脚下是陆地（大陆边缘可能没盖到出生点）──
            for (int i = 0; i < NumPlayers; ++i)
                RmgenLibrary.CreateArea(
                    new ChainPlacer(rng, 2,
                        Math.Floor(RmgenLibrary.ScaleByMapSize(5, 9, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(5, 20, MapSize)),
                        double.PositiveInfinity, playerPosition[i], 0,
                        new[] { (int)Math.Floor(RmgenLibrary.ScaleByMapSize(23, 50, MapSize)) }),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            HeightLandConst, 4),
                        new TileClassPainter(clLand),
                    },
                    null);

            // ── 按高度分带刷地形 ──
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 3, 4,
                HeightPlacer.Mode.IncludeMinIncludeMax, tMainTerrain);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 1, 3,
                HeightPlacer.Mode.ExcludeMinExcludeMax, biome.Shore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, -8, 1,
                HeightPlacer.Mode.ExcludeMinIncludeMax, biome.Water);

            // ── 玩家基地 ──
            RmgenCommon.PlacePlayerBases(rng, map, settings, tMainTerrain, ClPlayer, biome,
                playerPosition,
                cityPatchOuterTerrain: biome.RoadWild, cityPatchInnerTerrain: biome.Road,
                playerIDs: playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = ClBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = biome.FruitBush,
                    Mines = new List<(string, string?, object?)>
                    {
                        (biome.MetalLarge, null, null),
                        (biome.StoneLarge, null, null),
                    },
                    TreesTemplate = biome.Tree1,
                    TreesCount = 2,
                    DecorativesTemplate = biome.GrassShort,
                });

            // ── 起伏 ──
            RmgenCommon.CreateBumps(rng, map, new AndConstraint(new IConstraint[]
            {
                RmgenLibrary.AvoidClasses(ClPlayer, 10),
                RmgenLibrary.StayClasses(clLand, 5),
            }));

            var hillConstraint = new AndConstraint(new IConstraint[]
            {
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 15, ClBaseResource, 3),
                RmgenLibrary.StayClasses(clLand, 5),
            });
            int hillCount = (int)(RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers);

            if (rng.RandBool())
                RmgenCommon.CreateHills(rng, map,
                    new object[] { tMainTerrain, biome.Cliff, biome.Hill },
                    hillConstraint, ClHill, hillCount);
            else
                RmgenCommon.CreateMountains(rng, map, biome.Cliff,
                    hillConstraint, ClHill, hillCount);

            // ── 森林 ──
            var (forestTrees, stragglerTrees) = RmgenCommon.GetTreeCounts(
                biome.TreesMin, biome.TreesMax, biome.ForestProbability, MapSize);

            GaiaEntities.CreateDefaultForests(rng, map,
                new object[] { tMainTerrain, biome.ForestFloor1, biome.ForestFloor2,
                    pForest1, pForest2 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 17, ClHill, 0,
                        ClBaseResource, 2),
                    RmgenLibrary.StayClasses(clLand, 4),
                }),
                ClForest, forestTrees);

            // ── 泥地/草地斑块 ──
            var patchConstraint = new AndConstraint(new IConstraint[]
            {
                RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 12),
                RmgenLibrary.StayClasses(clLand, 5),
            });

            RmgenCommon.CreateLayeredPatches(rng, map,
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(3, 6, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 10, MapSize),
                    RmgenLibrary.ScaleByMapSize(8, 21, MapSize),
                },
                new object[]
                {
                    new[] { tMainTerrain, biome.Tier1Terrain },
                    new[] { biome.Tier1Terrain, biome.Tier2Terrain },
                    new[] { biome.Tier2Terrain, biome.Tier3Terrain },
                },
                new[] { 1, 1 },
                patchConstraint,
                (int)RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            RmgenCommon.CreatePatches(rng, map,
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(2, 4, MapSize),
                    RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                    RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
                },
                biome.Tier4Terrain, patchConstraint,
                (int)RmgenLibrary.ScaleByMapSize(15, 45, MapSize), ClDirt);

            // ── 矿藏 ──
            GaiaEntities.CreateBalancedMetalMines(rng, map, NumPlayers,
                biome.MetalSmall, biome.MetalLarge, ClMetal,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clLand, 6),
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer,
                        RmgenLibrary.ScaleByMapSize(20, 35, MapSize), ClHill, 1),
                }));

            GaiaEntities.CreateBalancedStoneMines(rng, map, NumPlayers,
                biome.StoneSmall, biome.StoneLarge, ClRock,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clLand, 6),
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer,
                        RmgenLibrary.ScaleByMapSize(20, 35, MapSize), ClHill, 1, ClMetal, 10),
                }));

            // ── 装饰（india biome 植被 ×8）──
            double planetm = BiomeName == "generic/india" ? 8 : 1;
            bool circular = settings.CircularMap;

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.RockLarge, 1, 2, 0, 1),
                        new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 2),
                    },
                    new IGroupElement[] { new ScatterObject(rng, biome.GrassShort, 1, 2, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.Grass, 2, 4, 0, 1.8),
                        new ScatterObject(rng, biome.GrassShort, 3, 6, 1.2, 2.5),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.BushMedium, 1, 2, 0, 2),
                        new ScatterObject(rng, biome.BushSmall, 2, 4, 0, 2),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, circular),
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, circular),
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, circular),
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, circular),
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, circular),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                    RmgenLibrary.StayClasses(clLand, 5),
                }));

            // ── 食物：猎物 / 浆果 / 鱼 ──
            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                        { new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4) },
                    new IGroupElement[]
                        { new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 20),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) },
                },
                new double[] { 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.Fish, 2, 3, 0, 2) },
                },
                new double[] { 50 * NumPlayers },
                RmgenLibrary.AvoidClasses(clLand, 2, clFood, 10),
                clFood);

            // ── 散落树 ──
            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 7, ClHill, 1, ClPlayer, 9,
                        ClMetal, 6, ClRock, 6),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }
    }
}
