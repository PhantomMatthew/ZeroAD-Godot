using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot;

// Match Settings 面板(对齐 session/MenuButtons.js 的 match-settings → getGameDescription 只读摘要)。
// 玩家花名册(玩家色/文明/队/状态/人口)经 GuiInterface.GetPlayerRoster 桥读(无内核直查)。
// 底部行显示真实开局配置:地图名取 SimBridge.MapPath(开局/冷加载/回放各路径都写此运行时
// 字段),胜利条件经 GuiInterface.GetVictoryConditions 桥读(EndGameManager 运行时值,含地图
// 脚本注入),种子取 GameLaunchConfig.Seed(SP 选图面板随机摇 / MP host 冻结经协议下发 /
// 教程固定 42;Load/Replay 局状态整份恢复、构造种子不再具含义,显示 "—")。
// 面板只读,Close 关闭。不暂停 sim。
public sealed partial class MatchSettingsPanel : ModalPanelBase
{
    private readonly SimBridge _sim;
    private GridContainer _grid = null!;
    private Label _status = null!;

    public MatchSettingsPanel(SimBridge sim) => _sim = sim;

    public override void _Ready()
    {
        var (content, status) = BuildShell("Match Settings", minWidth: 620);
        _status = status;

        _grid = new GridContainer { Columns = 5, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 16);
        _grid.AddThemeConstantOverride("v_separation", 6);
        content.AddChild(_grid);

        AddButton(content, "Close", Close, minWidth: 160);
    }

    protected override void OnOpen() => Rebuild();

    private void Rebuild()
    {
        foreach (var n in _grid.GetChildren())
            ((Node)n).QueueFree();

        foreach (var h in new[] { "Player", "Civ", "Team", "State", "Population" })
        {
            var lbl = MakeLabel(h, 14);
            lbl.AddThemeColorOverride("font_color", new Color(0.85f, 0.78f, 0.55f));
            _grid.AddChild(lbl);
        }

        int localId = (int)_sim.LocalPlayerId;
        foreach (var row in _sim.Gui.GetPlayerRoster())
        {
            var nameLbl = MakeLabel(row.PlayerId == localId
                ? $"Player {row.PlayerId} (You)" : $"Player {row.PlayerId}", 14);
            nameLbl.HorizontalAlignment = HorizontalAlignment.Left;
            nameLbl.AddThemeColorOverride("font_color", SimBridge.GetPlayerColor(row.PlayerId));

            _grid.AddChild(nameLbl);
            _grid.AddChild(Left(row.Civ));
            _grid.AddChild(Left(row.Team >= 0 ? (row.Team + 1).ToString() : "None"));
            var stateLbl = Left(StateName(row));
            stateLbl.AddThemeColorOverride("font_color", StateColor(row));
            _grid.AddChild(stateLbl);
            _grid.AddChild(Left($"{row.PopUsed}/{row.PopulationLimit}"));
        }

        _status.Text = $"Map: {MapDisplayName()}\nVictory: {VictoryDisplay()}\nSeed: {SeedDisplay()}";
    }

    /// <summary>地图显示名(同加载页口径 Main.MapTitleFromPath:文件名去扩展名、下划线
    /// 换空格、首字母大写;random/x 取脚本名)。无路径(PMP 缺失的生成回退地形)→
    /// "Generated terrain"。</summary>
    private string MapDisplayName()
    {
        string? rel = _sim.MapPath;
        if (string.IsNullOrEmpty(rel)) return "Generated terrain";
        string name = System.IO.Path.GetFileNameWithoutExtension(rel).Replace('_', ' ').Trim();
        return name.Length == 0 ? "Generated terrain" : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>胜利条件显示(桥读 EndGameManager 运行时值;空表 = 默认征服,与
    /// EndGameManager.HasCondition 口径一致)。条件名转读法:下划线换空格、首字母大写。</summary>
    private string VictoryDisplay()
    {
        var conditions = _sim.Gui.GetVictoryConditions();
        if (conditions.Count == 0) return "Conquest";
        var names = new List<string>(conditions.Count);
        foreach (var c in conditions)
        {
            string pretty = c.Replace('_', ' ').Trim();
            names.Add(pretty.Length == 0 ? c : char.ToUpperInvariant(pretty[0]) + pretty[1..]);
        }
        return string.Join(", ", names);
    }

    /// <summary>种子显示:Load/Replay 局世界状态自存档/录像整份恢复,构造种子不再具含义,
    /// 标 "—";其余(SP/MP/教程)显示 GameLaunchConfig.Seed 实际下发值。</summary>
    private string SeedDisplay()
    {
        var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
        return cfg.Mode is GameLaunchConfig.LaunchMode.Load or GameLaunchConfig.LaunchMode.Replay
            ? "—" : cfg.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Label Left(string text)
    {
        var l = MakeLabel(text, 14);
        l.HorizontalAlignment = HorizontalAlignment.Left;
        return l;
    }

    private static string StateName(GuiInterface.PlayerRosterRow r) =>
        r.IsDefeated ? "Defeated" : r.HasWon ? "Won" : "Active";

    private static Color StateColor(GuiInterface.PlayerRosterRow r) =>
        r.IsDefeated ? new Color(0.86f, 0.32f, 0.30f)
        : r.HasWon ? new Color(0.40f, 0.80f, 0.50f)
        : new Color(0.80f, 0.80f, 0.74f);
}
