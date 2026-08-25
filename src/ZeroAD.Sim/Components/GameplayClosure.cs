using System;
using System.Collections.Generic;
using ZeroAD.Sim.Events;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

// P0 缺失组件七件套(PORTING-GAPS §3A),自简到难 1:1 移植原版 JS:
//   DeathDamage      — 死亡自爆(fireship/flamethrower:DeathDamage.js)
//   AutoBuildable    — 地基自动完工(AutoBuildable.js;当前数据集 0 模板,保语义)
//   Upkeep           — 生产维护费(Upkeep.js;当前数据集 0 模板,保语义)
//   AttackDetection  — 玩家级受击警报(去重抑制 + 小地图 ping;AttackDetection.js)
//   BattleDetection  — 玩家级战区跟踪(BattleDetection.js 简化:战斗列表 + 衰减)
//   AlertRaiser      — 警铃(CC/货栈等:附近平民入楼避难 / 解除;AlertRaiser.js)
//   FlyingMotion     — 飞行运动分支挂在 UnitMotion(见 UnitMotion.IsFlying;
//                      template_bird/UnitMotionFlying.js 的直线飞行语义)
//
// 时间源全部用 Tick dt 累积(锁步确定),不用真实时钟。

/// <summary>死亡自爆(DeathDamage.js):实体被摧毁时对半径内造成伤害。
/// 由 SimBridge.RemoveDeadEntities 在 DestroyEntity 前调用 CauseDeathDamage。</summary>
[Component("DeathDamage", "DeathDamage")]
public sealed class DeathDamageComponent : ComponentBase
{
    public float Range = 20f;
    public bool FriendlyFire;
    public DamageBlock Damage = new();

    /// <summary>原版 CauseDeathDamage:CauseDamageOverArea 圆形溅射。
    /// 逐目标过 DelayedDamage 同款结算(抗性→扣血→击杀事件→战利品)。</summary>
    public void CauseDeathDamage(ComponentManager cm)
    {
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        if (pos == null || !pos.InWorld) return;
        int owner = cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;

        var range = SimSystem.Range;
        if (range == null) return;
        foreach (var target in range.ExecuteQuery(Entity, Fixed.Zero, Fixed.FromFloat(Range)))
        {
            if (target == Entity) continue;
            var targetHealth = cm.QueryInterface<HealthComponent>(target);
            if (targetHealth == null || targetHealth.IsDead) continue;
            // 非友军判定(原版 friendlyFire=false 时跳过非敌人)。
            if (!FriendlyFire && owner >= 0)
            {
                var targetOwner = cm.QueryInterface<OwnershipComponent>(target)?.PlayerId ?? -1;
                if (targetOwner == owner) continue;
                if (targetOwner >= 0 && !cm.Players.IsEnemy(owner, targetOwner)) continue;
            }
            DelayedDamage.ApplyHit(cm, Entity, target, Damage, null);
        }
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("range", Fixed.FromFloat(Range));
        s.Bool("ff", FriendlyFire);
        Damage.Serialize(s, "dmg");
    }

    public override void Deserialize(IDeserializer d)
    {
        Range = d.NumberFixed("range").ToFloat();
        FriendlyFire = d.Bool("ff");
        Damage = DamageBlock.Deserialize(d, "dmg");
    }
}

/// <summary>自动建造(AutoBuildable.js):地基实体以 Rate 持续自建(原版每 1s 一次,
/// 这里按 tick 连续 Build(self, rate·dt),聚合同值;0 数据模板,保语义)。</summary>
[Component("AutoBuildable", "AutoBuildable")]
public sealed class AutoBuildableComponent : ComponentBase
{
    public float Rate = 1f;

    public void Tick(ComponentManager cm, float dt)
    {
        if (Rate <= 0) return;
        var construction = cm.QueryInterface<FoundationComponent>(Entity);
        if (construction == null || construction.IsBuilt) return;
        construction.Build(Entity, Rate, dt);
    }

    public override void Serialize(ISerializer s) => s.NumberFixed("rate", Fixed.FromFloat(Rate));
    public override void Deserialize(IDeserializer d) => Rate = d.NumberFixed("rate").ToFloat();
}

