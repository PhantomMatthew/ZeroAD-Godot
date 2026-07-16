using System;
using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

[Component("ProductionQueue", "ProductionQueue")]
public sealed class ProductionQueue : ComponentBase, IComponentMessageHandler
{
    private readonly List<ProductionItem> _queue = new();
    private float _progress;

    public IReadOnlyList<ProductionItem> Queue => _queue;
    public float Progress => _progress;
    public int QueueCount => _queue.Count;

    protected override void OnInit()
    {
        _progress = 0;
    }

    public void Enqueue(string templateName, int woodCost, int foodCost, float buildTime, int count = 1)
    {
        _queue.Add(new ProductionItem
        {
            TemplateName = templateName,
            WoodCost = woodCost,
            FoodCost = foodCost,
            BuildTime = buildTime,
            Count = count
        });
    }

    public void ResetQueue()
    {
        _queue.Clear();
        _progress = 0;
    }

    public ProductionItem? Tick(float dt)
    {
        if (_queue.Count == 0)
            return null;

        var current = _queue[0];
        _progress += dt;

        if (_progress >= current.BuildTime)
        {
            _queue.RemoveAt(0);
            _progress = 0;
            return current;
        }

        return null;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("count", _queue.Count);
        s.NumberFixed("progress", ZeroAD.Sim.Maths.Fixed.FromFloat(_progress));
        foreach (var item in _queue)
        {
            s.StringASCII("tmpl", item.TemplateName);
            s.NumberI32("wood", item.WoodCost);
            s.NumberI32("food", item.FoodCost);
            s.NumberFixed("time", ZeroAD.Sim.Maths.Fixed.FromFloat(item.BuildTime));
        }
    }

    public override void Deserialize(IDeserializer d)
    {
        int count = d.NumberI32("count");
        _progress = d.NumberFixed("progress").ToFloat();
        _queue.Clear();
        for (int i = 0; i < count; i++)
        {
            _queue.Add(new ProductionItem
            {
                TemplateName = d.StringASCII("tmpl"),
                WoodCost = d.NumberI32("wood"),
                FoodCost = d.NumberI32("food"),
                BuildTime = d.NumberFixed("time").ToFloat()
            });
        }
    }

    public void HandleMessage(IMessage message) { }
}

public sealed class ProductionItem
{
    public string TemplateName = "";
    public int WoodCost;
    public int FoodCost;
    public float BuildTime;
    public int Count = 1;
}

[Component("Player", "Player")]
public sealed class PlayerComponent : ComponentBase, IComponentMessageHandler
{
    public int Wood;
    public int Food;
    public int Stone;
    public int Metal;
    public int Population;
    public int PopulationLimit;

    protected override void OnInit()
    {
        Wood = 300;
        Food = 300;
        Stone = 200;
        Metal = 100;
        Population = 0;
        PopulationLimit = 20;
    }

    public bool CanAfford(int wood, int food)
    {
        return Wood >= wood && Food >= food;
    }

    public void Spend(int wood, int food)
    {
        Wood -= wood;
        Food -= food;
    }

    public void AddResource(ResourceType type, int amount)
    {
        switch (type)
        {
            case ResourceType.Wood: Wood += amount; break;
            case ResourceType.Food: Food += amount; break;
            case ResourceType.Stone: Stone += amount; break;
            case ResourceType.Metal: Metal += amount; break;
        }
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("wood", Wood);
        s.NumberI32("food", Food);
        s.NumberI32("stone", Stone);
        s.NumberI32("metal", Metal);
        s.NumberI32("pop", Population);
        s.NumberI32("popLimit", PopulationLimit);
    }

    public override void Deserialize(IDeserializer d)
    {
        Wood = d.NumberI32("wood");
        Food = d.NumberI32("food");
        Stone = d.NumberI32("stone");
        Metal = d.NumberI32("metal");
        Population = d.NumberI32("pop");
        PopulationLimit = d.NumberI32("popLimit");
    }

    public void HandleMessage(IMessage message) { }
}

[Component("Identity", "Identity")]
public sealed class IdentityComponent : ComponentBase, IComponentMessageHandler
{
    public string Name = "Entity";
    public string TemplateName = "";
    public bool IsUnit = true;
    public bool IsBuilding;
    public List<string> Classes = new();

    protected override void OnInit() { }

    public bool HasClass(string className) => Classes.Contains(className);

    public bool MatchesClassList(string match) =>
        Content.EntityClassHelper.EntityMatchesClassList(Classes, match);

    public override void Serialize(ISerializer s)
    {
        s.StringASCII("name", Name);
        s.StringASCII("tmpl", TemplateName);
        s.Bool("unit", IsUnit);
        s.Bool("building", IsBuilding);
        s.NumberI32("classCount", Classes.Count);
        foreach (var c in Classes)
            s.StringASCII("cls", c);
    }

    public override void Deserialize(IDeserializer d)
    {
        Name = d.StringASCII("name");
        TemplateName = d.StringASCII("tmpl");
        IsUnit = d.Bool("unit");
        IsBuilding = d.Bool("building");
        int count = d.NumberI32("classCount");
        Classes.Clear();
        for (int i = 0; i < count; i++)
            Classes.Add(d.StringASCII("cls"));
    }

    public void HandleMessage(IMessage message) { }
}
