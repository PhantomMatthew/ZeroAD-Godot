using Godot;

namespace ZeroAD.Godot;

public static class UITheme
{
    private static Theme? _theme;

    /// <summary>复选框图标（默认主题的 checked 贴图在高分屏被拉成条——自绘 16px
    /// 金边暗盒 + 金勾，观感对齐原版 Modern checkbox）。</summary>
    private static Texture2D? _checkboxChecked, _checkboxUnchecked;

    public static Texture2D CheckboxChecked => (_checkboxChecked ??= BuildCheckbox(true));
    public static Texture2D CheckboxUnchecked => (_checkboxUnchecked ??= BuildCheckbox(false));

    /// <summary>给 CheckBox 套自绘图标（checked/unchecked/radio/disabled 全覆），
    /// 并清空 Button 系 stylebox——否则勾选态按 Button.pressed 渲染成整条石纹按钮。</summary>
    public static void ApplyCheckboxIcons(CheckBox box)
    {
        box.AddThemeIconOverride("checked", CheckboxChecked);
        box.AddThemeIconOverride("unchecked", CheckboxUnchecked);
        box.AddThemeIconOverride("radio_checked", CheckboxChecked);
        box.AddThemeIconOverride("radio_unchecked", CheckboxUnchecked);
        box.AddThemeIconOverride("checked_disabled", CheckboxChecked);
        box.AddThemeIconOverride("unchecked_disabled", CheckboxUnchecked);
        var empty = new StyleBoxEmpty();
        box.AddThemeStyleboxOverride("normal", empty);
        box.AddThemeStyleboxOverride("pressed", empty);
        box.AddThemeStyleboxOverride("hover", empty);
        box.AddThemeStyleboxOverride("disabled", empty);
        box.AddThemeStyleboxOverride("focus", empty);
    }

    private static Texture2D BuildCheckbox(bool check)
    {
        const int s = 16;
        var img = Image.CreateEmpty(s, s, false, Image.Format.Rgba8);
        var bg = new Color(0.10f, 0.09f, 0.07f);
        var border = new Color(0.72f, 0.60f, 0.35f);
        var gold = new Color(0.90f, 0.78f, 0.45f);
        img.Fill(bg);
        for (int i = 0; i < s; i++)
        {
            img.SetPixel(i, 0, border); img.SetPixel(i, s - 1, border);
            img.SetPixel(0, i, border); img.SetPixel(s - 1, i, border);
        }
        if (check)
            // 金勾:短边 (3,8)→(6,11),长边 (6,11)→(12,4),2px 粗
            for (int t = -1; t <= 1; t++)
            {
                for (int k = 0; k <= 3; k++)
                    img.SetPixel(3 + k, 8 + k + t, gold);
                for (int k = 0; k <= 7; k++)
                    img.SetPixel(6 + k, 11 - k + t, gold);
            }
        return ImageTexture.CreateFromImage(img);
    }

