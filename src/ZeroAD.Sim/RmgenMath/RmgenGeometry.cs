using System;
using System.Collections.Generic;

namespace ZeroAD.Sim.RmgenMath;

/// <summary>rmgen/math.js 几何辅助（逐字移植）——山脊图/路径放置依赖。
/// 全部 double；三角/开方走 SafeMath 保证跨平台一致。</summary>
public static class RmgenGeometry
{
    /// <summary>g_TileVertices（math.js）——图块 4 个角点偏移。</summary>
    public static readonly RmgenVector2D[] TileVertices =
        { new(0, 0), new(0, 1), new(1, 0), new(1, 1) };

    /// <summary>diskArea(radius) = PI * radius²。</summary>
    public static double DiskArea(double radius) => SafeMath.PI * SafeMath.Square(radius);

    /// <summary>distanceOfPointFromLine——点到有向直线的带符号距离
    /// （叉积：sub(lineStart,lineEnd).normalize().cross(sub(point,lineEnd))）。</summary>
    public static double DistanceOfPointFromLine(RmgenVector2D lineStart, RmgenVector2D lineEnd, RmgenVector2D point)
    {
        var a = RmgenVector2D.Sub(lineStart, lineEnd);
        a.Normalize();
        return a.Cross(RmgenVector2D.Sub(point, lineEnd));
    }

    /// <summary>testLineIntersection——两条给定宽度的线段是否相交/过近。</summary>
    public static bool TestLineIntersection(RmgenVector2D start1, RmgenVector2D end1,
        RmgenVector2D start2, RmgenVector2D end2, double width)
    {
        var start1end1 = RmgenVector2D.Sub(start1, end1);
        var start2end2 = RmgenVector2D.Sub(start2, end2);
        var start1start2 = RmgenVector2D.Sub(start1, start2);

        return
            Math.Abs(DistanceOfPointFromLine(start1, end1, start2)) < width ||
            Math.Abs(DistanceOfPointFromLine(start1, end1, end2)) < width ||
            Math.Abs(DistanceOfPointFromLine(start2, end2, start1)) < width ||
            Math.Abs(DistanceOfPointFromLine(start2, end2, end1)) < width ||
            start1end1.Cross(start1start2) * start1end1.Cross(RmgenVector2D.Sub(start1, end2)) <= 0 &&
            start2end2.Cross(start1start2) * start2end2.Cross(RmgenVector2D.Sub(start2, end1)) >= 0;
    }

    /// <summary>getBoundingBox——点集的轴对齐包围盒（min/max 角点）。</summary>
    public static (RmgenVector2D min, RmgenVector2D max) GetBoundingBox(IReadOnlyList<RmgenVector2D> points)
    {
        var min = points[0];
        var max = points[0];
        foreach (var p in points)
        {
            min = new RmgenVector2D(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y));
            max = new RmgenVector2D(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y));
        }
        return (min, max);
    }

    /// <summary>getPointsInBoundingBox——包围盒内全部整数图块点（含端点，x 外层 y 内层）。</summary>
    public static List<RmgenVector2D> GetPointsInBoundingBox(RmgenVector2D min, RmgenVector2D max)
    {
        var points = new List<RmgenVector2D>();
        for (int x = (int)min.X; x <= (int)max.X; ++x)
            for (int y = (int)min.Y; y <= (int)max.Y; ++y)
                points.Add(new RmgenVector2D(x, y));
        return points;
    }

    /// <summary>distributePointsOnCircularSegment——圆弧上等距取点（含两端），返回 (点, 角度)。</summary>
    public static (List<RmgenVector2D> points, List<double> angles) DistributePointsOnCircularSegment(
        int pointCount, double maxAngle, double startAngle, double radius, RmgenVector2D center)
    {
        var points = new List<RmgenVector2D>();
        var angles = new List<double>();

        for (int i = 0; i < pointCount; ++i)
        {
            double angle = startAngle + maxAngle * i / Math.Max(1, pointCount - 1);
            angles.Add(angle);
            var v = new RmgenVector2D(radius, 0);
            v.Rotate(-angle);
            points.Add(RmgenVector2D.Add(center, v));
        }

        return (points, angles);
    }

    /// <summary>distributePointsOnCircle——整圆上等距取点。</summary>
    public static (List<RmgenVector2D> points, List<double> angles) DistributePointsOnCircle(
        int pointCount, double startAngle, double radius, RmgenVector2D center)
        => DistributePointsOnCircularSegment(
            pointCount, 2 * SafeMath.PI * (pointCount - 1) / pointCount, startAngle, radius, center);

    /// <summary>sortPointsShortestCycle（math.js）——贪心插入使回路伸长最小的点序
    /// （返回索引）。≤3 点直接按原序。</summary>
    public static List<int> SortPointsShortestCycle(IReadOnlyList<RmgenVector2D> points)
    {
        var order = new List<int>();
        var distances = new List<double>();
        if (points.Count <= 3)
        {
            for (int i = 0; i < points.Count; ++i)
                order.Add(i);
            return order;
        }

        // 先放前 3 点
        var pointsToAdd = new List<RmgenVector2D>(points);
        for (int i = 0; i < 3; ++i)
        {
            order.Add(i);
            pointsToAdd.RemoveAt(0);
            if (i != 0)
                distances.Add(points[order[i]].DistanceTo(points[order[i - 1]]));
        }

        distances.Add(points[order[0]].DistanceTo(points[order[^1]]));

        // 剩余点插到伸长最小处
        int numPointsToAdd = pointsToAdd.Count;
        for (int i = 0; i < numPointsToAdd; ++i)
        {
            int indexToAddTo = 0;
            double minEnlengthen = double.PositiveInfinity;
            double minDist1 = 0;
            double minDist2 = 0;
            for (int k = 0; k < order.Count; ++k)
            {
                double dist1 = pointsToAdd[0].DistanceTo(points[order[k]]);
                double dist2 = pointsToAdd[0].DistanceTo(points[order[(k + 1) % order.Count]]);

                double enlengthen = dist1 + dist2 - distances[k];
                if (enlengthen < minEnlengthen)
                {
                    indexToAddTo = k;
                    minEnlengthen = enlengthen;
                    minDist1 = dist1;
                    minDist2 = dist2;
                }
            }
            order.Insert(indexToAddTo + 1, i + 3);
            distances.RemoveAt(indexToAddTo);
            distances.InsertRange(indexToAddTo, new[] { minDist1, minDist2 });
            pointsToAdd.RemoveAt(0);
        }

        return order;
    }
}
