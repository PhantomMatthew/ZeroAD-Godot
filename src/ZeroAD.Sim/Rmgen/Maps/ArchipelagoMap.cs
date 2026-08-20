using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>archipelago.js（315 行）——群岛：全图海床起底，每玩家一座定居岛 + 随机小岛，
    /// 岸线/水下按高度刷漆；森林/食物/矿限陆地（stayClasses(clLand)）。
    /// biome 调整表 archipelago_biome_tweaks.json（india/sahara 特例 + baseline）按
    /// currentBiome() 读取；setWater* 环境设置按约定省略；placePlayersNomad 未移植。</summary>
    public sealed class ArchipelagoMap : StandardMap
    {
        protected override double HeightLand => -5;   // heightSeaGround

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;

            const double heightSeaGround = -5;
            const double heightLand = 3;
            const double heightShore = 1;

            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clLand = new TileClass(MapSize);

            double islandRadius = RmgenLibrary.ScaleByMapSize(22, 31, MapSize);

            // 上游 playerPlacementByPattern(mapSettings.PlayerPlacement, ...)——
            // 布置模式由 gamesetup 下发;randomAngle 实参抽数位置与上游一致
            var (_, playerPosition) = RmgenCommon.PlayerPlacementByPattern(
                rng, map, settings, null, RmgenLibrary.FractionToTiles(0.35, MapSize),
                rng.RandomAngle());

            // ── 玩家岛屿（queue 固定首圆半径 = floor(islandRadius)）──
            for (int i = 0; i < NumPlayers; ++i)
                RmgenLibrary.CreateArea(
                    new ChainPlacer(rng, 2, Math.Floor(RmgenLibrary.ScaleByMapSize(5, 10, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(25, 60, MapSize)),
                        double.PositiveInfinity, playerPosition[i], 0,
                        new[] { (int)Math.Floor(islandRadius) }),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                            heightLand, 4),
                        new TileClassPainter(clLand),
                    },
                    null);

            // ── 随机小岛 ──
            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 8, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(8, 14, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(25, 60, MapSize)),
                    0.07, null, RmgenLibrary.ScaleByMapSize(30, 70, MapSize)),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, heightLand, 4),
                    new TileClassPainter(clLand),
                },
                null,
                RmgenLibrary.ScaleByMapSize(1, 5, MapSize) * rng.RandIntInclusive(5, 10));

            // ── 陆地/岸线/水下刷漆 ──
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightLand - 0.6, heightLand + 0.4,
                HeightPlacer.Mode.IncludeMinIncludeMax, biome.MainTerrain);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightShore, heightLand,
                HeightPlacer.Mode.ExcludeMinExcludeMax, biome.Shore);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightSeaGround, heightShore,
                HeightPlacer.Mode.ExcludeMinIncludeMax, biome.Water);

            // 上游 CityPatch radius = islandRadius/3（默认半径的 1/3 缩小版）
            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome,
                playerPosition, cityPatchRadius: islandRadius / 3,
                options: new RmgenCommon.PlayerBaseOptions
                {
                    BaseResourceClass = clBaseResource,
                    StartingAnimal = true,
                    BerriesTemplate = biome.FruitBush,
                    Mines = new() { (biome.MetalLarge, (string?)null, (object?)null),
                                    (biome.StoneLarge, (string?)null, (object?)null) },
                    Treasures = new() { ("gaia/treasure/wood", 14) },
                    TreesTemplate = biome.Tree1,
                    TreesCount = (int)RmgenLibrary.ScaleByMapSize(15, 30, MapSize),
                    DecorativesTemplate = biome.GrassShort,
                });

            // ── 起伏（限陆地）──
            RmgenLibrary.CreateAreas(rng,
                new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                    Math.Floor(RmgenLibrary.ScaleByMapSize(2, 5, MapSize)), 0),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, 2, 2,
                        relative: true),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, 10),
                    RmgenLibrary.StayClasses(clLand, 5),
                }),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            // ── 丘陵或山脉（限陆地）──
            var hillConstraint = new AndConstraint(new IConstraint[]
            {
                RmgenLibrary.AvoidClasses(ClPlayer, 2, ClHill, 15),
                RmgenLibrary.StayClasses(clLand, 0),
            });
            if (rng.RandBool())
                RmgenLibrary.CreateAreas(rng,
                    new ChainPlacer(rng, 1, Math.Floor(RmgenLibrary.ScaleByMapSize(4, 6, MapSize)),
                        Math.Floor(RmgenLibrary.ScaleByMapSize(16, 40, MapSize)), 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { biome.MainTerrain, biome.Cliff, biome.Hill },
                            new[] { 1, 2 }, rng),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, 18, 2),
                        new TileClassPainter(ClHill),
                    },
                    hillConstraint,
                    RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers);
            else
                RmgenCommon.CreateMountains(rng, map, biome.Cliff, hillConstraint, ClHill,
                    count: (int)(RmgenLibrary.ScaleByMapSize(1, 4, MapSize) * NumPlayers));

            // ── biome 调整（archipelago_biome_tweaks.json）──
            var tweaks = LoadBiomeTweaks(settings.DataRoot);
            var spec = tweaks.TryGetValue(BiomeName, out var t) ? t : tweaks["baseline"];

            double forestTrees = biome.ForestProbability * RmgenLibrary.ScaleByMapSize(
                biome.TreesMin * spec.TreeAmount, biome.TreesMax * spec.TreeAmount, MapSize);
            double stragglerTrees = (1 - biome.ForestProbability) * RmgenLibrary.ScaleByMapSize(
                biome.TreesMin * spec.TreeAmount, biome.TreesMax * spec.TreeAmount, MapSize);

            var pForest1 = new[]
            {
                biome.ForestFloor2 + "|" + biome.Tree1,
                biome.ForestFloor2 + "|" + biome.Tree2,
                biome.ForestFloor2,
            };
            var pForest2 = new[]
            {
                biome.ForestFloor1 + "|" + biome.Tree4,
                biome.ForestFloor1 + "|" + biome.Tree5,
                biome.ForestFloor1,
            };
            GaiaEntities.CreateForests(rng, map,
                new object[] { biome.MainTerrain, biome.ForestFloor1, biome.ForestFloor2, pForest1, pForest2 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClPlayer, spec.ForestPlayerSpacing,
                        ClForest, spec.ForestForestSpacing, ClHill, 0),
                    RmgenLibrary.StayClasses(clLand, 3),
                }),
                ClForest, forestTrees, NumPlayers);

            // ── 泥地分层斑块 ──
            var patchConstraint = new AndConstraint(new IConstraint[]
            {
                RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 3, ClPlayer, 12),
                RmgenLibrary.StayClasses(clLand, 7),
            });
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
                            new object[] { biome.MainTerrain, biome.Tier1Terrain },
                            new object[] { biome.Tier1Terrain, biome.Tier2Terrain },
                            new object[] { biome.Tier2Terrain, biome.Tier3Terrain },
                        }, new[] { 1, 1 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    patchConstraint,
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            // ── 草地斑块（tier4=dirt 名单，上游 tTier4Terrain = g_Terrains.dirt）──
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
                        new TerrainPainter(TerrainFactory.CreateTerrain(biome.Dirt), rng),
                        new TileClassPainter(ClDirt),
                    },
                    patchConstraint,
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            // ── 矿（限陆地）──
            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biome.StoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                        new ScatterObject(rng, biome.StoneLarge, 1, 1, 0, 4),
                    },
                    new IGroupElement[] { new ScatterObject(rng, biome.StoneSmall, 2, 5, 1, 3) },
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 7, ClRock, 10, ClHill, 1),
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                ClRock);

            GaiaEntities.CreateMines(rng, map,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, biome.MetalLarge, 1, 1, 0, 4) } },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 7, ClMetal, 10, ClRock, 5,
                        ClHill, 1),
                    RmgenLibrary.StayClasses(clLand, 6),
                }),
                ClMetal);

            // ── 装饰（india biome 8 倍密度）──
            int planetm = BiomeName == "generic/india" ? 8 : 1;
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
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    planetm * RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                    RmgenLibrary.StayClasses(clLand, 5),
                }));

            // ── 食物 ──
            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[] { new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4) },
                    new IGroupElement[] { new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2) },
                },
                new double[] { 3 * NumPlayers, 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 1, ClHill, 1, clFood, 20),
                    RmgenLibrary.StayClasses(clLand, 3),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) } },
                new double[] { 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 1, ClHill, 1, clFood, 10),
                    RmgenLibrary.StayClasses(clLand, 3),
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                    { new IGroupElement[] { new ScatterObject(rng, biome.Fish, 2, 3, 0, 2) } },
                new double[] { 35 * NumPlayers },
                RmgenLibrary.AvoidClasses(clLand, 3, ClPlayer, 2, clFood, 15),
                clFood);

            // ── 散落树 ──
            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1, biome.Tree2, biome.Tree4, biome.Tree3 },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 7, ClHill, 1, ClPlayer, 3, ClMetal, 6, ClRock, 6),
                    RmgenLibrary.StayClasses(clLand, 7),
                }),
                ClForest, stragglerTrees);

            return map.MakeExportable();
        }

        /// <summary>archipelago_biome_tweaks.json（treeAmount/forestForestSpacing/
        /// forestPlayerSpacing，按 currentBiome() 索引，无条目用 baseline）。
        /// dataRoot 缺失（测试环境）回退 baseline。</summary>
        private static Dictionary<string, BiomeTweaks> LoadBiomeTweaks(string? dataRoot)
        {
            var result = new Dictionary<string, BiomeTweaks>
            {
                ["baseline"] = new BiomeTweaks(1.2, 13, 10),
            };
            if (dataRoot == null)
                return result;

            string path = Path.Combine(dataRoot, "maps", "random", "archipelago_biome_tweaks.json");
            try
            {
                if (!File.Exists(path))
                    return result;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var v = prop.Value;
                    result[prop.Name] = new BiomeTweaks(
                        v.TryGetProperty("treeAmount", out var ta) ? ta.GetDouble() : 1.2,
                        v.TryGetProperty("forestForestSpacing", out var fs) ? fs.GetDouble() : 13,
                        v.TryGetProperty("forestPlayerSpacing", out var ps) ? ps.GetDouble() : 10);
                }
            }
            catch (Exception)
            {
                // 解析失败保留 baseline
            }
            return result;
        }

        private readonly record struct BiomeTweaks(
            double TreeAmount, double ForestForestSpacing, double ForestPlayerSpacing);
    }
}
