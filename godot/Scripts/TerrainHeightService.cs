using System;

namespace ZeroAD.Godot;

public static class TerrainHeightService
{
    private static Func<float, float, float>? _sampler;

    /// <summary>地图边长(米),SetupTerrain 与采样器一起设置。视觉镜像(C++ 左手系惯例:
    /// +z=北,见 Main._worldRoot)需要它在 sim/视觉 z 间换算:visZ = WorldSize − simZ。</summary>
    public static float WorldSize { get; private set; }

    public static void Set(Func<float, float, float> sampler, float worldSize = 0f)
    {
        _sampler = sampler;
        WorldSize = worldSize;
    }

    /// <summary>sim z → 视觉 z(镜像根 Position.z=WorldSize + Scale.z=−1 的逆变换)。</summary>
    public static float MirrorZ(float simZ) => WorldSize - simZ;

    public static float Sample(float worldX, float worldZ) =>
        _sampler?.Invoke(worldX, worldZ) ?? 0f;
}
