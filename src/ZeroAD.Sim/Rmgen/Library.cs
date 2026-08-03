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
        public static double ScaleByMapSize(double min, double max, int mapSize, int minSize = 128, int maxSize = 512)
            => min + (max - min) * (mapSize - minSize) / (maxSize - minSize);
        public static double ScaleByMapArea(double min, double max, int mapSize, bool circular)
        {
            double minArea = circular ? Math.PI * 64 * 64 : 128 * 128;
            double maxArea = circular ? Math.PI * 256 * 256 : 512 * 512;
            double area = circular ? Math.PI * (mapSize / 2.0) * (mapSize / 2.0) : mapSize * mapSize;
            return min + (max - min) * (area - minArea) / (maxArea - minArea);
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
