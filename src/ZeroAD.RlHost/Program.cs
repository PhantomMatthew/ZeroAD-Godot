using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeroAD.Sim.RL;

namespace ZeroAD.RlHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        string shmPath = "";
        int grpcPort = -1;
        int slots = 1;
        int seed = 1;
        bool privileged = false;
        int stepMul = 1;
        int commandDelay = 2;
        bool petra = true;
        int maxTurns = 10_000;
        string mapName = "";
        int mapSize = 128;
        string agentCiv = "athen";
        string oppCiv = "athen";
        bool randomCivs = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (a)
            {
                case "--shm": shmPath = Next(); break;
                case "--grpc": grpcPort = int.Parse(Next()); break;
                case "--slots": slots = Math.Max(1, int.Parse(Next())); break;
                case "--seed": seed = int.Parse(Next()); break;
                case "--privileged": privileged = true; break;
                case "--step-mul": stepMul = Math.Max(1, int.Parse(Next())); break;
                case "--command-delay": commandDelay = Math.Max(1, int.Parse(Next())); break;
                case "--petra": petra = true; break;
                case "--no-petra": petra = false; break;
                case "--max-turns": maxTurns = Math.Max(1, int.Parse(Next())); break;
                case "--map": mapName = Next(); break;
                case "--map-size": mapSize = Math.Max(64, int.Parse(Next())); break;
                case "--agent-civ": agentCiv = Next(); break;
                case "--opponent-civ": oppCiv = Next(); break;
                case "--random-civs": randomCivs = true; break;
                default:
                    Console.Error.WriteLine("unknown arg: " + a);
                    return 2;
            }
        }

        if (grpcPort >= 0)
            return RunGrpc(grpcPort).GetAwaiter().GetResult();

        if (string.IsNullOrEmpty(shmPath))
        {
            Console.Error.WriteLine(
                "usage: ZeroAD.RlHost --shm PATH | --grpc PORT [--slots N] [--seed S] [--privileged] [--petra|--no-petra] [--max-turns N]");
            return 2;
        }

        return RunShm(shmPath, slots, seed, privileged, stepMul, commandDelay, petra, maxTurns,
            mapName, mapSize, agentCiv, oppCiv, randomCivs);
    }

    private static async Task<int> RunGrpc(int port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, port, lo => lo.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddGrpc();
        var app = builder.Build();
        app.MapGrpcService<RlGrpcService>();
        await app.StartAsync().ConfigureAwait(false);
        string addr = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?
            .Addresses.FirstOrDefault() ?? ("http://127.0.0.1:" + port);
        Console.WriteLine("READY grpc=" + addr);
        Console.Out.Flush();
        await ((IHost)app).WaitForShutdownAsync().ConfigureAwait(false);
        return 0;
    }

    private static int RunShm(string shmPath, int slots, int seed, bool privileged,
        int stepMul, int commandDelay, bool petra, int maxTurns, string mapName, int mapSize,
        string agentCiv, string oppCiv, bool randomCivs)
    {
        int fileBytes = ShmLayout.FileBytes(slots);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(shmPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = new FileStream(shmPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.SetLength(fileBytes);
        using var mm = MemoryMappedFile.CreateFromFile(fs, null, fileBytes,
            MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        using var acc = mm.CreateViewAccessor(0, fileBytes, MemoryMappedFileAccess.ReadWrite);

        var scratch = new byte[fileBytes];
        ShmCodec.WriteHeader(scratch, slots, seed, privileged);
        acc.WriteArray(0, scratch, 0, ShmLayout.HeaderBytes);

        var envs = new RlEnvironment[slots];
        for (int s = 0; s < slots; s++)
            envs[s] = CreateSlot(unchecked((uint)(seed + s)), privileged, stepMul, commandDelay,
                petra, maxTurns, mapName, mapSize, agentCiv, oppCiv, randomCivs);

        Console.WriteLine("READY slots=" + slots + " bytes=" + fileBytes);
        Console.Out.Flush();

        try
        {
            RunLoop(acc, scratch, envs, slots, seed, privileged, stepMul, commandDelay, petra,
                maxTurns, mapName, mapSize, agentCiv, oppCiv, randomCivs);
            return 0;
        }
        finally
        {
            foreach (var e in envs) e.Dispose();
        }
    }

    private static RlEnvironment CreateSlot(uint seed, bool privileged, int stepMul, int delay,
        bool petra, int maxTurns, string mapName, int mapSize, string agentCiv, string oppCiv,
        bool randomCivs) =>
        ObsMapper.CreateEnv(ObsMapper.MakeConfig(seed, privileged, stepMul, delay, petra, maxTurns,
            mapName, mapSize, agentCiv, oppCiv, randomCivs));

    private static void RunLoop(MemoryMappedViewAccessor acc, byte[] scratch,
        RlEnvironment[] envs, int slots, int seed, bool privileged, int stepMul, int delay,
        bool petra, int maxTurns, string mapName, int mapSize, string agentCiv, string oppCiv,
        bool randomCivs)
    {
        while (true)
        {
            acc.ReadArray(0, scratch, 0, ShmLayout.HeaderBytes);
            int seqIn = BitConverter.ToInt32(scratch, ShmLayout.OffSeqIn);
            int seqOut = BitConverter.ToInt32(scratch, ShmLayout.OffSeqOut);
            if (seqIn == seqOut)
            {
                Thread.Sleep(1);
                continue;
            }

            int cmd = BitConverter.ToInt32(scratch, ShmLayout.OffCmd);
            if (cmd == ShmLayout.CmdQuit) return;

            acc.ReadArray(0, scratch, 0, scratch.Length);
            int headerTurns = BitConverter.ToInt32(scratch, ShmLayout.OffMaxTurns);
            int turns = headerTurns > 0 ? headerTurns : maxTurns;
            if (cmd == ShmLayout.CmdReset)
            {
                Parallel.For(0, slots, s =>
                {
                    envs[s].Dispose();
                    envs[s] = CreateSlot(unchecked((uint)(seed + s)), privileged, stepMul,
                        delay, petra, turns, mapName, mapSize, agentCiv, oppCiv, randomCivs);
                    var obs = envs[s].Reset();
                    ShmCodec.WriteObservation(
                        scratch.AsSpan(ShmLayout.SlotOffset(s), ShmLayout.SlotBytes), obs);
                });
            }
            else if (cmd == ShmLayout.CmdStep)
            {
                Parallel.For(0, slots, s =>
                {
                    var span = scratch.AsSpan(ShmLayout.SlotOffset(s), ShmLayout.SlotBytes);
                    var action = ShmCodec.ReadAction(span);
                    var opp = ShmCodec.ReadOpponentAction(span);
                    var result = envs[s].Step(action, opp.Function == RlFunction.NoOp ? null : opp);
                    ShmCodec.WriteObservation(span, result.Observation);
                });
            }

            acc.WriteArray(ShmLayout.HeaderBytes, scratch, ShmLayout.HeaderBytes,
                scratch.Length - ShmLayout.HeaderBytes);
            BitConverter.TryWriteBytes(scratch.AsSpan(ShmLayout.OffSeqOut), seqIn);
            BitConverter.TryWriteBytes(scratch.AsSpan(ShmLayout.OffCmd), ShmLayout.CmdIdle);
            acc.WriteArray(ShmLayout.OffCmd, scratch, ShmLayout.OffCmd, 12);
        }
    }
}
