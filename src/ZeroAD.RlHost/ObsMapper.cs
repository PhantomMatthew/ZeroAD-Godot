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
        return msg;
    }

    public static RlAction FromProto(ProtoAction action)
    {
        int fn = action.Function;
        if (fn < 0 || fn >= RlSpec.FunctionCount) fn = 0;
        return new RlAction(
            (RlFunction)fn,
            action.SelectedIndex,
            action.TargetEntityIndex,
            action.TargetCellX,
            action.TargetCellZ,
            action.CatalogId);
    }

    public static RlEnvironment CreateEnv(uint seed, bool privileged, int stepMul, int delay,
        bool tickOpponentAi = true, int maxEpisodeTurns = 10_000) =>
        new(new RlConfig
        {
            Seed = seed,
            PrivilegedVision = privileged,
            StepMul = Math.Max(1, stepMul <= 0 ? 1 : stepMul),
            CommandDelay = Math.Max(1, delay <= 0 ? 2 : delay),
            TickOpponentAi = tickOpponentAi,
            MaxEpisodeTurns = Math.Max(1, maxEpisodeTurns)
        });
}
