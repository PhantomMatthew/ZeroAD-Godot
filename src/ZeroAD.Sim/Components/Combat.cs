using System;
using System.Linq;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

[Component("Health", "Health")]
public sealed class HealthComponent : ComponentBase, IComponentMessageHandler
{
    // 默认值活在字段初始化器上(不覆写 OnInit):`new HealthComponent { Current = 50 }` 的
    // 调用方在 AddComponent 后保值——此前 OnInit 无条件重置 100/100,静默吞掉所有
    // 指定值(EntityAssembler 的模板 HP 就中招)。同 OwnershipComponent 修复模式。
    public int Current = 100;
    public int Max = 100;
    /// <summary>模板 Health/Unhealable(原版 Heal.js CanHeal 检查):不可被治疗,只能修理。</summary>
    public bool Unhealable;
    /// <summary>模板基值(修正值管线的输入)。0 = 未显式设置,回退用 Max
    /// (既有创建点只管 Max,语义等价)。科技改变 Max 时由
    /// <see cref="ValueModificationApplier.RescaleHealth"/> 按比例缩放 Current。</summary>
    public int BaseMax;

    /// <summary>修正值查询用的基值:BaseMax > 0 优先,否则 Max。</summary>
    public int BaseMaxOrMax => BaseMax > 0 ? BaseMax : Max;

    protected override void OnInit() { }

    public float HealthFraction => Max > 0 ? (float)Current / Max : 0f;

    /// <summary>原版 Health.js IsInjured:hp &lt; maxHp(Heal 的目标校验 + 补满即停判定)。</summary>
    public bool IsInjured => Current < Max;

    /// <summary>Apply a post-resistance damage block directly to health. This is the sink at the
    /// end of the Attack → DelayedDamage → Resistance → Health pipeline. Capture is handled
    /// separately (Capturable component) and ignored here.</summary>
    public void TakeDamage(DamageBlock damage)
    {
        Current = Math.Max(0, Current - damage.TotalPhysical);
    }

    /// <summary>Apply a flat amount of physical damage (post-resistance). Kept for back-compat
    /// with code paths that already computed the reduced value (e.g. tutorial scripting).</summary>
    public void TakeDamage(int amount)
    {
        Current = Math.Max(0, Current - amount);
    }

    public void Heal(int amount)
    {
        Current = Math.Min(Max, Current + amount);
    }

    public bool IsDead => Current <= 0;

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("cur", Current);
        s.NumberI32("max", Max);
        s.NumberI32("bmax", BaseMax);
        s.Bool("unhealable", Unhealable);
    }

    public override void Deserialize(IDeserializer d)
    {
        Current = d.NumberI32("cur");
        Max = d.NumberI32("max");
        BaseMax = d.NumberI32("bmax");
        Unhealable = d.Bool("unhealable");
    }

    public void HandleMessage(IMessage message) { }
}

[Component("Attack", "Attack")]
public sealed class AttackComponent : ComponentBase, IComponentMessageHandler
{
    // Per-type raw damage (pre-resistance). Populated from the template's Attack/Melee/Damage node.
    public DamageBlock Damage = new();
    // 默认值活在字段初始化器(OnInit 不覆写,同 HealthComponent 修复模式):
    // `new AttackComponent { Range = ... }` 调用方在 AddComponent 后保值。
    public float Range = 3.0f;
    public float Rate = 1.0f;
    public float Cooldown;
    public EntityId? Target;
    public AttackState State;
    /// <summary>远程单位 = true,决定修正值路径前缀(Attack/Ranged vs Attack/Melee)。
    /// 装配时由模板 Attack/Ranged 节点存在性推导(TemplateStats.AttackIsRanged)。</summary>
    public bool IsRanged;