/// <summary>维护费(Upkeep.js):每 Interval 向属主玩家扣 Rates;不足则标记欠费。
/// 原版欠费会 SetControllable(false)+周期扣血——本移植先保扣费与欠费标记(0 数据模板)。</summary>
[Component("Upkeep", "Upkeep")]
public sealed class UpkeepComponent : ComponentBase, IComponentMessageHandler
{
    public float IntervalMs = 10000f;
    public int Food, Wood, Stone, Metal;
    /// <summary>欠费中(原版 unpayed;GUI/AI 可查)。</summary>
    public bool Unpaid;

    private float _elapsed;

    public void Tick(ComponentManager cm, float dt)
    {
        _elapsed += dt * 1000f;
        if (_elapsed < IntervalMs) return;
        _elapsed -= IntervalMs;

        var owner = cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;
        var player = owner >= 0 ? cm.GetPlayerEntity(owner) : null;
        if (player == null) return;

        if (player.CanAfford(Wood, Food, Stone, Metal))
        {
            player.Spend(Wood, Food, Stone, Metal);
            Unpaid = false;
        }
        else
        {
            Unpaid = true;
        }
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("ivl", Fixed.FromFloat(IntervalMs));
        s.Bool("unpaid", Unpaid);
        s.NumberFixed("el", Fixed.FromFloat(_elapsed));
        s.NumberI32("f", Food); s.NumberI32("w", Wood); s.NumberI32("s", Stone); s.NumberI32("m", Metal);
    }

    public override void Deserialize(IDeserializer d)
    {
        IntervalMs = d.NumberFixed("ivl").ToFloat();
        Unpaid = d.Bool("unpaid");
        _elapsed = d.NumberFixed("el").ToFloat();
        Food = d.NumberI32("f"); Wood = d.NumberI32("w"); Stone = d.NumberI32("s"); Metal = d.NumberI32("m");
    }

    public void HandleMessage(IMessage message) { }
}

/// <summary>受击警报(AttackDetection.js,挂玩家实体):己方实体被攻击 → 去重抑制
/// (SuppressionRange/Time 内合并)→ PlayerAttackedAlert 事件(小地图 ping/警报音)。
/// 时间源 = TurnCount(锁步确定,替代原版系统 Timer)。</summary>
[Component("AttackDetection", "AttackDetection")]
public sealed class AttackDetectionComponent : ComponentBase, IComponentMessageHandler
{
    public float SuppressionTransferRange = 120f;
    public float SuppressionRange = 80f;
    public float SuppressionTimeMs = 60000f;

    // 抑制表:(target, x, z, expireMs)。
    private readonly List<(EntityId Target, float X, float Z, float ExpireMs)> _suppressed = new();
    private float _timeMs;

    public void Tick(float dt)
    {
        _timeMs += dt * 1000f;
        _suppressed.RemoveAll(s => s.ExpireMs <= _timeMs);
    }

    /// <summary>AttackAlert 语义(OnGlobalAttacked 入口):己方受击去重后报警。</summary>
    public void OnAttacked(ComponentManager cm, EntityId target, EntityId attacker)
    {
        int playerId = PlayerIdOf(cm);
        if (playerId < 0) return;
        var targetOwner = cm.QueryInterface<OwnershipComponent>(target)?.PlayerId ?? -1;
        if (targetOwner != playerId) return;
        int atkOwner = cm.QueryInterface<OwnershipComponent>(attacker)?.PlayerId ?? -1;
        if (atkOwner == playerId) return;   // 自己打自己不报警

        var pos = cm.QueryInterface<PositionComponent>(target);
        if (pos == null || !pos.InWorld) return;

        float x = pos.Position.X.ToFloat(), z = pos.Position.Z.ToFloat();
        for (int i = 0; i < _suppressed.Count; i++)
        {
            var s = _suppressed[i];
            float dx = s.X - x, dz = s.Z - z;
            if (dx * dx + dz * dz > SuppressionTransferRange * SuppressionTransferRange)
                continue;
            // 转移:同一事件簇刷新位置/时间(原版 UpdateSuppressionEvent)。
            _suppressed[i] = (target, x, z, _timeMs + SuppressionTimeMs);
            return;
        }
        // 新抑制范围检查 + 登记(原版 suppressedList + ActivateTimer)。
        _suppressed.Add((target, x, z, _timeMs + SuppressionTimeMs));

        bool isAnimal = cm.QueryInterface<IdentityComponent>(target)?.HasClass("Animal") == true;
        cm.Events.RaisePlayerAttackedAlert(new PlayerAttackedAlertEvent
        {
            PlayerId = playerId,
            Target = target,
            Attacker = attacker,
            X = x,
            Z = z,
            TargetIsDomesticAnimal = isAnimal,
        });
    }

