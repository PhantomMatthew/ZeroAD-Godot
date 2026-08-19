using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen
{
    /// <summary>heightmap/heightmap.js 高度图操作库（逐字移植）。
    /// 注意上游约定：中间计算用 double 数组，写回 RandomMap.Height（float32）才截断。
    /// diamond-square/侵蚀直接改写传入的 heightmap（默认即 g_Map.height）。</summary>
    public static class HeightmapLib
    {
        /// <summary>getMinAndMaxHeight。</summary>
        public static (double min, double max) GetMinAndMaxHeight(float[][] heightmap)
        {
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            for (int x = 0; x < heightmap.Length; ++x)
                for (int y = 0; y < heightmap[x].Length; ++y)
                {
                    min = Math.Min(min, heightmap[x][y]);
                    max = Math.Max(max, heightmap[x][y]);
                }
            return (min, max);
        }

        /// <summary>rescaleHeightmap——保持整体形状把值域线性映射到 [min,max]。</summary>
        public static void RescaleHeightmap(double minHeight, double maxHeight, float[][] heightmap)
        {
            var (oldMin, oldMax) = GetMinAndMaxHeight(heightmap);
            for (int x = 0; x < heightmap.Length; ++x)
                for (int y = 0; y < heightmap[x].Length; ++y)
                    heightmap[x][y] = (float)(minHeight + (heightmap[x][y] - oldMin) /
                        (oldMax - oldMin) * (maxHeight - minHeight));
        }

        /// <summary>setBaseTerrainDiamondSquare——倍涨初始高度图（diamond-square）
        /// 直到覆盖目标尺寸再居中裁剪。RNG 顺序：每轮 square 遍历（x 外 y 内，
        /// 仅对角新点抽数）→ diamond 遍历（每个未定义点按内/边分支抽数）。</summary>
        public static void SetBaseTerrainDiamondSquare(RmgenRng rng, float[][] heightmap,
            double minHeight, double maxHeight, double[][]? initialHeightmap, double smoothness)
        {
            double[][] current;
            if (initialHeightmap != null)
            {
                current = initialHeightmap;
            }
            else
            {
                current = new[]
                {
                    new[] { rng.RandFloat(minHeight / 2, maxHeight / 2), rng.RandFloat(minHeight / 2, maxHeight / 2) },
                    new[] { rng.RandFloat(minHeight / 2, maxHeight / 2), rng.RandFloat(minHeight / 2, maxHeight / 2) },
                };
            }

            double heightRange = maxHeight - minHeight;
            double offset = heightRange / 2;

            double[][] newHeightmap = current;
            while (current.Length < heightmap.Length)
            {
                int oldWidth = current.Length;
                int newWidth = 2 * oldWidth - 1;
                var nh = new double[newWidth][];

                // Square
                for (int x = 0; x < newWidth; ++x)
                {
                    nh[x] = new double[newWidth];
                    for (int y = 0; y < newWidth; ++y)
                    {
                        if (x % 2 == 0 && y % 2 == 0)   // 旧点
                            nh[x][y] = current[x / 2][y / 2];
                        else if (x % 2 == 1 && y % 2 == 1)   // 对角邻点新点
                        {
                            nh[x][y] = (current[(x - 1) / 2][(y - 1) / 2] +
                                        current[(x + 1) / 2][(y - 1) / 2] +
                                        current[(x - 1) / 2][(y + 1) / 2] +
                                        current[(x + 1) / 2][(y + 1) / 2]) / 4;
                            nh[x][y] += (nh[x][y] - minHeight) / heightRange *
                                rng.RandFloat(-offset, offset);
                        }
                        else   // 直边新点（diamond 阶段填）
                            nh[x][y] = double.NaN;
                    }
                }

                // Diamond
                for (int x = 0; x < newWidth; ++x)
                    for (int y = 0; y < newWidth; ++y)
                    {
                        if (!double.IsNaN(nh[x][y]))
                            continue;

                        if (x > 0 && x + 1 < newWidth - 1 && y > 0 && y + 1 < newWidth - 1)
                            nh[x][y] = (nh[x + 1][y] + nh[x][y + 1] + nh[x - 1][y] + nh[x][y - 1]) / 4;
                        else if (x < newWidth - 1 && y > 0 && y < newWidth - 1)   // 左边
                            nh[x][y] = (nh[x + 1][y] + nh[x][y + 1] + nh[x][y - 1]) / 3;
                        else if (x > 0 && y > 0 && y < newWidth - 1)   // 右边
                            nh[x][y] = (nh[x][y + 1] + nh[x - 1][y] + nh[x][y - 1]) / 3;
                        else if (x > 0 && x < newWidth - 1 && y < newWidth - 1)   // 下边
                            nh[x][y] = (nh[x + 1][y] + nh[x][y + 1] + nh[x - 1][y]) / 3;
                        else if (x > 0 && x < newWidth - 1 && y > 0)   // 上边
                            nh[x][y] = (nh[x + 1][y] + nh[x - 1][y] + nh[x][y - 1]) / 3;
                        else
                            continue;   // 角点保持未定义（上游同样跳过）

                        nh[x][y] += (nh[x][y] - minHeight) / heightRange *
                            rng.RandFloat(-offset, offset);
                    }

                current = nh;
                newHeightmap = nh;
                offset /= Math.Pow(2, smoothness);
            }

            // 居中裁剪到目标尺寸
            int shiftX = (newHeightmap.Length - heightmap.Length) / 2;
            int shiftY = (newHeightmap[0].Length - heightmap[0].Length) / 2;
            for (int x = 0; x < heightmap.Length; ++x)
                for (int y = 0; y < heightmap[0].Length; ++y)
                    heightmap[x][y] = (float)newHeightmap[x + shiftX][y + shiftY];
        }

        /// <summary>getGrad——环绕梯度场（splashErodeMap 用）。</summary>
        private static (double x, double y)[][] GetGrad(float[][] scalarField)
        {
            int maxX = scalarField.Length;
            int maxY = scalarField[0].Length;
            var vectorField = new (double, double)[maxX][];
            for (int x = 0; x < maxX; ++x)
            {
                vectorField[x] = new (double, double)[maxY];
                for (int y = 0; y < maxY; ++y)
                    vectorField[x][y] = (
                        scalarField[(x + 1) % maxX][y] - scalarField[x][y],
                        scalarField[x][(y + 1) % maxY] - scalarField[x][y]);
            }
            return vectorField;
        }

        /// <summary>splashErodeMap——按坡度顺流搬移高度（环绕边界，就地改写）。</summary>
        public static void SplashErodeMap(double strength, float[][] heightmap)
        {
            int maxX = heightmap.Length;
            int maxY = heightmap[0].Length;

            var dHeight = GetGrad(heightmap);

            for (int x = 0; x < maxX; ++x)
            {
                int nextX = (x + 1) % maxX;
                int prevX = (x + maxX - 1) % maxX;
                for (int y = 0; y < maxY; ++y)
                {
                    int nextY = (y + 1) % maxY;
                    int prevY = (y + maxY - 1) % maxY;

                    var slopes = new[]
                    {
                        -dHeight[x][y].x, -dHeight[x][y].y,
                        dHeight[prevX][y].x, dHeight[x][prevY].y,
                    };

                    double sumSlopes = 0;
                    foreach (double s in slopes)
                        if (s > 0)
                            sumSlopes += s;

                    var drain = new double[4];
                    for (int i = 0; i < 4; ++i)
                        if (slopes[i] > 0)
                            drain[i] += Math.Min(strength * slopes[i] / sumSlopes, slopes[i]);

                    double sumDrain = 0;
                    foreach (double d in drain)
                        sumDrain += d;

                    heightmap[x][y] -= (float)sumDrain;
                    heightmap[nextX][y] += (float)drain[0];
                    heightmap[x][nextY] += (float)drain[1];
                    heightmap[prevX][y] += (float)drain[2];
                    heightmap[x][prevY] += (float)drain[3];
                }
            }
        }

        /// <summary>getTileCenteredHeightmap——顶点高度 → 图块中心高度（小一圈）。</summary>
        public static float[][] GetTileCenteredHeightmap(float[][] heightmap)
        {
            int maxX = heightmap.Length - 1;
            int maxY = heightmap[0].Length - 1;
            var tchm = new float[maxX][];
            for (int x = 0; x < maxX; ++x)
            {
                tchm[x] = new float[maxY];
                for (int y = 0; y < maxY; ++y)
                    tchm[x][y] = (float)(0.25 * (heightmap[x][y] + heightmap[x + 1][y] +
                        heightmap[x][y + 1] + heightmap[x + 1][y + 1]));
            }
            return tchm;
        }

        /// <summary>getInclineMap——每图块的最大倾向向量（2D）。</summary>
        public static (double x, double y)[][] GetInclineMap(float[][] heightmap)
        {
            int maxX = heightmap.Length - 1;
            int maxY = heightmap[0].Length - 1;
            var inclineMap = new (double, double)[maxX][];
            for (int x = 0; x < maxX; ++x)
            {
                inclineMap[x] = new (double, double)[maxY];
                for (int y = 0; y < maxY; ++y)
                {
                    double dx = heightmap[x + 1][y] - heightmap[x][y];
                    double dy = heightmap[x][y + 1] - heightmap[x][y];
                    double nextDx = heightmap[x + 1][y + 1] - heightmap[x][y + 1];
                    double nextDy = heightmap[x + 1][y + 1] - heightmap[x + 1][y];
                    inclineMap[x][y] = (0.5 * (dx + nextDx), 0.5 * (dy + nextDy));
                }
            }
            return inclineMap;
        }

        /// <summary>getSlopeMap——倾向向量模长（float32 存储）。</summary>
        public static float[][] GetSlopeMap(float[][] heightmap)
        {
            var inclineMap = GetInclineMap(heightmap);
            int maxX = inclineMap.Length;
            var slopeMap = new float[maxX][];
            for (int x = 0; x < maxX; ++x)
            {
                int maxY = inclineMap[x].Length;
                slopeMap[x] = new float[maxY];
                for (int y = 0; y < maxY; ++y)
                    slopeMap[x][y] = (float)SafeMath.EuclidDistance2D(0, 0,
                        inclineMap[x][y].x, inclineMap[x][y].y);
            }
            return slopeMap;
        }

        /// <summary>getStartLocationsByHeightmap——maxTries 轮随机取点，
        /// 保留最小 pairwise 距离最大的一组（含上游非圆图恒真比较的原样语义）。</summary>
        public static List<RmgenVector2D>? GetStartLocationsByHeightmap(RmgenRng rng, RandomMap map,
            double minHeight, double maxHeight, int maxTries, double minDistToBorder,
            int numberOfPlayers, bool isCircular)
        {
            var validStartLoc = new List<RmgenVector2D>();
            var mapCenter = map.GetCenter();
            int mapSize = map.GetSize();

            var heightConstraint = new HeightConstraint(map, minHeight, maxHeight);

            for (int x = (int)minDistToBorder; x < mapSize - minDistToBorder; ++x)
                for (int y = (int)minDistToBorder; y < mapSize - minDistToBorder; ++y)
                {
                    var position = new RmgenVector2D(x, y);
                    // 上游 (!isCircular || distance) < limit：非圆图恒为 true（true→1 与数值比较）
                    double lhs = !isCircular ? 1.0 : position.DistanceTo(mapCenter);
                    if (heightConstraint.Allows(position) &&
                        lhs < mapSize / 2.0 - minDistToBorder)
                        validStartLoc.Add(position);
                }

            if (validStartLoc.Count == 0)
                return null;

            double maxMinDist = 0;
            List<RmgenVector2D>? finalStartLoc = null;

            for (int tries = 0; tries < maxTries; ++tries)
            {
                var startLoc = new List<RmgenVector2D>();
                double minDist = double.PositiveInfinity;

                for (int p = 0; p < numberOfPlayers; ++p)
                    startLoc.Add(rng.PickRandom(validStartLoc));

                for (int p1 = 0; p1 < numberOfPlayers - 1; ++p1)
                    for (int p2 = p1 + 1; p2 < numberOfPlayers; ++p2)
                    {
                        double dist = startLoc[p1].DistanceTo(startLoc[p2]);
                        if (dist < minDist)
                            minDist = dist;
                    }

                if (minDist > maxMinDist)
                {
                    maxMinDist = minDist;
                    finalStartLoc = startLoc;
                }
            }

            return finalStartLoc;
        }

        /// <summary>高度范围内的候选点（getPointsByHeight 的 avoid 条目）。</summary>
        public readonly struct HeightPoint
        {
            public readonly int X, Y;
            public readonly double Dist;
            public HeightPoint(int x, int y, double dist) { X = x; Y = y; Dist = dist; }
        }

        /// <summary>getPointsByHeight——在高度范围内随机均匀取点（彼此/回避点保持
        /// 最小间距；maxTries 次尝试）。wild_lake 不传 avoidClass。</summary>
        public static List<HeightPoint> GetPointsByHeight(RmgenRng rng, RandomMap map,
            double minHeight, double maxHeight, List<HeightPoint> avoidPoints,
            double minDistance = 20, int? maxTries = null, bool isCircular = false)
        {
            int tries = maxTries ?? 2 * map.GetSize();
            var points = new List<HeightPoint>();
            var placements = new List<HeightPoint>(avoidPoints);
            var validVertices = new List<HeightPoint>();
            double r = 0.5 * (map.Height.Length - 1);   // 图心 x/y 兼半径

            for (int x = (int)minDistance; x < map.Height.Length - minDistance; ++x)
                for (int y = (int)minDistance; y < map.Height[x].Length - minDistance; ++y)
                {
                    if (map.Height[x][y] > minHeight && map.Height[x][y] < maxHeight &&
                        (!isCircular ||
                            r - SafeMath.EuclidDistance2D(x, y, r, r) >= minDistance))
                        validVertices.Add(new HeightPoint(x, y, minDistance));
                }

            for (int t = 0; t < tries; ++t)
            {
                if (validVertices.Count == 0)
                    break;
                var point = rng.PickRandom(validVertices);
                bool ok = true;
                foreach (var p in placements)
                    if (SafeMath.EuclidDistance2D(p.X, p.Y, point.X, point.Y) <=
                        Math.Max(minDistance, p.Dist))
                    { ok = false; break; }
                if (ok)
                {
                    points.Add(point);
                    placements.Add(point);
                }
            }

            return points;
        }
    }
}
