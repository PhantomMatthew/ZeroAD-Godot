using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>african_plains.js（342 行）——非洲草原：水洼 + 丘陵错落，
    /// 水边鳄鱼/斑马/瞪羚，草原游荡长颈鹿/大象/狮群；平衡矿（createBalanced*Mines）。
    /// biome 限 savanna/sahara/nubia（上游 african_plains.json SupportedBiomes）。
    /// 环境设置与 placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class AfricanPlainsMap : StandardMap
    {
        private static readonly string[] tCliff =
            { "savanna_cliff_a", "savanna_cliff_a_red", "savanna_cliff_b", "savanna_cliff_b_red" };
        private const string tCitytiles = "savanna_tile_a";

        private const string oPalm = "gaia/tree/bush_tropic";
        private const string oPalm2 = "gaia/tree/cretan_date_palm_short";
        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oWildebeest = "gaia/fauna_wildebeest";
        private const string oZebra = "gaia/fauna_zebra";
        private const string oRhino = "gaia/fauna_rhinoceros_white";
        private const string oLion = "gaia/fauna_lion";
        private const string oLioness = "gaia/fauna_lioness";
        private const string oHawk = "birds/buzzard";
        private const string oGiraffe = "gaia/fauna_giraffe";
        private const string oGiraffe2 = "gaia/fauna_giraffe_infant";
        private const string oGazelle = "gaia/fauna_gazelle";
        private const string oElephant = "gaia/fauna_elephant_african_bush";
        private const string oElephant2 = "gaia/fauna_elephant_african_infant";
        private const string oCrocodile = "gaia/fauna_crocodile_nile";

        protected override double HeightLand => 2;

        /// <summary>上游 african_plains.json SupportedBiomes = [savanna, sahara, nubia]。</summary>
        protected override IReadOnlyList<string> SupportedBiomes => new[] { "savanna", "sahara", "nubia" };

        /// <summary>基底贴图为 biome mainTerrain 名单（上游 RandomMap 逐图块 pickRandom）。</summary>
        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain, Settings.CircularMap);

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;

            string tForestFloor = biome.Tier3Terrain;
            string tSecondary = biome.Tier1Terrain;
            string tGrassShrubs = biome.Tier2Terrain;
            string tDirt = biome.Tier3Terrain;
            string tDirt2 = biome.Tier4Terrain;
            var tDirt3 = biome.Dirt;
            var tDirt4 = biome.Dirt;
            string tShore = biome.Shore;
            string tWater = biome.Water;

            // 上游 oBaobab 在常量区 pickRandom（生成最前、biome 之后）
            string oBaobab = rng.PickRandom(new[]
                { "gaia/tree/baobab", "gaia/tree/baobab_3_mature", "gaia/tree/acacia" });

            const double heightSeaGround = -5;
            const double heightCliff = 3;

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);

            var (_, playerPosition, _, _) = RmgenCommon.PlayerPlacementCircle(
                rng, map, NumPlayers, RmgenLibrary.FractionToTiles(0.35, MapSize));

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, null,
                playerPosition, biome.MainTerrain0, tCitytiles);

            // ── 丘陵 + 水洼（本图特色：密集交错）──
            double nbHills = RmgenLibrary.ScaleByMapSize(6, 16, MapSize);
            double nbWateringHoles = RmgenLibrary.ScaleByMapSize(4, 10, MapSize);

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tDirt2, tCliff, tGrassShrubs },
                        new[] { 1, 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, 18, 2),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 30, ClHill, 15),
                nbHills);

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(60, 100, MapSize)),
                    double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tShore, tWater }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightSeaGround, 7),
                    new TileClassPainter(clWater),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 22, clWater, 8, ClHill, 2),
                nbWateringHoles);

            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightCliff, double.PositiveInfinity,
                HeightPlacer.Mode.ExcludeMinExcludeMax, tCliff);

            // ── 起伏（createBumps 默认参数）──
            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, 2, 2,
                        relative: true),
                },
                RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 20),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            // ── 森林（createDefaultForests，总树数 scale(200,1000)）──
            var pForest = new[]
            {
                tForestFloor + "|" + oPalm,
                tForestFloor + "|" + oPalm2,
                tForestFloor,
            };
            GaiaEntities.CreateDefaultForests(rng, map,
                new object[] { biome.MainTerrain, pForest, tForestFloor, pForest, pForest },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 15, ClHill, 0, clWater, 2),
                ClForest,
                RmgenLibrary.ScaleByMapSize(200, 1000, MapSize));

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
                            new object[] { tDirt, tDirt3 },
                            new object[] { tDirt2, tDirt4 },
                        }, new[] { 2 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClHill, 0, ClDirt, 5,
                        ClPlayer, 12),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            // ── 灌木斑块 + 草地斑块 ──
            foreach (string terrain in new[] { tGrassShrubs, tSecondary })
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
                            new TerrainPainter(terrain, rng),
                            new TileClassPainter(ClDirt),
                        },
                        RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClHill, 0, ClDirt, 5,
                            ClPlayer, 12),
                        RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            // ── 平衡矿 ──
            GaiaEntities.CreateBalancedMetalMines(rng, map, NumPlayers,
                biome.MetalSmall, biome.MetalLarge, ClMetal,
                RmgenLibrary.AvoidClasses(clWater, 4, ClForest, 1, ClPlayer,
                    RmgenLibrary.ScaleByMapSize(20, 35, MapSize), ClHill, 1));

            GaiaEntities.CreateBalancedStoneMines(rng, map, NumPlayers,
                biome.StoneSmall, biome.StoneLarge, ClRock,
                RmgenLibrary.AvoidClasses(clWater, 4, ClForest, 1, ClPlayer,
                    RmgenLibrary.ScaleByMapSize(20, 35, MapSize), ClHill, 1, ClMetal, 10));

            // ── 装饰 ──
            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.BushMedium, 1, 3, 0, 1) },
                    new IGroupElement[] { new ScatterObject(rng, biome.RockMedium, 1, 2, 0, 1) },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, settings.CircularMap),
                },
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 0));

            // ── 草原游荡动物（非 deprecated createObjectGroups）──
            var roamingConstraint = RmgenLibrary.AvoidClasses(clWater, 3, ClPlayer, 20, clFood, 11,
                ClHill, 4);
            double roamingCount = RmgenLibrary.ScaleByMapSize(3, 9, MapSize);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oGiraffe, 2, 4, 0, 4),
                    new ScatterObject(rng, oGiraffe2, 0, 2, 0, 4),
                }, true, clFood),
                0, roamingConstraint, roamingCount, 50);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oElephant, 2, 4, 0, 4),
                    new ScatterObject(rng, oElephant2, 0, 2, 0, 4),
                }, true, clFood),
                0, roamingConstraint, roamingCount, 50);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oLion, 0, 1, 0, 4),
                    new ScatterObject(rng, oLioness, 2, 3, 0, 4),
                }, true, clFood),
                0, roamingConstraint, roamingCount, 50);

            // 其他游荡动物
            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oHawk, 1, 1, 0, 3) },
                    new IGroupElement[] { new ScatterObject(rng, oGazelle, 3, 5, 0, 3) },
                    new IGroupElement[] { new ScatterObject(rng, oZebra, 3, 5, 0, 3) },
                    new IGroupElement[] { new ScatterObject(rng, oWildebeest, 4, 6, 0, 3) },
                    new IGroupElement[] { new ScatterObject(rng, oRhino, 1, 1, 0, 3) },
                },
                new double[]
                {
                    3 * NumPlayers, 3 * NumPlayers, 3 * NumPlayers, 3 * NumPlayers, 3 * NumPlayers,
                },
                RmgenLibrary.AvoidClasses(clFood, 20, clWater, 5, ClHill, 2, ClPlayer, 16),
                clFood);

            // ── 水洼边动物（borderClasses(clWater, 6, 3)）──
            var waterEdge = RmgenLibrary.BorderClasses(clWater, 6, 3);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oCrocodile, 2, 3, 0, 3) },
                    true, clFood),
                0, waterEdge, nbWateringHoles * 0.8, 50);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oZebra, 2, 5, 0, 3) },
                    true, clFood),
                0, waterEdge, nbWateringHoles, 50);
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oGazelle, 2, 5, 0, 3) },
                    true, clFood),
                0, waterEdge, nbWateringHoles, 50);

            // ── 浆果 + 鱼 ──
            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, oBerryBush, 5, 7, 0, 4) } },
                new double[] { rng.RandIntInclusive(1, 4) * NumPlayers + 2 },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 2, ClPlayer, 20, ClHill, 3,
                    clFood, 10),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, biome.Fish, 2, 3, 0, 2) } },
                new double[] { 15 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 20),
                    RmgenLibrary.StayClasses(clWater, 6),
                }),
                clFood);

            // ── 散落猴面包树 ──
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[] { new ScatterObject(rng, oBaobab, 1, 1, 0, 3) },
                    true, ClForest),
                0,
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 2, ClHill, 3, ClPlayer, 12,
                    ClMetal, 4, ClRock, 4),
                RmgenLibrary.ScaleByMapSize(15, 75, MapSize));

            return map.MakeExportable();
        }
    }
}
