using System;
using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

[Component("GarrisonHolder", "GarrisonHolder")]
public sealed class GarrisonHolder : ComponentBase, IComponentMessageHandler
{
    private readonly List<EntityId> _garrisoned = new();
    public int Capacity { get; private set; }
    public IReadOnlyList<EntityId> Garrisoned => _garrisoned;

    protected override void OnInit() => Capacity = 10;

    public bool CanGarrison => _garrisoned.Count < Capacity;

    public bool Garrison(EntityId entity)
    {
        if (!CanGarrison) return false;
        _garrisoned.Add(entity);
        return true;
    }

    public List<EntityId> UngarrisonAll(float x, float z, ComponentManager cm)
    {
        var ejected = new List<EntityId>(_garrisoned);
        foreach (var eid in _garrisoned)
        {
            var pos = cm.QueryInterface<PositionComponent>(eid);
            if (pos != null)
                pos.Position = new Maths.FixedVector3D(
                    Maths.Fixed.FromFloat(x + (ejected.Count - _garrisoned.IndexOf(eid)) * 3),
                    Maths.Fixed.Zero,
                    Maths.Fixed.FromFloat(z + 5));
        }
        _garrisoned.Clear();
        return ejected;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("cap", Capacity);
        s.NumberI32("count", _garrisoned.Count);
        foreach (var eid in _garrisoned)
            s.NumberU32("e", eid.Value);
    }

    public override void Deserialize(IDeserializer d)
    {
        Capacity = d.NumberI32("cap");
        int count = d.NumberI32("count");
        _garrisoned.Clear();
        for (int i = 0; i < count; i++)
            _garrisoned.Add(new EntityId(d.NumberU32("e")));
    }

    public void HandleMessage(IMessage message) { }
}

[Component("Market", "Market")]
public sealed class MarketComponent : ComponentBase, IComponentMessageHandler
{
    public int WoodBuyPrice = 100;
    public int FoodBuyPrice = 100;
    public int WoodSellPrice = 70;
    public int FoodSellPrice = 70;

    protected override void OnInit() { }

    public void BarterWood(PlayerComponent player, bool sell)
    {
        if (sell)
        {
            if (player.Wood < 100) return;
            player.Wood -= 100;
            player.Metal += WoodSellPrice;
        }
        else
        {
            if (player.Metal < WoodBuyPrice) return;
            player.Metal -= WoodBuyPrice;
            player.Wood += 100;
        }
    }

    public void BarterFood(PlayerComponent player, bool sell)
    {
        if (sell)
        {
            if (player.Food < 100) return;
            player.Food -= 100;
            player.Metal += FoodSellPrice;
        }
        else
        {
            if (player.Metal < FoodBuyPrice) return;
            player.Metal -= FoodBuyPrice;
            player.Food += 100;
        }
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("wbuy", WoodBuyPrice);
        s.NumberI32("fbuy", FoodBuyPrice);
        s.NumberI32("wsell", WoodSellPrice);
        s.NumberI32("fsell", FoodSellPrice);
    }

    public override void Deserialize(IDeserializer d)
    {
        WoodBuyPrice = d.NumberI32("wbuy");
        FoodBuyPrice = d.NumberI32("fbuy");
        WoodSellPrice = d.NumberI32("wsell");
        FoodSellPrice = d.NumberI32("fsell");
    }

    public void HandleMessage(IMessage message) { }
}

[Component("RallyPoint", "RallyPoint")]
public sealed class RallyPointComponent : ComponentBase, IComponentMessageHandler
{
    public Maths.FixedVector2D Position;

    protected override void OnInit()
    {
        Position = new Maths.FixedVector2D(Maths.Fixed.Zero, Maths.Fixed.Zero);
    }

    public void Set(Maths.FixedVector2D pos) => Position = pos;

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("x", Position.X);
        s.NumberFixed("z", Position.Y);
    }

    public override void Deserialize(IDeserializer d)
    {
        Position = new Maths.FixedVector2D(d.NumberFixed("x"), d.NumberFixed("z"));
    }

    public void HandleMessage(IMessage message) { }
}

[Component("Vision", "Vision")]
public sealed class VisionComponent : ComponentBase, IComponentMessageHandler
{
    public float Range;

    protected override void OnInit() => Range = 20.0f;

    public override void Serialize(ISerializer s) =>
        s.NumberFixed("range", Maths.Fixed.FromFloat(Range));

    public override void Deserialize(IDeserializer d) =>
        Range = d.NumberFixed("range").ToFloat();

    public void HandleMessage(IMessage message) { }
}

[Component("Promotion", "Promotion")]
public sealed class PromotionComponent : ComponentBase, IComponentMessageHandler
{
    public int XP;
    public int Level = 1;
    public int XpNext = 20;

    public void AddXP(int amount)
    {
        XP += amount;
        while (XP >= XpNext)
        {
            XP -= XpNext;
            Level++;
            XpNext = (int)(XpNext * 1.5f);
        }
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("xp", XP);
        s.NumberI32("lvl", Level);
        s.NumberI32("next", XpNext);
    }

    public override void Deserialize(IDeserializer d)
    {
        XP = d.NumberI32("xp");
        Level = d.NumberI32("lvl");
        XpNext = d.NumberI32("next");
    }

    public void HandleMessage(IMessage message) { }
}
