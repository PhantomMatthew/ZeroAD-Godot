using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen
{
    /// <summary>TerrainPainter（逐字移植 painter/TerrainPainter.js）——对区域每格落 Terrain。
    /// 接受 ITerrain(可为 "tex|entity" 混合);字符串构造器保持旧调用兼容(纯贴图)。</summary>
    public sealed class TerrainPainter : IPainter
    {
        private readonly ITerrain _terrain;
        private readonly RmgenRng _rng;

        public TerrainPainter(string texture, RmgenRng? rng = null)
        {
            _terrain = TerrainFactory.CreateTerrain(texture);
            _rng = rng ?? new RmgenRng(0);
        }

        /// <summary>混合地形版（嵌套数组/名单，如 ardennes 的 pForest）——走
        /// TerrainFactory.CreateTerrain(object) 递归解析。</summary>
        public TerrainPainter(object terrain, RmgenRng rng)
        {
            _terrain = TerrainFactory.CreateTerrain(terrain);
            _rng = rng;
        }

        public TerrainPainter(ITerrain terrain, RmgenRng rng)
        {
            _terrain = terrain;
            _rng = rng;
        }

        public void Paint(Area area)
        {
            var map = RmgenLibrary.CurrentMap;
            foreach (var p in area.GetPoints())
                _terrain.Place(map, _rng, p);
        }
    }

    /// <summary>LayeredPainter（逐字移植 painter/LayeredPainter.js）——按"到区域边界的 BFS
    /// 距离"分层落 Terrain:widths[i] = 第 i 层厚度,剩余中心区域落最后一个 terrain。
    /// 原版经 breadthFirstSearchPaint(brushSize=1, withinArea=contains)。</summary>
    public sealed class LayeredPainter : IPainter
    {
        private readonly List<ITerrain> _terrains;
        private readonly int[] _widths;
        private readonly RmgenRng _rng;

        /// <param name="terrains">string → 按 "tex|entity" 解析;string[]/List&lt;string&gt; →
        /// RandomTerrain;object[](string 与 string[] 混合,森林变体用) → 逐元素解析后 RandomTerrain;
        /// 已是 ITerrain 的直接用。</param>
        public LayeredPainter(IReadOnlyList<object> terrains, int[] widths, RmgenRng rng)
        {
            _terrains = new List<ITerrain>();
            foreach (var t in terrains)
                _terrains.Add(Resolve(t));
            if (widths.Length != _terrains.Count - 1)
                throw new System.ArgumentException("LayeredPainter: widths must have one item less than terrains");
            _widths = widths;
            _rng = rng;
        }

        private static ITerrain Resolve(object t)
        {
            switch (t)
            {
                case string s: return TerrainFactory.CreateTerrain(s);
                case IReadOnlyList<string> arr: return TerrainFactory.CreateTerrain(arr);
                case ITerrain it: return it;
                case System.Collections.IEnumerable mix:
                    // object[](string/string[] 混合,如森林 [ff, main, treeList])
                    var list = new List<ITerrain>();
                    foreach (var item in mix) list.Add(Resolve(item!));
                    return new RandomTerrain(list);
                default: throw new System.ArgumentException($"LayeredPainter: bad terrain {t}");
            }
        }

        public void Paint(Area area)
        {
            var map = RmgenLibrary.CurrentMap;

            // 多源 BFS:区域边界格(有邻格不在区域内)距离 1 起,向内递增。
            var dist = new Dictionary<(int, int), int>();
            var queue = new Queue<(int, int)>();
            foreach (var p in area.GetPoints())
            {
                var pt = ((int)p.X, (int)p.Y);
                bool border = !area.Contains(new RmgenVector2D(pt.Item1 + 1, pt.Item2))
                    || !area.Contains(new RmgenVector2D(pt.Item1 - 1, pt.Item2))
                    || !area.Contains(new RmgenVector2D(pt.Item1, pt.Item2 + 1))
                    || !area.Contains(new RmgenVector2D(pt.Item1, pt.Item2 - 1));
                if (border)
                {
                    dist[pt] = 1;
                    queue.Enqueue(pt);
                }
            }
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                int d = dist[cur] + 1;
                foreach (var nb in new[] { (cur.Item1 + 1, cur.Item2), (cur.Item1 - 1, cur.Item2),
                                          (cur.Item1, cur.Item2 + 1), (cur.Item1, cur.Item2 - 1) })
                {
                    if (dist.ContainsKey(nb)) continue;
                    if (!area.Contains(new RmgenVector2D(nb.Item1, nb.Item2))) continue;
                    dist[nb] = d;
                    queue.Enqueue(nb);
                }
            }

            foreach (var p in area.GetPoints())
            {
                var pt = ((int)p.X, (int)p.Y);
                int distance = dist.TryGetValue(pt, out var dd) ? dd : int.MaxValue;
                int width = 0, i = 0;
                for (; i < _widths.Length; i++)
                {
                    width += _widths[i];
                    if (width >= distance) break;
                }
                _terrains[i].Place(map, _rng, p);
            }
        }
    }

    /// <summary>ElevationPainter（逐字移植 painter/ElevationPainter.js）——设定高度。</summary>
    public sealed class ElevationPainter : IPainter
    {
        private readonly double _elevation;

        public ElevationPainter(double elevation) => _elevation = elevation;

        public void Paint(Area area)
        {
            var map = RmgenLibrary.CurrentMap;
            foreach (var p in area.GetPoints())
                map.SetHeight(p, _elevation);
        }
    }

    /// <summary>SmoothElevationPainter（逐字移植 painter/SmoothElevationPainter.js，~189 行）。
    /// BFS 平滑高度。Float32Array 存储中间结果（保真度关键）。
    /// 简化版——完整版需 BFS 边界扩展 + cubic interpolation。</summary>
    public sealed class SmoothElevationPainter : IPainter
    {
        public enum SmoothType { Blurry, Solid }

        private readonly double _elevation;
        private readonly SmoothType _type;
        private readonly double _blendRadius;
        private readonly bool _relative;

        /// <param name="relative">true 对应上游 ELEVATION_MODIFY（相对抬升），
        /// false 为 ELEVATION_SET（绝对设定，既有调用方默认）。</param>
        public SmoothElevationPainter(SmoothType type, double elevation, double blendRadius, bool relative = false)
        { _type = type; _elevation = elevation; _blendRadius = blendRadius; _relative = relative; }

        public void Paint(Area area)
        {
            var map = RmgenLibrary.CurrentMap;
            // 简化版：区域内设高度，边界做线性混合。
            // TODO: 完整移植 BFS smoothing + Float32Array 中间存储
            var areaSet = new HashSet<(int x, int y)>();
            foreach (var p in area.GetPoints())
                areaSet.Add(((int)p.X, (int)p.Y));

            foreach (var p in area.GetPoints())
                map.SetHeight(p, _relative ? map.GetHeight(p) + _elevation : _elevation);

            // 简化边界混合
            for (int r = 1; r <= _blendRadius; r++)
            {
                double weight = 1.0 - (double)r / (_blendRadius + 1);
                foreach (var p in area.GetPoints())
                {
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = (int)p.X + dx * r, ny = (int)p.Y + dy * r;
                            if (areaSet.Contains((nx, ny))) continue;
                            var pos = new RmgenVector2D(nx, ny);
                            if (!map.ValidHeight(pos)) continue;
                            double current = map.GetHeight(pos);
                            map.SetHeight(pos, _relative
                                ? current + _elevation * weight
                                : current * (1 - weight) + _elevation * weight);
                        }
                }
            }
        }
    }

    /// <summary>TileClassPainter（逐字移植 painter/TileClassPainter.js）——标记 TileClass。</summary>
    public sealed class TileClassPainter : IPainter
    {
        private readonly TileClass _tileClass;
        public TileClassPainter(TileClass tileClass) => _tileClass = tileClass;
        public void Paint(Area area)
        {
            foreach (var p in area.GetPoints())
                _tileClass.Add(p);
        }
    }

    /// <summary>TileClassUnPainter（逐字移植 painter/TileClassUnPainter.js）——取消标记。</summary>
    public sealed class TileClassUnPainter : IPainter
    {
        private readonly TileClass _tileClass;
        public TileClassUnPainter(TileClass tileClass) => _tileClass = tileClass;
        public void Paint(Area area)
        {
            foreach (var p in area.GetPoints())
                _tileClass.Remove(p);
        }
    }

    /// <summary>MultiPainter（逐字移植 painter/MultiPainter.js）——多个 Painter 组合。</summary>
    public sealed class MultiPainter : IPainter
    {
        private readonly List<IPainter> _painters;
        public MultiPainter(IEnumerable<IPainter> painters) => _painters = new(painters);
        public MultiPainter(params IPainter[] painters) => _painters = new(painters);
        public void Paint(Area area) { foreach (var p in _painters) p.Paint(area); }
    }

    /// <summary>RandomElevationPainter（逐字移植 painter/RandomElevationPainter.js）。</summary>
    public sealed class RandomElevationPainter : IPainter
    {
        private readonly double _min, _max;
        private readonly RmgenRng _rng;
        public RandomElevationPainter(RmgenRng rng, double min, double max) { _rng = rng; _min = min; _max = max; }
        public void Paint(Area area)
        {
            var map = RmgenLibrary.CurrentMap;
            foreach (var p in area.GetPoints())
                map.SetHeight(p, _rng.RandFloat(_min, _max));
        }
    }

    /// <summary>SimpleObject（逐字移植 Object.js）——单个实体放置。
    /// 实现 IObjectGroup。</summary>
    public sealed class SimpleObject : IObjectGroup
    {
        private readonly string _templateName;
        private readonly double _x, _z;
        private readonly bool _avoidSelf;

        public SimpleObject(string templateName, double x, double z, double angle, bool avoidSelf = false)
        {
            _templateName = templateName;
            _x = x; _z = z;
            _avoidSelf = avoidSelf;
        }

        public bool Place(int player, IConstraint constraint)
        {
            var map = RmgenLibrary.CurrentMap;
            var pos = new RmgenVector2D(_x, _z);
            if (!map.ValidTile(pos) || !constraint.Allows(pos)) return false;
            map.PlaceEntityPassable(_templateName, player, pos, 0);
            return true;
        }
    }

    /// <summary>SimpleGroup（逐字移植 Group.js）——实体组放置。</summary>
    public sealed class SimpleGroup : IObjectGroup
    {
        private readonly List<SimpleObject> _elements;
        private readonly int _avoidSelf;
        private readonly RmgenVector2D _center;
        private readonly RmgenRng _rng;

        public SimpleGroup(RmgenRng rng, IEnumerable<SimpleObject> elements, int avoidSelf, RmgenVector2D center)
        { _rng = rng; _elements = new(elements); _avoidSelf = avoidSelf; _center = center; }

        public bool Place(int player, IConstraint constraint)
        {
            var map = RmgenLibrary.CurrentMap;
            bool anyPlaced = false;
            foreach (var obj in _elements)
            {
                // 简化：直接放（完整版有随机偏移 + 约束检查）
                if (obj.Place(player, constraint))
                    anyPlaced = true;
            }
            return anyPlaced;
        }
    }
}
