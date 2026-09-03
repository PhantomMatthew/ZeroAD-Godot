using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// Turretable + TurretHolder — ports of Turretable.js / TurretHolder.js。
// 炮塔点(城墙/哨塔/船):远程兵占命名点位,位置随持有者可动(对齐原版
// Position.SetTurretParent 的父子联动,本移植由 UpdatePosition 每回合跟拍),
// 留在世界内可作战;Obstruction 停用避免干扰寻路;持有者被毁按 EjectOrKill 逐出/同灭。
//
// 不移植(记录):Pickup 接送、CreateSubunit(模板预置子单位——现网模板无一使用
// <Template> 子节点)、initTurrets(地图初始)、Angle 转向(原版即死代码:
// OccupyTurretPoint 里 `if (!turretPoint && ...)` 永假)、SetReservedTurretPoint、
// OnEntityRenamed 换壳、外交翻面即时下塔(=已知缺口,同 Garrison)。

[Component("TurretHolder", "TurretHolder")]
public sealed class TurretHolderComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>One named slot on the holder. <see cref="Entity"/> is the occupant, or null when empty.</summary>
    public sealed class TurretPoint
    {
        public string Name = "";
        public float OffsetX, OffsetY, OffsetZ;   // template 点位偏移(Y 仅表现层;内核管 XZ)
        public string AllowedClasses = "";        // template 可选;空 = 不限(原版 undefined)
        public float? Angle;                      // template 可选(弧度);原版使用处即死代码,仅存储
        public string Template = "";              // template 可选(CreateSubunit 不移植,仅存储)
        public bool Ejectable = true;
        public EntityId? Entity;
    }

    public readonly List<TurretPoint> TurretPoints = new();
    public float LoadingRange = 2f;   // template 可选(原版 +(LoadingRange || 2))
    public bool Pickup;               // template 可选;行为不移植,仅存字段

    public int Capacity => TurretPoints.Count;

    public TurretPoint? TurretPointByName(string name) =>
        TurretPoints.Find(p => p.Name == name);

    /// <summary>Port of AllowedToOccupyTurretPoint:空位(或替换语义)+ 互盟 + 类别。</summary>
    public bool AllowedToOccupyTurretPoint(ComponentManager cm, EntityId entity,
        TurretPoint? point, bool forReplacement = false)
    {
        if (point == null || (point.Entity != null && !forReplacement))
            return false;
        // IsOwnedByMutualAllyOfEntity(entity, holder):同主或互盟。
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        var entOwn = cm.QueryInterface<OwnershipComponent>(entity);
        if (own == null || entOwn == null)
            return false;
        if (entOwn.PlayerId != own.PlayerId
            && !cm.Players.GetMutualAllies(own.PlayerId).Contains(entOwn.PlayerId))
            return false;
        if (point.AllowedClasses.Length == 0)
            return true;
        var identity = cm.QueryInterface<IdentityComponent>(entity);
        return identity != null && identity.MatchesClassList(point.AllowedClasses);
    }

    /// <summary>Port of CanOccupy:任一点位可占即可。</summary>
    public bool CanOccupy(ComponentManager cm, EntityId entity) =>
        TurretPoints.Exists(p => AllowedToOccupyTurretPoint(cm, entity, p));

    /// <summary>Port of OccupyTurretPoint:点名占点(空名 = 第一个允许的空点)。
    /// 原版的朝向段是死代码(见文件头),不移植;位置由 Turretable.UpdatePosition 跟拍。</summary>
    public bool OccupyTurretPoint(ComponentManager cm, EntityId entity, string pointName = "")
    {
        if (cm.QueryInterface<PositionComponent>(entity) == null
            || cm.QueryInterface<PositionComponent>(Entity) == null)
            return false;
        if (GetOccupiedTurretPoint(entity) != null)
            return false;

        TurretPoint? point = pointName.Length > 0
            ? (AllowedToOccupyTurretPoint(cm, entity, TurretPointByName(pointName))
                ? TurretPointByName(pointName) : null)
            : TurretPoints.Find(p => p.Entity == null && AllowedToOccupyTurretPoint(cm, entity, p));
        if (point == null)
            return false;
        point.Entity = entity;
        return true;
    }

    /// <summary>Port of LeaveTurretPoint:非可逐点需 forced。</summary>
    public bool LeaveTurretPoint(EntityId entity, bool forced = false)
    {
        var point = GetOccupiedTurretPoint(entity);
        if (point == null || (!point.Ejectable && !forced))
            return false;
        point.Entity = null;
        return true;
    }

    public TurretPoint? GetOccupiedTurretPoint(EntityId entity) =>
        TurretPoints.Find(p => p.Entity == entity);

    public string GetOccupiedTurretPointName(EntityId entity) =>
        GetOccupiedTurretPoint(entity)?.Name ?? "";

    /// <summary>Port of GetEntities:全部在点实体。</summary>
    public List<EntityId> GetEntities()
    {
        var result = new List<EntityId>();
        foreach (var p in TurretPoints)
            if (p.Entity != null)
                result.Add(p.Entity.Value);
        return result;
    }

    // ── 即时逐出(原版 Turrets.js/驻军同规则:外交翻面/易主即逐非互盟占位者)──
    private ComponentManager? _subscribedCm;

    protected override void OnInit()
    {
        // 炮塔持有者创建总在世界初始化后(SimSystem.Sim 就位);测试夹具同款序。
        var cm = SimSystem.Sim;
        if (cm == null || _subscribedCm != null) return;
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

    private void EjectNonMutualAlliesNow(ComponentManager cm)
    {
        var hostiles = TurretPoints
            .Where(p => p.Entity != null
                && !DiplomacyComponent.IsMutualAllyOfEntity(cm, Entity, p.Entity.Value))
            .Select(p => p.Entity!.Value)
            .ToList();
        if (hostiles.Count > 0)
            EjectOrKill(cm, hostiles);
    }

    private void OnDiplomacyChanged(Events.DiplomacyChangedEvent e)
    {
        var cm = _subscribedCm;
        if (cm == null) return;
        int myOwner = cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;
        if (e.Player != myOwner && e.OtherPlayer != myOwner) return;
        EjectNonMutualAlliesNow(cm);
    }

    private void OnAnyOwnershipChanged(EntityId entity, int from, int to)
    {
        var cm = _subscribedCm;
        if (cm == null) return;
        if (entity != Entity && TurretPoints.All(p => p.Entity != entity)) return;
        EjectNonMutualAlliesNow(cm);
    }

    /// <summary>Port of EjectOrKill:能下塔的强制下塔;无 Turretable 件的占位者击杀
    /// (Health=0 等 RemoveDeadEntities 清扫;无 Health 直接销毁)。</summary>
    public void EjectOrKill(ComponentManager cm, List<EntityId> entities)
    {
        foreach (var e in entities)
        {
            var tb = cm.QueryInterface<TurretableComponent>(e);
            if (tb == null || !tb.LeaveTurret(cm, forced: true))
            {
                var point = GetOccupiedTurretPoint(e);
                if (point != null)
                    point.Entity = null;
                var health = cm.QueryInterface<HealthComponent>(e);
                if (health != null)
                    health.Current = 0;
                else
                    cm.DestroyEntity(e);
            }
        }
    }

    /// <summary>持有者被毁兜底(SimBridge.RemoveDeadEntities 在 DestroyEntity 前调用)。</summary>
    public void EjectOrKillAll(ComponentManager cm) => EjectOrKill(cm, GetEntities());

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("count", TurretPoints.Count);
        foreach (var p in TurretPoints)
        {
            s.StringASCII("name", p.Name);
            s.NumberU32("entity", p.Entity?.Value ?? 0);
            s.Bool("ejectable", p.Ejectable);
            s.NumberFixed("ox", Fixed.FromFloat(p.OffsetX));
            s.NumberFixed("oy", Fixed.FromFloat(p.OffsetY));
            s.NumberFixed("oz", Fixed.FromFloat(p.OffsetZ));
            s.StringASCII("allowed", p.AllowedClasses);
            s.Bool("hasAngle", p.Angle.HasValue);
            if (p.Angle.HasValue) s.NumberFixed("angle", Fixed.FromFloat(p.Angle.Value));
            s.StringASCII("tmpl", p.Template);
        }
        s.NumberFixed("loadingRange", Fixed.FromFloat(LoadingRange));
        s.Bool("pickup", Pickup);
    }

    public override void Deserialize(IDeserializer d)
    {
        int count = d.NumberI32("count");
        TurretPoints.Clear();
        for (int i = 0; i < count; i++)
        {
            var p = new TurretPoint();
            p.Name = d.StringASCII("name");
            uint e = d.NumberU32("entity");
            p.Entity = e != 0 ? new EntityId(e) : null;
            p.Ejectable = d.Bool("ejectable");
            p.OffsetX = d.NumberFixed("ox").ToFloat();
            p.OffsetY = d.NumberFixed("oy").ToFloat();
            p.OffsetZ = d.NumberFixed("oz").ToFloat();
            p.AllowedClasses = d.StringASCII("allowed");
            if (d.Bool("hasAngle")) p.Angle = d.NumberFixed("angle").ToFloat();
            p.Template = d.StringASCII("tmpl");
            TurretPoints.Add(p);
        }
        LoadingRange = d.NumberFixed("loadingRange").ToFloat();
        Pickup = d.Bool("pickup");
    }

    public void HandleMessage(IMessage message) { }
}

