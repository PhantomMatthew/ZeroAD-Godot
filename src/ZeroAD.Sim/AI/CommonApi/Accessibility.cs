using System.Collections.Generic;
using ZeroAD.Sim.Pathfinding;

namespace ZeroAD.Sim.AI.CommonApi;

/// <summary>地形分析 + 可达性（原版 common-api/terrain-analysis.js，392 行）。
/// TerrainAnalysis: passability grid → 8-bit 地形分类（Land/DeepWater/ShallowWater/Impassable）。
/// Accessibility: 双 flood-fill（land + naval）→ 区域 ID + regionLinks（陆↔海邻接）+
///   getAccessValue（某点的区域 ID）+ getTrajectTo（陆→海→陆路径）。
/// 逐字移植——flood-fill 是 load-bearing 的，不能"改进"。</summary>
public sealed class Accessibility
{
    private readonly byte[] _map;        // 地形分类（TerrainAnalysis 输出）
    public readonly int Width;
    public readonly int Height;
    public readonly int CellSize;
    public readonly int Length;

    private readonly ushort[] _landPassMap;
    private readonly ushort[] _navalPassMap;
    private readonly int _maxRegions = 65535;
    private readonly List<int> _regionSize = new();
    private readonly List<string> _regionType = new();
    private readonly List<List<int>> _regionLinks = new();

    /// <summary>从 passability grid 构造 TerrainAnalysis + Accessibility。</summary>
    public Accessibility(Grid<NavcellData> passabilityGrid, PassClass landMask, PassClass waterMask, int navcellsPerSide, int cellSize)
    {
        Width = navcellsPerSide;
        Height = navcellsPerSide;
        CellSize = cellSize;
        Length = Width * Height;

        // ── TerrainAnalysis: passability → 地形分类 ──
        // 原版 terrain-analysis.js:land 受阻 → IMPASSABLE;其中水(船)可通 → DEEP_WATER;
        // land 可通且水可通 → SHALLOW_WATER;land 可通水受阻 → LAND。
        _map = new byte[Length];
        var span = passabilityGrid.AsSpan();
        for (int i = 0; i < Length && i < span.Length; i++)
        {
            ushort cell = span[i];
            bool landBlocked = (cell & landMask.Mask) != 0;    // 不可陆通行
            bool waterBlocked = (cell & waterMask.Mask) != 0;  // 不可水(船)通行

            if (landBlocked)
                _map[i] = waterBlocked ? TerrainStates.Impassable : TerrainStates.DeepWater;
            else
                _map[i] = waterBlocked ? TerrainStates.Land : TerrainStates.ShallowWater;
        }

        // ── Accessibility: 双 flood-fill ──
        _landPassMap = new ushort[Length];
        _navalPassMap = new ushort[Length];
        ushort regionID = 2;  // 1 = impassable/inaccessible

        for (int i = 0; i < Length; i++)
        {
            if (_map[i] != TerrainStates.Impassable)
            {
                if (_landPassMap[i] == 0 && FloodFill(i, regionID, onWater: false))
                {
                    EnsureRegionCapacity(regionID);
                    _regionType[regionID] = "land";
                    regionID++;
                }
                if (_navalPassMap[i] == 0 && FloodFill(i, regionID, onWater: true))
                {
                    EnsureRegionCapacity(regionID);
                    _regionType[regionID] = "water";
                    regionID++;
                }
            }
            else if (_landPassMap[i] == 0)
            {
                FloodFill(i, 1, onWater: false);
                FloodFill(i, 1, onWater: true);
            }
        }

        // ── region links: 陆海邻接关系（4-连通，不含对角）──
        for (int x = 0; x < Width - 1; x++)
        {
            for (int y = 0; y < Height - 1; y++)
            {
                int idx = x + y * Width;
                int rightIdx = (x + 1) + y * Width;
                int bottomIdx = x + (y + 1) * Width;
                int thisLID = _landPassMap[idx];
                int thisNID = _navalPassMap[idx];
                int rightLID = _landPassMap[rightIdx];
                int rightNID = _navalPassMap[rightIdx];
                int bottomLID = _landPassMap[bottomIdx];
                int bottomNID = _navalPassMap[bottomIdx];

                if (thisLID > 1)
                {
                    if (rightNID > 1 && !_regionLinks[thisLID].Contains(rightNID)) _regionLinks[thisLID].Add(rightNID);
                    if (bottomNID > 1 && !_regionLinks[thisLID].Contains(bottomNID)) _regionLinks[thisLID].Add(bottomNID);
                }
                if (thisNID > 1)
                {
                    if (rightLID > 1 && !_regionLinks[thisNID].Contains(rightLID)) _regionLinks[thisNID].Add(rightLID);
                    if (bottomLID > 1 && !_regionLinks[thisNID].Contains(bottomLID)) _regionLinks[thisNID].Add(bottomLID);
                    if (thisLID > 1 && !_regionLinks[thisNID].Contains(thisLID)) _regionLinks[thisNID].Add(thisLID);
                }
            }
        }
    }

