using System;
using System.Collections.Generic;
using ZeroAD.Sim.Maths;

namespace ZeroAD.Sim.Components;

/// <summary>
/// 领土管理器(对齐原版 CCmpTerritoryManager —— 原版是 C++ 组件,本仓无源码,按 JS 调用点
/// 契约重建:TerritoryDecay.js / BuildRestrictions.js 的 GetOwner/IsConnected/IsTerritoryBlinking)。
/// 4m cell 网格,每 cell 记录 owner(0=gaia)+ connected(区域含 root 锚点)。
///
/// 影响力模型(确定性整数定点):每个持 <see cref="TerritoryInfluenceComponent"/> 且有主实体
/// 按放射衰减 stamp —— influence += weight × (r²−d²)/r²(d&lt;r);cell owner = 影响力最大
/// 且 &gt;0 的玩家(玩家升序 + 严格大于 → 平手小编号优先)。连通性:BFS 同 owner 4 邻区域,
/// 含该 owner 的 root cell 才 connected;未连通 = 原版 "blinking"。
///
/// 脏标记惰性重算(对齐原版 m_Dirty + 访问时 Recalculate):监听 ComponentManager 四类实体
/// 通知,任何查询先 EnsureComputed。重算是序列化状态的纯函数 → 各端同点查询结果一致;
/// 网格为派生态,不进 OOS hash(同 RangeManager/LosGrid 惯例),存档后首轮查询自动重算。
/// </summary>
public sealed class TerritoryManager
{
    /// <summary>Metres per territory cell.</summary>
    public const int CellSize = 4;

    /// <summary>网格边长(cell 数),表现层纹理尺寸用。</summary>
    public int GridWidth => _gridW;

    private const int MaxPlayers = LosGrid.MaxPlayers;
    private readonly ComponentManager _cm;
    private int _gridW;
    private byte[] _owner = Array.Empty<byte>();
    private bool[] _connected = Array.Empty<bool>();
    private bool _dirty = true;
    // TerritoryDecay 组件驱动的 blink 覆盖(cell → 闪烁):原版 SetTerritoryBlinking。
    // 非派生态(重算不清),SetBounds 才清;仅影响表现层,不影响建造判定(走 IsConnected)。
    private readonly Dictionary<int, bool> _blinkOverride = new();

    /// <summary>派生网格/覆盖每次变化自增,表现层据以重建纹理;不进 OOS hash。</summary>
    public int Version { get; private set; }

    public TerritoryManager(ComponentManager cm, int worldMeters)
    {
        _cm = cm;
        SetBounds(worldMeters);
        // 影响实体几乎全是静态建筑;任何实体增删/换主/移动都可能改变领土,统一置脏
        // (重算便宜且稀有,条件判脏反而脆弱 —— 销毁通知到达时组件已移除)。
        cm.EntityCreated += _ => _dirty = true;
        cm.EntityDestroyed += _ => _dirty = true;
        cm.OwnerChanged += (_, _, _) => _dirty = true;
        cm.PositionChanged += (_, _, _) => _dirty = true;
    }

