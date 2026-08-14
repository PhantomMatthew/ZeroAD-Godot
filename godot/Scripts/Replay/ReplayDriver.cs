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
/// 确定性回归验证:每个 HashCheckInterval 回合,用录制时存的状态哈希对比当前 sim 哈希。
/// 不一致即 desync(改了 NetCommand/sim 逻辑导致同输入不同输出)→ GD.PrintErr 告警。
/// 旧录像无哈希日志段 → _hashes 空 → 静默跳过(向后兼容)。
///
/// 预加载策略：构造时一次性读完全部命令批到字典。简单；API 设计留了流式升级空间。</summary>
public sealed class ReplayDriver
{
    private readonly SimBridge _sim;
    private readonly Dictionary<uint, NetCommand[]> _batches = new();
    private readonly Dictionary<uint, byte[]> _hashes = new();
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
        // 命令流读完后读尾部哈希日志段(旧录像无此段 → 空字典 → 验证静默跳过)。
        _hashes = reader.TryReadHashLog();
        // 订阅 OnTurnAdvanced:每回合推进后做哈希验证(只在校验点回合对比)。
        _sim.NetTurn.OnTurnAdvanced += VerifyHash;
        ZeroAD.Sim.Diag.Log("Replay", $"loaded {_batches.Count} turn batches, max turn {_maxTurn}, {_hashes.Count} hash checkpoints");
    }

    private void VerifyHash(uint turn)
    {
        if (_hashes.Count == 0) return;  // 旧录像无哈希日志
        if (turn % NetTurnManager.HashCheckInterval != 0) return;
        if (!_hashes.TryGetValue(turn, out var recorded)) return;
        byte[] actual;
        try { actual = _sim.Sim.ComputeStateHash(); }
        catch (Exception ex) { ZeroAD.Sim.Diag.Err("Replay", $"hash compute failed at turn {turn}: {ex.Message}"); return; }
        if (!HashEquals(actual, recorded))
        {
            ZeroAD.Sim.Diag.Err("Replay", $"OOS at turn {turn}: recorded {ToHex(recorded)} != actual {ToHex(actual)}");
            ZeroAD.Sim.Diag.Err("Replay", "确定性回归:同输入产生不同状态——检查是否改了 NetCommand 结构/sim 逻辑");
        }
    }

    private static bool HashEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static string ToHex(byte[] h) => BitConverter.ToString(h).Replace("-", "").ToLowerInvariant();

    /// <summary>每帧由 SimBridge._Process 调用（仅回放模式）。注入当前回合的命令或停止模拟。</summary>
    public void Pump()
    {
        if (_finished) return;
        uint current = _sim.NetTurn.CurrentTurn;
        if (current > _maxTurn)
        {
            _sim.SimulationRunning = false;
            _finished = true;
            _sim.NetTurn.OnTurnAdvanced -= VerifyHash;  // 退订
            ZeroAD.Sim.Diag.Log("Replay", "playback finished");
            return;
        }
        if (_batches.TryGetValue(current, out var cmds))
            _sim.NetTurn.InjectReplayBundle(current, cmds);
    }
}
