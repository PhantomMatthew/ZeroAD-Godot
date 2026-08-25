using System;
using System.Collections.Generic;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

[Component("Foundation", "Foundation")]
public sealed class FoundationComponent : ComponentBase, IComponentMessageHandler
{
    public float Progress;
    public float TotalTime;
    public string ResultTemplate = "";
    public bool IsBuilt;

    // 工人表(EntityId → 最近上报的 rate;原版 Foundation.js this.builders Map)。
    // 键序按 EntityId 排序遍历保确定。
    private readonly Dictionary<EntityId, float> _builders = new();
    /// <summary>工人速率合计(原版 totalBuilderRate)。</summary>
    public float TotalBuilderRate;
    /// <summary>当前递减系数(原版 buildMultiplier;n&lt;2 → 1)。</summary>
    public float BuildMultiplier = 1f;

    protected override void OnInit()
    {
        Progress = 0;
        TotalTime = 10;
        IsBuilt = false;
    }

    public void Configure(string template, float buildTime)
    {
        ResultTemplate = template;
        TotalTime = buildTime;
    }

    public float BuildFraction => TotalTime > 0 ? Progress / TotalTime : 1f;
    public int NumBuilders => _builders.Count;

    /// <summary>工人列表(EntityId 升序,确定性;原版 GetBuilders)。</summary>
    public List<EntityId> GetBuilders()
    {
        var list = new List<EntityId>(_builders.Keys);
        list.Sort((a, b) => a.Value.CompareTo(b.Value));
        return list;
    }

    /// <summary>原版 CalculateBuildMultiplier:num &lt; 2 → 1,否则 num^0.7 / num
    /// (buildTimePenalty=0.7,与 Repairable 同源)。经 Fixed.BuilderTimeMultiplier
    /// 查表确定化(MathF.Pow 属 libm,跨平台低位可能不同)。</summary>
    public static float CalculateBuildMultiplier(int num) =>
        Maths.Fixed.BuilderTimeMultiplier(num).ToFloat();

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

    /// <summary>一次建造推进(原版 Foundation.Build;由 BuilderComponent 每 tick 驱动,
    /// dt=回合秒数)。work = rate × buildMultiplier × dt;同步该工人最新 rate 进
    /// TotalBuilderRate。返回 true = 本次建成(调用方通知工人收工)。</summary>
    public bool Build(EntityId builderEnt, float rate, float dt)
    {
        if (IsBuilt) return true;
        AddProgress(rate * BuildMultiplier * dt);
        if (_builders.TryGetValue(builderEnt, out float old))
        {
            TotalBuilderRate += rate - old;
            _builders[builderEnt] = rate;
        }
        return IsBuilt;
    }

