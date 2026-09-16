using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

public enum ResourceType { Wood, Food, Stone, Metal }

[Component("ResourceSupply", "ResourceSupply")]
public sealed class ResourceSupply : ComponentBase, IComponentMessageHandler
{
    // 默认值活在字段初始化器(对齐 OwnershipComponent 的同款修复,Components.cs:53):
    // OnInit 在 AddComponent 内、对象构造之后执行——在这里赋默认值会覆盖对象初始化器
    // 已设的值(此前 new ResourceSupply { Type=Food, Amount=800 } 挂上后全被重置为
    // Wood/100,大象/鹿的资源在面板上显示成木头 100)。
    public ResourceType Type = ResourceType.Wood;
    public string SpecificType = "tree";
    public string GenericType = "wood";
    public int Amount = 100;
    public int MaxAmount = 100;
    /// <summary>ResourceSupply/KillBeforeGather(原版):须先杀死才能采集(动物)——
    /// delete 命令的豁免条件之一(isUndeletable)。</summary>
    public bool KillBeforeGather;

    protected override void OnInit() { }

    public void SetTypeString(string typeStr)
    {
        if (string.IsNullOrWhiteSpace(typeStr)) return;
        var parts = typeStr.Split('.');
        GenericType = parts[0];
        SpecificType = parts.Length > 1 ? parts[1] : parts[0];
        Type = GenericType switch
        {
            "food" => ResourceType.Food,
            "wood" => ResourceType.Wood,
            "stone" => ResourceType.Stone,
            "metal" => ResourceType.Metal,
            _ => Type
        };
    }

    /// <summary>模板 Max=Infinity(农田)。原版 IsInfinite: !isFinite(+template.Max)。</summary>
    public bool IsInfinite => MaxAmount == int.MaxValue;

    /// <summary>原版 TakeResources + Change:有限供应扣到 0 则 DestroyEntity;
    /// 无限供应原样返回请求量、不改 Amount。</summary>
    public int Take(int requested, ComponentManager cm)
    {
        if (requested <= 0)
            return 0;
        if (IsInfinite)
            return requested;

        int taken = Math.Min(requested, Amount);
        Amount -= taken;
        if (Amount == 0)
            cm.DestroyEntity(Entity);
        return taken;
    }

    public bool IsEmpty => Amount <= 0;

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("type", (int)Type);
        s.NumberI32("amount", Amount);
        s.NumberI32("max", MaxAmount);
        s.Bool("kbg", KillBeforeGather);
    }

    public override void Deserialize(IDeserializer d)
    {
        Type = (ResourceType)d.NumberI32("type");
        Amount = d.NumberI32("amount");
        MaxAmount = d.NumberI32("max");
        KillBeforeGather = d.Bool("kbg");
    }

    public void HandleMessage(IMessage message) { }
}

[Component("ResourceGatherer", "ResourceGatherer")]
public sealed class ResourceGatherer : ComponentBase, IComponentMessageHandler
{
    public int GatherRate;
    public int CarryAmount;
    public ResourceType CarryType;
    public EntityId? TargetSupply;
    public EntityId? TargetDropsite;
    public GatherState State;
    /// <summary>模板 Rates×BaseSpeed(键 "food.grain")。空 = 测试夹具,回退 <see cref="GatherRate"/>。</summary>
    public Dictionary<string, float> Rates = new(StringComparer.Ordinal);
    /// <summary>不足 1 的采集累计(原版 1000/rate 毫秒取 1;0.5×0.1 截成 int 会永远 0)。</summary>
    public float GatherAcc;

    public enum GatherState { Idle, MovingToResource, Gathering, MovingToDropsite, Dropping }

    /// <summary>经修正值管线的采集速率(科技如 "ResourceGatherer/Rates/wood.tree" ×1.15)。
    /// 前缀匹配:按资源类型(wood/food/stone/metal)命中其全部子类型路径。
    /// 再乘 "ResourceGatherer/BaseSpeed" 路径修正(原版两路径并存:子类型比率 ×
    /// 全局 BaseSpeed——AI 难度作弊 "AI Bonus" 走后者,见 PetraConfig.Cheat)。</summary>
    public int EffectiveRate(ComponentManager cm, ResourceType type)
    {
        float modified = cm.Modifiers.ApplyPrefix(
            "ResourceGatherer/Rates/" + type.ToString().ToLowerInvariant(), GatherRate, Entity);
        modified = cm.Modifiers.Apply("ResourceGatherer/BaseSpeed", modified, Entity);
        return (int)System.MathF.Round(modified, System.MidpointRounding.AwayFromZero);
    }

    /// <summary>对具体供应的每秒采集量(原版 GetTargetGatherRate)。无该 subtype 的 Rates
    /// 则不能采(原版 StartGathering rate=0)。</summary>
    public float GatherSpeed(ComponentManager cm, ResourceSupply supply)
    {
        EnsureRates(cm);
        string key = supply.GenericType + "." + supply.SpecificType;
        float baseRate = GatherRate;
        if (Rates.Count > 0)
        {
            if (!Rates.TryGetValue(key, out baseRate))
                return 0f;
        }
        float modified = cm.Modifiers.Apply("ResourceGatherer/Rates/" + key, baseRate, Entity);
        modified = cm.Modifiers.Apply("ResourceGatherer/BaseSpeed", modified, Entity);
        return modified;
    }

