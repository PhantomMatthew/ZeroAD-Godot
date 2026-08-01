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

        var center = new CenterContainer
        {
            AnchorsPreset = (int)Control.LayoutPreset.FullRect,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        AddChild(center);

        _panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(480, 220),
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
        center.AddChild(_panel);

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

        _leaveButton = new Button
        {
            Text = "Leave",
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(160, 36),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        _leaveButton.Pressed += OnLeavePressed;
        vbox.AddChild(_leaveButton);
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
