using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// GarrisonHolder + Garrisonable — ports of GarrisonHolder.js / Garrisonable.js。
// 单位进驻持有者(建筑/船)后离开世界(PositionComponent.InWorld=false,对齐原版
// Position.MoveOutOfWorld),UnitAI 冻结订单处理;持有者按 Garrisonable.TotalSize 计容量,
// 可 BuffHeal 每秒回血(原版 HEAL_TIMEOUT=1000ms 定时器),EjectHealth 低血逐出,
// 被毁时按 EjectClassesOnDestroy 逐出可逐类别、其余随主同灭(EjectOrKill)。
// 已移植:Pickup 接送(行为在 UnitAI GARRISON.APPROACHING 的 pickup 登记)、
// initGarrison(地图初始驻军,ScenarioLoader/SimBridge)、外交翻面/易主即时逐出
// (懒订阅 OwnerChanged + DiplomacyChanged,见 WireEvictionEvents)、
// AllowGarrisoning 外部锁(callerID→bool 与门,锁定拒进拒出,EjectOrKill forced 例外)、
// 类别表变更逐出(原版 OnValueModification;ModifiersManager 无变更钩子,Tick 1s 低频复查兜底)。
// 不移植(记录):UnloadTemplate/UnloadAllByOwner(GUI 批量卸载)、
// GetGarrisonedEntitiesCount 递归计数(统计面板用)。

