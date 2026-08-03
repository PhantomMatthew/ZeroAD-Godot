using System;
using System.Linq;
using ZeroAD.Sim.Components;

namespace ZeroAD.Sim.AI.CommonApi;

/// <summary>AI 实体过滤器（原版 common-api/filters.js，25 个工厂函数）。
/// 原版返回 {func, dynamicProperties}；C# 简化为 Func&lt;AIEntity, bool&gt;
/// （lazy LINQ 模式不需要 dynamicProperties 增量索引）。</summary>
public static class Filters
{
    public static Func<AIEntity, bool> ByType(string type) => e => e.Template.TemplateName == type;
    public static Func<AIEntity, bool> ByClass(string cls) => e => e.HasClass(cls);

    /// <summary>需全部类（空格分隔 AND，对齐原版 hasClasses）。</summary>
    public static Func<AIEntity, bool> ByClasses(string clsList)
    {
        var classes = clsList.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return e => classes.All(e.HasClass);
    }

    public static Func<AIEntity, bool> ByMetadata(EntityMetadata meta, string key, object? value)
        => e => meta.GetObject(e.Id, key)?.Equals(value) ?? false;

    public static Func<AIEntity, bool> ByHasMetadata(EntityMetadata meta, string key)
        => e => meta.TryGet(e.Id, key, out _);

    public static Func<AIEntity, bool> And(Func<AIEntity, bool> a, Func<AIEntity, bool> b)
        => e => a(e) && b(e);
    public static Func<AIEntity, bool> Or(Func<AIEntity, bool> a, Func<AIEntity, bool> b)
        => e => a(e) || b(e);
    public static Func<AIEntity, bool> Not(Func<AIEntity, bool> f) => e => !f(e);

    public static Func<AIEntity, bool> ByOwner(int owner) => e => e.Owner == owner;
    public static Func<AIEntity, bool> ByNotOwner(int owner) => e => e.Owner != owner;

    public static Func<AIEntity, bool> ByOwners(params int[] owners)
        => e => owners.Contains(e.Owner);

    public static Func<AIEntity, bool> ByCanGarrison()
        => e => e.Template.GarrisonCapacity > 0;

    public static Func<AIEntity, bool> ByTrainingQueue() => e => e.HasTrainingQueue;

    /// <summary>byResearchAvailable 暂 stub（需 GameState，Tier 4 后补真实实现）。</summary>
    public static Func<AIEntity, bool> ByResearchAvailable() => _ => false;

    public static Func<AIEntity, bool> ByCanAttackClass(string aClass)
        => e => e.CanAttack;  // 简化：有 Attack 组件即可（精确的 canAttackClass 需遍历 Attack/Bonuses）

    public static Func<AIEntity, bool> ByCanAttackTarget(AIEntity target)
        => e => e.CanAttack;  // 简化（精确版需距离/类校验）

    /// <summary>isGarrisoned：position 无效（驻军实体无独立位置）。</summary>
    public static Func<AIEntity, bool> IsGarrisoned()
        => e => e.Cm.QueryInterface<PositionComponent>(e.Entity) == null;

    public static Func<AIEntity, bool> IsIdle() => e => e.IsIdle;

    public static Func<AIEntity, bool> IsFoundation() => e => e.IsFoundation;
    public static Func<AIEntity, bool> IsBuilt() => e => !e.IsFoundation;

    /// <summary>hasDefensiveFire：有 BuildingAI 组件（驻军自动射击）。暂简化为有 Attack。</summary>
    public static Func<AIEntity, bool> HasDefensiveFire() => e => e.CanAttack;

    public static Func<AIEntity, bool> IsDropsite(string resourceType)
        => e => (e.Template.ResourceDropsiteTypes ?? "").Contains(resourceType);

    public static Func<AIEntity, bool> IsTreasure()
        => e => e.HasClass("Treasure")
            && e.Template.TemplateName != "gaia/treasure/shipwreck_debris"
            && e.Template.TemplateName != "gaia/treasure/shipwreck";

    public static Func<AIEntity, bool> ByResource(string resourceType)
        => e => e.ResourceSupplyAmount > 0
            && !e.HasClass("SeaCreature")
            && (e.Template.ResourceSubType ?? "").Contains(resourceType);

    public static Func<AIEntity, bool> IsHuntable()
        => e => e.HasClass("Animal") && e.ResourceSupplyAmount > 0 && !e.HasClass("SeaCreature");

    public static Func<AIEntity, bool> IsFishable()
        => e => e.HasClass("SeaCreature") && e.ResourceSupplyAmount > 0;
}
