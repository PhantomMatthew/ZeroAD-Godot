using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Pathfinding;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

[Component("UnitMotion", "UnitMotion")]
public sealed class UnitMotion : ComponentBase, IComponentMessageHandler
{
    public Fixed Speed;
    public Fixed CurrentSpeed;
    /// <summary>飞行运动(UnitMotionFlying):MoveToPoint 直线飞抵,不走寻路;
    /// 巡航高度 MaintainFlyingAltitude 维持(gaia 鸟群等装饰单位)。</summary>
    public bool IsFlying;
    public float FlyingHeight = 30f;

    private void MaintainFlyingAltitude()
    {
        var pos = SimSystem.GetComponent<PositionComponent>(Entity);
        if (pos == null) return;
        var alt = Fixed.FromFloat(FlyingHeight);
        if (pos.Position.Y != alt)
            pos.Position = new Maths.FixedVector3D(pos.Position.X, alt, pos.Position.Z);
    }
    public FixedVector2D TargetPos;
    public bool HasMoveTarget;

    /// <summary>原版 UnitMotion/PassabilityClass:通行类名,任意注册表类均可
    /// (default/large/ship/ship-small 等单位寻路类;plane 的 unrestricted 未移植)。
    /// 经 PathfinderConfig 按名解析位掩码/净空,未知名回退 default——船走水路,
    /// 陆军走陆地(此前一律 Default 陆地类——船在陆网格上无解,永远卡岸)。
    /// 装配时由模板写入,随存档序列化;运行期换类走 SetPassabilityClassName。</summary>
    public string PassClassName = "default";

    /// <summary>当前通行类的净空缓存(原版 CCmpUnitMotion m_Clearance;米)。
    /// 派生自 PassClassName × 类注册表——瞬态不序列化,Deserialize 末尾按
    /// PassClassName 重导(上游 CCmpUnitMotion.h:385 同款);ResolvePassClass
    /// 与 SetPassabilityClassName 也会刷新。</summary>
    public Fixed Clearance { get; private set; } = Fixed.FromFraction(4, 5);   // default 类 0.8

    /// <summary>推挤权重(原版 UnitMotion/Weight,"10 is the base value"):
    /// 大者更难被推也推得更狠(象兵 vs 步兵)。装配自模板,随存档序列化。</summary>
    public Fixed Weight = Fixed.FromInt(10);

    /// <summary>瞬时转向角(原版 UnitMotion/InstantTurnAngle,弧度):偏差不超它 →
    /// 边走边瞬对(速度×cos(偏差));超过 → 原地转向至剩 InstantTurnAngle 再走。
    /// 装配自模板(template_unit=1.5/攻城器 0.75/船 10),随存档(v15)。</summary>
    public Fixed InstantTurnAngle = Fixed.FromFraction(3, 2);

    /// <summary>到站后面向目标点(原版 m_FacePointAfterMove 默认 true;
    /// 炮塔持有者在占点/离点时关开——Turretable 接入记 backlog)。</summary>
    public bool FacePointAfterMove = true;

    /// <summary>推挤压力(原版 pushingPressure,u8 语义 0..255):扎堆越深压力越大,
    /// >10 线性减速(地板 1.5m/s),回合末 ×0.6 衰减。UnitSeparation 写,EffectiveSpeed 读。
    /// 瞬态不序列化(冷加载后一回合内由推挤重建——同 waypoints 惯例)。</summary>
    public int PushingPressure;

    /// <summary>模板通行类名 → 位掩码(原版 pathfinder.xml 9 类注册表;
    /// default/large/ship/ship-small/unrestricted 等单位类直接按名查,未知名 → default)。
    /// 顺带刷新净空缓存(类定义即掩码与净空的共同数据源)。</summary>
    private Pathfinding.PassClass ResolvePassClass(PathfinderComponent pf)
    {
        var cls = pf.GetClassByName(PassClassName) ?? pf.DefaultClass;
        Clearance = cls.Clearance;
        return cls.Mask;
    }

