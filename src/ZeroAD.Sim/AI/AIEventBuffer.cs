using System.Collections.Generic;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Events;

namespace ZeroAD.Sim.AI;

/// <summary>AI 事件类型（对齐原版 state.events 的分类）。</summary>
public enum AIEventType
{
    Create,              // 实体创建（EntityCreated）
    Destroy,             // 实体销毁（EntityDestroyed）
    TrainingFinished,    // 训练完成
    ConstructionFinished, // 建造完成（StructureBuilt）
    OwnershipChanged,    // 换主（含占领）
    PlayerDefeated,      // 玩家被击败
}

/// <summary>单条 AI 事件（最小化载荷，按类型解释字段）。</summary>
public readonly struct AIEvent
{
    public readonly AIEventType Type;
    public readonly uint Entity;       // 涉及的实体 ID（Create/Destroy/Train/Build/Ownership）
    public readonly uint TemplateHash; // 模板哈希（Create/Train/Build 时用于快速分类；0=无）
    public readonly int IntParam;      // 多义：OwnershipChanged=fromPlayer; Defeated=playerId; Train/Build=ownerId
    public readonly int IntParam2;     // OwnershipChanged=toPlayer

    public AIEvent(AIEventType type, uint entity, int p1 = 0, int p2 = 0, uint tmplHash = 0)
        => (Type, Entity, IntParam, IntParam2, TemplateHash) = (type, entity, p1, p2, tmplHash);
}

/// <summary>AI 事件缓冲。订阅 SimEventBus + ComponentManager.EntityDestroyed，
/// 按 think 间隔聚合事件。每个 AIComponent 持有一个；think 前提供 Events，think 后 Drain。
///
/// Petra 的各 manager.checkEvents(gameState, events) 消费此列表。
/// 事件是 per-turn 派生态（不序列化）——各端同跑同生成，确定性保证。</summary>
public sealed class AIEventBuffer
{
    private readonly List<AIEvent> _events = new();
    private ComponentManager? _cm;
    private bool _subscribed;

    /// <summary>订阅事件源。由 AIComponent.Configure 调（cm/net 注入后）。</summary>
    public void Attach(ComponentManager cm)
    {
        if (_subscribed) return;
        _subscribed = true;
        _cm = cm;
        var ev = cm.Events;
        ev.EntityCreated += OnCreated;
        ev.TrainingFinished += OnTrained;
        ev.StructureBuilt += OnBuilt;
        ev.OwnershipChanged += OnOwnership;
        ev.PlayerDefeated += OnDefeated;
        cm.EntityDestroyed += OnDestroyed;
    }

    public void Detach(ComponentManager cm)
    {
        if (!_subscribed) return;
        _subscribed = false;
        _cm = null;
        var ev = cm.Events;
        ev.EntityCreated -= OnCreated;
        ev.TrainingFinished -= OnTrained;
        ev.StructureBuilt -= OnBuilt;
        ev.OwnershipChanged -= OnOwnership;
        ev.PlayerDefeated -= OnDefeated;
        cm.EntityDestroyed -= OnDestroyed;
    }

    /// <summary>当前回合的事件列表（think 期间只读访问）。</summary>
    public IReadOnlyList<AIEvent> Events => _events;

    /// <summary>think 结束后清空（下一回合重新积累）。</summary>
    public void Drain() => _events.Clear();

    // ── 事件回调（转换到 AIEvent）──

    private void OnCreated(EntityCreatedEvent e)
        => _events.Add(new AIEvent(AIEventType.Create, e.Entity.Value, e.OwnerPlayerId, 0));

    private void OnDestroyed(EntityId e)
        => _events.Add(new AIEvent(AIEventType.Destroy, e.Value));

    private void OnTrained(TrainingFinishedEvent e)
    {
        var own = _cm?.QueryInterface<OwnershipComponent>(e.TrainerEntity);
        _events.Add(new AIEvent(AIEventType.TrainingFinished, e.TrainerEntity.Value, own?.PlayerId ?? 0));
    }

    private void OnBuilt(StructureBuiltEvent e)
    {
        var own = _cm?.QueryInterface<OwnershipComponent>(e.Building);
        _events.Add(new AIEvent(AIEventType.ConstructionFinished, e.Building.Value, own?.PlayerId ?? 0));
    }

    private void OnOwnership(OwnershipChangedEvent e)
        => _events.Add(new AIEvent(AIEventType.OwnershipChanged, e.Entity.Value, e.From, e.To));

    private void OnDefeated(PlayerDefeatedEvent e)
        => _events.Add(new AIEvent(AIEventType.PlayerDefeated, 0, e.PlayerId));
}
