using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

[Component("ProductionQueue", "ProductionQueue")]
public sealed class ProductionQueue : ComponentBase, IComponentMessageHandler
{
    private readonly List<ProductionItem> _queue = new();
    private float _progress;

    public IReadOnlyList<ProductionItem> Queue => _queue;
    public float Progress => _progress;
    public int QueueCount => _queue.Count;

    protected override void OnInit()
    {
        _progress = 0;
    }

    /// <summary>
    /// Simple enqueue kept for tests and legacy callers. Production code should prefer
    /// <see cref="EnqueueTraining"/>, which reads template cost/build-time, validates pop
    /// and entity limits, and charges the player before queuing.
    /// </summary>
    public void Enqueue(string templateName, int woodCost, int foodCost, float buildTime, int count = 1)
    {
        _queue.Add(new ProductionItem
        {
            TemplateName = templateName,
            WoodCost = woodCost,
            FoodCost = foodCost,
            BuildTime = buildTime,
            Count = count,
            OriginalCount = count,
        });
    }

    private bool Reject(string reason)
    {
        LastRejectionReason = reason;
        return false;
    }

    /// <summary>
    /// Deterministic training entry point. Resolves the trainer's owner, reads real template
    /// cost/build-time/pop-cost/category, validates affordability + entity limits + pop headroom,
    /// then charges the player and enqueues. Returns false (no side effects) on any validation
    /// failure. This is the single source of truth shared by SimBridge.CommandTrain and
    /// NetTurnManager.ExecuteCommand so single-player and lockstep multiplayer agree exactly.
    /// </summary>
    /// <summary>最近一次 EnqueueTraining 拒绝原因(cannot-afford/pop-limit/entity-limit/
    /// defeated/...;成功时清 null)。执行端据此发 train-rejected 事件给 GUI 弹提示——
    /// 原版训练失败有红字反馈,不能静默。只读诊断,不影响判定。</summary>
    public string? LastRejectionReason { get; private set; }

    /// <summary>autoQueue(原版 ProductionQueue.autoqueuing):队列空时自动重排同款
    /// 头项(重排会多耗一拍才开工——原版注释:比手动略亏)。</summary>
    public bool AutoQueueing;

    public void EnableAutoQueue() => AutoQueueing = true;
    public void DisableAutoQueue() => AutoQueueing = false;

    /// <summary>Trainer/Entities 合并原文(空格分隔 tokens,含 {civ}/{native} 占位;
    /// 由 SimBridge 装配自模板 stats.TrainableEntities)。空 = 旧装配路径(SpawnBuilding
    /// 兜底/测试直挂),此时训练不受列表门限制,保持旧行为。</summary>
    public string TrainableTokens = "";

    /// <summary>模板原生文明({native} 替换值;装配自模板 Identity/Civ)。</summary>
    public string NativeCiv = "";

    /// <summary>解析可训练列表(原版 Trainer.CalculateEntitiesMap):{civ}→当前属主文明
    /// (PlayerComponent.Civ——占领易主后自动跟随,无需重装配),{native}→NativeCiv;
    /// 过滤不存在模板(通用列表含本文明没有的兵种,如 athen 无 clubman)与未解析占位
    /// (无属主/无文明)。每次调用实时解析;GUI 训练面板与训练门共用此单一事实源。</summary>
    public List<string> GetTrainableEntities(ComponentManager cm)
    {
        var result = new List<string>();
        if (TrainableTokens.Length == 0) return result;
        string ownerCiv = "";
        var owner = cm.QueryInterface<OwnershipComponent>(Entity);
        if (owner != null)
        {
            var player = cm.GetPlayerEntity(owner.PlayerId);
            if (player != null) ownerCiv = player.Civ;
        }
        // 原版 Trainer.js split(/\s+/):单层(无继承合并)的 token 值保留 XML 换行,
        // 必须按任意空白切分(只切空格会把 "\n" 混进模板名,全被 TemplateExists 滤光)。
        foreach (var raw in TrainableTokens.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw;
            if (NativeCiv.Length > 0) token = token.Replace("{native}", NativeCiv);
            if (ownerCiv.Length > 0) token = token.Replace("{civ}", ownerCiv);
            // 占位未解析(无属主或文明缺失)→ 该项不可训练(原版 TemplateExists 同效过滤)。
            if (token.Contains('{')) continue;
            if (cm.Templates == null || !cm.Templates.TemplateExists(token)) continue;
            if (!result.Contains(token)) result.Add(token);
        }
        return result;
    }

