using System;
using System.Collections.Generic;
using System.IO;
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
                RmgenLibrary.CreateArea(placer, new SmoothElevationPainter(rng,
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
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Solid, elevation, 2),
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
                PlacePlayerBase(map, settings, GetCivCode(settings, p), p, pos, playerTileClass,
                    biome?.RoadWild, biome?.Road);
            }
        }

        /// <summary>显式位置版 placePlayerBases——配合 PlayerPlacementCircle（上游新流程：
        /// 先 playerPlacement* 定位置，再把位置传给 placePlayerBases）。角度取位置相对图心的方向。
        /// cityPatchOuter/Inner 可覆盖 CityPatch 贴图（默认 biome 的 roadWild/road；
        /// 无 biome 图必须显式给出，否则不刷基地区）。</summary>
        public static void PlacePlayerBases(RmgenRng rng, RandomMap map, MapSettings settings,
            string baseTerrain, TileClass playerTileClass, BiomeSet? biome,
            IReadOnlyList<RmgenVector2D> playerPositions,
            string? cityPatchOuterTerrain = null, string? cityPatchInnerTerrain = null,
            IReadOnlyList<int>? playerIDs = null)
        {
            int numPlayers = GetNumPlayers(settings);
            string? outer = cityPatchOuterTerrain ?? biome?.RoadWild;
            string? inner = cityPatchInnerTerrain ?? biome?.Road;
            for (int i = 0; i < numPlayers; i++)
            {
                // 上游 placePlayerBases：PlayerPlacement=[playerIDs, playerPosition] 按序配对
                int p = playerIDs?[i] ?? (i + 1);
                var pos = playerPositions[i];
                PlacePlayerBase(map, settings, GetCivCode(settings, p), p, pos, playerTileClass, outer, inner);
            }
        }

        private static void PlacePlayerBase(RandomMap map, MapSettings settings, string civ, int playerId,
            RmgenVector2D pos, TileClass playerTileClass,
            string? cityPatchOuterTerrain, string? cityPatchInnerTerrain)
        {
            // CityPatch:外圈 outer、内圈 inner,clPlayer 标整片基地区(半径 9)。
            if (cityPatchOuterTerrain != null && cityPatchInnerTerrain != null)
            {
                for (int dz = -9; dz <= 9; dz++)
                    for (int dx = -9; dx <= 9; dx++)
                    {
                        double r = Math.Sqrt(dx * dx + dz * dz);
                        if (r > 9) continue;
                        var tp = new RmgenVector2D(pos.X + dx, pos.Y + dz);
                        if (!map.InMapBounds(tp)) continue;
                        map.SetTexture(tp, r <= 5 ? cityPatchInnerTerrain : cityPatchOuterTerrain);
                        playerTileClass.Add(tp);
                    }
            }

            // 上游 placeCivDefaultStartingEntities：civ JSON 的 StartEntities 全表——
            // CC 居中 + 其余按环形布局，统一 BUILDING_ORIENTATION=-π/4 朝向。
            // 覆盖各族完整起始阵容（germ 的 wagon、maur 的大象、kush 的医师等）。
            PlaceStartingEntities(map, pos, playerId,
                GetStartingEntities(settings.DataRoot, civ), 6, -SafeMath.PI / 4);
            playerTileClass.Add(pos);
        }

        /// <summary>civs/{civ}.json 的 StartEntities（上游 g_CivData[civ].StartEntities）。
        /// 按 dataRoot 缓存整目录；数据缺失回退通用阵容（CC + 4 平民 + 2 矛兵）。</summary>
        public static List<(string Template, int Count)> GetStartingEntities(string? dataRoot, string civ)
        {
            if (dataRoot != null)
            {
                if (s_startEntitiesCache == null || s_startEntitiesCacheRoot != dataRoot)
                {
                    s_startEntitiesCacheRoot = dataRoot;
                    s_startEntitiesCache = new Dictionary<string, List<(string, int)>>(StringComparer.Ordinal);
                    string civsDir = Path.Combine(dataRoot, "simulation", "data", "civs");
                    if (Directory.Exists(civsDir))
                        foreach (var file in Directory.GetFiles(civsDir, "*.json"))
                        {
                            try
                            {
                                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                                string code = doc.RootElement.TryGetProperty("Code", out var c)
                                    ? c.GetString() ?? "" : "";
                                if (code.Length == 0 ||
                                    !doc.RootElement.TryGetProperty("StartEntities", out var se))
                                    continue;
                                var list = new List<(string, int)>();
                                foreach (var item in se.EnumerateArray())
                                {
                                    string tmpl = item.TryGetProperty("Template", out var t)
                                        ? t.GetString() ?? "" : "";
                                    int count = item.TryGetProperty("Count", out var n)
                                        ? n.GetInt32() : 1;
                                    if (tmpl.Length > 0)
                                        list.Add((tmpl, count));
                                }
                                if (list.Count > 0)
                                    s_startEntitiesCache[code] = list;
                            }
                            catch (Exception)
                            {
                                // 单个 civ JSON 解析失败 → 跳过（回退通用阵容）
                            }
                        }
                }
                if (s_startEntitiesCache != null &&
                    s_startEntitiesCache.TryGetValue(civ, out var found))
                    return found;
            }
            return new List<(string, int)>
            {
                ($"structures/{civ}/civil_centre", 1),
                ($"units/{civ}/support_civilian", 4),
                ($"units/{civ}/infantry_spearman_b", 2),
            };
        }

        private static string? s_startEntitiesCacheRoot;
        private static Dictionary<string, List<(string Template, int Count)>>? s_startEntitiesCache;

        /// <summary>原版 playerPlacementCircle——startAngle 未给定时消耗 1 次 randomAngle(),
        /// 位置按整圆等距分布后 round。</summary>
        public static (List<int> playerIDs, List<RmgenVector2D> playerPosition, List<double> playerAngle,
            double startAngle) PlayerPlacementCircle(RmgenRng rng, RandomMap map, int numPlayers,
                double radius, double? startingAngle = null, RmgenVector2D? center = null)
        {
            double startAngle = startingAngle ?? rng.RandomAngle();
            var (points, angles) = RmgenGeometry.DistributePointsOnCircle(
                numPlayers, startAngle, radius, center ?? map.GetCenter());
            var rounded = points.Select(p => { var q = p; q.Round(); return q; }).ToList();
            return (Enumerable.Range(1, numPlayers).ToList(), rounded, angles, startAngle);
        }

        // ── wall_builder.js ──

        /// <summary>放置城墙（原版 placeFortificationWall）。骨架。</summary>
        public static void PlaceFortificationWall(RmgenRng rng, RandomMap map,
            int playerId, RmgenVector2D start, RmgenVector2D end, string wallStyle)
        {
            // TODO: 完整版按 wallStyle 查 wall pieces 长度 + 沿线放置
        }

        // ── gaia_terrain.js 河流 ──

        /// <summary>rndRiver——周期为 1 的伪正弦（paintRiver 蜿蜒曲线用）。</summary>
        private static double RndRiver(double f, double seed)
        {
            double rndRw = seed;
            for (int i = 0; i <= f; ++i)
                rndRw = 10 * (rndRw % 1);

            double rndRr = f % 1;
            double retVal = ((int)Math.Floor(f) % 2 != 0 ? -1 : 1) * rndRr * (rndRr - 1);

            int rndRe = (int)Math.Floor(rndRw) % 5;
            if (rndRe == 0)
                retVal *= 2.3 * (rndRr - 0.5) * (rndRr - 0.5);
            else if (rndRe == 1)
                retVal *= 2.6 * (rndRr - 0.3) * (rndRr - 0.7);
            else if (rndRe == 2)
                retVal *= 22 * (rndRr - 0.2) * (rndRr - 0.3) * (rndRr - 0.3) * (rndRr - 0.8);
            else if (rndRe == 3)
                retVal *= 180 * (rndRr - 0.2) * (rndRr - 0.2) * (rndRr - 0.4) *
                    (rndRr - 0.6) * (rndRr - 0.6) * (rndRr - 0.8);
            else if (rndRe == 4)
                retVal *= 2.6 * (rndRr - 0.5) * (rndRr - 0.7);
            return retVal;
        }

        /// <summary>paintRiver（逐字移植 gaia_terrain.js）——双正弦叠加的蜿蜒河道。
        /// 注意 deviation 抽数发生在每个垂直投影在河道范围内的图块上（即使 deviation=0）。
        /// 仅移植本仓地图用到的参数（无 constraint/waterFunc/landFunc/minHeight）。</summary>
        public static void PaintRiver(RmgenRng rng, RandomMap map,
            RmgenVector2D start, RmgenVector2D end, double width, double fadeDist,
            double heightRiverbed, double heightLand,
            bool parallel = false, double deviation = 0, double meanderShort = 20, double meanderLong = 10)
        {
            int mapSize = map.GetSize();

            // 蜿蜒 = 两条 rndRiver 曲线叠加
            double meanderShortT = RmgenLibrary.FractionToTiles(
                meanderShort / RmgenLibrary.ScaleByMapSize(35, 160, mapSize), mapSize);
            double meanderLongT = RmgenLibrary.FractionToTiles(
                meanderLong / RmgenLibrary.ScaleByMapSize(35, 100, mapSize), mapSize);

            // 非平行河两岸各有独立种子/起始角
            double seed1 = rng.RandFloat(2, 3);
            double seed2 = rng.RandFloat(2, 3);
            double startingAngle1 = rng.RandFloat(0, 1);
            double startingAngle2 = rng.RandFloat(0, 1);

            double RiverCurve(double riverFraction, double startAngle, double seed) =>
                meanderShortT * RndRiver(startAngle + RmgenLibrary.FractionToTiles(riverFraction, mapSize) / 128, seed) +
                meanderLongT * RndRiver(startAngle + RmgenLibrary.FractionToTiles(riverFraction, mapSize) / 256, seed);

            double riverLength = start.DistanceTo(end);
            var unitVecRiver = RmgenVector2D.Sub(start, end);
            unitVecRiver.Normalize();
            var unitVecPerpendicular = unitVecRiver.Perpendicular();

            double riverMinX = Math.Min(start.X, end.X);
            double riverMinZ = Math.Min(start.Y, end.Y);
            double riverMaxX = Math.Max(start.X, end.X);
            double riverMaxZ = Math.Max(start.Y, end.Y);

            for (int ix = 0; ix < mapSize; ++ix)
                for (int iz = 0; iz < mapSize; ++iz)
                {
                    var vecPoint = new RmgenVector2D(ix, iz);

                    // 到河道的带符号最短距离
                    double distanceToRiver = RmgenGeometry.DistanceOfPointFromLine(start, end, vecPoint);

                    // 垂足（河道上的最近点）
                    var river = RmgenVector2D.Sub(vecPoint,
                        RmgenVector2D.Mult(unitVecPerpendicular, distanceToRiver));

                    // 只处理垂直投影落在河道上的点
                    if (river.X < riverMinX || river.X > riverMaxX ||
                        river.Y < riverMinZ || river.Y > riverMaxZ)
                        continue;

                    // 沿河道方向的 0..1 坐标
                    double riverFraction = river.DistanceTo(start) / riverLength;

                    double riverCurve1 = RiverCurve(riverFraction, startingAngle1, seed1);
                    double riverCurve2 = parallel ? riverCurve1 : RiverCurve(riverFraction, startingAngle2, seed2);

                    double dev = deviation * rng.RandFloat(-1, 1);

                    double shoreDist1 = riverCurve1 + distanceToRiver - dev - width / 2;
                    double shoreDist2 = riverCurve2 + distanceToRiver - dev + width / 2;

                    if (shoreDist1 < 0 && shoreDist2 > 0)
                    {
                        double height = heightRiverbed;
                        if (shoreDist1 > -fadeDist)
                            height += (heightLand - heightRiverbed) * (1 + shoreDist1 / fadeDist);
                        else if (shoreDist2 < fadeDist)
                            height += (heightLand - heightRiverbed) * (1 - shoreDist2 / fadeDist);
                        map.SetHeight(vecPoint, height);
                    }
                }
        }

        // ── player.js 组队放置 ──

        /// <summary>utility.js shuffleArray——inside-out Fisher-Yates（randIntInclusive(0, i)）。</summary>
        public static List<T> ShuffleArray<T>(RmgenRng rng, IReadOnlyList<T> source)
        {
            if (source.Count == 0)
                return new List<T>();
            var result = new List<T> { source[0] };
            for (int i = 1; i < source.Count; ++i)
            {
                int j = rng.RandIntInclusive(0, i);
                // j==i 时 JS 里 result[i]=result[i] 读到 undefined 但随即被 result[j]=source[i] 覆盖
                if (j == i)
                {
                    result.Add(source[i]);
                }
                else
                {
                    result.Add(result[j]);
                    result[j] = source[i];
                }
            }
            return result;
        }

        /// <summary>getPlayerTeam——未设置（null）即 -1。</summary>
        public static int GetPlayerTeam(MapSettings settings, int playerId)
            => settings.PlayerData[playerId].Team ?? -1;

        /// <summary>sortPlayers——先 shuffle 再按队伍稳定排序（V8 Array.sort 稳定 → OrderBy）。</summary>
        public static List<int> SortPlayers(RmgenRng rng, MapSettings settings, List<int> playerIDs)
            => ShuffleArray(rng, playerIDs).OrderBy(id => GetPlayerTeam(settings, id)).ToList();

        /// <summary>utility.js heapsPermute——对每个排列回调（Heap 算法；值类型即逐位复制）。</summary>
        private static void HeapsPermute<T>(List<T> array, Action<List<T>> callback) where T : struct
        {
            var c = new int[array.Count];
            callback(new List<T>(array));
            int i = 0;
            while (i < array.Count)
            {
                if (c[i] < i)
                {
                    int swapIndex = i % 2 != 0 ? c[i] : 0;
                    (array[swapIndex], array[i]) = (array[i], array[swapIndex]);
                    callback(new List<T>(array));
                    ++c[i];
                    i = 0;
                }
                else
                {
                    c[i] = 0;
                    ++i;
                }
            }
        }

        /// <summary>groupPlayersByArea——排列搜索使同队玩家位置最近（按队规模加权）。</summary>
        public static (List<int> playerIDs, List<RmgenVector2D> playerPosition) GroupPlayersByArea(
            RmgenRng rng, MapSettings settings, List<int> playerIDs, List<RmgenVector2D> locations)
        {
            playerIDs = SortPlayers(rng, settings, playerIDs);

            double minDist = double.PositiveInfinity;
            List<RmgenVector2D>? minLocations = null;

            var first = ShuffleArray(rng, locations).Take(playerIDs.Count).ToList();
            HeapsPermute(first, permutation =>
            {
                double dist = 0, teamDist = 0;
                int teamSize = 0;

                for (int i = 1; i < playerIDs.Count; ++i)
                {
                    int team1 = GetPlayerTeam(settings, playerIDs[i - 1]);
                    int team2 = GetPlayerTeam(settings, playerIDs[i]);
                    ++teamSize;
                    if (team1 != -1 && team1 == team2)
                    {
                        teamDist += permutation[i - 1].DistanceTo(permutation[i]);
                    }
                    else
                    {
                        dist += teamDist / teamSize;
                        teamDist = 0;
                        teamSize = 0;
                    }
                }

                if (teamSize != 0)
                    dist += teamDist / teamSize;

                if (dist < minDist)
                {
                    minDist = dist;
                    minLocations = permutation;
                }
            });

            return (playerIDs, minLocations!);
        }

        /// <summary>playerPlacementRiver——两条平行线上交错放置（中央河道图）。
        /// angle=0 即沿 Z 轴（南北向）。返回前按 groupPlayersByArea 组队。</summary>
        public static (List<int> playerIDs, List<RmgenVector2D> playerPosition) PlayerPlacementRiver(
            RmgenRng rng, RandomMap map, MapSettings settings, double angle, double width,
            RmgenVector2D? center = null)
        {
            int numPlayers = GetNumPlayers(settings);
            bool numPlayersEven = numPlayers % 2 == 0;
            int mapSize = map.GetSize();
            var centerPosition = center ?? map.GetCenter();
            var playerPosition = new List<RmgenVector2D>();

            for (int i = 0; i < numPlayers; ++i)
            {
                bool currentPlayerEven = i % 2 == 0;

                int offsetDivident = numPlayersEven || currentPlayerEven ? (i + 1) % 2 : 0;
                int offsetDivisor = numPlayersEven ? 0 : currentPlayerEven ? +1 : -1;

                var v = new RmgenVector2D(
                    width * (i % 2) + (mapSize - width) / 2,
                    RmgenLibrary.FractionToTiles(
                        ((i - 1 + offsetDivident) / 2.0 + 1) / ((numPlayers + offsetDivisor) / 2.0 + 1),
                        mapSize));
                v.RotateAround(angle, centerPosition);
                v.Round();
                playerPosition.Add(v);
            }

            return GroupPlayersByArea(rng, settings,
                Enumerable.Range(1, numPlayers).ToList(), playerPosition);
        }

        /// <summary>sortAllPlayers——sortPlayers(getPlayerIDs())。</summary>
        public static List<int> SortAllPlayers(RmgenRng rng, MapSettings settings)
            => SortPlayers(rng, settings, Enumerable.Range(1, GetNumPlayers(settings)).ToList());

        /// <summary>defaultPlayerBaseRadius——scaleByMapSize(15, 25)。</summary>
        public static double DefaultPlayerBaseRadius(int mapSize)
            => RmgenLibrary.ScaleByMapSize(15, 25, mapSize);

        /// <summary>playerPlacementRandom——约束区内随机取点（玩家最小间距 1/4 图径、
        /// 距图心不超过 (图心-边界)），500 次失败重置并渐缩间距，重置 500 次放弃返回 null。</summary>
        public static (List<int> playerIDs, List<RmgenVector2D> playerPosition)? PlayerPlacementRandom(
            RmgenRng rng, RandomMap map, MapSettings settings, IConstraint? constraints)
        {
            int numPlayers = GetNumPlayers(settings);
            var locations = new List<RmgenVector2D>();
            int attempts = 0;
            int resets = 0;

            var mapCenter = map.GetCenter();
            double playerMinDistSquared = SafeMath.Square(RmgenLibrary.FractionToTiles(0.25, map.GetSize()));
            double borderDistance = RmgenLibrary.FractionToTiles(0.08, map.GetSize());

            var area = RmgenLibrary.CreateArea(new MapBoundsPlacer(), (IPainter?)null, constraints);
            if (area == null)
                return null;

            for (int i = 0; i < numPlayers; ++i)
            {
                var position = rng.PickRandom(area.GetPoints());
                // JS pickRandom 空数组返回 undefined —— C# 以 Count==0 判定
                if (area.PointCount == 0)
                    return null;

                // 初始基地最小间距为图径 1/4
                bool tooClose = false;
                foreach (var loc in locations)
                    if (loc.DistanceToSquared(position) < playerMinDistSquared)
                    { tooClose = true; break; }

                if (tooClose ||
                    position.DistanceToSquared(mapCenter) > SafeMath.Square(mapCenter.X - borderDistance))
                {
                    --i;
                    ++attempts;

                    // 疑似死循环则重置
                    if (attempts > 500)
                    {
                        locations = new List<RmgenVector2D>();
                        i = -1;
                        attempts = 0;
                        ++resets;

                        // 渐缩最小间距
                        if (resets % 25 == 0)
                            playerMinDistSquared *= 0.95;

                        // 只抽到坏点则放弃
                        if (resets == 500)
                            return null;
                    }
                    continue;
                }

                if (locations.Count == i)
                    locations.Add(position);
                else
                    locations[i] = position;
            }

            return GroupPlayersByArea(rng, settings,
                Enumerable.Range(1, numPlayers).ToList(), locations);
        }

        /// <summary>findLocationInDirectionBasedOnHeight——startPoint→endPoint 方向上
        /// 首个高度落在 [min,max] 的位置（再沿方向偏移 offset）。</summary>
        public static RmgenVector2D? FindLocationInDirectionBasedOnHeight(RandomMap map,
            RmgenVector2D startPoint, RmgenVector2D endPoint, double minHeight, double maxHeight,
            double offset = 0)
        {
            var stepVec = RmgenVector2D.Sub(endPoint, startPoint);
            int distance = (int)Math.Ceiling(stepVec.Length());
            stepVec.Normalize();

            for (int i = 0; i < distance; ++i)
            {
                var pos = RmgenVector2D.Add(startPoint, RmgenVector2D.Mult(stepVec, i));
                var ipos = pos;
                ipos.Round();

                if (map.ValidHeight(ipos) &&
                    map.GetHeight(ipos) >= minHeight &&
                    map.GetHeight(ipos) <= maxHeight)
                    return RmgenVector2D.Add(pos, RmgenVector2D.Mult(stepVec, offset));
            }

            return null;
        }

        /// <summary>placeStartingEntities——首实体（structures/ 前缀）居中，
        /// 其余环绕（BUILDING_ORIENTATION = -π/4 默认朝向）。</summary>
        public static void PlaceStartingEntities(RandomMap map, RmgenVector2D location, int playerID,
            IReadOnlyList<(string Template, int Count)> civEntities, double dist = 6,
            double orientation = -SafeMath.PI / 4)
        {
            int i = 0;
            string firstTemplate = civEntities[0].Template;
            if (firstTemplate.StartsWith("structures/", StringComparison.Ordinal))
            {
                map.PlaceEntityPassable(firstTemplate, playerID, location, orientation);
                ++i;
            }

            const double space = 2;
            for (int j = i; j < civEntities.Count; ++j)
            {
                double angle = orientation - SafeMath.PI * (1 - j / 2.0);
                int count = civEntities[j].Count;

                for (int num = 0; num < count; ++num)
                {
                    var a = new RmgenVector2D(dist, 0);
                    a.Rotate(-angle);
                    var b = new RmgenVector2D(space * (-num + (count - 1) / 2.0), 0);
                    b.Rotate(angle);
                    var position = RmgenVector2D.Add(RmgenVector2D.Add(location, a), b);
                    map.PlaceEntityPassable(civEntities[j].Template, playerID, position, angle);
                }
            }
        }

        /// <summary>addCivicCenterAreaToClass——CC 大小圆盘标 TileClass。</summary>
        public static void AddCivicCenterAreaToClass(RandomMap map, RmgenVector2D position, TileClass tileClass)
            => RmgenLibrary.CreateArea(new DiskPlacer(5, position), new TileClassPainter(tileClass), null);

        /// <summary>groupPlayersCycle——起始位置按最短回路排序后，
        /// 旋转玩家（按队排好序的）使同队距离最大者最小化。</summary>
        public static (List<int> playerIDs, List<RmgenVector2D> playerPosition) GroupPlayersCycle(
            RmgenRng rng, MapSettings settings, List<RmgenVector2D> startLocations)
        {
            var startLocationOrder = RmgenGeometry.SortPointsShortestCycle(startLocations);

            var newStartLocations = new List<RmgenVector2D>();
            for (int i = 0; i < startLocations.Count; ++i)
                newStartLocations.Add(startLocations[startLocationOrder[i]]);
            startLocations = newStartLocations;

            // 按队排序玩家
            var playerIDs = new List<int>();
            var teams = new List<int>();
            for (int i = 0; i < settings.PlayerData.Count - 1; ++i)
            {
                playerIDs.Add(i + 1);
                int t = settings.PlayerData[i + 1].Team ?? -1;
                if (!teams.Contains(t))
                    teams.Add(t);
            }

            playerIDs = SortPlayers(rng, settings, playerIDs);

            if (teams.Count == 0)
                return (playerIDs, startLocations);

            // 最小化队内最大距离
            double minDistance = double.PositiveInfinity;
            int bestShift = 0;
            for (int s = 0; s < playerIDs.Count; ++s)
            {
                double maxTeamDist = 0;
                for (int pi = 0; pi < playerIDs.Count - 1; ++pi)
                {
                    int t1 = GetPlayerTeam(settings, playerIDs[(pi + s) % playerIDs.Count]);
                    if (!teams.Contains(t1))
                        continue;

                    for (int pj = pi + 1; pj < playerIDs.Count; ++pj)
                    {
                        if (t1 != GetPlayerTeam(settings, playerIDs[(pj + s) % playerIDs.Count]))
                            continue;

                        maxTeamDist = Math.Max(maxTeamDist,
                            SafeMath.EuclidDistance2D(
                                startLocations[pi].X, startLocations[pi].Y,
                                startLocations[pj].X, startLocations[pj].Y));
                    }
                }

                if (maxTeamDist < minDistance)
                {
                    minDistance = maxTeamDist;
                    bestShift = s;
                }
            }

            if (bestShift != 0)
                playerIDs = playerIDs.Select((_, i) => playerIDs[(i + bestShift) % playerIDs.Count]).ToList();

            return (playerIDs, startLocations);
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
