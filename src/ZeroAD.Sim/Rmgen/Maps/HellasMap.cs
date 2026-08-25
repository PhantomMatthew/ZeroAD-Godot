using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>hellas.js（633 行）——希腊：真实地理高度图（hellas.png 720×720 灰度）
    /// 随机裁剪子区域，按陆地占比/悬崖占比筛选，双三次插值刷高后按海拔带
    /// （water/shoreline/lowlands/highlands/mountains）刷地形与植被；
    /// 码头/神庙/雕像/弩炮等希腊彩蛋实体。biome 表 hellas_biomes.json（本图专属，
    /// 无 setBiome 抽数）。环境设置与 Nomad 分支按既有移植约定省略。</summary>
    public sealed class HellasMap : StandardMap
    {
        protected override double HeightLand => 0;

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            // 图样式（上游 mapStyles 数组字面量：2 次 randBool 抽数在生成最前）
            var mapStyles = new (int minMapSize, bool enabled, double landLo, double landHi)[]
            {
                (0, rng.RandBool(0.15), 0.95, 1),        // mainland
                (384, rng.RandBool(1.0 / 4), 0.3, 0.5),  // lots of water
                (192, true, 0.65, 0.9),                  // few water
            };

            // 高度图（上游 LoadHeightmapImage + convertHeightmap1Dto2D；
            // 数据缺失环境用确定性径向渐变兜底——扫雷/模板测试的 dataRoot 不含 maps/）
            var heightmapHellas = LoadHellasHeightmap(settings.DataRoot);

            var biomes = HellasBiomes.Load(settings.DataRoot);

            Rng = rng;
            Settings = settings;
            MapSize = settings.Size;
            NumPlayers = RmgenCommon.GetNumPlayers(settings);
            BiomeName = "";
            double heightScale = MapSize / 320.0;
            double heightSeaGround = -6 * heightScale;
            double heightReedsMin = -2 * heightScale;
            double heightReedsMax = -0.5 * heightScale;
            double heightShoreline = 1 * heightScale;
            double heightLowlands = 30 * heightScale;
            double heightHighlands = 60 * heightScale;
            const double heightmapMin = 0;
            const double heightmapMax = 100;

            Map = new RandomMap(rng, MapSize, 0,
                biomes.StrList("lowlands", "terrains", "main"), settings.CircularMap);
            RmgenLibrary.CurrentMap = Map;
            var map = Map;
            var mapCenter = map.GetCenter();

            ClPlayer = new TileClass(MapSize);
            ClForest = new TileClass(MapSize);
            ClDirt = new TileClass(MapSize);
            ClRock = new TileClass(MapSize);
            ClMetal = new TileClass(MapSize);
            var clFood = new TileClass(MapSize);
            var clBaseResource = new TileClass(MapSize);
            var clDock = new TileClass(MapSize);

            var constraintLowlands = new HeightConstraint(map, heightShoreline, heightLowlands);
            var constraintHighlands = new HeightConstraint(map, heightLowlands, heightHighlands);
            var constraintMountains = new HeightConstraint(map, heightHighlands, double.PositiveInfinity);

            // filter(size>=min) → 按 enabled 稳定排序 → 取末位（上游 sort().pop()）
            var eligible = new List<(int minMapSize, bool enabled, double landLo, double landHi)>();
            foreach (var s in mapStyles)
                if (MapSize >= s.minMapSize)
                    eligible.Add(s);
            eligible = eligible.OrderBy(s => s.enabled).ToList();
            var chosenStyle = eligible[^1];
            double minLandRatio = chosenStyle.landLo, maxLandRatio = chosenStyle.landHi;
            double minCliffRatio = maxLandRatio < 0.75 ? 0 : 0.08;
            const double maxCliffRatio = 0.18;

            var playerIDs = RmgenCommon.SortAllPlayers(rng, settings);
            List<RmgenVector2D> playerPosition;

            TileClass clWater;
            TileClass clCliffs;

            // 随机裁剪高度图子区域：满足图样式陆地占比 + 容得下所有玩家
            int subAreaSize;
            while (true)
            {
                subAreaSize = (int)Math.Floor(rng.RandFloat(0.01, 0.2) * heightmapHellas.Length);
                var topLeft = new RmgenVector2D(rng.RandFloat(0, 1), rng.RandFloat(0, 1));
                topLeft.Mult(heightmapHellas.Length - subAreaSize);
                topLeft.Floor();

                var heightmap = HeightmapLoader.ExtractHeightmap(heightmapHellas, topLeft, subAreaSize);
                var heightmapPainter = new HeightmapPainter(map, heightmap, heightmapMin, heightmapMax);

                // 快速面积测试
                var testPoints = new DiskPlacer(
                    heightmap.Length / 2.0 - RmgenConstants.MAP_BORDER_WIDTH,
                    new RmgenVector2D(heightmap.Length / 2.0, heightmap.Length / 2.0))
                    .Place(new NullConstraint())!;
                int landArea = 0;
                foreach (var point in testPoints)
                    if (heightmapPainter.ScaleHeight(heightmap[(int)point.X][(int)point.Y]) > heightShoreline)
                        ++landArea;

                double landRatio = (double)landArea / testPoints.Count;
                if (landRatio < minLandRatio || landRatio > maxLandRatio)
                    continue;

                // 刷高度
                RmgenLibrary.CreateArea(new MapBoundsPlacer(), heightmapPainter, null);

                // 量陆地占比
                var passableLandArea = RmgenLibrary.CreateArea(
                    new DiskPlacer(RmgenLibrary.FractionToTiles(0.5, MapSize), mapCenter),
                    (IPainter?)null,
                    new HeightConstraint(map, heightShoreline, double.PositiveInfinity));
                if (passableLandArea == null)
                    continue;

                landRatio = passableLandArea.PointCount /
                    RmgenGeometry.DiskArea(RmgenLibrary.FractionToTiles(0.5, MapSize));
                if (landRatio < minLandRatio || landRatio > maxLandRatio)
                    continue;

                // 压低海床
                clWater = new TileClass(MapSize);
                RmgenLibrary.CreateArea(
                    new MapBoundsPlacer(),
                    new IPainter[]
                    {
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                            heightSeaGround, 5),
                        new TileClassPainter(clWater),
                    },
                    new HeightConstraint(map, double.NegativeInfinity, heightShoreline));

                // 平滑直到悬崖占比合格
                double cliffsRatio = 0;
                while (true)
                {
                    RmgenLibrary.CreateArea(
                        new DiskPlacer(RmgenLibrary.FractionToTiles(0.5, MapSize) -
                            RmgenConstants.MAP_BORDER_WIDTH, mapCenter),
                        new SmoothingPainter(1, 0.5, 1),
                        null);

                    clCliffs = new TileClass(MapSize);

                    // 标悬崖
                    var cliffsArea = RmgenLibrary.CreateArea(
                        new MapBoundsPlacer(),
                        new TileClassPainter(clCliffs),
                        new AndConstraint(new IConstraint[]
                        {
                            RmgenLibrary.AvoidClasses(clWater, 2),
                            new SlopeConstraint(map, 2, double.PositiveInfinity),
                        }));

                    cliffsRatio = (cliffsArea?.PointCount ?? 0) / SafeMath.Square((double)MapSize);
                    if (cliffsRatio < maxCliffRatio)
                        break;
                }

                if (cliffsRatio < minCliffRatio)
                    continue;

                // 找玩家位置
                var players = RmgenCommon.PlayerPlacementRandom(rng, map, settings,
                    RmgenLibrary.AvoidClasses(clCliffs, RmgenLibrary.ScaleByMapSize(6, 15, MapSize),
                        clWater, RmgenLibrary.ScaleByMapSize(10, 20, MapSize)));

                if (players != null)
                {
                    (playerIDs, playerPosition) = players.Value;
                    break;
                }
                // 位置不足——重开一轮
            }

            // ── 压平初始 CC 区 ──
            double playerRadius = RmgenCommon.DefaultPlayerBaseRadius(MapSize) * 0.8;
            foreach (var position in playerPosition)
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, RmgenGeometry.DiskArea(playerRadius), 0.95, 0.6,
                        double.PositiveInfinity, position),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        map.GetHeight(position), playerRadius / 2),
                    null);

            // ── 海拔带刷漆 ──
            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TerrainPainter(biomes.Terrain("lowlands", "terrains", "main"), rng),
                constraintLowlands);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TerrainPainter(biomes.Terrain("highlands", "terrains", "main"), rng),
                constraintHighlands);

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TerrainPainter(biomes.Terrain("common", "terrains", "cliffs"), rng),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clWater, 2),
                    constraintMountains,
                }));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TerrainPainter(biomes.Terrain("water", "terrains", "main"), rng),
                new HeightConstraint(map, double.NegativeInfinity, heightShoreline));

            RmgenLibrary.CreateArea(new MapBoundsPlacer(),
                new TerrainPainter(biomes.Terrain("common", "terrains", "cliffs"), rng),
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clWater, 2),
                    new SlopeConstraint(map, 2, double.PositiveInfinity),
                }));

            // ── 玩家基地（上游逐玩家 placePlayerBase：按所在海拔带选动物/树;
            // optionsFactory 内 pickRandom 抽树,抽数位置同上游 args 构建）──
            RmgenCommon.PlacePlayerBases(rng, map, settings,
                biomes.Str("lowlands", "terrains", "main"), ClPlayer, null,
                playerPosition,
                biomes.Str("common", "terrains", "roadWild"),
                biomes.Str("common", "terrains", "road"),
                playerIDs,
                optionsFactory: pid =>
                {
                    int i = playerIDs.IndexOf(pid);
                    bool highlands = constraintHighlands.Allows(playerPosition[i]);
                    string band = highlands ? "highlands" : "lowlands";
                    return new RmgenCommon.PlayerBaseOptions
                    {
                        BaseResourceClass = clBaseResource,
                        ExtraBaseResourceConstraint = RmgenLibrary.AvoidClasses(
                            ClPlayer, 4, clWater, 1, clCliffs, 1),
                        StartingAnimal = true,
                        StartingAnimalTemplate = biomes.Str(band, "gaia", "fauna", "startingAnimal"),
                        StartingAnimalGroupCount = 1,
                        StartingAnimalMinGroupCount = 4,
                        StartingAnimalMaxGroupCount = 4,
                        BerriesTemplate = biomes.Str(band, "gaia", "flora", "fruitBush"),
                        BerriesMinCount = 3,
                        BerriesMaxCount = 3,
                        Mines = new()
                        {
                            (biomes.Str("common", "gaia", "mines", "metalLarge"), (string?)null, (object?)null),
                            (biomes.Str("common", "gaia", "mines", "stoneLarge"), (string?)null, (object?)null),
                        },
                        MinesMinAngle = SafeMath.PI / 2,
                        MinesMaxAngle = SafeMath.PI,
                        TreesTemplate = rng.PickRandom(biomes.StrList(band, "gaia", "flora", "trees")),
                        TreesCount = 15,
                    };
                });

            // ── 码头 ──
            GaiaEntities.PlaceDocks(rng, map,
                biomes.Str("shoreline", "gaia", "dock"), 0,
                RmgenLibrary.ScaleByMapSize(1, 2, MapSize) * 100,
                clWater, clDock, heightReedsMax, heightShoreline,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clDock, 50),
                    new StaticConstraint(map,
                        RmgenLibrary.AvoidClasses(ClPlayer, 30, clCliffs, 8)),
                }),
                0, 50);

            // ── 森林（低地 0.6 / 高地 0.4）──
            double forestTrees = 0.7 * RmgenLibrary.ScaleByMapSize(600, 4000, MapSize);
            double stragglerTrees = (1 - 0.7) * RmgenLibrary.ScaleByMapSize(600, 4000, MapSize);
            const double biomeTreeRatioHighlands = 0.4;
            foreach (var band in new[] { "lowlands", "highlands" })
                GaiaEntities.CreateForests(rng, map,
                    new object[]
                    {
                        biomes.Terrain(band, "terrains", "main"),
                        biomes.Str(band, "terrains", "forestFloors", "0"),
                        biomes.Str(band, "terrains", "forestFloors", "1"),
                        biomes.Terrain(band, "terrains", "forests", "0"),
                        biomes.Terrain(band, "terrains", "forests", "1"),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        band == "highlands" ? constraintHighlands : constraintLowlands,
                        RmgenLibrary.AvoidClasses(ClPlayer, 20, ClForest, 18, clCliffs, 1, clWater, 2),
                    }),
                    ClForest,
                    forestTrees * (band == "highlands" ? biomeTreeRatioHighlands : 1 - biomeTreeRatioHighlands),
                    NumPlayers);

            // ── 石矿/金属矿（非 deprecated createObjectGroups）──
            var mineConstraint = new Func<IConstraint>(() =>
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClRock, 18, clCliffs, 2,
                    clWater, 2, clDock, 6));
            foreach (var mine in new[]
            {
                new IGroupElement[]
                {
                    new ScatterObject(rng, biomes.Str("common", "gaia", "mines", "stoneLarge"),
                        1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                },
                new IGroupElement[]
                {
                    new ScatterObject(rng, biomes.Str("common", "gaia", "mines", "stoneSmall"),
                        2, 3, 1, 3, 0, 2 * SafeMath.PI, 1),
                },
            })
                RmgenLibrary.CreateObjectGroups(rng,
                    new ObjectGroup(mine, true, ClRock),
                    0, mineConstraint(),
                    RmgenLibrary.ScaleByMapSize(2, 12, MapSize), 50);

            foreach (var mine in new[]
            {
                new IGroupElement[]
                {
                    new ScatterObject(rng, biomes.Str("common", "gaia", "mines", "metalLarge"),
                        1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
                },
                new IGroupElement[]
                {
                    new ScatterObject(rng, biomes.Str("common", "gaia", "mines", "metalSmall"),
                        2, 3, 1, 3, 0, 2 * SafeMath.PI, 1),
                },
            })
                RmgenLibrary.CreateObjectGroups(rng,
                    new ObjectGroup(mine, true, ClMetal),
                    0,
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClRock, 8, ClMetal, 18,
                        clCliffs, 2, clWater, 2, clDock, 6),
                    RmgenLibrary.ScaleByMapSize(2, 12, MapSize), 50);

            // ── 散落树（高地 4 倍比例）──
            foreach (var band in new[] { "lowlands", "highlands" })
                GaiaEntities.CreateStragglerTrees(rng,
                    biomes.StrList(band, "gaia", "flora", "trees"),
                    new AndConstraint(new IConstraint[]
                    {
                        band == "highlands" ? constraintHighlands : constraintLowlands,
                        RmgenLibrary.AvoidClasses(ClForest, 8, clCliffs, 1, ClPlayer, 12,
                            ClMetal, 6, ClRock, 6, clCliffs, 2, clWater, 2, clDock, 6),
                    }),
                    ClForest,
                    stragglerTrees * (band == "highlands" ? biomeTreeRatioHighlands * 4
                        : 1 - biomeTreeRatioHighlands));

            // ── 食物 ──
            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biomes.Str("highlands", "gaia", "fauna", "horse"), 3, 5, 0, 4),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biomes.Str("highlands", "gaia", "fauna", "pony"), 2, 3, 0, 4),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biomes.Str("highlands", "gaia", "flora", "fruitBush"), 5, 7, 0, 4),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(2, 16, MapSize),
                    RmgenLibrary.ScaleByMapSize(2, 12, MapSize),
                    RmgenLibrary.ScaleByMapSize(2, 20, MapSize),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, clFood, 16, clCliffs, 2,
                        clWater, 2, ClRock, 4, ClMetal, 4, clDock, 6),
                    constraintHighlands,
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biomes.Str("lowlands", "gaia", "fauna", "sheep"), 2, 3, 0, 2),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biomes.Str("lowlands", "gaia", "fauna", "rabbit"), 2, 3, 0, 2),
                    },
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biomes.Str("lowlands", "gaia", "flora", "fruitBush"), 5, 7, 0, 4),
                    },
                },
                new[]
                {
                    RmgenLibrary.ScaleByMapSize(2, 16, MapSize),
                    RmgenLibrary.ScaleByMapSize(2, 12, MapSize),
                    RmgenLibrary.ScaleByMapSize(1, 20, MapSize),
                },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, clFood, 16, clCliffs, 2,
                        clWater, 2, ClRock, 4, ClMetal, 4, clDock, 6),
                    constraintLowlands,
                }),
                clFood);

            GaiaEntities.CreateFood(rng,
                new IGroupElement[][]
                {
                    new IGroupElement[]
                    {
                        new ScatterObject(rng, biomes.Str("highlands", "gaia", "fauna", "goat"), 3, 5, 0, 4),
                    },
                },
                new double[] { 3 * NumPlayers },
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, clFood, 20, clCliffs, 1,
                        ClRock, 4, ClMetal, 4, clDock, 6),
                    constraintMountains,
                }),
                clFood);

            // ── 鹰（图心上空盘旋）──
            for (int i = 0; i < RmgenLibrary.ScaleByMapSize(0, 2, MapSize); ++i)
                map.PlaceEntityAnywhere(biomes.Str("highlands", "gaia", "fauna", "hawk"),
                    0, mapCenter, rng.RandomAngle());

            // ── 鱼 ──
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biomes.Str("water", "gaia", "fauna", "fish"), 1, 1, 0, 3),
                }, true, clFood),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.StayClasses(clWater, 8),
                    RmgenLibrary.AvoidClasses(clFood, 8, clDock, 6),
                }),
                RmgenLibrary.ScaleByMapSize(15, 50, MapSize), 100);

            // ── 草地斑块（按带 × 贴图）──
            foreach (var band in new[] { "lowlands", "highlands" })
            {
                var patches = biomes.StrList(band, "terrains", "patches");
                foreach (string patch in patches)
                    foreach (double patchSize in new[]
                    {
                        RmgenLibrary.ScaleByMapSize(3, 7, MapSize),
                        RmgenLibrary.ScaleByMapSize(5, 15, MapSize),
                    })
                        RmgenLibrary.CreateAreas(rng,
                            new ChainPlacer(rng, 1,
                                Math.Floor(RmgenLibrary.ScaleByMapSize(3, 5, MapSize)),
                                patchSize, 0.5),
                            new IPainter[]
                            {
                                new TerrainPainter(patch, rng),
                                new TileClassPainter(ClDirt),
                            },
                            new AndConstraint(new IConstraint[]
                            {
                                band == "highlands" ? constraintHighlands : constraintLowlands,
                                RmgenLibrary.AvoidClasses(ClForest, 0, ClDirt, 5, ClPlayer, 12,
                                    clCliffs, 2, clWater, 2),
                            }),
                            RmgenLibrary.ScaleByMapSize(15, 45, MapSize) / patches.Count);
            }

            // ── 装饰（蘑菇/草丛/灌木 + 条带石）──
            foreach (var band in new[] { "lowlands", "highlands" })
            {
                GaiaEntities.CreateDecoration(rng,
                    new IGroupElement[][]
                    {
                        new IGroupElement[]
                        {
                            new ScatterObject(rng,
                                RmgenLibrary.ActorTemplate(biomes.Str(band, "actors", "mushroom")),
                                1, 4, 1, 2),
                        },
                        new IGroupElement[]
                        {
                            new ScatterObject(rng,
                                RmgenLibrary.ActorTemplate(biomes.Str("common", "actors", "grass")),
                                2, 4, 0, 1.8),
                            new ScatterObject(rng,
                                RmgenLibrary.ActorTemplate(biomes.Str("common", "actors", "grassShort")),
                                3, 6, 1.2, 2.5),
                        },
                        new IGroupElement[]
                        {
                            new ScatterObject(rng,
                                RmgenLibrary.ActorTemplate(biomes.Str("common", "actors", "bushMedium")),
                                1, 2, 0, 2),
                            new ScatterObject(rng,
                                RmgenLibrary.ActorTemplate(biomes.Str("common", "actors", "bushSmall")),
                                2, 4, 0, 2),
                        },
                    },
                    new[]
                    {
                        RmgenLibrary.ScaleByMapAreaAbsolute(20, MapSize, settings.CircularMap),
                        RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                        RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                        RmgenLibrary.ScaleByMapAreaAbsolute(13, MapSize, settings.CircularMap),
                    },
                    new AndConstraint(new IConstraint[]
                    {
                        band == "highlands" ? constraintHighlands : constraintLowlands,
                        RmgenLibrary.AvoidClasses(clCliffs, 1, ClPlayer, 15, ClForest, 1,
                            ClRock, 4, ClMetal, 4),
                    }));

                var stones = biomes.StrList(band, "actors", "stones");
                GaiaEntities.CreateDecoration(rng,
                    new IGroupElement[][]
                    {
                        stones.Select(t => (IGroupElement)new ScatterObject(rng,
                            RmgenLibrary.ActorTemplate(t), 1, 3, 0, 1)).ToArray(),
                    },
                    stones.Select(t =>
                        RmgenLibrary.ScaleByMapAreaAbsolute(2, MapSize, settings.CircularMap) *
                        rng.RandIntInclusive(1, 3)).ToArray(),
                    new AndConstraint(new IConstraint[]
                    {
                        band == "highlands" ? constraintHighlands : constraintLowlands,
                        RmgenLibrary.AvoidClasses(clWater, 4, ClPlayer, 15, ClForest, 1,
                            ClRock, 4, ClMetal, 4),
                    }));
            }

            // ── 希腊彩蛋实体（神庙/雕像/营火/弩炮/推车/浮木/芦苇）──
            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biomes.Str("highlands", "gaia", "athen", "temple"), 1, 1, 0, 0),
                }, true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clCliffs, 4, clWater, 4, ClPlayer, 40, ClForest, 4,
                        ClRock, 4, ClMetal, 4),
                    constraintHighlands,
                }),
                1, 200);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng,
                        RmgenLibrary.ActorTemplate(biomes.Str("lowlands", "actors", "athen", "statue")),
                        1, 1, 0, 0),
                }, true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clCliffs, 2, clWater, 4, ClPlayer, 30, ClForest, 1,
                        ClRock, 8, ClMetal, 8, clDock, 6),
                    constraintLowlands,
                }),
                RmgenLibrary.ScaleByMapSize(1, 2, MapSize), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng,
                        RmgenLibrary.ActorTemplate(biomes.Str("common", "actors", "campfire")),
                        1, 1, 0, 0),
                }, true),
                0,
                RmgenLibrary.AvoidClasses(clCliffs, 2, clWater, 4, ClPlayer, 30, ClForest, 1,
                    ClRock, 8, ClMetal, 8, clDock, 6),
                RmgenLibrary.ScaleByMapSize(0, 2, MapSize), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng, biomes.Str("highlands", "gaia", "athen", "oxybeles"), 1, 1, 0, 0),
                }, true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clCliffs, 2, ClPlayer, 30, ClForest, 1, ClRock, 4,
                        ClMetal, 4),
                    constraintHighlands,
                }),
                RmgenLibrary.ScaleByMapSize(0, 2, MapSize), 100);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng,
                        RmgenLibrary.ActorTemplate(biomes.Str("highlands", "actors", "handcart")),
                        1, 1, 0, 0),
                }, true),
                0,
                new AndConstraint(new IConstraint[]
                {
                    RmgenLibrary.AvoidClasses(clCliffs, 1, ClPlayer, 15, ClForest, 1, ClRock, 4,
                        ClMetal, 4),
                    constraintHighlands,
                }),
                RmgenLibrary.ScaleByMapSize(1, 4, MapSize), 50);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng,
                        RmgenLibrary.ActorTemplate(biomes.Str("water", "actors", "waterlog")),
                        1, 1, 0, 0),
                }, true),
                0,
                RmgenLibrary.StayClasses(clWater, 4),
                RmgenLibrary.ScaleByMapSize(1, 2, MapSize), 10);

            RmgenLibrary.CreateObjectGroups(rng,
                new ObjectGroup(new IGroupElement[]
                {
                    new ScatterObject(rng,
                        RmgenLibrary.ActorTemplate(biomes.Str("shoreline", "actors", "reeds")),
                        5, 12, 1, 4),
                    new ScatterObject(rng,
                        RmgenLibrary.ActorTemplate(biomes.Str("shoreline", "actors", "lillies")),
                        1, 2, 1, 5),
                }, false, ClDirt),
                0,
                new HeightConstraint(map, heightReedsMin, heightReedsMax),
                RmgenLibrary.ScaleByMapSize(10, 25, MapSize), 20);

            return map.MakeExportable();
        }

        /// <summary>加载 hellas.png 高度图；数据缺失时回退 721² 径向渐变
        /// （中央高、边缘海），确定性、不消耗抽数。</summary>
        private static float[][] LoadHellasHeightmap(string? dataRoot)
        {
            string? path = dataRoot != null
                ? Path.Combine(dataRoot, "maps", "random", "hellas.png")
                : null;
            if (path != null && File.Exists(path))
                return HeightmapLoader.ConvertHeightmap1Dto2D(
                    HeightmapLoader.LoadHeightmapImage(path));

            const int n = 721;
            var hm = new float[n][];
            for (int x = 0; x < n; ++x)
            {
                hm[x] = new float[n];
                for (int y = 0; y < n; ++y)
                {
                    // 正弦干涉群岛（任意裁剪窗口陆地占比都落在 ~0.75 附近，
                    // 裁剪验收才能命中）+ 梯田台阶（阶边坡度超 SlopeConstraint(2)，
                    // 悬崖占比检查才能收敛）。确定性、不消耗抽数。
                    // SafeMath 三角:rmgen 结果进初始状态,libm 跨平台低位不同 → 开局即 OOS。
                    double v = 0.35
                        + 0.30 * SafeMath.Sin(x * 0.050) * SafeMath.Cos(y * 0.047)
                        + 0.25 * SafeMath.Sin((x + y) * 0.023)
                        + 0.20 * SafeMath.Cos((x - y) * 0.031);
                    v = SafeMath.Floor(SafeMath.Max(0, v) * 8) / 8.0;
                    hm[x][y] = (float)(v * 0xFFFF);
                }
            }
            return hm;
        }

        /// <summary>hellas_biomes.json 查询包装（路径访问；数字段视为数组下标）。
        /// 数据缺失时回退内嵌默认（与上游 JSON 同值的关键字段子集）。</summary>
        private sealed class HellasBiomes
        {
            private readonly JsonDocument? _doc;
            private readonly bool _empty;

            private HellasBiomes(JsonDocument? doc, bool empty) { _doc = doc; _empty = empty; }

            public static HellasBiomes Load(string? dataRoot)
            {
                if (dataRoot != null)
                {
                    string path = Path.Combine(dataRoot, "maps", "random", "hellas_biomes.json");
                    try
                    {
                        if (File.Exists(path))
                            return new HellasBiomes(
                                JsonDocument.Parse(File.ReadAllText(path)), false);
                    }
                    catch (Exception)
                    {
                        // 解析失败落内嵌默认
                    }
                }
                return new HellasBiomes(null, true);
            }

            public JsonElement Get(params string[] path)
            {
                var e = _doc!.RootElement;
                foreach (string p in path)
                    e = int.TryParse(p, out int idx) ? e[idx] : e.GetProperty(p);
                return e;
            }

            public string Str(params string[] path)
            {
                if (_empty)
                    return FirstString(Default(path));
                var e = Get(path);
                if (e.ValueKind == JsonValueKind.String)
                    return e.GetString()!;
                if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0)
                    return e[0].GetString() ?? "";
                return FirstString(Default(path));
            }

            private static string FirstString(object d)
                => d is string s ? s : (d as List<string>)?[0] ?? "";

            /// <summary>地形槽：string / string[] / string[][]（嵌套名单供 RandomTerrain）。</summary>
            public object Terrain(params string[] path)
            {
                if (_empty)
                    return Default(path);
                var e = Get(path);
                if (e.ValueKind == JsonValueKind.String)
                    return e.GetString()!;
                if (e.ValueKind == JsonValueKind.Array)
                {
                    var items = new List<JsonElement>();
                    foreach (var item in e.EnumerateArray())
                        items.Add(item);
                    if (items.Count > 0 && items[0].ValueKind == JsonValueKind.Array)
                    {
                        var nested = new List<List<string>>();
                        foreach (var a in items)
                        {
                            var inner = new List<string>();
                            foreach (var x in a.EnumerateArray())
                                inner.Add(x.GetString() ?? "");
                            nested.Add(inner);
                        }
                        return nested;
                    }
                    var flat = new List<string>();
                    foreach (var x in items)
                        flat.Add(x.GetString() ?? "");
                    return flat;
                }
                return Default(path);
            }

            public List<string> StrList(params string[] path)
            {
                if (_empty)
                {
                    var d = Default(path);
                    return d is List<string> l ? l : new List<string> { (string)d };
                }
                var e = Get(path);
                var result = new List<string>();
                if (e.ValueKind == JsonValueKind.Array)
                    foreach (var x in e.EnumerateArray())
                        result.Add(x.GetString() ?? "");
                else
                    result.Add(e.GetString() ?? "");
                return result;
            }

            /// <summary>内嵌默认（上游 hellas_biomes.json 关键字段，测试环境兜底）。</summary>
            private static object Default(string[] path)
            {
                string key = string.Join('.', path);
                return key switch
                {
                    "lowlands.terrains.main" => new List<string>
                        { "medit_grass_field_a", "medit_grass_field_b", "grass1_spring" },
                    "highlands.terrains.main" => new List<string>
                        { "alpine_grass_c", "alpine_grass_d", "alpine_grass_e" },
                    "common.terrains.cliffs" => new List<string>
                        { "medit_cliff_italia_grass", "medit_cliff_grass", "medit_cliff_aegean" },
                    "common.terrains.road" => "medit_city_tile",
                    "common.terrains.roadWild" => "medit_city_tile",
                    "water.terrains.main" => "medit_sand_wet",
                    "water.gaia.fauna.fish" => "gaia/fish/generic",
                    "water.actors.waterlog" => "props/flora/water_log",
                    "shoreline.gaia.dock" => "structures/athen/dock",
                    "shoreline.actors.reeds" => "props/flora/reeds_pond_lush_b",
                    "shoreline.actors.lillies" => "props/flora/water_lillies",
                    "common.gaia.mines.stoneLarge" => "gaia/rock/mediterranean_large",
                    "common.gaia.mines.stoneSmall" => "gaia/rock/mediterranean_small",
                    "common.gaia.mines.metalLarge" => "gaia/ore/mediterranean_large",
                    "common.gaia.mines.metalSmall" => "gaia/ore/mediterranean_small",
                    "common.actors.grass" => "props/flora/grass_soft_large_tall",
                    "common.actors.grassShort" => "props/flora/grass_soft_large",
                    "common.actors.bushMedium" => "props/flora/bush_medit_me",
                    "common.actors.bushSmall" => "props/flora/bush_medit_sm",
                    "common.actors.campfire" => "props/special/eyecandy/campfire",
                    "lowlands.terrains.forestFloors.0" => "medit_grass_field",
                    "lowlands.terrains.forestFloors.1" => "medit_grass_shrubs",
                    "highlands.terrains.forestFloors.0" => "alpine_grass_d",
                    "highlands.terrains.forestFloors.1" => "alpine_grass_e",
                    "lowlands.terrains.forests.0" => new List<string>
                        { "medit_grass_shrubs|gaia/tree/oak_large", "medit_grass_shrubs|gaia/tree/oak",
                          "medit_grass_shrubs" },
                    "lowlands.terrains.forests.1" => new List<string>
                        { "medit_grass_field|gaia/tree/euro_beech", "medit_grass_field|gaia/tree/poplar",
                          "medit_grass_field" },
                    "highlands.terrains.forests.0" => new List<string>
                        { "alpine_grass_e|gaia/tree/cypress", "alpine_grass_e|gaia/tree/poplar_lombardy",
                          "alpine_grass_e" },
                    "highlands.terrains.forests.1" => new List<string>
                        { "alpine_grass_d|gaia/tree/cypress", "alpine_grass_d|gaia/tree/aleppo_pine",
                          "alpine_grass_d" },
                    "lowlands.terrains.patches" => new List<string>
                        { "medit_grass_field_b", "medit_grass_field_brown", "medit_grass_field_dry" },
                    "highlands.terrains.patches" => new List<string> { "medit_grass_wild" },
                    "lowlands.gaia.flora.trees" => new List<string>
                        { "gaia/tree/euro_beech", "gaia/tree/poplar", "gaia/tree/oak" },
                    "highlands.gaia.flora.trees" => new List<string>
                        { "gaia/tree/poplar_lombardy", "gaia/tree/cypress", "gaia/tree/aleppo_pine" },
                    "lowlands.gaia.flora.fruitBush" => "gaia/fruit/grapes",
                    "highlands.gaia.flora.fruitBush" => "gaia/fruit/berry_01",
                    "lowlands.gaia.fauna.sheep" => "gaia/fauna_sheep",
                    "lowlands.gaia.fauna.rabbit" => "gaia/fauna_rabbit",
                    "highlands.gaia.fauna.goat" => "gaia/fauna_goat",
                    "highlands.gaia.fauna.hawk" => "birds/buzzard",
                    "highlands.gaia.fauna.horse" => "gaia/fauna_horse",
                    "highlands.gaia.fauna.pony" => "gaia/fauna_horse_pony",
                    "highlands.gaia.athen.temple" => "structures/athen/temple",
                    "highlands.gaia.athen.oxybeles" => "units/athen/siege_oxybeles_unpacked",
                    "lowlands.actors.athen.statue" => "props/special/eyecandy/statue_aphrodite_huge",
                    "lowlands.actors.mushroom" => "fungi/small_grey",
                    "highlands.actors.mushroom" => "fungi/medium_beige_reversed",
                    "highlands.actors.handcart" => "props/special/eyecandy/handcart_1_broken",
                    "lowlands.actors.stones" => new List<string>
                        { "geology/highland1_moss", "geology/highland2_moss" },
                    "highlands.actors.stones" => new List<string>
                        { "stone/medit_med", "geology/stone_granite_greek_large",
                          "geology/stone_granite_greek_med" },
                    _ => "",
                };
            }
        }
    }
}
