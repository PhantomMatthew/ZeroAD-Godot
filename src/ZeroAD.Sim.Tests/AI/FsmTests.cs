using System.Collections.Generic;
using ZeroAD.Sim.AI;
using Xunit;

namespace ZeroAD.Sim.Tests.AI;

// Tests for the FSM port (globalscripts/FSM.js). Each case asserts one behaviour
// documented in FSM.js: handler inheritance, leave/enter walk order, SetNextState
// drain loop, DeferMessage parent fallback, alias resolution, relative state lookup.
//
// Note on the DSL: .State("X") returns the X node; further .State/.On on it create
// children of X. To create sibling top-level states, call spec.State(...) again from
// the spec root. Each test rebuilds the spec from the root explicitly to avoid the
// ambiguity that chained .State("A").state-on-A().state-on-A1() stays nested.
public sealed class FsmTests
{
    // Minimal host that records transitions so tests can assert ordering.
    private sealed class TestHost : IFsmHost
    {
        public string FsmStateName { get; set; } = "";
        public string? FsmNextState { get; set; }
        public List<string> Log { get; } = new();
        public void OnFsmStateChanged(string stateName) => Log.Add("state=" + stateName);
    }

    private struct Msg { public string Text; }

    private static FsmSpec<TestHost, Msg> NewSpec() => FsmSpec<TestHost, Msg>.Create();

    [Fact]
    public void Init_RunsEnterHooksTopDownAndLandsInInitialState()
    {
        var host = new TestHost();
        var spec = NewSpec();
        spec.State("ROOT").Enter(h => h.Log.Add($"enter:{h.FsmStateName}"));
        spec.State("ROOT").State("A").Enter(h => h.Log.Add($"enter:{h.FsmStateName}"));
        spec.State("ROOT").State("B"); // sibling
        var fsm = spec.Build();

        fsm.Init(host, "ROOT.A");

        // ROOT.enter fires first, then A.enter (top-down walk).
        Assert.Equal(new[] { "enter:ROOT", "enter:ROOT.A", "state=ROOT.A" }, host.Log);
        Assert.Equal("ROOT.A", host.FsmStateName);
    }

    [Fact]
    public void ProcessMessage_DispatchesToCurrentStateHandler()
    {
        var host = new TestHost();
        var spec = NewSpec();
        spec.State("IDLE").On("Tick", (h, m) => h.Log.Add($"idle:{m.Text}"));
        var fsm = spec.Build();
        fsm.Init(host, "IDLE");

        fsm.ProcessMessage(host, new Msg { Text = "x" }, "Tick");

        Assert.Contains("idle:x", host.Log);
    }

    [Fact]
    public void Handlers_InheritFromAncestorsWhenChildDoesNotOverride()
    {
        var host = new TestHost();
        var spec = NewSpec();
        // Default Tick at INDIVIDUAL; overridden in COMBAT.ATTACKING only.
        spec.State("INDIVIDUAL").On("Tick", (h, _) => h.Log.Add("individual-tick"));
        spec.State("INDIVIDUAL").State("COMBAT");
        spec.State("INDIVIDUAL").State("COMBAT").State("ATTACKING").On("Tick", (h, _) => h.Log.Add("attacking-tick"));
        var fsm = spec.Build();

        // Inheriting child COMBAT (no own Tick) → uses INDIVIDUAL's Tick.
        fsm.Init(host, "INDIVIDUAL.COMBAT");
        fsm.ProcessMessage(host, default, "Tick");
        Assert.Contains("individual-tick", host.Log);

        // Leaf ATTACKING overrides.
        host.Log.Clear();
        fsm.Init(host, "INDIVIDUAL.COMBAT.ATTACKING");
        fsm.ProcessMessage(host, default, "Tick");
        Assert.Contains("attacking-tick", host.Log);
    }

