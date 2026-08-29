using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen
{
    /// <summary>ChainPlacer（逐字移植 placer/centered/ChainPlacer.js，114 行）。
    /// 随机链式放置圆形——每次从边缘随机选中心点画圆。</summary>
    public sealed class ChainPlacer : ICenteredPlacer
    {
        private readonly double _minRadius, _maxRadius;
        private readonly double _numCircles;   // 上游允许浮点（for i < numCircles 等效 ceil）
        private readonly double _failFraction;
        private readonly double _maxDistance;
        private readonly List<int> _queue;
        private RmgenVector2D _center;
        private readonly RmgenRng _rng;

        public ChainPlacer(RmgenRng rng, double minRadius, double maxRadius, double numCircles,
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
    /// 周长噪声圆团：size = 平均点数（≈面积，radius = sqrt(size/π)）。
    /// ctrlCoords/ctrlVals/noise 用 float 存储复现 Float32Array 截断（保真度关键）；
    /// 每步角度从圆心沿单位向量累加、逐点 floor 去重。</summary>
    public sealed class ClumpPlacer : ICenteredPlacer
    {
        private readonly double _size, _coherence, _smoothness, _failFraction;
        private RmgenVector2D _center;
        private readonly RmgenRng _rng;

        public ClumpPlacer(RmgenRng rng, double size, double coherence = 0.5, double smoothness = 0.1,
            double failFraction = 0, RmgenVector2D? centerPosition = null)
        {
            _rng = rng; _size = size; _coherence = coherence;
            _smoothness = smoothness; _failFraction = failFraction;
            _center = centerPosition ?? default;
            if (centerPosition.HasValue) SetCenterPosition(centerPosition.Value);
        }

        public void SetCenterPosition(RmgenVector2D position) { var p = position; p.Round(); _center = p; }

        public List<RmgenVector2D>? Place(IConstraint constraint)
        {
            var map = RmgenLibrary.CurrentMap;
            // 预检：中心必须在图内且满足约束
            if (!map.InMapBounds(_center) || !constraint.Allows(_center)) return null;

            var points = new List<RmgenVector2D>();
            int size = map.GetSize();
            var gotRet = new byte[size, size];
            double radius = SafeMath.Sqrt(_size / SafeMath.PI);
            double perim = 4 * radius * 2 * SafeMath.PI;
            int intPerim = (int)Math.Ceiling(perim);

            int ctrlPts = 1 + (int)Math.Floor(1.0 / Math.Max(_smoothness, 1.0 / intPerim));
            if (ctrlPts > radius * 2 * SafeMath.PI)
                ctrlPts = (int)Math.Floor(radius * 2 * SafeMath.PI) + 1;

            var noise = new float[intPerim];
            var ctrlCoords = new float[ctrlPts + 1];
            var ctrlVals = new float[ctrlPts + 1];

            // 生成插值噪声的控制点
            for (int i = 0; i < ctrlPts; i++)
            {
                ctrlCoords[i] = (float)(i * perim / ctrlPts);
                ctrlVals[i] = (float)_rng.RandFloat(0, 2);
            }

            int c = 0;
            int looped = 0;
            for (int i = 0; i < intPerim; ++i)
            {
                if ((double)ctrlCoords[(c + 1) % ctrlPts] < i && looped == 0)
                {
                    c = (c + 1) % ctrlPts;
                    if (c == ctrlPts - 1)
                        looped = 1;
                }

                noise[i] = (float)Interpolation.CubicInterpolation(
                    1,
                    (i - (double)ctrlCoords[c]) /
                        ((looped != 0 ? perim : (double)ctrlCoords[(c + 1) % ctrlPts]) - ctrlCoords[c]),
                    ctrlVals[(c + ctrlPts - 1) % ctrlPts],
                    ctrlVals[c],
                    ctrlVals[(c + 1) % ctrlPts],
                    ctrlVals[(c + 2) % ctrlPts]);
            }

            int failed = 0, count = 0;
            for (int stepAngle = 0; stepAngle < intPerim; ++stepAngle)
            {
                var position = _center;
                var radiusUnitVector = new RmgenVector2D(0, 1);
                radiusUnitVector.Rotate(-2 * SafeMath.PI * stepAngle / perim);
                int maxRadiusSteps = (int)Math.Ceiling(radius * (1 + (1 - _coherence) * noise[stepAngle]));

                count += maxRadiusSteps;
                for (int stepRadius = 0; stepRadius < maxRadiusSteps; ++stepRadius)
                {
                    var tilePos = position;
                    tilePos.Floor();

                    if (map.InMapBounds(tilePos) && constraint.Allows(tilePos))
                    {
                        if (gotRet[(int)tilePos.X, (int)tilePos.Y] == 0)
                        {
                            gotRet[(int)tilePos.X, (int)tilePos.Y] = 1;
                            points.Add(tilePos);
                        }
                    }
                    else
                        ++failed;

                    position.Add(radiusUnitVector);
                }
            }

            return failed > count * _failFraction ? null : points;
        }
    }

    /// <summary>DiskPlacer（逐字移植 placer/centered/DiskPlacer.js）——简单圆盘。</summary>
    public sealed class DiskPlacer : ICenteredPlacer
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

    /// <summary>ConvexPolygonPlacer（逐字移植 placer/noncentered/ConvexPolygonPlacer.js）——
    /// 返回给定点凸包内的全部图块点。Ctor 先对顶点 round。</summary>
    public sealed class ConvexPolygonPlacer : IPlacer
    {
        private readonly List<RmgenVector2D> _polygonVertices;
        private readonly double _failFraction;

        public ConvexPolygonPlacer(IReadOnlyList<RmgenVector2D> points, double failFraction = 0)
        {
            var rounded = points.Select(p => { var q = p; q.Round(); return q; }).ToList();
            _polygonVertices = GetConvexHull(rounded);
            _failFraction = failFraction;
        }

        public List<RmgenVector2D>? Place(IConstraint constraint)
        {
            var map = RmgenLibrary.CurrentMap;
            var points = new List<RmgenVector2D>();
            int count = 0, failed = 0;

            var (min, max) = RmgenGeometry.GetBoundingBox(_polygonVertices);
            foreach (var point in RmgenGeometry.GetPointsInBoundingBox(min, max))
            {
                bool outside = false;
                for (int i = 0; i < _polygonVertices.Count; i++)
                    if (RmgenGeometry.DistanceOfPointFromLine(
                            _polygonVertices[i], _polygonVertices[(i + 1) % _polygonVertices.Count], point) > 0)
                    { outside = true; break; }
                if (outside) continue;

                ++count;
                if (map.InMapBounds(point) && constraint.Allows(point))
                    points.Add(point);
                else
                    ++failed;
            }

            return failed <= _failFraction * count ? points : null;
        }

        /// <summary>gift-wrapping 凸包（上游 getConvexHull；输入已按值去重后引用比较即值比较）。</summary>
        private static List<RmgenVector2D> GetConvexHull(List<RmgenVector2D> points)
        {
            var uniquePoints = new List<RmgenVector2D>();
            foreach (var point in points)
                if (uniquePoints.All(p => p.X != point.X || p.Y != point.Y))
                    uniquePoints.Add(point);

            // 最左点起手
            var leftmost = uniquePoints[0];
            foreach (var p in uniquePoints)
                if (p.X < leftmost.X) leftmost = p;
            var result = new List<RmgenVector2D> { leftmost };

            while (result.Count < uniquePoints.Count)
            {
                RmgenVector2D? nextLeftmostPoint = null;
                foreach (var point in uniquePoints)
                {
                    if (RmgenVector2D.IsEqualTo(point, result[^1]))
                        continue;
                    if (!nextLeftmostPoint.HasValue ||
                        RmgenGeometry.DistanceOfPointFromLine(nextLeftmostPoint.Value, result[^1], point) <= 0)
                        nextLeftmostPoint = point;
                }

                // 回到已知点——剩余点都在凸包内
                if (result.Contains(nextLeftmostPoint!.Value))
                    break;
                result.Add(nextLeftmostPoint.Value);
            }

            return result;
        }
    }

    /// <summary>PathPlacer（逐字移植 placer/noncentered/PathPlacer.js）——两点间蜿蜒路径。
    /// Start/End/Width 为公开字段（上游 MountainRangeBuilder 在构造后再赋值）。
    /// ctrlVals/noise 用 float 存储复现 Float32Array 截断。</summary>
    public sealed class PathPlacer : IPlacer
    {
        public RmgenVector2D Start, End;
        public double Width;
        private readonly double _waviness, _smoothness, _offset, _tapering, _failFraction;
        private readonly RmgenRng _rng;

        public PathPlacer(RmgenRng rng, double waviness, double smoothness,
            double offset, double tapering, double failFraction = 0)
        {
            _rng = rng;
            _waviness = waviness; _smoothness = smoothness;
            _offset = offset; _tapering = tapering; _failFraction = failFraction;
        }

        public List<RmgenVector2D>? Place(IConstraint constraint)
        {
            var map = RmgenLibrary.CurrentMap;
            double pathLength = Start.DistanceTo(End);

            int numStepsWaviness = 1 + (int)Math.Floor(pathLength / 4 * _waviness);
            int numStepsLength = 1 + (int)Math.Floor(pathLength / 4 * _smoothness);
            int offset = 1 + (int)Math.Floor(pathLength / 4 * _offset);

            // 随机控制值（Float32Array 语义）
            var ctrlVals = new float[numStepsWaviness];
            for (int j = 1; j < numStepsWaviness - 1; ++j)
                ctrlVals[j] = (float)_rng.RandFloat(-offset, offset);

            // 三次样条插值成平滑 1D 噪声
            int totalSteps = numStepsWaviness * numStepsLength;
            var noise = new float[totalSteps + 1];
            for (int j = 0; j < numStepsWaviness; ++j)
                for (int k = 0; k < numStepsLength; ++k)
                    noise[j * numStepsLength + k] = (float)Interpolation.CubicInterpolation(
                        1,
                        (double)k / numStepsLength,
                        ctrlVals[(j + numStepsWaviness - 1) % numStepsWaviness],
                        ctrlVals[j],
                        ctrlVals[(j + 1) % numStepsWaviness],
                        ctrlVals[(j + 2) % numStepsWaviness]);

            // 沿直线路径叠加噪声
            var pathPerpendicular = RmgenVector2D.Sub(End, Start);
            pathPerpendicular.Normalize();
            pathPerpendicular = pathPerpendicular.Perpendicular();
            var segments1 = new List<RmgenVector2D>();
            var segments2 = new List<RmgenVector2D>();

            for (int j = 0; j < totalSteps; ++j)
            {
                double step1 = (double)j / totalSteps;
                double step2 = (double)(j + 1) / totalSteps;
                var stepStart = RmgenVector2D.Add(RmgenVector2D.Mult(Start, 1 - step1), RmgenVector2D.Mult(End, step1));
                var stepEnd = RmgenVector2D.Add(RmgenVector2D.Mult(Start, 1 - step2), RmgenVector2D.Mult(End, step2));

                var noiseStart = RmgenVector2D.Add(stepStart, RmgenVector2D.Mult(pathPerpendicular, noise[j]));
                var noiseEnd = RmgenVector2D.Add(stepEnd, RmgenVector2D.Mult(pathPerpendicular, noise[j + 1]));
                var noisePerpendicular = RmgenVector2D.Sub(noiseEnd, noiseStart);
                noisePerpendicular.Normalize();
                noisePerpendicular = noisePerpendicular.Perpendicular();

                double taperedWidth = (1 - step1 * _tapering) * Width / 2;

                var s1 = RmgenVector2D.Sub(noiseStart, RmgenVector2D.Mult(noisePerpendicular, taperedWidth));
                s1.Round();
                segments1.Add(s1);
                var s2 = RmgenVector2D.Add(noiseEnd, RmgenVector2D.Mult(noisePerpendicular, taperedWidth));
                s2.Round();
                segments2.Add(s2);
            }

            // 逐段刷凸多边形
            int size = map.GetSize();
            var gotRet = new byte[size, size];
            var retVec = new List<RmgenVector2D>();
            int failed = 0;

            for (int j = 0; j < segments1.Count - 1; ++j)
            {
                var points = new ConvexPolygonPlacer(
                    new[] { segments1[j], segments1[j + 1], segments2[j], segments2[j + 1] },
                    double.PositiveInfinity).Place(new NullConstraint());
                if (points == null)
                    continue;

                foreach (var point in points)
                {
                    if (!constraint.Allows(point))
                    {
                        if (_failFraction == 0)
                            return null;
                        ++failed;
                        continue;
                    }

                    if (map.InMapBounds(point) && gotRet[(int)point.X, (int)point.Y] == 0)
                    {
                        retVec.Add(point);
                        gotRet[(int)point.X, (int)point.Y] = 1;
                    }
                }
            }

            return failed > _failFraction * Width * pathLength ? null : retVec;
        }
    }

    /// <summary>HeightPlacer（逐字移植 placer/noncentered/HeightPlacer.js）——按高度选择。
    /// 遍历图块 0..size-1（上游 getPointsInBoundingBox([0,0],[size-1,size-1]) 含端点）；
    /// 高度取样于整数图块坐标（corner-based 高度表的前 size×size 项）。</summary>
    /// <summary>实体障碍放置器(原版 noncentered/EntitiesObstructionPlacer):
    /// 给定实体的模板障碍框(±margin)在自身位置/朝向上的四角,逐实体取
    /// ConvexPolygonPlacer 在框内过 constraint 的全部点——建筑间精确避碰。</summary>
    public sealed class EntitiesObstructionPlacer : IPlacer
    {
        private readonly IReadOnlyList<RmgenEntity> _entities;
        private readonly double _margin, _failFraction;

        public EntitiesObstructionPlacer(IReadOnlyList<RmgenEntity> entities,
            double margin = 0, double failFraction = 0)
        { _entities = entities; _margin = margin; _failFraction = failFraction; }

        public List<RmgenVector2D>? Place(IConstraint constraint)
        {
            var points = new List<RmgenVector2D>();
            foreach (var entity in _entities)
            {
                var half = RmgenLibrary.GetObstructionSize(entity.TemplateName, _margin);
                half.X *= 0.5; half.Y *= 0.5;

                var corners = new List<RmgenVector2D>
                {
                    new(-half.X, -half.Y),
                    new(-half.X, +half.Y),
                    new(+half.X, -half.Y),
                    new(+half.X, +half.Y),
                };
                // 原版:corner.rotate(-rotation.y) 再平移到实体位置。
                var rotated = new List<RmgenVector2D>();
                foreach (var c in corners)
                {
                    var q = c; q.Rotate(-entity.Orientation);
                    rotated.Add(RmgenVector2D.Add(entity.Position, q));
                }

                var sub = new ConvexPolygonPlacer(rotated, _failFraction).Place(constraint);
                if (sub != null) points.AddRange(sub);
            }
            return points;
        }
    }

    /// <summary>蜿蜒路径放置器(原版 noncentered/RandomPathPlacer):
    /// 起终点间随机角步进,每步 DiskPlacer 盖一点(比 sin 形 PathPlacer 更乱;
    /// offset 内缩起止,blended 加 0.5 偏转向)。</summary>
    public sealed class RandomPathPlacer : IPlacer
    {
        private readonly RmgenVector2D _pathStart, _pathEnd;
        private readonly double _offsetSquared;
        private readonly bool _blended;
        private readonly DiskPlacer _diskPlacer;
        private readonly RmgenRng _rng;
        private readonly int _maxPathLength;

        public RandomPathPlacer(RmgenRng rng, RmgenVector2D pathStart, RmgenVector2D pathEnd,
            double pathWidth, double offset, bool blended)
        {
            _rng = rng;
            _pathEnd = pathEnd;
            // 原版:pathStart = start + normalize(end-start)*offset,round。
            var dir = RmgenVector2D.Sub(pathEnd, pathStart);
            dir.Normalize();
            var start = RmgenVector2D.Add(pathStart, RmgenVector2D.Mult(dir, offset));
            start.Round();
            _pathStart = start;
            _offsetSquared = offset * offset;
            _blended = blended;
            _diskPlacer = new DiskPlacer(pathWidth, start);
            // 原版 fractionToTiles(2) = mapSize*2。
            _maxPathLength = RmgenLibrary.CurrentMap.GetSize() * 2;
        }

        public List<RmgenVector2D>? Place(IConstraint constraint)
        {
            int pathLength = 0;
            var points = new List<RmgenVector2D>();
            var position = _pathStart;

            while (position.DistanceToSquared(_pathEnd) >= _offsetSquared
                && pathLength++ < _maxPathLength)
            {
                // 原版:step = (1,0).rotate(-getAngle(start,end) + PI/2*(randFloat(-1,1)+blended?0.5:0))。
                double baseAngle = -SafeMath.Atan2(
                    _pathEnd.Y - _pathStart.Y, _pathEnd.X - _pathStart.X);
                double jitter = SafeMath.PI / 2
                    * (_rng.RandFloat(-1, 1) + (_blended ? 0.5 : 0));
                var step = new RmgenVector2D(1, 0);
                step.Rotate(baseAngle + jitter);
                position.Add(step);
                position.Round();

                _diskPlacer.SetCenterPosition(position);
                var disk = _diskPlacer.Place(constraint);
                if (disk == null) continue;
                foreach (var p in disk)
                    if (!points.Any(q => RmgenVector2D.IsEqualTo(q, p)))
                        points.Add(p);
            }
            return points;
        }
    }

    public sealed class HeightPlacer : IPlacer
    {
        /// <summary>上游 Elevation_* 常量（是否包含 min/max 边界）。</summary>
        public enum Mode
        {
            ExcludeMinExcludeMax = 0,
            IncludeMinExcludeMax = 1,
            ExcludeMinIncludeMax = 2,
            IncludeMinIncludeMax = 3,
        }

        private readonly double _minHeight, _maxHeight;
        private readonly Mode _mode;
        private readonly RandomMap _map;

        public HeightPlacer(RandomMap map, Mode mode, double min, double max)
        { _map = map; _mode = mode; _minHeight = min; _maxHeight = max; }

        public List<RmgenVector2D>? Place(IConstraint constraint)
        {
            var points = new List<RmgenVector2D>();
            int size = _map.GetSize();
            for (int x = 0; x < size; x++)
                for (int z = 0; z < size; z++)
                {
                    double h = _map.Height[x][z];
                    bool within = _mode switch
                    {
                        Mode.ExcludeMinExcludeMax => h > _minHeight && h < _maxHeight,
                        Mode.IncludeMinExcludeMax => h >= _minHeight && h < _maxHeight,
                        Mode.ExcludeMinIncludeMax => h > _minHeight && h <= _maxHeight,
                        _ => h >= _minHeight && h <= _maxHeight,
                    };
                    if (within)
                    {
                        var pos = new RmgenVector2D(x, z);
                        if (constraint.Allows(pos)) points.Add(pos);
                    }
                }
            return points;
        }
    }
}
