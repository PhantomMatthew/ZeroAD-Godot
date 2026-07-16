using System;
using System.Collections.Generic;

namespace ZeroAD.Sim
{
    public sealed class ObstructionManager
    {
        private readonly bool[,] _blocked;
        public int GridSize { get; }
        public float CellSize { get; }

        public ObstructionManager(int gridSize, float cellSize)
        {
            GridSize = gridSize;
            CellSize = cellSize;
            _blocked = new bool[gridSize, gridSize];
        }

        public void Clear()
        {
            Array.Clear(_blocked, 0, _blocked.Length);
        }

        public void BlockCircle(float worldX, float worldZ, float radius)
        {
            int cx = WorldToGrid(worldX);
            int cz = WorldToGrid(worldZ);
            int r = Math.Max(1, (int)(radius / CellSize + 0.5f));

            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r * r) continue;
                    int x = cx + dx;
                    int z = cz + dz;
                    if (x >= 0 && x < GridSize && z >= 0 && z < GridSize)
                        _blocked[x, z] = true;
                }
            }
        }

        public void UnblockCircle(float worldX, float worldZ, float radius)
        {
            int cx = WorldToGrid(worldX);
            int cz = WorldToGrid(worldZ);
            int r = Math.Max(1, (int)(radius / CellSize + 0.5f));

            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r * r) continue;
                    int x = cx + dx;
                    int z = cz + dz;
                    if (x >= 0 && x < GridSize && z >= 0 && z < GridSize)
                        _blocked[x, z] = false;
                }
            }
        }

        public bool IsBlocked(int gx, int gz)
        {
            if (gx < 0 || gx >= GridSize || gz < 0 || gz >= GridSize)
                return true;
            return _blocked[gx, gz];
        }

        public int WorldToGrid(float world) => (int)(world / CellSize);
        public float GridToWorld(int grid) => grid * CellSize + CellSize * 0.5f;

        public List<(int x, int z)> FindPath(int sx, int sz, int ex, int ez)
        {
            if (IsBlocked(ex, ez)) return new List<(int, int)>();

            var open = new PriorityQueue();
            var cameFrom = new Dictionary<int, int>();
            var gScore = new Dictionary<int, int>();
            var closed = new HashSet<int>();

            int start = Key(sx, sz);
            int end = Key(ex, ez);
            gScore[start] = 0;
            open.Enqueue(start, Heuristic(sx, sz, ex, ez));

            while (open.Count > 0)
            {
                int current = open.Dequeue();
                if (current == end)
                    return Reconstruct(cameFrom, current);

                closed.Add(current);
                int cx = current / GridSize;
                int cz = current % GridSize;

                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = cx + dx;
                        int nz = cz + dz;
                        if (IsBlocked(nx, nz)) continue;
                        if (dx != 0 && dz != 0)
                        {
                            if (IsBlocked(cx + dx, cz) || IsBlocked(cx, cz + dz))
                                continue;
                        }
                        int neighbor = Key(nx, nz);
                        if (closed.Contains(neighbor)) continue;

                        int stepCost = (dx != 0 && dz != 0) ? 14 : 10;
                        int tentativeG = gScore[current] + stepCost;

                        if (!gScore.TryGetValue(neighbor, out int existing) || tentativeG < existing)
                        {
                            cameFrom[neighbor] = current;
                            gScore[neighbor] = tentativeG;
                            int f = tentativeG + Heuristic(nx, nz, ex, ez);
                            open.Enqueue(neighbor, f);
                        }
                    }
                }
            }

            return new List<(int, int)>();
        }

        private int Key(int x, int z) => x * GridSize + z;
        private static int Heuristic(int sx, int sz, int ex, int ez)
        {
            int dx = Math.Abs(sx - ex);
            int dz = Math.Abs(sz - ez);
            return 10 * (dx + dz) + 4 * Math.Min(dx, dz);
        }

        private List<(int x, int z)> Reconstruct(Dictionary<int, int> cameFrom, int current)
        {
            var path = new List<(int, int)>();
            while (cameFrom.ContainsKey(current))
            {
                path.Add((current / GridSize, current % GridSize));
                current = cameFrom[current];
            }
            path.Reverse();
            return path;
        }

        private sealed class PriorityQueue
        {
            private readonly List<(int item, int priority)> _items = new();
            public int Count => _items.Count;

            public void Enqueue(int item, int priority)
            {
                _items.Add((item, priority));
            }

            public int Dequeue()
            {
                int bestIdx = 0;
                for (int i = 1; i < _items.Count; i++)
                    if (_items[i].priority < _items[bestIdx].priority)
                        bestIdx = i;
                int item = _items[bestIdx].item;
                _items.RemoveAt(bestIdx);
                return item;
            }
        }
    }
}
