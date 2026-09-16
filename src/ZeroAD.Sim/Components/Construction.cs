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
    /// <summary>原版 Foundation.committed:首个工人开工即提交——清场(DeleteUponConstruction
    /// 实体销毁)+ 挤出(GetEntitiesBlockingConstruction 的单位收 LeaveFoundation 走出地基)。
    /// 序列化(存档 v17 同波)。</summary>
    public bool Committed;

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

    /// <summary>刚建成瞬间的工人快照。build 相位里工人会 ClearRegistrations,
    /// found 相位再读 GetBuilders 会是空表;autoharvest 必须用这份拷贝。</summary>
    public IReadOnlyList<EntityId> CompletedBuilders => _completedBuilders;
    private List<EntityId> _completedBuilders = new();

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
    /// TotalBuilderRate。返回 true = 本次建成(调用方通知工人收工)。
    /// 未提交且挤出未完成 → 本拍不推进进度(原版 Commit 失败则 Build 直接 return)。</summary>
    public bool Build(EntityId builderEnt, float rate, float dt)
    {
        if (IsBuilt) return true;
        if (!Committed && !Commit(SimSystem.Sim)) return false;
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
            _completedBuilders = GetBuilders();
            IsBuilt = true;
            Progress = TotalTime;
        }
    }

    /// <summary>原版 Foundation.Commit:清场 + 挤出。重叠未清完 → false(本拍不提交,
    /// 下拍 Build 重试);成功则恢复 Movement/Pathfinding 阻挡并 committed=true。</summary>
    public bool Commit(ComponentManager? cm)
    {
        if (Committed) return true;
        if (cm == null)
        {
            Committed = true;
            return true;
        }
        var obs = cm.QueryInterface<ObstructionComponent>(Entity);
        var mgr = SimSystem.Obstructions;
        if (obs != null && mgr != null
            && (obs.Flags & ObstructionFlags.BlockMovement) != 0)
        {
            foreach (var ent in mgr.GetEntitiesDeletedUponConstruction(obs.Tag))
                cm.DestroyEntity(ent);
            var collisions = mgr.GetEntitiesBlockingConstruction(obs.Tag);
            if (collisions.Count > 0)
            {
                foreach (var ent in collisions)
                    cm.QueryInterface<UnitAIComponent>(ent)?.LeaveFoundation(cm, Entity);
                return false;
            }
        }
        obs?.SetDisableBlockMovementPathfinding(false, false);
        Committed = true;
        return true;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("prog", Maths.Fixed.FromFloat(Progress));
        s.NumberFixed("total", Maths.Fixed.FromFloat(TotalTime));
        s.StringASCII("tmpl", ResultTemplate);
        s.Bool("built", IsBuilt);
        s.Bool("committed", Committed);   // 存档 v17
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
        Committed = d.Bool("committed");
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

    /// <summary>原版 Builder.GetRange.max:2 + 工人自身阻挡半径。</summary>
    public static float WorkRange(ComponentManager cm, EntityId builder)
    {
        float max = 2f;
        var obs = cm.QueryInterface<ObstructionComponent>(builder);
        if (obs != null) max += obs.GetSize().ToFloat();
        return max;
    }

    /// <summary>原版 IsInTargetRange(Builder):距目标阻挡边缘 ≤ WorkRange,
    /// 不是距中心 8m(兵营半宽 8.5m,8m 工位在壳内,提交后永远走不到)。</summary>
    public static bool InWorkRange(ComponentManager cm, EntityId builder, EntityId target)
    {
        var a = cm.QueryInterface<PositionComponent>(builder);
        var b = cm.QueryInterface<PositionComponent>(target);
        if (a == null || b == null) return false;
        float dx = a.Position.X.ToFloat() - b.Position.X.ToFloat();
        float dz = a.Position.Z.ToFloat() - b.Position.Z.ToFloat();
        float dist = MathF.Sqrt(dx * dx + dz * dz);
        float extra = WorkRange(cm, builder);
        var tobs = cm.QueryInterface<ObstructionComponent>(target);
        if (tobs != null) extra += tobs.GetSize().ToFloat();
        // UnitMotion 到站阈值约 1m;对角接近时 float 半径会差几厘米,无容差会误判
        // 未到岗又朝中心寻路,提交后卡在壳边进度永远 0。
        return dist <= extra + 1f;
    }

    private bool IsLeavingFoundation(ComponentManager cm)
    {
        var cur = cm.QueryInterface<UnitAIComponent>(Entity)?.CurrentOrder;
        return cur != null && Target != null && cur.Type == "Walk" && cur.Target == Target;
    }

    private void MoveToWorkRange(ComponentManager cm, EntityId target)
    {
        var motion = cm.QueryInterface<UnitMotion>(Entity);
        if (motion == null) return;
        var self = cm.QueryInterface<PositionComponent>(Entity);
        var pos = cm.QueryInterface<PositionComponent>(target);
        if (self == null || pos == null) return;
        var obs = cm.QueryInterface<ObstructionComponent>(target);
        float margin = WorkRange(cm, Entity);
        float cx = pos.Position.X.ToFloat();
        float cz = pos.Position.Z.ToFloat();
        float gx = cx, gz = cz;
        if (obs != null)
        {
            float dx = self.Position.X.ToFloat() - cx;
            float dz = self.Position.Z.ToFloat() - cz;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d < 0.01f) { dx = 1f; dz = 0f; d = 1f; }
            float offset = obs.GetSize().ToFloat() + margin;
            gx = cx + dx / d * offset;
            gz = cz + dz / d * offset;
        }
        motion.MoveToPoint(new Maths.FixedVector2D(
            Maths.Fixed.FromFloat(gx), Maths.Fixed.FromFloat(gz)));
    }

    private void TickFoundation(ComponentManager cm, FoundationComponent foundation)
    {
        if (IsLeavingFoundation(cm))
        {
            ClearRegistrations(cm);
            return;
        }

        var motion = cm.QueryInterface<UnitMotion>(Entity);
        if (!InWorkRange(cm, Entity, Target!.Value))
        {
            ClearRegistrations(cm);
            MoveToWorkRange(cm, Target.Value);
            return;
        }

        float rate = cm.Modifiers.Apply("Builder/Rate", BuildSpeed, Entity);
        if (!_foundationRegistered)
        {
            foundation.AddBuilder(Entity, rate);
            _foundationRegistered = true;
        }
        if (foundation.Build(Entity, rate, 0.1f))
        {
            AtWorksite = true;
            ClearRegistrations(cm);
            Target = null;
            return;
        }
        // 挤出未完成:不要 Stop,否则会取消 LeaveFoundation 走路、人卡在壳里。
        if (!foundation.Committed) return;

        AtWorksite = true;
        if (motion != null) motion.Stop();
    }

    private void TickRepair(ComponentManager cm, RepairableComponent repairable)
    {
        if (!InWorkRange(cm, Entity, Target!.Value))
        {
            ClearRegistrations(cm);
            MoveToWorkRange(cm, Target.Value);
            return;
        }

        AtWorksite = true;
        var motion = cm.QueryInterface<UnitMotion>(Entity);
        if (motion != null) motion.Stop();
        float rate = cm.Modifiers.Apply("Builder/Rate", BuildSpeed, Entity);
        if (!_repairRegistered)
        {
            repairable.AddBuilder(Entity, rate);
            _repairRegistered = true;
        }
        if (repairable.Repair(cm, Entity, rate, 0.1f))
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
