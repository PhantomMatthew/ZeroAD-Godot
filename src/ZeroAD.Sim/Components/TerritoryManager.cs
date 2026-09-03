using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Components;

/// <summary>
/// 领土管理器——对齐原版 CCmpTerritoryManager(CCmpTerritoryManager.cpp:425-661)逐字移植:
/// 8m 领土瓦片(navcell×8),影响力 = 成本加权 FIFO 洪泛(非闭式径向戳):
///   relativeFalloff = weight×8/radius(整数地板);逐格 falloff = relativeFalloff ×
///   costGrid(不可通行=4,界外=255,正常=1;对角 ×362/256≈√2);Dijkstra 式再松弛
///   (新成本不优于已存值即不扩);cell 主 = 累计权严格最大(玩家升序+严格 > →
///   平手小编号优先);root 洪泛(8 向,同主格)置 connected 位并计数
///   (cost < impassable 才计)供 GetTerritoryPercentage。
/// blink:纯 SetTerritoryBlinking 驱动(重算清空 → 末段逐 TerritoryDecay 实体
/// IsConnected 重导,上游 SetBlinkingEntities 同款;不再有 !connected 自动兜底)。
///
/// 脏标记惰性重算(原版 MakeDirty + 查询时 Calculate):只对有 TerritoryInfluence 的
/// 实体置脏(原版 MakeDirtyIfRelevantEntity;销毁时组件已摘除 → 销毁恒脏,稀有)。
/// 派生态不进 OOS hash;存档后首轮查询自动重算。
/// 无寻路网格时(纯内核测试)退化为全通行成本网格(上游此时直接不算——我们为
/// 测试/无头可玩性保留均匀成本,记录在案)。
/// </summary>
public sealed class TerritoryManager
{
    /// <summary>Metres per territory cell(原版 NAVCELL_SIZE×NAVCELLS_PER_TERRITORY_TILE=8)。</summary>
    public const int CellSize = 8;

    /// <summary>网格边长(cell 数),表现层纹理尺寸用。</summary>
    public int GridWidth => _gridW;

    /// <summary>领土归属网格的只读访问（AI 的 territoryMap.data[i] 等价物）。
    /// 行主序 byte 数组：index = z * GridWidth + x，值 = owner player id（0=gaia）。</summary>
    public ReadOnlySpan<byte> OwnerGrid => _owner.AsSpan();

    private const int MaxPlayers = LosGrid.MaxPlayers;
    private const int ImpassableCost = 4;   // territorymanager.xml ImpassableCost
    private const int OffWorldCost = 255;

    private readonly ComponentManager _cm;
    private int _gridW;
    private byte[] _owner = Array.Empty<byte>();
    private bool[] _connected = Array.Empty<bool>();
    private bool[] _blinking = Array.Empty<bool>();
    private int[] _cellCounts = new int[MaxPlayers + 1];   // connected 且可通行的 cell 数
    private int _totalPassable;
    private bool _dirty = true;

    /// <summary>派生网格/覆盖每次变化自增,表现层据以重建纹理;不进 OOS hash。</summary>
    public int Version { get; private set; }

    public TerritoryManager(ComponentManager cm, int worldMeters)
    {
        _cm = cm;
        SetBounds(worldMeters);
        // 原版 MakeDirtyIfRelevantEntity:只有 TerritoryInfluence 实体的增删/换主/移动
        // 才弄脏;销毁通知到达时组件已移除 → 销毁恒脏(稀有,可承受)。
        cm.EntityCreated += e =>
        {
            if (cm.QueryInterface<TerritoryInfluenceComponent>(e) != null) _dirty = true;
        };
        cm.EntityDestroyed += _ => _dirty = true;
        cm.OwnerChanged += (e, _, _) =>
        {
            if (cm.QueryInterface<TerritoryInfluenceComponent>(e) != null) _dirty = true;
        };
        cm.PositionChanged += (e, _, _) =>
        {
            if (cm.QueryInterface<TerritoryInfluenceComponent>(e) != null) _dirty = true;
        };
    }