    // --- Capture 攻击类型(原版 Attack/Capture 顶层元素;CaptureStrength=0 = 无此类型) ---
    /// <summary>模板 Attack/Capture/Capture 值(小数:步兵 2.5/骑兵 1.75)。</summary>
    public Maths.Fixed CaptureStrength;
    public float CaptureRange = 4f;      // 模板 Attack/Capture/MaxRange
    public float CaptureRate = 1f;       // 模板 Attack/Capture/RepeatTime → 1000/ms
    /// <summary>模板 Attack/Capture/RestrictedClasses token 串(不可捕获的类别,
    /// 如 "Field Palisade Wall")。</summary>
    public string CaptureRestrictedClasses = "";
    /// <summary>物理类型(Melee|Ranged)RestrictedClasses token 串(原版逐型 CanAttack
    /// 门:命中即不可用此型攻击,如冲车 "Field Organic"、猎犬 "Structure Ship Siege")。</summary>
    public string PhysicalRestrictedClasses = "";
    /// <summary>物理类型(Melee|Ranged)PreferredClasses token 串
    /// (GetBestAttackAgainst 偏好 +2 判定,如 "Unit+!Ship")。</summary>
    public string PreferredClasses = "";
    /// <summary>当前目标使用的攻击类型(true=Capture 型)。原版存 order.data.attackType
    /// (Order.Attack 时 GetBestAttackAgainst 选一次);我们挂组件,AttackTarget 重选,等价。</summary>
    public bool CurrentAttackIsCapture;

    public enum AttackState { Idle, Approaching, Attacking }

    /// <summary>攻击类型选择结果(原版 GetBestAttackAgainst 返回类型字符串;
    /// 我们只有物理(Melee|Ranged 合一)+ Capture 两型)。</summary>
    public enum AttackChoice { Physical, Capture }

    protected override void OnInit() { }

    /// <summary>选类型 + 锁定目标(对齐原版 Order.Attack:GetBestAttackAgainst 选不到
    /// (两门皆关)→ 拒单)。返回 false = 无任何可用攻击类型,调用方 FinishOrder。</summary>
    public bool AttackTarget(ComponentManager cm, EntityId target, bool allowCapture = false)
    {
        var choice = GetBestAttackAgainst(cm, target, allowCapture);
        if (choice == null) return false;
        CurrentAttackIsCapture = choice == AttackChoice.Capture;
        Target = target;
        State = AttackState.Approaching;
        return true;
    }

    /// <summary>Stop attacking and clear the current target. Called by UnitAI when an order
    /// finishes or the target is lost.</summary>
    public void StopAttacking()
    {
        Target = null;
        State = AttackState.Idle;
        Cooldown = 0;
    }

    /// <summary>对齐原版 Attack.GetBestAttackAgainst:过滤 CanAttack 的类型按偏好公式
    /// 取最大(原版 sort 升序 .pop):pref = PreferredClasses 命中(+2,仅物理型有)
    /// + 指令偏好(+1,allowCapture ? 捕获型 : 物理型);得分 = 类型序号 +
    /// (pref&gt;0 ? pref + 类型数 : 0);类型序号 Physical=0/Capture=1(原版 g_AttackTypes
    /// 中 Capture 恒排最后),平手归 Capture(升序尾部)。选不到 → null。</summary>
    public AttackChoice? GetBestAttackAgainst(ComponentManager cm, EntityId target, bool allowCapture)
    {
        bool physicalOk = CanAttackPhysical(cm, target);
        bool captureOk = CaptureStrength > Maths.Fixed.Zero && CanAttackCapture(cm, target);
        if (!physicalOk && !captureOk) return null;
        if (!physicalOk) return AttackChoice.Capture;
        if (!captureOk) return AttackChoice.Physical;

        var identity = cm.QueryInterface<IdentityComponent>(target);
        bool prefMatch = identity != null
            && Content.EntityClassHelper.MatchesClassList(identity.Classes, PreferredClasses);
        int types = 2;
        int prefP = (prefMatch ? 2 : 0) + (allowCapture ? 0 : 1);
        int prefC = allowCapture ? 1 : 0;
        int scoreP = 0 + (prefP > 0 ? prefP + types : 0);
        int scoreC = 1 + (prefC > 0 ? prefC + types : 0);
        return scoreC >= scoreP ? AttackChoice.Capture : AttackChoice.Physical;
    }

