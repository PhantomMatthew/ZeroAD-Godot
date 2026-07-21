using Xunit;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Tests;

/// <summary>
/// Tests for the rewritten ObstructionManager: shape CRUD, spatial-subdivision indexing, and the
/// placement tests (TestUnitShape/TestStaticShape) that BuildRestrictions and Footprint rely on.
/// </summary>
public class ObstructionTests
{
    private static ObstructionManager NewMgr(int gridSize = 32, float cellSize = 4f)
        => new(gridSize, cellSize);

    private static readonly FixedVector2D U = new(Fixed.FromInt(1), Fixed.Zero);
    private static readonly FixedVector2D V = new(Fixed.Zero, Fixed.FromInt(1));

    [Fact]
    public void AddStaticShape_ThenTestOverlapping_DetectsCollision()
    {
        var mgr = NewMgr();
        // 10×10 building at (20, 20) — half-size 5×5.
        mgr.AddStaticShape(new EntityId(1), Fixed.FromInt(20), Fixed.FromInt(20), U, V,
            Fixed.FromInt(5), Fixed.FromInt(5), ObstructionFlags.DefaultBlock, 0, 0);

        // A 3×3 OBB at (22, 20) (overlaps) must hit.
        var hits = mgr.TestStaticShape(null, Fixed.FromInt(22), Fixed.FromInt(20), U, V,
            Fixed.FromInt(1), Fixed.FromInt(1));
        Assert.NotEmpty(hits);
    }

    [Fact]
    public void TestStaticShape_ClearArea_NoCollisions()
    {
        var mgr = NewMgr();
        mgr.AddStaticShape(new EntityId(1), Fixed.FromInt(20), Fixed.FromInt(20), U, V,
            Fixed.FromInt(5), Fixed.FromInt(5), ObstructionFlags.DefaultBlock, 0, 0);

        var hits = mgr.TestStaticShape(null, Fixed.FromInt(100), Fixed.FromInt(100), U, V,
            Fixed.FromInt(2), Fixed.FromInt(2));
        Assert.Empty(hits);
    }

    [Fact]
    public void TestUnitShape_CollidesWithStatic()
    {
        var mgr = NewMgr();
        mgr.AddStaticShape(new EntityId(1), Fixed.FromInt(20), Fixed.FromInt(20), U, V,
            Fixed.FromInt(5), Fixed.FromInt(5), ObstructionFlags.DefaultBlock, 0, 0);

        // Unit circle of radius 2 at (25, 20): edge of building is at 25, so touching → collide.
        var hits = mgr.TestUnitShape(null, Fixed.FromInt(25), Fixed.FromInt(20), Fixed.FromInt(2));
        Assert.NotEmpty(hits);
    }

    [Fact]
    public void TestUnitShape_ClearOfStatic_NoCollisions()
    {
        var mgr = NewMgr();
        mgr.AddStaticShape(new EntityId(1), Fixed.FromInt(20), Fixed.FromInt(20), U, V,
            Fixed.FromInt(5), Fixed.FromInt(5), ObstructionFlags.DefaultBlock, 0, 0);

        // Unit circle of radius 1 at (50, 50) is far away.
        var hits = mgr.TestUnitShape(null, Fixed.FromInt(50), Fixed.FromInt(50), Fixed.FromInt(1));
        Assert.Empty(hits);
    }

    [Fact]
    public void Filter_SkipsByGroup()
    {
        var mgr = NewMgr();
        // Two shapes in group 7.
        mgr.AddStaticShape(new EntityId(1), Fixed.FromInt(20), Fixed.FromInt(20), U, V,
            Fixed.FromInt(5), Fixed.FromInt(5), ObstructionFlags.DefaultBlock, 7, 0);
        // Filter that skips anything in group 7.
        ObstructionShapeFilter skipGroup7 = (_, _, group, _) => group == 7;

        var hits = mgr.TestStaticShape(skipGroup7, Fixed.FromInt(22), Fixed.FromInt(20), U, V,
            Fixed.FromInt(1), Fixed.FromInt(1));
        Assert.Empty(hits);
    }

