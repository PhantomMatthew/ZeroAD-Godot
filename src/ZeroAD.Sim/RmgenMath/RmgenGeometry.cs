using System;
using System.Collections.Generic;

namespace ZeroAD.Sim.RmgenMath;

/// <summary>rmgen/math.js 几何辅助（逐字移植）——山脊图/路径放置依赖。
/// 全部 double；三角/开方走 SafeMath 保证跨平台一致。</summary>
public static class RmgenGeometry
{
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
}
