using System.Collections.Generic;
using System.IO;
using Godot;

namespace ZeroAD.Godot;

/// <summary>用户报告(原版 gui/pregame/userreport/page_userreport.xml +
/// userreport.js):用户报告服务条款 + 启用/禁用开关。
/// 条款文本读 gui/userreport/Terms_and_Conditions.txt;启用状态写
/// userreport.terms 配置持久化(原版 loadTermsAcceptance/setUserReportEnabled)。
/// 骨架版:条款展示 + 接受/拒绝开关(上传功能未移植——XMPP/HTTP 上报通道
/// 属网络层,不在这)。</summary>
public sealed partial class UserReportPanel : ModalPanelBase
{
    private CheckBox _acceptBox = null!;
    private Label _statusLabel = null!;

    public override void _Ready()
    {
        var (content, status) = BuildShell("User Report", 600);
        status.Text = "";

        var title = new Label
        {
            Text = "UserReporter Terms and Conditions",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 16);
        content.AddChild(title);

        // 条款文本(原版 Terms_and_Conditions.txt 读入;富文本滚动)。
        var termsText = new RichTextLabel
        {
            BbcodeEnabled = false,
            FitContent = true,
            ScrollActive = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 240),
        };
        termsText.AddThemeFontSizeOverride("normal_font_size", 12);
        termsText.Text = LoadTerms();
        content.AddChild(termsText);

        _acceptBox = new CheckBox
        {
            Text = "I accept the terms and conditions",
            ButtonPressed = Options.OptionsApplier.GetBool("userreport.terms", false),
        };
        content.AddChild(_acceptBox);

        _statusLabel = new Label
        {
            Text = Options.OptionsApplier.GetBool("userreport.terms", false)
                ? "User reporting enabled." : "User reporting disabled.",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        content.AddChild(_statusLabel);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 12);
        content.AddChild(buttons);
        AddButton(buttons, "Close", CloseRequested, minWidth: 160);
        AddButton(buttons, "Apply", Apply, minWidth: 160);
    }

    private void Apply()
    {
        // 原版:setUserReportEnabled 写配置持久化(userreport.terms)。
        var tree = (SceneTree)Engine.GetMainLoop();
        var cfg = tree?.Root.GetNodeOrNull<UserConfig>("/root/UserConfig");
        cfg?.SetUserValue("userreport.terms", _acceptBox.ButtonPressed ? "true" : "false");
        cfg?.Save();
        _statusLabel.Text = _acceptBox.ButtonPressed
            ? "User reporting enabled." : "User reporting disabled.";
    }

    private void CloseRequested() => Close();

    /// <summary>条款文本(gui/userreport/Terms_and_Conditions.txt;缺失回退占位)。</summary>
    private static string LoadTerms()
    {
        string? path = RuntimePaths.FindPublicPath("gui", "userreport",
            "Terms_and_Conditions.txt");
        if (path != null)
            return File.ReadAllText(path);
        return "By enabling user reporting, you allow 0 A.D. to send crash reports "
            + "and system information to help improve the game. "
            + "No personal data is collected.";
    }
}
