using System.Collections.Generic;
using ZeroAD.Sim.AI;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// UnitAI — the unit command/state machine. Ported from
// binaries/data/mods/public/simulation/components/UnitAI.js (6842 lines).
//
// In the original, UnitAI is a hierarchical FSM (via globalscripts/FSM.js) that owns the
// order queue and drives every unit behaviour: walk, gather, attack, repair, heal, trade,
// garrison, pack, patrol, guard, etc. Each order pushes onto a queue; the front order is
// the active one and its state machine handles arrival/target-lost/capacity transitions.
//
// This C# port uses the FSM engine (AI/Fsm.cs) for the same hierarchical structure. Scope for
// MS3/P0: the full INDIVIDUAL state tree compiles and the core loop (Walk→Gather→ReturnResource,
// Attack→COMBAT, Repair→REPAIR) runs end-to-end. P1 behaviours (Trade/Pack/Garrison/Formation/
// Heal/Treasure/Patrol/Guard) are wired through the FSM but delegate to stub components.
//
// Command routing: SimBridge.Command* and NetTurnManager.ExecuteCommand both converge here.
// Each public Walk/Gather/Attack/... method enqueues an Order; the FSM's Order.X handlers
// accept or reject it and set the active state.

/// <summary>An order in the UnitAI queue. Mirrors UnitAI.js order {type, data}.</summary>
public sealed record UnitOrder
{
    public string Type = "";
    public EntityId? Target;
    public FixedVector2D Position;
    public bool Force;          // queued even if it can't run immediately
    public bool Queued;         // add to back of queue (false = replace queue)
}

[Component("UnitAI", "UnitAI")]
public sealed class UnitAIComponent : ComponentBase, IComponentMessageHandler, IFsmHost
{
    // --- Order queue (front = active order) ---
    private readonly LinkedList<UnitOrder> _orderQueue = new();

    // FSM state name + pending next-state. Serialized so OOS hashing covers AI state.
    public string FsmStateName { get; set; } = "";
    public string? FsmNextState { get; set; }

    /// <summary>Stance controls auto-acquire behaviour (aggressive/defensive/passive/...).
    /// P0 uses aggressive defaults; full stance logic is P1.</summary>
    public string Stance { get; private set; } = "aggressive";

    /// <summary>True when the order queue is empty and the unit is in IDLE.</summary>
    public bool IsIdle => _orderQueue.Count == 0;

    // The compiled FSM is shared across all UnitAI instances (one per process).
    private static readonly Fsm<UnitAIComponent, FsmMessage> s_fsm = BuildFsm();

    public UnitAIComponent()
    {
        // Enter the initial state (INDIVIDUAL.IDLE for standalone units).
        s_fsm.Init(this, "INDIVIDUAL.IDLE");
    }

    public void OnFsmStateChanged(string stateName) { /* presentation layer can hook here later */ }

    // =========================================================================
    // Public command surface — called by SimBridge.Command* / NetTurnManager.
    // Each mirrors a UnitAI.js AddOrder variant: queue front (replace) or back (append).
    // =========================================================================

    /// <summary>Move to a point. Mirrors UnitAI.Walk(x,z,queued).</summary>
    public void Walk(FixedVector2D target, bool queued = false)
    {
        PushOrder(new UnitOrder { Type = "Walk", Position = target, Queued = queued });
    }

    /// <summary>Gather from a resource supply. Mirrors UnitAI.Gather(target,queued).</summary>
    public void Gather(EntityId target, bool queued = false)
    {
        PushOrder(new UnitOrder { Type = "Gather", Target = target, Queued = queued });
    }

    /// <summary>Attack a target. Mirrors UnitAI.Attack(target,queued).</summary>
    public void Attack(EntityId target, bool queued = false)
    {
        PushOrder(new UnitOrder { Type = "Attack", Target = target, Queued = queued });
    }

    /// <summary>Repair / build a foundation. Mirrors UnitAI.Repair(target,queued).</summary>
    public void Repair(EntityId target, bool queued = false)
    {
        PushOrder(new UnitOrder { Type = "Repair", Target = target, Queued = queued });
    }

