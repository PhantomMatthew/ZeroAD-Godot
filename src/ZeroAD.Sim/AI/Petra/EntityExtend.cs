using System;
using System.Collections.Generic;
using ZeroAD.Sim.AI.CommonApi;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.Petra;

/// <summary>Petra 实体辅助函数（原版 petra/entityExtend.js，446 行）。
/// 自由函数模块（非 prototype monkey-patch），操作 AIEntity/GameState。</summary>
public static class EntityExtend
{
    /// <summary>是否 siege 单位（原版 isSiegeUnit）。</summary>
    public static bool IsSiegeUnit(AIEntity ent)
        => ent.HasClass("Siege") || (ent.HasClass("Elephant") && ent.HasClass("Melee"));

    /// <summary>是否快速移动单位。</summary>
    public static bool IsFastMoving(AIEntity ent) => ent.HasClass("FastMoving");

    /// <summary>战斗力评估（DPS×health 因子）。逐字移植 getMaxStrength。
    /// 简化版：用 AttackTotalDamage + MaxHitpoints 算近似值（原版还加权 damageType/range/repeat/resistance）。</summary>
    public static double GetMaxStrength(AIEntity ent, Dictionary<string, double> damageTypeImportance)
    {
        double strength = 0;
        // 遍历攻击类型，加权和
        foreach (var type in ent.Template.AttackTypeList)
        {
            if (type == "Slaughter") continue;
            int dmg = ent.Template.AttackTotalDamage(type);
            // 加权（简化：平均 damageTypeImportance 权重）
            double avgWeight = 0;
            int count = 0;
            foreach (var w in damageTypeImportance.Values) { avgWeight += w; count++; }
            if (count > 0) strength += (avgWeight / count) * dmg;

            // 射程加成
            float range = ent.Template.AttackRange(type);
            if (range > 0) strength += range * 0.0125;

            // repeat/prepare 时间修正
            float repeat = ent.Template.AttackRepeatTime(type);
            if (repeat > 0) strength += repeat / 100000.0;
            float prepare = ent.Template.AttackPrepareTime(type);
            if (prepare > 0) strength -= prepare / 100000.0;
        }

        // 防御力加成（简化：Hack+Pierce+Crush 求和 × 平均权重）
        int res = ent.Template.ResistanceValue("Hack") + ent.Template.ResistanceValue("Pierce")
                  + ent.Template.ResistanceValue("Crush");
        double avgW2 = 0; int c2 = 0;
        foreach (var w in damageTypeImportance.Values) { avgW2 += w; c2++; }
        if (c2 > 0) strength += (avgW2 / c2) * res / 3.0;

        return strength * ent.MaxHitpoints / 100.0;
    }

    /// <summary>陆地可达性（缓存到 metadata）。单位实时查；建筑缓存。
    /// 逐字移植 getLandAccess（简化：不做 dock/shore 特殊处理）。</summary>
    public static ushort GetLandAccess(GameState gameState, AIEntity ent)
    {
        if (ent.IsUnit)
        {
            var pos = ent.Position2D;
            return gameState.Accessibility?.GetAccessValue(pos.X.ToFloat(), pos.Y.ToFloat(), onWater: false) ?? 0;
        }
        // 建筑：缓存到 metadata
        if (gameState.Metadata.TryGet(ent.Id, "access", out var cached) && cached is ushort u)
            return u;
        ushort access = gameState.Accessibility?.GetAccessValue(
            ent.Position2D.X.ToFloat(), ent.Position2D.Y.ToFloat(), onWater: false) ?? (ushort)0;
        gameState.Metadata.Set(ent.Id, "access", access);
        return access;
    }

    /// <summary>海上可达性（缓存到 metadata）。</summary>
    public static ushort GetSeaAccess(GameState gameState, AIEntity ent)
    {
        if (gameState.Metadata.TryGet(ent.Id, "sea", out var cached) && cached is ushort u)
            return u;
        ushort sea = gameState.Accessibility?.GetAccessValue(
            ent.Position2D.X.ToFloat(), ent.Position2D.Y.ToFloat(), onWater: true) ?? (ushort)0;
        gameState.Metadata.Set(ent.Id, "sea", sea);
        return sea;
    }

    /// <summary>占领 vs 摧毁决策（原版 allowCapture 简化版）。
    /// 简化：目标是盟友建筑的占领点 → 尝试夺回；否则简单返回 false（优先摧毁）。
    /// 原版计算 antiCapture vs captureStrength，此处简化为 false（Phase 3 补精确版）。</summary>
    public static bool AllowCapture(GameState gameState, AIEntity ent, AIEntity target)
    {
        // 简化：不占领（优先摧毁）。完整版需 capturableTargets 缓存 + captureStrength 计算。
        return false;
    }

    /// <summary>取最佳基地（从 metadata 读 base 字段）。</summary>
    public static int? GetBestBase(AIEntity ent, GameState gameState)
    {
        if (gameState.Metadata.TryGet(ent.Id, "base", out var b) && b != null)
            return Convert.ToInt32(b);
        return null;
    }

    /// <summary>攻击加成倍数（getAttackBonus 简化版）。</summary>
    public static double GetAttackBonus(AIEntity ent, AIEntity target, string attackType)
    {
        // 简化：返回 1.0（原版查 Attack/Bonuses/<b>/Classes 匹配目标类）
        return 1.0;
    }

    /// <summary>取最近的非阻挡位置（用于建筑放置 fallback）。</summary>
    public static FixedVector2D FindNearestPassable(GameState gameState, FixedVector2D pos)
    {
        // 简化：返回 pos（精确版用 Accessibility 的 spiral search）
        return pos;
    }
}
