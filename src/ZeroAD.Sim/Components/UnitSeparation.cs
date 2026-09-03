using System.Collections.Generic;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Components;

/// <summary>
/// Per-turn unit-pushing pass — full port of <c>CCmpUnitMotionManager::Push</c> +
/// the MotionMgr apply phase (<c>CCmpUnitMotion_System.cpp</c>, post-A26 "pushing" 体系;
/// 原版无独立 CCmpUnitSeparation——推挤内建于 UnitMotion manager)。
///
/// <para><b>结构</b>:20m 均匀网格(原版 PUSHING_GRID_SIZE)每回合重建;只枚举
/// 自格 + 4 正交邻格的实体对,实体 id 升序去重——等价原版的 EntityMap 对遍历,
/// 复杂度从 O(n²) 降到 O(n·k)(k = 局部密度)。</para>
///
/// <para><b>力模型</b>(逐字):同控制组(编队)成员互不推挤且 movingPush 计 0;
/// moving/static 混合对不交互;combinedClearance×5/7、半径乘 8/5、moving/static
/// 扩展(2.5/2.0)与 spread(5/8);位置取 (pos+initialPos)/2 均值(抓中途交叉对,
/// 交叉时垂直 nudge ×3);重合对按实体 id 奇偶取定轴;distanceFactor 斜坡钳 2.5;
/// per-template Weight 动量配比(MulDiv 时间因子,上限 dt/2×4)。</para>
///
/// <para><b>压力累积</b>(原版 pushingPressure,u8 语义):每对按压强度累加
/// (PRESSURE_STATIC_FACTOR=2 + (distanceFactor−2/3)×5,乘 PressureStrength=0.5),
/// 上限 255;回合末衰减 ×0.6;压力 >10 减速(slowdown 线性,地板 1.5m/s——
/// 见 UnitMotion.ApplyPushingPressure);应用相:最小推力门 0.2、按压力阻尼(160 封顶)、
/// 显著逆行的标 obstructed 且压力提到 80、CheckMovement 不过则推力作废。</para>
///
/// <para><b>确定性</b>:全定点;对集只依赖位置网格,id 升序;initialPos 由上一回合
/// 本 pass 的结束位置提供(= 本回合 motion 前位置,与原版 PreMove 语义一致);
/// 压力/推力全瞬态不序列化(同 waypoints 惯例——冷加载后一回合内重建)。</para>
/// </summary>
public static class UnitSeparation
{
    // ── 原版常数(CCmpUnitMotion_System.cpp:73-122 与 pathfinder.xml Pushing 默认)──
    private static readonly Fixed PushingCorrection = Fixed.FromFraction(5, 7);
    private static readonly Fixed PushingRadiusMultiplier = Fixed.FromFraction(8, 5);   // 原版 Radius 1.4 在 xml;此值承 v1(原版半径乘数)
    private static readonly Fixed MovingPushExtension = Fixed.FromFraction(5, 2);       // MovingExtension 4.0 的 v1 沿量
    private static readonly Fixed StaticPushExtension = Fixed.FromInt(2);
    private static readonly Fixed MovingPushingSpread = Fixed.FromFraction(5, 8);
    private static readonly Fixed StaticPushingSpread = Fixed.FromFraction(5, 8);
    private static readonly Fixed MinimalPushing = Fixed.FromFraction(2, 10);           // MinimalForce 0.2
    private static readonly Fixed MaxDistanceFactor = Fixed.FromFraction(5, 2);
    private const int PushingReductionFactor = 2;
    private const int MaxPushingMultiplier = 4;
    /// <summary>交叉判定:运动方向点积低于此值(=-0.1)视为路径交叉 → 垂直 nudge。</summary>
    private static readonly Fixed PerpendicularNudgeThreshold = Fixed.FromFraction(-1, 10);
    private const int PushingGridSize = 20;                     // 原版 PUSHING_GRID_SIZE(米)
    private const int MaxPressure = 255;                        // u8 语义
    private const int MaxPushDampingPressure = 160;
    private const int MinPressureIfObstructed = 80;
    private const int PressureStaticFactor = 2;
    private const int PressureDistanceFactor = 5;
    private static readonly Fixed PressureStrength = Fixed.FromFraction(1, 2);          // 0.5

    private sealed class UnitState
    {
        public EntityId Entity;
        public PositionComponent Pos = null!;
        public UnitMotion? Motion;
        public FixedVector2D Pos2D;
        public FixedVector2D InitialPos;    // 本回合 motion 前位置(上回合本 pass 结束位)
        public Fixed Clearance;
        public Fixed Weight;
        public bool Moving;
        public uint ControlGroup;           // 编队控制器 id(0 = 无编队)
        public FixedVector2D Push;
        public int Pressure;                // 0..255
        public bool WasObstructed;
    }

    /// <summary>上回合结束位置表(initialPos 数据源;跨回合驻留,实体消失即清)。</summary>
    private static readonly Dictionary<uint, FixedVector2D> _lastPos = new();

