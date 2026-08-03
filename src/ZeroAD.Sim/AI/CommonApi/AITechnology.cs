using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim.AI.CommonApi;

/// <summary>科技门面（原版 common-api/technology.js）。封装 TechnologyDefinition + TechCatalog，
/// 暴露 Petra 需要的 accessors。简化版：暂不实现 per-civ specificName 和 techCostMultiplier（Tier 4 补）。</summary>
public sealed class AITechnology
{
    private readonly TechnologyDefinition _def;
    private readonly TechCatalog _catalog;

    public AITechnology(TechnologyDefinition def, TechCatalog catalog)
    { _def = def; _catalog = catalog; }

    public string Name => _def.Name;
    public string GenericName => _def.GenericName;

    public int ResearchTime => (int)_def.ResearchTime;
    public int Wood => _def.Wood;
    public int Food => _def.Food;
    public int Stone => _def.Stone;
    public int Metal => _def.Metal;

    public bool AutoResearch => _def.AutoResearch;
    public string? Supersedes => _def.Supersedes;

    /// <summary>前置条件树。Petra 的 canResearch 消费。</summary>
    public IReadOnlyList<TechRequirement> Requirements => _def.Requirements;

    /// <summary>是否是 pair-definer（定义一对互斥科技）。</summary>
    public bool IsPairDefiner => _catalog.Pairs.ContainsKey(Name);

    /// <summary>若本科技是某个 pair 的成员，返回同 pair 的另一科技；否则 null。</summary>
    public string? PairedWith
    {
        get
        {
            foreach (var kvp in _catalog.Pairs)
            {
                if (kvp.Value.Contains(Name) && kvp.Value.Count == 2)
                    return kvp.Value[0] == Name ? kvp.Value[1] : kvp.Value[0];
            }
            return null;
        }
    }
}
