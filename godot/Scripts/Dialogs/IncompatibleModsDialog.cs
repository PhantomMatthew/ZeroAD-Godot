using System.IO;
using global::Godot;

namespace ZeroAD.Godot.Dialogs;

// IncompatibleModsDialog — 原版 gui/mod/gui/incompatible_mods 的端口:
// 纯信息页——读 gui/incompatible_mods/incompatible_mods.txt 展示,一个 Close 按钮。
// 当启用了互不兼容的 mod 时由 ModmodPanel 自动弹出(原版 modmod.js init 同款)。
public sealed partial class IncompatibleModsDialog : ModalPanelBase
{
    public static IncompatibleModsDialog Show(Node parent)
    {
        var dlg = new IncompatibleModsDialog();
        parent.AddChild(dlg);
        dlg.Open();
        return dlg;
    }

    public override void _Ready()
    {
        var (content, _) = BuildShell(Localization.Tr("Incompatible Mods"), 560);

        var text = new RichTextLabel
        {
            CustomMinimumSize = new Vector2(500, 260),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            BbcodeEnabled = false,
        };
        text.AddThemeFontSizeOverride("normal_font_size", 13);
        text.Text = LoadText();
        content.AddChild(text);

        var buttons = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        content.AddChild(buttons);
        AddButton(buttons, "Close", () => { Close(); QueueFree(); });
    }

    private static string LoadText()
    {
        string? binDir = StoneButtonStyle.FindBinariesDir();
        if (binDir == null) return "(incompatible mods notice unavailable)";
        string path = Path.Combine(binDir, "data", "mods", "mod", "gui",
            "incompatible_mods", "incompatible_mods.txt");
        return File.Exists(path) ? File.ReadAllText(path) : "(incompatible mods notice unavailable)";
    }
}