[Component("GarrisonHolder", "GarrisonHolder")]
public sealed class GarrisonHolderComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>runtime:舱内实体(原版 this.entities)。</summary>
    public readonly List<EntityId> Entities = new();
    public int Max = 10;                              // template GarrisonHolder/Max
    public readonly List<string> AllowedClasses = new(); // template GarrisonHolder/List
    public string EjectClassesOnDestroy = "";         // template GarrisonHolder/EjectClassesOnDestroy
    public float BuffHeal;                            // template GarrisonHolder/BuffHeal(HP/s)
    public float LoadingRange = 2f;                   // template GarrisonHolder/LoadingRange
    public float EjectHealth = -1f;                   // template 可选;-1 = 无阈值(原版 undefined)
    public bool Pickup;                               // template 可选;接送行为在 UnitAI GARRISON.APPROACHING
    public float HealElapsed;                         // runtime:距上次回血的累计秒数

    // ── AllowGarrisoning 外部锁(原版 GarrisonHolder.js:117-131:callerID→bool Map 与门)──
    // 任一 caller 置 false 即拒进拒出(行驶中的载具不可上下等场景)。
    // 确定性:SortedList 按键定序枚举,不依赖 Dictionary 枚举序(与门本可乱序,
    // 但序列化写序必须定序,统一用排序容器)。
    private readonly SortedList<string, bool> _garrisoningLocks = new(StringComparer.Ordinal);
    private float _recheckElapsed;                    // runtime:距上次驻军类别复查的累计秒数

    // 默认值全在字段初始化器,OnInit 保持空 —— 调用方用对象初始化器赋值不被 clobber。

    // ── 即时逐出(原版 OnGlobalOwnershipChanged/OnDiplomacyChanged)──
    private ComponentManager? _subscribedCm;

    /// <summary>事件订阅(懒,首个 Tick;OnInit 期 SimSystem.Sim 可能未就位)。
    /// 原版:GarrisonHolder.js OnGlobalOwnershipChanged(易主即逐出非互盟)+
    /// OnDiplomacyChanged(外交翻面即逐出非互盟)。</summary>
    private void WireEvictionEvents(ComponentManager cm)
    {
        if (_subscribedCm != null) return;
        _subscribedCm = cm;
        cm.OwnerChanged += OnAnyOwnershipChanged;
        cm.Events.DiplomacyChanged += OnDiplomacyChanged;
    }

    protected override void OnDeinit()
    {
        if (_subscribedCm == null) return;
        _subscribedCm.OwnerChanged -= OnAnyOwnershipChanged;
        _subscribedCm.Events.DiplomacyChanged -= OnDiplomacyChanged;
        _subscribedCm = null;
    }

    /// <summary>非互盟驻军即时逐出(原版 EjectOrKill(entities.filter(!互盟)))。</summary>
    private void EjectNonMutualAllies(ComponentManager cm)
    {
        if (Entities.Count == 0) return;
        var hostiles = Entities
            .Where(e => !DiplomacyComponent.IsMutualAllyOfEntity(cm, Entity, e))
            .ToList();
        if (hostiles.Count > 0)
            EjectOrKill(cm, hostiles);
    }

    private void OnDiplomacyChanged(Events.DiplomacyChangedEvent e)
    {
        // 只关心涉及本 holder 属主的变化(原版全局广播,各 holder 自查)。
        var cm = _subscribedCm;
        if (cm == null) return;
        int myOwner = cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;
        if (e.Player != myOwner && e.OtherPlayer != myOwner) return;
        EjectNonMutualAllies(cm);
    }

    private void OnAnyOwnershipChanged(EntityId entity, int from, int to)
    {
        var cm = _subscribedCm;
        if (cm == null) return;
        // 原版 OnGlobalOwnershipChanged:holder 自身易主,或舱内单位易主
        // (被抢走的单位不算——to==INVALID 由 Guard 侧管;新主与我非互盟 → 逐)。
        if (entity != Entity && !Entities.Contains(entity)) return;
        EjectNonMutualAllies(cm);
    }


    /// <summary>Port of GarrisonHolder.CanPickup(GarrisonHolder.js:69-75):
    /// 模板 Pickup + 未满 + 乘客与持有者同主(原版 IsOwnedByPlayer)。</summary>
    public bool CanPickup(ComponentManager cm, EntityId passenger)
    {
        if (!Pickup) return false;
        var g = cm.QueryInterface<GarrisonableComponent>(passenger);
        if (g == null || OccupiedSlots(cm) + g.TotalSize(cm) > GetCapacity(cm)) return false;
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        var pown = cm.QueryInterface<OwnershipComponent>(passenger);
        return own != null && pown != null && own.PlayerId == pown.PlayerId;
    }

    /// <summary>Port of GetCapacity(经修正值管线)。</summary>
    public int GetCapacity(ComponentManager cm) =>
        (int)Math.Round(cm.Modifiers.ApplyPrefix("GarrisonHolder/Max", Max, Entity),
            MidpointRounding.AwayFromZero);

    /// <summary>Port of GetHealRate(经修正值管线)。</summary>
    public float GetHealRate(ComponentManager cm) =>
        cm.Modifiers.ApplyPrefix("GarrisonHolder/BuffHeal", BuffHeal, Entity);

    /// <summary>Port of OccupiedSlots:舱内各 Garrisonable 的 TotalSize 之和。</summary>
    public int OccupiedSlots(ComponentManager cm)
    {
        int count = 0;
        foreach (var e in Entities)
            count += cm.QueryInterface<GarrisonableComponent>(e)?.TotalSize(cm) ?? 0;
        return count;
    }

    /// <summary>Port of AllowGarrisoning(GarrisonHolder.js:117-131):登记 caller 的放行票。
    /// 每个调用方用自己的 callerID;与门——一票否决。</summary>
    public void SetGarrisoningAllowed(string callerId, bool allowed) =>
        _garrisoningLocks[callerId] = allowed;

    /// <summary>Port of IsGarrisoningAllowed(GarrisonHolder.js:159):无登记或全放行 → true。
    /// 锁定时舱内单位也不可出驻(原版注释:行驶中的载具不可上下),forced 逐出例外。</summary>
    public bool IsGarrisoningAllowed()
    {
        foreach (var kv in _garrisoningLocks)
            if (!kv.Value) return false;
        return true;
    }

    /// <summary>Port of IsAllowedToGarrison:IsGarrisoningAllowed 门控 + 容量 +
    /// IsAllowedToBeGarrisoned。</summary>
    public bool IsAllowedToGarrison(ComponentManager cm, EntityId entity)
    {
        if (!IsGarrisoningAllowed())
            return false;
        var g = cm.QueryInterface<GarrisonableComponent>(entity);
        if (g == null || OccupiedSlots(cm) + g.TotalSize(cm) > GetCapacity(cm))
            return false;
        return IsAllowedToBeGarrisoned(cm, entity);
    }

    /// <summary>Port of IsAllowedToBeGarrisoned:互盟持有 + 类别匹配。</summary>
    public bool IsAllowedToBeGarrisoned(ComponentManager cm, EntityId entity)
    {
        // IsOwnedByMutualAllyOfEntity(entity, holder):同主或互盟(同队 seed)。
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        var entOwn = cm.QueryInterface<OwnershipComponent>(entity);
        if (own == null || entOwn == null)
            return false;
        if (entOwn.PlayerId != own.PlayerId
            && !cm.Players.GetMutualAllies(own.PlayerId).Contains(entOwn.PlayerId))
            return false;
        var identity = cm.QueryInterface<IdentityComponent>(entity);
        return identity != null && identity.MatchesClassList(string.Join(" ", AllowedClasses));
    }

    /// <summary>Port of Garrison:允许 + 血量足够 → 入舱(回血计时器由 Tick 驱动,无需启动)。</summary>
    public bool Garrison(ComponentManager cm, EntityId entity)
    {
        if (!IsAllowedToGarrison(cm, entity))
            return false;
        if (!HasEnoughHealth(cm))
            return false;
        Entities.Add(entity);
        return true;
    }

    /// <summary>Port of Eject(GarrisonHolder.js:211):锁定且非 forced 拒绝;不在舱内视为
    /// 成功(原版注释:通常已被逐出)。forced = 建筑被毁/外交逐出等内部路径。</summary>
    public bool Eject(EntityId entity, bool forced = false)
    {
        if (!IsGarrisoningAllowed() && !forced)
            return false;
        return Entities.Remove(entity) || true;
    }

    /// <summary>Port of Unload:命令该单位自行出驻。</summary>
    public bool Unload(ComponentManager cm, EntityId entity) =>
        cm.QueryInterface<GarrisonableComponent>(entity)?.UnGarrison(cm) ?? false;

    /// <summary>Port of UnloadAll(原版还会走集结点,见 Garrisonable.UnGarrison)。</summary>
    public bool UnloadAll(ComponentManager cm)
    {
        bool success = true;
        foreach (var e in new List<EntityId>(Entities))
            if (!Unload(cm, e))
                success = false;
        return success;
    }

    /// <summary>Port of HasEnoughHealth:无阈值/无 Health → true;否则 Current > 阈值下限。</summary>
    public bool HasEnoughHealth(ComponentManager cm)
    {
        if (EjectHealth < 0f)
            return true;
        var health = cm.QueryInterface<HealthComponent>(Entity);
        return health == null || health.Current > (int)Math.Floor(EjectHealth * health.Max);
    }

    /// <summary>每回合驱动(SimBridge):低血逐出(替代原版 OnHealthChanged 消息)+
    /// 类别复查逐出(1s 节流,替代原版 OnValueModification)+
    /// BuffHeal 每秒一次回血(替代原版 1s HealTimeout 定时器;无舱员/无速率即停表)。</summary>
    public void Tick(float dt, ComponentManager cm)
    {
        WireEvictionEvents(cm);
        if (Entities.Count > 0 && !HasEnoughHealth(cm))
            EjectOrKill(cm, new List<EntityId>(Entities));

        // 类别表变更逐出(原版 OnValueModification:GarrisonHolder/List 经修正值变更后,
        // EjectOrKill 不再匹配 IsAllowedToBeGarrisoned 者)。本移植 ModifiersManager 无变更
        // 通知钩子 → Tick 内 1s 低频复查兜底(互盟段与事件逐出重叠,复查无害)。
        if (Entities.Count > 0)
        {
            _recheckElapsed += dt;
            if (_recheckElapsed >= 1f)
            {
                _recheckElapsed = 0f;
                var mismatched = Entities
                    .Where(e => !IsAllowedToBeGarrisoned(cm, e))
                    .ToList();
                if (mismatched.Count > 0)
                    EjectOrKill(cm, mismatched);
            }
        }
        else
            _recheckElapsed = 0f;

        float rate = GetHealRate(cm);
        if (Entities.Count == 0 || rate <= 0f)
        {
            HealElapsed = 0f;
            return;
        }
        HealElapsed += dt;
        while (HealElapsed >= 1f)
        {
            HealElapsed -= 1f;
            int amount = (int)Math.Round(rate, MidpointRounding.AwayFromZero);
            foreach (var e in Entities)
            {
                var health = cm.QueryInterface<HealthComponent>(e);
                if (health != null && !health.Unhealable)
                    health.Heal(amount);
            }
        }
    }

    /// <summary>Port of EjectOrKill:持有者在世界内 → 逐出匹配 EjectClassesOnDestroy 者;
    /// 其余就地击杀(Health=0,由 RemoveDeadEntities 清扫;无 Health 直接销毁)。</summary>
    public void EjectOrKill(ComponentManager cm, List<EntityId> entities)
    {
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        if (pos is { InWorld: true })
        {
            foreach (var e in new List<EntityId>(entities))
                if (IsEjectable(cm, e))
                    // forced=true:锁定时建筑被毁/外交逐出也必须能逐出(原版 Eject 的 forced 例外)。
                    cm.QueryInterface<GarrisonableComponent>(e)?.UnGarrison(cm, forced: true);
        }
        foreach (var e in entities)
        {
            if (!Entities.Remove(e))
                continue;   // 已逐出(或早已离舱)
            var health = cm.QueryInterface<HealthComponent>(e);
            var g = cm.QueryInterface<GarrisonableComponent>(e);
            if (g != null) g.Holder = null;
            if (health != null)
                health.Current = 0;   // 原版 cmpHealth.Kill();死亡清扫在 SimBridge.RemoveDeadEntities
            else
                cm.DestroyEntity(e);
        }
    }

    /// <summary>持有者被毁兜底(SimBridge.RemoveDeadEntities 在 DestroyEntity 前调用)。</summary>
    public void EjectOrKillAll(ComponentManager cm) => EjectOrKill(cm, new List<EntityId>(Entities));

    /// <summary>Port of IsEjectable:在舱且类别匹配 EjectClassesOnDestroy(空串 → 全不可逐)。</summary>
    public bool IsEjectable(ComponentManager cm, EntityId entity)
    {
        if (!Entities.Contains(entity))
            return false;
        var identity = cm.QueryInterface<IdentityComponent>(entity);
        return identity != null && identity.MatchesClassList(EjectClassesOnDestroy);
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("ent_n", Entities.Count);
        foreach (var e in Entities) s.NumberU32("ent", e.Value);
        s.NumberI32("max", Max);
        s.NumberI32("allowed_n", AllowedClasses.Count);
        foreach (var c in AllowedClasses) s.StringASCII("allowed", c);
        s.StringASCII("ejectClasses", EjectClassesOnDestroy);
        s.NumberFixed("buffHeal", Fixed.FromFloat(BuffHeal));
        s.NumberFixed("loadingRange", Fixed.FromFloat(LoadingRange));
        s.NumberFixed("ejectHealth", Fixed.FromFloat(EjectHealth));
        s.Bool("pickup", Pickup);
        s.NumberFixed("healElapsed", Fixed.FromFloat(HealElapsed));
        // 存档 v18 尾段:AllowGarrisoning 锁表(键序定序)+ 类别复查计时器。
        s.NumberI32("glock_n", _garrisoningLocks.Count);
        foreach (var kv in _garrisoningLocks)
        {
            s.StringASCII("glock", kv.Key);
            s.Bool("glockv", kv.Value);
        }
        s.NumberFixed("recheck", Fixed.FromFloat(_recheckElapsed));
    }

    public override void Deserialize(IDeserializer d)
    {
        Entities.Clear();
        int n = d.NumberI32("ent_n");
        for (int i = 0; i < n; i++) Entities.Add(new EntityId(d.NumberU32("ent")));
        Max = d.NumberI32("max");
        AllowedClasses.Clear();
        int an = d.NumberI32("allowed_n");
        for (int i = 0; i < an; i++) AllowedClasses.Add(d.StringASCII("allowed"));
        EjectClassesOnDestroy = d.StringASCII("ejectClasses");
        BuffHeal = d.NumberFixed("buffHeal").ToFloat();
        LoadingRange = d.NumberFixed("loadingRange").ToFloat();
        EjectHealth = d.NumberFixed("ejectHealth").ToFloat();
        Pickup = d.Bool("pickup");
        HealElapsed = d.NumberFixed("healElapsed").ToFloat();
        // 存档 v18 尾段:v17 及更早档无此段,按空表/零计时读(见 SaveFormat.LoadedVersion)。
        _garrisoningLocks.Clear();
        _recheckElapsed = 0f;
        if (SaveFormat.LoadedVersion >= 18)
        {
            int gn = d.NumberI32("glock_n");
            for (int i = 0; i < gn; i++)
                _garrisoningLocks[d.StringASCII("glock")] = d.Bool("glockv");
            _recheckElapsed = d.NumberFixed("recheck").ToFloat();
        }
    }

    public void HandleMessage(IMessage message) { }
}