    /// <summary>地图加载后重设世界尺寸(对齐 RangeManager.SetBounds 调用点)。</summary>
    public void SetBounds(int worldMeters)
    {
        _gridW = Math.Max(1, worldMeters / CellSize);
        int n = _gridW * _gridW;
        _owner = new byte[n];
        _connected = new bool[n];
        _blinking = new bool[n];
        _cellCounts = new int[MaxPlayers + 1];
        _dirty = true;
        Version++;
    }

    /// <summary>位打包领土网格快照(边界描线用;位布局 = 上游 ICmpTerritoryManager:
    /// owner 0-4 | connected 位5 | blink 位6)。派生快照,调用方按 Version 门控重建。</summary>
    public byte[] GetBoundaryGridSnapshot()
    {
        EnsureComputed();
        int n = _gridW * _gridW;
        var packed = new byte[n];
        for (int i = 0; i < n; i++)
        {
            byte v = _owner[i];
            if (_connected[i]) v |= TerritoryBoundaryCalculator.ConnectedMask;
            if (_blinking[i]) v |= TerritoryBoundaryCalculator.BlinkingMask;
            packed[i] = v;
        }
        return packed;
    }

    /// <summary>按 cell 索引读 owner(AI 的 territoryMap.data[i] 等价物;越界 = gaia)。</summary>
    public int GetOwnerByIndex(int idx)
    {
        EnsureComputed();
        return idx >= 0 && idx < _owner.Length ? _owner[idx] : 0;
    }

    /// <summary>按 cell 索引读连通性(越界 = false)。</summary>
    public bool IsConnectedByIndex(int idx)
    {
        EnsureComputed();
        return idx >= 0 && idx < _connected.Length && _connected[idx];
    }

    /// <summary>世界坐标(米)处的领土 owner;越界 = gaia(0)。</summary>
    public int GetOwner(Fixed x, Fixed z)
    {
        EnsureComputed();
        int idx = CellIndex(x, z);
        return idx < 0 ? 0 : _owner[idx];
    }

    /// <summary>世界坐标(米)处领土区域是否连通到 root 锚点;越界/gaia = false。</summary>
    public bool IsConnected(Fixed x, Fixed z)
    {
        EnsureComputed();
        int idx = CellIndex(x, z);
        return idx >= 0 && _connected[idx];
    }

    /// <summary>原版 IsTerritoryBlinking:纯位读(重算清空,decay 实体重导)。</summary>
    public bool IsTerritoryBlinking(Fixed x, Fixed z)
    {
        EnsureComputed();
        int idx = CellIndex(x, z);
        return idx >= 0 && _blinking[idx];
    }