[Component("Turretable", "Turretable")]
public sealed class TurretableComponent : ComponentBase, IComponentMessageHandler
{
    public EntityId? Holder;                  // this.holder
    public bool Ejectable = true;             // 本次占点的可逐性(OccupyTurret 参数)
    public string TurretPointName = "";       // this.turretPointName

    public bool IsTurreted => Holder != null;

    /// <summary>Port of CanOccupy:未在点 + 持有者可占。</summary>
    public bool CanOccupy(ComponentManager cm, EntityId target)
    {
        if (Holder != null)
            return false;
        var holder = cm.QueryInterface<TurretHolderComponent>(target);
        return holder != null && holder.CanOccupy(cm, Entity);
    }

    /// <summary>装填射程内判定(edge-to-edge,同 Garrisonable.IsInLoadingRange;
    /// 对应原版 CheckTargetRange(IID_Turretable))。</summary>
    public bool IsInLoadingRange(ComponentManager cm, EntityId target, TurretHolderComponent holder)
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

    /// <summary>Port of OccupyTurret:占点 → UnitAI 炮塔姿态(SetTurretStance/SetImmobile)→
    /// 障碍停用 → 位置跟拍到点位。</summary>
    public bool OccupyTurret(ComponentManager cm, EntityId target,
        string pointName = "", bool ejectable = true)
    {
        if (!CanOccupy(cm, target))
            return false;
        var holder = cm.QueryInterface<TurretHolderComponent>(target)!;
        if (!holder.OccupyTurretPoint(cm, Entity, pointName))
            return false;

        Holder = target;
        Ejectable = ejectable;
        TurretPointName = holder.GetOccupiedTurretPointName(Entity);

        SimSystem.GetComponent<UnitAIComponent>(Entity)?.SetTurretStance(cm);
        SimSystem.GetComponent<ObstructionComponent>(Entity)?.SetActive(false);
        UpdatePosition(cm);
        return true;
    }

