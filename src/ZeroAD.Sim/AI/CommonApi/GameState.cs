using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Content;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Net;

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

    /// <summary>AI 命令通道（AIComponent 构造后注入）。null = 测试/无网环境,
    /// SubmitCommand 静默 no-op——与原版"AI 命令不落盘"语义一致的可测替代。</summary>
    public NetTurnManager? Net { get; set; }

    /// <summary>经 AI 专用本地通道下发命令（与玩家命令同路径同延迟;永不进网络 outbox）。</summary>
    public void SubmitCommand(NetCommand cmd) => Net?.SubmitAiCommand(cmd);

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

    /// <summary>科技名归一(原版 phase 文件分 civ 特制/通用两类的处理):
    /// phase_town_{civ}/phase_city_{civ} 按文明展开后,目录无此特制文件时回落
    /// *_generic(gaul 等无特制 phase 的文明 → phase_town_generic)。
    /// 非 phase 名原样返回。</summary>
    public string ResolveTechName(string name)
    {
        if (TechCatalog.Technologies.ContainsKey(name)) return name;
        const string townPrefix = "phase_town_";
        const string cityPrefix = "phase_city_";
        if (name.StartsWith(townPrefix, System.StringComparison.Ordinal)
            && TechCatalog.Technologies.ContainsKey("phase_town_generic"))
            return "phase_town_generic";
        if (name.StartsWith(cityPrefix, System.StringComparison.Ordinal)
            && TechCatalog.Technologies.ContainsKey("phase_city_generic"))
            return "phase_city_generic";
        return name;
    }

    /// <summary>可研究列表逐 token 归一(FindResearchers 匹配用:模板 phase_town_{civ}
    /// 展开为 phase_town_gaul 后,gaul 的研究者匹配归一后的 phase_town_generic)。</summary>
    private string ResolveTechList(string tokens)
    {
        if (tokens.Length == 0) return tokens;
        var parts = tokens.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
            parts[i] = ResolveTechName(parts[i]);
        return string.Join(' ', parts);
    }

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

    // 每实例缓存:此前 AllEntities()/集合方法都是 lazy LINQ——每次 .Values()/.Length
    // 迭代都把 7400 个实体重新包一遍 AIEntity(Identity 查询 + 模板查找 + 两次分配);
    // DefenseManager.IsDangerous 每个敌人 foreach 一遍 → 全图重建 ×敌人数,实测
    // 25 秒/think。物化一次并复用,把成本降回每 tick 一次。
    // 活性由 ComponentManager.EntitySetVersion 保证:世界(实体集)一变就整体失效
    // 重建——测试长期持有 GameState 也不会拿到过期集合。
    private int _cacheVersion = -1;
    private List<AIEntity>? _allCache;

    /// <summary>实体集一变就整体失效重建。所有缓存访问的入口检查——必须跑在
    /// 任何缓存读取之前(否则命中的旧集合绕过版本比对)。</summary>
    private void EnsureFresh()
    {
        if (_cacheVersion == Cm.EntitySetVersion) return;
        _cacheVersion = Cm.EntitySetVersion;
        _allCache = null;
        _byId.Clear();
        _collectionCache.Clear();
    }

    private List<AIEntity> AllEntitiesList()
    {
        EnsureFresh();
        return _allCache ??= Cm.AllEntities.Select(e => MakeEntity(e)).Where(e => e != null)!.ToList()!;
    }

    private IEnumerable<AIEntity> AllEntities() => AllEntitiesList();

    // 常用集合的物化缓存:DefenseManager 的 IsDangerous 每敌人 foreach
    // GetOwnStructures().Values() —— lazy 时每调用都是全图 7400 扫描 ×敌人数。
    // 改为按谓词 key 物化一次并复用(GameState 按玩家按 tick 新建,缓存天然正确)。
    private readonly Dictionary<string, List<AIEntity>> _collectionCache = new();
    private EntityCollection Col(string key, Func<AIEntity, bool> pred)
    {
        EnsureFresh();
        return new(_collectionCache.TryGetValue(key, out var l)
            ? l
            : _collectionCache[key] = AllEntitiesList().Where(pred).ToList());
    }

    private AIEntity? MakeEntity(EntityId eid)
    {
        var tmplName = Cm.QueryInterface<IdentityComponent>(eid)?.TemplateName;
        if (tmplName == null) return null;
        if (!Templates.Cache.TryGetValue(tmplName, out var node)) return null;
        return new AIEntity(Cm, eid, new AITemplate(tmplName, node));
    }

    public EntityCollection GetOwnEntities()
        => Col("own", e => e.Owner == PlayerId);
    public EntityCollection GetOwnStructures()
        => Col("ownStructures", e => e.Owner == PlayerId && e.IsStructure);
    public EntityCollection GetOwnUnits()
        => Col("ownUnits", e => e.Owner == PlayerId && e.IsUnit);
    public EntityCollection GetEnemyEntities()
        => Col("enemy", e => IsPlayerEnemy(e.Owner));
    public EntityCollection GetEnemyStructures()
        => Col("enemyStructures", e => IsPlayerEnemy(e.Owner) && e.IsStructure);
    public EntityCollection GetEnemyUnits()
        => Col("enemyUnits", e => IsPlayerEnemy(e.Owner) && e.IsUnit);
    public EntityCollection GetAllyEntities()
        => Col("ally", e => e.Owner != PlayerId && IsPlayerAlly(e.Owner));
    public EntityCollection GetEntities(int player)
        => Col("p" + player, e => e.Owner == player);
    public EntityCollection GetStructures()
        => Col("structures", e => e.IsStructure);
    public EntityCollection GetOwnFoundations()
        => Col("ownFoundations", e => e.Owner == PlayerId && e.IsFoundation);
    public EntityCollection GetOwnEntitiesByClass(string cls)
        => Col("ownClass:" + cls, e => e.Owner == PlayerId && e.HasClass(cls));

    // ── 资源点 ──

    public EntityCollection GetResourceSupplies(string resourceType)
        => Col("supplies:" + resourceType, Filters.ByResource(resourceType));
    public EntityCollection GetHuntableSupplies()
        => Col("huntable", Filters.IsHuntable());
    public EntityCollection GetFishableSupplies()
        => Col("fishable", Filters.IsFishable());
    /// <summary>可拾取宝藏(原版 Filters.IsTreasure;worker.startGathering 的 gatherTreasure 首选)。</summary>
    public EntityCollection GetTreasureSupplies()
        => Col("treasures", Filters.IsTreasure());

    /// <summary>游戏已进行秒数(原版 ai.elapsedTime;由锁步回合 × 0.1s 推导,Net 缺失时为 0)。</summary>
    public double ElapsedTime => (Net?.CurrentTurn ?? 0) * 0.1;

    /// <summary>原版 getPassabilityClassMask:类名 → 通行位掩码(pathfinder.xml 注册表;
    /// 未知名 → default)。AI 选址/寻路按名取类(building-land/ship 等)。</summary>
    public Pathfinding.PassClass GetPassabilityClassMask(string name) =>
        SimSystem.Pathfinder?.GetPassabilityClassMask(name) ?? new Pathfinding.PassClass(1);

    public EntityCollection GetOwnDropsites(string resourceType)
        => Col("ownDropsites:" + resourceType, e => e.Owner == PlayerId && Filters.IsDropsite(resourceType)(e));
    public EntityCollection GetAnyDropsites(string resourceType)
        => Col("dropsites:" + resourceType, Filters.IsDropsite(resourceType));

    // ── 生产/研究设施 ──

    public EntityCollection GetOwnTrainingFacilities()
        => Col("ownTraining", e => e.Owner == PlayerId && e.Template.CanTrain);
    public EntityCollection GetOwnResearchFacilities()
        => Col("ownResearch", e => e.Owner == PlayerId && e.Template.CanResearch);

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

    /// <summary>可训练/可建造 tokens 的占位替换({native}/{civ} → 属主文明)。
    /// 模板原样存占位符,裸 Contains 永远匹配不到文明具体模板名;
    /// 实体已被 Owner==PlayerId 过滤,属主文明即玩家文明。</summary>
    /// <summary>{native}/{civ} 令牌展开(原版 template 名的 civ 替换惯例)。</summary>
    public string ResolveTokens(string? tokens)
        => (tokens ?? "").Replace("{native}", GetPlayerCiv()).Replace("{civ}", GetPlayerCiv());

    public EntityCollection FindTrainers(string template)
        => new(AllEntities().Where(e => e.Owner == PlayerId && e.Template.CanTrain
            && ResolveTokens(e.Template.TrainableEntities).Contains(template)));
    public bool HasTrainer(string template) => FindTrainers(template).HasEntities();
    public EntityCollection FindResearchers(string templateName, bool noRequirementCheck = false)
        => new(AllEntities().Where(e => e.Owner == PlayerId && e.Template.CanResearch
            && ResolveTechList(ResolveTokens(e.Template.ResearchableTechnologies)).Contains(templateName)));
    public bool HasResearchers(string templateName, bool noRequirementCheck = false)
        => FindResearchers(templateName, noRequirementCheck).HasEntities();
    public EntityCollection FindBuilder(string template)
        => new(AllEntities().Where(e => e.Owner == PlayerId && e.Template.CanBuild
            && ResolveTokens(e.Template.BuildableEntities).Contains(template)));

    /// <summary>查找可训练的单位（按类匹配）。简化版：遍历可训练模板，过滤类。
    /// classes/anticlasses 空格分隔;classes 全需(AND),anticlasses 任一排除
    /// (原版 findTrainableUnits 的 excluded 语义——Hero/SiegeTower 隐式排除用)。</summary>
    public List<(string template, AITemplate def)> FindTrainableUnits(string classes, string anticlasses = "")
    {
        var result = new List<(string template, AITemplate def)>();
        var requiredClasses = classes.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var excludedClasses = anticlasses.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        // 从 CC 等训练设施的 TrainableEntities 列表取候选
        foreach (var trainer in GetOwnTrainingFacilities().Values())
        {
            var trainable = trainer.Template.TrainableEntities;
            if (trainable == null) continue;
            // {native} = 建筑自身文明(Trainer.js:503-558;驻扎在占领的异族 CC 上时
            // native≠owner civ)。{civ} = 玩家文明(ApplyCiv)。
            string nativeCiv = trainer.Template.Get("Identity/Civ") ?? "";
            foreach (var tmplName in trainable.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
            {
                var resolved = ApplyCiv(tmplName).Replace("{native}", nativeCiv);
                if (!Templates.Cache.ContainsKey(resolved)) continue;
                var def = GetTemplate(resolved);
                if (def == null) continue;
                if (!requiredClasses.All(def.HasClass)) continue;
                if (excludedClasses.Any(def.HasClass)) continue;
                if (result.Any(r => r.template == resolved)) continue;   // 多训练设施去重
                result.Add((resolved, def));
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

    private readonly Dictionary<uint, AIEntity?> _byId = new();

    public AIEntity? GetEntityById(uint id)
    {
        EnsureFresh();
        if (_byId.TryGetValue(id, out var cached)) return cached;
        var e = MakeEntity(new EntityId(id));
        _byId[id] = e;
        return e;
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
