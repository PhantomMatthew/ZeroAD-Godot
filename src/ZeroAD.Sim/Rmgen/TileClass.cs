using System;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen
{
    /// <summary>TileClass（逐字移植 TileClass.js，100 行）。
    /// 位打包的包含网格——每 16 个 tile 占一个 ushort 位。用于快速半径查询。
    /// countInRadius 用 Math.ceil/floor/sqrt——全部 JS 语义（C# 一致）。</summary>
    public sealed class TileClass
    {
        public readonly int Size;
        public readonly int Width;
        private readonly ushort[] _inclusionGrid;

        public TileClass(int size)
        {
            Size = size;
            Width = (int)Math.Ceiling(size / 16.0);
            _inclusionGrid = new ushort[size * Width];
        }

        /// <summary>position 是否在 tileclass 中。用 x>>4 索引 + x&amp;0xF 位偏移。</summary>
        public bool Has(RmgenVector2D pos)
        {
            int x = (int)pos.X, y = (int)pos.Y;
            if (x < 0 || x >= Size || y < 0 || y >= Size) return false;
            return (_inclusionGrid[y * Width + (x >> 4)] & (1 << (x & 0xF))) != 0;
        }

        public void Add(RmgenVector2D pos)
        {
            int x = (int)pos.X, y = (int)pos.Y;
            if (x < 0 || x >= Size || y < 0 || y >= Size) return;
            _inclusionGrid[y * Width + (x >> 4)] |= (ushort)(1 << (x & 0xF));
        }

        public void Remove(RmgenVector2D pos)
        {
            int x = (int)pos.X, y = (int)pos.Y;
            if (x < 0 || x >= Size || y < 0 || y >= Size) return;
            _inclusionGrid[y * Width + (x >> 4)] &= (ushort)~(1 << (x & 0xF));
        }

        /// <summary>半径内计数（逐字移植 countInRadius）。</summary>
        public int CountInRadius(RmgenVector2D pos, double radius, bool returnMembers)
        {
            int members = 0, total = 0;
            double radius2 = radius * radius;
            int x = (int)pos.X, y = (int)pos.Y;

            int yMin = (int)Math.Max(Math.Ceiling(y - radius), 0);
            int yMax = (int)Math.Min(Math.Floor(y + radius), Size - 1);

            for (int iy = yMin; iy <= yMax; iy++)
            {
                double dy = iy - y;
                double dy2 = dy * dy;
                double delta = Math.Sqrt(radius2 - dy2);
                int xMin = (int)Math.Max(Math.Ceiling(x - delta), 0);
                int xMax = (int)Math.Min(Math.Floor(x + delta), Size - 1);

                int indexXMin = xMin >> 4;
                int indexXMax = xMax >> 4;
                int indexY = iy * Width;
                for (int indexX = indexXMin; indexX <= indexXMax; indexX++)
                {
                    int imin = indexX == indexXMin ? xMin & 0xF : 0;
                    int imax = indexX == indexXMax ? xMax & 0xF : 15;
                    total += imax - imin + 1;
                    ushort grid = _inclusionGrid[indexY + indexX];
                    if (grid != 0)
                        for (int i = imin; i <= imax; i++)
                            if ((grid & (1 << i)) != 0)
                                members++;
                }
            }
            return returnMembers ? members : total - members;
        }

        public int CountMembersInRadius(RmgenVector2D pos, double radius)
            => CountInRadius(pos, radius, true);

        public int CountNonMembersInRadius(RmgenVector2D pos, double radius)
            => CountInRadius(pos, radius, false);
    }

    /// <summary>Area（逐字移植 Area.js）——点集 + 缓存。</summary>
    public sealed class Area
    {
        public readonly RandomMap Map;
        private readonly System.Collections.Generic.HashSet<(int x, int y)> _points = new();
        private readonly RmgenVector2D[] _pointArray;

        public Area(RandomMap map, System.Collections.Generic.List<RmgenVector2D> points)
        {
            Map = map;
            _pointArray = points.ToArray();
            foreach (var p in points)
                _points.Add(((int)p.X, (int)p.Y));
        }

        public bool Contains(RmgenVector2D pos)
            => _points.Contains(((int)pos.X, (int)pos.Y));

        /// <summary>getClosestPointTo——区域内距 position 最近的点（空区域返回 null）。</summary>
        public RmgenVector2D? GetClosestPointTo(RmgenVector2D position)
        {
            if (_pointArray.Length == 0)
                return null;

            var closestPoint = _pointArray[0];
            double shortestDistance = double.PositiveInfinity;
            foreach (var point in _pointArray)
            {
                double currentDistance = point.DistanceToSquared(position);
                if (currentDistance < shortestDistance)
                {
                    shortestDistance = currentDistance;
                    closestPoint = point;
                }
            }
            return closestPoint;
        }

        public System.Collections.Generic.List<RmgenVector2D> GetPoints()
            => new(_pointArray);

        public int PointCount => _pointArray.Length;
    }
}