    /// <summary>Port of LeaveTurret:找出生点 → 让出点位 → 落位 → UnitAI 复位 → 障碍恢复
    /// → 集结点 Walk。出生点语义同 Garrisonable.UnGarrison(固定偏移兜底,记录差异);
    /// 原版 Ungarrison 标记指令不移植(同 Garrison 的决定)。</summary>
    public bool LeaveTurret(ComponentManager cm, bool forced = false)
    {
        if (Holder is not { } holderId)
            return true;
        if (!Ejectable && !forced)
            return false;
        var holderPos = cm.QueryInterface<PositionComponent>(holderId);
        if (holderPos == null)
            return false;

        float radius = cm.QueryInterface<ObstructionComponent>(Entity)?.GetSize().ToFloat() ?? 1f;
        var spawn = GarrisonableComponent.FindSpawnOutside(cm, holderId, radius);

        var holder = cm.QueryInterface<TurretHolderComponent>(holderId);
        if (holder == null || !holder.LeaveTurretPoint(Entity, forced))
            return false;

        var pos = cm.QueryInterface<PositionComponent>(Entity);
        if (pos != null)
        {
            var old = new FixedVector2D(pos.Position.X, pos.Position.Z);
            pos.Position = spawn;
            SimSystem.NotifyPositionChanged(Entity, old, new FixedVector2D(spawn.X, spawn.Z));
        }

        var ai = SimSystem.GetComponent<UnitAIComponent>(Entity);
        ai?.ResetTurretStance(cm);
        SimSystem.GetComponent<ObstructionComponent>(Entity)?.SetActive(true);

        Holder = null;
        TurretPointName = "";
        Ejectable = true;   // 原版 delete this.ejectable(回默认);本移植显式复位

        var rally = cm.QueryInterface<RallyPointComponent>(holderId);
        if (ai != null && rally != null
            && (rally.Position.X != Fixed.Zero || rally.Position.Y != Fixed.Zero))
            ai.Walk(rally.Position, queued: false);
        return true;
    }

