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

    // ── gamesetup 选项（对齐原版;仅 random 图生效的项在 skirmish/scenario 被忽略）──
    /// <summary>地图尺寸(Tiny 128 … Giant 512;原版默认 Normal 256)。0 = 不改(192)。</summary>
    public int MapSize;
    /// <summary>biome 选择("random" = 图内随机,同上游;"" = 未设置)。</summary>
    public string BiomeId = "";
    /// <summary>玩家布置(circle/river/groupedLines/randomGroup/stronghold;"" = 图脚本默认)。</summary>
    public string PlayerPlacement = "";
    /// <summary>起始资源(四项同值;原版 Low=300 默认)。0 = 模板默认(300/300/200/100)。</summary>
    public int StartingResources;
    /// <summary>人口上限(每玩家;0 = 默认 300)。</summary>
    public int PopulationCap;
    /// <summary>游戏速度倍率(原版默认 1.0;0 = 不改)。</summary>
    public float GameSpeed;
    /// <summary>Nomad(无 CC 开局,仅起始单位)。</summary>
    public bool Nomad;
    /// <summary>宝藏(原版默认开;当前 rmgen 图内宝藏暂不随此项开关,仅记录)。</summary>
    public bool Treasures = true;
    /// <summary>已探索(开局全图进入 explored 迷雾态)。</summary>
    public bool ExploredMap;
    /// <summary>全图揭示(无迷雾)。</summary>
    public bool RevealedMap;
    /// <summary>盟友视野共享(开局即共享 LOS)。</summary>
    public bool AlliedView;
    /// <summary>锁定队伍(禁改外交)。</summary>
    public bool LockedTeams;
    /// <summary>作弊(当前无作弊指令实现,仅记录)。</summary>
    public bool Cheats;
    /// <summary>胜利条件(原版 victory_conditions 名;空 = ["conquest"] 默认征服)。</summary>
    public System.Collections.Generic.List<string> VictoryConditions = new();

    // ── 战役上下文(原版 initAttributes.settings.campaignData)──
    /// <summary>战役 run 存档文件名(user://saves/campaigns/{file}.0adcampaign;"" = 非战役局)。</summary>
    public string CampaignRunFile = "";
    /// <summary>战役关卡 id(模板 Levels 键;胜利时回写 MarkLevelComplete)。</summary>
    public string CampaignLevelId = "";

    // ── autostart(CLI -autostart-*;原版 binaries/data/mods/public/autostart/)──
    /// <summary>每玩家 AI 难度(playerId → 0..5;autostart-aidiff;无项 = Medium 默认)。</summary>
    public System.Collections.Generic.Dictionary<int, int> AiDifficulties = new();
    /// <summary>MP 自动连接:非空 = 跳过连接表单直 host/join(host 为 "host",client 为 IP)。</summary>
    public string MpAutoTarget = "";
    /// <summary>MP 自动连接端口(0 = 用 UserConfig 默认)。</summary>
    public int MpAutoPort;
    /// <summary>MP host 的人类玩家数(autostart-host-players;0 = 默认)。</summary>
    public int MpAutoHostPlayers;

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
        MapSize = 0;
        BiomeId = "";
        PlayerPlacement = "";
        StartingResources = 0;
        PopulationCap = 0;
        GameSpeed = 0;
        Nomad = false;
        Treasures = true;
        ExploredMap = false;
        RevealedMap = false;
        AlliedView = false;
        LockedTeams = false;
        Cheats = false;
        VictoryConditions = new();
        CampaignRunFile = "";
        CampaignLevelId = "";
        AiDifficulties = new();
        MpAutoTarget = "";
        MpAutoPort = 0;
        MpAutoHostPlayers = 0;
    }
}
