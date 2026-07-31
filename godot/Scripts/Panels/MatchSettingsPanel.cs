using System;
using Godot;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

// Match Settings 面板(对齐 session/MenuButtons.js 的 match-settings → getGameDescription 只读摘要)。
// v1 显示可从 sim 直接读取的内容:玩家花名册(玩家色/文明/队/状态/人口)+ 人口上限。
// 地图名/胜利条件/种子属 gamesetup 会话外数据(当前建图硬编码 seed=42、civ=athen 为已知缺口),
// 本轮显示运行时实际设置并标注为待接线。面板只读,Close 关闭。不暂停 sim。
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
        foreach (int pid in _sim.Sim.Players.GetNonGaiaPlayerIds())
        {
            var p = _sim.Sim.GetPlayerEntity(pid);
            if (p == null) continue;

            var nameLbl = MakeLabel(pid == localId ? $"Player {pid} (You)" : $"Player {pid}", 14);
            nameLbl.HorizontalAlignment = HorizontalAlignment.Left;
            nameLbl.AddThemeColorOverride("font_color", SimBridge.GetPlayerColor(pid));

            _grid.AddChild(nameLbl);
            _grid.AddChild(Left(p.Civ));
            _grid.AddChild(Left(p.Team >= 0 ? (p.Team + 1).ToString() : "None"));
            var stateLbl = Left(StateName(p.State));
            stateLbl.AddThemeColorOverride("font_color", StateColor(p.State));
            _grid.AddChild(stateLbl);
            _grid.AddChild(Left($"{p.PopUsed}/{p.PopulationLimit}"));
        }

        _status.Text = "Map / victory condition / seed: not yet captured (gamesetup hard-coding is a known gap).";
    }

    private static Label Left(string text)
    {
        var l = MakeLabel(text, 14);
        l.HorizontalAlignment = HorizontalAlignment.Left;
        return l;
    }

    private static string StateName(PlayerState s) => s switch
    {
        PlayerState.Defeated => "Defeated",
        PlayerState.Won => "Won",
        _ => "Active",
    };

    private static Color StateColor(PlayerState s) => s switch
    {
        PlayerState.Defeated => new Color(0.86f, 0.32f, 0.30f),
        PlayerState.Won => new Color(0.40f, 0.80f, 0.50f),
        _ => new Color(0.80f, 0.80f, 0.74f),
    };
}