    public bool EnqueueTraining(string templateName, int count, ComponentManager cm)
    {
        LastRejectionReason = null;
        if (count <= 0) return Reject("bad-count");
        if (cm.Templates == null) return Reject("no-templates");

        var owner = cm.QueryInterface<OwnershipComponent>(Entity);
        if (owner == null) return Reject("no-owner");

        var stats = cm.Templates.ExtractStats(templateName);
        int totalWood = stats.WoodCost * count;
        int totalFood = stats.FoodCost * count;
        int totalStone = stats.StoneCost * count;
        int totalMetal = stats.MetalCost * count;

        // Find the owner's PlayerComponent. Player entities are registered via ComponentManager's
        // player map (set up by the presentation layer at world init); resolve through it.
        var player = cm.GetPlayerEntity(owner.PlayerId);
        if (player == null) return Reject("no-player");
        // A defeated player can't train units.
        if (player.IsDefeated()) return Reject("defeated");

        // 可训练列表门(原版 Trainer.AddToBatch 拒非列表项):GUI 只展示列表项,这里是
        // 执行端兜底,挡 AI/热键/存档回放发出的列表外模板。TrainableTokens 空 = 旧装配
        // 路径(SpawnBuilding 兜底),不门,保持旧行为。
        if (TrainableTokens.Length > 0 && !GetTrainableEntities(cm).Contains(templateName))
            return Reject("not-trainable");

        if (!player.CanAfford(totalWood, totalFood, totalStone, totalMetal)) return Reject("cannot-afford");

        // 前置科技门(原版 RequirementsMet:RequiredTechs 未满足 → 拒训练;
        // 阶段过滤如 cavalry_archer_b 需 phase_town)。否定 token(-/!)跳过。
        if (stats.RequiredTechs.Length > 0)
        {
            var techMgr = cm.GetPlayerEntityId(owner.PlayerId) is { } peid
                ? cm.QueryInterface<ZeroAD.Sim.Components.TechnologyManager>(peid)
                : null;
            if (techMgr != null)
            {
                foreach (var tok in stats.RequiredTechs.Split((char[]?)null,
                    System.StringSplitOptions.RemoveEmptyEntries))
                {
                    if (tok.StartsWith("-") || tok.StartsWith("!")) continue;
                    if (!techMgr.IsResearched(tok)) return Reject("requirements-unmet");
                }
            }
        }

        // Pop headroom check (pop is charged immediately on spawn, but we pre-validate to avoid
        // charging resources for a unit that can never appear).
        int popCost = stats.PopulationCost * count;
        if (popCost > player.PopHeadroom) return Reject("pop-limit");

        // Entity limits (category caps like "Hero: 1").
        EntityLimitsComponent? limits = null;
        EntityId? playerEid = cm.GetPlayerEntityId(owner.PlayerId);
        if (playerEid is { } pe)
            limits = cm.QueryInterface<EntityLimitsComponent>(pe);
        if (limits != null && !limits.AllowedToTrain(stats.TrainingCategory, count))
            return Reject("entity-limit");

        // 训练时间过修正值管线(科技如 "Cost/BuildTime" ×0.9;单位未出生,走模板查询)
        float buildTime = stats.BuildTime;
        if (playerEid.HasValue)
            buildTime = cm.Modifiers.ApplyTemplate(
                "Cost/BuildTime", stats.BuildTime, stats.GetClassList(), playerEid.Value);

        // All checks passed — commit.
        player.Spend(totalWood, totalFood, totalStone, totalMetal);
        // 资源花费事件（驱动 StatisticsTracker.resourcesUsed）。镜像 Player.js:349。
        int pid = owner.PlayerId;
        if (totalWood > 0) cm.Events.RaiseResourceSpent(new ZeroAD.Sim.Events.ResourceSpentEvent { PlayerId = pid, Type = ResourceType.Wood, Amount = totalWood });
        if (totalFood > 0) cm.Events.RaiseResourceSpent(new ZeroAD.Sim.Events.ResourceSpentEvent { PlayerId = pid, Type = ResourceType.Food, Amount = totalFood });
        if (totalStone > 0) cm.Events.RaiseResourceSpent(new ZeroAD.Sim.Events.ResourceSpentEvent { PlayerId = pid, Type = ResourceType.Stone, Amount = totalStone });
        if (totalMetal > 0) cm.Events.RaiseResourceSpent(new ZeroAD.Sim.Events.ResourceSpentEvent { PlayerId = pid, Type = ResourceType.Metal, Amount = totalMetal });
        _queue.Add(new ProductionItem
        {
            TemplateName = templateName,
            WoodCost = stats.WoodCost,
            FoodCost = stats.FoodCost,
            StoneCost = stats.StoneCost,
            MetalCost = stats.MetalCost,
            PopulationCost = stats.PopulationCost,
            BuildTime = buildTime,
            Count = count,
            OriginalCount = count,
            TrainingCategory = stats.TrainingCategory
        });

        cm.Events.RaiseTrainingQueued(new Events.TrainingQueuedEvent
        {
            TrainerEntity = Entity,
            UnitTemplate = templateName,
            Count = count
        });
        return true;
    }

