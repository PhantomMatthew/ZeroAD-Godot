using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

[Component("UnitMotion", "UnitMotion")]
public sealed class UnitMotion : ComponentBase, IComponentMessageHandler
{
    public Fixed Speed;
    public Fixed CurrentSpeed;
    public FixedVector2D TargetPos;
    public bool HasMoveTarget;

    private readonly List<(float x, float z)> _waypoints = new();
    private int _currentWaypoint;

    // --- Path-request throttle (perf, not semantics) ---
    // MoveToPoint runs the FULL hierarchical/A* pipeline (PathfinderComponent.ComputePath)
    // synchronously. When a unit chases a moving target, its caller re-issues MoveToPoint
    // nearly every tick: the short combat-range path is consumed in <1 tick → HasMoveTarget
    // flips false → the approach Timer handler re-requests. Unthrottled this is N full A*
    // solves per tick (one per approaching unit), which tanks FPS once combat starts.
    //
    // We recompute the full path at most once per RepathInterval; between, we keep walking
    // the existing waypoints (or a cheap direct beeline if they're exhausted). If the goal
    // jumped beyond RepathGoalThreshold since the last solve we recompute immediately (the
    // target genuinely relocated, not just crept). All-throttle state is transient cache:
    // it is NOT serialized (waypoints aren't either), so it never touches the OOS hash and
    // re-converges to a full solve on the first MoveToPoint after a load.
    private const float RepathInterval = 0.3f;     // seconds between full A* solves
    private const float RepathGoalThreshold = 5f;  // metres; bigger shift → re-solve now
    private static readonly long RepathGoalThresholdSqInternal =
        (long)Fixed.FromFloat(RepathGoalThreshold).InternalValue * Fixed.FromFloat(RepathGoalThreshold).InternalValue;
    private float _pathAge;                         // seconds since the last full ComputePath
    private Fixed _lastGoalX, _lastGoalZ;
    private bool _hasLastGoal;

    protected override void OnInit()
    {
        Speed = Fixed.FromFloat(8.0f);
        CurrentSpeed = Fixed.Zero;
        TargetPos = new FixedVector2D(Fixed.Zero, Fixed.Zero);
        HasMoveTarget = false;
    }