    /// <summary>地图加载后重设世界尺寸(对齐 RangeManager.SetBounds 调用点)。</summary>
    public void SetBounds(int worldMeters)
    {
        _gridW = Math.Max(1, worldMeters / CellSize);
        int n = _gridW * _gridW;
        _owner = new byte[n];
        _connected = new bool[n];
        _blinkOverride.Clear();
        _dirty = true;
        Version++;
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

    /// <summary>原版 BuildRestrictions 的 isConnected = !IsTerritoryBlinking 契约,叠加
    /// TerritoryDecay 的 SetTerritoryBlinking 覆盖(未连通但获盟军连通背书时不闪)。</summary>
    public bool IsTerritoryBlinking(Fixed x, Fixed z)
    {
        EnsureComputed();
        int idx = CellIndex(x, z);
        if (idx < 0) return false;
        if (_blinkOverride.TryGetValue(idx, out bool blink)) return blink;
        return !_connected[idx] && _owner[idx] != 0;
    }

    /// <summary>原版 CCmpTerritoryManager::SetTerritoryBlinking:TerritoryDecay 逐实体覆盖
    /// 某 cell 的闪烁(gaia/无主 cell 忽略——原版 blinking 只对有主领土有意义)。</summary>
    public void SetTerritoryBlinking(Fixed x, Fixed z, bool blinking)
    {
        EnsureComputed();
        int idx = CellIndex(x, z);
        if (idx < 0 || _owner[idx] == 0) return;
        if (_blinkOverride.TryGetValue(idx, out bool cur) && cur == blinking) return;
        _blinkOverride[idx] = blinking;
        Version++;
    }

    /// <summary>原版 CCmpTerritoryManager::GetNeighbours(pos, onlyConnected):对 pos 所在
    /// 同主区域 BFS,统计区域 4 邻外侧每玩家的相邻 cell 数(下标 0=gaia)。
    /// TerritoryDecay 据它决定 decay 的 CP 流向(分给连通邻主,无邻居则归 gaia)。</summary>
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
            int cx = c % w, cz = c / w;
            if (cx > 0) Visit(c - 1);
            if (cx < w - 1) Visit(c + 1);
            if (cz > 0) Visit(c - w);
            if (cz < w - 1) Visit(c + w);

            void Visit(int nb)
            {
                if (_owner[nb] == owner)
                {
                    if (!visited[nb]) { visited[nb] = true; queue.Enqueue(nb); }
                    return;
                }
                if (onlyConnected && !_connected[nb]) return;
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

    private void Recompute()
    {
        _dirty = false;
        int w = _gridW, n = w * w;
        var influence = new long[MaxPlayers + 1][];
        var rootCell = new bool[MaxPlayers + 1][];
        for (int p = 1; p <= MaxPlayers; p++) { influence[p] = new long[n]; rootCell[p] = new bool[n]; }

        // --- 影响力 stamp(AllEntities 存储序 → 确定性)---
        foreach (var e in _cm.AllEntities)
        {
            var ti = _cm.QueryInterface<TerritoryInfluenceComponent>(e);
            if (ti == null || ti.Radius <= Fixed.Zero) continue;
            var own = _cm.QueryInterface<OwnershipComponent>(e);
            if (own == null || own.PlayerId < 1 || own.PlayerId > MaxPlayers) continue;
            var pos = _cm.QueryInterface<PositionComponent>(e);
            if (pos == null) continue;
            Stamp(influence[own.PlayerId], rootCell[own.PlayerId],
                pos.Position.X.InternalValue, pos.Position.Z.InternalValue,
                ti.Radius.InternalValue, ti.Weight, ti.Root, w);
        }

        // --- argmax 定主(升序严格大于 → 平手小编号优先)---
        for (int i = 0; i < n; i++)
        {
            long best = 0;
            byte bestPlayer = 0;
            for (byte p = 1; p <= MaxPlayers; p++)
            {
                long v = influence[p][i];
                if (v > best) { best = v; bestPlayer = p; }
            }
            _owner[i] = bestPlayer;
        }

        // --- BFS 同主区域连通性:区域含该 owner 的 root cell 才 connected ---
        Array.Clear(_connected, 0, n);
        var visited = new bool[n];
        var region = new List<int>();
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            if (visited[i] || _owner[i] == 0) continue;
            int owner = _owner[i];
            bool hasRoot = false;
            region.Clear();
            queue.Clear();
            visited[i] = true;
            queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                region.Add(c);
                if (rootCell[owner][c]) hasRoot = true;
                int cx = c % w, cz = c / w;
                if (cx > 0 && !visited[c - 1] && _owner[c - 1] == owner) { visited[c - 1] = true; queue.Enqueue(c - 1); }
                if (cx < w - 1 && !visited[c + 1] && _owner[c + 1] == owner) { visited[c + 1] = true; queue.Enqueue(c + 1); }
                if (cz > 0 && !visited[c - w] && _owner[c - w] == owner) { visited[c - w] = true; queue.Enqueue(c - w); }
                if (cz < w - 1 && !visited[c + w] && _owner[c + w] == owner) { visited[c + w] = true; queue.Enqueue(c + w); }
            }
            if (hasRoot)
                foreach (int c in region) _connected[c] = true;
        }
        Version++;
    }

    /// <summary>放射衰减 stamp:falloff = (r²−d²)/r²(d&lt;r),16.16 定点整数,无浮点。
    /// 仅 <paramref name="root"/> 实体(CC 等 Root=true)在 home cell 落 root 锚点。</summary>
    private void Stamp(long[] influence, bool[] rootCell, long exInt, long ezInt, long rInt, int weight, bool root, int w)
    {
        long r2 = (rInt * rInt) >> 16;                       // 16.16
        if (r2 <= 0) return;

        int homeCx = (int)(exInt >> 16) / CellSize;
        int homeCz = (int)(ezInt >> 16) / CellSize;
        if (root && homeCx >= 0 && homeCz >= 0 && homeCx < w && homeCz < w)
            rootCell[homeCz * w + homeCx] = true;

        int reach = (int)((rInt >> 16) / CellSize) + 1;      // 半径覆盖的 cell 数(上取)
        int x0 = Math.Max(0, homeCx - reach), x1 = Math.Min(w - 1, homeCx + reach);
        int z0 = Math.Max(0, homeCz - reach), z1 = Math.Min(w - 1, homeCz + reach);
        for (int cz = z0; cz <= z1; cz++)
        {
            long dz = ((long)(cz * CellSize + CellSize / 2) << 16) - ezInt;
            long dz2 = (dz * dz) >> 16;
            if (dz2 >= r2) continue;
            for (int cx = x0; cx <= x1; cx++)
            {
                long dx = ((long)(cx * CellSize + CellSize / 2) << 16) - exInt;
                long d2 = ((dx * dx) >> 16) + dz2;
                if (d2 >= r2) continue;
                // inc = weight × (r2−d2)/r2,全 16.16 定点(结果 ≤ weight<<16,存 long)。
                influence[cz * w + cx] += weight * (((r2 - d2) << 16) / r2);
            }
        }
    }
}
