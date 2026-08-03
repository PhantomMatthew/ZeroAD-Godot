using Godot;

namespace ZeroAD.Godot.Options;

/// <summary>单个热键行的重绑控件。镜像原版 HotkeyPicker.js。
/// 布局：[动作名 Label] [当前组合 Label] [重绑 Button] [重置 Button]。
/// 点"重绑"→ 进入监听模式（显示"按下按键..."）→ 捕获下一个 InputEventKey/InputEventMouseButton →
/// 调 HotkeyApplier.Apply + 刷新显示。Esc 取消监听。</summary>
public sealed partial class HotkeyPicker : HBoxContainer
{
    private readonly UserConfig _cfg;
    private readonly HotkeyAction _action;
    private Label _comboLabel = null!;
    private Button _rebindBtn = null!;
    private bool _listening;

    public HotkeyPicker(UserConfig cfg, HotkeyAction action)
    {
        _cfg = cfg;
        _action = action;
        SizeFlagsHorizontal = SizeFlags.Fill;
    }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 8);
        // 动作名（固定宽度左对齐）
        var nameLabel = new Label
        {
            Text = _action.DisplayLabel,
            CustomMinimumSize = new Vector2(220, 0),
            SizeFlagsHorizontal = SizeFlags.Fill,
            ClipText = true,
        };
        AddChild(nameLabel);

        // 当前组合
        _comboLabel = new Label
        {
            CustomMinimumSize = new Vector2(140, 0),
            SizeFlagsHorizontal = SizeFlags.Fill,
        };
        AddChild(_comboLabel);
        RefreshDisplay();

        // 重绑按钮
        _rebindBtn = new Button { Text = "重绑", CustomMinimumSize = new Vector2(60, 28) };
        _rebindBtn.Pressed += StartListening;
        AddChild(_rebindBtn);

        // 重置按钮
        var resetBtn = new Button { Text = "重置", CustomMinimumSize = new Vector2(60, 28) };
        resetBtn.Pressed += () =>
        {
            HotkeyApplier.Reset(_cfg, _action.FullName);
            RefreshDisplay();
        };
        AddChild(resetBtn);
    }

    private void StartListening()
    {
        _listening = true;
        _rebindBtn.Text = "按下按键...";
        _rebindBtn.ButtonPressed = true;
    }

    public override void _UnhandledInput(InputEvent evt)
    {
        if (!_listening) return;
        // Esc 取消
        if (evt is InputEventKey ek && ek.Keycode == Key.Escape && ek.Pressed)
        {
            CancelListening();
            return;
        }
        // 捕获按键按下（忽略释放）
        if (!evt.IsPressed()) return;
        if (evt is InputEventKey k && k.Keycode != Key.Ctrl && k.Keycode != Key.Shift
            && k.Keycode != Key.Alt && k.Keycode != Key.Meta)
        {
            string combo = HotkeyCombo.Format(k);
            HotkeyApplier.Apply(_cfg, _action.FullName, combo);
            CancelListening();
        }
        else if (evt is InputEventMouseButton m && m.ButtonIndex != MouseButton.None)
        {
            string combo = HotkeyCombo.Format(m);
            HotkeyApplier.Apply(_cfg, _action.FullName, combo);
            CancelListening();
        }
    }

    private void CancelListening()
    {
        _listening = false;
        _rebindBtn.Text = "重绑";
        _rebindBtn.ButtonPressed = false;
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        var combos = HotkeyApplier.GetCurrentCombos(_cfg, _action);
        _comboLabel.Text = combos.Count > 0 ? string.Join(" / ", combos) : "(无)";
    }
}