    private void EnsureRates(ComponentManager cm)
    {
        if (Rates.Count > 0 || cm.Templates == null) return;
        var id = cm.QueryInterface<IdentityComponent>(Entity);
        if (string.IsNullOrEmpty(id?.TemplateName)) return;
        try
        {
            var stats = cm.Templates.ExtractStats(id.TemplateName);
            if (stats == null) return;
            foreach (var kv in stats.GatherRates)
                Rates[kv.Key] = kv.Value;
        }
        catch (Exception)
        {
            // 缺模板时保持空表,回退 GatherRate。
        }
    }

    protected override void OnInit()
    {
        GatherRate = 10;
        CarryAmount = 0;
        CarryType = ResourceType.Wood;
        State = GatherState.Idle;
        GatherAcc = 0f;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("rate", GatherRate);
        s.NumberI32("carry", CarryAmount);
        s.NumberI32("carryType", (int)CarryType);
        s.NumberI32("state", (int)State);
        s.NumberU32("target", TargetSupply?.Value ?? 0);
        s.NumberFixed("gacc", Fixed.FromFloat(GatherAcc));
    }

    public override void Deserialize(IDeserializer d)
    {
        GatherRate = d.NumberI32("rate");
        CarryAmount = d.NumberI32("carry");
        CarryType = (ResourceType)d.NumberI32("carryType");
        State = (GatherState)d.NumberI32("state");
        uint tid = d.NumberU32("target");
        TargetSupply = tid != 0 ? new EntityId(tid) : null;
        GatherAcc = SaveFormat.LoadedVersion >= 23
            ? d.NumberFixed("gacc").ToFloat() : 0f;
    }

    public void HandleMessage(IMessage message) { }
}

[Component("ResourceDropsite", "ResourceDropsite")]
public sealed class ResourceDropsite : ComponentBase, IComponentMessageHandler
{
    public bool AcceptsWood;
    public bool AcceptsFood;
    public bool AcceptsStone;
    public bool AcceptsMetal;

    protected override void OnInit()
    {
        AcceptsWood = true;
        AcceptsFood = true;
        AcceptsStone = true;
        AcceptsMetal = true;
    }

    public bool Accepts(ResourceType type) => type switch
    {
        ResourceType.Wood => AcceptsWood,
        ResourceType.Food => AcceptsFood,
        ResourceType.Stone => AcceptsStone,
        ResourceType.Metal => AcceptsMetal,
        _ => false,
    };

    public override void Serialize(ISerializer s)
    {
        s.Bool("wood", AcceptsWood);
        s.Bool("food", AcceptsFood);
        s.Bool("stone", AcceptsStone);
        s.Bool("metal", AcceptsMetal);
    }

    public override void Deserialize(IDeserializer d)
    {
        AcceptsWood = d.Bool("wood");
        AcceptsFood = d.Bool("food");
        AcceptsStone = d.Bool("stone");
        AcceptsMetal = d.Bool("metal");
    }

    public void HandleMessage(IMessage message) { }
}

/// <summary>采集目标判定:未完工地基不可采(原版 foundation| 滤镜剥掉 ResourceSupply);
/// 另拒敌方属主农田与敌方领土上的 gaia 浆果。</summary>
public static class GatherTargetFilter
{
    public static bool IsIncompleteFoundation(ComponentManager cm, EntityId entity)
    {
        var foundation = cm.QueryInterface<FoundationComponent>(entity);
        return foundation != null && !foundation.IsBuilt;
    }

    public static bool IsGatherable(ComponentManager cm, int gathererPlayer, EntityId supply)
    {
        var s = cm.QueryInterface<ResourceSupply>(supply);
        if (s == null || s.IsEmpty) return false;
        if (IsIncompleteFoundation(cm, supply)) return false;
        return !IsHostile(cm, gathererPlayer, supply);
    }

    public static bool IsHostile(ComponentManager cm, int gathererPlayer, EntityId supply)
    {
        if (gathererPlayer <= 0) return false;
        int supplyOwner = cm.QueryInterface<OwnershipComponent>(supply)?.PlayerId ?? 0;
        if (supplyOwner > 0 && cm.Players.IsEnemy(gathererPlayer, supplyOwner))
            return true;
        var pos = cm.QueryInterface<PositionComponent>(supply);
        var terr = SimSystem.Territory;
        if (pos != null && terr != null)
        {
            int tile = terr.GetOwner(pos.Position.X, pos.Position.Z);
            if (tile > 0 && cm.Players.IsEnemy(gathererPlayer, tile))
                return true;
        }
        return false;
    }
}

/// <summary>尸体标记(原版:killBeforeGather 的 gaia 动物死亡不销毁,转尸体继续供采集)。
/// 死亡清扫见此组件即跳过;UnitAI/移动/攻击的 tick 驱动见此组件即停。
/// 无字段——存在即语义。</summary>
[Component("Corpse", "Corpse")]
public sealed class CorpseComponent : ComponentBase, IComponentMessageHandler
{
    protected override void OnInit() { }
    public override void Serialize(ISerializer s) { }
    public override void Deserialize(IDeserializer d) { }
    public void HandleMessage(IMessage message) { }
}
