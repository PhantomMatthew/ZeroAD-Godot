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
        /// <summary>Nomad 模式(无 CC 开局;原版 g_MapSettings.Nomad——placePlayerBase 直接跳过,
        /// 只放起始单位)。</summary>
        public bool Nomad;
        /// <summary>玩家布置模式(gamesetup PlayerPlacement 下发;"circle" 默认。
        /// 仅被 playerPlacementByPattern 系地图(arctic_summer/archipelago/african_plains)读取。</summary>
        public string PlayerPlacement = "circle";
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
            int treesPerForest = (int)SafeMath.Ceil((double)totalNumberOfTrees / nbForests);
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
            string baseTerrain, TileClass playerTileClass, BiomeSet? biome = null,
            PlayerBaseOptions? options = null)
        {
            int numPlayers = GetNumPlayers(settings);
            for (int p = 1; p <= numPlayers; p++)
            {
                // SafeMath 三角:玩家出生点决定初始实体位置,libm 跨平台差异 → 开局即 OOS。
                double angle = (double)(p - 1) / numPlayers * 2 * SafeMath.PI;
                double dist = map.GetSize() * 0.35;
                double x = map.GetSize() / 2.0 + dist * SafeMath.Cos(angle);
                double z = map.GetSize() / 2.0 + dist * SafeMath.Sin(angle);
                var pos = new RmgenVector2D(x, z);
                pos.Floor();
                PlacePlayerBase(map, rng, settings, GetCivCode(settings, p), p, pos, playerTileClass,
                    biome?.RoadWild, biome?.Road, 0, 0.6, 0.3, options);
            }
        }

        /// <summary>显式位置版 placePlayerBases——配合 PlayerPlacementCircle（上游新流程：
        /// 先 playerPlacement* 定位置，再把位置传给 placePlayerBases）。
        /// cityPatchOuter/Inner 可覆盖 CityPatch 贴图（string 或名单；默认 biome 的
        /// roadWild/road；无 biome 图必须显式给出，否则不刷基地区）。
        /// cityPatchRadius=0 表示上游默认 defaultPlayerBaseRadius()/3。</summary>
        public static void PlacePlayerBases(RmgenRng rng, RandomMap map, MapSettings settings,
            string baseTerrain, TileClass playerTileClass, BiomeSet? biome,
            IReadOnlyList<RmgenVector2D> playerPositions,
            object? cityPatchOuterTerrain = null, object? cityPatchInnerTerrain = null,
            IReadOnlyList<int>? playerIDs = null,
            double cityPatchRadius = 0, double cityPatchCoherence = 0.6, double cityPatchSmoothness = 0.3,
            PlayerBaseOptions? options = null,
            Func<int, PlayerBaseOptions>? optionsFactory = null)
        {
            int numPlayers = GetNumPlayers(settings);
            object? outer = cityPatchOuterTerrain ?? biome?.RoadWild;
            object? inner = cityPatchInnerTerrain ?? biome?.Road;
            for (int i = 0; i < numPlayers; i++)
            {
                // 上游 placePlayerBases：PlayerPlacement=[playerIDs, playerPosition] 按序配对
                int p = playerIDs?[i] ?? (i + 1);
                var pos = playerPositions[i];
                // optionsFactory 逐玩家求值（hellas 按所在海拔带选不同动物/树,
                // 抽数发生在每玩家 args 构建时,与上游一致）
                var opt = optionsFactory?.Invoke(p) ?? options;
                PlacePlayerBase(map, rng, settings, GetCivCode(settings, p), p, pos, playerTileClass,
                    outer, inner, cityPatchRadius, cityPatchCoherence, cityPatchSmoothness, opt);
            }
        }

        private static void PlacePlayerBase(RandomMap map, RmgenRng rng, MapSettings settings,
            string civ, int playerId, RmgenVector2D pos, TileClass playerTileClass,
            object? cityPatchOuterTerrain, object? cityPatchInnerTerrain,
            double cityPatchRadius, double cityPatchCoherence, double cityPatchSmoothness,
            PlayerBaseOptions? options)
        {
            // Nomad（上游 placePlayerBase 首行即 return）：无 CC/基地区,
            // 只放非建筑起始单位（上游 placePlayersNomad 另摇随机点;本版用布置位）。
            if (settings.Nomad)
            {
                var unitsOnly = GetStartingEntities(settings.DataRoot, civ)
                    .Where(t => !t.Template.StartsWith("structures/", StringComparison.Ordinal))
                    .ToList();
                if (unitsOnly.Count > 0)
                    PlaceStartingEntities(map, pos, playerId, unitsOnly, 6, -SafeMath.PI / 4);
                return;
            }

            // 上游 placePlayerBase 顺序:placeCivDefaultStartingEntities →
            // addCivicCenterAreaToClass(半径 5)→ g_PlayerBaseFunctions
            // [CityPatch, Trees, Mines, Treasures, Berries, StartingAnimal, Decoratives]。
            PlaceStartingEntities(map, pos, playerId,
                GetStartingEntities(settings.DataRoot, civ), 6, -SafeMath.PI / 4);
            playerTileClass.Add(pos);
            RmgenLibrary.CreateArea(new DiskPlacer(5, pos),
                new TileClassPainter(playerTileClass), null);

            // CityPatch（逐字移植 placePlayerBaseCityPatch）：ClumpPlacer 噪声团块
            // （默认半径 defaultPlayerBaseRadius()/3）+ LayeredPainter 外圈 1 格分层。
            if (cityPatchOuterTerrain != null && cityPatchInnerTerrain != null)
            {
                double radius = cityPatchRadius > 0
                    ? cityPatchRadius
                    : DefaultPlayerBaseRadius(map.GetSize()) / 3;
                RmgenLibrary.CreateArea(
                    new ClumpPlacer(rng, Math.Floor(RmgenGeometry.DiskArea(radius)),
                        cityPatchCoherence, cityPatchSmoothness, double.PositiveInfinity, pos),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[] { cityPatchOuterTerrain, cityPatchInnerTerrain },
                            new double[] { 1 }, rng),
                        new TileClassPainter(playerTileClass),
                    },
                    null);
            }

            // 逐基地资源（完整版;options=null 时全跳过,保持旧简化行为）
            if (options != null)
            {
                IConstraint baseResourceConstraint = options.BaseResourceClass != null
                    ? RmgenLibrary.AvoidClasses(options.BaseResourceClass, 4)
                    : (IConstraint)new NullConstraint();
                if (options.ExtraBaseResourceConstraint != null)
                    baseResourceConstraint = new AndConstraint(new[]
                        { baseResourceConstraint, options.ExtraBaseResourceConstraint });

                if (options.TreesTemplate != null)
                    PlacePlayerBaseTrees(rng, map, options, pos, baseResourceConstraint);
                if (options.Mines != null)
                    PlacePlayerBaseMines(rng, map, options, pos, baseResourceConstraint);
                if (options.Treasures != null)
                    PlacePlayerBaseTreasures(rng, map, options, pos, baseResourceConstraint);
                if (options.BerriesTemplate != null)
                    PlacePlayerBaseBerries(rng, map, options, pos, baseResourceConstraint);
                if (options.StartingAnimal)
                    PlacePlayerBaseStartingAnimal(rng, map, settings, options, pos, baseResourceConstraint);
                if (options.DecorativesTemplate != null)
                    PlacePlayerBaseDecoratives(rng, map, options, pos, baseResourceConstraint);
            }
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

        // ══════════ 完整版 placePlayerBases 逐基地资源（原版 player.js 的
        // placePlayerBase{Trees,Mines,Treasures,Berries,StartingAnimal,Decoratives}）══════════

        /// <summary>placePlayerBases 逐基地资源配置（对应上游 playerBaseArgs 的稀疏参数;
        /// 字段为 null/false = 该项不放）。字段默认值逐一对齐上游各 placePlayerBase* 函数。</summary>
        public sealed class PlayerBaseOptions
        {
            /// <summary>基地资源标记 class（浆果/矿/树线落点互斥 + 约束）。</summary>
            public TileClass? BaseResourceClass;

            // StartingAnimal（上游 "StartingAnimal": {} 存在即放;count 默认按鸡肉量换算）
            public bool StartingAnimal;
            public string StartingAnimalTemplate = "gaia/fauna_chicken";
            public int StartingAnimalGroupCount = 2;
            public double StartingAnimalDistance = 9;
            public int? StartingAnimalCount;
            public int? StartingAnimalMinGroupCount, StartingAnimalMaxGroupCount;
            public double StartingAnimalMinGroupDistance = 0, StartingAnimalMaxGroupDistance = 2;

            // Berries
            public string? BerriesTemplate;
            public int BerriesMinCount = 5, BerriesMaxCount = 5;
            public double BerriesDistance = 12;

            // Mines（Type=="stone_formation" 走 createStoneMineFormation）
            public List<(string Template, string? Type, object? Terrain)>? Mines;
            public double MinesDistance = 12;
            public double MinesMinAngle = Math.PI / 6, MinesMaxAngle = Math.PI / 3;
            /// <summary>矿点附属小件（oasis 的 shuffleArray(...)——调用方用 rng 洗好传入）。</summary>
            public List<IGroupElement>? MinesGroupElements;

            /// <summary>叠加进 baseResourceConstraint 的额外约束（上游 playerBaseArgs.
            /// baseResourceConstraint;hellas 的 avoidClasses(clPlayer,4,clWater,1,clCliffs,1)）。</summary>
            public IConstraint? ExtraBaseResourceConstraint;

            // Trees
            public string? TreesTemplate;
            public int? TreesCount;              // null → floor(scaleByMapSize(7, 20))
            public double TreesMinDist = 11, TreesMaxDist = 13;
            public double TreesMinDistGroup = 0, TreesMaxDistGroup = 5;

            // Treasures
            public List<(string Template, int Count)>? Treasures;
            public double TreasureMinDist = 11, TreasureMaxDist = 13;
            public double TreasureMinDistGroup = 1, TreasureMaxDistGroup = 3;

            // Decoratives
            public string? DecorativesTemplate;
            public int? DecorativesCount;        // null → scaleByMapSize(2, 5)
            public int DecorativesMinDist = 8, DecorativesMaxDist = 11;
            public int DecorativesMinCount = 2, DecorativesMaxCount = 5;
        }

        /// <summary>模板 ResourceSupply.Max（Engine.GetTemplate 语义;沿 parent 上溯,
        /// Max 缺失取 Amount）。缓存按 dataRoot+template。</summary>
        private static int GetResourceSupplyMax(string? dataRoot, string template)
        {
            if (dataRoot == null) return 100;
            if (s_supplyMaxCache == null || s_supplyMaxCacheRoot != dataRoot)
            {
                s_supplyMaxCacheRoot = dataRoot;
                s_supplyMaxCache = new Dictionary<string, int>(StringComparer.Ordinal);
            }
            if (s_supplyMaxCache.TryGetValue(template, out int cached))
                return cached;

            int result = 100;
            string current = template;
            for (int depth = 0; depth < 8; depth++)
            {
                string path = Path.Combine(dataRoot, "simulation", "templates",
                    current + ".xml");
                if (!File.Exists(path)) break;
                try
                {
                    var doc = System.Xml.Linq.XDocument.Load(path);
                    var root = doc.Root!;
                    var rs = root.Element("ResourceSupply");
                    var maxStr = rs?.Element("Max")?.Value ?? rs?.Element("Amount")?.Value;
                    if (maxStr != null && int.TryParse(maxStr, out int v))
                    {
                        result = v;
                        break;
                    }
                    string? parent = root.Attribute("parent")?.Value;
                    if (string.IsNullOrEmpty(parent)) break;
                    current = parent;
                }
                catch { break; }
            }
            s_supplyMaxCache[template] = result;
            return result;
        }

        private static string? s_supplyMaxCacheRoot;
        private static Dictionary<string, int>? s_supplyMaxCache;

        /// <summary>placePlayerBaseStartingAnimal（逐字移植;error→return 同上游）。</summary>
        private static void PlacePlayerBaseStartingAnimal(RmgenRng rng, RandomMap map,
            MapSettings settings, PlayerBaseOptions opt, RmgenVector2D playerPos, IConstraint constraint)
        {
            string template = opt.StartingAnimalTemplate;
            int count = opt.StartingAnimalCount ??
                (template == "gaia/fauna_chicken" ? 5 :
                    (int)SafeMath.Round(5.0 * GetResourceSupplyMax(settings.DataRoot, "gaia/fauna_chicken") /
                        GetResourceSupplyMax(settings.DataRoot, template)));

            for (int i = 0; i < opt.StartingAnimalGroupCount; ++i)
            {
                bool success = false;
                for (int tries = 0; tries < 30; ++tries)
                {
                    var off = new RmgenVector2D(0, opt.StartingAnimalDistance);
                    off.Rotate(rng.RandomAngle());
                    var position = RmgenVector2D.Add(off, playerPos);
                    if (RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(rng, template,
                                opt.StartingAnimalMinGroupCount ?? count,
                                opt.StartingAnimalMaxGroupCount ?? count,
                                opt.StartingAnimalMinGroupDistance,
                                opt.StartingAnimalMaxGroupDistance),
                        }, true, opt.BaseResourceClass, position),
                        0, constraint))
                    {
                        success = true;
                        break;
                    }
                }
                if (!success)
                    return;
            }
        }

        /// <summary>placePlayerBaseBerries。</summary>
        private static void PlacePlayerBaseBerries(RmgenRng rng, RandomMap map,
            PlayerBaseOptions opt, RmgenVector2D playerPos, IConstraint constraint)
        {
            for (int tries = 0; tries < 30; ++tries)
            {
                var off = new RmgenVector2D(0, opt.BerriesDistance);
                off.Rotate(rng.RandomAngle());
                var position = RmgenVector2D.Add(off, playerPos);
                if (RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, opt.BerriesTemplate!,
                            opt.BerriesMinCount, opt.BerriesMaxCount, 1, 3),
                    }, true, opt.BaseResourceClass, position),
                    0, constraint))
                    return;
            }
        }

        /// <summary>placePlayerBaseMines（含 stone_formation 支路）。</summary>
        private static void PlacePlayerBaseMines(RmgenRng rng, RandomMap map,
            PlayerBaseOptions opt, RmgenVector2D playerPos, IConstraint constraint)
        {
            double angleBetweenMines = rng.RandFloat(opt.MinesMinAngle, opt.MinesMaxAngle);
            int mineCount = opt.Mines!.Count;

            for (int tries = 0; tries < 75; ++tries)
            {
                // 先找能放下全部矿的位置
                RmgenVector2D[]? pos = new RmgenVector2D[mineCount];
                double startAngle = rng.RandomAngle();
                for (int i = 0; i < mineCount; ++i)
                {
                    double angle = startAngle + angleBetweenMines * (i + (mineCount - 1) / 2.0);
                    var off = new RmgenVector2D(0, opt.MinesDistance);
                    off.Rotate(angle);
                    var p = RmgenVector2D.Add(off, playerPos);
                    p.Round();
                    pos[i] = p;
                    if (!map.ValidTilePassable(p) || !constraint.Allows(p))
                    {
                        pos = null;
                        break;
                    }
                }
                if (pos == null)
                    continue;

                // 放矿
                for (int i = 0; i < mineCount; ++i)
                {
                    var type = opt.Mines[i];
                    if (type.Type == "stone_formation")
                    {
                        GaiaEntities.CreateStoneMineFormation(rng, map, pos[i],
                            type.Template, type.Terrain ?? "");
                        opt.BaseResourceClass?.Add(pos[i]);
                        continue;
                    }

                    var objs = new List<IGroupElement>
                        { new ScatterObject(rng, type.Template, 1, 1, 0, 0) };
                    if (opt.MinesGroupElements != null)
                        objs.AddRange(opt.MinesGroupElements);
                    RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(objs, true, opt.BaseResourceClass, pos[i]), 0, null);
                }
                return;
            }
        }

        /// <summary>placePlayerBaseTrees。</summary>
        private static void PlacePlayerBaseTrees(RmgenRng rng, RandomMap map,
            PlayerBaseOptions opt, RmgenVector2D playerPos, IConstraint constraint)
        {
            int num = opt.TreesCount ?? (int)Math.Floor(RmgenLibrary.ScaleByMapSize(7, 20, map.GetSize()));

            for (int x = 0; x < 30; ++x)
            {
                var off = new RmgenVector2D(0, rng.RandFloat(opt.TreesMinDist, opt.TreesMaxDist));
                off.Rotate(rng.RandomAngle());
                var position = RmgenVector2D.Add(off, playerPos);
                position.Round();

                if (RmgenLibrary.CreateObjectGroup(
                    new ObjectGroup(new IGroupElement[]
                    {
                        new ScatterObject(rng, opt.TreesTemplate!, num, num,
                            opt.TreesMinDistGroup, opt.TreesMaxDistGroup),
                    }, false, opt.BaseResourceClass, position),
                    0, constraint))
                    return;
            }
        }

        /// <summary>placePlayerBaseTreasures。</summary>
        private static void PlacePlayerBaseTreasures(RmgenRng rng, RandomMap map,
            PlayerBaseOptions opt, RmgenVector2D playerPos, IConstraint constraint)
        {
            foreach (var treasure in opt.Treasures!)
            {
                bool success = false;
                for (int tries = 0; tries < 30; ++tries)
                {
                    var off = new RmgenVector2D(0,
                        rng.RandFloat(opt.TreasureMinDist, opt.TreasureMaxDist));
                    off.Rotate(rng.RandomAngle());
                    var position = RmgenVector2D.Add(off, playerPos);
                    position.Round();

                    if (RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(rng, treasure.Template, treasure.Count, treasure.Count,
                                opt.TreasureMinDistGroup, opt.TreasureMaxDistGroup),
                        }, false, opt.BaseResourceClass, position),
                        0, constraint))
                    {
                        success = true;
                        break;
                    }
                }
                if (!success)
                    return;
            }
        }

        /// <summary>placePlayerBaseDecoratives（失败不告警,同上游）。</summary>
        private static void PlacePlayerBaseDecoratives(RmgenRng rng, RandomMap map,
            PlayerBaseOptions opt, RmgenVector2D playerPos, IConstraint constraint)
        {
            int count = opt.DecorativesCount ?? (int)RmgenLibrary.ScaleByMapSize(2, 5, map.GetSize());
            for (int i = 0; i < count; ++i)
            {
                bool success = false;
                for (int x = 0; x < 30; ++x)
                {
                    var off = new RmgenVector2D(0,
                        rng.RandIntInclusive(opt.DecorativesMinDist, opt.DecorativesMaxDist));
                    off.Rotate(rng.RandomAngle());
                    var position = RmgenVector2D.Add(off, playerPos);
                    position.Round();

                    if (RmgenLibrary.CreateObjectGroup(
                        new ObjectGroup(new IGroupElement[]
                        {
                            new ScatterObject(rng, opt.DecorativesTemplate!,
                                opt.DecorativesMinCount, opt.DecorativesMaxCount, 0, 1),
                        }, false, opt.BaseResourceClass, position),
                        0, constraint))
                    {
                        success = true;
                        break;
                    }
                }
                if (!success)
                    return;
            }
        }

        /// <summary>playerPlacementByPattern——按布置模式定玩家位置。
        /// patternName null 时读 settings.PlayerPlacement（gamesetup 下发,"circle" 默认）。
        /// 注意 angle 通常由调用方 randomAngle() 先抽（与上游实参求值一致）。
        /// 上游未知模式 throw；本版回退 circle（gamesetup 之外的调用方可能传自定义名）。</summary>
        public static (List<int> playerIDs, List<RmgenVector2D> playerPosition) PlayerPlacementByPattern(
            RmgenRng rng, RandomMap map, MapSettings settings, string? patternName,
            double distance, double groupedDistance = 0, double? angle = null,
            RmgenVector2D? center = null)
        {
            patternName ??= settings.PlayerPlacement;
            switch (patternName)
            {
                case "river":
                    return PlayerPlacementRiver(rng, map, settings, angle ?? rng.RandomAngle(),
                        distance, center);

                case "groupedLines":
                    return PlaceLine(map, GetTeamsArray(rng, settings), distance, groupedDistance,
                        angle ?? rng.RandomAngle());

                case "stronghold":
                    return PlaceStronghold(map, GetTeamsArray(rng, settings), distance,
                        groupedDistance * 1.4, angle ?? rng.RandomAngle());

                case "randomGroup":
                    // 上游 playerPlacementRandom(getPlayerIDs(), undefined)——失败返回 undefined，
                    // 调用方（rmgen2 playerbaseTypes）再回退 circle。
                    var random = PlayerPlacementRandom(rng, map, settings, null);
                    if (random.HasValue)
                        return random.Value;
                    break;
            }

            var (ids, pos, _, _) = PlayerPlacementCircle(rng, map, GetNumPlayers(settings),
                distance, angle, center);
            return (ids, pos);
        }

        /// <summary>getTeamsArray（逐字移植 player.js）——按队分组的玩家 ID 二维表；
        /// 无队玩家各自成组，最后过滤掉空洞（上游 filter(team =&gt; true) 去 sparse 洞）。</summary>
        public static List<List<int>> GetTeamsArray(RmgenRng rng, MapSettings settings)
        {
            int numPlayers = GetNumPlayers(settings);
            var playerIDs = Enumerable.Range(1, numPlayers).ToList();

            // JS 稀疏数组 teams[team] —— 以 (队号 → 成员) 字典 + 队号升序还原枚举顺序
            var byTeam = new SortedDictionary<int, List<int>>();
            foreach (int id in playerIDs)
            {
                int team = GetPlayerTeam(settings, id);
                if (team == -1) continue;
                if (!byTeam.TryGetValue(team, out var members))
                    byTeam[team] = members = new List<int>();
                members.Add(id);
            }

            var teams = byTeam.Values.ToList();
            foreach (int id in playerIDs)
                if (GetPlayerTeam(settings, id) == -1)
                    teams.Add(new List<int> { id });

            return teams;
        }

        /// <summary>placeLine（逐字移植 player.js）——每队沿一条自图心向外的射线排开。</summary>
        public static (List<int> playerIDs, List<RmgenVector2D> playerPosition) PlaceLine(
            RandomMap map, IReadOnlyList<List<int>> teamsArray, double distance,
            double groupedDistance, double startAngle)
        {
            var playerIDs = new List<int>();
            var playerPosition = new List<RmgenVector2D>();

            var mapCenter = map.GetCenter();
            int numPlayers = teamsArray.Sum(t => t.Count);
            double numAcross = 2.0 * numPlayers / teamsArray.Count;
            double dist = RmgenLibrary.FractionToTiles(
                numAcross == 2 ? 0.45 : 0.66 + -0.01 * numAcross, map.GetSize());
            groupedDistance *= 3.00 + -0.225 * numAcross;

            for (int i = 0; i < teamsArray.Count; ++i)
            {
                double safeDist = distance;
                if (distance + teamsArray[i].Count * groupedDistance > dist)
                    safeDist = dist - teamsArray[i].Count * groupedDistance;

                double teamAngle = startAngle + (i + 1) * 2 * SafeMath.PI / teamsArray.Count;

                for (int p = 0; p < teamsArray[i].Count; ++p)
                {
                    playerIDs.Add(teamsArray[i][p]);
                    var offset = new RmgenVector2D(safeDist + p * groupedDistance, 0);
                    offset.Rotate(-teamAngle);
                    var pos = RmgenVector2D.Add(mapCenter, offset);
                    pos.Round();
                    playerPosition.Add(pos);
                }
            }

            return (playerIDs, playerPosition);
        }

        /// <summary>placeStronghold（逐字移植 player.js）——每队一个据点圆环，
        /// 据点沿图心圆按各自半径等弧长分布。</summary>
        public static (List<int> playerIDs, List<RmgenVector2D> playerPosition) PlaceStronghold(
            RandomMap map, IReadOnlyList<List<int>> teamsArray, double distance,
            double groupedDistance, double startAngle)
        {
            var mapCenter = map.GetCenter();
            var playerIDs = new List<int>();
            var playerPosition = new List<RmgenVector2D>();

            // 单人队放在队位置正中（半径 0）
            var strongholdRadius = teamsArray.Select(team => team.Count == 1
                ? 0
                : groupedDistance / 2 / SafeMath.Sin(SafeMath.PI / team.Count)).ToList();

            double distanceBetweenStrongholds =
                (distance * 2 * SafeMath.PI - 2 * strongholdRadius.Sum()) / strongholdRadius.Count;

            // 上游 strongholdRadius.at(i - 1)：i=0 时取末元素（负索引回卷）
            var relativeTeamAngles = strongholdRadius.Select((r1, i) =>
                (distanceBetweenStrongholds +
                    strongholdRadius[(i - 1 + strongholdRadius.Count) % strongholdRadius.Count] + r1)
                / distance).ToList();

            var teamAngles = new List<double>();
            for (int i = 0; i < relativeTeamAngles.Count; ++i)
                teamAngles.Add((i == 0 ? startAngle : teamAngles[^1]) + relativeTeamAngles[i]);

            for (int i = 0; i < teamsArray.Count; ++i)
            {
                var teamOffset = new RmgenVector2D(distance * 0.8, 0);
                teamOffset.Rotate(-teamAngles[i]);
                var teamPosition = RmgenVector2D.Add(mapCenter, teamOffset);

                for (int p = 0; p < teamsArray[i].Count; ++p)
                {
                    double angle = startAngle + (p + 1) * 2 * SafeMath.PI / teamsArray[i].Count;
                    playerIDs.Add(teamsArray[i][p]);
                    var offset = new RmgenVector2D(strongholdRadius[i], 0);
                    offset.Rotate(-angle);
                    var pos = RmgenVector2D.Add(teamPosition, offset);
                    pos.Round();
                    playerPosition.Add(pos);
                }
            }

            return (playerIDs, playerPosition);
        }

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

        /// <summary>createPassage（逐字移植 gaia_terrain.js）——在 start/end 之间开一条
        /// 两端宽度可变的平滑可通行通道，边缘按 smoothWidth 做高度插值。
        /// startHeight/endHeight 为 null 时取端点当前高度。</summary>
        public static Area? CreatePassage(RmgenRng rng, RandomMap map,
            RmgenVector2D start, RmgenVector2D end, double startWidth, double endWidth,
            double smoothWidth, object? terrain = null, object? edgeTerrain = null,
            TileClass? tileClass = null, IConstraint? constraints = null,
            double? startHeight = null, double? endHeight = null)
        {
            int Bound(double x) => (int)Math.Max(0, Math.Min(SafeMath.Round(x), map.GetSize() - 1));

            double h0 = startHeight ?? map.GetHeight(new RmgenVector2D(Bound(start.X), Bound(start.Y)));
            double h1 = endHeight ?? map.GetHeight(new RmgenVector2D(Bound(end.X), Bound(end.Y)));

            var passageVec = RmgenVector2D.Sub(end, start);
            var widthDirection = passageVec.Perpendicular();
            widthDirection.Normalize();
            double lengthStep = 1 / (2 * passageVec.Length());
            var points = new List<RmgenVector2D>();

            var constraint = constraints != null ? new StaticConstraint(map, constraints) : null;
            var terrainObj = terrain != null ? TerrainFactory.CreateTerrain(terrain) : null;
            var edgeTerrainObj = edgeTerrain != null ? TerrainFactory.CreateTerrain(edgeTerrain) : null;

            for (double lengthFraction = 0; lengthFraction <= 1; lengthFraction += lengthStep)
            {
                var locationLength = RmgenVector2D.Add(start,
                    RmgenVector2D.Mult(passageVec, lengthFraction));
                double halfPassageWidth = (startWidth + (endWidth - startWidth) * lengthFraction) / 2;
                double passageHeight = h0 + (h1 - h0) * lengthFraction;

                for (double stepWidth = -halfPassageWidth; stepWidth <= halfPassageWidth; stepWidth += 0.5)
                {
                    var location = RmgenVector2D.Add(locationLength,
                        RmgenVector2D.Mult(widthDirection, stepWidth));
                    location.Round();

                    if (!map.InMapBounds(location) ||
                        constraint != null && !constraint.Allows(location))
                        continue;

                    points.Add(location);

                    double smoothDistance = smoothWidth + Math.Abs(stepWidth) - halfPassageWidth;

                    map.SetHeight(location, smoothDistance > 0
                        ? (map.GetHeight(location) * smoothDistance + passageHeight / smoothDistance)
                            / (smoothDistance + 1 / smoothDistance)
                        : passageHeight);

                    tileClass?.Add(location);

                    if (edgeTerrainObj != null && smoothDistance > 0)
                        edgeTerrainObj.Place(map, rng, location);
                    else
                        terrainObj?.Place(map, rng, location);
                }
            }

            return points.Count == 0 ? null : new Area(map, points);
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
