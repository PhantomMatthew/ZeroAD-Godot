using System;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// Treasure + TreasureCollector — ports of Treasure.js / TreasureCollector.js.
// A treasure is a gaia pickup: a collector stands within MaxDistance for CollectTime seconds,
// then the treasure grants its resources to the collector's owner and destroys itself.
// 略:Fogging.Activate、StatisticsTracker、Trigger 事件(表现/统计层)。
//
// Original collector timer = SetTimeout(CollectionTime) one-shot with availability+range
// checks at fire; the port accumulates dt in Tick (driven by UnitAI's COLLECTTREASURE.
// COLLECTING state) with the same checks at the fire point.

[Component("Treasure", "Treasure")]
public sealed class TreasureComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>template Treasure/CollectTime(毫秒 → 秒)。</summary>
    public float CollectTimeSec = 1f;
    // template Treasure/Resources/{Food,Wood,Stone,Metal}(基值,结算时过修正值管线)。
    public int Food;
    public int Wood;
    public int Stone;
    public int Metal;
    public bool IsTaken;

    public float CollectionTime() => CollectTimeSec;
    public bool IsAvailable() => !IsTaken;

    /// <summary>Port of Treasure.js Reward:给收集者所属玩家发资源并销毁自己。
    /// False = 已取走或收集者无所属玩家。</summary>
    public bool Reward(ComponentManager cm, EntityId collector)
    {
        if (IsTaken)
            return false;
        var own = cm.QueryInterface<OwnershipComponent>(collector);
        if (own == null || own.PlayerId < 1)
            return false;
        var player = cm.GetPlayerEntity(own.PlayerId);
        if (player == null)
            return false;

        // 原版 ComputeReward 用 ApplyValueModificationsToEntity("Treasure/Resources/X");
        // 结算时查询等价(初始化即取与取值改动通知的唯一区别是缓存时机,结果相同)。
        GrantModified(cm, player, ResourceType.Food, Food);
        GrantModified(cm, player, ResourceType.Wood, Wood);
        GrantModified(cm, player, ResourceType.Stone, Stone);
        GrantModified(cm, player, ResourceType.Metal, Metal);

        IsTaken = true;
        cm.DestroyEntity(Entity);
        return true;
    }

    private void GrantModified(ComponentManager cm, PlayerComponent player, ResourceType type, int baseAmount)
    {
        if (baseAmount == 0)
            return;
        float modified = cm.Modifiers.ApplyPrefix("Treasure/Resources/" + type, baseAmount, Entity);
        player.AddResource(type, (int)MathF.Round(modified, MidpointRounding.AwayFromZero));
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("collectTime", Maths.Fixed.FromFloat(CollectTimeSec));
        s.NumberI32("food", Food);
        s.NumberI32("wood", Wood);
        s.NumberI32("stone", Stone);
        s.NumberI32("metal", Metal);
        s.Bool("taken", IsTaken);
    }

    public override void Deserialize(IDeserializer d)
    {
        CollectTimeSec = d.NumberFixed("collectTime").ToFloat();
        Food = d.NumberI32("food");
        Wood = d.NumberI32("wood");
        Stone = d.NumberI32("stone");
        Metal = d.NumberI32("metal");
        IsTaken = d.Bool("taken");
    }

    public void HandleMessage(IMessage message) { }
}

/// <summary>Outcome of one <see cref="TreasureCollectorComponent.Tick"/> — UnitAI FSM transitions.</summary>
public enum CollectTickResult
{
    Idle,
    Collecting,
    /// <summary> Treasure 已被取走/销毁。</summary>
    TargetInvalid,
    /// <summary>结算时不在射程内。</summary>
    OutOfRange,
    /// <summary>结算完成(资源已发,宝物已销毁)。</summary>
    Done,
}

[Component("TreasureCollector", "TreasureCollector")]
public sealed class TreasureCollectorComponent : ComponentBase, IComponentMessageHandler
{
    public float MaxDistance = 5f;   // template TreasureCollector/MaxDistance — collection radius
    public EntityId? Treasure;       // runtime: entity being collected
    public float CollectTime = 1f;   // runtime: captured from the treasure at StartCollecting
    public float Elapsed;            // runtime: progress 0..CollectTime

    /// <summary>Port of TreasureCollector.js CanCollect.</summary>
    public bool CanCollect(ComponentManager cm, EntityId target) =>
        cm.QueryInterface<TreasureComponent>(target)?.IsAvailable() == true;

    /// <summary>Port of StartCollecting(callerIID/visual 略)。False = 目标不可取。</summary>
    public bool StartCollecting(ComponentManager cm, EntityId target)
    {
        if (Treasure != null)
            StopCollecting();
        var treasure = cm.QueryInterface<TreasureComponent>(target);
        if (treasure == null || !treasure.IsAvailable())
            return false;
        Treasure = target;
        CollectTime = treasure.CollectionTime();
        Elapsed = 0f;
        return true;
    }

    /// <summary>Port of StopCollecting(reason/callerIID 通知略——UnitAI 由 Tick 返回值驱动)。</summary>
    public void StopCollecting()
    {
        Treasure = null;
    }

    /// <summary>Advance collection; fires once Elapsed ≥ CollectTime with the original's
    /// availability + range checks at the fire point.</summary>
    public CollectTickResult Tick(float dt, ComponentManager cm)
    {
        if (Treasure is not { } target)
            return CollectTickResult.Idle;
        Elapsed += dt;
        if (Elapsed < CollectTime)
            return CollectTickResult.Collecting;

        var treasure = cm.QueryInterface<TreasureComponent>(target);
        if (treasure == null || !treasure.IsAvailable())
        {
            StopCollecting();
            return CollectTickResult.TargetInvalid;
        }
        if (!IsTargetInRange(cm, target))
        {
            StopCollecting();
            return CollectTickResult.OutOfRange;
        }
        treasure.Reward(cm, Entity);
        StopCollecting();
        return CollectTickResult.Done;
    }

    /// <summary>Port of IsTargetInRange — edge-to-edge(中心距 − 目标障碍半径 ≤ MaxDistance)。</summary>
    public bool IsTargetInRange(ComponentManager cm, EntityId target)
    {
        var a = cm.QueryInterface<PositionComponent>(Entity);
        var b = cm.QueryInterface<PositionComponent>(target);
        if (a == null || b == null)
            return false;
        var dx = a.Position.X - b.Position.X;
        var dz = a.Position.Z - b.Position.Z;
        long d2 = (long)dx.InternalValue * dx.InternalValue
                + (long)dz.InternalValue * dz.InternalValue;
        var eff = Maths.Fixed.FromFloat(MaxDistance);
        var obs = cm.QueryInterface<ObstructionComponent>(target);
        if (obs != null)
            eff += obs.GetSize();
        long r2 = (long)eff.InternalValue * eff.InternalValue;
        return d2 <= r2;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("maxDistance", Maths.Fixed.FromFloat(MaxDistance));
        s.NumberU32("treasure", Treasure?.Value ?? 0);
        s.NumberFixed("collectTime", Maths.Fixed.FromFloat(CollectTime));
        s.NumberFixed("elapsed", Maths.Fixed.FromFloat(Elapsed));
    }

    public override void Deserialize(IDeserializer d)
    {
        MaxDistance = d.NumberFixed("maxDistance").ToFloat();
        uint t = d.NumberU32("treasure");
        Treasure = t != 0 ? new EntityId(t) : null;
        CollectTime = d.NumberFixed("collectTime").ToFloat();
        Elapsed = d.NumberFixed("elapsed").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}