    /// <summary>原版 SetPassabilityClassName/SetPassabilityData:换通行类并按类定义
    /// 重导派生缓存(净空)。Formation 成员类上卷与晋升/换模板路径用。</summary>
    public void SetPassabilityClassName(string name)
    {
        PassClassName = name;
        RefreshPassabilityData();
    }

    /// <summary>按 PassClassName 重导净空缓存。无寻路组件(纯测试/读档早期)用内建
    /// 默认注册表(与上游 XML 逐值一致);未知名回退 default 类(与 MaskOf 一致)。</summary>
    private void RefreshPassabilityData()
    {
        var pf = SimSystem.Pathfinder;
        var cls = pf != null
            ? pf.GetClassByName(PassClassName) ?? pf.DefaultClass
            : FallbackConfig.ByName(PassClassName) ?? FallbackConfig.Classes[0];
        Clearance = cls.Clearance;
    }

    private static readonly PathfinderConfig FallbackConfig = PathfinderConfig.Default();

    /// <summary>路标(定点;原版 m_LongPath/m_ShortPath 序列化——读档续走不重复寻路,
    /// 存档-不存档两端演化逐位一致;此前 float 瞬态,读档后单位停摆到下次重请求)。
    /// 瞬态残留:pending ticket(读档丢弃,节流到期重请求)与 stuck/sidestep 看门狗。</summary>
    private readonly List<FixedVector2D> _waypoints = new();
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
    // 节流态本身不序列化(读档后首节流派即重请求,与上游节流语义一致)——
    // 路标本体现在序列化(v16;见字段注释)。
    private const float RepathInterval = 0.3f;     // seconds between full A* solves
    private const float RepathGoalThreshold = 5f;  // metres; bigger shift → re-solve now
    private static readonly long RepathGoalThresholdSqInternal =
        (long)Fixed.FromFloat(RepathGoalThreshold).InternalValue * Fixed.FromFloat(RepathGoalThreshold).InternalValue;
    private float _pathAge;                         // seconds since the last full ComputePath
    private Fixed _lastGoalX, _lastGoalZ;
    private bool _hasLastGoal;

    // --- 异步路径请求(上游 CCmpUnitMotion m_ExpectedPathTicket 语义) ---
    // 超出同回合即答预算(MaxSameTurnPaths=20)的求解入队,结果次回合由
    // PathfinderComponent.HarvestPathResults 投递到 OnPathResult。等待期间继续走
    // 旧路标(上游:pending 时 PerformMove 照旧);旧路标耗尽 → 直线暂行(不站桩)。
    // 瞬态不序列化(同 waypoints 惯例;冷加载后由节流到期自然重请求)。0 = 无待答。
    private uint _pendingPathTicket;

    // --- 阻挡缓释(原版 CCmpUnitMotion C++ 侧防卡 + UnitAI obstructionMitigationAttempted)---
    // A. 不可达目标不穿墙:长程求解为空且直线受阻时,沿直线取最远合法点走到即停
    //    (原版 likelyFailure → FinishOrder 的等价;此前直接直线穿墙)。
    // B. 卡死看门狗:有目标但 StuckWindowSec 窗口位移 < StuckMinProgress(人群夹死)
    //    → 垂直方向试探侧绕点(±3m/±6m 首个直线可达),到侧点后自动重解原目标。
    //    每次全新求解只试一次(_mitigationAttempted 随 full-solve 重置)。
    // 看门狗/侧绕态为瞬态(不序列化;读档后首次卡死重新触发)——路标本体
    // 已序列化(v16),这些只是防卡缓释。
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

