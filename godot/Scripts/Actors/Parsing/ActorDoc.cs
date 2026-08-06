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
    string? Material,
    ColorVec? Color = null,            // <color>r g b</color> object-color tint (hair etc.)
    DecalSpec? Decal = null,           // <decal/> 贴花(无 mesh 的地面贴图变体)
    string? Particles = null)          // <particles file="x"/> 粒子系统(不可移植,跳过渲染)
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

/// <summary>0 A.D. &lt;decal&gt; 贴花定义(原版走贴花渲染器,非 mesh)——平躺 quad + baseTex。
/// angle 单位为弧度;offsetx/z 为地面偏移(米)。</summary>
public sealed record DecalSpec(float Width, float Depth, float Angle, float OffsetX, float OffsetZ);

/// <summary>0 A.D. &lt;color&gt; variant field: an 0-255 RGB tint multiplied into
/// objectcolor-material regions where baseTex alpha is 0 (male hair on props heads).</summary>
public readonly record struct ColorVec(byte R, byte G, byte B);

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
