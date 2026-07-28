using System;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.AI;

/// <summary>AI 决策用的内核侧纯函数 helper(搬自原 Godot 层 SimBridge,去 Godot 依赖)。</summary>
internal static class AiUtils
{
    /// <summary>线性扫描全实体,返回最近一个命中 predicate 的(平方距离比较,对齐原 FindNearest)。
    /// 内核无空间索引缓存的等价需求——AllEntities 遍历即原 SimBridge.GetAllEntitiesSnapshot 语义。</summary>
    public static EntityId? FindNearest(ComponentManager cm, EntityId from, Func<EntityId, bool> predicate)
    {
        var fromPos = cm.QueryInterface<PositionComponent>(from);
        if (fromPos == null) return null;

        float bestDist = float.MaxValue;
        EntityId? best = null;
        foreach (var entity in cm.AllEntities)
        {
            if (entity == from) continue;
            if (!predicate(entity)) continue;
            var pos = cm.QueryInterface<PositionComponent>(entity);
            if (pos == null) continue;

            float dx = pos.Position.X.ToFloat() - fromPos.Position.X.ToFloat();
            float dz = pos.Position.Z.ToFloat() - fromPos.Position.Z.ToFloat();
            float dist = dx * dx + dz * dz;
            if (dist < bestDist) { bestDist = dist; best = entity; }
        }
        return best;
    }

    /// <summary>AI 短名(House/Barracks/...)→ 全模板名。纯字符串映射,零依赖。
    /// 搬自 SimBridge.MapBuildNameToTemplate,供内核侧 BuildManager 调用。</summary>
    public static string MapBuildNameToTemplate(string name) => name switch
    {
        "House" => "structures/spart/house",
        "Storehouse" => "structures/spart/storehouse",
        "Farmstead" => "structures/spart/farmstead",
        "Field" => "structures/spart/field",
        "Barracks" => "structures/spart/barracks",
        "Outpost" => "structures/spart/outpost",
        "Tower" => "structures/spart/defense_tower",
        "Forge" => "structures/spart/forge",
        "Market" => "structures/spart/market",
        "Temple" => "structures/spart/temple",
        "Arsenal" => "structures/spart/arsenal",
        _ => $"structures/spart/{name.ToLowerInvariant()}"
    };
}
