using System;
using System.Collections.Generic;
using ZeroAD.Sim.Templates;

namespace ZeroAD.Sim.AI.CommonApi;

/// <summary>模板门面（原版 common-api/entity.js 的 Template 类，~600 行）。
/// 封装 ParamNode（TemplateLoader.Cache 的合并 XML 树），提供 slash-path Get + 90+ accessor。
/// 不预计算——按需走 GetChild，确保 AI 能读 sim 不关心的字段（如 Attack/Bonuses、Promotion）。</summary>
public sealed class AITemplate
{
    private readonly ParamNode _node;
    private readonly string _templateName;

    public AITemplate(string templateName, ParamNode node)
    { _templateName = templateName; _node = node; }

    public string TemplateName => _templateName;

    /// <summary>底层合并 XML 树(Bonuses 等子表遍历用;只读语义,勿改)。</summary>
    public ParamNode Node => _node;

    /// <summary>slash-path 取值（原版 Template.get）。"Attack/Melee/Damage/Hack" → 逐级 GetChild。
    /// "@attr" 后缀取 XML 属性（ParamNode 存储为 "@name" 子节点）。返回 null 表示路径不存在。</summary>
    public string? Get(string slashPath)
    {
        var node = _node;
        foreach (var seg in slashPath.Split('/'))
        {
            node = node.GetChild(seg);
            if (!node.IsOk) return null;
        }
        return node.Value;
    }

    public float GetFloat(string path, float def = 0f)
    {
        var v = Get(path);
        return v != null && float.TryParse(v, out var f) ? f : def;
    }

    public int GetInt(string path, int def = 0)
    {
        var v = Get(path);
        return v != null && int.TryParse(v, out var i) ? i : def;
    }

    public bool GetBool(string path) => Get(path) == "true";

    // ── Identity ──

    public string GenericName => Get("Identity/GenericName") ?? "";
    public string? SpecificName => Get("Identity/SpecificName");
    public string? Civ => Get("Identity/Civ");
    public string? Icon => Get("Identity/Icon");
    public string Classes => Get("Identity/Classes") ?? "";
    public string VisibleClasses => Get("Identity/VisibleClasses") ?? "";

    /// <summary>原版 GetIdentityClasses 语义:Classes + VisibleClasses 合并判定
    /// (VisibleClasses 承载 "Dock"/"Siege" 等功能类——此前只查 Classes,码头等漏判)。</summary>
    public bool HasClass(string className)
        => Array.IndexOf(Classes.Split(' ', StringSplitOptions.RemoveEmptyEntries), className) >= 0
        || Array.IndexOf(VisibleClasses.Split(' ', StringSplitOptions.RemoveEmptyEntries), className) >= 0;

    public bool IsUnit => HasClass("Unit") || !_node.HasChild("Building");
    public bool IsStructure => HasClass("Structure") || HasClass("Defensive") || HasClass("CivCentre");

    public string? PhaseRequirement => Get("Identity/Requirements/Techs");

    // ── Cost ──

    public int CostWood => GetInt("Cost/Resources/wood");
    public int CostFood => GetInt("Cost/Resources/food");
    public int CostStone => GetInt("Cost/Resources/stone");
    public int CostMetal => GetInt("Cost/Resources/metal");
    public float BuildTime => GetFloat("Cost/BuildTime");
    public int PopulationCost => GetInt("Cost/Population");
    public int PopulationBonus => GetInt("Cost/PopulationBonus") != 0 ? GetInt("Cost/PopulationBonus") : GetInt("Population/Bonus");

    // ── Health ──

    public int MaxHealth => GetInt("Health/Max");
    public bool Unhealable => GetBool("Health/Unhealable");

    // ── Attack（按类型 Melee/Ranged）──

    public string? AttackTypes => Get("Attack");  // 子节点名 = 攻击类型
    public IEnumerable<string> AttackTypeList
    {
        get
        {
            var attack = _node.GetChild("Attack");
            if (!attack.IsOk) yield break;
            foreach (var child in attack.Children.Keys)
                if (child != "@datatype") yield return child;
        }
    }

