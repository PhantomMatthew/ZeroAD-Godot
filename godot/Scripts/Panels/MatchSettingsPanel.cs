using Godot;

namespace ZeroAD.Godot;

// Match Settings 面板(对齐 session/MenuButtons.js 的 match-settings → getGameDescription 只读摘要)。
// 玩家花名册(玩家色/文明/队/状态/人口)经 GuiInterface.GetPlayerRoster 桥读(收敛后无内核直查)。
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

        _status.Text = "Map / victory condition / seed: not yet captured (gamesetup hard-coding is a known gap).";
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
