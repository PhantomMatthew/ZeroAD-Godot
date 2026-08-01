using Godot;

namespace ZeroAD.Godot;

// 跨场景"怎么开始这局游戏"载体(autoload)。MainMenu 写入 → ChangeScene(session)→ Main._Ready 读取。
// 替代 ZEROAD_AUTOSTART/TUTORIAL 环境变量:那些是进程级,ChangeScene 回主菜单会重触发;此 singleton
// 每次开始前 Reset,无此问题。Load 分支与 Slots 供 Phase 2(LoadGame)/MP 跨场景重构用。
public partial class GameLaunchConfig : Node
{
    public enum LaunchMode { Lobby, SinglePlayer, Tutorial, Load, Multiplayer }

    public LaunchMode Mode = LaunchMode.Lobby;
    public uint Seed = 42;
    public string LoadSlot = "";
    public System.Collections.Generic.IReadOnlyList<ZeroAD.Sim.Net.PlayerSlotSetup>? Slots;

    /// <summary>开始新一局前重置为大厅默认(避免上一局残留 Mode 触发错误启动)。</summary>
    public void Reset()
    {
        Mode = LaunchMode.Lobby;
        Seed = 42;
        LoadSlot = "";
        Slots = null;
    }
}
