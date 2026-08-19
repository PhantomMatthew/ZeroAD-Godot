using System.Collections.Generic;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>建筑自动防御(移植原版 BuildingAI.js 核心):范围内出现敌军 → 按攻击速率齐射。
/// 箭数 = DefaultArrowCount + 驻军中弓手类别数 × GarrisonArrowMultiplier(上限 MaxArrowCount)。
/// 目标选择:原版按 attack preference + 距离排序;此处取最近敌(偏好表未移植,记录在案)。
/// 手动集火目标(unitAITarget/focusTargets)未移植——自动索敌是全量行为。
/// 结算走 AttackComponent.PerformAttack(修正值管线/投射物事件/伤害与单位同路)。</summary>
[Component("BuildingAI", "BuildingAI")]
public sealed class BuildingAIComponent : ComponentBase, IComponentMessageHandler
{
    // 模板参数(装配时灌入,序列化)。
    public int DefaultArrowCount = 1;
    public int MaxArrowCount;               // 0 = 不限(原版 Infinity)
    public float GarrisonArrowMultiplier = 1f;
    public string GarrisonArrowClasses = "";

    private const float ScanInterval = 1.0f;   // 原版 range query 事件 → 1s 节流轮询
    private float _scanElapsed;
    private float _cooldown;
    private readonly List<EntityId> _targets = new();

    protected override void OnInit() { }

    /// <summary>当前箭数(驻军变化实时算,原版 GetArrowCount)。</summary>
    public int GetArrowCount(ComponentManager cm)
    {
        int archers = 0;
        var holder = cm.QueryInterface<GarrisonHolderComponent>(Entity);
        if (holder != null && GarrisonArrowClasses.Length > 0)
        {
            foreach (var gid in holder.Entities)
            {
                var id = cm.QueryInterface<IdentityComponent>(gid);
                if (id != null && id.MatchesClassList(GarrisonArrowClasses)) archers++;
            }
        }
        int count = DefaultArrowCount + (int)System.MathF.Round(archers * GarrisonArrowMultiplier);
        return MaxArrowCount > 0 ? System.Math.Min(count, MaxArrowCount) : count;
    }

    public void Tick(float dt, ComponentManager cm)
    {
        var attack = cm.QueryInterface<AttackComponent>(Entity);
        var own = cm.QueryInterface<OwnershipComponent>(Entity);
        if (attack == null || own == null) return;
        // 未完工地基不放箭(原版:建筑完工才有防御)。
        if (cm.QueryInterface<FoundationComponent>(Entity) is { IsBuilt: false }) return;

        _cooldown -= dt;
        _scanElapsed += dt;
        if (_scanElapsed >= ScanInterval)
        {
            _scanElapsed = 0;
            RefreshTargets(cm, attack, own.PlayerId);
        }
        if (_targets.Count == 0 || _cooldown > 0f) return;

        // 齐射:箭数轮摊到存活目标(近者优先;原版按 preference+proximity 排序后逐箭分派)。
        int arrows = GetArrowCount(cm);
        var alive = new List<EntityId>();
        foreach (var t in _targets)
            if (cm.QueryInterface<HealthComponent>(t) is { IsDead: false }) alive.Add(t);
        if (alive.Count == 0) { _cooldown = 1f / attack.Rate; return; }
        for (int i = 0; i < arrows; i++)
        {
            attack.Target = alive[i % alive.Count];
            attack.CurrentAttackIsCapture = false;
            attack.PerformAttack(cm);
        }
        attack.Target = null;
        _cooldown = 1f / attack.Rate;   // 原版 RepeatTime 一个周期
    }

    /// <summary>范围内可见敌军(1s 节流刷新;原版 OnRangeUpdate 维护 targetUnits)。</summary>
    private void RefreshTargets(ComponentManager cm, AttackComponent attack, int myPlayer)
    {
        _targets.Clear();
        var range = SimSystem.Range;
        if (range == null) return;
        var found = range.ExecuteQuery(Entity, Fixed.Zero, Fixed.FromFloat(attack.Range), e =>
        {
            var eo = cm.QueryInterface<OwnershipComponent>(e);
            if (eo == null || eo.PlayerId <= 0) return false;               // 不打野(原版不射 gaia)
            if (!cm.Players.IsEnemy(myPlayer, eo.PlayerId)) return false;
            if (cm.QueryInterface<UnitMotion>(e) == null) return false;      // 只打移动单位(原版目标是单位)
            if (range.GetLosVisibility(e, myPlayer) == LosVisibility.Hidden) return false;
            return cm.QueryInterface<HealthComponent>(e) is { IsDead: false };
        });
        // 近者优先(原版 proximity 次序)。
        var pos = cm.QueryInterface<PositionComponent>(Entity);
        if (pos != null)
        {
            float px = pos.Position.X.ToFloat(), pz = pos.Position.Z.ToFloat();
            found.Sort((a, b) =>
            {
                var pa = cm.QueryInterface<PositionComponent>(a);
                var pb = cm.QueryInterface<PositionComponent>(b);
                if (pa == null || pb == null) return 0;
                float da = (pa.Position.X.ToFloat() - px) * (pa.Position.X.ToFloat() - px)
                         + (pa.Position.Z.ToFloat() - pz) * (pa.Position.Z.ToFloat() - pz);
                float db = (pb.Position.X.ToFloat() - px) * (pb.Position.X.ToFloat() - px)
                         + (pb.Position.Z.ToFloat() - pz) * (pb.Position.Z.ToFloat() - pz);
                return da.CompareTo(db);
            });
        }
        _targets.AddRange(found);
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberI32("defArrows", DefaultArrowCount);
        s.NumberI32("maxArrows", MaxArrowCount);
        s.NumberFixed("garrMult", Fixed.FromFloat(GarrisonArrowMultiplier));
        s.StringASCII("garrCls", GarrisonArrowClasses);
        s.NumberFixed("scan", Fixed.FromFloat(_scanElapsed));
        s.NumberFixed("cooldown", Fixed.FromFloat(_cooldown));
    }

    public override void Deserialize(IDeserializer d)
    {
        DefaultArrowCount = d.NumberI32("defArrows");
        MaxArrowCount = d.NumberI32("maxArrows");
        GarrisonArrowMultiplier = d.NumberFixed("garrMult").ToFloat();
        GarrisonArrowClasses = d.StringASCII("garrCls");
        _scanElapsed = d.NumberFixed("scan").ToFloat();
        _cooldown = d.NumberFixed("cooldown").ToFloat();
    }

    public void HandleMessage(IMessage message) { }
}
