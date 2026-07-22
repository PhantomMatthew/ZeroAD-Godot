using System.Collections.Generic;
using ZeroAD.Sim.Content;

namespace ZeroAD.Sim;

// TemplateManager — runtime template fetch/cache. Ported from
// source/simulation2/components/CCmpTemplateManager.cpp (there is no JS layer).
//
// Wraps TemplateLoader and exposes the query surface that SpawnEntity and
// ProductionQueue.EnqueueTraining need (GetStats/TemplateExists). ComponentManager.Templates
// already serves this today; this class formalises the manager so the responsibility is
// named and SimSystem can hold a typed reference. ComponentManager.Templates stays as a
// forwarding accessor for compatibility.

/// <summary>Runtime template query surface. Holds a reference to the loaded template cache.</summary>
public sealed class TemplateManager
{
    private readonly TemplateLoader _loader;

    public TemplateManager(TemplateLoader loader) => _loader = loader;

    /// <summary>Fetch parsed template stats by name, or null if the template is missing/malformed.</summary>
    public TemplateStats? GetStats(string templateName)
    {
        try { return _loader.ExtractStats(templateName); }
        catch { return null; }
    }

    public bool TemplateExists(string templateName) => GetStats(templateName) != null;

    /// <summary>Access the underlying loader (rarely needed; prefer GetStats).</summary>
    public TemplateLoader Loader => _loader;

    public IReadOnlyDictionary<string, Templates.ParamNode> Cache => _loader.Cache;
}
