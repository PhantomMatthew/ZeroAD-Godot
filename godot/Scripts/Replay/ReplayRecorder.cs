using System;
using Godot;
using ZeroAD.Sim.Net;

namespace ZeroAD.Godot;

/// <summary>自动录像录制器。挂钩 <see cref="NetTurnManager.OnBatchDue"/> 旁听每回合命令批，
/// 逐回合写入 <see cref="ReplayWriter"/>。命令流本来就在 NTM 里流转，录制零额外开销。
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
    private bool _finalized;

    public string FilePath { get; }

    internal ReplayRecorder(ReplayWriter writer, NetTurnManager turn, string filePath)
    {
        _writer = writer;
        _turn = turn;
        FilePath = filePath;
        _turn.OnBatchDue += OnBatch;  // 旁听：每回合命令批（含空批）
    }

    private void OnBatch(uint turn, NetCommand[] commands)
    {
        if (_finalized) return;
        _writer.WriteTurnBatch(turn, commands);
    }

    /// <summary>游戏结束时调用（胜利/失败/退出）。退订事件、写 trailer、关流。
    /// 幂等：重复调用安全。</summary>
    public void Finalize(string description)
    {
        if (_finalized) return;
        _finalized = true;
        _turn.OnBatchDue -= OnBatch;
        try { _writer.Dispose(); }
        catch (Exception ex) { GD.PrintErr($"[Replay] finalize failed: {ex.Message}"); }
        GD.Print($"[Replay] recorded to {FilePath}");
    }
}