    private int PlayerIdOf(ComponentManager cm)
    {
        foreach (int pid in cm.Players.GetNonGaiaPlayerIds())
            if (cm.Players.GetPlayerEntityId(pid) == Entity) return pid;
        return -1;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("n", _suppressed.Count);
        foreach (var (t, x, z, exp) in _suppressed)
        {
            s.NumberU32("t", t.Value);
            s.NumberFixed("x", Fixed.FromFloat(x));
            s.NumberFixed("z", Fixed.FromFloat(z));
            s.NumberFixed("e", Fixed.FromFloat(exp));
        }
        s.NumberFixed("now", Fixed.FromFloat(_timeMs));
    }

    public override void Deserialize(IDeserializer d)
    {
        _suppressed.Clear();
        int n = d.NumberI32("n");
        for (int i = 0; i < n; i++)
        {
            var t = new EntityId(d.NumberU32("t"));
            float x = d.NumberFixed("x").ToFloat();
            float z = d.NumberFixed("z").ToFloat();
            float e = d.NumberFixed("e").ToFloat();
            _suppressed.Add((t, x, z, e));
        }
        _timeMs = d.NumberFixed("now").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}

/// <summary>战区跟踪(BattleDetection.js 简化,挂玩家实体):己方/敌对交战聚簇为
/// "战斗"条目,PhaseTime 衰减。供 AI 与 GUI 查询(原版 Petra 消费)。</summary>
[Component("BattleDetection", "BattleDetection")]
public sealed class BattleDetectionComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>战斗条目:位置 + 最近活跃时间(ms,组件时基)。</summary>
    public sealed class Battle
    {
        public float X, Z;
        public float LastActiveMs;
        public int Hits;
    }

    public float JoinRange = 40f;        // 新攻击并入既有战斗的范围
    public float TimeoutMs = 12000f;     // 静默超时移除

    private readonly List<Battle> _battles = new();
    private float _timeMs;

    public IReadOnlyList<Battle> Battles => _battles;

    public void Tick(float dt)
    {
        _timeMs += dt * 1000f;
        _battles.RemoveAll(b => b.LastActiveMs + TimeoutMs <= _timeMs);
    }

    /// <summary>己方受击(与 AttackDetection 同源)→ 并入/新建战斗条目。</summary>
    public void OnAttacked(ComponentManager cm, EntityId target, EntityId attacker)
    {
        var pos = cm.QueryInterface<PositionComponent>(target);
        if (pos == null || !pos.InWorld) return;
        float x = pos.Position.X.ToFloat(), z = pos.Position.Z.ToFloat();

        foreach (var b in _battles)
        {
            float dx = b.X - x, dz = b.Z - z;
            if (dx * dx + dz * dz <= JoinRange * JoinRange)
            {
                b.LastActiveMs = _timeMs;
                b.Hits++;
                return;
            }
        }
        _battles.Add(new Battle { X = x, Z = z, LastActiveMs = _timeMs, Hits = 1 });
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("n", _battles.Count);
        foreach (var b in _battles)
        {
            s.NumberFixed("x", Fixed.FromFloat(b.X));
            s.NumberFixed("z", Fixed.FromFloat(b.Z));
            s.NumberFixed("a", Fixed.FromFloat(b.LastActiveMs));
            s.NumberI32("h", b.Hits);
        }
        s.NumberFixed("now", Fixed.FromFloat(_timeMs));
    }

