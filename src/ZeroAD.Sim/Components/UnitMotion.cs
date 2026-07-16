using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

[Component("UnitMotion", "UnitMotion")]
public sealed class UnitMotion : ComponentBase, IComponentMessageHandler
{
    public Fixed Speed;
    public Fixed CurrentSpeed;
    public FixedVector2D TargetPos;
    public bool HasMoveTarget;

    private readonly List<(float x, float z)> _waypoints = new();
    private int _currentWaypoint;
    private static ObstructionManager? _obstructions;

    public static void SetObstructionManager(ObstructionManager mgr) => _obstructions = mgr;

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

        _waypoints.Clear();
        _currentWaypoint = 0;

        if (_obstructions != null)
        {
            var posComp = SimSystem.GetComponent<PositionComponent>(Entity);
            if (posComp != null)
            {
                int sx = _obstructions.WorldToGrid(posComp.Position.X.ToFloat());
                int sz = _obstructions.WorldToGrid(posComp.Position.Z.ToFloat());
                int ex = _obstructions.WorldToGrid(target.X.ToFloat());
                int ez = _obstructions.WorldToGrid(target.Y.ToFloat());

                var path = _obstructions.FindPath(sx, sz, ex, ez);
                foreach (var (px, pz) in path)
                    _waypoints.Add((_obstructions.GridToWorld(px), _obstructions.GridToWorld(pz)));
                _waypoints.Add((target.X.ToFloat(), target.Y.ToFloat()));
            }
        }
        else
        {
            _waypoints.Add((target.X.ToFloat(), target.Y.ToFloat()));
        }
    }

    public void Stop()
    {
        HasMoveTarget = false;
        CurrentSpeed = Fixed.Zero;
        _waypoints.Clear();
    }

    public void Tick(float dt)
    {
        if (!HasMoveTarget || _currentWaypoint >= _waypoints.Count)
        {
            HasMoveTarget = false;
            CurrentSpeed = Fixed.Zero;
            return;
        }

        var posComp = SimSystem.GetComponent<PositionComponent>(Entity);
        if (posComp == null) return;

        var wp = _waypoints[_currentWaypoint];
        var currentPos = new FixedVector2D(posComp.Position.X, posComp.Position.Z);
        var wpFixed = new FixedVector2D(Fixed.FromFloat(wp.x), Fixed.FromFloat(wp.z));

        var diff = wpFixed - currentPos;
        ulong dx2 = (ulong)((long)diff.X.InternalValue * (long)diff.X.InternalValue);
        ulong dy2 = (ulong)((long)diff.Y.InternalValue * (long)diff.Y.InternalValue);
        uint isqrt = MathInt.Sqrt64(dx2 + dy2);
        Fixed dist = Fixed.Zero.WithInternalValue((int)isqrt);

        Fixed stepDist = Speed.Multiply(Fixed.FromFloat(dt));

        if (dist < stepDist || dist < Fixed.FromFloat(1.0f))
        {
            _currentWaypoint++;
            if (_currentWaypoint >= _waypoints.Count)
            {
                posComp.Position = new FixedVector3D(
                    TargetPos.X, posComp.Position.Y, TargetPos.Y);
                HasMoveTarget = false;
                CurrentSpeed = Fixed.Zero;
            }
            return;
        }

        FixedVector2D dir = new(diff.X / dist, diff.Y / dist);
        Fixed dx = dir.X.Multiply(stepDist);
        Fixed dz = dir.Y.Multiply(stepDist);

        posComp.Position = new FixedVector3D(
            posComp.Position.X + dx,
            posComp.Position.Y,
            posComp.Position.Z + dz);

        CurrentSpeed = Speed;
    }

    public override void Serialize(ISerializer s)
    {
        s.NumberFixed("speed", Speed);
        s.Bool("moving", HasMoveTarget);
        s.NumberFixed("tx", TargetPos.X);
        s.NumberFixed("tz", TargetPos.Y);
    }

    public override void Deserialize(IDeserializer d)
    {
        Speed = d.NumberFixed("speed");
        HasMoveTarget = d.Bool("moving");
        TargetPos = new FixedVector2D(d.NumberFixed("tx"), d.NumberFixed("tz"));
    }

    public void HandleMessage(IMessage message) { }
}

public static class SimSystem
{
    private static ComponentManager? _cm;
    public static void Init(ComponentManager cm) => _cm = cm;
    public static T? GetComponent<T>(EntityId entity) where T : class, IComponent =>
        _cm?.QueryInterface<T>(entity);
}
