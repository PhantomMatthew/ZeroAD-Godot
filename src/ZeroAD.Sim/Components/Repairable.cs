using System;
using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>可修理的建筑/机械(原版 Repairable.js 移植;template_structure 默认件,
/// 攻城器/船亦有)。修理=按建造时间反推的回血速率,多工人收益递减(n^0.7/n)。
/// 与 FoundationComponent 的区别:foundation 是从零建造;本组件修理已建成的受损实体。</summary>
[Component("Repairable", "Repairable")]
public sealed class RepairableComponent : ComponentBase, IComponentMessageHandler
{
    /// <summary>模板 RepairTimeRatio(修理时间 = 建造时间 × 本值;structure 默认 2.0)。</summary>
    public float RepairTimeRatio = 2.0f;
    /// <summary>原版 unrepairable(升级等特殊路径可禁用修理)。</summary>
    public bool Unrepairable;

    /// <summary>多工人递减指数(原版 buildTimePenalty = 0.7):10 人合力 = 10^0.7 ≈ 5.01 倍。</summary>
    public const float BuildTimePenalty = 0.7f;

    // 工人表(EntityId → 最近上报的 rate;原版 Map)。键序按 EntityId 排序遍历保确定。
    private readonly Dictionary<EntityId, float> _builders = new();
    /// <summary>工人速率合计(原版 totalBuilderRate)。</summary>
    public float TotalBuilderRate;
    /// <summary>当前递减系数(原版 buildMultiplier;n&lt;2 → 1)。</summary>
    public float BuildMultiplier = 1f;
    // 小数回血结转:整型 HP 下 sub-1 的修理量逐 tick 累积(原版浮点 HP 无此问题)。
    private float _fractionCarry;

    protected override void OnInit() { }

    public bool IsRepairable => !Unrepairable;
    public int NumBuilders => _builders.Count;

    /// <summary>工人列表(EntityId 升序,确定性;原版 GetBuilders)。</summary>
    public List<EntityId> GetBuilders()
    {
        var list = new List<EntityId>(_builders.Keys);
        list.Sort((a, b) => a.Value.CompareTo(b.Value));
        return list;
    }

    /// <summary>原版 CalculateBuildMultiplier:num &lt; 2 → 1,否则 num^0.7 / num。</summary>
    public static float CalculateBuildMultiplier(int num) =>
        num < 2 ? 1f : MathF.Pow(num, BuildTimePenalty) / num;

    public void AddBuilder(EntityId builder, float rate)
    {
        if (_builders.ContainsKey(builder)) return;
        _builders.Add(builder, rate);
        TotalBuilderRate += rate;
        BuildMultiplier = CalculateBuildMultiplier(_builders.Count);
    }

    public void RemoveBuilder(EntityId builder)
    {
        if (!_builders.TryGetValue(builder, out float rate)) return;
        TotalBuilderRate -= rate;
        _builders.Remove(builder);
        BuildMultiplier = CalculateBuildMultiplier(_builders.Count);
    }

    /// <summary>修理速率(HP/秒;原版 GetRepairRate):maxHp / (ratio × buildTime)。
    /// 无 Cost/建造时间 → 1(原版回退)。</summary>
    public float GetRepairRate(ComponentManager cm)
    {
        var health = cm.QueryInterface<HealthComponent>(Entity);
        var cost = cm.QueryInterface<CostComponent>(Entity);
        if (health == null) return 1f;
        float repairTime = RepairTimeRatio * (cost?.BuildTime ?? 0f);
        return repairTime > 0 ? health.Max / repairTime : 1f;
    }

    /// <summary>一次修理推进(原版 Repair;由 BuilderComponent 每 tick 驱动,dt=回合秒数)。
    /// work = rate × buildMultiplier × GetRepairRate × dt,封顶剩余损伤;同步该工人的
    /// 最新 rate 进 TotalBuilderRate。返回 true = 本次修满(调用方通知工人收工)。</summary>
    public bool Repair(ComponentManager cm, EntityId builderEnt, float rate, float dt)
    {
        var health = cm.QueryInterface<HealthComponent>(Entity);
        if (health == null || Unrepairable) return false;
        int damage = health.Max - health.Current;
        if (damage <= 0) return true;

        float work = rate * BuildMultiplier * GetRepairRate(cm) * dt;
        float amount = MathF.Min(damage, work + _fractionCarry);
        int whole = (int)MathF.Floor(amount);
        _fractionCarry = amount - whole;
        health.Current = Math.Min(health.Max, health.Current + whole);

        // 同步该工人最新 rate(原版 Repair 内的 totalBuilderRate 更新)。
        if (_builders.TryGetValue(builderEnt, out float old))
        {
            TotalBuilderRate += rate - old;
            _builders[builderEnt] = rate;
        }
        return health.Current >= health.Max;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("ratio", Maths.Fixed.FromFloat(RepairTimeRatio));
        s.Bool("unrep", Unrepairable);
        s.NumberFixed("totrate", Maths.Fixed.FromFloat(TotalBuilderRate));
        s.NumberFixed("mult", Maths.Fixed.FromFloat(BuildMultiplier));
        s.NumberFixed("frac", Maths.Fixed.FromFloat(_fractionCarry));
        // 工人表:数量 + 升序 (id, rate) 对。
        var builders = GetBuilders();
        s.NumberI32("nb", builders.Count);
        foreach (var b in builders)
        {
            s.NumberU32("bid", b.Value);
            s.NumberFixed("brate", Maths.Fixed.FromFloat(_builders[b]));
        }
    }

    public override void Deserialize(IDeserializer d)
    {
        RepairTimeRatio = d.NumberFixed("ratio").ToFloat();
        Unrepairable = d.Bool("unrep");
        TotalBuilderRate = d.NumberFixed("totrate").ToFloat();
        BuildMultiplier = d.NumberFixed("mult").ToFloat();
        _fractionCarry = d.NumberFixed("frac").ToFloat();
        _builders.Clear();
        int n = d.NumberI32("nb");
        for (int i = 0; i < n; i++)
        {
            uint id = d.NumberU32("bid");
            float rate = d.NumberFixed("brate").ToFloat();
            _builders[new EntityId(id)] = rate;
        }
    }

    public void HandleMessage(IMessage message) { }
}
