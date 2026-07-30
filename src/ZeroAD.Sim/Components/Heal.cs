using System;
using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// Heal — port of Heal.js. A healer restores HP to an injured allied unit in range: first heal
// after a 1 s prepare, then one tick every Interval. The interval since the last performed
// heal carries across target switches (repeatLeft), matching the original's timer semantics.
//
// Original timer = Engine Timer SetInterval(prepare, repeat); the port accumulates dt in
// Tick (driven by UnitAI's HEAL.HEALING state) with fire points at Prepare, Prepare+Rate, …
// SinceLastHeal replaces the original's absolute-timestamp `lastHealed` (the kernel has no
// global clock); it ages only while this component ticks and is capped — both deterministic,
// and identical in effect to the original for any interval ≪ cap.

/// <summary>Outcome of one <see cref="HealComponent.Tick"/> — UnitAI maps these to FSM transitions.</summary>
public enum HealTickResult
{
    /// <summary>No active target.</summary>
    Idle,
    /// <summary>Healing in progress (or waiting for the next fire point).</summary>
    Healing,
    /// <summary>Target can no longer be healed (dead, full HP, invalid) — healing stopped.</summary>
    TargetInvalid,
    /// <summary>Target left heal range — healing stopped.</summary>
    OutOfRange,
}

[Component("Heal", "Heal")]
public sealed class HealComponent : ComponentBase, IComponentMessageHandler
{
    public int HealAmount = 5;     // template Heal/Health (HP restored per tick)
    public float Range = 15f;      // template Heal/Range
    public float Rate = 1f;        // template Heal/Interval (seconds between ticks)
    // Template Heal/HealableClasses + Heal/UnhealableClasses — restrict valid heal targets.
    public readonly List<string> HealableClasses = new();
    public readonly List<string> UnhealableClasses = new();

    public EntityId? Target;        // runtime: entity currently being healed
    public float Prepare = PrepareTime;   // seconds until the first heal of this session
    public float Elapsed;                 // seconds since StartHealing
    public float SinceLastHeal = NeverHealed; // seconds since the last performed heal (capped)

    private const float PrepareTime = 1.0f;    // 原版 GetTimers().prepare = 1000ms
    private const float NeverHealed = 1000f;   // ≫ 任何 Interval → repeatLeft<0 → 默认 prepare

    /// <summary>Port of Heal.js CanHeal: injured + ally-owned + class-restricted.</summary>
    public bool CanHeal(ComponentManager cm, EntityId target)
    {
        var health = cm.QueryInterface<HealthComponent>(target);
        if (health == null || health.Unhealable || !health.IsInjured)
            return false;

        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        if (own == null)
            return false;
        // IsOwnedByAllyOfPlayer:同主或互盟(原版 helper;互盟 = 双向 ally,同队 seed)。
        var targetOwn = cm.QueryInterface<OwnershipComponent>(target);
        if (targetOwn == null)
            return false;
        if (targetOwn.PlayerId != own.PlayerId
            && !cm.Players.GetMutualAllies(own.PlayerId).Contains(targetOwn.PlayerId))
            return false;

        var identity = cm.QueryInterface<IdentityComponent>(target);
        if (identity == null)
            return false;
        // 原版:unhealable 命中即拒(即使 healable 也命中);healable 必须命中。
        return !identity.MatchesClassList(string.Join(" ", UnhealableClasses))
            && identity.MatchesClassList(string.Join(" ", HealableClasses));
    }

    /// <summary>Port of Heal.js StartHealing (callerIID/visual 略). False = 目标不可治疗。</summary>
    public bool StartHealing(ComponentManager cm, EntityId target)
    {
        if (Target != null)
            StopHealing();
        if (!CanHeal(cm, target))
            return false;

        // 距上次治疗不足一个 Interval 时,prepare 延长到 repeatLeft(防止换目标刷快治疗)。
        Prepare = PrepareTime;
        float repeatLeft = Rate - SinceLastHeal;
        if (repeatLeft > Prepare)
            Prepare = repeatLeft;

        Target = target;
        Elapsed = 0f;
        return true;
    }

