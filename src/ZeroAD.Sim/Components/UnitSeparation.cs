using System.Collections.Generic;
using System.Runtime.InteropServices;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Components;

/// <summary>
/// Per-turn unit-pushing pass — a faithful, slimmed port of
/// <c>CCmpUnitMotionManager::Move</c>/<c>Push</c> (<c>CCmpUnitMotion_System.cpp</c>). Without it,
/// units rallied to (or converging on) the same point stack on one coordinate and occlude each
/// other — only one renders. This runs every sim turn, after <see cref="UnitMotion.Tick"/>, and
/// pushes overlapping units apart so a cluster spreads into a visible group.
///
/// <para><b>Determinism</b>: this lives in the sim turn body (called from <c>TickSimulation</c>),
/// which runs identically on every lockstep peer. All math is fixed-point; pairs are visited once
/// in ascending entity-id order; the degenerate coincident case uses entity-id parity for a
/// deterministic push axis. No new state is serialized — the pass is pure (reads positions,
/// writes positions).</para>
///
/// <para><b>Scope vs. the original (v1)</b>: the core push geometry is ported exactly —
/// <see cref="PushingCorrection"/>, the radius multiplier, moving/static push extensions &amp;
/// spread, the distance factor, the per-pair weight/time cap, and the minimal-push gate. Omitted
/// refinements (flagged inline): pushing-pressure bog-down, formation control-group bypass, the
/// mid-turn path-crossing perpendicular nudge (needs the pre-move position), per-template weight,
/// and the <c>CheckMovement</c> "don't push into impassable terrain" clamp (needs the unit
/// pass-class). These affect crowd dynamics at the margins, not the core unclumping the rally
/// bug hinges on.</para>
/// </summary>
public static class UnitSeparation
{
    // Circle-square area correction (units are circles on a square grid).
    private static readonly Fixed PushingCorrection = Fixed.FromFraction(5, 7);
    // Combined clearance is scaled by this to get the base pushing distance.
    private static readonly Fixed PushingRadiusMultiplier = Fixed.FromFraction(8, 5);
    // Additive range extensions: moving units reach farther than idle ones.
    private static readonly Fixed MovingPushExtension = Fixed.FromFraction(5, 2);
    private static readonly Fixed StaticPushExtension = Fixed.FromInt(2);
    // Pushing is "in full force" within this fraction of the max distance.
    private static readonly Fixed MovingPushingSpread = Fixed.FromFraction(5, 8);
    private static readonly Fixed StaticPushingSpread = Fixed.FromFraction(5, 8);
    // Pushes below this are dropped so units don't drift forever by rounding noise.
    private static readonly Fixed MinimalPushing = Fixed.FromFraction(2, 10);
    // Distance factor ceiling for heavily overlapping pairs.
    private static readonly Fixed MaxDistanceFactor = Fixed.FromFraction(5, 2);
    private const int PushingReductionFactor = 2;
    private const int MaxPushingMultiplier = 4;

    private struct UnitState
    {
        public EntityId Entity;
        public PositionComponent Pos;
        public FixedVector2D Pos2D;
        public Fixed Clearance;
        public bool Moving;
        public FixedVector2D Push;
    }

    /// <summary>Run one pushing pass over all in-world units. Call once per sim turn, after
    /// <see cref="UnitMotion.Tick"/> has advanced positions. Mutates <see cref="PositionComponent"/>
    /// and notifies spatial listeners (RangeManager/ObstructionManager) of the moves.</summary>
    public static void Separate(ComponentManager cm, Fixed dt)
    {
        var units = new List<UnitState>();
        foreach (var eid in cm.AllEntities)
        {
            var pos = cm.QueryInterface<PositionComponent>(eid);
            var obs = cm.QueryInterface<ObstructionComponent>(eid);
            if (pos == null || obs == null || obs.Type != ObstructionType.Unit || !obs.Active)
                continue; // Only unit-shape obstructions get pushed; buildings (Static) stay put.

            var motion = cm.QueryInterface<UnitMotion>(eid);
            units.Add(new UnitState
            {
                Entity = eid,
                Pos = pos,
                Pos2D = new FixedVector2D(pos.Position.X, pos.Position.Z),
                Clearance = obs.Size0,
                // Post-move: a unit that stepped this turn has CurrentSpeed > 0; one that just
                // arrived (or is idle) has 0. Mirrors C++ MotionState.isMoving.
                Moving = motion != null && motion.CurrentSpeed > Fixed.Zero,
            });
        }
        if (units.Count < 2) return;

        // Deterministic pair order: ascending entity id (matches the C++ EntityMap + `it<it2` guard).
        units.Sort((a, b) => a.Entity.Value.CompareTo(b.Entity.Value));

        var span = CollectionsMarshal.AsSpan(units);
        for (int i = 0; i < span.Length; i++)
            for (int j = i + 1; j < span.Length; j++)
                Push(ref span[i], ref span[j], dt);

        for (int i = 0; i < span.Length; i++)
        {
            ref var u = ref span[i];
            if (u.Push.CompareLength(MinimalPushing) <= 0)
                continue; // Negligible — drop to avoid perpetual sub-step drift.

            // TODO(fidelity): port the CheckMovement clamp — refuse the push if pos→pos+push crosses
            // an impassable navcell (needs the unit's PassClass from the pathfinder). Skipped in v1;
            // on open ground (the rally case) the push stays passable.
            FixedVector2D old2 = u.Pos2D;
            u.Pos2D = new FixedVector2D(old2.X + u.Push.X, old2.Y + u.Push.Y);
            u.Pos.Position = new FixedVector3D(u.Pos2D.X, u.Pos.Position.Y, u.Pos2D.Y);
            SimSystem.NotifyPositionChanged(u.Entity, old2, u.Pos2D);
        }
    }