    public void ResetQueue()
    {
        _queue.Clear();
        _progress = 0;
    }

    /// <summary>
    /// 取消队列第 index 项并全额退还已付资源(对齐原版 RemoveItem:进度作废,资源退还;
    /// 我们的批次原子完成、不存在部分产出,故按 Count 全退)。取消头项时进度清零。
    /// </summary>
    public bool CancelAt(int index, ComponentManager cm)
    {
        if (index < 0 || index >= _queue.Count) return false;
        var item = _queue[index];
        var owner = cm.QueryInterface<OwnershipComponent>(Entity);
        var player = owner != null ? cm.GetPlayerEntity(owner.PlayerId) : null;
        if (player != null)
        {
            if (item.WoodCost > 0) player.AddResource(ResourceType.Wood, item.WoodCost * item.Count);
            if (item.FoodCost > 0) player.AddResource(ResourceType.Food, item.FoodCost * item.Count);
            if (item.StoneCost > 0) player.AddResource(ResourceType.Stone, item.StoneCost * item.Count);
            if (item.MetalCost > 0) player.AddResource(ResourceType.Metal, item.MetalCost * item.Count);
        }
        _queue.RemoveAt(index);
        if (index == 0) _progress = 0;
        return true;
    }

    /// <summary>
    /// Advance the queue by <paramref name="dt"/> seconds. When the head item completes, spawns
    /// <see cref="ProductionItem.Count"/> entities via <see cref="ComponentManager.SpawnEntity"/>
    /// (sim-owned, deterministic), applies the trainer's rally point, and raises
    /// TrainingFinished. This replaces the legacy SimBridge spawn-after-tick path so the whole
    /// train→spawn loop is replayable and OOS-safe.
    /// </summary>
    /// <summary>
    /// Advance the queue by <paramref name="dt"/> seconds. 逐单位出货(原版 Trainer.Item.
    /// Finish:每拍 Spawn 一只,count-- 未空则保留;BuildTime 为单只时间)。队列溢出
    /// 时间自动转给下一项(原版 ProgressTimeout 的 while(time>0) 分配语义)。
    /// </summary>
    public void Tick(float dt, ComponentManager cm)
    {
        if (_queue.Count == 0) return;

        _progress += dt;

        var trainerPos = cm.QueryInterface<PositionComponent>(Entity);
        var owner = cm.QueryInterface<OwnershipComponent>(Entity);
        var rally = cm.QueryInterface<RallyPointComponent>(Entity);
        var footprint = cm.QueryInterface<FootprintComponent>(Entity);
        int ownerId = owner?.PlayerId ?? -1;

        while (_queue.Count > 0)
        {
            var current = _queue[0];
            float unitTime = current.BuildTime / Math.Max(1, current.OriginalCount);
            if (_progress < unitTime) break;
            _progress -= unitTime;

            if (trainerPos == null)
            {
                cm.Events.RaiseTrainingFinished(new Events.TrainingFinishedEvent
                {
                    TrainerEntity = Entity,
                    UnitTemplate = current.TemplateName
                });
                current.Count--;
            }
            else
            {
                SpawnOne(cm, current, trainerPos, footprint, rally, ownerId);
                cm.Events.RaiseTrainingFinished(new Events.TrainingFinishedEvent
                {
                    TrainerEntity = Entity,
                    UnitTemplate = current.TemplateName
                });
                current.Count--;
            }

            if (current.Count <= 0)
            {
                _queue.RemoveAt(0);
                if (AutoQueueing && _queue.Count == 0)
                {
                    // 原版 AddItem 校验(资源/上限);失败即 DisableAutoQueue。
                    if (!EnqueueTraining(current.TemplateName, current.OriginalCount, cm))
                        DisableAutoQueue();
                    _progress = 0;   // 重排项本拍不开工(原版:比手动略亏)
                    return;
                }
                if (_queue.Count == 0) { _progress = 0; return; }
            }
        }
    }

