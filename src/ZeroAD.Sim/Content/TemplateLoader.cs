using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Templates;

namespace ZeroAD.Sim.Content
{
    public sealed class TemplateLoader
    {
        private readonly string _templatesRoot;
        private readonly Dictionary<string, ParamNode> _cache = new();

        public TemplateLoader(string templatesRoot)
        {
            _templatesRoot = templatesRoot;
        }

        public ParamNode LoadTemplate(string templateName)
        {
            if (_cache.TryGetValue(templateName, out var cached))
                return cached;

            var resolved = ParamNode.ResolveTemplate(templateName, LoadXmlDocument);
            _cache[templateName] = resolved;
            return resolved;
        }

        public Dictionary<string, ParamNode> LoadAllTemplates()
        {
            if (!Directory.Exists(_templatesRoot)) return _cache;

            var files = Directory.GetFiles(_templatesRoot, "*.xml", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                string relPath = Path.GetRelativePath(_templatesRoot, file)
                    .Replace('\\', '/')
                    .Replace(".xml", "");
                try
                {
                    LoadTemplate(relPath);
                }
                catch { }
            }
            return _cache;
        }

        private XDocument LoadXmlDocument(string templateName)
        {
            string relPath = templateName.Replace('/', Path.DirectorySeparatorChar) + ".xml";
            string[] searchDirs = { "special" + Path.DirectorySeparatorChar + "filter", "mixins", "" };

            foreach (string dir in searchDirs)
            {
                string fullPath = string.IsNullOrEmpty(dir)
                    ? Path.Combine(_templatesRoot, relPath)
                    : Path.Combine(_templatesRoot, dir, relPath);
                if (File.Exists(fullPath))
                    return XDocument.Load(fullPath);
            }

            return XDocument.Parse("<Entity/>");
        }

        public TemplateStats ExtractStats(string templateName)
        {
            var node = LoadTemplate(templateName);
            return ExtractStatsFromNode(node);
        }

