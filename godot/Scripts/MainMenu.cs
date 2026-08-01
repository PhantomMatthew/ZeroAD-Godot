using System;
using Godot;
using ZeroAD.Godot.Options;

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
        // 已存设置全量重放(音量/全屏/垂直同步/GUI 缩放等即时生效项;场景相关项此处无 light/env
        // → no-op,进 session 后由 Main 再重放)。菜单上下文 inGame:false(adaptivefps 取 menu 值)。
        OptionsApplier.ApplyAll(GetNode<UserConfig>("/root/UserConfig"), GetTree(), inGame: false);
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
            // 暂用渐变底(原版背景图轮播留 backlog)。FullRect 铺满。
            Texture = MakeBackgroundGradient(),
            AnchorsPreset = (int)LayoutPreset.FullRect,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        });

        // 对齐原版 pregame/menupanel.xml:主菜单是**左侧竖条面板**(size 60 -2 300 100%+2,
        // 宽 240 通高、上下各溢出 2px),非居中对话框。锚点布局,gui.scale 任意值位置不变。
        var panel = new Panel
        {
            AnchorLeft = 0f, AnchorRight = 0f, AnchorTop = 0f, AnchorBottom = 1f,
            OffsetLeft = 60, OffsetRight = 300, OffsetTop = -2, OffsetBottom = 2,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        var bg = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.09f, 0.07f, 1.0f),
            // 原版 MainMenuPanelRightBorder:右缘 2px 金色描边(230 190 80)。
            BorderColor = new Color(0.90f, 0.75f, 0.31f),
            BorderWidthRight = 2,
        };
        panel.AddThemeStyleboxOverride("panel", bg);
        AddChild(panel);

        // 原版 productLogo 区(面板内 50%±110, y 10..110)——无贴图资源,用大号标题文字占位。
        var title = new Label
        {
            Text = "0 A.D.",
            HorizontalAlignment = HorizontalAlignment.Center,
            Theme = UITheme.GetTheme(),
            AnchorLeft = 0f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 0f,
            OffsetTop = 40, OffsetBottom = 110,
        };
        title.AddThemeFontSizeOverride("font_size", 34);
        panel.AddChild(title);

        // 原版 mainMenuButtons(面板内 8 146 100%-8 346):按钮列起始于 y=146,左右留 8px。
        var vbox = new VBoxContainer
        {
            AnchorLeft = 0f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 0f,
            OffsetLeft = 8, OffsetRight = -8, OffsetTop = 146,
        };
        vbox.AddThemeConstantOverride("separation", 8);
        panel.AddChild(vbox);

        AddButton(vbox, "Single Player", OnSinglePlayer);
        AddButton(vbox, "Tutorial", OnTutorial);
        AddButton(vbox, "Load Game", OnLoadGame);
        AddButton(vbox, "Options", OnOptions);
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

    private void OnLoadGame()
    {
        var panel = new LoadGamePanel();
        AddChild(panel);
        panel.Open();
    }

    private void OnOptions()
    {
        var panel = new OptionsPanel();
        AddChild(panel);
        panel.Open();
    }

    private static void AddButton(Control parent, string label, Action onPressed,
        bool disabled = false, string tip = "")
    {
        var btn = new Button
        {
            Text = label,
            Theme = UITheme.GetTheme(),
            CustomMinimumSize = new Vector2(0, 32),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
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
