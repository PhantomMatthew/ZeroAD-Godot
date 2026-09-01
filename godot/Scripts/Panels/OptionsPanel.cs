using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using ZeroAD.Godot.Options;

namespace ZeroAD.Godot;

// Options 页(原版 gui/page_options.xml + gui/options/options.js 的全量移植,功能与 C++ 一致)。
// 数据驱动:9 类 tab × 96 项全列出(类别/标签/tooltip/默认值/dependencies 与原版 options.json 一致),
// 7 控件渲染器(boolean/string/number/slider/dropdown/dropdownNumber/color),dependencies 未满足
// 禁用+缩进 25px。底部 Reset/Revert/Save/Close 语义对齐原版:
//   改动即时生效(写 user 命名空间+OptionsApplier)但仅本会话;Save 持久化;Revert 弃未存改动;
//   Reset 删全部用户值回落默认并立即持久化;Close 有未存改动时确认(未存值仍留内存生效)。
// gui.scale 独有 5s "保留更改?" 超时确认(原版 timedConfirmation),未确认回落旧值。
// 从 MainMenu(Layer 58)与 PauseMenu(Layer 65,inGame:true)两处打开。
public sealed partial class OptionsPanel : ModalPanelBase
{
    /// <summary>一行选项:标签宿主(禁用置灰)+ 按类型的禁用/校验回调。</summary>
    private sealed class Row
    {
        public required OptionDef Def;
        public required Control LabelHost;
        public required Action<bool> SetEnabled;
        public required Func<bool> IsInvalid;
    }

    private readonly int _layer;
    private readonly bool _inGame;
    private UserConfig _cfg = null!;
    private VBoxContainer _rows = null!;
    private readonly List<Button> _tabButtons = new();
    private readonly List<Row> _currentRows = new();
    private Button _revertButton = null!;
    private Button _saveButton = null!;
    private Label _status = null!;
    private int _selectedCategory;
    private bool _populating;   // 初值填充期抑制 change(对齐原版填充时 stub change handler)

    // gui.scale 超时确认(原版 dropdownNumber 的 timeout → timedConfirmation 5s)。
    private ConfirmationDialog _timeoutDialog = null!;
    private global::Godot.Timer _timeoutTimer = null!;
    private OptionDef? _timeoutDef;
    private string? _timeoutOldUserValue;

    public OptionsPanel(int layer = 58, bool inGame = false)
    {
        // 默认 58(高于普通菜单面板 55)。从 PauseMenu(Layer 60)打开时传 65。
        _layer = layer;
        _inGame = inGame;
    }

    public override void _Ready()
    {
        // 原版 ModernDialog 800×748 居中(size 50%±400/50%±374)——BuildShell 锚点居中 + 内部按
        // 原版比例:左 tab 列(15..230≈215 宽)、右选项区(240..785≈545 宽)、底部 4 键(各 162×28)。
        var (content, status) = BuildShell("Game Options", 800);
        // BuildShell 硬编码 Layer=55——覆盖为请求层,须在其后设置才生效。
        Layer = _layer;
        _status = status;
        _cfg = GetNode<UserConfig>("/root/UserConfig");

        var split = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddThemeConstantOverride("separation", 12);
        content.AddChild(split);

        // 左:9 类竖排 tab(对齐原版 tab_buttons 列,宽≈215)。
        var tabs = new VBoxContainer { CustomMinimumSize = new Vector2(210, 0) };
        tabs.AddThemeConstantOverride("separation", 4);
        split.AddChild(tabs);
        for (int i = 0; i < OptionsCatalog.Categories.Count; i++)
        {
            int idx = i;
            var cat = OptionsCatalog.Categories[i];
            var btn = new Button
            {
                Text = Localization.Tr(cat.Label),
                TooltipText = cat.Tooltip,
                Theme = UITheme.GetTheme(),
                ToggleMode = true,
                Alignment = HorizontalAlignment.Left,
            };
            // 原版 tab_buttons 为 StoneButtonFancy 贴图样式。
            StoneButtonStyle.Apply(btn, StoneButtonStyle.FindBinariesDir());
            btn.Pressed += () => SelectCategory(idx);
            tabs.AddChild(btn);
            _tabButtons.Add(btn);
        }

        // 右:选项行滚动区(每类≤25 行,对齐原版行模板池;原版区宽≈545)。
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(530, 600),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_rows);
        split.AddChild(scroll);

