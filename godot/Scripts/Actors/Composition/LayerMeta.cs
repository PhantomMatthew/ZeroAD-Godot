namespace ZeroAD.Godot.Actors.Composition;

internal static class LayerMeta
{
    public const string ActorPath = "actorPath";
    public const string MeshGlbPath = "meshGlbPath";
    /// <summary>Set on every attached prop's root node: the attachpoint it hangs on.
    /// Read by StatePropSwitcher to hide/show base props per animation state.</summary>
    public const string PropAttachpoint = "propAttachpoint";
}
