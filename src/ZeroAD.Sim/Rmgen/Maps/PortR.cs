using System;
using System.Collections.Generic;
using ZeroAD.Sim.Rmgen.Common;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>alpine_lakes.js（250 行）——高山湖泊：先用图专属 alpine/ biome 铺底，
    /// 大量山脉围出地势，再以 ChainPlacer 串出湖泊链；按雪线刷悬崖/积雪。
    /// 环境设置除雾/饱和度 biome 分支与水高外由表驱动；placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class AlpineLakesMap2 : StandardMap
    {
        protected override double HeightLand => 3;

        /// <summary>上游 alpine_lakes.json SupportedBiomes = "alpine/"（图专属 biome 目录）。</summary>
        protected override IReadOnlyList<string> SupportedBiomes => BiomeLoader.AlpineBiomes;

        protected override RandomMap CreateMap(BiomeSet biome)
            => new(Rng, MapSize, HeightLand, biome.MainTerrain, Settings.CircularMap);

        /// <summary>上游按 biome 分支的雾/饱和度三条，加 setWaterHeight(heightSeaGround=-5)。</summary>
        protected internal override void ApplyExtraEnvironment(RmgenEnvironment env, RmgenRng rng)
        {
            bool lateSpring = BiomeName == "alpine/late_spring";
            env.SetFogThickness(lateSpring ? 0.26 : 0.19);
            env.SetFogFactor(lateSpring ? 0.4 : 0.35);
            env.SetPPSaturation(lateSpring ? 0.48 : 0.37);
            env.SetWaterHeight(-5);
        }

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;

            const double heightSeaGround = -5;

            var pForest = new[]
            {
                biome.ForestFloor + "|" + biome.Tree1,
                biome.ForestFloor,
            };

            var clWater = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, settings.PlayerPlacement,
                RmgenLibrary.FractionToTiles(0.35, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize),
                rng.RandomAngle());

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                playerPosition, biome.RoadWild, biome.Road, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = biome.FruitBush,
                    Mines = new() { (biome.MetalLarge, (string?)null, (object?)null),
                                    (biome.StoneLarge, (string?)null, (object?)null) },
                    TreesTemplate = biome.Tree1,
                    TreesCount = (int)Math.Floor(RmgenLibrary.ScaleByMapSize(3, 12, MapSize)),
                    DecorativesTemplate = biome.GrassShort,
                });

            RmgenCommon.CreateMountains(rng, map, biome.Cliff,
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 8),
                ClHill,
                count: (int)SafeMath.Ceil(RmgenLibrary.ScaleByMapSize(10, 40, MapSize) * NumPlayers),
                maxHeight: Math.Floor(RmgenLibrary.ScaleByMapSize(40, 60, MapSize)),
                minRadius: (int)Math.Floor(RmgenLibrary.ScaleByMapSize(4, 5, MapSize)),
                maxRadius: (int)Math.Floor(RmgenLibrary.ScaleByMapSize(7, 15, MapSize)),
                numCircles: (int)Math.Floor(RmgenLibrary.ScaleByMapSize(5, 15, MapSize)));

            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 8, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(40, 180, MapSize)), 0.7),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Shore, biome.Water }, new[] { 1 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightSeaGround, 5),
                    new TileClassPainter(clWater),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, clWater, 8),
                RmgenLibrary.ScaleByMapSize(5, 16, MapSize), 1);

            double snowlineHeight = Math.Floor(RmgenLibrary.ScaleByMapSize(20, 40, MapSize));
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, 3, snowlineHeight,
                HeightPlacer.Mode.ExcludeMinExcludeMax, biome.Cliff);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, snowlineHeight, 100,
                HeightPlacer.Mode.IncludeMinIncludeMax, biome.SnowLimited);

            CreateDefaultBumps(rng, RmgenLibrary.AvoidClasses(clWater, 2, ClPlayer, 20));

            double treeCount = RmgenLibrary.ScaleByMapSize(500, 3000, MapSize);
            double forestTrees = 0.7 * treeCount;
            double stragglerTrees = (1 - 0.7) * treeCount;
            GaiaEntities.CreateForests(rng, map,
                new object[] { biome.MainTerrain, biome.ForestFloor, biome.ForestFloor, pForest, pForest },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 17, ClHill, 0, clWater, 2),
                ClForest, forestTrees, NumPlayers);

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
                            new object[] { biome.Dirt, biome.HalfSnow },
                            new object[] { biome.HalfSnow, biome.SnowLimited },
                        }, new[] { 2 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 12),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

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
                        new TerrainPainter(biome.Tier2Terrain, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 12),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.StoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                        // 上游此处也用 stoneSmall，而非 stoneLarge。
                        new ScatterObject(rng, biome.StoneSmall, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                    },
                    new IGroupElement[] { new ScatterObject(rng, biome.StoneSmall, 2, 5, 1, 3) },
                },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20, ClRock, 10, ClHill, 1),
                ClRock);

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.MetalLarge, 1, 1, 0, 4) },
                },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 1, ClPlayer, 20, ClMetal, 10,
                    ClRock, 5, ClHill, 1),
                ClMetal);

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
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                },
                RmgenLibrary.AvoidClasses(clWater, 0, ClForest, 0, ClPlayer, 0, ClHill, 0));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 20),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) },
                },
                new double[] { rng.RandIntInclusive(1, 4) * NumPlayers + 2 },
                RmgenLibrary.AvoidClasses(clWater, 3, ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.Fish, 2, 3, 0, 2) },
                },
                new double[] { 20 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clFood, 8),
                    RmgenLibrary.StayClasses(clWater, 2),
                }),
                clFood);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1 },
                RmgenLibrary.AvoidClasses(clWater, 5, ClForest, 3, ClHill, 1, ClPlayer, 12,
                    ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }

        private void CreateDefaultBumps(RmgenRng rng, IConstraint constraint)
            => RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, 2, 2,
                        relative: true),
                },
                constraint,
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));
    }

    /// <summary>atlas_mountains.js（244 行）——阿特拉斯山地：岩草基底、密集山脉与
    /// 山地树林；额外放置食物/木材宝藏。环境设置由表驱动，placePlayersNomad 省略。</summary>
    public sealed class AtlasMountainsMap2 : StandardMap
    {
        private static readonly string[] tPrimary =
        {
            "medit_rocks_grass", "medit_rocks_grass", "medit_rocks_grass",
            "medit_rocks_grass", "medit_rocks_grass_shrubs", "medit_rocks_shrubs",
        };
        private static readonly string[] tGrass = { "medit_rocks_grass_shrubs", "medit_rocks_shrubs" };
        private const string tForestFloor = "medit_grass_field_dry";
        private const string tCliff = "medit_cliff_italia";
        private const string tGrassDirt = "medit_rocks_grass";
        private const string tDirt = "medit_dirt";
        private const string tRoad = "medit_city_tile";
        private const string tRoadWild = "medit_city_tile";
        private const string tGrass2 = "medit_rocks_grass_shrubs";
        private const string tGrassPatch = "medit_grass_wild";

        private const string oCarob = "gaia/tree/carob";
        private const string oAleppoPine = "gaia/tree/aleppo_pine";
        private const string oBerryBush = "gaia/fruit/berry_01";
        private const string oDeer = "gaia/fauna_deer";
        private const string oSheep = "gaia/fauna_sheep";
        private const string oStoneLarge = "gaia/rock/mediterranean_large";
        private const string oStoneSmall = "gaia/rock/mediterranean_small";
        private const string oMetalLarge = "gaia/ore/mediterranean_large";
        private const string oWoodTreasure = "gaia/treasure/wood";
        private const string oFoodTreasure = "gaia/treasure/food_bin";

        private const string aGrass = "actor|props/flora/grass_soft_large_tall.xml";
        private const string aGrassShort = "actor|props/flora/grass_soft_large.xml";
        private const string aRockLarge = "actor|geology/stone_granite_large.xml";
        private const string aRockMedium = "actor|geology/stone_granite_med.xml";
        private const string aBushMedium = "actor|props/flora/bush_medit_me.xml";
        private const string aBushSmall = "actor|props/flora/bush_medit_sm.xml";
        private const string aCarob = "actor|flora/trees/carob.xml";
        private const string aAleppoPine = "actor|flora/trees/aleppo_pine.xml";

        protected override double HeightLand => 3;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tPrimary);
            var map = Map;

            var pForest1 = new[]
            {
                tForestFloor + "|" + oCarob,
                tForestFloor,
            };
            var pForest2 = new[]
            {
                tForestFloor + "|" + oAleppoPine,
                tForestFloor,
            };

            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clTreasure = new TileClass(MapSize);

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, settings.PlayerPlacement,
                RmgenLibrary.FractionToTiles(0.35, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize),
                rng.RandomAngle());

            RmgenCommon.PlacePlayerBases(rng, map, settings, tPrimary[0], ClPlayer, null,
                playerPosition, tRoadWild, tRoad, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = oBerryBush,
                    Mines = new() { (oMetalLarge, (string?)null, (object?)null),
                                    (oStoneLarge, (string?)null, (object?)null) },
                    TreesTemplate = oCarob,
                    TreesCount = (int)Math.Floor(RmgenLibrary.ScaleByMapSize(2, 8, MapSize)),
                    DecorativesTemplate = aGrassShort,
                });

            CreateDefaultBumps(rng, RmgenLibrary.AvoidClasses(ClPlayer, 9));

            RmgenCommon.CreateMountains(rng, map, tCliff,
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 8),
                ClHill,
                count: (int)SafeMath.Ceil(RmgenLibrary.ScaleByMapSize(20, 120, MapSize)));

            double treeCount = RmgenLibrary.ScaleByMapSize(500, 3000, MapSize);
            double forestTrees = 0.7 * treeCount;
            double stragglerTrees = (1 - 0.7) * treeCount;
            GaiaEntities.CreateForests(rng, map,
                new object[] { tGrass, tForestFloor, tForestFloor, pForest1, pForest2 },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 14, ClHill, 1),
                ClForest, forestTrees, NumPlayers);

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
                        new LayeredPainter(new object[] { tGrassDirt, tDirt }, new[] { 2 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 3, ClPlayer, 10),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

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
                        new LayeredPainter(new object[] { tGrass2, tGrassPatch }, new[] { 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 3, ClPlayer, 10),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

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
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClMetal, 10, ClRock, 5, ClHill, 2),
                ClRock);

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4) },
                },
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClMetal, 10, ClRock, 5, ClHill, 2),
                ClMetal);

            GaiaEntities.CreateDecoration(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, aRockMedium, 1, 3, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                        new ScatterObject(rng, aRockMedium, 1, 3, 0, 2),
                    },
                    new IGroupElement[] { new ScatterObject(rng, aGrassShort, 1, 2, 0, 1) },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aGrass, 2, 4, 0, 1.8),
                        new ScatterObject(rng, aGrassShort, 3, 6, 1.2, 2.5),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, aBushMedium, 1, 2, 0, 2),
                        new ScatterObject(rng, aBushSmall, 2, 4, 0, 2),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapAreaAbsolute(16, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(8, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                },
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0));

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oSheep, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, oDeer, 2, 3, 0, 2) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers },
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 20),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, oBerryBush, 5, 7, 0, 4) },
                },
                new double[] { rng.RandIntInclusive(3, 12) * NumPlayers + 2 },
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10),
                clFood);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oFoodTreasure, 2, 3, 0, 2),
                }, true, clTreasure),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 18, ClHill, 1, clFood, 5),
                3 * NumPlayers, 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oWoodTreasure, 2, 3, 0, 2),
                }, true, clTreasure),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 18, ClHill, 1),
                3 * NumPlayers, 50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oCarob, oAleppoPine },
                RmgenLibrary.AvoidClasses(ClForest, 1, ClHill, 1, ClPlayer, 10, ClMetal, 6,
                    ClRock, 6, clTreasure, 4),
                ClForest, stragglerTrees);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { aCarob, aAleppoPine },
                RmgenLibrary.StayClasses(ClHill, 2),
                ClForest, stragglerTrees / 5);

            return map.MakeExportable();
        }

        private void CreateDefaultBumps(RmgenRng rng, IConstraint constraint)
            => RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, 2, 2,
                        relative: true),
                },
                constraint,
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));
    }

    /// <summary>volcanic_lands.js（201 行）——火山荒地：中央活火山含三层熔岩与烟雾，
    /// 周边火山岩丘陵、死树森林和裸岩矿点。placePlayersNomad 按既有移植约定省略。</summary>
    public sealed class VolcanicLandsMap2 : StandardMap
    {
        private static readonly string[] tGrass = { "cliff volcanic light", "ocean_rock_a", "ocean_rock_b" };
        private const string tGrassA = "cliff volcanic light";
        private const string tGrassB = "ocean_rock_a";
        private const string tGrassC = "ocean_rock_b";
        private static readonly string[] tCliff = { "cliff volcanic coarse", "cave_walls" };
        private const string tRoad = "road1";
        private const string tRoadWild = "road1";
        private const string tLava1 = "LavaTest05";
        private const string tLava2 = "LavaTest04";
        private const string tLava3 = "LavaTest03";

        private const string oTree = "gaia/tree/dead";
        private const string oStoneLarge = "gaia/rock/alpine_large";
        private const string oStoneSmall = "gaia/rock/alpine_small";
        private const string oMetalLarge = "gaia/ore/alpine_large";

        private const string aRockLarge = "actor|geology/stone_granite_med.xml";
        private const string aRockMedium = "actor|geology/stone_granite_med.xml";

        protected override double HeightLand => 1;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContextNoBiome(rng, settings, tGrassB);
            var map = Map;
            var mapCenter = map.GetCenter();

            const double heightHillValue = 18;

            var pForestD = new[]
            {
                tGrassC + "|" + oTree,
                tGrassC,
            };
            var pForestP = new[]
            {
                tGrassB + "|" + oTree,
                tGrassB,
            };

            var clBaseResource = new TileClass(MapSize);

            var (playerIDs, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, settings.PlayerPlacement,
                RmgenLibrary.FractionToTiles(0.35, MapSize),
                RmgenLibrary.FractionToTiles(0.1, MapSize),
                rng.RandomAngle());

            RmgenCommon.PlacePlayerBases(rng, map, settings, tGrassB, ClPlayer, null,
                playerPosition, tRoadWild, tRoad, playerIDs,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    Mines = new() { (oMetalLarge, (string?)null, (object?)null),
                                    (oStoneLarge, (string?)null, (object?)null) },
                    TreesTemplate = oTree,
                    TreesCount = (int)Math.Floor(RmgenLibrary.ScaleByMapSize(12, 30, MapSize)),
                });

            CreateVolcano(rng, mapCenter, ClHill, tCliff, new[] { tLava1, tLava2, tLava3 }, true);

            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 150, MapSize),
                    0.2, 0.1, double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { tCliff, tGrass }, new[] { 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                        heightHillValue, 2),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 12, ClHill, 15, clBaseResource, 2),
                RmgenLibrary.ScaleByMapSize(2, 8, MapSize) * NumPlayers);

            double treeCount = RmgenLibrary.ScaleByMapSize(200, 1250, MapSize);
            double forestTrees = 0.7 * treeCount;
            double stragglerTrees = (1 - 0.7) * treeCount;
            var types = new object[][]
            {
                new object[] { new object[] { tGrassB, tGrassA, pForestD }, new object[] { tGrassB, pForestD } },
                new object[] { new object[] { tGrassB, tGrassA, pForestP }, new object[] { tGrassB, pForestP } },
            };
            double forestSize = forestTrees / (RmgenLibrary.ScaleByMapSize(2, 8, MapSize) * NumPlayers);
            double forestNum = Math.Floor(forestSize / types.Length);
            foreach (var type in types)
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, forestTrees / forestNum, 0.1, 0.1, double.PositiveInfinity),
                    new IPainter[]
                    {
                        new LayeredPainter(type, new[] { 2 }, rng),
                        new TileClassPainter(ClForest),
                    },
                    RmgenLibrary.AvoidClasses(ClPlayer, 12, ClForest, 10, ClHill, 0, clBaseResource, 6),
                    forestNum);

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 84, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 128, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, patchSize, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { tGrassA, tGrassA }, new[] { 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClPlayer, 12),
                    RmgenLibrary.ScaleByMapSize(20, 80, MapSize));

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 84, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 128, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, patchSize, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new TerrainPainter(tGrassB, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClPlayer, 12),
                    RmgenLibrary.ScaleByMapSize(20, 80, MapSize));

            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 84, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 128, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, patchSize, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new TerrainPainter(tGrassC, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClPlayer, 12),
                    RmgenLibrary.ScaleByMapSize(20, 80, MapSize));

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                    new ScatterObject(rng, oStoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                }, true, ClRock),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oStoneSmall, 2, 5, 1, 3),
                }, true, ClRock),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, oMetalLarge, 1, 1, 0, 4),
                }, true, ClMetal),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 10, ClMetal, 10, ClRock, 5, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aRockMedium, 1, 3, 0, 1),
                }, true),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            RmgenLibrary.CreateObjectGroupsDeprecated(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, aRockLarge, 1, 2, 0, 1),
                    new ScatterObject(rng, aRockMedium, 1, 3, 0, 2),
                }, true),
                0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            GaiaEntities.CreateStragglerTrees(rng,
                new[] { oTree },
                RmgenLibrary.AvoidClasses(ClForest, 1, ClHill, 1, ClPlayer, 12, ClMetal, 6,
                    ClRock, 6, clBaseResource, 6),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }

        /// <summary>createVolcano（rmgen-common/gaia_terrain.js 同名函数）——五层同心火山。</summary>
        private void CreateVolcano(RmgenRng rng, RmgenVector2D position, TileClass tileClass,
            object terrainTexture, IReadOnlyList<string>? lavaTextures, bool smoke)
        {
            var clLava = new TileClass(MapSize);
            IPainter? lavaPainter = lavaTextures == null ? null :
                new LayeredPainter(new object[]
                {
                    terrainTexture,
                    lavaTextures[0],
                    lavaTextures[1],
                    lavaTextures[2],
                }, new[] { 1, 1, 1 }, rng);

            var layers = new (double clumps, double elevation, TileClass tileClass, IPainter? painter, double steepness)[]
            {
                (RmgenGeometry.DiskArea(RmgenLibrary.ScaleByMapSize(18, 25, MapSize)), 15, tileClass, null, 3),
                (RmgenGeometry.DiskArea(RmgenLibrary.ScaleByMapSize(16, 23, MapSize)), 25,
                    new TileClass(MapSize), null, 3),
                (RmgenGeometry.DiskArea(RmgenLibrary.ScaleByMapSize(10, 15, MapSize)), 45,
                    new TileClass(MapSize), null, 3),
                (RmgenGeometry.DiskArea(RmgenLibrary.ScaleByMapSize(8, 11, MapSize)), 62,
                    new TileClass(MapSize), null, 3),
                (RmgenGeometry.DiskArea(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)), 42,
                    clLava, lavaPainter, 1),
            };

            for (int i = 0; i < layers.Length; ++i)
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, layers[i].clumps, 0.7, 0.05,
                        double.PositiveInfinity, position),
                    new IPainter[]
                    {
                        layers[i].painter ?? new LayeredPainter(new object[] { terrainTexture, terrainTexture },
                            new[] { 3 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid,
                            layers[i].elevation, layers[i].steepness),
                        new TileClassPainter(layers[i].tileClass),
                    },
                    i == 0 ? null : RmgenLibrary.StayClasses(layers[i - 1].tileClass, 1));

            if (!smoke)
                return;

            double num = Math.Floor(RmgenGeometry.DiskArea(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)));
            RmgenLibrary.CreateObjectGroup(
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, "actor|particle/smoke.xml", num, num, 0, 7),
                }, false, clLava, position),
                0,
                RmgenLibrary.StayClasses(tileClass, 1));
        }
    }
}