    private void EnsureRegionCapacity(int upTo)
    {
        while (_regionLinks.Count <= upTo) _regionLinks.Add(new List<int>());
        while (_regionSize.Count <= upTo) _regionSize.Add(0);
        while (_regionType.Count <= upTo) _regionType.Add("inaccessible");
    }

    /// <summary>某点的区域 ID（onWater=true 取海军区域）。impassable 时螺旋搜索邻域。</summary>
    public ushort GetAccessValue(float px, float pz, bool onWater = false)
    {
        int x = (int)(px / CellSize);
        int y = (int)(pz / CellSize);
        x = x >= Width ? Width - 1 : x < 0 ? 0 : x;
        y = y >= Height ? Height - 1 : y < 0 ? 0 : y;
        int idx = x + y * Width;

        if (onWater) return _navalPassMap[idx];
        ushort ret = _landPassMap[idx];
        if (ret == 1)
        {
            // 螺旋搜索 8 邻域
            int[][] dirs = { new[] { -1, -1 }, new[] { -1, 0 }, new[] { -1, 1 }, new[] { 0, 1 },
                             new[] { 1, 1 }, new[] { 1, 0 }, new[] { 1, -1 }, new[] { 0, -1 } };
            foreach (var d in dirs)
            {
                int nx = x + d[0], ny = y + d[1];
                if (nx < 0 || nx >= Width || ny < 0 || ny >= Height) continue;
                ret = _landPassMap[nx + ny * Width];
                if (ret != 1) return ret;
            }
        }
        return ret;
    }

    /// <summary>从 start 到 end 的区域路径（陆→海→陆序列）。用于海军运输/码头选址。
    /// 返回 null 表示不可达。</summary>
    public List<int>? GetTrajectToIndex(int istart, int iend)
    {
        if (istart == iend) return new List<int> { istart };

        var trajects = new List<List<int>> { new() { istart } };
        var explored = new HashSet<int> { istart };

        while (trajects.Count > 0)
        {
            var newTrajects = new List<List<int>>();
            foreach (var traj in trajects)
            {
                int ilast = traj[^1];
                if (ilast >= _regionLinks.Count) continue;
                foreach (int inew in _regionLinks[ilast])
                {
                    if (inew == iend)
                    {
                        traj.Add(iend);
                        return traj;
                    }
                    if (explored.Contains(inew)) continue;
                    var newTraj = new List<int>(traj) { inew };
                    newTrajects.Add(newTraj);
                    explored.Add(inew);
                }
            }
            trajects = newTrajects;
        }
        return null;
    }

