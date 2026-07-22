using System;
using System.Collections.Generic;

namespace ZeroAD.Sim.AI;

// Hierarchical finite state machine — faithful C# port of
// binaries/data/mods/public/globalscripts/FSM.js.
//
// FSMs are specified as a tree of states (names A-Z only). Each state may carry
// message handlers (keyed by message type), an `enter`/`leave` hook, and nested
// substates. A child state inherits every handler it does not override from its
// ancestors. Transitions walk the shared ancestor: leave the old branch bottom-up,
// then enter the new branch top-down. This inheritance + walk is what lets UnitAI
// express "INDIVIDUAL.COMBAT.ATTACKING" while sharing common handlers at the
// INDIVIDUAL and COMBAT levels.
//
// The FSM holds NO per-instance state. The component object (TObj) owns its own
// fields and carries FsmStateName / FsmNextState on the side, so the component
// serializes as plain data — no need to persist the compiled FSM structure. This
// mirrors the JS design comment in FSM.js (lines 8-11).

/// <summary>Host object contract: the FSM reads/mutates the current state name and
/// notifies the host on transition. Hosts store FsmStateName/FsmNextState as
/// serialized fields (strings) so cross-platform OOS hashing works.</summary>
public interface IFsmHost
{
    /// <summary>Current fully-qualified state name (e.g. "INDIVIDUAL.COMBAT.ATTACKING"). "" before Init.</summary>
    string FsmStateName { get; set; }

    /// <summary>Pending state set by a handler via <see cref="Fsm{TObj,TMsg}.SetNextState"/>; cleared by the FSM after switching.</summary>
    string? FsmNextState { get; set; }

    /// <summary>Called whenever the active state name changes. Mirrors JS FsmStateNameChanged.</summary>
    void OnFsmStateChanged(string stateName);
}

