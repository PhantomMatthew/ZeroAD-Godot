using System;

namespace ZeroAD.Sim.AI.CommonApi;

/// <summary>8-bit 影响力网格（原版 common-api/map-module.js 的 InfoMap）。
/// 用于 AI 建造选址（findBestTile）、资源密度图、领地影响力图。
/// 逐字移植：set(clamp)、addInfluence(linear/quadratic/constant)、findBestTile、isObstructedTile。</summary>
public sealed class InfoMap
{
    public readonly byte[] Map;
    public readonly int Width;
    public readonly int Height;
    public readonly int CellSize;
    public readonly int Length;
    public int MaxVal = 255;

    public InfoMap(int width, int height, int cellSize)
    {
        Width = width; Height = height; CellSize = cellSize;
        Length = width * height;
        Map = new byte[Length];
    }

    /// <summary>从已有 byte 数组拷贝构造。</summary>
    public InfoMap(int width, int height, int cellSize, byte[] source)
    {
        Width = width; Height = height; CellSize = cellSize;
        Length = width * height;
        Map = new byte[Length];
        Array.Copy(source, Map, Math.Min(Length, source.Length));
    }

    public void SetMaxVal(int val) => MaxVal = val;

    /// <summary>世界坐标 → 网格坐标 [x, y]。</summary>
    public (int x, int y) GamePosToMapPos(float px, float pz)
        => ((int)(px / CellSize), (int)(pz / CellSize));

    /// <summary>世界坐标处的值。</summary>
    public byte Point(float px, float pz)
    {
        var (x, y) = GamePosToMapPos(px, pz);
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        return Map[x + Width * y];
    }

    /// <summary>设值（clamp 到 [0, MaxVal]）。</summary>
    public void Set(int index, double value)
        => Map[index] = (byte)Math.Clamp(value < 0 ? 0 : value > MaxVal ? MaxVal : value, 0, 255);

    /// <summary>加影响力（linear/quadratic/constant 衰减）。逐字移植 addInfluence。</summary>
    public void AddInfluence(int cx, int cy, double maxDist, double strength, string type = "linear")
    {
        if (strength == 0) strength = maxDist;
        int x0 = Math.Max(0, (int)(cx - maxDist));
        int y0 = Math.Max(0, (int)(cy - maxDist));
        int x1 = Math.Min(Width - 1, (int)(cx + maxDist));
        int y1 = Math.Min(Height - 1, (int)(cy + maxDist));
        double maxDist2 = maxDist * maxDist;

        if (type == "linear")
        {
            double str = strength / maxDist;
            for (int y = y0; y <= y1; y++)
            {
                double dy2 = (y - cy) * (y - cy);
                int yw = y * Width;
                for (int x = x0; x <= x1; x++)
                {
                    double dx = x - cx;
                    double r2 = dx * dx + dy2;
                    if (r2 >= maxDist2) continue;
                    int w = x + yw;
                    Set(w, Map[w] + str * (maxDist - Math.Sqrt(r2)));
                }
            }
        }
        else if (type == "quadratic")
        {
            double str = strength / maxDist2;
            for (int y = y0; y <= y1; y++)
            {
                double dy2 = (y - cy) * (y - cy);
                int yw = y * Width;
                for (int x = x0; x <= x1; x++)
                {
                    double dx = x - cx;
                    double r2 = dx * dx + dy2;
                    if (r2 >= maxDist2) continue;
                    int w = x + yw;
                    Set(w, Map[w] + str * (maxDist2 - r2));
                }
            }
        }
        else // constant
        {
            for (int y = y0; y <= y1; y++)
            {
                int yw = y * Width;
                for (int x = x0; x <= x1; x++)
                {
                    double dx = x - cx;
                    double dy = y - cy;
                    if (dx * dx + dy * dy >= maxDist2) continue;
                    int w = x + yw;
                    Set(w, Map[w] + strength);
                }
            }
        }
    }