    [Fact]
    public void SetNextState_DrainsAfterHandler_AndRunsLeaveThenEnter()
    {
        var host = new TestHost();
        var spec = NewSpec();
        // "Go" targets the REST leaf by relative name (resolved against A.WORK's ancestors).
        spec.State("A").On("Go", (h, _) => h.FsmNextState = "B.REST");
        spec.State("A").State("WORK").Enter(h => h.Log.Add("enter:WORK")).Leave(h => h.Log.Add("leave:WORK"));
        spec.State("B").State("REST").Enter(h => h.Log.Add("enter:REST"));
        var fsm = spec.Build();

        fsm.Init(host, "A.WORK");
        host.Log.Clear();
        fsm.ProcessMessage(host, default, "Go");

        // leave:WORK (leave old leaf) before enter:REST (enter new leaf).
        Assert.Equal(new[] { "leave:WORK", "enter:REST", "state=B.REST" }, host.Log);
        Assert.Equal("B.REST", host.FsmStateName);
        Assert.Null(host.FsmNextState); // drained
    }

    [Fact]
    public void SetNextState_EnterHookReturningTrueAbortsAndSignals()
    {
        var host = new TestHost();
        var spec = NewSpec();
        spec.State("A").On("Go", (h, _) => h.FsmNextState = "B.REST");
        spec.State("A").State("WORK");
        spec.State("B").State("REST").Enter(h =>
        {
            h.Log.Add("enter:REST-redirect");
            h.FsmNextState = "A.WORK"; // re-route mid-transition
            return true;               // abort current walk, drain loop picks up the redirect
        });
        var fsm = spec.Build();

        fsm.Init(host, "A.WORK");
        host.Log.Clear();
        fsm.ProcessMessage(host, default, "Go");

        Assert.Contains("enter:REST-redirect", host.Log);
        Assert.Equal("A.WORK", host.FsmStateName); // ended up where REST redirected to
    }

    [Fact]
    public void DeferMessage_DispatchesToParentStateHandler()
    {
        var host = new TestHost();
        var spec = NewSpec();
        spec.State("ROOT").On("Tick", (h, _) => h.Log.Add("parent-tick"));
        spec.State("ROOT").State("CHILD").On("Tick", (h, _) => h.Log.Add("child-tick"));
        var fsm = spec.Build();
        fsm.Init(host, "ROOT.CHILD");

        // DeferMessage hands the message to the PARENT state's handler (ROOT.Tick).
        fsm.DeferMessage(host, default, "Tick");
        Assert.Contains("parent-tick", host.Log);
    }

    [Fact]
    public void Alias_ResolvedToReferencedStateTree()
    {
        var host = new TestHost();
        var spec = NewSpec();
        // Handler names must NOT be all-uppercase (those are state names per FSM.js /^[A-Z]+$/).
        spec.State("A").On("tick", (h, _) => h.Log.Add("a-tick"));
        spec.State("A").State("LEAF").Enter(h => h.Log.Add("enter:LEAF"));
        // B is an alias of A (shares A's handlers and LEAF subtree).
        spec.State("B").Alias("A");
        var fsm = spec.Build();

        fsm.Init(host, "B.LEAF");
        Assert.Contains("enter:LEAF", host.Log);
        host.Log.Clear();
        fsm.ProcessMessage(host, default, "tick");
        Assert.Contains("a-tick", host.Log);
    }

    [Fact]
    public void RelativeStateName_ResolvesAgainstAncestors()
    {
        var host = new TestHost();
        var spec = NewSpec();
        spec.State("INDIVIDUAL").On("Go", (h, _) => h.FsmNextState = "IDLE");
        spec.State("INDIVIDUAL").State("IDLE").Enter(h => h.Log.Add("enter-individual-idle"));
        spec.State("INDIVIDUAL").State("COMBAT");
        var fsm = spec.Build();

        fsm.Init(host, "INDIVIDUAL.COMBAT");
        fsm.ProcessMessage(host, default, "Go");

        Assert.Equal("INDIVIDUAL.IDLE", host.FsmStateName);
        Assert.Contains("enter-individual-idle", host.Log);
    }
}
