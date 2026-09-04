using System.Collections.Generic;
using System.IO;
using Godot;

namespace ZeroAD.Godot;

/// <summary>模板 hotreload(开发期工具;上游 15 年 TODO —— ICmpTemplateManager.h:127,
/// 此处超越上游实现)。语义:
///  - 监视各启用 mod 的 simulation/templates(递归)与 simulation/components(顶层);
///  - 模板 XML 变化 → TemplateLoader.Invalidate(缓存+校验 memo 失效)→ 重新加载
///    (自动跑 strict schema 校验,错误进 Diag 面板)→ RebuildAllVisuals(视觉重组装);
///  - mixin/filter 图层或组件 JS 变化 → 影响面不可局部化 → InvalidateAll(组件 JS 还会
///    重建 grammar;上游连 grammar 重建都是 TODO,见 CCmpTemplateManager.cpp:60);
///  - 新 spawn 立即用新参数;**存量实体的 sim 侧字段不重灌**(上游同款 TODO——
///    EntityAssembler 是 add-only 组装;记录在 PORTING-GAPS)。
/// 门:仅调试构建 + 单机(NetRole.Standalone)——模板数据进 sim 状态,MP 热载必 OOS。
/// 去抖:编辑器保存常伴随 temp+rename 连发,300ms 静默期后统一生效。
/// FileSystemWatcher 事件在线程池线程触发 → CallDeferred 回主线程。</summary>
public sealed partial class TemplateHotReloader : Node
{
    private const double DebounceMs = 300;

    private SimBridge _sim = null!;
    /// <summary>监视根(绝对路径,'/' 规范化)→ 模板名前缀(""、"mixins/" 等)。</summary>
    private readonly List<(string Root, string RelPrefix)> _templateRoots = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private bool _watchComponents;
    private string _componentsRoot = "";
    /// <summary>待生效变更:规范路径 → 到期毫秒。</summary>
    private readonly Dictionary<string, double> _pending = new();
    private bool _reloadAllComponents;

    /// <summary>modsRoot = …/binaries/data/mods;mods = 启用表(升序,优先级无关——全看)。</summary>
    public void Install(SimBridge sim, string modsRoot, IReadOnlyList<string> mods)
    {
        _sim = sim;
        foreach (string mod in mods)
        {
            string tdir = Path.GetFullPath(Path.Combine(modsRoot, mod, "simulation", "templates"));
            if (Directory.Exists(tdir))
            {
                _templateRoots.Add((Norm(tdir) + "/", ""));
                AddWatcher(tdir, recursive: true);
            }
            string cdir = Path.GetFullPath(Path.Combine(modsRoot, mod, "simulation", "components"));
            if (Directory.Exists(cdir))
            {
                _watchComponents = true;
                _componentsRoot = Norm(cdir);
                AddWatcher(cdir, recursive: false, filter: "*.js");
            }
        }
        ZeroAD.Sim.Diag.Log("Hotload",
            $"template hot-reload armed: {_templateRoots.Count} template root(s)" +
            (_watchComponents ? " + components" : ""));
    }

    private void AddWatcher(string dir, bool recursive, string filter = "*.xml")
    {
        var w = new FileSystemWatcher(dir, filter)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        w.Changed += (_, e) => Enqueue(e.FullPath);
        w.Created += (_, e) => Enqueue(e.FullPath);
        w.Renamed += (_, e) => Enqueue(e.FullPath);
        _watchers.Add(w);
    }

    private void Enqueue(string absPath)
        => CallDeferred(nameof(EnqueueDeferred), Norm(absPath));

    private void EnqueueDeferred(string normPath)
    {
        // 组件 JS:整 grammar 重建 + 全失效(只认顶层组件文件)。
        if (_watchComponents && normPath.StartsWith(_componentsRoot + "/"))
        {
            if (normPath.IndexOf('/', _componentsRoot.Length + 1) >= 0) return;   // 子目录跳过
            _reloadAllComponents = true;
            _pending[normPath] = Time.GetTicksMsec() + DebounceMs;
            return;
        }
        foreach (var (root, _) in _templateRoots)
        {
            if (normPath.StartsWith(root))
            {
                _pending[normPath] = Time.GetTicksMsec() + DebounceMs;
                return;
            }
        }
    }

