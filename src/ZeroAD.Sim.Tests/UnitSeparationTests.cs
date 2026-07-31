using System.Collections.Generic;
using Xunit;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Tests for <see cref="UnitSeparation"/> — the per-turn unit-pushing pass that ports
/// <c>CCmpUnitMotionManager::Move</c>/<c>Push</c>. Without it, units rallied to the same point
/// stack on one coordinate and occlude each other (only one renders). These tests pin the core
/// contract: coincident units unclump, and the result is byte-identical across two identical runs
/// (lockstep determinism — every peer must compute the same pushed positions).
/// </summary>
public sealed class UnitSeparationTests
{
    private const float Tick = 0.1f;

    /// <summary>Build a world with <paramref name="n"/> units stacked exactly on the origin,
    /// each with a unit obstruction of clearance 1. No ObstructionManager / Pathfinder wired —
    /// separation reads clearance straight off the component, so the kernel test stays hermetic.
    /// Returns the local <see cref="ComponentManager"/> so callers never touch the static
    /// <see cref="SimSystem.Sim"/> (which other parallel test classes re-Init out from under us).</summary>
    private static (ComponentManager cm, List<EntityId> ids) BuildStackedUnits(int n)
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        var ids = new List<EntityId>();
        for (int i = 0; i < n; i++)
        {
            var e = cm.CreateEntity();
            cm.AddComponent(e, new PositionComponent());
            cm.QueryInterface<PositionComponent>(e)!.Position =
                new FixedVector3D(Fixed.Zero, Fixed.Zero, Fixed.Zero);
            cm.AddComponent(e, new UnitMotion());           // static (HasMoveTarget=false) → pushed as idle
            cm.AddComponent(e, new ObstructionComponent { Type = ObstructionType.Unit, Size0 = Fixed.FromInt(1) });
            ids.Add(e);
        }
        return (cm, ids);
    }

    private static List<(Fixed x, Fixed z)> Positions(ComponentManager cm, List<EntityId> ids)
    {
        var result = new List<(Fixed x, Fixed z)>();
        foreach (var e in ids)
        {
            var p = cm.QueryInterface<PositionComponent>(e)!.Position;
            result.Add((p.X, p.Z));
        }
        return result;
    }

    private static int DistinctCount(List<(Fixed x, Fixed z)> pts)
    {
        int distinct = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            bool unique = true;
            for (int j = 0; j < i; j++)
                if ((pts[i].x - pts[j].x).Absolute.ToFloat() + (pts[i].z - pts[j].z).Absolute.ToFloat() < 0.05f)
                { unique = false; break; }
            if (unique) distinct++;
        }
        return distinct;
    }

    [Fact]
    public void Separate_UnclumpsStackedUnits()
    {
        var (cm, ids) = BuildStackedUnits(6);

        // All six start coincident at the origin.
        Assert.Equal(1, DistinctCount(Positions(cm, ids)));

        // Run a few seconds of sim; the pushing pass spreads them off the single point.
        for (int i = 0; i < 60; i++)
            UnitSeparation.Separate(cm, Fixed.FromFloat(Tick));

        // They must no longer all occupy one spot (the reported bug: only one rendered).
        Assert.True(DistinctCount(Positions(cm, ids)) >= 3,
            "coincident units must be pushed apart by the separation pass");
    }

    [Fact]
    public void Separate_IsDeterministicAcrossRuns()
    {
        // Two independent identical worlds must produce identical pushed positions — the
        // lockstep/OOS contract. Any non-determinism here would desync multiplayer.
        var (cm1, ids1) = BuildStackedUnits(5);
        for (int i = 0; i < 40; i++) UnitSeparation.Separate(cm1, Fixed.FromFloat(Tick));
        var final1 = Positions(cm1, ids1);

        var (cm2, ids2) = BuildStackedUnits(5);
        for (int i = 0; i < 40; i++) UnitSeparation.Separate(cm2, Fixed.FromFloat(Tick));
        var final2 = Positions(cm2, ids2);

        Assert.Equal(final1.Count, final2.Count);
        for (int i = 0; i < final1.Count; i++)
        {
            Assert.Equal(final1[i].x.InternalValue, final2[i].x.InternalValue);
            Assert.Equal(final1[i].z.InternalValue, final2[i].z.InternalValue);
        }
    }

    [Fact]
    public void Separate_LeavesBuildingsAlone()
    {
        // Buildings (Static obstruction) must never be pushed — only units unclump.
        var cm = new ComponentManager(7);
        SimSystem.Init(cm);
        var building = cm.CreateEntity();
        cm.AddComponent(building, new PositionComponent());
        cm.QueryInterface<PositionComponent>(building)!.Position =
            new FixedVector3D(Fixed.Zero, Fixed.Zero, Fixed.Zero);
        cm.AddComponent(building, new ObstructionComponent
        { Type = ObstructionType.Static, Size0 = Fixed.FromInt(6), Size1 = Fixed.FromInt(6) });
        var before = cm.QueryInterface<PositionComponent>(building)!.Position;

        for (int i = 0; i < 20; i++)
            UnitSeparation.Separate(cm, Fixed.FromFloat(Tick));

        var after = cm.QueryInterface<PositionComponent>(building)!.Position;
        Assert.Equal(before.X.InternalValue, after.X.InternalValue);
        Assert.Equal(before.Z.InternalValue, after.Z.InternalValue);
    }
}