        public static TemplateStats ExtractStatsFromNode(ParamNode node)
        {
            var stats = new TemplateStats();

            // <Auras> 是 <Entity> 直接子(datatype="tokens"),不在 <Identity> 内。
            // 空格分隔的 aura 文件名,供 AuraComponent 装配时读取。
            var auras = node.GetChild("Auras");
            if (auras.IsOk)
                stats.Auras = auras.ToString();

            var identity = node.GetChild("Identity");
            if (identity.IsOk)
            {
                stats.Name = identity.GetChild("GenericName").IsOk
                    ? identity.GetChild("GenericName").ToString() : template_Name(node);
                var classes = identity.GetChild("Classes");
                if (classes.IsOk)
                    stats.Classes = classes.ToString();
                var visibleClasses = identity.GetChild("VisibleClasses");
                if (visibleClasses.IsOk)
                    stats.VisibleClasses = visibleClasses.ToString();
                var genericName = identity.GetChild("GenericName");
                if (genericName.IsOk)
                    stats.GenericName = genericName.ToString();
                var category = identity.GetChild("Category");
                if (category.IsOk)
                    stats.Category = category.ToString();
            }

            var health = node.GetChild("Health");
            if (health.IsOk)
            {
                stats.MaxHealth = health.GetChild("Max").IsOk
                    ? health.GetChild("Max").ToInt() : 100;
            }

            var cost = node.GetChild("Cost");
            if (cost.IsOk)
            {
                var resources = cost.GetChild("Resources");
                if (resources.IsOk)
                {
                    stats.WoodCost = resources.GetChild("wood").ToInt();
                    stats.FoodCost = resources.GetChild("food").ToInt();
                    stats.StoneCost = resources.GetChild("stone").ToInt();
                    stats.MetalCost = resources.GetChild("metal").ToInt();
                }
                stats.PopulationCost = cost.GetChild("Population").ToInt();
                // PopulationBonus lives on buildings (e.g. House +10) under <Cost> in 0 A.D. data.
                var popBonus = cost.GetChild("PopulationBonus");
                if (popBonus.IsOk)
                    stats.PopulationBonus = popBonus.ToInt();
                stats.BuildTime = cost.GetChild("BuildTime").IsOk
                    ? cost.GetChild("BuildTime").ToFixed().ToFloat() : 5.0f;
            }

            var trainingRestrictions = node.GetChild("TrainingRestrictions");
            if (trainingRestrictions.IsOk)
            {
                var category = trainingRestrictions.GetChild("Category");
                if (category.IsOk)
                    stats.TrainingCategory = category.ToString();
            }

            var attack = node.GetChild("Attack");
            if (attack.IsOk)
            {
                // 远程节点存在性决定修正值路径前缀(Attack/Ranged vs Attack/Melee)
                stats.AttackIsRanged = attack.GetChild("Ranged").IsOk;
                var melee = attack.GetChild("Melee");
                if (melee.IsOk)
                {
                    var dmg = melee.GetChild("Damage");
                    if (dmg.IsOk)
                    {
                        // Read all three physical damage types (any subset may be present).
                        stats.AttackHack = dmg.GetChild("Hack").IsOk ? dmg.GetChild("Hack").ToInt() : 0;
                        stats.AttackPierce = dmg.GetChild("Pierce").IsOk ? dmg.GetChild("Pierce").ToInt() : 0;
                        stats.AttackCrush = dmg.GetChild("Crush").IsOk ? dmg.GetChild("Crush").ToInt() : 0;
                    }
                    var capture = melee.GetChild("Capture");
                    if (capture.IsOk)
                        stats.AttackCapture = capture.GetChild("Value").IsOk ? capture.GetChild("Value").ToInt() : 0;
                    stats.AttackRange = 3.0f;
                    stats.AttackRate = melee.GetChild("RepeatTime").IsOk
                        ? 1000f / melee.GetChild("RepeatTime").ToInt() : 1.0f;
                }
            }

            // Resistance: per-type damage reduction. Read from Resistance/Entity/Damage/{type}
            // (the Foundation form is ignored in P0 — we collapse to the Entity form).
            var resistance = node.GetChild("Resistance");
            if (resistance.IsOk)
            {
                var entityForm = resistance.GetChild("Entity");
                if (entityForm.IsOk)
                {
                    var rDmg = entityForm.GetChild("Damage");
                    if (rDmg.IsOk)
                    {
                        stats.ResistanceHack = rDmg.GetChild("Hack").IsOk ? rDmg.GetChild("Hack").ToInt() : 0;
                        stats.ResistancePierce = rDmg.GetChild("Pierce").IsOk ? rDmg.GetChild("Pierce").ToInt() : 0;
                        stats.ResistanceCrush = rDmg.GetChild("Crush").IsOk ? rDmg.GetChild("Crush").ToInt() : 0;
                    }
                    var rCap = entityForm.GetChild("Capture");
                    if (rCap.IsOk)
                        stats.ResistanceCapture = rCap.ToInt();
                }
            }

            var resourceSupply = node.GetChild("ResourceSupply");
            if (resourceSupply.IsOk)
            {
                var amount = resourceSupply.GetChild("Amount");
                if (amount.IsOk) stats.ResourceAmount = amount.ToInt();
                var max = resourceSupply.GetChild("Max");
                if (max.IsOk && stats.ResourceAmount == 0)
                    stats.ResourceAmount = max.ToInt();
                var type = resourceSupply.GetChild("Type");
                if (type.IsOk)
                {
                    stats.ResourceTypeString = type.ToString();
                    var tStr = stats.ResourceTypeString;
                    stats.ResourceType = tStr.Contains("wood", StringComparison.OrdinalIgnoreCase)
                        ? ResourceType.Wood
                        : tStr.Contains("food", StringComparison.OrdinalIgnoreCase)
                            ? ResourceType.Food
                            : tStr.Contains("stone", StringComparison.OrdinalIgnoreCase)
                                ? ResourceType.Stone
                                : ResourceType.Metal;
                }
            }

            var unitMotion = node.GetChild("UnitMotion");
            if (unitMotion.IsOk)
            {
                var walkSpeed = unitMotion.GetChild("WalkSpeed");
                if (walkSpeed.IsOk)
                    stats.WalkSpeed = walkSpeed.ToFixed().ToFloat();
            }

            var vision = node.GetChild("Vision");
            if (vision.IsOk)
            {
                var range = vision.GetChild("Range");
                if (range.IsOk)
                    stats.VisionRange = range.ToInt();
            }

            // Fog-of-war: <Fogging/> (structures/gaia) enables mirage spawning;
            // <Visibility><RetainInFog> keeps the entity standing in explored fog.
            if (node.GetChild("Fogging").IsOk)
                stats.HasFogging = true;
            var visibility = node.GetChild("Visibility");
            if (visibility.IsOk)
            {
                var retain = visibility.GetChild("RetainInFog");
                if (retain.IsOk)
                    stats.RetainInFog = retain.ToBool();
            }

            var dropsite = node.GetChild("ResourceDropsite");
            if (dropsite.IsOk)
                stats.IsDropsite = true;

            var production = node.GetChild("ProductionQueue");
            if (production.IsOk)
                stats.CanTrain = true;

            var builder = node.GetChild("Builder");
            if (builder.IsOk)
                stats.CanBuild = true;

            var gatherer = node.GetChild("ResourceGatherer");
            if (gatherer.IsOk)
                stats.CanGather = true;

            var garrisonHolder = node.GetChild("GarrisonHolder");
            if (garrisonHolder.IsOk)
            {
                stats.GarrisonCapacity = garrisonHolder.GetChild("Max").IsOk
                    ? garrisonHolder.GetChild("Max").ToInt() : 10;
            }

            // Footprint: physical extent used for spawn-point search (FootprintComponent) and click
            // hit-testing. Either <Square width depth/> or <Circle radius/>.
            var footprint = node.GetChild("Footprint");
            if (footprint.IsOk)
            {
                var square = footprint.GetChild("Square");
                if (square.IsOk)
                {
                    stats.FootprintShape = "square";
                    stats.FootprintSize0 = square.GetChild("width").IsOk ? square.GetChild("width").ToFixed() : stats.FootprintSize0;
                    stats.FootprintSize1 = square.GetChild("depth").IsOk ? square.GetChild("depth").ToFixed() : stats.FootprintSize1;
                }
                var circle = footprint.GetChild("Circle");
                if (circle.IsOk)
                {
                    stats.FootprintShape = "circle";
                    stats.FootprintSize0 = circle.GetChild("radius").IsOk ? circle.GetChild("radius").ToFixed() : stats.FootprintSize0;
                    stats.FootprintSize1 = stats.FootprintSize0;
                }
                var height = footprint.GetChild("Height");
                if (height.IsOk) stats.FootprintHeight = height.ToFixed();
            }

            // Obstruction: what this entity blocks. Either <Static width depth/> (building) or <Unit/> (mobile).
            // Drives ObstructionComponent shape + flags at spawn time.
            var obstruction = node.GetChild("Obstruction");
            if (obstruction.IsOk)
            {
                var staticEl = obstruction.GetChild("Static");
                if (staticEl.IsOk)
                {
                    stats.ObstructionShape = "static";
                    stats.ObstructionSize0 = staticEl.GetChild("width").IsOk ? staticEl.GetChild("width").ToFixed() : stats.ObstructionSize0;
                    stats.ObstructionSize1 = staticEl.GetChild("depth").IsOk ? staticEl.GetChild("depth").ToFixed() : stats.ObstructionSize1;
                }
                else if (obstruction.GetChild("Unit").IsOk)
                {
                    stats.ObstructionShape = "unit";
                }
                // Flags default to all-block; the XML can override but we keep the common case simple.
                stats.ObstructionActive = obstruction.GetChild("Active").IsOk
                    ? obstruction.GetChild("Active").ToBool()
                    : true;
            }

            // BuildRestrictions/Territory:空格分隔的 own/ally/neutral/enemy tokens(模板继承链
            // 已合并 —— template_structure 默认 "own",CC/殖民地 "own neutral")。无节点 = 空串
            // (非建筑,无领土限制)。
            var buildRestrictions = node.GetChild("BuildRestrictions");
            if (buildRestrictions.IsOk)
            {
                var territory = buildRestrictions.GetChild("Territory");
                if (territory.IsOk) stats.BuildRestrictionsTerritory = territory.ToString();
            }

            // TerritoryInfluence:无节点 → Radius=0,不装配 TerritoryInfluenceComponent。
            var territoryInfluence = node.GetChild("TerritoryInfluence");
            if (territoryInfluence.IsOk)
            {
                var radius = territoryInfluence.GetChild("Radius");
                if (radius.IsOk) stats.TerritoryInfluenceRadius = radius.ToFixed();
                var weight = territoryInfluence.GetChild("Weight");
                if (weight.IsOk) stats.TerritoryInfluenceWeight = weight.ToInt();
                var root = territoryInfluence.GetChild("Root");
                if (root.IsOk) stats.TerritoryInfluenceRoot = root.ToBool();
            }

            return stats;
        }

