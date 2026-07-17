using System.Collections.Concurrent;

namespace ZeroAD.Godot.Actors.Parsing;

/// <summary>
/// Thread-safe cache of parsed <see cref="ActorDoc"/> keyed by absolute actor XML path.
/// A null value indicates a confirmed miss (file missing or parse failed).
/// </summary>
public static class ActorDocCache
{
    private static readonly ConcurrentDictionary<string, ActorDoc?> _cache = new();

    public static ActorDoc? GetOrLoad(string absActorPath) =>
        _cache.GetOrAdd(absActorPath, ActorParser.Parse);

    public static void Clear() => _cache.Clear();
}
