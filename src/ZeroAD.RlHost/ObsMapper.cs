using System;
using ProtoAction = ZeroAD.RlHost.Proto.Action;
using ZeroAD.Sim.RL;
using ZeroAD.RlHost.Proto;

namespace ZeroAD.RlHost;

internal static class ObsMapper
{
    public static Observation ToProto(RlObservation obs)
    {
        var msg = new Observation
        {
            Turn = obs.Turn,
            Reward = obs.Reward,
            Done = obs.Done,
            FunctionMask = Google.Protobuf.ByteString.CopyFrom(obs.FunctionMask)
        };
        msg.Entities.AddRange(obs.Entities);
        msg.Spatial.AddRange(obs.Spatial);
        msg.Scalars.AddRange(obs.Scalars);
        for (int i = 0; i < obs.EntityMask.Length; i++)
            msg.EntityMask.Add(obs.EntityMask[i]);
        msg.Catalog.AddRange(obs.Catalog);
        return msg;
    }

    public static RlAction FromProto(ProtoAction action)
    {
        int fn = action.Function;
        if (fn < 0 || fn >= RlSpec.FunctionCount) fn = 0;
        int s0 = action.SelectedIndex;
        int s1 = -1, s2 = -1, s3 = -1, s4 = -1, s5 = -1, s6 = -1, s7 = -1;
        if (action.Selected.Count > 0)
        {
            s0 = action.Selected[0];
            if (action.Selected.Count > 1) s1 = action.Selected[1];
            if (action.Selected.Count > 2) s2 = action.Selected[2];
            if (action.Selected.Count > 3) s3 = action.Selected[3];
            if (action.Selected.Count > 4) s4 = action.Selected[4];
            if (action.Selected.Count > 5) s5 = action.Selected[5];
            if (action.Selected.Count > 6) s6 = action.Selected[6];
            if (action.Selected.Count > 7) s7 = action.Selected[7];
        }
        return new RlAction(
            (RlFunction)fn,
            s0,
            action.TargetEntityIndex,
            action.TargetCellX,
            action.TargetCellZ,
            action.CatalogId,
            s1, s2, s3, s4, s5, s6, s7);
    }

    public static RlAction? OpponentFromProto(ProtoAction action)
    {
        if (action.Opponent == null) return null;
        var opp = FromProto(action.Opponent);
        return opp.Function == RlFunction.NoOp ? null : opp;
    }

    public static RlEnvironment CreateEnv(RlConfig cfg) => new(cfg);

    public static RlConfig MakeConfig(uint seed, bool privileged, int stepMul, int delay,
        bool tickOpponentAi, int maxEpisodeTurns, string mapName = "", int mapSize = 128,
        string agentCiv = "athen", string opponentCiv = "athen", bool randomizeCivs = false) =>
        new()
        {
            Seed = seed,
            PrivilegedVision = privileged,
            StepMul = Math.Max(1, stepMul <= 0 ? 1 : stepMul),
            CommandDelay = Math.Max(1, delay <= 0 ? 2 : delay),
            TickOpponentAi = tickOpponentAi,
            MaxEpisodeTurns = Math.Max(1, maxEpisodeTurns),
            MapName = mapName,
            MapSize = mapSize,
            AgentCiv = agentCiv,
            OpponentCiv = opponentCiv,
            RandomizeCivs = randomizeCivs
        };

    public static RlEnvironment CreateEnv(uint seed, bool privileged, int stepMul, int delay,
        bool tickOpponentAi = true, int maxEpisodeTurns = 10_000) =>
        CreateEnv(MakeConfig(seed, privileged, stepMul, delay, tickOpponentAi, maxEpisodeTurns));
}
