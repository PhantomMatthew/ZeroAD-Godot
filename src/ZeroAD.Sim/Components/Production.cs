using System;
using System.Collections.Generic;
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
            Count = count
        });
    }

    /// <summary>
    /// Deterministic training entry point. Resolves the trainer's owner, reads real template
    /// cost/build-time/pop-cost/category, validates affordability + entity limits + pop headroom,
    /// then charges the player and enqueues. Returns false (no side effects) on any validation
    /// failure. This is the single source of truth shared by SimBridge.CommandTrain and
    /// NetTurnManager.ExecuteCommand so single-player and lockstep multiplayer agree exactly.
    /// </summary>
    public bool EnqueueTraining(string templateName, int count, ComponentManager cm)
    {
        if (count <= 0) return false;
        if (cm.Templates == null) return false;

        var owner = cm.QueryInterface<OwnershipComponent>(Entity);
        if (owner == null) return false;

        var stats = cm.Templates.ExtractStats(templateName);
        int totalWood = stats.WoodCost * count;
        int totalFood = stats.FoodCost * count;
        int totalStone = stats.StoneCost * count;
        int totalMetal = stats.MetalCost * count;

        // Find the owner's PlayerComponent. Player entities are registered via ComponentManager's
        // player map (set up by the presentation layer at world init); resolve through it.
        var player = cm.GetPlayerEntity(owner.PlayerId);
        if (player == null) return false;
        // A defeated player can't train units.
        if (player.IsDefeated()) return false;

        if (!player.CanAfford(totalWood, totalFood, totalStone, totalMetal)) return false;

        // Pop headroom check (pop is charged immediately on spawn, but we pre-validate to avoid
        // charging resources for a unit that can never appear).
        int popCost = stats.PopulationCost * count;
        if (popCost > player.PopHeadroom) return false;

        // Entity limits (category caps like "Hero: 1").
        EntityLimitsComponent? limits = null;
        EntityId? playerEid = cm.GetPlayerEntityId(owner.PlayerId);
        if (playerEid is { } pe)
            limits = cm.QueryInterface<EntityLimitsComponent>(pe);
        if (limits != null && !limits.AllowedToTrain(stats.TrainingCategory, count))
            return false;

        // 训练时间过修正值管线(科技如 "Cost/BuildTime" ×0.9;单位未出生,走模板查询)
        float buildTime = stats.BuildTime;
        if (playerEid.HasValue)
            buildTime = cm.Modifiers.ApplyTemplate(
                "Cost/BuildTime", stats.BuildTime, stats.GetClassList(), playerEid.Value);

        // All checks passed — commit.
        player.Spend(totalWood, totalFood, totalStone, totalMetal);
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
    /// Advance the queue by <paramref name="dt"/> seconds. When the head item completes, spawns
    /// <see cref="ProductionItem.Count"/> entities via <see cref="ComponentManager.SpawnEntity"/>
    /// (sim-owned, deterministic), applies the trainer's rally point, and raises
    /// TrainingFinished. This replaces the legacy SimBridge spawn-after-tick path so the whole
    /// train→spawn loop is replayable and OOS-safe.
    /// </summary>
    public void Tick(float dt, ComponentManager cm)
    {
        if (_queue.Count == 0) return;

        var current = _queue[0];
        _progress += dt;

        if (_progress < current.BuildTime) return;

        // Head item finished: spawn Count units around the trainer, then dequeue.
        _queue.RemoveAt(0);
        _progress = 0;

        var trainerPos = cm.QueryInterface<PositionComponent>(Entity);
        var owner = cm.QueryInterface<OwnershipComponent>(Entity);
        var rally = cm.QueryInterface<RallyPointComponent>(Entity);
        int ownerId = owner?.PlayerId ?? -1;

        if (trainerPos == null)
        {
            // No position to spawn at — still notify so GUI can react, but no entities appear.
            cm.Events.RaiseTrainingFinished(new Events.TrainingFinishedEvent
            {
                TrainerEntity = Entity,
                UnitTemplate = current.TemplateName
            });
            return;
        }

        float baseX = trainerPos.Position.X.ToFloat();
        float baseZ = trainerPos.Position.Z.ToFloat();
        // Footprint-driven spawn: ask the trainer's FootprintComponent for a free slot just outside
        // its footprint, validated by the Pathfinder so the unit doesn't appear on water / inside
        // another building. Falls back to a simple ring if no Footprint or no free slot is found.
        var footprint = cm.QueryInterface<FootprintComponent>(Entity);
        Fixed spawnedRadius = Fixed.FromFloat(1.0f);

        for (int i = 0; i < current.Count; i++)
        {
            float sx, sz;
            var spawn = footprint?.PickSpawnPoint(spawnedRadius);
            if (spawn != null && spawn.Value.X.ToFloat() >= 0)
            {
                sx = spawn.Value.X.ToFloat();
                sz = spawn.Value.Z.ToFloat();
            }
            else
            {
                // Fallback: golden-angle ring around the trainer (keeps training working even when
                // the area is crowded — units may overlap, but they'll disperse via UnitMotion).
                float angle = i * 2.4f;
                float radius = 6f + (i / 6) * 3f;
                sx = baseX + MathF.Cos(angle) * radius;
                sz = baseZ + MathF.Sin(angle) * radius;
            }
            var spawned = cm.SpawnEntity(current.TemplateName, sx, sz, ownerId);

            // Rally point: issue a real Walk order through UnitAI. A raw UnitMotion
            // MoveToPoint only sets the motion goal and leaves the FSM in IDLE, so the
            // freshly-trained unit glides to the rally without a walk animation
            // (ResolveAnimationState keys off the FSM state). Walk pushes Order.Walk,
            // which StartMovingTo-s the destination AND transitions to WALKING —
            // matching the original ProductionQueue issuing a Walk order on spawn.
            if (rally != null && !rally.Position.IsZero)
            {
                var ai = cm.QueryInterface<UnitAIComponent>(spawned);
                ai?.Walk(new Maths.FixedVector2D(rally.Position.X, rally.Position.Y));
            }
        }

        cm.Events.RaiseTrainingFinished(new Events.TrainingFinishedEvent
        {
            TrainerEntity = Entity,
            UnitTemplate = current.TemplateName
        });
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("count", _queue.Count);
        s.NumberFixed("progress", ZeroAD.Sim.Maths.Fixed.FromFloat(_progress));
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
    public int Count = 1;
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
    public int PopUsed;
    /// <summary>Sum of PopulationComponent.Bonus across the player's buildings.</summary>
    public int PopBonuses;
    /// <summary>Hard global cap (0 A.D. default 300).</summary>
    public int MaxPopCap = 300;

    /// <summary>文明代码(athen/spart/...),驱动科技 requirements {civ} 判定与 civ 加成。
    /// 默认 athen(与 SimBridge 当前硬编码一致);gamesetup GUI 落地后由开局参数注入。</summary>
    public string Civ = "athen";

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
        s.NumberI32("tradeGoods_n", TradingGoods.Count);
        foreach (var (goods, proba) in TradingGoods)
        {
            s.NumberI32("goods", (int)goods);
            s.NumberI32("proba", proba);
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
        TradingGoods.Clear();
        int tn = d.NumberI32("tradeGoods_n");
        for (int i = 0; i < tn; i++)
            TradingGoods.Add(new KeyValuePair<ResourceType, int>(
                (ResourceType)d.NumberI32("goods"), d.NumberI32("proba")));
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
        int count = d.NumberI32("classCount");
        Classes.Clear();
        for (int i = 0; i < count; i++)
            Classes.Add(d.StringASCII("cls"));
    }

    public void HandleMessage(IMessage message) { }
}
