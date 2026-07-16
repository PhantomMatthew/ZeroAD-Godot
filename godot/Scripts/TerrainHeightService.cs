using System;

namespace ZeroAD.Godot;

public static class TerrainHeightService
{
    private static Func<float, float, float>? _sampler;

    public static void Set(Func<float, float, float> sampler) => _sampler = sampler;

    public static float Sample(float worldX, float worldZ) =>
        _sampler?.Invoke(worldX, worldZ) ?? 0f;
}
