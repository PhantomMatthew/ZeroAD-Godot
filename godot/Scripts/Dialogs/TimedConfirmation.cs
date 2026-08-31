using global::Godot;

namespace ZeroAD.Godot.Dialogs;

// TimedConfirmation — 原版 gui/mod/gui/timedconfirmation 的端口:
// 带倒计时的确认框,消息含 %(time)s 占位(秒,向上取整)逐帧刷新;超时自动点按钮 0
// (原版 onTick:tmcButton1.onPress——即首按钮)。Esc = 按钮 0。
// 用法:TimedConfirmation.Show(parent, 400, 200, "Closing in %(time)s s", "time", 10000, "title");
public sealed partial class TimedConfirmation : ModalPanelBase
{
    private string _message = "";
    private string _timeParameter = "time";
    private double _timeoutMs = 10000;
    private string[] _captions = { "OK" };
    private System.Action<int>? _onClose;
    private double _deadlineMsec;
    private Label _lbl = null!;
    private int _width = 400, _height = 200;
    private string _titleText = "";

    public static TimedConfirmation Show(Node parent, int width, int height, string message,
        string timeParameter, double timeoutMs, string title,
        string[]? buttonCaptions = null, System.Action<int>? onClosed = null)
    {
        var dlg = new TimedConfirmation
        {
            _message = message,
            _timeParameter = timeParameter,
            _timeoutMs = timeoutMs,
            _titleText = title,
            _captions = buttonCaptions is { Length: > 0 } c ? c : new[] { "OK" },
            _onClose = onClosed,
            _width = width,
            _height = height,
        };
        parent.AddChild(dlg);
        dlg.Open();
        return dlg;
    }

    public override void _Ready()
    {
        var (content, _) = BuildShell(_titleText, Mathf.Min(_width, 500));
        _deadlineMsec = Time.GetTicksMsec() + _timeoutMs;

        var holder = new PanelContainer { CustomMinimumSize = new Vector2(0, Mathf.Max(_height - 140, 40)) };
        holder.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        _lbl = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _lbl.AddThemeFontSizeOverride("font_size", 14);
        holder.AddChild(_lbl);
        content.AddChild(holder);
        UpdateText(_timeoutMs);

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

    public override void _Process(double delta)
    {
        if (!Visible) return;
        double remaining = _deadlineMsec - Time.GetTicksMsec();
        if (remaining < 1)
        {
            Finish(0);   // 原版:超时触发按钮 0
            return;
        }
        UpdateText(remaining);
    }

    private void UpdateText(double remainingMs)
    {
        // sprintf(message, {[timeParameter]: ceil(ms/1000)})—— %(name)s 占位替换。
        _lbl.Text = _message.Replace("%(" + _timeParameter + ")s",
            Mathf.CeilToInt(remainingMs / 1000).ToString());
    }

    private void Finish(int idx)
    {
        SetProcess(false);
        Close();
        QueueFree();
        _onClose?.Invoke(idx);
    }

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
