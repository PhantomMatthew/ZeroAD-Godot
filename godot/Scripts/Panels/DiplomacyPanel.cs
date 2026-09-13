using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using ZeroAD.Sim.Components;

namespace ZeroAD.Godot;

// Diplomacy 面板(对齐 session/diplomacy/DiplomacyDialog.js + diplomacy/Player.js 控件)。
// 表头:Player / Civ / Team / Their Stance / Our Stance(A·N·E) / Tribute(Food·Wood·Stone·Metal) / Request。
// 每 non-gaia 玩家一行(含自己行,控件禁用):
//   - 名字(玩家色)+ 状态后缀(Defeated/Won)
//   - 文明(PlayerComponent.Civ)、队(Team>=0?Team+1:"None")
//   - "Their Stance" = 对方 DiplomacyComponent.GetStance(local) → Ally/Neutral/Enemy(只读)
//   - A/N/E 三钮:设本地对其立场(当前档标记),点击 → CommandSetStance(原版 unilateral-worsening 在内核)
//   - 进贡 4 钮:普通=100,Shift=500(原版);双方 inactive 或本地余额不足时禁用 → CommandTribute
//   - 请求列:盟友行攻击请求钮 + 间谍钮(原版 SpyRequestButton:贿赂对方随机可贿单位,
//     限时共享其视野;前置/费用不足置灰+提示;点击后 pending 置灰待 SpyResponse 答复;
//     1s 漂移刷新钮态——余额/研究随局内变化)
// 延后:外交颜色切换。面板不暂停 sim。
public sealed partial class DiplomacyPanel : ModalPanelBase
{
    private readonly SimBridge _sim;
    private GridContainer _grid = null!;
    private Label _status = null!;
    private Label _ceasefireLabel = null!;
    /// <summary>已发间谍请求待答复的目标玩家集(原版 SpyRequestButton.spyRequests)。</summary>
    private readonly HashSet<int> _spyPending = new();
    /// <summary>行 → 间谍钮(Rebuild 重填;漂移刷新按此逐行更新置灰/提示)。</summary>
    private readonly Dictionary<int, Button> _spyButtons = new();
    private float _driftAccum;

    public DiplomacyPanel(SimBridge sim) => _sim = sim;