    public override void _Process(double delta)
    {
        if (_pending.Count == 0) return;
        double now = Time.GetTicksMsec();
        var due = new List<string>();
        foreach (var (path, when) in _pending)
            if (when <= now) due.Add(path);
        if (due.Count == 0) return;
        foreach (string p in due) _pending.Remove(p);
        Apply(due);
    }

    private void Apply(List<string> changedPaths)
    {
        var templates = _sim.Templates;
        if (templates == null) return;

        bool invalidateAll = _reloadAllComponents;
        var names = new List<string>();
        foreach (string path in changedPaths)
        {
            if (_reloadAllComponents) break;
            foreach (var (root, _) in _templateRoots)
            {
                if (!path.StartsWith(root)) continue;
                string rel = path[root.Length..];
                string name = rel.EndsWith(".xml") ? rel[..^4] : rel;
                // mixin/filter 图层变更 → 反向依赖不可局部化,全失效。
                if (name.StartsWith("mixins/") || name.StartsWith("special/filter/"))
                {
                    invalidateAll = true;
                    break;
                }
                names.Add(name);
            }
        }

        if (_reloadAllComponents)
        {
            // 组件 schema 变了:重建 grammar(JS 提取重跑),重挂校验,全失效。
            string modsRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(_sim.TemplatesPath!, "..", "..", ".."));
            var mods = ReadEnabledMods();
            var schema = ZeroAD.Sim.Content.Schema.TemplateSchema.Build(
                ZeroAD.Sim.Content.VfsResolver.FromConfig(modsRoot, string.Join(' ', mods)));
            foreach (var w in schema.Warnings)
                ZeroAD.Sim.Diag.Warn("Templates", "schema reload: " + w);
            templates.EnableSchemaValidation(schema, strict: true);
            _reloadAllComponents = false;
            ZeroAD.Sim.Diag.Log("Hotload", "component schema changed → grammar rebuilt");
        }

        if (invalidateAll)
        {
            templates.InvalidateAll();
            ZeroAD.Sim.Diag.Log("Hotload", "mixin/filter layer changed → all templates invalidated");
        }
        else
        {
            foreach (string name in names)
            {
                templates.Invalidate(name);
                // 立即重载+重校验(strict 错误会在 Diag 面板报)。
                try { templates.LoadTemplate(name); }
                catch (System.Exception e)
                {
                    ZeroAD.Sim.Diag.Warn("Hotload", $"{name}: reload parse failed ({e.Message})");
                }
                // 存量实体重灌(超越上游的 15 年 TODO):该模板在役实体按新模板
                // 重写组件字段(战斗/血量/驻军/生产/阻挡/视野/成本);视觉仍走
                // RebuildAllVisuals。
                int refreshed = ZeroAD.Sim.Content.TemplateStatsRefresher
                    .RefreshAllEntitiesWithTemplate(_sim.Sim, templates, name);
                ZeroAD.Sim.Diag.Log("Hotload",
                    $"template reloaded: {name} ({refreshed} component(s) re-applied)");
            }
        }

        // 视觉重组装(actor 路径/prop 可能随模板变)。存量实体 sim 字段不重灌(见类注释)。
        if (invalidateAll || names.Count > 0)
            _sim.RebuildAllVisuals();
    }

    private IReadOnlyList<string> ReadEnabledMods()
    {
        var cfg = GetNodeOrNull<UserConfig>("/root/UserConfig")?.GetEffective("mod.enabledmods");
        return cfg is { Length: > 0 } c
            ? c.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            : new[] { "mod", "public" };
    }

    private static string Norm(string p) => p.Replace('\\', '/');

    public override void _ExitTree()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}
