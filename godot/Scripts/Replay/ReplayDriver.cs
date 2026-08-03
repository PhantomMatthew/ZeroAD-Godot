using System;
using System.Collections.Generic;
using Godot;
using ZeroAD.Sim.Net;

namespace ZeroAD.Godot;

/// <summary>回放播放驱动器。持有 SimBridge 引用，每帧把预录制命令注入当前回合。
///
/// 关键设计：不改变 turn 循环逻辑。每帧（在 SimBridge._Process 推进 sim 之前）调 Pump：
///   - 若录像已播完（currentTurn > maxTurn）→ 停止模拟
///   - 否则把当前回合的预录制命令经 InjectReplayBundle 塞入 NTM
/// AdvanceTurn 执行它们时走与实时游戏完全相同的代码路径，确定性自动保证。
///
/// 预加载策略：构造时一次性读完全部命令批到字典。简单；API 设计留了流式升级空间。</summary>
public sealed class ReplayDriver
{
    private readonly SimBridge _sim;
    private readonly Dictionary<uint, NetCommand[]> _batches = new();
    private readonly uint _maxTurn;
    private bool _finished;

    public uint TotalTurns => _maxTurn;
    public bool IsFinished => _finished;

    public ReplayDriver(SimBridge sim, ReplayReader reader)
    {
        _sim = sim;
        uint max = 0;
        while (reader.TryReadTurnBatch(out uint turn, out var cmds))
        {
            _batches[turn] = cmds;
            if (turn > max) max = turn;
        }
        _maxTurn = max;
        GD.Print($"[Replay] loaded {_batches.Count} turn batches, max turn {_maxTurn}");
    }

    /// <summary>每帧由 SimBridge._Process 调用（仅回放模式）。注入当前回合的命令或停止模拟。</summary>
    public void Pump()
    {
        if (_finished) return;
        uint current = _sim.NetTurn.CurrentTurn;
        if (current > _maxTurn)
        {
            _sim.SimulationRunning = false;
            _finished = true;
            GD.Print("[Replay] playback finished");
            return;
        }
        if (_batches.TryGetValue(current, out var cmds))
            _sim.NetTurn.InjectReplayBundle(current, cmds);
    }
}
