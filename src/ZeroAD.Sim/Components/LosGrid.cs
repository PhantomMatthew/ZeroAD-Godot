using System;
using ZeroAD.Sim.Maths;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Sim.Components;

/// <summary>LOS vertex state per player (2-bit). Aligned with Los.h LosState.</summary>
public enum LosState : byte { Unexplored = 0, Explored = 1, Visible = 2 }

/// <summary>
/// Per-player line-of-sight grid. Ported from CCmpRangeManager.cpp's LOS subsystem
/// (LosAddStrip/LosRemoveStrip/LosUpdateHelper, lines 2145-2305) and Los.h.
///
/// Layout: one u32 per vertex packs 2 bits per player (player p at shift 2*(p-1),
/// max 16 players): 0=unexplored, 1=explored, 2=visible (visible implies explored).
/// A per-player u16 count grid records how many seers cover each vertex; the 0→1
/// transition sets VISIBLE|EXPLORED, 1→0 clears only VISIBLE (explored never decays).
///
/// Determinism: all circle math runs in tile-space Fixed (coordinates/range >> 2)
/// so squared distances stay far below the Q15.16 ceiling; no float, no sqrt.
/// Serialization keeps only the state grid + explored counters; counts are rebuilt
/// by re-adding seers after load (caller's job — Deserialize zeroes counts and
/// leaves the state words untouched).
/// </summary>
public sealed class LosGrid
{
    public const int TileSize = 4;
    public const int MaxPlayers = 16;

    public int VerticesPerSide { get; private set; }

    private uint[] _state = Array.Empty<uint>();
    private ushort[]?[] _counts = new ushort[MaxPlayers + 1][];
    private readonly int[] _explored = new int[MaxPlayers + 1];
    private int _totalInworld;

    public LosGrid(int worldMeters) => Reset(worldMeters);

    /// <summary>(Re)size the grid for a new map. Wipes all state (called on map load).</summary>
    public void Reset(int worldMeters)
    {
        VerticesPerSide = Math.Max(1, worldMeters / TileSize + 1);
        _state = new uint[VerticesPerSide * VerticesPerSide];
        _counts = new ushort[MaxPlayers + 1][];
        Array.Clear(_explored);
        _totalInworld = VerticesPerSide * VerticesPerSide;
    }

    // --- Queries ---

    public bool IsVisible(int player, int i, int j) =>
        (_state[j * VerticesPerSide + i] >> Shift(player) & 2) != 0;

    public bool IsExplored(int player, int i, int j) =>
        (_state[j * VerticesPerSide + i] >> Shift(player) & 1) != 0;

    public int GetCount(int player, int i, int j) =>
        _counts[player]?[j * VerticesPerSide + i] ?? 0;

    public int GetPercentExplored(int player) =>
        _totalInworld > 0 ? _explored[player] * 100 / _totalInworld : 0;

    /// <summary>Nearest vertex to a world position, clamped into the grid.</summary>
    public (int i, int j) WorldToVertex(Fixed x, Fixed z) => (ClampVert((x >> 2).ToIntRoundToNearest()),
                                                              ClampVert((z >> 2).ToIntRoundToNearest()));

    // --- Mutation ---

    /// <summary>Add a seer's vision circle (count +1 per covered vertex).</summary>
    public void AddLos(int player, Fixed x, Fixed z, Fixed range) => UpdateCircle(player, x, z, range, adding: true);

    /// <summary>Remove a seer's vision circle (count -1 per covered vertex).</summary>
    public void RemoveLos(int player, Fixed x, Fixed z, Fixed range) => UpdateCircle(player, x, z, range, adding: false);

    /// <summary>Move a seer. MVP: full remove + add (the original's incremental
    /// dual-circle diff is a later optimization, same observable result).</summary>
    public void MoveLos(int player, Fixed fromX, Fixed fromZ, Fixed toX, Fixed toZ, Fixed range)
    {
        RemoveLos(player, fromX, fromZ, range);
        AddLos(player, toX, toZ, range);
    }

    /// <summary>Zero all count grids (state words kept). Call after Deserialize;
    /// the caller then re-adds every seer to rebuild counts deterministically.</summary>
    public void RebuildCountsClear() => _counts = new ushort[MaxPlayers + 1][];

    /// <summary>把整张图对该玩家标为已探索（gamesetup "Explored Map"：
    /// 迷雾全开但无实时视野）。与视野圈共存——可见区仍由 counts 驱动。</summary>
    public void ExploreAll(int player)
    {
        if (player < 1 || player > MaxPlayers) return;
        int shift = Shift(player);
        uint exploredBit = 1u << shift;
        int n = VerticesPerSide * VerticesPerSide;
        for (int idx = 0; idx < n; idx++)
            _state[idx] |= exploredBit;
        _explored[player] = _totalInworld;
    }

