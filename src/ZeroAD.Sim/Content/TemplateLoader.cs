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
            var stats = ExtractStatsFromNode(node);
            // 回填模板名:IdentityComponent.TemplateName(SpawnUnit)、头像解析、存档重建
            // 都依赖它;此前恒空导致 SimBridge 生成的实体全部丢模板名(头像/Garrisonable
            // 门/视觉回退连锁受影响)。视觉解析仍走 SpawnFromTemplate 的显式参数,勿改回。
            stats.TemplateName = templateName;
            return stats;
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
                stats.HasHealth = true;
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

            // 新版 0 A.D. 数据(A26+):人口加成在顶层 <Population><Bonus>N</Bonus></Population>
            // (与 <Cost><Population> 占用费不同节点)——房子 +5、CC +20 都在这。顶层优先,
            // 缺失时回落上面的旧版 <Cost><PopulationBonus> 读法。
            var popNode = node.GetChild("Population");
            if (popNode.IsOk && popNode.GetChild("Bonus").IsOk)
                stats.PopulationBonus = popNode.GetChild("Bonus").ToInt();

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
                    stats.AttackRange = 3.0f;
                    stats.AttackRate = melee.GetChild("RepeatTime").IsOk
                        ? 1000f / melee.GetChild("RepeatTime").ToInt() : 1.0f;
                }

                // 物理型 PreferredClasses(GetBestAttackAgainst 偏好 +2;Melee 优先,
                // 无 Melee 取 Ranged——原版逐型各有一份,我们物理合一取存在的那型)。
                // RestrictedClasses 同理(原版逐型 CanAttack 门,如冲车 "Field Organic")。
                var physType = melee.IsOk ? melee : attack.GetChild("Ranged");
                if (physType.IsOk)
                {
                    var pref = physType.GetChild("PreferredClasses");
                    if (pref.IsOk) stats.AttackPreferredClasses = pref.ToString();
                    var restr = physType.GetChild("RestrictedClasses");
                    if (restr.IsOk) stats.AttackPhysicalRestrictedClasses = restr.ToString();
                }

                // Capture 攻击类型(原版 Attack/Capture 顶层元素,步兵 2.5/骑兵 1.75):
                // 与物理型并列独立,一次命中只用一型(GetBestAttackAgainst 按 allowCapture 选)。
                var captureType = attack.GetChild("Capture");
                if (captureType.IsOk)
                {
                    var capVal = captureType.GetChild("Capture");
                    if (capVal.IsOk) stats.AttackCaptureStrength = capVal.ToFixed();
                    var capRange = captureType.GetChild("MaxRange");
                    if (capRange.IsOk) stats.AttackCaptureRange = capRange.ToFixed().ToFloat();
                    var capRepeat = captureType.GetChild("RepeatTime");
                    if (capRepeat.IsOk) stats.AttackCaptureRate = 1000f / capRepeat.ToInt();
                    var capRestr = captureType.GetChild("RestrictedClasses");
                    if (capRestr.IsOk) stats.AttackCaptureRestrictedClasses = capRestr.ToString();
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
                stats.HasGarrisonHolder = true;
                stats.GarrisonCapacity = garrisonHolder.GetChild("Max").IsOk
                    ? garrisonHolder.GetChild("Max").ToInt() : 10;
                // GarrisonHolder.js 行为字段(List 必需;EjectHealth/Pickup 可选)。
                var ghList = garrisonHolder.GetChild("List");
                if (ghList.IsOk) stats.GarrisonHolderList = ghList.ToString().Trim();
                var ghEject = garrisonHolder.GetChild("EjectClassesOnDestroy");
                if (ghEject.IsOk) stats.GarrisonHolderEjectClasses = ghEject.ToString().Trim();
                var ghHeal = garrisonHolder.GetChild("BuffHeal");
                if (ghHeal.IsOk) stats.GarrisonHolderBuffHeal = ghHeal.ToFixed().ToFloat();
                var ghRange = garrisonHolder.GetChild("LoadingRange");
                if (ghRange.IsOk) stats.GarrisonHolderLoadingRange = ghRange.ToFixed().ToFloat();
                var ghEjectHealth = garrisonHolder.GetChild("EjectHealth");
                if (ghEjectHealth.IsOk) stats.GarrisonHolderEjectHealth = ghEjectHealth.ToFixed().ToFloat();
                var ghPickup = garrisonHolder.GetChild("Pickup");
                if (ghPickup.IsOk) stats.GarrisonHolderPickup = ghPickup.ToBool();
            }

            // Garrisonable(可驻防单位;template_unit 默认 Size=1)。
            var garrisonable = node.GetChild("Garrisonable");
            if (garrisonable.IsOk)
            {
                stats.GarrisonableSize = garrisonable.GetChild("Size").IsOk
                    ? garrisonable.GetChild("Size").ToInt() : 1;
            }

            // Turretable(可上炮塔点;远程兵系)。Schema 为 <empty/>,存在即挂。
            if (node.GetChild("Turretable").IsOk)
                stats.HasTurretable = true;

            // TurretHolder(城墙/哨塔的命名炮塔点;子元素名即点位名)。
            var turretHolder = node.GetChild("TurretHolder");
            if (turretHolder.IsOk)
            {
                var points = turretHolder.GetChild("TurretPoints");
                if (points.IsOk)
                {
                    stats.HasTurretHolder = true;
                    // ParamNode.Children 为排序字典:点位按名序(确定性;记录:原版按模板书写序)。
                    foreach (var (pointName, pointNode) in points.Children)
                    {
                        var def = new TurretPointDef
                        {
                            Name = pointName,
                            X = pointNode.GetChild("X").ToFloat(),
                            Y = pointNode.GetChild("Y").ToFloat(),
                            Z = pointNode.GetChild("Z").ToFloat(),
                            AllowedClasses = pointNode.GetChild("AllowedClasses").IsOk
                                ? pointNode.GetChild("AllowedClasses").ToString().Trim() : "",
                            Angle = pointNode.GetChild("Angle").IsOk
                                ? pointNode.GetChild("Angle").ToFloat() * (float)(System.Math.PI / 180) : null,
                            Template = pointNode.GetChild("Template").IsOk
                                ? pointNode.GetChild("Template").ToString().Trim() : "",
                            Ejectable = !pointNode.GetChild("Ejectable").IsOk
                                || pointNode.GetChild("Ejectable").ToBool(),
                        };
                        stats.TurretPoints.Add(def);
                    }
                }
                var thRange = turretHolder.GetChild("LoadingRange");
                if (thRange.IsOk) stats.TurretHolderLoadingRange = thRange.ToFixed().ToFloat();
                var thPickup = turretHolder.GetChild("Pickup");
                if (thPickup.IsOk) stats.TurretHolderPickup = thPickup.ToBool();
            }

            // Formation(编队控制器;special/formations/* 模板;Formation.js 行为件)。
            // SortingClasses 按 "|" 分层存为字符串列表(每层原文);SortingOrder/MinColumns/
            // MaxColumns/MaxRows/CenterGap 可选(模板缺省 = 0/"")。
            var formation = node.GetChild("Formation");
            if (formation.IsOk)
            {
                stats.HasFormation = true;
                var fReq = formation.GetChild("RequiredMemberCount");
                if (fReq.IsOk) stats.FormationRequiredMemberCount = fReq.ToInt();
                var fSpeed = formation.GetChild("SpeedMultiplier");
                if (fSpeed.IsOk) stats.FormationSpeedMultiplier = fSpeed.ToFixed().ToFloat();
                var fShape = formation.GetChild("FormationShape");
                if (fShape.IsOk) stats.FormationShape = fShape.ToString().Trim();
                var fTurn = formation.GetChild("MaxTurningAngle");
                if (fTurn.IsOk) stats.FormationMaxTurningAngle = fTurn.ToFixed().ToFloat();
                var fSort = formation.GetChild("SortingClasses");
                if (fSort.IsOk)
                {
                    foreach (var level in fSort.ToString().Split('|',
                        System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
                        stats.FormationSortingClasses.Add(level);
                }
                var fSortOrder = formation.GetChild("SortingOrder");
                if (fSortOrder.IsOk) stats.FormationSortingOrder = fSortOrder.ToString().Trim();
                var fShift = formation.GetChild("ShiftRows");
                if (fShift.IsOk) stats.FormationShiftRows = fShift.ToBool();
                var fSepW = formation.GetChild("UnitSeparationWidthMultiplier");
                if (fSepW.IsOk) stats.FormationSepWidthMultiplier = fSepW.ToFixed().ToFloat();
                var fSepD = formation.GetChild("UnitSeparationDepthMultiplier");
                if (fSepD.IsOk) stats.FormationSepDepthMultiplier = fSepD.ToFixed().ToFloat();
                var fSlop = formation.GetChild("Sloppiness");
                if (fSlop.IsOk) stats.FormationSloppiness = fSlop.ToFixed().ToFloat();
                var fRatio = formation.GetChild("WidthDepthRatio");
                if (fRatio.IsOk) stats.FormationWidthDepthRatio = fRatio.ToFixed().ToFloat();
                var fMinC = formation.GetChild("MinColumns");
                if (fMinC.IsOk) stats.FormationMinColumns = fMinC.ToInt();
                var fMaxC = formation.GetChild("MaxColumns");
                if (fMaxC.IsOk) stats.FormationMaxColumns = fMaxC.ToInt();
                var fMaxR = formation.GetChild("MaxRows");
                if (fMaxR.IsOk) stats.FormationMaxRows = fMaxR.ToInt();
                var fGap = formation.GetChild("CenterGap");
                if (fGap.IsOk) stats.FormationCenterGap = fGap.ToFixed().ToFloat();
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

            // TerritoryDecay(建筑默认 template_structure:20 点/秒,"neutral enemy"):
            // DecayRate="Infinity" → 归属跟随领土模式(本数据无实例,标志照原版保留)。
            var territoryDecay = node.GetChild("TerritoryDecay");
            if (territoryDecay.IsOk)
            {
                stats.HasTerritoryDecay = true;
                var decayRate = territoryDecay.GetChild("DecayRate");
                if (decayRate.IsOk)
                {
                    if (decayRate.ToString().Trim() == "Infinity") stats.TerritoryDecayOwnership = true;
                    else stats.TerritoryDecayRate = decayRate.ToFixed();
                }
                var decayTerritory = territoryDecay.GetChild("Territory");
                if (decayTerritory.IsOk) stats.TerritoryDecayTerritory = decayTerritory.ToString().Trim();
            }

            // Capturable(template_structure:500 CP,regen 5/秒):占领点池,TerritoryDecay 下游。
            var capturable = node.GetChild("Capturable");
            if (capturable.IsOk)
            {
                stats.HasCapturable = true;
                var cp = capturable.GetChild("CapturePoints");
                if (cp.IsOk) stats.CapturablePoints = cp.ToFixed();
                var regen = capturable.GetChild("RegenRate");
                if (regen.IsOk) stats.CapturableRegenRate = regen.ToFixed();
                var garrisonRegen = capturable.GetChild("GarrisonRegenRate");
                if (garrisonRegen.IsOk) stats.CapturableGarrisonRegenRate = garrisonRegen.ToFixed();
            }

            // Heal(治疗者单位):Range 米、Health 每次治疗量、Interval 毫秒(→ 秒)、
            // HealableClasses/UnhealableClasses token 串(空格分隔,语义同 MatchesClassList)。
            var healNode = node.GetChild("Heal");
            if (healNode.IsOk)
            {
                stats.HasHeal = true;
                var range = healNode.GetChild("Range");
                if (range.IsOk) stats.HealRange = range.ToFixed().ToFloat();
                var healHealth = healNode.GetChild("Health");
                if (healHealth.IsOk) stats.HealAmount = healHealth.ToInt();
                var interval = healNode.GetChild("Interval");
                if (interval.IsOk) stats.HealInterval = interval.ToInt() / 1000f;
                var healable = healNode.GetChild("HealableClasses");
                if (healable.IsOk) stats.HealHealableClasses = healable.ToString().Trim();
                var unhealable = healNode.GetChild("UnhealableClasses");
                if (unhealable.IsOk) stats.HealUnhealableClasses = unhealable.ToString().Trim();
            }

            // Pack(攻城器):Time 毫秒(→ 秒)、Entity 换形目标模板、State 初始形态。
            var packNode = node.GetChild("Pack");
            if (packNode.IsOk)
            {
                stats.HasPack = true;
                var ptime = packNode.GetChild("Time");
                if (ptime.IsOk) stats.PackTime = ptime.ToInt() / 1000f;
                var pentity = packNode.GetChild("Entity");
                if (pentity.IsOk) stats.PackEntity = pentity.ToString().Trim();
                var pstate = packNode.GetChild("State");
                if (pstate.IsOk) stats.PackStartsPacked = pstate.ToString().Trim() == "packed";
            }

            // TreasureCollector(单位收集宝物;template_unit 默认件):MaxDistance 米。
            var tcNode = node.GetChild("TreasureCollector");
            if (tcNode.IsOk)
            {
                stats.HasTreasureCollector = true;
                var maxDist = tcNode.GetChild("MaxDistance");
                if (maxDist.IsOk) stats.TreasureCollectorMaxDistance = maxDist.ToFixed().ToFloat();
            }

            // Treasure(gaia 宝物):CollectTime 毫秒(→ 秒)+ Resources 四资源。
            var treasureNode = node.GetChild("Treasure");
            if (treasureNode.IsOk)
            {
                stats.HasTreasure = true;
                var collectTime = treasureNode.GetChild("CollectTime");
                if (collectTime.IsOk) stats.TreasureCollectTime = collectTime.ToInt() / 1000f;
                var resources = treasureNode.GetChild("Resources");
                if (resources.IsOk)
                {
                    var food = resources.GetChild("Food");
                    if (food.IsOk) stats.TreasureFood = food.ToInt();
                    var wood = resources.GetChild("Wood");
                    if (wood.IsOk) stats.TreasureWood = wood.ToInt();
                    var stone = resources.GetChild("Stone");
                    if (stone.IsOk) stats.TreasureStone = stone.ToInt();
                    var metal = resources.GetChild("Metal");
                    if (metal.IsOk) stats.TreasureMetal = metal.ToInt();
                }
            }

            // Trader(贸易单位):GainMultiplier 基准倍率 + 可选 GarrisonGainMultiplier。
            var traderNode = node.GetChild("Trader");
            if (traderNode.IsOk)
            {
                stats.HasTrader = true;
                var gainMult = traderNode.GetChild("GainMultiplier");
                if (gainMult.IsOk) stats.TraderGainMultiplier = gainMult.ToFixed().ToFloat();
                var garrisonMult = traderNode.GetChild("GarrisonGainMultiplier");
                if (garrisonMult.IsOk) stats.TraderGarrisonGainMultiplier = garrisonMult.ToFixed().ToFloat();
            }

            // Market(市场/船坞):TradeType token 串(land/naval)+ InternationalBonus。
            var marketNode = node.GetChild("Market");
            if (marketNode.IsOk)
            {
                stats.HasMarket = true;
                var tradeType = marketNode.GetChild("TradeType");
                if (tradeType.IsOk) stats.MarketTradeTypes = tradeType.ToString().Trim();
                var intlBonus = marketNode.GetChild("InternationalBonus");
                if (intlBonus.IsOk) stats.MarketInternationalBonus = intlBonus.ToFixed().ToFloat();
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
        /// <summary>True only when the template XML actually declares a &lt;Health&gt; node.
        /// Gaia resources (trees/rocks) have none in 0 A.D. data — they are not attackable —
        /// while fauna does. Spawn paths must key HealthComponent creation off this flag, not
        /// off MaxHealth (which defaults to 100 even when undeclared).</summary>
        public bool HasHealth;
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
        /// <summary>模板含 Attack/Ranged 节点 = 远程单位(修正值路径前缀用)。</summary>
        public bool AttackIsRanged;

        // Capture 攻击类型(原版 Attack/Capture 顶层元素;Strength=0 = 无此类型)。
        public Maths.Fixed AttackCaptureStrength = Maths.Fixed.Zero;
        public float AttackCaptureRange = 4f;
        public float AttackCaptureRate = 1f;
        public string AttackCaptureRestrictedClasses = "";
        /// <summary>物理型(Melee|Ranged)PreferredClasses token 串。</summary>
        public string AttackPreferredClasses = "";
        /// <summary>物理型(Melee|Ranged)RestrictedClasses token 串。</summary>
        public string AttackPhysicalRestrictedClasses = "";

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
        /// <summary>模板含 &lt;GarrisonHolder&gt; 块(GarrisonHolder.js 行为件标记)。</summary>
        public bool HasGarrisonHolder;
        public string GarrisonHolderList = "";
        public string GarrisonHolderEjectClasses = "";
        public float GarrisonHolderBuffHeal;
        public float GarrisonHolderLoadingRange = 2f;
        public float GarrisonHolderEjectHealth = -1f;   // -1 = 无阈值(原版 undefined)
        public bool GarrisonHolderPickup;
        /// <summary>&lt;Garrisonable&gt;&lt;Size&gt;;0 = 无该组件。</summary>
        public int GarrisonableSize;
        /// <summary>&lt;Turretable/&gt; 存在(schema 为空元素)。</summary>
        public bool HasTurretable;
        public bool HasTurretHolder;
        public float TurretHolderLoadingRange = 2f;
        public bool TurretHolderPickup;
        public readonly List<TurretPointDef> TurretPoints = new();
        /// <summary>模板含 &lt;Formation&gt; 块(编队控制器;Formation.js 行为件标记)。
        /// 为真时 EntityAssembler 走 AssembleFormationController 分支(非普通单位)。</summary>
        public bool HasFormation;
        public int FormationRequiredMemberCount = 2;
        public float FormationSpeedMultiplier = 1f;
        public string FormationShape = "square";
        public float FormationMaxTurningAngle = 1f;
        /// <summary>SortingClasses 按 "|" 分层(每层原文,如 "Melee Ranged")。</summary>
        public readonly List<string> FormationSortingClasses = new();
        public string FormationSortingOrder = "";
        public bool FormationShiftRows;
        public float FormationSepWidthMultiplier = 1f;
        public float FormationSepDepthMultiplier = 1f;
        public float FormationSloppiness;
        public float FormationWidthDepthRatio = 1f;
        public int FormationMinColumns;
        public int FormationMaxColumns;
        public int FormationMaxRows;
        public float FormationCenterGap;
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

        // TerritoryDecay:HasTerritoryDecay=false → 不装配(单位无衰减)。
        public bool HasTerritoryDecay;
        public Maths.Fixed TerritoryDecayRate = Maths.Fixed.Zero;
        public string TerritoryDecayTerritory = "";
        public bool TerritoryDecayOwnership;

        // Capturable:HasCapturable=false → 不装配。
        public bool HasCapturable;
        public Maths.Fixed CapturablePoints = Maths.Fixed.Zero;
        public Maths.Fixed CapturableRegenRate = Maths.Fixed.Zero;
        public Maths.Fixed CapturableGarrisonRegenRate = Maths.Fixed.Zero;

        // Heal(治疗者;template_unit_support_healer 等):HasHeal=false → 不装配。
        public bool HasHeal;
        public float HealRange = 15f;
        public int HealAmount = 5;
        /// <summary>Heal/Interval(模板毫秒 → 秒)。</summary>
        public float HealInterval = 2f;
        public string HealHealableClasses = "";
        public string HealUnhealableClasses = "";

        // Pack(攻城器打包;template_unit_siege_*):HasPack=false → 不装配。
        public bool HasPack;
        /// <summary>Pack/Time(模板毫秒 → 秒)。</summary>
        public float PackTime = 5f;
        /// <summary>Pack/Entity:打包完成换成的模板名。</summary>
        public string PackEntity = "";
        /// <summary>Pack/State == "packed":该模板本身是打包态( packed 变体模板)。</summary>
        public bool PackStartsPacked;

        // TreasureCollector(template_unit 默认件):HasTreasureCollector=false → 不装配。
        public bool HasTreasureCollector;
        public float TreasureCollectorMaxDistance = 5f;

        // Treasure(gaia 宝物;template_gaia_treasure):HasTreasure=false → 不装配。
        public bool HasTreasure;
        /// <summary>Treasure/CollectTime(模板毫秒 → 秒)。</summary>
        public float TreasureCollectTime = 1f;
        public int TreasureFood;
        public int TreasureWood;
        public int TreasureStone;
        public int TreasureMetal;

        // Trader(贸易单位;template_unit_support_trader 等):HasTrader=false → 不装配。
        public bool HasTrader;
        public float TraderGainMultiplier = 0.75f;
        /// <summary>Trader/GarrisonGainMultiplier(可选;0 = 无舰载商人加成)。</summary>
        public float TraderGarrisonGainMultiplier;

        // Market(市场/船坞;template_structure_economic_market):HasMarket=false → 不装配。
        public bool HasMarket;
        /// <summary>Market/TradeType 原文("land"/"naval",可空格分隔两者)。</summary>
        public string MarketTradeTypes = "";
        public float MarketInternationalBonus = 0.2f;

        public List<string> GetClassList() =>
            EntityClassHelper.BuildClassList(Classes, VisibleClasses,
                string.IsNullOrWhiteSpace(Category) ? GenericName : Category);
    }

    /// <summary>&lt;TurretHolder&gt;&lt;TurretPoints&gt; 下的一个命名点位(TurretHolder.js)。</summary>
    public struct TurretPointDef
    {
        public string Name;
        public float X, Y, Z;
        public string AllowedClasses;
        public float? Angle;      // 弧度(模板为度,解析时换算)
        public string Template;
        public bool Ejectable;
    }
}