    /// <summary>单只出货(原版 Trainer.Item.Spawn 每周期一单位):Footprint 出生点
    /// → 黄金角环回退;集结点走 UnitAI.Walk 指令。</summary>
    private void SpawnOne(ComponentManager cm, ProductionItem current,
        PositionComponent trainerPos, FootprintComponent? footprint,
        RallyPointComponent? rally, int ownerId)
    {
        float baseX = trainerPos.Position.X.ToFloat();
        float baseZ = trainerPos.Position.Z.ToFloat();
        Fixed spawnedRadius = Fixed.FromFloat(1.0f);
        string spawnPassClass = "default";
        try
        {
            spawnPassClass = cm.Templates?.ExtractStats(current.TemplateName)?.PassabilityClass
                ?? "default";
        }
        catch { }

        int batchIndex = current.OriginalCount - current.Count;
        float sx, sz;
        var spawn = footprint?.PickSpawnPoint(spawnedRadius, spawnPassClass);
        if (spawn != null && spawn.Value.X.ToFloat() >= 0)
        {
            sx = spawn.Value.X.ToFloat();
            sz = spawn.Value.Z.ToFloat();
        }
        else
        {
            // Fallback: golden-angle ring around the trainer (keeps training working even when
            // the area is crowded — units may overlap, but they'll disperse via UnitMotion).
            // 定点 sincos:出生点是 sim 位置,libm 三角跨平台漂移 → OOS。
            float angle = batchIndex * 2.4f;
            float radius = 6f + (batchIndex / 6) * 3f;
            Trig.SinCosApprox(Maths.Fixed.FromFloat(angle), out Maths.Fixed spSin, out Maths.Fixed spCos);
            sx = baseX + spCos.ToFloat() * radius;
            sz = baseZ + spSin.ToFloat() * radius;
        }
        var spawned = cm.SpawnEntity(current.TemplateName, sx, sz, ownerId);

        // 原版 RallyPoint.OrderToRallyPoint:出厂单位按集结队列下发排队指令链
        // (多点 + 逐点指令类型;空队列 → 不动)。
        // 门槛用"任一玩家有集结点"(OrderToRallyPoint 内部走属主回落链——
        // 集结点存建筑属主名下,出厂单位属主通常一致,兼容键 -1 兜底旧路径)。
        if (rally != null && rally.HasAnyPositions)
            rally.OrderToRallyPoint(cm, spawned);
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("count", _queue.Count);
        s.NumberFixed("progress", ZeroAD.Sim.Maths.Fixed.FromFloat(_progress));
        s.StringASCII("trainToks", TrainableTokens);
        s.StringASCII("nativeCiv", NativeCiv);
        s.Bool("autoq", AutoQueueing);
        foreach (var item in _queue)
        {
            s.StringASCII("tmpl", item.TemplateName);
            s.NumberI32("wood", item.WoodCost);
            s.NumberI32("food", item.FoodCost);
            s.NumberI32("stone", item.StoneCost);
            s.NumberI32("metal", item.MetalCost);
            s.NumberI32("pop", item.PopulationCost);
            s.NumberI32("batch", item.Count);
            s.StringASCII("cat", item.TrainingCategory);
            s.NumberFixed("time", ZeroAD.Sim.Maths.Fixed.FromFloat(item.BuildTime));
        }
    }

