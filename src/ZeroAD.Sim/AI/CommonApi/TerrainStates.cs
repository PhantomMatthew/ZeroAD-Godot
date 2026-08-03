namespace ZeroAD.Sim.AI.CommonApi;

/// <summary>TerrainAnalysis 用的 8-bit 地形分类常量。移植自 common-api/terrain-states.js（17 行）。</summary>
public static class TerrainStates
{
    public const byte Impassable = 0;
    public const byte DeepWater = 200;
    public const byte ShallowWater = 201;
    public const byte Land = 255;
}
