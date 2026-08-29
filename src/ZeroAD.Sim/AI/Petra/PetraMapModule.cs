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

    /// <summary>地图边界/前线图(原版 mapModule.createBorderMap):
    /// - 地图外(outside)与内侧边界(border):圆形图按半径、方形图按边距
    ///   (原版 border=80m/cellSize);
    /// - 领土窄/宽前线(narrow/large frontier):我方领土与非我方/敌的界线内侧
    ///   (原版 headquarters 的 narrow/large 更新语义)。
    /// 建造选址/防御选址按 FullBorder/FullFrontier 位与过滤。</summary>
    public static InfoMap CreateBorderMap(GameState gameState, bool circularMap = true)
    {
        var territory = SimSystem.Territory;
        int w = territory?.GridWidth ?? 1;
        var map = new InfoMap(w, w, TerritoryManager.CellSize);

        // 1. 地图外 + 内侧边界(原版 createBorderMap 的 outside/border)。
        int border = (int)System.Math.Round(80f / map.CellSize);
        if (circularMap)
        {
            float ic = (w - 1) / 2f;
            float radcut = (ic - border) * (ic - border);
            for (int j = 0; j < map.Length; j++)
            {
                int dx = j % w - (int)ic;
                int dy = j / w - (int)ic;
                float radius = dx * dx + dy * dy;
                if (radius < radcut) continue;
                map.Map[j] = MapMask.Outside;
                if (radius < (ic + border) * (ic + border))
                    map.Map[j] = MapMask.Border;
            }
        }
        else
        {
            int borderCut = w - border;
            for (int j = 0; j < map.Length; j++)
            {
                int ix = j % w;
                int iy = j / w;
                if (ix < border || ix >= borderCut || iy < border || iy >= borderCut)
                {
                    map.Map[j] = MapMask.Outside;
                    if (ix >= border - 1 && ix <= borderCut + 1
                        && iy >= border - 1 && iy <= borderCut + 1)
                        map.Map[j] = MapMask.Border;
                }
            }
        }

        // 2. 领土窄/宽前线(原版 headquarters 的 frontier 更新):我方领土
        // 与非我方/敌邻接的 cell 标 narrow;宽线 = 窄线向外再扩一线(原版
        // largeFrontier 独立标记,扩张选址区分内外前线)。
        if (territory != null && territory.GridWidth > 0)
        {
            var owners = territory.OwnerGrid;
            int playerId = gameState.PlayerId;
            for (int j = 0; j < map.Length; j++)
            {
                if ((map.Map[j] & MapMask.Outside) != 0) continue;
                int ix = j % w;
                int iy = j / w;
                bool myTerritory = owners[j] == playerId;
                // 窄前线:我方领土与非我方邻接。
                bool narrow = false;
                for (int d = 0; d < 4; d++)
                {
                    int nx = ix + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    int ny = iy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || nx >= w || ny < 0 || ny >= w) continue;
                    if (owners[ny * w + nx] != playerId)
                    {
                        narrow = true;
                        break;
                    }
                }
                if (narrow && myTerritory)
                    map.Map[j] |= MapMask.NarrowFrontier;
            }
            // 宽前线:窄线外侧的非我方 cell(原版 largeFrontier 独立语义)。
            for (int j = 0; j < map.Length; j++)
            {
                if ((map.Map[j] & (MapMask.Outside | MapMask.NarrowFrontier)) != 0) continue;
                int ix = j % w;
                int iy = j / w;
                for (int d = 0; d < 4; d++)
                {
                    int nx = ix + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    int ny = iy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || nx >= w || ny < 0 || ny >= w) continue;
                    if ((map.Map[ny * w + nx] & MapMask.NarrowFrontier) != 0)
                    {
                        map.Map[j] |= MapMask.LargeFrontier;
                        break;
                    }
                }
            }
        }
        return map;
    }
}