    /// <summary>Accumulate a push between two units into their <see cref="UnitState.Push"/> vectors.
    /// Ports <c>CCmpUnitMotionManager::Push</c>; see the class doc for the omitted refinements.</summary>
    private static void Push(ref UnitState a, ref UnitState b, Fixed dt)
    {
        // Moving vs. static pairs don't interact (the original's simplification): moving pushes
        // moving, idle pushes idle, but a walker never shoves a stopped unit and vice-versa.
        int movingPush = (a.Moving ? 1 : 0) + (b.Moving ? 1 : 0);
        if (movingPush == 1) return;

        Fixed combinedClearance = (a.Clearance + b.Clearance).Multiply(PushingCorrection);
        Fixed extension = movingPush != 0 ? MovingPushExtension : StaticPushExtension;
        Fixed maxDist = combinedClearance.Multiply(PushingRadiusMultiplier) + extension;
        Fixed spread = movingPush != 0 ? MovingPushingSpread : StaticPushingSpread;
        combinedClearance = maxDist.Multiply(spread);

        // v1: uses current positions. The original averages (pos+initialPos)/2 to catch pairs that
        // cross mid-turn; we don't capture initialPos here, so the crossing-perpendicular nudge
        // (which depends on that delta) is omitted too.
        FixedVector2D offset = a.Pos2D - b.Pos2D;
        if (offset.CompareLength(maxDist) > 0) return; // Beyond pushing range.

        Fixed offsetLength = offset.Length();
        if (offsetLength <= Fixed.Epsilon * 10)
        {
            // Coincident: pick a deterministic axis from a's entity-id parity so the cluster
            // unclumps instead of sitting on a divide-by-zero.
            bool dir = (a.Entity.Value & 1u) != 0u;
            offset = new FixedVector2D(
                dir ? Fixed.FromInt(1) : Fixed.Zero,
                dir ? Fixed.Zero : Fixed.FromInt(1));
            offsetLength = Fixed.Epsilon * 10;
        }
        else
        {
            offset = new FixedVector2D(offset.X / offsetLength, offset.Y / offsetLength);
        }

        // 1 at the spread-modified clearance, up to MaxDistanceFactor when heavily overlapping.
        Fixed distanceFactor = maxDist - combinedClearance;
        if (distanceFactor <= Fixed.Zero || offsetLength < combinedClearance / 2)
        {
            distanceFactor = MaxDistanceFactor;
        }
        else
        {
            Fixed val = (maxDist - offsetLength) / distanceFactor;
            if (val < Fixed.Zero) val = Fixed.Zero;
            if (val > MaxDistanceFactor) val = MaxDistanceFactor;
            distanceFactor = val;
        }

        FixedVector2D pushingDir = offset.Multiply(distanceFactor);

        // v1: uniform weight (original reads a per-template GetWeight). With weight 1 on both
        // sides the push is symmetric and equal — the common case for same-species clumps.
        Fixed aWeight = Fixed.FromInt(1);
        Fixed bWeight = Fixed.FromInt(1);
        Fixed timeFactor = dt / PushingReductionFactor;
        Fixed maxPushing = timeFactor * MaxPushingMultiplier;

        Fixed aMag = bWeight.MulDiv(timeFactor, aWeight);
        if (aMag > maxPushing) aMag = maxPushing;
        Fixed bMag = aWeight.MulDiv(timeFactor, bWeight);
        if (bMag > maxPushing) bMag = maxPushing;

        a.Push += pushingDir.Multiply(aMag);
        b.Push -= pushingDir.Multiply(bMag);
    }
}
