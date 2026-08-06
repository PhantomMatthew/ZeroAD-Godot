using System;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>被击杀时给击杀者的战利品(原版 Loot.js 移植)。
/// 模板 Loot/{xp,food,wood,stone,metal};读取时过修正值管线("Loot/xp"、"Loot/{res}",
/// 原版 ApplyValueModificationsToEntity + Math.floor)。</summary>
[Component("Loot", "Loot")]
public sealed class LootComponent : ComponentBase, IComponentMessageHandler
{
    // 默认值活在字段初始化器(OnInit 不覆写,同 HealthComponent 修复模式)。
    public int Xp;
    public int Food;
    public int Wood;
    public int Stone;
    public int Metal;

    protected override void OnInit() { }

    public int GetXp(ComponentManager cm) =>
        (int)MathF.Floor(cm.Modifiers.Apply("Loot/xp", Xp, Entity));

    /// <summary>单资源战利品(修正值管线 "Loot/{code}" + floor,对齐原版 GetResources)。</summary>
    public int GetResource(ComponentManager cm, ResourceType type)
    {
        int baseValue = type switch
        {
            ResourceType.Food => Food,
            ResourceType.Wood => Wood,
            ResourceType.Stone => Stone,
            ResourceType.Metal => Metal,
            _ => 0
        };
        if (baseValue == 0) return 0;
        return (int)MathF.Floor(cm.Modifiers.Apply(
            "Loot/" + type.ToString().ToLowerInvariant(), baseValue, Entity));
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("xp", Xp);
        s.NumberI32("food", Food);
        s.NumberI32("wood", Wood);
        s.NumberI32("stone", Stone);
        s.NumberI32("metal", Metal);
    }

    public override void Deserialize(IDeserializer d)
    {
        Xp = d.NumberI32("xp");
        Food = d.NumberI32("food");
        Wood = d.NumberI32("wood");
        Stone = d.NumberI32("stone");
        Metal = d.NumberI32("metal");
    }

    public void HandleMessage(IMessage message) { }
}

/// <summary>击杀者的战利品收集(原版 Looter.js 移植;template_unit 默认件)。
/// 击杀结算点(DelayedDamage.ApplyDirect 的 EntityKilled)调用 Collect:
/// 目标 Loot 模板资源 + 目标身上携带的资源(采集者携带 + 商人货物),
/// 过 "Looter/Resource/{type}" 修正值管线后计入击杀者属主,并更新统计。</summary>
[Component("Looter", "Looter")]
public sealed class LooterComponent : ComponentBase, IComponentMessageHandler
{
    protected override void OnInit() { }

    public void Collect(ComponentManager cm, EntityId target)
    {
        var loot = cm.QueryInterface<LootComponent>(target);
        if (loot == null) return;

        // 目标携带的资源(原版 calculateCarriedResources:采集者携带 + 商人货物)。
        int carryFood = 0, carryWood = 0, carryStone = 0, carryMetal = 0;
        var gatherer = cm.QueryInterface<ResourceGatherer>(target);
        if (gatherer != null && gatherer.CarryAmount > 0)
        {
            switch (gatherer.CarryType)
            {
                case ResourceType.Food: carryFood += gatherer.CarryAmount; break;
                case ResourceType.Wood: carryWood += gatherer.CarryAmount; break;
                case ResourceType.Stone: carryStone += gatherer.CarryAmount; break;
                case ResourceType.Metal: carryMetal += gatherer.CarryAmount; break;
            }
        }
        var trader = cm.QueryInterface<TraderComponent>(target);
        if (trader != null && trader.HasGain && trader.TraderGain > 0)
        {
            switch (trader.GoodsType)
            {
                case ResourceType.Food: carryFood += trader.TraderGain; break;
                case ResourceType.Wood: carryWood += trader.TraderGain; break;
                case ResourceType.Stone: carryStone += trader.TraderGain; break;
                case ResourceType.Metal: carryMetal += trader.TraderGain; break;
            }
        }

        int food = Modified(cm, ResourceType.Food, loot.GetResource(cm, ResourceType.Food)) + carryFood;
        int wood = Modified(cm, ResourceType.Wood, loot.GetResource(cm, ResourceType.Wood)) + carryWood;
        int stone = Modified(cm, ResourceType.Stone, loot.GetResource(cm, ResourceType.Stone)) + carryStone;
        int metal = Modified(cm, ResourceType.Metal, loot.GetResource(cm, ResourceType.Metal)) + carryMetal;
        if (food == 0 && wood == 0 && stone == 0 && metal == 0) return;

        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        var player = own != null ? cm.GetPlayerEntity(own.PlayerId) : null;
        if (player == null) return;
        if (food > 0) player.AddResource(ResourceType.Food, food);
        if (wood > 0) player.AddResource(ResourceType.Wood, wood);
        if (stone > 0) player.AddResource(ResourceType.Stone, stone);
        if (metal > 0) player.AddResource(ResourceType.Metal, metal);

        // 统计(原版 IncreaseLootCollectedCounter):记录总回收量。
        var stats = cm.QueryInterface<StatisticsTrackerComponent>(player.Entity);
        if (stats != null)
            stats.LootCollected += food + wood + stone + metal;
    }

    private int Modified(ComponentManager cm, ResourceType type, int baseValue)
    {
        if (baseValue == 0) return 0;
        return (int)MathF.Floor(cm.Modifiers.Apply(
            "Looter/Resource/" + type.ToString().ToLowerInvariant(), baseValue, Entity));
    }

    public override void Serialize(ISerializer s) { }
    public override void Deserialize(IDeserializer d) { }
    public void HandleMessage(IMessage message) { }
}
