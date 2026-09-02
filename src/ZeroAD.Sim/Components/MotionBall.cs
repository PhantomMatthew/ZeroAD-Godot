using System;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Components;

/// <summary>MotionBall — 原版 CCmpMotionBall/MotionBall.js 的移植。
/// 注意:这是原版 type="test" 的**测试组件**(demo/test 地图里的滚下坡小球),
/// 不是投射物系统(原版投射物飞行纯表现层 CCmpProjectileManager,命中结算走
/// Attack.js 的 projectileDelay —— 已由我们的 DelayedDamage 等价承载,
/// 弹着点/溅射圆心取命中时刻目标位置,与原版同语义)。
///
/// 行为(逐字):地形法向 × g 加速 + 指数阻力(drag^dt),MoveTo 移动。</summary>
[Component("MotionBall", "MotionBall")]
public sealed class MotionBallComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>当前速度(米/秒,原版 m_SpeedX/Z)。表现层参数,不参与战斗判定。</summary>
    public float SpeedX, SpeedZ;

    /// <summary>重力(原版 g=10)。</summary>
    private const float Gravity = 10f;
    /// <summary>阻力衰减(原版 drag=0.5 每秒分数衰减;speedX *= drag^dt)。</summary>
    private const float Drag = 0.5f;

    protected override void OnInit() { SpeedX = 0; SpeedZ = 0; }

    /// <summary>每回合驱动(原版 MT_Update;dt 秒)。由 SimBridge 组件 Tick 循环调。</summary>
    public void Tick(float dt, ComponentManager cm)
    {
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        var terrain = SimSystem.Terrain;
        if (pos == null || !pos.InWorld || terrain == null) return;

        // 地形法向(原版 CTerrain::CalcNormal):高度网格中心差分。
        var normal = CalcNormal(terrain, pos.Position.X, pos.Position.Z);

        SpeedX += normal.X.ToFloat() * Gravity * dt;
        SpeedZ += normal.Y.ToFloat() * Gravity * dt;
        // 指数阻力:drag^dt —— 落点影响实体位置(sim 状态),必须跨平台确定:
        // SafeMath.Pow 是确定性重实现(libm 超越函数在门禁黑名单)。
        float decay = (float)RmgenMath.SafeMath.Pow(Drag, dt);
        SpeedX *= decay;
        SpeedZ *= decay;

        pos.Position = new FixedVector3D(
            pos.Position.X + Fixed.FromFloat(SpeedX * dt),
            pos.Position.Y,
            pos.Position.Z + Fixed.FromFloat(SpeedZ * dt));
        cm.NotifyPositionChanged(Entity,
            new FixedVector2D(pos.Position.X, pos.Position.Z),
            new FixedVector2D(pos.Position.X, pos.Position.Z));
    }

    /// <summary>原版 CTerrain::CalcNormal:格点高度中心差分得坡向,法向 = (-dx, -dz)
    /// 归一(滚球顺坡加速)。本组件只需水平分量。</summary>
    private static FixedVector2D CalcNormal(TerrainComponent terrain, Fixed x, Fixed z)
    {
        float ts = terrain.TileSize;
        Fixed hL = terrain.GetHeight(x - Fixed.FromFloat(ts), z);
        Fixed hR = terrain.GetHeight(x + Fixed.FromFloat(ts), z);
        Fixed hD = terrain.GetHeight(x, z - Fixed.FromFloat(ts));
        Fixed hU = terrain.GetHeight(x, z + Fixed.FromFloat(ts));
        // 水平法向分量 = -∂h(下坡方向)。
        float nx = -(hR - hL).ToFloat() / (2 * ts);
        float nz = -(hU - hD).ToFloat() / (2 * ts);
        return new FixedVector2D(Fixed.FromFloat(nx), Fixed.FromFloat(nz));
    }

    public override void Serialize(Serialization.ISerializer s)
    {
        s.NumberFixed("sx", Fixed.FromFloat(SpeedX));
        s.NumberFixed("sz", Fixed.FromFloat(SpeedZ));
    }

    public override void Deserialize(Serialization.IDeserializer d)
    {
        SpeedX = d.NumberFixed("sx").ToFloat();
        SpeedZ = d.NumberFixed("sz").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}