[Component("Garrisonable", "Garrisonable")]
public sealed class GarrisonableComponent : ComponentBase, IComponentMessageHandler
{
    public EntityId? Holder;   // this.holder — 当前持有者(舱内实体)
    public int Size = 1;       // template Garrisonable/Size(占用槽数)

    /// <summary>Port of UnitSize(经修正值管线)。</summary>
    public int UnitSize(ComponentManager cm) =>
        (int)Math.Round(cm.Modifiers.ApplyPrefix("Garrisonable/Size", Size, Entity),
            MidpointRounding.AwayFromZero);

    /// <summary>Port of TotalSize:自身 Size + 自身作为持有者的已占槽(船载兵上船)。</summary>
    public int TotalSize(ComponentManager cm)
    {
        int size = UnitSize(cm);
        size += cm.QueryInterface<GarrisonHolderComponent>(Entity)?.OccupiedSlots(cm) ?? 0;
        return size;
    }

    public bool IsGarrisoned => Holder != null;

    /// <summary>Port of CanGarrison:未驻防 + 持有者允许。</summary>
    public bool CanGarrison(ComponentManager cm, EntityId target)
    {
        if (Holder != null)
            return false;
        var holder = cm.QueryInterface<GarrisonHolderComponent>(target);
        return holder != null && holder.IsAllowedToGarrison(cm, Entity);
    }