    /// <summary>原版 getTrajectTo(terrain-analysis.js:158-179):世界坐标两点间的
    /// 区域路径。起点落格:陆格 >1 走陆,否则水格 >1 走水,皆不可 → null(不可达);
    /// 终点取陆格、陆不可则取水格,皆不可 → null。返回含首尾的区域 id 序列
    /// (陆海交替;海军运输的"需要哪片海的码头"判据)。</summary>
    public List<int>? GetTrajectTo(float sx, float sz, float ex, float ez)
    {
        int istart = ToIndex(sx, sz);
        int iend = ToIndex(ex, ez);

        bool onLand = true;
        if (_landPassMap[istart] <= 1 && _navalPassMap[istart] > 1)
            onLand = false;
        if (_landPassMap[istart] <= 1 && _navalPassMap[istart] <= 1)
            return null;

        int endRegion = _landPassMap[iend];
        if (endRegion <= 1 && _navalPassMap[iend] > 1)
            endRegion = _navalPassMap[iend];
        else if (endRegion <= 1)
            return null;

        int startRegion = onLand ? _landPassMap[istart] : _navalPassMap[istart];
        return GetTrajectToIndex(startRegion, endRegion);
    }

    /// <summary>世界坐标 → 格索引(边界钳制)。</summary>
    private int ToIndex(float px, float pz)
    {
        int x = (int)(px / CellSize);
        int y = (int)(pz / CellSize);
        x = x >= Width ? Width - 1 : x < 0 ? 0 : x;
        y = y >= Height ? Height - 1 : y < 0 ? 0 : y;
        return x + y * Width;
    }

    /// <summary>某格的区域类型("land"/"water"/"inaccessible";原版 regionType)。</summary>
    public string GetRegionType(int regionId) =>
        regionId >= 0 && regionId < _regionType.Count ? _regionType[regionId] : "inaccessible";

    /// <summary>陆地区域 id(0/1 = 非陆地)。</summary>
    public ushort LandRegionAt(float px, float pz) => _landPassMap[ToIndex(px, pz)];
    /// <summary>水域区域 id(0/1 = 非水域)。</summary>
    public ushort WaterRegionAt(float px, float pz) => _navalPassMap[ToIndex(px, pz)];

    /// <summary>最大水域区域的格数(0 = 无水域;Petra navalMap 判定的输入:
    /// 原版按水域规模决定是否当海图运营)。</summary>
    public int LargestWaterRegionSize()
    {
        int best = 0;
        for (int id = 2; id < _regionType.Count; id++)
            if (_regionType[id] == "water" && _regionSize[id] > best)
                best = _regionSize[id];
        return best;
    }