    [Fact]
    public void Filter_SkipsByFlag()
    {
        var mgr = NewMgr();
        // A shape that blocks movement but NOT foundation.
        mgr.AddStaticShape(new EntityId(1), Fixed.FromInt(20), Fixed.FromInt(20), U, V,
            Fixed.FromInt(5), Fixed.FromInt(5), ObstructionFlags.BlockMovement, 0, 0);
        // Placement filter: only count shapes that block FOUNDATION.
        ObstructionShapeFilter requireFoundation = (_, flags, _, _) =>
            (flags & ObstructionFlags.BlockFoundation) == 0;

        var hits = mgr.TestStaticShape(requireFoundation, Fixed.FromInt(22), Fixed.FromInt(20), U, V,
            Fixed.FromInt(1), Fixed.FromInt(1));
        Assert.Empty(hits);
    }

    [Fact]
    public void RemoveShape_StopsColliding()
    {
        var mgr = NewMgr();
        var tag = mgr.AddStaticShape(new EntityId(1), Fixed.FromInt(20), Fixed.FromInt(20), U, V,
            Fixed.FromInt(5), Fixed.FromInt(5), ObstructionFlags.DefaultBlock, 0, 0);

        Assert.NotEmpty(mgr.TestStaticShape(null, Fixed.FromInt(22), Fixed.FromInt(20), U, V,
            Fixed.FromInt(1), Fixed.FromInt(1)));

        mgr.RemoveShape(tag);

        Assert.Empty(mgr.TestStaticShape(null, Fixed.FromInt(22), Fixed.FromInt(20), U, V,
            Fixed.FromInt(1), Fixed.FromInt(1)));
    }

    [Fact]
    public void MoveUnitShape_UpdatesCollisions()
    {
        var mgr = NewMgr();
        var tag = mgr.AddUnitShape(new EntityId(1), Fixed.FromInt(10), Fixed.FromInt(10),
            Fixed.FromInt(2), ObstructionFlags.BlockMovement, 0);
        // Add a static building at (20,10).
        mgr.AddStaticShape(new EntityId(2), Fixed.FromInt(20), Fixed.FromInt(10), U, V,
            Fixed.FromInt(3), Fixed.FromInt(3), ObstructionFlags.DefaultBlock, 0, 0);

        // When testing placement we must skip the unit's own shape, otherwise a unit always
        // collides with itself. (This is exactly why the original uses SkipTag filters.)
        ObstructionShapeFilter skipSelf = (t, _, _, _) => t == tag;

        // Unit at (10,10) is far from building at (20,10, half=3, so edge at 17).
        Assert.Empty(mgr.TestUnitShape(skipSelf, Fixed.FromInt(10), Fixed.FromInt(10), Fixed.FromInt(2)));

        // Move the unit onto the building.
        mgr.MoveUnitShape(tag, Fixed.FromInt(20), Fixed.FromInt(10));
        Assert.NotEmpty(mgr.TestUnitShape(skipSelf, Fixed.FromInt(20), Fixed.FromInt(10), Fixed.FromInt(2)));
    }

    [Fact]
    public void LegacyGrid_BlockCircleStillWorks()
    {
        // The bool[,] + FindPath compatibility layer must still route around blocked cells.
        var mgr = NewMgr(gridSize: 16, cellSize: 4f);
        mgr.BlockCircle(20f, 20f, 8f);
        Assert.True(mgr.IsBlocked(mgr.WorldToGrid(20f), mgr.WorldToGrid(20f)));
        Assert.False(mgr.IsBlocked(mgr.WorldToGrid(4f), mgr.WorldToGrid(4f)));
    }

    [Fact]
    public void LegacyGrid_FindPathRoutesAroundBlock()
    {
        var mgr = NewMgr(gridSize: 16, cellSize: 4f);
        mgr.BlockCircle(20f, 20f, 8f);
        var path = mgr.FindPath(0, 0, 7, 7);
        // A path should exist (16×16 grid, one blocked region).
        Assert.NotEmpty(path);
    }
}
