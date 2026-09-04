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
    private readonly MultiplayerController? _mp;
    private Label _statusLabel = null!;
    private VBoxContainer _lagBox = null!;
    private string _lagSignature = "";
    private ConfirmationDialog? _resignConfirm;

    public event Action? OnSave;
    public event Action? OnLoad;
    public event Action? OnLeave;

    public PauseMenu(SimBridge sim, MultiplayerController? mp = null)
    {
        _sim = sim;
        _mp = mp;
        // Insurance: stay interactive even if the tree were ever paused (we don't rely on tree
        // pause — SimBridge.Paused gates _Process — but this keeps the buttons clickable regardless).
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Ready()
    {
        Layer = 60;            // above the HUD and GameOverOverlay (Layer 50)
        Visible = false;

        // 原版 session/Menu.xml:菜单面板贴右缘、顶端对齐(menuButtonPanel
        // size="100%-164 0 100% 0"),无标题、无全屏压暗——此前我们是居中模态+压暗,
        // 位置与 C++ 版明显不一致。透明全屏捕捉层负责"点外面关闭"。
        var catcher = new Control
        {
            AnchorsPreset = (int)Control.LayoutPreset.FullRect,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        catcher.GuiInput += (ev) =>
        {
            if (ev is InputEventMouseButton { Pressed: true }) Close();
        };
        AddChild(catcher);

        var panel = new PanelContainer
        {
            AnchorLeft = 1f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 0f,
            OffsetLeft = -164, OffsetTop = 0, OffsetRight = 0,
            CustomMinimumSize = new Vector2(164, 0),
            GrowVertical = Control.GrowDirection.End,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        // 原版 StonePanelThinBorder:深色底 + 细金边。
        var bg = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.05f, 0.04f, 0.97f),
            BorderColor = new Color(0.90f, 0.75f, 0.31f),
        };
        bg.SetBorderWidthAll(2);
        bg.SetContentMarginAll(4);
        panel.AddThemeStyleboxOverride("panel", bg);
        AddChild(panel);

        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        vbox.AddThemeConstantOverride("separation", 0);
        panel.AddChild(vbox);

        _statusLabel = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 12);
        vbox.AddChild(_statusLabel);

        AddButton(vbox, "Resume", Close);
        AddButton(vbox, "Save", () => OnSave?.Invoke());
        AddButton(vbox, "Quick Load", () => OnLoad?.Invoke());
        AddButton(vbox, "Load Game", OpenLoadGame);
        AddButton(vbox, "Manual", OpenManual);
        AddButton(vbox, "Options", OpenOptions);
        AddButton(vbox, "Hotkeys", OpenHotkeys);
        AddButton(vbox, "Resign", ShowResignConfirm);
        AddButton(vbox, "Leave", () => OnLeave?.Invoke());

        // MP 超时警示名单(原版 NETWORK_WARNING_TIMEOUT 的 GUI 呈现:ClientTimeout 警告 +
        // host 手动踢出)。名单由 MultiplayerController 每 0.5s 刷新;host 见逐行 + Kick 钮,
        // client 只见 host 失联提示(踢出是 host 特权)。踢人后走既有掉线→AI 接管路径。
        _lagBox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Visible = false,
        };
        vbox.AddChild(_lagBox);
        if (_mp != null) _mp.OnLaggingChanged += RebuildLagRows;

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

    private void OpenHotkeys()
    {
        var panel = new HotkeysPanel(layer: 65);
        AddChild(panel);
        panel.Open();
    }

    private static void AddButton(Control parent, string label, Action onPressed)
    {
        var btn = new Button
        {
            Text = Localization.Tr(label),
            Theme = UITheme.GetTheme(),
            // 原版:按钮满宽(4 4 100%-4 32,StoneButtonFancy)。
            CustomMinimumSize = new Vector2(0, 32),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
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

    public override void _ExitTree()
    {
        if (_mp != null) _mp.OnLaggingChanged -= RebuildLagRows;
    }

    // 名单签名相同则跳过重建(事件每 0.5s 来一次,大多数 tick 内容不变)。
    private void RebuildLagRows()
    {
        var mp = _mp;
        if (mp == null) return;
        long now = (long)Time.GetTicksMsec();
        var sig = string.Join("|", mp.LaggingPeers.ConvertAll(e => $"{e.Peer}:{e.PlayerId}:{(now - e.LastMs) / 1000}"));
        if (sig == _lagSignature) return;
        _lagSignature = sig;

        foreach (var child in _lagBox.GetChildren()) child.QueueFree();
        if (mp.LaggingPeers.Count == 0)
        {
            _lagBox.Visible = false;
            return;
        }
        _lagBox.Visible = true;
        foreach (var (peer, playerId, lastMs) in mp.LaggingPeers)
        {
            long secs = (now - lastMs) / 1000;
            if (mp.IsHost)
            {
                var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                var label = new Label
                {
                    Text = Localization.Tr($"Player {playerId} lagging ({secs}s)"),
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
                label.AddThemeFontSizeOverride("font_size", 12);
                row.AddChild(label);
                long p = peer;
                var kick = new Button
                {
                    Text = Localization.Tr("Kick"),
                    Theme = UITheme.GetTheme(),
                    CustomMinimumSize = new Vector2(48, 24),
                };
                kick.Pressed += () => mp.KickPeer(p);
                row.AddChild(kick);
                _lagBox.AddChild(row);
            }
            else
            {
                var label = new Label
                {
                    Text = Localization.Tr($"Host not responding ({secs}s)"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
                label.AddThemeFontSizeOverride("font_size", 12);
                _lagBox.AddChild(label);
            }
        }
    }

    /// <summary>Status line feedback ("Saved." / "Loaded turn 42." / "No save file.").</summary>
    public void SetStatus(string text) => _statusLabel.Text = text;
}
