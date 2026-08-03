using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.AI.CommonApi;

/// <summary>每玩家游戏状态门面（原版 common-api/gamestate.js，~970 行）。
/// ComponentManager + TemplateLoader + TechCatalog 的只读视图，按玩家视角过滤。
/// 所有集合方法返回 EntityCollection（lazy IEnumerable）；phases 从科技目录的
/// supersedes/replaces 链解开（UnravelPhases）。</summary>
public sealed class GameState
{
    public readonly ComponentManager Cm;
    public readonly TemplateLoader Templates;
    public readonly TechCatalog TechCatalog;
    public readonly int PlayerId;
    public readonly EntityMetadata Metadata;
    public readonly Accessibility? Accessibility;
    public readonly AIEventBuffer Events;

    private readonly List<string> _phases;

    public GameState(ComponentManager cm, TemplateLoader templates, TechCatalog techCatalog,
        int playerId, EntityMetadata metadata, AIEventBuffer events, Accessibility? accessibility)
    {
        Cm = cm; Templates = templates; TechCatalog = techCatalog;
        PlayerId = playerId; Metadata = metadata; Events = events; Accessibility = accessibility;
        _phases = DerivePhases(techCatalog);
    }

    // ── 玩家数据 ──

    public int GetPlayerId() => PlayerId;
    public string GetPlayerCiv() => Cm.GetPlayerEntity(PlayerId)?.Civ ?? "athen";
    public ResourcesManager GetResources() => ResourcesManager.FromPlayer(Cm.GetPlayerEntity(PlayerId)!);
    public int GetPopulation() => Cm.GetPlayerEntity(PlayerId)?.PopUsed ?? 0;
    public int GetPopulationLimit() => Cm.GetPlayerEntity(PlayerId)?.PopulationLimit ?? 0;
    public int GetPopulationMax() => Cm.GetPlayerEntity(PlayerId)?.MaxPopCap ?? 300;

    // ── 模板 ──

    public AITemplate? GetTemplate(string type)
        => Templates.Cache.TryGetValue(type, out var node) ? new AITemplate(type, node) : null;

    public string ApplyCiv(string str) => str.Replace("{civ}", GetPlayerCiv());

    // ── 阶段（phase_village/town/city）──

    public int CurrentPhase()
    {
        var tm = Cm.QueryInterface<TechnologyManager>(Cm.GetPlayerEntityId(PlayerId) ?? default);
        if (tm == null) return 0;
        for (int i = _phases.Count - 1; i >= 0; i--)
            if (tm.IsResearched(_phases[i])) return i;
        return 0;
    }
    public int GetNumberOfPhases() => _phases.Count;
    public string GetPhaseName(int i) => i < _phases.Count ? _phases[i] : "";
    public IReadOnlyList<string> Phases => _phases;

    // ── 外交 ──

    public bool IsPlayerAlly(int other) => !Cm.Players.IsEnemy(PlayerId, other);
    public bool IsPlayerEnemy(int other) => Cm.Players.IsEnemy(PlayerId, other);
    public bool IsPlayerMutualAlly(int other) => IsPlayerAlly(other) && !Cm.Players.IsEnemy(other, PlayerId);
    public bool HasAllies() => Cm.Players.GetNonGaiaPlayerIds().Any(p => p != PlayerId && IsPlayerAlly(p));
    public bool HasEnemies() => Cm.Players.GetNonGaiaPlayerIds().Any(p => IsPlayerEnemy(p));
    public List<int> GetEnemies() => Cm.Players.GetNonGaiaPlayerIds().Where(IsPlayerEnemy).ToList();
    public List<int> GetAllies() => Cm.Players.GetNonGaiaPlayerIds().Where(p => p != PlayerId && IsPlayerAlly(p)).ToList();

    public bool IsEntityAlly(AIEntity e) => IsPlayerAlly(e.Owner);
    public bool IsEntityEnemy(AIEntity e) => IsPlayerEnemy(e.Owner);
    public bool IsEntityOwn(AIEntity e) => e.Owner == PlayerId;

    // ── 实体集合（lazy LINQ）──

    private IEnumerable<AIEntity> AllEntities()
        => Cm.AllEntities.Select(e => MakeEntity(e)).Where(e => e != null)!;

    private AIEntity? MakeEntity(EntityId eid)
    {
        var tmplName = Cm.QueryInterface<IdentityComponent>(eid)?.TemplateName;
        if (tmplName == null) return null;
        if (!Templates.Cache.TryGetValue(tmplName, out var node)) return null;
        return new AIEntity(Cm, eid, new AITemplate(tmplName, node));
    }