    public void MoveToPoint(FixedVector2D target)
    {
        TargetPos = target;
        HasMoveTarget = true;

        // Throttle: if we already solved a path recently toward ~the same goal, keep walking
        // it instead of re-running A*. See the field doc above for the failure mode this fixes.
        long dxg = target.X.InternalValue - _lastGoalX.InternalValue;
        long dzg = target.Y.InternalValue - _lastGoalZ.InternalValue;
        long goalShiftSq = dxg * dxg + dzg * dzg;
        bool goalNear = _hasLastGoal && goalShiftSq <= RepathGoalThresholdSqInternal;
        if (_hasLastGoal && _pathAge < RepathInterval && goalNear)
        {
            // Fresh enough and goal hasn't relocated: don't recompute. If the previous path is
            // already exhausted (unit arrived at the old goal but the caller still wants to
            // close on a target that crept within threshold), extend with a direct beeline so
            // the unit keeps moving instead of stalling until the interval elapses.
            if (_currentWaypoint >= _waypoints.Count)
                _waypoints.Add((target.X.ToFloat(), target.Y.ToFloat()));
            return;
        }

        // Full solve. Reset the throttle bookkeeping.
        _hasLastGoal = true;
        _lastGoalX = target.X;
        _lastGoalZ = target.Y;
        _pathAge = 0f;

        _waypoints.Clear();
        _currentWaypoint = 0;

        var posComp = SimSystem.GetComponent<PositionComponent>(Entity);
        if (posComp == null)
        {
            _waypoints.Add((target.X.ToFloat(), target.Y.ToFloat()));
            return;
        }

        // Prefer the M3 pathfinder (PathfinderComponent) when it's wired and has a grid. This is
        // the deterministic, hierarchical + A* + vertex pipeline. Falls back to the legacy
        // ObstructionManager A* grid when the new pathfinder isn't initialized (pure determinism
        // tests that don't load a map), and finally to a straight beeline.
        var pathfinder = SimSystem.Pathfinder;
        if (pathfinder != null)
        {
            var start = new FixedVector2D(posComp.Position.X, posComp.Position.Z);
            var goal = Pathfinding.PathGoal.Point(target.X, target.Y);
            var path = pathfinder.ComputePath(start, goal);
            // WaypointPath.Waypoints is stored start→goal; consume front-to-back (matching the
            // existing _waypoints contract). Each waypoint is world-space Fixed → float.
            foreach (var wp in path.Waypoints)
                _waypoints.Add((wp.X.ToFloat(), wp.Z.ToFloat()));
            if (_waypoints.Count == 0)
                _waypoints.Add((target.X.ToFloat(), target.Y.ToFloat()));
            return;
        }

#pragma warning disable CS0618 // FindPath is [Obsolete]; retained as the pre-pathfinder fallback.
        if (SimSystem.Obstructions is { } obstructions)
        {
            int sx = obstructions.WorldToGrid(posComp.Position.X.ToFloat());
            int sz = obstructions.WorldToGrid(posComp.Position.Z.ToFloat());
            int ex = obstructions.WorldToGrid(target.X.ToFloat());
            int ez = obstructions.WorldToGrid(target.Y.ToFloat());

            var path = obstructions.FindPath(sx, sz, ex, ez);
            foreach (var (px, pz) in path)
                _waypoints.Add((obstructions.GridToWorld(px), obstructions.GridToWorld(pz)));
            _waypoints.Add((target.X.ToFloat(), target.Y.ToFloat()));
        }
        else
#pragma warning restore CS0618
        {
            _waypoints.Add((target.X.ToFloat(), target.Y.ToFloat()));
        }
    }

    public void Stop()
    {
        HasMoveTarget = false;
        CurrentSpeed = Fixed.Zero;
        _waypoints.Clear();
        // Drop the cached goal so the next MoveToPoint always solves a fresh path (a Stop
        // means the caller deliberately cancelled movement, not a chase tick).
        _hasLastGoal = false;
        _pathAge = 0f;
    }

    public void Tick(float dt)
    {
        _pathAge += dt;
        if (!HasMoveTarget || _currentWaypoint >= _waypoints.Count)
        {
            HasMoveTarget = false;
            CurrentSpeed = Fixed.Zero;
            return;
        }

        var posComp = SimSystem.GetComponent<PositionComponent>(Entity);
        if (posComp == null) return;

        var wp = _waypoints[_currentWaypoint];
        var currentPos = new FixedVector2D(posComp.Position.X, posComp.Position.Z);
        var wpFixed = new FixedVector2D(Fixed.FromFloat(wp.x), Fixed.FromFloat(wp.z));

        var diff = wpFixed - currentPos;
        ulong dx2 = (ulong)((long)diff.X.InternalValue * (long)diff.X.InternalValue);
        ulong dy2 = (ulong)((long)diff.Y.InternalValue * (long)diff.Y.InternalValue);
        uint isqrt = MathInt.Sqrt64(dx2 + dy2);
        Fixed dist = Fixed.Zero.WithInternalValue((int)isqrt);

        Fixed stepDist = EffectiveSpeed().Multiply(Fixed.FromFloat(dt));

        if (dist < stepDist || dist < Fixed.FromFloat(1.0f))
        {
            _currentWaypoint++;
            if (_currentWaypoint >= _waypoints.Count)
            {
                posComp.Position = new FixedVector3D(
                    TargetPos.X, posComp.Position.Y, TargetPos.Y);
                HasMoveTarget = false;
                CurrentSpeed = Fixed.Zero;
            }
            return;
        }

        FixedVector2D dir = new(diff.X / dist, diff.Y / dist);
        Fixed dx = dir.X.Multiply(stepDist);
        Fixed dz = dir.Y.Multiply(stepDist);

        var oldPos2D = new FixedVector2D(posComp.Position.X, posComp.Position.Z);
        posComp.Position = new FixedVector3D(
            posComp.Position.X + dx,
            posComp.Position.Y,
            posComp.Position.Z + dz);
        var newPos2D = new FixedVector2D(posComp.Position.X, posComp.Position.Z);

        // Keep spatial indices (RangeManager, dynamic obstruction layer) in sync with the move.
        SimSystem.NotifyPositionChanged(Entity, oldPos2D, newPos2D);

        CurrentSpeed = Speed;
    }

