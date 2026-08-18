using System;

namespace ZeroAD.Sim.Pathfinding;

// Binary-heap priority queue with decrease-key (promote). Ported from
// source/simulation2/helpers/PriorityQueue.h (PriorityQueueHeap).
//
// Used by LongPathfinder (JPS) and VertexPathfinder (visibility A*) as the open set.
// Replaces the O(n)-per-op LegacyPriorityQueue in ObstructionManager. Items are integer ids
// (navcell keys / vertex ids); the heap orders by a rank value (PathCost or comparable).
//
// Design: the heap stores (id, rank) pairs in an array; a parallel `positions[id]` map gives
// each id's current index so `Promote` can bubble-up in O(log n). This matches the original's
// template <typename ID, typename RANK, typename HANDLER> shape, adapted to a C# class.

/// <summary>Min-heap of int ids keyed by a long rank. Supports O(log n) push/pop and
/// O(log n) promote (decrease-rank). Capacity-bounded; callers pick a max id for the
/// position lookup. Ids must be non-negative and &lt; capacity.</summary>
public sealed class PriorityQueueHeap
{
    private struct Entry { public int Id; public long Rank; }

    private Entry[] _heap;
    private int _count;
    // id → current index in _heap (or -1 if not present). Sized by the caller's id space.
    private readonly int[] _position;

    /// <param name="maxId">Exclusive upper bound on ids (positions array is sized maxId).
    /// For navcell A*, this is the grid cell count (width*height).</param>
    public PriorityQueueHeap(int maxId)
    {
        _heap = new Entry[16];
        _count = 0;
        _position = new int[maxId];
        Array.Fill(_position, -1);
    }

    public int Count => _count;
    public bool IsEmpty => _count == 0;

    /// <summary>position 查找表容量(id 空间上限)。用于堆复用判定。</summary>
    public int Capacity => _position.Length;

    public void Clear()
    {
        for (int i = 0; i < _count; i++)
            _position[_heap[i].Id] = -1;
        _count = 0;
    }

    /// <summary>Push id with the given rank. If id is already present, this is equivalent to
    /// Promote (the lower rank wins). Returns true if the id was newly inserted.</summary>
    public bool Push(int id, long rank)
    {
        if ((uint)id >= (uint)_position.Length)
            throw new ArgumentOutOfRangeException(nameof(id), "id exceeds capacity");
        if (_position[id] >= 0)
        {
            // Already present — treat as promote if the new rank is better.
            if (rank < _heap[_position[id]].Rank) Promote(id, rank);
            return false;
        }
        if (_count == _heap.Length)
        {
            Entry[] grown = new Entry[_heap.Length * 2];
            Array.Copy(_heap, grown, _count);
            _heap = grown;
        }
        int i = _count++;
        _heap[i] = new Entry { Id = id, Rank = rank };
        _position[id] = i;
        SiftUp(i);
        return true;
    }

    /// <summary>Remove and return the id with the lowest rank.</summary>
    public int Pop()
    {
        if (_count == 0) throw new InvalidOperationException("heap empty");
        int topId = _heap[0].Id;
        _position[topId] = -1;
        int last = --_count;
        if (last > 0)
        {
            _heap[0] = _heap[last];
            _position[_heap[0].Id] = 0;
            SiftDown(0);
        }
        return topId;
    }

    /// <summary>Decrease the rank of an id already in the heap. No-op if the new rank isn't better.</summary>
    public void Promote(int id, long newRank)
    {
        int i = _position[id];
        if (i < 0) return;
        if (newRank < _heap[i].Rank)
        {
            _heap[i].Rank = newRank;
            SiftUp(i);
        }
    }

    public bool Contains(int id) => (uint)id < (uint)_position.Length && _position[id] >= 0;

    public long RankOf(int id)
    {
        int i = _position[id];
        return i >= 0 ? _heap[i].Rank : long.MaxValue;
    }

    private void SiftUp(int i)
    {
        Entry e = _heap[i];
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (!(_heap[parent].Rank > e.Rank)) break;
            _heap[i] = _heap[parent];
            _position[_heap[i].Id] = i;
            i = parent;
        }
        _heap[i] = e;
        _position[e.Id] = i;
    }

    private void SiftDown(int i)
    {
        Entry e = _heap[i];
        int half = _count >> 1;
        while (i < half)
        {
            int child = (i << 1) + 1;
            Entry c = _heap[child];
            int right = child + 1;
            if (right < _count && _heap[right].Rank < c.Rank)
            {
                child = right;
                c = _heap[child];
            }
            if (!(c.Rank < e.Rank)) break;
            _heap[i] = c;
            _position[c.Id] = i;
            i = child;
        }
        _heap[i] = e;
        _position[e.Id] = i;
    }
}