        private static string template_Name(ParamNode node)
        {
            return node.GetChild("Identity").GetChild("SpecificName").IsOk
                ? node.GetChild("Identity").GetChild("SpecificName").ToString()
                : "Entity";
        }

        public IReadOnlyDictionary<string, ParamNode> Cache => _cache;
    }

    public sealed class TemplateStats
    {
        public string Name = "Entity";
        public string GenericName = "";
        public string Category = "";
        public string Classes = "";
        public string VisibleClasses = "";
        /// <summary>&lt;Auras datatype="tokens"&gt;:空格分隔的 aura 文件名(units/heroes/iber_hero_indibil)。
        /// 是 &lt;Entity&gt; 直接子节点(非 &lt;Identity&gt;)。空 = 无光环。</summary>
        public string Auras = "";
        public string TemplateName = "";
        public int MaxHealth = 100;
        public int WoodCost;
        public int FoodCost;
        public int StoneCost;
        public int MetalCost;
        public int PopulationCost;
        /// <summary>Pop capacity granted by buildings (House +10). Read from &lt;Cost&gt;&lt;PopulationBonus&gt;.</summary>
        public int PopulationBonus;
        public float BuildTime = 5f;

        // Multi-type attack damage (Hack/Pierce/Crush). Read from Attack/Melee/Damage/{type}.
        // AttackDamage (below) is derived as the total for back-compat with callers that just
        // check "does this unit deal damage" (AttackDamage > 0).
        public int AttackHack;
        public int AttackPierce;
        public int AttackCrush;
        public int AttackCapture;
        /// <summary>模板含 Attack/Ranged 节点 = 远程单位(修正值路径前缀用)。</summary>
        public bool AttackIsRanged;