    /// <summary>位置跟拍(原版 SetTurretParent 的引擎侧联动):每回合由 SimBridge 调用,
    /// 把单位锁到持有者位置 + 按持有者朝向旋转的点位偏移,朝向同步。</summary>
    public void UpdatePosition(ComponentManager cm)
    {
        if (Holder is not { } holderId)
            return;
        var holderPos = cm.QueryInterface<PositionComponent>(holderId);
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        if (holderPos == null || pos == null)
            return;
        var point = cm.QueryInterface<TurretHolderComponent>(holderId)?.TurretPointByName(TurretPointName);
        float ox = point?.OffsetX ?? 0f, oz = point?.OffsetZ ?? 0f;

        float angle = holderPos.Rotation.Y.ToFloat();
        // 定点 sincos(炮塔位 = sim 位置;libm 三角跨平台低位不同 → 漂移 OOS)。
        Trig.SinCosApprox(Maths.Fixed.FromFloat(angle), out Maths.Fixed tSin, out Maths.Fixed tCos);
        float cos = tCos.ToFloat(), sin = tSin.ToFloat();
        var nx = holderPos.Position.X + Fixed.FromFloat(ox * cos + oz * sin);
        var nz = holderPos.Position.Z + Fixed.FromFloat(-ox * sin + oz * cos);
        if (nx == pos.Position.X && nz == pos.Position.Z
            && pos.Rotation.Y == holderPos.Rotation.Y)
            return;   // 无移动不打扰空间索引
        var old = new FixedVector2D(pos.Position.X, pos.Position.Z);
        pos.Position = new FixedVector3D(nx, pos.Position.Y, nz);
        pos.Rotation = new FixedVector3D(pos.Rotation.X, holderPos.Rotation.Y, pos.Rotation.Z);
        SimSystem.NotifyPositionChanged(Entity, old, new FixedVector2D(nx, nz));
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberU32("holder", Holder?.Value ?? 0);
        s.Bool("ejectable", Ejectable);
        s.StringASCII("turretPoint", TurretPointName);
    }

    public override void Deserialize(IDeserializer d)
    {
        uint h = d.NumberU32("holder");
        Holder = h != 0 ? new EntityId(h) : null;
        Ejectable = d.Bool("ejectable");
        TurretPointName = d.StringASCII("turretPoint");
    }

    public void HandleMessage(IMessage message) { }
}
