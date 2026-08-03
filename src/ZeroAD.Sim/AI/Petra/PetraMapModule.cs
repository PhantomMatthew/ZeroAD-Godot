using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>Petra 地图辅助函数（原版 petra/mapModule.js，217 行）。
/// createObstructionMap: 从 passability + territory 构建建造选址障碍图（过滤敌对领地+不可通行）。
/// createTerritoryMap: 封装领土图为 InfoMap（加 getOwner/isBlinking）。
/// createBorderMap: 地图边界 + 领土前线图。
///
/// 简化版：算法框架移植，依赖 GameState 的完整 passability/territory 接口的部分标 TODO。
/// createObstructionMap 完整版需 GameState.GetPassabilityClassMask（building-land/building-shore），
/// 当前 C# PassabilityGrid 只有 Default/Ship 两个 class——需扩展命名 class mask（Phase 2 后续）。</summary>
public static class PetraMapModule
{
    private const int TerritoryPlayerMask = 0x1F;
    private const int TerritoryBlinkingMask = 0x40;

    /// <summary>创建建造选址障碍图（原版 createObstructionMap）。
    /// 从 passability grid + territory grid 构建：在可建造领地内且可通行的 cell 标 255。
    /// 简化版：用 passability grid 直接构建（暂不做 territory 过滤和 building-land mask）。</summary>
    public static InfoMap CreateObstructionMap(GameState gameState, ushort? accessIndex, AITemplate? template)
    {
        // 完整版需要：
        //   1. gameState.GetPassabilityClassMask("building-land") / ("building-shore")
        //   2. territoryMap.data 逐 cell 过滤（own/ally/neutral/enemy 领地）
        //   3. buildDistance/MinDistance 排除
        // 当前简化：用 Default class mask 的 passability grid 直接构建。
        var grid = gameState.Cm;  // TODO: 经 PathfinderComponent 取 grid
        // 简化版：返回空 InfoMap（Phase 2 后续接入完整 passability/territory）
        return new InfoMap(1, 1, 1);
    }

    /// <summary>封装领土图为 InfoMap（原版 createTerritoryMap）。
    /// getOwner/getOwnerIndex/isBlinking 从 byte 值解码（player mask + blinking bit）。</summary>
    public static InfoMap CreateTerritoryMap(GameState gameState)
    {
        // 完整版需要 TerritoryManager.OwnerGrid 的尺寸 + cellSize。
        // 简化版：返回空 InfoMap（Phase 2 后续接入）。
        return new InfoMap(1, 1, 4);
    }

    public static int GetOwnerFromTerritory(byte cellValue) => cellValue & TerritoryPlayerMask;
    public static bool IsBlinking(byte cellValue) => (cellValue & TerritoryBlinkingMask) != 0;
}