    /// <summary>到持有者的装填射程内判定(edge-to-edge,同 Heal/Trader 语义;
    /// 对应原版 CheckTargetRange(IID_Garrisonable) → holder.LoadingRange)。</summary>
    public bool IsInLoadingRange(ComponentManager cm, EntityId target, GarrisonHolderComponent holder)
    {
        var a = cm.QueryInterface<PositionComponent>(Entity);
        var b = cm.QueryInterface<PositionComponent>(target);
        if (a == null || b == null)
            return false;
        var dx = a.Position.X - b.Position.X;
        var dz = a.Position.Z - b.Position.Z;
        long d2 = (long)dx.InternalValue * dx.InternalValue
                + (long)dz.InternalValue * dz.InternalValue;
        var eff = Fixed.FromFloat(holder.LoadingRange);
        var obs = cm.QueryInterface<ObstructionComponent>(target);
        if (obs != null)
            eff += obs.GetSize();
        long r2 = (long)eff.InternalValue * eff.InternalValue;
        return d2 <= r2;
    }

    /// <summary>Port of Garrison:入舱 → UnitAI 冻结(SetGarrisoned/SetImmobile)→ 离开世界
    /// (MoveOutOfWorld;RangeManager 同步移出空间索引与 LOS,全玩家 HIDDEN)。</summary>
    public bool Garrison(ComponentManager cm, EntityId target)
    {
        if (!CanGarrison(cm, target))
            return false;
        var holder = cm.QueryInterface<GarrisonHolderComponent>(target)!;
        if (!holder.Garrison(cm, Entity))
            return false;

        Holder = target;
        SimSystem.GetComponent<UnitAIComponent>(Entity)?.SetGarrisoned();
        var pos = SimSystem.GetComponent<PositionComponent>(Entity);
        if (pos != null)
        {
            pos.InWorld = false;
            SimSystem.Range?.SetInWorld(Entity, false);
        }
        return true;
    }

