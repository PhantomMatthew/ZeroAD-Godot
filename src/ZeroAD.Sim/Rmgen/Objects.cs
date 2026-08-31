using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen
{
    /// <summary>实体放置规格（原版 Object.js place 返回的 entitySpec）。</summary>
    public sealed class EntitySpec
    {
        public readonly string TemplateName;
        public readonly int PlayerId;
        public readonly RmgenVector2D Position;
        public readonly double Angle;

        public EntitySpec(string templateName, int playerId, RmgenVector2D position, double angle)
        { TemplateName = templateName; PlayerId = playerId; Position = position; Angle = angle; }
    }

    /// <summary>回避条目（原版 avoidPositions 元素：position + distanceSquared）。</summary>
    public readonly struct AvoidPosition
    {
        public readonly RmgenVector2D Position;
        public readonly double DistanceSquared;

        public AvoidPosition(RmgenVector2D position, double distanceSquared)
        { Position = position; DistanceSquared = distanceSquared; }
    }

    /// <summary>组内可放置元素（原版 Object.js 的 object.place 协议）。
    /// 失败返回 null——SimpleGroup 任一元素失败即整组放弃。</summary>
    public interface IGroupElement
    {
        double AvoidDistanceSquared { get; }

        List<EntitySpec>? Place(RmgenVector2D centerPosition, int playerId,
            List<AvoidPosition>? avoidPositions, IConstraint constraint, int maxRetries);
    }

    /// <summary>SimpleObject（逐字移植 Object.js）——围绕中心在随机距离/角度放随机数量实体。
    /// RNG 消耗顺序与上游严格一致：randIntInclusive(数量) → 每实体重试循环
    /// [randFloat(距离) → randomAngle() → 合法则 randFloat(朝向) 并 break]。
    /// "actor|" 前缀模板用 validTile（可压不可通行边界），其余用 validTilePassable。</summary>
    public sealed class ScatterObject : IGroupElement
    {
        /// <summary>上游 g_ActorPrefix（library.js）。</summary>
        public const string ActorPrefix = "actor|";

        public readonly string TemplateName;
        /// <summary>上游 SimpleObject 的 min/maxCount 是裸数值，rmgen2 大量传小数
        /// （randIntInclusive 自身接受小数并 floor），故此处保 double 不提前取整。</summary>
        public readonly double MinCount, MaxCount;
        public readonly double MinDistance, MaxDistance, MinAngle, MaxAngle;
        public double AvoidDistanceSquared { get; }
        private readonly RmgenRng _rng;

        public ScatterObject(RmgenRng rng, string templateName, double minCount, double maxCount,
            double minDistance, double maxDistance,
            double minAngle = 0, double maxAngle = 2 * SafeMath.PI, double avoidDistance = 1)
        {
            if (minCount > maxCount)
                throw new ArgumentException("SimpleObject: minCount should be less than or equal to maxCount");
            if (minDistance > maxDistance)
                throw new ArgumentException("SimpleObject: minDistance should be less than or equal to maxDistance");
            if (minAngle > maxAngle)
                throw new ArgumentException("SimpleObject: minAngle should be less than or equal to maxAngle");
            _rng = rng;
            TemplateName = templateName;
            MinCount = minCount; MaxCount = maxCount;
            MinDistance = minDistance; MaxDistance = maxDistance;
            MinAngle = minAngle; MaxAngle = maxAngle;
            AvoidDistanceSquared = SafeMath.Square(avoidDistance);
        }

        public List<EntitySpec>? Place(RmgenVector2D centerPosition, int playerId,
            List<AvoidPosition>? avoidPositions, IConstraint constraint, int maxRetries)
        {
            var map = RmgenLibrary.CurrentMap;
            bool isActor = TemplateName.StartsWith(ActorPrefix, StringComparison.Ordinal);
            var entitySpecs = new List<EntitySpec>();
            int numRetries = 0;

            int count = _rng.RandIntInclusive(MinCount, MaxCount);
            for (int i = 0; i < count; ++i)
                while (true)
                {
                    double distance = _rng.RandFloat(MinDistance, MaxDistance);
                    double angle = _rng.RandomAngle();

                    var offset = new RmgenVector2D(distance, 0);
                    offset.Rotate(-angle);
                    var position = RmgenVector2D.Add(
                        RmgenVector2D.Add(centerPosition, new RmgenVector2D(0.5, 0.5)), offset);

                    bool validTile = isActor ? map.ValidTile(position) : map.ValidTilePassable(position);
                    var floored = position;
                    floored.Floor();

                    if (validTile &&
                        (avoidPositions == null ||
                            entitySpecs.All(e => e.Position.DistanceToSquared(position) >= AvoidDistanceSquared) &&
                            avoidPositions.All(a => a.Position.DistanceToSquared(position) >=
                                Math.Max(AvoidDistanceSquared, a.DistanceSquared))) &&
                        constraint.Allows(floored))
                    {
                        entitySpecs.Add(new EntitySpec(TemplateName, playerId, position,
                            _rng.RandFloat(MinAngle, MaxAngle)));
                        break;
                    }

                    if (numRetries++ > maxRetries)
                        return null;
                }

            return entitySpecs;
        }
    }

    /// <summary>RandomObject（逐字移植 Object.js）——place 时先 pickRandom 选一模板（消耗 1 次 RNG），
    /// 再按 SimpleObject 语义放置。</summary>
    public sealed class RandomObject : IGroupElement
    {
        private readonly RmgenRng _rng;
        private readonly IReadOnlyList<string> _templateNames;
        private readonly double _minCount, _maxCount;
        private readonly double _minDistance, _maxDistance, _minAngle, _maxAngle, _avoidDistance;

        public RandomObject(RmgenRng rng, IReadOnlyList<string> templateNames, double minCount, double maxCount,
            double minDistance, double maxDistance,
            double minAngle = 0, double maxAngle = 2 * SafeMath.PI, double avoidDistance = 1)
        {
            _rng = rng;
            _templateNames = templateNames;
            _minCount = minCount; _maxCount = maxCount;
            _minDistance = minDistance; _maxDistance = maxDistance;
            _minAngle = minAngle; _maxAngle = maxAngle;
            _avoidDistance = avoidDistance;
        }

        public double AvoidDistanceSquared => SafeMath.Square(_avoidDistance);

        public List<EntitySpec>? Place(RmgenVector2D centerPosition, int playerId,
            List<AvoidPosition>? avoidPositions, IConstraint constraint, int maxRetries)
            => new ScatterObject(_rng, _rng.PickRandom(_templateNames), _minCount, _maxCount,
                _minDistance, _maxDistance, _minAngle, _maxAngle, _avoidDistance)
                .Place(centerPosition, playerId, avoidPositions, constraint, maxRetries);
    }

    /// <summary>SimpleGroup（逐字移植 Group.js）——整组试放：任一元素失败则全组一个都不放。
    /// setCenterPosition 会 round 到整数图块。avoidSelf=true 时组内实体互不重叠
    /// （回避半径取各元素自己的 avoidDistance）。</summary>
    public sealed class ObjectGroup : ICenteredObjectGroup
    {
        private readonly IReadOnlyList<IGroupElement> _objects;
        private readonly TileClass? _tileClass;
        private readonly bool _avoidSelf;
        private RmgenVector2D _centerPosition;

        public ObjectGroup(IReadOnlyList<IGroupElement> objects, bool avoidSelf = false,
            TileClass? tileClass = null, RmgenVector2D? centerPosition = null)
        {
            _objects = objects;
            _avoidSelf = avoidSelf;
            _tileClass = tileClass;
            if (centerPosition.HasValue)
                SetCenterPosition(centerPosition.Value);
        }

        public void SetCenterPosition(RmgenVector2D position)
        {
            var p = position;
            p.Round();
            _centerPosition = p;
        }

        public bool Place(int player, IConstraint constraint)
        {
            var map = RmgenLibrary.CurrentMap;
            var entitySpecsResult = new List<EntitySpec>();
            List<AvoidPosition>? avoidPositions = _avoidSelf ? new List<AvoidPosition>() : null;

            // 先试放全部元素——任一失败则一个都不放
            foreach (var obj in _objects)
            {
                var entitySpecs = obj.Place(_centerPosition, player, avoidPositions, constraint, 30);
                if (entitySpecs == null)
                    return false;

                entitySpecsResult.AddRange(entitySpecs);

                if (_avoidSelf)
                    avoidPositions!.AddRange(entitySpecs.Select(
                        s => new AvoidPosition(s.Position, obj.AvoidDistanceSquared)));
            }

            // 全部可行——统一下实体（placeEntityAnywhere：元素已保证非 actor 不压边界）
            foreach (var spec in entitySpecsResult)
            {
                map.PlaceEntityAnywhere(spec.TemplateName, spec.PlayerId, spec.Position, spec.Angle);
                if (_tileClass != null)
                {
                    var floored = spec.Position;
                    floored.Floor();
                    _tileClass.Add(floored);
                }
            }
            return true;
        }
    }

    /// <summary>RandomGroup（逐字移植 Group.js）——构造时 pickRandom 选一个元素（此时消耗 1 次 RNG），
    /// 之后行为同 SimpleGroup。</summary>
    public sealed class RandomGroup : ICenteredObjectGroup
    {
        private readonly ObjectGroup _simpleGroup;

        public RandomGroup(RmgenRng rng, IReadOnlyList<IGroupElement> objects, bool avoidSelf = false,
            TileClass? tileClass = null, RmgenVector2D? centerPosition = null)
            => _simpleGroup = new ObjectGroup(new IGroupElement[] { rng.PickRandom(objects) },
                avoidSelf, tileClass, centerPosition);

        public void SetCenterPosition(RmgenVector2D position) => _simpleGroup.SetCenterPosition(position);

        public bool Place(int player, IConstraint constraint) => _simpleGroup.Place(player, constraint);
    }
}
