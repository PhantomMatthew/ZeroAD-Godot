using Godot;
using System.Collections.Generic;

namespace ZeroAD.Godot;

/// <summary>语言选择页(原版 gui/page_locale.xml 的移植):
/// Settings → Language 打开;ModernDialog 居中,Language 下拉(动态发现 .po 包)+
/// 当前 locale 码显示 + Cancel/Accept。Accept 写 locale 配置并即时切包——
/// 主菜单在面板关闭后重建自身应用全量翻译(同 Options 路径)。Advanced(自定义
/// locale 输入,page_locale_advanced)留 backlog。</summary>
public sealed partial class LocalePanel : ModalPanelBase
{
    private OptionButton _langList = null!;
    private Label _localeText = null!;
    private List<(string Code, string Name)> _locales = new();

    public override void _Ready()
    {
        var (content, _) = BuildShell(Localization.Tr("Language"), 420);

        _locales = Localization.AvailableLocales();

        // Language: 下拉(原版 languageList)
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(new Label
        {
            Text = Localization.Tr("Language:"),
            CustomMinimumSize = new Vector2(110, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        _langList = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        int selected = 0;
        for (int i = 0; i < _locales.Count; i++)
        {
            _langList.AddItem(_locales[i].Name);
            if (_locales[i].Code == Localization.CurrentLocale) selected = i;
        }
        _langList.Selected = selected;
        _langList.ItemSelected += i => _localeText.Text = _locales[(int)i].Code.Length > 0
            ? _locales[(int)i].Code : "en";
        row.AddChild(_langList);
        content.AddChild(row);

        // Locale: 码显示(原版 localeText)
        var row2 = new HBoxContainer();
        row2.AddThemeConstantOverride("separation", 8);
        row2.AddChild(new Label
        {
            Text = Localization.Tr("Locale:"),
            CustomMinimumSize = new Vector2(110, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        _localeText = new Label
        {
            Text = _locales[selected].Code.Length > 0 ? _locales[selected].Code : "en",
            VerticalAlignment = VerticalAlignment.Center,
        };
        row2.AddChild(_localeText);
        content.AddChild(row2);

        // Cancel / Accept(原版左右两枚 ModernButtonRed;Advanced 留 backlog)
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 12);
        content.AddChild(buttons);
        AddButton(buttons, "Cancel", CloseRequested, minWidth: 140);
        // 原版 locale 页底部 Advanced(原版:page_locale_advanced 自定义 locale)。
        AddButton(buttons, "Advanced", OpenAdvanced, minWidth: 140);
        AddButton(buttons, "Accept", Apply, minWidth: 140);
    }

    private void Apply()
    {
        string code = _locales[_langList.Selected].Code;
        var cfg = GetNode<UserConfig>("/root/UserConfig");
        cfg.SetUserValue("locale", code);
        cfg.Save();   // 语言属即时持久化(原版 locale 页 Accept 即写盘)
        Localization.SetLocale(code);
        CloseRequested();
    }

    private void OpenAdvanced()
    {
        var panel = new LocaleAdvancedPanel();
        GetParent().AddChild(panel);
        panel.Open();
    }

    private void CloseRequested() => QueueFree();
}
