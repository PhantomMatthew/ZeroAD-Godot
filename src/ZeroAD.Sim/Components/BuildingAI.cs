using System.Collections.Generic;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>建筑自动防御(移植原版 BuildingAI.js 核心):范围内出现敌军 → 按攻击速率齐射。
/// 箭数 = DefaultArrowCount + 驻军中弓手类别数 × GarrisonArrowMultiplier(上限 MaxArrowCount)。
/// 目标选择(原版 preference 排序已移植):focusTargets(玩家 focus-fire 队列)非空即只打它们;
/// 否则 unitAITarget 并入候选后按 attack preference + 距离排序;unitAITarget 已在射程表内
/// → 升格为唯一 focusTarget(原版 FireArrows 的 else-if 升格分支)。手动集火两路并存:
/// SetUnitAITarget(右键敌目标立即集火)/AddFocusTarget(focus-fire 命令,目标须在射程内才接受)。
/// 翻面清理(懒订阅事件,见 WireClearEvents):易主清 focusTargets+unitAITarget;外交翻面只清
/// unitAITarget(上游 OnOwnershipChanged/OnDiplomacyChanged 仅重置射程目标表,本移植按 WS3
/// 裁定加码清集火,记录在案)。逐箭校验射程 + LOS(原版 CheckTargetVisible +
/// IsInTargetParabolicRange),打不中顺延下一目标。
/// 结算走 AttackComponent.PerformAttack(修正值管线/投射物事件/伤害与单位同路)。</summary>
[Component("BuildingAI", "BuildingAI")]
public sealed class BuildingAIComponent : ComponentBase, IComponentMessageHandler
{
    // 模板参数(装配时灌入,序列化)。
    public int DefaultArrowCount = 1;
    public int MaxArrowCount;               // 0 = 不限(原版 Infinity)
    public float GarrisonArrowMultiplier = 1f;
    public string GarrisonArrowClasses = "";

    private const float ScanInterval = 1.0f;   // 原版 range query 事件 → 1s 节流轮询
    private float _scanElapsed;
    private float _cooldown;
    private readonly List<EntityId> _targets = new();

    // ── 手动集火(原版 unitAITarget/focusTargets)──
    /// <summary>玩家点名的集火目标(原版 unitAITarget;0 = 无)。在射程外也记住,
    /// 进射程即打(原版 addTarget 把它并入目标表)。</summary>
    public EntityId UnitAITarget;
    /// <summary>集火队列(原版 focusTargets;Shift 追加/覆盖语义在执行器)。</summary>
    public readonly List<EntityId> FocusTargets = new();

    /// <summary>原版 SetUnitAITarget:玩家对建筑下攻击令 → 记集火目标并立即参与齐射。</summary>
    public void SetUnitAITarget(EntityId target) => UnitAITarget = target;

    /// <summary>原版 AddFocusTarget(BuildingAI.js:276-286):目标须在射程表(_targets)内
    /// 才接受,否则忽略;queued 追加尾 / pushFront 头插 / 否则覆盖为单目标。</summary>
    public void AddFocusTarget(EntityId target, bool queued, bool pushFront = false)
    {
        if (target == default || !_targets.Contains(target)) return;
        if (queued) { if (!FocusTargets.Contains(target)) FocusTargets.Add(target); }
        else if (pushFront) { FocusTargets.Remove(target); FocusTargets.Insert(0, target); }
        else { FocusTargets.Clear(); FocusTargets.Add(target); }
    }

    /// <summary>原版 OnOwnershipChanged/OnDiplomacyChanged:翻面即清集火。</summary>
    public void ClearFocusTargets()
    {
        UnitAITarget = default;
        FocusTargets.Clear();
    }

    // ── 翻面清集火(懒订阅,首个 Tick;OnInit 期 SimSystem.Sim 可能未就位。
    // 模式同 GarrisonHolder.WireEvictionEvents)──
    private ComponentManager? _subscribedCm;

