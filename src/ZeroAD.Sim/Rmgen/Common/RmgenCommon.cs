using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Common
{
    /// <summary>地图设置（原版 g_MapSettings）。</summary>
    public sealed class MapSettings
    {
        public int Size = 192;
        public uint Seed = 0;
        public bool CircularMap = false;
        public List<PlayerData> PlayerData = new();
        /// <summary>binaries/data/mods/public 数据根(biome JSON 加载用);null → 内置 temperate 默认。</summary>
        public string? DataRoot;
        /// <summary>调用方预解析的 biome(选图 UI 未来可指定);null → 由地图按 SupportedBiomes 自选。</summary>
        public BiomeSet? BiomeData;
    }

    public sealed class PlayerData
    {
        public string Civ = "athen";
        public int? Team = -1;
        public string Name = "";
    }

    /// <summary>rmgen-common 高层辅助（原版 rmgen-common/ 4 文件 ~2900 行）。
    /// 包含 gaia_terrain（createBumps/createHills/createMountains/...）、
    /// gaia_entities（createDefaultForests/createBalancedMetalMines/createFood/...）、
    /// player（placePlayerBases/playerPlacementByPattern/getStartingEntities/...）、
    /// wall_builder（placeFortificationWall/placeLinearWall/...）。
    /// 骨架版——核心函数签名移植，复杂逻辑标 TODO。</summary>
    public static class RmgenCommon
    {
        // ── player.js 辅助 ──

        public static int GetNumPlayers(MapSettings settings)
            => settings.PlayerData.Count > 0 ? settings.PlayerData.Count - 1 : 0;  // index 0 = gaia

        public static string GetCivCode(MapSettings settings, int playerId)
            => playerId < settings.PlayerData.Count ? settings.PlayerData[playerId].Civ : "athen";

        public static bool AreAllies(MapSettings settings, int p1, int p2)
        {
            if (p1 >= settings.PlayerData.Count || p2 >= settings.PlayerData.Count) return false;
            var t1 = settings.PlayerData[p1].Team;
            var t2 = settings.PlayerData[p2].Team;
            return t1.HasValue && t2.HasValue && t1.Value != -1 && t1.Value == t2.Value;
        }

        // ── gaia_terrain.js ──

        /// <summary>创建起伏（原版 createBumps）。</summary>
        public static void CreateBumps(RmgenRng rng, RandomMap map, IConstraint constraint,
            int? count = null, double elevation = 2)
        {
            int n = count ?? (int)RmgenLibrary.ScaleByMapSize(100, 200, map.GetSize());
            for (int i = 0; i < n; i++)
            {
                var pos = RandomCoordinate(rng, map, passableOnly: true);
                var placer = new ChainPlacer(rng, 1,
                    (int)RmgenLibrary.ScaleByMapSize(4, 6, map.GetSize()),
                    (int)RmgenLibrary.ScaleByMapSize(2, 5, map.GetSize()), 0, pos);
                RmgenLibrary.CreateArea(placer, new SmoothElevationPainter(
                    SmoothElevationPainter.SmoothType.Blurry, elevation, 2), constraint);
            }
        }

        /// <summary>创建丘陵（原版 createHills）。terrainSet 元素为 string 或
        /// List&lt;string&gt;/string[](biome 的 cliff/hill 名单);逐层 RandomTerrain 语义——
        /// 拍平后均匀抽 = 外层先抽组再组内抽(名单重复项天然加权,如 [cliff,cliff,hill])。</summary>
        public static void CreateHills(RmgenRng rng, RandomMap map, object[] terrainSet,
            IConstraint constraint, TileClass tileClass, int? count = null, double elevation = 18)
        {
            int n = count ?? (int)(RmgenLibrary.ScaleByMapSize(1, 4, map.GetSize()) * GetNumPlayers(new MapSettings()));
            var flat = new List<string>();
            foreach (var t in terrainSet)
            {
                switch (t)
                {
                    case string s: flat.Add(s); break;
                    case IEnumerable<string> arr: flat.AddRange(arr); break;
                }
            }
            var terrain = TerrainFactory.CreateTerrain(flat);
            for (int i = 0; i < n; i++)
            {
                var pos = RandomCoordinate(rng, map, passableOnly: false);
                var placer = new ChainPlacer(rng, 1,
                    (int)RmgenLibrary.ScaleByMapSize(4, 6, map.GetSize()),
                    (int)RmgenLibrary.ScaleByMapSize(16, 40, map.GetSize()), 0.5, pos);
                RmgenLibrary.CreateArea(placer, new IPainter[] {
                    new TerrainPainter(terrain, rng),
                    new SmoothElevationPainter(SmoothElevationPainter.SmoothType.Solid, elevation, 2),
                    new TileClassPainter(tileClass)
                }, constraint);
            }
        }

        /// <summary>创建山脉（逐字移植 gaia_terrain.js createMountains）——每座山调
        /// CreateMountain(锥形高度 + 地形贴图 + tileClass)。terrain 可为 string 或
        /// IEnumerable&lt;string&gt;(名单 → 逐格 RandomTerrain,同上游 createTerrain(数组))。
        /// q 队列参数上游未用,不移植。</summary>
        public static void CreateMountains(RmgenRng rng, RandomMap map, object terrain,
            IConstraint constraint, TileClass tileClass, int? count = null, double? maxHeight = null,
            int minRadius = 0, int maxRadius = 0, int numCircles = 0)
        {
            int size = map.GetSize();
            int n = count ?? (int)(RmgenLibrary.ScaleByMapSize(1, 4, size) * GetNumPlayers(new MapSettings()));
            for (int i = 0; i < n; i++)
                CreateMountain(rng, map,
                    maxHeight ?? Math.Floor(RmgenLibrary.ScaleByMapSize(30, 50, size)),
                    minRadius > 0 ? minRadius : (int)Math.Floor(RmgenLibrary.ScaleByMapSize(3, 4, size)),
                    maxRadius > 0 ? maxRadius : (int)Math.Floor(RmgenLibrary.ScaleByMapSize(6, 12, size)),
                    numCircles > 0 ? numCircles : (int)Math.Floor(RmgenLibrary.ScaleByMapSize(4, 10, size)),
                    constraint,
                    rng.RandIntExclusive(0, size),
                    rng.RandIntExclusive(0, size),
                    terrain, tileClass, 14);
        }

        /// <summary>逐字移植 createMountain:ChainPlacer 式团块生长(gotRet: -1 未访 /
        /// -2 在圆内 / >=0 边索引),再逐圆堆锥形高度并刷 terrain/标 tileClass。
        /// JS Math.round 对非负值 = floor(v+0.5),此处高度恒非负,按此实现。</summary>
        private static void CreateMountain(RmgenRng rng, RandomMap map, double maxHeight,
            int minRadius, int maxRadius, int numCircles, IConstraint constraint,
            int x, int z, object terrain, TileClass tileClass, int fcc)
        {
            var position = new RmgenVector2D(x, z);
            if (!map.InMapBounds(position) || !constraint.Allows(position)) return;

            var terrainObj = terrain is string s
                ? TerrainFactory.CreateTerrain(s)
                : TerrainFactory.CreateTerrain((System.Collections.Generic.IEnumerable<string>)terrain);

            int mapSize = map.GetSize();
            var gotRet = new int[mapSize, mapSize];
            for (int i = 0; i < mapSize; i++)
                for (int j = 0; j < mapSize; j++)
                    gotRet[i, j] = -1;

            minRadius = Math.Max(1, Math.Min(minRadius, maxRadius));
            mapSize--;   // 原版 --mapSize:此后用作上限索引

            var edges = new List<(int x, int z)> { (x, z) };
            var circles = new List<(int cx, int cz, int r)>();

            for (int i = 0; i < numCircles; i++)
            {
                bool badPoint = false;
                var (cx, cz) = rng.PickRandom(edges);
                int radius = rng.RandIntInclusive(minRadius, maxRadius);

                int sx = Math.Max(0, cx - radius), sz = Math.Max(0, cz - radius);
                int lx = Math.Min(cx + radius, mapSize), lz = Math.Min(cz + radius, mapSize);
                int radius2 = radius * radius;

                for (int ix = sx; ix <= lx && !badPoint; ix++)
                {
                    for (int iz = sz; iz <= lz; iz++)
                    {
                        // euclidDistance2D 平方距离 vs radius²(整数精确,无浮点)
                        int dx = ix - cx, dz = iz - cz;
                        if (dx * dx + dz * dz > radius2 || !map.InMapBounds(new RmgenVector2D(ix, iz)))
                            continue;
                        if (!constraint.Allows(new RmgenVector2D(ix, iz)))
                        {
                            badPoint = true;
                            break;
                        }

                        int state = gotRet[ix, iz];
                        if (state == -1)
                        {
                            gotRet[ix, iz] = -2;
                        }
                        else if (state >= 0)
                        {
                            edges.RemoveAt(state);
                            gotRet[ix, iz] = -2;
                            for (int k = state; k < edges.Count; k++)
                                gotRet[edges[k].x, edges[k].z]--;
                        }
                    }
                }
                if (badPoint) continue;

                circles.Add((cx, cz, radius));

                for (int ix = sx; ix <= lx; ix++)
                    for (int iz = sz; iz <= lz; iz++)
                    {
                        if (gotRet[ix, iz] != -2 ||
                            fcc != 0 && (x - ix > fcc || ix - x > fcc || z - iz > fcc || iz - z > fcc) ||
                            ix > 0 && gotRet[ix - 1, iz] == -1 ||
                            iz > 0 && gotRet[ix, iz - 1] == -1 ||
                            ix < mapSize && gotRet[ix + 1, iz] == -1 ||
                            iz < mapSize && gotRet[ix, iz + 1] == -1)
                            continue;
                        edges.Add((ix, iz));
                        gotRet[ix, iz] = edges.Count - 1;
                    }
            }

            foreach (var (cx, cz, radius) in circles)
            {
                int sx = Math.Max(0, cx - radius), sz = Math.Max(0, cz - radius);
                int lx = Math.Min(cx + radius, mapSize), lz = Math.Min(cz + radius, mapSize);
                double clumpHeight = (double)radius / maxRadius * maxHeight * rng.RandFloat(0.8, 1.2);

                for (int ix = sx; ix <= lx; ix++)
                    for (int iz = sz; iz <= lz; iz++)
                    {
                        double distance = SafeMath.Sqrt((ix - cx) * (ix - cx) + (iz - cz) * (iz - cz));
                        // 原版:randIntInclusive(0,2) 的抽数在 distance 检查**之前**消费
                        int jitter = rng.RandIntInclusive(0, 2);
                        int newHeight = jitter +
                            (int)SafeMath.Floor(2.0 / 3 * clumpHeight *
                                (SafeMath.Sin(SafeMath.PI * 2.0 / 3 * (3.0 / 4 - distance / radius)) + 0.5) + 0.5);

                        if (distance > radius) continue;

                        var dp = new RmgenVector2D(ix, iz);
                        if (map.GetHeight(dp) < newHeight)
                            map.SetHeight(dp, newHeight);
                        else if (map.GetHeight(dp) >= newHeight && map.GetHeight(position) < newHeight + 4)
                            map.SetHeight(dp, newHeight + 4);

                        // 原版 createTerrain(terrain).place(dp)——名单逐格 RandomTerrain 抽选
                        terrainObj.Place(map, rng, dp);
                        tileClass.Add(dp);
                    }
            }
        }

        // ── gaia_entities.js ──

        /// <summary>树木数量（原版 getTreeCounts）。</summary>
        public static (int forestTrees, int stragglerTrees) GetTreeCounts(
            int minTrees, int maxTrees, double forestRatio, int mapSize)
        {
            double scaled = RmgenLibrary.ScaleByMapSize(minTrees, maxTrees, mapSize);
            return ((int)(forestRatio * scaled), (int)((1 - forestRatio) * scaled));
        }

        /// <summary>原版 createAreas:每次尝试把 placer 重定位到 randomCoordinate(false),
        /// retryPlacing 语义——amount 次成功为止,失败上限 amount×retryFactor。</summary>
        public static int CreateAreas(RmgenRng rng, RandomMap map, IPlacer placer,
            IEnumerable<IPainter> painters, IConstraint? constraint, int amount, int retryFactor = 10)
        {
            int placed = 0, bad = 0, maxFail = amount * retryFactor;
            while (placed < amount && bad <= maxFail)
            {
                if (placer is ChainPlacer cp)
                    cp.SetCenterPosition(RandomCoordinate(rng, map, passableOnly: false));
                else if (placer is ClumpPlacer kp)
                    kp.SetCenterPosition(RandomCoordinate(rng, map, passableOnly: false));
                var area = RmgenLibrary.CreateArea(placer, painters, constraint);
                if (area != null) placed++;
                else bad++;
            }
            return placed;
        }

        /// <summary>原版 createForests(gaia_entities.js):terrainSet =
        /// [mainTerrain, forestFloor1, forestFloor2, forestTree1, forestTree2],
        /// 其中 forestTree* 可为 string[]("ff|tree" 混合列表)。两种森林变体,
        /// 边界稀内部密,LayeredPainter([border, interior], [2])。</summary>
        public static void CreateForests(RmgenRng rng, RandomMap map,
            object[] terrainSet, IConstraint constraint, TileClass tileClass,
            int numberOfForests, int treesPerForest, int retryFactor = 10)
        {
            if (numberOfForests <= 0 || treesPerForest <= 0) return;
            object main = terrainSet[0], ff1 = terrainSet[1], ff2 = terrainSet[2];
            object tree1 = terrainSet[3], tree2 = terrainSet[4];

            var variants = new (object[] border, object[] interior)[]
            {
                (new[] { ff2, main, tree1 }, new[] { ff2, tree1 }),
                (new[] { ff1, main, tree2 }, new[] { ff1, tree2 }),
            };

            foreach (var v in variants)
            {
                var placer = new ChainPlacer(rng, 1,
                    (int)RmgenLibrary.ScaleByMapSize(3, 5, map.GetSize()),
                    treesPerForest, 0.5);
                CreateAreas(rng, map, placer, new IPainter[]
                {
                    new LayeredPainter(new object[] { v.border, v.interior }, new[] { 2 }, rng),
                    new TileClassPainter(tileClass),
                }, constraint, numberOfForests, retryFactor);
            }
        }

        /// <summary>原版 createDefaultForests:g_DefaultNumberOfForests = scaleByMapSize(8,36)。</summary>
        public static void CreateDefaultForests(RmgenRng rng, RandomMap map,
            object[] terrainSet, IConstraint constraint, TileClass tileClass, int totalNumberOfTrees)
        {
            int nbForests = (int)RmgenLibrary.ScaleByMapSize(8, 36, map.GetSize());
            if (nbForests <= 0) return;
            // 上游 numCircles = numberOfTrees/numberOfForests(JS 浮点,for i<numCircles ⇒ ceil)
            int treesPerForest = (int)System.Math.Ceiling((double)totalNumberOfTrees / nbForests);
            CreateForests(rng, map, terrainSet, constraint, tileClass, nbForests, treesPerForest);
        }

        /// <summary>原版 createPatches(gaia_terrain.js):多尺寸泥/草斑块破单调。</summary>
        public static void CreatePatches(RmgenRng rng, RandomMap map,
            double[] sizes, string terrain, IConstraint constraint, int count,
            TileClass tileClass, double failFraction = 0.5)
        {
            foreach (double size in sizes)
            {
                var placer = new ChainPlacer(rng, 1,
                    (int)RmgenLibrary.ScaleByMapSize(3, 5, map.GetSize()),
                    (int)size, failFraction);
                CreateAreas(rng, map, placer, new IPainter[]
                {
                    new TerrainPainter(TerrainFactory.CreateTerrain(terrain), rng),
                    new TileClassPainter(tileClass),
                }, constraint, count);
            }
        }

        /// <summary>原版 createLayeredPatches:斑块按到边界距离分层刷多种贴图。</summary>
        public static void CreateLayeredPatches(RmgenRng rng, RandomMap map,
            double[] sizes, object[] terrains, int[] widths, IConstraint constraint, int count,
            TileClass tileClass, double failFraction = 0.5)
        {
            foreach (double size in sizes)
            {
                var placer = new ChainPlacer(rng, 1,
                    (int)RmgenLibrary.ScaleByMapSize(3, 5, map.GetSize()),
                    (int)size, failFraction);
                CreateAreas(rng, map, placer, new IPainter[]
                {
                    new LayeredPainter(terrains, widths, rng),
                    new TileClassPainter(tileClass),
                }, constraint, count);
            }
        }

        /// <summary>创建默认森林（简化版：随机放树）——保留给旧调用方,新管线用 object[] 版。</summary>
        public static void CreateDefaultForests(RmgenRng rng, RandomMap map,
            string[] terrainSet, IConstraint constraint, TileClass tileClass,
            (int forestTrees, int stragglerTrees) treeCounts, int numPlayers)
        {
            string treeTemplate = "gaia/tree/oak_large";
            // 放置森林：每片 ~10 棵树
            int forests = treeCounts.forestTrees / 10;
            for (int i = 0; i < forests; i++)
            {
                var pos = RandomCoordinate(rng, map, passableOnly: true);
                if (!map.ValidTilePassable(pos) || !constraint.Allows(pos)) continue;
                // 每片森林放一棵树（简化——完整版用 ClumpPlacer + LayeredPainter）
                map.SetTerrainEntity(treeTemplate, 0, pos, rng.RandFloat(0, 2 * SafeMath.PI));
                tileClass.Add(pos);
            }
        }

        /// <summary>创建金属矿（每玩家附近放一个大矿）。</summary>
        public static void CreateBalancedMetalMines(RmgenRng rng, RandomMap map,
            string metalTemplate, IConstraint constraint, TileClass tileClass)
        {
            // 简化版：随机放 N 个矿
            int count = (int)RmgenLibrary.ScaleByMapSize(2, 6, map.GetSize());
            for (int i = 0; i < count; i++)
            {
                var pos = RandomCoordinate(rng, map, passableOnly: true);
                if (!constraint.Allows(pos)) continue;
                map.SetTerrainEntity(metalTemplate, 0, pos, rng.RandFloat(0, 2 * SafeMath.PI));
                tileClass.Add(pos);
            }
        }

        /// <summary>创建石矿（每玩家附近放一个大矿）。</summary>
        public static void CreateBalancedStoneMines(RmgenRng rng, RandomMap map,
            string stoneTemplate, IConstraint constraint, TileClass tileClass)
        {
            int count = (int)RmgenLibrary.ScaleByMapSize(2, 6, map.GetSize());
            for (int i = 0; i < count; i++)
            {
                var pos = RandomCoordinate(rng, map, passableOnly: true);
                if (!constraint.Allows(pos)) continue;
                map.SetTerrainEntity(stoneTemplate, 0, pos, rng.RandFloat(0, 2 * SafeMath.PI));
                tileClass.Add(pos);
            }
        }

        /// <summary>创建食物来源（随机放动物群）。</summary>
        public static void CreateFood(RmgenRng rng, RandomMap map,
            string[] animalTemplates, IConstraint constraint, TileClass tileClass)
        {
            int count = (int)RmgenLibrary.ScaleByMapSize(10, 30, map.GetSize());
            for (int i = 0; i < count; i++)
            {
                var pos = RandomCoordinate(rng, map, passableOnly: true);
                if (!constraint.Allows(pos)) continue;
                string tmpl = animalTemplates.Length > 0
                    ? rng.PickRandom(new System.Collections.Generic.List<string>(animalTemplates))
                    : "gaia/fauna_deer";
                map.SetTerrainEntity(tmpl, 0, pos, rng.RandFloat(0, 2 * SafeMath.PI));
                tileClass.Add(pos);
            }
        }

        /// <summary>创建装饰物（随机放岩石/草丛）。</summary>
        public static void CreateDecoration(RmgenRng rng, RandomMap map,
            string[] decorativeTemplates, IConstraint constraint)
        {
            int count = (int)RmgenLibrary.ScaleByMapSize(20, 60, map.GetSize());
            for (int i = 0; i < count; i++)
            {
                var pos = RandomCoordinate(rng, map, passableOnly: true);
                if (!constraint.Allows(pos)) continue;
                string tmpl = decorativeTemplates.Length > 0
                    ? rng.PickRandom(new System.Collections.Generic.List<string>(decorativeTemplates))
                    : "actor|geology/stone_granite_med.xml";
                map.SetTerrainEntity(tmpl, 0, pos, rng.RandFloat(0, 2 * SafeMath.PI));
            }
        }

        /// <summary>创建散落树木（原版 createStragglerTrees）。</summary>
        public static void CreateStragglerTrees(RmgenRng rng, RandomMap map,
            string[] treeTemplates, IConstraint constraint, TileClass tileClass,
            int count)
        {
            for (int i = 0; i < count; i++)
            {
                var pos = RandomCoordinate(rng, map, passableOnly: true);
                if (!constraint.Allows(pos)) continue;
                string tmpl = treeTemplates.Length > 0
                    ? rng.PickRandom(new System.Collections.Generic.List<string>(treeTemplates))
                    : "gaia/tree/oak";
                map.SetTerrainEntity(tmpl, 0, pos, rng.RandFloat(0, 2 * SafeMath.PI));
                tileClass.Add(pos);
            }
        }

        // ── player.js 放置 ──

        /// <summary>放置玩家基地（原版 placePlayerBases）。骨架 + 起始单位 + CityPatch 刷漆。
        /// 原版完整版用 playerPlacementByPattern 选位置 + placePlayerBaseBuildings
        /// （含浆果/矿/初始树线）;本版:CC + 3 村 2 兵 + 基地区刷 roadWild(外)/road(内)
        /// 并标 clPlayer 半径(原版 CityPatch.outerTerrain/innerTerrain 语义——基地区不再长森林/斑块)。</summary>
        public static void PlacePlayerBases(RmgenRng rng, RandomMap map, MapSettings settings,
            string baseTerrain, TileClass playerTileClass, BiomeSet? biome = null)
        {
            int numPlayers = GetNumPlayers(settings);
            for (int p = 1; p <= numPlayers; p++)
            {
                double angle = (double)(p - 1) / numPlayers * 2 * Math.PI;
                double dist = map.GetSize() * 0.35;
                double x = map.GetSize() / 2.0 + dist * Math.Cos(angle);
                double z = map.GetSize() / 2.0 + dist * Math.Sin(angle);
                var pos = new RmgenVector2D(x, z);
                pos.Floor();
                var civ = GetCivCode(settings, p);

                // CityPatch:外圈 roadWild、内圈 road,clPlayer 标整片基地区(半径 9)。
                if (biome != null)
                {
                    for (int dz = -9; dz <= 9; dz++)
                        for (int dx = -9; dx <= 9; dx++)
                        {
                            double r = Math.Sqrt(dx * dx + dz * dz);
                            if (r > 9) continue;
                            var tp = new RmgenVector2D(pos.X + dx, pos.Y + dz);
                            if (!map.InMapBounds(tp)) continue;
                            map.SetTexture(tp, r <= 5 ? biome.Road : biome.RoadWild);
                            playerTileClass.Add(tp);
                        }
                }

                map.PlaceEntityAnywhere($"structures/{civ}/civil_centre", p, pos, (float)angle);
                playerTileClass.Add(pos);

                // 起始单位(原版 placePlayerBases 的 units 组;兵种模板与 skirmish 占位同系)
                for (int i = 0; i < 3; i++)
                {
                    double a = angle + 0.9 + i * 0.5;
                    var up = new RmgenVector2D(pos.X + 6 * Math.Cos(a), pos.Y + 6 * Math.Sin(a));
                    up.Floor();
                    map.PlaceEntityAnywhere($"units/{civ}/support_female_citizen", p, up, (float)a);
                }
                for (int i = 0; i < 2; i++)
                {
                    double a = angle - 0.9 - i * 0.5;
                    var up = new RmgenVector2D(pos.X + 7 * Math.Cos(a), pos.Y + 7 * Math.Sin(a));
                    up.Floor();
                    map.PlaceEntityAnywhere($"units/{civ}/infantry_spearman_b", p, up, (float)a);
                }
            }
        }

        // ── wall_builder.js ──

        /// <summary>放置城墙（原版 placeFortificationWall）。骨架。</summary>
        public static void PlaceFortificationWall(RmgenRng rng, RandomMap map,
            int playerId, RmgenVector2D start, RmgenVector2D end, string wallStyle)
        {
            // TODO: 完整版按 wallStyle 查 wall pieces 长度 + 沿线放置
        }

        // ── 辅助 ──

        /// <summary>随机地图坐标（原版 RandomMap.randomCoordinate）。</summary>
        public static RmgenVector2D RandomCoordinate(RmgenRng rng, RandomMap map, bool passableOnly)
        {
            if (map.IsCircularMap())
            {
                double border = passableOnly ? RmgenConstants.MAP_BORDER_WIDTH : 0;
                var center = map.GetCenter();
                double r = (map.GetSize() / 2.0 - border) * SafeMath.Sqrt(rng.RandFloat(0, 1));
                var offset = new RmgenVector2D(r, 0);
                offset.Rotate(rng.RandomAngle());
                offset.Floor();
                return RmgenVector2D.Add(center, offset);
            }
            else
            {
                int border = passableOnly ? RmgenConstants.MAP_BORDER_WIDTH : 0;
                int size = map.GetSize();
                return new RmgenVector2D(
                    rng.RandIntExclusive(border, size - border),
                    rng.RandIntExclusive(border, size - border));
            }
        }
    }
}
