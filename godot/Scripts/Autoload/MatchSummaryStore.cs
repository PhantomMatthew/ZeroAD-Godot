using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot;

// 战局统计跨场景快照(autoload)。session 结束(PlayerWon/PlayerDefeated)前由表现层写入,
// ChangeScene 到 Summary 场景后由 SummaryPanel 读取。本轮 Phase 0 只存基础可见信息;Phase 4 起
// 补内核 StatisticsTracker 完整统计(kills/gathered/score/sequences)再扩字段。
public partial class MatchSummaryStore : Node
{
    public record PlayerSnapshot(
        int Id, string Civ, int Team, Color Color, string State,
        int Wood, int Food, int Stone, int Metal, int PopUsed, int PopLimit);

    public string MapName = "";
    public uint TimeElapsedTurns;
    public string ResultText = "";
    public IReadOnlyList<PlayerSnapshot> Players = new List<PlayerSnapshot>();

    public void Clear()
    {
        MapName = "";
        TimeElapsedTurns = 0;
        ResultText = "";
        Players = new List<PlayerSnapshot>();
    }
}