    public override void Deserialize(IDeserializer d)
    {
        int count = d.NumberI32("count");
        _progress = d.NumberFixed("progress").ToFloat();
        TrainableTokens = d.StringASCII("trainToks");
        NativeCiv = d.StringASCII("nativeCiv");
        AutoQueueing = d.Bool("autoq");
        _queue.Clear();
        for (int i = 0; i < count; i++)
        {
            _queue.Add(new ProductionItem
            {
                TemplateName = d.StringASCII("tmpl"),
                WoodCost = d.NumberI32("wood"),
                FoodCost = d.NumberI32("food"),
                StoneCost = d.NumberI32("stone"),
                MetalCost = d.NumberI32("metal"),
                PopulationCost = d.NumberI32("pop"),
                Count = d.NumberI32("batch"),
                OriginalCount = d.NumberI32("batch"),
                TrainingCategory = d.StringASCII("cat"),
                BuildTime = d.NumberFixed("time").ToFloat()
            });
        }
    }

    public void HandleMessage(IMessage message) { }
}

public sealed class ProductionItem
{
    public string TemplateName = "";
    public int WoodCost;
    public int FoodCost;
    public int StoneCost;
    public int MetalCost;
    public int PopulationCost;
    public float BuildTime;
    /// <summary>剩余未出的单位数(逐单位出货递减;原版 Trainer.Item.count)。</summary>
    public int Count = 1;
    /// <summary>入队时的批大小(出货角序/autoQueue 重排用;原版 Item 原批参数)。</summary>
    public int OriginalCount = 1;
    public string TrainingCategory = "";
}

[Component("Player", "Player")]
public sealed class PlayerComponent : ComponentBase, IComponentMessageHandler
{
    public int Wood;
    public int Food;
    public int Stone;
    public int Metal;
    /// <summary>Live pop usage (units owned). Maintained by ownership-change handlers.</summary>
    /// <summary>易物价乘数(原版 Player.js barterMultiplier:模板 special/players/{civ}
    /// 基值 + 科技/光环修正值管线重算;键 = 资源码)。</summary>
    public Dictionary<string, float> BarterMultiplierBuy = new();
    public Dictionary<string, float> BarterMultiplierSell = new();

    /// <summary>重算易物乘数(原版 OnValueModification 的 BarterMultiplier 分支;
    /// 研究完成/兜底重算时由 ValueModificationApplier 调用)。</summary>
    public void RecomputeBarterMultipliers(ComponentManager cm)
    {
        foreach (var res in BarterMultiplierBuy.Keys.ToArray())
            BarterMultiplierBuy[res] = cm.Modifiers.Apply(
                $"Player/BarterMultiplier/Buy/{res}", BarterMultiplierBuy[res], Entity);
        foreach (var res in BarterMultiplierSell.Keys.ToArray())
            BarterMultiplierSell[res] = cm.Modifiers.Apply(
                $"Player/BarterMultiplier/Sell/{res}", BarterMultiplierSell[res], Entity);
    }

    /// <summary>易物乘数(原版 GetBarterMultiplier;缺省 1)。</summary>
    public float GetBarterMultiplierBuy(string res) =>
        BarterMultiplierBuy.TryGetValue(res, out var v) ? v : 1f;
    public float GetBarterMultiplierSell(string res) =>
        BarterMultiplierSell.TryGetValue(res, out var v) ? v : 1f;

    public int PopUsed;
    /// <summary>Sum of PopulationComponent.Bonus across the player's buildings.</summary>
    public int PopBonuses;
    /// <summary>Hard global cap (0 A.D. default 300).</summary>
    public int MaxPopCap = 300;

    /// <summary>文明代码(athen/spart/...),驱动科技 requirements {civ} 判定与 civ 加成。
    /// 默认 athen(与 SimBridge 当前硬编码一致);gamesetup GUI 落地后由开局参数注入。</summary>
    public string Civ = "athen";

    /// <summary>运行时队伍号(-1 = FFA / 无队伍)。原版 Player.js team。由 SimBridge 建图处从
    /// PlayerSlotSetup.Team 写入(SeedDiplomacyFromTeams 调用点)。外交面板"Team"列显示用。
    /// 序列化进存档(随 PlayerComponent 流;SaveGameManager 版本 +1)。</summary>
    public int Team = -1;

