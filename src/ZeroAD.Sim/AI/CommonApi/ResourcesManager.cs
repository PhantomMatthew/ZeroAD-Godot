using System.Collections.Generic;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.CommonApi;

/// <summary>资源包（原版 common-api/resources.js）。可变 { wood, food, stone, metal } + canAfford/add/subtract。
/// 从 PlayerComponent 的活字段读（门面，非快照）。</summary>
public sealed class ResourcesManager
{
    public int Wood, Food, Stone, Metal;

    public ResourcesManager() { }
    public ResourcesManager(int wood, int food, int stone, int metal)
        => (Wood, Food, Stone, Metal) = (wood, food, stone, metal);

    /// <summary>从 PlayerComponent 的活字段构造（live 门面，每 think 重建）。</summary>
    public static ResourcesManager FromPlayer(PlayerComponent p)
        => new(p.Wood, p.Food, p.Stone, p.Metal);

    public int this[ResourceType t] => t switch
    {
        ResourceType.Wood => Wood, ResourceType.Food => Food,
        ResourceType.Stone => Stone, ResourceType.Metal => Metal,
        _ => 0,
    };

    public bool CanAfford(int wood, int food, int stone, int metal)
        => Wood >= wood && Food >= food && Stone >= stone && Metal >= metal;

    public bool CanAfford(ResourcesManager cost)
        => Wood >= cost.Wood && Food >= cost.Food && Stone >= cost.Stone && Metal >= cost.Metal;

    public void Subtract(int wood, int food, int stone, int metal)
        => (Wood, Food, Stone, Metal) = (Wood - wood, Food - food, Stone - stone, Metal - metal);

    public void Add(ResourcesManager other)
        => (Wood, Food, Stone, Metal) = (Wood + other.Wood, Food + other.Food, Stone + other.Stone, Metal + other.Metal);

    public int Sum => Wood + Food + Stone + Metal;
}
