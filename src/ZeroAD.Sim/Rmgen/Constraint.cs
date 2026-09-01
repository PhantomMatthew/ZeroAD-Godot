using System;
using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen
{
    /// <summary>约束接口（原版 Constraint.js 的 prototype.allows）。</summary>
    public interface IConstraint
    {
        bool Allows(RmgenVector2D position);
    }

    /// <summary>始终满足。</summary>
    public sealed class NullConstraint : IConstraint
    {
        public bool Allows(RmgenVector2D position) => true;
    }

    /// <summary>全部约束都满足（AND）。</summary>
    public sealed class AndConstraint : IConstraint
    {
        private readonly List<IConstraint> _constraints;
        public AndConstraint(IEnumerable<IConstraint> constraints) => _constraints = constraints.ToList();
        public AndConstraint(params IConstraint[] constraints) => _constraints = constraints.ToList();
        public bool Allows(RmgenVector2D pos) => _constraints.All(c => c.Allows(pos));
    }

    /// <summary>任一约束满足（OR）。</summary>
    public sealed class OrConstraint : IConstraint
    {
        private readonly List<IConstraint> _constraints;
        public OrConstraint(IEnumerable<IConstraint> constraints) => _constraints = constraints.ToList();
        public OrConstraint(params IConstraint[] constraints) => _constraints = constraints.ToList();
        public bool Allows(RmgenVector2D pos) => _constraints.Any(c => c.Allows(pos));
    }

    /// <summary>在给定 Area 内。</summary>
    public sealed class StayAreasConstraint : IConstraint
    {
        private readonly List<Area> _areas;
        public StayAreasConstraint(IEnumerable<Area> areas) => _areas = areas.ToList();
        public bool Allows(RmgenVector2D pos) => _areas.Any(a => a.Contains(pos));
    }

    /// <summary>避开给定 Area。</summary>
    public sealed class AvoidAreasConstraint : IConstraint
    {
        private readonly List<Area> _areas;
        public AvoidAreasConstraint(IEnumerable<Area> areas) => _areas = areas.ToList();
        public bool Allows(RmgenVector2D pos) => _areas.All(a => !a.Contains(pos));
    }

    /// <summary>与给定 Area 相邻但不在其内(逐字移植 Constraint.js 的 AdjacentToAreaConstraint):
    /// 自身不在任一 area 内,且 4 邻域至少一个点在该 area 内。</summary>
    public sealed class AdjacentToAreaConstraint : IConstraint
    {
        private readonly List<Area> _areas;
        public AdjacentToAreaConstraint(IEnumerable<Area> areas) => _areas = areas.ToList();
        public bool Allows(RmgenVector2D pos)
        {
            var map = RmgenLibrary.CurrentMap;
            foreach (var area in _areas)
            {
                if (area.Contains(pos)) continue;
                foreach (var adj in map.GetAdjacentPoints(pos))
                    if (area.Contains(adj))
                        return true;
            }
            return false;
        }
    }

    /// <summary>纹理匹配。</summary>
    public sealed class StayTextureConstraint : IConstraint
    {
        private readonly string _texture;
        private readonly RandomMap _map;
        public StayTextureConstraint(RandomMap map, string texture) { _map = map; _texture = texture; }
        public bool Allows(RmgenVector2D pos) => _map.GetTexture(pos) == _texture;
    }

    /// <summary>纹理不匹配。</summary>
    public sealed class AvoidTextureConstraint : IConstraint
    {
        private readonly string _texture;
        private readonly RandomMap _map;
        public AvoidTextureConstraint(RandomMap map, string texture) { _map = map; _texture = texture; }
        public bool Allows(RmgenVector2D pos) => _map.GetTexture(pos) != _texture;
    }

    /// <summary>半径内无 TileClass 成员。</summary>
    public sealed class AvoidTileClassConstraint : IConstraint
    {
        private readonly TileClass _tileClass;
        private readonly double _distance;
        public AvoidTileClassConstraint(TileClass tileClass, double distance) { _tileClass = tileClass; _distance = distance; }
        public bool Allows(RmgenVector2D pos) => _tileClass.CountMembersInRadius(pos, _distance) == 0;
    }

    /// <summary>半径内全部是 TileClass 成员。</summary>
    public sealed class StayInTileClassConstraint : IConstraint
    {
        private readonly TileClass _tileClass;
        private readonly double _distance;
        public StayInTileClassConstraint(TileClass tileClass, double distance) { _tileClass = tileClass; _distance = distance; }
        public bool Allows(RmgenVector2D pos) => _tileClass.CountNonMembersInRadius(pos, _distance) == 0;
    }

    /// <summary>半径内至少有一个 TileClass 成员。</summary>
    public sealed class NearTileClassConstraint : IConstraint
    {
        private readonly TileClass _tileClass;
        private readonly double _distance;
        public NearTileClassConstraint(TileClass tileClass, double distance) { _tileClass = tileClass; _distance = distance; }
        public bool Allows(RmgenVector2D pos) => _tileClass.CountMembersInRadius(pos, _distance) > 0;
    }

    /// <summary>边界 TileClass（内有非成员 + 外有成员）。</summary>
    public sealed class BorderTileClassConstraint : IConstraint
    {
        private readonly TileClass _tileClass;
        private readonly double _distanceInside, _distanceOutside;
        public BorderTileClassConstraint(TileClass tc, double distInside, double distOutside)
        { _tileClass = tc; _distanceInside = distInside; _distanceOutside = distOutside; }
        public bool Allows(RmgenVector2D pos)
            => _tileClass.CountMembersInRadius(pos, _distanceOutside) > 0
            && _tileClass.CountNonMembersInRadius(pos, _distanceInside) > 0;
    }

    /// <summary>高度在 [minHeight, maxHeight] 范围内。</summary>
    public sealed class HeightConstraint : IConstraint
    {
        private readonly double _minHeight, _maxHeight;
        private readonly RandomMap _map;
        public HeightConstraint(RandomMap map, double minHeight, double maxHeight)
        { _map = map; _minHeight = minHeight; _maxHeight = maxHeight; }
        public bool Allows(RmgenVector2D pos)
        {
            double h = _map.GetHeight(pos);
            return _minHeight <= h && h <= _maxHeight;
        }
    }

    /// <summary>可通行地图区域。</summary>
    public sealed class PassableMapAreaConstraint : IConstraint
    {
        private readonly RandomMap _map;
        public PassableMapAreaConstraint(RandomMap map) => _map = map;
        public bool Allows(RmgenVector2D pos) => _map.ValidTilePassable(pos);
    }

    /// <summary>SlopeConstraint（逐字移植 Constraint.js）——坡度（8 邻域平均高度差）
    /// 在 [min,max]（含端点）；单侧时另一侧传 ±Infinity。</summary>
    public sealed class SlopeConstraint : IConstraint
    {
        private readonly double _minSlope, _maxSlope;
        private readonly RandomMap _map;
        public SlopeConstraint(RandomMap map, double minSlope, double maxSlope)
        { _map = map; _minSlope = minSlope; _maxSlope = maxSlope; }
        public bool Allows(RmgenVector2D pos)
        {
            double s = _map.GetSlope(pos);
            return _minSlope <= s && s <= _maxSlope;
        }
    }

    /// <summary>静态缓存约束（整个地图预计算一次，后续查缓存）。</summary>
    public sealed class StaticConstraint : IConstraint
    {
        private readonly IConstraint _constraint;
        private readonly byte[][] _cache;  // 0=未计算, 1=false, 2=true

        public StaticConstraint(RandomMap map, params IConstraint[] constraints)
        {
            _constraint = new AndConstraint(constraints);
            int size = map.GetSize();
            _cache = new byte[size][];
            for (int i = 0; i < size; i++)
                _cache[i] = new byte[size];
        }

        public bool Allows(RmgenVector2D pos)
        {
            int x = (int)pos.X, y = (int)pos.Y;
            if (_cache[x][y] == 0)
                _cache[x][y] = _constraint.Allows(pos) ? (byte)2 : (byte)1;
            return _cache[x][y] == 2;
        }
    }
}
