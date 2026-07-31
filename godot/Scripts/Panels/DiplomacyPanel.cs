using System;
using Godot;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

// Diplomacy 面板(对齐 session/diplomacy/DiplomacyDialog.js + diplomacy/Player.js 控件)。
// 表头:Player / Civ / Team / Their Stance / Our Stance(A·N·E) / Tribute(Food·Wood·Stone·Metal)。
// 每 non-gaia 玩家一行(含自己行,控件禁用):
//   - 名字(玩家色)+ 状态后缀(Defeated/Won)
//   - 文明(PlayerComponent.Civ)、队(Team>=0?Team+1:"None")
//   - "Their Stance" = 对方 DiplomacyComponent.GetStance(local) → Ally/Neutral/Enemy(只读)
//   - A/N/E 三钮:设本地对其立场(当前档标记),点击 → CommandSetStance(原版 unilateral-worsening 在内核)
//   - 进贡 4 钮:普通=100,Shift=500(原版);双方 inactive 或本地余额不足时禁用 → CommandTribute
// 延后(占位禁用):停火计数器、攻击请求、间谍请求、外交颜色切换。面板不暂停 sim。
public sealed partial class DiplomacyPanel : ModalPanelBase
{
    private readonly SimBridge _sim;
    private GridContainer _grid = null!;
    private Label _status = null!;

    public DiplomacyPanel(SimBridge sim) => _sim = sim;

    public override void _Ready()
    {
        var (content, status) = BuildShell("Diplomacy", minWidth: 760);
        _status = status;

        _grid = new GridContainer { Columns = 6, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 12);
        _grid.AddThemeConstantOverride("v_separation", 6);
        content.AddChild(_grid);

        AddButton(content, "Close", Close, minWidth: 160);

        // 延后项占位提示。
        var note = MakeLabel("Ceasefire / spy / attack-request: not yet wired (shown disabled).", 12);
        note.AddThemeColorOverride("font_color", new Color(0.7f, 0.65f, 0.5f));
        content.AddChild(note);
    }

    protected override void OnOpen() => Rebuild();

    private void Rebuild()
    {
        foreach (var n in _grid.GetChildren())
            ((Node)n).QueueFree();

        // 表头。
        foreach (var h in new[] { "Player", "Civ", "Team", "Their Stance", "Our Stance", "Tribute" })
        {
            var lbl = MakeLabel(h, 14);
            lbl.AddThemeColorOverride("font_color", new Color(0.85f, 0.78f, 0.55f));
            _grid.AddChild(lbl);
        }

        int localId = (int)_sim.LocalPlayerId;
        var localEnt = _sim.Sim.GetPlayerEntityId(localId);
        var localDip = localEnt.HasValue ? _sim.Sim.QueryInterface<DiplomacyComponent>(localEnt.Value) : null;
        var localPlayer = _sim.GetPlayer();

        foreach (int pid in _sim.Sim.Players.GetNonGaiaPlayerIds())
        {
            var otherEnt = _sim.Sim.GetPlayerEntityId(pid);
            var other = _sim.Sim.GetPlayerEntity(pid);
            var otherDip = otherEnt.HasValue ? _sim.Sim.QueryInterface<DiplomacyComponent>(otherEnt.Value) : null;
            if (other == null) continue;

            // 1) 玩家名(玩家色)+ 状态后缀。
            bool self = pid == localId;
            string name = self ? $"Player {pid} (You)" : $"Player {pid}";
            if (other.IsDefeated()) name += "  [Defeated]";
            else if (other.HasWon()) name += "  [Won]";
            var nameLbl = MakeLabel(name, 14);
            nameLbl.HorizontalAlignment = HorizontalAlignment.Left;
            nameLbl.AddThemeColorOverride("font_color", SimBridge.GetPlayerColor(pid));

            // 2) 文明。
            var civLbl = MakeLabel(other.Civ, 14);
            civLbl.HorizontalAlignment = HorizontalAlignment.Left;

            // 3) 队。
            var teamLbl = MakeLabel(other.Team >= 0 ? (other.Team + 1).ToString() : "None", 14);

            // 4) 对方对我立场(只读)。
            int theirStance = otherDip?.GetStance(localId) ?? DiplomacyComponent.Neutral;
            var theirLbl = MakeLabel(StanceName(theirStance), 14);
            theirLbl.AddThemeColorOverride("font_color", StanceColor(theirStance));

            // 5) A/N/E 设立场钮(当前档标记)。
            int ourStance = localDip?.GetStance(pid) ?? DiplomacyComponent.Neutral;
            _grid.AddChild(nameLbl);
            _grid.AddChild(civLbl);
            _grid.AddChild(teamLbl);
            _grid.AddChild(theirLbl);
            _grid.AddChild(MakeStanceButtons(pid, ourStance, self || (localDip?.IsTeamLocked() ?? false)));
            _grid.AddChild(MakeTributeButtons(pid, self, localPlayer, other));
        }

        _status.Text = localPlayer == null
            ? "No local player."
            : $"Resources:  Wood {localPlayer.Wood}   Food {localPlayer.Food}   Stone {localPlayer.Stone}   Metal {localPlayer.Metal}";
    }

    private HBoxContainer MakeStanceButtons(int pid, int current, bool disabled)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        foreach (var (stance, glyph) in new[]
        {
            (DiplomacyComponent.Ally, "Ally"),
            (DiplomacyComponent.Neutral, "Neutral"),
            (DiplomacyComponent.Enemy, "Enemy"),
        })
        {
            bool isCurrent = stance == current;
            var btn = new Button
            {
                Text = glyph,
                Theme = UITheme.GetTheme(),
                Disabled = disabled,
                ToggleMode = true,
                ButtonPressed = isCurrent,
                CustomMinimumSize = new Vector2(64, 26),
            };
            int capturedStance = stance;
            btn.Pressed += () => _sim.CommandSetStance(pid, capturedStance);
            row.AddChild(btn);
        }
        return row;
    }

    private HBoxContainer MakeTributeButtons(int pid, bool self, PlayerComponent? local, PlayerComponent other)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        bool localActive = local?.IsActive() ?? false;
        bool targetActive = other.IsActive();
        foreach (var t in AllResources)
        {
            bool afford100 = local != null && local.CanAfford(t, 100);
            var btn = new Button
            {
                Text = ResourceName(t),
                Theme = UITheme.GetTheme(),
                Disabled = self || !localActive || !targetActive || !afford100,
                CustomMinimumSize = new Vector2(60, 26),
                TooltipText = self ? "" : "Click = 100, Shift = 500",
            };
            btn.AddThemeColorOverride("font_color", ResourceColor(t));
            ResourceType captured = t;
            btn.Pressed += () =>
            {
                int amount = IsShiftHeld() ? 500 : 100;
                _sim.CommandTribute(pid, captured, amount);
            };
            row.AddChild(btn);
        }
        return row;
    }

    private static bool IsShiftHeld() =>
        global::Godot.Input.IsPhysicalKeyPressed(Key.Shift);

    private static string StanceName(int s) => s switch
    {
        DiplomacyComponent.Ally => "Ally",
        DiplomacyComponent.Enemy => "Enemy",
        _ => "Neutral",
    };

    private static Color StanceColor(int s) => s switch
    {
        DiplomacyComponent.Ally => new Color(0.35f, 0.75f, 0.40f),
        DiplomacyComponent.Enemy => new Color(0.86f, 0.32f, 0.30f),
        _ => new Color(0.80f, 0.78f, 0.62f),
    };
}
