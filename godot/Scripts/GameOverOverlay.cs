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
        // 战役败局也走 endgame 流程(原版 CampaignSession.onFinish 胜/负均跑
        // endgame 页:won=false 只收集自定义结算数据,不 markLevelComplete)。
        RunCampaignEndgame(won: false);
        ShowOverlay(
            title: "Defeat",
            titleColor: new Color(0.85f, 0.22f, 0.18f),
            message: e.Reason);
    }

    private void OnPlayerWon(PlayerWonEvent e)
    {
        // Only react to the local player's victory.
        if (e.PlayerId != _localPlayerId) return;
        if (RunCampaignEndgame(won: true)) return;   // 全战役通关 → 通关页替代胜利遮罩
        ShowOverlay(
            title: "Victory!",
            titleColor: new Color(0.20f, 0.78f, 0.30f),
            message: "You are victorious.");
    }

    /// <summary>战役 endgame 流程(原版 campaigns/default_menu/endgame/endgame.js
    /// 瞬态页):胜 → markLevelComplete;胜/负均收集地图脚本自定义结算数据
    /// (原版 Trigger.prototype.OnCampaignGameEnd 经 GuiInterfaceCall)并入
    /// run.data 落盘。返回 true = 战役全关卡完成且已改示通关页(调用方不再示
    /// 普通胜负遮罩)。</summary>
    private bool RunCampaignEndgame(bool won)
    {
        var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
        if (cfg.CampaignRunFile.Length == 0 || cfg.CampaignLevelId.Length == 0) return false;
        string? binDir = StoneButtonStyle.FindBinariesDir();
        string? dataRoot = binDir == null ? null
            : System.IO.Path.Combine(binDir, "data", "mods", "public");
        var run = Campaigns.CampaignRun.Load(dataRoot, cfg.CampaignRunFile);
        if (run is not { Broken: false }) return false;

        if (won)
        {
            run.MarkLevelComplete(cfg.CampaignLevelId);
            ZeroAD.Sim.Diag.Log("Campaign",
                $"run '{cfg.CampaignRunFile}': level '{cfg.CampaignLevelId}' completed");
        }
        // 自定义结算数据(原版 endGameData.custom):地图脚本键值对并入 run.data。
        foreach (var kv in _sim.GetCampaignGameEndData())
            run.ExtraData[kv.Key] = System.Text.Json.Nodes.JsonValue.Create(kv.Value);
        run.Save();

        // endgame 页(原版 campaigns/default_menu/endgame/ 的等价):
        // 全部关卡完成 → 通关页替代普通胜利遮罩。
        var template = run.Template;
        bool runComplete = won && template != null && template.Levels.Count > 0
            && System.Linq.Enumerable.All(template.Levels.Keys,
                id => run.CompletedLevels.Contains(id));
        if (runComplete)
            ShowEndgamePanel(run);
        return runComplete;
    }

    /// <summary>通关页(原版 endgame 瞬态页;回战役菜单 = 离开本局后由主菜单的
    /// Continue Campaign 路径打开 run 菜单)。</summary>
    private void ShowEndgamePanel(Campaigns.CampaignRun run)
    {
        var panel = new CampaignEndgamePanel(run, () =>
        {
            // 与 Leave 按钮同路:离开本局回主菜单(战役菜单由主菜单继续入口打开)。
            OnLeavePressed();
        });
        AddChild(panel);
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
        // 战役局离开 → 回战役菜单(原版 endGame 的 nextPage=run.getMenuPath();
        // summary skipSummary 分支同理)。非战役局回主菜单主页。
        var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
        cfg.ReturnToCampaignMenu = cfg.CampaignRunFile.Length > 0;
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
