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

        /// <summary>创建丘陵（原版 createHills）。骨架。</summary>
        public static void CreateHills(RmgenRng rng, RandomMap map, string[] terrainSet,
            IConstraint constraint, TileClass tileClass, int? count = null, double elevation = 18)
        {
            int n = count ?? (int)(RmgenLibrary.ScaleByMapSize(1, 4, map.GetSize()) * GetNumPlayers(new MapSettings()));
            // TODO: 完整版用 LayeredPainter + SmoothElevationPainter + TileClassPainter 组合
            for (int i = 0; i < n; i++)
            {
                var pos = RandomCoordinate(rng, map, passableOnly: false);
                var placer = new ChainPlacer(rng, 1,
                    (int)RmgenLibrary.ScaleByMapSize(4, 6, map.GetSize()),
                    (int)RmgenLibrary.ScaleByMapSize(16, 40, map.GetSize()), 0.5, pos);
                RmgenLibrary.CreateArea(placer, new IPainter[] {
                    new TerrainPainter(terrainSet.Length > 0 ? terrainSet[0] : " Cliff"),
                    new SmoothElevationPainter(SmoothElevationPainter.SmoothType.Solid, elevation, 2),
                    new TileClassPainter(tileClass)
                }, constraint);
            }
        }

        /// <summary>创建山脉（原版 createMountains）。骨架。</summary>
        public static void CreateMountains(RmgenRng rng, RandomMap map, string terrain,
            IConstraint constraint, TileClass tileClass, int? count = null, double maxHeight = 30)
        {
            int n = count ?? (int)(RmgenLibrary.ScaleByMapSize(1, 4, map.GetSize()));
            for (int i = 0; i < n; i++)
            {
                int x = rng.RandIntExclusive(0, map.GetSize());
                int z = rng.RandIntExclusive(0, map.GetSize());
                // TODO: 完整版用 createMountain（ChainPlacer-like 圆锥高度）
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

        /// <summary>创建默认森林（简化版：随机放树）。</summary>
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

        /// <summary>放置玩家基地（原版 placePlayerBases）。骨架。</summary>
        public static void PlacePlayerBases(RmgenRng rng, RandomMap map, MapSettings settings,
            string baseTerrain, TileClass playerTileClass)
        {
            int numPlayers = GetNumPlayers(settings);
            // TODO: 完整版用 playerPlacementByPattern 选位置 + placePlayerBaseBuildings
            // 骨架：均匀分布玩家 CC
            for (int p = 1; p <= numPlayers; p++)
            {
                double angle = (double)(p - 1) / numPlayers * 2 * Math.PI;
                double dist = map.GetSize() * 0.35;
                double x = map.GetSize() / 2.0 + dist * Math.Cos(angle);
                double z = map.GetSize() / 2.0 + dist * Math.Sin(angle);
                var pos = new RmgenVector2D(x, z);
                pos.Floor();
                var civ = GetCivCode(settings, p);
                map.PlaceEntityAnywhere($"structures/{civ}/civil_centre", p, pos, (float)angle);
                playerTileClass.Add(pos);
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