    public override void Deserialize(IDeserializer d)
    {
        _battles.Clear();
        int n = d.NumberI32("n");
        for (int i = 0; i < n; i++)
        {
            _battles.Add(new Battle
            {
                X = d.NumberFixed("x").ToFloat(),
                Z = d.NumberFixed("z").ToFloat(),
                LastActiveMs = d.NumberFixed("a").ToFloat(),
                Hits = d.NumberI32("h"),
            });
        }
        _timeMs = d.NumberFixed("now").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}

/// <summary>警铃(AlertRaiser.js,CC/货栈/粮仓/市场):RaiseAlert 让半径内己方
/// List 类单位(默认 Civilian)入楼避难;EndAlert 全部放出。冷却 = 同回合幂等
/// (原版 lastTime == GetTime 拒绝;回合即锁步时基)。</summary>
[Component("AlertRaiser", "AlertRaiser")]
public sealed class AlertRaiserComponent : ComponentBase, IComponentMessageHandler
{
    public string List = "Civilian";     // 目标类 token
    public float RaiseAlertRange = 120f;
    public float EndOfAlertRange = 180f;
    public float SearchRange = 100f;

    private float _timeMs;
    private float _lastRaiseMs = -1e9f;
    /// <summary>警铃激活中(EndAlert 置 false;存档保持)。</summary>
    public bool AlertActive;

    public void Tick(float dt) => _timeMs += dt * 1000f;

    /// <summary>原版 RaiseAlert:范围内己方平民入楼(容量感知,排队优先先到者)。
    /// 冷却 = 同时刻幂等(原版 lastTime == GetTime 拒绝;锁步时基 = 累计 tick)。</summary>
    public void RaiseAlert(ComponentManager cm)
    {
        if (_lastRaiseMs == _timeMs) return;   // 同 tick 重复敲铃无效
        _lastRaiseMs = _timeMs;
        AlertActive = true;

        int owner = cm.QueryInterface<OwnershipComponent>(Entity)?.PlayerId ?? -1;
        if (owner < 0) return;
        var range = SimSystem.Range;
        if (range == null) return;
        var myPos = cm.QueryInterface<PositionComponent>(Entity);
        if (myPos == null || !myPos.InWorld) return;

        var holder = cm.QueryInterface<GarrisonHolderComponent>(Entity);
        int capacity = holder?.Max ?? 0;

        var targets = new List<EntityId>(range.ExecuteQuery(Entity, Fixed.Zero,
            Fixed.FromFloat(RaiseAlertRange)));
        targets.Sort((a, b) => a.Value.CompareTo(b.Value));   // EntityId 序确定
        foreach (var unit in targets)
        {
            if (capacity <= 0) break;
            var identity = cm.QueryInterface<IdentityComponent>(unit);
            if (identity == null || !MatchesClass(identity, List)) continue;
            var unitOwner = cm.QueryInterface<OwnershipComponent>(unit)?.PlayerId ?? -1;
            if (unitOwner != owner) continue;
            var garrisonable = cm.QueryInterface<GarrisonableComponent>(unit);
            if (garrisonable == null) continue;
            var unitAi = cm.QueryInterface<UnitAIComponent>(unit);
            if (unitAi == null) continue;

            // 原版按 garrison 预留表分配最近的有位建筑;本楼即避难所,容量减员。
            int size = garrisonable.Size;
            if (size > capacity) continue;
            capacity -= size;
            unitAi.Garrison(Entity, queued: false);
        }
    }

    /// <summary>原版 EndAlert:警铃楼里的己方单位全部放出(UnloadAll)。</summary>
    public void EndAlert(ComponentManager cm)
    {
        AlertActive = false;
        cm.QueryInterface<GarrisonHolderComponent>(Entity)?.UnloadAll(cm);
    }

    private static bool MatchesClass(IdentityComponent identity, string tokens)
    {
        foreach (var t in tokens.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
            if (identity.HasClass(t)) return true;
        return false;
    }

    public override void Serialize(ISerializer s)
    {
        s.Bool("active", AlertActive);
        s.NumberFixed("now", Fixed.FromFloat(_timeMs));
        s.NumberFixed("last", Fixed.FromFloat(_lastRaiseMs));
    }

    public override void Deserialize(IDeserializer d)
    {
        AlertActive = d.Bool("active");
        _timeMs = d.NumberFixed("now").ToFloat();
        _lastRaiseMs = d.NumberFixed("last").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}
