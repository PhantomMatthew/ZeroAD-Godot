using System;
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
    // FormationWalk 负载(原版 order.data.x/z):相对编队控制器的未旋转偏移。
    public float OffsetX;
    public float OffsetZ;
    /// <summary>Attack 单负载(原版 order.data.allowCapture):允许用 Capture 攻击类型
    /// (GUI 的 Ctrl+攻击)。默认 false(原版 DEFAULT_CAPTURE)。</summary>
    public bool AllowCapture;
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
    /// 行为语义 = 原版 g_Stances 表(见 s_stances);GUI 五档经 NetCommand.SetUnitStance 改。</summary>
    public string Stance { get; private set; } = "aggressive";

    /// <summary>原版 g_Stances 单行:九 flag 决定自动索敌/受击响应。selectable=false 的
    /// (skittish/passive-defensive/none)仅 AI/脚本用,GUI 不出现。</summary>
    public readonly record struct StanceFlags(
        bool TargetVisibleEnemies, bool TargetAttackersAlways,
        bool RespondFlee, bool RespondFleeOnSight,
        bool RespondChase, bool RespondChaseBeyondVision,
        bool RespondStandGround, bool RespondHoldGround, bool Selectable);

    private static readonly IReadOnlyDictionary<string, StanceFlags> s_stances =
        new Dictionary<string, StanceFlags>
        {
            ["violent"]     = new(true,  true,  false, false, true,  true,  false, false, true),
            ["aggressive"]  = new(true,  false, false, false, true,  false, false, false, true),
            ["defensive"]   = new(true,  false, false, false, false, false, false, true,  true),
            ["passive"]     = new(false, false, true,  false, false, false, false, false, true),
            ["standground"] = new(true,  false, false, false, false, false, true,  false, true),
            ["skittish"]    = new(false, false, true,  true,  false, false, false, false, false),
            ["passive-defensive"] = new(false, false, false, false, false, false, false, true, false),
            ["none"]        = new(false, false, false, false, false, false, false, false, false),
        };

    /// <summary>当前站姿的 flag 行(未知名落 aggressive——与默认值一致,不抛)。</summary>
    public StanceFlags CurrentStanceFlags =>
        s_stances.TryGetValue(Stance, out var f) ? f : s_stances["aggressive"];

    /// <summary>GUI 可选站姿(stance 按钮条的数据源,顺序 = 原版图标条)。</summary>
    public static IReadOnlyList<string> SelectableStances { get; } =
        new[] { "violent", "aggressive", "defensive", "passive", "standground" };

    /// <summary>换站姿(原版 UnitAI.SetStance:非法名报错不改)。defensive 立即锚定
    /// heldPosition 到脚下(原版 SwitchToStance 语义:驻防点随换岗刷新)。</summary>
    public bool SetStance(string stance, ComponentManager cm)
    {
        if (!s_stances.ContainsKey(stance)) return false;
        Stance = stance;
        if (CurrentStanceFlags.RespondHoldGround)
            CaptureHeldPosition(cm);
        return true;
    }

    // defensive 驻防锚点(原版 heldPosition):换岗/显式 Walk 时刷新;驻守攻击后
    // 自动走回。null = 未锚定(首次换 defensive 或地图载入前)。
    private FixedVector2D? _heldPosition;

    private void CaptureHeldPosition(ComponentManager cm)
    {
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        if (pos != null)
            _heldPosition = new FixedVector2D(pos.Position.X, pos.Position.Z);
    }

    /// <summary>True when the order queue is empty and the unit is in IDLE.</summary>
    public bool IsIdle => _orderQueue.Count == 0;

    /// <summary>Port of UnitAI.js isGarrisoned:驻防中冻结订单处理(缓存标志,性能语义同原版)。</summary>
    public bool IsGarrisoned { get; private set; }

    /// <summary>Port of UnitAI.IsTurret()(缓存):在炮塔点上。不冻结 Tick(炮塔兵可作战),
    /// 仅 SetImmobile + 拒驻军/再上塔指令。原版的站姿切 stand-ground 不移植(站姿系统=P0 桩)。</summary>
    public bool IsTurret { get; private set; }

    /// <summary>Port of SetGarrisoned:冻结 + SetImmobile(停走)。由 Garrisonable.Garrison 调用。</summary>
    public void SetGarrisoned()
    {
        IsGarrisoned = true;
        StopMoving(this);
    }

    /// <summary>Port of UnsetGarrisoned(SetMobile)。由 Garrisonable.UnGarrison 调用。</summary>
    public void UnsetGarrisoned() => IsGarrisoned = false;

    /// <summary>Port of SetTurretStance:SetImmobile(站姿切换不移植,见 IsTurret)。</summary>
    public void SetTurretStance()
    {
        IsTurret = true;
        StopMoving(this);
    }

    /// <summary>Port of ResetTurretStance:SetMobile(站姿还原则略)。</summary>
    public void ResetTurretStance() => IsTurret = false;

    // --- 编队(Formation.js 联动;MS5 落地) ---

    /// <summary>Port of UnitAI.formationController:所属编队控制器;null = 不在编队。</summary>
    public EntityId? FormationController { get; private set; }

    /// <summary>本实体是编队控制器(模板 UnitAI/FormationController=true 路径,
    /// <see cref="InitAsFormationController"/> 设置)。控制器的 IDLE 也要处理 Timer
    /// (定期重排),见 Tick 门。</summary>
    public bool IsFormationController { get; private set; }

    /// <summary>Port of UnitAI.SetFormationController(由 FormationComponent.SetMembers/
    /// AddMembers 调用)。原版同时把 Obstruction ControlGroup 切到控制器(编队成员
    /// 互穿),我们的 Obstruction 不换控制组(记录在案)。</summary>
    public void SetFormationController(EntityId controller) => FormationController = controller;

    /// <summary>Port of UnitAI.UnsetFormationController:清链接并派 FormationLeave
    /// FSM 消息(FORMATIONMEMBER 树:停走/丢 FormationWalk 回 INDIVIDUAL.IDLE;
    /// INDIVIDUAL 树:仅 LeaveFormation 指令收尾,该指令未移植 → 空操作)。</summary>
    public void UnsetFormationController()
    {
        FormationController = null;
        s_fsm.ProcessMessage(this, new FsmMessage { Type = "FormationLeave", Cm = SimSystem.Sim }, "FormationLeave");
    }

    /// <summary>编队控制器初始化(模板 UnitAI/FormationController=true):初始态切到
    /// FORMATIONCONTROLLER.IDLE。由装配路径(AddComponent 之后)调用。</summary>
    public void InitAsFormationController()
    {
        IsFormationController = true;
        s_fsm.Init(this, "FORMATIONCONTROLLER.IDLE");
    }

    /// <summary>FormationComponent.AddMembers 的空闲成员入队态(原版
    /// cmpUnitAI.SetNextState("FORMATIONMEMBER.IDLE"))。我们的空闲单位不处理
    /// Timer,SetNextState 没有 drain 时机 → 直接 Init 切换(两态均无 enter/leave 钩子)。
    /// 有订单的成员保持现态(原版同:等 FormationWalk 指令带走)。</summary>
    public void EnterFormationMemberIdleIfIdle()
    {
        if (IsIdle && FormationController != null)
            s_fsm.Init(this, "FORMATIONMEMBER.IDLE");
    }

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

    /// <summary>Move to a point. Mirrors UnitAI.Walk(x,z,queued).
    /// Force=true:玩家显式指令,受击响应不得打断(原版 GUI 命令 force:true)。</summary>
    public void Walk(FixedVector2D target, bool queued = false)
    {
        PushOrder(new UnitOrder { Type = "Walk", Position = target, Queued = queued, Force = true });
    }

    /// <summary>Gather from a resource supply. Mirrors UnitAI.Gather(target,queued).</summary>
    public void Gather(EntityId target, bool queued = false)
    {
        PushOrder(new UnitOrder { Type = "Gather", Target = target, Queued = queued, Force = true });
    }

    /// <summary>Attack a target. Mirrors UnitAI.Attack(target, allowCapture, queued);
    /// allowCapture 默认 false(原版 DEFAULT_CAPTURE,GUI Ctrl+攻击才传 true)。</summary>
    public void Attack(EntityId target, bool allowCapture = false, bool queued = false)
    {
        PushOrder(new UnitOrder { Type = "Attack", Target = target, Queued = queued, AllowCapture = allowCapture, Force = true });
    }

    /// <summary>Repair / build a foundation. Mirrors UnitAI.Repair(target,queued).</summary>
    public void Repair(EntityId target, bool queued = false)
    {
        PushOrder(new UnitOrder { Type = "Repair", Target = target, Queued = queued, Force = true });
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
        PushOrder(new UnitOrder { Type = "Garrison", Target = holder, Queued = queued, Force = true });
    public void Heal(EntityId target, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "Heal", Target = target, Queued = queued, Force = true });
    public void Trade(EntityId? market, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "Trade", Target = market, Queued = queued });
    public void Pack() => PushOrder(new UnitOrder { Type = "Pack" });
    public void Unpack() => PushOrder(new UnitOrder { Type = "Unpack" });
    public void CancelPack() => PushOrder(new UnitOrder { Type = "CancelPack" });
    public void CancelUnpack() => PushOrder(new UnitOrder { Type = "CancelUnpack" });
    public void CollectTreasure(EntityId target, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "CollectTreasure", Target = target, Queued = queued });
    /// <summary>占炮塔点。对应原版 UnitAI.OccupyTurret(order "Garrison" + garrison:false);
    /// 本移植以独立指令类型承载该标志,与驻军共用 GARRISON 子树。</summary>
    public void OccupyTurret(EntityId target, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "OccupyTurret", Target = target, Queued = queued });

    /// <summary>编队走位(原版 ArrangeFormation → AddOrder("FormationWalk", {target,x,z},
    /// !force)):target=控制器,x/z=未旋转偏移。由 FormationComponent.ArrangeFormation
    /// 发放;force=true → queued=false(替换成员队列)。</summary>
    public void FormationWalk(EntityId controller, float offsetX, float offsetZ, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "FormationWalk", Target = controller, OffsetX = offsetX, OffsetZ = offsetZ, Queued = queued });

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
        // 驻防中:订单队列冻结(对齐原版 isGarrisoned 时 FinishOrder 不派发后续订单;
        // 新入队指令留待出驻后处理)。
        if (IsGarrisoned) return;

        // Dispatch any newly-queued order first (the Order.X handler sets the active state).
        if (_dispatchPending)
            DispatchFrontOrder(cm);

        // 空闲 stance 行为(原版 IDLE.enter 的 FindNewTargets/FindSightedEnemies +
        // LosAttackRangeUpdate;我们以 1s 节流轮询替代 LOS 事件订阅)。编队成员/控制器
        // 不自行索敌(原版 FORMATIONMEMBER 无个体响应)。
        if (IsIdle && !IsGarrisoned && !IsTurret && FormationController == null && !IsFormationController)
            StanceIdleScan(dt, cm);
        // 扫描可能入队自动攻击/回锚订单:立即派发,否则下方 Timer 会打进无 handler 的
        // IDLE 态而抛异常(同"订单残留 IDLE"坑)。
        if (_dispatchPending)
            DispatchFrontOrder(cm);

        // Then let the FSM handle periodic checks via a Timer-style message. Per-state handlers
        // advance the active order (move-arrival polling, gather progress, attack cycles).
        // 编队控制器空闲时也要收 Timer(IDLE 定期重排,对齐原版控制器 IDLE 定时器)。
        if (!IsIdle || _orderQueue.Count > 0 || IsFormationController)
            s_fsm.ProcessMessage(this, new FsmMessage { Type = "Tick", Dt = dt, Cm = cm }, "Timer");
    }

    // 编队控制器定期重排计时(原版控制器 IDLE/WALKING 的 StartTimer 2s 间隔;
    // 我们的 Timer 逐 tick 触发 → 用累计器折成 2s 一拍)。序列化以保持读档后节拍一致。
    private float _formationTimerElapsed;
    private const float FormationUpdateInterval = 2f;

    private bool FormationTimerElapsed(float dt)
    {
        _formationTimerElapsed += dt;
        if (_formationTimerElapsed < FormationUpdateInterval) return false;
        _formationTimerElapsed = 0;
        return true;
    }

    private void ResetFormationTimer() => _formationTimerElapsed = 0;

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
            // 显式走位即重锚驻防点(原版 UpdateHeldPosition:defensive 的"当前位置"
            // 跟随玩家指令,不留在旧锚点)。
            if (u.CurrentStanceFlags.RespondHoldGround)
                u._heldPosition = m.Order.Position;
            u.FsmNextState = "WALKING";
        });

        ind.On("Order.Gather", (u, m) =>
        {
            // 拒收路径一律 FinishOrder 出队(对齐原版,同 Order.Attack):仅置 IDLE 会让
            // 订单残留队列,同 Tick 的 Timer 在无 handler 的 IDLE 态抛出。
            var gatherer = m.Cm!.QueryInterface<ResourceGatherer>(u.Entity);
            if (gatherer == null) { u.FinishOrder(); return; }
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
            // 类型选择(对齐原版 Order.Attack 的 GetBestAttackAgainst):物理型走
            // 敌对+活目标门,捕获型走 CanCapture+RestrictedClasses 门;两门皆关
            // (!type)→ FinishOrder 拒收。
            if (!attack.AttackTarget(m.Cm!, m.Order.Target.Value, m.Order.AllowCapture))
            {
                u.FinishOrder();
                return;
            }
            u.FsmNextState = "COMBAT.APPROACHING";
        });

        ind.On("Order.Repair", (u, m) =>
        {
            // 拒收路径一律 FinishOrder 出队(对齐原版,同 Order.Attack/Gather)。
            var builder = m.Cm!.QueryInterface<BuilderComponent>(u.Entity);
            if (builder == null || m.Order!.Target == null) { u.FinishOrder(); return; }
            builder.Build(m.Order.Target.Value);
            MoveToTarget(u, m.Order.Target.Value, m.Cm!);
            u.FsmNextState = "REPAIR.APPROACHING";
        });

        ind.On("Order.Stop", (u, _) =>
        {
            StopMoving(u);
            // 进行中的治疗/打包等随 Stop 一并取消(对齐原版 HEALING.leave / PACKING.leave)。
            SimSystem.GetComponent<HealComponent>(u.Entity)?.StopHealing();
            SimSystem.GetComponent<TreasureCollectorComponent>(u.Entity)?.StopCollecting();
            u.FsmNextState = "IDLE";
        });

        // P1 orders — accepted, transition to stub states.
        ind.On("Order.Garrison", (u, m) =>
        {
            // 对齐原版 Order.Garrison:无 Garrisonable 件/目标空/不可驻(满员/类别/外交)→
            // FinishOrder 拒收;装填射程内直接 GARRISONING,否则 APPROACHING 接近。
            // 已在炮塔点 → 拒(原版 UnitAI.Garrison:IsTurret 拒收)。
            // (原版 CanPack 时先打包、Pickup 接送不移植。)
            if (u.IsTurret) { u.FinishOrder(); return; }
            var g = m.Cm!.QueryInterface<GarrisonableComponent>(u.Entity);
            if (g == null || m.Order!.Target is not { } t || !g.CanGarrison(m.Cm, t))
            {
                u.FinishOrder();
                return;
            }
            var holder = m.Cm.QueryInterface<GarrisonHolderComponent>(t)!;   // CanGarrison 已验
            if (g.IsInLoadingRange(m.Cm, t, holder))
            {
                u.FsmNextState = "GARRISON.GARRISONING";
                return;
            }
            MoveToTargetEdge(u, t, m.Cm, Fixed.FromFloat(holder.LoadingRange));
            u.FsmNextState = "GARRISON.APPROACHING";
        });
        ind.On("Order.OccupyTurret", (u, m) =>
        {
            // 对应原版 order "Garrison" + garrison:false:驻防中不可上塔(原版本单位 AI 入口
            // 即拒);其余同驻军路径。
            if (u.IsGarrisoned) { u.FinishOrder(); return; }
            var tb = m.Cm!.QueryInterface<TurretableComponent>(u.Entity);
            if (tb == null || m.Order!.Target is not { } t || !tb.CanOccupy(m.Cm, t))
            {
                u.FinishOrder();
                return;
            }
            var holder = m.Cm.QueryInterface<TurretHolderComponent>(t)!;   // CanOccupy 已验
            if (tb.IsInLoadingRange(m.Cm, t, holder))
            {
                u.FsmNextState = "GARRISON.GARRISONING";
                return;
            }
            MoveToTargetEdge(u, t, m.Cm, Fixed.FromFloat(holder.LoadingRange));
            u.FsmNextState = "GARRISON.APPROACHING";
        });
        ind.On("Order.Heal", (u, m) =>
        {
            // 对齐原版 Order.Heal:目标死亡/自己/不可治疗 → FinishOrder 拒收;
            // 射程内直接 HEALING,否则 APPROACHING 追击。
            var heal = m.Cm!.QueryInterface<HealComponent>(u.Entity);
            if (heal == null || m.Order!.Target is not { } t || t == u.Entity) { u.FinishOrder(); return; }
            var targetHealth = m.Cm.QueryInterface<HealthComponent>(t);
            if (targetHealth == null || targetHealth.IsDead) { u.FinishOrder(); return; }
            if (heal.IsTargetInRange(m.Cm, t))
            {
                if (!heal.StartHealing(m.Cm, t)) { u.FinishOrder(); return; }
                u.FsmNextState = "HEAL.HEALING";
                return;
            }
            if (!heal.CanHeal(m.Cm, t)) { u.FinishOrder(); return; }
            MoveToTargetEdge(u, t, m.Cm, Fixed.FromFloat(heal.Range));
            u.FsmNextState = "HEAL.APPROACHING";
        });
        ind.On("Order.Trade", (u, m) =>
        {
            // 对齐原版 Order.Trade(back-to-work):无 Trader 件或未建双市场路由 → FinishOrder。
            // 路由建立(SetTargetMarket)不走指令,由命令层直调 Trader 组件(同原版
            // setup-trade-route)。指令目标缺省取当前 index 市场。
            var trader = m.Cm!.QueryInterface<TraderComponent>(u.Entity);
            if (trader == null || !trader.HasBothMarkets()) { u.FinishOrder(); return; }
            m.Order!.Target ??= trader.GetCurrentMarket();
            if (m.Order.Target == null) { u.FinishOrder(); return; }
            MoveToTargetEdge(u, m.Order.Target.Value, m.Cm, Fixed.FromFloat(trader.GetTradeRange(m.Cm)));
            u.FsmNextState = "TRADE.APPROACHINGMARKET";
        });
        ind.On("Order.Pack", (u, m) =>
        {
            // 对齐原版 Order.Pack:无 Pack 件或不可打包 → FinishOrder 拒收。
            var pack = m.Cm!.QueryInterface<PackComponent>(u.Entity);
            if (pack == null || !pack.CanPack()) { u.FinishOrder(); return; }
            pack.Pack();
            u.FsmNextState = "PACKING";
        });
        ind.On("Order.Unpack", (u, m) =>
        {
            var pack = m.Cm!.QueryInterface<PackComponent>(u.Entity);
            if (pack == null || !pack.CanUnpack()) { u.FinishOrder(); return; }
            pack.Unpack();
            u.FsmNextState = "UNPACKING";
        });
        ind.On("Order.CancelPack", (u, _) => u.FinishOrder());
        ind.On("Order.CancelUnpack", (u, _) => u.FinishOrder());
        ind.On("Order.CollectTreasure", (u, m) =>
        {
            // 对齐原版 Order.CollectTreasure:不可取(无 Treasure/已被取)→ FinishOrder;
            // 射程内直接 COLLECTING,否则 APPROACHING。
            var tc = m.Cm!.QueryInterface<TreasureCollectorComponent>(u.Entity);
            if (tc == null || m.Order!.Target is not { } t) { u.FinishOrder(); return; }
            if (!tc.CanCollect(m.Cm, t)) { u.FinishOrder(); return; }
            if (tc.IsTargetInRange(m.Cm, t))
            {
                if (!tc.StartCollecting(m.Cm, t)) { u.FinishOrder(); return; }
                u.FsmNextState = "COLLECTTREASURE.COLLECTING";
                return;
            }
            MoveToTargetEdge(u, t, m.Cm, Fixed.FromFloat(tc.MaxDistance));
            u.FsmNextState = "COLLECTTREASURE.APPROACHING";
        });

        // 编队指令(原版定义在 FSM 根,两树共享;我们分别挂在 INDIVIDUAL/FORMATIONMEMBER
        // 根)。Order.FormationWalk:非成员/驻防(AbleToMove)→ FinishOrder;攻城器
        // CanPack 分支不移植(编队遇攻城器打包)。
        ind.On("Order.FormationWalk", (u, m) =>
        {
            if (u.FormationController == null || u.IsGarrisoned) { u.FinishOrder(); return; }
            u.FsmNextState = "FORMATIONMEMBER.WALKING";
        });
        // FormationLeave(原版根处理器):仅收尾 LeaveFormation 指令——该指令未移植
        // (原版 GUI 选中出队路径),此处为空操作;真正的脱队逻辑在 FORMATIONMEMBER 树。
        ind.On("FormationLeave", (u, _) =>
        {
            if (u.CurrentOrder?.Type == "LeaveFormation") u.FinishOrder();
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

        // GARRISON 子树(原版 UnitAI.js GARRISON 状态):APPROACHING 接近至装填射程 →
        // GARRISONING 入驻(enter 一次性完成:驻防/占塔 + 交付携带资源 + FinishOrder)。
        // 驻军与占塔共用(原版同,以 order.data.garrison 区分;本移植看 order.Type)。
        // 目标失效/变满 → FinishOrder(原版 Pickup 接送不移植)。
        spec.State("INDIVIDUAL").State("GARRISON").State("APPROACHING")
            .On("Timer", (u, m) =>
            {
                if (u.CurrentOrder?.Type == "OccupyTurret")
                {
                    var tb = m.Cm!.QueryInterface<TurretableComponent>(u.Entity);
                    if (tb == null || u.CurrentOrder?.Target is not { } tt || !tb.CanOccupy(m.Cm, tt))
                    {
                        u.FinishOrder();
                        return;
                    }
                    var th = m.Cm.QueryInterface<TurretHolderComponent>(tt)!;
                    if (tb.IsInLoadingRange(m.Cm, tt, th))
                    {
                        StopMoving(u);
                        u.FsmNextState = "GARRISON.GARRISONING";
                        return;
                    }
                    var tmotion = m.Cm.QueryInterface<UnitMotion>(u.Entity);
                    if (tmotion != null && !tmotion.HasMoveTarget)
                        MoveToTargetEdge(u, tt, m.Cm, Fixed.FromFloat(th.LoadingRange));
                    return;
                }
                var g = m.Cm!.QueryInterface<GarrisonableComponent>(u.Entity);
                if (g == null || u.CurrentOrder?.Target is not { } t || !g.CanGarrison(m.Cm, t))
                {
                    u.FinishOrder();
                    return;
                }
                var holder = m.Cm.QueryInterface<GarrisonHolderComponent>(t)!;
                if (g.IsInLoadingRange(m.Cm, t, holder))
                {
                    StopMoving(u);
                    u.FsmNextState = "GARRISON.GARRISONING";
                    return;
                }
                var motion = m.Cm.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget)
                    MoveToTargetEdge(u, t, m.Cm, Fixed.FromFloat(holder.LoadingRange));
            });
        spec.State("INDIVIDUAL").State("GARRISON").State("GARRISONING")
            .Enter(u =>
            {
                // 原版 GARRISONING.enter:入驻失败 → FinishOrder 并中止入态(return true);
                // 成功后向投放站交付携带资源(原版驻军/占塔两路都交付),FinishOrder,中止入态。
                var cm = SimSystem.Sim;
                if (cm != null && u.CurrentOrder?.Target is { } t)
                {
                    bool ok = u.CurrentOrder.Type == "OccupyTurret"
                        ? SimSystem.GetComponent<TurretableComponent>(u.Entity)?.OccupyTurret(cm, t) == true
                        : SimSystem.GetComponent<GarrisonableComponent>(u.Entity)?.Garrison(cm, t) == true;
                    if (ok)
                        AfterGarrisoned(u, cm, t);
                }
                u.FinishOrder();
                return true;
            });

        // HEAL 子树(原版 UnitAI.js HEAL 状态):APPROACHING 追击 → HEALING 治疗;
        // 目标出射程回 APPROACHING 再追(对齐 OutOfRange → ShouldChaseTargetedEntity),
        // 目标不可治疗/补满 → FinishOrder(原版 FINDINGNEWTARGET 自动找新伤员,P1 不移植)。
        spec.State("INDIVIDUAL").State("HEAL").State("APPROACHING")
            .On("Timer", (u, m) =>
            {
                var heal = m.Cm!.QueryInterface<HealComponent>(u.Entity);
                if (heal == null || u.CurrentOrder?.Target is not { } t) { u.FinishOrder(); return; }
                if (!heal.CanHeal(m.Cm, t)) { u.FinishOrder(); return; }
                if (heal.IsTargetInRange(m.Cm, t))
                {
                    StopMoving(u);
                    if (!heal.StartHealing(m.Cm, t)) { u.FinishOrder(); return; }
                    u.FsmNextState = "HEAL.HEALING";
                    return;
                }
                // 未到射程:移动目标丢失(目标在动)就重发追击。
                var motion = m.Cm.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget)
                    MoveToTargetEdge(u, t, m.Cm, Fixed.FromFloat(heal.Range));
            });
        spec.State("INDIVIDUAL").State("HEAL").State("HEALING")
            .On("Timer", (u, m) =>
            {
                var heal = m.Cm!.QueryInterface<HealComponent>(u.Entity);
                if (heal == null || heal.Target is not { } t) { u.FinishOrder(); return; }
                switch (heal.Tick(m.Dt, m.Cm!))
                {
                    case HealTickResult.OutOfRange:
                        MoveToTargetEdge(u, t, m.Cm, Fixed.FromFloat(heal.Range));
                        u.FsmNextState = "HEAL.APPROACHING";
                        break;
                    case HealTickResult.TargetInvalid:
                        u.FinishOrder();
                        break;
                }
            })
            .Leave(u => SimSystem.GetComponent<HealComponent>(u.Entity)?.StopHealing());
        // TRADE 子树(原版同名):APPROACHINGMARKET 走向当前目标市场 → TRADING 到港结算
        // (PerformTrade:付上一程+选品+定价下一程)→ 目标切到下一市场继续往返。
        // 市场失效/不可交易/零收益 → FinishOrder(原版 TradingCanceled/查找替代市场不移植)。
        spec.State("INDIVIDUAL").State("TRADE").State("APPROACHINGMARKET")
            .On("Timer", (u, m) =>
            {
                var trader = m.Cm!.QueryInterface<TraderComponent>(u.Entity);
                if (trader == null || u.CurrentOrder?.Target is not { } t) { u.FinishOrder(); return; }
                if (!trader.CanTrade(m.Cm, t)) { u.FinishOrder(); return; }
                if (trader.IsInTradeRange(m.Cm, t))
                {
                    StopMoving(u);
                    u.FsmNextState = "TRADE.TRADING";
                    return;
                }
                var motion = m.Cm.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget)
                    MoveToTargetEdge(u, t, m.Cm, Fixed.FromFloat(trader.GetTradeRange(m.Cm)));
            });
        spec.State("INDIVIDUAL").State("TRADE").State("TRADING")
            .On("Timer", (u, m) =>
            {
                var trader = m.Cm!.QueryInterface<TraderComponent>(u.Entity);
                if (trader == null || u.CurrentOrder?.Target is not { } cur) { u.FinishOrder(); return; }
                if (!trader.CanTrade(m.Cm, cur)) { u.FinishOrder(); return; }
                if (!trader.IsInTradeRange(m.Cm, cur))
                {
                    MoveToTargetEdge(u, cur, m.Cm, Fixed.FromFloat(trader.GetTradeRange(m.Cm)));
                    u.FsmNextState = "TRADE.APPROACHINGMARKET";
                    return;
                }
                var next = trader.PerformTrade(m.Cm, cur);
                if (next == null || !trader.HasGain || trader.TraderGain <= 0) { u.FinishOrder(); return; }
                u.CurrentOrder!.Target = next.Value;   // 原版 order.data.target = nextMarket
                MoveToTargetEdge(u, next.Value, m.Cm, Fixed.FromFloat(trader.GetTradeRange(m.Cm)));
                u.FsmNextState = "TRADE.APPROACHINGMARKET";
            });
        // PACKING/UNPACKING(原版 UnitAI.js 同名状态):Tick 进度,完成(Tick→true,即
        // MT_PackFinished 等价)FinishOrder;离开状态(Stop/换指令/取消)leave 钩子 CancelPack
        // —— 完成后 Packing 已为 false,CancelPack 幂等无操作。
        spec.State("INDIVIDUAL").State("PACKING")
            .On("Timer", (u, m) =>
            {
                var pack = m.Cm!.QueryInterface<PackComponent>(u.Entity);
                if (pack == null) { u.FinishOrder(); return; }
                if (pack.Tick(m.Dt, m.Cm!)) u.FinishOrder();
            })
            .Leave(u => SimSystem.GetComponent<PackComponent>(u.Entity)?.CancelPack());
        spec.State("INDIVIDUAL").State("UNPACKING")
            .On("Timer", (u, m) =>
            {
                var pack = m.Cm!.QueryInterface<PackComponent>(u.Entity);
                if (pack == null) { u.FinishOrder(); return; }
                if (pack.Tick(m.Dt, m.Cm!)) u.FinishOrder();
            })
            .Leave(u => SimSystem.GetComponent<PackComponent>(u.Entity)?.CancelPack());
        spec.State("INDIVIDUAL").State("PATROL");
        spec.State("INDIVIDUAL").State("GUARD");
        spec.State("INDIVIDUAL").State("FLEEING");
        spec.State("INDIVIDUAL").State("RETURNRESOURCE");

        // COLLECTTREASURE 子树(原版同名):APPROACHING 接近 → COLLECTING 计时结算;
        // 结算完成/目标失效 → FinishOrder(原版 FINDINGNEWTARGET 自动找附近宝物,P1 不移植);
        // 结算点出射程 → 回 APPROACHING(宝物不动,罕见)。
        spec.State("INDIVIDUAL").State("COLLECTTREASURE").State("APPROACHING")
            .On("Timer", (u, m) =>
            {
                var tc = m.Cm!.QueryInterface<TreasureCollectorComponent>(u.Entity);
                if (tc == null || u.CurrentOrder?.Target is not { } t) { u.FinishOrder(); return; }
                if (!tc.CanCollect(m.Cm, t)) { u.FinishOrder(); return; }
                if (tc.IsTargetInRange(m.Cm, t))
                {
                    StopMoving(u);
                    if (!tc.StartCollecting(m.Cm, t)) { u.FinishOrder(); return; }
                    u.FsmNextState = "COLLECTTREASURE.COLLECTING";
                    return;
                }
                var motion = m.Cm.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget)
                    MoveToTargetEdge(u, t, m.Cm, Fixed.FromFloat(tc.MaxDistance));
            });
        spec.State("INDIVIDUAL").State("COLLECTTREASURE").State("COLLECTING")
            .On("Timer", (u, m) =>
            {
                var tc = m.Cm!.QueryInterface<TreasureCollectorComponent>(u.Entity);
                if (tc == null || tc.Treasure is not { } t) { u.FinishOrder(); return; }
                switch (tc.Tick(m.Dt, m.Cm!))
                {
                    case CollectTickResult.Done:
                    case CollectTickResult.TargetInvalid:
                        u.FinishOrder();
                        break;
                    case CollectTickResult.OutOfRange:
                        MoveToTargetEdge(u, t, m.Cm, Fixed.FromFloat(tc.MaxDistance));
                        u.FsmNextState = "COLLECTTREASURE.APPROACHING";
                        break;
                }
            })
            .Leave(u => SimSystem.GetComponent<TreasureCollectorComponent>(u.Entity)?.StopCollecting());
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
                // 自动(非玩家强制)攻击订单的追击边界(原版 COMBAT.CHASING/APPROACHING
                // 的 stance 门):standground 永不追击;defensive 目标跑出驻防圈即弃;
                // aggressive 目标脱出视野即弃(violent 的 respondChaseBeyondVision 豁免)。
                if (u.CurrentOrder is { Type: "Attack", Force: false })
                {
                    var flags = u.CurrentStanceFlags;
                    // standground 绝不追击——但原版 APPROACHING 仅"目标在射程外"才进入
                    // (射程内直进 ATTACKING);我们的移植必经此态,故只在超射程时拦截。
                    if (flags.RespondStandGround)
                    {
                        var tp = m.Cm.QueryInterface<PositionComponent>(attack.Target.Value);
                        var mp = m.Cm.QueryInterface<PositionComponent>(u.Entity);
                        if (tp != null && mp != null)
                        {
                            float sdx = tp.Position.X.ToFloat() - mp.Position.X.ToFloat();
                            float sdz = tp.Position.Z.ToFloat() - mp.Position.Z.ToFloat();
                            float sreach = attack.CurrentAttackIsCapture ? attack.CaptureRange : attack.Range;
                            if (sdx * sdx + sdz * sdz > sreach * sreach) { u.FinishOrder(); return; }
                        }
                    }
                    if (flags.RespondHoldGround && u._heldPosition is { } held)
                    {
                        var tp = m.Cm.QueryInterface<PositionComponent>(attack.Target.Value);
                        if (tp != null)
                        {
                            float hdx = tp.Position.X.ToFloat() - held.X.ToFloat();
                            float hdz = tp.Position.Z.ToFloat() - held.Y.ToFloat();
                            float reach = attack.CurrentAttackIsCapture ? attack.CaptureRange : attack.Range;
                            if (hdx * hdx + hdz * hdz > reach * reach) { u.FinishOrder(); return; }
                        }
                    }
                    if (!flags.RespondChaseBeyondVision)
                    {
                        var own = m.Cm.QueryInterface<OwnershipComponent>(u.Entity);
                        if (own != null && SimSystem.Range != null &&
                            SimSystem.Range.GetLosVisibility(attack.Target.Value, own.PlayerId) != LosVisibility.Visible)
                        {
                            u.FinishOrder();
                            return;
                        }
                    }
                }
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
        // FORMATIONCONTROLLER(原版 UnitAI.js 同名树,MS5 落地 Walk/Stop;编队作战/
        // Gather/Heal 等 CallMemberFunction 路径随编队作战一起做)。
        var fc = spec.State("FORMATIONCONTROLLER");

        fc.On("Order.Walk", (u, m) =>
        {
            // 原版:SetHeldPosition(编队作战回位用,未移植)+ WALKING。
            u.FsmNextState = "WALKING";
        });
        fc.On("Order.Stop", (u, m) =>
        {
            // 原版:CallMemberFunction("Stop") + FinishOrder(不把成员拉回队形位)。
            var formation = m.Cm!.QueryInterface<FormationComponent>(u.Entity);
            if (formation != null)
                foreach (var member in formation.Members)
                    m.Cm.QueryInterface<UnitAIComponent>(member)?.Stop();
            StopMoving(u);
            u.FsmNextState = "IDLE";
        });

        // IDLE:定期 RequestFormationUpdate()(非强制重排;成员变动使偏移作废后在此补齐)。
        fc.State("IDLE")
            .Enter(u => u.ResetFormationTimer())
            .On("Timer", (u, m) =>
            {
                if (!u.FormationTimerElapsed(m.Dt)) return;
                m.Cm!.QueryInterface<FormationComponent>(u.Entity)
                    ?.UpdateFormation(m.Cm, moveCenter: false, force: false);
            });

        // WALKING:入态强制重排(moveCenter:跳到成员质心+朝目标转向)+ 控制器自身走向
        // 目标;每 2s 强制重排(原版 Timer → RequestFormationUpdate(false, true));
        // 到达(移动目标清空)→ FinishOrder。成员是否全部到位不阻塞控制器(原版同,
        // 成员的 FormationWalk 各自独立收尾)。
        fc.State("WALKING")
            .Enter(u =>
            {
                u.ResetFormationTimer();
                var cm = SimSystem.Sim;
                cm?.QueryInterface<FormationComponent>(u.Entity)
                    ?.UpdateFormation(cm, moveCenter: true, force: true);
                if (u.CurrentOrder is { } order)
                    SimSystem.GetComponent<UnitMotion>(u.Entity)?.MoveToPoint(order.Position);
            })
            .On("Timer", (u, m) =>
            {
                if (u.FormationTimerElapsed(m.Dt))
                    m.Cm!.QueryInterface<FormationComponent>(u.Entity)
                        ?.UpdateFormation(m.Cm, moveCenter: false, force: true);
                var motion = m.Cm!.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget)
                    u.FinishOrder();
            })
            .Leave(u => StopMoving(u));

        spec.State("FORMATIONCONTROLLER").State("COMBAT");
    }

    private static void BuildFormationMemberTree(FsmSpec<UnitAIComponent, FsmMessage> spec)
    {
        // FORMATIONMEMBER(原版 UnitAI.js 同名树,MS5 落地 FormationWalk/WALKING;
        // 个体作战指令由 INDIVIDUAL 树处理——原版成员无订单时 IDLE 别名 INDIVIDUAL.IDLE,
        // 控制器 CallMemberFunction 的 Gather/Attack 等随编队作战一起做)。
        var fm = spec.State("FORMATIONMEMBER");

        // FormationLeave(原版同名):停走;丢下当前(FormationWalk)指令;回 INDIVIDUAL.IDLE。
        fm.On("FormationLeave", (u, m) =>
        {
            StopMoving(u);
            if (!u.IsIdle) u.FinishOrder();
            u.FsmNextState = "INDIVIDUAL.IDLE";
        });
        fm.On("Order.FormationWalk", (u, m) =>
        {
            if (u.FormationController == null || u.IsGarrisoned) { u.FinishOrder(); return; }
            u.FsmNextState = "FORMATIONMEMBER.WALKING";
        });
        fm.On("Order.Stop", (u, _) =>
        {
            StopMoving(u);
            u.FsmNextState = "IDLE";
        });

        // 原版 "IDLE": "INDIVIDUAL.IDLE" 别名——成员无订单时按个体空闲处理。
        fm.State("IDLE").Alias("INDIVIDUAL.IDLE");

        // WALKING:走向"控制器位置+旋转后的偏移",逐 tick 跟踪移动中的控制器
        // (原版 UnitMotion.MoveToFormationOffset 的持续跟踪语义);到位(≤1m,同
        // UnitMotion 吸附半径)→ SetFinishedEntity + FinishOrder。
        fm.State("WALKING")
            .Enter(u =>
            {
                // 原版 enter:MoveToFormationOffset + PossiblyAtDestination → 立即 FinishOrder
                // 并中止入态。
                var cm = SimSystem.Sim;
                if (cm == null) return false;
                FormationWalkStep(u, cm);
                return u.CurrentOrder == null;   // 到位 FinishOrder 后队列空 → 中止入态
            })
            .On("Timer", (u, m) => FormationWalkStep(u, m.Cm!))
            .Leave(u => StopMoving(u));
    }

    /// <summary>FORMATIONMEMBER.WALKING 的 enter/Timer 共用步进:重算世界偏移目标
    /// (控制器位置+朝向旋转,公式同 Formation.GetRealOffsetPositions);到位 → 转正 +
    /// 标记完成 + FinishOrder;否则在目标偏移超 1m 或停走时重发移动。</summary>
    private static void FormationWalkStep(UnitAIComponent u, ComponentManager cm)
    {
        if (u.CurrentOrder is not { } order || order.Type != "FormationWalk") return;
        if (u.FormationController is not { } ctrl) { u.FinishOrder(); return; }
        var formation = cm.QueryInterface<FormationComponent>(ctrl);
        var ctrlPos = cm.QueryInterface<PositionComponent>(ctrl);
        var pos = cm.QueryInterface<PositionComponent>(u.Entity);
        if (formation == null || ctrlPos == null || pos == null) { u.FinishOrder(); return; }

        float rot = ctrlPos.Rotation.Y.ToFloat();
        float sin = MathF.Sin(rot), cos = MathF.Cos(rot);
        float tx = ctrlPos.Position.X.ToFloat() + order.OffsetZ * sin + order.OffsetX * cos;
        float tz = ctrlPos.Position.Z.ToFloat() + order.OffsetZ * cos - order.OffsetX * sin;

        float dx = tx - pos.Position.X.ToFloat(), dz = tz - pos.Position.Z.ToFloat();
        // 到位判定要求控制器也已停走(原版 FORMATIONMEMBER.WALKING 注释:MovementUpdate
        // 只在"单位到位且控制器走完"时到达)——否则成员行军途中到位即停,只能等下一次
        // 重排再追赶(蛙跳式掉队)。
        var ctrlMotion = cm.QueryInterface<UnitMotion>(ctrl);
        bool ctrlDone = ctrlMotion == null || !ctrlMotion.HasMoveTarget;
        if (ctrlDone && dx * dx + dz * dz <= 1f)
        {
            // 到位(原版 MovementUpdate → SetFinishedEntity + FinishOrder)。
            StopMoving(u);
            formation.SetFinishedEntity(cm, u.Entity);
            u.FinishOrder();
            return;
        }
        var motion = cm.QueryInterface<UnitMotion>(u.Entity);
        if (motion == null) { u.FinishOrder(); return; }
        float mdx = tx - motion.TargetPos.X.ToFloat(), mdz = tz - motion.TargetPos.Y.ToFloat();
        if (!motion.HasMoveTarget || mdx * mdx + mdz * mdz > 1f)
            motion.MoveToPoint(new FixedVector2D(Fixed.FromFloat(tx), Fixed.FromFloat(tz)));
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
            // 采集入账事件（驱动 StatisticsTracker.resourcesGathered）。镜像 ResourceGatherer.js:286。
            cm.Events.RaiseResourceGathered(new ZeroAD.Sim.Events.ResourceGatheredEvent
            {
                PlayerId = own.PlayerId,
                Type = g.CarryType,
                Amount = g.CarryAmount,
            });
        }
        g.CarryAmount = 0;
    }

    /// <summary>Port of GARRISONING.enter 的资源交付段:驻军目标是接受所携资源的投放站
    /// (CanReturnResource(target, true))→ CommitResources 就地交付。</summary>
    private static void AfterGarrisoned(UnitAIComponent u, ComponentManager cm, EntityId target)
    {
        var g = cm.QueryInterface<ResourceGatherer>(u.Entity);
        if (g == null || g.CarryAmount <= 0) return;
        var dropsite = cm.QueryInterface<ResourceDropsite>(target);
        if (dropsite == null || !dropsite.Accepts(g.CarryType)) return;
        DepositResources(u.Entity, g, cm);
    }

    // =========================================================================
    // Serialization — the order queue + FSM state name. Deterministic across platforms.
    // =========================================================================

    // =========================================================================
    // Stance behaviour — 原版 IDLE.enter 的 FindNewTargets/FindSightedEnemies 与
    // FSM "Attacked" 消息(DelayedDamage → OnAttacked)的移植。事件订阅以 1s 节流
    // 轮询代替(确定性;相位由实体 id 铺开,避免同帧全员扫描)。
    // =========================================================================

    private const float StanceScanInterval = 1.0f;
    private float _stanceScanElapsed;

    private void StanceIdleScan(float dt, ComponentManager cm)
    {
        _stanceScanElapsed += dt;
        if (_stanceScanElapsed < StanceScanInterval) return;
        _stanceScanElapsed = 0;

        var flags = CurrentStanceFlags;
        // 顺序对齐原版 IDLE.enter:先 FindNewTargets(索敌响应),再回驻防锚点。
        if (flags.TargetVisibleEnemies || flags.RespondFleeOnSight)
        {
            var enemies = FindVisibleEnemies(cm, flags);
            if (enemies.Count > 0)
            {
                RespondToTargetedEntity(enemies[0], cm);
                return;
            }
        }
        // 回锚(原版 respondHoldGround && heldPosition && 距锚 >10m → WalkToHeldPosition)。
        if (flags.RespondHoldGround && _heldPosition is { } held)
        {
            var pos = cm.QueryInterface<PositionComponent>(Entity);
            if (pos != null)
            {
                float dx = pos.Position.X.ToFloat() - held.X.ToFloat();
                float dz = pos.Position.Z.ToFloat() - held.Y.ToFloat();
                if (dx * dx + dz * dz > 100f)
                    PushOrder(new UnitOrder { Type = "Walk", Position = held, Force = false });
            }
        }
    }

    /// <summary>视野内可见、可攻击的敌对玩家实体(原版 FindNewTargets 的目标掩码)。
    /// gaia(owner≤0)排除:不自动打猎/砍树;敌对野兽的反击经 OnAttacked 覆盖。</summary>
    private List<EntityId> FindVisibleEnemies(ComponentManager cm, StanceFlags flags)
    {
        var empty = new List<EntityId>();
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        var range = SimSystem.Range;
        if (own == null || range == null) return empty;
        var vision = cm.QueryInterface<VisionComponent>(Entity);
        if (vision == null || vision.Range <= Fixed.Zero) return empty;
        bool canFight = cm.QueryInterface<AttackComponent>(Entity) != null;
        if (!canFight && !flags.RespondFleeOnSight) return empty;
        int me = own.PlayerId;
        return range.ExecuteQuery(Entity, Fixed.Zero, vision.Range, e =>
        {
            var eo = cm.QueryInterface<OwnershipComponent>(e);
            if (eo == null || eo.PlayerId <= 0 || !cm.Players.IsEnemy(me, eo.PlayerId)) return false;
            if (range.GetLosVisibility(e, me) != LosVisibility.Visible) return false;
            var h = cm.QueryInterface<HealthComponent>(e);
            return h is { IsDead: false };
        });
    }

    /// <summary>原版 RespondToTargetedEntities 单目标版:chase/standground→攻击;
    /// holdGround→驻防圈内才攻击;flee→逃离。</summary>
    private void RespondToTargetedEntity(EntityId target, ComponentManager cm)
    {
        var flags = CurrentStanceFlags;
        if (flags.RespondChase || flags.RespondStandGround)
        {
            // standground 的"绝不移动"由 COMBAT.APPROACHING 的 stance 门执行
            // (原版同构:AttackVisibleEntity 推单,APPROACHING.enter 拦截非强制追击)。
            TryPushStanceAttack(target, cm);
            return;
        }
        if (flags.RespondHoldGround)
        {
            // AttackEntityInZone:目标须处驻防锚点的攻击射程内。
            if (_heldPosition is not { } held) return;
            var tp = cm.QueryInterface<PositionComponent>(target);
            var attack = cm.QueryInterface<AttackComponent>(Entity);
            if (tp == null || attack == null) return;
            float dx = tp.Position.X.ToFloat() - held.X.ToFloat();
            float dz = tp.Position.Z.ToFloat() - held.Y.ToFloat();
            if (dx * dx + dz * dz <= attack.Range * attack.Range)
                TryPushStanceAttack(target, cm);
            return;
        }
        if (flags.RespondFlee)
            FleeFrom(target, cm);
    }

    private void TryPushStanceAttack(EntityId target, ComponentManager cm)
    {
        if (cm.QueryInterface<AttackComponent>(Entity) == null) return;
        // Force=false:stance 自发攻击;Order.Attack handler 仍走 AttackTarget 合法性门。
        PushOrder(new UnitOrder { Type = "Attack", Target = target, Force = false });
    }

    /// <summary>逃离威胁(原版 Flee 订单的 v1 简化:一次性背离 15m;原版持续逃离
    /// 更新留 backlog)。方向取"我−威胁"归一;重叠时走确定性 +x。</summary>
    private void FleeFrom(EntityId threat, ComponentManager cm)
    {
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        var tp = cm.QueryInterface<PositionComponent>(threat);
        if (pos == null || tp == null) return;
        float dx = pos.Position.X.ToFloat() - tp.Position.X.ToFloat();
        float dz = pos.Position.Z.ToFloat() - tp.Position.Z.ToFloat();
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 0.001f) { dx = 1f; dz = 0f; len = 1f; }
        const float fleeDist = 15f;
        var dest = new FixedVector2D(
            Fixed.FromFloat(pos.Position.X.ToFloat() + dx / len * fleeDist),
            Fixed.FromFloat(pos.Position.Z.ToFloat() + dz / len * fleeDist));
        PushOrder(new UnitOrder { Type = "Walk", Position = dest, Force = false });
    }

    /// <summary>受击响应(原版 FSM "Attacked" 消息;唯一调用点 = DelayedDamage.ApplyDirect,
    /// 物理伤害 >0 时)。按 stance 表反击/逃跑/无视;玩家强制订单(Force=true)不被
    /// 打断、攻击者须可见——violent(targetAttackersAlways)豁免这两条。</summary>
    public void OnAttacked(EntityId attacker, ComponentManager cm)
    {
        if (IsGarrisoned || IsTurret || IsFormationController) return;
        var flags = CurrentStanceFlags;
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        var aOwn = cm.QueryInterface<OwnershipComponent>(attacker);
        if (own == null || aOwn == null) return;
        if (!cm.Players.IsEnemy(own.PlayerId, aOwn.PlayerId)) return;
        var ah = cm.QueryInterface<HealthComponent>(attacker);
        if (ah == null || ah.IsDead) return;
        if (!flags.TargetAttackersAlways && CurrentOrder is { Force: true }) return;
        if (!flags.TargetAttackersAlways)
        {
            var range = SimSystem.Range;
            if (range == null || range.GetLosVisibility(attacker, own.PlayerId) != LosVisibility.Visible)
                return;
        }
        RespondToTargetedEntity(attacker, cm);
    }

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
            // FormationWalk 负载(本 v2 周期内追加,读序须与写序逐位一致)。
            s.NumberFixed("ox", Fixed.FromFloat(o.OffsetX));
            s.NumberFixed("oz", Fixed.FromFloat(o.OffsetZ));
            // Attack 单负载(本存档周期追加,读序须与写序逐位一致)。
            s.Bool("allowcap", o.AllowCapture);
        }
        s.Bool("garrisoned", IsGarrisoned);
        s.Bool("turret", IsTurret);
        s.NumberU32("formationController", FormationController?.Value ?? 0);
        s.Bool("isFormationController", IsFormationController);
        s.NumberFixed("formationTimer", Fixed.FromFloat(_formationTimerElapsed));
        // Stance 负载(本存档周期追加,读序须与写序逐位一致)。
        s.StringASCII("stance", Stance);
        s.Bool("heldValid", _heldPosition.HasValue);
        s.NumberFixed("heldX", _heldPosition?.X ?? Fixed.Zero);
        s.NumberFixed("heldZ", _heldPosition?.Y ?? Fixed.Zero);
        s.NumberFixed("stanceScan", Fixed.FromFloat(_stanceScanElapsed));
    }

    public override void Deserialize(IDeserializer d)
    {
        _orderQueue.Clear();
        FsmStateName = d.StringASCII("state");
        int count = d.NumberI32("orders");
        for (int i = 0; i < count; i++)
        {
            // 读取顺序必须与 Serialize 写入逐位一致(type/target/px/pz/force/queued/ox/oz)——
            // BinaryDeserializer 是位置流,对象初始化器把 target 拖到最后会整体错位。
            var o = new UnitOrder { Type = d.StringASCII("type") };
            uint t = d.NumberU32("target");
            o.Target = t != 0 ? new EntityId(t) : null;
            o.Position = new FixedVector2D(d.NumberFixed("px"), d.NumberFixed("pz"));
            o.Force = d.Bool("force");
            o.Queued = d.Bool("queued");
            o.OffsetX = d.NumberFixed("ox").ToFloat();
            o.OffsetZ = d.NumberFixed("oz").ToFloat();
            o.AllowCapture = d.Bool("allowcap");
            _orderQueue.AddLast(o);
        }
        IsGarrisoned = d.Bool("garrisoned");
        IsTurret = d.Bool("turret");
        uint fctrl = d.NumberU32("formationController");
        FormationController = fctrl != 0 ? new EntityId(fctrl) : null;
        IsFormationController = d.Bool("isFormationController");
        _formationTimerElapsed = d.NumberFixed("formationTimer").ToFloat();
        Stance = d.StringASCII("stance");
        bool heldValid = d.Bool("heldValid");
        var heldX = d.NumberFixed("heldX");
        var heldZ = d.NumberFixed("heldZ");
        _heldPosition = heldValid ? new FixedVector2D(heldX, heldZ) : null;
        _stanceScanElapsed = d.NumberFixed("stanceScan").ToFloat();
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