    /// <summary>Cancel all orders and stop. Mirrors UnitAI.Stop(queued).</summary>
    public void Stop()
    {
        _orderQueue.Clear();
        _dispatchPending = false;
        // Order.Stop needs no ComponentManager (it just stops motion + returns to IDLE), so
        // dispatch it immediately.
        s_fsm.ProcessMessage(this, new FsmMessage { Type = "Stop" }, "Order.Stop");
    }

    // --- P1 order entry points (wire through, delegate to stubs) ---
    public void Garrison(EntityId holder, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "Garrison", Target = holder, Queued = queued });
    public void Heal(EntityId target, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "Heal", Target = target, Queued = queued });
    public void Trade(EntityId? market, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "Trade", Target = market, Queued = queued });
    public void Pack() => PushOrder(new UnitOrder { Type = "Pack" });
    public void Unpack() => PushOrder(new UnitOrder { Type = "Unpack" });

    // =========================================================================
    // Order queue mechanics — port of UnitAI PushOrder / PushOrderFront / FinishOrder.
    // =========================================================================

    private void PushOrder(UnitOrder order)
    {
        if (!order.Queued && _orderQueue.Count > 0)
        {
            // Replace: clear the queue, then add the new order as the sole item.
            _orderQueue.Clear();
        }
        _orderQueue.AddLast(order);
        // The Order.<Type> FSM handler runs on the next Tick (which has the ComponentManager
        // the handlers need for component lookups). Mark that a dispatch is pending.
        _dispatchPending = true;
    }

    private bool _dispatchPending;

    private void FinishOrder()
    {
        if (_orderQueue.Count > 0)
            _orderQueue.RemoveFirst();
        // The next order (if any) is dispatched on the next Tick, which has the ComponentManager.
        _dispatchPending = _orderQueue.Count > 0;
        if (_orderQueue.Count == 0)
            s_fsm.SetNextState(this, "IDLE");
    }

    // Feed the front order into the FSM as an Order.<Type> message. Called from Tick (which
    // owns the ComponentManager the handlers need). The FSM's Order handler for the current
    // state decides whether to accept (and transition) or reject (FinishOrder).
    private void DispatchFrontOrder(ComponentManager cm)
    {
        if (_orderQueue.First is not { } node) return;
        var order = node.Value;
        s_fsm.ProcessMessage(this, new FsmMessage { Type = order.Type, Order = order, Cm = cm }, "Order." + order.Type);
        _dispatchPending = false;
    }

    /// <summary>Current order (front of queue), or null if idle.</summary>
    public UnitOrder? CurrentOrder => _orderQueue.First?.Value;

    // =========================================================================
    // Tick — driven once per sim turn by the presentation layer.
    // =========================================================================

    public void Tick(float dt, ComponentManager cm)
    {
        // Dispatch any newly-queued order first (the Order.X handler sets the active state).
        if (_dispatchPending)
            DispatchFrontOrder(cm);

        // Then let the FSM handle periodic checks via a Timer-style message. Per-state handlers
        // advance the active order (move-arrival polling, gather progress, attack cycles).
        if (!IsIdle || _orderQueue.Count > 0)
            s_fsm.ProcessMessage(this, new FsmMessage { Type = "Tick", Dt = dt, Cm = cm }, "Timer");
    }

    // =========================================================================
    // FSM spec — hierarchical state tree. Port of UnitAI.prototype.UnitFsmSpec.
    // P0 implements the INDIVIDUAL tree fully for the core loop; FORMATIONCONTROLLER and
    // FORMATIONMEMBER are stubbed (formation logic is P1).
    // =========================================================================

    private static Fsm<UnitAIComponent, FsmMessage> BuildFsm()
    {
        var spec = FsmSpec<UnitAIComponent, FsmMessage>.Create();
        BuildIndividualTree(spec);
        BuildFormationControllerTree(spec);
        BuildFormationMemberTree(spec);
        return spec.Build();
    }

    private static void BuildIndividualTree(FsmSpec<UnitAIComponent, FsmMessage> spec)
    {
        var ind = spec.State("INDIVIDUAL");

        // --- Order handlers (accept and transition). Defined at INDIVIDUAL so they apply in
        // every substate unless overridden (matches JS inheritance). ---

        ind.On("Order.Walk", (u, m) =>
        {
            StartMovingTo(u, m.Order!.Position, m.Cm!);
            u.FsmNextState = "WALKING";
        });

        ind.On("Order.Gather", (u, m) =>
        {
            var gatherer = m.Cm!.QueryInterface<ResourceGatherer>(u.Entity);
            if (gatherer == null) { u.FsmNextState = "IDLE"; return; }
            if (m.Order!.Target is { } target)
            {
                gatherer.TargetSupply = target;
                MoveToTargetEdge(u, target, m.Cm!, Fixed.FromInt(1));
            }
            u.FsmNextState = "GATHER.APPROACHING";
        });

        ind.On("Order.Attack", (u, m) =>
        {
            // 拒收路径一律 FinishOrder 出队(对齐原版):仅置 IDLE 会让订单残留队列,
            // 同 Tick 的 Timer 在无 handler 的 IDLE 态抛出。
            var attack = m.Cm!.QueryInterface<AttackComponent>(u.Entity);
            if (attack == null || m.Order!.Target == null) { u.FinishOrder(); return; }
            // 敌对校验(对齐原版 CanAttack):self/盟友/中立目标拒收;无外交数据默认=敌,
            // 无 OwnershipComponent 的目标(gaia 资源等)不拦。
            var own = m.Cm.QueryInterface<OwnershipComponent>(u.Entity);
            var targetOwn = m.Cm.QueryInterface<OwnershipComponent>(m.Order.Target.Value);
            if (own != null && targetOwn != null
                && !m.Cm.Players.IsEnemy(own.PlayerId, targetOwn.PlayerId))
            {
                u.FinishOrder();
                return;
            }
            attack.AttackTarget(m.Order.Target.Value);
            u.FsmNextState = "COMBAT.APPROACHING";
        });

        ind.On("Order.Repair", (u, m) =>
        {
            var builder = m.Cm!.QueryInterface<BuilderComponent>(u.Entity);
            if (builder == null || m.Order!.Target == null) { u.FsmNextState = "IDLE"; return; }
            builder.Build(m.Order.Target.Value);
            MoveToTarget(u, m.Order.Target.Value, m.Cm!);
            u.FsmNextState = "REPAIR.APPROACHING";
        });

        ind.On("Order.Stop", (u, _) => { StopMoving(u); u.FsmNextState = "IDLE"; });

        // P1 orders — accepted, transition to stub states.
        ind.On("Order.Garrison", (u, m) =>
        {
            if (m.Order!.Target is { } holder)
                m.Cm!.QueryInterface<GarrisonableComponent>(u.Entity)?.Garrison(holder);
            u.FsmNextState = "GARRISON.APPROACHING";
        });
        ind.On("Order.Heal", (u, m) =>
        {
            if (m.Order!.Target is { } t)
                m.Cm!.QueryInterface<HealComponent>(u.Entity)?.StartHealing(t);
            u.FsmNextState = "HEAL";
        });
        ind.On("Order.Trade", (u, m) =>
        {
            if (m.Order!.Target is { } market)
                m.Cm!.QueryInterface<TraderComponent>(u.Entity)?.SetFirstMarket(market);
            u.FsmNextState = "TRADE";
        });
        ind.On("Order.Pack", (u, m) =>
        {
            m.Cm!.QueryInterface<PackComponent>(u.Entity)?.Pack();
            u.FsmNextState = "PACKING";
        });
        ind.On("Order.Unpack", (u, m) =>
        {
            m.Cm!.QueryInterface<PackComponent>(u.Entity)?.Unpack();
            u.FsmNextState = "UNPACKING";
        });

        // --- States ---

        spec.State("INDIVIDUAL").State("IDLE");

        var walking = spec.State("INDIVIDUAL").State("WALKING");
        walking.On("Timer", (u, m) =>
        {
            var motion = m.Cm!.QueryInterface<UnitMotion>(u.Entity);
            if (motion != null && !motion.HasMoveTarget)
                u.FinishOrder();   // arrived → next order or IDLE
        });

        BuildCombatSubtree(spec);
        BuildGatherSubtree(spec);
        BuildRepairSubtree(spec);

        // P1 states (reachable, no-op on Tick beyond finishing immediately for demo).
        spec.State("INDIVIDUAL").State("GARRISON").State("APPROACHING")
            .On("Timer", (u, _) => u.FinishOrder());
        spec.State("INDIVIDUAL").State("HEAL")
            .On("Timer", (u, _) => u.FinishOrder());
        spec.State("INDIVIDUAL").State("TRADE")
            .On("Timer", (u, _) => u.FinishOrder());
        spec.State("INDIVIDUAL").State("PACKING")
            .On("Timer", (u, _) => u.FinishOrder());
        spec.State("INDIVIDUAL").State("UNPACKING")
            .On("Timer", (u, _) => u.FinishOrder());
        spec.State("INDIVIDUAL").State("PATROL");
        spec.State("INDIVIDUAL").State("GUARD");
        spec.State("INDIVIDUAL").State("FLEEING");
        spec.State("INDIVIDUAL").State("RETURNRESOURCE");
        spec.State("INDIVIDUAL").State("COLLECTTREASURE");
        spec.State("INDIVIDUAL").State("CHEERING");
        spec.State("INDIVIDUAL").State("WALKINGANDFIGHTING");
        spec.State("INDIVIDUAL").State("PICKUP");
    }

    private static void BuildCombatSubtree(FsmSpec<UnitAIComponent, FsmMessage> spec)
    {
        // COMBAT.APPROACHING — move toward target until in range, then attack.
        spec.State("INDIVIDUAL").State("COMBAT").State("APPROACHING")
            .On("Timer", (u, m) =>
            {
                var attack = m.Cm!.QueryInterface<AttackComponent>(u.Entity);
                if (attack?.Target == null) { u.FinishOrder(); return; }
                attack.Tick(m.Dt, m.Cm!);
                if (attack.State == AttackComponent.AttackState.Attacking)
                    u.FsmNextState = "COMBAT.ATTACKING";
            });

        // COMBAT.ATTACKING — in range; let AttackComponent run its cycle.
        spec.State("INDIVIDUAL").State("COMBAT").State("ATTACKING")
            .On("Timer", (u, m) =>
            {
                var attack = m.Cm!.QueryInterface<AttackComponent>(u.Entity);
                if (attack?.Target == null) { u.FinishOrder(); return; }
                attack.Tick(m.Dt, m.Cm!);
                if (attack.State == AttackComponent.AttackState.Approaching)
                    u.FsmNextState = "COMBAT.APPROACHING";
            });

        spec.State("INDIVIDUAL").State("COMBAT").State("FINDINGNEWTARGET");
        spec.State("INDIVIDUAL").State("COMBAT").State("CHASING");
    }

    private static void BuildGatherSubtree(FsmSpec<UnitAIComponent, FsmMessage> spec)
    {
        // GATHER.APPROACHING — moving to resource; hand off to gatherer once in range.
        // The move target is the supply's exact centre, which is unreachable (trees/
        // rocks have obstructions), so we also transition on a proximity check instead
        // of waiting for HasMoveTarget to clear (it never does for obstructed targets).
        spec.State("INDIVIDUAL").State("GATHER").State("APPROACHING")
            .On("Timer", (u, m) =>
            {
                var gatherer = m.Cm!.QueryInterface<ResourceGatherer>(u.Entity);
                if (gatherer == null || gatherer.TargetSupply == null) { u.FinishOrder(); return; }
                var motion = m.Cm!.QueryInterface<UnitMotion>(u.Entity);
                bool arrived = motion != null && !motion.HasMoveTarget;
                if (!arrived)
                    arrived = WithinRange(u.Entity, gatherer.TargetSupply.Value, m.Cm!, GatherRange);
                if (arrived)
                {
                    StopMoving(u);
                    gatherer.State = ResourceGatherer.GatherState.Gathering;
                    u.FsmNextState = "GATHER.GATHERING";
                }
            });


        // GATHER.GATHERING — collect until full, then return to dropsite.
        spec.State("INDIVIDUAL").State("GATHER").State("GATHERING")
            .On("Timer", (u, m) =>
            {
                var gatherer = m.Cm!.QueryInterface<ResourceGatherer>(u.Entity);
                if (gatherer == null || gatherer.TargetSupply == null) { u.FinishOrder(); return; }
                var supply = m.Cm!.QueryInterface<ResourceSupply>(gatherer.TargetSupply.Value);
                if (supply == null || supply.IsEmpty) { u.FinishOrder(); return; }

                int gathered = supply.Take((int)(gatherer.EffectiveRate(m.Cm!, supply.Type) * m.Dt));
                gatherer.CarryAmount += gathered;
                gatherer.CarryType = supply.Type;

                if (gatherer.CarryAmount >= 10 || supply.IsEmpty)
                {
                    gatherer.CarryAmount = System.Math.Clamp(gatherer.CarryAmount, 0, 10);
                    var dropsite = FindNearestDropsite(u.Entity, m.Cm!);
                    if (dropsite.HasValue)
                    {
                        gatherer.TargetDropsite = dropsite;
                        MoveToTargetEdge(u, dropsite.Value, m.Cm!, Fixed.FromInt(1));
                        gatherer.State = ResourceGatherer.GatherState.MovingToDropsite;
                        u.FsmNextState = "GATHER.RETURNINGRESOURCE";
                    }
                }
            });

        // GATHER.RETURNINGRESOURCE — drop off at dropsite, then go back to gathering.
        spec.State("INDIVIDUAL").State("GATHER").State("RETURNINGRESOURCE")
            .On("Timer", (u, m) =>
            {
                var gatherer = m.Cm!.QueryInterface<ResourceGatherer>(u.Entity);
                var motion = m.Cm!.QueryInterface<UnitMotion>(u.Entity);
                if (gatherer == null) { u.FinishOrder(); return; }
                if (motion != null && !motion.HasMoveTarget)
                {
                    // Deposit carried resources at the dropsite.
                    DepositResources(u.Entity, gatherer, m.Cm!);
                    // Return to the original supply (if still valid) for another load.
                    if (gatherer.TargetSupply is { } supply && MoveToTargetEdge(u, supply, m.Cm!, Fixed.FromInt(1)))
                    {
                        gatherer.State = ResourceGatherer.GatherState.MovingToResource;
                        u.FsmNextState = "GATHER.APPROACHING";
                    }
                    else
                    {
                        u.FinishOrder();
                    }
                }
            });

        spec.State("INDIVIDUAL").State("GATHER").State("FINDINGNEWTARGET");
    }

    private static void BuildRepairSubtree(FsmSpec<UnitAIComponent, FsmMessage> spec)
    {
        // REPAIR.APPROACHING — move to the foundation; BuilderComponent advances it on its own.
        // BuilderComponent.Tick(cm) (no dt — it applies a fixed per-call build increment) drives
        // both the approach and the building; when its target clears (foundation complete or
        // invalid) the order is done.
        spec.State("INDIVIDUAL").State("REPAIR").State("APPROACHING")
            .On("Timer", (u, m) =>
            {
                var builder = m.Cm!.QueryInterface<BuilderComponent>(u.Entity);
                if (builder == null) { u.FinishOrder(); return; }
                builder.Tick(m.Cm!);
                if (builder.Target == null) u.FinishOrder();
            });

        spec.State("INDIVIDUAL").State("REPAIR").State("REPAIRING")
            .On("Timer", (u, m) =>
            {
                var builder = m.Cm!.QueryInterface<BuilderComponent>(u.Entity);
                if (builder == null) { u.FinishOrder(); return; }
                builder.Tick(m.Cm!);
                if (builder.Target == null) u.FinishOrder();
            });
    }

    private static void BuildFormationControllerTree(FsmSpec<UnitAIComponent, FsmMessage> spec)
    {
        // P1 stub: formation controller states exist so the FSM compiles the full tree.
        spec.State("FORMATIONCONTROLLER").State("IDLE")
            .On("Timer", (_, _) => { });
        spec.State("FORMATIONCONTROLLER").State("WALKING");
        spec.State("FORMATIONCONTROLLER").State("COMBAT");
    }

    private static void BuildFormationMemberTree(FsmSpec<UnitAIComponent, FsmMessage> spec)
    {
        spec.State("FORMATIONMEMBER").State("WALKING")
            .On("Timer", (_, _) => { });
        spec.State("FORMATIONMEMBER").State("WALKINGTOPOINT");
    }

    // =========================================================================
    // Movement / target helpers. These wrap UnitMotion so order handlers stay terse.
    // =========================================================================

    private static void StartMovingTo(UnitAIComponent u, FixedVector2D pos, ComponentManager cm)
    {
        cm.QueryInterface<UnitMotion>(u.Entity)?.MoveToPoint(pos);
    }

    private static bool MoveToTarget(UnitAIComponent u, EntityId target, ComponentManager cm)
    {
        var pos = cm.QueryInterface<PositionComponent>(target);
        if (pos == null) return false;
        cm.QueryInterface<UnitMotion>(u.Entity)?.MoveToPoint(
            new FixedVector2D(pos.Position.X, pos.Position.Z));
        return true;
    }

    /// <summary>Like <see cref="MoveToTarget"/>, but aims at a point just outside the
    /// target's obstruction edge, pulled toward the unit's current position (mirrors
    /// the original's MoveToTargetRange). The exact centre sits inside the target's
    /// own obstruction; pathing to it can strand the unit cells away with the move
    /// target never clearing (observed: gatherer frozen 10 m from a poplar).
    /// Falls back to the centre when the target has no obstruction or the unit is
    /// already inside the offset.</summary>
    private static bool MoveToTargetEdge(UnitAIComponent u, EntityId target, ComponentManager cm, Fixed margin)
    {
        var pos = cm.QueryInterface<PositionComponent>(target);
        if (pos == null) return false;
        var goal = new FixedVector2D(pos.Position.X, pos.Position.Z);

        var self = cm.QueryInterface<PositionComponent>(u.Entity);
        var obs = cm.QueryInterface<ObstructionComponent>(target);
        if (self != null && obs != null)
        {
            long dx = self.Position.X.InternalValue - pos.Position.X.InternalValue;
            long dz = self.Position.Z.InternalValue - pos.Position.Z.InternalValue;
            long offset = (obs.GetSize() + margin).InternalValue;
            long d2 = dx * dx + dz * dz;
            if (d2 > offset * offset && d2 > 0)
            {
                // goal = centre + (self − centre) × (offset / dist), all in fixed-point
                // internal units ((a·b)/c preserves the 16.16 scale).
                long dist = (long)MathInt.Sqrt64((ulong)d2);
                long gx = pos.Position.X.InternalValue + dx * offset / dist;
                long gz = pos.Position.Z.InternalValue + dz * offset / dist;
                goal = new FixedVector2D(
                    Fixed.Zero.WithInternalValue((int)gx),
                    Fixed.Zero.WithInternalValue((int)gz));
            }
        }

        cm.QueryInterface<UnitMotion>(u.Entity)?.MoveToPoint(goal);
        return true;
    }

    /// <summary>Max distance (game metres) at which a gatherer can start collecting.
    /// Resources have obstructions so the unit can't reach their exact centre; this
    /// lets GATHER.APPROACHING hand off to GATHERING once the gatherer is adjacent.</summary>
    private const int GatherRange = 3;

    /// <summary>True when <paramref name="entity"/> is within <paramref name="range"/> metres
    /// of <paramref name="target"/>'s obstruction EDGE (2D, ignoring height). The original
    /// measures gather range from the target's obstruction shape, not its centre — a big
    /// tree's centre sits inside its own obstruction and can never be reached, so a
    /// centre-distance check strands the gatherer in APPROACHING forever.</summary>
    private static bool WithinRange(EntityId entity, EntityId target, ComponentManager cm, int range)
    {
        var a = cm.QueryInterface<PositionComponent>(entity);
        var b = cm.QueryInterface<PositionComponent>(target);
        if (a == null || b == null) return false;
        var dx = a.Position.X - b.Position.X;
        var dz = a.Position.Z - b.Position.Z;
        long d2 = (long)dx.InternalValue * (long)dx.InternalValue
                + (long)dz.InternalValue * (long)dz.InternalValue;
        // effective range = range + target obstruction radius → distance-to-edge semantics.
        var eff = Fixed.FromInt(range);
        var obs = cm.QueryInterface<ObstructionComponent>(target);
        if (obs != null) eff += obs.GetSize();
        long r2 = (long)eff.InternalValue * (long)eff.InternalValue;
        return d2 <= r2;
    }

    private static void StopMoving(UnitAIComponent u) =>
        SimSystem.GetComponent<UnitMotion>(u.Entity)?.Stop();

    private static EntityId? FindNearestDropsite(EntityId gatherer, ComponentManager cm)
    {
        var gpos = cm.QueryInterface<PositionComponent>(gatherer);
        if (gpos == null) return null;
        var gatherCmp = cm.QueryInterface<ResourceGatherer>(gatherer);
        ResourceType carryType = gatherCmp?.CarryType ?? ResourceType.Wood;

        EntityId? best = null;
        Fixed bestDist = Fixed.Zero;
        bool first = true;
        foreach (var e in cm.AllEntities)
        {
            var ds = cm.QueryInterface<ResourceDropsite>(e);
            if (ds == null || !ds.Accepts(carryType)) continue;
            var pos = cm.QueryInterface<PositionComponent>(e);
            if (pos == null) continue;
            var dx = pos.Position.X - gpos.Position.X;
            var dz = pos.Position.Z - gpos.Position.Z;
            ulong d2 = (ulong)((long)dx.InternalValue * (long)dx.InternalValue + (long)dz.InternalValue * (long)dz.InternalValue);
            Fixed dist = Fixed.Zero.WithInternalValue((int)MathInt.Sqrt64(d2));
            if (first || dist < bestDist) { best = e; bestDist = dist; first = false; }
        }
        return best;
    }

    private static void DepositResources(EntityId gatherer, ResourceGatherer g, ComponentManager cm)
    {
        var own = cm.QueryInterface<OwnershipComponent>(gatherer);
        if (own == null) { g.CarryAmount = 0; return; }
        var player = cm.GetPlayerEntity(own.PlayerId);
        if (player != null)
        {
            // AddResource takes (type, amount).
            player.AddResource(g.CarryType, g.CarryAmount);
        }
        g.CarryAmount = 0;
    }

    // =========================================================================
    // Serialization — the order queue + FSM state name. Deterministic across platforms.
    // =========================================================================

    public override void Serialize(ISerializer s)
    {
        s.StringASCII("state", FsmStateName);
        s.NumberI32("orders", _orderQueue.Count);
        foreach (var o in _orderQueue)
        {
            s.StringASCII("type", o.Type);
            s.NumberU32("target", o.Target?.Value ?? 0);
            s.NumberFixed("px", o.Position.X);
            s.NumberFixed("pz", o.Position.Y);
            s.Bool("force", o.Force);
            s.Bool("queued", o.Queued);
        }
    }

    public override void Deserialize(IDeserializer d)
    {
        _orderQueue.Clear();
        FsmStateName = d.StringASCII("state");
        int count = d.NumberI32("orders");
        for (int i = 0; i < count; i++)
        {
            var o = new UnitOrder
            {
                Type = d.StringASCII("type"),
                Position = new FixedVector2D(d.NumberFixed("px"), d.NumberFixed("pz")),
                Force = d.Bool("force"),
                Queued = d.Bool("queued")
            };
            uint t = d.NumberU32("target");
            o.Target = t != 0 ? new EntityId(t) : null;
            _orderQueue.AddLast(o);
        }
    }

    public void HandleMessage(IMessage message) { }

    /// <summary>Internal FSM message carrying the order + sim context into handlers.</summary>
    private sealed record FsmMessage
    {
        public string Type = "";
        public UnitOrder? Order;
        public float Dt;
        public ComponentManager? Cm;
    }
}