    /// <summary>原版 CanAttack 的非捕获型分支:外交敌对 + 目标有 Health 且 hp&gt;0
    /// + RestrictedClasses 不命中 + 本组件确有物理伤害(原版该型不存在即跳过;
    /// 我们的 AttackComponent 恒代表物理型,零伤害=型不存在)。
    /// 记录在案:无 OwnershipComponent 的目标不拦(P0 旧语义,gaia 资源可打);
    /// 高度差检查未移植(无 HeightOffset)。</summary>
    private bool CanAttackPhysical(ComponentManager cm, EntityId target)
    {
        if (Damage.TotalPhysical <= 0) return false;
        var health = cm.QueryInterface<HealthComponent>(target);
        if (health == null || health.Current <= 0) return false;
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        var targetOwn = cm.QueryInterface<OwnershipComponent>(target);
        if (own != null && targetOwn != null
            && !cm.Players.IsEnemy(own.PlayerId, targetOwn.PlayerId))
            return false;
        if (PhysicalRestrictedClasses.Length > 0)
        {
            var identity = cm.QueryInterface<IdentityComponent>(target);
            if (identity != null
                && Content.EntityClassHelper.MatchesClassList(identity.Classes, PhysicalRestrictedClasses))
                return false;
        }
        return true;
    }

    /// <summary>原版 CanAttack 的捕获型分支:目标可占领且 Capturable.CanCapture(我方)
    /// + RestrictedClasses 不命中。</summary>
    private bool CanAttackCapture(ComponentManager cm, EntityId target)
    {
        var capturable = cm.QueryInterface<CapturableComponent>(target);
        if (capturable == null) return false;
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        if (own == null || own.PlayerId < 0) return false;
        if (!capturable.CanCapture(cm, own.PlayerId)) return false;
        if (CaptureRestrictedClasses.Length > 0)
        {
            var identity = cm.QueryInterface<IdentityComponent>(target);
            if (identity != null
                && Content.EntityClassHelper.MatchesClassList(identity.Classes, CaptureRestrictedClasses))
                return false;
        }
        return true;
    }

    /// <summary>Perform one attack hit against the current target. Routes through DelayedDamage
    /// so resistance is applied and (for ranged) travel latency is honoured. Called by UnitAI's
    /// COMBAT.ATTACKING state on each attack cycle. Damage passes the modifier pipeline here
    /// (tech effects on Attack/{Melee|Ranged}/Damage/{type}), so research applies at hit time.
    /// 物理/捕获互斥(对齐原版:一次命中只用一种攻击类型,Capture 模板无 Damage 元素)。</summary>
    public void PerformAttack(ComponentManager cm)
    {
        if (Target == null) return;
        if (CurrentAttackIsCapture)
        {
            // 修正值路径对齐原版 GetAttackEffectsData("Attack/Capture"):Attack/Capture/Capture。
            float cap = cm.Modifiers.Apply("Attack/Capture/Capture", CaptureStrength.ToFloat(), Entity);
            DelayedDamage.ScheduleHit(cm, Entity, Target.Value,
                new DamageBlock { Capture = Maths.Fixed.FromFloat(cap) }, delayTurns: 0);
            Cooldown = 1.0f / CaptureRate;
            return;
        }
        string prefix = IsRanged ? "Attack/Ranged/Damage/" : "Attack/Melee/Damage/";
        var mod = new DamageBlock();
        foreach (var kv in Damage.Amounts.OrderBy(k => (int)k.Key)) // 排序保确定
            mod.Amounts[kv.Key] = (int)MathF.Round(
                cm.Modifiers.Apply(prefix + kv.Key, kv.Value, Entity), MidpointRounding.AwayFromZero);
        DelayedDamage.ScheduleHit(cm, Entity, Target.Value, mod, delayTurns: 0);
        Cooldown = 1.0f / Rate;
    }