    public static Theme GetTheme()
    {
        if (_theme != null) return _theme;
        _theme = new Theme();

        // 原版 StoneButton(gui/common/sprites.xml)= global/button/button_stone_unselected.png
        // 石纹按钮——直接走 StoneButtonStyle 的合成样式盒(junction 读上游贴图,
        // 与顶栏/结算页同一实现)。本地 btn_normal.png 是深色占位条(全图均色≈(50,48,43)),
        // 整幅拉伸就是"菜单按钮黑糊一片"的来源——仅作素材缺失时的回退。
        StyleBox btnNormal, btnHover, btnPressed, btnDisabled;
        if (StoneButtonStyle.TryGetStyleboxes(StoneButtonStyle.FindBinariesDir(),
                out var sn, out var sh, out var sp, out var sd))
        {
            btnNormal = sn!; btnHover = sh!; btnPressed = sp!; btnDisabled = sd!;
        }
        else
        {
            btnNormal = MakeButtonStyle("res://assets/ui/btn_normal.png", new Color(1, 1, 1));
            btnHover = MakeButtonStyle("res://assets/ui/btn_hover.png", new Color(1, 1, 1));
            btnPressed = MakeButtonStyle("res://assets/ui/btn_hover.png", new Color(0.85f, 0.8f, 0.7f));
            btnDisabled = MakeButtonStyle("res://assets/ui/btn_normal.png", new Color(0.5f, 0.5f, 0.5f, 0.6f));
        }

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

    // =========================================================================
    // Modern 风(mods/mod gui/common/modern):gamesetup_mp 等 MP 对话框的原版样式。
    // =========================================================================

    /// <summary>ModernButtonRed(modern/sprites.xml):红石按钮。原版图集为 9 件
    /// (8px 边),已离线拼成单幅 red-button-9patch.png(144×32);Godot
    /// StyleBoxTexture 8px 纹理边距等价 9-patch。hover=微亮,pressed=压暗,
    /// disabled=降饱和半透明(原版同贴图不同色)。</summary>
    public static Theme GetRedButtonTheme()
    {
        var theme = new Theme();
        theme.SetStylebox("normal", "Button", MakeRedButtonStyle(Colors.White));
        theme.SetStylebox("hover", "Button", MakeRedButtonStyle(new Color(1.12f, 1.08f, 1.02f)));
        theme.SetStylebox("pressed", "Button", MakeRedButtonStyle(new Color(0.82f, 0.76f, 0.70f)));
        theme.SetStylebox("disabled", "Button", MakeRedButtonStyle(new Color(0.7f, 0.7f, 0.7f, 0.55f)));
        // 原版:sans-bold-stroke-14 白字居中;disabled 210 210 210 160。
        theme.SetColor("font_color", "Button", Colors.White);
        theme.SetColor("font_hover_color", "Button", Colors.White);
        theme.SetColor("font_pressed_color", "Button", Colors.White);
        theme.SetColor("font_disabled_color", "Button", new Color(210f / 255f, 210f / 255f, 210f / 255f, 160f / 255f));
        theme.SetFontSize("font_size", "Button", 14);
        return theme;
    }

    private static StyleBox MakeRedButtonStyle(Color modulate)
    {
        var tex = TryLoad("res://assets/ui/modern/button/red-button-9patch.png");
        if (tex == null)
        {
            var flat = new StyleBoxFlat { BgColor = new Color(0.45f, 0.16f, 0.12f) * modulate };
            flat.SetContentMarginAll(8);
            return flat;
        }
        var style = new StyleBoxTexture { Texture = tex, ModulateColor = modulate };
        style.SetTextureMarginAll(8);
        style.SetContentMarginAll(8);
        style.AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile;
        style.AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile;
        return style;
    }

    /// <summary>ModernDialog(modern/sprites.xml)装饰:深色底(background.png 平铺)
    /// + 上下金线(border.png)+ 顶/底渐变阴影(shadow-low.png 底部正置、顶部翻转)。
    /// Godot 单层 StyleBox 无法叠加 → Panel 底盒 + 子 TextureRect 叠层。</summary>
    public static void ApplyModernDialog(Panel panel)
    {
        var bgTex = TryLoad("res://assets/ui/modern/background.png");
        if (bgTex != null)
        {
            var bg = new StyleBoxTexture { Texture = bgTex };
            bg.AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile;
            bg.AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile;
            bg.SetContentMarginAll(10);
            panel.AddThemeStyleboxOverride("panel", bg);
        }
        else
        {
            panel.AddThemeStyleboxOverride("panel",
                new StyleBoxFlat { BgColor = new Color(0.10f, 0.09f, 0.08f, 0.98f) });
        }

        var borderTex = TryLoad("res://assets/ui/modern/border.png");
        if (borderTex != null)
        {
            foreach (bool top in new[] { true, false })
            {
                var line = new TextureRect
                {
                    Texture = borderTex,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.Tile,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                line.SetAnchorsPreset(top ? Control.LayoutPreset.TopWide : Control.LayoutPreset.BottomWide);
                line.OffsetLeft = 4; line.OffsetRight = -4;
                if (top) { line.OffsetTop = 0; line.OffsetBottom = 4; }
                else { line.OffsetTop = -8; line.OffsetBottom = 0; }
                panel.AddChild(line);
            }
        }

        var shadowTex = TryLoad("res://assets/ui/modern/shadow-low.png");
        if (shadowTex != null)
        {
            foreach (bool top in new[] { true, false })
            {
                var shade = new TextureRect
                {
                    Texture = shadowTex,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.Scale,
                    FlipV = top,   // 原版顶部为同图上下镜像
                    Modulate = new Color(1, 1, 1, 0.5f),
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                shade.SetAnchorsPreset(top ? Control.LayoutPreset.TopWide : Control.LayoutPreset.BottomWide);
                shade.OffsetLeft = 4; shade.OffsetRight = -4;
                if (top) { shade.OffsetTop = 4; shade.OffsetBottom = 60; }
                else { shade.OffsetTop = -60; shade.OffsetBottom = -4; }
                panel.AddChild(shade);
            }
        }
    }

    public static Texture2D? TryLoad(string resPath)
    {
        var img = AssetIO.LoadImageRes(resPath);
        return img != null ? ImageTexture.CreateFromImage(img) : null;
    }
}
