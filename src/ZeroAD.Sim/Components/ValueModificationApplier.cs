using System;

namespace ZeroAD.Sim.Components;

/// <summary>
/// 修改值变更后的实体刷新(原版 MT_ValueModification 的最小对应)。
/// 只有 Health 需要响应:Max 变化时 Current 按比例缩放(原版 Health.js 同款,
/// 防止血量科技白送差值)。其余组件查询时计算,天然新鲜,不订阅。
/// 调用点:研究完成 / autoResearch 落地后(由 SimBridge 驱动)。
/// </summary>
public static class ValueModificationApplier
{
    /// <summary>重算某玩家全部实体的 Health.Max(经修正值管线),并按比例缩放 Current。</summary>
    public static void RescaleHealth(ComponentManager cm, EntityId playerEntity)
    {
        var ownership = cm.QueryInterface<OwnershipComponent>(playerEntity);
        if (ownership == null) return;
        int playerId = ownership.PlayerId;

        foreach (var ent in cm.AllEntities)
        {
            var own = cm.QueryInterface<OwnershipComponent>(ent);
            if (own == null || own.PlayerId != playerId) continue;
            var hp = cm.QueryInterface<HealthComponent>(ent);
            if (hp == null) continue;

            int newMax = Math.Max(1, (int)MathF.Round(
                cm.Modifiers.Apply("Health/Max", hp.BaseMaxOrMax, ent), MidpointRounding.AwayFromZero));
            if (newMax == hp.Max) continue;

            hp.Current = hp.Max > 0
                ? Math.Clamp(
                    (int)MathF.Round(hp.Current * (float)newMax / hp.Max, MidpointRounding.AwayFromZero),
                    0, newMax)
                : newMax;
            hp.Max = newMax;
        }
    }
}
