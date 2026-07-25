using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Components;

/// <summary>
/// 修改值变更后的实体刷新(原版 MT_ValueModification 的最小对应)。
/// Health:Max 变化时 Current 按比例缩放(原版 Health.js 同款,防止血量科技白送差值)。
/// Vision:有效视野变化时通知 RangeManager 重铺 LOS 圆(MT_VisionRangeChanged 对应)。
/// 其余组件查询时计算,天然新鲜,不订阅。
/// 调用点:研究完成 / autoResearch 落地后 + 每回合兜底(由 SimBridge 驱动)。
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

    /// <summary>有效视野 = 模板基值经 "Vision/Range" 修正值管线(原版 CCmpVision::GetRange 同款,
    /// 查询时计算)。</summary>
    public static Fixed EffectiveVisionRange(ComponentManager cm, EntityId ent, VisionComponent vis) =>
        Fixed.FromFloat(cm.Modifiers.Apply("Vision/Range", vis.Range.ToFloat(), ent));

    /// <summary>重算某玩家全部 seer 的有效视野;变化经 OnVisionRangeChanged 重铺 LOS 圆。
    /// 无变化时逐实体 no-op,不产生任何网格抖动。</summary>
    public static void ReapplyVisionRange(ComponentManager cm, EntityId playerEntity)
    {
        var ownership = cm.QueryInterface<OwnershipComponent>(playerEntity);
        if (ownership == null) return;
        int playerId = ownership.PlayerId;
        var rm = SimSystem.Range;
        if (rm == null) return;

        foreach (var ent in cm.AllEntities) // List: 插入序,跨端确定
        {
            var own = cm.QueryInterface<OwnershipComponent>(ent);
            if (own == null || own.PlayerId != playerId) continue;
            var vis = cm.QueryInterface<VisionComponent>(ent);
            if (vis == null) continue;
            rm.OnVisionRangeChanged(ent, EffectiveVisionRange(cm, ent, vis));
        }
    }

    /// <summary>全部非 gaia 玩家的视野重算(每回合兜底驱动;玩家 id 排序保证确定)。</summary>
    public static void ReapplyVisionRangeAll(ComponentManager cm)
    {
        var players = new List<int>(cm.Players.GetNonGaiaPlayerIds());
        players.Sort();
        foreach (int pid in players)
        {
            var pe = cm.GetPlayerEntityId(pid);
            if (pe.HasValue)
                ReapplyVisionRange(cm, pe.Value);
        }
    }
}
