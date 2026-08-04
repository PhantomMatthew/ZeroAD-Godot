using Godot;

namespace ZeroAD.Godot;

public static class UITheme
{
    private static Theme? _theme;

    public static Theme GetTheme()
    {
        if (_theme != null) return _theme;
        _theme = new Theme();

        // 原版 StoneButton(gui/common/sprites.xml)= global/button/button_stone_unselected.png
        // 整幅拉伸(size="0 0 100% 100%")——即 btn_normal.png(256×32 石条)。hover/pressed 用
        // button_stone_selected(btn_hover.png)。之前错用 button_brown 的 center 散件,与 C++ 版
        // 明显不一致(gamesetup/对话框按钮都应是深色石条)。
        var btnNormal = MakeButtonStyle("res://assets/ui/btn_normal.png", new Color(1, 1, 1));
        var btnHover = MakeButtonStyle("res://assets/ui/btn_hover.png", new Color(1, 1, 1));
        var btnPressed = MakeButtonStyle("res://assets/ui/btn_hover.png", new Color(0.85f, 0.8f, 0.7f));
        var btnDisabled = MakeButtonStyle("res://assets/ui/btn_normal.png", new Color(0.5f, 0.5f, 0.5f, 0.6f));

        _theme.SetStylebox("normal", "Button", btnNormal);
        _theme.SetStylebox("hover", "Button", btnHover);
        _theme.SetStylebox("pressed", "Button", btnPressed);
        _theme.SetStylebox("disabled", "Button", btnDisabled);
        _theme.SetColor("font_color", "Button", new Color(1f, 0.95f, 0.8f));
        _theme.SetColor("font_hover_color", "Button", Colors.White);
        _theme.SetColor("font_disabled_color", "Button", new Color(0.8f, 0.8f, 0.8f, 0.6f));
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

        // 下拉(OptionButton)/数值框(SpinBox 内嵌 LineEdit 已随上行)在大厅槽位表里裸露——
        // 原版 ModernDropDown 是半透黑盒 + 金线(见 MakeModernDarkBox),套同款避免 Godot 默认灰蓝。
        _theme.SetStylebox("normal", "OptionButton", MakeModernDarkBox());
        _theme.SetStylebox("hover", "OptionButton", MakeModernDarkBox());
        _theme.SetStylebox("pressed", "OptionButton", MakeModernDarkBox());
        _theme.SetStylebox("disabled", "OptionButton", MakeModernDarkBox());
        _theme.SetColor("font_color", "OptionButton", Colors.White);
        _theme.SetColor("font_hover_color", "OptionButton", Colors.White);
        _theme.SetColor("font_disabled_color", "OptionButton", new Color(0.7f, 0.7f, 0.7f, 0.5f));
        _theme.SetFontSize("font_size", "OptionButton", 14);

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

    /// <summary>ModernInput/ModernDropDown 的底盒(mods/mod modern/sprites.xml):
    /// ModernDarkBox 半透明黑(12 12 12 100)+ ModernDarkBoxGoldBorder 上下 1px 金线。
    /// 输入框/下拉/映射框共用。</summary>
    public static StyleBoxFlat MakeModernDarkBox()
    {
        var box = new StyleBoxFlat
        {
            BgColor = new Color(12f / 255f, 12f / 255f, 12f / 255f, 100f / 255f),
            BorderColor = new Color(0.90f, 0.75f, 0.31f),
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
        };
        box.SetContentMarginAll(4);
        return box;
    }

    /// <summary>把控件装扮成 ModernInput(LineEdit)或 ModernDropDown(OptionButton)/映射框(Button)。</summary>
    public static void ApplyModernInput(Control c)
    {
        switch (c)
        {
            case LineEdit le:
                le.AddThemeStyleboxOverride("normal", MakeModernDarkBox());
                le.AddThemeStyleboxOverride("focus", MakeModernDarkBox());
                le.AddThemeStyleboxOverride("read_only", MakeModernDarkBox());
                le.AddThemeColorOverride("font_color", Colors.White);
                le.AddThemeColorOverride("font_placeholder_color", Colors.Gray);
                le.AddThemeFontSizeOverride("font_size", 14);
                break;
            case OptionButton ob:
                ob.AddThemeStyleboxOverride("normal", MakeModernDarkBox());
                ob.AddThemeStyleboxOverride("hover", MakeModernDarkBox());
                ob.AddThemeStyleboxOverride("pressed", MakeModernDarkBox());
                ob.AddThemeStyleboxOverride("focus", MakeModernDarkBox());
                ob.AddThemeColorOverride("font_color", Colors.White);
                ob.AddThemeColorOverride("font_hover_color", Colors.White);
                ob.AddThemeFontSizeOverride("font_size", 14);
                ob.Alignment = HorizontalAlignment.Left;
                break;
            case Button b:
                b.AddThemeStyleboxOverride("normal", MakeModernDarkBox());
                b.AddThemeStyleboxOverride("hover", MakeModernDarkBox());
                b.AddThemeStyleboxOverride("pressed", MakeModernDarkBox());
                b.AddThemeStyleboxOverride("focus", MakeModernDarkBox());
                b.AddThemeColorOverride("font_color", Colors.White);
                b.AddThemeColorOverride("font_hover_color", Colors.White);
                b.AddThemeFontSizeOverride("font_size", 14);
                b.Alignment = HorizontalAlignment.Left;
                break;
        }
    }

    public static Texture2D? TryLoad(string resPath)
    {
        string abs = ProjectSettings.GlobalizePath(resPath);
        if (!System.IO.File.Exists(abs)) return null;
        var img = Image.LoadFromFile(abs);
        return img != null ? ImageTexture.CreateFromImage(img) : null;
    }
}
