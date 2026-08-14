using System;
using System.Collections.Generic;
using Godot;
using ZeroAD.Sim.Net;

namespace ZeroAD.Godot;

/// <summary>自动录像录制器。挂钩 <see cref="NetTurnManager.OnBatchDue"/> 旁听每回合命令批，
/// 逐回合写入 <see cref="ReplayWriter"/>。命令流本来就在 NTM 里流转，录制零额外开销。
///
/// 确定性回归验证:每 <see cref="NetTurnManager.HashCheckInterval"/> 回合算一次状态哈希
/// (MD5),存入内存字典,Finalize 时写尾部哈希日志段。回放时 ReplayDriver 对比同一回合
/// 的哈希——不一致即 desync 回归(改了 NetCommand/sim 逻辑导致同输入不同输出)。
///
/// 生命周期（由 SimBridge 管理）：
///   InitWorld 末尾创建（Standalone/Host 时）→ 游戏运行中每回合 OnBatch 写盘 →
///   OnGameOver / Cleanup 时 Finalize。
///
/// 文件路径：<c>user://replays/{timestamp}_{map}.zreplay</c>（镜像 SaveGameManager 的 user://saves 约定）。</summary>
public sealed class ReplayRecorder
{
    private readonly ReplayWriter _writer;
    private readonly NetTurnManager _turn;
    private readonly Func<byte[]> _hashSource;
    private readonly Dictionary<uint, byte[]> _hashes = new();
    private bool _finalized;

    public string FilePath { get; }

    internal ReplayRecorder(ReplayWriter writer, NetTurnManager turn, string filePath, Func<byte[]> hashSource)
    {
        _writer = writer;
        _turn = turn;
        FilePath = filePath;
        _hashSource = hashSource;
        _turn.OnBatchDue += OnBatch;  // 旁听：每回合命令批（含空批）
        _turn.OnTurnAdvanced += OnTurnAdvanced;  // 哈希校验点
    }

    private void OnBatch(uint turn, NetCommand[] commands)
    {
        if (_finalized) return;
        _writer.WriteTurnBatch(turn, commands);
    }

    private void OnTurnAdvanced(uint turn)
    {
        if (_finalized) return;
        // 与 CheckOOS 同节流:每 HashCheckInterval 回合存一次哈希(够密够省)。
        if (turn % NetTurnManager.HashCheckInterval != 0) return;
        try { _hashes[turn] = _hashSource(); }
        catch (Exception ex) { ZeroAD.Sim.Diag.Err("Replay", $"hash sample failed at turn {turn}: {ex.Message}"); }
    }

    /// <summary>游戏结束时调用（胜利/失败/退出）。退订事件、写 trailer、关流。
    /// 幂等：重复调用安全。</summary>
    public void Finalize(string description)
    {
        if (_finalized) return;
        _finalized = true;
        _turn.OnBatchDue -= OnBatch;
        _turn.OnTurnAdvanced -= OnTurnAdvanced;
        try
        {
            _writer.WriteHashLog(_hashes);
            _writer.Dispose();
        }
        catch (Exception ex) { ZeroAD.Sim.Diag.Err("Replay", $"finalize failed: {ex.Message}"); }
        ZeroAD.Sim.Diag.Log("Replay", $"recorded to {FilePath} ({_hashes.Count} hash checkpoints)");
    }
}
