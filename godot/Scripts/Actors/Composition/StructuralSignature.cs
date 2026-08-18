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
        IReadOnlyList<KeyValuePair<string, PropSpec>> props)
    {
        // 保序遍历(同 attachpoint 多个并存);签名按 (attachpoint,actorPath) 排序保证确定性,
        // 缓存 key 不因同 attachpoint 多 prop 而抖动。
        foreach (var kv in props.OrderBy(p => p.Key, System.StringComparer.Ordinal)
                                .ThenBy(p => p.Value.ActorPath, System.StringComparer.Ordinal))
            yield return kv;
    }
}