    /// <summary>Win/loss state. Mono-directional from Active (only Active can transition).
    /// Ported from Player.js STATE_ACTIVE/DEFEATED/WON. Defaults live on the field initializer
    /// (not OnInit) so callers using `new PlayerComponent { ... }` keep their values.</summary>
    public PlayerState State = PlayerState.Active;

    public bool IsActive() => State == PlayerState.Active;
    public bool IsDefeated() => State == PlayerState.Defeated;
    public bool HasWon() => State == PlayerState.Won;

    /// <summary>Mark this player defeated. Idempotent: only transitions from Active (mirrors
    /// Player.js SetState's `!IsActive() return` guard). Returns true if the state changed.</summary>
    public bool SetDefeated()
    {
        if (!IsActive()) return false;
        State = PlayerState.Defeated;
        return true;
    }

    /// <summary>Mark this player victorious. Idempotent: only transitions from Active.</summary>
    public bool SetWon()
    {
        if (!IsActive()) return false;
        State = PlayerState.Won;
        return true;
    }

    protected override void OnInit()
    {
        Wood = 300;
        Food = 300;
        Stone = 200;
        Metal = 100;
        PopUsed = 0;
        PopBonuses = 20;
        MaxPopCap = 300;
    }

    /// <summary>
    /// Effective pop limit = min(global cap, sum of building bonuses). Mirrors
    /// Player.js GetPopulationLimit. Kept as a computed property so it never drifts from
    /// PopBonuses/MaxPopCap. Legacy direct writes (SimBridge house +10) are routed through
    /// <see cref="AddPopulationBonus"/> / <see cref="PopulationLimitSetter"/> during migration.
    /// </summary>
    public int PopulationLimit
    {
        get => Math.Min(MaxPopCap, PopBonuses);
        // Setter preserved for SimBridge compatibility; folds into PopBonuses so the
        // computed getter still wins. Prefer AddPopulationBonus for new code.
        set => PopBonuses = value;
    }

    /// <summary>Amount of headroom remaining (never negative).</summary>
    public int PopHeadroom => Math.Max(0, PopulationLimit - PopUsed);

    public void AddPopulationBonus(int delta) => PopBonuses = Math.Max(0, PopBonuses + delta);

    public bool CanAfford(int wood, int food)
    {
        return Wood >= wood && Food >= food;
    }

    public bool CanAfford(int wood, int food, int stone, int metal)
    {
        return Wood >= wood && Food >= food && Stone >= stone && Metal >= metal;
    }

    public void Spend(int wood, int food)
    {
        Wood -= wood;
        Food -= food;
    }

    public void Spend(int wood, int food, int stone, int metal)
    {
        Wood -= wood;
        Food -= food;
        Stone -= stone;
        Metal -= metal;
    }

    /// <summary>全额退款(研究取消/生产取消;原版 RefundResources)。</summary>
    public void Refund(int wood, int food, int stone, int metal)
    {
        Wood += wood;
        Food += food;
        Stone += stone;
        Metal += metal;
    }

    public void AddResource(ResourceType type, int amount)
    {
        switch (type)
        {
            case ResourceType.Wood: Wood += amount; break;
            case ResourceType.Food: Food += amount; break;
            case ResourceType.Stone: Stone += amount; break;
            case ResourceType.Metal: Metal += amount; break;
        }
    }

    /// <summary>单资源余额判定(进贡/易物校验用)。</summary>
    public bool CanAfford(ResourceType type, int amount) => type switch
    {
        ResourceType.Wood => Wood >= amount,
        ResourceType.Food => Food >= amount,
        ResourceType.Stone => Stone >= amount,
        ResourceType.Metal => Metal >= amount,
        _ => false,
    };

    /// <summary>单资源扣减;余额不足则不改并返回 false(进贡/易物用)。</summary>
    public bool TrySpend(ResourceType type, int amount)
    {
        if (!CanAfford(type, amount)) return false;
        switch (type)
        {
            case ResourceType.Wood: Wood -= amount; break;
            case ResourceType.Food: Food -= amount; break;
            case ResourceType.Stone: Stone -= amount; break;
            case ResourceType.Metal: Metal -= amount; break;
        }
        return true;
    }

