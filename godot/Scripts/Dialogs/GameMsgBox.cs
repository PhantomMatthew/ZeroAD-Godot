using global::Godot;

namespace ZeroAD.Godot.Dialogs;

// GameMsgBox — 原版 gui/mod/gui/msgbox(page_msgbox.xml + common/functions_msgbox.js)的端口:
// 居中定宽高对话框,标题 + 消息 + 最多 3 个按钮(惯例:cancel 在第一位),Esc = 按钮 0。
// 返回被点按钮的 0-based 索引(原版 Promise resolve(i))。
// 用法:GameMsgBox.Show(parent, 400, 200, "msg", "title", new[]{"No","Yes"}, idx => …);
public sealed partial class GameMsgBox : ModalPanelBase
{
    private string _message = "";
    private string[] _captions = { "OK" };
    private System.Action<int>? _onClose;

    /// <summary>显示消息框。captions 缺省 ["OK"];最多 3 个(原版上限)。</summary>
    public static GameMsgBox Show(Node parent, int width, int height, string message,
        string title, string[]? buttonCaptions = null, System.Action<int>? onClosed = null)
    {
        var box = new GameMsgBox
        {
            _message = message,
            _captions = buttonCaptions is { Length: > 0 } c ? c : new[] { "OK" },
            _onClose = onClosed,
            _width = width,
            _height = height,
            _titleText = title,
        };
        parent.AddChild(box);
        box.Open();
        return box;
    }

    private int _width = 400, _height = 200;
    private string _titleText = "";

    public override void _Ready()
    {
        var (content, _) = BuildShell(_titleText, Mathf.Min(_width, 500));
        // 原版 mbMain 定宽定高(50%±w/2);内容区给一个近似最小高度。
        var holder = new PanelContainer { CustomMinimumSize = new Vector2(0, Mathf.Max(_height - 140, 40)) };
        holder.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        var lbl = new Label
        {
            Text = _message,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        lbl.AddThemeFontSizeOverride("font_size", 14);
        holder.AddChild(lbl);
        content.AddChild(holder);

        // 按钮行(distributeButtonsHorizontally:水平均分;cancel 惯例在 index 0)。
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        row.AddThemeConstantOverride("separation", 12);
        content.AddChild(row);
        int n = Mathf.Min(_captions.Length, 3);
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            AddButton(row, _captions[i], () => Finish(idx));
        }
    }

    private void Finish(int idx)
    {
        Close();
        QueueFree();
        _onClose?.Invoke(idx);
    }

    /// <summary>Esc = 按钮 0(原版 cancelHotkey → buttons[0])。</summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible) return;
        if (e is InputEventKey k && k.Pressed && k.Keycode == Key.Escape)
        {
            Finish(0);
            GetViewport().SetInputAsHandled();
        }
    }
}