    public void Tick(float dt, ComponentManager cm)
    {
        if (Target == null) return;
        if (Cooldown > 0) Cooldown -= dt;

        var targetHealth = cm.QueryInterface<HealthComponent>(Target.Value);
        if (targetHealth == null || targetHealth.IsDead)
        {
            StopAttacking();
            return;
        }

        // 翻面/外交变化重门(原版 OnOwnershipChanged/OnDiplomacyChanged 触发 UnitAI
        // 重评;我们 Tick 轮询等价):捕获型目标已不再可捕获(翻面完成/CP 抽干)→ 收工;
        // 物理型目标变非敌(被我方占领/外交变化)→ 收工,不再打自己的建筑。
        if (CurrentAttackIsCapture)
        {
            var capturable = cm.QueryInterface<CapturableComponent>(Target.Value);
            var myOwn = cm.QueryInterface<OwnershipComponent>(Entity);
            if (capturable == null || myOwn == null || !capturable.CanCapture(cm, myOwn.PlayerId))
            {
                StopAttacking();
                return;
            }
        }
        else if (!CanAttackPhysical(cm, Target.Value))
        {
            StopAttacking();
            return;
        }

        var myPos = cm.QueryInterface<PositionComponent>(Entity);
        var targetPos = cm.QueryInterface<PositionComponent>(Target.Value);
        if (myPos == null || targetPos == null) return;

        float dx = targetPos.Position.X.ToFloat() - myPos.Position.X.ToFloat();
        float dz = targetPos.Position.Z.ToFloat() - myPos.Position.Z.ToFloat();
        float dist = MathF.Sqrt(dx * dx + dz * dz);

        var motion = cm.QueryInterface<UnitMotion>(Entity);
        float range = CurrentAttackIsCapture ? CaptureRange : Range;

        if (dist > range)
        {
            State = AttackState.Approaching;
            if (motion != null && !motion.HasMoveTarget)
            {
                motion.MoveToPoint(new Maths.FixedVector2D(
                    targetPos.Position.X, targetPos.Position.Z));
            }
        }
        else
        {
            State = AttackState.Attacking;
            if (motion != null) motion.Stop();

            if (Cooldown <= 0)
                PerformAttack(cm);
        }
    }

    public override void Serialize(ISerializer s)
    {
        Damage.Serialize(s, "dmg");
        s.NumberFixed("range", Maths.Fixed.FromFloat(Range));
        s.NumberFixed("rate", Maths.Fixed.FromFloat(Rate));
        s.NumberI32("state", (int)State);
        s.NumberU32("target", Target?.Value ?? 0);
        s.Bool("ranged", IsRanged);
        // Capture 攻击类型(本存档周期追加,读序须与写序逐位一致)。
        s.NumberFixed("capstr", CaptureStrength);
        s.NumberFixed("caprange", Maths.Fixed.FromFloat(CaptureRange));
        s.NumberFixed("caprate", Maths.Fixed.FromFloat(CaptureRate));
        s.StringASCII("caprestr", CaptureRestrictedClasses);
        s.StringASCII("prefcls", PreferredClasses);
        s.Bool("curcap", CurrentAttackIsCapture);
        s.StringASCII("physrestr", PhysicalRestrictedClasses);
    }

    public override void Deserialize(IDeserializer d)
    {
        Damage = DamageBlock.Deserialize(d, "dmg");
        Range = d.NumberFixed("range").ToFloat();
        Rate = d.NumberFixed("rate").ToFloat();
        State = (AttackState)d.NumberI32("state");
        uint tid = d.NumberU32("target");
        Target = tid != 0 ? new EntityId(tid) : null;
        IsRanged = d.Bool("ranged");
        CaptureStrength = d.NumberFixed("capstr");
        CaptureRange = d.NumberFixed("caprange").ToFloat();
        CaptureRate = d.NumberFixed("caprate").ToFloat();
        CaptureRestrictedClasses = d.StringASCII("caprestr");
        PreferredClasses = d.StringASCII("prefcls");
        CurrentAttackIsCapture = d.Bool("curcap");
        PhysicalRestrictedClasses = d.StringASCII("physrestr");
    }

    public void HandleMessage(IMessage message) { }
}