    private void WireClearEvents(ComponentManager cm)
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

    /// <summary>本建筑易主 → 清集火队列 + 清 UnitAITarget(WS3 裁定;上游只重置
    /// 射程目标表 targetUnits,本移植的目标表每秒重建,无需清)。</summary>
    private void OnAnyOwnershipChanged(EntityId entity, int from, int to)
    {
        if (entity != Entity) return;
        ClearFocusTargets();
    }

    /// <summary>外交翻面涉及本建筑属主 → 只清 UnitAITarget(不清 focusTargets,WS3 裁定)。</summary>
    private void OnDiplomacyChanged(Events.DiplomacyChangedEvent e)
    {
        var cm = _subscribedCm;
        if (cm == null) return;
        int myOwner = cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;
        if (e.Player != myOwner && e.OtherPlayer != myOwner) return;
        UnitAITarget = default;
    }

    protected override void OnInit() { }

    /// <summary>当前箭数(驻军变化实时算,原版 GetArrowCount)。</summary>
    public int GetArrowCount(ComponentManager cm)
    {
        int archers = 0;
        var holder = cm.QueryInterface<GarrisonHolderComponent>(Entity);
        if (holder != null && GarrisonArrowClasses.Length > 0)
        {
            foreach (var gid in holder.Entities)
            {
                var id = cm.QueryInterface<IdentityComponent>(gid);
                if (id != null && id.MatchesClassList(GarrisonArrowClasses)) archers++;
            }
        }
        int count = DefaultArrowCount + (int)System.MathF.Round(archers * GarrisonArrowMultiplier);
        return MaxArrowCount > 0 ? System.Math.Min(count, MaxArrowCount) : count;
    }

