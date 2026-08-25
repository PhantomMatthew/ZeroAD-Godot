using System;
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
        public Components.StatusEffectSpec? Status;   // 攻击附带状态(原版 ApplyStatus)
    }

    private readonly List<PendingHit> _pending = new();
    private int _currentTurn;

    /// <summary>Advance the current turn counter. Call once per sim turn, before TickPending.</summary>
    public void AdvanceTurn() => _currentTurn++;

    /// <summary>Queue a damage event to settle after a number of turns (0 = same turn, next Tick).</summary>
    public static void ScheduleHit(ComponentManager cm, EntityId attacker, EntityId target,
        DamageBlock damage, int delayTurns, Components.StatusEffectSpec? status = null)
    {
        var dd = cm.DelayedDamage;
        if (dd == null)
        {
            // No delay system wired (pure determinism tests): apply instantly through Resistance.
            ApplyDirect(cm, attacker, target, damage, status);
            return;
        }
        dd._pending.Add(new PendingHit
        {
            TriggerTurn = dd._currentTurn + delayTurns,
            Attacker = attacker,
            Target = target,
            Damage = damage,
            Status = status
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
                ApplyDirect(cm, hit.Attacker, hit.Target, hit.Damage, hit.Status);
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
    private static void ApplyDirect(ComponentManager cm, EntityId attacker, EntityId target,
        DamageBlock raw, Components.StatusEffectSpec? status)
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

        int hpBefore = health?.Current ?? 0;
        health?.TakeDamage(final);
        int dealt = health != null ? hpBefore - health.Current : 0;   // 实际扣血(封顶后)

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
        // 原版 Health.js:xp = 受害者 Loot/xp × 本次实际扣血占 maxHp 的比例
        // (按比例计入晋升经验;无 Loot/xp 的目标不给经验——此前按伤害量平给)。
        var promotion = cm.QueryInterface<PromotionComponent>(attacker);
        if (promotion != null && dealt > 0 && health != null && health.Max > 0)
        {
            var loot = cm.QueryInterface<Components.LootComponent>(target);
            int lootXp = loot?.GetXp(cm) ?? 0;
            if (lootXp > 0)
            {
                int xp = (int)MathF.Floor(lootXp * (float)dealt / health.Max);
                if (xp > 0) promotion.AddXP(cm, xp);
            }
        }

        // 攻击附带状态效果(原版 ApplyStatus → StatusEffectsReceiver.ApplyStatus):
        // 命中即挂;叠放规则由接收器自理。
        if (status != null && final.TotalPhysical > 0)
        {
            var receiver = cm.QueryInterface<Components.StatusEffectsReceiverComponent>(target);
            if (receiver != null)
            {
                int attackerOwner = cm.QueryInterface<OwnershipComponent>(attacker)?.PlayerId ?? -1;
                receiver.AddStatus(cm, status.Name, status.ToStatusEffect(), attacker, attackerOwner);
            }
        }

        // Notify the sim event bus so the presentation layer can play hit feedback.
        cm.Events.RaiseAttackLanded(new AttackLandedEvent
        {
            Target = target,
            Attacker = attacker,
            DamageDealt = final.TotalPhysical,
            CaptureDealt = captureDealt.ToFloat(),
        });

        // 玩家级受击分发(原版 MT_Attacked 广播 → 各玩家 AttackDetection/BattleDetection
        // 过滤己方目标):受害方玩家得警报(抑制去重)与战区更新。
        if (final.TotalPhysical > 0 && health != null)
        {
            int victimOwner = cm.QueryInterface<OwnershipComponent>(target)?.PlayerId ?? -1;
            if (victimOwner > 0
                && cm.Players.GetPlayerEntityId(victimOwner) is { } victimPlayerEntity)
            {
                cm.QueryInterface<AttackDetectionComponent>(victimPlayerEntity)
                    ?.OnAttacked(cm, target, attacker);
                cm.QueryInterface<BattleDetectionComponent>(victimPlayerEntity)
                    ?.OnAttacked(cm, target, attacker);
            }
        }
    }

    /// <summary>Number of hits still queued (for debugging/testing).</summary>
    public int PendingCount => _pending.Count;

    /// <summary>直接结算一次命中(抗性→扣血→事件)。DeathDamage 的圆形溅射逐目标
    /// 走此入口,与排队命中同管线(原版 CauseDamageOverArea → Hit)。</summary>
    public static void ApplyHit(ComponentManager cm, EntityId attacker, EntityId target,
        DamageBlock raw, Components.StatusEffectSpec? status) =>
        ApplyDirect(cm, attacker, target, raw, status);
}
