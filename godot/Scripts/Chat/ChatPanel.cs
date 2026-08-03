using System.Collections.Generic;
using Godot;
using ZeroAD.Sim;
using ZeroAD.Sim.Events;

namespace ZeroAD.Godot;

/// <summary>局内聊天面板。左上角消息日志 + 隐藏的输入框（Enter 打开）。
/// 镜像原版 gui/session/chat（简化：不做 addressee 下拉/tab 补全/30s 淡出）。
///
/// 消息来源：SimEventBus.ChatMessage（SP 系统消息 + MP ReceiveChat 转发）。
/// 发送：SP 直接回显；MP 经 MultiplayerController.SendChat → host 广播。
/// Enter 打开输入框 → 输入 → Enter 发送 / Esc 取消。</summary>
public sealed partial class ChatPanel : CanvasLayer
{
    private readonly SimBridge _sim;
    private readonly MultiplayerController? _mp;
    private readonly uint _localPlayerId;
    private readonly List<string> _lines = new();
    private const int MaxLines = 20;

    private VBoxContainer _logContainer = null!;
    private LineEdit _input = null!;
    private bool _inputVisible;

    public ChatPanel(SimBridge sim, MultiplayerController? mp, uint localPlayerId)
    {
        _sim = sim;
        _mp = mp;
        _localPlayerId = localPlayerId;
        Layer = 40;  // 在 HUD 之下，不挡资源栏
    }

    public override void _Ready()
    {
        // 左上角消息日志（资源栏下方，y>36）
        _logContainer = new VBoxContainer
        {
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 0, AnchorBottom = 0,
            OffsetLeft = 4, OffsetTop = 40,
            OffsetRight = 340, OffsetBottom = 200,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _logContainer.AddThemeConstantOverride("separation", 2);
        AddChild(_logContainer);

        // 输入框（默认隐藏，Enter 打开）
        _input = new LineEdit
        {
            PlaceholderText = "输入消息，Enter 发送，Esc 取消",
            Visible = false,
            AnchorLeft = 0, AnchorTop = 0,
            OffsetLeft = 4, OffsetTop = 210,
            OffsetRight = 340, OffsetBottom = 242,
            MaxLength = 256,
        };
        _input.GuiInput += OnInputGui;
        AddChild(_input);

        // 订阅聊天消息
        _sim.Events.ChatMessage += OnChatMessage;
    }

    public override void _ExitTree()
    {
        _sim.Events.ChatMessage -= OnChatMessage;
        base._ExitTree();
    }

    /// <summary>打开输入框（由 Main._UnhandledInput 的 Enter 热键调用）。</summary>
    public void OpenInput()
    {
        if (_inputVisible) return;
        _inputVisible = true;
        _input.Visible = true;
        _input.Text = "";
        _input.GrabFocus();
    }

    private void CloseInput()
    {
        _inputVisible = false;
        _input.Visible = false;
        _input.ReleaseFocus();
    }

    private void OnInputGui(InputEvent evt)
    {
        if (evt is not InputEventKey k || !k.Pressed) return;
        if (k.Keycode == Key.Enter)
        {
            SubmitInput();
            CloseInput();
        }
        else if (k.Keycode == Key.Escape)
        {
            CloseInput();
        }
    }

    private void SubmitInput()
    {
        string text = _input.Text.Trim();
        if (text.Length == 0) return;
        // 发送：MP 经 controller 广播；SP 直接 raise 本地事件。
        if (_mp != null)
            _mp.SendChat((int)_localPlayerId, text);
        else
            _sim.Events.RaiseChatMessage(new ChatMessageEvent
            { Kind = ChatMessageEvent.KindType.Message, SenderPlayerId = (int)_localPlayerId, Text = text });
    }

    private void OnChatMessage(ChatMessageEvent e)
    {
        string line = e.Kind == ChatMessageEvent.KindType.System
            ? $"[i]{e.Text}[/i]"  // 系统消息斜体
            : $"P{e.SenderPlayerId}: {e.Text}";
        AddLine(line);
    }

    private void AddLine(string bbcode)
    {
        _lines.Add(bbcode);
        if (_lines.Count > MaxLines) _lines.RemoveAt(0);
        // 重建日志（简单实现：清空重加）
        foreach (var child in _logContainer.GetChildren())
            ((Node)child).QueueFree();
        foreach (var l in _lines)
        {
            var label = new Label
            {
                Text = l,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            label.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.85f));
            _logContainer.AddChild(label);
        }
    }
}