    public EntityCollection GetOwnEntities()
        => new(AllEntities().Where(e => e.Owner == PlayerId));
    public EntityCollection GetOwnStructures()
        => new(AllEntities().Where(e => e.Owner == PlayerId && e.IsStructure));
    public EntityCollection GetOwnUnits()
        => new(AllEntities().Where(e => e.Owner == PlayerId && e.IsUnit));
    public EntityCollection GetEnemyEntities()
        => new(AllEntities().Where(e => IsPlayerEnemy(e.Owner)));
    public EntityCollection GetEnemyStructures()
        => new(AllEntities().Where(e => IsPlayerEnemy(e.Owner) && e.IsStructure));
    public EntityCollection GetEnemyUnits()
        => new(AllEntities().Where(e => IsPlayerEnemy(e.Owner) && e.IsUnit));
    public EntityCollection GetAllyEntities()
        => new(AllEntities().Where(e => e.Owner != PlayerId && IsPlayerAlly(e.Owner)));
    public EntityCollection GetEntities(int player)
        => new(AllEntities().Where(e => e.Owner == player));
    public EntityCollection GetStructures()
        => new(AllEntities().Where(e => e.IsStructure));
    public EntityCollection GetOwnFoundations()
        => new(AllEntities().Where(e => e.Owner == PlayerId && e.IsFoundation));
    public EntityCollection GetOwnEntitiesByClass(string cls)
        => new(AllEntities().Where(e => e.Owner == PlayerId && e.HasClass(cls)));

    // ── 资源点 ──

    public EntityCollection GetResourceSupplies(string resourceType)
        => new(AllEntities().Where(e => Filters.ByResource(resourceType)(e)));
    public EntityCollection GetHuntableSupplies()
        => new(AllEntities().Where(e => Filters.IsHuntable()(e)));
    public EntityCollection GetFishableSupplies()
        => new(AllEntities().Where(e => Filters.IsFishable()(e)));

    public EntityCollection GetOwnDropsites(string resourceType)
        => new(AllEntities().Where(e => e.Owner == PlayerId && Filters.IsDropsite(resourceType)(e)));
    public EntityCollection GetAnyDropsites(string resourceType)
        => new(AllEntities().Where(e => Filters.IsDropsite(resourceType)(e)));

    // ── 生产/研究设施 ──

    public EntityCollection GetOwnTrainingFacilities()
        => new(AllEntities().Where(e => e.Owner == PlayerId && e.Template.CanTrain));
    public EntityCollection GetOwnResearchFacilities()
        => new(AllEntities().Where(e => e.Owner == PlayerId && e.Template.CanResearch));

    // ── 科技查询 ──

    public bool IsResearched(string tech)
    {
        var tm = Cm.QueryInterface<TechnologyManager>(Cm.GetPlayerEntityId(PlayerId) ?? default);
        return tm?.IsResearched(tech) ?? false;
    }
    public bool IsResearching(string tech)
    {
        // 检查是否有研究设施正在研究该科技（查 ProductionQueue/Researcher 状态）。
        // 简化版：遍历 owned 有 Researcher 的实体，查其队列。精确版需 ResearcherComponent 暴露队列。
        return false;  // TODO: Phase 2 补精确实现
    }

    public bool CanResearch(string tech)
    {
        if (IsResearched(tech) || IsResearching(tech)) return false;
        if (!TechCatalog.Technologies.ContainsKey(tech)) return false;
        // 简化：检查 requirements 里的 tech 前置（不检查 entity/class 前置——Phase 0 调查确认 entity 前置视为满足）
        var def = TechCatalog.Technologies[tech];
        return CheckRequirements(def.Requirements);
    }

    private bool CheckRequirements(IReadOnlyList<TechRequirement> reqs)
    {
        foreach (var req in reqs)
        {
            if (req.Tech != null && !IsResearched(req.Tech)) return false;
            if (req.Civ != null && GetPlayerCiv() != req.Civ) return false;
            // Any/All 递归
            if (req.Any != null && !req.Any.Any(r => CheckRequirements(new[] { r }))) return false;
            if (req.All != null && !req.All.All(r => CheckRequirements(new[] { r }))) return false;
        }
        return true;
    }

    // ── 计数 ──

    public int CountEntitiesByType(string type, bool maintain = false)
        => AllEntities().Count(e => e.Owner == PlayerId && e.Template.TemplateName == type);
    public int CountOwnEntitiesByRole(string role)
        => AllEntities().Count(e => e.Owner == PlayerId && Metadata.GetObject(e.Id, "role")?.ToString() == role);

