using global::Godot;

namespace ZeroAD.Godot.Dialogs;

// ColorMixer — 原版 gui/mod/gui/colormixer 的端口:
// RGB 三通道滑杆(0..255 步进 1)+ 实时色块预览;Cancel 返回初始色的净化值,Save 返回当前色。
// 返回值格式 "r g b"(空格分隔,原版 currentColor() 同款)。
public sealed partial class ColorMixer : ModalPanelBase
{
    private int[] _channels = { 0, 0, 0 };
    private string _sanitized = "0 0 0";
    private System.Action<string>? _onClose;
    private ColorRect _preview = null!;
    private Label[] _valueLabels = new Label[3];
    private static readonly string[] Labels = { "Red", "Green", "Blue" };

    /// <summary>initialColor 格式 "100 0 200"(空格分隔 RGB)。</summary>
    public static ColorMixer Show(Node parent, string initialColor, System.Action<string>? onClosed = null)
    {
        var dlg = new ColorMixer { _onClose = onClosed };
        // 初始化即净化(floor + 越界钳 0..255,原版 Math.floor(+split[i] || 0))。
        var parts = initialColor.Split(' ');
        for (int i = 0; i < 3; i++)
            if (i < parts.Length && int.TryParse(parts[i], out int v))
                dlg._channels[i] = Mathf.Clamp(v, 0, 255);
        dlg._sanitized = $"{dlg._channels[0]} {dlg._channels[1]} {dlg._channels[2]}";
        parent.AddChild(dlg);
        dlg.Open();
        return dlg;
    }

    public override void _Ready()
    {
        var (content, _) = BuildShell(Localization.Tr("Color"), 420);
        content.AddChild(MakeLabel(
            Localization.Tr("Move the sliders to change the Red, Green and Blue components of the Color"), 13));

        _preview = new ColorRect
        {
            CustomMinimumSize = new Vector2(0, 48),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        content.AddChild(_preview);

        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var lbl = MakeLabel(Localization.Tr(Labels[i]), 13);
            lbl.CustomMinimumSize = new Vector2(52, 0);
            lbl.HorizontalAlignment = HorizontalAlignment.Left;
            row.AddChild(lbl);
            var slider = new HSlider
            {
                MinValue = 0, MaxValue = 255, Step = 1, Value = _channels[i],
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            slider.ValueChanged += v =>
            {
                _channels[idx] = (int)v;
                _valueLabels[idx].Text = ((int)v).ToString();
                RefreshPreview();
            };
            row.AddChild(slider);
            _valueLabels[i] = MakeLabel(_channels[i].ToString(), 13);
            _valueLabels[i].CustomMinimumSize = new Vector2(32, 0);
            row.AddChild(_valueLabels[i]);
            content.AddChild(row);
        }
        RefreshPreview();

        var buttons = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        buttons.AddThemeConstantOverride("separation", 12);
        content.AddChild(buttons);
        AddButton(buttons, "Cancel", () => Finish(_sanitized));   // 原版:cancel 返回净化的初始色
        AddButton(buttons, "Save", () => Finish(CurrentColor()));
    }

    private string CurrentColor() => $"{_channels[0]} {_channels[1]} {_channels[2]}";

    private void RefreshPreview() =>
        _preview.Color = new Color(_channels[0] / 255f, _channels[1] / 255f, _channels[2] / 255f);

    private void Finish(string color)
    {
        Close();
        QueueFree();
        _onClose?.Invoke(color);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible) return;
        if (e is InputEventKey k && k.Pressed && k.Keycode == Key.Escape)
        {
            Finish(_sanitized);
            GetViewport().SetInputAsHandled();
        }
    }
}
