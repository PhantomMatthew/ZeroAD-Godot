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
        return null;
    }

    private static string[] Candidates(string attachpoint) => new[]
    {
        attachpoint,
        "prop-" + attachpoint,
        "prop_" + attachpoint,
    };
}
