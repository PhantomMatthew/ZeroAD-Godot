using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen
{
    /// <summary>Placer 接口（原版 placer 的 prototype.place）。</summary>
    public interface IPlacer
    {
        /// <summary>生成满足 constraint 的点集，返回 null 表示无解。</summary>
        List<RmgenVector2D>? Place(IConstraint constraint);
    }

    /// <summary>可外部设定中心的 Placer（原版 centered placer 的 setCenterPosition 协议，
    /// createAreas/createAreasInAreas 依赖它）。</summary>
    public interface ICenteredPlacer : IPlacer
    {
        void SetCenterPosition(RmgenVector2D position);
    }

    /// <summary>Painter 接口（原版 painter 的 prototype.paint）。</summary>
    public interface IPainter
    {
        void Paint(Area area);
    }

    /// <summary>Object/Group 接口（原版 Group 的 prototype.place）。</summary>
    public interface IObjectGroup
    {
        bool Place(int player, IConstraint constraint);
    }

    /// <summary>可外部设定中心的实体组（原版 Group.js 的 setCenterPosition 协议，
    /// createObjectGroups/createObjectGroupsByAreas 依赖它）。</summary>
    public interface ICenteredObjectGroup : IObjectGroup
    {
        void SetCenterPosition(RmgenVector2D position);
    }

    /// <summary>核心 API 函数（逐字移植 library.js 的全局函数）。</summary>
    public static class RmgenLibrary
    {
        /// <summary>构造 Area + 调用 Painter（原版 createArea）。</summary>
        public static Area? CreateArea(IPlacer placer, IPainter? painter, IConstraint? constraint)
        {
            var points = placer.Place(constraint ?? new NullConstraint());
            if (points == null || points.Count == 0) return null;
            var area = new Area(s_currentMap!, points);
            painter?.Paint(area);
            return area;
        }

        /// <summary>构造 Area + 多个 Painter。</summary>
        public static Area? CreateArea(IPlacer placer, IEnumerable<IPainter> painters, IConstraint? constraint)
        {
            var points = placer.Place(constraint ?? new NullConstraint());
            if (points == null || points.Count == 0) return null;
            var area = new Area(s_currentMap!, points);
            foreach (var p in painters) p.Paint(area);
            return area;
        }

        /// <summary>放置实体组（原版 createObjectGroup）。</summary>
        public static bool CreateObjectGroup(IObjectGroup group, int player, IConstraint? constraint)
            => group.Place(player, constraint ?? new NullConstraint());

        // ── 批量放置（原版 retryPlacing/createAreas/createObjectGroups 系列）──
        // amount 保 double：上游 JS 数量常是 scaleByMapSize 的浮点结果，
        // results.length < amount 的浮点比较决定实际尝试次数（等效 ceil）。

        /// <summary>retryPlacing（实体组版）：反复尝试直到成功 amount 次或失败超 amount*retryFactor。
        /// behaveDeprecated=true 时无论成败都计一次尝试（旧图兼容，恰好尝试 amount 次）。</summary>
        private static List<bool> RetryPlacingGroups(Func<bool> placeFunc, int retryFactor, double amount, bool behaveDeprecated)
        {
            double maxFail = amount * retryFactor;
            var results = new List<bool>();
            int bad = 0;
            while (results.Count < amount && bad <= maxFail)
            {
                bool result = placeFunc();
                if (result || behaveDeprecated)
                    results.Add(result);
                else
                    ++bad;
            }
            return results;
        }

        /// <summary>retryPlacing（区域版，behaveDeprecated=false——上游 createAreas 系列恒为此）。</summary>
        private static List<Area> RetryPlacingAreas(Func<Area?> placeFunc, int retryFactor, double amount)
        {
            double maxFail = amount * retryFactor;
            var results = new List<Area>();
            int bad = 0;
            while (results.Count < amount && bad <= maxFail)
            {
                var result = placeFunc();
                if (result != null)
                    results.Add(result);
                else
                    ++bad;
            }
            return results;
        }

        /// <summary>createObjectGroups：随机可通行中心放置 amount 次实体组，返回成功数。</summary>
        public static int CreateObjectGroups(RmgenRng rng, ICenteredObjectGroup group, int player,
            IConstraint? constraint, double amount, int retryFactor = 10, bool behaveDeprecated = false)
        {
            var map = CurrentMap;
            return RetryPlacingGroups(() =>
            {
                group.SetCenterPosition(Common.RmgenCommon.RandomCoordinate(rng, map, true));
                return CreateObjectGroup(group, player, constraint);
            }, retryFactor, amount, behaveDeprecated).Count(r => r);
        }

        /// <summary>createObjectGroupsDeprecated（library.js）——旧图兼容版，无论成败计一次尝试。</summary>
        public static int CreateObjectGroupsDeprecated(RmgenRng rng, ICenteredObjectGroup group, int player,
            IConstraint? constraint, double amount, int retryFactor = 10)
            => CreateObjectGroups(rng, group, player, constraint, amount, retryFactor, behaveDeprecated: true);

        /// <summary>createObjectGroupsByAreas：在给定 Area 集合的随机点上放置实体组。</summary>
        public static int CreateObjectGroupsByAreas(RmgenRng rng, ICenteredObjectGroup group, int player,
            IConstraint? constraint, double amount, int retryFactor, IReadOnlyList<Area> areas,
            bool behaveDeprecated = false)
        {
            var nonEmpty = areas.Where(a => a.PointCount > 0).ToList();
            if (nonEmpty.Count == 0)
                return 0;  // 上游此处 log 警告并返回 []
            return RetryPlacingGroups(() =>
            {
                group.SetCenterPosition(rng.PickRandom(rng.PickRandom(nonEmpty).GetPoints()));
                return CreateObjectGroup(group, player, constraint);
            }, retryFactor, amount, behaveDeprecated).Count(r => r);
        }

        /// <summary>createObjectGroupsByAreasDeprecated（library.js）——旧图兼容版。</summary>
        public static int CreateObjectGroupsByAreasDeprecated(RmgenRng rng, ICenteredObjectGroup group, int player,
            IConstraint? constraint, double amount, int retryFactor, IReadOnlyList<Area> areas)
            => CreateObjectGroupsByAreas(rng, group, player, constraint, amount, retryFactor, areas,
                behaveDeprecated: true);

        /// <summary>createAreas：随机中心放置 amount 个区域，返回成功创建的 Area 列表。</summary>
        public static List<Area> CreateAreas(RmgenRng rng, ICenteredPlacer placer,
            IEnumerable<IPainter> painters, IConstraint? constraint, double amount, int retryFactor = 10)
        {
            var map = CurrentMap;
            return RetryPlacingAreas(() =>
            {
                placer.SetCenterPosition(Common.RmgenCommon.RandomCoordinate(rng, map, false));
                return CreateArea(placer, painters, constraint);
            }, retryFactor, amount);
        }

        /// <summary>createAreasInAreas：在给定 Area 集合的随机点上放置区域。</summary>
        public static List<Area> CreateAreasInAreas(RmgenRng rng, ICenteredPlacer placer,
            IEnumerable<IPainter> painters, IConstraint? constraint, double amount, int retryFactor,
            IReadOnlyList<Area> areas)
        {
            var nonEmpty = areas.Where(a => a.PointCount > 0).ToList();
            if (nonEmpty.Count == 0)
                return new List<Area>();  // 上游此处 log 警告并返回 []
            return RetryPlacingAreas(() =>
            {
                placer.SetCenterPosition(rng.PickRandom(rng.PickRandom(nonEmpty).GetPoints()));
                return CreateArea(placer, painters, constraint);
            }, retryFactor, amount);
        }

        /// <summary>paintTerrainBasedOnHeight：对高度区间（按 mode 含端点）内的图块刷地形。</summary>
        public static Area? PaintTerrainBasedOnHeight(RmgenRng rng, double minHeight, double maxHeight,
            HeightPlacer.Mode mode, string terrain)
            => CreateArea(new HeightPlacer(CurrentMap, mode, minHeight, maxHeight),
                new TerrainPainter(terrain, rng), null);

        /// <summary>paintTerrainBasedOnHeight（名单版）——名单逐图块 RandomTerrain 抽取。</summary>
        public static Area? PaintTerrainBasedOnHeight(RmgenRng rng, double minHeight, double maxHeight,
            HeightPlacer.Mode mode, IReadOnlyList<string> terrain)
            => CreateArea(new HeightPlacer(CurrentMap, mode, minHeight, maxHeight),
                new TerrainPainter(TerrainFactory.CreateTerrain(terrain), rng), null);

        /// <summary>paintTileClassBasedOnHeight：高度区间内标 TileClass。</summary>
        public static Area? PaintTileClassBasedOnHeight(double minHeight, double maxHeight,
            HeightPlacer.Mode mode, TileClass tileClass)
            => CreateArea(new HeightPlacer(CurrentMap, mode, minHeight, maxHeight),
                new TileClassPainter(tileClass), null);

        /// <summary>unPaintTileClassBasedOnHeight：高度区间内取消 TileClass 标记。</summary>
        public static Area? UnPaintTileClassBasedOnHeight(double minHeight, double maxHeight,
            HeightPlacer.Mode mode, TileClass tileClass)
            => CreateArea(new HeightPlacer(CurrentMap, mode, minHeight, maxHeight),
                new TileClassUnPainter(tileClass), null);

        // ── 约束工厂（原版 avoidClasses/stayClasses/borderClasses）──

        /// <summary>avoidClasses(class1, dist1, class2, dist2, ...)。</summary>
        public static IConstraint AvoidClasses(params object[] args)
        {
            var list = new List<IConstraint>();
            for (int i = 0; i + 1 < args.Length; i += 2)
                list.Add(new AvoidTileClassConstraint((TileClass)args[i], Convert.ToDouble(args[i + 1])));
            return list.Count == 1 ? list[0] : new AndConstraint(list);
        }

        /// <summary>stayClasses(class1, dist1, ...)。</summary>
        public static IConstraint StayClasses(params object[] args)
        {
            var list = new List<IConstraint>();
            for (int i = 0; i + 1 < args.Length; i += 2)
                list.Add(new StayInTileClassConstraint((TileClass)args[i], Convert.ToDouble(args[i + 1])));
            return list.Count == 1 ? list[0] : new AndConstraint(list);
        }

        /// <summary>borderClasses(class1, idist1, odist1, ...)。</summary>
        public static IConstraint BorderClasses(params object[] args)
        {
            var list = new List<IConstraint>();
            for (int i = 0; i + 2 < args.Length; i += 3)
                list.Add(new BorderTileClassConstraint((TileClass)args[i],
                    Convert.ToDouble(args[i + 1]), Convert.ToDouble(args[i + 2])));
            return list.Count == 1 ? list[0] : new AndConstraint(list);
        }

        // ── 辅助函数 ──

        public static double FractionToTiles(double f, int mapSize) => mapSize * f;

        /// <summary>tilesToFraction(t) = t / mapSize。</summary>
        public static double TilesToFraction(double t, int mapSize) => t / mapSize;

        /// <summary>randomPositionOnTile——图块内随机点（randFloat(0,1) × 2 次抽数）。</summary>
        public static RmgenVector2D RandomPositionOnTile(RmgenRng rng, RmgenVector2D tilePosition)
            => RmgenVector2D.Add(tilePosition,
                new RmgenVector2D(rng.RandFloat(0, 1), rng.RandFloat(0, 1)));
        public static double ScaleByMapSize(double min, double max, int mapSize, int minSize = 128, int maxSize = 512)
            => min + (max - min) * (mapSize - minSize) / (maxSize - minSize);
        public static double ScaleByMapArea(double min, double max, int mapSize, bool circular)
        {
            double minArea = circular ? Math.PI * 64 * 64 : 128 * 128;
            double maxArea = circular ? Math.PI * 256 * 256 : 512 * 512;
            double area = circular ? Math.PI * (mapSize / 2.0) * (mapSize / 2.0) : mapSize * mapSize;
            return min + (max - min) * (area - minArea) / (maxArea - minArea);
        }

        /// <summary>scaleByMapAreaAbsolute(base, disallowedArea=0)——
        /// scaleByMapArea(0, base, disallowedArea, getArea(128)+disallowedArea)，
        /// 即 base * (area - disallowedArea) / getArea(128)。</summary>
        public static double ScaleByMapAreaAbsolute(double baseValue, int mapSize, bool circular,
            double disallowedArea = 0)
        {
            double baseArea = circular ? Math.PI * 64 * 64 : 128 * 128;
            double area = circular ? Math.PI * (mapSize / 2.0) * (mapSize / 2.0) : mapSize * mapSize;
            return baseValue * (area - disallowedArea) / baseArea;
        }

        // ── 全局状态 ──

        private static RandomMap? s_currentMap;
        public static RandomMap CurrentMap
        {
            get => s_currentMap ?? throw new InvalidOperationException("No active RandomMap");
            set => s_currentMap = value;
        }
    }
}
