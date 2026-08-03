using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim.AI.CommonApi;

/// <summary>共享状态（原版 common-api/shared.js 的 SharedScript）。
/// 在 read-only 门面模式下，原版的 snapshot 构建逻辑大部分消失——
/// 替代为：Accessibility 缓存（每地图一次）+ GameState 工厂（每 think 每 AI 玩家一个）。
///
/// 原 SharedScript 的职责映射：
///   ApplyEntitiesDelta → AIEventBuffer（Phase 0 已实现）
///   _entityMetadata → EntityMetadata（Phase 0 已实现，挂在 AIComponent）
///   _templatesModifications/_entitiesModifications → 简化：暂不做 per-tech 修正缓存
///     （AI 读模板原始值；修正由 ModifiersManager 在 sim 侧应用，AI 的 get 走 ParamNode 原文）
///   createResourceMaps → InfoMap 资源密度图（Tier 5a InfoMap 已就绪，调用方按需构建）
///   TerrainAnalysis/Accessibility → Accessibility（Tier 5c 已实现，缓存在此）</summary>
public sealed class SharedState
{
    public readonly TemplateLoader Templates;
    public readonly TechCatalog TechCatalog;
    public Accessibility? Accessibility { get; private set; }

    public SharedState(TemplateLoader templates, TechCatalog techCatalog)
    {
        Templates = templates;
        TechCatalog = techCatalog;
    }

    /// <summary>初始化 Accessibility（从 passability grid 构建地形分析 + flood-fill）。
    /// 由 SimBridge 在地图加载后调用一次（grid 重建时重调）。</summary>
    public void BuildAccessibility(PathfinderComponent pathfinder)
    {
        var grid = pathfinder.PassabilityGrid;
        if (grid == null) return;
        Accessibility = new Accessibility(
            grid,
            pathfinder.DefaultClass.Mask,
            pathfinder.ShipClass.Mask,
            pathfinder.NavcellsPerSide,
            cellSize: 1);  // navcell size（PathfindingCore.NavcellSize = 1 固定点单位）
    }

    /// <summary>为指定 AI 玩家构造 GameState（每 think 调一次）。</summary>
    public GameState CreateGameState(ComponentManager cm, int playerId,
        EntityMetadata metadata, AIEventBuffer events)
        => new(cm, Templates, TechCatalog, playerId, metadata, events, Accessibility);
}
