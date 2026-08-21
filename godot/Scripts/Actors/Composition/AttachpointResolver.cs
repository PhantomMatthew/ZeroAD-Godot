using System;
using Godot;

namespace ZeroAD.Godot.Actors.Composition;

public static class AttachpointResolver
{
    private const string Root = "root";

    public static Skeleton3D? FindSkeleton(Node root)
    {
        if (root is Skeleton3D sk) return sk;
        foreach (var child in root.GetChildren())
        {
            var found = FindSkeleton(child);
            if (found != null) return found;
        }
        return null;
    }

    public static int FindBoneIndex(Skeleton3D skeleton, string attachpoint)
    {
        if (string.IsNullOrEmpty(attachpoint))
            return -1;
        foreach (var name in Candidates(attachpoint))
        {
            int idx = skeleton.FindBone(name);
            if (idx != -1)
                return idx;
        }
        return -1;
    }

    public static Node3D? FindNode(Node root, string attachpoint)
    {
        if (string.IsNullOrEmpty(attachpoint))
            return null;
        if (string.Equals(attachpoint, Root, StringComparison.OrdinalIgnoreCase))
            return root as Node3D;
        foreach (var name in Candidates(attachpoint))
        {
            var found = root.FindChild(name, recursive: true, owned: false);
            if (found is Node3D n3)
                return n3;
        }
        // 兜底:后缀匹配(剥 armature 前缀比对)——Biped_head 之类。
        return FindBySuffix(root, attachpoint);
    }

    private static string[] Candidates(string attachpoint) => new[]
    {
        attachpoint,
        "prop-" + attachpoint,
        "prop_" + attachpoint,
        // 缓存反导出的 GLB 里,骨架子节点带骨架名前缀(Biped_head/Biped_prop-head)——
        // Godot 导入 glTF 骨架时给 BoneAttachment/子节点加了 armature 前缀。挂点
        // 匹配须前缀不敏感(剥掉 <name>_ 前缀比对),否则村民/士兵的头与手持物全挂不上。
        "Biped_" + attachpoint,
        "Biped_prop-" + attachpoint,
        "Biped_prop_" + attachpoint,
    };

    private static Node3D? FindBySuffix(Node node, string attachpoint)
    {
        if (node is Node3D n3)
        {
            string n = n3.Name;
            if (n.EndsWith("-" + attachpoint, StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("_" + attachpoint, StringComparison.OrdinalIgnoreCase))
                return n3;
        }
        foreach (var child in node.GetChildren())
        {
            var found = FindBySuffix(child, attachpoint);
            if (found != null) return found;
        }
        return null;
    }
}