        // 底:Reset/Revert/Save/Close(原版同序,各 162×28 居中成组)。
        var buttons = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        buttons.AddThemeConstantOverride("separation", 12);
        content.AddChild(buttons);
        AddButton(buttons, "Reset", ConfirmReset, minWidth: 160);
        _revertButton = AddButton(buttons, "Revert", RevertChanges, minWidth: 160);
        _saveButton = AddButton(buttons, "Save", SaveChanges, minWidth: 160);
        AddButton(buttons, "Close", CloseRequested, minWidth: 160);

        _timeoutDialog = new ConfirmationDialog
        {
            Title = "Warning",
            OkButtonText = "Yes",
            CancelButtonText = "No",
        };
        _timeoutDialog.Confirmed += KeepTimeoutChange;
        _timeoutDialog.Canceled += RevertTimeoutChange;
        AddChild(_timeoutDialog);
        _timeoutTimer = new global::Godot.Timer { OneShot = true };
        _timeoutTimer.Timeout += OnTimeoutExpired;
        AddChild(_timeoutTimer);
    }

    protected override void OnOpen()
    {
        SelectCategory(_selectedCategory);
        UpdateButtons();
    }

    // ── tab 与行构建 ──

    private void SelectCategory(int idx)
    {
        _selectedCategory = idx;
        for (int i = 0; i < _tabButtons.Count; i++)
            _tabButtons[i].ButtonPressed = i == idx;

        _populating = true;
        foreach (var child in _rows.GetChildren())
            child.QueueFree();
        _currentRows.Clear();
        foreach (var opt in OptionsCatalog.Categories[idx].Options)
            BuildRow(opt);
        _populating = false;
        RefreshDependencies();
    }

    private void BuildRow(OptionDef opt)
    {
        var rowBox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rowBox.AddThemeConstantOverride("separation", 8);

        // 标签:带 dependencies 的项缩进 25px(原版 option_label 视觉线索)。
        var labelHost = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 0.44f,
        };
        if (opt.Dependencies != null)
            labelHost.AddThemeConstantOverride("margin_left", 25);
        var label = new Label
        {
            Text = Localization.Tr(opt.Label),
            TooltipText = opt.Tooltip,
            Theme = UITheme.GetTheme(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        labelHost.AddChild(label);
        rowBox.AddChild(labelHost);

        var controlHost = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 0.56f,
        };
        controlHost.AddThemeConstantOverride("separation", 6);
        rowBox.AddChild(controlHost);

        var (setEnabled, isInvalid) = BuildControl(opt, controlHost);
        _rows.AddChild(rowBox);
        _currentRows.Add(new Row { Def = opt, LabelHost = labelHost, SetEnabled = setEnabled, IsInvalid = isInvalid });
    }

    /// <summary>按 type 渲染控件(对齐 options.js 的 g_OptionType 分派),返回(禁用回调, 校验回调)。</summary>
    private (Action<bool>, Func<bool>) BuildControl(OptionDef opt, HBoxContainer host)
    {
        string eff = _cfg.GetEffective(opt.Config);
        switch (opt.Type)
        {
            case "boolean":
            {
                var cb = new CheckBox
                {
                    ButtonPressed = eff == "true",
                    TooltipText = opt.Tooltip,
                    // 原版 checkbox 在选项区右缘(size 95%..100%)——ShrinkEnd 右对齐。
                    SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
                };
                cb.Toggled += on => Changed(opt, on ? "true" : "false");
                host.AddChild(cb);
                return (en => cb.Disabled = !en, () => false);
            }
            case "string":
            {
                var le = MakeLineEdit(opt, eff);
                le.TextChanged += t => Changed(opt, t);
                host.AddChild(le);
                return (en => le.Editable = en, () => false);
            }
            case "number":
            {
                var le = MakeLineEdit(opt, eff);
                // 原版 number 存原始字符串;sanitize 只驱动 invalid 视觉态(不写回 clamp)。
                MarkInvalidNumber(le, opt, eff);
                le.TextChanged += t =>
                {
                    MarkInvalidNumber(le, opt, t);
                    Changed(opt, t);
                };
                host.AddChild(le);
                return (en => le.Editable = en, () => IsInvalidNumber(opt, _cfg.GetEffective(opt.Config)));
            }
            case "slider":
            {
                var hs = new HSlider
                {
                    MinValue = opt.MinValue,
                    MaxValue = opt.MaxValue,
                    Step = 0.01,
                    Value = SliderValue(opt, eff),
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    TooltipText = SliderTooltip(opt, eff),
                };
                var valLabel = new Label
                {
                    Text = FormatNumber(eff),
                    CustomMinimumSize = new Vector2(48, 0),
                    Theme = UITheme.GetTheme(),
                };
                hs.ValueChanged += v =>
                {
                    string s = FormatNumber(v);
                    valLabel.Text = s;
                    hs.TooltipText = SliderTooltip(opt, s);
                    Changed(opt, s);
                };
                host.AddChild(hs);
                host.AddChild(valLabel);
                return (en => hs.Editable = en, () => false);
            }
            case "dropdown":
            case "dropdownNumber":
            {
                var ob = new OptionButton { TooltipText = opt.Tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                // locale 下拉动态填充(原版 options.js fillLocaleList 同款:静态 JSON 无法
                // 预知用户放入的 .po 包);其余下拉走静态 list。
                var entries = opt.Config == "locale"
                    ? Localization.AvailableLocales()
                        .Select(l => new OptionListEntry(l.Code, l.Name, null)).ToList()
                    : (opt.List ?? (IReadOnlyList<OptionListEntry>)Array.Empty<OptionListEntry>());
                foreach (var e in entries)
                    ob.AddItem(e.Label);
                for (int i = 0; i < entries.Count; i++)
                    ob.SetItemTooltip(i, entries[i].Tooltip ?? opt.Tooltip);
                ob.Selected = FindListIndex(entries, eff);
                ob.ItemSelected += i => Changed(opt, entries[(int)i].Value);
                host.AddChild(ob);
                return (en => ob.Disabled = !en, () => false);
            }
            case "color":
            {
                var le = MakeLineEdit(opt, eff);
                le.CustomMinimumSize = new Vector2(120, 0);
                var swatch = new ColorRect
                {
                    CustomMinimumSize = new Vector2(28, 28),
                    Color = ParseColor(eff) ?? InsaneColor,   // 非法值显示原版 g_InsaneColor 品红
                    TooltipText = ColorTooltip(opt),
                };
                var picker = new ColorPickerButton
                {
                    CustomMinimumSize = new Vector2(28, 28),
                    Color = ParseColor(eff) ?? new Color(1f, 1f, 1f),
                    TooltipText = opt.Tooltip,
                    EditAlpha = false,
                };
                le.TextChanged += t =>
                {
                    swatch.Color = ParseColor(t) ?? InsaneColor;
                    Changed(opt, t);
                };
                picker.ColorChanged += c =>
                {
                    string s = $"{c.R8} {c.G8} {c.B8}";
                    _populating = true;
                    le.Text = s;
                    _populating = false;
                    swatch.Color = c;
                    Changed(opt, s);
                };
                host.AddChild(le);
                host.AddChild(swatch);
                host.AddChild(picker);
                return (en =>
                {
                    le.Editable = en;
                    picker.Disabled = !en;
                }, () => ParseColor(_cfg.GetEffective(opt.Config)) == null);
            }
            default:
            {
                var lbl = MakeLabel($"(unsupported type: {opt.Type})", 13);
                host.AddChild(lbl);
                return (_ => { }, () => false);
            }
        }
    }

    private LineEdit MakeLineEdit(OptionDef opt, string text) => new()
    {
        Text = text,
        TooltipText = opt.Tooltip,
        Theme = UITheme.GetTheme(),
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };

    // ── 变更路径(对齐原版 change handler:写 user 命名空间 → 即时生效 → 重估依赖/按钮) ──

    private void Changed(OptionDef opt, string value)
    {
        if (_populating) return;
        string? oldUserValue = _cfg.GetUserValue(opt.Config);
        _cfg.SetUserValue(opt.Config, value);
        // locale 改动即时切换语言包(之后打开的面板即用新语言;已打开面板不实时重排,
        // tooltip 已注明回主菜单/重启后全量生效——与原版"部分页面需重开"行为一致)。
        if (opt.Config == "locale")
            Localization.SetLocale(value);
        OptionsApplier.Apply(opt, value, _cfg, GetTree(), _inGame);
        if (opt.TimeoutMs > 0)
            StartTimeoutConfirm(opt, oldUserValue);
        RefreshDependencies();
        UpdateButtons();
    }

    private void RefreshDependencies()
    {
        foreach (var row in _currentRows)
        {
            bool enabled = row.Def.Dependencies == null || row.Def.Dependencies.All(IsDependencyMet);
            row.SetEnabled(enabled);
            row.LabelHost.Modulate = enabled ? Colors.White : new Color(1f, 1f, 1f, 0.45f);
        }
    }

    /// <summary>对齐 options.js isDependencyMet:值按 GetEffective(user 带 default 回落)读取;
    /// ==/!=为字符串比,</<=/>/>=为数值比(JS +x,NaN 比较全 false);未知 op 缺省 ==。</summary>
    private bool IsDependencyMet(OptionDependency dep)
    {
        string actual = _cfg.GetEffective(dep.Config);
        return dep.Op switch
        {
            "!=" => actual != dep.Value,
            "<" => NumOrNaN(actual) < NumOrNaN(dep.Value),
            "<=" => NumOrNaN(actual) <= NumOrNaN(dep.Value),
            ">" => NumOrNaN(actual) > NumOrNaN(dep.Value),
            ">=" => NumOrNaN(actual) >= NumOrNaN(dep.Value),
            _ => actual == dep.Value,
        };
    }

    private void UpdateButtons()
    {
        bool dirty = _cfg.HasChanges;
        _revertButton.Disabled = !dirty;
        _saveButton.Disabled = !dirty;
    }

    // ── 底部四键(原版 setDefaults/revertChanges/saveChanges/closeButton 语义) ──

    private void ConfirmReset()
    {
        var dlg = new ConfirmationDialog
        {
            Title = "Reset Settings",
            DialogText = "Resetting the options will erase your saved settings and restore the defaults. Continue?",
            OkButtonText = "Reset",
        };
        dlg.Confirmed += () =>
        {
            _cfg.ClearUserNamespace();
            _cfg.Save();                    // 原版 Reset 立即持久化(destructive)
            ReapplyAndBroadcast();
            SelectCategory(_selectedCategory);
            UpdateButtons();
            _status.Text = "Settings reset to defaults.";
        };
        AddChild(dlg);
        dlg.PopupCentered();
    }

    private void RevertChanges()
    {
        _cfg.Revert();                      // 重读盘,丢未存改动
        ReapplyAndBroadcast();
        SelectCategory(_selectedCategory);
        UpdateButtons();
        _status.Text = "Changes reverted.";
    }

    private void SaveChanges()
    {
        // 原版先扫全部类别的 invalid 值,有则确认"仍要保存?"。
        bool anyInvalid = OptionsCatalog.Categories
            .SelectMany(c => c.Options)
            .Any(IsCurrentlyInvalid);
        if (!anyInvalid)
        {
            DoSave();
            return;
        }
        var dlg = new ConfirmationDialog
        {
            Title = "Invalid Settings",
            DialogText = "Some setting values are invalid! Are you sure you want to save them?",
            OkButtonText = "Save",
        };
        dlg.Confirmed += DoSave;
        AddChild(dlg);
        dlg.PopupCentered();
    }

    private void DoSave()
    {
        _cfg.Save();
        UpdateButtons();
        _status.Text = "Settings saved.";
    }

    private void CloseRequested()
    {
        if (!_cfg.HasChanges)
        {
            Close();
            return;
        }
        var dlg = new ConfirmationDialog
        {
            Title = "Unsaved Changes",
            DialogText = "You have unsaved changes, do you want to close this window?\nUnsaved changes affect this session only.",
            OkButtonText = "Close",
        };
        dlg.Confirmed += Close;
        AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>Revert/Reset 后:全量重放 + 广播全部唯一 config 键(对齐原版 revertChanges 重调
    /// 所有 function + 关闭页时 fireConfigChangeHandlers(changedKeys))。</summary>
    private void ReapplyAndBroadcast()
    {
        OptionsApplier.ApplyAll(_cfg, GetTree(), _inGame);
        _cfg.FireConfigChanged(AllConfigKeys());
    }

    private static List<string> AllConfigKeys() =>
        OptionsCatalog.Categories.SelectMany(c => c.Options).Select(o => o.Config).Distinct().ToList();

    /// <summary>Save 前的全类别 invalid 扫描(number 越界/非数、color 格式错;对齐原版 sanitize 判据)。</summary>
    private bool IsCurrentlyInvalid(OptionDef opt) => opt.Type switch
    {
        "number" => IsInvalidNumber(opt, _cfg.GetEffective(opt.Config)),
        "color" => ParseColor(_cfg.GetEffective(opt.Config)) == null,
        _ => false,
    };

    // ── gui.scale 超时确认(原版 timedConfirmation:5s 内未确认回落旧值) ──

    private void StartTimeoutConfirm(OptionDef opt, string? oldUserValue)
    {
        _timeoutDef = opt;
        _timeoutOldUserValue = oldUserValue;
        _timeoutDialog.DialogText =
            $"Changes will be reverted in {(int)(opt.TimeoutMs / 1000)} seconds. Do you want to keep changes?";
        _timeoutTimer.Start(opt.TimeoutMs / 1000.0);
        _timeoutDialog.PopupCentered();
    }

    private void KeepTimeoutChange()
    {
        _timeoutTimer.Stop();
        _timeoutDef = null;
    }

    private void OnTimeoutExpired()
    {
        if (_timeoutDialog.Visible)
            _timeoutDialog.Hide();
        RevertTimeoutChange();
    }

    /// <summary>原版 revertChange:恢复旧用户值(此前无覆盖则删键回落默认)+ 重新生效 + 重显。</summary>
    private void RevertTimeoutChange()
    {
        _timeoutTimer.Stop();
        if (_timeoutDef == null) return;
        var opt = _timeoutDef;
        _timeoutDef = null;
        if (_timeoutOldUserValue == null)
            _cfg.ResetUserValue(opt.Config);
        else
            _cfg.SetUserValue(opt.Config, _timeoutOldUserValue);
        OptionsApplier.Apply(opt, _cfg.GetEffective(opt.Config), _cfg, GetTree(), _inGame);
        SelectCategory(_selectedCategory);
        UpdateButtons();
    }

    // ── 值解析(对齐原版各 type 的 configToValue / guiToRgbColor) ──

    private static readonly Color InsaneColor = new(1f, 0f, 1f);   // 原版 g_InsaneColor "255 0 255"

    private static bool IsInvalidNumber(OptionDef opt, string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return true;
        return v < opt.MinValue || v > opt.MaxValue;
    }

    private void MarkInvalidNumber(LineEdit le, OptionDef opt, string text) =>
        le.Modulate = IsInvalidNumber(opt, text) ? new Color(1f, 0.45f, 0.45f) : Colors.White;

    private static double SliderValue(OptionDef opt, string eff) =>
        double.TryParse(eff, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v)
            ? Math.Clamp(v, opt.MinValue, opt.MaxValue)
            : opt.MinValue;

    /// <summary>对齐原版 slider tooltip:"Value: X (min: m, max: n)"(toFixed(2))。</summary>
    private static string SliderTooltip(OptionDef opt, string currentValue) =>
        $"{opt.Tooltip}\nValue: {FormatNumber(currentValue)} (min: {opt.MinRaw}, max: {opt.MaxRaw})";

    /// <summary>对齐原版 color tooltip 附加 "Default: X"(读 default 命名空间)。</summary>
    private string ColorTooltip(OptionDef opt)
    {
        string? dflt = UserConfig.GetDefault(opt.Config);
        return dflt == null ? opt.Tooltip : $"{opt.Tooltip}\nDefault: {dflt}";
    }

    private static string FormatNumber(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatNumber(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? FormatNumber(v) : s;

    /// <summary>对齐原版 guiToRgbColor:"r g b[ a]"(0-255 整数 3-4 分量)→ Color;非法返回 null。</summary>
    private static Color? ParseColor(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 3 or > 4) return null;
        var c = new float[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int n) || n is < 0 or > 255)
                return null;
            c[i] = n / 255f;
        }
        return new Color(c[0], c[1], c[2], parts.Length == 4 ? c[3] : 1f);
    }

    /// <summary>下拉选中匹配:字符串相等,或双方皆可解析为数值且数值相等
    /// (原版 JS String(1.0)=="1" 对 default.cfg 的 "1.0" 也能选中)。</summary>
    private static int FindListIndex(IReadOnlyList<OptionListEntry> entries, string current)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Value == current) return i;
            double a = NumOrNaN(entries[i].Value), b = NumOrNaN(current);
            if (!double.IsNaN(a) && !double.IsNaN(b) && a == b) return i;
        }
        return -1;
    }

    private static double NumOrNaN(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
}