    public void Tick(float dt, ComponentManager cm)
    {
        WireClearEvents(cm);
        var attack = cm.QueryInterface<AttackComponent>(Entity);
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        if (attack == null || own == null) return;
        // 未完工地基不放箭(原版:建筑完工才有防御)。
        if (cm.QueryInterface<FoundationComponent>(Entity) is { IsBuilt: false }) return;

        _cooldown -= dt;
        _scanElapsed += dt;
        if (_scanElapsed >= ScanInterval)
        {
            _scanElapsed = 0;
            RefreshTargets(cm, attack, own.PlayerId);
        }
        // 上游 FireArrows:targetUnits 空但 focusTargets/unitAITarget 在场仍走齐射判定
        // (手动集火目标可能暂未进射程表;原版 `if (!targetUnits.length && !unitAITarget) return`)。
        if ((_targets.Count == 0 && FocusTargets.Count == 0 && UnitAITarget == default)
            || _cooldown > 0f) return;

        // 原版 FireArrows 升格分支(BuildingAI.js:343-344):unitAITarget 已在射程表内
        // → 升格为唯一 focusTarget。
        if (UnitAITarget != default && _targets.Contains(UnitAITarget))
        {
            FocusTargets.Clear();
            FocusTargets.Add(UnitAITarget);
        }

        // 原版 FireArrows 目标序:focusTargets 优先(玩家集火);unitAITarget 在表外
        // 则并入;否则按 (preference ?? 49) 升序 → 距离升序 逐箭分派。
        int arrows = GetArrowCount(cm);
        var alive = new List<EntityId>();
        if (FocusTargets.Count > 0)
        {
            // 集火队列(原版:focusTargets 非空即只打它们)。
            foreach (var t in FocusTargets)
                if (cm.QueryInterface<HealthComponent>(t) is { IsDead: false }) alive.Add(t);
            if (UnitAITarget != default && !alive.Contains(UnitAITarget)
                && cm.QueryInterface<HealthComponent>(UnitAITarget) is { IsDead: false })
                alive.Add(UnitAITarget);
        }
        else
        {
            foreach (var t in _targets)
                if (cm.QueryInterface<HealthComponent>(t) is { IsDead: false }) alive.Add(t);
            if (UnitAITarget != default && !alive.Contains(UnitAITarget)
                && cm.QueryInterface<HealthComponent>(UnitAITarget) is { IsDead: false })
                alive.Add(UnitAITarget);
            // preference + 距离排序(原版 targets.sort 同款;无偏好 49 垫底)。
            var pos = cm.QueryInterface<PositionComponent>(Entity);
            float px = pos?.Position.X.ToFloat() ?? 0f, pz = pos?.Position.Z.ToFloat() ?? 0f;
            alive.Sort((a, b) =>
            {
                int pa = attack.GetPreference(cm, a) ?? 49;
                int pb = attack.GetPreference(cm, b) ?? 49;
                if (pa != pb) return pa.CompareTo(pb);
                var ppa = cm.QueryInterface<PositionComponent>(a);
                var ppb = cm.QueryInterface<PositionComponent>(b);
                float da = ppa != null
                    ? (ppa.Position.X.ToFloat() - px) * (ppa.Position.X.ToFloat() - px)
                      + (ppa.Position.Z.ToFloat() - pz) * (ppa.Position.Z.ToFloat() - pz)
                    : float.MaxValue;
                float db = ppb != null
                    ? (ppb.Position.X.ToFloat() - px) * (ppb.Position.X.ToFloat() - px)
                      + (ppb.Position.Z.ToFloat() - pz) * (ppb.Position.Z.ToFloat() - pz)
                    : float.MaxValue;
                return da.CompareTo(db);
            });
        }
        if (alive.Count == 0) { _cooldown = 1f / attack.Rate; return; }
        // 逐箭校验(原版 FireArrows 的 CheckTargetVisible + IsInTargetParabolicRange:
        // range query 是近似量程,逐箭复核射程与 LOS;打不中顺延下一目标)。
        // LOS 查询频率:每次齐射每候选一次(齐射间隔 = 1/Rate 秒),开销可忽略。
        var valid = new List<EntityId>(alive.Count);
        foreach (var t in alive)
            if (CheckTargetVisible(cm, t, own.PlayerId) && IsInArrowRange(cm, attack, t))
                valid.Add(t);
        if (valid.Count == 0) { _cooldown = 1f / attack.Rate; return; }
        for (int i = 0; i < arrows; i++)
        {
            attack.Target = valid[i % valid.Count];
            attack.CurrentAttackIsCapture = false;
            attack.PerformAttack(cm);
        }
        attack.Target = null;
        _cooldown = 1f / attack.Rate;   // 原版 RepeatTime 一个周期
    }

    /// <summary>原版 CheckTargetVisible(BuildingAI.js:401-415):mirage 替身视为可见;
    /// 否则 LOS 非 Hidden。无 RangeManager(裸测试环境)→ 不拦。</summary>
    private bool CheckTargetVisible(ComponentManager cm, EntityId target, int myPlayer)
    {
        var range = SimSystem.Range;
        if (range == null) return true;
        var fogging = cm.QueryInterface<FoggingComponent>(target);
        if (fogging != null && fogging.IsMiraged(myPlayer)) return true;
        return range.GetLosVisibility(target, myPlayer) != LosVisibility.Hidden;
    }

    /// <summary>原版 IsInTargetParabolicRange(BuildingAI.js:369-384)的平面近似:
    /// 中心距(edge-to-edge,减目标障碍半径)≤ 射程。定点长整型比较(跨平台确定性),
    /// 无位置件/不在世界的目标不可打。</summary>
    private bool IsInArrowRange(ComponentManager cm, AttackComponent attack, EntityId target)
    {
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        var tpos = cm.QueryInterface<PositionComponent>(target);
        if (pos == null || tpos == null || !tpos.InWorld) return false;
        var dx = tpos.Position.X - pos.Position.X;
        var dz = tpos.Position.Z - pos.Position.Z;
        long d2 = (long)dx.InternalValue * dx.InternalValue
                + (long)dz.InternalValue * dz.InternalValue;
        var eff = Fixed.FromFloat(attack.Range);
        var obs = cm.QueryInterface<ObstructionComponent>(target);
        if (obs != null)
            eff += obs.GetSize();
        long r2 = (long)eff.InternalValue * eff.InternalValue;
        return d2 <= r2;
    }

