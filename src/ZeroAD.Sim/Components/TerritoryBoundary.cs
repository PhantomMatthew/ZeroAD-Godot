using System;
using System.Collections.Generic;

namespace ZeroAD.Sim.Components;

/// <summary>领土边界轮廓描线——graphics/TerritoryBoundary.cpp 逐字移植。
/// 输入 = 位打包领土网格快照(owner 位 0-4 | connected 位 5 | blink 位 6,
/// 与上游 ICmpTerritoryManager 的掩码布局一致;本类内部再用 位7 作 processed)。
/// 算法:逐行扫描找"底边是边界的格"→ 从底边起逆时针(Moore 邻域变体)追闭合环,
/// 内沿环自然成顺时针(双色背靠背边条靠这个);曲率 ±4 自校验。
/// 输出世界坐标(米)点列。表现层专用(渲染/小地图),不进 sim 哈希。</summary>
public static class TerritoryBoundaryCalculator
{
    // 与上游 ICmpTerritoryManager.h:54-57 对齐的位布局。
    public const byte PlayerMask = 0x1F;
    public const byte ConnectedMask = 0x20;
    public const byte BlinkingMask = 0x40;
    private const byte ProcessedMask = 0x80;
    private const byte DiscrMask = BlinkingMask | PlayerMask;

    public sealed class Boundary
    {
        public int Owner;
        public bool Blinking;
        /// <summary>闭合环点列(世界米;首尾相同点隐式闭合)。</summary>
        public readonly List<(float X, float Z)> Points = new();
    }

    /// <summary>cellSize = 领土瓦片米数(TerritoryManager.CellSize=8)。</summary>
    public static List<Boundary> ComputeBoundaries(byte[] territory, int width, int cellSize)
    {
        int height = territory.Length / width;
        var grid = (byte[])territory.Clone();   // processed 位在副本上打
        var boundaries = new List<Boundary>();

        // 底边中点起点的边偏移(底/右/顶/左;格内局部坐标)。
        var edgeOffsets = new (float X, float Z)[]
        { (0.5f, 0f), (1f, 0.5f), (0.5f, 1f), (0f, 0.5f) };

        const int TileBottom = 0, TileRight = 1, TileTop = 2, TileLeft = 3;
        const int CurveCw = -1, CurveCcw = 1;

        for (int j = 0; j < height; j++)
        for (int i = 0; i < width; i++)
        {
            byte tileState = grid[j * width + i];
            byte tileDiscr = (byte)(tileState & DiscrMask);
            if (tileDiscr == 0) continue;   // 无主格
            bool processed = (tileState & ProcessedMask) != 0;
            bool eligible = j == 0
                || tileDiscr != (byte)(grid[(j - 1) * width + i] & DiscrMask);
            if (processed || !eligible) continue;

            int curvature = 0;
            var b = new Boundary
            {
                Owner = tileState & PlayerMask,
                Blinking = (tileState & BlinkingMask) != 0,
            };
            int dir = TileBottom;
            int cdir = dir, ci = i, cj = j;
            int maxI = width - 1, maxJ = height - 1;

            while (true)
            {
                var off = edgeOffsets[cdir];
                b.Points.Add(((ci + off.X) * cellSize, (cj + off.Z) * cellSize));

                switch (cdir)
                {
                    case TileBottom:
                        grid[cj * width + ci] |= ProcessedMask;
                        if (ci < maxI && cj > 0
                            && (grid[(cj - 1) * width + ci + 1] & DiscrMask) == tileDiscr)
                        { ci++; cj--; cdir = TileLeft; curvature += CurveCw; }
                        else if (ci < maxI
                            && (grid[cj * width + ci + 1] & DiscrMask) == tileDiscr)
                        { ci++; }
                        else
                        { cdir = TileRight; curvature += CurveCcw; }
                        break;
                    case TileRight:
                        if (ci < maxI && cj < maxJ
                            && (grid[(cj + 1) * width + ci + 1] & DiscrMask) == tileDiscr)
                        { ci++; cj++; cdir = TileBottom; curvature += CurveCw; }
                        else if (cj < maxJ
                            && (grid[(cj + 1) * width + ci] & DiscrMask) == tileDiscr)
                        { cj++; }
                        else
                        { cdir = TileTop; curvature += CurveCcw; }
                        break;
                    case TileTop:
                        if (ci > 0 && cj < maxJ
                            && (grid[(cj + 1) * width + ci - 1] & DiscrMask) == tileDiscr)
                        { ci--; cj++; cdir = TileRight; curvature += CurveCw; }
                        else if (ci > 0
                            && (grid[cj * width + ci - 1] & DiscrMask) == tileDiscr)
                        { ci--; }
                        else
                        { cdir = TileLeft; curvature += CurveCcw; }
                        break;
                    case TileLeft:
                        if (ci > 0 && cj > 0
                            && (grid[(cj - 1) * width + ci - 1] & DiscrMask) == tileDiscr)
                        { ci--; cj--; cdir = TileTop; curvature += CurveCw; }
                        else if (cj > 0
                            && (grid[(cj - 1) * width + ci] & DiscrMask) == tileDiscr)
                        { cj--; }
                        else
                        { cdir = TileBottom; curvature += CurveCcw; }
                        break;
                }

                if (ci == i && cj == j && cdir == dir) break;
            }

            if (curvature == 0 || Math.Abs(curvature) % 4 != 0)
                throw new InvalidOperationException(
                    $"territory boundary trace ended with curvature {curvature} (must be ±4)");
            boundaries.Add(b);
        }
        return boundaries;
    }
}
