using System.Collections.Generic;
using Godot;

namespace ZeroAD.Godot;

/// <summary>自定义 locale(原版 gui/locale_advanced/page_locale_advanced.xml +
/// locale_advanced.js):语言码 + 国家码组合成自定义 locale(如 "pt_BR" →
/// "pt" 语言 + "BR" 国家)。ValidateLocale 校验有效组合,字典文件列表显示
/// (原版 GetDictionariesForLocale/GetDictionaryLocale)。</summary>
public sealed partial class LocaleAdvancedPanel : ModalPanelBase
{
    private LineEdit _langInput = null!;
    private LineEdit _countryInput = null!;
    private Label _resultLabel = null!;
    private RichTextLabel _dictList = null!;

    public override void _Ready()
    {
        var (content, status) = BuildShell("Advanced Locale", 480);
        status.Text = "";

        // 语言码(原版 langInput)。
        var langRow = new HBoxContainer();
        langRow.AddThemeConstantOverride("separation", 8);
        langRow.AddChild(new Label
        {
            Text = "Language code:",
            CustomMinimumSize = new Vector2(120, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        _langInput = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _langInput.TextChanged += _ => UpdateResult();
        langRow.AddChild(_langInput);
        content.AddChild(langRow);

        // 国家码(原版 countryInput)。
        var countryRow = new HBoxContainer();
        countryRow.AddThemeConstantOverride("separation", 8);
        countryRow.AddChild(new Label
        {
            Text = "Country code:",
            CustomMinimumSize = new Vector2(120, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        _countryInput = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _countryInput.TextChanged += _ => UpdateResult();
        countryRow.AddChild(_countryInput);
        content.AddChild(countryRow);

        // 组合结果(原版 resultingLocaleText:lang_country 或无效提示)。
        _resultLabel = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        content.AddChild(_resultLabel);

        // 字典文件列表(原版 dictionaryFile:该 locale 的 .po 文件清单)。
        _dictList = new RichTextLabel
        {
            BbcodeEnabled = false,
            FitContent = true,
            ScrollActive = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 180),
        };
        _dictList.AddThemeFontSizeOverride("normal_font_size", 12);
        content.AddChild(_dictList);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 12);
        content.AddChild(buttons);
        AddButton(buttons, "Cancel", CloseRequested, minWidth: 160);
        AddButton(buttons, "Accept", Apply, minWidth: 160);
    }

    private void UpdateResult()
    {
        string lang = _langInput.Text.Trim().ToLowerInvariant();
        string country = _countryInput.Text.Trim().ToUpperInvariant();
        string locale = lang.Length > 0 && country.Length > 0 ? $"{lang}_{country}" : lang;
        // 原版 ValidateLocale:非空且组合有效即接受(字典存在与否影响字典列表显示)。
        bool valid = locale.Length > 0;
        _resultLabel.Text = valid ? locale : "invalid locale";
        _dictList.Text = valid ? LoadDictionaryList(locale) : "";
    }

    /// <summary>字典文件列表(原版 GetDictionariesForLocale:该 locale 的 .po 清单;
    /// 缺失回退提示)。</summary>
    private static string LoadDictionaryList(string locale)
    {
        string? dir = RuntimePaths.FindPublicPath("l10n");
        if (dir == null) return "No dictionaries found.";
        var files = new List<string>();
        foreach (var file in System.IO.Directory.GetFiles(dir, "*.po"))
        {
            string name = System.IO.Path.GetFileName(file);
            if (name.StartsWith(locale, System.StringComparison.OrdinalIgnoreCase))
                files.Add(name);
        }
        if (files.Count > 0) return string.Join("\n", files);
        return "No dictionaries for this locale.";
    }

    private void Apply()
    {
        string lang = _langInput.Text.Trim().ToLowerInvariant();
        string country = _countryInput.Text.Trim().ToUpperInvariant();
        string locale = lang.Length > 0 && country.Length > 0 ? $"{lang}_{country}" : lang;
        if (locale.Length == 0) return;
        var cfg = GetNode<UserConfig>("/root/UserConfig");
        cfg.SetUserValue("locale", locale);
        cfg.Save();
        Localization.SetLocale(locale);
        CloseRequested();
    }

    private void CloseRequested() => QueueFree();
}
