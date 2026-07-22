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
            if (player != null && player.IsDefeated()) { Target = null; return; }
        }

        var foundation = cm.QueryInterface<FoundationComponent>(Target.Value);
        if (foundation == null || foundation.IsBuilt)
        {
            Target = null;
            return;
        }

        var foundationPos = cm.QueryInterface<PositionComponent>(Target.Value);
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
            foundation.AddProgress(BuildSpeed * 0.1f);
        }
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("speed", Maths.Fixed.FromFloat(BuildSpeed));
        s.NumberU32("target", Target?.Value ?? 0);
    }

    public override void Deserialize(IDeserializer d)
    {
        BuildSpeed = d.NumberFixed("speed").ToFloat();
        uint tid = d.NumberU32("target");
        Target = tid != 0 ? new EntityId(tid) : null;
    }

    public void HandleMessage(IMessage message) { }
}
