using System;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

[Component("Health", "Health")]
public sealed class HealthComponent : ComponentBase, IComponentMessageHandler
{
    public int Current;
    public int Max;

    protected override void OnInit()
    {
        Current = 100;
        Max = 100;
    }

    public float HealthFraction => Max > 0 ? (float)Current / Max : 0f;

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
    }

    public override void Deserialize(IDeserializer d)
    {
        Current = d.NumberI32("cur");
        Max = d.NumberI32("max");
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
    /// COMBAT.ATTACKING state on each attack cycle.</summary>
    public void PerformAttack(ComponentManager cm)
    {
        if (Target == null) return;
        DelayedDamage.ScheduleHit(cm, Entity, Target.Value, Damage, delayTurns: 0);
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
    }

    public override void Deserialize(IDeserializer d)
    {
        Damage = DamageBlock.Deserialize(d, "dmg");
        Range = d.NumberFixed("range").ToFloat();
        Rate = d.NumberFixed("rate").ToFloat();
        State = (AttackState)d.NumberI32("state");
        uint tid = d.NumberU32("target");
        Target = tid != 0 ? new EntityId(tid) : null;
    }

    public void HandleMessage(IMessage message) { }
}
