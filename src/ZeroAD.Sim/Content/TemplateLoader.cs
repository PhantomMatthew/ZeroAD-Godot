using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Templates;

namespace ZeroAD.Sim.Content
{
    public sealed class TemplateLoader
    {
        private readonly string _templatesRoot;
        private readonly Dictionary<string, ParamNode> _cache = new();

        /// <summary>VFS 分层解析器(mod 挂载;null = 单根目录旧行为)。
        /// 模板根相对 mods 根固定为 "simulation/templates"。</summary>
        private readonly VfsResolver? _vfs;
        private readonly string _relRoot;

        public TemplateLoader(string templatesRoot)
        {
            _templatesRoot = templatesRoot;
            _relRoot = "";
        }

        /// <summary>分层构造:mod 挂载(mod.enabledmods 升序,末位最高优先)。</summary>
        public TemplateLoader(VfsResolver vfs, string relRoot = "simulation/templates")
        {
            _vfs = vfs;
            _relRoot = relRoot;
            _templatesRoot = "(vfs:" + relRoot + ")";   // 仅日志/错误信息用
        }

        /// <summary>批量枚举装载期间不校验(装载 ≠ 请求;上游校验发生在 GetTemplate
        /// 访问时。否则抽象父 template_* 会被拒成空节点并刷告警——它们本就
        /// "individually invalid",从不被独立请求)。</summary>
        private bool _suppressValidation;

        public ParamNode LoadTemplate(string templateName)
        {
            if (_cache.TryGetValue(templateName, out var cached))
            {
                // 缓存命中也过校验 memo:批量装载期跳过的模板在首次真正被请求时校验
                // (上游 m_TemplateSchemaValidity 同此语义:访问时一次性判定)。
                if (!CheckSchemaValid(templateName, cached) && _validationStrict)
                    return new ParamNode();
                return cached;
            }

            var resolved = ParamNode.ResolveTemplate(templateName, LoadXmlDocument);
            // Schema 校验(上游:合并树校验,无效即拒载)。strict 下无效 → 缓存空节点
            // (与缺失模板同语义,上游 GetTemplate 返回 NULL)。
            if (!CheckSchemaValid(templateName, resolved) && _validationStrict)
                resolved = new ParamNode();
            _cache[templateName] = resolved;
            return resolved;
        }

        public Dictionary<string, ParamNode> LoadAllTemplates()
        {
            _suppressValidation = true;
            try { return LoadAllTemplatesCore(); }
            finally { _suppressValidation = false; }
        }

