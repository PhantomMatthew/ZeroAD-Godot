using System.Collections.Generic;
using System.Text;

namespace ZeroAD.Godot.Actors.Composition;

using ZeroAD.Godot.Actors.Variation;

public static class StructuralSignature
{
    private const int MaxDepth = 8;

    public static string Compute(ResolvedActorSpec spec)
    {
        var sb = new StringBuilder();
        BuildInto(spec, sb, depth: 0);
        return sb.ToString();
    }

    private static void BuildInto(ResolvedActorSpec spec, StringBuilder sb, int depth)
    {
        sb.Append("mesh=").Append(spec.MeshGlbPath ?? "<none>").Append(';');
        sb.Append("mat=").Append(spec.Material ?? "<none>").Append(';');

        if (spec.Props.Count == 0 || depth >= MaxDepth)
        {
            sb.Append("props=<none>;");
            return;
        }

        sb.Append("props=[");
        bool first = true;
        foreach (var kv in OrderProps(spec.Props))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(kv.Key).Append("=>");

            var childSpec = SpecMerger.MergeFromActorPath(
                ActorLoader.ResolveActorAbsPath(kv.Value.ActorPath),
                kv.Value.SubSeed,
                AssetPathResolver.Instance);

            if (childSpec == null)
            {
                sb.Append("<missing:");
                sb.Append(kv.Value.ActorPath);
                sb.Append('>');
            }
            else
            {
                BuildInto(childSpec, sb, depth + 1);
            }
        }
        sb.Append("];");
    }

    private static IEnumerable<KeyValuePair<string, PropSpec>> OrderProps(
        IReadOnlyDictionary<string, PropSpec> props)
    {
        var keys = new List<string>(props.Keys);
        keys.Sort(System.StringComparer.Ordinal);
        foreach (var k in keys)
            yield return new KeyValuePair<string, PropSpec>(k, props[k]);
    }
}
