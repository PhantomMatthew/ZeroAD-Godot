using Godot;

namespace ZeroAD.Godot;

// 跨场景"怎么开始这局游戏"载体(autoload)。MainMenu 写入 → ChangeScene(session)→ Main._Ready 读取。
// 替代 ZEROAD_AUTOSTART/TUTORIAL 环境变量:那些是进程级,ChangeScene 回主菜单会重触发;此 singleton
// 每次开始前 Reset,无此问题。Load 分支与 Slots 供 Phase 2(LoadGame)/MP 跨场景重构用。
public partial class GameLaunchConfig : Node
{
    public enum LaunchMode { Lobby, SinglePlayer, Tutorial, Load, Replay, Multiplayer }

    public LaunchMode Mode = LaunchMode.Lobby;
    public uint Seed = 42;
    public string LoadSlot = "";
    public string ReplaySlot = "";   // Replay 模式：user://replays/ 下的录像 slot 名
    public bool MpHost;
    /// <summary>本局地图 rel 路径（SP 专用）。"" = 默认回退链（arcadia→laconia）。
    /// 支持三类：scenario pmp（"maps/scenarios/x.pmp"）、skirmish pmp
    /// （"maps/skirmishes/x.pmp"——XML 占位实体按槽位文明替换生成）、随机图
    /// （"random/mainland" 等）。MP 未进协议前由 host 默认地图。</summary>
    public string MapPath = "";
    /// <summary>停战时长(分钟;0=关,原版 gamesetup Ceasefire 默认)。&gt;0 时开局全体
    /// 非 gaia 玩家互置中立,倒计时结束恢复外交(EndGameManager.StartCeasefire)。</summary>
    public int CeasefireMinutes;
    public System.Collections.Generic.IReadOnlyList<ZeroAD.Sim.Net.PlayerSlotSetup>? Slots;

    /// <summary>开始新一局前重置为大厅默认(避免上一局残留 Mode 触发错误启动)。</summary>
    public void Reset()
    {
        Mode = LaunchMode.Lobby;
        Seed = 42;
        LoadSlot = "";
        ReplaySlot = "";
        MpHost = false;
        MapPath = "";
        CeasefireMinutes = 0;
        Slots = null;
    }
}
