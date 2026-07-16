using System;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

public enum ResourceType { Wood, Food, Stone, Metal }

[Component("ResourceSupply", "ResourceSupply")]
public sealed class ResourceSupply : ComponentBase, IComponentMessageHandler
{
    public ResourceType Type;
    public string SpecificType = "";
    public string GenericType = "";
    public int Amount;
    public int MaxAmount;

    protected override void OnInit()
    {
        Type = ResourceType.Wood;
        SpecificType = "tree";
        GenericType = "wood";
        Amount = 100;
        MaxAmount = 100;
    }

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
    }

    public override void Deserialize(IDeserializer d)
    {
        Type = (ResourceType)d.NumberI32("type");
        Amount = d.NumberI32("amount");
        MaxAmount = d.NumberI32("max");
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