    /// <summary>进贡(原版 Player.js TributeResource):双方须 active、amount&gt;0、源余额足;
    /// 源 TrySpend + 目的 AddResource。任一不满足返回 false(执行器静默丢弃,对齐原版 notify)。</summary>
    public bool TributeResource(PlayerComponent dest, ResourceType type, int amount)
    {
        if (!IsActive() || !dest.IsActive() || amount <= 0) return false;
        if (!TrySpend(type, amount)) return false;
        dest.AddResource(type, amount);
        return true;
    }

    /// <summary>贸易品概率表(原版 Player.js tradingGoods:可贸易资源按步进 5 等概率,
    /// GUI 可调)。默认值活在字段初始化器;Deserialize 先清后填。</summary>
    public readonly List<KeyValuePair<ResourceType, int>> TradingGoods = new()
    {
        new(ResourceType.Food, 25),
        new(ResourceType.Wood, 25),
        new(ResourceType.Stone, 25),
        new(ResourceType.Metal, 25),
    };

    /// <summary>Port of Player.js GetNextTradingGoods:按概率表掷下一程贸易品。
    /// 用共享确定性 RNG(原版 randFloat(0,100))。</summary>
    public ResourceType GetNextTradingGoods(ComponentManager cm)
    {
        if (TradingGoods.Count == 0)
            return ResourceType.Metal;
        double value = cm.RNG.NextDouble() * 100.0;
        int last = TradingGoods.Count - 1;
        int sumProba = 0;
        for (int i = 0; i < last; ++i)
        {
            sumProba += TradingGoods[i].Value;
            if (value < sumProba)
                return TradingGoods[i].Key;
        }
        return TradingGoods[last].Key;
    }

    /// <summary>当前贸易品比例(4 资源百分比,GUI 贸易面板读)。缺省资源补 0。</summary>
    public Dictionary<ResourceType, int> GetTradingGoods()
    {
        var d = new Dictionary<ResourceType, int>();
        foreach (var (res, proba) in TradingGoods)
            d[res] = proba;
        foreach (ResourceType r in (ResourceType[])Enum.GetValues(typeof(ResourceType)))
            d.TryAdd(r, 0);
        return d;
    }

    /// <summary>设置贸易品比例(原版 Player.js SetTradingGoods):每值 ≥0 且和=100,否则拒(不变)。
    /// 按规范序 Food/Wood/Stone/Metal 重建列表(与默认初始化器一致,确定性;GetNextTradingGoods 依赖序)。</summary>
    public void SetTradingGoods(IReadOnlyDictionary<ResourceType, int> goods)
    {
        int sum = 0;
        foreach (var (_, pct) in goods)
        {
            if (pct < 0) return;
            sum += pct;
        }
        if (sum != 100) return;
        TradingGoods.Clear();
        TradingGoods.Add(new KeyValuePair<ResourceType, int>(ResourceType.Food,   goods.TryGetValue(ResourceType.Food, out var f) ? f : 0));
        TradingGoods.Add(new KeyValuePair<ResourceType, int>(ResourceType.Wood,   goods.TryGetValue(ResourceType.Wood, out var w) ? w : 0));
        TradingGoods.Add(new KeyValuePair<ResourceType, int>(ResourceType.Stone,  goods.TryGetValue(ResourceType.Stone, out var st) ? st : 0));
        TradingGoods.Add(new KeyValuePair<ResourceType, int>(ResourceType.Metal,  goods.TryGetValue(ResourceType.Metal, out var m) ? m : 0));
    }