    /// <summary>乘影响力（同 addInfluence 但乘而非加）。</summary>
    public void MultiplyInfluence(int cx, int cy, double maxDist, double strength, string type = "constant")
    {
        if (strength == 0) strength = maxDist;
        int x0 = Math.Max(0, (int)(cx - maxDist));
        int y0 = Math.Max(0, (int)(cy - maxDist));
        int x1 = Math.Min(Width, (int)(cx + maxDist));
        int y1 = Math.Min(Height, (int)(cy + maxDist));
        double maxDist2 = maxDist * maxDist;

        if (type == "linear")
        {
            double str = strength / maxDist;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    double dx = x - cx, dy = y - cy, r2 = dx * dx + dy * dy;
                    if (r2 >= maxDist2) continue;
                    int w = x + y * Width;
                    Set(w, str * (maxDist - Math.Sqrt(r2)) * Map[w]);
                }
        }
        else if (type == "quadratic")
        {
            double str = strength / maxDist2;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    double dx = x - cx, dy = y - cy, r2 = dx * dx + dy * dy;
                    if (r2 >= maxDist2) continue;
                    int w = x + y * Width;
                    Set(w, str * (maxDist2 - r2) * Map[w]);
                }
        }
        else
        {
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy >= maxDist2) continue;
                    int w = x + y * Width;
                    Set(w, Map[w] * strength);
                }
        }
    }

    /// <summary>逐像素加另一个图。</summary>
    public void Add(InfoMap other)
    {
        for (int i = 0; i < Length; i++)
            Set(i, Map[i] + other.Map[i]);
    }

    /// <summary>找最佳非阻挡 tile（值最大且周围 radius 内无阻挡）。</summary>
    public (int idx, byte val) FindBestTile(int radius, InfoMap obstruction)
    {
        int bestIdx = -1;
        byte bestVal = 0;
        for (int j = 0; j < Length; j++)
        {
            if (Map[j] <= bestVal) continue;
            int i = obstruction.GetNonObstructedTile(j, radius, this);
            if (i < 0) continue;
            bestVal = Map[j];
            bestIdx = i;
        }
        return (bestIdx, bestVal);
    }

    /// <summary>在大 tile i 内找非阻挡的小 tile（用于跨分辨率：territory→passability）。</summary>
    public int GetNonObstructedTile(int i, int radius, InfoMap fineMap)
    {
        double ratio = (double)fineMap.CellSize / CellSize;
        int ix = (int)((i % Width) * ratio);
        int iy = (int)((i / Width) * ratio);
        int w = fineMap.Width;
        double r2 = radius * radius;
        (int x, int y)? lastPoint = null;
        for (int kx = ix; kx < ix + ratio; kx++)
        {
            if (kx < radius || kx >= w - radius) continue;
            for (int ky = iy; ky < iy + ratio; ky++)
            {
                if (ky < radius || ky >= w - radius) continue;
                if (lastPoint.HasValue && (kx - lastPoint.Value.x) * (kx - lastPoint.Value.x) + (ky - lastPoint.Value.y) * (ky - lastPoint.Value.y) < r2)
                    continue;
                lastPoint = fineMap.IsObstructedTile(kx, ky, radius);
                if (!lastPoint.HasValue)
                    return kx + ky * w;
            }
        }
        return -1;
    }

    private int[]? _pattern;
    private int _patternRadius = -1;

    /// <summary>tile (kx,ky) 周围 radius 内是否有阻挡。返回阻挡点或 null（未阻挡）。
    /// 用缓存的 disk pattern 加速（同原版）。</summary>
    public (int x, int y)? IsObstructedTile(int kx, int ky, int radius)
    {
        int w = Width;
        if (kx < radius || kx >= w - radius || ky < radius || ky >= w - radius || Map[kx + ky * w] == 0)
            return (kx, ky);
        if (_pattern == null || _patternRadius != radius)
        {
            _patternRadius = radius;
            _pattern = new int[radius + 1];
            int r2 = radius * radius;
            for (int i = 1; i <= radius; i++)
                _pattern[i] = (int)(Math.Sqrt(r2 - (i - 0.5) * (i - 0.5)) + 0.5);
        }
        for (int dy = 0; dy <= radius; dy++)
        {
            int dxmax = _pattern[dy];
            int xp = kx + (ky + dy) * w;
            int xm = kx + (ky - dy) * w;
            for (int dx = 0; dx <= dxmax; dx++)
            {
                if (Map[xp + dx] == 0) return (kx + dx, ky + dy);
                if (Map[xm + dx] == 0) return (kx + dx, ky - dy);
                if (Map[xp - dx] == 0) return (kx - dx, ky + dy);
                if (Map[xm - dx] == 0) return (kx - dx, ky - dy);
            }
        }
        return null;
    }
}
