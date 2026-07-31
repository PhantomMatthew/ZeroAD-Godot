using System;
using Godot;
using ZeroAD.Sim;

namespace ZeroAD.Godot;

// PauseMenu — modal overlay opened from the top-bar Menu button.
//
// Freezes the sim via a presentation-layer flag (SimBridge.Paused), NOT Godot's tree pause:
// the sim stops ticking but UI/camera stay alive (the player can still pan the view). SP-focused
// — in MP the overlay still opens, but AdvanceTurn is driven by the lockstep barrier, so it does
// not truly pause (a real MP pause needs lockstep negotiation; out of scope).
//
// Mirrors GameOverOverlay's CanvasLayer + CenterContainer + PanelContainer style. Save/Load are
// delegated to Main (which owns the QuickSave/QuickLoad + visual-rebuild logic) via events, the
// same decoupling LobbyUI uses.

public sealed partial class PauseMenu : CanvasLayer
{
    private readonly SimBridge _sim;
    private Label _statusLabel = null!;

    public event Action? OnSave;
    public event Action? OnLoad;
    public event Action? OnLeave;

    public PauseMenu(SimBridge sim)
    {
        _sim = sim;
        // Insurance: stay interactive even if the tree were ever paused (we don't rely on tree
        // pause — SimBridge.Paused gates _Process — but this keeps the buttons clickable regardless).
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Ready()
    {
        Layer = 60;            // above the HUD and GameOverOverlay (Layer 50)
        Visible = false;

        // Full-screen dim that eats all clicks → modal. MouseFilter.Stop blocks input passthrough
        // so game clicks/hotkeys behind the overlay never land (Main._UnhandledInput is also gated
        // on _sim.Paused as a second line of defense).
        var dim = new ColorRect
        {
            Color = new Color(0, 0, 0, 0.55f),
            AnchorsPreset = (int)Control.LayoutPreset.FullRect,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        AddChild(dim);

        var center = new CenterContainer
        {
            AnchorsPreset = (int)Control.LayoutPreset.FullRect,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        AddChild(center);

        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(360, 0),
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
        panel.AddThemeStyleboxOverride("panel", bg);
        center.AddChild(panel);

        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        vbox.AddThemeConstantOverride("separation", 12);
        panel.AddChild(vbox);

        var title = new Label
        {
            Text = "Menu",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        title.AddThemeFontSizeOverride("font_size", 26);
        vbox.AddChild(title);

        _statusLabel = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 14);
        vbox.AddChild(_statusLabel);

        AddButton(vbox, "Resume", Close);
        AddButton(vbox, "Save", () => OnSave?.Invoke());
        AddButton(vbox, "Load", () => OnLoad?.Invoke());
        AddButton(vbox, "Leave", () => OnLeave?.Invoke());
    }

    private static void AddButton(Control parent, string label, Action onPressed)
    {
        var btn = new Button
        {
            Text = label,
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(200, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        btn.Pressed += onPressed;
        parent.AddChild(btn);
    }

    public void Open()
    {
        SetStatus("");
        Visible = true;
        _sim.Paused = true;
    }

    public void Close()
    {
        Visible = false;
        _sim.Paused = false;
    }

    /// <summary>Status line feedback ("Saved." / "Loaded turn 42." / "No save file.").</summary>
    public void SetStatus(string text) => _statusLabel.Text = text;
}
