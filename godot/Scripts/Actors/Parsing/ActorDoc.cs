using System.Collections.Generic;

namespace ZeroAD.Godot.Actors.Parsing;

// Immutable intermediate representation of a parsed actor XML.

public sealed record ActorDoc(
    string Path,
    bool CastShadow,
    string? Material,
    IReadOnlyList<VariantGroup> Groups);

public sealed record VariantGroup(IReadOnlyList<ActorVariant> Variants);

public sealed record ActorVariant(
    string Name,                       // lowercase; "" if none
    int Frequency,
    string? Mesh,                      // raw dae path or null
    IReadOnlyDictionary<string, string> Textures,  // sampler -> path
    IReadOnlyDictionary<string, PropRef> Props,    // attachpoint -> prop
    IReadOnlyList<AnimRef> Animations,
    string? Material)
{
    public static ActorVariant Empty(string name, int freq) =>
        new(
            name ?? "",
            freq,
            Mesh: null,
            Textures: EmptyDict<string, string>.Value,
            Props: EmptyDict<string, PropRef>.Value,
            Animations: EmptyList<AnimRef>.Value,
            Material: null);
}

/// <summary>Attachpoint prop entry. A null <paramref name="ActorPath"/> is a CLEAR
/// (&lt;prop attachpoint="x"/&gt; with no actor): the original erases whatever prop the
/// base variation had at that attachpoint (e.g. gather_tree hides weapon_R/shield).</summary>
public sealed record PropRef(string? ActorPath, string Attachpoint);
public sealed record AnimRef(string Name, string File, int Speed);

internal static class EmptyDict<TKey, TValue> where TKey : notnull
{
    public static readonly IReadOnlyDictionary<TKey, TValue> Value =
        new Dictionary<TKey, TValue>();
}

internal static class EmptyList<T>
{
    public static readonly IReadOnlyList<T> Value = System.Array.Empty<T>();
}