    public int AttackDamage(string type, string damageType)
        => GetInt($"Attack/{type}/Damage/{damageType}");

    public float AttackRange(string type) => GetFloat($"Attack/{type}/MaxRange");
    public float AttackMinRange(string type) => GetFloat($"Attack/{type}/MinRange");
    public float AttackPrepareTime(string type) => GetFloat($"Attack/{type}/PrepareTime");
    public float AttackRepeatTime(string type) => GetFloat($"Attack/{type}/RepeatTime");

    /// <summary>某攻击类型的总伤害（Hack+Pierce+Crush 求和）。</summary>
    public int AttackTotalDamage(string type)
        => AttackDamage(type, "Hack") + AttackDamage(type, "Pierce") + AttackDamage(type, "Crush");

    // ── Resistance ──

    public int ResistanceValue(string damageType) => GetInt($"Resistance/EntityStates/Damage/{damageType}");

    // ── Obstruction ──

    public float ObstructionRadius
        => GetFloat("Obstruction/Static/@radius") != 0 ? GetFloat("Obstruction/Static/@radius")
           : GetFloat("Obstruction/Unit/@radius");

    public float ObstructionWidth => GetFloat("Obstruction/Static/@width");
    public float ObstructionDepth => GetFloat("Obstruction/Static/@depth");

    // ── Builder / Trainer / Researcher ──

    public string? BuildableEntities => Get("Builder/Entities");
    public string? TrainableEntities => Get("Trainer/Entities");
    public string? ResearchableTechnologies => Get("Researcher/Technologies");
    public bool CanBuild => _node.HasChild("Builder");
    public bool CanTrain => _node.HasChild("Trainer");
    public bool CanResearch => _node.HasChild("Researcher");

    // ── Garrison ──

    public int GarrisonCapacity => GetInt("GarrisonHolder/Max");
    public string? GarrisonableClasses => Get("GarrisonHolder/List");

    // ── Resource ──

    public int ResourceMaxAmount => GetInt("ResourceSupply/Amount");
    public string? ResourceSubType => Get("ResourceSupply/Type");
    public int GatherRate => GetInt("ResourceGatherer/BaseSpeed");
    public string? ResourceDropsiteTypes => Get("ResourceDropsite/Types");

    /// <summary>采集速率表(原版 ent.resourceGatherRates()):ResourceGatherer/Rates 子键
    /// ("food.meat"/"wood.tree"…)× BaseSpeed;*.ruins 原版明确忽略。worker.startGathering
    /// 用它过滤"这工人会不会采这 subtype"(不会采的 supply 直接跳过)。</summary>
    public Dictionary<string, float> ResourceGatherRates()
    {
        var rates = new Dictionary<string, float>(StringComparer.Ordinal);
        var gatherer = _node.GetChild("ResourceGatherer");
        if (!gatherer.IsOk) return rates;
        float baseSpeed = gatherer.GetChild("BaseSpeed").IsOk
            ? gatherer.GetChild("BaseSpeed").ToFixed().ToFloat() : 1f;
        var r = gatherer.GetChild("Rates");
        if (!r.IsOk) return rates;
        foreach (var (key, node) in r.Children)
        {
            if (key.StartsWith('@')) continue;
            if (key.EndsWith(".ruins", StringComparison.Ordinal)) continue;
            rates[key] = node.ToFixed().ToFloat() * baseSpeed;
        }
        return rates;
    }

    // ── Promotion ──

    public string? PromotionEntity => Get("Promotion/Entity");
    public int PromotionRequiredXp => GetInt("Promotion/RequiredXp");

    // ── Territory / BuildRestrictions ──

    public string? BuildTerritories => Get("BuildRestrictions/Territory");
    public string? BuildCategory => Get("BuildRestrictions/Category");
    public float BuildDistance => GetFloat("BuildRestrictions/PlacementType/DistanceFromWater");

    // ── Aura ──

    public string? AuraName => Get("Auras/__string__") ?? Get("Auras");
}