        // 飞行运动(UnitMotionFlying.js 简化):直线飞抵目标,不走寻路/不受地形阻挡;
        // 高度维持固定巡航高(鸟群装饰;原版有起飞/降落速率,装饰单位不建模)。
        if (IsFlying)
        {
            _waypoints.Clear();
            _currentWaypoint = 0;
            _waypoints.Add(target);
            MaintainFlyingAltitude();
            return;
        }

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
                _waypoints.Add(target);
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
            _waypoints.Add(target);
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
            var pc = ResolvePassClass(pathfinder);
            if (pathfinder.RequestLongPath(Entity, start, goal, pc, out var path, out uint ticket))
            {
                // 同回合即答(缓存命中/预算内):立即安装路径(旧同步行为)。
                _pendingPathTicket = 0;
                AdoptPath(pathfinder, path, start, target, pc);
            }
            else
            {
                // 异步 pending:保留旧路标继续走(上游 pending 语义——此前这里清空,
                // 单位会在结果到达前站桩);旧路标已耗尽 → 直线暂行到目标。
                _pendingPathTicket = ticket;
                if (_currentWaypoint >= _waypoints.Count)
                    _waypoints.Add(target);
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
                _waypoints.Add(new FixedVector2D(Fixed.FromFloat(obstructions.GridToWorld(px)),
                    Fixed.FromFloat(obstructions.GridToWorld(pz))));
            _waypoints.Add(target);
        }
        else
#pragma warning restore CS0618
        {
            _waypoints.Add(target);
        }
    }

    /// <summary>安装一次求解结果(即答与异步投递共用)。空路径的缓释 A
    /// (钳到最远可达点/不动)与原内联路径逐字一致。</summary>
    private void AdoptPath(PathfinderComponent pathfinder, WaypointPath path,
        FixedVector2D start, FixedVector2D target, PassClass pc)
    {
        _waypoints.Clear();
        _currentWaypoint = 0;
        foreach (var wp in path.Waypoints)
            _waypoints.Add(new FixedVector2D(wp.X, wp.Z));
        if (_waypoints.Count == 0)
        {
            // 长程求解为空:起点≈终点 → 直线即达;否则目标不可达——不直线穿墙,
            // 沿直线钳到最远合法点(缓释 A);完全堵死 → 不动(订单随后 FinishOrder)。
            float dxs = target.X.ToFloat() - start.X.ToFloat();
            float dzs = target.Y.ToFloat() - start.Y.ToFloat();
            if (dxs * dxs + dzs * dzs < 1f)
            {
                _waypoints.Add(target);
                return;
            }
            if (TryClampToReachable(pathfinder, start, target, pc, out var clamped))
            {
                _waypoints.Add(clamped);
                TargetPos = clamped;   // 到点判定按可达点(原目标不可达)
            }
            else
            {
                HasMoveTarget = false;
            }
        }
    }

    /// <summary>异步路径结果投递(PathfinderComponent.HarvestPathResults → 此处)。
    /// 上游 CCmpUnitMotion::PathResult:ticket 过期(期间又有新请求)即丢弃。</summary>
    public void OnPathResult(uint ticket, WaypointPath path)
    {
        if (ticket != _pendingPathTicket || ticket == 0) return;   // 过期结果
        _pendingPathTicket = 0;
        if (!HasMoveTarget) return;   // Stop 过——结果作废
        var pathfinder = SimSystem.Pathfinder;
        var posComp = SimSystem.GetComponent<PositionComponent>(Entity);
        if (pathfinder == null || posComp == null) return;
        var start = new FixedVector2D(posComp.Position.X, posComp.Position.Z);
        AdoptPath(pathfinder, path, start, TargetPos, ResolvePassClass(pathfinder));
    }

    public void Stop()
    {
        HasMoveTarget = false;
        CurrentSpeed = Fixed.Zero;
        _waypoints.Clear();
        _pendingPathTicket = 0;   // 在途结果到时自然作废(ticket 校验)
        // Drop the cached goal so the next MoveToPoint always solves a fresh path (a Stop
        // means the caller deliberately cancelled movement, not a chase tick).
        _hasLastGoal = false;
        _pathAge = 0f;
    }

    public void Tick(float dt)
    {
        _pathAge += dt;
        if (IsFlying) MaintainFlyingAltitude();
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

        var wpFixed = _waypoints[_currentWaypoint];   // 定点路标(不再 float 往返)
        var currentPos = new FixedVector2D(posComp.Position.X, posComp.Position.Z);

        var diff = wpFixed - currentPos;
        ulong dx2 = (ulong)((long)diff.X.InternalValue * (long)diff.X.InternalValue);
        ulong dy2 = (ulong)((long)diff.Y.InternalValue * (long)diff.Y.InternalValue);
        uint isqrt = MathInt.Sqrt64(dx2 + dy2);
        Fixed dist = Fixed.Zero.WithInternalValue((int)isqrt);

        // ── 转向物理(原版 CCmpUnitMotion::PerformMove L1285-1323 逐字)──
        // turnRate 取自 Position/TurnRate;angle 即 Position.Rotation.Y(sim 态)。
        // 偏差 > InstantTurnAngle:原地转向(本拍不走),转完才走剩余时间;
        // ≤:瞬对目标方位,速度 ×cos(偏差)(小弯减速)。
        Fixed timeLeft = Fixed.FromFloat(dt);
        Fixed speedScale = Fixed.FromInt(1);
        Fixed turnRate = posComp.TurnRate;
        if (turnRate > Fixed.Zero && dist > Fixed.Zero)
        {
            Fixed targetAngle = Trig.Atan2Approx(diff.X, diff.Y);
            Fixed angle = posComp.Rotation.Y;
            Fixed angleDiff = angle - targetAngle;
            Fixed absoluteAngleDiff = angleDiff.Absolute;
            var pi = Fixed.Pi;
            if (absoluteAngleDiff > pi)
                absoluteAngleDiff = pi * 2 - absoluteAngleDiff;

            if (absoluteAngleDiff > InstantTurnAngle)
            {
                // 大角度:停走,原地转。
                speedScale = Fixed.Zero;
                Fixed maxRotation = turnRate.Multiply(timeLeft);
                int direction = (Fixed.Zero < angleDiff && angleDiff <= pi) || angleDiff < -pi ? -1 : 1;
                if (absoluteAngleDiff - InstantTurnAngle > maxRotation)
                {
                    // 本拍转不完:转 maxRotation,不走。
                    angle += maxRotation * direction;
                    if (angle * direction > pi)
                        angle -= pi * 2 * direction;
                    posComp.Rotation = new FixedVector3D(posComp.Rotation.X, angle, posComp.Rotation.Z);
                    return;   // 转向不占空间索引(仅 yaw),无需通知
                }
                // 转完:对准后走剩余时间(原版 timeLeft 折算逐字)。
                angle = targetAngle;
                posComp.Rotation = new FixedVector3D(posComp.Rotation.X, angle, posComp.Rotation.Z);
                Fixed spent = maxRotation - absoluteAngleDiff + InstantTurnAngle;
                timeLeft = spent < maxRotation ? spent / turnRate : maxRotation / turnRate;
            }
            else
            {
                // 小角度:边走边对正,速度 ×cos(偏差)。
                Trig.SinCosApprox(angleDiff, out _, out Fixed cos);
                speedScale = cos < Fixed.Zero ? Fixed.Zero : cos;
                posComp.Rotation = new FixedVector3D(posComp.Rotation.X, targetAngle, posComp.Rotation.Z);
            }
        }

        Fixed stepDist = EffectiveSpeed().Multiply(speedScale).Multiply(timeLeft);

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
                float gdx = TargetPos.X.ToFloat() - lastWp.X.ToFloat();
                float gdz = TargetPos.Y.ToFloat() - lastWp.Y.ToFloat();
                bool reachedGoal = gdx * gdx + gdz * gdz <= 1.5f * 1.5f;
                posComp.Position = reachedGoal
                    ? new FixedVector3D(TargetPos.X, posComp.Position.Y, TargetPos.Y)
                    : new FixedVector3D(lastWp.X, posComp.Position.Y, lastWp.Y);
                HasMoveTarget = false;
                CurrentSpeed = Fixed.Zero;
                // 到站面向目标点(原版 StopMoving → FaceTowardsPointFromPos;m_FacePointAfterMove)。
                if (FacePointAfterMove && reachedGoal)
                {
                    var faceDiff = new FixedVector2D(
                        TargetPos.X - posComp.Position.X, TargetPos.Y - posComp.Position.Z);
                    if (!faceDiff.IsZero)
                    {
                        var faceAngle = Trig.Atan2Approx(faceDiff.X, faceDiff.Y);
                        posComp.Rotation = new FixedVector3D(
                            posComp.Rotation.X, faceAngle, posComp.Rotation.Z);
                    }
                }
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

        // 压力衰减(原版 PostMove 后每回合 ×0.6;整数 ×3/5 截断)。
        if (PushingPressure > 0) PushingPressure = PushingPressure * 3 / 5;

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
            _waypoints.Add(c);
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
        var baseSpeed = cm == null ? Speed
            : Fixed.FromFloat(cm.Modifiers.Apply("UnitMotion/WalkSpeed", Speed.ToFloat(), Entity));
        return ApplyPushingPressure(baseSpeed);
    }

    /// <summary>压力减速(原版 CCmpUnitMotion::PerformMove L1236-1255 逐字):
    /// pressure ≤10 不减速;以上线性压到地板 min(模板速, 1.5m/s)——
    /// maxPressure = 255−10−80 = 165,slowdown = 165 − min(165, max(0, p−10))。</summary>
    private Fixed ApplyPushingPressure(Fixed basicSpeed)
    {
        if (PushingPressure <= 0) return basicSpeed;
        const int pressureMinThreshold = 10;
        const int maxPressure = 255 - pressureMinThreshold - 80;   // 165
        int over = PushingPressure - pressureMinThreshold;
        if (over < 0) over = 0;
        int slowdown = maxPressure - (over > maxPressure ? maxPressure : over);
        var slowed = basicSpeed.Multiply(Fixed.FromInt(slowdown) / Fixed.FromInt(maxPressure));
        var floor = Speed < Fixed.FromFraction(3, 2) ? Speed : Fixed.FromFraction(3, 2);
        return slowed > floor ? slowed : floor;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("speed", Speed);
        s.Bool("moving", HasMoveTarget);
        s.NumberFixed("tx", TargetPos.X);
        s.NumberFixed("tz", TargetPos.Y);
        s.StringASCII("passclass", PassClassName);
        s.NumberFixed("weight", Weight);   // 存档 v14
        s.NumberFixed("ita", InstantTurnAngle);   // 存档 v15
        s.Bool("fpam", FacePointAfterMove);
        // 路标(存档 v16):原版 m_LongPath/m_ShortPath 骑缝——读档续走不重复寻路。
        s.NumberI32("wpCount", _waypoints.Count);
        foreach (var wp in _waypoints)
        {
            s.NumberFixed("wpx", wp.X);
            s.NumberFixed("wpz", wp.Y);
        }
        s.NumberI32("wpCur", _currentWaypoint);
    }

    public override void Deserialize(IDeserializer d)
    {
        Speed = d.NumberFixed("speed");
        HasMoveTarget = d.Bool("moving");
        TargetPos = new FixedVector2D(d.NumberFixed("tx"), d.NumberFixed("tz"));
        PassClassName = d.StringASCII("passclass");
        Weight = d.NumberFixed("weight");
        InstantTurnAngle = d.NumberFixed("ita");
        FacePointAfterMove = d.Bool("fpam");
        _waypoints.Clear();
        int wpCount = d.NumberI32("wpCount");
        for (int i = 0; i < wpCount; i++)
            _waypoints.Add(new FixedVector2D(d.NumberFixed("wpx"), d.NumberFixed("wpz")));
        _currentWaypoint = d.NumberI32("wpCur");
        if (_currentWaypoint > _waypoints.Count) _currentWaypoint = _waypoints.Count;
        // 在途 ticket 读档作废(瞬态;节流到期自然重请求)。
        _pendingPathTicket = 0;
        // 净空缓存按 PassClassName 重导(瞬态不序列化;上游 Deserialize→SetPassabilityData 同款)。
        RefreshPassabilityData();
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
        // 易物价差归零(全局静态经济状态;同上——新世界不带旧账)。
        BarterSystem.Reset();
        // 推挤 initialPos 驻留表归零(跨世界的实体 id 可能复用,旧位置会污染首回合)。
        UnitSeparation.Reset();
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
