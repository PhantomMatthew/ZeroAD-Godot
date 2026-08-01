using System;
using System.IO;
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
        // 原版 page_pregame 背景:启动随机一套多层视差图(gui/pregame/backgrounds 端口,
        // 见 PregameBackground);binaries 缺失时回退渐变底。
        string? binDir = FindBinariesDir();
        var parallax = new PregameBackground();
        if (parallax.Init(binDir))
        {
            AddChild(parallax);
        }
        else
        {
            AddChild(new TextureRect
            {
                Texture = MakeBackgroundGradient(),
                AnchorsPreset = (int)LayoutPreset.FullRect,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            });
        }

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

        // 原版 productLogo(ProjectInformation.xml:面板内 50%±110, y 10..110,
        // sprite 0ADLogo = pregame/shell/logo/0ad_logo.png)。缺失时回退文字标题。
        string logoPath = binDir == null ? "" : Path.Combine(binDir,
            "data", "mods", "public", "art", "textures", "ui", "pregame", "shell", "logo", "0ad_logo.png");
        var logoImg = binDir == null ? null : Image.LoadFromFile(logoPath);
        if (logoImg != null)
        {
            panel.AddChild(new TextureRect
            {
                Texture = ImageTexture.CreateFromImage(logoImg),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0f, AnchorBottom = 0f,
                OffsetLeft = -110, OffsetRight = 110, OffsetTop = 10, OffsetBottom = 110,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        }
        else
        {
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
        }

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

        // 原版 ProjectInformation 底部信息框(面板内 8 100%-368 100%-8 100%-94,
        // TranslucentPanelThinBorder + 白色 sans-14 描述)。community 按钮留 backlog。
        var infoBox = new PanelContainer
        {
            AnchorLeft = 0f, AnchorRight = 1f, AnchorTop = 1f, AnchorBottom = 1f,
            OffsetLeft = 8, OffsetRight = -8, OffsetTop = -368, OffsetBottom = -94,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var infoBg = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.45f),
            BorderColor = new Color(1f, 1f, 1f, 0.25f),
            BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
        };
        infoBg.SetContentMarginAll(8);
        infoBox.AddThemeStyleboxOverride("panel", infoBg);
        panel.AddChild(infoBox);

        var infoLbl = new Label
        {
            Text = "0 A.D. Godot Rewrite\n\nNotice: This game is under development and many features have not been added yet.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        infoLbl.AddThemeFontSizeOverride("font_size", 14);
        infoLbl.AddThemeColorOverride("font_color", Colors.White);
        infoBox.AddChild(infoLbl);
    }

    /// <summary>binaries/ 目录定位(与 LoadingOverlay.FindBinariesDir 同款 ../、../../ 回退)。</summary>
    private static string? FindBinariesDir()
    {
        string projRoot = ProjectSettings.GlobalizePath("res://");
        foreach (var candidate in new[]
        {
            Path.GetFullPath(Path.Combine(projRoot, "..", "binaries")),
            Path.GetFullPath(Path.Combine(projRoot, "..", "..", "binaries")),
        })
        {
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
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
