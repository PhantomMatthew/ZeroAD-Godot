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
    /// <summary>编队控制器订单负载(原版 order.data.returningState):MEMBER 等待完毕
    /// 后回到的状态名(WALKINGANDFIGHTING 等);null = 直接 FinishOrder。</summary>
    public string? ReturningState;
    /// <summary>Repair 单负载(原版 order.data.autocontinue):修完/建完且队列空时
    /// 就近(64m)找同属主未建成地基续建。GUI/集结点单 true,AI 单 false(原版 AI
    /// 显式禁)。</summary>
    public bool AutoContinue;
    /// <summary>Trade 单负载(原版 order.data.route):集结点贸易的前导 walk 点折叠成的
    /// 航线 waypoints,每程往返都依序经过;null = 直航。走向第二市场时反转(原版
    /// waypoints.reverse())。</summary>
    public List<FixedVector2D>? Route;
    /// <summary>Route 消费游标(原版 this.waypoints 的弹出进度)。</summary>
    public int RouteIndex;
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

    /// <summary>Pickup 接送的持有者(乘客侧;原版 UnitAI.js this.pickup):
    /// 进入 GARRISON.APPROACHING 且持有者 CanPickup 时登记并通知持有者
    /// (OnPickupRequested);离开 APPROACHING 任意出口发 OnPickupCanceled
    /// (取消兼作完成握手——乘客入驻成功也走它)。</summary>
    public EntityId? PickupHolder { get; private set; }

    /// <summary>清 pickup 登记并通知持有者(原版 leave 段的 PostMessage(PickupCanceled))。</summary>
    public void ClearPickup(ComponentManager? cm)
    {
        if (PickupHolder is not { } holder) return;
        PickupHolder = null;
        if (cm != null)
            cm.QueryInterface<UnitAIComponent>(holder)?.OnPickupCanceled(Entity);
    }

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

    /// <summary>炮塔站姿(原版 UnitAI.SetTurretStance 全量,UnitAI.js:6187-6200):
    /// SetImmobile + 强制切到首个 respondStandGround 站姿(standground)——塔上单位
    /// 原地自动接敌,不追不动;旧站姿存底,下塔还原。</summary>
    private string? _previousStance;
    public void SetTurretStance(ComponentManager cm)
    {
        IsTurret = true;
        StopMoving(this);
        if (CurrentStanceFlags.RespondStandGround) return;
        _previousStance = Stance;
        SetStance("standground", cm);
    }

    /// <summary>Port of ResetTurretStance:SetMobile + 还原旧站姿。</summary>
    public void ResetTurretStance(ComponentManager cm)
    {
        IsTurret = false;
        if (_previousStance == null) return;
        SetStance(_previousStance, cm);
        _previousStance = null;
    }

    // --- 编队(Formation.js 联动;MS5 落地) ---

    /// <summary>Port of UnitAI.formationController:所属编队控制器;null = 不在编队。</summary>
    public EntityId? FormationController { get; private set; }

    /// <summary>本实体是编队控制器(模板 UnitAI/FormationController=true 路径,
    /// <see cref="InitAsFormationController"/> 设置)。控制器的 IDLE 也要处理 Timer
    /// (定期重排),见 Tick 门。</summary>
    public bool IsFormationController { get; private set; }

    /// <summary>原版 UnitAI.LeaveFoundation(地基开工挤出,UnitAI.js:4269-4278):
    /// 拒走矩阵:非盟友的敌方非动物单位(停战外)不走、打包中/可打包/不可动不走;
    /// 已在 4m 外不走;否则走出地基(向背离地基中心方向撤到 半对角+4m)。
    /// 近似记录:敌方判定用互敌(原版 IsOwnedByAllyOfEntity 反面);可打包用 Pack 件。</summary>
    public void LeaveFoundation(ComponentManager cm, EntityId foundation)
    {
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        var fown = cm.QueryInterface<OwnershipComponent>(foundation);
        if (own != null && fown != null && fown.PlayerId > 0
            && !cm.Players.GetMutualAllies(own.PlayerId).Contains(fown.PlayerId)
            && own.PlayerId != fown.PlayerId)
        {
            // 敌方单位(非动物)在停战外不离开(原版:他们有权留下打建造者)。
            var identity = cm.QueryInterface<IdentityComponent>(Entity);
            bool isAnimal = identity != null && identity.MatchesClassList("Animal");
            if (!isAnimal && !cm.EndGame.CeasefireActive)
                return;
        }
        var pack = cm.QueryInterface<PackComponent>(Entity);
        if (pack != null && (pack.Packed || pack.Packing)) return;
        var motion = cm.QueryInterface<UnitMotion>(Entity);
        if (motion == null || IsGarrisoned || IsTurret) return;

        var pos = cm.QueryInterface<PositionComponent>(Entity);
        var fpos = cm.QueryInterface<PositionComponent>(foundation);
        if (pos == null || fpos == null) return;
        var obs = cm.QueryInterface<ObstructionComponent>(foundation);
        float halfDiag = obs != null ? obs.GetSize().ToFloat() : 2f;

        float dx = pos.Position.X.ToFloat() - fpos.Position.X.ToFloat();
        float dz = pos.Position.Z.ToFloat() - fpos.Position.Z.ToFloat();
        float d = MathF.Sqrt(dx * dx + dz * dz);
        // 已在界外(半对角 + 4m,原版 g_LeaveFoundationRange)不走。
        if (d >= halfDiag + 4f) return;
        if (d < 0.01f) { dx = 1f; dz = 0f; d = 1f; }   // 正中重合:向 +X 撤
        float esc = halfDiag + 4f;
        Walk(new FixedVector2D(
            Fixed.FromFloat(fpos.Position.X.ToFloat() + dx / d * esc),
            Fixed.FromFloat(fpos.Position.Z.ToFloat() + dz / d * esc)), queued: false);
    }

    // --- Pickup 接送(运输侧;原版 UnitAI.js OnPickupRequested/OnPickupCanceled/
    // HasPickupOrder + Order.PickupUnit + INDIVIDUAL.PICKUP 双子态) ---

    /// <summary>原版 HasPickupOrder:队列里有接该乘客的 PickupUnit 单。</summary>
    public bool HasPickupOrder(EntityId passenger)
    {
        foreach (var o in _orderQueue)
            if (o.Type == "PickupUnit" && o.Target == passenger) return true;
        return false;
    }

    /// <summary>原版 OnPickupRequested:已有接该乘客的单 → 忽略;否则在强制前缀后
    /// 插入 PickupUnit(PushOrderAfterForced——玩家当前的强制单不被抢断)。</summary>
    public void OnPickupRequested(EntityId passenger)
    {
        if (HasPickupOrder(passenger)) return;
        var order = new UnitOrder { Type = "PickupUnit", Target = passenger, Force = true };
        // 强制前缀后插入(队列首 = 当前执行单)。
        var node = _orderQueue.First;
        while (node != null && node.Value.Force && node.Value.Type == "PickupUnit")
            node = node.Next;   // 连续 PickupUnit 强制链排尾(同目标去重先行)
        if (node != null && node.Value.Force)
        {
            _orderQueue.AddAfter(node, order);
        }
        else
        {
            _orderQueue.AddFirst(order);
        }
    }

    /// <summary>原版 OnPickupCanceled:当前单是该乘客的接送 → FinishOrder;
    /// 在队 → 摘除。入驻成功也走这条路(取消 = 完成握手)。</summary>
    public void OnPickupCanceled(EntityId passenger)
    {
        var node = _orderQueue.First;
        while (node != null)
        {
            if (node.Value.Type == "PickupUnit" && node.Value.Target == passenger)
            {
                if (node == _orderQueue.First)
                {
                    FinishOrder();
                }
                else
                {
                    _orderQueue.Remove(node);
                }
                return;
            }
            node = node.Next;
        }
    }

    /// <summary>Port of UnitAI.SetFormationController(由 FormationComponent.SetMembers/
    /// AddMembers 调用)。原版同款把 Obstruction ControlGroup 切到控制器
    /// (UnitAI.js:5427-5432:编队成员互不阻挡/互推;离队还原为自身 id)。</summary>
    public void SetFormationController(EntityId controller)
    {
        FormationController = controller;
        SimSystem.GetComponent<ObstructionComponent>(Entity)?.SetControlGroup(controller.Value);
    }

    /// <summary>Port of UnitAI.UnsetFormationController:清链接并派 FormationLeave
    /// FSM 消息(FORMATIONMEMBER 树:停走/丢 FormationWalk 回 INDIVIDUAL.IDLE;
    /// INDIVIDUAL 树:仅 LeaveFormation 指令收尾,该指令未移植 → 空操作)。</summary>
    public void UnsetFormationController()
    {
        FormationController = null;
        // 还原控制组为自身(原版 UnsetFormationController:SetControlGroup(this.entity))。
        SimSystem.GetComponent<ObstructionComponent>(Entity)?.SetControlGroup(Entity.Value);
        s_fsm.ProcessMessage(this, new FsmMessage { Type = "FormationLeave", Cm = SimSystem.Sim }, "FormationLeave");
    }

    /// <summary>Port of UnitAI.CanUseFormation:模板 UnitAI/Formations 列表含该阵型
    /// (shape 为短名如 "box";列表 token 是全名 special/formations/{shape})。
    /// <![CDATA[<Formations disable=""/>]]> 的 support 系、无列表的攻城器/船 → false:
    /// 这些单位不进编队成员表、也不计 RequiredMemberCount(阵型面板同规则置灰)。</summary>
    public bool CanUseFormation(ComponentManager cm, string shape)
    {
        var identity = cm.QueryInterface<IdentityComponent>(Entity);
        if (identity == null) return false;
        Content.TemplateStats? stats = null;
        try { stats = cm.Templates?.ExtractStats(identity.TemplateName); } catch { }
        if (stats == null || stats.FormationShapes.Length == 0) return false;
        string full = "special/formations/" + shape;
        foreach (var tok in stats.FormationShapes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(tok, full, StringComparison.Ordinal)) return true;
        return false;
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

    /// <summary>Repair / build a foundation. Mirrors UnitAI.Repair(target,queued);
    /// autocontinue 对齐原版(GUI/集结点末点 true → 完工就近续建;AI 显式 false)。</summary>
    public void Repair(EntityId target, bool queued = false, bool autocontinue = true)
    {
        PushOrder(new UnitOrder { Type = "Repair", Target = target, Queued = queued, Force = true, AutoContinue = autocontinue });
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
    /// <summary>原版 UnitAI.SwitchMarketOrder:航线订单目标 old → new(迷雾镜像互换;
    /// 当前订单与排队订单都改)。</summary>
    public void SwitchMarketOrder(EntityId oldMarket, EntityId newMarket)
    {
        var cur = CurrentOrder;
        if (cur is { Type: "Trade" } && cur.Target == oldMarket)
            _orderQueue.First!.Value = cur with { Target = newMarket };
        var node = _orderQueue.First;
        while (node != null)
        {
            if (node.Value.Type == "Trade" && node.Value.Target == oldMarket)
                node.Value = node.Value with { Target = newMarket };
            node = node.Next;
        }
    }

    /// <summary>原版 UnitAI.MarketRemoved:市场没了——摘掉指向它的 Trade 订单;
    /// 当前订单指向它 → FinishOrder(航线中断由 Trader.RemoveMarket 的字段前移兜底)。</summary>
    public void MarketRemoved(ComponentManager cm, EntityId market)
    {
        var node = _orderQueue.First;
        while (node != null)
        {
            var next = node.Next;
            if (node.Value.Type == "Trade" && node.Value.Target == market)
                _orderQueue.Remove(node);
            node = next;
        }
        var cur = CurrentOrder;
        if (cur is { Type: "Trade" } && cur.Target == market)
            FinishOrder();
    }

    public void Trade(EntityId? market, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "Trade", Target = market, Queued = queued });

    /// <summary>原版 UnitAI.SetupTradeRoute(集结点贸易链):建双市场路由
    /// (Trader.SetTargetMarket(target, source))+ Trade 订单带航线 waypoints。
    /// 不可贸易 → 退化走向目标(原版 WalkToTarget);路由未变更 → 不下单;
    /// 双市场齐 → AddOrder Trade(force:false,原版同款,可被受击响应打断);
    /// 否则走向首市场待命(原版 else 分支 WalkToTarget(firstMarket))。
    /// 已知差异(不移植):AI BackToWork 捷径(UnitAI.js:6006-6019,workOrders 未移植)
    /// 与编队控制器分支(CallMemberFunction AddOrder Trade + Disband,6041-6047)。</summary>
    public void SetupTradeRoute(ComponentManager cm, EntityId target, EntityId? source,
        List<FixedVector2D>? route, bool queued)
    {
        var trader = cm.QueryInterface<TraderComponent>(Entity);
        if (trader == null || !trader.CanTrade(cm, target))
        {
            var pos = cm.QueryInterface<PositionComponent>(target);
            if (pos != null)
                Walk(new FixedVector2D(pos.Position.X, pos.Position.Z), queued);
            return;
        }
        if (!trader.SetTargetMarket(cm, target, source)) return;
        if (trader.HasBothMarkets())
        {
            PushOrder(new UnitOrder
            { Type = "Trade", Target = trader.FirstMarket, Queued = queued, Route = route });
        }
        else if (trader.FirstMarket is { } first)
        {
            // 单市场:走向首市场待命(原版 else 分支;第二市场设好后由后续命令接续)。
            var pos = cm.QueryInterface<PositionComponent>(first);
            if (pos != null)
                Walk(new FixedVector2D(pos.Position.X, pos.Position.Z), queued);
        }
    }

    /// <summary>原版 UnitAI.CancelSetupTradeRoute:摘掉待定首市场(仅单市场可摘,
    /// Trader.RemoveTargetMarket);编队控制器广播成员(原版 CallMemberFunction;
    /// 控制器无 Trader 件 → 整单无效,原版同款早退)。</summary>
    public void CancelSetupTradeRoute(ComponentManager cm, EntityId target)
    {
        var trader = cm.QueryInterface<TraderComponent>(Entity);
        if (trader == null) return;
        trader.RemoveTargetMarket(cm, target);
        if (!IsFormationController) return;
        var formation = cm.QueryInterface<FormationComponent>(Entity);
        if (formation == null) return;
        foreach (var member in formation.Members)
            if (member != Entity)
                cm.QueryInterface<UnitAIComponent>(member)?.CancelSetupTradeRoute(cm, target);
    }
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

    /// <summary>攻击移动(原版 WalkAndFight;上游 Ctrl+点击):走向目标点,沿途发现敌人
    /// (stance 允许时)前插攻击订单,打完继续走向目的地。</summary>
    public void WalkAndFight(FixedVector2D target, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "WalkAndFight", Position = target, Queued = queued, Force = !queued });

    /// <summary>巡逻(原版 Patrol;上游 P+点击):在"下单时位置 ↔ 目标点"间往返,
    /// 沿途按 stance 索敌。</summary>
    public void Patrol(FixedVector2D target, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "Patrol", Position = target, Queued = queued, Force = !queued });

    /// <summary>就近采集(原版 GatherNearPosition / gather-near-position 命令):
    /// 采集离目标点最近的资源(AI/集结点采集用)。</summary>
    public void GatherNearPosition(FixedVector2D position, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "GatherNearPosition", Position = position, Queued = queued, Force = !queued });

    /// <summary>逃跑(原版 Flee):背离威胁奔跑 FleeDistance;动物/被动站姿的受击响应。</summary>
    public void Flee(EntityId threat, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "Flee", Target = threat, Queued = queued, Force = !queued });

    // =========================================================================
    // 动物行为(原版 UnitAI.js 的 LINGERING/ROAMING + IsAnimal=RoamDistance>0)──
    // =========================================================================

    /// <summary>原版判定:RoamDistance > 0 即动物(template_unit_fauna 系列)。</summary>
    public bool IsAnimal => RoamDistance > 0f;

    /// <summary>模板 UnitAI 参数(装配时从 TemplateStats 灌入;秒)。RoamDistance>0 即动物。</summary>
    public float RoamDistance;
    public float RoamTimeMin = 2f, RoamTimeMax = 8f;
    public float FeedTimeMin = 15f, FeedTimeMax = 60f;

    // 游荡循环状态(序列化;读档后节拍一致)。
    private float _animalTimer = -1f;     // 当前阶段剩余秒;-1 = 未初始化(首拍取随机进食)
    private bool _animalFeeding = true;   // true=进食(LINGERING),false=游走等待(ROAMING)
    private float _roamAngle;             // MoveRandomly 的多边形步进角(±π/6)
    private float _roamStartAngle;
    private bool _roamAngleInit;
    private bool _isCorpse;               // 尸体(见 OnCorpseConverted):全行为停摆

    private void AnimalIdleTick(float dt, ComponentManager cm)
    {
        if (_animalTimer < 0f)
        {
            // 原版开局先进 LINGERING 且随机时长——避免全图动物同步起步。
            _animalFeeding = true;
            _animalTimer = RandRange(cm, FeedTimeMin, FeedTimeMax);
            return;
        }
        _animalTimer -= dt;
        if (_animalTimer > 0f) return;
        if (_animalFeeding)
        {
            // 进食结束 → 游走一圈(原版 ROAMING.enter:MoveRandomly + RoamTime 计时)。
            _animalFeeding = false;
            MoveRandomly(cm);
            _animalTimer = RandRange(cm, RoamTimeMin, RoamTimeMax);
        }
        else
        {
            _animalFeeding = true;
            _animalTimer = RandRange(cm, FeedTimeMin, FeedTimeMax);
        }
    }

    /// <summary>原版 MoveRandomly:近似多边形的圆周游走——每边先半转面向再半转,
    /// 边长 0.5~1.5×RoamDistance(防卡死角 + 防全图漂移)。RNG 走 cm.RNG(确定性)。</summary>
    private void MoveRandomly(ComponentManager cm)
    {
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        if (pos == null || !pos.InWorld) return;
        if (cm.QueryInterface<UnitMotion>(Entity) == null) return;

        float ang = pos.Rotation.Y.ToFloat();
        if (!_roamAngleInit)
        {
            _roamAngleInit = true;
            _roamAngle = (cm.RNG.NextInt(0, 2) == 0 ? 1 : -1) * MathF.PI / 6f;
            ang -= _roamAngle / 2f;
            _roamStartAngle = ang;
        }
        else if (MathF.Abs((ang - _roamStartAngle + MathF.PI) % (2f * MathF.PI) - MathF.PI)
                 < MathF.Abs(_roamAngle / 2f))
            _roamAngle *= cm.RNG.NextInt(0, 2) == 0 ? 1 : -1;

        float halfDelta = RandRange(cm, _roamAngle / 4f, _roamAngle * 3f / 4f);
        // 原版先半转(FaceTowardsPoint)再半转;视觉上移动朝向由表现层随走位刷新。
        ang += halfDelta;
        ang += halfDelta;
        float dist = RandRange(cm, 0.5f, 1.5f) * RoamDistance;
        _ = dist;   // 原版 target = pos - [sin,cos]·0.5(见下),dist 备将来边长用
        // 游走方向:定点 sincos(libm 的 Sin/Cos 跨平台低位不同 → 目标点漂移 → OOS)。
        Trig.SinCosApprox(Maths.Fixed.FromFloat(ang), out Maths.Fixed roamSin, out Maths.Fixed roamCos);
        float tx = pos.Position.X.ToFloat() - 0.5f * roamSin.ToFloat();
        float tz = pos.Position.Z.ToFloat() - 0.5f * roamCos.ToFloat();
        // 游走用排队单(Force=false):不抢占受击/逃跑等强制响应。
        PushOrder(new UnitOrder
        {
            Type = "Walk",
            Position = new FixedVector2D(Fixed.FromFloat(tx), Fixed.FromFloat(tz)),
            Force = false,
            Queued = true,
        });
    }

    private static float RandRange(ComponentManager cm, float min, float max)
    {
        if (max <= min) return min;
        return min + (float)cm.RNG.NextDouble() * (max - min);
    }

    /// <summary>死亡转尸体(SimBridge.RemoveDeadEntities 调用;仅 killBeforeGather 的
    /// gaia 动物):停单/停走/停攻,FSM 永久停摆(Tick 首行查 _isCorpse)。实体保留
    /// Position/Identity/ResourceSupply,尸体继续供采集(原版行为)。</summary>
    public void OnCorpseConverted(ComponentManager cm)
    {
        _isCorpse = true;
        _orderQueue.Clear();
        _dispatchPending = false;
        cm.QueryInterface<UnitMotion>(Entity)?.Stop();
        cm.QueryInterface<AttackComponent>(Entity)?.StopAttacking();
    }


    /// <summary>护卫(原版 Guard):跟随友方目标并响应其周边战斗;目标受伤时可治疗者自动治疗。
    /// 模板 UnitAI/CanGuard=false(动物/攻城器/船/商队)→ 退化走向目标(原版 WalkToTarget 回退)。</summary>
    public void Guard(EntityId target, bool queued = false)
    {
        if (!CanGuard(SimSystem.Sim))
        {
            var pos = SimSystem.Sim?.QueryInterface<PositionComponent>(target);
            if (pos != null)
                Walk(new FixedVector2D(pos.Position.X, pos.Position.Z), queued);
            return;
        }
        PushOrder(new UnitOrder { Type = "Guard", Target = target, Queued = queued, Force = !queued });
    }

    /// <summary>原版 UnitAI.CanGuard:编队控制器恒可(成员各自判断);否则模板
    /// UnitAI/CanGuard == "true"。无模板库(纯内核测试)→ 按 template_unit 默认 true。</summary>
    public bool CanGuard(ComponentManager? cm)
    {
        if (IsFormationController) return true;
        var identity = cm?.QueryInterface<IdentityComponent>(Entity);
        if (identity == null) return true;
        Content.TemplateStats? stats = null;
        try { stats = cm!.Templates?.ExtractStats(identity.TemplateName); } catch { }
        return stats?.CanGuard ?? true;
    }

    /// <summary>原版 UnitAI.IsGuardOf:我正在护卫的目标(无 = null)。</summary>
    public bool IsGuardOf(EntityId target) => _isGuardOf == target;

    /// <summary>原版 UnitAI.RemoveGuard:停卫(清 _isGuardOf + 结束 Guard 订单)。</summary>
    public void RemoveGuard(ComponentManager cm)
    {
        if (_isGuardOf is { } g)
            cm.QueryInterface<GuardComponent>(g)?.RemoveGuard(Entity);
        _isGuardOf = null;
        if (CurrentOrder?.Type == "Guard")
            FinishOrder();
    }

    /// <summary>原版 UnitAI.js GuardedAttacked(4536+ 与 GUARDING 态 1609+ 两个处理点合并):
    /// 被护卫者受击时——Guard 订单前有 force 订单 → 不动;已在打别的活目标 → 先打完;
    /// 我是 Support 且被护方受伤 → 治/修它;攻击者是建筑(BuildingAI)且我可修 → 修被护方;
    /// 否则反击攻击者(可见 → Attack 前插;不可见 → WalkAndFight 到其位置前插)。</summary>
    public void OnGuardedAttacked(ComponentManager cm, EntityId guarded, EntityId attacker)
    {
        if (_isGuardOf != guarded) return;   // 不是我护卫的(簿记漂移兜底)
        // Guard 订单前有 force 订单 → 不动(原版同款队列扫描)。
        foreach (var o in _orderQueue)
        {
            if (o.Type == "Guard") break;
            if (o.Force) return;
        }
        // 正在打别的活目标 → 先打完(原版:target != attacker 且可攻击 → 保持)。
        var curOrder = CurrentOrder;
        if (curOrder?.Type is "Attack" or "WalkAndFight"
            && curOrder.Target is { } cur && cur != attacker)
        {
            var curEnt = cm.QueryInterface<HealthComponent>(cur);
            if (curEnt is { Current: > 0 })
            {
                var atkPos = cm.QueryInterface<PositionComponent>(attacker);
                if (atkPos != null) return;
            }
        }

        // Support 且被护方受伤 → 治/修(原版同款优先级)。
        var identity = cm.QueryInterface<IdentityComponent>(Entity);
        var guardedHealth = cm.QueryInterface<HealthComponent>(guarded);
        if (identity != null && identity.HasClass("Support")
            && guardedHealth != null && guardedHealth.Current < guardedHealth.Max)
        {
            var heal = cm.QueryInterface<HealComponent>(Entity);
            if (heal != null && heal.CanHeal(cm, guarded))
                PushOrderFront(new UnitOrder { Type = "Heal", Target = guarded });
            else if (cm.QueryInterface<BuilderComponent>(Entity) != null)
                PushOrderFront(new UnitOrder { Type = "Repair", Target = guarded });
            return;
        }

        // 攻击者是建筑(有 BuildingAI)且我可修 → 修被护方(原版同款)。
        if (cm.QueryInterface<BuildingAIComponent>(attacker) != null
            && cm.QueryInterface<BuilderComponent>(Entity) != null
            && guardedHealth != null && guardedHealth.Current < guardedHealth.Max)
        {
            PushOrderFront(new UnitOrder { Type = "Repair", Target = guarded });
            return;
        }

        // 反击:可见 → Attack;不可见 → WalkAndFight 到攻击者位置(原版同款分支)。
        if (CheckTargetVisible(this, attacker, cm))
            PushOrderFront(new UnitOrder { Type = "Attack", Target = attacker });
        else
        {
            var atkPos = cm.QueryInterface<PositionComponent>(attacker);
            if (atkPos == null || !atkPos.InWorld) return;
            PushOrderFront(new UnitOrder
            {
                Type = "WalkAndFight",
                Position = new Maths.FixedVector2D(atkPos.Position.X, atkPos.Position.Z),
                Target = attacker,
            });
            // 队列已有 WalkAndFight → 只留最新(原版 splice 语义)。
            if (_orderQueue.Count > 1)
            {
                var second = _orderQueue.First!.Next;
                if (second != null && second.Value.Type == "WalkAndFight"
                    && second.Value.Target != attacker)
                    _orderQueue.Remove(second);
            }
        }
    }

    /// <summary>编队走位(原版 ArrangeFormation → AddOrder("FormationWalk", {target,x,z},
    /// !force)):target=控制器,x/z=未旋转偏移。由 FormationComponent.ArrangeFormation
    /// 发放;force=true → queued=false(替换成员队列)。</summary>
    public void FormationWalk(EntityId controller, float offsetX, float offsetZ, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "FormationWalk", Target = controller, OffsetX = offsetX, OffsetZ = offsetZ, Queued = queued });

    /// <summary>返回资源到指定投放站(原版 ReturnResource;右键投放站/控制器广播用)。</summary>
    public void ReturnResource(EntityId target, bool queued = false) =>
        PushOrder(new UnitOrder { Type = "ReturnResource", Target = target, Queued = queued, Force = !queued });

    /// <summary>就近交付(原版 DropAtNearestDropSite):找最近接收所携类型的投放站交付。</summary>
    public void DropAtNearestDropSite(bool queued = false) =>
        PushOrder(new UnitOrder { Type = "DropAtNearestDropSite", Queued = queued, Force = !queued });

    /// <summary>退出编队(原版 LeaveFormation):成员脱离所属编队控制器。</summary>
    public void LeaveFormation() =>
        PushOrder(new UnitOrder { Type = "LeaveFormation", Force = true });

    // =========================================================================
    // Order queue mechanics — port of UnitAI PushOrder / PushOrderFront / FinishOrder.
    // =========================================================================

    private void PushOrder(UnitOrder order)
    {
        if (!order.Queued && _orderQueue.Count > 0)
        {
            // Replace: clear the queue, then add the new order as the sole item.
            _orderQueue.Clear();
            // 巡逻起点/护卫目标随订单链替换而作废(原版 Patrol.leave 删除 patrolStartPosOrder)。
            _patrolStart = null;
            if (_isGuardOf is { } g && SimSystem.Sim != null)
                SimSystem.Sim.QueryInterface<GuardComponent>(g)?.RemoveGuard(Entity);
            _isGuardOf = null;
        }
        _orderQueue.AddLast(order);
        // The Order.<Type> FSM handler runs on the next Tick (which has the ComponentManager
        // the handlers need for component lookups). Mark that a dispatch is pending.
        _dispatchPending = true;
    }

    /// <summary>前插订单(原版 PushOrderFront):不清队列,插在队首——WAF/巡逻/护卫
    /// 发现敌人时前插 Attack,攻击结束后队列下一条即原订单,自动恢复("returningState"
    /// 语义的队列式实现)。</summary>
    private void PushOrderFront(UnitOrder order)
    {
        _orderQueue.AddFirst(order);
        _dispatchPending = true;
    }

    private bool _dispatchPending;

    // ── WAF/巡逻/护卫状态负载(序列化见 Serialize/Deserialize 尾部)──
    /// <summary>巡逻起点(原版 patrolStartPosOrder;首个 Patrol 订单下单时锚定,往返用)。</summary>
    private FixedVector2D? _patrolStart;
    /// <summary>护卫对象(原版 isGuardOf)。</summary>
    private EntityId? _isGuardOf;
    /// <summary>巡逻路点已等待秒数(原版 stopSurveying,模板 PatrolWaitTime=1s)。</summary>
    private float _patrolWaitElapsed;
    /// <summary>COMBAT.CHASING 的 1s 重查节流(原版 StartTimer(1000,1000))。</summary>
    private float _chaseElapsed;
    /// <summary>WAF/巡逻/护卫的索敌节流器(与 StanceIdleScan 同款 1s)。</summary>
    private float _combatScanElapsed;
    /// <summary>编队控制器绕障跳跃冷却(原版 obstructionMitigationAttempted + 5s
    /// SetTimeout 复位;UnitAI.js:6786-6838)。瞬态不入档(原版同:Timer 状态不序列化)。</summary>
    private float _obstructionMitigationCooldown;

    private void FinishOrder()
    {
        if (_orderQueue.Count > 0)
            _orderQueue.RemoveFirst();
        // The next order (if any) is dispatched on the next Tick, which has the ComponentManager.
        _dispatchPending = _orderQueue.Count > 0;
        if (_orderQueue.Count == 0)
        {
            // 原版 FinishOrder 末段:编队成员完成个体任务 → 回报控制器
            // (SetFinishedEntity,供控制器 MEMBER 态等待)+ 回 FORMATIONMEMBER.IDLE。
            if (FormationController is { } ctrl && !IsFormationController)
            {
                var cm = SimSystem.Sim;
                cm?.QueryInterface<FormationComponent>(ctrl)?.SetFinishedEntity(cm, Entity);
                s_fsm.SetNextState(this, "FORMATIONMEMBER.IDLE");
                return;
            }
            s_fsm.SetNextState(this, "IDLE");
        }
    }

    // Feed the front order into the FSM as an Order.<Type> message. Called from Tick (which
    // owns the ComponentManager the handlers need). The FSM's Order handler for the current
    // state decides whether to accept (and transition) or reject (FinishOrder).
    private void DispatchFrontOrder(ComponentManager cm)
    {
        if (_orderQueue.First is not { } node) return;
        var order = node.Value;
        long t0 = ProfSw.ElapsedTicks;
        s_fsm.ProcessMessage(this, new FsmMessage { Type = order.Type, Order = order, Cm = cm }, "Order." + order.Type);
        long cost = ProfSw.ElapsedTicks - t0;
        ProfOrderMs.TryGetValue(order.Type, out long acc);
        ProfOrderMs[order.Type] = acc + cost;
        ProfOrderCount.TryGetValue(order.Type, out long cnt);
        ProfOrderCount[order.Type] = cnt + 1;
        // 处理器内可能 FinishOrder+PushOrderFront(如 DropAtNearestDropSite 前插
        // ReturnResource):队首已换 → 保持派发标记,下拍续派新首;无条件 false 会把
        // 新单闷在 IDLE(随后 Timer 打进无 handler 的 IDLE 抛异常)。
        // 必须引用比较:UnitOrder 是 record(值相等),两条内容相同的相邻订单
        // (连点同一棵树/同一集合点连发)用 != 会误判"队首未变"把新单闷死。
        _dispatchPending = _orderQueue.Count > 0 && !ReferenceEquals(_orderQueue.First!.Value, order);
    }

    /// <summary>Current order (front of queue), or null if idle.</summary>
    public UnitOrder? CurrentOrder => _orderQueue.First?.Value;

    /// <summary>订单队列快照(测试观测口;非热路径,勿在 tick 内用)。</summary>
    public IReadOnlyList<UnitOrder> OrderQueueSnapshot => new List<UnitOrder>(_orderQueue);

    // =========================================================================
    // Tick — driven once per sim turn by the presentation layer.
    // =========================================================================

    public void Tick(float dt, ComponentManager cm)
    {
        ProfCalls++;
        if (_isCorpse) return;   // 尸体:FSM 全停(死亡转换见 OnCorpseConverted)
        // 驻防中:订单队列冻结(对齐原版 isGarrisoned 时 FinishOrder 不派发后续订单;
        // 新入队指令留待出驻后处理)。
        if (IsGarrisoned) return;
        if (_obstructionMitigationCooldown > 0f)
            _obstructionMitigationCooldown -= dt;   // 原版 5s SetTimeout 复位

        long t0 = ProfSw.ElapsedTicks;
        // Dispatch any newly-queued order first (the Order.X handler sets the active state).
        if (_dispatchPending)
            DispatchFrontOrder(cm);
        long t1 = ProfSw.ElapsedTicks;

        // 空闲 stance 行为(原版 IDLE.enter 的 FindNewTargets/FindSightedEnemies +
        // LosAttackRangeUpdate;我们以 1s 节流轮询替代 LOS 事件订阅)。编队成员/控制器
        // 不自行索敌(原版 FORMATIONMEMBER 无个体响应)。
        if (IsIdle && !IsGarrisoned && !IsTurret && FormationController == null && !IsFormationController)
            StanceIdleScan(dt, cm);
        // 动物游荡(原版 LINGERING/ROAMING:进食 FeedTime → 游走一圈 → 再进食)。
        // 仅空闲无单时驱动;受击/被猎产生的订单会抢占(Flee/Attack 入队即非 idle)。
        if (IsAnimal && IsIdle && _orderQueue.Count == 0 && !IsGarrisoned && !IsTurret
            && FormationController == null && !IsFormationController)
            AnimalIdleTick(dt, cm);
        // 扫描可能入队自动攻击/回锚订单:立即派发,否则下方 Timer 会打进无 handler 的
        // IDLE 态而抛异常(同"订单残留 IDLE"坑)。
        if (_dispatchPending)
            DispatchFrontOrder(cm);
        long t2 = ProfSw.ElapsedTicks;

        // Then let the FSM handle periodic checks via a Timer-style message. Per-state handlers
        // advance the active order (move-arrival polling, gather progress, attack cycles).
        // 编队控制器空闲时也要收 Timer(IDLE 定期重排,对齐原版控制器 IDLE 定时器)。
        if (!IsIdle || _orderQueue.Count > 0 || IsFormationController)
            s_fsm.ProcessMessage(this, new FsmMessage { Type = "Tick", Dt = dt, Cm = cm }, "Timer");
        long t3 = ProfSw.ElapsedTicks;
        ProfDispatch += t1 - t0; ProfScan += t2 - t1; ProfFsm += t3 - t2;
    }

    /// <summary>性能探针:Tick 分段耗时(Stopwatch ticks;SimBridge 聚合打印后清零)。</summary>
    public static long ProfDispatch, ProfScan, ProfFsm, ProfCalls;
    public static readonly System.Diagnostics.Stopwatch ProfSw = System.Diagnostics.Stopwatch.StartNew();
    /// <summary>按订单类型拆 dispatch 耗时(Stopwatch ticks)与次数。</summary>
    public static readonly System.Collections.Generic.Dictionary<string, long> ProfOrderMs = new();
    public static readonly System.Collections.Generic.Dictionary<string, long> ProfOrderCount = new();

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
        // 根作用域兜底(原版 UnitFsmSpec 顶层默认 handler):任何状态都能收攻击/停止令——
        // 编队成员在 FORMATIONMEMBER.* 下接敌必须能转个体 COMBAT,否则订单卡死队列。
        // 子树的同名注册(FORMATIONCONTROLLER 的 Order.Attack 等)覆盖根,语义不变。
        spec.Root.On("Order.Attack", (u, m) =>
        {
            var attack = m.Cm!.QueryInterface<AttackComponent>(u.Entity);
            if (attack == null || m.Order!.Target == null) { u.FinishOrder(); return; }
            if (!attack.AttackTarget(m.Cm!, m.Order.Target.Value, m.Order.AllowCapture))
            {
                u.FinishOrder();
                return;
            }
            u.FsmNextState = "COMBAT.APPROACHING";
        });
        spec.Root.On("Order.Stop", (u, _) =>
        {
            StopMoving(u);
            SimSystem.GetComponent<HealComponent>(u.Entity)?.StopHealing();
            SimSystem.GetComponent<TreasureCollectorComponent>(u.Entity)?.StopCollecting();
            u.FsmNextState = "IDLE";
        });
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

        // 攻击移动(原版 Order.WalkAndFight):锚定驻防点为目标点,走向目标并沿途索敌。
        ind.On("Order.WalkAndFight", (u, m) =>
        {
            if (m.Cm!.QueryInterface<UnitMotion>(u.Entity) == null) { u.FinishOrder(); return; }
            u._heldPosition = m.Order!.Position;   // 原版 SetHeldPosition(msg.data)
            StartMovingTo(u, m.Order.Position, m.Cm);
            u._combatScanElapsed = 0;              // 原版 StartTimer(0,1000):首拍立即扫描
            u.FsmNextState = "WALKINGANDFIGHTING";
        });

        // 巡逻(原版 Order.Patrol):首个巡逻订单锚定起点;走向目标点,到达后折返。
        ind.On("Order.Patrol", (u, m) =>
        {
            if (m.Cm!.QueryInterface<UnitMotion>(u.Entity) == null) { u.FinishOrder(); return; }
            if (u._patrolStart == null)
            {
                var pos = m.Cm.QueryInterface<PositionComponent>(u.Entity);
                if (pos == null) { u.FinishOrder(); return; }
                u._patrolStart = new FixedVector2D(pos.Position.X, pos.Position.Z);
            }
            StartMovingTo(u, m.Order!.Position, m.Cm);
            u._combatScanElapsed = 0;
            u.FsmNextState = "PATROL.PATROLLING";
        });

        // 逃跑(原版 Order.Flee):背离威胁奔跑 FleeDistance。
        ind.On("Order.Flee", (u, m) =>
        {
            if (m.Cm!.QueryInterface<UnitMotion>(u.Entity) == null) { u.FinishOrder(); return; }
            if (!StartFlee(u, m.Order!.Target, m.Cm)) { u.FinishOrder(); return; }
            u.FsmNextState = "FLEEING";
        });

        // 护卫(原版 Order.Guard):目标是友方存活实体才成立;在护卫半径内直接 GUARDING,
        // 否则 ESCORTING 追赶。
        ind.On("Order.Guard", (u, m) =>
        {
            if (m.Cm == null || m.Order!.Target is not { } t || t == u.Entity || !ShouldGuard(u, t, m.Cm))
            {
                u.FinishOrder();
                return;
            }
            u._isGuardOf = t;
            // 双向登记(原版 Guard.js AddGuard):被护方的 GuardComponent 记录我——
            // 其受击时转发通知我反击。目标缺组件时懒挂(原版全体单位/建筑自带)。
            var guard = m.Cm.QueryInterface<GuardComponent>(t);
            if (guard == null)
            {
                guard = new GuardComponent();
                m.Cm.AddComponent(t, guard);
            }
            guard.AddGuard(u.Entity);
            u._combatScanElapsed = 0;
            var guardRange = GuardRangeOf(u, m.Cm);
            if (InGuardRange(u, t, m.Cm, guardRange))
                u.FsmNextState = "GUARD.GUARDING";
            else if (m.Cm.QueryInterface<UnitMotion>(u.Entity) != null)
            {
                MoveToTargetEdge(u, t, m.Cm, guardRange);
                u.FsmNextState = "GUARD.ESCORTING";
            }
            else
                u.FinishOrder();
        });

        ind.On("Order.Gather", (u, m) =>
        {
            // 拒收路径一律 FinishOrder 出队(对齐原版,同 Order.Attack):仅置 IDLE 会让
            // 订单残留队列,同 Tick 的 Timer 在无 handler 的 IDLE 态抛出。
            var gatherer = m.Cm!.QueryInterface<ResourceGatherer>(u.Entity);
            if (gatherer == null) { u.FinishOrder(); return; }
            if (m.Order!.Target is { } target)
            {
                // 狩猎重定向(原版 killBeforeGather):活体动物先猎杀,死后采尸体——
                // 队列改为 [Attack, Gather]:Attack 在目标死亡后完成,接着 Gather 采尸体。
                var supply = m.Cm.QueryInterface<ResourceSupply>(target);
                if (supply != null && supply.KillBeforeGather
                    && m.Cm.QueryInterface<HealthComponent>(target) is { IsDead: false })
                {
                    // 目标不在属主视野(动物游走进雾):攻击单会被追击门取消,采集也无处
                    // 下手——整单取消,防 Attack↔Gather 乒乓(原版:雾里根本点不到目标)。
                    var ownEnt = m.Cm.QueryInterface<OwnershipComponent>(u.Entity);
                    if (ownEnt != null && SimSystem.Range != null
                        && SimSystem.Range.GetLosVisibility(target, ownEnt.PlayerId) != LosVisibility.Visible)
                    {
                        u.FinishOrder();
                        return;
                    }
                    u.FinishOrder();   // 弹出当前 Gather,下面以前插重建顺序
                    u.PushOrderFront(new UnitOrder
                        { Type = "Gather", Target = target, Force = m.Order.Force, Queued = m.Order.Queued });
                    u.PushOrderFront(new UnitOrder
                        { Type = "Attack", Target = target, Force = m.Order.Force });
                    return;
                }
                gatherer.TargetSupply = target;
                MoveToTargetEdge(u, target, m.Cm!, Fixed.FromInt(1));
            }
            u.FsmNextState = "GATHER.APPROACHING";
        });

        // 就近采集(原版 Order.GatherNearPosition):找离目标点最近的资源并采集;
        // 无资源 → 订单失败(原版同 FinishOrder)。
        ind.On("Order.GatherNearPosition", (u, m) =>
        {
            var gatherer = m.Cm!.QueryInterface<ResourceGatherer>(u.Entity);
            if (gatherer == null) { u.FinishOrder(); return; }
            var supply = FindSupplyNear(u, m.Cm, m.Order!.Position, specific: null, template: null);
            if (supply == null) { u.FinishOrder(); return; }
            gatherer.TargetSupply = supply;
            MoveToTargetEdge(u, supply.Value, m.Cm, Fixed.FromInt(1));
            gatherer.State = ResourceGatherer.GatherState.MovingToResource;
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
            // 目标门(原版 UnitAI.Repair 校验):未建成 foundation,或已建成但受损的
            // Repairable 实体;其余(满血/不可修/无件)拒收。
            var builder = m.Cm!.QueryInterface<BuilderComponent>(u.Entity);
            if (builder == null || m.Order!.Target == null) { u.FinishOrder(); return; }
            var target = m.Order.Target.Value;
            var foundation = m.Cm.QueryInterface<FoundationComponent>(target);
            bool validFoundation = foundation != null && !foundation.IsBuilt;
            bool validRepair = foundation == null
                && m.Cm.QueryInterface<RepairableComponent>(target) is { IsRepairable: true }
                && m.Cm.QueryInterface<HealthComponent>(target) is { IsInjured: true };
            if (!validFoundation && !validRepair) { u.FinishOrder(); return; }
            builder.Build(target);
            MoveToTarget(u, target, m.Cm!);
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

        // 返回资源(原版 Order.ReturnResource):携货才成立;在交付半径内就地交付,
        // 否则 RETURNRESOURCE.APPROACHING 接近。
        ind.On("Order.ReturnResource", (u, m) =>
        {
            var gatherer = m.Cm!.QueryInterface<ResourceGatherer>(u.Entity);
            if (gatherer == null || m.Order!.Target == null || gatherer.CarryAmount <= 0)
            {
                u.FinishOrder();
                return;
            }
            var target = m.Order.Target.Value;
            gatherer.TargetDropsite = target;
            if (WithinRange(u.Entity, target, m.Cm, GatherRange))
            {
                StopMoving(u);
                DepositResources(u.Entity, gatherer, m.Cm!);
                u.FinishOrder();
                return;
            }
            MoveToTargetEdge(u, target, m.Cm!, Fixed.FromInt(1));
            gatherer.State = ResourceGatherer.GatherState.MovingToDropsite;
            u.FsmNextState = "RETURNRESOURCE.APPROACHING";
        });

        // 就近交付(原版 Order.DropAtNearestDropSite):找最近投放站 → 前插 ReturnResource。
        ind.On("Order.DropAtNearestDropSite", (u, m) =>
        {
            var gatherer = m.Cm!.QueryInterface<ResourceGatherer>(u.Entity);
            if (gatherer == null) { u.FinishOrder(); return; }
            var dropsite = FindNearestDropsite(u.Entity, m.Cm!);
            if (!dropsite.HasValue) { u.FinishOrder(); return; }
            u.FinishOrder();   // 先出本单(再前插会误弹新单)
            u.PushOrderFront(new UnitOrder { Type = "ReturnResource", Target = dropsite, Force = true });
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
            // 原版 GARRISON.APPROACHING.enter 的 pickup 登记:持有者 CanPickup →
            // 通知持有者(它会插一单 PickupUnit 来接)。
            if (holder.CanPickup(m.Cm, u.Entity))
            {
                u.PickupHolder = t;
                m.Cm.QueryInterface<UnitAIComponent>(t)?.OnPickupRequested(u.Entity);
            }
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
            if (holder.CanPickup(m.Cm, u.Entity))
            {
                u.PickupHolder = t;
                m.Cm.QueryInterface<UnitAIComponent>(t)?.OnPickupRequested(u.Entity);
            }
            u.FsmNextState = "GARRISON.APPROACHING";
        });
        // 原版 Order.PickupUnit(持有者=本实体,目标=乘客):满员 → FinishOrder;
        // 乘客能自力到达且够近(<200m)→ LOADING 等;否则 APPROACHING 去接。
        ind.On("Order.PickupUnit", (u, m) =>
        {
            if (m.Order!.Target is not { } passenger) { u.FinishOrder(); return; }
            // 装填类型:驻军优先(GarrisonHolder+乘客 Garrisonable),否则炮塔对。
            var gh = m.Cm!.QueryInterface<GarrisonHolderComponent>(u.Entity);
            var th = gh == null ? m.Cm.QueryInterface<TurretHolderComponent>(u.Entity) : null;
            if (gh == null && th == null) { u.FinishOrder(); return; }
            bool full = gh != null
                ? (m.Cm.QueryInterface<GarrisonableComponent>(passenger) is { } g2
                    && gh.OccupiedSlots(m.Cm) + g2.TotalSize(m.Cm) > gh.GetCapacity(m.Cm))
                : !th!.TurretPoints.Exists(p2 => p2.Entity == null);
            if (full) { u.FinishOrder(); return; }
            float loadRange = gh?.LoadingRange ?? th!.LoadingRange;

            // 原版反查:乘客能自己走过来且直线距离 <200 → 不挪窝,原地 LOADING。
            var passengerMotion = m.Cm.QueryInterface<UnitMotion>(passenger);
            bool passengerCanReach = passengerMotion != null;
            var pf = SimSystem.Pathfinder;
            if (passengerCanReach && pf != null)
            {
                var pp = m.Cm.QueryInterface<PositionComponent>(passenger);
                var up = m.Cm.QueryInterface<PositionComponent>(u.Entity);
                if (pp != null && up != null)
                {
                    // 同陆区才可自力到达(原版 IsTargetRangeReachable 的可达性近似)。
                    passengerCanReach = pf.GetLandRegion(pp.Position.X, pp.Position.Z)
                        == pf.GetLandRegion(up.Position.X, up.Position.Z);
                }
            }
            float dist = -1f;
            var pp2 = m.Cm.QueryInterface<PositionComponent>(passenger);
            var up2 = m.Cm.QueryInterface<PositionComponent>(u.Entity);
            if (pp2 != null && up2 != null)
            {
                float dx = pp2.Position.X.ToFloat() - up2.Position.X.ToFloat();
                float dz = pp2.Position.Z.ToFloat() - up2.Position.Z.ToFloat();
                dist = MathF.Sqrt(dx * dx + dz * dz);
            }
            if (passengerCanReach && dist >= 0f && dist < 200f)
            {
                u.FsmNextState = "PICKUP.LOADING";
                return;
            }
            if (passengerMotion == null) { u.FinishOrder(); return; }   // 双方都动不了
            MoveToTargetEdge(u, passenger, m.Cm, Fixed.FromFloat(loadRange));
            u.FsmNextState = "PICKUP.APPROACHING";
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
            // 航线 waypoints(集结点折叠):先依序走路点,再贴近市场(原版
            // MoveToMarket 消费 this.waypoints)。
            m.Order.RouteIndex = 0;
            if (m.Order.Route is { Count: > 0 } route)
                StartMovingTo(u, route[0], m.Cm);
            else
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
        // FormationLeave(原版根处理器):仅收尾 LeaveFormation 指令。
        ind.On("FormationLeave", (u, _) =>
        {
            if (u.CurrentOrder?.Type == "LeaveFormation") u.FinishOrder();
        });
        // Order.LeaveFormation(原版根级指令,任何状态可用):脱离编队控制器。
        // RemoveMembers 内部会派 FormationLeave(上条 handler 已收尾本单)——此处
        // 仅在其未触发时兜底 FinishOrder,避免双弹出队。
        ind.On("Order.LeaveFormation", (u, m) =>
        {
            if (u.FormationController is { } fc)
                m.Cm!.QueryInterface<FormationComponent>(fc)
                    ?.RemoveMembers(m.Cm, new List<EntityId> { u.Entity });
            if (u.CurrentOrder?.Type == "LeaveFormation") u.FinishOrder();
        });

        // --- States ---

        // IDLE 兜底 Timer:订单残留在 IDLE(某路径 FinishOrder/状态推进失序)时重臂派发,
        // 自愈卡死——此前 IDLE 无 Timer handler,进 Timer 即抛异常并截断整个 sim tick
        // (其后的 foundations 等阶段全不跑,完工建筑永不落位)。
        spec.State("INDIVIDUAL").State("IDLE")
            .On("Timer", (u, m) =>
            {
                if (u._orderQueue.Count > 0) u._dispatchPending = true;
            });

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
            .Leave(u =>
            {
                // 原版 GARRISON.APPROACHING.leave:pickup 取消(兼作完成握手)+ 停走。
                if (u.PickupHolder is { } holder)
                {
                    u.ClearPickup(SimSystem.Sim);
                    _ = holder;
                }
                return false;
            })
            .On("Timer", (u, m) =>
            {
                // 原版 MovementUpdate 中止条件:持有者已无接送单且不在空闲 → 乘客放弃。
                if (u.PickupHolder is { } holder2)
                {
                    var hai = m.Cm!.QueryInterface<UnitAIComponent>(holder2);
                    if (hai == null || (!hai.HasPickupOrder(u.Entity) && !hai.IsIdle))
                    {
                        u.FinishOrder();
                        return;
                    }
                }
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
        // PICKUP 子树(原版 UnitAI.js INDIVIDUAL.PICKUP;运输侧——接送乘客):
        // APPROACHING 接近乘客至装填射程 → LOADING 原地等乘客上船。
        // PickupCanceled 不经 FSM 消息:乘客侧直调 OnPickupCanceled → FinishOrder。
        spec.State("INDIVIDUAL").State("PICKUP").State("APPROACHING")
            .On("Timer", (u, m) =>
            {
                if (u.CurrentOrder?.Target is not { } passenger) { u.FinishOrder(); return; }
                var pai = m.Cm!.QueryInterface<UnitAIComponent>(passenger);
                // 乘客已上船/上塔/死了 → 收单(防御;正常由取消握手先行)。
                if (pai == null || pai.IsGarrisoned || pai.IsTurret) { u.FinishOrder(); return; }
                float loadRange = m.Cm.QueryInterface<GarrisonHolderComponent>(u.Entity)?.LoadingRange
                    ?? m.Cm.QueryInterface<TurretHolderComponent>(u.Entity)?.LoadingRange ?? 2f;
                if (WithinRange(u.Entity, passenger, m.Cm, (int)System.Math.Ceiling(loadRange)))
                {
                    StopMoving(u);
                    u.FsmNextState = "PICKUP.LOADING";
                    return;
                }
                var motion = m.Cm.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget)
                    MoveToTargetEdge(u, passenger, m.Cm, Fixed.FromFloat(loadRange));
            });
        spec.State("INDIVIDUAL").State("PICKUP").State("LOADING")
            .On("Timer", (u, m) =>
            {
                if (u.CurrentOrder?.Target is not { } passenger) { u.FinishOrder(); return; }
                var pai = m.Cm!.QueryInterface<UnitAIComponent>(passenger);
                if (pai == null || pai.IsGarrisoned || pai.IsTurret) { u.FinishOrder(); return; }
                // 满员(期间别人先上了)→ 收单。
                var gh = m.Cm.QueryInterface<GarrisonHolderComponent>(u.Entity);
                var th = gh == null ? m.Cm.QueryInterface<TurretHolderComponent>(u.Entity) : null;
                if (gh == null && th == null) { u.FinishOrder(); return; }
                bool full = gh != null
                    ? (m.Cm.QueryInterface<GarrisonableComponent>(passenger) is { } g2
                        && gh.OccupiedSlots(m.Cm) + g2.TotalSize(m.Cm) > gh.GetCapacity(m.Cm))
                    : !th!.TurretPoints.Exists(p2 => p2.Entity == null);
                if (full) u.FinishOrder();
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
                        // 目标不可治疗/补满(原版 FINDINGNEWTARGET):视野内找
                        // 新伤员(CanHeal 过滤),无则收单。
                        {
                            var newTarget = FindNewHealTarget(u, heal, m.Cm!);
                            if (newTarget.HasValue)
                            {
                                u.Heal(newTarget.Value);   // 重发 Heal 指令换目标
                                break;
                            }
                            u.FinishOrder();
                        }
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
                // 航线 waypoints 消费(原版 APPROACHINGMARKET 的 waypoints 分支):
                // 逐点走近(2m 判到),走完后才贴近市场。
                var order = u.CurrentOrder;
                if (order.Route is { Count: > 0 } route && order.RouteIndex < route.Count)
                {
                    var self = m.Cm.QueryInterface<PositionComponent>(u.Entity);
                    var motion2 = m.Cm.QueryInterface<UnitMotion>(u.Entity);
                    if (self == null || motion2 == null) { u.FinishOrder(); return; }
                    var wp = route[order.RouteIndex];
                    long wdx = self.Position.X.InternalValue - wp.X.InternalValue;
                    long wdz = self.Position.Z.InternalValue - wp.Y.InternalValue;
                    long arrive = Fixed.FromInt(2).InternalValue;
                    if (wdx * wdx + wdz * wdz <= arrive * arrive)
                    {
                        order.RouteIndex++;
                        if (order.RouteIndex < route.Count)
                            motion2.MoveToPoint(route[order.RouteIndex]);
                        else
                            MoveToTargetEdge(u, t, m.Cm, Fixed.FromFloat(trader.GetTradeRange(m.Cm)));
                    }
                    else if (!motion2.HasMoveTarget)
                        motion2.MoveToPoint(wp);
                    return;
                }
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
                // 换程重走航线(原版:waypoints = route.slice(),向第二市场时反转)。
                if (u.CurrentOrder.Route is { Count: > 0 } route)
                {
                    u.CurrentOrder.RouteIndex = 0;
                    if (next.Value == trader.SecondMarket)
                        route.Reverse();
                    StartMovingTo(u, route[0], m.Cm);
                }
                else
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
        spec.State("INDIVIDUAL").State("PATROL").State("PATROLLING")
            .On("Timer", (u, m) =>
            {
                // 到达 → 路点等待(原版 MovementUpdate → CHECKINGWAYPOINT)
                var motion = m.Cm!.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget)
                {
                    u._patrolWaitElapsed = 0;
                    u.FsmNextState = "PATROL.CHECKINGWAYPOINT";
                    return;
                }
                // 沿途索敌(原版 PATROLLING Timer 的 FindWalkAndFightTargets)
                ScanAndEngage(u, m);
            });
        spec.State("INDIVIDUAL").State("PATROL").State("CHECKINGWAYPOINT")
            .On("Timer", (u, m) =>
            {
                // 路点停留 PatrolWaitTime(模板默认 1s)后折返:队列为空时补"回起点"单,
                // 再压回当前目标点 → 起点⇄终点无限往返(原版 PushOrder 双推同款)。
                u._patrolWaitElapsed += m.Dt;
                if (u._patrolWaitElapsed >= PatrolWaitTime)
                {
                    var cur = u.CurrentOrder;
                    if (u._patrolStart is { } start && u._orderQueue.Count == 1)
                        u.PushOrder(new UnitOrder { Type = "Patrol", Position = start, Queued = true });
                    if (cur != null)
                        u.PushOrder(new UnitOrder { Type = "Patrol", Position = cur.Position, Queued = true });
                    u.FinishOrder();
                    return;
                }
                ScanAndEngage(u, m);
            });
        spec.State("INDIVIDUAL").State("GUARD").State("ESCORTING")
            .On("Timer", (u, m) =>
            {
                if (u._isGuardOf is not { } t || !ShouldGuard(u, t, m.Cm!)) { u.FinishOrder(); return; }
                var guardRange = GuardRangeOf(u, m.Cm!);
                // 原版 ESCORTING Timer:3×guardRange 内速度跟随被护方
                // (TryMatchTargetSpeed(isGuardOf, mayRun:false) → 速度上限 = min(基速, 目标速))。
                var myMotion = m.Cm!.QueryInterface<UnitMotion>(u.Entity);
                if (myMotion != null && InGuardRange(u, t, m.Cm, guardRange * 3))
                {
                    var tMotion = m.Cm.QueryInterface<UnitMotion>(t);
                    myMotion.FollowSpeedCap = tMotion != null && tMotion.CurrentSpeed > Fixed.Zero
                        ? (tMotion.CurrentSpeed < myMotion.Speed ? tMotion.CurrentSpeed : myMotion.Speed)
                        : null;
                }
                else if (myMotion != null)
                    myMotion.FollowSpeedCap = null;
                if (InGuardRange(u, t, m.Cm, guardRange))
                {
                    StopMoving(u);
                    u.FsmNextState = "GUARD.GUARDING";
                    return;
                }
                if (myMotion != null && !myMotion.HasMoveTarget)
                    MoveToTargetEdge(u, t, m.Cm, guardRange);
            })
            // 离态清速度跟随上限(不留陈旧减速到 GUARDING/其他订单)。
            .Leave(u => SimSystem.GetComponent<UnitMotion>(u.Entity)?.ClearFollowSpeedCap());
        spec.State("INDIVIDUAL").State("GUARD").State("GUARDING")
            .On("Timer", (u, m) =>
            {
                if (u._isGuardOf is not { } t || !ShouldGuard(u, t, m.Cm!)) { u.FinishOrder(); return; }
                var guardRange = GuardRangeOf(u, m.Cm!);
                // 出护卫半径 → 回到追赶(原版 GUARDING Timer 同款)
                if (!InGuardRange(u, t, m.Cm!, guardRange))
                {
                    if (m.Cm!.QueryInterface<UnitMotion>(u.Entity) == null) { u.FinishOrder(); return; }
                    MoveToTargetEdge(u, t, m.Cm, guardRange);
                    u.FsmNextState = "GUARD.ESCORTING";
                    return;
                }
                // 目标受伤且可治疗 → 前插治疗(原版 GUARDING Timer;治完自动回护卫)
                var th = m.Cm!.QueryInterface<HealthComponent>(t);
                if (th != null && th.Current < th.Max
                    && m.Cm.QueryInterface<HealComponent>(u.Entity)?.CanHeal(m.Cm, t) == true)
                {
                    u.PushOrderFront(new UnitOrder { Type = "Heal", Target = t, Force = false });
                    return;
                }
                ScanAndEngage(u, m);
            });
        spec.State("INDIVIDUAL").State("FLEEING")
            .On("Timer", (u, m) =>
            {
                // 跑到(或被堵住停下来)→ 订单完成(原版 MovementUpdate likelyFailure/到位)
                var motion = m.Cm!.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget)
                    u.FinishOrder();
            });
        // RETURNRESOURCE 子树(原版同名):APPROACHING 接近投放站 → 交付 + FinishOrder;
        // 目标失效(投放站被毁)→ FinishOrder。
        spec.State("INDIVIDUAL").State("RETURNRESOURCE").State("APPROACHING")
            .On("Timer", (u, m) =>
            {
                var gatherer = m.Cm!.QueryInterface<ResourceGatherer>(u.Entity);
                if (gatherer == null || gatherer.TargetDropsite is not { } ds) { u.FinishOrder(); return; }
                if (m.Cm.QueryInterface<PositionComponent>(ds) == null) { u.FinishOrder(); return; }
                if (WithinRange(u.Entity, ds, m.Cm, GatherRange))
                {
                    StopMoving(u);
                    DepositResources(u.Entity, gatherer, m.Cm);
                    u.FinishOrder();
                    return;
                }
                var motion = m.Cm.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget)
                    MoveToTargetEdge(u, ds, m.Cm, Fixed.FromInt(1));
            });

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
                        // 结算完成/目标失效(原版 FINDINGNEWTARGET):找附近可收宝物,
                        // 无则收单。
                        {
                            var next = FindNewTreasureTarget(u, tc, m.Cm!);
                            if (next.HasValue)
                            {
                                u.CollectTreasure(next.Value);   // 重发 CollectTreasure 换目标
                                break;
                            }
                            u.FinishOrder();
                        }
                        break;
                    case CollectTickResult.OutOfRange:
                        MoveToTargetEdge(u, t, m.Cm, Fixed.FromFloat(tc.MaxDistance));
                        u.FsmNextState = "COLLECTTREASURE.APPROACHING";
                        break;
                }
            })
            .Leave(u => SimSystem.GetComponent<TreasureCollectorComponent>(u.Entity)?.StopCollecting());
        spec.State("INDIVIDUAL").State("CHEERING");
        // 攻击移动(原版 WALKINGANDFIGHTING):走向目标点;1s 节流索敌,发现即前插
        // Attack(打完队列下一条 = 本订单,自动继续走向目的地);到达即完成。
        spec.State("INDIVIDUAL").State("WALKINGANDFIGHTING")
            .On("Timer", (u, m) =>
            {
                var motion = m.Cm!.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget) { u.FinishOrder(); return; }
                ScanAndEngage(u, m);
            });
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
                if (attack.State == AttackComponent.AttackState.Approaching)
                {
                    if (u.CurrentOrder is { Type: "Attack", Force: false }
                        && u.CurrentStanceFlags.RespondStandGround)
                    {
                        u.FinishOrder();
                        return;
                    }
                    u.FsmNextState = "COMBAT.CHASING";
                }
                else if (attack.State == AttackComponent.AttackState.Attacking)
                    u.FsmNextState = "COMBAT.ATTACKING";
            });

        // COMBAT.ATTACKING — in range; let AttackComponent run its cycle.
        spec.State("INDIVIDUAL").State("COMBAT").State("ATTACKING")
            .On("Timer", (u, m) =>
            {
                var attack = m.Cm!.QueryInterface<AttackComponent>(u.Entity);
                if (attack?.Target == null)
                {
                    // 目标失效(原版 TargetInvalidated):找替代敌,否则收单。
                    u.FsmNextState = "COMBAT.FINDINGNEWTARGET";
                    return;
                }
                attack.Tick(m.Dt, m.Cm!);
                // 目标跑出射程(原版 OutOfRange):追击型转 CHASING;站桩门拦下收单
                // (原版 APPROACHING 的 stance 门同款语义,在超射程处拦截)。
                if (attack.State == AttackComponent.AttackState.Approaching)
                {
                    if (u.CurrentOrder is { Type: "Attack", Force: false }
                        && u.CurrentStanceFlags.RespondStandGround)
                    {
                        u.FinishOrder();
                        return;
                    }
                    u.FsmNextState = "COMBAT.CHASING";
                }
            });

        // COMBAT.FINDINGNEWTARGET(原版):目标失效/不可攻 → 视野内找替代敌
        // (FindVisibleEnemies = 原版 FindNewTargets 的 losAttackRangeQuery);
        // 找到即换目标续战,否则收单(原版 FinishOrder;WAF 模式另觅 WAF 目标
        // 由 FindWalkAndFightTargets 扫描覆盖)。
        spec.State("INDIVIDUAL").State("COMBAT").State("FINDINGNEWTARGET")
            .Enter(u =>
            {
                var cm = SimSystem.Sim!;
                var flags = u.CurrentStanceFlags;
                if (flags.TargetVisibleEnemies)
                {
                    var enemies = u.FindVisibleEnemies(cm, flags);
                    if (enemies.Count > 0)
                    {
                        // 换目标续战(原版 AttackEntityInZone → Order.Attack 重发;
                        // 偏好分组取最优)。
                        var pick = u.PickTargetByPreference(cm, enemies);
                        if (pick.HasValue)
                        {
                            u.Attack(pick.Value);
                            return;
                        }
                    }
                }
                u.FinishOrder();
            });

        // COMBAT.CHASING(原版):攻击目标出射程 → 追击至攻击射程内
        // (MoveToTargetAttackRange 语义);1s 节流重查,追不上放弃
        // (ShouldAbandonChase:目标消失/不可攻 → FinishOrder)。
        spec.State("INDIVIDUAL").State("COMBAT").State("CHASING")
            .Enter(u =>
            {
                var cm = SimSystem.Sim!;
                var attack = cm.QueryInterface<AttackComponent>(u.Entity);
                var target = attack?.Target;
                if (attack == null || target == null) { u.FinishOrder(); return; }
                // 首拍即跑(原版 StartTimer 首个 timeout=0:入态立即一轮确认),
                // 之后 1s 节流(StartTimer(1000,1000) 的续期)。
                u._chaseElapsed = 1f;
            })
            .On("Timer", (u, m) =>
            {
                var attack = m.Cm!.QueryInterface<AttackComponent>(u.Entity);
                var target = attack?.Target;
                if (attack == null || target == null) { u.FinishOrder(); return; }
                u._chaseElapsed += m.Dt;
                if (u._chaseElapsed < 1f) return;   // 原版 StartTimer(1000,1000)
                u._chaseElapsed = 0;

                // 追不上(目标死/失效/外交翻非敌)→ 收单(原版 ShouldAbandonChase)。
                var targetHealth = m.Cm!.QueryInterface<HealthComponent>(target.Value);
                if (targetHealth == null || targetHealth.IsDead)
                {
                    u.FinishOrder();
                    return;
                }
                var own = m.Cm!.QueryInterface<OwnershipComponent>(u.Entity);
                var targetOwn = m.Cm!.QueryInterface<OwnershipComponent>(target.Value);
                if (own != null && targetOwn != null
                    && !m.Cm!.Players.IsEnemy(own.PlayerId, targetOwn.PlayerId))
                {
                    u.FinishOrder();
                    return;
                }

                // LOS 门(原版 APPROACHING 的 ShouldChaseTargetedEntity 同款;
                // 追击到视野外即弃——aggressive 目标脱出视野收单,violent 豁免)。
                if (u.CurrentOrder is { Type: "Attack", Force: false }
                    && !u.CurrentStanceFlags.RespondChaseBeyondVision
                    && own != null && SimSystem.Range != null
                    && SimSystem.Range.GetLosVisibility(target.Value, own.PlayerId) != LosVisibility.Visible)
                {
                    u.FinishOrder();
                    return;
                }

                // 追击:向目标攻击射程内的可达点走(原版 MoveToTargetAttackRange)。
                float reach = attack.CurrentAttackIsCapture ? attack.CaptureRange : attack.Range;
                var myPos = m.Cm!.QueryInterface<PositionComponent>(u.Entity);
                var tp = m.Cm!.QueryInterface<PositionComponent>(target.Value);
                if (myPos == null || tp == null) return;
                float dx = tp.Position.X.ToFloat() - myPos.Position.X.ToFloat();
                float dz = tp.Position.Z.ToFloat() - myPos.Position.Z.ToFloat();
                float dist = MathF.Sqrt(dx * dx + dz * dz);
                if (dist <= reach)
                {
                    // 已到射程 → 回 COMBAT.APPROACHING 接管攻击。
                    u.FsmNextState = "COMBAT.APPROACHING";
                    return;
                }
                // 朝目标走至 reach 距离处(沿连线缩进,不到目标中心)。
                float t = reach / dist;
                var motion = m.Cm!.QueryInterface<UnitMotion>(u.Entity);
                motion?.MoveToPoint(new Maths.FixedVector2D(
                    Maths.Fixed.FromFloat(myPos.Position.X.ToFloat() + dx * t),
                    Maths.Fixed.FromFloat(myPos.Position.Z.ToFloat() + dz * t)));
            });
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
                if (supply == null || supply.IsEmpty)
                {
                    // 采空 → FINDINGNEWTARGET 自动续目标(原版同;此前直接 FinishOrder 停工)。
                    u._depletedSupply = gatherer.TargetSupply;
                    u.FsmNextState = "GATHER.FINDINGNEWTARGET";
                    return;
                }

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
                        // 原供应失效 → FINDINGNEWTARGET 自动续目标(原版同)。
                        u._depletedSupply = gatherer.TargetSupply;
                        u.FsmNextState = "GATHER.FINDINGNEWTARGET";
                    }
                }
            });

        // GATHER.FINDINGNEWTARGET(原版同名状态):采空/失效后自动找下一个同类型资源。
        // 过滤:排除刚采空目标、同 specific(肉须同模板,不换猎物种类)、可见;
        // 搜索半径 64(原版常量),非强制单搜当前位置,强制单搜原目标位置(原版语义:
        // 强制采集远赴资源点是有意的,回该区域续采)。
        spec.State("INDIVIDUAL").State("GATHER").State("FINDINGNEWTARGET")
            .On("Timer", (u, m) =>
            {
                var next = FindNearbySupply(u, m.Cm!);
                if (next == null) { u.FinishOrder(); return; }
                var gatherer = m.Cm!.QueryInterface<ResourceGatherer>(u.Entity);
                if (gatherer == null) { u.FinishOrder(); return; }
                gatherer.TargetSupply = next;
                MoveToTargetEdge(u, next.Value, m.Cm!, Fixed.FromInt(1));
                gatherer.State = ResourceGatherer.GatherState.MovingToResource;
                u.FsmNextState = "GATHER.APPROACHING";
            });
    }

    private static void BuildRepairSubtree(FsmSpec<UnitAIComponent, FsmMessage> spec)
    {
        // REPAIR.APPROACHING — move to the foundation; BuilderComponent advances it on its own.
        // BuilderComponent.Tick(cm) (no dt — it applies a fixed per-call build increment) drives
        // both the approach and the building; when its target clears (foundation complete or
        // invalid) the order is done. 到岗(AtWorksite,原版 MoveCompleted)→ REPAIRING:
        // 此前一直停在 APPROACHING,动画解析成 walk(工人"原地踏步盖房子")。
        spec.State("INDIVIDUAL").State("REPAIR").State("APPROACHING")
            .On("Timer", (u, m) =>
            {
                var builder = m.Cm!.QueryInterface<BuilderComponent>(u.Entity);
                if (builder == null) { u.FinishOrder(); return; }
                builder.Tick(m.Cm!);
                if (builder.Target == null) { AutocontinueRepair(u, m.Cm); return; }
                if (builder.AtWorksite) u.FsmNextState = "REPAIR.REPAIRING";
            });

        spec.State("INDIVIDUAL").State("REPAIR").State("REPAIRING")
            .On("Timer", (u, m) =>
            {
                var builder = m.Cm!.QueryInterface<BuilderComponent>(u.Entity);
                if (builder == null) { u.FinishOrder(); return; }
                builder.Tick(m.Cm!);
                if (builder.Target == null) { AutocontinueRepair(u, m.Cm); return; }
                // 目标移位/被挤离工位 → 回 APPROACHING 重新接近(原版同双向转移)。
                if (!builder.AtWorksite) u.FsmNextState = "REPAIR.APPROACHING";
            });
    }

    /// <summary>原版 REPAIR 完工(ConstructionFinished)的 autocontinue:当前单
    /// AutoContinue 且队列已空 → 就近(64m、同属主、LOS 可见)找未建成地基续建
    /// (UnitAI.js:3362-3399;原版的 autoharvest 转采集分支不在本范围)。否则正常出队。</summary>
    private static void AutocontinueRepair(UnitAIComponent u, ComponentManager cm)
    {
        if (u.CurrentOrder is { AutoContinue: true } && u._orderQueue.Count <= 1
            && FindNearbyFoundation(u, cm) is { } nextFoundation)
        {
            u.FinishOrder();
            u.Repair(nextFoundation, queued: true, autocontinue: true);
            return;
        }
        u.FinishOrder();
    }

    /// <summary>原版 FindNearbyFoundation:64m 内同属主未建成地基取最近(LOS 可见过滤
    /// 对齐 FindSupplyNear)。搜索中心取单位当前位置(完工时单位必在工地旁;原版搜
    /// 建成建筑位置,等价)。无 RangeManager 的测试环境降级为全实体线性扫描。</summary>
    private static EntityId? FindNearbyFoundation(UnitAIComponent u, ComponentManager cm)
    {
        var own = cm.QueryInterface<OwnershipComponent>(u.Entity);
        if (own == null || own.PlayerId < 0) return null;
        var self = cm.QueryInterface<PositionComponent>(u.Entity);
        if (self == null) return null;
        var range = SimSystem.Range;

        bool Eligible(EntityId e)
        {
            var f = cm.QueryInterface<FoundationComponent>(e);
            if (f == null || f.IsBuilt) return false;
            if (cm.QueryInterface<OwnershipComponent>(e)?.PlayerId != own.PlayerId) return false;
            if (range != null
                && range.GetLosVisibility(e, own.PlayerId) != LosVisibility.Visible)
                return false;
            return cm.QueryInterface<PositionComponent>(e) != null;
        }

        var candidates = range != null
            ? range.ExecuteQuery(u.Entity, Fixed.Zero, Fixed.FromInt(64), Eligible)
            : System.Linq.Enumerable.Where(cm.AllEntities, Eligible);
        EntityId? best = null;
        float bestDist2 = float.MaxValue;
        foreach (var e in candidates)
        {
            var p = cm.QueryInterface<PositionComponent>(e)!;
            float dx = p.Position.X.ToFloat() - self.Position.X.ToFloat();
            float dz = p.Position.Z.ToFloat() - self.Position.Z.ToFloat();
            float d2 = dx * dx + dz * dz;
            if (d2 < bestDist2) { best = e; bestDist2 = d2; }
        }
        return best;
    }

    private static void BuildFormationControllerTree(FsmSpec<UnitAIComponent, FsmMessage> spec)
    {
        // FORMATIONCONTROLLER(原版 UnitAI.js 同名树)。成员作战经 CallMemberFunction
        // 广播个体订单(Attack/Stop/Guard);控制器自身的移动只为整队走位。
        var fc = spec.State("FORMATIONCONTROLLER");

        fc.On("Order.Walk", (u, m) =>
        {
            // 原版:SetHeldPosition(成员驻锚,防御站姿回位用——成员个体锚已各自维护,
            // 此处不广播)+ WALKING。
            u.FsmNextState = "WALKING";
        });
        fc.On("Order.Stop", (u, m) =>
        {
            // 原版:ResetOrderVariant(动画变体,无对应物)+ 非编队作战时成员 Stop;
            // 不把成员拉回队形位。
            if (!IsAttackingAsFormation(u, m.Cm!))
                CallMemberStop(u, m.Cm!);
            StopMoving(u);
            u.FsmNextState = "IDLE";
        });
        fc.On("Order.Attack", (u, m) =>
        {
            if (m.Order?.Target is not { } rawTarget) { u.FinishOrder(); return; }
            var cm = m.Cm!;
            // 目标是敌方编队成员 → 以其控制器为编队目标(原版 formationTarget)。
            var target = ResolveToFormationController(rawTarget, cm) ?? rawTarget;
            if (!CheckFormationTargetAttackRange(u, target, cm))
            {
                if (HasMotion(u, cm) && CheckTargetVisible(u, rawTarget, cm))
                {
                    u.FsmNextState = "COMBAT.APPROACHING";
                    return;
                }
                u.FinishOrder();
                return;
            }
            CallMemberAttack(u, cm, target, m.Order.AllowCapture);
            u.FsmNextState = CanAttackAsFormation(u, cm) ? "COMBAT.ATTACKING" : "MEMBER";
        });
        fc.On("Order.WalkAndFight", (u, m) =>
        {
            if (!HasMotion(u, m.Cm!)) { u.FinishOrder(); return; }
            u.FsmNextState = "WALKINGANDFIGHTING";
        });
        fc.On("Order.Guard", (u, m) =>
        {
            // 原版:成员 Guard 后解散编队(护卫是个体行为)。
            if (m.Order?.Target is { } t)
                CallMemberGuard(u, m.Cm!, t);
            m.Cm!.QueryInterface<FormationComponent>(u.Entity)?.Disband(m.Cm);
        });
        fc.On("Order.Patrol", (u, m) =>
        {
            if (!HasMotion(u, m.Cm!)) { u.FinishOrder(); return; }
            // 原版 PATROL.enter:首个巡逻单锚定起点(往返用)。
            var pos = m.Cm!.QueryInterface<PositionComponent>(u.Entity);
            if (pos != null && u._patrolStart == null)
                u._patrolStart = new FixedVector2D(pos.Position.X, pos.Position.Z);
            u.FsmNextState = "PATROL.PATROLLING";
        });

        // CallMemberFunction 广播型订单(原版控制器同名 handler):在射程内直接广播 +
        // MEMBER 等待;否则 CALLMEMBER.APPROACHING 整队压近(原版 WalkToTargetRange
        // 前插 + secondTry 的等价)。成员无对应能力时其个体 handler 自行拒收。
        fc.On("Order.Gather", (u, m) => FormationCallMemberOrder(u, m, 10f));
        fc.On("Order.Heal", (u, m) => FormationCallMemberOrder(u, m, 10f));
        fc.On("Order.Repair", (u, m) => FormationCallMemberOrder(u, m, 10f));
        fc.On("Order.ReturnResource", (u, m) => FormationCallMemberOrder(u, m, 10f));
        fc.On("Order.DropAtNearestDropSite", (u, m) =>
        {
            // 无目标广播(原版控制器同名 handler:直接 CallMemberFunction + MEMBER)。
            CallMemberOrderFor(u, m.Cm!, m.Order!);
            u.FsmNextState = "MEMBER";
        });
        fc.On("Order.CollectTreasure", (u, m) => FormationCallMemberOrder(u, m, 20f));
        fc.On("Order.GatherNearPosition", (u, m) =>
        {
            if (m.Order == null) { u.FinishOrder(); return; }
            if (ControllerWithinPointRange(u, m.Order.Position, 20f, m.Cm!))
            {
                CallMemberOrderFor(u, m.Cm!, m.Order);
                u.FsmNextState = "MEMBER";
            }
            else
            {
                u.FsmNextState = "CALLMEMBER.APPROACHING";
            }
        });
        fc.On("Order.Pack", (u, m) =>
        {
            CallMemberPack(u, m.Cm!, unpack: false);
            u.FsmNextState = "MEMBER";
        });
        fc.On("Order.Unpack", (u, m) =>
        {
            CallMemberPack(u, m.Cm!, unpack: true);
            u.FsmNextState = "MEMBER";
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
                var fc2 = m.Cm!.QueryInterface<FormationComponent>(u.Entity);
                if (u.FormationTimerElapsed(m.Dt))
                    fc2?.UpdateFormation(m.Cm, moveCenter: false, force: true);
                // 原版 UpdateTwinFormationsForMerge 由 MovementUpdate 驱动(移动途中持续
                // 检查);2s 计时器对短行军太稀(6m 一跳 ~1s 即到,检查永远赶不上)——每拍查。
                fc2?.MergeTwinFormations(m.Cm);
                // 绕障缓释(原版 MovementUpdate veryObstructed → AttemptObstructionMitigation)。
                TryObstructionMitigation(u, m.Cm);
                var motion = m.Cm!.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget)
                    u.FinishOrder();
            })
            .Leave(u => StopMoving(u));

        // WALKINGANDFIGHTING(原版同名):combat 变体重排后整队推进;1s 索敌——有成员
        // 接敌 → MEMBER 等待成员打完,订单的 ReturningState 记录回本状态(原版
        // order.data.returningState)。
        fc.State("WALKINGANDFIGHTING")
            .Enter(u =>
            {
                u.ResetFormationTimer();
                u._combatScanElapsed = 0;
                var cm = SimSystem.Sim;
                cm?.QueryInterface<FormationComponent>(u.Entity)
                    ?.ArrangeFormation(cm, moveCenter: true, force: true, "combat");
                if (u.CurrentOrder is { } order)
                {
                    order.ReturningState = "WALKINGANDFIGHTING";
                    SimSystem.GetComponent<UnitMotion>(u.Entity)?.MoveToPoint(order.Position);
                }
            })
            .On("Timer", (u, m) =>
            {
                // 绕障缓释(原版 MovementUpdate veryObstructed → AttemptObstructionMitigation)。
                TryObstructionMitigation(u, m.Cm!);
                u._combatScanElapsed += m.Dt;
                if (u._combatScanElapsed >= StanceScanIntervalCombat)
                {
                    u._combatScanElapsed = 0;
                    if (MembersEngageVisibleEnemies(u, m.Cm!))
                    {
                        u.FsmNextState = "MEMBER";
                        return;
                    }
                }
                var motion = m.Cm!.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget)
                    u.FinishOrder();
            })
            .Leave(u => StopMoving(u));

        // PATROL(原版控制器巡逻子树):整队压向路点,沿途 1s 索敌(接敌 → MEMBER,
        // ReturningState 回 PATROLLING 续巡);到达 → CHECKINGWAYPOINT 停留
        // PatrolWaitTime 后折返(起点⇄终点往返,同个体巡逻的双推语义)。
        var fcPatrol = fc.State("PATROL");
        fcPatrol.State("PATROLLING")
            .Enter(u =>
            {
                u.ResetFormationTimer();
                u._combatScanElapsed = 0;
                var cm = SimSystem.Sim;
                cm?.QueryInterface<FormationComponent>(u.Entity)
                    ?.ArrangeFormation(cm, moveCenter: true, force: true, "combat");
                if (u.CurrentOrder is { } order)
                {
                    order.ReturningState = "PATROL.PATROLLING";
                    SimSystem.GetComponent<UnitMotion>(u.Entity)?.MoveToPoint(order.Position);
                }
            })
            .On("Timer", (u, m) =>
            {
                // 绕障缓释(原版 PATROLLING 的 MovementUpdate veryObstructed →
                // AttemptObstructionMitigation 并 return)。
                TryObstructionMitigation(u, m.Cm!);
                var motion = m.Cm!.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget)
                {
                    u._patrolWaitElapsed = 0;
                    u.FsmNextState = "PATROL.CHECKINGWAYPOINT";
                    return;
                }
                u._combatScanElapsed += m.Dt;
                if (u._combatScanElapsed >= StanceScanIntervalCombat)
                {
                    u._combatScanElapsed = 0;
                    if (MembersEngageVisibleEnemies(u, m.Cm!))
                        u.FsmNextState = "MEMBER";
                }
            })
            .Leave(u => StopMoving(u));
        fcPatrol.State("CHECKINGWAYPOINT")
            .On("Timer", (u, m) =>
            {
                u._patrolWaitElapsed += m.Dt;
                if (u._patrolWaitElapsed >= PatrolWaitTime)
                {
                    // 折返:起点回单 + 当前点回单(Queued 不清 _patrolStart)。
                    var cur = u.CurrentOrder;
                    if (u._patrolStart is { } start)
                        u.PushOrder(new UnitOrder { Type = "Patrol", Position = start, Queued = true });
                    if (cur != null)
                        u.PushOrder(new UnitOrder { Type = "Patrol", Position = cur.Position, Queued = true });
                    u.FinishOrder();
                    return;
                }
                u._combatScanElapsed += m.Dt;
                if (u._combatScanElapsed >= StanceScanIntervalCombat)
                {
                    u._combatScanElapsed = 0;
                    if (MembersEngageVisibleEnemies(u, m.Cm!))
                        u.FsmNextState = "MEMBER";
                }
            });

        // CALLMEMBER(广播型订单的整队压近;原版 WalkToTargetRange 前插的等价):
        // 控制器走向目标/目标点,进入订单射程 → 广播 + MEMBER。
        fc.State("CALLMEMBER").State("APPROACHING")
            .Enter(u =>
            {
                u.ResetFormationTimer();
                var cm = SimSystem.Sim;
                cm?.QueryInterface<FormationComponent>(u.Entity)
                    ?.ArrangeFormation(cm, moveCenter: true, force: true, null);
                if (u.CurrentOrder?.Target is { } t)
                    MoveToTarget(u, t, cm!);
                else if (u.CurrentOrder is { Type: "GatherNearPosition" } o)
                    SimSystem.GetComponent<UnitMotion>(u.Entity)?.MoveToPoint(o.Position);
            })
            .On("Timer", (u, m) =>
            {
                var order = u.CurrentOrder;
                if (order == null) { u.FinishOrder(); return; }
                float range = order.Type is "CollectTreasure" or "GatherNearPosition" ? 20f : 10f;
                bool inRange = order.Target is { } t
                    ? ControllerWithinRange(u, t, m.Cm!, range)
                    : order.Type == "GatherNearPosition"
                        && ControllerWithinPointRange(u, order.Position, range, m.Cm!);
                if (inRange)
                {
                    CallMemberOrderFor(u, m.Cm!, order);
                    u.FsmNextState = "MEMBER";
                    return;
                }
                var motion = m.Cm!.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget)
                    u.FinishOrder();
            })
            .Leave(u => StopMoving(u));

        // COMBAT(编队作战子树)。
        var combat = fc.State("COMBAT");

        // APPROACHING:combat 变体重排 + 整队压向目标;进入编队射程 → 成员开打,
        // 按 CanAttackAsFormation 分派 ATTACKING(整体作战)或 MEMBER(散开各自为战)。
        combat.State("APPROACHING")
            .Enter(u =>
            {
                u.ResetFormationTimer();
                var cm = SimSystem.Sim;
                if (cm == null) return;
                cm.QueryInterface<FormationComponent>(u.Entity)
                    ?.ArrangeFormation(cm, moveCenter: true, force: true, "combat");
                if (u.CurrentOrder?.Target is { } t)
                {
                    var target = ResolveToFormationController(t, cm) ?? t;
                    if (!MoveToTarget(u, target, cm))
                        u.FinishOrder();
                }
            })
            .On("Timer", (u, m) =>
            {
                if (u.CurrentOrder?.Target is not { } rawTarget) { u.FinishOrder(); return; }
                var cm = m.Cm!;
                var target = ResolveToFormationController(rawTarget, cm) ?? rawTarget;
                if (CheckFormationTargetAttackRange(u, target, cm))
                {
                    // 原版 MovementUpdate 分支:到位 → 成员 Attack 广播。
                    CallMemberAttack(u, cm, target, u.CurrentOrder.AllowCapture);
                    u.FsmNextState = CanAttackAsFormation(u, cm) ? "COMBAT.ATTACKING" : "MEMBER";
                    return;
                }
                var motion = cm.QueryInterface<UnitMotion>(u.Entity);
                if (motion != null && !motion.HasMoveTarget)
                    u.FinishOrder();   // 走完仍未及程(目标移走/被挡)→ 收工
            })
            .Leave(u => StopMoving(u));

        // ATTACKING(整体作战:phalanx 类):控制器留场,200ms 轮询编队射程,
        // 出程 → 回 APPROACHING(目标仍在的情况;目标消亡由成员各自收工、
        // 射程检查失败兜底)。
        combat.State("ATTACKING")
            .Enter(u => u._combatScanElapsed = 0)
            .On("Timer", (u, m) =>
            {
                u._combatScanElapsed += m.Dt;
                if (u._combatScanElapsed < 0.2f) return;   // 原版 StartTimer(200,200)
                u._combatScanElapsed = 0;
                if (u.CurrentOrder?.Target is not { } rawTarget) { u.FinishOrder(); return; }
                var cm = m.Cm!;
                var target = ResolveToFormationController(rawTarget, cm) ?? rawTarget;
                if (!CheckFormationTargetAttackRange(u, target, cm))
                    u.FsmNextState = "COMBAT.APPROACHING";
            });

        // MEMBER(散开作战/等待成员):控制器移出世界(原版 MEMBER.enter 的
        // MoveOutOfWorld——等待期间编队无确定位置);全员完成 → ReturningState
        // 或 FinishOrder。回原状态(WAF)时重排会把控制器拉回成员质心。
        fc.State("MEMBER")
            .Enter(u =>
            {
                var cm = SimSystem.Sim;
                var pos = cm?.QueryInterface<PositionComponent>(u.Entity);
                if (pos != null && pos.InWorld)
                {
                    pos.InWorld = false;
                    SimSystem.Range?.SetInWorld(u.Entity, false);
                }
            })
            .On("Timer", (u, m) =>
            {
                var formation = m.Cm!.QueryInterface<FormationComponent>(u.Entity);
                if (formation != null && !formation.AreAllMembersFinished()) return;
                if (u.CurrentOrder?.ReturningState is { } rs)
                    u.FsmNextState = rs;
                else
                    u.FinishOrder();
            })
            .Leave(u =>
            {
                var cm = SimSystem.Sim;
                var pos = cm?.QueryInterface<PositionComponent>(u.Entity);
                if (pos != null && !pos.InWorld)
                {
                    pos.InWorld = true;
                    SimSystem.Range?.SetInWorld(u.Entity, true);
                }
            });
    }

    // =========================================================================
    // 编队作战辅助(原版 UnitAI.js 的 CallMemberFunction/CheckFormationTargetAttackRange
    // /IsAttackingAsFormation/FindWalkAndFightTargets 移植)。
    // =========================================================================

    /// <summary>目标是编队成员 → 其控制器(原版 Order.Attack 的 formationTarget 解析)。</summary>
    private static EntityId? ResolveToFormationController(EntityId target, ComponentManager cm)
    {
        var ai = cm.QueryInterface<UnitAIComponent>(target);
        if (ai != null && ai.FormationController != null && !ai.IsFormationController)
            return ai.FormationController;
        return null;
    }

    /// <summary>控制器可否整体作战(原版 FormationAttack.CanAttackAsFormation)。</summary>
    private static bool CanAttackAsFormation(UnitAIComponent u, ComponentManager cm) =>
        cm.QueryInterface<FormationComponent>(u.Entity)?.CanAttackAsFormation == true;

    /// <summary>原版 IsAttackingAsFormation:可整体作战 且 当前在 COMBAT.ATTACKING。</summary>
    private static bool IsAttackingAsFormation(UnitAIComponent u, ComponentManager cm) =>
        CanAttackAsFormation(u, cm) && u.FsmStateName == "FORMATIONCONTROLLER.COMBAT.ATTACKING";

    /// <summary>控制器有移动件(原版 AbleToMove 的近似:控制器恒可动,无件即不可)。</summary>
    private static bool HasMotion(UnitAIComponent u, ComponentManager cm) =>
        cm.QueryInterface<UnitMotion>(u.Entity) != null;

    /// <summary>原版 CheckTargetVisible:目标对控制器属主非 hidden 即可见
    /// (fogged/miraged 视同可见;驻防中不可见——控制器永不驻防,略)。</summary>
    private static bool CheckTargetVisible(UnitAIComponent u, EntityId target, ComponentManager cm)
    {
        var own = cm.QueryInterface<OwnershipComponent>(u.Entity);
        var range = SimSystem.Range;
        if (own == null || range == null) return false;
        return range.GetLosVisibility(target, own.PlayerId) != LosVisibility.Hidden;
    }

    /// <summary>原版 CheckFormationTargetAttackRange:目标为编队 → 取距控制器最近成员;
    /// 射程 = FormationComponent.GetAttackRange(跨成员聚合 + 队深折算);距离按
    /// 控制器中心到目标边缘(减目标 obstruction 半径)判定 [min,max]。</summary>
    private static bool CheckFormationTargetAttackRange(UnitAIComponent u, EntityId target, ComponentManager cm)
    {
        var formation = cm.QueryInterface<FormationComponent>(u.Entity);
        var ctrlPos = cm.QueryInterface<PositionComponent>(u.Entity);
        if (formation == null || ctrlPos == null) return false;

        var effTarget = target;
        if (cm.QueryInterface<FormationComponent>(target) is { } targetFormation)
            effTarget = targetFormation.GetClosestMemberToEntity(cm, u.Entity) ?? target;

        var targetPos = cm.QueryInterface<PositionComponent>(effTarget);
        if (targetPos == null || !targetPos.InWorld) return false;

        var (min, max) = formation.GetAttackRange(cm, effTarget);
        if (max < 0) return false;

        float dx = targetPos.Position.X.ToFloat() - ctrlPos.Position.X.ToFloat();
        float dz = targetPos.Position.Z.ToFloat() - ctrlPos.Position.Z.ToFloat();
        float dist = MathF.Sqrt(dx * dx + dz * dz);
        // 边缘折算(原版 IsInTargetRange 经 ObstructionManager 按边缘量程)。
        float targetRadius = cm.QueryInterface<ObstructionComponent>(effTarget)?.GetSize().ToFloat() ?? 0f;
        dist -= targetRadius;
        return dist <= max && dist >= min;
    }

    /// <summary>原版 CallMemberFunction("Attack"):先清完成标记,再逐成员广播 Attack
    /// 订单(替换队列,非排队)。目标是敌方编队 → 每成员各自解析"离我最近的敌编队
    /// 成员"(原版成员 CheckTargetAttackRange 经控制器再解析的等价)。</summary>
    private static void CallMemberAttack(UnitAIComponent u, ComponentManager cm, EntityId target, bool allowCapture)
    {
        var formation = cm.QueryInterface<FormationComponent>(u.Entity);
        if (formation == null) return;
        formation.ResetFinishedEntities();
        var targetFormation = cm.QueryInterface<FormationComponent>(target);
        foreach (var member in formation.Members)
        {
            var ai = cm.QueryInterface<UnitAIComponent>(member);
            if (ai == null) continue;
            var effTarget = target;
            if (targetFormation != null)
                effTarget = targetFormation.GetClosestMemberToEntity(cm, member) ?? target;
            ai.Attack(effTarget, allowCapture);
        }
    }

    /// <summary>原版 CallMemberFunction("Stop")。</summary>
    private static void CallMemberStop(UnitAIComponent u, ComponentManager cm)
    {
        var formation = cm.QueryInterface<FormationComponent>(u.Entity);
        if (formation == null) return;
        formation.ResetFinishedEntities();
        foreach (var member in formation.Members)
            cm.QueryInterface<UnitAIComponent>(member)?.Stop();
    }

    /// <summary>原版 CallMemberFunction("Guard")。</summary>
    private static void CallMemberGuard(UnitAIComponent u, ComponentManager cm, EntityId target)
    {
        var formation = cm.QueryInterface<FormationComponent>(u.Entity);
        if (formation == null) return;
        formation.ResetFinishedEntities();
        foreach (var member in formation.Members)
            cm.QueryInterface<UnitAIComponent>(member)?.Guard(target);
    }

    /// <summary>广播型订单(Gather/Heal/Repair/CollectTreasure)的入口分流
    /// (原版控制器同名 Order handler):控制器在射程内 → 立即广播 + MEMBER;
    /// 否则 CALLMEMBER.APPROACHING 整队压近。</summary>
    private static void FormationCallMemberOrder(UnitAIComponent u, FsmMessage m, float range)
    {
        if (m.Order?.Target is not { } t) { u.FinishOrder(); return; }
        if (ControllerWithinRange(u, t, m.Cm!, range))
        {
            CallMemberOrderFor(u, m.Cm!, m.Order);
            u.FsmNextState = "MEMBER";
        }
        else
        {
            u.FsmNextState = "CALLMEMBER.APPROACHING";
        }
    }

    /// <summary>控制器中心到目标中心距离 ≤ range(原版 CheckTargetRangeExplicit 的
    /// 近似:不做障碍边缘折算,广播半径 10/20m 本身已宽松)。</summary>
    private static bool ControllerWithinRange(UnitAIComponent u, EntityId target, ComponentManager cm, float range)
    {
        var pos = cm.QueryInterface<PositionComponent>(u.Entity);
        var tp = cm.QueryInterface<PositionComponent>(target);
        if (pos == null || tp == null || !tp.InWorld) return false;
        float dx = tp.Position.X.ToFloat() - pos.Position.X.ToFloat();
        float dz = tp.Position.Z.ToFloat() - pos.Position.Z.ToFloat();
        return dx * dx + dz * dz <= range * range;
    }

    private static bool ControllerWithinPointRange(UnitAIComponent u, FixedVector2D point, float range, ComponentManager cm)
    {
        var pos = cm.QueryInterface<PositionComponent>(u.Entity);
        if (pos == null) return false;
        float dx = point.X.ToFloat() - pos.Position.X.ToFloat();
        float dz = point.Y.ToFloat() - pos.Position.Z.ToFloat();
        return dx * dx + dz * dz <= range * range;
    }

    // =========================================================================
    // 编队控制器绕障缓释(原版 UnitAI.js AttemptObstructionMitigation:6786-6838):
    // 控制器(大净空)被障碍卡死但成员仍能走时,控制器跳到"离目的地最近的成员"
    // 位置绕过障碍;该成员须比控制器离目的地近 >2m;5s 冷却防鬼畜。
    // 触发点 = 控制器 WALKING/WALKINGANDFIGHTING/PATROLLING 的 Timer(上游是
    // MovementUpdate 的 veryObstructed;我们用 UnitMotion.IsStuckThisLeg 看门狗信号)。
    // =========================================================================

    /// <summary>卡死且冷却完毕 → 跳最近成员位。落点校验:ObstructionManager 无推出
    /// 兜底,故跳前必须确认可站立(不与非本控制组的 BlockMovement 形状重叠;
    /// 有寻路网格时再查控制器通行类的 navcell 可通行)。</summary>
    private void AttemptObstructionMitigation(ComponentManager cm, FixedVector2D destination)
    {
        if (_obstructionMitigationCooldown > 0f) return;
        var formation = cm.QueryInterface<FormationComponent>(Entity);
        if (formation == null) return;
        var closest = formation.GetClosestMemberToPosition(cm, destination);
        if (closest is not { } member) return;
        var memberPos = cm.QueryInterface<PositionComponent>(member);
        var ctrlPos = cm.QueryInterface<PositionComponent>(Entity);
        if (memberPos == null || ctrlPos == null || !memberPos.InWorld) return;

        // 成员比控制器离目的地近 >2m 才跳(原版 distanceTo 差值判定,定点)。
        var memberDiff = new FixedVector2D(
            destination.X - memberPos.Position.X, destination.Y - memberPos.Position.Z);
        var ctrlDiff = new FixedVector2D(
            destination.X - ctrlPos.Position.X, destination.Y - ctrlPos.Position.Z);
        var closerThreshold = ctrlDiff.Length() - Fixed.FromInt(2);
        if (closerThreshold <= Fixed.Zero
            || memberDiff.CompareLength(closerThreshold) >= 0)
            return;
        if (!ControllerCanStandAt(cm, memberPos.Position.X, memberPos.Position.Z))
            return;

        // 跳:直接改 Position + NotifyPositionChanged
        // (Formation.SetupPositionAndHandleRotation 同款模式)。
        var old = new FixedVector2D(ctrlPos.Position.X, ctrlPos.Position.Z);
        ctrlPos.Position = new FixedVector3D(memberPos.Position.X, memberPos.Position.Y,
            memberPos.Position.Z);
        cm.NotifyPositionChanged(Entity, old,
            new FixedVector2D(ctrlPos.Position.X, ctrlPos.Position.Z));
        _obstructionMitigationCooldown = 5f;
        // 跳后从落点重解路径(旧路标在障碍另一侧,续走会回溯再卡)。
        cm.QueryInterface<UnitMotion>(Entity)?.MoveToPoint(destination);
    }

    /// <summary>落点可站立校验(见 <see cref="AttemptObstructionMitigation"/>)。</summary>
    private bool ControllerCanStandAt(ComponentManager cm, Fixed x, Fixed z)
    {
        var obs = cm.QueryInterface<ObstructionComponent>(Entity);
        var mgr = SimSystem.Obstructions;
        if (obs != null && mgr != null)
        {
            uint group = obs.ControlGroup;
            ObstructionShapeFilter filter = (_, flags, g, g2) =>
                g == group || g2 == group || (flags & ObstructionFlags.BlockMovement) == 0;
            if (mgr.TestUnitShape(filter, x, z, obs.GetSize()).Count > 0)
                return false;
        }
        var pf = SimSystem.Pathfinder;
        var motion = cm.QueryInterface<UnitMotion>(Entity);
        if (pf?.PassabilityGrid is { } grid && motion != null)
        {
            var cls = pf.GetClassByName(motion.PassClassName) ?? pf.DefaultClass;
            int ni = Pathfinding.PathfindingCore.WorldToNavcell(x);
            int nj = Pathfinding.PathfindingCore.WorldToNavcell(z);
            if (ni < 0 || nj < 0 || ni >= grid.W || nj >= grid.H)
                return false;
            if (!Pathfinding.PathfindingCore.IsPassable(grid.Get(ni, nj), cls.Mask))
                return false;
        }
        return true;
    }

    /// <summary>控制器三态 Timer 共用的触发门:卡死信号 + 订单带目的地 → 尝试跳。</summary>
    private static void TryObstructionMitigation(UnitAIComponent u, ComponentManager cm)
    {
        if (u._obstructionMitigationCooldown > 0f) return;
        if (u.CurrentOrder is not { } order) return;
        var motion = cm.QueryInterface<UnitMotion>(u.Entity);
        if (motion == null || !motion.IsStuckThisLeg) return;
        u.AttemptObstructionMitigation(cm, order.Position);
    }

    /// <summary>按订单类型向成员广播对应个体订单(原版 CallMemberFunction 的
    /// Gather/Heal/Repair/CollectTreasure/GatherNearPosition 分支)。成员无能力
    /// 时其个体 Order handler 拒收(FinishOrder 出队)——广播本身恒发。</summary>
    private static void CallMemberOrderFor(UnitAIComponent u, ComponentManager cm, UnitOrder order)
    {
        var formation = cm.QueryInterface<FormationComponent>(u.Entity);
        if (formation == null) return;
        formation.ResetFinishedEntities();
        foreach (var member in formation.Members)
        {
            var ai = cm.QueryInterface<UnitAIComponent>(member);
            if (ai == null) continue;
            switch (order.Type)
            {
                case "Gather":
                    if (order.Target is { } gt) ai.Gather(gt);
                    break;
                case "Heal":
                    if (order.Target is { } ht) ai.Heal(ht);
                    break;
                case "Repair":
                    if (order.Target is { } rt) ai.Repair(rt);
                    break;
                case "CollectTreasure":
                    if (order.Target is { } ct) ai.CollectTreasure(ct);
                    break;
                case "GatherNearPosition":
                    ai.GatherNearPosition(order.Position);
                    break;
                case "ReturnResource":
                    if (order.Target is { } returnTarget) ai.ReturnResource(returnTarget);
                    break;
                case "DropAtNearestDropSite":
                    ai.DropAtNearestDropSite();
                    break;
            }
        }
    }

    /// <summary>原版 CallMemberFunction("Pack"/"Unpack"):逐成员打包/解包。</summary>
    private static void CallMemberPack(UnitAIComponent u, ComponentManager cm, bool unpack)
    {
        var formation = cm.QueryInterface<FormationComponent>(u.Entity);
        if (formation == null) return;
        formation.ResetFinishedEntities();
        foreach (var member in formation.Members)
        {
            var ai = cm.QueryInterface<UnitAIComponent>(member);
            if (unpack) ai?.Unpack();
            else ai?.Pack();
        }
    }

    /// <summary>原版控制器的 FindWalkAndFightTargets(= CallMemberFunction 同名):
    /// 逐成员按站姿索敌(复用个体 WAF 的 ScanAndEngage 逻辑,立即扫描一拍),
    /// 任一成员前插 Attack → true(控制器转 MEMBER 等待)。</summary>
    private static bool MembersEngageVisibleEnemies(UnitAIComponent u, ComponentManager cm)
    {
        var formation = cm.QueryInterface<FormationComponent>(u.Entity);
        if (formation == null) return false;
        bool engaged = false;
        foreach (var member in formation.Members)
        {
            var ai = cm.QueryInterface<UnitAIComponent>(member);
            if (ai == null) continue;
            var flags = ai.CurrentStanceFlags;
            if (!flags.TargetVisibleEnemies) continue;
            var enemies = ai.FindVisibleEnemies(cm, flags);
            if (enemies.Count == 0) continue;
            var pick = ai.PickTargetByPreference(cm, enemies);
            if (!pick.HasValue) continue;
            ai.PushOrderFront(new UnitOrder { Type = "Attack", Target = pick.Value, Force = false });
            engaged = true;
        }
        if (engaged)
            formation.ResetFinishedEntities();
        return engaged;
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
        // LeaveFormation(原版根处理器):成员脱离编队控制器(RemoveMembers 会
        // 顺带清链接;低于 RequiredMemberCount 时编队解散由 Formation 组件自理)。
        fm.On("Order.LeaveFormation", (u, m) =>
        {
            if (u.FormationController is { } fc)
                m.Cm!.QueryInterface<FormationComponent>(fc)
                    ?.RemoveMembers(m.Cm, new List<EntityId> { u.Entity });
            if (u.CurrentOrder?.Type == "LeaveFormation") u.FinishOrder();
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
        // 定点 sincos(编队偏移目标点 = sim 位置,libm 三角跨平台漂移 → 队形散位 OOS)。
        Trig.SinCosApprox(Maths.Fixed.FromFloat(rot), out Maths.Fixed fsin, out Maths.Fixed fcos);
        float sin = fsin.ToFloat(), cos = fcos.ToFloat();
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

    /// <summary>原版 FindNearbyResource 的移植:半径 64 内找下一个可采资源。
    /// 过滤:非空供应、排除刚采空目标、同 specific(肉须同模板,不换猎物种类)、属主可见;
    /// 返回最近者。强制单搜原目标位置,非强制搜当前位置(原版 previousForced 语义:
    /// 强制采集远赴资源点是有意的,回该区域续采)。</summary>
    private EntityId? _depletedSupply;

    private static EntityId? FindNearbySupply(UnitAIComponent u, ComponentManager cm)
    {
        var gatherer = cm.QueryInterface<ResourceGatherer>(u.Entity);
        if (gatherer?.TargetSupply == null) return null;

        var prevSupply = cm.QueryInterface<ResourceSupply>(gatherer.TargetSupply.Value);
        if (prevSupply == null) return null;
        string specific = prevSupply.SpecificType;
        string? template = null;
        if (specific == "meat")
            template = cm.QueryInterface<IdentityComponent>(gatherer.TargetSupply.Value)?.TemplateName ?? "";

        // 搜索中心:强制单搜原目标位置,否则当前位置(原版 previousForced 语义)。
        FixedVector2D center;
        if (u.CurrentOrder is { Force: true })
        {
            var tp = cm.QueryInterface<PositionComponent>(gatherer.TargetSupply.Value);
            if (tp == null) return null;
            center = new FixedVector2D(tp.Position.X, tp.Position.Z);
        }
        else
        {
            var pos = cm.QueryInterface<PositionComponent>(u.Entity);
            if (pos == null) return null;
            center = new FixedVector2D(pos.Position.X, pos.Position.Z);
        }
        return FindSupplyNear(u, cm, center, specific, template);
    }

    /// <summary>共享就近资源查找(原版 FindNearbyResource 的核心):半径 64、非空供应、
    /// 排除刚采空目标、可选 specific/template 过滤、属主可见,取最近者。
    /// 无 RangeManager 的测试/回放环境降级为 AllEntities 线性扫描(LOS 过滤随之省略)。</summary>
    private static EntityId? FindSupplyNear(UnitAIComponent u, ComponentManager cm,
        FixedVector2D center, string? specific, string? template)
    {
        var range = SimSystem.Range;
        var own = cm.QueryInterface<OwnershipComponent>(u.Entity);
        var exclude = u._depletedSupply;

        bool Eligible(EntityId e)
        {
            if (exclude.HasValue && e == exclude.Value) return false;
            var supply = cm.QueryInterface<ResourceSupply>(e);
            if (supply == null || supply.IsEmpty) return false;
            if (specific != null && supply.SpecificType != specific) return false;
            if (template != null)
            {
                var id = cm.QueryInterface<IdentityComponent>(e);
                if (id == null || id.TemplateName != template) return false;
            }
            if (range != null && own != null
                && range.GetLosVisibility(e, own.PlayerId) != LosVisibility.Visible)
                return false;
            return cm.QueryInterface<PositionComponent>(e) != null;
        }

        EntityId? best = null;
        float bestDist2 = float.MaxValue;
        var candidates = range != null
            ? range.ExecuteQuery(u.Entity, Fixed.Zero, Fixed.FromInt(64), Eligible)
            : System.Linq.Enumerable.Where(cm.AllEntities, Eligible);
        foreach (var e in candidates)
        {
            var p = cm.QueryInterface<PositionComponent>(e)!;
            float dx = p.Position.X.ToFloat() - center.X.ToFloat();
            float dz = p.Position.Z.ToFloat() - center.Y.ToFloat();
            float d2 = dx * dx + dz * dz;
            if (d2 < bestDist2) { bestDist2 = d2; best = e; }
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

    /// <summary>COLLECTTREASURE 换目标(原版 FINDINGNEWTARGET):视野内找首个可收宝物
    /// (最近优先,EntityId 序保平手确定)。无 → null(调用方 FinishOrder)。</summary>
    private static EntityId? FindNewTreasureTarget(UnitAIComponent u,
        TreasureCollectorComponent tc, ComponentManager cm)
    {
        var pos = cm.QueryInterface<PositionComponent>(u.Entity);
        var range = SimSystem.Range;
        var vision = cm.QueryInterface<VisionComponent>(u.Entity);
        if (pos == null || range == null) return null;
        if (vision == null || vision.Range <= Fixed.Zero) return null;

        EntityId? best = null;
        float bestDist = float.MaxValue;
        foreach (var e in range.ExecuteQuery(u.Entity, Fixed.Zero, vision.Range))
        {
            if (e == u.Entity) continue;
            if (!tc.CanCollect(cm, e)) continue;
            var ep = cm.QueryInterface<PositionComponent>(e);
            if (ep == null) continue;
            float dx = ep.Position.X.ToFloat() - pos.Position.X.ToFloat();
            float dz = ep.Position.Z.ToFloat() - pos.Position.Z.ToFloat();
            float d2 = dx * dx + dz * dz;
            if (d2 < bestDist || (d2 == bestDist && e.Value < (best?.Value ?? uint.MaxValue)))
            {
                bestDist = d2;
                best = e;
            }
        }
        return best;
    }

    /// <summary>HEAL 目标失效后的换目标(原版 FINDINGNEWTARGET):视野内找首个可治疗
    /// 的己方伤员(最近优先,EntityId 序保平手确定)。无 → null(调用方 FinishOrder)。</summary>
    private static EntityId? FindNewHealTarget(UnitAIComponent u, HealComponent heal, ComponentManager cm)
    {
        var own = cm.QueryInterface<OwnershipComponent>(u.Entity);
        var pos = cm.QueryInterface<PositionComponent>(u.Entity);
        var range = SimSystem.Range;
        var vision = cm.QueryInterface<VisionComponent>(u.Entity);
        if (own == null || pos == null || range == null) return null;
        if (vision == null || vision.Range <= Fixed.Zero) return null;

        EntityId? best = null;
        float bestDist = float.MaxValue;
        foreach (var e in range.ExecuteQuery(u.Entity, Fixed.Zero, vision.Range))
        {
            if (e == u.Entity) continue;
            var eo = cm.QueryInterface<OwnershipComponent>(e);
            if (eo == null || eo.PlayerId != own.PlayerId) continue;
            if (!heal.CanHeal(cm, e)) continue;
            var h = cm.QueryInterface<HealthComponent>(e);
            if (h == null || h.IsDead || !h.IsInjured) continue;
            var ep = cm.QueryInterface<PositionComponent>(e);
            if (ep == null) continue;
            float dx = ep.Position.X.ToFloat() - pos.Position.X.ToFloat();
            float dz = ep.Position.Z.ToFloat() - pos.Position.Z.ToFloat();
            float d2 = dx * dx + dz * dz;
            if (d2 < bestDist || (d2 == bestDist && e.Value < (best?.Value ?? uint.MaxValue)))
            {
                bestDist = d2;
                best = e;
            }
        }
        return best;
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
                // 原版 IDLE.enter → FindNewTargets → AttackEntitiesByPreference:
                // 按攻击偏好分组响应(不再是裸取最近)。
                var pick = PickTargetByPreference(cm, enemies);
                if (pick.HasValue)
                {
                    RespondToTargetedEntity(pick.Value, cm);
                    return;
                }
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

    /// <summary>偏好分组取目标(原版 AttackEntitiesByPreference):
    /// 按攻击件 GetPreference 升序分组,同组内最近优先;无偏好(动物等)垫底。
    /// 原版对 pref==0 短路直应——这里统一返回全表最优。
    /// 距离比较用定点 internal 差平方(不开方不 float,跨平台逐位一致;
    /// 16.16 内值 ~6.6e7/1000m,平方和 ~8.6e15 ≪ long 上限)。</summary>
    private EntityId? PickTargetByPreference(ComponentManager cm, List<EntityId> enemies)
    {
        if (enemies.Count == 0) return null;
        var attack = cm.QueryInterface<AttackComponent>(Entity);
        if (attack == null) return enemies[0];
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        long px = pos?.Position.X.InternalValue ?? 0;
        long pz = pos?.Position.Z.InternalValue ?? 0;

        EntityId? best = null;
        int bestPref = int.MaxValue;
        long bestDist2 = long.MaxValue;
        foreach (var e in enemies)
        {
            int pref = attack.GetPreference(cm, e) ?? int.MaxValue - 1;
            var ep = cm.QueryInterface<PositionComponent>(e);
            long dx = (ep?.Position.X.InternalValue ?? 0) - px;
            long dz = (ep?.Position.Z.InternalValue ?? 0) - pz;
            long dist2 = dx * dx + dz * dz;
            // 偏好升序 → 距离升序(确定性 tie-break:id 升序)。
            if (best == null || pref < bestPref || pref == bestPref && dist2 < bestDist2
                || pref == bestPref && dist2 == bestDist2 && e.Value < best.Value.Value)
            {
                bestPref = pref;
                bestDist2 = dist2;
                best = e;
            }
        }
        return best;
    }

    /// <summary>视野内可见、可攻击的敌对玩家实体(原版 FindNewTargets 的目标掩码)。
    /// gaia(owner≤0)排除:不自动打猎/砍树;敌对野兽的反击经 OnAttacked 覆盖。</summary>
    private List<EntityId> FindVisibleEnemies(ComponentManager cm, StanceFlags flags)
    {
        var empty = new List<EntityId>();
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        var range = SimSystem.Range;
        var vision = cm.QueryInterface<VisionComponent>(Entity);
        if (vision == null || vision.Range <= Fixed.Zero) return empty;
        if (own == null)
        {
            // gaia 动物(狼等 aggressive 站姿)无主索敌:目标是任意真实玩家的存活单位;
            // gaia 无 LOS 网格,不做视野过滤(原版动物感知不走玩家视野)。
            if (range == null || !IsAnimal || !flags.TargetVisibleEnemies) return empty;
            if (cm.QueryInterface<AttackComponent>(Entity) == null) return empty;
            return range.ExecuteQuery(Entity, Fixed.Zero, vision.Range, e =>
            {
                var eo = cm.QueryInterface<OwnershipComponent>(e);
                if (eo == null || eo.PlayerId <= 0) return false;
                var h = cm.QueryInterface<HealthComponent>(e);
                return h is { IsDead: false };
            });
        }
        if (range == null) return empty;
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

    // =========================================================================
    // WAF / 巡逻 / 逃跑 / 护卫辅助(原版 UnitAI.js 同名逻辑的 C# 形)
    // =========================================================================

    private const float StanceScanIntervalCombat = 1.0f;   // 原版 StartTimer(0,1000)
    // 护卫半径不再用硬编码常量:GuardRangeOf() = GuardComponent.GetRange(护卫自身)
    // (原版 Guard.js GetRange:8 + footprint 派生;template_unit.xml 的 <Guard/> 无 Range 字段)。
    /// <summary>逃跑距离(template_unit.xml FleeDistance=12;动物模板可覆盖,如 fauna 24)。</summary>
    public float FleeDistance = 12f;
    private const float PatrolWaitTime = 1f;               // template_unit.xml PatrolWaitTime

    /// <summary>WAF/巡逻/护卫共用的 1s 节流索敌(原版 FindWalkAndFightTargets):
    /// stance 允许索敌时按攻击偏好取目标前插 Attack(AttackEntitiesByPreference——
    /// 偏好升序 → 距离升序 → id 保平手,不再是裸取 enemies[0]);攻击订单完成后
    /// 队列回到当前订单,自动继续(= 原版 returningState 语义)。</summary>
    private static void ScanAndEngage(UnitAIComponent u, FsmMessage m)
    {
        u._combatScanElapsed += m.Dt;
        if (u._combatScanElapsed < StanceScanIntervalCombat) return;
        u._combatScanElapsed = 0;
        var flags = u.CurrentStanceFlags;
        if (!flags.TargetVisibleEnemies) return;
        var enemies = u.FindVisibleEnemies(m.Cm!, flags);
        if (enemies.Count == 0) return;
        var pick = u.PickTargetByPreference(m.Cm!, enemies);
        if (pick.HasValue)
            u.PushOrderFront(new UnitOrder { Type = "Attack", Target = pick.Value, Force = false });
    }

    /// <summary>原版 FLEEING.enter:背离威胁移动 FleeDistance(原版 distanceToFlee =
    /// 当前距离+FleeDistance,等价于直线背离 12m)。威胁/位置缺失 → false(订单失败)。</summary>
    private static bool StartFlee(UnitAIComponent u, EntityId? threat, ComponentManager cm)
    {
        if (threat is not { } t) return false;
        var pos = cm.QueryInterface<PositionComponent>(u.Entity);
        var tp = cm.QueryInterface<PositionComponent>(t);
        if (pos == null || tp == null) return false;
        float dx = pos.Position.X.ToFloat() - tp.Position.X.ToFloat();
        float dz = pos.Position.Z.ToFloat() - tp.Position.Z.ToFloat();
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 0.001f) { dx = 1f; dz = 0f; len = 1f; }   // 重叠 → 确定性 +x 方向
        var dest = new FixedVector2D(
            Fixed.FromFloat(pos.Position.X.ToFloat() + dx / len * u.FleeDistance),
            Fixed.FromFloat(pos.Position.Z.ToFloat() + dz / len * u.FleeDistance));
        StartMovingTo(u, dest, cm);
        return true;
    }

    /// <summary>原版 ShouldGuard:我方/盟军 且(存活 || 有 Capturable || 有
    /// StatusEffectsReceiver)——后两支让"可占领/可上状态效果的实体"(如可占建筑)
    /// 在无 Health/将死时仍可被护卫(UnitAI.js:5584-5589)。</summary>
    private static bool ShouldGuard(UnitAIComponent u, EntityId target, ComponentManager cm)
    {
        var own = cm.QueryInterface<OwnershipComponent>(u.Entity);
        var tOwn = cm.QueryInterface<OwnershipComponent>(target);
        if (own == null || tOwn == null || tOwn.PlayerId <= 0) return false;
        if (cm.Players.IsEnemy(own.PlayerId, tOwn.PlayerId)) return false;
        var h = cm.QueryInterface<HealthComponent>(target);
        if (h != null && !h.IsDead) return true;
        return cm.QueryInterface<CapturableComponent>(target) != null
            || cm.QueryInterface<StatusEffectsReceiverComponent>(target) != null;
    }

    /// <summary>护卫半径(原版 this.guardRange = cmpGuard.GetRange(this.entity),
    /// AddGuard 时取):8 + 护卫自身 footprint 派生。按需重算(footprint 恒定,
    /// 省去序列化字段)。</summary>
    private static Fixed GuardRangeOf(UnitAIComponent u, ComponentManager cm) =>
        GuardComponent.GetRange(cm, u.Entity);

    /// <summary>是否在护卫半径内(原版 CheckTargetRangeExplicit(isGuardOf, 0, guardRange);
    /// 中心距,定点比较)。</summary>
    private static bool InGuardRange(UnitAIComponent u, EntityId target, ComponentManager cm, Fixed range)
    {
        var pos = cm.QueryInterface<PositionComponent>(u.Entity);
        var tp = cm.QueryInterface<PositionComponent>(target);
        if (pos == null || tp == null) return false;
        var diff = new FixedVector2D(pos.Position.X - tp.Position.X, pos.Position.Z - tp.Position.Z);
        return diff.CompareLength(range) <= 0;
    }

    private void TryPushStanceAttack(EntityId target, ComponentManager cm)
    {
        if (cm.QueryInterface<AttackComponent>(Entity) == null) return;
        // Force=false:stance 自发攻击;Order.Attack handler 仍走 AttackTarget 合法性门。
        PushOrder(new UnitOrder { Type = "Attack", Target = target, Force = false });
    }

    /// <summary>逃离威胁——升级为真 Flee 订单(FLEEING 状态:背离 12m 奔跑,
    /// 到达/被堵才结算;原一次性 Walk 15m 简化版移除)。站姿自发响应 Force=false
    /// (可被后续强制订单打断,同 stance 攻击;OnAttacked 的 Force 门依赖此)。</summary>
    private void FleeFrom(EntityId threat, ComponentManager cm)
        => PushOrder(new UnitOrder { Type = "Flee", Target = threat, Queued = false, Force = false });

    /// <summary>受击响应(原版 FSM "Attacked" 消息;唯一调用点 = DelayedDamage.ApplyDirect,
    /// 物理伤害 >0 时)。按 stance 表反击/逃跑/无视;玩家强制订单(Force=true)不被
    /// 打断、攻击者须可见——violent(targetAttackersAlways)豁免这两条。</summary>
    public void OnAttacked(EntityId attacker, ComponentManager cm)
    {
        if (_isCorpse || IsGarrisoned || IsTurret || IsFormationController) return;
        var flags = CurrentStanceFlags;
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        var aOwn = cm.QueryInterface<OwnershipComponent>(attacker);
        // 攻击者须为真实玩家;无主实体(gaia 动物)按玩家 0——动物不设外交检查
        // (gaia 无人可敌,但动物被打要跑/反击,原版即如此),且跳过 LOS 可见性门
        // (gaia 无 LOS 网格;原版动物感知不走玩家视野)。
        if (aOwn == null || aOwn.PlayerId <= 0) return;
        if (own != null && !cm.Players.IsEnemy(own.PlayerId, aOwn.PlayerId)) return;
        var ah = cm.QueryInterface<HealthComponent>(attacker);
        if (ah == null || ah.IsDead) return;
        if (!flags.TargetAttackersAlways && CurrentOrder is { Force: true }) return;
        if (!flags.TargetAttackersAlways && own != null)
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
            // 编队控制器 returningState(本存档周期追加)。
            s.Bool("hasret", o.ReturningState != null);
            if (o.ReturningState != null) s.StringASCII("retstate", o.ReturningState);
            // 存档 v19 尾段:Repair autocontinue + Trade 航线 route(原版
            // order.data.autocontinue / order.data.route)。
            s.Bool("autocont", o.AutoContinue);
            s.Bool("hasroute", o.Route != null);
            if (o.Route != null)
            {
                s.NumberI32("routen", o.Route.Count);
                s.NumberI32("routei", o.RouteIndex);
                foreach (var wp in o.Route)
                {
                    s.NumberFixed("rwx", wp.X);
                    s.NumberFixed("rwz", wp.Y);
                }
            }
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
        // 巡逻/护卫负载(本存档周期追加,读序须与写序逐位一致)。
        s.Bool("patrolValid", _patrolStart.HasValue);
        s.NumberFixed("patrolX", _patrolStart?.X ?? Fixed.Zero);
        s.NumberFixed("patrolZ", _patrolStart?.Y ?? Fixed.Zero);
        s.NumberFixed("patrolWait", Fixed.FromFloat(_patrolWaitElapsed));
        s.NumberU32("guardOf", _isGuardOf?.Value ?? 0);
        s.NumberFixed("combatScan", Fixed.FromFloat(_combatScanElapsed));
        // 动物行为负载(本存档周期追加,读序须与写序逐位一致)。
        s.NumberFixed("roamDist", Fixed.FromFloat(RoamDistance));
        s.NumberFixed("roamTMin", Fixed.FromFloat(RoamTimeMin));
        s.NumberFixed("roamTMax", Fixed.FromFloat(RoamTimeMax));
        s.NumberFixed("feedTMin", Fixed.FromFloat(FeedTimeMin));
        s.NumberFixed("feedTMax", Fixed.FromFloat(FeedTimeMax));
        s.NumberFixed("fleeDist", Fixed.FromFloat(FleeDistance));
        s.NumberFixed("animalTimer", Fixed.FromFloat(_animalTimer));
        s.Bool("animalFeeding", _animalFeeding);
        s.NumberFixed("roamAngle", Fixed.FromFloat(_roamAngle));
        s.NumberFixed("roamStartAngle", Fixed.FromFloat(_roamStartAngle));
        s.Bool("roamAngleInit", _roamAngleInit);
        s.Bool("isCorpse", _isCorpse);
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
            o.ReturningState = d.Bool("hasret") ? d.StringASCII("retstate") : null;
            // 存档 v19 尾段(更早的档没有,按默认读;见 SaveFormat.LoadedVersion)。
            if (SaveFormat.LoadedVersion >= 19)
            {
                o.AutoContinue = d.Bool("autocont");
                if (d.Bool("hasroute"))
                {
                    int routen = d.NumberI32("routen");
                    o.RouteIndex = d.NumberI32("routei");
                    var route = new List<FixedVector2D>(routen);
                    for (int r = 0; r < routen; r++)
                        route.Add(new FixedVector2D(d.NumberFixed("rwx"), d.NumberFixed("rwz")));
                    o.Route = route;
                }
            }
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
        // 巡逻/护卫负载(读序与 Serialize 写序逐位一致)。
        bool patrolValid = d.Bool("patrolValid");
        var patrolX = d.NumberFixed("patrolX");
        var patrolZ = d.NumberFixed("patrolZ");
        _patrolStart = patrolValid ? new FixedVector2D(patrolX, patrolZ) : null;
        _patrolWaitElapsed = d.NumberFixed("patrolWait").ToFloat();
        uint guardOf = d.NumberU32("guardOf");
        _isGuardOf = guardOf != 0 ? new EntityId(guardOf) : null;
        _combatScanElapsed = d.NumberFixed("combatScan").ToFloat();
        // 动物行为负载(与写序逐位一致)。
        RoamDistance = d.NumberFixed("roamDist").ToFloat();
        RoamTimeMin = d.NumberFixed("roamTMin").ToFloat();
        RoamTimeMax = d.NumberFixed("roamTMax").ToFloat();
        FeedTimeMin = d.NumberFixed("feedTMin").ToFloat();
        FeedTimeMax = d.NumberFixed("feedTMax").ToFloat();
        FleeDistance = d.NumberFixed("fleeDist").ToFloat();
        _animalTimer = d.NumberFixed("animalTimer").ToFloat();
        _animalFeeding = d.Bool("animalFeeding");
        _roamAngle = d.NumberFixed("roamAngle").ToFloat();
        _roamStartAngle = d.NumberFixed("roamStartAngle").ToFloat();
        _roamAngleInit = d.Bool("roamAngleInit");
        _isCorpse = d.Bool("isCorpse");
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
