using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.CommonApi;

/// <summary>AI 工具函数（原版 common-api/utils.js）。距离计算 + map 索引辅助。
/// 原版用 [x,z] JS 数组；C# 用 FixedVector2D（已有 Length/Dot）。</summary>
public static class AIUtils3
{
    /// <summary>2D 欧几里得距离（原版 VectorDistance）。</summary>
    public static float Distance(FixedVector2D a, FixedVector2D b)
        => (a - b).Length().ToFloat();

    /// <summary>2D 平方距离（原版 SquareVectorDistance，避免 sqrt）。
    /// 注意:返回的是**内部定点单位的平方**(1m = 65536),只能用于排序比较;
    /// 阈值判定请用 <see cref="SquareDistanceMeters"/>。</summary>
    public static long SquareDistance(FixedVector2D a, FixedVector2D b)
    {
        var d = a - b;
        return (long)d.X.InternalValue * d.X.InternalValue + (long)d.Y.InternalValue * d.Y.InternalValue;
    }

    /// <summary>2D 平方距离(米²)——阈值判定用(threat/muster range 等)。</summary>
    public static float SquareDistanceMeters(FixedVector2D a, FixedVector2D b)
    {
        var d = a - b;
        float x = d.X.ToFloat(), y = d.Y.ToFloat();
        return x * x + y * y;
    }

    /// <summary>地图缩放的最大索引（原版 getMaxMapIndex）。gamePos → 缩放 map 的索引。</summary>
    public static int MaxMapIndex(int mapWidth, int mapHeight, int cellSize, FixedVector2D gamePos)
    {
        int x = (int)(gamePos.X.ToFloat() / cellSize);
        int z = (int)(gamePos.Y.ToFloat() / cellSize);
        if (x < 0) x = 0; else if (x >= mapWidth) x = mapWidth - 1;
        if (z < 0) z = 0; else if (z >= mapHeight) z = mapHeight - 1;
        return z * mapWidth + x;
    }
}
