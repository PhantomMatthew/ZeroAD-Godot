using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly double[] _widths;
        private readonly RmgenRng _rng;

        /// <param name="terrains">string → 按 "tex|entity" 解析;string[]/List&lt;string&gt; →
        /// RandomTerrain;object[](string 与 string[] 混合,森林变体用) → 逐元素解析后 RandomTerrain;
        /// 已是 ITerrain 的直接用。</param>
        public LayeredPainter(IReadOnlyList<object> terrains, int[] widths, RmgenRng rng)
            : this(terrains, System.Array.ConvertAll(widths, w => (double)w), rng)
        {
        }

        /// <summary>浮点 widths 版（上游 widths 本就是浮点比较，如 oasis 的 forestDistance）。</summary>
        public LayeredPainter(IReadOnlyList<object> terrains, double[] widths, RmgenRng rng)
        {
            _terrains = new List<ITerrain>();
            foreach (var t in terrains)
                _terrains.Add(Resolve(t));
            // 上游不做长度校验：widths 比 terrains-1 少时，索引 i 封顶在 widths.Length，
            // 多出来的末尾 terrain 永不使用（rmgen2 addLayeredPatches 就是 4 层 + [1,1]，
            // tier4Terrain 实际是死层）；widths 比 terrains-1 多时（flood 的
            // [shore, main] + [shoreRadius, 100]），多出的 widths 也永不命中——封顶的是 i，
            // 超出的层根本选不中。两种都照搬，不做校验。
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
                double width = 0;
                int i = 0;
                for (; i < _widths.Length; i++)
                {
                    width += _widths[i];
                    if (width >= distance) break;
                }
                // 上游无界：widths 比 terrains 长时 i 可能越界，封顶在最后一层（照 flood.js）。
                if (i >= _terrains.Count) i = _terrains.Count - 1;
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

    /// <summary>SmoothElevationPainter（逐字移植 painter/SmoothElevationPainter.js + 依赖的
    /// breadthFirstSearchPaint）——以"到区域边界的 BFS 距离"（顶点级，border=1 起）把高度
    /// 从现状渐变到目标值：a = distance ≤ blendRadius ? (distance-1)/blendRadius : 1；
    /// SET: h = (1-a)*current + a*elevation；MODIFY: h = current + a*elevation。
    /// 每个被刷顶点都消耗一次 randFloat(-0.5, 0.5)（乘 randomElevation，即便为 0 也抽）。
    /// 末趟 3×3 邻域均值再与自身平均。newHeight 用 float 存储（Float32Array 保真）。</summary>
    public sealed class SmoothElevationPainter : IPainter
    {
        public enum SmoothType { Blurry, Solid }

        private readonly double _elevation;
        private readonly SmoothType _type;
        private readonly double _blendRadius;
        private readonly bool _relative;
        private readonly double _randomElevation;
        private readonly RmgenRng _rng;

        /// <param name="relative">true 对应上游 ELEVATION_MODIFY（相对抬升），
        /// false 为 ELEVATION_SET（绝对设定，既有调用方默认）。</param>
        public SmoothElevationPainter(RmgenRng rng, SmoothType type, double elevation,
            double blendRadius, bool relative = false, double randomElevation = 0)
        {
            _rng = rng;
            _type = type; _elevation = elevation; _blendRadius = blendRadius;
            _relative = relative; _randomElevation = randomElevation;
        }

        public void Paint(Area area)
        {
            var map = RmgenLibrary.CurrentMap;
            int heightmapSize = map.GetSize() + 1;   // 顶点网格比图块网格大 1

            // 记录改写前高度
            var gotHeightPt = new byte[heightmapSize, heightmapSize];
            var newHeight = new float[heightmapSize, heightmapSize];

            // 收集区域内/相邻的顶点（brushSize=2 窗口）
            const int brushSize = 2;
            var heightPoints = new List<(int x, int z)>();
            foreach (var point in area.GetPoints())
                for (int dx = -1; dx < 1 + brushSize; ++dx)
                {
                    int nx = (int)point.X + dx;
                    for (int dz = -1; dz < 1 + brushSize; ++dz)
                    {
                        int nz = (int)point.Y + dz;
                        var position = new RmgenVector2D(nx, nz);
                        if (map.ValidHeight(position) && gotHeightPt[nx, nz] == 0)
                        {
                            newHeight[nx, nz] = (float)map.GetHeight(position);
                            gotHeightPt[nx, nz] = 1;
                            heightPoints.Add((nx, nz));
                        }
                    }
                }

            // 顶点在区域内 ⟺ 它相邻的 4 个图块任一个在区域内
            bool WithinArea(RmgenVector2D position)
            {
                foreach (var tv in RmgenGeometry.TileVertices)
                    if (area.Contains(RmgenVector2D.Sub(position, tv)))
                        return true;
                return false;
            }

            // BFS：区域外边界点作种子（dist=0），向内逐圈距离递增
            var saw = new byte[heightmapSize, heightmapSize];
            var dist = new ushort[heightmapSize, heightmapSize];
            bool WithinGrid(int x, int z) => Math.Min(x, z) >= 0 && Math.Max(x, z) < heightmapSize;

            var pointQueue = new Queue<(int x, int z)>();
            foreach (var point in area.GetPoints())
                for (int dx = -1; dx < 1 + brushSize; ++dx)
                {
                    int nx = (int)point.X + dx;
                    for (int dz = -1; dz < 1 + brushSize; ++dz)
                    {
                        int nz = (int)point.Y + dz;
                        if (!WithinGrid(nx, nz) || WithinArea(new RmgenVector2D(nx, nz)) || saw[nx, nz] != 0)
                            continue;
                        saw[nx, nz] = 1;
                        dist[nx, nz] = 0;
                        pointQueue.Enqueue((nx, nz));
                    }
                }

            while (pointQueue.Count > 0)
            {
                var (px, pz) = pointQueue.Dequeue();
                int distance = dist[px, pz];
                var p = new RmgenVector2D(px, pz);

                if (WithinArea(p))
                {
                    double a = 1;
                    if (distance <= _blendRadius)
                        a = (distance - 1) / _blendRadius;

                    if (!_relative)
                        newHeight[px, pz] = (float)((1 - a) * map.GetHeight(p));

                    newHeight[px, pz] += (float)(a * _elevation +
                        _rng.RandFloat(-0.5, 0.5) * _randomElevation);
                }

                for (int dx = -1; dx <= 1; ++dx)
                {
                    int nx = px + dx;
                    for (int dz = -1; dz <= 1; ++dz)
                    {
                        int nz = pz + dz;
                        if (!WithinGrid(nx, nz) || !WithinArea(new RmgenVector2D(nx, nz)) || saw[nx, nz] != 0)
                            continue;
                        saw[nx, nz] = 1;
                        dist[nx, nz] = (ushort)(distance + 1);
                        pointQueue.Enqueue((nx, nz));
                    }
                }
            }

            // 平滑收尾：3×3 邻域均值与自身再平均（只处理区域内顶点）
            foreach (var (x, z) in heightPoints)
            {
                if (!WithinArea(new RmgenVector2D(x, z)))
                    continue;

                int count = 0;
                double sum = 0;
                for (int dx = -1; dx <= 1; ++dx)
                {
                    int nx = x + dx;
                    for (int dz = -1; dz <= 1; ++dz)
                    {
                        int nz = z + dz;
                        if (map.ValidHeight(new RmgenVector2D(nx, nz)))
                        {
                            sum += newHeight[nx, nz];
                            ++count;
                        }
                    }
                }

                map.SetHeight(new RmgenVector2D(x, z), (newHeight[x, z] + sum / count) / 2);
            }
        }
    }

    /// <summary>SmoothingPainter（逐字移植 painter/SmoothingPainter.js）——曼哈顿距离加权
    /// 邻域平滑。注意上游克隆的高度图只用于取尺寸，读写都落在活地图上
    /// （Gauss-Seidel 式，按 Area 点序 × 4 角点序）。</summary>
    public sealed class SmoothingPainter : IPainter
    {
        private readonly int _size;
        private readonly double _strength;
        private readonly int _iterations;

        public SmoothingPainter(double size, double strength, int iterations)
        {
            if (size < 1)
                throw new ArgumentException("Invalid size: " + size);
            if (strength <= 0 || strength > 1)
                throw new ArgumentException("Invalid strength: " + strength);
            if (iterations <= 0)
                throw new ArgumentException("Invalid iterations: " + iterations);
            _size = (int)Math.Floor(size);
            _strength = strength;
            _iterations = iterations;
        }

        public void Paint(Area area)
        {
            var map = RmgenLibrary.CurrentMap;
            var brushPoints = RmgenGeometry.GetPointsInBoundingBox(
                new RmgenVector2D(-_size, -_size), new RmgenVector2D(_size, _size));

            for (int i = 0; i < _iterations; ++i)
            {
                int hms = map.GetSize() + 1;
                var seen = new byte[hms, hms];

                foreach (var point in area.GetPoints())
                    foreach (var tileVertex in RmgenGeometry.TileVertices)
                    {
                        var vertex = RmgenVector2D.Add(point, tileVertex);
                        if (!map.ValidHeight(vertex) || seen[(int)vertex.X, (int)vertex.Y] != 0)
                            continue;
                        seen[(int)vertex.X, (int)vertex.Y] = 1;

                        double sumWeightedHeights = 0;
                        double sumWeights = 0;

                        foreach (var brushPoint in brushPoints)
                        {
                            var position = RmgenVector2D.Add(vertex, brushPoint);
                            double distance = Math.Abs(brushPoint.X) + Math.Abs(brushPoint.Y);
                            if (distance == 0 || !map.ValidHeight(position))
                                continue;

                            sumWeightedHeights += map.GetHeight(position) / distance;
                            sumWeights += 1 / distance;
                        }

                        map.SetHeight(vertex,
                            _strength * sumWeightedHeights / sumWeights +
                            (1 - _strength) * map.GetHeight(vertex));
                    }
            }
        }
    }

    /// <summary>HeightmapPainter（逐字移植 painter/HeightmapPainter.js）——把外部高度图
    /// （可为子区域裁剪）按双三次插值刷到地图高度表。高度值为源图的 u16 原始值
    /// （0..0xFFFF），经 scaleHeight 映射到 normalMin..normalMax（320 图基准）。</summary>
    public sealed class HeightmapPainter : IPainter
    {
        private readonly float[][] _heightmap;
        private readonly double? _normalMinHeight, _normalMaxHeight;
        private readonly RandomMap _map;

        public HeightmapPainter(RandomMap map, float[][] heightmap,
            double? normalMinHeight = null, double? normalMaxHeight = null)
        {
            _map = map;
            _heightmap = heightmap;
            VerticesPerSide = heightmap.Length;
            _normalMinHeight = normalMinHeight;
            _normalMaxHeight = normalMaxHeight;
        }

        public int VerticesPerSide { get; }

        public double GetScale() => (double)VerticesPerSide / (_map.GetSize() + 1);

        public double ScaleHeight(double height)
        {
            if (_normalMinHeight == null || _normalMaxHeight == null)
                return height / GetScale() / RmgenConstants.HEIGHT_UNITS_PER_METRE;

            double minHeight = _normalMinHeight.Value * (_map.GetSize() + 1) / 321.0;
            double maxHeight = _normalMaxHeight.Value * (_map.GetSize() + 1) / 321.0;
            return minHeight + (maxHeight - minHeight) * height / 0xFFFF;
        }

        public void Paint(Area area)
        {
            double scale = GetScale();
            int vps = VerticesPerSide;
            int hms = _map.GetSize() + 1;
            var seen = new byte[hms, hms];

            foreach (var point in area.GetPoints())
                foreach (var vertex in RmgenGeometry.TileVertices)
                {
                    var vertexPos = RmgenVector2D.Add(point, vertex);
                    if (!_map.ValidHeight(vertexPos) || seen[(int)vertexPos.X, (int)vertexPos.Y] != 0)
                        continue;
                    seen[(int)vertexPos.X, (int)vertexPos.Y] = 1;

                    var sourcePos = RmgenVector2D.Mult(vertexPos, scale);
                    var sourceTilePos = sourcePos;
                    sourceTilePos.Floor();

                    // brushPosition = max((0,0), min(sourceTilePos-(1,1), (vps,vps)-(3,3)-(1,1)))
                    int bx = (int)Math.Max(0, Math.Min(sourceTilePos.X - 1, vps - 3 - 1));
                    int bz = (int)Math.Max(0, Math.Min(sourceTilePos.Y - 1, vps - 3 - 1));

                    // 4×4 采样（getPointsInBoundingBox 顺序：x 外层 y 内层）
                    var s = new double[16];
                    int k = 0;
                    for (int sx = bx; sx <= bx + 3; ++sx)
                        for (int sz = bz; sz <= bz + 3; ++sz)
                            s[k++] = ScaleHeight(_heightmap[sx][sz]);

                    _map.SetHeight(vertexPos, Interpolation.BicubicInterpolation(
                        new RmgenVector2D(sourcePos.X - bx - 1, sourcePos.Y - bz - 1),
                        s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7],
                        s[8], s[9], s[10], s[11], s[12], s[13], s[14], s[15]));
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

    /// <summary>ElevationBlendingPainter（原版 painter/ElevationBlendingPainter.js）:
    /// 区域高度向 targetHeight 按 strength 加权插值。</summary>
    public sealed class ElevationBlendingPainter : IPainter
    {
        private readonly double _targetHeight, _strength;
        public ElevationBlendingPainter(double targetHeight, double strength)
        { _targetHeight = targetHeight; _strength = strength; }
        public void Paint(Area area)
        {
            foreach (var point in area.GetPoints())
                area.Map.SetHeight(point,
                    _strength * _targetHeight + (1 - _strength) * area.Map.GetHeight(point));
        }
    }

    /// <summary>纹理阵列绘制器（原版 painter/TerrainTextureArrayPainter.js）:
    /// 按一维纹理索引图给区域铺纹理(源尺寸 vs 地图尺寸的 scale 缩放)。</summary>
    public sealed class TerrainTextureArrayPainter : IPainter
    {
        private readonly int[] _textureIDs;
        private readonly string[] _textureNames;
        public TerrainTextureArrayPainter(int[] textureIDs, string[] textureNames)
        { _textureIDs = textureIDs; _textureNames = textureNames; }
        public void Paint(Area area)
        {
            int sourceSize = (int)Math.Sqrt(_textureIDs.Length);
            double scale = (double)sourceSize / area.Map.GetSize();
            foreach (var point in area.GetPoints())
            {
                int sx = (int)(point.X * scale), sy = (int)(point.Y * scale);
                if (sx < 0 || sy < 0 || sx >= sourceSize || sy >= sourceSize) continue;
                area.Map.SetTexture(point, _textureNames[_textureIDs[sx * sourceSize + sy]]);
            }
        }
    }

    /// <summary>城市绘制器（原版 painter/CityPainter.js）:
    /// 网格扫描区域,按模板列表(maxCount/constraint/painter)摆建筑——
    /// 障碍框旋转 + 凸多边形避碰,旋转角度对齐 cityAngle + 90° 随机。</summary>
    public sealed class CityPainter : IPainter
    {
        /// <summary>单建筑模板规格(原版 templates 元素:margin/constraints/painters 可选)。</summary>
        public sealed class CityTemplate
        {
            public required string TemplateName;
            public int MaxCount = int.MaxValue;
            public double Margin;
            public IConstraint? Constraint;
            public IPainter? Painter;
        }

        private readonly List<CityTemplate> _templates;
        private readonly double _angle;
        private readonly int _playerID;
        private readonly RmgenRng _rng;

        public CityPainter(RmgenRng rng, IEnumerable<CityTemplate> templates,
            double angle, int playerID)
        { _rng = rng; _templates = new(templates); _angle = angle; _playerID = playerID; }

        public void Paint(Area area)
        {
            var templates = new List<CityTemplate>(_templates);
            var counts = new Dictionary<string, int>();
            foreach (var t in templates) counts[t.TemplateName] = 0;

            var map = area.Map;
            var mapCenter = map.GetCenter();
            int mapSize = map.GetSize();

            // 已占格跟踪(原版 processed Uint8Array;每实体障碍框经 TileClass 标占)。
            var tileClass = new TileClass(mapSize);
            var processed = new bool[mapSize, mapSize];

            for (double x = 0; x < mapSize; x += 0.5)
            {
                for (double y = 0; y < mapSize; y += 0.5)
                {
                    var point = new RmgenVector2D(x, y);
                    point.RotateAround(_angle, mapCenter);
                    point.Round();
                    if (!area.Contains(point) || processed[(int)point.X, (int)point.Y]
                        || !map.ValidTilePassable(point))
                        continue;
                    processed[(int)point.X, (int)point.Y] = true;

                    // 洗牌后试摆(原版 shuffleArray:失败换下一模板)。
                    var shuffled = new List<CityTemplate>(templates);
                    for (int i = shuffled.Count - 1; i > 0; i--)
                    {
                        int j = _rng.RandIntExclusive(0, i + 1);
                        (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
                    }

                    foreach (var template in shuffled)
                    {
                        if (template.Constraint != null && !template.Constraint.Allows(point))
                            continue;

                        // 障碍框四角(原版 obstructionCorners × rotate(buildingAngle) + point)。
                        var half = RmgenLibrary.GetObstructionSize(template.TemplateName, template.Margin);
                        double buildingAngle = _angle + _rng.RandIntInclusive(0, 3) * SafeMath.PI / 2;
                        var corners = new List<RmgenVector2D>
                        {
                            new(0, 0),
                            new(half.X, 0),
                            new(0, half.Y),
                            new(half.X, half.Y),
                        };
                        var obstructionCorners = new List<RmgenVector2D>();
                        foreach (var c in corners)
                        {
                            var q = c; q.Rotate(buildingAngle);
                            obstructionCorners.Add(RmgenVector2D.Add(point, q));
                        }

                        // 凸多边形避碰(原版:区域内 + 未占 + 可通行)。
                        var placer = new ConvexPolygonPlacer(obstructionCorners, 0);
                        var obstructionPoints = placer.Place(
                            new AndConstraint(
                                new StayAreasConstraint(new[] { area }),
                                new AvoidTileClassConstraint(tileClass, 0),
                                new PassableMapAreaConstraint(map)));
                        if (obstructionPoints == null || obstructionPoints.Count == 0)
                            continue;

                        // 实体摆放(原版 placeEntityPassable:中心 = 障碍框均值)。
                        double cx = 0, cy = 0;
                        foreach (var c in obstructionCorners) { cx += c.X; cy += c.Y; }
                        cx /= obstructionCorners.Count; cy /= obstructionCorners.Count;
                        map.PlaceEntityPassable(template.TemplateName, _playerID,
                            new RmgenVector2D(cx, cy), -buildingAngle);

                        template.Painter?.Paint(new Area(map, obstructionPoints));

                        foreach (var p in obstructionPoints) tileClass.Add(p);

                        counts[template.TemplateName]++;
                        templates.RemoveAll(t => counts[t.TemplateName] >= t.MaxCount);
                        break;
                    }
                }
            }
        }
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
