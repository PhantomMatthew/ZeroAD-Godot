using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>Petra 地图辅助函数（原版 petra/mapModule.js，217 行）。
/// createObstructionMap: 从 passability + territory 构建建造选址障碍图（过滤敌对领地+不可通行）。
/// createTerritoryMap: 封装领土图为 InfoMap（加 getOwner/isBlinking）。
/// createBorderMap: 地图边界 + 领土前线图。
///
/// 简化版：算法框架移植，依赖 GameState 的完整 passability/territory 接口的部分标 TODO。
/// createObstructionMap 用的命名通行类掩码已就绪:PassabilityGrid 是逐 navcell 16 位
/// 位掩码,PathfinderConfig 9 类注册表(default/ship/building-land/building-shore/
/// *-terrain-only 等,pathfinder.xml 数据驱动)经 GetPassabilityClassMask 按名查询。</summary>
public static class PetraMapModule
{
    private const int TerritoryPlayerMask = 0x1F;
    private const int TerritoryBlinkingMask = 0x40;

    /// <summary>创建建造选址障碍图(原版 petra/mapModule.js createObstructionMap 全量移植):
    /// 领土图逐 cell 按模板的 buildTerritory(own/ally/neutral/enemy)过滤(未连通区
    /// 按 buildNeutral 门控),其覆盖的 navcell 再过通行类位图(land → building-land,
    /// shore → building-shore)+ 陆区 accessIndex 匹配;合格 navcell 标 255。
    /// 尾段:模板带 BuildRestrictions/Distance 时,FromClass 同类建筑周围
    /// MinDistance 内负影响(原版 addInfluence -255)。</summary>
    public static InfoMap CreateObstructionMap(GameState gameState, ushort? accessIndex, AITemplate? template)
    {
        var pf = SimSystem.Pathfinder;
        var territory = SimSystem.Territory;
        var grid = pf?.PassabilityGrid;
        if (pf == null || territory == null || grid == null)
            return new InfoMap(1, 1, 1);   // 网格未建(测试环境)→ 空图

        // 模板侧默认(原版:createObstructionMap 的缺省块)。
        string placementType = "land";
        bool buildOwn = true, buildAlly = true, buildNeutral = true, buildEnemy = false;
        if (template != null)
        {
            placementType = template.Get("BuildRestrictions/PlacementType") ?? "land";
            string territories = template.BuildTerritories ?? "own ally neutral";
            buildOwn = territories.Contains("own");
            buildAlly = territories.Contains("ally");
            buildNeutral = territories.Contains("neutral");
            buildEnemy = territories.Contains("enemy");
        }

        bool shore = placementType == "shore";
        var obstructionMask = pf.GetPassabilityClassMask(shore ? "building-shore" : "building-land");
        int navW = grid.W;
        int ratio = TerritoryManager.CellSize;   // territory cell 4m ÷ navcell 1m = 4
        var tiles = new byte[navW * navW];

        int tW = territory.GridWidth;
        for (int k = 0; k < tW * tW; k++)
        {
            int tilePlayer = territory.GetOwnerByIndex(k);
            bool isConnected = territory.IsConnectedByIndex(k);
            if (tilePlayer == gameState.PlayerId)
            {
                if (!buildOwn || !buildNeutral && !isConnected) continue;
            }
            else if (tilePlayer != 0 && gameState.IsPlayerMutualAlly(tilePlayer))
            {
                if (!buildAlly || !buildNeutral && !isConnected) continue;
            }
            else if (tilePlayer == 0)
            {
                if (!buildNeutral) continue;
            }
            else if (!buildEnemy) continue;

            int tx = ratio * (k % tW);
            int tz = ratio * (k / tW);
            for (int ix = 0; ix < ratio; ix++)
                for (int iz = 0; iz < ratio; iz++)
                {
                    int i = tx + ix + (tz + iz) * navW;
                    // 陆类过滤陆区(岸类不过滤——dock 建在岸线上,原版同款)。
                    if (!shore && accessIndex != null && gameState.Accessibility != null)
                    {
                        ushort region = gameState.Accessibility.GetAccessValue(
                            (i % navW) + 0.5f, (i / navW) + 0.5f);
                        if (region != accessIndex.Value) continue;
                    }
                    if (Pathfinding.PathfindingCore.IsPassable(grid.Get(i % navW, i / navW), obstructionMask))
                        tiles[i] = 255;
                }
        }

        var map = new InfoMap(navW, navW, 1, tiles);
        map.SetMaxVal(255);

        // buildDistance:同类(FromClass)建筑 MinDistance 内禁建(原版尾段)。
        if (template != null)
        {
            float minDist = template.GetFloat("BuildRestrictions/Distance/MinDistance");
            string? fromClass = template.Get("BuildRestrictions/Distance/FromClass");
            if (minDist > 0 && fromClass != null)
            {
                float oRadius = ObstructionRadiusMin(template);
                minDist -= oRadius;
                if (minDist > 0)
                {
                    int cellDist = 1 + (int)(minDist / 1f);   // navcell 1m
                    foreach (var ent in gameState.GetOwnStructures()
                        .Filter(e => e.HasClass(fromClass)).Values())
                    {
                        if (ent.Position2D == default) continue;
                        map.AddInfluence((int)ent.Position2D.X.ToFloat(),
                            (int)ent.Position2D.Y.ToFloat(), cellDist, -255, "constant");
                    }
                }
            }
        }
        return map;
    }

    /// <summary>模板障碍半径最小值(原版 obstructionRadius().min:Square 取半宽深小值,
    /// Circle 取半径;无 → 0)。</summary>
    private static float ObstructionRadiusMin(AITemplate template)
    {
        float w = template.GetFloat("Obstruction/Static/@width");
        float d = template.GetFloat("Obstruction/Static/@depth");
        if (w > 0 || d > 0) return System.Math.Min(w, d) / 2f;
        return template.GetFloat("Obstruction/Circle/@radius");
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
