using Godot;

namespace ZeroAD.Godot;

public static class UITheme
{
    private static Theme? _theme;

    public static Theme GetTheme()
    {
        if (_theme != null) return _theme;
        _theme = new Theme();

        var btnNormal = MakeButtonStyle("res://assets/ui/center.png", new Color(1, 1, 1));
        var btnHover = MakeButtonStyle("res://assets/ui/center.png", new Color(1.2f, 1.15f, 1.0f));
        var btnPressed = MakeButtonStyle("res://assets/ui/center.png", new Color(0.8f, 0.75f, 0.65f));

        _theme.SetStylebox("normal", "Button", btnNormal);
        _theme.SetStylebox("hover", "Button", btnHover);
        _theme.SetStylebox("pressed", "Button", btnPressed);
        _theme.SetColor("font_color", "Button", new Color(1f, 0.95f, 0.8f));
        _theme.SetColor("font_hover_color", "Button", Colors.White);
        _theme.SetFontSize("font_size", "Button", 18);

        var panelStyle = MakePanelStyle("res://assets/ui/panel_bg.png");
        _theme.SetStylebox("panel", "Panel", panelStyle);

        _theme.SetColor("font_color", "Label", new Color(1f, 0.95f, 0.82f));
        _theme.SetFontSize("font_size", "Label", 16);

        var editStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.10f, 0.08f, 0.9f),
            BorderColor = new Color(0.55f, 0.45f, 0.30f),
        };
        editStyle.SetBorderWidthAll(1);
        editStyle.SetContentMarginAll(6);
        _theme.SetStylebox("normal", "LineEdit", editStyle);
        _theme.SetColor("font_color", "LineEdit", new Color(1f, 0.95f, 0.8f));

        return _theme;
    }

    private static StyleBox MakeButtonStyle(string texPath, Color modulate)
    {
        var tex = TryLoad(texPath);
        if (tex == null)
        {
            var flat = new StyleBoxFlat { BgColor = new Color(0.35f, 0.25f, 0.15f) * modulate };
            flat.SetCornerRadiusAll(3);
            flat.SetContentMarginAll(8);
            return flat;
        }
        var style = new StyleBoxTexture { Texture = tex, ModulateColor = modulate };
        style.SetContentMarginAll(8);
        return style;
    }

    private static StyleBox MakePanelStyle(string texPath)
    {
        var tex = TryLoad(texPath);
        if (tex == null)
            return new StyleBoxFlat { BgColor = new Color(0.15f, 0.12f, 0.10f, 0.95f) };
        var style = new StyleBoxTexture { Texture = tex };
        style.SetContentMarginAll(16);
        return style;
    }

    public static Texture2D? TryLoad(string resPath)
    {
        string abs = ProjectSettings.GlobalizePath(resPath);
        if (!System.IO.File.Exists(abs)) return null;
        var img = Image.LoadFromFile(abs);
        return img != null ? ImageTexture.CreateFromImage(img) : null;
    }
}
