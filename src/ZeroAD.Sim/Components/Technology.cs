using System;
using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

public sealed class Technology
{
    public string Name = "";
    public string DisplayName = "";
    public int WoodCost;
    public int FoodCost;
    public int StoneCost;
    public int MetalCost;
    public float ResearchTime;
    public Dictionary<string, float> Effects = new();
}

[Component("TechnologyManager", "TechnologyManager")]
public sealed class TechnologyManager : ComponentBase, IComponentMessageHandler
{
    private readonly HashSet<string> _researched = new();
    private readonly Dictionary<string, Technology> _available = new();
    private readonly Dictionary<string, float> _modifiers = new();

    public IReadOnlySet<string> Researched => _researched;
    public IReadOnlyDictionary<string, Technology> Available => _available;

    protected override void OnInit()
    {
        RegisterTech("phase_town", "Advance to Town Phase", 100, 0, 0, 0, 30,
            new() { { "pop_limit", 10 } });
        RegisterTech("phase_town_generic", "Advance to Town Phase", 100, 0, 0, 0, 30,
            new() { { "pop_limit", 10 } });
        RegisterTech("phase_city", "Advance to City Phase", 300, 300, 0, 0, 60,
            new() { { "pop_limit", 20 } });
        RegisterTech("phase_city_generic", "Advance to City Phase", 300, 300, 0, 0, 60,
            new() { { "pop_limit", 20 } });
        RegisterTech("infantry_attack", "Infantry Attack I", 50, 0, 0, 50, 20,
            new() { { "infantry_attack", 0.2f } });
        RegisterTech("infantry_armor", "Infantry Armor I", 50, 0, 50, 0, 20,
            new() { { "infantry_armor", 0.2f } });
        RegisterTech("cavalry_speed", "Cavalry Speed I", 40, 0, 0, 40, 15,
            new() { { "cavalry_speed", 0.15f } });
        RegisterTech("gather_capacity", "Gathering Basket", 50, 50, 0, 0, 20,
            new() { { "gather_capacity", 0.5f } });
        RegisterTech("gather_wood", "Wheelsaw", 40, 0, 40, 0, 20,
            new() { { "gather_wood", 0.15f } });
        RegisterTech("gather_food", "Farming", 40, 0, 40, 0, 20,
            new() { { "gather_food", 0.15f } });
    }

    private void RegisterTech(string name, string displayName,
        int wood, int food, int stone, int metal, float time,
        Dictionary<string, float> effects)
    {
        _available[name] = new Technology
        {
            Name = name,
            DisplayName = displayName,
            WoodCost = wood,
            FoodCost = food,
            StoneCost = stone,
            MetalCost = metal,
            ResearchTime = time,
            Effects = effects
        };
    }

    public bool IsResearched(string tech) => _researched.Contains(tech);

    public float GetModifier(string key)
    {
        return _modifiers.TryGetValue(key, out float v) ? v : 0f;
    }

    public void ApplyResearch(string techName)
    {
        if (!_available.TryGetValue(techName, out var tech)) return;
        if (_researched.Contains(techName)) return;

        _researched.Add(techName);
        foreach (var eff in tech.Effects)
        {
            if (_modifiers.ContainsKey(eff.Key))
                _modifiers[eff.Key] += eff.Value;
            else
                _modifiers[eff.Key] = eff.Value;
        }
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("count", _researched.Count);
        foreach (var tech in _researched)
            s.StringASCII("tech", tech);
    }

    public override void Deserialize(IDeserializer d)
    {
        int count = d.NumberI32("count");
        _researched.Clear();
        for (int i = 0; i < count; i++)
        {
            var t = d.StringASCII("tech");
            _researched.Add(t);
            if (_available.TryGetValue(t, out var tech))
                foreach (var eff in tech.Effects)
                {
                    if (_modifiers.ContainsKey(eff.Key))
                        _modifiers[eff.Key] += eff.Value;
                    else
                        _modifiers[eff.Key] = eff.Value;
                }
        }
    }

    public void HandleMessage(IMessage message) { }
}

[Component("Researcher", "Researcher")]
public sealed class ResearcherComponent : ComponentBase, IComponentMessageHandler
{
    private string? _currentTech;
    private float _progress;

    public bool IsResearching => _currentTech != null;
    public string? CurrentTech => _currentTech;
    public float Progress => _progress;

    public bool StartResearch(string techName, TechnologyManager techMgr, PlayerComponent player)
    {
        if (_currentTech != null) return false;
        if (!techMgr.Available.TryGetValue(techName, out var tech)) return false;
        if (techMgr.IsResearched(techName)) return false;

        if (player.Wood < tech.WoodCost || player.Food < tech.FoodCost) return false;

        player.Wood -= tech.WoodCost;
        player.Food -= tech.FoodCost;
        player.Stone -= tech.StoneCost;
        player.Metal -= tech.MetalCost;
        _currentTech = techName;
        _progress = 0;
        return true;
    }

    public string? Tick(float dt, TechnologyManager techMgr)
    {
        if (_currentTech == null) return null;
        if (!techMgr.Available.TryGetValue(_currentTech, out var tech)) return null;

        _progress += dt;
        if (_progress >= tech.ResearchTime)
        {
            techMgr.ApplyResearch(_currentTech);
            string done = _currentTech;
            _currentTech = null;
            _progress = 0;
            return done;
        }
        return null;
    }

    public override void Serialize(ISerializer s)
    {
        s.StringASCII("tech", _currentTech ?? "");
        s.NumberFixed("prog", Maths.Fixed.FromFloat(_progress));
    }

    public override void Deserialize(IDeserializer d)
    {
        _currentTech = d.StringASCII("tech");
        if (string.IsNullOrEmpty(_currentTech)) _currentTech = null;
        _progress = d.NumberFixed("prog").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}
