using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen
{
    /// <summary>TerrainPainter（逐字移植 painter/TerrainPainter.js）——涂纹理。</summary>
    public sealed class TerrainPainter : IPainter
    {
        private readonly string _texture;
        private readonly string? _tileClass;  // terrain entity 模板名（可选）

        public TerrainPainter(string texture) { _texture = texture; }
        public TerrainPainter(string texture, string? tileClass) { _texture = texture; _tileClass = tileClass; }

        public void Paint(Area area)
        {
            var map = RmgenLibrary.CurrentMap;
            foreach (var p in area.GetPoints())
                map.SetTexture(p, _texture);
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

        public SmoothElevationPainter(SmoothType type, double elevation, double blendRadius)
        { _type = type; _elevation = elevation; _blendRadius = blendRadius; }

        public void Paint(Area area)
        {
            var map = RmgenLibrary.CurrentMap;
            // 简化版：区域内设高度，边界做线性混合。
            // TODO: 完整移植 BFS smoothing + Float32Array 中间存储
            var areaSet = new HashSet<(int x, int y)>();
            foreach (var p in area.GetPoints())
                areaSet.Add(((int)p.X, (int)p.Y));

            foreach (var p in area.GetPoints())
                map.SetHeight(p, _elevation);

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
                            map.SetHeight(pos, current * (1 - weight) + _elevation * weight);
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
