using System;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

[Component("Foundation", "Foundation")]
public sealed class FoundationComponent : ComponentBase, IComponentMessageHandler
{
    public float Progress;
    public float TotalTime;
    public string ResultTemplate = "";
    public bool IsBuilt;

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
    }

    public override void Deserialize(IDeserializer d)
    {
        Progress = d.NumberFixed("prog").ToFloat();
        TotalTime = d.NumberFixed("total").ToFloat();
        ResultTemplate = d.StringASCII("tmpl");
        IsBuilt = d.Bool("built");
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
        if (Target == null) return;

        // A defeated player's builders stop working.
        var owner = cm.QueryInterface<OwnershipComponent>(Entity);
        if (owner != null)
        {
            var player = cm.GetPlayerEntity(owner.PlayerId);
            if (player != null && player.IsDefeated()) { ClearRepairRegistration(cm); Target = null; return; }
        }

        var foundation = cm.QueryInterface<FoundationComponent>(Target.Value);
        if (foundation != null)
        {
            if (foundation.IsBuilt)
            {
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
            ClearRepairRegistration(cm);
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
            if (motion != null && !motion.HasMoveTarget)
                motion.MoveToPoint(new Maths.FixedVector2D(
                    foundationPos.Position.X, foundationPos.Position.Z));
        }
        else
        {
            if (motion != null) motion.Stop();
            // 建造速度过修正值管线(科技如 "Builder/Rate" ×1.15)
            foundation.AddProgress(cm.Modifiers.Apply("Builder/Rate", BuildSpeed, Entity) * 0.1f);
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
            ClearRepairRegistration(cm);
            if (motion != null && !motion.HasMoveTarget)
                motion.MoveToPoint(new Maths.FixedVector2D(
                    targetPos.Position.X, targetPos.Position.Z));
            return;
        }

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
            ClearRepairRegistration(cm);
            Target = null;
        }
    }

    private void ClearRepairRegistration(ComponentManager cm)
    {
        if (!_repairRegistered) return;
        if (Target != null)
            cm.QueryInterface<RepairableComponent>(Target.Value)?.RemoveBuilder(Entity);
        _repairRegistered = false;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("speed", Maths.Fixed.FromFloat(BuildSpeed));
        s.NumberU32("target", Target?.Value ?? 0);
        s.Bool("repreg", _repairRegistered);
    }

    public override void Deserialize(IDeserializer d)
    {
        BuildSpeed = d.NumberFixed("speed").ToFloat();
        uint tid = d.NumberU32("target");
        Target = tid != 0 ? new EntityId(tid) : null;
        _repairRegistered = d.Bool("repreg");
    }

    public void HandleMessage(IMessage message) { }
}
