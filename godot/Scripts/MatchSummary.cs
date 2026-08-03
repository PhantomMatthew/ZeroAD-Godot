using System.Collections.Generic;
using System.Linq;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

/// <summary>单玩家结算数据快照（UI 展示用 POCO）。从 StatisticsTracker + PlayerComponent 提取。</summary>
public sealed class PlayerSummary
{
    public int PlayerId;
    public string Civ = "";
    public int Team = -1;
    public string State = "";   // Active/Defeated/Won
    public StatisticsSnapshot Stats = new();
    public (int total, int economy, int military, int exploration) Score;
}

/// <summary>整局结算数据。GameEndedEvent 时由 SimBridge.GetMatchSummary() 收集，传给 SummaryPanel。</summary>
public sealed class MatchSummary
{
    public List<PlayerSummary> Players = new();
    public int WinnerPlayerId = -1;
    public string MapPath = "";
}

/// <summary>结算数据导出扩展。挂在 SimBridge 上（同 SaveGameManager 模式）。</summary>
public static class MatchSummaryExporter
{
    /// <summary>从当前 sim 收集所有玩家的统计快照 + 元信息。在 GameEndedEvent 或手动查看时调用。</summary>
    public static MatchSummary Collect(SimBridge sim)
    {
        var summary = new MatchSummary
        {
            MapPath = sim.MapPath ?? "",
        };
        var cm = sim.Sim;
        foreach (int pid in cm.Players.GetNonGaiaPlayerIds())
        {
            var playerEnt = cm.Players.GetPlayerEntityId(pid);
            if (playerEnt == null) continue;
            var player = cm.QueryInterface<PlayerComponent>(playerEnt.Value);
            var tracker = cm.QueryInterface<StatisticsTrackerComponent>(playerEnt.Value);
            var ps = new PlayerSummary
            {
                PlayerId = pid,
                Civ = player?.Civ ?? "",
                Team = player?.Team ?? -1,
                State = player?.State.ToString() ?? "",
                Stats = tracker?.GetStatistics() ?? new StatisticsSnapshot(),
                Score = tracker?.GetScore() ?? (0, 0, 0, 0),
            };
            summary.Players.Add(ps);
        }
        summary.Players.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
        return summary;
    }
}
