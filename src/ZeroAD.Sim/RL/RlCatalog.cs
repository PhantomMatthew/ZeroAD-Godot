using System;
using System.Collections.Generic;
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim.RL;

/// <summary>Stable string→id maps (sorted names). Same data root ⇒ same ids across processes.</summary>
public sealed class RlCatalog
{
    private readonly Dictionary<string, int> _templates = new(StringComparer.Ordinal);
    private readonly List<string> _templateById = new() { "" };
    private readonly Dictionary<string, int> _unitAi = new(StringComparer.Ordinal);
    private readonly List<string> _unitAiById = new() { "" };
    private readonly Dictionary<string, int> _techs = new(StringComparer.Ordinal);
    private readonly List<string> _techById = new() { "" };

    public int TemplateCount => _templateById.Count;
    public int UnitAiCount => _unitAiById.Count;
    public int TechCount => _techById.Count;

    public static RlCatalog FromLoader(TemplateLoader? templates, TechCatalog? techs)
    {
        var c = new RlCatalog();
        if (templates != null)
        {
            var names = new List<string>(templates.Cache.Keys);
            names.Sort(StringComparer.Ordinal);
            foreach (var n in names) c.InternTemplate(n);
        }
        if (techs != null)
        {
            var names = new List<string>(techs.Technologies.Keys);
            names.Sort(StringComparer.Ordinal);
            foreach (var n in names) c.InternTech(n);
        }
        return c;
    }

    public int InternTemplate(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        if (_templates.TryGetValue(name, out int id)) return id;
        id = _templateById.Count;
        _templates[name] = id;
        _templateById.Add(name);
        return id;
    }

    public int InternUnitAi(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        if (_unitAi.TryGetValue(name, out int id)) return id;
        id = _unitAiById.Count;
        _unitAi[name] = id;
        _unitAiById.Add(name);
        return id;
    }

    public int InternTech(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        if (_techs.TryGetValue(name, out int id)) return id;
        id = _techById.Count;
        _techs[name] = id;
        _techById.Add(name);
        return id;
    }

    public string TemplateName(int id) =>
        id > 0 && id < _templateById.Count ? _templateById[id] : "";

    public string TechName(int id) =>
        id > 0 && id < _techById.Count ? _techById[id] : "";
}
