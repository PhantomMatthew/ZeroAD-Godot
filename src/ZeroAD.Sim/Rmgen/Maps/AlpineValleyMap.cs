using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;
using ZeroAD.Sim.Rmgen.Common;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>alpine_valley.js（528 行）——高山峡谷。
    /// MountainRangeBuilder 图算法布置互不封闭的山脊（顶点为玩家附近四环点 + 图心，
    /// 随机选边、排除相交/过近/成环的边，PathPlacer 蜿蜒山径 + 两端 ClumpPlacer 圆山），
    /// 再按高度刷悬崖/雪线。biome 为图专属 alpine/ 目录（late_spring|winter）。
    /// 上游发电机 yield（加载进度）与环境设置（setSkySet/setSun*）按既有移植约定省略；
    /// placePlayersNomad 未移植（无 Nomad 设置）。</summary>
    public sealed class AlpineValleyMap : StandardMap
    {
        protected override double HeightLand => 3;

        /// <summary>上游 alpine_valley.json SupportedBiomes = "alpine/"（图专属 biome 目录）。</summary>
        protected override IReadOnlyList<string> SupportedBiomes => new[] { "alpine/late_spring", "alpine/winter" };

        public override MapExport Generate(RmgenRng rng, MapSettings settings)
        {
            InitContext(rng, settings);
            var biome = Biome;
            var map = Map;

            const double heightLand = 3;
            const double heightOffsetBump = 2;
            const double snowlineHeight = 29;
            const double heightMountain = 30;

            var clFood = new TileClass(MapSize);
            var mapCenter = map.GetCenter();

            var (_, playerPosition, _, startAngle) = RmgenCommon.PlayerPlacementCircle(
                rng, map, NumPlayers, RmgenLibrary.FractionToTiles(0.35, MapSize));

            RmgenCommon.PlacePlayerBases(rng, map, settings, biome.MainTerrain0, ClPlayer, biome, playerPosition);

            // ── 山脊（MountainRangeBuilder 图算法）──
            double mountainWidth = RmgenLibrary.ScaleByMapSize(9, 15, MapSize);
            var mountainPainters = new IPainter[]
            {
                new LayeredPainter(new object[] { biome.Cliff, biome.MainTerrain }, new[] { 3 }, rng),
                new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, heightMountain, 2),
                new TileClassPainter(ClHill),
            };
            new MountainRangeBuilder(rng, NumPlayers,
                new PathPlacer(rng, 0.4, RmgenLibrary.ScaleByMapSize(3, 12, MapSize), 0.1, 0.1, 0.1),
                mountainPainters,
                RmgenLibrary.AvoidClasses(ClPlayer, 20),
                RmgenLibrary.ScaleByMapSize(10, 15, MapSize),
                mountainWidth,
                3,
                BuildMountainVertices(mapCenter, startAngle))
                .CreateMountainRanges();

            // ── 雪线刷漆（Elevation_ExcludeMin_ExcludeMax=0 / IncludeMinIncludeMax=3）──
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, heightLand + 0.1, snowlineHeight,
                HeightPlacer.Mode.ExcludeMinExcludeMax, biome.Cliff);
            RmgenLibrary.PaintTerrainBasedOnHeight(rng, snowlineHeight, heightMountain,
                HeightPlacer.Mode.IncludeMinIncludeMax, biome.SnowLimited);

            // ── 起伏（ELEVATION_MODIFY 相对抬升）──
            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(20, 50, MapSize), 0.3, 0.06,
                    double.PositiveInfinity),
                new IPainter[]
                {
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry,
                        heightOffsetBump, 2, relative: true),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 10),
                RmgenLibrary.ScaleByMapSize(100, 200, MapSize));

            // ── 丘陵 ──
            RmgenLibrary.CreateAreas(rng,
                new ClumpPlacer(rng, RmgenLibrary.ScaleByMapSize(40, 150, MapSize), 0.2, 0.1,
                    double.PositiveInfinity),
                new IPainter[]
                {
                    new LayeredPainter(new object[] { biome.Cliff, biome.SnowLimited }, new[] { 2 }, rng),
                    new SmoothElevationPainter(rng, SmoothElevationPainter.SmoothType.Blurry, heightMountain, 2),
                    new TileClassPainter(ClHill),
                },
                RmgenLibrary.AvoidClasses(ClPlayer, 20, ClHill, 14),
                RmgenLibrary.ScaleByMapSize(10, 80, MapSize) * NumPlayers);

            // ── 森林（getTreeCounts(500, 3000, 0.7)，浮点数量）──
            double forestTrees = 0.7 * RmgenLibrary.ScaleByMapSize(500, 3000, MapSize);
            double stragglerTrees = (1 - 0.7) * RmgenLibrary.ScaleByMapSize(500, 3000, MapSize);

            var pForest = new object[] { biome.ForestFloor + "|" + biome.Tree1, biome.ForestFloor };
            var types = new[]
            {
                new object[]
                {
                    new object[] { biome.ForestFloor, biome.MainTerrain, pForest },
                    new object[] { biome.ForestFloor, pForest },
                }
            };

            double size = forestTrees / (RmgenLibrary.ScaleByMapSize(2, 8, MapSize) * NumPlayers);
            int num = (int)Math.Floor(size / types.Length);
            foreach (var type in types)
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, forestTrees / num, 0.1, 0.1, double.PositiveInfinity),
                    new IPainter[] { new LayeredPainter(type, new[] { 2 }, rng), new TileClassPainter(ClForest) },
                    RmgenLibrary.AvoidClasses(ClPlayer, 12, ClForest, 10, ClHill, 0),
                    num);

            // ── 泥地斑块（dirt→halfSnow→snowLimited 三层渐变）──
            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 84, MapSize),
                RmgenLibrary.ScaleByMapSize(8, 128, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, patchSize, 0.3, 0.06, 0.5),
                    new IPainter[]
                    {
                        new LayeredPainter(new object[]
                        {
                            new object[] { biome.Dirt, biome.HalfSnow },
                            new object[] { biome.HalfSnow, biome.SnowLimited },
                        }, new[] { 2 }, rng),
                        new TileClassPainter(ClDirt),
                    },
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 12),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            // ── 草地斑块（tier2）──
            foreach (double patchSize in new[]
            {
                RmgenLibrary.ScaleByMapSize(2, 32, MapSize),
                RmgenLibrary.ScaleByMapSize(3, 48, MapSize),
                RmgenLibrary.ScaleByMapSize(5, 80, MapSize),
            })
                RmgenLibrary.CreateAreas(rng,
                    new ClumpPlacer(rng, patchSize, 0.3, 0.06, 0.5),
                    new IPainter[] { new TerrainPainter(biome.Tier2Terrain, rng) },
                    RmgenLibrary.AvoidClasses(ClForest, 0, ClHill, 0, ClDirt, 5, ClPlayer, 12),
                    RmgenLibrary.ScaleByMapSize(15, 45, MapSize));

            // ── 矿藏 ──
            var group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, biome.StoneSmall, 0, 2, 0, 4, 0, 2 * SafeMath.PI, 1),
                new ScatterObject(rng, biome.StoneLarge, 1, 1, 0, 4, 0, 2 * SafeMath.PI, 4),
            }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, biome.StoneSmall, 2, 5, 1, 3) }, true, ClRock);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClRock, 10, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, biome.MetalLarge, 1, 1, 0, 4) }, true, ClMetal);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 1, ClPlayer, 20, ClMetal, 10, ClRock, 5, ClHill, 1),
                RmgenLibrary.ScaleByMapSize(4, 16, MapSize), 100);

            // ── 装饰岩石 ──
            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 1) }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(16, 262, MapSize), 50);

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, biome.RockLarge, 1, 2, 0, 1),
                new ScatterObject(rng, biome.RockMedium, 1, 3, 0, 2),
            }, true);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 0, ClHill, 0),
                RmgenLibrary.ScaleByMapSize(8, 131, MapSize), 50);

            // ── 食物 ──
            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, biome.MainHuntableAnimal, 5, 7, 0, 4) }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, ClHill, 1, clFood, 20),
                3 * NumPlayers, 50);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, biome.FruitBush, 5, 7, 0, 4) }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 20, ClHill, 1, clFood, 10),
                rng.RandIntInclusive(1, 4) * NumPlayers + 2, 50);

            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, biome.SecondaryHuntableAnimal, 2, 3, 0, 2) }, true, clFood);
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClForest, 0, ClPlayer, 10, ClHill, 1, clFood, 20),
                3 * NumPlayers, 50);

            // ── 散落树 ──
            GaiaEntities.CreateStragglerTrees(rng,
                new[] { biome.Tree1 },
                RmgenLibrary.AvoidClasses(ClForest, 1, ClHill, 1, ClPlayer, 12, ClMetal, 6, ClRock, 6),
                ClForest, stragglerTrees);

            // ── 草丛/灌木（planetm=1）──
            group = new ObjectGroup(new IGroupElement[]
                { new ScatterObject(rng, biome.GrassShort, 1, 2, 0, 1, -SafeMath.PI / 8, SafeMath.PI / 8) });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClHill, 2, ClPlayer, 2, ClDirt, 0),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, biome.Grass, 2, 4, 0, 1.8, -SafeMath.PI / 8, SafeMath.PI / 8),
                new ScatterObject(rng, biome.GrassShort, 3, 6, 1.2, 2.5, -SafeMath.PI / 8, SafeMath.PI / 8),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClHill, 2, ClPlayer, 2, ClDirt, 1, ClForest, 0),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize));

            group = new ObjectGroup(new IGroupElement[]
            {
                new ScatterObject(rng, biome.BushMedium, 1, 2, 0, 2),
                new ScatterObject(rng, biome.BushSmall, 2, 4, 0, 2),
            });
            RmgenLibrary.CreateObjectGroupsDeprecated(rng, group, 0,
                RmgenLibrary.AvoidClasses(ClHill, 1, ClPlayer, 1, ClDirt, 1),
                RmgenLibrary.ScaleByMapSize(13, 200, MapSize), 50);

            return map.MakeExportable();
        }

        /// <summary>山脊顶点：玩家四周 4 环点（0.49/0.34/0.34/0.18 圈半径，
        /// 相位 1/1.4/0.6/1 × π/玩家数）+ 图心。点不 round（上游此处不取整）。</summary>
        private List<RmgenVector2D> BuildMountainVertices(RmgenVector2D mapCenter, double startAngle)
        {
            var points = new List<RmgenVector2D>();
            points.AddRange(RmgenGeometry.DistributePointsOnCircle(NumPlayers,
                startAngle + SafeMath.PI / NumPlayers,
                RmgenLibrary.FractionToTiles(0.49, MapSize), mapCenter).points);
            points.AddRange(RmgenGeometry.DistributePointsOnCircle(NumPlayers,
                startAngle + SafeMath.PI / NumPlayers * 1.4,
                RmgenLibrary.FractionToTiles(0.34, MapSize), mapCenter).points);
            points.AddRange(RmgenGeometry.DistributePointsOnCircle(NumPlayers,
                startAngle + SafeMath.PI / NumPlayers * 0.6,
                RmgenLibrary.FractionToTiles(0.34, MapSize), mapCenter).points);
            points.AddRange(RmgenGeometry.DistributePointsOnCircle(NumPlayers,
                startAngle + SafeMath.PI / NumPlayers,
                RmgenLibrary.FractionToTiles(0.18, MapSize), mapCenter).points);
            points.Add(mapCenter);
            return points;
        }

        /// <summary>MountainRangeBuilder（逐字移植 alpine_valley.js 同名类）。
        /// 从近完全图开始：随机选边→若度数/成环/可画性允许则落成山脊并剔除
        /// 相交或过近的边，否则恢复可连接标记；重复直至所有边处理完。
        /// 顶点"相等"按索引比较（上游为同一 Vector2D 实例的引用相等）。</summary>
        private sealed class MountainRangeBuilder
        {
            private readonly int _numPlayers;
            private readonly PathPlacer _pathplacer;
            private readonly IPainter[] _painters;
            private readonly IConstraint _constraint;
            private readonly double _mountainWidth;
            private readonly double _minDistance;
            private readonly List<RmgenVector2D> _vertices;
            private readonly int[] _vertexDegree;
            private readonly int _maxDegree;
            private readonly List<int[]> _possibleEdges = new();
            private readonly bool[][] _verticesConnectable;
            private readonly RmgenRng _rng;

            private int _index;
            private int[] _currentEdge = null!;
            private RmgenVector2D _currentEdgeStart, _currentEdgeEnd;

            public MountainRangeBuilder(RmgenRng rng, int numPlayers, PathPlacer pathplacer,
                IPainter[] painters, IConstraint constraint, double passageWidth,
                double mountainWidth, int maxDegree, List<RmgenVector2D> points)
            {
                _rng = rng;
                _numPlayers = numPlayers;
                _pathplacer = pathplacer;
                _painters = painters;
                _constraint = constraint;
                _mountainWidth = mountainWidth;
                _minDistance = mountainWidth + passageWidth;
                _vertices = points;
                _vertexDegree = new int[points.Count];
                _maxDegree = maxDegree;
                InitPossibleEdges();
                _verticesConnectable = InitConnectable();
            }

            private void InitPossibleEdges()
            {
                for (int i = 0; i < _vertices.Count; ++i)
                    for (int j = _numPlayers; j < _vertices.Count; ++j)
                        if (j > i)
                            _possibleEdges.Add(new[] { i, j });
            }

            private bool[][] InitConnectable()
            {
                var c = new bool[_vertices.Count][];
                for (int i = 0; i < _vertices.Count; ++i)
                {
                    c[i] = new bool[_vertices.Count];
                    for (int j = 0; j < _vertices.Count; ++j)
                        c[i][j] = i >= _numPlayers || j >= _numPlayers || i == j ||
                            (i != j - 1 && i != j + 1);
                }
                return c;
            }

            private void SetConnectable(bool isConnectable)
            {
                _verticesConnectable[_currentEdge[0]][_currentEdge[1]] = isConnectable;
                _verticesConnectable[_currentEdge[1]][_currentEdge[0]] = isConnectable;
            }

            private void UpdateCurrentEdge()
            {
                _currentEdge = _possibleEdges[_index];
                _currentEdgeStart = _vertices[_currentEdge[0]];
                _currentEdgeEnd = _vertices[_currentEdge[1]];
            }

            /// <summary>剔除与当前山脊相交或过近的边。</summary>
            private void RemoveInvalidEdges()
            {
                for (int i = 0; i < _possibleEdges.Count; ++i)
                {
                    UpdateCurrentEdge();

                    var comparedEdge = _possibleEdges[i];
                    var comparedEdgeStart = _vertices[comparedEdge[0]];
                    var comparedEdgeEnd = _vertices[comparedEdge[1]];

                    bool edge0Equal = _currentEdge[0] == comparedEdge[0];
                    bool edge1Equal = _currentEdge[0] == comparedEdge[1];
                    bool edge2Equal = _currentEdge[1] == comparedEdge[1];
                    bool edge3Equal = _currentEdge[1] == comparedEdge[0];

                    if (!edge0Equal && !edge2Equal && !edge1Equal && !edge3Equal &&
                            RmgenGeometry.TestLineIntersection(_currentEdgeStart, _currentEdgeEnd,
                                comparedEdgeStart, comparedEdgeEnd, _minDistance) ||
                        (edge0Equal && !edge2Equal || !edge1Equal && edge3Equal) &&
                            RmgenGeometry.DistanceOfPointFromLine(_currentEdgeStart, _currentEdgeEnd,
                                comparedEdgeEnd) < _minDistance ||
                        (!edge0Equal && edge2Equal || edge1Equal && !edge3Equal) &&
                            RmgenGeometry.DistanceOfPointFromLine(_currentEdgeStart, _currentEdgeEnd,
                                comparedEdgeStart) < _minDistance)
                    {
                        _possibleEdges.RemoveAt(i);
                        --i;
                        if (_index > i)
                            --_index;
                    }
                }
            }

            /// <summary>DFS 判环——加入当前边是否会围出封闭区域。</summary>
            private bool HasCycles()
            {
                var tree = new List<int>();
                var backtree = new List<int>();
                var pointQueue = new List<int> { _currentEdge[0] };

                while (pointQueue.Count > 0)
                {
                    int selectedPoint = pointQueue[0];
                    pointQueue.RemoveAt(0);

                    if (!tree.Contains(selectedPoint))
                    {
                        tree.Add(selectedPoint);
                        backtree.Add(-1);
                    }

                    for (int i = 0; i < _vertices.Count; ++i)
                    {
                        if (_verticesConnectable[selectedPoint][i] ||
                            i == backtree[tree.LastIndexOf(selectedPoint)])
                            continue;

                        if (tree.Contains(i))
                            return true;

                        pointQueue.Insert(0, i);
                        tree.Add(i);
                        backtree.Add(selectedPoint);
                    }
                }

                return false;
            }

            private bool PaintCurrentEdge()
            {
                _pathplacer.Start = _currentEdgeStart;
                _pathplacer.End = _currentEdgeEnd;
                _pathplacer.Width = _mountainWidth;

                // 山脊本体
                if (RmgenLibrary.CreateArea(_pathplacer, _painters, _constraint) == null)
                    return false;

                // 两端各一座圆山
                foreach (var point in new[] { _currentEdgeStart, _currentEdgeEnd })
                    RmgenLibrary.CreateArea(
                        new ClumpPlacer(_rng, RmgenGeometry.DiskArea(_mountainWidth / 2), 0.95, 0.6,
                            double.PositiveInfinity, point),
                        _painters,
                        _constraint);

                return true;
            }

            public void CreateMountainRanges()
            {
                while (_possibleEdges.Count > 0)
                {
                    _index = _rng.RandIntExclusive(0, _possibleEdges.Count);
                    UpdateCurrentEdge();
                    SetConnectable(false);

                    if (_vertexDegree[_currentEdge[0]] < _maxDegree &&
                        _vertexDegree[_currentEdge[1]] < _maxDegree &&
                        !HasCycles() &&
                        PaintCurrentEdge())
                    {
                        ++_vertexDegree[_currentEdge[0]];
                        ++_vertexDegree[_currentEdge[1]];
                        RemoveInvalidEdges();
                    }
                    else
                        SetConnectable(true);

                    _possibleEdges.RemoveAt(_index);
                }
            }
        }
    }
}
