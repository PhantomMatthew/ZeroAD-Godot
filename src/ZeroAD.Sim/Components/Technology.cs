using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>
/// 数据驱动的科技管理器(对齐原版 TechnologyManager.js)。
/// 科技定义来自 <see cref="TechnologyLoader"/> 注入的 <see cref="TechCatalog"/>;
/// 研究落地的修改值写入 <see cref="ModifiersManager"/>(目标 = 本组件所在玩家实体)。
/// 序列化只存已研究科技名(派生的修改值由 <see cref="RebuildModifiers"/> 重放重建)。
/// </summary>
[Component("TechnologyManager", "TechnologyManager")]
public sealed class TechnologyManager : ComponentBase, IComponentMessageHandler
{
    private readonly HashSet<string> _researched = new();
    private readonly HashSet<string> _lockedByPair = new();
    private readonly Dictionary<string, string> _pairOf = new(); // tech → pair 文件名
    private TechCatalog _catalog = new(new Dictionary<string, TechnologyDefinition>(),
                                       new Dictionary<string, IReadOnlyList<string>>());
    private string _civ = "athen";

    public IReadOnlySet<string> Researched => _researched;

    /// <summary>注入数据目录(世界初始化时、任何研究判定前调用)。civ 用于 requirements {civ} 判定。</summary>
    public void Configure(TechCatalog catalog, string civ)
    {
        _catalog = catalog;
        _civ = civ;
        _pairOf.Clear();
        foreach (var (pairName, members) in catalog.Pairs)
            foreach (var m in members)
                _pairOf[m] = pairName;
    }

    public TechnologyDefinition? GetDefinition(string tech) =>
        _catalog.Technologies.TryGetValue(tech, out var def) ? def : null;

    public bool IsResearched(string tech) => _researched.Contains(tech);

    /// <summary>可否开始研究:定义存在 + 未研究 + 未被 pair 锁定 + requirements 全满足。</summary>
    public bool CanResearch(string tech)
    {
        if (!_catalog.Technologies.TryGetValue(tech, out var def)) return false;
        if (_researched.Contains(tech) || _lockedByPair.Contains(tech)) return false;
        return def.Requirements.All(ReqMet);
    }

    private bool ReqMet(TechRequirement r)
    {
        if (r.Tech != null) return _researched.Contains(r.Tech);
        if (r.Civ != null) return string.Equals(r.Civ, _civ, StringComparison.OrdinalIgnoreCase);
        if (r.Any != null) return r.Any.Any(ReqMet);
        if (r.All != null) return r.All.All(ReqMet);
        return true; // entity 等被跳过形态的恒真占位(设计文档 §5)
    }

    /// <summary>
    /// 研究落地(免费路径,不扣资源——扣费在 ResearcherComponent.StartResearch)。
    /// 标记已研究(含 replaces/supersedes/pair 伪科技),修改值写入 ModifiersManager。
    /// </summary>
    public void ApplyResearch(string techName, ComponentManager cm)
    {
        if (!_catalog.Technologies.TryGetValue(techName, out var def)) return;
        if (_researched.Contains(techName)) return;
        MarkResearched(techName, def);
        cm.Modifiers.AddModifiers(techName, def.Modifications, Entity);
    }

    private void MarkResearched(string techName, TechnologyDefinition def)
    {
        _researched.Add(techName);
        foreach (var r in def.Replaces) _researched.Add(r);
        if (def.Supersedes != null) _researched.Add(def.Supersedes);
        if (_pairOf.TryGetValue(techName, out var pairName))
        {
            _researched.Add(pairName); // 原版:任一成员研究后 pair 伪科技视为已研究
            foreach (var member in _catalog.Pairs[pairName])
                if (member != techName) _lockedByPair.Add(member);
        }
    }

    /// <summary>autoResearch 扫描(原版 UpdateAutoResearch):满足条件即免费研究。
    /// 排序遍历保证确定性;返回本次新研究的科技名。</summary>
    public IReadOnlyList<string> UpdateAutoResearch(ComponentManager cm)
    {
        var done = new List<string>();
        foreach (var name in _catalog.Technologies.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var def = _catalog.Technologies[name];
            if (!def.AutoResearch || _researched.Contains(name)) continue;
            if (def.Requirements.All(ReqMet)) { ApplyResearch(name, cm); done.Add(name); }
        }
        return done;
    }

    /// <summary>反序列化后重放(派生态重建):按科技名排序重新写入修改值。</summary>
    public void RebuildModifiers(ComponentManager cm)
    {
        foreach (var name in _researched.OrderBy(k => k, StringComparer.Ordinal))
            if (_catalog.Technologies.TryGetValue(name, out var def))
                cm.Modifiers.AddModifiers(name, def.Modifications, Entity);
    }

    public override void Serialize(ISerializer s)
    {
        // 排序遍历:状态哈希确定性(HashSet 迭代序不作为序列化序)
        var names = _researched.OrderBy(k => k, StringComparer.Ordinal).ToList();
        s.NumberI32("count", names.Count);
        foreach (var tech in names) s.StringASCII("tech", tech);
        s.StringASCII("civ", _civ);
        var locked = _lockedByPair.OrderBy(k => k, StringComparer.Ordinal).ToList();
        s.NumberI32("locked", locked.Count);
        foreach (var tech in locked) s.StringASCII("lock", tech);
    }

    public override void Deserialize(IDeserializer d)
    {
        int count = d.NumberI32("count");
        _researched.Clear();
        for (int i = 0; i < count; i++) _researched.Add(d.StringASCII("tech"));
        _civ = d.StringASCII("civ");
        int locked = d.NumberI32("locked");
        _lockedByPair.Clear();
        for (int i = 0; i < locked; i++) _lockedByPair.Add(d.StringASCII("lock"));
        // 修改值不在此重建——由调用方在 Configure 后调 RebuildModifiers。
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

    /// <summary>开始研究:校验 CanResearch(前置/pair/重复)+ 四资源扣费。</summary>
    public bool StartResearch(string techName, TechnologyManager techMgr, PlayerComponent player)
    {
        if (_currentTech != null) return false;
        if (!techMgr.CanResearch(techName)) return false;
        var tech = techMgr.GetDefinition(techName);
        if (tech == null) return false;

        if (!player.CanAfford(tech.Wood, tech.Food, tech.Stone, tech.Metal)) return false;
        player.Spend(tech.Wood, tech.Food, tech.Stone, tech.Metal);
        _currentTech = techName;
        _progress = 0;
        return true;
    }

    /// <summary>推进研究;完成时落地(ApplyResearch)并返回科技名,否则 null。</summary>
    public string? Tick(float dt, TechnologyManager techMgr, ComponentManager cm)
    {
        if (_currentTech == null) return null;
        var tech = techMgr.GetDefinition(_currentTech);
        if (tech == null) { _currentTech = null; _progress = 0; return null; }

        _progress += dt;
        if (_progress >= tech.ResearchTime)
        {
            techMgr.ApplyResearch(_currentTech, cm);
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