        private Dictionary<string, ParamNode> LoadAllTemplatesCore()
        {
            if (_vfs != null)
            {
                // 分层并集(同名高优先覆盖;rel 去 .xml 作模板名)。
                foreach (var (rel, _) in _vfs.EnumerateLayered(_relRoot, "*.xml"))
                {
                    string relPath = rel.Replace(".xml", "");
                    try { LoadTemplate(relPath); } catch { }
                }
                return _cache;
            }
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

            if (_vfs != null)
            {
                foreach (string dir in searchDirs)
                {
                    string rel = string.IsNullOrEmpty(dir)
                        ? _relRoot + "/" + relPath.Replace('\\', '/')
                        : _relRoot + "/" + dir.Replace('\\', '/') + "/" + relPath.Replace('\\', '/');
                    // relPath 已是平台分隔;转回正斜杠供 VFS。
                    string vfsRel = rel.Replace(Path.DirectorySeparatorChar, '/');
                    string? fullPath = _vfs.ResolveFile(vfsRel);
                    if (fullPath != null)
                        return XDocument.Load(fullPath);
                }
                return XDocument.Parse("<Entity/>");
            }

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

        /// <summary>模板文件存在性(原版 TemplateManager.TemplateExists;可训练列表过滤用——
        /// 通用兵营列表含本文明没有的兵种,如 athen 无 clubman/maceman,须在解析端剔除)。
        /// 与 LoadXmlDocument 走同一组搜索目录。</summary>
        public bool TemplateExists(string templateName)
        {
            string relPath = templateName.Replace('/', Path.DirectorySeparatorChar) + ".xml";
            string[] searchDirs = { "special" + Path.DirectorySeparatorChar + "filter", "mixins", "" };
            if (_vfs != null)
            {
                foreach (string dir in searchDirs)
                {
                    string rel = string.IsNullOrEmpty(dir)
                        ? _relRoot + "/" + relPath
                        : _relRoot + "/" + dir + "/" + relPath;
                    string vfsRel = rel.Replace(Path.DirectorySeparatorChar, '/')
                        .Replace("\\", "/");
                    if (_vfs.ResolveFile(vfsRel) != null)
                        return true;
                }
                return false;
            }
            foreach (string dir in searchDirs)
            {
                string fullPath = string.IsNullOrEmpty(dir)
                    ? Path.Combine(_templatesRoot, relPath)
                    : Path.Combine(_templatesRoot, dir, relPath);
                if (File.Exists(fullPath))
                    return true;
            }
            return false;
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

        /// <summary>读模板节点的属性子节点:ParamNode 把 XML 属性存为 "@name"(ApplyLayer),
        /// 先试 "@name",无则回落元素子节点(兼容手写模板)。</summary>
        private static ParamNode Attr(ParamNode node, string name)
        {
            var a = node.GetChild("@" + name);
            return a.IsOk ? a : node.GetChild(name);
        }

        public static TemplateStats ExtractStatsFromNode(ParamNode node)
        {            var stats = new TemplateStats();

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
                // SpecificName(原语言专名,如 Agora/Oikos;structree 建筑列标题用,原版同)。
                var specificName = identity.GetChild("SpecificName");
                if (specificName.IsOk)
                    stats.SpecificName = specificName.ToString();
                var category = identity.GetChild("Category");
                if (category.IsOk)
                    stats.Category = category.ToString();
                // Identity/Requirements/Techs:前置科技(phase_* 等;训练/建造/研究面板的
                // 阶段过滤数据源——原版未满足即隐藏)。否定 token(-/!)跳过。
                var reqs = identity.GetChild("Requirements");
                if (reqs.IsOk)
                {
                    var techs = reqs.GetChild("Techs");
                    if (techs.IsOk)
                        stats.RequiredTechs = techs.ToString();
                }
                // Identity/Civ:模板原生文明({native} 占位替换值;PlayerComponent.Civ 是
                // 属主文明,二者在被占领建筑上不同——原版 Trainer.js 正是如此区分)。
                var civ = identity.GetChild("Civ");
                if (civ.IsOk)
                    stats.Civ = civ.ToString().Trim();
                // Identity/Icon:原版头像路径(units/athen/infantry_spearman.png,
                // 相对 art/textures/ui/session/portraits/),GUI 训练按钮数据驱动头像。
                var icon = identity.GetChild("Icon");
                if (icon.IsOk)
                    stats.Icon = icon.ToString().Trim();
                // Identity/Undeletable:原版 Identity.js IsUndeletable(=="true")——
                // 英雄棺椁/阵型控制器/gaia 等不可自毁;删除命令与第三面板禁用态的数据源。
                var undeletable = identity.GetChild("Undeletable");
                if (undeletable.IsOk)
                    stats.Undeletable = undeletable.ToBool();
            }

            var health = node.GetChild("Health");
            if (health.IsOk)
            {
                stats.HasHealth = true;
                stats.MaxHealth = health.GetChild("Max").IsOk
                    ? health.GetChild("Max").ToInt() : 100;
                // RegenRate/IdleRegenRate(原版 Health.js 每秒再生;建筑默认 5)。
                var regen = health.GetChild("RegenRate");
                if (regen.IsOk) stats.HealthRegenRate = regen.ToFixed().ToFloat();
                var idleRegen = health.GetChild("IdleRegenRate");
                if (idleRegen.IsOk) stats.HealthIdleRegenRate = idleRegen.ToFixed().ToFloat();
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

            // Trainer/Entities(A26+ 数据:可训练列表在 Trainer 组件;<ProductionQueue/>
            // 只是空壳能力标记)。ParamNode 已按原版语义跨继承链合并 datatype="tokens"
            // (父列表保留、子追加、"-token" 删除)——athen CC = 父 units/{native}/support_civilian
            // + 子 3 个 {civ} 兵。占位符保持原文,由 ProductionQueue 装配后按属主实时解析
            // ({civ}=属主文明、{native}=模板原生文明),不存在的模板在解析端过滤。
            var trainer = node.GetChild("Trainer");
            if (trainer.IsOk)
            {
                var entities = trainer.GetChild("Entities");
                if (entities.IsOk)
                    stats.TrainableEntities = entities.ToString().Trim();
            }

            // Builder/Entities(可建造列表;同 Trainer/Entities 的 tokens 合并与
            // {civ}/{native} 占位语义——建造面板数据驱动用,由 HUD 按属主实时解析)。
            var builderEnts = node.GetChild("Builder");
            if (builderEnts.IsOk)
            {
                var entities = builderEnts.GetChild("Entities");
                if (entities.IsOk)
                    stats.BuildableEntities = entities.ToString().Trim();
            }

            // Researcher/Technologies(可研究列表;同语义——研究面板数据驱动用)。
            var researcher = node.GetChild("Researcher");
            if (researcher.IsOk)
            {
                var techs = researcher.GetChild("Technologies");
                if (techs.IsOk)
                    stats.ResearchableTechnologies = techs.ToString().Trim();
            }

            // UnitAI/Formations(可编队形列表;阵型面板数据驱动用——单位级 tokens,
            // special/formations/{shape} 全名)。无节点 = 不可编队。
            var unitAi = node.GetChild("UnitAI");
            if (unitAi.IsOk)
            {
                var formations = unitAi.GetChild("Formations");
                if (formations.IsOk)
                    stats.FormationShapes = formations.ToString().Trim();

                // 动物行为参数(原版 UnitAI.js:RoamDistance 存在即动物;template_unit_fauna
                // 系列)。DefaultStance 决定受击响应(skittish 逃/passive-defensive 反击/
                // aggressive 主动);时间字段毫秒 → 秒。
                var stance = unitAi.GetChild("DefaultStance");
                if (stance.IsOk)
                    stats.DefaultStance = stance.ToString().Trim();
                if (unitAi.GetChild("RoamDistance").IsOk)
                    stats.RoamDistance = unitAi.GetChild("RoamDistance").ToFixed().ToFloat();
                if (unitAi.GetChild("FleeDistance").IsOk)
                    stats.FleeDistance = unitAi.GetChild("FleeDistance").ToFixed().ToFloat();
                if (unitAi.GetChild("RoamTimeMin").IsOk)
                    stats.RoamTimeMin = unitAi.GetChild("RoamTimeMin").ToInt() / 1000f;
                if (unitAi.GetChild("RoamTimeMax").IsOk)
                    stats.RoamTimeMax = unitAi.GetChild("RoamTimeMax").ToInt() / 1000f;
                if (unitAi.GetChild("FeedTimeMin").IsOk)
                    stats.FeedTimeMin = unitAi.GetChild("FeedTimeMin").ToInt() / 1000f;
                if (unitAi.GetChild("FeedTimeMax").IsOk)
                    stats.FeedTimeMax = unitAi.GetChild("FeedTimeMax").ToInt() / 1000f;
            }

            // WallSet(城墙组,原版 WallSet.js schema):各部件模板 + 塔楼重叠度;
            // 墙段长度在各自模板的 WallPiece/Length(见下方)。
            var wallSet = node.GetChild("WallSet");
            if (wallSet.IsOk)
            {
                var templates = wallSet.GetChild("Templates");
                if (templates.IsOk)
                {
                    stats.WallSetTower = templates.GetChild("Tower").ToString().Trim();
                    stats.WallSetGate = templates.GetChild("Gate").ToString().Trim();
                    stats.WallSetLong = templates.GetChild("WallLong").ToString().Trim();
                    stats.WallSetMedium = templates.GetChild("WallMedium").ToString().Trim();
                    stats.WallSetShort = templates.GetChild("WallShort").ToString().Trim();
                }
                if (wallSet.GetChild("MinTowerOverlap").IsOk)
                    stats.WallSetMinTowerOverlap = wallSet.GetChild("MinTowerOverlap").ToFixed().ToFloat();
                if (wallSet.GetChild("MaxTowerOverlap").IsOk)
                    stats.WallSetMaxTowerOverlap = wallSet.GetChild("MaxTowerOverlap").ToFixed().ToFloat();
            }
            // WallPiece/Length(墙段/塔楼模板;原版 WallPiece.js,墙体拼链算法的长度源)。
            var wallPiece = node.GetChild("WallPiece");
            if (wallPiece.IsOk && wallPiece.GetChild("Length").IsOk)
                stats.WallPieceLength = wallPiece.GetChild("Length").ToFixed().ToFloat();

            // BuildingAI(建筑自动防御,原版 BuildingAI.js):默认箭数 + 驻军加成倍率/类别。
            var buildingAi = node.GetChild("BuildingAI");
            if (buildingAi.IsOk)
            {
                stats.HasBuildingAI = true;
                if (buildingAi.GetChild("DefaultArrowCount").IsOk)
                    stats.DefaultArrowCount = buildingAi.GetChild("DefaultArrowCount").ToInt();
                if (buildingAi.GetChild("MaxArrowCount").IsOk)
                    stats.MaxArrowCount = buildingAi.GetChild("MaxArrowCount").ToInt();
                if (buildingAi.GetChild("GarrisonArrowMultiplier").IsOk)
                    stats.GarrisonArrowMultiplier = buildingAi.GetChild("GarrisonArrowMultiplier").ToFixed().ToFloat();
                var garClasses = buildingAi.GetChild("GarrisonArrowClasses");
                if (garClasses.IsOk)
                    stats.GarrisonArrowClasses = garClasses.ToString().Trim();
            }

            // Upgrade(建筑升级路径,原版 Upgrade.js):首个升级子节点的目标模板/造价/时间。
            // 哨塔→防御塔等;Entity 含 {civ} 占位,解析端替换。
            var upgrade = node.GetChild("Upgrade");            if (upgrade.IsOk)
            {
                var target = upgrade.GetOnlyChild();
                if (target.IsOk)
                {
                    stats.UpgradeToTemplate = target.GetChild("Entity").ToString().Trim();
                    var upCost = target.GetChild("Cost");
                    if (upCost.IsOk)
                    {
                        stats.UpgradeCostWood = upCost.GetChild("wood").IsOk ? upCost.GetChild("wood").ToInt() : 0;
                        stats.UpgradeCostFood = upCost.GetChild("food").IsOk ? upCost.GetChild("food").ToInt() : 0;
                        stats.UpgradeCostStone = upCost.GetChild("stone").IsOk ? upCost.GetChild("stone").ToInt() : 0;
                        stats.UpgradeCostMetal = upCost.GetChild("metal").IsOk ? upCost.GetChild("metal").ToInt() : 0;
                    }
                    stats.UpgradeTime = target.GetChild("Time").IsOk ? target.GetChild("Time").ToFloat() : 0f;
                }
            }

            // Gate(城门标记;GateComponent 装配/门面板按钮用)
            var gateNode = node.GetChild("Gate");
            if (gateNode.IsOk)
            {
                stats.HasGate = true;
                var passRange = gateNode.GetChild("PassRange");
                if (passRange.IsOk)
                    stats.GatePassRange = passRange.ToFixed().ToFloat();
            }

            var attack = node.GetChild("Attack");
            if (attack.IsOk)
            {
                // 远程节点存在性决定修正值路径前缀(Attack/Ranged vs Attack/Melee)
                stats.AttackIsRanged = attack.GetChild("Ranged").IsOk;
                // Attack/Ranged/RangeOverlay:原版选中时画射程圈的开关(CC/箭塔等防御
                // 建筑有;近战无 → 不显示)。范围圈纹理/厚度不在此提取。
                if (stats.AttackIsRanged)
                    stats.HasRangeOverlay = attack.GetChild("Ranged").GetChild("RangeOverlay").IsOk;
                var melee = attack.GetChild("Melee");
                if (melee.IsOk)
                {
                    var dmg = melee.GetChild("Damage");
                    if (dmg.IsOk)
                    {
                        // Read all physical damage types (any subset may be present;
                        // Fire = 火焰/燃烧系,火攻船等)。
                        stats.AttackHack = dmg.GetChild("Hack").IsOk ? dmg.GetChild("Hack").ToInt() : 0;
                        stats.AttackPierce = dmg.GetChild("Pierce").IsOk ? dmg.GetChild("Pierce").ToInt() : 0;
                        stats.AttackCrush = dmg.GetChild("Crush").IsOk ? dmg.GetChild("Crush").ToInt() : 0;
                        stats.AttackFire = dmg.GetChild("Fire").IsOk ? dmg.GetChild("Fire").ToInt() : 0;
                    }
                    stats.AttackRange = 3.0f;
                    stats.AttackRate = melee.GetChild("RepeatTime").IsOk
                        ? 1000f / melee.GetChild("RepeatTime").ToInt() : 1.0f;
                    // ApplyStatus(攻击附带状态效果;原版 schema:Melee/Ranged 下
                    // <ApplyStatus><效果名><Duration/Interval/Damage/Stackability></>)。
                    ReadApplyStatus(melee, stats);
                }
                var rangedNode = attack.GetChild("Ranged");
                if (rangedNode.IsOk)
                {
                    ReadApplyStatus(rangedNode, stats);
                    // Ranged/Damage:此前只读 Melee/Damage,纯远程实体(CC/箭塔/弓兵)的
                    // Hack/Pierce/Crush/Fire 全丢 → AttackDamage=0 → 不挂 AttackComponent
                    // → 选中无射程圈、且不能攻击。Melee 缺失时(Ranged-only)此处补读。
                    var rDmg = rangedNode.GetChild("Damage");
                    if (rDmg.IsOk && !melee.IsOk)
                    {
                        stats.AttackHack = rDmg.GetChild("Hack").IsOk ? rDmg.GetChild("Hack").ToInt() : 0;
                        stats.AttackPierce = rDmg.GetChild("Pierce").IsOk ? rDmg.GetChild("Pierce").ToInt() : 0;
                        stats.AttackCrush = rDmg.GetChild("Crush").IsOk ? rDmg.GetChild("Crush").ToInt() : 0;
                        stats.AttackFire = rDmg.GetChild("Fire").IsOk ? rDmg.GetChild("Fire").ToInt() : 0;
                    }
                    // MaxRange / RepeatTime:Melee 路径硬编 AttackRange=3;Ranged 的 60m 等
                    // 此前没读 → CC 范围错(3m 而非 60m)。
                    var maxRange = rangedNode.GetChild("MaxRange");
                    if (maxRange.IsOk) stats.AttackRange = maxRange.ToFloat();
                    var rRepeat = rangedNode.GetChild("RepeatTime");
                    if (rRepeat.IsOk) stats.AttackRate = 1000f / rRepeat.ToInt();
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

                // 逐型 tooltip 记录(getAttackTooltip 遍历同款):Melee/Ranged/Capture
                // 各一条,Slaughter 剔除,AttackName 缺省回落类型名。
                foreach (var (typeName, typeNode) in attack.Children)
                {
                    if (typeName.StartsWith('@')) continue;
                    if (typeName == "Slaughter") continue;
                    var info = new AttackTypeInfo
                    {
                        TypeName = typeName,
                        AttackName = typeNode.GetChild("AttackName").IsOk
                            ? typeNode.GetChild("AttackName").ToString() : typeName,
                    };
                    var infoDmg = typeNode.GetChild("Damage");
                    if (infoDmg.IsOk)
                    {
                        if (infoDmg.GetChild("Hack").IsOk) info.Hack = infoDmg.GetChild("Hack").ToFixed().ToFloat();
                        if (infoDmg.GetChild("Pierce").IsOk) info.Pierce = infoDmg.GetChild("Pierce").ToFixed().ToFloat();
                        if (infoDmg.GetChild("Crush").IsOk) info.Crush = infoDmg.GetChild("Crush").ToFixed().ToFloat();
                        if (infoDmg.GetChild("Fire").IsOk) info.Fire = infoDmg.GetChild("Fire").ToFixed().ToFloat();
                    }
                    var infoCap = typeNode.GetChild("Capture");
                    if (infoCap.IsOk) info.Capture = infoCap.ToFixed().ToFloat();
                    var repeat = typeNode.GetChild("RepeatTime");
                    if (repeat.IsOk) info.RepeatTimeMs = repeat.ToInt();
                    var maxR = typeNode.GetChild("MaxRange");
                    if (maxR.IsOk) info.MaxRange = maxR.ToFixed().ToFloat();
                    var minR = typeNode.GetChild("MinRange");
                    if (minR.IsOk) info.MinRange = minR.ToFixed().ToFloat();
                    // 逐型 Restricted/Preferred 类门(原版 AttackType.CanAttack/偏好 +2)。
                    var restr = typeNode.GetChild("RestrictedClasses");
                    if (restr.IsOk) info.RestrictedClasses = restr.ToString().Trim();
                    var pref = typeNode.GetChild("PreferredClasses");
                    if (pref.IsOk) info.PreferredClasses = pref.ToString().Trim();
                    // 逐型 ApplyStatus(攻击附带状态)。
                    var aps = typeNode.GetChild("ApplyStatus");
                    if (aps.IsOk)
                    {
                        foreach (var (effName, effNode) in aps.Children)
                        {
                            if (effName.StartsWith('@')) continue;
                            info.StatusEffectName = effName;
                            if (effNode.GetChild("Duration").IsOk)
                                info.StatusEffectDurationMs = effNode.GetChild("Duration").ToFixed().ToFloat();
                            if (effNode.GetChild("Interval").IsOk)
                                info.StatusEffectIntervalMs = effNode.GetChild("Interval").ToFixed().ToFloat();
                            var st = effNode.GetChild("Stackability");
                            if (st.IsOk) info.StatusEffectStackability = st.ToString().Trim();
                            var sd = effNode.GetChild("Damage");
                            if (sd.IsOk)
                            {
                                if (sd.GetChild("Hack").IsOk) info.StatusEffectDmgHack = sd.GetChild("Hack").ToInt();
                                if (sd.GetChild("Pierce").IsOk) info.StatusEffectDmgPierce = sd.GetChild("Pierce").ToInt();
                                if (sd.GetChild("Crush").IsOk) info.StatusEffectDmgCrush = sd.GetChild("Crush").ToInt();
                                if (sd.GetChild("Fire").IsOk) info.StatusEffectDmgFire = sd.GetChild("Fire").ToInt();
                            }
                            break;   // 原版 oneOrMore,单效果
                        }
                    }
                    // 逐型 Splash(范围伤害;原版 Attack/*/Splash 块,圆形衰减)。
                    var splash = typeNode.GetChild("Splash");
                    if (splash.IsOk)
                    {
                        if (splash.GetChild("Range").IsOk)
                            info.SplashRange = splash.GetChild("Range").ToFixed().ToFloat();
                        var ff = splash.GetChild("FriendlyFire");
                        if (ff.IsOk) info.SplashFriendlyFire = ff.ToBool();
                        var sd = splash.GetChild("Damage");
                        if (sd.IsOk)
                        {
                            if (sd.GetChild("Hack").IsOk) info.SplashHack = sd.GetChild("Hack").ToFixed().ToFloat();
                            if (sd.GetChild("Pierce").IsOk) info.SplashPierce = sd.GetChild("Pierce").ToFixed().ToFloat();
                            if (sd.GetChild("Crush").IsOk) info.SplashCrush = sd.GetChild("Crush").ToFixed().ToFloat();
                            if (sd.GetChild("Fire").IsOk) info.SplashFire = sd.GetChild("Fire").ToFixed().ToFloat();
                        }
                    }
                    stats.AttackTypes.Add(info);
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
                        stats.ResistanceFire = rDmg.GetChild("Fire").IsOk ? rDmg.GetChild("Fire").ToInt() : 0;
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
                // KillBeforeGather(原版 ResourceSupply.js):动物须先猎杀才能采肉——
                // 原版 isUndeletable 的豁免理由之一(删除命令跳过)。
                var killFirst = resourceSupply.GetChild("KillBeforeGather");
                if (killFirst.IsOk)
                    stats.KillBeforeGather = killFirst.ToBool();
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
                // 建筑无 UnitMotion(getSpeedTooltip 不显示 Speed 行)——不设此旗的
                // 模板 WalkSpeed 保持字段默认 8,不能作为显示依据。
                stats.HasUnitMotion = true;
                var walkSpeed = unitMotion.GetChild("WalkSpeed");
                if (walkSpeed.IsOk)
                    stats.WalkSpeed = walkSpeed.ToFixed().ToFloat();
                // RunMultiplier/Acceleration(getSpeedTooltip 的 Run/Acceleration 段)。
                var runMult = unitMotion.GetChild("RunMultiplier");
                if (runMult.IsOk) stats.RunMultiplier = runMult.ToFixed().ToFloat();
                var accel = unitMotion.GetChild("Acceleration");
                if (accel.IsOk) stats.Acceleration = accel.ToFixed().ToFloat();
                // PassabilityClass(原版:default/ship;plane 的 unrestricted 未移植)——
                // 船走水路寻路、水面出生;陆军走陆地。
                var passClass = unitMotion.GetChild("PassabilityClass");
                if (passClass.IsOk) stats.PassabilityClass = passClass.ToString().Trim();
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
            {
                stats.IsDropsite = true;
                var types = dropsite.GetChild("Types");
                if (types.IsOk)
                    stats.DropsiteTypes = types.ToString();
            }

            var production = node.GetChild("ProductionQueue");
            if (production.IsOk)
                stats.CanTrain = true;

            var builder = node.GetChild("Builder");
            if (builder.IsOk)
                stats.CanBuild = true;

            var gatherer = node.GetChild("ResourceGatherer");
            if (gatherer.IsOk)
            {
                stats.CanGather = true;
                // Rates × BaseSpeed = 原版 GetTemplateData 的 resourceGatherRates
                // (getGatherTooltip 数据源);*.ruins 原版明确忽略。
                float baseSpeed = gatherer.GetChild("BaseSpeed").IsOk
                    ? gatherer.GetChild("BaseSpeed").ToFixed().ToFloat() : 1f;
                var rates = gatherer.GetChild("Rates");
                if (rates.IsOk)
                {
                    foreach (var (rateKey, rateNode) in rates.Children)
                    {
                        if (rateKey.StartsWith('@')) continue;
                        if (rateKey.EndsWith(".ruins", System.StringComparison.Ordinal)) continue;
                        stats.GatherRates[rateKey] = rateNode.ToFixed().ToFloat() * baseSpeed;
                    }
                }
            }

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
                var fDisTip = formation.GetChild("DisabledTooltip");
                if (fDisTip.IsOk) stats.FormationDisabledTooltip = fDisTip.ToString().Trim();
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

            // FormationAttack(编队作战;template_formation 默认 CanAttackAsFormation=false,
            // phalanx/syntagma 等为 true)。该组件在原版注册为 IID_Attack(控制器无实体攻击,
            // 仅聚合成员射程 + 标记编队是否可整体作战)。
            var fAttackNode = node.GetChild("FormationAttack");
            if (fAttackNode.IsOk)
            {
                var caaf = fAttackNode.GetChild("CanAttackAsFormation");
                if (caaf.IsOk) stats.FormationCanAttackAsFormation = caaf.ToBool();
            }

            // Footprint: physical extent used for spawn-point search (FootprintComponent) and click
            // hit-testing. Either <Square width depth/> or <Circle radius/> — width/depth/radius
            // 是 XML **属性**,ParamNode 以 "@name" 子节点存储(见 ParamNode.ApplyLayer);
            // 裸名读取恒取不到 → 曾全局回落 12(选择框/阻挡全偏小)。
            var footprint = node.GetChild("Footprint");
            if (footprint.IsOk)
            {
                var square = footprint.GetChild("Square");
                if (square.IsOk)
                {
                    stats.FootprintShape = "square";
                    stats.FootprintSize0 = Attr(square, "width").IsOk ? Attr(square, "width").ToFixed() : stats.FootprintSize0;
                    stats.FootprintSize1 = Attr(square, "depth").IsOk ? Attr(square, "depth").ToFixed() : stats.FootprintSize1;
                }
                var circle = footprint.GetChild("Circle");
                if (circle.IsOk)
                {
                    stats.FootprintShape = "circle";
                    stats.FootprintSize0 = Attr(circle, "radius").IsOk ? Attr(circle, "radius").ToFixed() : stats.FootprintSize0;
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
                    stats.ObstructionSize0 = Attr(staticEl, "width").IsOk ? Attr(staticEl, "width").ToFixed() : stats.ObstructionSize0;
                    stats.ObstructionSize1 = Attr(staticEl, "depth").IsOk ? Attr(staticEl, "depth").ToFixed() : stats.ObstructionSize1;
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

            // Upkeep(生产维护费;Upkeep.js)。
            var upkeepNode = node.GetChild("Upkeep");
            if (upkeepNode.IsOk)
            {
                stats.HasUpkeep = true;
                var uIvl = upkeepNode.GetChild("Interval");
                if (uIvl.IsOk) stats.UpkeepIntervalMs = uIvl.ToFixed().ToFloat();
                var uRates = upkeepNode.GetChild("Rates");
                if (uRates.IsOk)
                {
                    if (uRates.GetChild("food").IsOk) stats.UpkeepFood = uRates.GetChild("food").ToInt();
                    if (uRates.GetChild("wood").IsOk) stats.UpkeepWood = uRates.GetChild("wood").ToInt();
                    if (uRates.GetChild("stone").IsOk) stats.UpkeepStone = uRates.GetChild("stone").ToInt();
                    if (uRates.GetChild("metal").IsOk) stats.UpkeepMetal = uRates.GetChild("metal").ToInt();
                }
            }

            // AlertRaiser(CC/货栈/粮仓/市场;警铃范围参数)。
            var alertNode = node.GetChild("AlertRaiser");
            if (alertNode.IsOk)
            {
                stats.HasAlertRaiser = true;
                var aList = alertNode.GetChild("List");
                if (aList.IsOk) stats.AlertRaiserList = aList.ToString().Trim();
                if (alertNode.GetChild("RaiseAlertRange").IsOk)
                    stats.AlertRaiseRange = alertNode.GetChild("RaiseAlertRange").ToFixed().ToFloat();
                if (alertNode.GetChild("EndOfAlertRange").IsOk)
                    stats.AlertEndRange = alertNode.GetChild("EndOfAlertRange").ToFixed().ToFloat();
                if (alertNode.GetChild("SearchRange").IsOk)
                    stats.AlertSearchRange = alertNode.GetChild("SearchRange").ToFixed().ToFloat();
            }

            // DeathDamage(死亡自爆;fireship/flamethrower)。
            var deathNode = node.GetChild("DeathDamage");
            if (deathNode.IsOk)
            {
                stats.HasDeathDamage = true;
                if (deathNode.GetChild("Range").IsOk)
                    stats.DeathDamageRange = deathNode.GetChild("Range").ToFixed().ToFloat();
                var ff = deathNode.GetChild("FriendlyFire");
                if (ff.IsOk) stats.DeathDamageFriendlyFire = ff.ToBool();
                var dd = deathNode.GetChild("Damage");
                if (dd.IsOk)
                {
                    if (dd.GetChild("Hack").IsOk) stats.DeathDamageHack = dd.GetChild("Hack").ToInt();
                    if (dd.GetChild("Pierce").IsOk) stats.DeathDamagePierce = dd.GetChild("Pierce").ToInt();
                    if (dd.GetChild("Crush").IsOk) stats.DeathDamageCrush = dd.GetChild("Crush").ToInt();
                    if (dd.GetChild("Fire").IsOk) stats.DeathDamageFire = dd.GetChild("Fire").ToInt();
                }
            }

            // AutoBuildable(自动完工;Rate)。
            var autoNode = node.GetChild("AutoBuildable");
            if (autoNode.IsOk)
            {
                stats.HasAutoBuildable = true;
                if (autoNode.GetChild("Rate").IsOk)
                    stats.AutoBuildRate = autoNode.GetChild("Rate").ToFixed().ToFloat();
            }

            // MotionBall(原版 type="test" 滚坡测试组件;节点存在即装配)。
            if (node.GetChild("MotionBall").IsOk)
                stats.HasMotionBall = true;

            // UnitMotionFlying(飞行单位;MaxSpeed)。
            var flyNode = node.GetChild("UnitMotionFlying");
            if (flyNode.IsOk)
            {
                stats.HasUnitMotionFlying = true;
                if (flyNode.GetChild("MaxSpeed").IsOk)
                    stats.FlyingMaxSpeed = flyNode.GetChild("MaxSpeed").ToFixed().ToFloat();
            }

            // Promotion(军衔晋升):Entity = 下一 rank 模板(继承链合并——基类只给
            // RequiredXp,rank 模板给 Entity;elite 段无 Promotion 即到顶)。
            var promoNode = node.GetChild("Promotion");
            if (promoNode.IsOk)
            {
                stats.HasPromotion = true;
                var pEnt = promoNode.GetChild("Entity");
                if (pEnt.IsOk) stats.PromotionEntity = pEnt.ToString().Trim();
                var pXp = promoNode.GetChild("RequiredXp");
                if (pXp.IsOk) stats.PromotionRequiredXp = pXp.ToInt();
            }

            // Loot(战利品;template_unit/gaia 动物等 247 模板):xp + 四资源直子节点。
            var lootNode = node.GetChild("Loot");
            if (lootNode.IsOk)
            {
                stats.HasLoot = true;
                var xp = lootNode.GetChild("xp");
                if (xp.IsOk) stats.LootXp = xp.ToInt();
                var lf = lootNode.GetChild("food");
                if (lf.IsOk) stats.LootFood = lf.ToInt();
                var lw = lootNode.GetChild("wood");
                if (lw.IsOk) stats.LootWood = lw.ToInt();
                var ls = lootNode.GetChild("stone");
                if (ls.IsOk) stats.LootStone = ls.ToInt();
                var lm = lootNode.GetChild("metal");
                if (lm.IsOk) stats.LootMetal = lm.ToInt();
            }

            // ResourceTrickle(资源涓流;奇观/牲口棚/玩家模板):Rates 四资源 + Interval(ms)。
            var trickleNode = node.GetChild("ResourceTrickle");
            if (trickleNode.IsOk)
            {
                stats.HasResourceTrickle = true;
                var interval = trickleNode.GetChild("Interval");
                if (interval.IsOk) stats.TrickleIntervalMs = interval.ToFixed().ToFloat();
                var rates = trickleNode.GetChild("Rates");
                if (rates.IsOk)
                {
                    var tf = rates.GetChild("food");
                    if (tf.IsOk) stats.TrickleFood = tf.ToFixed().ToFloat();
                    var tw = rates.GetChild("wood");
                    if (tw.IsOk) stats.TrickleWood = tw.ToFixed().ToFloat();
                    var ts = rates.GetChild("stone");
                    if (ts.IsOk) stats.TrickleStone = ts.ToFixed().ToFloat();
                    var tm = rates.GetChild("metal");
                    if (tm.IsOk) stats.TrickleMetal = tm.ToFixed().ToFloat();
                }
            }

            // Repairable(可修理;template_structure 默认 + 攻城器/船):RepairTimeRatio。
            var repairableNode = node.GetChild("Repairable");
            if (repairableNode.IsOk)
            {
                stats.HasRepairable = true;
                var ratio = repairableNode.GetChild("RepairTimeRatio");
                if (ratio.IsOk) stats.RepairTimeRatio = ratio.ToFixed().ToFloat();
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

        /// <summary>读攻击型的 ApplyStatus(原版 helpers/Attack.js 的 StatusEffectsSchema):
        /// 首个子元素=效果名(Burning/Poisoned),子级 Duration(ms)/Interval(ms)/
        /// Damage/{Hack|Pierce|Crush|Fire}/Stackability(Ignore|Extend|Replace|Stack)。</summary>
        private static void ReadApplyStatus(ParamNode attackTypeNode, TemplateStats stats)
        {
            var applyStatus = attackTypeNode.GetChild("ApplyStatus");
            if (!applyStatus.IsOk) return;
            foreach (var (effectName, effectNode) in applyStatus.Children)
            {
                if (effectName.StartsWith('@')) continue;   // 属性子节点不算
                stats.StatusEffectName = effectName;
                var dur = effectNode.GetChild("Duration");
                if (dur.IsOk) stats.StatusEffectDurationMs = dur.ToFixed().ToFloat();
                var interval = effectNode.GetChild("Interval");
                if (interval.IsOk) stats.StatusEffectIntervalMs = interval.ToFixed().ToFloat();
                var stack = effectNode.GetChild("Stackability");
                if (stack.IsOk) stats.StatusEffectStackability = stack.ToString().Trim();
                var dmg = effectNode.GetChild("Damage");
                if (dmg.IsOk)
                {
                    if (dmg.GetChild("Hack").IsOk) stats.StatusEffectDamageHack = dmg.GetChild("Hack").ToInt();
                    if (dmg.GetChild("Pierce").IsOk) stats.StatusEffectDamagePierce = dmg.GetChild("Pierce").ToInt();
                    if (dmg.GetChild("Crush").IsOk) stats.StatusEffectDamageCrush = dmg.GetChild("Crush").ToInt();
                    if (dmg.GetChild("Fire").IsOk) stats.StatusEffectDamageFire = dmg.GetChild("Fire").ToInt();
                }
                return;   // 原版 oneOrMore,单效果够用(现有数据均单效果)
            }
        }

        public IReadOnlyDictionary<string, ParamNode> Cache => _cache;

        // ── Schema 校验(原版 CCmpTemplateManager 的 m_Validator + m_TemplateSchemaValidity)──

        private Schema.TemplateSchemaValidator? _schemaValidator;
        private bool _validationStrict;
        /// <summary>每模板名有效性记忆(上游 m_TemplateSchemaValidity:只算一次)。</summary>
        private readonly Dictionary<string, bool> _validityMemo = new();

        /// <summary>启用 Xeromyces 级 schema 校验(strict:无效模板按缺失处理——上游
        /// GetTemplate 返回 NULL 的语义;非 strict:仅 Diag 告警)。在 LoadAllTemplates
        /// 之前调用才会全量生效;之后调用则对后续新加载的模板生效。</summary>
        public void EnableSchemaValidation(Schema.TemplateSchema schema, bool strict)
        {
            _schemaValidator = new Schema.TemplateSchemaValidator(schema);
            _validationStrict = strict;
            _validityMemo.Clear();
        }

        /// <summary>hotload 入口:使单个模板的缓存与校验记忆失效(下次访问重载重校验)。
        /// 上游模板 XML 从不热载(15 年 TODO,ICmpTemplateManager.h:127);此处超越上游:
        /// 失效后新 spawn 即得新参数(存量实体重灌见 PORTING-GAPS)。</summary>
        public void Invalidate(string templateName)
        {
            _cache.Remove(templateName);
            _validityMemo.Remove(templateName);
        }

        /// <summary>全量失效(mod 切换/批量重载)。</summary>
        public void InvalidateAll()
        {
            _cache.Clear();
            _validityMemo.Clear();
        }

        /// <summary>加载后校验( memo 命中直接返回有效性)。返回 false = 无效。</summary>
        internal bool CheckSchemaValid(string templateName, ParamNode merged)
        {
            if (_schemaValidator == null || _suppressValidation) return true;
            // 继承图层(mixins/special filter)从不独立校验(上游同,见 TemplateSchemaValidator)。
            if (!Schema.TemplateSchemaValidator.IsStandaloneTemplateName(templateName)) return true;
            if (_validityMemo.TryGetValue(templateName, out bool memo)) return memo;

            var errors = _schemaValidator.ValidateOne(merged);
            bool valid = errors.Count == 0;
            _validityMemo[templateName] = valid;
            if (!valid)
            {
                // 上游:LOGERROR("Failed to validate entity template '%s'") + 结构化错误。
                void report(string msg) { if (_validationStrict) Diag.Err("Templates", msg); else Diag.Warn("Templates", msg); }
                report($"Failed to validate entity template '{templateName}' ({errors.Count} error(s))" +
                    (_validationStrict ? " — refused (strict)" : ""));
                foreach (string e in errors.Take(5))
                    report($"  {templateName}: {e}");
                if (errors.Count > 5)
                    report($"  {templateName}: … and {errors.Count - 5} more");
            }
            return valid;
        }
    }

    public sealed class TemplateStats
    {
        public string Name = "Entity";
        public string GenericName = "";
        /// <summary>Identity/SpecificName(原语言专名,如 Agora、Oikos;科技树建筑列标题用)。</summary>
        public string SpecificName = "";
        public string Category = "";
        /// <summary>Identity/Requirements/Techs 原文(空格分隔;含否定 token)。
        /// 空 = 无前置。阶段过滤:全部肯定 token 已研究才显示/可用。</summary>
        public string RequiredTechs = "";
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
        /// <summary>Fire 伤害(火焰/燃烧;data/damage_types/fire.json,order 4)。</summary>
        public int AttackFire;
        /// <summary>模板含 Attack/Ranged 节点 = 远程单位(修正值路径前缀用)。</summary>
        public bool AttackIsRanged;
        /// <summary>模板含 Attack/Ranged/RangeOverlay——原版选中时画射程圈的开关
        /// (防御建筑有,近战无)。</summary>
        public bool HasRangeOverlay;

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
        public int AttackDamage => AttackHack + AttackPierce + AttackCrush + AttackFire;

        // ApplyStatus(攻击附带状态;空名 = 无)。
        public string StatusEffectName = "";
        public float StatusEffectDurationMs;
        public float StatusEffectIntervalMs;
        public string StatusEffectStackability = "Ignore";
        public int StatusEffectDamageHack;
        public int StatusEffectDamagePierce;
        public int StatusEffectDamageCrush;
        public int StatusEffectDamageFire;

        public float AttackRange = 3f;
        public float AttackRate = 1f;
        public int ResourceAmount;
        public ResourceType ResourceType = ResourceType.Wood;
        public string ResourceTypeString = "";
        /// <summary>Identity/Undeletable(原版 Identity.js IsUndeletable):英雄棺椁/阵型
        /// 控制器/gaia 等不可被 delete 命令自毁(第三面板禁用态 + 执行端跳过的数据源)。</summary>
        public bool Undeletable;
        /// <summary>ResourceSupply/KillBeforeGather(原版):须先杀死才能采集(动物)——
        /// 原版 isUndeletable 的豁免理由之一。</summary>
        public bool KillBeforeGather;
        /// <summary>UnitAI/DefaultStance(原版 g_Stances 行名:aggressive/skittish/
        /// passive-defensive …)。空 = 用组件默认(aggressive)。</summary>
        public string DefaultStance = "";
        /// <summary>UnitAI/RoamDistance(原版:IsAnimal 的判定——&gt;0 即动物)及配套
        /// 游荡/进食/逃跑参数(模板毫秒已转秒)。</summary>
        public float RoamDistance;
        public float FleeDistance;
        public float RoamTimeMin, RoamTimeMax, FeedTimeMin, FeedTimeMax;

        /// <summary>WallSet/Templates 各部件模板(空 = 非墙组)。原版 Walls.js GetWallPlacement
        /// 的输入。</summary>
        public string WallSetTower = "";
        public string WallSetGate = "";
        public string WallSetLong = "";
        public string WallSetMedium = "";
        public string WallSetShort = "";
        public float WallSetMinTowerOverlap = 0.05f;
        public float WallSetMaxTowerOverlap = 0.9f;
        public bool IsWallSet => WallSetLong.Length > 0;
        /// <summary>WallPiece/Length(墙段/塔楼的链长,拼链算法用)。</summary>
        public float WallPieceLength;

        /// <summary>BuildingAI 段存在(原版:防御塔/CC 等自动放箭)。</summary>
        public bool HasBuildingAI;
        /// <summary>BuildingAI/DefaultArrowCount:无驻军时的基础箭数(塔 2、CC 6)。</summary>
        public int DefaultArrowCount = 1;
        /// <summary>BuildingAI/MaxArrowCount:箭数上限(0 = 不限,原版 Infinity)。</summary>
        public int MaxArrowCount;
        /// <summary>BuildingAI/GarrisonArrowMultiplier:每个驻军弓手加箭倍率。</summary>
        public float GarrisonArrowMultiplier = 1f;
        /// <summary>BuildingAI/GarrisonArrowClasses:计入加箭的类别(tokens,如 "Infantry"/"Soldier")。</summary>
        public string GarrisonArrowClasses = "";
        public float WalkSpeed = 8f;
        /// <summary>UnitMotion/Weight 推挤权重(原版缺省 10;大者难推也推得狠)。</summary>
        public ZeroAD.Sim.Maths.Fixed MovementWeight = ZeroAD.Sim.Maths.Fixed.FromInt(10);
        /// <summary>模板声明 &lt;UnitMotion&gt;(单位/船;建筑无 → 不显示 Speed 行)。</summary>
        public bool HasUnitMotion;
        /// <summary>UnitMotion/RunMultiplier(template_unit 默认 1.67,继承合并后可读)。
        /// Run 速度 = WalkSpeed × RunMultiplier(getSpeedTooltip 同式)。</summary>
        public float RunMultiplier = 1f;
        /// <summary>UnitMotion/Acceleration(template_unit 默认 35;tooltip 用)。</summary>
        public float Acceleration;
        /// <summary>ResourceGatherer/Rates × BaseSpeed(原版 GetTemplateData 的
        /// resourceGatherRates 同式)。键为 subtype 原文("food.meat"…;*.ruins 已剔除)。
        /// 空 = 无采集率(getGatherTooltip 不显示)。</summary>
        public Dictionary<string, float> GatherRates = new(System.StringComparer.Ordinal);
        /// <summary>Attack 逐型 tooltip 记录(Melee/Ranged/Capture;Slaughter 剔除)。
        /// 与上方 Attack* 合计字段并存——合计供 AttackComponent/HUD,逐型供
        /// getAttackTooltip 格式化。</summary>
        public List<AttackTypeInfo> AttackTypes = new();

        // ── P0 补齐件(PORTING-GAPS §3A)──
        /// <summary>DeathDamage(火船/喷火器):死亡自爆。</summary>
        public bool HasDeathDamage;
        public float DeathDamageRange = 20f;
        public bool DeathDamageFriendlyFire;
        public int DeathDamageHack, DeathDamagePierce, DeathDamageCrush, DeathDamageFire;
        /// <summary>AutoBuildable(自动完工;当前数据 0 模板)。</summary>
        public bool HasAutoBuildable;
        public float AutoBuildRate = 1f;
        /// <summary>Upkeep(维护费;当前数据 0 模板)。</summary>
        public bool HasUpkeep;
        public float UpkeepIntervalMs = 10000f;
        public int UpkeepFood, UpkeepWood, UpkeepStone, UpkeepMetal;
        /// <summary>AlertRaiser(CC/货栈/粮仓/市场的警铃)。</summary>
        public bool HasAlertRaiser;
        public string AlertRaiserList = "Civilian";
        public float AlertRaiseRange = 120f, AlertEndRange = 180f, AlertSearchRange = 100f;
        /// <summary>UnitMotionFlying(鸟群等飞行单位)。</summary>
        public bool HasUnitMotionFlying;
        /// <summary>模板含 <MotionBall/> 节点(原版 test 组件标记;demo/test 地图滚球)。</summary>
        public bool HasMotionBall;
        public float FlyingMaxSpeed = 15f;
        /// <summary>Promotion(军衔):晋升链(空 = 到顶)与阈值(继承链合并:
        /// template_unit_infantry 基类 RequiredXp=100,各兵种可覆盖)。</summary>
        public bool HasPromotion;
        /// <summary>Health/RegenRate(HP/秒;建筑 template_structure 默认 5)。</summary>
        public float HealthRegenRate;
        /// <summary>Health/IdleRegenRate(空闲单位额外再生)。</summary>
        public float HealthIdleRegenRate;
        public string PromotionEntity = "";
        public int PromotionRequiredXp = 100;
        /// <summary>UnitMotion/PassabilityClass("default"/"ship";原版 plane 另有
        /// unrestricted,未移植)。船 = "ship" → 水路寻路 + 水面出生。</summary>
        public string PassabilityClass = "default";
        public int VisionRange = 20;
        /// <summary>&lt;Fogging/&gt; 模板(建筑/gaia):雾中由 mirage 顶替。对齐 Fogging.js。</summary>
        public bool HasFogging;
        /// <summary>&lt;Visibility&gt;&lt;RetainInFog&gt;:已探索雾中保持可见(FOGGED)。单位 false,建筑/gaia true。</summary>
        public bool RetainInFog;
        public bool IsDropsite;
        /// <summary>ResourceDropsite/Types 原文(空格分隔:wood stone metal)——
        /// structree tooltip 的"Dropsite for:"图标行。</summary>
        public string DropsiteTypes = "";
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
        /// <summary>Formation/DisabledTooltip(人数不足置灰时的提示,如
        /// "Requires at least 2 Soldiers or Siege Engines.")。</summary>
        public string FormationDisabledTooltip = "";
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
        /// <summary>FormationAttack/CanAttackAsFormation(编队整体作战能力;
        /// phalanx/syntagma 类为 true,默认 false)。</summary>
        public bool FormationCanAttackAsFormation;
        /// <summary>TrainingRestrictions/Category (Civilian/Hero/WarDog/...). Empty if absent.</summary>
        public string TrainingCategory = "";

        /// <summary>Identity/Civ:模板原生文明代码(athen/spart/...),Trainer {native} 占位
        /// 替换值。与 PlayerComponent.Civ(属主文明)在被占领建筑上不同。空 = 模板未声明。</summary>
        public string Civ = "";
        /// <summary>Identity/Icon:原版头像相对路径(units/athen/infantry_spearman.png,
        /// 相对 art/textures/ui/session/portraits/),GUI 数据驱动头像用。空 = 未声明。</summary>
        public string Icon = "";
        /// <summary>Trainer/Entities 跨继承链合并原文(空格分隔 tokens,含 {civ}/{native}
        /// 占位;合并语义=父列表保留+子追加+"-token"删除,对齐 CParamNode)。空 = 不可训练。
        /// 装配进 ProductionQueue.TrainableTokens,按属主文明实时解析。</summary>
        public string TrainableEntities = "";
        /// <summary>Builder/Entities tokens(可建造列表;{civ}/{native} 占位,解析端替换)。</summary>
        public string BuildableEntities = "";
        /// <summary>Researcher/Technologies tokens(可研究列表;同占位语义)。</summary>
        public string ResearchableTechnologies = "";
        /// <summary>UnitAI/Formations tokens(可编队形列表,special/formations/{shape} 全名)。</summary>
        public string FormationShapes = "";

        /// <summary>升级目标模板(Upgrade 首个子节点的 Entity;含 {civ} 占位)。空 = 不可升级。</summary>
        public string UpgradeToTemplate = "";
        public int UpgradeCostWood;
        public int UpgradeCostFood;
        public int UpgradeCostStone;
        public int UpgradeCostMetal;
        public float UpgradeTime;
        /// <summary>Gate 节点存在(城门模板;GateComponent 装配与 gate 面板按钮用)。</summary>
        public bool HasGate;
        /// <summary>Gate/PassRange 开门感应半径(原版默认 20m)。</summary>
        public float GatePassRange = 20f;

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
        /// <summary>Fire 抗性(燃烧/火攻)。</summary>
        public int ResistanceFire;
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

        // Loot(战利品;template_unit/gaia 动物):HasLoot=false → 不装配。
        public bool HasLoot;
        public int LootXp;
        public int LootFood;
        public int LootWood;
        public int LootStone;
        public int LootMetal;

        // ResourceTrickle(资源涓流;奇观/牲口棚/玩家模板):HasResourceTrickle=false → 不装配。
        public bool HasResourceTrickle;
        public float TrickleIntervalMs = 1000f;
        public float TrickleFood;
        public float TrickleWood;
        public float TrickleStone;
        public float TrickleMetal;

        // Repairable(可修理;template_structure 默认 + 攻城器/船):HasRepairable=false → 不装配。
        public bool HasRepairable;
        public float RepairTimeRatio = 2.0f;

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

    /// <summary>单个攻击型的 tooltip 数据(原版 getAttackTooltip 遍历 Attack 子节点;
    /// Damage 四型 + Capture + RepeatTime/MaxRange/MinRange)。</summary>
    public sealed class AttackTypeInfo
    {
        /// <summary>类型名:Melee / Ranged / Capture(Slaughter 解析期已剔除)。</summary>
        public string TypeName = "";
        /// <summary>AttackName(原版 attackLabel 数据源;缺省回落类型名)。</summary>
        public string AttackName = "";
        public float Hack, Pierce, Crush, Fire;
        public float Capture;
        /// <summary>RepeatTime 毫秒;0 = 无间隔段。</summary>
        public int RepeatTimeMs;
        /// <summary>MaxRange/MinRange 米;0 = 无该段。</summary>
        public float MaxRange, MinRange;
        /// <summary>逐型 RestrictedClasses/PreferredClasses(原版 AttackType 门/偏好)。</summary>
        public string RestrictedClasses = "";
        public string PreferredClasses = "";
        /// <summary>逐型 Splash(范围伤害;0 = 无溅射)。</summary>
        public float SplashRange;
        public bool SplashFriendlyFire;
        public float SplashHack, SplashPierce, SplashCrush, SplashFire;
        /// <summary>逐型 ApplyStatus(攻击附带状态;空名 = 无)。</summary>
        public string StatusEffectName = "";
        public float StatusEffectDurationMs, StatusEffectIntervalMs;
        public string StatusEffectStackability = "Ignore";
        public int StatusEffectDmgHack, StatusEffectDmgPierce, StatusEffectDmgCrush, StatusEffectDmgFire;
    }
}
