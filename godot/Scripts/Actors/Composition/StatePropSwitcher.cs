using System;
using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot.Actors.Composition;

using ZeroAD.Godot.Actors.Variation;

/// <summary>
/// Applies per-animation-state prop deltas (see <see cref="StatePropDelta"/>) to a
/// composed unit instance. The original re-runs actor variation whenever the UnitAI
/// animation state changes: state-named variants (gather_tree, build, ...) add props
/// (axe in hand while chopping) and clear base props (weapons/shield hidden).
///
/// Base props are never detached — toggling Visible on the prop root is reversible
/// and cheap. State-added props are composed lazily on first entry into the state,
/// cached per (state, attachpoint), then just shown/hidden afterwards.
///
/// Like <see cref="ZeroAD.Godot.SkeletalAnim.ManualAnimator"/>, this carries C#
/// state and therefore must be attached AFTER PackedScene instantiation
/// (ActorLoader.Instantiate), never during BuildStructural.
/// </summary>
public sealed partial class StatePropSwitcher : Node
{
    public const string NodeName = "StatePropSwitcher";

    private Node3D? _root;
    private Skeleton3D? _skeleton;
    private Color _teamColor;
    private int _seed;
    private IReadOnlyDictionary<string, StatePropDelta> _deltas =
        new Dictionary<string, StatePropDelta>();
    // 同 attachpoint 可有多个 base prop(雅典 CC 7 个 root 装饰 prop)。clear 该 attachpoint
    // 要隐藏整组,不只第一个。
    private readonly Dictionary<string, List<Node3D>> _baseProps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Node3D> _spawned = new(StringComparer.OrdinalIgnoreCase);
    private string _current = "";

    // Shared composer for state props — its instance state is only warn-once sets.
    private static readonly ActorComposer PropComposer = new();
    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

    public static StatePropSwitcher? Find(Node node) =>
        node.FindChild(NodeName, recursive: false, owned: false) as StatePropSwitcher;

    /// <summary>One-line diagnostic: current state + spawned/base prop visibility.</summary>
    public string Summary
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"pstate={_current} deltas={_deltas.Count}");
            foreach (var kv in _spawned)
                sb.Append(' ').Append(kv.Key).Append(kv.Value.Visible ? "=on" : "=off");
            foreach (var kv in _baseProps)
            {
                // Parent type proves how the prop rides the skeleton:
                // BoneAttachment3D = follows animated bone; anything else = frozen.
                // 同 attachpoint 多 prop:报第一个的 parent(同组共享挂法)。
                var first = kv.Value.Count > 0 ? kv.Value[0] : null;
                var p = first?.GetParent();
                string pt = p is BoneAttachment3D ba ? $"bone{ba.BoneIdx}" : p?.GetType().Name ?? "?";
                sb.Append(' ').Append(kv.Key).Append('@').Append(pt);
                if (kv.Value.Count > 1) sb.Append($"x{kv.Value.Count}");
                if (kv.Value.Any(n => !n.Visible))
                    sb.Append("=off");
            }
            return sb.ToString();
        }
    }

    /// <summary>Attaches a switcher to the instantiated unit. Returns null when the
    /// actor has no state prop deltas (most props/buildings) — nothing to switch.</summary>
    public static StatePropSwitcher? Attach(Node3D instance, ResolvedActorSpec spec, Color teamColor, int seed)
    {
        if (spec.StateProps.Count == 0) return null;
        var switcher = new StatePropSwitcher { Name = NodeName };
        instance.AddChild(switcher);
        switcher.Init(instance, spec, teamColor, seed);
        return switcher;
    }

    private void Init(Node3D instance, ResolvedActorSpec spec, Color teamColor, int seed)
    {
        _root = instance;
        _skeleton = AttachpointResolver.FindSkeleton(instance);
        _teamColor = teamColor;
        _seed = seed;
        _deltas = spec.StateProps;
        CollectBaseProps(instance);
    }

    private void CollectBaseProps(Node node)
    {
        if (node is Node3D n3 && node.HasMeta(LayerMeta.PropAttachpoint))
        {
            var v = node.GetMeta(LayerMeta.PropAttachpoint);
            if (v.VariantType == Variant.Type.String)
            {
                string attach = (string)v;
                if (!_baseProps.TryGetValue(attach, out var list))
                {
                    list = new List<Node3D>();
                    _baseProps[attach] = list;
                }
                list.Add(n3);   // 同 attachpoint 多个全收
            }
        }
        foreach (var child in node.GetChildren())
            CollectBaseProps(child);
    }

    /// <summary>Switches the visible prop set to match the given animation state:
    /// undoes the previous state's adds/clears, then applies the new state's.</summary>
    public void Apply(string state)
    {
        if (string.IsNullOrEmpty(state) || state == _current || _root == null) return;

        if (_current.Length > 0 && _deltas.TryGetValue(_current, out var old))
        {
            foreach (var clear in old.Clears)
                SetBaseVisible(clear, true);
            foreach (var attach in old.Adds.Keys)
            {
                if (_spawned.TryGetValue(SpawnKey(_current, attach), out var node))
                    node.Visible = false;
                // A state add replaces the base prop at the same attachpoint — restore it.
                SetBaseVisible(attach, true);
            }
        }

        if (_deltas.TryGetValue(state, out var delta))
        {
            foreach (var clear in delta.Clears)
                SetBaseVisible(clear, false);
            foreach (var kv in delta.Adds)
            {
                SetBaseVisible(kv.Key, false);
                GetOrSpawn(state, kv.Key, kv.Value).Visible = true;
            }
        }

        _current = state;
    }

    private void SetBaseVisible(string attachpoint, bool visible)
    {
        if (_baseProps.TryGetValue(attachpoint, out var list))
            foreach (var node in list)
                node.Visible = visible;
    }

    private Node3D GetOrSpawn(string state, string attachpoint, PropSpec prop)
    {
        string key = SpawnKey(state, attachpoint);
        if (_spawned.TryGetValue(key, out var existing))
            return existing;

        var childSpec = ActorComposer.ResolveChildSpec(prop);
        Node3D? node = childSpec != null ? PropComposer.BuildStructural(childSpec, depth: 1) : null;
        if (node == null)
        {
            if (Warned.Add(key))
                ZeroAD.Sim.Diag.Warn("Actor", $"StatePropSwitcher: failed to build prop '{prop.ActorPath}' for state '{state}'");
            // Placeholder so a broken prop doesn't retry the compose on every state entry.
            node = new Node3D { Name = "MissingStateProp" };
        }
        else
        {
            InstanceCustomizer.Apply(node, childSpec!, _teamColor, _seed);
        }

        if (!ActorComposer.AttachPropAt(_root!, _skeleton, attachpoint, node)
            && Warned.Add("attach:" + key))
            ZeroAD.Sim.Diag.Warn("Actor", $"StatePropSwitcher: attachpoint '{attachpoint}' not found for state '{state}'");

        _spawned[key] = node;
        return node;
    }

    private static string SpawnKey(string state, string attachpoint) => state + "|" + attachpoint;
}