    /// <summary>经修正值管线的移动速度(科技如 "UnitMotion/WalkSpeed" ×1.15)。
    /// 无 sim 上下文(纯测试)时回退基值。Speed 字段保持基值不动。</summary>
    private Fixed EffectiveSpeed()
    {
        var cm = SimSystem.Sim;
        if (cm == null) return Speed;
        return Fixed.FromFloat(cm.Modifiers.Apply("UnitMotion/WalkSpeed", Speed.ToFloat(), Entity));
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("speed", Speed);
        s.Bool("moving", HasMoveTarget);
        s.NumberFixed("tx", TargetPos.X);
        s.NumberFixed("tz", TargetPos.Y);
    }

    public override void Deserialize(IDeserializer d)
    {
        Speed = d.NumberFixed("speed");
        HasMoveTarget = d.Bool("moving");
        TargetPos = new FixedVector2D(d.NumberFixed("tx"), d.NumberFixed("tz"));
    }

    public void HandleMessage(IMessage message) { }
}

public static class SimSystem
{
    private static ComponentManager? _cm;
    private static ObstructionManager? _obstructions;
    private static RangeManager? _range;
    private static PathfinderComponent? _pathfinder;
    private static WaterManager? _water;
    private static TerritoryManager? _territory;
    public static void Init(ComponentManager cm)
    {
        _cm = cm;
        // 重置所有系统级静态字段：Init 的语义是"开启一个新世界"，不应携带上一个世界的残留。
        // 生产代码 (SimBridge) 紧接着会逐个 Set* 重新填充；测试代码只调 Init 时，这避免了
        // 跨测试的静态状态泄漏（曾导致 WalkSpeed_Tech_AppliesAtMoveAdvance flaky：上一个测试
        // 留下的 _obstructions 让 MoveToPoint 走错误的 FindPath 分支，用旧世界网格算路径）。
        _obstructions = null;
        _range = null;
        _pathfinder = null;
        _water = null;
        _territory = null;
    }
    public static ComponentManager? Sim => _cm;
    public static ObstructionManager? Obstructions => _obstructions;
    public static RangeManager? Range => _range;
    public static PathfinderComponent? Pathfinder => _pathfinder;
    public static WaterManager? Water => _water;
    public static TerritoryManager? Territory => _territory;
    public static void SetObstructionManager(ObstructionManager mgr) => _obstructions = mgr;
    public static void SetRangeManager(RangeManager mgr) => _range = mgr;
    public static void SetPathfinder(PathfinderComponent mgr) => _pathfinder = mgr;
    public static void SetWaterManager(WaterManager mgr) => _water = mgr;
    public static void SetTerritoryManager(TerritoryManager mgr) => _territory = mgr;
    public static T? GetComponent<T>(EntityId entity) where T : class, IComponent =>
        _cm?.QueryInterface<T>(entity);

    /// <summary>Forward a position change to system listeners (RangeManager, ObstructionComponent).
    /// Call after mutating a PositionComponent so spatial indices stay in sync.</summary>
    public static void NotifyPositionChanged(EntityId entity, Maths.FixedVector2D from, Maths.FixedVector2D to)
        => _cm?.NotifyPositionChanged(entity, from, to);
}