    /// <summary>范围内可见敌军(1s 节流刷新;原版 OnRangeUpdate 维护 targetUnits)。</summary>
    private void RefreshTargets(ComponentManager cm, AttackComponent attack, int myPlayer)
    {
        _targets.Clear();
        var range = SimSystem.Range;
        if (range == null) return;
        var found = range.ExecuteQuery(Entity, Fixed.Zero, Fixed.FromFloat(attack.Range), e =>
        {
            var eo = cm.QueryInterface<OwnershipComponent>(e);
            if (eo == null || eo.PlayerId <= 0) return false;               // 不打野(原版不射 gaia)
            if (!cm.Players.IsEnemy(myPlayer, eo.PlayerId)) return false;
            if (cm.QueryInterface<UnitMotion>(e) == null) return false;      // 只打移动单位(原版目标是单位)
            if (range.GetLosVisibility(e, myPlayer) == LosVisibility.Hidden) return false;
            return cm.QueryInterface<HealthComponent>(e) is { IsDead: false };
        });
        // 近者优先(原版 proximity 次序)。
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        if (pos != null)
        {
            float px = pos.Position.X.ToFloat(), pz = pos.Position.Z.ToFloat();
            found.Sort((a, b) =>
            {
                var pa = cm.QueryInterface<PositionComponent>(a);
                var pb = cm.QueryInterface<PositionComponent>(b);
                if (pa == null || pb == null) return 0;
                float da = (pa.Position.X.ToFloat() - px) * (pa.Position.X.ToFloat() - px)
                         + (pa.Position.Z.ToFloat() - pz) * (pa.Position.Z.ToFloat() - pz);
                float db = (pb.Position.X.ToFloat() - px) * (pb.Position.X.ToFloat() - px)
                         + (pb.Position.Z.ToFloat() - pz) * (pb.Position.Z.ToFloat() - pz);
                return da.CompareTo(db);
            });
        }
        _targets.AddRange(found);
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("defArrows", DefaultArrowCount);
        s.NumberI32("maxArrows", MaxArrowCount);
        s.NumberFixed("garrMult", Fixed.FromFloat(GarrisonArrowMultiplier));
        s.StringASCII("garrCls", GarrisonArrowClasses);
        s.NumberFixed("scan", Fixed.FromFloat(_scanElapsed));
        s.NumberFixed("cooldown", Fixed.FromFloat(_cooldown));
        s.NumberU32("unitAITarget", UnitAITarget.Value);
        s.NumberI32("focus", FocusTargets.Count);
        foreach (var t in FocusTargets) s.NumberU32("f", t.Value);
    }

    public override void Deserialize(IDeserializer d)
    {
        DefaultArrowCount = d.NumberI32("defArrows");
        MaxArrowCount = d.NumberI32("maxArrows");
        GarrisonArrowMultiplier = d.NumberFixed("garrMult").ToFloat();
        GarrisonArrowClasses = d.StringASCII("garrCls");
        _scanElapsed = d.NumberFixed("scan").ToFloat();
        _cooldown = d.NumberFixed("cooldown").ToFloat();
        UnitAITarget = new EntityId(d.NumberU32("unitAITarget"));
        int focus = d.NumberI32("focus");
        for (int i = 0; i < focus; i++)
            FocusTargets.Add(new EntityId(d.NumberU32("f")));
    }

    public void HandleMessage(IMessage message) { }
}
