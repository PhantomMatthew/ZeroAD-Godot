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
                stats.BuildTime = cost.GetChild("BuildTime").IsOk
                    ? cost.GetChild("BuildTime").ToFixed().ToFloat() : 5.0f;
            }

            var attack = node.GetChild("Attack");
            if (attack.IsOk)
            {
                var melee = attack.GetChild("Melee");
                if (melee.IsOk)
                {
                    var dmg = melee.GetChild("Damage");
                    if (dmg.IsOk)
                    {
                        stats.AttackDamage = dmg.GetChild("Hack").IsOk
                            ? dmg.GetChild("Hack").ToInt()
                            : dmg.GetChild("Pierce").IsOk
                                ? dmg.GetChild("Pierce").ToInt()
                                : dmg.GetChild("Crush").ToInt();
                    }
                    stats.AttackRange = 3.0f;
                    stats.AttackRate = melee.GetChild("RepeatTime").IsOk
                        ? 1000f / melee.GetChild("RepeatTime").ToInt() : 1.0f;
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
        public string TemplateName = "";
        public int MaxHealth = 100;
        public int WoodCost;
        public int FoodCost;
        public int StoneCost;
        public int MetalCost;
        public int PopulationCost;
        public float BuildTime = 5f;
        public int AttackDamage;
        public float AttackRange = 3f;
        public float AttackRate = 1f;
        public int ResourceAmount;
        public ResourceType ResourceType = ResourceType.Wood;
        public string ResourceTypeString = "";
        public float WalkSpeed = 8f;
        public int VisionRange = 20;
        public bool IsDropsite;
        public bool CanTrain;
        public bool CanBuild;
        public bool CanGather;
        public bool IsBuilding;
        public int GarrisonCapacity;

        public List<string> GetClassList() =>
            EntityClassHelper.BuildClassList(Classes, VisibleClasses,
                string.IsNullOrWhiteSpace(Category) ? GenericName : Category);
    }
}
