using Godot;
using ZeroAD.Sim;
using ZeroAD.Sim.Events;

namespace ZeroAD.Godot;

// GameOverOverlay — the victory/defeat panel shown when the match ends.
//
// Subscribes to the sim's PlayerWon / PlayerDefeated / GameEnded events (raised by
// ComponentManager.TickVictory). When the local player (player 1) wins, shows "Victory";
// when they're defeated, shows "Defeat". Modelled on TutorialPanel's overlay style.
//
// This is presentation-only: all win/loss logic lives in the deterministic kernel.

public sealed partial class GameOverOverlay : CanvasLayer
{
    private readonly SimBridge _sim;
    private readonly int _localPlayerId;
    private PanelContainer _panel = null!;
    private Label _titleLabel = null!;
    private Label _messageLabel = null!;
    private Button _leaveButton = null!;

    /// <param name="localPlayerId">The player whose perspective drives Victory/Defeat labeling.</param>
    public GameOverOverlay(SimBridge sim, int localPlayerId = 1)
    {
        _sim = sim;
        _localPlayerId = localPlayerId;
        // Subscribe before _Ready so we don't miss an event raised between construction and display.
        _sim.Events.PlayerDefeated += OnPlayerDefeated;
        _sim.Events.PlayerWon += OnPlayerWon;
    }

    public override void _Ready()
    {
        Layer = 50;   // above the HUD
        Visible = false;

        // 锚点居中(非 CenterContainer,理由见 ModalPanelBase):四锚 0.5 + 双向 Grow。
        _panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(480, 220),
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        var bg = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.05f, 0.04f, 0.94f),
            BorderColor = new Color(0.55f, 0.45f, 0.30f),
            BorderWidthBottom = 3,
            BorderWidthTop = 3,
            BorderWidthLeft = 3,
            BorderWidthRight = 3,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        bg.SetContentMarginAll(24);
        _panel.AddThemeStyleboxOverride("panel", bg);
        AddChild(_panel);

        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        vbox.AddThemeConstantOverride("separation", 16);
        _panel.AddChild(vbox);

        _titleLabel = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _titleLabel.AddThemeFontSizeOverride("font_size", 32);
        vbox.AddChild(_titleLabel);

        _messageLabel = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _messageLabel.AddThemeFontSizeOverride("font_size", 15);
        vbox.AddChild(_messageLabel);

        // 按钮行:查看统计 + 离开(石头贴图按钮,与顶栏/结算页同族皮肤)。
        var buttonRow = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        buttonRow.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(buttonRow);

        var statsButton = new Button
        {
            Text = "查看统计",
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(130, 36),
        };
        StoneButtonStyle.Apply(statsButton, StoneButtonStyle.FindBinariesDir());
        statsButton.Pressed += OnShowStats;
        buttonRow.AddChild(statsButton);

        _leaveButton = new Button
        {
            Text = "Leave",
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(130, 36),
        };
        StoneButtonStyle.Apply(_leaveButton, StoneButtonStyle.FindBinariesDir());
        _leaveButton.Pressed += OnLeavePressed;
        buttonRow.AddChild(_leaveButton);
    }

    private void OnShowStats()
    {
        // 收集结算数据并打开 SummaryPanel(全屏统计页)。
        var summary = MatchSummaryExporter.Collect(_sim);
        var panel = new SummaryPanel(summary, _localPlayerId);
        AddChild(panel);
        panel.Open();
    }

    private void OnPlayerDefeated(PlayerDefeatedEvent e)
    {
        // Only react to the local player's defeat for the overlay.
        if (e.PlayerId != _localPlayerId) return;
        ShowOverlay(
            title: "Defeat",
            titleColor: new Color(0.85f, 0.22f, 0.18f),
            message: e.Reason);
    }

    private void OnPlayerWon(PlayerWonEvent e)
    {
        // Only react to the local player's victory.
        if (e.PlayerId != _localPlayerId) return;
        ShowOverlay(
            title: "Victory!",
            titleColor: new Color(0.20f, 0.78f, 0.30f),
            message: "You are victorious.");
    }

    private void ShowOverlay(string title, Color titleColor, string message)
    {
        CallDeferred(nameof(Display), title, titleColor, message);
    }

    private void Display(string title, Color titleColor, string message)
    {
        _titleLabel.Text = title;
        _titleLabel.AddThemeColorOverride("font_color", titleColor);
        _messageLabel.Text = message;
        Visible = true;
    }

    private void OnLeavePressed()
    {
        // Return to the main menu by reloading the startup scene. GetTree().ChangeScene
        // is the standard Godot way; the project entry scene is the main menu.
        GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn");
    }

    public override void _ExitTree()
    {
        _sim.Events.PlayerDefeated -= OnPlayerDefeated;
        _sim.Events.PlayerWon -= OnPlayerWon;
        base._ExitTree();
    }
}
