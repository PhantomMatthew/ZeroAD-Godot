using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>Guard — 原版 simulation/components/Guard.js 全量移植(97 行)。
/// 挂在**被护卫方**身上,记录"谁在护卫我"(原版 entities);UnitAI 的 _isGuardOf
/// 是反向"我护卫谁"。受击时转发给全部护卫(原版 MT_GuardedAttacked → 护卫反击);
/// 易主/外交变化时非互盟护卫被摘除(CheckGuards)。
/// 装配:template_unit/template_structure 基模板自带 &lt;Guard/&gt; —— EntityAssembler
/// 对单位/建筑无条件挂载(原版同款全体持有)。</summary>
[Component("Guard", "Guard")]
public sealed class GuardComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>护卫我的单位列表(原版 this.entities)。</summary>
    private readonly List<EntityId> _guards = new();
    public IReadOnlyList<EntityId> Entities => _guards;

    private ComponentManager? _subscribedCm;

    /// <summary>原版 OnOwnershipChanged(将死易主 to==-1 强制)+ OnDiplomacyChanged
    /// 的守卫清单维护:非互盟/被护方将死 → 摘除。</summary>
    protected override void OnInit()
    {
        var cm = SimSystem.Sim;
        if (cm == null || _subscribedCm != null) return;
        _subscribedCm = cm;
        cm.OwnerChanged += OnAnyOwnershipChanged;
        cm.Events.DiplomacyChanged += OnDiplomacyChanged;
        cm.Events.EntityRenamed += OnEntityRenamed;
    }

    protected override void OnDeinit()
    {
        if (_subscribedCm == null) return;
        _subscribedCm.OwnerChanged -= OnAnyOwnershipChanged;
        _subscribedCm.Events.DiplomacyChanged -= OnDiplomacyChanged;
        // EntityRenamed 不退订:原版 MT_EntityRenamed 在 DestroyEntity 之前广播
        // (helpers/Transform.js:180),本移植的 Promotion.Promote 先毁后广播——退订
        // 会让"先毁"场景丢掉改指(同 RallyPoint,见 ExtraComponents.cs 注释)。
        // 留存代价是已毁实体 Guard 的空表闭包(有界)。
        _subscribedCm = null;
    }

    private void OnAnyOwnershipChanged(EntityId entity, int from, int to)
    {
        var cm = _subscribedCm;
        if (cm == null || entity != Entity) return;
        CheckGuards(cm, force: to <= 0);   // 将死/无主 → 全摘
    }

    private void OnDiplomacyChanged(Events.DiplomacyChangedEvent e)
    {
        var cm = _subscribedCm;
        if (cm == null) return;
        int myOwner = cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;
        if (e.Player != myOwner && e.OtherPlayer != myOwner) return;
        CheckGuards(cm);
    }

    public void AddGuard(EntityId ent)
    {
        if (!_guards.Contains(ent)) _guards.Add(ent);
    }

    public void RemoveGuard(EntityId ent) => _guards.Remove(ent);

    /// <summary>原版 RenameGuard 的接线(原版调用点 = helpers/Transform.js:197:
    /// 护卫单位晋升/变身换号时,被护方的守卫清单旧号 → 新号)。</summary>
    private void OnEntityRenamed(Events.EntityRenamedEvent e) =>
        RenameGuard(e.OldEntity, e.NewEntity);

    /// <summary>原版 Guard.GetRange(entity):8 + footprint 派生——square 加
    /// √(w²+d²)×2/3,circle 加 radius×1.5;无 footprint 仅 8。定点实现(√ 走
    /// <see cref="MathInt.Sqrt64"/>)。注意取的是**护卫自身**的 footprint(原版
    /// AddGuard 传 this.entity),不是被护方的。</summary>
    public static Fixed GetRange(ComponentManager cm, EntityId entity)
    {
        var range = Fixed.FromInt(8);
        var fp = cm.QueryInterface<FootprintComponent>(entity);
        if (fp == null) return range;
        if (fp.Shape == FootprintShape.Square)
        {
            long w = fp.Size0.InternalValue, d = fp.Size1.InternalValue;
            var diag = Fixed.Zero.WithInternalValue((int)MathInt.Sqrt64((ulong)(w * w + d * d)));
            range += diag.Multiply(Fixed.FromFraction(2, 3));
        }
        else
        {
            range += fp.Size0.Multiply(Fixed.FromFraction(3, 2));
        }
        return range;
    }

    /// <summary>原版 RenameGuard:晋升/变身换实体号时改指。</summary>
    public void RenameGuard(EntityId oldEnt, EntityId newEnt)
    {
        int idx = _guards.IndexOf(oldEnt);
        if (idx != -1) _guards[idx] = newEnt;
    }

    /// <summary>原版 OnAttacked:受击转发给每个护卫(其 UnitAI 走 GuardedAttacked 响应:
    /// 反击/治疗/修理被护卫者)。</summary>
    public void NotifyAttacked(ComponentManager cm, EntityId attacker)
    {
        foreach (var ent in _guards.ToList())
            cm.QueryInterface<UnitAIComponent>(ent)?.OnGuardedAttacked(cm, Entity, attacker);
    }

    /// <summary>原版 CheckGuards:force(将死易主)或非互盟 → 摘卫(对方 UnitAI 若
    /// 正护卫我 → 停卫;否则仅从列表除名)。</summary>
    public void CheckGuards(ComponentManager cm, bool force = false)
    {
        foreach (var ent in _guards.ToList())
        {
            if (!force && IsMutualAllyOf(cm, ent)) continue;
            var ai = cm.QueryInterface<UnitAIComponent>(ent);
            if (ai != null && ai.IsGuardOf(Entity))
                ai.RemoveGuard(cm);
            else
                _guards.Remove(ent);
        }
    }

    /// <summary>互盟判定走 <see cref="DiplomacyComponent.IsMutualAllyOfEntity"/>
    /// (原版 IsOwnedByMutualAllyOfEntity)。</summary>
    private bool IsMutualAllyOf(ComponentManager cm, EntityId ent) =>
        DiplomacyComponent.IsMutualAllyOfEntity(cm, Entity, ent);

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("count", _guards.Count);
        foreach (var g in _guards) s.NumberU32("g", g.Value);
    }

    public override void Deserialize(IDeserializer d)
    {
        _guards.Clear();
        int n = d.NumberI32("count");
        for (int i = 0; i < n; i++) _guards.Add(new EntityId(d.NumberU32("g")));
    }

    public void HandleMessage(IMessage message) { }
}
