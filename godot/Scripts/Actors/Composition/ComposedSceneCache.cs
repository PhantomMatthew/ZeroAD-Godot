using System;
using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot.Actors.Composition;

public sealed class ComposedSceneCache
{
    private const int MaxEntries = 256;

    public static readonly ComposedSceneCache Instance = new();

    public sealed record Stats(int Count, int Hits, int Misses);

    private readonly Dictionary<string, Entry> _cache = new();
    private readonly LinkedList<string> _accessOrder = new();
    private readonly object _lock = new();
    private int _hits;
    private int _misses;

    public Stats GetStats()
    {
        lock (_lock) return new Stats(_cache.Count, _hits, _misses);
    }

    public int Count
    {
        get
        {
            lock (_lock) return _cache.Count;
        }
    }

    public PackedScene GetOrBuild(string structuralKey, Func<Node3D> build)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(structuralKey, out var existing))
            {
                TouchLocked(existing);
                _hits++;
                return existing.Scene;
            }
            _misses++;
        }

        Node3D temp = build() ?? throw new InvalidOperationException(
            $"ComposedSceneCache: build returned null for key '{structuralKey}'");

        SetOwnerRecursive(temp, temp);

        var packed = new PackedScene();
        Error err = packed.Pack(temp);
        temp.QueueFree();
        if (err != Error.Ok)
            throw new InvalidOperationException(
                $"ComposedSceneCache: PackedScene.Pack failed (err={err}) for key '{structuralKey}'");

        lock (_lock)
        {
            if (_cache.TryGetValue(structuralKey, out var winner))
            {
                TouchLocked(winner);
                return winner.Scene;
            }
            var node = _accessOrder.AddLast(structuralKey);
            _cache[structuralKey] = new Entry(packed, node);
            EvictLocked();
            return packed;
        }
    }

    public bool Contains(string structuralKey)
    {
        lock (_lock) return _cache.ContainsKey(structuralKey);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _accessOrder.Clear();
            _hits = 0;
            _misses = 0;
        }
    }

    private void TouchLocked(Entry entry)
    {
        _accessOrder.Remove(entry.Node);
        _accessOrder.AddLast(entry.Node);
    }

    private void EvictLocked()
    {
        while (_cache.Count > MaxEntries)
        {
            var first = _accessOrder.First;
            if (first == null) break;
            _accessOrder.RemoveFirst();
            _cache.Remove(first.Value);
        }
    }

    private static void SetOwnerRecursive(Node node, Node root)
    {
        foreach (var child in node.GetChildren())
        {
            child.Owner = root;
            SetOwnerRecursive(child, root);
        }
    }

    private sealed record Entry(PackedScene Scene, LinkedListNode<string> Node);
}