        /// <summary>Total physical attack damage (Hack+Pierce+Crush). Derived; 0 means civilian.
        /// Kept as a field so existing `stats.AttackDamage > 0` checks keep working.</summary>
        public int AttackDamage => AttackHack + AttackPierce + AttackCrush;

        public float AttackRange = 3f;
        public float AttackRate = 1f;
        public int ResourceAmount;
        public ResourceType ResourceType = ResourceType.Wood;
        public string ResourceTypeString = "";
        public float WalkSpeed = 8f;
        public int VisionRange = 20;
        /// <summary>&lt;Fogging/&gt; 模板(建筑/gaia):雾中由 mirage 顶替。对齐 Fogging.js。</summary>
        public bool HasFogging;
        /// <summary>&lt;Visibility&gt;&lt;RetainInFog&gt;:已探索雾中保持可见(FOGGED)。单位 false,建筑/gaia true。</summary>
        public bool RetainInFog;
        public bool IsDropsite;
        public bool CanTrain;
        public bool CanBuild;
        public bool CanGather;
        public bool IsBuilding;
        public int GarrisonCapacity;
        /// <summary>TrainingRestrictions/Category (Civilian/Hero/WarDog/...). Empty if absent.</summary>
        public string TrainingCategory = "";

        // Footprint: physical extent for spawn-point search + click hit-testing.
        // Shape: "" (none), "square" (Size0=width, Size1=depth), "circle" (Size0=radius).
        public string FootprintShape = "";
        public Maths.Fixed FootprintSize0 = Maths.Fixed.Zero;
        public Maths.Fixed FootprintSize1 = Maths.Fixed.Zero;
        public Maths.Fixed FootprintHeight = Maths.Fixed.Zero;

        // Obstruction: what this entity blocks.
        // Shape: "" (default to unit circle), "static" (Size0=width, Size1=depth), "unit" (mobile circle).
        public string ObstructionShape = "";
        public Maths.Fixed ObstructionSize0 = Maths.Fixed.Zero;
        public Maths.Fixed ObstructionSize1 = Maths.Fixed.Zero;
        public bool ObstructionActive = true;

        // Resistance: per-type damage reduction (read from Resistance/Entity/Damage/{type}).
        // The damage formula (0.9^resistance) lives in DamageBlock.WithResistanceApplied.
        public int ResistanceHack;
        public int ResistancePierce;
        public int ResistanceCrush;
        public int ResistanceCapture;

        /// <summary>BuildRestrictions/Territory tokens(空格分隔 own/ally/neutral/enemy)。
        /// 空串 = 无领土限制(非建筑)。</summary>
        public string BuildRestrictionsTerritory = "";

        // TerritoryInfluence:Radius=0 → 不装配 TerritoryInfluenceComponent(无影响力)。
        public Maths.Fixed TerritoryInfluenceRadius = Maths.Fixed.Zero;
        public int TerritoryInfluenceWeight = 1;
        public bool TerritoryInfluenceRoot;

        public List<string> GetClassList() =>
            EntityClassHelper.BuildClassList(Classes, VisibleClasses,
                string.IsNullOrWhiteSpace(Category) ? GenericName : Category);
    }
}