    public void AddProgress(float dt)
    {
        if (IsBuilt) return;
        Progress += dt;
        if (Progress >= TotalTime)
        {
            IsBuilt = true;
            Progress = TotalTime;
        }
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("prog", Maths.Fixed.FromFloat(Progress));
        s.NumberFixed("total", Maths.Fixed.FromFloat(TotalTime));
        s.StringASCII("tmpl", ResultTemplate);
        s.Bool("built", IsBuilt);
        s.NumberFixed("totrate", Maths.Fixed.FromFloat(TotalBuilderRate));
        s.NumberFixed("mult", Maths.Fixed.FromFloat(BuildMultiplier));
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
        Progress = d.NumberFixed("prog").ToFloat();
        TotalTime = d.NumberFixed("total").ToFloat();
        ResultTemplate = d.StringASCII("tmpl");
        IsBuilt = d.Bool("built");
        TotalBuilderRate = d.NumberFixed("totrate").ToFloat();
        BuildMultiplier = d.NumberFixed("mult").ToFloat();
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

[Component("Builder", "Builder")]
public sealed class BuilderComponent : ComponentBase, IComponentMessageHandler
{
    public float BuildSpeed;
    public EntityId? Target;
    // 修理登记:当前是否已在目标的 Repairable 工人表中(递减乘数按在表人数算)。
    private bool _repairRegistered;
    // 建造登记:当前是否已在目标的 Foundation 工人表中(同上,n^0.7/n 递减)。
    private bool _foundationRegistered;
    /// <summary>已到工位(本 tick 与目标距离 ≤ 工作半径)。UnitAI 的 REPAIR.APPROACHING
    /// → REPAIRING 转移判据(原版 MoveCompleted;建造动画由 REPAIRING 态承载)。
    /// 每 tick 重算的瞬态,不序列化。</summary>
    public bool AtWorksite;

    protected override void OnInit()
    {
        BuildSpeed = 1.0f;
    }

    public void Build(EntityId foundationEntity)
    {
        Target = foundationEntity;
    }

    public void Tick(ComponentManager cm)
    {
        AtWorksite = false;
        if (Target == null) return;

        // A defeated player's builders stop working.
        var owner = cm.QueryInterface<OwnershipComponent>(Entity);
        if (owner != null)
        {
            var player = cm.GetPlayerEntity(owner.PlayerId);
            if (player != null && player.IsDefeated()) { ClearRegistrations(cm); Target = null; return; }
        }

        var foundation = cm.QueryInterface<FoundationComponent>(Target.Value);
        if (foundation != null)
        {
            if (foundation.IsBuilt)
            {
                ClearRegistrations(cm);
                Target = null;
                return;
            }
            TickFoundation(cm, foundation);
            return;
        }

        // 修理分支(原版 Repairable.js):目标为已建成、受损的 Repairable 实体。
        var repairable = cm.QueryInterface<RepairableComponent>(Target.Value);
        var health = cm.QueryInterface<HealthComponent>(Target.Value);
        if (repairable == null || !repairable.IsRepairable || health == null || !health.IsInjured)
        {
            ClearRegistrations(cm);
            Target = null;
            return;
        }
        TickRepair(cm, repairable);
    }

    private void TickFoundation(ComponentManager cm, FoundationComponent foundation)
    {
        var foundationPos = cm.QueryInterface<PositionComponent>(Target!.Value);
        var myPos = cm.QueryInterface<PositionComponent>(Entity);
        if (foundationPos == null || myPos == null) return;

        float dx = foundationPos.Position.X.ToFloat() - myPos.Position.X.ToFloat();
        float dz = foundationPos.Position.Z.ToFloat() - myPos.Position.Z.ToFloat();
        float dist = MathF.Sqrt(dx * dx + dz * dz);

        var motion = cm.QueryInterface<UnitMotion>(Entity);
        if (dist > 8.0f)
        {
            // 离开工位即出工人表(与修理分支同规则:不在岗不算人头)。
            ClearRegistrations(cm);
            if (motion != null && !motion.HasMoveTarget)
                motion.MoveToPoint(new Maths.FixedVector2D(
                    foundationPos.Position.X, foundationPos.Position.Z));
        }
        else
        {
            AtWorksite = true;
            if (motion != null) motion.Stop();
            // 进工位:入工人表(Foundation 按人头算 n^0.7/n 递减)。
            // 建造速度过修正值管线(科技如 "Builder/Rate" ×1.15)
            float rate = cm.Modifiers.Apply("Builder/Rate", BuildSpeed, Entity);
            if (!_foundationRegistered)
            {
                foundation.AddBuilder(Entity, rate);
                _foundationRegistered = true;
            }
            if (foundation.Build(Entity, rate, 0.1f))
            {
                ClearRegistrations(cm);
                Target = null;
            }
        }
    }

    private void TickRepair(ComponentManager cm, RepairableComponent repairable)
    {
        var targetPos = cm.QueryInterface<PositionComponent>(Target!.Value);
        var myPos = cm.QueryInterface<PositionComponent>(Entity);
        if (targetPos == null || myPos == null) return;

        float dx = targetPos.Position.X.ToFloat() - myPos.Position.X.ToFloat();
        float dz = targetPos.Position.Z.ToFloat() - myPos.Position.Z.ToFloat();
        float dist = MathF.Sqrt(dx * dx + dz * dz);

        var motion = cm.QueryInterface<UnitMotion>(Entity);
        if (dist > 8.0f)
        {
            // 离开工位即出工人表(原版 Repair 定时器停了就不再算人头)。
            ClearRegistrations(cm);
            if (motion != null && !motion.HasMoveTarget)
                motion.MoveToPoint(new Maths.FixedVector2D(
                    targetPos.Position.X, targetPos.Position.Z));
            return;
        }

        AtWorksite = true;
        if (motion != null) motion.Stop();
        // 进工位:入工人表(Repairable 按人头算 n^0.7/n 递减)。
        float rate = cm.Modifiers.Apply("Builder/Rate", BuildSpeed, Entity);
        if (!_repairRegistered)
        {
            repairable.AddBuilder(Entity, rate);
            _repairRegistered = true;
        }
        bool done = repairable.Repair(cm, Entity, rate, 0.1f);
        if (done)
        {
            ClearRegistrations(cm);
            Target = null;
        }
    }

    private void ClearRegistrations(ComponentManager cm)
    {
        if (Target != null)
        {
            if (_repairRegistered)
                cm.QueryInterface<RepairableComponent>(Target.Value)?.RemoveBuilder(Entity);
            if (_foundationRegistered)
                cm.QueryInterface<FoundationComponent>(Target.Value)?.RemoveBuilder(Entity);
        }
        _repairRegistered = false;
        _foundationRegistered = false;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("speed", Maths.Fixed.FromFloat(BuildSpeed));
        s.NumberU32("target", Target?.Value ?? 0);
        s.Bool("repreg", _repairRegistered);
        s.Bool("fdnreg", _foundationRegistered);
    }

    public override void Deserialize(IDeserializer d)
    {
        BuildSpeed = d.NumberFixed("speed").ToFloat();
        uint tid = d.NumberU32("target");
        Target = tid != 0 ? new EntityId(tid) : null;
        _repairRegistered = d.Bool("repreg");
        _foundationRegistered = d.Bool("fdnreg");
    }

    public void HandleMessage(IMessage message) { }
}
