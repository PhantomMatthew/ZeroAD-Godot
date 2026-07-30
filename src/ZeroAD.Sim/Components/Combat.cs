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
    public float Range;
    public float Rate;
    public float Cooldown;
    public EntityId? Target;
    public AttackState State;
    /// <summary>远程单位 = true,决定修正值路径前缀(Attack/Ranged vs Attack/Melee)。
    /// 装配时由模板 Attack/Ranged 节点存在性推导(TemplateStats.AttackIsRanged)。</summary>
    public bool IsRanged;

    public enum AttackState { Idle, Approaching, Attacking }

    protected override void OnInit()
    {
        // Defaults live on the field initializer so callers using
        // `new AttackComponent { Damage = ..., Range = ... }` keep their values.
        Range = 3.0f;
        Rate = 1.0f;
        Cooldown = 0;
        State = AttackState.Idle;
    }

    public void AttackTarget(EntityId targetEntity)
    {
        Target = targetEntity;
        State = AttackState.Approaching;
    }

    /// <summary>Stop attacking and clear the current target. Called by UnitAI when an order
    /// finishes or the target is lost.</summary>
    public void StopAttacking()
    {
        Target = null;
        State = AttackState.Idle;
        Cooldown = 0;
    }

    /// <summary>Perform one attack hit against the current target. Routes through DelayedDamage
    /// so resistance is applied and (for ranged) travel latency is honoured. Called by UnitAI's
    /// COMBAT.ATTACKING state on each attack cycle. Damage passes the modifier pipeline here
    /// (tech effects on Attack/{Melee|Ranged}/Damage/{type}), so research applies at hit time.</summary>
    public void PerformAttack(ComponentManager cm)
    {
        if (Target == null) return;
        string prefix = IsRanged ? "Attack/Ranged/Damage/" : "Attack/Melee/Damage/";
        var mod = new DamageBlock { Capture = Damage.Capture };
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

        var myPos = cm.QueryInterface<PositionComponent>(Entity);
        var targetPos = cm.QueryInterface<PositionComponent>(Target.Value);
        if (myPos == null || targetPos == null) return;

        float dx = targetPos.Position.X.ToFloat() - myPos.Position.X.ToFloat();
        float dz = targetPos.Position.Z.ToFloat() - myPos.Position.Z.ToFloat();
        float dist = MathF.Sqrt(dx * dx + dz * dz);

        var motion = cm.QueryInterface<UnitMotion>(Entity);

        if (dist > Range)
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
    }

    public void HandleMessage(IMessage message) { }
}