    /// <summary>以 (cx,cz) 为心做扩环扫描,找最近的岸线格(陆格且 4 邻接水域
    /// region>1;码头选址用)。命中输出该格中心世界坐标;扫描半径内无 → false。
    /// 确定性:半径升序、环内按 (dx,dz) 字典序。</summary>
    public bool TryFindShoreline(float cx, float cz, out float sx, out float sz,
        int maxRadiusCells = 80)
    {
        int ci = (int)(cx / CellSize), cj = (int)(cz / CellSize);
        for (int r = 1; r <= maxRadiusCells; r++)
        {
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dz)) != r) continue;
                    int x = ci + dx, y = cj + dz;
                    if (x < 1 || y < 1 || x >= Width - 1 || y >= Height - 1) continue;
                    int idx = x + y * Width;
                    if (_landPassMap[idx] <= 1) continue;
                    // 4 邻接有水域?
                    if (_navalPassMap[idx - 1] > 1 || _navalPassMap[idx + 1] > 1
                        || _navalPassMap[idx - Width] > 1 || _navalPassMap[idx + Width] > 1)
                    {
                        sx = (x + 0.5f) * CellSize;
                        sz = (y + 0.5f) * CellSize;
                        return true;
                    }
                }
        }
        sx = sz = 0;
        return false;
    }

    /// <summary>区域大小（cells 数）。</summary>
    public int GetRegionSize(int index, bool onWater)
    {
        int id = onWater ? _navalPassMap[index] : _landPassMap[index];
        return id < _regionSize.Count ? _regionSize[id] : 0;
    }

    /// <summary>扫描线 flood-fill（逐字移植 terrain-analysis.js:235-391）。
    /// value = 区域 ID；onWater 区分陆/海填充。返回 false = 已填充或不可填充。</summary>
    private bool FloodFill(int startIndex, ushort value, bool onWater)
    {
        if (value > _maxRegions)
        {
            _landPassMap[startIndex] = 1;
            _navalPassMap[startIndex] = 1;
            return false;
        }

        if (!onWater && _landPassMap[startIndex] != 0 || onWater && _navalPassMap[startIndex] != 0)
            return false;  // 已填充

        if (_map[startIndex] == TerrainStates.Impassable)
        {
            _landPassMap[startIndex] = 1;
            _navalPassMap[startIndex] = 1;
            return false;
        }

        string floodFor = "land";
        if (onWater)
        {
            if (_map[startIndex] != TerrainStates.DeepWater && _map[startIndex] != TerrainStates.ShallowWater)
            {
                _navalPassMap[startIndex] = 1;
                return false;
            }
            floodFor = "water";
        }
        else if (_map[startIndex] == TerrainStates.DeepWater)
        {
            _landPassMap[startIndex] = 1;
            return false;
        }

        EnsureRegionCapacity(value);
        int w = Width;
        int h = Height;

        var indexArray = new Stack<int>();
        indexArray.Push(startIndex);

        while (indexArray.Count > 0)
        {
            int newIndex = indexArray.Pop();
            int y = 0;
            // 向上扫描找到该列的起点
            while (true)
            {
                y--;
                int index = newIndex + w * y;
                if (index < 0) break;
                if (floodFor == "land" && _landPassMap[index] == 0
                    && _map[index] != TerrainStates.Impassable && _map[index] != TerrainStates.DeepWater)
                    continue;
                if (floodFor == "water" && _navalPassMap[index] == 0
                    && (_map[index] == TerrainStates.DeepWater || _map[index] == TerrainStates.ShallowWater))
                    continue;
                break;
            }
            y++;
            bool reachLeft = false, reachRight = false;
            int index2;
            do
            {
                index2 = newIndex + w * y;

                if (floodFor == "land" && _landPassMap[index2] == 0
                    && _map[index2] != TerrainStates.Impassable && _map[index2] != TerrainStates.DeepWater)
                {
                    _landPassMap[index2] = value;
                    _regionSize[value]++;
                }
                else if (floodFor == "water" && _navalPassMap[index2] == 0
                    && (_map[index2] == TerrainStates.DeepWater || _map[index2] == TerrainStates.ShallowWater))
                {
                    _navalPassMap[index2] = value;
                    _regionSize[value]++;
                }
                else break;

                // 左邻
                if (index2 % w > 0)
                {
                    bool leftOk = floodFor == "land"
                        ? _landPassMap[index2 - 1] == 0 && _map[index2 - 1] != TerrainStates.Impassable && _map[index2 - 1] != TerrainStates.DeepWater
                        : _navalPassMap[index2 - 1] == 0 && (_map[index2 - 1] == TerrainStates.DeepWater || _map[index2 - 1] == TerrainStates.ShallowWater);
                    if (leftOk) { if (!reachLeft) { indexArray.Push(index2 - 1); reachLeft = true; } }
                    else if (reachLeft) reachLeft = false;
                }
                // 右邻
                if (index2 % w < w - 1)
                {
                    bool rightOk = floodFor == "land"
                        ? _landPassMap[index2 + 1] == 0 && _map[index2 + 1] != TerrainStates.Impassable && _map[index2 + 1] != TerrainStates.DeepWater
                        : _navalPassMap[index2 + 1] == 0 && (_map[index2 + 1] == TerrainStates.DeepWater || _map[index2 + 1] == TerrainStates.ShallowWater);
                    if (rightOk) { if (!reachRight) { indexArray.Push(index2 + 1); reachRight = true; } }
                    else if (reachRight) reachRight = false;
                }
                y++;
            } while (index2 / w < h - 1);
        }
        return true;
    }
}