/// <summary>
/// A compiled, immutable hierarchical state machine. One instance is shared by all
/// component objects of a given type (e.g. every UnitAI entity uses the same Fsm).
/// Construct it once from a spec, then drive per-object instances through
/// ProcessMessage / SetNextState.
/// </summary>
public sealed class Fsm<TObj, TMsg>
    where TObj : IFsmHost
{
    // A flattened state, produced at construction. Inherits handlers from ancestors
    // during compilation, so message dispatch is a single dictionary lookup at runtime.
    private sealed class CompiledState
    {
        public string Name = "";                 // fully-qualified, e.g. "A.B.C"
        public string Parent = "";               // fully-qualified parent, "" for top level
        public Dictionary<string, string> Refs = new(StringComparer.Ordinal); // local-name → full-name
        public Func<TObj, bool>? Enter;          // enter takes only the host (no message) — matches JS; returns true to abort the walk
        public Func<TObj, bool>? Leave;
        public Dictionary<string, Action<TObj, TMsg>> Handlers = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, CompiledState> _states = new(StringComparer.Ordinal);

    // Per-state-name ancestor chain, e.g. "A.B.C" → ["A","A.B","A.B.C"].
    // Used by SwitchToNextState to compute the leave/enter sets.
    private readonly Dictionary<string, string[]> _decompose = new(StringComparer.Ordinal);

    // DeferMessage recursion guard — mirrors FSM.js deferFromState. Not reentrant
    // across objects (same caveat as the JS comment at FSM.js:307), but UnitAI
    // processes one entity's messages at a time so this is safe.
    private string? _deferFromState;

    /// <summary>Build the FSM from a spec tree.</summary>
    public Fsm(FsmSpec<TObj, TMsg> spec)
    {
        Compile(spec.Root, Array.Empty<string>(), inheritedHandlers: null);
    }

    // Recursive compilation — port of FSM.js process() (lines 161-235).
    private void Compile(FsmSpecNode<TObj, TMsg> node, string[] path, Dictionary<string, Action<TObj, TMsg>>? inheritedHandlers)
    {
        // String references to nodes defined elsewhere ("OTHERNAME": "A.B") resolve here.
        if (node.Reference is { } refName)
        {
            if (node.Owner == null || !node.TryResolveReference(refName, out var referenced) || referenced == null)
                throw new InvalidOperationException($"FSM node {string.Join(".", path)} refers to non-defined node {refName}");
            node = referenced;
        }

        string fullName = path.Length == 0 ? "" : string.Join(".", path);
        var state = new CompiledState { Name = fullName, Parent = path.Length <= 1 ? "" : string.Join(".", path, 0, path.Length - 1) };
        _states[fullName] = state;

        // Start from inherited handlers (shallow copy) so children override selectively.
        var newHandlers = inheritedHandlers != null
            ? new Dictionary<string, Action<TObj, TMsg>>(inheritedHandlers, StringComparer.Ordinal)
            : new Dictionary<string, Action<TObj, TMsg>>(StringComparer.Ordinal);

        // First pass: pick up enter/leave, register substate refs, collect new handlers.
        foreach (var (key, child) in node.Children)
        {
            if (key == "enter")
            {
                state.Enter = child.EnterHandler;
            }
            else if (key == "leave")
            {
                state.Leave = child.LeaveHandler;
            }
            else if (IsStateName(key))
            {
                state.Refs[key] = fullName.Length == 0 ? key : fullName + "." + key;
            }
            else if (child.Handler != null)
            {
                newHandlers[key] = child.Handler;
            }
        }

        // Commit inherited + overridden handlers onto this state.
        foreach (var (evt, h) in newHandlers)
            state.Handlers[evt] = h;

        // Second pass: recurse into substates, also computing decompose paths.
        foreach (var (key, child) in node.Children)
        {
            if (!IsStateName(key)) continue;

            var newPath = new string[path.Length + 1];
            Array.Copy(path, newPath, path.Length);
            newPath[path.Length] = key;

            var decomposed = new string[newPath.Length];
            decomposed[0] = newPath[0];
            for (int i = 1; i < newPath.Length; i++)
                decomposed[i] = decomposed[i - 1] + "." + newPath[i];
            _decompose[string.Join(".", newPath)] = decomposed;

            Compile(child, newPath, newHandlers);

            // Merge child refs upward so a parent can SetNextState by a grandchild's local name.
            var childState = _states[string.Join(".", newPath)];
            foreach (var (local, full) in childState.Refs)
                state.Refs[key + "." + local] = full;
        }
    }

    private static bool IsStateName(string key)
    {
        if (key.Length == 0 || key[0] is < 'A' or > 'Z') return false;
        foreach (char c in key)
            if (c is < 'A' or > 'Z') return false;
        return true;
    }

    /// <summary>Initialize a host object into its starting state (runs enter hooks).</summary>
    public void Init(TObj obj, string initialState)
    {
        _deferFromState = null;
        obj.FsmStateName = "";
        obj.FsmNextState = null;
        SwitchToNextState(obj, initialState);
    }

    /// <summary>Request a state change. Resolved relative to the current state's ancestors.
    /// The actual switch happens after the current handler returns (see ProcessMessage).</summary>
    public void SetNextState(TObj obj, string state) => obj.FsmNextState = state;

    /// <summary>Dispatch a message to the current state's handler, then drain any
    /// pending SetNextState transitions. Port of FSM.js ProcessMessage (lines 254-278).</summary>
    public void ProcessMessage(TObj obj, TMsg msg, string messageType)
    {
        if (!_states.TryGetValue(obj.FsmStateName, out var state))
            throw new InvalidOperationException($"FSM in unknown state '{obj.FsmStateName}'");

        if (!state.Handlers.TryGetValue(messageType, out var handler))
            throw new InvalidOperationException($"Unhandled event '{messageType}' in state '{obj.FsmStateName}'");

        handler(obj, msg);

        // Drain queued transitions. An enter hook may itself call SetNextState; keep switching.
        while (obj.FsmNextState != null)
        {
            string next = LookupState(obj.FsmStateName, obj.FsmNextState);
            obj.FsmNextState = null;
            SwitchToNextState(obj, next);
        }
    }

    /// <summary>Hand a message to the parent state's handler (inheritance fallback from
    /// within a handler). Port of FSM.js DeferMessage (lines 280-309).</summary>
    public void DeferMessage(TObj obj, TMsg msg, string messageType)
    {
        string? old = _deferFromState;
        string from = old ?? obj.FsmStateName;
        if (!_states.TryGetValue(from, out var fromState))
            throw new InvalidOperationException($"Cannot defer from unknown state '{from}'");
        _deferFromState = fromState.Parent.Length > 0 ? fromState.Parent : null;

        if (_deferFromState == null || !_states.TryGetValue(_deferFromState, out var parentState) ||
            !parentState.Handlers.TryGetValue(messageType, out var handler))
        {
            throw new InvalidOperationException($"Failed to defer event '{messageType}' from state '{obj.FsmStateName}'");
        }
        handler(obj, msg);

        _deferFromState = old;
    }

    public string GetCurrentState(TObj obj) => obj.FsmStateName;

    // Resolve a (possibly relative) state name against the current state's ancestor chain.
    // Port of FSM.js LookupState (lines 311-318).
    private string LookupState(string currentStateName, string stateName)
    {
        for (string? s = currentStateName; s != null && s.Length > 0;)
        {
            if (_states[s].Refs.TryGetValue(stateName, out var full))
                return full;
            s = _states[s].Parent.Length > 0 ? _states[s].Parent : null;
        }
        return stateName;
    }

    // Walk from current to next via their nearest common ancestor: leave old branch
    // bottom-up, enter new branch top-down. Any hook returning true aborts the walk
    // (used by enter hooks that re-route the transition). Port of SwitchToNextState (lines 325-376).
    private void SwitchToNextState(TObj obj, string nextStateName)
    {
        if (!_decompose.TryGetValue(obj.FsmStateName, out var fromState))
            fromState = Array.Empty<string>();
        if (!_decompose.TryGetValue(nextStateName, out var toState))
            throw new InvalidOperationException($"Tried to change to non-existent state '{nextStateName}'");

        int equalPrefix = 0;
        while (equalPrefix < fromState.Length && equalPrefix < toState.Length &&
               fromState[equalPrefix] == toState[equalPrefix])
            ++equalPrefix;

        // Same state: leave/enter up one level so cleanup fires (matches JS behaviour).
        if (equalPrefix > 0 && equalPrefix == toState.Length)
            --equalPrefix;

        // Leave: bottom-up from current leaf to the common ancestor.
        for (int i = fromState.Length - 1; i >= equalPrefix; i--)
        {
            var leave = _states[fromState[i]].Leave;
            if (leave != null)
            {
                obj.FsmStateName = fromState[i];
                if (leave(obj))
                {
                    obj.OnFsmStateChanged(obj.FsmStateName);
                    return;
                }
            }
        }

        // Enter: top-down from the common ancestor to the new leaf.
        for (int i = equalPrefix; i < toState.Length; i++)
        {
            var enter = _states[toState[i]].Enter;
            if (enter != null)
            {
                obj.FsmStateName = toState[i];
                if (enter(obj))
                {
                    obj.OnFsmStateChanged(obj.FsmStateName);
                    return;
                }
            }
        }

        obj.FsmStateName = nextStateName;
        obj.OnFsmStateChanged(obj.FsmStateName);
    }
}

// --- Spec DSL -------------------------------------------------------------
// A spec is a tree of FsmSpecNode<TObj,TMsg>. Use FsmSpec<TObj,TMsg>.Create() then
// .State(name) to build top-level states, chaining .State(...)/.On(...)/.Enter()/.Leave().
// This keeps the C# spec readable while mirroring the JS object-literal structure.

/// <summary>Typed spec builder. Generic in the host + message types so handlers are statically typed.</summary>
public sealed class FsmSpec<TObj, TMsg>
    where TObj : IFsmHost
{
    internal FsmSpecNode<TObj, TMsg> Root = new();

    private FsmSpec()
    {
        Root.Owner = this;
    }
    public static FsmSpec<TObj, TMsg> Create() => new();

    public FsmSpecNode<TObj, TMsg> State(string name) => Root.Child(name);

    public Fsm<TObj, TMsg> Build() => new(this);
}

public sealed class FsmSpecNode<TObj, TMsg>
    where TObj : IFsmHost
{
    internal FsmSpec<TObj, TMsg>? Owner;
    internal Func<TObj, bool>? EnterHandler;
    internal Func<TObj, bool>? LeaveHandler;
    internal Action<TObj, TMsg>? Handler;
    internal string? Reference;
    internal readonly Dictionary<string, FsmSpecNode<TObj, TMsg>> Children = new(StringComparer.Ordinal);

    internal FsmSpecNode() { }

    /// <summary>Declare or fetch a child node by key. Keys "enter"/"leave" attach hooks;
    /// all-uppercase keys are substates; anything else is a message handler name.</summary>
    public FsmSpecNode<TObj, TMsg> Child(string key)
    {
        if (!Children.TryGetValue(key, out var child))
        {
            child = new FsmSpecNode<TObj, TMsg> { Owner = Owner };
            Children[key] = child;
        }
        return child;
    }

    /// <summary>Convenience: declare a substate (all-uppercase name) and return its node.</summary>
    public FsmSpecNode<TObj, TMsg> State(string name) => Child(name);

    /// <summary>Register an enter hook (no message — matches JS). Return true to abort
    /// the transition walk (used when the hook re-routes via SetNextState). Returns this node.</summary>
    public FsmSpecNode<TObj, TMsg> Enter(Action enter) => Enter(_ => { enter(); return false; });

    public FsmSpecNode<TObj, TMsg> Enter(Action<TObj> enter) => Enter(obj => { enter(obj); return false; });

    public FsmSpecNode<TObj, TMsg> Enter(Func<TObj, bool> enter)
    {
        Child("enter").EnterHandler = enter;
        return this;
    }

    public FsmSpecNode<TObj, TMsg> Leave(Action leave) => Leave(_ => { leave(); return false; });

    public FsmSpecNode<TObj, TMsg> Leave(Action<TObj> leave) => Leave(obj => { leave(obj); return false; });

    public FsmSpecNode<TObj, TMsg> Leave(Func<TObj, bool> leave)
    {
        Child("leave").LeaveHandler = leave;
        return this;
    }

    /// <summary>Register a handler for a message type. The host (component) and message are passed in.</summary>
    public FsmSpecNode<TObj, TMsg> On(string messageType, Action handler) => On(messageType, (obj, _) => handler());

    public FsmSpecNode<TObj, TMsg> On(string messageType, Action<TObj> handler) => On(messageType, (obj, _) => handler(obj));

    public FsmSpecNode<TObj, TMsg> On(string messageType, Action<TObj, TMsg> handler)
    {
        Child(messageType).Handler = handler;
        return this;
    }

    /// <summary>Mark this node as an alias of another (already-defined) state by full dotted name.</summary>
    public FsmSpecNode<TObj, TMsg> Alias(string referencedFullName)
    {
        Reference = referencedFullName;
        return this;
    }

    /// <summary>Build the FSM from this node's owning spec. Lets a fluent chain that ended on
    /// a node (via .State/.On/.Enter) be compiled without holding a separate spec variable.</summary>
    public Fsm<TObj, TMsg> Build()
    {
        if (Owner == null)
            throw new InvalidOperationException("Cannot Build from a spec node without an owner.");
        return new Fsm<TObj, TMsg>(Owner);
    }

    // Walk the root spec tree by dotted path to resolve an alias target. Must be on
    // the node itself (not its Owner) so the recursion starts from the spec root.
    internal bool TryResolveReference(string fullName, out FsmSpecNode<TObj, TMsg>? node)
    {
        node = null;
        if (Owner == null) return false;
        FsmSpecNode<TObj, TMsg>? current = Owner.Root;
        foreach (var part in fullName.Split('.'))
        {
            if (current == null || !current.Children.TryGetValue(part, out var next)) return false;
            current = next;
        }
        node = current;
        return true;
    }
}