    // ── 训练/建造查询 ──

    public EntityCollection FindTrainers(string template)
        => new(AllEntities().Where(e => e.Owner == PlayerId && e.Template.CanTrain
            && (e.Template.TrainableEntities ?? "").Contains(template)));
    public bool HasTrainer(string template) => FindTrainers(template).HasEntities();
    public EntityCollection FindResearchers(string templateName, bool noRequirementCheck = false)
        => new(AllEntities().Where(e => e.Owner == PlayerId && e.Template.CanResearch
            && (e.Template.ResearchableTechnologies ?? "").Contains(templateName)));
    public bool HasResearchers(string templateName, bool noRequirementCheck = false)
        => FindResearchers(templateName, noRequirementCheck).HasEntities();
    public EntityCollection FindBuilder(string template)
        => new(AllEntities().Where(e => e.Owner == PlayerId && e.Template.CanBuild
            && (e.Template.BuildableEntities ?? "").Contains(template)));

    /// <summary>查找可训练的单位（按类匹配）。简化版：遍历可训练模板，过滤类。</summary>
    public List<(string template, AITemplate def)> FindTrainableUnits(string classes, string anticlasses = "")
    {
        var result = new List<(string, AITemplate)>();
        var requiredClasses = classes.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        // 从 CC 等训练设施的 TrainableEntities 列表取候选
        foreach (var trainer in GetOwnTrainingFacilities().Values())
        {
            var trainable = trainer.Template.TrainableEntities;
            if (trainable == null) continue;
            foreach (var tmplName in trainable.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
            {
                var resolved = ApplyCiv(tmplName);
                if (!Templates.Cache.ContainsKey(resolved)) continue;
                var def = GetTemplate(resolved);
                if (def == null) continue;
                if (requiredClasses.All(def.HasClass)) result.Add((resolved, def));
            }
        }
        return result;
    }

    /// <summary>可用科技列表（简化版：所有未被研究的非 autoResearch 科技）。</summary>
    public List<string> FindAvailableTech()
    {
        var result = new List<string>();
        foreach (var kvp in TechCatalog.Technologies)
            if (CanResearch(kvp.Key)) result.Add(kvp.Key);
        return result;
    }

    // ── 实体限制 ──

    public Dictionary<string, int> GetEntityLimits()
    {
        var limits = Cm.QueryInterface<EntityLimitsComponent>(Cm.GetPlayerEntityId(PlayerId) ?? default);
        return limits?.Limits ?? new Dictionary<string, int>();
    }
    public bool IsEntityLimitReached(string category)
    {
        var limits = GetEntityLimits();
        // 简化：需要 EntityLimitsComponent 暴露当前计数——暂返回 false
        return false;
    }
    public bool IsTemplateAvailable(string templateName) => Templates.TemplateExists(templateName);
    public bool IsTemplateDisabled(string templateName) => false;  // 简化：当前无 disabled 机制

    // ── 单实体查找 ──

    public AIEntity? GetEntityById(uint id)
    {
        var eid = new EntityId(id);
        return MakeEntity(eid);
    }

    // ── 地图 ──

    public Accessibility? GetAccessibility() => Accessibility;

    // ── 阶段推导（UnravelPhases）──

    private static List<string> DerivePhases(TechCatalog catalog)
    {
        // 从科技目录找 phase_* 科技，按 supersedes/replaces 链排序。
        var phaseTechs = catalog.Technologies.Keys
            .Where(k => k.StartsWith("phase_") && !k.Contains("_") // phase_village/town/city（不含 phase_town_athen）
                || k == "phase_village" || k == "phase_town" || k == "phase_city")
            .Distinct().ToList();

        // 按 supersedes 链排序
        var ordered = new List<string>();
        string? current = phaseTechs.FirstOrDefault(p => p == "phase_village");
        if (current == null && phaseTechs.Count > 0) current = phaseTechs[0];
        var remaining = new HashSet<string>(phaseTechs);
        while (current != null && remaining.Count > 0)
        {
            ordered.Add(current);
            remaining.Remove(current);
            // 找 supersedes 当前 phase 的下一个
            current = phaseTechs.FirstOrDefault(p => remaining.Contains(p)
                && catalog.Technologies.TryGetValue(p, out var def) && def.Supersedes == current);
        }
        // 补上未链上的
        ordered.AddRange(remaining);
        return ordered.Count > 0 ? ordered : new List<string> { "phase_village", "phase_town", "phase_city" };
    }
}
