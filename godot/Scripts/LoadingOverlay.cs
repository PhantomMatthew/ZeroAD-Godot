using Godot;

namespace ZeroAD.Godot;

/// <summary>
/// Full-screen overlay shown while the scenario loads (terrain + entity spawn).
/// Mirrors the C++ loading screen: dark background, centered title, progress text.
/// Shown for one frame before the blocking BeginGameplay call so the user sees
/// "Loading..." instead of a frozen lobby for several seconds.
/// </summary>
public sealed partial class LoadingOverlay : CanvasLayer
{
    public LoadingOverlay(string title)
    {
        Layer = 100; // above everything

        var bg = new ColorRect { Color = new Color(0.04f, 0.04f, 0.06f, 0.92f) };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        bg.MouseFilter = Control.MouseFilterEnum.Stop;
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 16);
        center.AddChild(vbox);

        var label = new Label
        {
            Text = title,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.AddThemeFontSizeOverride("font_size", 22);
        label.AddThemeColorOverride("font_color", new Color(1f, 0.89f, 0.58f));
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 4);
        vbox.AddChild(label);

        var sub = new Label
        {
            Text = "Please wait...",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        sub.AddThemeFontSizeOverride("font_size", 14);
        sub.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        vbox.AddChild(sub);
    }
}
