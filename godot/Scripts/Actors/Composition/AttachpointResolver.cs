using System;
using Godot;

namespace ZeroAD.Godot.Actors.Composition;

/// <summary>
/// Resolves an attachpoint name to a Godot node inside an instantiated actor scene.
/// Matches the 3 conventions found in 0 A.D. GLBs: exact name, "prop-NAME" (biped/infantry),
/// and "prop_NAME" (horse). "root" maps to the scene root.
/// </summary>
public static class AttachpointResolver
{
    private const string Root = "root";

    public static Node3D? FindAttachpoint(Node root, string attachpoint)
    {
        if (string.IsNullOrEmpty(attachpoint))
            return null;

        if (string.Equals(attachpoint, Root, StringComparison.OrdinalIgnoreCase))
            return root as Node3D;

        string[] candidates =
        {
            attachpoint,
            "prop-" + attachpoint,
            "prop_" + attachpoint,
        };

        foreach (var name in candidates)
        {
            var found = root.FindChild(name, recursive: true, owned: false);
            if (found is Node3D n3)
                return n3;
        }
        return null;
    }
}