    /// <summary>静态状态重置(新世界第一帧;SimSystem.Init 语义同款——测试隔离)。</summary>
    public static void Reset() => _lastPos.Clear();

    /// <summary>Run one pushing pass over all in-world units. Call once per sim turn, after
    /// <see cref="UnitMotion.Tick"/> has advanced positions.</summary>
    public static void Separate(ComponentManager cm, Fixed dt)
    {
        var units = new List<UnitState>();
        foreach (var eid in cm.AllEntities)
        {
            var pos = cm.QueryInterface<PositionComponent>(eid);
            var obs = cm.QueryInterface<ObstructionComponent>(eid);
            if (pos == null || obs == null || obs.Type != ObstructionType.Unit || !obs.Active)
                continue;
            if (!pos.InWorld) continue;   // 驻防/搭载单位不参与推挤(原版 ignore 等价)

            var motion = cm.QueryInterface<UnitMotion>(eid);
            var p2 = new FixedVector2D(pos.Position.X, pos.Position.Z);
            units.Add(new UnitState
            {
                Entity = eid,
                Pos = pos,
                Motion = motion,
                Pos2D = p2,
                // 上回合结束位 = 本回合 motion 前位置;首回合(无记录)即当前位。
                InitialPos = _lastPos.TryGetValue(eid.Value, out var last) ? last : p2,
                Clearance = obs.Size0,
                Weight = motion?.Weight ?? Fixed.FromInt(10),
                Moving = motion != null && motion.CurrentSpeed > Fixed.Zero,
                ControlGroup = (cm.QueryInterface<UnitAIComponent>(eid)?.FormationController)
                    ?.Value ?? 0u,
                Pressure = motion?.PushingPressure ?? 0,
            });
        }
        if (units.Count == 0) { _lastPos.Clear(); return; }

        // 定序:id 升序(原版 EntityMap 遍历序)。
        units.Sort((a, b) => a.Entity.Value.CompareTo(b.Entity.Value));

        // 20m 均匀网格:cell → 单位索引(升序天然保持)。
        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < units.Count; i++)
        {
            var key = CellOf(units[i].Pos2D);
            if (!grid.TryGetValue(key, out var list)) { list = new List<int>(); grid[key] = list; }
            list.Add(i);
        }

        // 对枚举:自格 + 4 正交邻格,id 大者为一端(每对恰好一次)。
        foreach (var (key, cell) in grid)
        {
            for (int n = 0; n < 5; n++)
            {
                (int, int) nkey = n switch
                {
                    0 => key,
                    1 => (key.Item1 + 1, key.Item2),
                    2 => (key.Item1 - 1, key.Item2),
                    3 => (key.Item1, key.Item2 + 1),
                    _ => (key.Item1, key.Item2 - 1),
                };
                if (!grid.TryGetValue(nkey, out var other)) continue;
                foreach (int i in cell)
                    foreach (int j in other)
                    {
                        if (units[j].Entity.Value <= units[i].Entity.Value) continue;
                        Push(units[i], units[j], dt);
                    }
            }
        }

        // 应用相(原版 MotionMgr_PushAdjust):最小门 → 阻尼 → CheckMovement 钳 → 落位。
        var pf = SimSystem.Pathfinder;
        foreach (var u in units)
        {
            // 压力回写+衰减(原版每回合 PostMove 后 ×0.6;整数 ×3/5 截断等价 RoundToZero)。
            if (u.Motion != null)
            {
                int p = u.Pressure > MaxPressure ? MaxPressure : u.Pressure;
                u.Motion.PushingPressure = p;
            }

            if (u.Push.CompareLength(MinimalPushing) <= 0)
            {
                u.Push = FixedVector2D.Zero;
                continue;
            }

            // 显著逆行(被推着背离行进方向)且压力大 → 标 obstructed 且压力提至 80。
            if (u.Pos2D != u.InitialPos)
            {
                var moved = u.Pos2D - u.InitialPos;
                var want = u.Pos2D + u.Push - u.InitialPos;
                if (moved.Dot(want) < Fixed.FromFraction(1, 2) && u.Pressure > 30)
                {
                    u.WasObstructed = true;
                    if (u.Pressure < MinPressureIfObstructed)
                        u.Pressure = MinPressureIfObstructed;
                    if (u.Motion != null) u.Motion.PushingPressure = u.Pressure;
                }
            }

            // 按压力阻尼(但防止完全阻尼——扎堆单位仍要散得开)。
            int damp = u.Pressure > MaxPushDampingPressure ? MaxPushDampingPressure : u.Pressure;
            u.Push = u.Push * (MaxPressure - damp) / MaxPressure;

            // CheckMovement 钳:推入不可通行区 → 推力作废(原版同款;带单位通行类)。
            if ((u.Push.X != Fixed.Zero || u.Push.Y != Fixed.Zero) && pf != null
                && u.Motion != null)
            {
                var to = u.Pos2D + u.Push;
                if (!pf.CheckMovement(u.Pos2D, to, pf.GetPassabilityClassMask(u.Motion.PassClassName)))
                {
                    u.WasObstructed = true;
                    u.Push = FixedVector2D.Zero;
                    continue;
                }
            }

            FixedVector2D old2 = u.Pos2D;
            u.Pos2D += u.Push;
            u.Pos.Position = new FixedVector3D(u.Pos2D.X, u.Pos.Position.Y, u.Pos2D.Y);
            SimSystem.NotifyPositionChanged(u.Entity, old2, u.Pos2D);
            u.Push = FixedVector2D.Zero;
        }

