using System.Threading.Tasks;
using Grpc.Core;
using ProtoAction = ZeroAD.RlHost.Proto.Action;
using ZeroAD.Sim.RL;
using ZeroAD.RlHost.Proto;

namespace ZeroAD.RlHost;

internal sealed class RlGrpcService : ZeroAdRl.ZeroAdRlBase
{
    private readonly object _gate = new();
    private RlEnvironment? _env;

    public override Task<Observation> Reset(ResetRequest request, ServerCallContext context)
    {
        lock (_gate)
        {
            return Task.FromResult(ResetCore(request));
        }
    }

    public override Task<Observation> Step(ProtoAction request, ServerCallContext context)
    {
        lock (_gate)
        {
            if (_env == null)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Reset first"));
            var result = _env.Step(ObsMapper.FromProto(request));
            return Task.FromResult(ObsMapper.ToProto(result.Observation));
        }
    }

    public override async Task Session(
        IAsyncStreamReader<ClientMessage> requestStream,
        IServerStreamWriter<Observation> responseStream,
        ServerCallContext context)
    {
        await foreach (var msg in requestStream.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
        {
            Observation obs;
            lock (_gate)
            {
                if (msg.PayloadCase == ClientMessage.PayloadOneofCase.Reset)
                    obs = ResetCore(msg.Reset);
                else if (msg.PayloadCase == ClientMessage.PayloadOneofCase.Step)
                {
                    if (_env == null)
                        throw new RpcException(new Status(StatusCode.FailedPrecondition, "Reset first"));
                    obs = ObsMapper.ToProto(_env.Step(ObsMapper.FromProto(msg.Step)).Observation);
                }
                else
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "empty client message"));
            }
            await responseStream.WriteAsync(obs, context.CancellationToken).ConfigureAwait(false);
        }
    }

    private Observation ResetCore(ResetRequest request)
    {
        _env?.Dispose();
        _env = ObsMapper.CreateEnv(
            request.Seed == 0 ? 1u : request.Seed,
            request.Privileged,
            request.StepMul,
            request.CommandDelay);
        return ObsMapper.ToProto(_env.Reset());
    }
}
