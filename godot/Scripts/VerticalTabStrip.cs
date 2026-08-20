using System;
using Godot;

namespace ZeroAD.Godot;

/// <summary>原版 gamesetup 右侧纵向页签条(gui/common/tab_buttons.js 的
/// placeTabButtons(categories, horizontal=false, 34, 4) + GameSettingsTabs):
/// Map/Player/Game Type 竖排全宽按钮,34px 高、4px 间距,点击回调页签索引。
/// "贴图" = mod/gui/common/modern/sprites.xml 的 ModernTabVertical*——纯 backcolor
/// 绘制(无纹理文件):未选 = 底 50 35 0 α120 + 四边 1px 金;选中 = 底 255 255 255 α40
/// + 四边 2px 金(gold = 237 227 167,mod/gui/common/modern/setup.xml)。文字 =
/// ModernLabelText(白、居中、14)。外框 = ModernDarkBoxGold(上下 1px 金线 +
/// 底 12 12 12 α100)。</summary>
public partial class VerticalTabStrip : PanelContainer
{
    public event Action<int>? TabSelected;

    private static readonly Color Gold = new(237 / 255f, 227 / 255f, 167 / 255f);
    private const int ButtonHeight = 34;   // GameSettingTabs.TabButtonHeight
    private const int ButtonMargin = 4;    // GameSettingTabs.TabButtonMargin

    private readonly Button[] _buttons;

    public VerticalTabStrip(string[] labels)
    {
        // ModernDarkBoxGold 外框(上下金线 + 深色底,按钮区域无外边框)。
        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(12 / 255f, 12 / 255f, 12 / 255f, 100 / 255f),
            BorderWidthTop = 1, BorderWidthBottom = 1,
            BorderColor = Gold,
            ContentMarginTop = 3, ContentMarginBottom = 3,
        });

        var vb = new VBoxContainer();
        vb.AddThemeConstantOverride("separation", ButtonMargin);
        AddChild(vb);

        _buttons = new Button[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            int idx = i;
            var btn = new Button
            {
                Text = labels[i],
                CustomMinimumSize = new Vector2(0, ButtonHeight),
            };
            btn.AddThemeFontSizeOverride("font_size", 14);
            btn.AddThemeColorOverride("font_color", Colors.White);
            btn.AddThemeColorOverride("font_hover_color", Colors.White);
            btn.AddThemeColorOverride("font_pressed_color", Colors.White);
            btn.Pressed += () => Select(idx);
            vb.AddChild(btn);
            _buttons[i] = btn;
        }
        Restyle(0);
    }

    public int SelectedIndex { get; private set; }

    /// <summary>选中页签:重贴按钮样式并回调(构造时默认选中 0,不回调——
    /// 订阅者尚未挂上,初始页显隐由调用方自己摆)。</summary>
    public void Select(int idx)
    {
        if (idx < 0 || idx >= _buttons.Length || idx == SelectedIndex) return;
        SelectedIndex = idx;
        Restyle(idx);
        TabSelected?.Invoke(idx);
    }

    private void Restyle(int selected)
    {
        SelectedIndex = selected;
        for (int i = 0; i < _buttons.Length; i++)
            ApplySprite(_buttons[i], i == selected);
    }

    private static void ApplySprite(Button btn, bool selected)
    {
        var box = new StyleBoxFlat
        {
            BgColor = selected
                ? new Color(1f, 1f, 1f, 40 / 255f)
                : new Color(50 / 255f, 35 / 255f, 0f, 120 / 255f),
            BorderWidthTop = selected ? 2 : 1,
            BorderWidthBottom = selected ? 2 : 1,
            BorderWidthLeft = selected ? 2 : 1,
            BorderWidthRight = selected ? 2 : 1,
            BorderColor = Gold,
        };
        // 原版 ModernTabButtonVertical 无独立 hover/pressed 贴图——三态同一样式。
        btn.AddThemeStyleboxOverride("normal", box);
        btn.AddThemeStyleboxOverride("hover", box);
        btn.AddThemeStyleboxOverride("pressed", box);
        btn.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
    }
}