    public override void _Ready()
    {
        var (content, status) = BuildShell("Diplomacy", minWidth: 760);
        _status = status;

        _grid = new GridContainer { Columns = 7, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 12);
        _grid.AddThemeConstantOverride("v_separation", 6);
        content.AddChild(_grid);

        // 停火倒计时(原版 DiplomacyDialogCeasefireCounter;激活时才显)。
        _ceasefireLabel = MakeLabel("", 14);
        _ceasefireLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.4f));
        content.AddChild(_ceasefireLabel);

        AddButton(content, "Close", Close, minWidth: 160);
    }

    protected override void OnOpen()
    {
        // 停火倒计时(原版 update 每帧;我们打开即填,行内足够)。
        var cf = _sim.Gui.GetCeasefireState();
        _ceasefireLabel.Visible = cf.Active;
        if (cf.Active)
        {
            int total = (int)cf.RemainingSeconds;
            _ceasefireLabel.Text = string.Format(
                Localization.Tr("Remaining ceasefire time: {0}"), $"{total / 60}:{total % 60:00}");
        }
        // 间谍 pending 只在开页期接收答复(关页退订);上次未竟请求按上游重开
        // 对话框的语义丢弃,重建为可点。
        _spyPending.Clear();
        _sim.Sim.Events.SpyResponse += OnSpyResponse;
        Rebuild();
    }

    protected override void OnClose() => _sim.Sim.Events.SpyResponse -= OnSpyResponse;

    /// <summary>间谍答复(内核锁步执行后必发;无可贿单位时另有 spy-failed toast,见
    /// Main.OnPlayerCommandEvent):清 pending 并刷新钮态。只应本地玩家发出的请求。</summary>
    private void OnSpyResponse(ZeroAD.Sim.Events.SpyResponseEvent e)
    {
        if (e.Requester != (int)_sim.LocalPlayerId) return;
        _spyPending.Remove(e.Target);
        if (Visible) RefreshSpyButtons();
    }

    /// <summary>1s 漂移刷新(TradePanel 同款节拍):余额/研究随局内变化,只重算间谍钮
    /// 置灰与提示;可见性翻转(对方战败/立场变更——罕见)才整表 Rebuild。</summary>
    public override void _Process(double delta)
    {
        if (!Visible) return;
        _driftAccum += (float)delta;
        if (_driftAccum < 1f) return;
        _driftAccum = 0;
        RefreshSpyButtons();
    }

    private void RefreshSpyButtons()
    {
        var state = _sim.Gui.GetDiplomacyState((int)_sim.LocalPlayerId);
        foreach (var row in state.Rows)
        {
            var st = _sim.Gui.GetSpyRequestState((int)_sim.LocalPlayerId, row.PlayerId);
            if (st.Visible != _spyButtons.ContainsKey(row.PlayerId)) { Rebuild(); return; }
            if (!st.Visible) continue;
            var btn = _spyButtons[row.PlayerId];
            btn.Disabled = !st.Researched || !st.Affordable || _spyPending.Contains(row.PlayerId);
            btn.TooltipText = SpyTooltip(st);
        }
    }

    private void Rebuild()
    {
        _spyButtons.Clear();
        foreach (var n in _grid.GetChildren())
            ((Node)n).QueueFree();

        // 表头。
        foreach (var h in new[] { "Player", "Civ", "Team", "Their Stance", "Our Stance", "Tribute", "Request" })
        {
            var lbl = MakeLabel(h, 14);
            lbl.AddThemeColorOverride("font_color", new Color(0.85f, 0.78f, 0.55f));
            _grid.AddChild(lbl);
        }

        // 全部 sim 读数经 GuiInterface 桥(原版 DiplomacyDialog 只读 GetSimulationState;
        // 此前绕桥直查 Sim/GetPlayerEntityId/QueryInterface——收敛点)。
        var state = _sim.Gui.GetDiplomacyState((int)_sim.LocalPlayerId);

        foreach (var row in state.Rows)
        {
            // 1) 玩家名(玩家色)+ 状态后缀。
            string name = row.IsSelf ? $"Player {row.PlayerId} (You)" : $"Player {row.PlayerId}";
            if (row.IsDefeated) name += "  [Defeated]";
            else if (row.HasWon) name += "  [Won]";
            var nameLbl = MakeLabel(name, 14);
            nameLbl.HorizontalAlignment = HorizontalAlignment.Left;
            nameLbl.AddThemeColorOverride("font_color", SimBridge.GetPlayerColor(row.PlayerId));

            // 2) 文明。
            var civLbl = MakeLabel(row.Civ, 14);
            civLbl.HorizontalAlignment = HorizontalAlignment.Left;

            // 3) 队。
            var teamLbl = MakeLabel(row.Team >= 0 ? (row.Team + 1).ToString() : "None", 14);

            // 4) 对方对我立场(只读)。
            var theirLbl = MakeLabel(StanceName(row.TheirStance), 14);
            theirLbl.AddThemeColorOverride("font_color", StanceColor(row.TheirStance));

            // 5) A/N/E 设立场钮(当前档标记)。
            _grid.AddChild(nameLbl);
            _grid.AddChild(civLbl);
            _grid.AddChild(teamLbl);
            _grid.AddChild(theirLbl);
            _grid.AddChild(MakeStanceButtons(row.PlayerId, row.OurStance, row.IsSelf || row.TeamLocked));
            _grid.AddChild(MakeTributeButtons(row, state.LocalActive));
            // 请求列(攻击请求 + 间谍钮,同格并列;两者皆无 → 空格占位)。
            _grid.AddChild(MakeRequestButtons(row));
        }

        _status.Text = !state.HasLocalPlayer
            ? "No local player."
            : $"Resources:  Wood {state.LocalWood}   Food {state.LocalFood}   Stone {state.LocalStone}   Metal {state.LocalMetal}";
    }

    private HBoxContainer MakeStanceButtons(int pid, GuiInterface.Stance current, bool disabled)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        foreach (var stance in new[]
        {
            GuiInterface.Stance.Ally,
            GuiInterface.Stance.Neutral,
            GuiInterface.Stance.Enemy,
        })
        {
            bool isCurrent = stance == current;
            var btn = new Button
            {
                Text = StanceName(stance),
                Theme = UITheme.GetTheme(),
                Disabled = disabled,
                ToggleMode = true,
                ButtonPressed = isCurrent,
                CustomMinimumSize = new Vector2(64, 26),
            };
            // Stance 枚举值与 DiplomacyComponent 常量对齐(GuiInterface.Stance 定义处注记),
            // 命令侧 (int) 转换即原版 stance 值。
            int capturedStance = (int)stance;
            btn.Pressed += () => _sim.CommandSetStance(pid, capturedStance);
            row.AddChild(btn);
        }
        return row;
    }

    private HBoxContainer MakeTributeButtons(GuiInterface.DiplomacyRow row, bool localActive)
    {
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 4);
        // 原版禁用条件:self || !localActive || !targetActive || !afford100。
        bool enable = !row.IsSelf && localActive && row.IsActive;
        foreach (var t in AllResources)
        {
            bool afford100 = row.Tributeable.TryGetValue(t, out bool can) && can;
            var btn = new Button
            {
                Text = ResourceName(t),
                Theme = UITheme.GetTheme(),
                Disabled = !enable || !afford100,
                CustomMinimumSize = new Vector2(60, 26),
                TooltipText = row.IsSelf ? "" : "Click = 100, Shift = 500",
            };
            btn.AddThemeColorOverride("font_color", ResourceColor(t));
            ResourceType captured = t;
            btn.Pressed += () =>
            {
                int amount = IsShiftHeld() ? 500 : 100;
                _sim.CommandTribute(row.PlayerId, captured, amount);
            };
            hbox.AddChild(btn);
        }
        return hbox;
    }

    /// <summary>请求列(原版 DiplomacyPlayerControl 的 attackRequest + SpyRequestButton,
    /// 同格 HBox 并列):盟友行给攻击请求钮;间谍钮可见性由 GuiInterface.GetSpyRequestState
    /// 聚合(敌/中立行恒可见,互盟行仅共享 LOS 开时——原版同规则)。</summary>
    private Control MakeRequestButtons(GuiInterface.DiplomacyRow row)
    {
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 4);
        // 攻击请求(原版 attackRequest;只对盟友行显示)。
        if (!row.IsSelf && row.OurStance == GuiInterface.Stance.Ally)
        {
            int pid = row.PlayerId;
            var askBtn = new Button
            {
                Text = Localization.Tr("Ask to attack"),
                Theme = UITheme.GetTheme(),
                CustomMinimumSize = new Vector2(0, 24),
                TooltipText = Localization.Tr("Ask this ally to attack an enemy of yours."),
            };
            StoneButtonStyle.Apply(askBtn, StoneButtonStyle.FindBinariesDir());
            askBtn.Pressed += () => AskAttack(pid);
            hbox.AddChild(askBtn);
        }
        var spy = _sim.Gui.GetSpyRequestState((int)_sim.LocalPlayerId, row.PlayerId);
        if (spy.Visible)
            hbox.AddChild(MakeSpyButton(row.PlayerId, spy));
        return hbox;
    }

    /// <summary>间谍请求钮(原版 SpyRequestButton):前置未满足 / 费用不足 → 置灰+提示;
    /// 点击发锁步命令后置 pending(disabled),待 SpyResponse 答复解锁(原版 spyRequests 集)。</summary>
    private Button MakeSpyButton(int targetPid, GuiInterface.SpyRequestState st)
    {
        var btn = new Button
        {
            Text = Localization.Tr("Spy"),
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(0, 24),
            TooltipText = SpyTooltip(st),
            Disabled = !st.Researched || !st.Affordable || _spyPending.Contains(targetPid),
        };
        StoneButtonStyle.Apply(btn, StoneButtonStyle.FindBinariesDir());
        btn.Pressed += () =>
        {
            _sim.CommandSpyRequest(targetPid);
            _spyPending.Add(targetPid);
            btn.Disabled = true;
        };
        _spyButtons[targetPid] = btn;
        return btn;
    }

    /// <summary>间谍钮 tooltip(原版 SpyRequestButton.Tooltip/TooltipFailed 语义):基础句
    /// +(前置不足 → 需求行;否则费用行 + 付不起时缺口行)+ 失败成本注(恒追加)。</summary>
    private string SpyTooltip(GuiInterface.SpyRequestState st)
    {
        var sb = new StringBuilder(Localization.Tr(
            "Bribe a random unit from this player and share its vision during a limited period."));
        if (!st.Researched)
        {
            sb.Append('\n').Append(string.Format(
                Localization.Tr("Requires: {0}"), UnmetSpyTechName(st)));
        }
        else
        {
            sb.Append('\n').Append(string.Format(Localization.Tr("Cost: {0}"), st.Cost.Describe()));
            if (!st.Affordable)
                sb.Append('\n').Append(string.Format(
                    Localization.Tr("Need: {0}"), st.NeededResources.Describe()));
        }
        sb.Append('\n').Append(Localization.Tr("A failed bribe will cost you:"));
        sb.Append('\n').Append(st.FailureCost.Describe());
        return sb.ToString();
    }

    /// <summary>首个未满足前置科技的显示名(原版 getRequirementsTooltip 的精简版;
    /// token 语义与内核 TechnologyManager.MeetsRequirements 对齐:"!" = 须未研究)。</summary>
    private string UnmetSpyTechName(GuiInterface.SpyRequestState st)
    {
        var tm = _sim.Gui.GetTechnologyManager((int)_sim.LocalPlayerId);
        if (tm == null) return st.RequiredTechs;
        foreach (var tok in st.RequiredTechs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            bool neg = tok.StartsWith('!');
            string tech = neg ? tok[1..] : tok;
            if (tm.IsResearched(tech) == neg)
                return tm.GetDefinition(tech)?.GenericName ?? tech;
        }
        return st.RequiredTechs;
    }

    /// <summary>攻击请求(原版 attack-request):目标 = 我方最强敌(实体数最多;
    /// 原版由请求方在子面板选敌——此处取最强敌简化,AI 侧评估兵力后答复)。</summary>
    private void AskAttack(int allyId)
    {
        var state = _sim.Gui.GetDiplomacyState((int)_sim.LocalPlayerId);
        int target = -1;
        int best = -1;
        foreach (var row in state.Rows)
        {
            if (row.IsSelf) continue;
            if (row.OurStance != GuiInterface.Stance.Enemy) continue;
            // 近似强度:活跃即候选;取第一个敌人(行序确定)。
            if (row.IsActive && best < 0) { best = 0; target = row.PlayerId; }
        }
        if (target < 0) return;
        int chosen = target;
        Dialogs.GameMsgBox.Show(this, 400, 180,
            string.Format(Localization.Tr("Ask Player {0} to attack Player {1}?"), allyId, chosen),
            Localization.Tr("Attack Request"),
            new[] { Localization.Tr("Cancel"), Localization.Tr("Request") },
            idx =>
            {
                if (idx != 1) return;
                _sim.CommandRequestAttack(chosen);
                _status.Text = string.Format(
                    Localization.Tr("Attack request sent to Player {0}."), allyId);
            });
    }

    private static bool IsShiftHeld() =>
        global::Godot.Input.IsPhysicalKeyPressed(Key.Shift);

    private static string StanceName(GuiInterface.Stance s) => s switch
    {
        GuiInterface.Stance.Ally => "Ally",
        GuiInterface.Stance.Enemy => "Enemy",
        _ => "Neutral",
    };

    private static Color StanceColor(GuiInterface.Stance s) => s switch
    {
        GuiInterface.Stance.Ally => new Color(0.35f, 0.75f, 0.40f),
        GuiInterface.Stance.Enemy => new Color(0.86f, 0.32f, 0.30f),
        _ => new Color(0.80f, 0.78f, 0.62f),
    };
}
