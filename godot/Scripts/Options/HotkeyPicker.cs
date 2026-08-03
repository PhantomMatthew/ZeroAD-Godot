using Godot;

namespace ZeroAD.Godot.Options;

// UITheme 在 ZeroAD.Godot 命名空间。
using UITheme = ZeroAD.Godot.UITheme;

/// <summary>单个热键行。镜像原版 hotkeys.xml 列表行:[Name 60%] [Mapping 40% 深色输入框]。
/// 交互对齐原版 HotkeyPicker.js:点 mapping 框进入监听(显示 "Press a key...")→ 捕获下一个
/// InputEventKey/InputEventMouseButton → HotkeyApplier.Apply + 刷新显示;Esc 或左键点别处取消。</summary>
public sealed partial class HotkeyPicker : HBoxContainer
{
    private readonly UserConfig _cfg;
    private readonly HotkeyAction _action;
    private Button _mappingBtn = null!;
    private bool _listening;
    // 全局单监听:点另一行的 mapping 时先取消上一行(否则下一次按键会同时写两行)。
    private static HotkeyPicker? _activeListener;

    public HotkeyPicker(UserConfig cfg, HotkeyAction action)
    {
        _cfg = cfg;
        _action = action;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 8);
        // Name 列(60%)
        var nameLabel = new Label
        {
            Text = _action.DisplayLabel,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 0.6f,
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true,
        };
        AddChild(nameLabel);

        // Mapping 列(40%):ModernInput 样式深色框,点击进入监听(原版 combMappingBtn 覆盖 input)。
        _mappingBtn = new Button
        {
            CustomMinimumSize = new Vector2(0, 26),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 0.4f,
            ClipText = true,
            TooltipText = "Click to set the hotkey",
        };
        UITheme.ApplyModernInput(_mappingBtn);
        _mappingBtn.Pressed += StartListening;
        AddChild(_mappingBtn);
        RefreshDisplay();
    }

    private void StartListening()
    {
        if (_activeListener != null && _activeListener != this)
            _activeListener.CancelListening();
        _activeListener = this;
        _listening = true;
        _mappingBtn.Text = "Press a key...";
    }

    public override void _UnhandledInput(InputEvent evt)
    {
        if (!_listening) return;
        // Esc 取消
        if (evt is InputEventKey ek && ek.Keycode == Key.Escape && ek.Pressed)
        {
            CancelListening();
            GetViewport().SetInputAsHandled();
            return;
        }
        // 捕获按键按下(忽略释放)
        if (!evt.IsPressed()) return;
        if (evt is InputEventKey k && k.Keycode != Key.Ctrl && k.Keycode != Key.Shift
            && k.Keycode != Key.Alt && k.Keycode != Key.Meta)
        {
            HotkeyApplier.Apply(_cfg, _action.FullName, HotkeyCombo.Format(k));
            CancelListening();
            GetViewport().SetInputAsHandled();
        }
        else if (evt is InputEventMouseButton m && m.ButtonIndex != MouseButton.None)
        {
            // 左键 = 点别处取消(否则会误绑 MouseLeft);其余鼠标键照常捕获。
            if (m.ButtonIndex == MouseButton.Left)
            {
                CancelListening();
                return;
            }
            HotkeyApplier.Apply(_cfg, _action.FullName, HotkeyCombo.Format(m));
            CancelListening();
            GetViewport().SetInputAsHandled();
        }
    }

    private void CancelListening()
    {
        if (_activeListener == this) _activeListener = null;
        _listening = false;
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        var combos = HotkeyApplier.GetCurrentCombos(_cfg, _action);
        _mappingBtn.Text = combos.Count > 0 ? string.Join(" / ", combos) : "";
    }
}