    // --- Circle rasterization ---

    private void UpdateCircle(int player, Fixed x, Fixed z, Fixed range, bool adding)
    {
        if (player < 1 || player > MaxPlayers || range <= Fixed.Zero) return;
        int n = VerticesPerSide * VerticesPerSide;
        ushort[] counts = _counts[player] ??= new ushort[n];

        // Tile-space: keeps squared distances tiny (Q15.16 safe). >> 2 is exact.
        Fixed xt = x >> 2, zt = z >> 2, rt = range >> 2;
        Fixed r2 = rt.Square();

        int jMin = ClampVert((zt - rt).ToIntRoundToInfinity());
        int jMax = ClampVert((zt + rt).ToIntRoundToNegInfinity());
        int xcenter = ClampVert(xt.ToIntRoundToNearest());

        int i0 = xcenter, i1 = xcenter;
        for (int j = jMin; j <= jMax; j++)
        {
            Fixed dy = Fixed.FromInt(j) - zt;
            Fixed dy2 = dy.Square();

            // Left edge: expand while the next vertex is inside, then shrink while outside.
            while (i0 > 0 && Inside(i0 - 1, dy2, xt, r2)) i0--;
            while (i0 <= xcenter && !Inside(i0, dy2, xt, r2)) i0++;
            // Right edge: same, mirrored.
            while (i1 < VerticesPerSide - 1 && Inside(i1 + 1, dy2, xt, r2)) i1++;
            while (i1 >= xcenter && !Inside(i1, dy2, xt, r2)) i1--;
            if (i0 > i1 || !Inside(i0, dy2, xt, r2)) continue; // empty boundary row

            ApplyStrip(player, counts, j, i0, i1, adding);
        }
    }

    private static bool Inside(int i, Fixed dy2, Fixed xt, Fixed r2)
    {
        Fixed dx = Fixed.FromInt(i) - xt;
        return dy2 + dx.Square() <= r2;
    }

    private void ApplyStrip(int player, ushort[] counts, int j, int i0, int i1, bool adding)
    {
        int shift = Shift(player);
        uint visibleBit = 2u << shift;
        uint bothBits = 3u << shift;
        int rowBase = j * VerticesPerSide;
        if (adding)
        {
            for (int i = i0; i <= i1; i++)
            {
                int idx = rowBase + i;
                if (counts[idx]++ == 0)
                {
                    if ((_state[idx] >> shift & 1) == 0) _explored[player]++;
                    _state[idx] |= bothBits; // VISIBLE|EXPLORED
                }
            }
        }
        else
        {
            for (int i = i0; i <= i1; i++)
            {
                int idx = rowBase + i;
                if (counts[idx] == 0) continue; // mismatched remove guard
                if (--counts[idx] == 0)
                    _state[idx] &= ~visibleBit; // clear VISIBLE, keep EXPLORED
            }
        }
    }

    // --- Serialization (state grid + explored counters only; counts rebuilt on load) ---

    public void Serialize(ISerializer s)
    {
        s.NumberI32("verts", VerticesPerSide);
        s.NumberI32("inworld", _totalInworld);
        for (int p = 1; p <= MaxPlayers; p++)
            s.NumberI32("expl", _explored[p]);
        var bytes = new byte[_state.Length * 4];
        Buffer.BlockCopy(_state, 0, bytes, 0, bytes.Length);
        s.NumberI32("stateLen", _state.Length);
        s.RawBytes("state", bytes);
    }

    public void Deserialize(IDeserializer d)
    {
        int verts = d.NumberI32("verts");
        if (verts != VerticesPerSide) Reset((verts - 1) * TileSize);
        _totalInworld = d.NumberI32("inworld");
        for (int p = 1; p <= MaxPlayers; p++)
            _explored[p] = d.NumberI32("expl");
        int stateLen = d.NumberI32("stateLen");
        var bytes = new byte[stateLen * 4];
        d.RawBytes("state", bytes);
        _state = new uint[stateLen];
        Buffer.BlockCopy(bytes, 0, _state, 0, bytes.Length);
        RebuildCountsClear(); // counts come back via caller re-adding seers
    }

    // --- Helpers ---

    private static int Shift(int player) => 2 * (player - 1);

    private int ClampVert(int v) => Math.Clamp(v, 0, VerticesPerSide - 1);
}