    /// <summary>Port of Heal.js StopHealing (reason/callerIID 通知略——UnitAI 由 Tick 返回值驱动)。</summary>
    public void StopHealing()
    {
        Target = null;
    }

    /// <summary>Advance the heal timer; performs heals at Prepare, Prepare+Rate, …
    /// CanHeal/range re-checks happen at fire points only — same granularity as the original's
    /// interval timer.</summary>
    public HealTickResult Tick(float dt, ComponentManager cm)
    {
        if (SinceLastHeal < NeverHealed)
            SinceLastHeal = Math.Min(NeverHealed, SinceLastHeal + dt);
        if (Target is not { } target)
            return HealTickResult.Idle;

        Elapsed += dt;
        while (Elapsed >= Prepare)
        {
            if (!CanHeal(cm, target))
            {
                StopHealing();
                return HealTickResult.TargetInvalid;
            }
            if (!IsTargetInRange(cm, target))
            {
                StopHealing();
                return HealTickResult.OutOfRange;
            }

            var health = cm.QueryInterface<HealthComponent>(target)!;
            health.Heal(HealAmount);
            SinceLastHeal = 0f;
            Prepare += Rate;

            // 原版:补满即停(TargetInvalidated → UnitAI 找新目标/收工)。
            if (!health.IsInjured)
            {
                StopHealing();
                return HealTickResult.TargetInvalid;
            }
        }
        return HealTickResult.Healing;
    }

    /// <summary>Port of Heal.js IsTargetInRange — edge-to-edge(中心距 − 目标障碍半径 ≤ Range)。</summary>
    public bool IsTargetInRange(ComponentManager cm, EntityId target)
    {
        var a = cm.QueryInterface<PositionComponent>(Entity);
        var b = cm.QueryInterface<PositionComponent>(target);
        if (a == null || b == null)
            return false;
        var dx = a.Position.X - b.Position.X;
        var dz = a.Position.Z - b.Position.Z;
        long d2 = (long)dx.InternalValue * dx.InternalValue
                + (long)dz.InternalValue * dz.InternalValue;
        var eff = Maths.Fixed.FromFloat(Range);
        var obs = cm.QueryInterface<ObstructionComponent>(target);
        if (obs != null)
            eff += obs.GetSize();
        long r2 = (long)eff.InternalValue * eff.InternalValue;
        return d2 <= r2;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("hp", HealAmount);
        s.NumberFixed("range", Maths.Fixed.FromFloat(Range));
        s.NumberFixed("rate", Maths.Fixed.FromFloat(Rate));
        s.NumberI32("healable_n", HealableClasses.Count);
        foreach (var cls in HealableClasses) s.StringASCII("healable", cls);
        s.NumberI32("unhealable_n", UnhealableClasses.Count);
        foreach (var cls in UnhealableClasses) s.StringASCII("unhealable", cls);
        s.NumberU32("target", Target?.Value ?? 0);
        s.NumberFixed("prepare", Maths.Fixed.FromFloat(Prepare));
        s.NumberFixed("elapsed", Maths.Fixed.FromFloat(Elapsed));
        s.NumberFixed("sinceLastHeal", Maths.Fixed.FromFloat(SinceLastHeal));
    }

    public override void Deserialize(IDeserializer d)
    {
        HealAmount = d.NumberI32("hp");
        Range = d.NumberFixed("range").ToFloat();
        Rate = d.NumberFixed("rate").ToFloat();
        HealableClasses.Clear();
        int hn = d.NumberI32("healable_n");
        for (int i = 0; i < hn; i++) HealableClasses.Add(d.StringASCII("healable"));
        UnhealableClasses.Clear();
        int un = d.NumberI32("unhealable_n");
        for (int i = 0; i < un; i++) UnhealableClasses.Add(d.StringASCII("unhealable"));
        uint t = d.NumberU32("target");
        Target = t != 0 ? new EntityId(t) : null;
        Prepare = d.NumberFixed("prepare").ToFloat();
        Elapsed = d.NumberFixed("elapsed").ToFloat();
        SinceLastHeal = d.NumberFixed("sinceLastHeal").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}