    /// <summary>出驻/下塔出生点(与 Turretable.LeaveTurret 共用):优先持有者
    /// Footprint.PickSpawnPoint(对齐原版 GetSpawnPosition);无寻路或未找到 → 持有者障碍
    /// 边缘外 +X 固定偏移(确定性兜底,记录:原版无位拒出)。调用方须先验持有者位置件。</summary>
    internal static FixedVector3D FindSpawnOutside(ComponentManager cm, EntityId holderId, float radius)
    {
        var holderPos = cm.QueryInterface<PositionComponent>(holderId)!;
        var fp = cm.QueryInterface<FootprintComponent>(holderId);
        var spawn = fp?.PickSpawnPoint(Fixed.FromFloat(radius))
            ?? new FixedVector3D(Fixed.FromInt(-1), Fixed.FromInt(-1), Fixed.FromInt(-1));
        if (spawn.X.ToFloat() >= 0f)
            return spawn;
        float holderSize = cm.QueryInterface<ObstructionComponent>(holderId)?.GetSize().ToFloat() ?? 4f;
        float off = holderSize + radius + 1f;
        return new FixedVector3D(
            holderPos.Position.X + Fixed.FromFloat(off), Fixed.Zero, holderPos.Position.Z);
    }

    /// <summary>Port of UnGarrison:找出生点 → 出舱 → 回世界 → UnitAI 解冻 → 集结点 Walk。
    /// 出生点优先持有者 Footprint.PickSpawnPoint(对齐原版 GetSpawnPosition);未找到时
    /// 本移植用固定偏移兜底(原版拒出;内核无寻路环境下必须可出,记录差异)。
    /// 原版 UnitAI 的 "Ungarrison" 标记指令不移植(本 FSM 的标记+余单路径会卡 IDLE 派发),
    /// 集结点 Walk 直接入队。</summary>
    public bool UnGarrison(ComponentManager cm, bool forced = false)
    {
        if (Holder is not { } holderId)
            return true;
        var holderPos = cm.QueryInterface<PositionComponent>(holderId);
        if (holderPos == null)
            return false;

        float radius = cm.QueryInterface<ObstructionComponent>(Entity)?.GetSize().ToFloat() ?? 1f;
        var spawn = FindSpawnOutside(cm, holderId, radius);

        var holder = cm.QueryInterface<GarrisonHolderComponent>(holderId);
        if (holder == null || !holder.Eject(Entity, forced))
            return false;

        var pos = cm.QueryInterface<PositionComponent>(Entity);
        if (pos != null)
        {
            var old = new FixedVector2D(pos.Position.X, pos.Position.Z);
            pos.Position = spawn;
            pos.InWorld = true;
            SimSystem.NotifyPositionChanged(Entity, old, new FixedVector2D(spawn.X, spawn.Z));
            SimSystem.Range?.SetInWorld(Entity, true);
        }

        var ai = SimSystem.GetComponent<UnitAIComponent>(Entity);
        ai?.UnsetGarrisoned();
        Holder = null;

        // 集结点(原版 RallyPoint.OrderToRallyPoint,略 "garrison" 忽略集 → 直接 Walk)。
        var rally = cm.QueryInterface<RallyPointComponent>(holderId);
        if (ai != null && rally != null
            && (rally.Position.X != Fixed.Zero || rally.Position.Y != Fixed.Zero))
            ai.Walk(rally.Position, queued: false);
        return true;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberU32("holder", Holder?.Value ?? 0);
        s.NumberI32("size", Size);
    }

    public override void Deserialize(IDeserializer d)
    {
        uint h = d.NumberU32("holder");
        Holder = h != 0 ? new EntityId(h) : null;
        Size = d.NumberI32("size");
    }

    public void HandleMessage(IMessage message) { }
}
