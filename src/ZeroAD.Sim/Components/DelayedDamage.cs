using System.Collections.Generic;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Events;

namespace ZeroAD.Sim;

// DelayedDamage — deferred damage settlement. Ported from
// binaries/data/mods/public/simulation/components/DelayedDamage.js (system component).
//
// In the original, this exists for ranged units: Attack schedules a projectile via Timer, and
// when it lands DelayedDamage.Hit applies the damage through AttackHelper (which consults
// Resistance, then Health). The C# P0 port uses a turn-count delay queue instead of a real
// projectile/travel model (that's M4 rendering territory). Melee uses delay=0 (settles same turn).
//
// Damage pipeline: AttackComponent.PerformAttack → DelayedDamage.ScheduleHit →
// (after delay turns) → consult Resistance → Health.TakeDamage. This keeps the Resistance
// indirection and the delay seam in place so ranged projectiles slot in later without
// rewiring combat.

/// <summary>System-level delayed-damage queue. Tick once per sim turn from the presentation layer.</summary>
public sealed class DelayedDamage
{
    private struct PendingHit
    {
        public int TriggerTurn;        // settle when current turn >= this
        public EntityId Attacker;
        public EntityId Target;
        public DamageBlock Damage;
    }

    private readonly List<PendingHit> _pending = new();
    private int _currentTurn;

    /// <summary>Advance the current turn counter. Call once per sim turn, before TickPending.</summary>
    public void AdvanceTurn() => _currentTurn++;

    /// <summary>Queue a damage event to settle after a number of turns (0 = same turn, next Tick).</summary>
    public static void ScheduleHit(ComponentManager cm, EntityId attacker, EntityId target,
        DamageBlock damage, int delayTurns)
    {
        var dd = cm.DelayedDamage;
        if (dd == null)
        {
            // No delay system wired (pure determinism tests): apply instantly through Resistance.
            ApplyDirect(cm, attacker, target, damage);
            return;
        }
        dd._pending.Add(new PendingHit
        {
            TriggerTurn = dd._currentTurn + delayTurns,
            Attacker = attacker,
            Target = target,
            Damage = damage
        });
    }

    /// <summary>Settle all hits whose delay has elapsed. Call once per sim turn.</summary>
    public void TickPending(ComponentManager cm)
    {
        // Iterate by index and compact in place; pending lists are short (combat is sparse).
        int write = 0;
        for (int read = 0; read < _pending.Count; read++)
        {
            if (_pending[read].TriggerTurn <= _currentTurn)
            {
                var hit = _pending[read];
                ApplyDirect(cm, hit.Attacker, hit.Target, hit.Damage);
            }
            else
            {
                _pending[write++] = _pending[read];
            }
        }
        _pending.RemoveRange(write, _pending.Count - write);
    }

    // Central settlement: apply Resistance → Health, then route the Capture channel.
    // Mirrors AttackHelper.HandleAttackEffects (invulnerability check, resistance reduction,
    // then receivers in registry order: Damage(order 1) → Capture(order 2)).
    private static void ApplyDirect(ComponentManager cm, EntityId attacker, EntityId target, DamageBlock raw)
    {
        var health = cm.QueryInterface<HealthComponent>(target);
        if (health != null && health.IsDead) return;

        var resistance = cm.QueryInterface<ResistanceComponent>(target);
        if (resistance != null && resistance.IsInvulnerable()) return;

        DamageBlock final;
        if (resistance != null)
        {
            final = raw.WithResistanceApplied(resistance.Resistances, resistance.CaptureResistance);
        }
        else
        {
            final = raw;
        }

        health?.TakeDamage(final);

        // 击杀归属：命中结算后若目标死亡，raise EntityKilledEvent。这是唯一同时知道
        // attacker 和 target 且能检测死亡的位置（镜像 Health.js:221 的 KilledEntity/LostEntity）。
        // StatisticsTracker 订阅此事件更新 enemyUnitsKilled / unitsLost / *Value 计数器。
        if (health != null && health.IsDead)
        {
            cm.Events.RaiseEntityKilled(new EntityKilledEvent { Victim = target, Killer = attacker });
            // 战利品收集(原版 Looter.js Collect,由 Health.js KilledEntity 触发):
            // 击杀者的 Looter 组件收走目标 Loot + 携带资源。
            cm.QueryInterface<Components.LooterComponent>(attacker)?.Collect(cm, target);
        }

        // 受击响应钩子(原版 MT_Attacked → UnitAI):物理伤害 >0 才触发;捕获通道
        // (Capture)不触发(对齐原版攻击效果接收序,捕获自身不引起反击)。
        if (final.TotalPhysical > 0)
            cm.QueryInterface<Components.UnitAIComponent>(target)?.OnAttacked(attacker, cm);

        // 捕获通道(对齐原版 g_AttackEffects 接收序:Damage 先结算,Capture 读扣血后 hp)。
        // GetTotalAttackEffects 的 hp 缩放:total /= 0.1 + 0.9×hp/maxHp(血越少越易占领);
        // 目标无 Health → 无缩放(原版 cmpHealth 缺失分支)。
        Maths.Fixed captureDealt = Maths.Fixed.Zero;
        if (final.Capture > Maths.Fixed.Zero)
        {
            var capturable = cm.QueryInterface<CapturableComponent>(target);
            var attackerOwn = cm.QueryInterface<OwnershipComponent>(attacker);
            if (capturable != null && attackerOwn != null && attackerOwn.PlayerId >= 0)
            {
                Maths.Fixed scale = health != null && health.Max > 0
                    ? Maths.Fixed.FromFloat(0.1f) + Maths.Fixed.FromFloat(0.9f) * health.Current / health.Max
                    : Maths.Fixed.FromInt(1);
                captureDealt = capturable.Capture(cm, final.Capture / scale, attacker, attackerOwn.PlayerId);
            }
        }

        // Award XP to the attacker's Promotion component if it has one.
        var promotion = cm.QueryInterface<PromotionComponent>(attacker);
        if (promotion != null && final.TotalPhysical > 0)
            promotion.AddXP(final.TotalPhysical);

        // Notify the sim event bus so the presentation layer can play hit feedback.
        cm.Events.RaiseAttackLanded(new AttackLandedEvent
        {
            Target = target,
            Attacker = attacker,
            DamageDealt = final.TotalPhysical,
            CaptureDealt = captureDealt.ToFloat(),
        });
    }

    /// <summary>Number of hits still queued (for debugging/testing).</summary>
    public int PendingCount => _pending.Count;
}
