using Godot;

namespace ZeroAD.Godot;

/// <summary>欢迎页(原版 gui/splashscreen/page_splashscreen.xml + splashscreen.js):
/// 首次运行(原版 gui.splashscreen.enable 配置)显示欢迎+新特性说明;
/// "Show this message in the future" 勾选框持久化。ModalPanelBase 外壳。</summary>
public sealed partial class SplashscreenPanel : ModalPanelBase
{
    private CheckBox _showNextTime = null!;

    public override void _Ready()
    {
        var (content, status) = BuildShell("Welcome!", 640);
        status.Text = "";

        // 原版 splashscreen.xml 的三段欢迎说明。
        var welcomeText = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        welcomeText.AddThemeFontSizeOverride("normal_font_size", 14);
        welcomeText.Text =
            "[center][font_size=16][b]Thank you for installing 0 A.D. Empires Ascendant![/b][/font_size][/center]\n\n" +
            "0 A.D. is still in active development. You may encounter bugs, and some features are not as fleshed out as we would like. However, the game is being improved constantly. Expect some content drops and balance changes each release as we're still working to make the game the best it can be! Check our website and forums for updates!\n\n" +
            "The game is fully playable. But at times it can have performance problems with large maps and a great number of units, especially on weaker hardware.\n\n" +
            "0 A.D. is Free Software: you can participate in its development. If you want to help with art, sound, gameplay or programming, make sure to join our official forum.";
        content.AddChild(welcomeText);

        // 下次显示勾选(原版 displaySplashScreen 复选框持久化)。
        var checkboxRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        checkboxRow.AddThemeConstantOverride("separation", 8);
        content.AddChild(checkboxRow);
        _showNextTime = new CheckBox
        {
            Text = "Show this message in the future",
            ButtonPressed = Options.OptionsApplier.GetBool("gui.splashscreen.enable", true),
        };
        checkboxRow.AddChild(_showNextTime);

        AddButton(content, "OK", Close, minWidth: 160);
    }

    protected override void OnOpen() { }

    /// <summary>关页时持久化勾选(原版:gui.splashscreen.enable 配置写入)。</summary>
    public new void Close()
    {
        SaveSetting(_showNextTime.ButtonPressed);
        base.Close();
    }

    private static void SaveSetting(bool enable)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var cfg = tree?.Root.GetNodeOrNull<UserConfig>("/root/UserConfig");
        cfg?.SetUserValue("gui.splashscreen.enable", enable ? "true" : "false");
        cfg?.Save();
    }

    /// <summary>首运判定(原版:gui.splashscreen.enable 配置缺省=true)。</summary>
    public static bool ShouldShowOnFirstRun()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var cfg = tree?.Root.GetNodeOrNull<UserConfig>("/root/UserConfig");
        return cfg == null || cfg.GetEffective("gui.splashscreen.enable") != "false";
    }
}
