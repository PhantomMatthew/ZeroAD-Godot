using System;
using Godot;

namespace ZeroAD.Godot;

/// <summary>回放游戏内控制条（画面顶部）。仅回放模式显示。
/// 控制：▶/⏸ 暂停、1x/2x/4x/8x 速度、"回合 N / M" 显示、退出。
/// 复用 SimBridge.Paused / SpeedMultiplier——与实时游戏的暂停/调速完全相同机制。</summary>
public sealed partial class ReplayControls : CanvasLayer
{
    private readonly SimBridge _sim;
    private Button _playPauseBtn = null!;
    private Label _turnLabel = null!;
    private double _labelTimer;  // 节流：回合显示每 0.25s 刷新一次（避免每帧扫 NTM）

    public event Action? OnExit;

    public ReplayControls(SimBridge sim)
    {
        _sim = sim;
        Layer = 70;  // 在 HUD/GameOverOverlay 之上
        ProcessMode = ProcessModeEnum.Always;  // 暂停时仍可交互
    }

    public override void _Ready()
    {
        var bar = new HBoxContainer
        {
            AnchorRight = 1.0f,
            OffsetTop = 4,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        // 半透明深色背景，让控制条在任意地图上都可读
        var bg = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.65f),
            ContentMarginLeft = 8, ContentMarginRight = 8,
            ContentMarginTop = 4, ContentMarginBottom = 4,
        };
        bar.AddThemeStyleboxOverride("panel", bg);
        AddChild(bar);

        _playPauseBtn = new Button { Text = "⏸", CustomMinimumSize = new Vector2(40, 32), FocusMode = Control.FocusModeEnum.None };
        _playPauseBtn.Pressed += OnPlayPause;
        bar.AddChild(_playPauseBtn);

        bar.AddChild(new Label { Text = "  速度:  ", VerticalAlignment = VerticalAlignment.Center });

        foreach (var speed in new[] { 1.0, 2.0, 4.0, 8.0 })
        {
            var b = new Button { Text = $"{speed}x", ToggleMode = true,
                CustomMinimumSize = new Vector2(44, 28), FocusMode = Control.FocusModeEnum.None };
            if (Math.Abs(speed - 1.0) < 0.01) b.ButtonPressed = true;  // 默认 1x
            b.Pressed += () => OnSpeed(speed, b);
            bar.AddChild(b);
        }

        _turnLabel = new Label { Text = "回合 0", VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(120, 0) };
        bar.AddChild(_turnLabel);

        var exitBtn = new Button { Text = "退出", FocusMode = Control.FocusModeEnum.None };
        exitBtn.Pressed += () => OnExit?.Invoke();
        bar.AddChild(exitBtn);
    }

    public override void _Process(double delta)
    {
        _labelTimer += delta;
        if (_labelTimer >= 0.25)
        {
            _labelTimer = 0;
            uint cur = _sim.NetTurn.CurrentTurn;
            uint total = _sim.IsReplayMode ? _sim.ReplayTotalTurns : 0;
            _turnLabel.Text = total > 0 ? $"回合 {cur} / {total}" : $"回合 {cur}";
        }
    }

    private void OnPlayPause()
    {
        _sim.Paused = !_sim.Paused;
        _playPauseBtn.Text = _sim.Paused ? "▶" : "⏸";
    }

    private void OnSpeed(double speed, Button sender)
    {
        _sim.SpeedMultiplier = speed;
        // 取消同组其它速度按钮的按下态（单选行为）
        foreach (var child in ((HBoxContainer)_playPauseBtn.GetParent()).GetChildren())
            if (child is Button b && b.ToggleMode && b != sender)
                b.ButtonPressed = false;
        sender.ButtonPressed = true;
    }
}
