using System;

namespace ZeroAD.Sim.Components;

/// <summary>
/// Maps UnitAI FSM (+ motion/combat fallbacks) to actor clip names.
/// Original CCmpUnitMotion selects walk/run from actual movement; the Godot
/// layer maps FSM names instead (10 Hz sim vs 60 fps would stutter on deltas).
/// EndsWith(".WALKING") misses WALKINGANDFIGHTING (tutorial attack-walk).
/// </summary>
public static class UnitAnimationMapper
{
    public static string Resolve(
        string fsm,
        bool hasMoveTarget,
        AttackComponent.AttackState attackState = AttackComponent.AttackState.Idle,
        string? gatherSpecific = null)
    {
        fsm ??= "";

        if (fsm.Contains("GATHER.GATHERING", StringComparison.Ordinal))
            return string.IsNullOrEmpty(gatherSpecific) ? "gather_tree" : "gather_" + gatherSpecific;
        if (fsm.Contains("REPAIR.REPAIRING", StringComparison.Ordinal))
            return "Build";
        if (fsm.Contains("COMBAT.ATTACKING", StringComparison.Ordinal))
            return "attack_melee";

        if (IsWalkFsm(fsm)
            || hasMoveTarget
            || attackState == AttackComponent.AttackState.Approaching)
            return "Walk";

        return "Idle";
    }

    private static bool IsWalkFsm(string fsm) =>
        fsm.Contains("WALKING", StringComparison.Ordinal)
        || fsm.Contains("APPROACHING", StringComparison.Ordinal)
        || fsm.Contains("RETURNINGRESOURCE", StringComparison.Ordinal)
        || fsm.Contains("FLEEING", StringComparison.Ordinal)
        || fsm.Contains("PATROLLING", StringComparison.Ordinal)
        || fsm.Contains("ESCORTING", StringComparison.Ordinal);
}