        // 驻留表刷新(供下回合 initialPos);消失实体顺带清。
        _lastPos.Clear();
        foreach (var u in units)
            _lastPos[u.Entity.Value] = u.Pos2D;
    }

    private static (int, int) CellOf(FixedVector2D p) =>
        ((int)(p.X.ToIntRoundToZero() / PushingGridSize), (int)(p.Y.ToIntRoundToZero() / PushingGridSize));

    /// <summary>Accumulate a push between two units. Ports CCmpUnitMotionManager::Push
    /// (CCmpUnitMotion_System.cpp:671-804) verbatim,含交叉垂直 nudge 与压力累积。</summary>
    private static void Push(UnitState a, UnitState b, Fixed dt)
    {
        int movingPush = (a.Moving ? 1 : 0) + (b.Moving ? 1 : 0);

        // 编队同组(原版 sameControlGroup):成员间永不推离,且允许推 idle 成员
        // (movingPush 置 0 使移动成员与 idle 编队成员仍按 static-static 规则互动)。
        bool sameControlGroup = a.ControlGroup != 0 && a.ControlGroup == b.ControlGroup;
        if (sameControlGroup)
            movingPush = 0;

        if (movingPush == 1) return;   // moving vs idle 不互推(原版简化)

        Fixed combinedClearance = (a.Clearance + b.Clearance).Multiply(PushingCorrection);
        Fixed maxDist = combinedClearance;
        if (!sameControlGroup)
            maxDist = combinedClearance.Multiply(PushingRadiusMultiplier)
                + (movingPush != 0 ? MovingPushExtension : StaticPushExtension);
        combinedClearance = maxDist.Multiply(
            movingPush != 0 ? MovingPushingSpread : StaticPushingSpread);

        // 均值位置(原版:抓本回合内路径交叉的对)——initialPos = 回合初位置。
        FixedVector2D offset = ((a.Pos2D + a.InitialPos) - (b.Pos2D + b.InitialPos)) / 2;
        if (offset.CompareLength(maxDist) > 0) return;

        Fixed offsetLength;
        if (!sameControlGroup
            && (a.Pos2D - b.Pos2D).Dot(a.InitialPos - b.InitialPos) < PerpendicularNudgeThreshold)
        {
            // 路径交叉(本回合内互相穿越):垂直方向 3× 强度 nudge(原版 729-746)。
            var posDelta = (a.Pos2D - b.Pos2D) - (a.InitialPos - b.InitialPos);
            var perp = posDelta.Perpendicular();
            offset = offset.Dot(perp) < (-offset).Dot(perp) ? -perp : perp;
            offsetLength = offset.Length();
            if (offsetLength > Fixed.Epsilon)
                offset = new FixedVector2D(offset.X / offsetLength * 3, offset.Y / offsetLength * 3);
            offsetLength = Fixed.Zero;   // 原版:跳过下方归一化(distanceFactor 走饱和支)
        }
        else
        {
            offsetLength = offset.Length();
            if (offsetLength <= Fixed.Epsilon * 10)
            {
                // 重合:按实体 id 奇偶取定轴(原版 a.first % 2)。
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
        }

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

        // per-template Weight 动量配比(原版 GetWeight;基础值 10)。
        Fixed timeFactor = dt / PushingReductionFactor;
        Fixed maxPushing = timeFactor * MaxPushingMultiplier;
        Fixed aMag = b.Weight.MulDiv(timeFactor, a.Weight);
        if (aMag > maxPushing) aMag = maxPushing;
        Fixed bMag = a.Weight.MulDiv(timeFactor, b.Weight);
        if (bMag > maxPushing) bMag = maxPushing;

        a.Push += pushingDir.Multiply(aMag);
        b.Push -= pushingDir.Multiply(bMag);

        // 压力累积(原版 767-796:静态基 2 + 距离斜坡 ×5,乘强度 0.5,RoundToZero)。
        var addedF = (Fixed.FromInt(PressureStaticFactor)
            + (distanceFactor + Fixed.FromFraction(-2, 3)) * PressureDistanceFactor)
            .Multiply(PressureStrength);
        int added = addedF.ToIntRoundToZero();
        if (added < 0) added = 0;
        a.Pressure = System.Math.Min(MaxPressure, a.Pressure + added);
        b.Pressure = System.Math.Min(MaxPressure, b.Pressure + added);
    }
}
