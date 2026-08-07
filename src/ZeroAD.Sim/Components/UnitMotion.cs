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

    /// <summary>原版 UnitMotion/PassabilityClass("default"/"ship";plane 的 unrestricted
    /// 未移植)。决定寻路/阻挡缓释用哪套通行网格:船走水路,陆军走陆地(此前一律
    /// Default 陆地类——船在陆网格上无解,永远卡岸)。装配时由模板写入,随存档序列化。</summary>
    public string PassClassName = "default";

    /// <summary>当前单位的通行类掩码(ship → Ship 水类;其余 → Default 陆地类)。</summary>
    private Pathfinding.PassClass ResolvePassClass(PathfinderComponent pf) =>
        PassClassName == "ship" ? pf.ShipClass.Mask : pf.DefaultClass.Mask;

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

    // --- 阻挡缓释(原版 CCmpUnitMotion C++ 侧防卡 + UnitAI obstructionMitigationAttempted)---
    // A. 不可达目标不穿墙:长程求解为空且直线受阻时,沿直线取最远合法点走到即停
    //    (原版 likelyFailure → FinishOrder 的等价;此前直接直线穿墙)。
    // B. 卡死看门狗:有目标但 StuckWindowSec 窗口位移 < StuckMinProgress(人群夹死)
    //    → 垂直方向试探侧绕点(±3m/±6m 首个直线可达),到侧点后自动重解原目标。
    //    每次全新求解只试一次(_mitigationAttempted 随 full-solve 重置)。
    // 全部为瞬态:不序列化,不进 OOS 哈希(同 waypoints 惯例)。
    private const float StuckWindowSec = 0.6f;
    private const float StuckMinProgress = 0.05f;
    private float _stuckTimer;
    private FixedVector2D _stuckAnchor;
    private bool _stuckAnchorValid;
    private bool _mitigationAttempted;
    private bool _sidestepping;                     // 正在走侧绕点(到点 → 重解原目标)

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
        // 全新求解 = 新的移动尝试:阻挡缓释重新获得一次机会。
        _mitigationAttempted = false;
        _sidestepping = false;

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
            var path = pathfinder.ComputePath(start, goal, ResolvePassClass(pathfinder));
            // WaypointPath.Waypoints is stored start→goal; consume front-to-back (matching the
            // existing _waypoints contract). Each waypoint is world-space Fixed → float.
            foreach (var wp in path.Waypoints)
                _waypoints.Add((wp.X.ToFloat(), wp.Z.ToFloat()));
            if (_waypoints.Count == 0)
            {
                // 长程求解为空:起点≈终点 → 直线即达;否则目标不可达——不直线穿墙,
                // 沿直线钳到最远合法点(缓释 A);完全堵死 → 不动(订单随后 FinishOrder)。
                float dxs = target.X.ToFloat() - start.X.ToFloat();
                float dzs = target.Y.ToFloat() - start.Y.ToFloat();
                if (dxs * dxs + dzs * dzs < 1f)
                {
                    _waypoints.Add((target.X.ToFloat(), target.Y.ToFloat()));
                    return;
                }
                if (TryClampToReachable(pathfinder, start, target, ResolvePassClass(pathfinder), out var clamped))
                {
                    _waypoints.Add((clamped.X.ToFloat(), clamped.Y.ToFloat()));
                    TargetPos = clamped;   // 到点判定按可达点(原目标不可达)
                }
                else
                {
                    HasMoveTarget = false;
                }
            }
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
            // 侧绕点走完 → 自动重解原目标(缓释 B 的续程;_mitigationAttempted 保持,
            // 不再二次侧绕)。
            if (HasMoveTarget && _sidestepping)
            {
                _sidestepping = false;
                var resume = TargetPos;
                MoveToPoint(resume);
                _mitigationAttempted = true;   // MoveToPoint 的 full-solve 会重置,补回
                return;
            }
            HasMoveTarget = false;
            CurrentSpeed = Fixed.Zero;
            _stuckTimer = 0;
            _stuckAnchorValid = false;
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
                // 侧绕点到站:不收官,交给下一拍顶部的 _sidestepping 续程分支重解原目标
                // (此前靠"瞬移到 TargetPos"的 bug 掩盖了这一步)。
                if (_sidestepping) return;
                // 到达判定:路径末端≈原始目标(可达;末端路标即目标 navcell 中心,
                // 差 ≤0.71m)→ 精确到点;否则(不可达目标的"最近可达点"路径)→ 停在
                // 路径末端。此前无条件瞬移到原始目标:最近可达路径走完后单位穿水/穿墙
                // 瞬移(陆军直接渡过水带)。
                var lastWp = _waypoints[_waypoints.Count - 1];
                float gdx = TargetPos.X.ToFloat() - lastWp.x;
                float gdz = TargetPos.Y.ToFloat() - lastWp.z;
                bool reachedGoal = gdx * gdx + gdz * gdz <= 1.5f * 1.5f;
                posComp.Position = reachedGoal
                    ? new FixedVector3D(TargetPos.X, posComp.Position.Y, TargetPos.Y)
                    : new FixedVector3D(Fixed.FromFloat(lastWp.x), posComp.Position.Y,
                        Fixed.FromFloat(lastWp.z));
                HasMoveTarget = false;
                CurrentSpeed = Fixed.Zero;
            }
            return;
        }

        FixedVector2D dir = new(diff.X / dist, diff.Y / dist);
        Fixed dx = dir.X.Multiply(stepDist);
        Fixed dz = dir.Y.Multiply(stepDist);

        var oldPos2D = new FixedVector2D(posComp.Position.X, posComp.Position.Z);
        // Y 贴地(原版 GetHeightOffset 语义:单位随地形起伏;Attack 高度差判定依赖此)。
        Fixed newY = SimSystem.TerrainHeight(posComp.Position.X + dx, posComp.Position.Z + dz);
        posComp.Position = new FixedVector3D(
            posComp.Position.X + dx,
            newY,
            posComp.Position.Z + dz);
        var newPos2D = new FixedVector2D(posComp.Position.X, posComp.Position.Z);

        // Keep spatial indices (RangeManager, dynamic obstruction layer) in sync with the move.
        SimSystem.NotifyPositionChanged(Entity, oldPos2D, newPos2D);

        CurrentSpeed = Speed;

        // 卡死看门狗(缓释 B):窗口实际位移不足 → 一次侧绕。
        _stuckTimer += dt;
        if (!_stuckAnchorValid) { _stuckAnchor = newPos2D; _stuckAnchorValid = true; }
        if (_stuckTimer >= StuckWindowSec)
        {
            float disp = (newPos2D - _stuckAnchor).Length().ToFloat();
            _stuckAnchor = newPos2D;
            _stuckTimer = 0;
            if (disp < StuckMinProgress && !_mitigationAttempted && !_sidestepping)
                TrySidestep(posComp);
        }
    }

    /// <summary>缓释 A:直线受阻时,从远到近采样 12 档取首个 CheckMovement 可达点
    /// (走到即停,不再穿墙)。直线本就可走 → 原目标;无寻路(纯测试)→ 原目标。
    /// 通行类随单位(船按水类钳到岸线,陆军按陆类钳到水线)。</summary>
    private static bool TryClampToReachable(PathfinderComponent? pf, FixedVector2D from,
        FixedVector2D to, Pathfinding.PassClass pc, out FixedVector2D result)
    {
        result = to;
        if (pf == null) return true;
        if (pf.CheckMovement(from, to, pc)) return true;
        for (int i = 11; i >= 1; i--)
        {
            float t = i / 12f;
            var candidate = new FixedVector2D(
                from.X + (to.X - from.X).Multiply(Fixed.FromFloat(t)),
                from.Y + (to.Y - from.Y).Multiply(Fixed.FromFloat(t)));
            if (pf.CheckMovement(from, candidate, pc))
            {
                result = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>缓释 B:垂直于目标方向试探侧绕点(±3m/±6m,确定性顺序),
    /// 首个直线可达者替换路标;到点后由 Tick 的 _sidestepping 分支重解原目标。</summary>
    private void TrySidestep(PositionComponent posComp)
    {
        _mitigationAttempted = true;
        var pf = SimSystem.Pathfinder;
        if (pf == null) return;
        var from = new FixedVector2D(posComp.Position.X, posComp.Position.Z);
        float fx = from.X.ToFloat(), fz = from.Y.ToFloat();
        float dx = TargetPos.X.ToFloat() - fx;
        float dz = TargetPos.Y.ToFloat() - fz;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 0.01f) return;
        float px = -dz / len, pz = dx / len;   // 垂直单位向量
        var pc = ResolvePassClass(pf);
        foreach (float side in new[] { 3f, -3f, 6f, -6f })
        {
            var c = new FixedVector2D(
                Fixed.FromFloat(fx + px * side), Fixed.FromFloat(fz + pz * side));
            if (!pf.CheckMovement(from, c, pc)) continue;
            _waypoints.Clear();
            _waypoints.Add((c.X.ToFloat(), c.Y.ToFloat()));
            _currentWaypoint = 0;
            _sidestepping = true;
            return;
        }
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
        s.StringASCII("passclass", PassClassName);
    }

    public override void Deserialize(IDeserializer d)
    {
        Speed = d.NumberFixed("speed");
        HasMoveTarget = d.Bool("moving");
        TargetPos = new FixedVector2D(d.NumberFixed("tx"), d.NumberFixed("tz"));
        PassClassName = d.StringASCII("passclass");
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
    private static Net.NetTurnManager? _net;
    private static TerrainComponent? _terrain;
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
        _net = null;
        _terrain = null;
    }
    public static ComponentManager? Sim => _cm;
    public static ObstructionManager? Obstructions => _obstructions;
    public static RangeManager? Range => _range;
    public static PathfinderComponent? Pathfinder => _pathfinder;
    public static WaterManager? Water => _water;
    public static TerritoryManager? Territory => _territory;
    /// <summary>回合管理器(地图脚本下 gaia 命令等内核侧命令通道;InitWorld 注入)。</summary>
    public static Net.NetTurnManager? Net => _net;
    /// <summary>地形组件(高度网格;Attack 高度差/单位 Y 贴地用)。</summary>
    public static TerrainComponent? Terrain => _terrain;
    /// <summary>地形高度查询的静态便捷口(无地形组件 → 0)。</summary>
    public static Fixed TerrainHeight(Fixed x, Fixed z) => _terrain?.GetHeight(x, z) ?? Fixed.Zero;
    public static void SetObstructionManager(ObstructionManager mgr) => _obstructions = mgr;
    public static void SetRangeManager(RangeManager mgr) => _range = mgr;
    public static void SetPathfinder(PathfinderComponent mgr) => _pathfinder = mgr;
    public static void SetWaterManager(WaterManager mgr) => _water = mgr;
    public static void SetTerritoryManager(TerritoryManager mgr) => _territory = mgr;
    public static void SetNet(Net.NetTurnManager net) => _net = net;
    public static void SetTerrainComponent(TerrainComponent terrain) => _terrain = terrain;
    public static T? GetComponent<T>(EntityId entity) where T : class, IComponent =>
        _cm?.QueryInterface<T>(entity);

    /// <summary>Forward a position change to system listeners (RangeManager, ObstructionComponent).
    /// Call after mutating a PositionComponent so spatial indices stay in sync.</summary>
    public static void NotifyPositionChanged(EntityId entity, Maths.FixedVector2D from, Maths.FixedVector2D to)
        => _cm?.NotifyPositionChanged(entity, from, to);
}
