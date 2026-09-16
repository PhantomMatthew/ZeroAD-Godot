using System;
using System.Collections.Generic;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;
using Xunit;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Rally overlay polyline vs <c>CCmpRallyPointRenderer::RecomputeRallyPointPath</c>.
/// The old Godot path prepended the building centre, walked goal-first waypoints,
/// then appended the flags — a there-and-back fold.
/// </summary>
public sealed class RallyPointPathTests
{
    private const int Tiles = 32;
    private const float TileSize = 4f;

    private static (ComponentManager Cm, PathfinderComponent Pf, EntityId Building, PositionComponent Pos,
        FootprintComponent Fp) SetupSquareBuilding(float x, float z, int size)
    {
        var cm = new ComponentManager(42);
        SimSystem.Init(cm);
        SimSystem.SetObstructionManager(new ObstructionManager(Tiles * (int)TileSize, TileSize));

        var terrain = new TerrainComponent();
        terrain.Configure(Tiles, TileSize);
        var grid = new TerrainClass[Tiles, Tiles];
        for (int i = 0; i < Tiles; i++)
            for (int j = 0; j < Tiles; j++)
                grid[i, j] = TerrainClass.Land;
        terrain.SetPassabilityGrid(grid);

        var pf = new PathfinderComponent(cm);
        pf.SetTerrain(terrain);
        pf.RebuildGrid();
        SimSystem.SetPathfinder(pf);

        var b = cm.CreateEntity();
        var pos = new PositionComponent();
        cm.AddComponent(b, pos);
        pos.Position = new FixedVector3D(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
        var fp = new FootprintComponent
        {
            Shape = FootprintShape.Square,
            Size0 = Fixed.FromInt(size),
            Size1 = Fixed.FromInt(size),
        };
        cm.AddComponent(b, fp);
        return (cm, pf, b, pos, fp);
    }

    private static float PolyLength(IReadOnlyList<FixedVector2D> pts)
    {
        float len = 0f;
        for (int i = 1; i < pts.Count; i++)
            len += (pts[i] - pts[i - 1]).Length().ToFloat();
        return len;
    }

    [Fact]
    public void ClosestEdge_EastRally_LandsOnEastFace()
    {
        var (_, _, _, pos, fp) = SetupSquareBuilding(32f, 64f, 27);
        var rally = new FixedVector2D(Fixed.FromInt(100), Fixed.FromInt(64));
        var edge = RallyPointPath.ClosestEdgePoint(pos, fp, rally);
        Assert.Equal(64f, edge.Y.ToFloat(), 1);
        Assert.InRange(edge.X.ToFloat(), 32f + 13f - 0.5f, 32f + 13.5f + 0.5f);
        Assert.True(edge.X.ToFloat() > 32f + 5f, "edge must not be the building centre");
    }

    [Fact]
    public void TravelPolyline_DoesNotFoldBackThroughTheBuilding()
    {
        var (_, pf, _, pos, fp) = SetupSquareBuilding(32f, 64f, 27);
        var rally = new FixedVector2D(Fixed.FromInt(100), Fixed.FromInt(64));
        var rallies = new[] { rally };

        var travel = RallyPointPath.ComputeTravelPolyline(pf, pos, fp, rallies);
        Assert.True(travel.Count >= 2);

        var start = RallyPointPath.ClosestEdgePoint(pos, fp, rally);
        Assert.True((travel[0] - start).Length().ToFloat() < 2f,
            "travel polyline must start at the footprint edge, not the centre");
        Assert.True((travel[travel.Count - 1] - rally).Length().ToFloat() < 2f,
            "travel polyline must end at the rally flag");

        float straight = (rally - start).Length().ToFloat();
        float ours = PolyLength(travel);
        Assert.True(ours < straight * 1.6f,
            $"overlay length {ours:F1} is too long vs straight {straight:F1} (fold?)");

        for (int i = 1; i < travel.Count; i++)
            Assert.True(travel[i].X.ToFloat() + 1.5f >= travel[i - 1].X.ToFloat(),
                $"travel X must not reverse toward the CC (i={i} {travel[i - 1].X.ToFloat():F1}→{travel[i].X.ToFloat():F1})");

        var raw = pf.ComputePath(start, PathGoal.Point(rally.X, rally.Y),
            pf.GetPassabilityClassMask(RallyPointPath.LinePassabilityClass));
        if (raw.Waypoints.Count >= 2)
        {
            var wps = new List<FixedVector2D>(raw.Waypoints.Count);
            for (int i = 0; i < raw.Waypoints.Count; i++)
                wps.Add(new FixedVector2D(raw.Waypoints[i].X, raw.Waypoints[i].Z));
            if ((wps[0] - rally).CompareLength(wps[wps.Count - 1] - rally) > 0)
                wps.Reverse();
            var folded = new List<FixedVector2D>();
            folded.Add(new FixedVector2D(pos.Position.X, pos.Position.Z));
            folded.AddRange(wps);
            folded.Add(rally);
            float foldedLen = PolyLength(folded);
            Assert.True(ours * 1.4f < foldedLen,
                $"expected C++ overlay ({ours:F1}) shorter than centre+goal-first+flag fold ({foldedLen:F1})");
        }
    }

    [Fact]
    public void EmptyPath_IsEdgeToFlag_NotCentre()
    {
        var (_, pf, _, pos, fp) = SetupSquareBuilding(32f, 64f, 27);
        var toward = new FixedVector2D(Fixed.FromInt(100), Fixed.FromInt(64));
        var edge = RallyPointPath.ClosestEdgePoint(pos, fp, toward);
        // Same navcell as the edge → pathfinder returns < 2 waypoints (C++ two-point fallback).
        var rally = new FixedVector2D(edge.X + Fixed.FromFloat(0.2f), edge.Y);
        var seg = RallyPointPath.ComputeSegment(pf, pos, fp, new[] { rally }, 0);
        Assert.Equal(2, seg.Count);
        Assert.Equal(RallyPointPath.ClosestEdgePoint(pos, fp, rally), seg[0]);
        Assert.Equal(rally, seg[1]);
        Assert.NotEqual(new FixedVector2D(pos.Position.X, pos.Position.Z), seg[0]);
    }

    [Fact]
    public void SecondFlag_PathsFromPreviousFlag()
    {
        var (_, pf, _, pos, fp) = SetupSquareBuilding(32f, 64f, 27);
        var a = new FixedVector2D(Fixed.FromInt(80), Fixed.FromInt(64));
        var b = new FixedVector2D(Fixed.FromInt(110), Fixed.FromInt(64));
        var travel = RallyPointPath.ComputeTravelPolyline(pf, pos, fp, new[] { a, b });
        Assert.True(travel.Count >= 2);
        Assert.True((travel[travel.Count - 1] - b).Length().ToFloat() < 2f);
        float len = PolyLength(travel);
        float via = (a - RallyPointPath.ClosestEdgePoint(pos, fp, a)).Length().ToFloat()
                    + (b - a).Length().ToFloat();
        Assert.True(len < via * 1.6f, $"two-flag overlay {len:F1} vs via-flags {via:F1}");
    }
}