    /// <summary>原版 SetTerritoryBlinking(CCmpTerritoryManager.cpp:841-871):从 (x,z) 起
    /// 8 向洪泛同主格,整区置 blink 位(不是单格!)。无主/越界忽略。</summary>
    public void SetTerritoryBlinking(Fixed x, Fixed z, bool blinking)
    {
        EnsureComputed();
        int start = CellIndex(x, z);
        if (start < 0 || _owner[start] == 0) return;
        byte owner = _owner[start];
        int w = _gridW;
        if (_blinking[start] == blinking)
        {
            // 洪泛仍须走完(同区可能有未同步格);快速路径:抽查无差即返回——
            // 原版直接整区洪泛置位,此处照办(不设快速路径,保行为一致)。
        }
        var visited = new bool[w * w];
        var queue = new Queue<int>();
        visited[start] = true;
        queue.Enqueue(start);
        bool changed = false;
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            if (_blinking[c] != blinking) { _blinking[c] = blinking; changed = true; }
            foreach (int nb in Neighbours8(c, w))
            {
                if (visited[nb] || _owner[nb] != owner) continue;
                visited[nb] = true;
                queue.Enqueue(nb);
            }
        }
        if (changed) Version++;
    }

    /// <summary>原版 GetTerritoryPercentage:连通可通行 cell 占比(全体可通行格为分母)。</summary>
    public int GetTerritoryPercentage(int player)
    {
        EnsureComputed();
        if (_totalPassable == 0 || player < 1 || player > MaxPlayers) return 0;
        return _cellCounts[player] * 100 / _totalPassable;
    }

    /// <summary>原版 GetNeighbours(pos, onlyConnected):pos 所在同主区域 8 向洪泛,
    /// 统计区域外沿每玩家的相邻 cell 数(下标 0=gaia;onlyConnected 只数连通格)。
    /// TerritoryDecay 据它决定 decay 的 CP 流向。</summary>
    public int[] GetNeighbours(Fixed x, Fixed z, bool onlyConnected)
    {
        var counts = new int[MaxPlayers + 1];
        EnsureComputed();
        int start = CellIndex(x, z);
        if (start < 0 || _owner[start] == 0) return counts;
        byte owner = _owner[start];
        int w = _gridW;
        var visited = new bool[w * w];
        var queue = new Queue<int>();
        visited[start] = true;
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            foreach (int nb in Neighbours8(c, w))
            {
                if (_owner[nb] == owner)
                {
                    if (!visited[nb]) { visited[nb] = true; queue.Enqueue(nb); }
                    continue;
                }
                if (onlyConnected && !_connected[nb]) continue;
                counts[_owner[nb]]++;
            }
        }
        return counts;
    }

    /// <summary>
    /// 建造领土限制(逐行对齐 BuildRestrictions.js:186-240):own 需 "own"(未连通还需
    /// "neutral");互盟需 "ally"(未连通还需 "neutral");gaia 需 "neutral";其余 = 敌方
    /// 需 "enemy"。<paramref name="territoryTokens"/> 为空 = 无限制(非建筑)。
    /// </summary>
    public bool CanBuildHere(string territoryTokens, int playerId, Fixed x, Fixed z)
    {
        if (string.IsNullOrWhiteSpace(territoryTokens)) return true;
        int tileOwner = GetOwner(x, z);
        bool connected = IsConnected(x, z);
        if (tileOwner == playerId)
            return Has(territoryTokens, "own") && (connected || Has(territoryTokens, "neutral"));
        if (tileOwner > 0 && _cm.Players.GetMutualAllies(playerId).Contains(tileOwner))
            return Has(territoryTokens, "ally") && (connected || Has(territoryTokens, "neutral"));
        if (tileOwner == 0)
            return Has(territoryTokens, "neutral");
        return Has(territoryTokens, "enemy");
    }

    private static bool Has(string tokens, string token)
    {
        foreach (var t in tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(t, token, StringComparison.Ordinal)) return true;
        return false;
    }

    private int CellIndex(Fixed x, Fixed z)
    {
        int cx = x.ToIntRoundToNegInfinity() / CellSize;
        int cz = z.ToIntRoundToNegInfinity() / CellSize;
        if (cx < 0 || cz < 0 || cx >= _gridW || cz >= _gridW) return -1;
        return cz * _gridW + cx;
    }

    private void EnsureComputed()
    {
        if (_dirty) Recompute();
    }

    /// <summary>8 向邻居(原版 Floodfill 的 neighbours 表)。</summary>
    private static IEnumerable<int> Neighbours8(int c, int w)
    {
        int cx = c % w, cz = c / w;
        if (cx + 1 < w) yield return c + 1;
        if (cx > 0) yield return c - 1;
        if (cz + 1 < w) yield return c + w;
        if (cz > 0) yield return c - w;
        if (cx + 1 < w && cz + 1 < w) yield return c + 1 + w;
        if (cx > 0 && cz > 0) yield return c - 1 - w;
        if (cx + 1 < w && cz > 0) yield return c + 1 - w;
        if (cx > 0 && cz + 1 < w) yield return c - 1 + w;
    }

    // ── 成本网格(原版 CalculateCostGrid:8×8 navcell 下采样 OR)──

    private byte[] ComputeCostGrid()
    {
        var pf = SimSystem.Pathfinder;
        var grid = pf?.PassabilityGrid;
        int w = _gridW;
        var cost = new byte[w * w];
        if (grid == null)
        {
            // 无寻路网格(纯内核测试):全通行(见类注释的背离记录)。
            Array.Fill(cost, (byte)1);
            _totalPassable = w * w;
            return cost;
        }
        var territoryMask = pf!.GetPassabilityClassMask("default-terrain-only");
        var unrestrictedMask = pf.GetPassabilityClassMask("unrestricted");
        const int ratio = CellSize;   // 8 navcell per territory tile(1m navcell)
        _totalPassable = 0;
        for (int cz = 0; cz < w; cz++)
            for (int cx = 0; cx < w; cx++)
            {
                bool terrPass = false, freePass = false;
                int nx0 = cx * ratio, nz0 = cz * ratio;
                for (int dz = 0; dz < ratio && !(terrPass && freePass); dz++)
                    for (int dx = 0; dx < ratio; dx++)
                    {
                        int nx = nx0 + dx, nz = nz0 + dz;
                        if (nx >= grid.W || nz >= grid.H) continue;
                        var cell = grid.Get(nx, nz);
                        if (Pathfinding.PathfindingCore.IsPassable(cell, territoryMask)) terrPass = true;
                        if (Pathfinding.PathfindingCore.IsPassable(cell, unrestrictedMask)) freePass = true;
                    }
                if (!freePass) cost[cz * w + cx] = OffWorldCost;
                else if (!terrPass) cost[cz * w + cx] = ImpassableCost;
                else { cost[cz * w + cx] = 1; _totalPassable++; }
            }
        return cost;
    }

    // ── 主重算(原版 CalculateTerritories 逐字)──

    private void Recompute()
    {
        _dirty = false;
        int w = _gridW, n = w * w;
        var cost = ComputeCostGrid();

        Array.Clear(_owner, 0, n);
        Array.Clear(_connected, 0, n);
        Array.Clear(_blinking, 0, n);
        Array.Clear(_cellCounts, 0, _cellCounts.Length);

        // 影响力实体按主分桶(升序玩家 → 平手小编号优先)。
        var influenceEntities = new SortedDictionary<int, List<EntityId>>();
        var rootEntities = new List<EntityId>();
        foreach (var e in _cm.AllEntities)
        {
            var ti = _cm.QueryInterface<TerritoryInfluenceComponent>(e);
            if (ti == null) continue;
            var own = _cm.QueryInterface<OwnershipComponent>(e);
            if (own == null || own.PlayerId < 1 || own.PlayerId > MaxPlayers) continue;
            if (ti.Weight == 0 || ti.Radius <= Fixed.Zero) continue;
            var pos = _cm.QueryInterface<PositionComponent>(e);
            if (pos == null || !pos.InWorld) continue;
            if (!influenceEntities.TryGetValue(own.PlayerId, out var list))
                influenceEntities[own.PlayerId] = list = new List<EntityId>();
            list.Add(e);
            if (ti.Root) rootEntities.Add(e);
        }

        var bestWeight = new uint[n];
        foreach (var (owner, ents) in influenceEntities)
        {
            var entityGrid = new uint[n];
            var playerGrid = new uint[n];
            foreach (var ent in ents)
            {
                var ti = _cm.QueryInterface<TerritoryInfluenceComponent>(ent)!;
                var pos = _cm.QueryInterface<PositionComponent>(ent)!;
                uint originWeight = (uint)ti.Weight;
                uint radius = (uint)ti.Radius.ToIntRoundToZero();
                if (originWeight == 0 || radius == 0) continue;
                // relativeFalloff = originWeight × 8 / radius(整数地板;上游 ToInt_RoundToNegInfinity)。
                uint relativeFalloff = originWeight * (uint)CellSize / radius;

                int homeCx = pos.Position.X.ToIntRoundToNegInfinity() / CellSize;
                int homeCz = pos.Position.Z.ToIntRoundToNegInfinity() / CellSize;
                if (homeCx < 0 || homeCz < 0 || homeCx >= w || homeCz >= w) continue;

                uint playerB = (uint)owner;
                InfluenceFlood(homeCx, homeCz, w, cost, entityGrid, playerGrid, bestWeight,
                    originWeight, relativeFalloff, (byte)playerB);
                Array.Clear(entityGrid, 0, n);
            }
        }

        // root 连通洪泛(8 向同主;置 connected + 计数可通行格)。
        foreach (var ent in rootEntities)
        {
            var own = _cm.QueryInterface<OwnershipComponent>(ent)!;
            var pos = _cm.QueryInterface<PositionComponent>(ent)!;
            int hx = pos.Position.X.ToIntRoundToNegInfinity() / CellSize;
            int hz = pos.Position.Z.ToIntRoundToNegInfinity() / CellSize;
            if (hx < 0 || hz < 0 || hx >= w || hz >= w) continue;
            int start = hz * w + hx;
            if (_owner[start] != (byte)own.PlayerId || _connected[start]) continue;
            var queue = new Queue<int>();
            queue.Enqueue(start);
            _connected[start] = true;
            if (cost[start] < ImpassableCost) _cellCounts[own.PlayerId]++;
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                foreach (int nb in Neighbours8(c, w))
                {
                    if (_connected[nb] || _owner[nb] != (byte)own.PlayerId) continue;
                    _connected[nb] = true;
                    if (cost[nb] < ImpassableCost) _cellCounts[own.PlayerId]++;
                    queue.Enqueue(nb);
                }
            }
        }

        // blink 重导(原版末段 SetBlinkingEntities:逐 decay 实体 IsConnected,
        // 内部含盟友背书 + SetTerritoryBlinking 洪泛)。
        foreach (var e in _cm.AllEntities)
        {
            var decay = _cm.QueryInterface<TerritoryDecayComponent>(e);
            if (decay != null)
                decay.IsConnected(_cm, this);
        }
        Version++;
    }

    /// <summary>原版影响力洪泛(Floodfill + decider,CCmpTerritoryManager.cpp:554-590 逐字):
    /// FIFO 队列 + 再松弛(新成本不优于已存即不扩);weight = 前驱 − falloff;
    /// totalWeight = weight + (playerGrid − entityGrid 旧值);严格大于才换主。</summary>
    private void InfluenceFlood(int hx, int hz, int w, byte[] cost, uint[] entityGrid,
        uint[] playerGrid, uint[] bestWeight, uint originWeight, uint relativeFalloff, byte owner)
    {
        int n = w * w;
        int origin = hz * w + hx;
        // 首格(decider 的 current=null 分支):weight = originWeight。
        entityGrid[origin] = originWeight;
        playerGrid[origin] += originWeight;
        if (originWeight > bestWeight[origin])
        {
            bestWeight[origin] = originWeight;
            _owner[origin] = owner;
        }

        var queue = new Queue<int>();
        queue.Enqueue(origin);
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            int cx = current % w, cz = current / w;
            foreach (int nb in Neighbours8(current, w))
            {
                bool diagonal = (nb % w) != cx && (nb / w) != cz;
                uint falloffPerTile = relativeFalloff * cost[nb];
                uint falloff = diagonal ? (falloffPerTile * 362) / 256 : falloffPerTile;

                // 新成本不优于已存 → 不扩(排列防下溢)。
                if (entityGrid[current] <= entityGrid[nb] + falloff) continue;

                uint weight = entityGrid[current] - falloff;
                uint totalWeight = weight + (playerGrid[nb] - entityGrid[nb]);
                playerGrid[nb] = totalWeight;
                entityGrid[nb] = weight;
                if (totalWeight > bestWeight[nb])
                {
                    bestWeight[nb] = totalWeight;
                    _owner[nb] = owner;
                }
                queue.Enqueue(nb);
            }
        }
        _ = n;
    }
}
