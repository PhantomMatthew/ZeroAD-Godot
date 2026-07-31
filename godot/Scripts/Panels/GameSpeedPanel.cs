using System;
using Godot;

namespace ZeroAD.Godot;

// Game Speed 面板(对齐 session/GameSpeedControl.js):11 档倍率 OptionButton,选中即设
// SimBridge.SpeedMultiplier(本地表现层节奏;原版 Engine.SetSimRate 亦本地,MP 下会失步→延后)。
// Fast-Forward 档(5/10/20×)原版仅当本地玩家 state≠active 显示;端口恒显示但本地玩家已败/胜时
// 仍允许(无害)。面板不暂停 sim(只模态挡鼠标)。
public sealed partial class GameSpeedPanel : ModalPanelBase
{
    private readonly SimBridge _sim;
    private OptionButton _speeds = null!;
    private Label _status = null!;

    // 原版 GameSpeedControl.js 的 11 档(rate 与显示)。
    private static readonly (double rate, string label)[] Speeds =
    {
        (0.5, "0.5×"), (0.75, "0.75×"), (1.0, "Normal"), (1.25, "1.25×"),
        (1.5, "1.5×"), (2.0, "2×"), (5.0, "Fast (5×)"),
        (10.0, "Very Fast (10×)"), (20.0, " Extremely Fast (20×)"),
    };

    public GameSpeedPanel(SimBridge sim) => _sim = sim;

    public override void _Ready()
    {
        var (content, status) = BuildShell("Game Speed", minWidth: 320);
        _status = status;

        _speeds = new OptionButton { Theme = UITheme.GetTheme(), SizeFlagsHorizontal = Control.SizeFlags.Fill };
        _speeds.AddThemeConstantOverride("minimum_character_width", 22);
        foreach (var (_, label) in Speeds)
            _speeds.AddItem(label);
        _speeds.ItemSelected += OnSpeedSelected;
        content.AddChild(_speeds);

        AddButton(content, "Close", Close, minWidth: 160);
    }

    private void OnSpeedSelected(long idx)
    {
        if (idx < 0 || idx >= Speeds.Length) return;
        double rate = Speeds[(int)idx].rate;
        _sim.SpeedMultiplier = rate;
        _status.Text = $"Game speed: {rate}×";
    }

    protected override void OnOpen()
    {
        // 回显当前倍率(取最近档)。
        double cur = _sim.SpeedMultiplier;
        int best = 2; // 默认 Normal
        double bestDiff = double.MaxValue;
        for (int i = 0; i < Speeds.Length; i++)
        {
            double d = Math.Abs(Speeds[i].rate - cur);
            if (d < bestDiff) { bestDiff = d; best = i; }
        }
        _speeds.Selected = best;
        _status.Text = $"Game speed: {cur}×";
    }
}
