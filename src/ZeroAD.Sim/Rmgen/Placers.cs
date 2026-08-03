using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen
{
    /// <summary>ChainPlacer（逐字移植 placer/centered/ChainPlacer.js，114 行）。
    /// 随机链式放置圆形——每次从边缘随机选中心点画圆。</summary>
    public sealed class ChainPlacer : IPlacer
    {
        private readonly double _minRadius, _maxRadius;
        private readonly int _numCircles;
        private readonly double _failFraction;
        private readonly double _maxDistance;
        private readonly List<int> _queue;
        private RmgenVector2D _center;
        private readonly RmgenRng _rng;

        public ChainPlacer(RmgenRng rng, double minRadius, double maxRadius, int numCircles,
            double failFraction = 0, RmgenVector2D? centerPosition = null, double maxDistance = 0, int[]? queue = null)
        {
            _rng = rng;
            _minRadius = minRadius; _maxRadius = maxRadius; _numCircles = numCircles;
            _failFraction = failFraction; _maxDistance = maxDistance;
            _queue = queue != null ? new List<int>(Array.ConvertAll(queue, r => (int)Math.Floor((double)r))) : new();
            _center = centerPosition ?? default;
            if (centerPosition.HasValue) SetCenterPosition(centerPosition.Value);
        }

        public void SetCenterPosition(RmgenVector2D position)
        {
            var p = position; p.Round();
            _center = p;
        }

        public List<RmgenVector2D>? Place(IConstraint constraint)
        {
            var map = RmgenLibrary.CurrentMap;
            if (!map.InMapBounds(_center) || !constraint.Allows(_center)) return null;

            var points = new List<RmgenVector2D>();
            int size = map.GetSize();
            int failed = 0, count = 0;
            var gotRet = new ushort[size * size];
            int at(int x, int y) => x + y * size;
            int sizeM1 = size - 1;

            double minR = Math.Min(_maxRadius, Math.Max(_minRadius, 1));
            var edges = new List<RmgenVector2D> { _center };

            for (int i = 0; i < _numCircles; i++)
            {
                var chainPos = _rng.PickRandom(edges);
                int radius = _queue.Count > 0 ? PopLast(_queue) : _rng.RandIntInclusive(minR, _maxRadius);
                double radius2 = SafeMath.Square(radius);

                int x0 = (int)Math.Max(0, chainPos.X - radius);
                int y0 = (int)Math.Max(0, chainPos.Y - radius);
                int x1 = (int)Math.Min(chainPos.X + radius, sizeM1);
                int y1 = (int)Math.Min(chainPos.Y + radius, sizeM1);

                for (int x = x0; x <= x1; x++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        var pos = new RmgenVector2D(x, y);
                        if (pos.DistanceToSquared(chainPos) >= radius2) continue;
                        count++;
                        if (!map.InMapBounds(pos) || !constraint.Allows(pos)) { failed++; continue; }
                        int s = gotRet[at(x, y)];
                        if (s == 0) { points.Add(pos); gotRet[at(x, y)] = 1; }
                        else if (s >= 2)
                        {
                            edges.RemoveAt(s - 2);
                            gotRet[at(x, y)] = 1;
                            for (int k = s - 2; k < edges.Count; k++)
                                gotRet[at((int)edges[k].X, (int)edges[k].Y)]--;
                        }
                    }
                }

                for (int x = x0; x <= x1; x++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        var pos = new RmgenVector2D(x, y);
                        if (_maxDistance > 0 && (Math.Abs(_center.X - pos.X) > _maxDistance || Math.Abs(_center.Y - pos.Y) > _maxDistance)) continue;
                        if (gotRet[at(x, y)] != 1) continue;
                        if ((x > 0 && gotRet[at(x - 1, y)] == 0) ||
                            (y > 0 && gotRet[at(x, y - 1)] == 0) ||
                            (x < sizeM1 && gotRet[at(x + 1, y)] == 0) ||
                            (y < sizeM1 && gotRet[at(x, y + 1)] == 0))
                        {
                            edges.Add(pos);
                            gotRet[at(x, y)] = (ushort)(edges.Count + 1);
                        }
                    }
                }
            }

            return failed > count * _failFraction ? null : points;
        }

        private static int PopLast(List<int> list) { int v = list[^1]; list.RemoveAt(list.Count - 1); return v; }
    }

    /// <summary>ClumpPlacer（逐字移植 placer/centered/ClumpPlacer.js，~107 行）。
    /// 用噪声生成不规则团块。Float32Array 存储 ctrlVals/noise（保真度关键）。</summary>
    public sealed class ClumpPlacer : IPlacer
    {
        private readonly double _radius, _coherence, _smoothness, _failFraction;
        private RmgenVector2D _center;
        private readonly RmgenRng _rng;

        public ClumpPlacer(RmgenRng rng, double radius, double coherence = 0.5, double smoothness = 0.1,
            double failFraction = 0, RmgenVector2D? centerPosition = null)
        {
            _rng = rng; _radius = radius; _coherence = coherence;
            _smoothness = smoothness; _failFraction = failFraction;
            _center = centerPosition ?? default;
            if (centerPosition.HasValue) SetCenterPosition(centerPosition.Value);
        }

        public void SetCenterPosition(RmgenVector2D position) { var p = position; p.Round(); _center = p; }

        public List<RmgenVector2D>? Place(IConstraint constraint)
        {
            var map = RmgenLibrary.CurrentMap;
            if (!map.InMapBounds(_center) || !constraint.Allows(_center)) return null;

            var points = new List<RmgenVector2D>();
            double radius = _radius;
            double radius2 = SafeMath.Square(radius);
            int size = map.GetSize();

            int failed = 0, count = 0;
            int perim = 4 * (int)Math.Floor(radius * Math.PI / 4 * (1 + 1.0 / 10));  // simplified perimeter
            // 简化版：用圆形 + 噪声扰动。完整版用 ctrlVals Float32Array + cubicInterpolation。
            // TODO: 完整移植 ClumpPlacer.js 的 ctrlCoords/ctrlVals/cubicInterpolation 噪声管线。

            int cx = (int)_center.X, cy = (int)_center.Y;
            int x0 = (int)Math.Max(0, cx - radius - 1);
            int y0 = (int)Math.Max(0, cy - radius - 1);
            int x1 = (int)Math.Min(cx + radius + 1, size - 1);
            int y1 = (int)Math.Min(cy + radius + 1, size - 1);

            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    double dx = x - cx, dy = y - cy;
                    double dist2 = dx * dx + dy * dy;
                    if (dist2 >= radius2) continue;

                    count++;
                    var pos = new RmgenVector2D(x, y);
                    if (!map.InMapBounds(pos) || !constraint.Allows(pos)) { failed++; continue; }
                    points.Add(pos);
                }
            }

            return failed > count * _failFraction ? null : points;
        }
    }

    /// <summary>DiskPlacer（逐字移植 placer/centered/DiskPlacer.js）——简单圆盘。</summary>
    public sealed class DiskPlacer : IPlacer
    {
        private readonly double _radius;
        private RmgenVector2D _center;

        public DiskPlacer(double radius, RmgenVector2D centerPosition)
        { _radius = radius; _center = centerPosition; }

        public void SetCenterPosition(RmgenVector2D pos) { _center = pos; }

        public List<RmgenVector2D>? Place(IConstraint constraint)
        {
            var map = RmgenLibrary.CurrentMap;
            double r2 = _radius * _radius;
            var points = new List<RmgenVector2D>();
            int cx = (int)_center.X, cy = (int)_center.Y;
            int x0 = (int)Math.Max(0, cx - _radius), y0 = (int)Math.Max(0, cy - _radius);
            int x1 = (int)Math.Min(cx + _radius, map.GetSize() - 1);
            int y1 = (int)Math.Min(cy + _radius, map.GetSize() - 1);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    double dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy > r2) continue;
                    var pos = new RmgenVector2D(x, y);
                    if (map.InMapBounds(pos) && constraint.Allows(pos))
                        points.Add(pos);
                }
            return points;
        }
    }

    /// <summary>RectPlacer（逐字移植 placer/noncentered/RectPlacer.js）——矩形区域。</summary>
    public sealed class RectPlacer : IPlacer
    {
        private readonly int _x1, _y1, _x2, _y2;

        public RectPlacer(int x1, int y1, int x2, int y2) => (_x1, _y1, _x2, _y2) = (x1, y1, x2, y2);

        public List<RmgenVector2D>? Place(IConstraint constraint)
        {
            var map = RmgenLibrary.CurrentMap;
            var points = new List<RmgenVector2D>();
            for (int x = _x1; x <= _x2; x++)
                for (int y = _y1; y <= _y2; y++)
                {
                    var pos = new RmgenVector2D(x, y);
                    if (map.InMapBounds(pos) && constraint.Allows(pos))
                        points.Add(pos);
                }
            return points;
        }
    }

    /// <summary>MapBoundsPlacer（逐字移植 placer/noncentered/MapBoundsPlacer.js）——全图。</summary>
    public sealed class MapBoundsPlacer : IPlacer
    {
        public List<RmgenVector2D>? Place(IConstraint constraint)
        {
            var map = RmgenLibrary.CurrentMap;
            var points = new List<RmgenVector2D>();
            int size = map.GetSize();
            for (int x = 0; x < size; x++)
                for (int y = 0; y < size; y++)
                {
                    var pos = new RmgenVector2D(x, y);
                    if (constraint.Allows(pos)) points.Add(pos);
                }
            return points;
        }
    }

    /// <summary>HeightPlacer（逐字移植 placer/noncentered/HeightPlacer.js）——按高度选择。</summary>
    public sealed class HeightPlacer : IPlacer
    {
        public enum Mode { IncludeMin = -1, ExcludeMin = 0, IncludeMax = 1, ExcludeMax = 2 }

        private readonly double _minHeight, _maxHeight;
        private readonly Mode _mode;
        private readonly RandomMap _map;

        public HeightPlacer(RandomMap map, Mode mode, double min, double max)
        { _map = map; _mode = mode; _minHeight = min; _maxHeight = max; }

        public List<RmgenVector2D>? Place(IConstraint constraint)
        {
            var points = new List<RmgenVector2D>();
            int hms = _map.GetSize() + 1;
            for (int x = 0; x < hms; x++)
                for (int z = 0; z < hms; z++)
                {
                    double h = _map.Height[x][z];
                    bool minOk = _mode == Mode.IncludeMin || _mode == Mode.IncludeMax ? h >= _minHeight : h > _minHeight;
                    bool maxOk = _mode == Mode.IncludeMin || _mode == Mode.ExcludeMin ? h <= _maxHeight : h < _maxHeight;
                    if (minOk && maxOk)
                    {
                        var pos = new RmgenVector2D(x, z);
                        if (constraint.Allows(pos)) points.Add(pos);
                    }
                }
            return points;
        }
    }
}
