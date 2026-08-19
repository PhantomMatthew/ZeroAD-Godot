using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>wild_lake.js（692 行）——野湖：diamond-square 起形 + 溅射侵蚀 +
    /// 重缩放，中央低洼成湖；按 8 档高度带 × 坡度中位分刷贴图/撒实体
    /// （wildLakeBiome 表）；玩家环形分布，资源点（矿/树林/营地/雇佣兵营/农场围栏）
    /// 沿高度带散布。biome 实体表 wild_lake_biomes.json（按 currentBiome() 读
    /// farmEntities/mercenaryCampEntities）。setWaterHeight 环境设置按约定省略。</summary>
    public sealed class WildLakeMap : StandardMap
    {
        protected override double HeightLand => 0;

        /// <summary>上游 wild_lake.json SupportedBiomes = "generic/"（全部 generic biome）。</summary>
        protected override IReadOnlyList<string> SupportedBiomes => BiomeLoader.KnownBiomes;

        private const double MinHeight = -RmgenConstants.SEA_LEVEL;
        private const double MaxHeight = 0xFFFF / RmgenConstants.HEIGHT_UNITS_PER_METRE - RmgenConstants.SEA_LEVEL;

        /// <summary>wildLakeBiome 高度带条目（texture/entity/textureHS/entityHS + 概率）。</summary>
        private readonly struct Band
        {
            public readonly List<string> Texture, TextureHS;
            public readonly List<string> Entity, EntityHS;
            public readonly double EntityProb, EntityHSProb;
            public Band(List<string> texture, List<string> entity, double entityProb,
                List<string> textureHS, List<string> entityHS, double entityHSProb)
            {
                Texture = texture; TextureHS = textureHS;
                Entity = entity; EntityHS = entityHS;
                EntityProb = entityProb; EntityHSProb = entityHSProb;
            }
        }

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            // 上游先 new RandomMap(0, "whiteness") 再 setBiome——基底纯贴图无抽数，
            // biome 选择抽数在地图创建后（与上游 setBiome 时序一致）
            Rng = rng;
            Settings = settings;
            MapSize = settings.Size;
            NumPlayers = RmgenCommon.GetNumPlayers(settings);
            Map = new RandomMap(rng, MapSize, 0, "whiteness", settings.CircularMap);
            RmgenLibrary.CurrentMap = Map;
            var map = Map;

            if (settings.BiomeData != null)
            {
                Biome = settings.BiomeData;
                BiomeName = "";
            }
            else
            {
                string picked = rng.PickRandom(SupportedBiomes);
                BiomeName = picked.Contains('/') ? picked : "generic/" + picked;
                Biome = BiomeLoader.Load(settings.DataRoot, picked, rng);
            }
            var biome = Biome;

            var biomeEntities = WildLakeBiomes.Load(settings.DataRoot,
                BiomeName.Length > 0 ? BiomeName : "generic/temperate");

            // ── wildLakeBiome 高度带表（0 深水 … 7 山顶森林）──
            var bands = BuildBands(biome);

            var clGaiaCamp = new TileClass(MapSize);

            // ── 基底地形（diamond-square + 侵蚀 + 平滑 + 重缩放）──
            double heightScale = (MapSize + 512) / 1024.0 / 5;
            double rangeMin = MinHeight * heightScale;
            double rangeMax = MaxHeight * heightScale;

            const double averageWaterCoverage = 1.0 / 5;
            double heightSeaGround = -MinHeight + rangeMin +
                averageWaterCoverage * (rangeMax - rangeMin);
            double heightSeaGroundAdjusted = heightSeaGround + MinHeight;

            double lowH = rangeMin;
            double medH = (rangeMin + rangeMax) / 2;

            double[][] initialHeightmap;
            if (MapSize < 256)
                initialHeightmap = new[]
                {
                    new[] { medH, medH, medH, medH, medH },
                    new[] { medH, medH, medH, medH, medH },
                    new[] { medH, medH, lowH, medH, medH },
                    new[] { medH, medH, medH, medH, medH },
                    new[] { medH, medH, medH, medH, medH },
                };
            else if (MapSize >= 384)
                initialHeightmap = new[]
                {
                    new[] { medH, medH, medH, medH, medH, medH, medH, medH },
                    new[] { medH, medH, medH, medH, medH, medH, medH, medH },
                    new[] { medH, medH, medH, medH, medH, medH, medH, medH },
                    new[] { medH, medH, medH, lowH, lowH, medH, medH, medH },
                    new[] { medH, medH, medH, lowH, lowH, medH, medH, medH },
                    new[] { medH, medH, medH, medH, medH, medH, medH, medH },
                    new[] { medH, medH, medH, medH, medH, medH, medH, medH },
                    new[] { medH, medH, medH, medH, medH, medH, medH, medH },
                };
            else
                initialHeightmap = new[]
                {
                    new[] { medH, medH, medH, medH, medH, medH },
                    new[] { medH, medH, medH, medH, medH, medH },
                    new[] { medH, medH, lowH, lowH, medH, medH },
                    new[] { medH, medH, lowH, lowH, medH, medH },
                    new[] { medH, medH, medH, medH, medH, medH },
                    new[] { medH, medH, medH, medH, medH, medH },
                };

            HeightmapLib.SetBaseTerrainDiamondSquare(rng, map.Height,
                rangeMin, rangeMax, initialHeightmap, 0.8);

            for (int i = 0; i < 5; ++i)
                HeightmapLib.SplashErodeMap(0.1, map.Height);

            RmgenLibrary.CreateArea(
                new MapBoundsPlacer(),
                new SmoothingPainter(1, 0.5, (int)Math.Ceiling(MapSize / 128.0) + 1),
                null);

            HeightmapLib.RescaleHeightmap(rangeMin, rangeMax, map.Height);

            // ── 高度带阈值 ──
            var heightLimits = new[]
            {
                rangeMin + 3.0 / 4 * (heightSeaGroundAdjusted - rangeMin),   // 0 深水
                heightSeaGroundAdjusted,                                     // 1 浅水
                heightSeaGroundAdjusted + 2.0 / 8 * (rangeMax - heightSeaGroundAdjusted),  // 2 岸
                heightSeaGroundAdjusted + 3.0 / 8 * (rangeMax - heightSeaGroundAdjusted),  // 3 低地
                heightSeaGroundAdjusted + 4.0 / 8 * (rangeMax - heightSeaGroundAdjusted),  // 4 玩家/路径
                heightSeaGroundAdjusted + 6.0 / 8 * (rangeMax - heightSeaGroundAdjusted),  // 5 高地
                heightSeaGroundAdjusted + 7.0 / 8 * (rangeMax - heightSeaGroundAdjusted),  // 6 森林下缘
                rangeMax,                                                    // 7 森林
            };
            double playerHeightMin = heightLimits[3], playerHeightMax = heightLimits[4];
            double resourceSpotMin = (heightLimits[2] + heightLimits[3]) / 2;
            double resourceSpotMax = (heightLimits[4] + heightLimits[5]) / 2;

            // ── 起始位置（高度约束 + 最大最小间距 + 最短回路组队）──
            var startLocations = HeightmapLib.GetStartLocationsByHeightmap(rng, map,
                playerHeightMin, playerHeightMax, 1000, 30, NumPlayers, settings.CircularMap)
                ?? throw new InvalidOperationException("wild_lake: no valid start locations");
            var (playerIDs, playerPosition) = RmgenCommon.GroupPlayersCycle(rng, settings, startLocations);

            // 起始点局部压平
            foreach (var position in playerPosition)
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, RmgenGeometry.DiskArea(20), 0.8, 0.8,
                        double.PositiveInfinity, position),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        map.GetHeight(position), 20),
                    null);

            // ── 图块中心高度表 → 分带 → 坡度中位分刷漆 ──
            var tchm = HeightmapLib.GetTileCenteredHeightmap(map.Height);
            var areas = new List<RmgenVector2D>[heightLimits.Length];
            for (int h = 0; h < areas.Length; ++h)
                areas[h] = new List<RmgenVector2D>();
            for (int x = 0; x < tchm.Length; ++x)
                for (int y = 0; y < tchm[0].Length; ++y)
                {
                    double minH = rangeMin;
                    for (int h = 0; h < heightLimits.Length; ++h)
                    {
                        if (tchm[x][y] >= minH && tchm[x][y] <= heightLimits[h])
                        {
                            areas[h].Add(new RmgenVector2D(x, y));
                            break;
                        }
                        minH = heightLimits[h];
                    }
                }

            var slopeMap = HeightmapLib.GetSlopeMap(map.Height);
            var minSlope = new double[heightLimits.Length];
            var maxSlope = new double[heightLimits.Length];
            for (int h = 0; h < heightLimits.Length; ++h)
            {
                minSlope[h] = double.PositiveInfinity;
                maxSlope[h] = 0;
                foreach (var point in areas[h])
                {
                    double slope = slopeMap[(int)point.X][(int)point.Y];
                    if (slope > maxSlope[h]) maxSlope[h] = slope;
                    if (slope < minSlope[h]) minSlope[h] = slope;
                }
            }

            for (int h = 0; h < heightLimits.Length; ++h)
                foreach (var point in areas[h])
                {
                    string? entity = null;
                    string texture = rng.PickRandom(bands[h].Texture);

                    if (slopeMap[(int)point.X][(int)point.Y] < (minSlope[h] + maxSlope[h]) / 2)
                    {
                        if (rng.RandBool(bands[h].EntityProb))
                            entity = rng.PickRandom(bands[h].Entity);
                    }
                    else
                    {
                        texture = rng.PickRandom(bands[h].TextureHS);
                        if (rng.RandBool(bands[h].EntityHSProb))
                            entity = rng.PickRandom(bands[h].EntityHS);
                    }

                    map.SetTexture(point, texture);

                    if (entity != null)
                        map.PlaceEntityPassable(entity, 0,
                            RmgenLibrary.RandomPositionOnTile(rng, point), rng.RandomAngle());
                }

            // ── 资源点（高度带内、距玩家 30）──
            var avoidPoints = playerPosition
                .Select(p => new HeightmapLib.HeightPoint((int)p.X, (int)p.Y, 30)).ToList();
            var resourceSpots = HeightmapLib.GetPointsByHeight(rng, map,
                resourceSpotMin, resourceSpotMax, avoidPoints,
                isCircular: settings.CircularMap)
                .Select(p => new RmgenVector2D(p.X, p.Y)).ToList();

            // ── 玩家（简化起始实体 + 起始资源环）──
            RmgenCommon.PlacePlayerBases(rng, map, settings, "whiteness",
                new TileClass(MapSize), null, playerPosition, playerIDs: playerIDs);
            foreach (var position in playerPosition)
                PlaceStartLocationResources(rng, map, biome, position);

            // ── 农场围栏（4 式样 + 镜像 4 个）──
            var otherStyle = WallBuilder.WildLakeOtherStyle(
                biomeEntities.FarmAnimal, biomeEntities.FarmBuilding);
            var fences = new List<WallBuilder.Fortress>
            {
                new("fence", new[]
                {
                    "foodBin", "farmstead", "bench",
                    "turn_0.25", "animal", "turn_0.25", "fence",
                    "turn_0.25", "animal", "turn_0.25", "fence",
                    "turn_0.25", "animal", "turn_0.25", "fence",
                }),
                new("fence", new[]
                {
                    "foodBin", "farmstead", "fence",
                    "turn_0.25", "animal", "turn_0.25", "fence",
                    "turn_0.25", "animal", "turn_0.25", "bench", "animal", "fence",
                    "turn_0.25", "animal", "turn_0.25", "fence",
                }),
                new("fence", new[]
                {
                    "foodBin", "farmstead", "turn_0.5", "bench", "turn_-0.5", "fence_short",
                    "turn_0.25", "animal", "turn_0.25", "fence",
                    "turn_0.25", "animal", "turn_0.25", "fence",
                    "turn_0.25", "animal", "turn_0.25", "fence_short", "animal", "fence",
                }),
                new("fence", new[]
                {
                    "foodBin", "farmstead", "turn_0.5", "fence_short", "turn_-0.5", "bench",
                    "turn_0.25", "animal", "turn_0.25", "fence",
                    "turn_0.25", "animal", "turn_0.25", "fence",
                    "turn_0.25", "animal", "turn_0.25", "fence_short", "animal", "fence",
                }),
                new("fence", new[]
                {
                    "foodBin", "farmstead", "fence",
                    "turn_0.25", "animal", "turn_0.25", "bench", "animal", "fence",
                    "turn_0.25", "animal", "turn_0.25", "fence_short", "animal", "fence",
                    "turn_0.25", "animal", "turn_0.25", "fence_short", "animal", "fence",
                }),
            };
            int fenceNum = fences.Count;
            for (int i = 0; i < fenceNum; ++i)
            {
                var reversed = new List<string>(fences[i].Wall);
                reversed.Reverse();
                fences.Add(new WallBuilder.Fortress("fence", reversed));
            }

            // ── 资源点放置（矿/树林/营地/雇佣兵营/农场轮替）──
            int mercenaryCamps = (int)Math.Ceiling(MapSize / 256.0);
            for (int i = 0; i < resourceSpots.Count; ++i)
            {
                double radius = 0;
                int choice = i % 5;
                if (choice == 0)
                    PlaceMine(rng, map, biome, resourceSpots[i], biome.StoneLarge);
                if (choice == 1)
                    PlaceMine(rng, map, biome, resourceSpots[i], biome.MetalLarge);
                if (choice == 2)
                    PlaceGrove(rng, map, biome, resourceSpots[i]);
                if (choice == 3)
                {
                    PlaceCamp(rng, map, biomeEntities, resourceSpots[i], clGaiaCamp);
                    radius = 5;
                }
                if (choice == 4)
                {
                    if (mercenaryCamps > 0)
                    {
                        RmgenCommon.PlaceStartingEntities(map, resourceSpots[i], 0,
                            biomeEntities.MercenaryCampEntities);
                        radius = 15;
                        --mercenaryCamps;
                    }
                    else
                    {
                        WallBuilder.PlaceCustomFortress(map, otherStyle, resourceSpots[i],
                            rng.PickRandom(fences), 0, rng.RandomAngle(), null);
                        radius = 10;
                    }
                }

                if (radius != 0)
                    RmgenLibrary.CreateArea(
                        new DiskPlacer(radius, resourceSpots[i]),
                        new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                            map.GetHeight(resourceSpots[i]), radius / 3),
                        null);
            }

            return map.MakeExportable();
        }

        // ── wild_lake.js 局部函数移植 ──

        /// <summary>placeMine——中心矿 + 11..23 个装饰环。</summary>
        private static void PlaceMine(RmgenRng rng, RandomMap map, BiomeSet biome,
            RmgenVector2D position, string centerEntity)
        {
            var decorativeActors = new[]
            {
                biome.Grass, biome.GrassShort, biome.RockLarge,
                biome.RockMedium, biome.BushMedium, biome.BushSmall,
            };

            map.PlaceEntityPassable(centerEntity, 0, position, rng.RandomAngle());

            int quantity = rng.RandIntInclusive(11, 23);
            double dAngle = 2 * SafeMath.PI / quantity;
            for (int i = 0; i < quantity; ++i)
            {
                // 实参求值顺序与上游一致：pickRandom → randFloat(2,5) → randFloat(i,i+1) → randomAngle
                string template = rng.PickRandom(decorativeActors);
                double dist = rng.RandFloat(2, 5);
                double angle = dAngle * rng.RandFloat(i, i + 1);
                double orientation = rng.RandomAngle();
                var offset = new RmgenVector2D(dist, 0);
                offset.Rotate(-angle);
                map.PlaceEntityPassable(template, 0,
                    RmgenVector2D.Add(position, offset), orientation);
            }
        }

        /// <summary>placeGrove——前哨/树中心 + 20..30 个树/装饰环 + 每点 ClumpPlacer 刷林地表。</summary>
        private static void PlaceGrove(RmgenRng rng, RandomMap map, BiomeSet biome, RmgenVector2D point)
        {
            var groveEntities = new[]
            {
                biome.Tree1, biome.Tree1, biome.Tree1, biome.Tree1, biome.Tree1,
                biome.Tree2, biome.Tree2, biome.Tree2, biome.Tree2,
                biome.Tree3, biome.Tree3, biome.Tree3,
                biome.Tree4, biome.Tree4, biome.Tree5,
            };
            var groveActors = new[] { biome.Grass, biome.RockMedium, biome.BushMedium };
            var groveTerrainTexture = new List<string> { biome.ForestFloor1 };

            string centerTemplate = rng.PickRandom(new[] { "structures/gaul/outpost", biome.Tree1 });
            double centerAngle = rng.RandomAngle();
            map.PlaceEntityPassable(centerTemplate, 0, point, centerAngle);

            int quantity = rng.RandIntInclusive(20, 30);
            double dAngle = 2 * SafeMath.PI / quantity;
            for (int i = 0; i < quantity; ++i)
            {
                double angle = dAngle * rng.RandFloat(i, i + 1);
                double dist = rng.RandFloat(2, 5);
                var objectList = i % 3 == 0 ? groveActors : groveEntities;

                var offset = new RmgenVector2D(dist, 0);
                offset.Rotate(-angle);
                var pos = RmgenVector2D.Add(point, offset);
                map.PlaceEntityPassable(rng.PickRandom(objectList), 0, pos, rng.RandomAngle());

                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, 5, 1, 1, double.PositiveInfinity, pos),
                    new TerrainPainter(groveTerrainTexture, rng),
                    null);
            }
        }

        /// <summary>placeCamp——营火 + 5..11 个营地道具环 + clGaiaCamp 标记。</summary>
        private static void PlaceCamp(RmgenRng rng, RandomMap map, WildLakeBiomes biomeEntities,
            RmgenVector2D position, TileClass clGaiaCamp)
        {
            map.PlaceEntityPassable("actor|props/special/eyecandy/campfire.xml", 0, position,
                rng.RandomAngle());

            int quantity = rng.RandIntInclusive(5, 11);
            double dAngle = 2 * SafeMath.PI / quantity;
            for (int i = 0; i < quantity; ++i)
            {
                double angle = dAngle * rng.RandFloat(i, i + 1);
                double dist = rng.RandFloat(1, 3);
                string template = rng.PickRandom(biomeEntities.CampEntities);
                double orientation = rng.RandomAngle();
                var offset = new RmgenVector2D(dist, 0);
                offset.Rotate(-angle);
                map.PlaceEntityPassable(template, 0,
                    RmgenVector2D.Add(position, offset), orientation);
            }

            RmgenCommon.AddCivicCenterAreaToClass(map, position, clGaiaCamp);
        }

        /// <summary>placeStartLocationResources——CC 周围石矿/树林/金属矿/浆果牲畜四环。</summary>
        private static void PlaceStartLocationResources(RmgenRng rng, RandomMap map, BiomeSet biome,
            RmgenVector2D point)
        {
            var foodEntities = new[] { biome.FruitBush, biome.StartingAnimal };
            var groveEntities = new[]
            {
                biome.Tree1, biome.Tree1, biome.Tree1, biome.Tree1, biome.Tree1,
                biome.Tree2, biome.Tree2, biome.Tree2, biome.Tree2,
                biome.Tree3, biome.Tree3, biome.Tree3,
                biome.Tree4, biome.Tree4, biome.Tree5,
            };
            var groveActors = new[] { biome.Grass, biome.RockMedium, biome.BushMedium };
            var groveTerrainTexture = new List<string> { biome.ForestFloor1 };

            const double averageDistToCC = 10;
            const double dAverageDistToCC = 2;
            double GetRandDist() => averageDistToCC + rng.RandFloat(-dAverageDistToCC, dAverageDistToCC);

            double currentAngle = rng.RandomAngle();

            // 石矿
            double dAngle = 4.0 / 9 * SafeMath.PI;
            double angle = currentAngle + rng.RandFloat(dAngle / 4, 3 * dAngle / 4);
            var stoneOffset = new RmgenVector2D(averageDistToCC, 0);
            stoneOffset.Rotate(-angle);
            PlaceMine(rng, map, biome, RmgenVector2D.Add(point, stoneOffset), biome.StoneLarge);
            currentAngle += dAngle;

            // 木（80 棵环 + 逐棵刷林地表）
            int quantity = 80;
            dAngle = 2.0 / 3 * SafeMath.PI / quantity;
            for (int i = 0; i < quantity; ++i)
            {
                angle = currentAngle + rng.RandFloat(0, dAngle);
                double dist = GetRandDist();
                var objectList = i % 2 == 0 ? groveActors : groveEntities;

                var offset = new RmgenVector2D(dist, 0);
                offset.Rotate(-angle);
                var position = RmgenVector2D.Add(point, offset);
                map.PlaceEntityPassable(rng.PickRandom(objectList), 0, position, rng.RandomAngle());

                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, 5, 1, 1, double.PositiveInfinity, position),
                    new TerrainPainter(groveTerrainTexture, rng),
                    null);
                currentAngle += dAngle;
            }

            // 金属矿
            dAngle = 4.0 / 9 * SafeMath.PI;
            angle = currentAngle + rng.RandFloat(dAngle / 4, 3 * dAngle / 4);
            var metalOffset = new RmgenVector2D(averageDistToCC, 0);
            metalOffset.Rotate(-angle);
            PlaceMine(rng, map, biome, RmgenVector2D.Add(point, metalOffset), biome.MetalLarge);
            currentAngle += dAngle;

            // 浆果 + 牲畜
            quantity = 15;
            dAngle = 4.0 / 9 * SafeMath.PI / quantity;
            for (int i = 0; i < quantity; ++i)
            {
                angle = currentAngle + rng.RandFloat(0, dAngle);
                double dist = GetRandDist();
                var offset = new RmgenVector2D(dist, 0);
                offset.Rotate(-angle);
                map.PlaceEntityPassable(rng.PickRandom(foodEntities), 0,
                    RmgenVector2D.Add(point, offset), rng.RandomAngle());
                currentAngle += dAngle;
            }
        }

        /// <summary>wildLakeBiome 表（wild_lake.js 常量区逐行移植）。</summary>
        private static Band[] BuildBands(BiomeSet biome)
        {
            List<string> GetArray(string s) => new() { s };
            List<string> GetArrayList(List<string> l) => l;

            var decorativeCommon = new List<string>
            {
                biome.Grass, biome.GrassShort, biome.RockLarge,
                biome.RockMedium, biome.BushMedium, biome.BushSmall,
            };
            var hillsideEntities = new List<string>
                { biome.GrassShort, biome.RockMedium, biome.BushSmall };

            // 岸边实体名单（tree1×15 + tree2×15 + 主猎物 + grass×2 + rockMedium×8 + bushMedium×8）
            var shoreEntities = new List<string>();
            for (int i = 0; i < 15; ++i) shoreEntities.Add(biome.Tree1);
            for (int i = 0; i < 15; ++i) shoreEntities.Add(biome.Tree2);
            shoreEntities.Add(biome.MainHuntableAnimal);
            shoreEntities.Add(biome.Grass); shoreEntities.Add(biome.Grass);
            for (int i = 0; i < 8; ++i) shoreEntities.Add(biome.RockMedium);
            for (int i = 0; i < 8; ++i) shoreEntities.Add(biome.BushMedium);

            return new[]
            {
                // 0 深水
                new Band(GetArray(biome.Water), new List<string> { biome.Fish }, 0.005,
                    GetArray(biome.Water), new List<string> { biome.Fish }, 0.01),
                // 1 浅水
                new Band(GetArray(biome.Water), new List<string> { biome.Lillies, biome.Reeds }, 0.3,
                    GetArray(biome.Water), new List<string> { biome.Lillies }, 0.1),
                // 2 岸
                new Band(GetArray(biome.Shore), shoreEntities, 0.3,
                    GetArrayList(biome.Cliff), hillsideEntities, 0.05),
                // 3 低地
                new Band(GetArray(biome.Tier1Terrain), decorativeCommon, 0.07,
                    GetArrayList(biome.Cliff), hillsideEntities, 0.05),
                // 4 玩家/路径高度
                new Band(GetArrayList(biome.MainTerrain), decorativeCommon, 0.07,
                    GetArrayList(biome.Cliff), hillsideEntities, 0.05),
                // 5 高地
                new Band(GetArray(biome.Tier2Terrain), decorativeCommon, 0.07,
                    GetArrayList(biome.Cliff), hillsideEntities, 0.05),
                // 6 森林下缘
                new Band(GetArrayList(biome.Dirt), new List<string>
                    {
                        biome.Tree1, biome.Tree1, biome.Tree3, biome.Tree3,
                        biome.FruitBush, biome.SecondaryHuntableAnimal,
                        biome.Grass, biome.Grass,
                        biome.RockMedium, biome.RockMedium,
                        biome.BushMedium, biome.BushMedium,
                    }, 0.25,
                    GetArrayList(biome.Cliff), hillsideEntities, 0.1),
                // 7 山顶森林
                new Band(GetArray(biome.ForestFloor1), new List<string>
                    {
                        biome.Tree1, biome.Tree2, biome.Tree3, biome.Tree4, biome.Tree5,
                        biome.Tree, biome.Grass, biome.RockMedium, biome.BushMedium,
                    }, 0.3,
                    GetArrayList(biome.Cliff), hillsideEntities, 0.1),
            };
        }

        /// <summary>wild_lake_biomes.json（farmEntities/mercenaryCampEntities/campEntities）。
        /// 数据缺失时回退上游 generic/temperate 同值。</summary>
        private sealed class WildLakeBiomes
        {
            public string FarmAnimal = "gaia/fauna_pig";
            public string FarmBuilding = "structures/mace/farmstead";
            public List<(string Template, int Count)> MercenaryCampEntities = new()
            {
                ("structures/merc_camp_egyptian", 1),
                ("units/mace/infantry_javelineer_b", 4),
                ("units/mace/cavalry_spearman_e", 3),
                ("units/mace/infantry_archer_a", 4),
                ("units/mace/champion_infantry_spearman", 3),
            };
            public List<string> CampEntities = new()
            {
                "gaia/treasure/metal", "gaia/treasure/standing_stone",
                "actor|props/special/common/waypoint_flag_factions.xml",
                "actor|props/special/eyecandy/barrel_a.xml",
                "actor|props/special/eyecandy/basket_celt_a.xml",
                "actor|props/special/eyecandy/crate_a.xml",
                "actor|props/special/eyecandy/dummy_a.xml",
                "actor|props/special/eyecandy/handcart_1.xml",
                "actor|props/special/eyecandy/handcart_1_broken.xml",
                "actor|props/special/eyecandy/sack_1.xml",
                "actor|props/special/eyecandy/sack_1_rough.xml",
            };

            public static WildLakeBiomes Load(string? dataRoot, string biomeName)
            {
                var result = new WildLakeBiomes();
                if (dataRoot != null)
                {
                    string path = Path.Combine(dataRoot, "maps", "random", "wild_lake_biomes.json");
                    try
                    {
                        if (File.Exists(path))
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(path));
                            var root = doc.RootElement;

                            // campEntities（共享道具名单）
                            if (root.TryGetProperty("campEntities", out var camp))
                            {
                                result.CampEntities = new List<string>();
                                foreach (var e in camp.EnumerateArray())
                                    result.CampEntities.Add(e.GetString() ?? "");
                            }

                            if (root.TryGetProperty(biomeName, out var biomeEl))
                            {
                                if (biomeEl.TryGetProperty("farmEntities", out var farm))
                                {
                                    if (farm.TryGetProperty("animal", out var animal))
                                        result.FarmAnimal = animal.GetString() ?? result.FarmAnimal;
                                    if (farm.TryGetProperty("building", out var building))
                                        result.FarmBuilding = building.GetString() ?? result.FarmBuilding;
                                }
                                if (biomeEl.TryGetProperty("mercenaryCampEntities", out var merc))
                                {
                                    result.MercenaryCampEntities = new();
                                    foreach (var e in merc.EnumerateArray())
                                        result.MercenaryCampEntities.Add((
                                            e.GetProperty("Template").GetString() ?? "",
                                            e.TryGetProperty("Count", out var c) ? c.GetInt32() : 1));
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // 解析失败保留内嵌默认
                    }
                }

                // guards（mercenaryCampEntities 中 units/ 前缀者）并入 campEntities
                var guards = result.MercenaryCampEntities
                    .Where(e => e.Template.Contains("units/", StringComparison.Ordinal))
                    .Select(e => e.Template);
                result.CampEntities = result.CampEntities.Concat(guards).ToList();

                return result;
            }
        }
    }
}