    /// <summary>玩家是否可易物(原版 cmpPlayer.CanBarter):拥有至少一个 MarketComponent 建筑。
    /// <paramref name="playerId"/> = 玩家号(OwnershipComponent.PlayerId)。</summary>
    public bool CanBarter(ComponentManager cm, int playerId)
    {
        foreach (var eid in cm.AllEntities)
        {
            if (cm.QueryInterface<MarketComponent>(eid) == null) continue;
            var own = cm.QueryInterface<OwnershipComponent>(eid);
            if (own != null && own.PlayerId == playerId) return true;
        }
        return false;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("wood", Wood);
        s.NumberI32("food", Food);
        s.NumberI32("stone", Stone);
        s.NumberI32("metal", Metal);
        s.NumberI32("popUsed", PopUsed);
        s.NumberI32("popBonus", PopBonuses);
        s.NumberI32("popCap", MaxPopCap);
        s.NumberI32("state", (int)State);
        s.StringASCII("civ", Civ);
        s.NumberI32("team", Team);
        s.NumberI32("tradeGoods_n", TradingGoods.Count);
        foreach (var (goods, proba) in TradingGoods)
        {
            s.NumberI32("goods", (int)goods);
            s.NumberI32("proba", proba);
        }
        // 易物乘数(存档 v17):键序定序,值 float 位级。
        s.NumberI32("barterBuy_n", BarterMultiplierBuy.Count);
        foreach (var kv in BarterMultiplierBuy.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            s.StringASCII("bres", kv.Key);
            s.NumberFloat("bval", kv.Value);
        }
        s.NumberI32("barterSell_n", BarterMultiplierSell.Count);
        foreach (var kv in BarterMultiplierSell.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            s.StringASCII("sres", kv.Key);
            s.NumberFloat("sval", kv.Value);
        }
    }

    public override void Deserialize(IDeserializer d)
    {
        Wood = d.NumberI32("wood");
        Food = d.NumberI32("food");
        Stone = d.NumberI32("stone");
        Metal = d.NumberI32("metal");
        PopUsed = d.NumberI32("popUsed");
        PopBonuses = d.NumberI32("popBonus");
        MaxPopCap = d.NumberI32("popCap");
        State = (PlayerState)d.NumberI32("state");
        Civ = d.StringASCII("civ");
        Team = d.NumberI32("team");
        TradingGoods.Clear();
        int tn = d.NumberI32("tradeGoods_n");
        for (int i = 0; i < tn; i++)
            TradingGoods.Add(new KeyValuePair<ResourceType, int>(
                (ResourceType)d.NumberI32("goods"), d.NumberI32("proba")));
        // 易物乘数(存档 v17)。
        BarterMultiplierBuy.Clear();
        int bb = d.NumberI32("barterBuy_n");
        for (int i2 = 0; i2 < bb; i2++)
            BarterMultiplierBuy[d.StringASCII("bres")] = d.NumberFloat("bval");
        BarterMultiplierSell.Clear();
        int bs = d.NumberI32("barterSell_n");
        for (int i3 = 0; i3 < bs; i3++)
            BarterMultiplierSell[d.StringASCII("sres")] = d.NumberFloat("sval");

    }

    public void HandleMessage(IMessage message) { }
}

/// <summary>Player win/loss state. Ported from Player.js STATE_* constants.
/// Mono-directional: only Active can transition to Defeated or Won.</summary>
public enum PlayerState
{
    Active = 0,
    Defeated = 1,
    Won = 2,
}

[Component("Identity", "Identity")]
public sealed class IdentityComponent : ComponentBase, IComponentMessageHandler
{
    public string Name = "Entity";
    public string TemplateName = "";
    public bool IsUnit = true;
    public bool IsBuilding;
    /// <summary>模板 Identity/Undeletable(原版 Identity.js IsUndeletable):不可被
    /// delete 命令自毁(英雄棺椁/阵型控制器/gaia 等);HUD 删除钮禁用态据此。</summary>
    public bool Undeletable;
    public List<string> Classes = new();

    protected override void OnInit() { }

    public bool HasClass(string className) => Classes.Contains(className);

    public bool MatchesClassList(string match) =>
        Content.EntityClassHelper.EntityMatchesClassList(Classes, match);

    public override void Serialize(ISerializer s)
    {
        s.StringASCII("name", Name);
        s.StringASCII("tmpl", TemplateName);
        s.Bool("unit", IsUnit);
        s.Bool("building", IsBuilding);
        s.Bool("undel", Undeletable);
        s.NumberI32("classCount", Classes.Count);
        foreach (var c in Classes)
            s.StringASCII("cls", c);
    }

    public override void Deserialize(IDeserializer d)
    {
        Name = d.StringASCII("name");
        TemplateName = d.StringASCII("tmpl");
        IsUnit = d.Bool("unit");
        IsBuilding = d.Bool("building");
        Undeletable = d.Bool("undel");
        int count = d.NumberI32("classCount");
        Classes.Clear();
        for (int i = 0; i < count; i++)
            Classes.Add(d.StringASCII("cls"));
    }

    public void HandleMessage(IMessage message) { }
}
