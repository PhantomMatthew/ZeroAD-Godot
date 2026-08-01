using System;
using Godot;

namespace ZeroAD.Godot;

// MainMenu — 会话外主页(独立场景,project main_scene)。原版是 gui/page_pregame.xml 的独立页面,
// 这里贴原版"独立场景"模型:本场景做主菜单前置页 → 点 SP/Tutorial/MP 设 GameLaunchConfig →
// ChangeScene 到 session 场景(Main.tscn)→ Main._Ready 读 GameLaunchConfig 决定启动方式。
//
// ZEROAD_AUTOSTART/TUTORIAL 环境变量降级为 dev fallback:仅本页 _Ready 首次读取并**读取后清空**
// (修历史 bug——进程级 env 会在 ChangeScene 回主菜单时重触发,误以为还要自动开局)。
public sealed partial class MainMenu : Control
{
    private GameLaunchConfig _cfg = null!;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        // dev 跳过主菜单:ZEROAD_TUTORIAL/AUTOSTART 读一次即清空,设 GameLaunchConfig 后转 session。
        if (TryConsumeAutostartEnv())
            return; // 已 CallDeferred 切场景,本帧不必构建菜单。

        _cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
        BuildUi();
    }

    private bool TryConsumeAutostartEnv()
    {
        string tut = OS.GetEnvironment("ZEROAD_TUTORIAL");
        string auto = OS.GetEnvironment("ZEROAD_AUTOSTART");
        if (string.IsNullOrEmpty(tut) && string.IsNullOrEmpty(auto))
            return false;

        // 清空:避免 Leave 回主菜单时 _Ready 再次读到,重触发自动开局。
        OS.SetEnvironment("ZEROAD_TUTORIAL", "");
        OS.SetEnvironment("ZEROAD_AUTOSTART", "");

        var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
        cfg.Reset();
        cfg.Mode = !string.IsNullOrEmpty(tut)
            ? GameLaunchConfig.LaunchMode.Tutorial
            : GameLaunchConfig.LaunchMode.SinglePlayer;
        cfg.Seed = 42;

        CallDeferred(nameof(GotoSession));
        return true;
    }

    private void BuildUi()
    {
        AddChild(new TextureRect
        {
            // 暂用渐变底(原版背景图留 backlog)。FullRect 铺满。
            Texture = MakeBackgroundGradient(),
            AnchorsPreset = (int)LayoutPreset.FullRect,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        });

        var center = new CenterContainer
        {
            AnchorsPreset = (int)LayoutPreset.FullRect,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(320, 0) };
        var bg = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.05f, 0.04f, 0.92f),
            BorderColor = new Color(0.55f, 0.45f, 0.30f),
            BorderWidthBottom = 3, BorderWidthTop = 3, BorderWidthLeft = 3, BorderWidthRight = 3,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        };
        bg.SetContentMarginAll(24);
        panel.AddThemeStyleboxOverride("panel", bg);
        center.AddChild(panel);

        var vbox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 10);
        panel.AddChild(vbox);

        var title = new Label
        {
            Text = "0 A.D.",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Theme = UITheme.GetTheme(),
        };
        title.AddThemeFontSizeOverride("font_size", 34);
        vbox.AddChild(title);

        AddButton(vbox, "Single Player", OnSinglePlayer);
        AddButton(vbox, "Tutorial", OnTutorial);
        AddButton(vbox, "Load Game", () => { }, disabled: true, tip: "(Phase 2)");
        AddButton(vbox, "Options", () => { }, disabled: true, tip: "(Phase 3)");
        AddButton(vbox, "Manual", OnManual);
        AddButton(vbox, "Multiplayer", OnMultiplayer);
        AddButton(vbox, "Quit", () => GetTree().Quit());
    }

    private void OnSinglePlayer() => Start(GameLaunchConfig.LaunchMode.SinglePlayer);
    private void OnTutorial() => Start(GameLaunchConfig.LaunchMode.Tutorial);
    private void OnMultiplayer() => Start(GameLaunchConfig.LaunchMode.Multiplayer);

    private void Start(GameLaunchConfig.LaunchMode mode)
    {
        _cfg.Reset();
        _cfg.Mode = mode;
        _cfg.Seed = 42;
        GotoSession();
    }

    private void GotoSession() => GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");

    private void OnManual()
    {
        var manual = new ManualPanel();
        AddChild(manual);
        manual.Open();
    }

    private static void AddButton(Control parent, string label, Action onPressed,
        bool disabled = false, string tip = "")
    {
        var btn = new Button
        {
            Text = label,
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(220, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            Disabled = disabled,
            TooltipText = tip,
        };
        btn.Pressed += onPressed;
        parent.AddChild(btn);
    }

    // 深色顶→更深底渐变,近似原版主菜单暗调。原版真实背景贴图留 backlog。
    private static GradientTexture2D MakeBackgroundGradient()
    {
        var grad = new Gradient();
        grad.SetColor(0, new Color(0.10f, 0.09f, 0.07f));
        grad.SetColor(1, new Color(0.03f, 0.03f, 0.02f));
        return new GradientTexture2D
        {
            Gradient = grad,
            FillTo = new Vector2(0, 1),
            Width = 2,
            Height = 256,
        };
    }
}
