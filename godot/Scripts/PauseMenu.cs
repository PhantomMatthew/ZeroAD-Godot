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
    private ConfirmationDialog? _resignConfirm;

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

        // 锚点居中(非 CenterContainer,理由见 ModalPanelBase):四锚 0.5 + 双向 Grow,
        // gui.scale>1 缩小逻辑画布时仍对称居中,不被钳到左上角。
        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(360, 0),
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
        panel.AddThemeStyleboxOverride("panel", bg);
        AddChild(panel);

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
        AddButton(vbox, "Quick Load", () => OnLoad?.Invoke());
        AddButton(vbox, "Load Game", OpenLoadGame);
        AddButton(vbox, "Manual", OpenManual);
        AddButton(vbox, "Options", OpenOptions);
        AddButton(vbox, "Resign", ShowResignConfirm);
        AddButton(vbox, "Leave", () => OnLeave?.Invoke());

        // Resign 确认框(对齐原版 Menu → ResignConfirmation):Confirmed → 本地玩家认输(走既有
        // PlayerDefeated 路径 → GameOverOverlay 显失败屏),取消则关框不动。SP 够用;MP 广播延后。
        _resignConfirm = new ConfirmationDialog
        {
            Title = "Resign",
            DialogText = "Are you sure you want to resign?\nYou will be defeated.",
            OkButtonText = "Resign",
        };
        _resignConfirm.Confirmed += () =>
        {
            _sim.ResignLocalPlayer();
            Close();
        };
        AddChild(_resignConfirm);
    }

    private void ShowResignConfirm() => _resignConfirm?.PopupCentered();

    // 子项:打开手册页(Layer 65 浮在本菜单 60 之上,关闭后落回暂停菜单)。不关 PauseMenu,
    // 故 sim 仍暂停——手册只是暂停菜单的一个只读子视图。
    private void OpenManual()
    {
        var manual = new ManualPanel(layer: 65);
        AddChild(manual);
        manual.Open();
    }

    // 子项:打开存档浏览器(Layer 65 浮在本菜单 60 之上)。Load → ChangeScene 冷加载,
    // 区别于 Quick Load(同场景快读)。
    private void OpenLoadGame()
    {
        var panel = new LoadGamePanel(layer: 65);
        AddChild(panel);
        panel.Open();
    }

    // 子项:打开 Options(Layer 65 浮在本菜单 60 之上;inGame:true → adaptivefps 取 session 值,
    // 图形项可作用于本会话场景的 light/env)。
    private void OpenOptions()
    {
        var panel = new OptionsPanel(layer: 65, inGame: true);
        AddChild(panel);
        panel.Open();
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
