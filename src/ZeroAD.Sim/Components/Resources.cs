using System;
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

    public int Take(int requested)
    {
        int taken = Math.Min(requested, Amount);
        Amount -= taken;
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

    public enum GatherState { Idle, MovingToResource, Gathering, MovingToDropsite, Dropping }

    /// <summary>经修正值管线的采集速率(科技如 "ResourceGatherer/Rates/wood.tree" ×1.15)。
    /// 前缀匹配:按资源类型(wood/food/stone/metal)命中其全部子类型路径。</summary>
    public int EffectiveRate(ComponentManager cm, ResourceType type)
    {
        float modified = cm.Modifiers.ApplyPrefix(
            "ResourceGatherer/Rates/" + type.ToString().ToLowerInvariant(), GatherRate, Entity);
        return (int)System.MathF.Round(modified, System.MidpointRounding.AwayFromZero);
    }

    protected override void OnInit()
    {
        GatherRate = 10;
        CarryAmount = 0;
        CarryType = ResourceType.Wood;
        State = GatherState.Idle;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("rate", GatherRate);
        s.NumberI32("carry", CarryAmount);
        s.NumberI32("carryType", (int)CarryType);
        s.NumberI32("state", (int)State);
        s.NumberU32("target", TargetSupply?.Value ?? 0);
    }

    public override void Deserialize(IDeserializer d)
    {
        GatherRate = d.NumberI32("rate");
        CarryAmount = d.NumberI32("carry");
        CarryType = (ResourceType)d.NumberI32("carryType");
        State = (GatherState)d.NumberI32("state");
        uint tid = d.NumberU32("target");
        TargetSupply = tid != 0 ? new EntityId(tid) : null;
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
