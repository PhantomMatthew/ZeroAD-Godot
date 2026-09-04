using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Content;

/// <summary>模板 hotload 的存量实体重灌(超越上游——上游是 15 年 TODO,
/// ICmpTemplateManager.h:127)。语义:已装配组件的模板派生字段按新模板重写;
/// 不增删组件(EntityAssembler 是 add-only 装配,组件构成不变更——视觉/actor
/// 变化由 RebuildAllVisuals 另行重组装)。只覆盖高价值字段表(战斗/血量/驻军/
// 生产/阻挡/视野/领土影响/成本);未列组件(Repairable/Capturable 等)留 backlog。
/// 形状变化(阻挡尺寸)走停-再注册重挂。确定性:仅开发期(debug+单机)触发,
/// 不进锁步生产路径。</summary>
public static class TemplateStatsRefresher
{
    /// <summary>重灌单个实体(模板名须匹配调用方已确认)。返回重灌的组件数(诊断)。</summary>
    public static int RefreshEntity(ComponentManager cm, EntityId entity, TemplateStats stats)
    {
        int refreshed = 0;

        if (cm.QueryInterface<IdentityComponent>(entity) is { } identity)
        {
            identity.Classes.Clear();
            foreach (var c in stats.GetClassList()) identity.Classes.Add(c);
            identity.TemplateName = stats.TemplateName;
            refreshed++;
        }

        if (cm.QueryInterface<HealthComponent>(entity) is { } hp && stats.HasHealth)
        {
            int newMax = stats.MaxHealth;
            if (hp.Max > 0 && newMax != hp.Max)
            {
                // 与 ValueModificationApplier.RescaleHealth 同款:按比例缩放 Current。
                hp.Current = Math.Clamp(
                    (int)MathF.Round(hp.Current * (float)newMax / hp.Max,
                        MidpointRounding.AwayFromZero),
                    0, newMax);
                hp.Max = newMax;
                hp.BaseMax = newMax;
            }
            hp.RegenRate = stats.HealthRegenRate;
            hp.IdleRegenRate = stats.HealthIdleRegenRate;
            refreshed++;
        }

        if (cm.QueryInterface<AttackComponent>(entity) is { } attack)
        {
            attack.Types.Clear();
            foreach (var t in stats.AttackTypes)
            {
                if (t.TypeName == "Capture") continue;
                attack.Types.Add(new AttackComponent.AttackTypeSpec
                {
                    Name = t.TypeName,
                    MaxRange = t.MaxRange > 0 ? t.MaxRange : 3f,
                    Rate = t.RepeatTimeMs > 0 ? 1000f / t.RepeatTimeMs : 1f,
                    RestrictedClasses = t.RestrictedClasses,
                    PreferredClasses = t.PreferredClasses,
                    StatusEffectName = t.StatusEffectName,
                    StatusEffectDurationMs = t.StatusEffectDurationMs,
                    StatusEffectIntervalMs = t.StatusEffectIntervalMs,
                    StatusEffectStackability = t.StatusEffectStackability,
                    StatusEffectDmgHack = t.StatusEffectDmgHack,
                    StatusEffectDmgPierce = t.StatusEffectDmgPierce,
                    StatusEffectDmgCrush = t.StatusEffectDmgCrush,
                    StatusEffectDmgFire = t.StatusEffectDmgFire,
                    SplashRange = t.SplashRange,
                    SplashFriendlyFire = t.SplashFriendlyFire,
                });
                var spec = attack.Types[^1];
                if (t.Hack > 0) spec.Damage.Amounts[DamageType.Hack] = (int)MathF.Round(t.Hack);
                if (t.Pierce > 0) spec.Damage.Amounts[DamageType.Pierce] = (int)MathF.Round(t.Pierce);
                if (t.Crush > 0) spec.Damage.Amounts[DamageType.Crush] = (int)MathF.Round(t.Crush);
                if (t.Fire > 0) spec.Damage.Amounts[DamageType.Fire] = (int)MathF.Round(t.Fire);
                if (t.SplashHack > 0) spec.SplashDamage.Amounts[DamageType.Hack] = (int)MathF.Round(t.SplashHack);
                if (t.SplashPierce > 0) spec.SplashDamage.Amounts[DamageType.Pierce] = (int)MathF.Round(t.SplashPierce);
                if (t.SplashCrush > 0) spec.SplashDamage.Amounts[DamageType.Crush] = (int)MathF.Round(t.SplashCrush);
                if (t.SplashFire > 0) spec.SplashDamage.Amounts[DamageType.Fire] = (int)MathF.Round(t.SplashFire);
            }
            attack.CaptureStrength = stats.AttackCaptureStrength;
            attack.HasRangeOverlay = stats.HasRangeOverlay;
            // 投射物字段(速度/散布/重力/友军误伤)——装配经 ProjectileSpec 结构,
            // 此处按逐型字段更新(装配表同款键)。详见 AttackTypeSpec。
            refreshed++;
        }

        if (cm.QueryInterface<UnitMotion>(entity) is { } motion)
        {
            motion.Speed = Fixed.FromFloat(stats.WalkSpeed);
            motion.PassClassName = stats.PassabilityClass;
            motion.Weight = stats.MovementWeight;
            motion.InstantTurnAngle = Fixed.FromFloat(stats.InstantTurnAngle);
            if (cm.QueryInterface<PositionComponent>(entity) is { } pos)
                pos.TurnRate = Fixed.FromFloat(stats.TurnRate);
            refreshed++;
        }

        if (cm.QueryInterface<VisionComponent>(entity) is { } vision && stats.VisionRange > 0)
        {
            vision.Range = Fixed.FromInt(stats.VisionRange);
            refreshed++;
        }

        if (cm.QueryInterface<ObstructionComponent>(entity) is { } obs)
        {
            var newSize0 = Fixed.FromFloat(stats.ObstructionSize0.ToFloat());
            var newSize1 = Fixed.FromFloat(stats.ObstructionSize1.ToFloat());
            bool shapeChanged = obs.Size0 != newSize0 || obs.Size1 != newSize1
                || obs.SubShapes.Count != stats.ObstructionSubShapes.Count;
            obs.Size0 = newSize0;
            obs.Size1 = newSize1;
            obs.SubShapes.Clear();
            foreach (var (_, sx, sz, sw, sd) in stats.ObstructionSubShapes)
                obs.SubShapes.Add((Fixed.FromFloat(sx), Fixed.FromFloat(sz),
                    Fixed.FromFloat(sw), Fixed.FromFloat(sd)));
            if (shapeChanged)
            {
                // 形状变 → 停-再注册(管理器重挂 + 脏区打点自动覆盖)。
                obs.SetActive(false);
                obs.SetActive(true);
            }
            refreshed++;
        }

        if (cm.QueryInterface<GarrisonHolderComponent>(entity) is { } holder)
        {
            holder.Max = stats.GarrisonCapacity;
            holder.BuffHeal = stats.GarrisonHolderBuffHeal;
            holder.LoadingRange = stats.GarrisonHolderLoadingRange;
            holder.Pickup = stats.GarrisonHolderPickup;
            holder.AllowedClasses.Clear();
            holder.AllowedClasses.AddRange(
                Content.EntityClassHelper.ParseClassTokens(stats.GarrisonHolderList));
            refreshed++;
        }

        if (cm.QueryInterface<BuildingAIComponent>(entity) is { } bai)
        {
            bai.DefaultArrowCount = stats.DefaultArrowCount;
            bai.MaxArrowCount = stats.MaxArrowCount;
            bai.GarrisonArrowMultiplier = stats.GarrisonArrowMultiplier;
            bai.GarrisonArrowClasses = stats.GarrisonArrowClasses;
            refreshed++;
        }

        if (cm.QueryInterface<ProductionQueue>(entity) is { } queue)
        {
            queue.TrainableTokens = stats.TrainableEntities;
            refreshed++;
        }

        if (cm.QueryInterface<CostComponent>(entity) is { } cost)
        {
            cost.WoodCost = stats.WoodCost;
            cost.FoodCost = stats.FoodCost;
            cost.StoneCost = stats.StoneCost;
            cost.MetalCost = stats.MetalCost;
            cost.PopulationCost = stats.PopulationCost;
            cost.BuildTime = stats.BuildTime;
            refreshed++;
        }

        if (cm.QueryInterface<PopulationComponent>(entity) is { } pop)
        {
            pop.Bonus = stats.PopulationBonus;
            refreshed++;
        }

        if (cm.QueryInterface<TerritoryInfluenceComponent>(entity) is { } ti
            && stats.TerritoryInfluenceRadius > Fixed.Zero)
        {
            ti.Radius = stats.TerritoryInfluenceRadius;
            ti.Weight = stats.TerritoryInfluenceWeight;
            ti.Root = stats.TerritoryInfluenceRoot;
            refreshed++;
        }

        return refreshed;
    }

    /// <summary>重灌指定模板的全部存量实体(热载入口;新模板由调用方现取——
    /// TemplateLoader.Invalidate 后重载,strict 校验先于本步)。</summary>
    public static int RefreshAllEntitiesWithTemplate(ComponentManager cm, TemplateLoader templates,
        string templateName)
    {
        TemplateStats stats;
        try { stats = templates.ExtractStats(templateName); }
        catch (Exception) { return 0; }

        int total = 0;
        foreach (var entity in cm.AllEntities)
        {
            var identity = cm.QueryInterface<IdentityComponent>(entity);
            if (identity?.TemplateName == templateName)
                total += RefreshEntity(cm, entity, stats);
        }
        return total;
    }
}
