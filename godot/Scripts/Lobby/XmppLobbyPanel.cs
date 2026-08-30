using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ZeroAD.Godot.Lobby;

/// <summary>XMPP 大厅面板（原版 gui/lobby/）。
/// 登录 → 大厅（玩家列表 + 游戏列表 + 聊天 + 排行榜）。
/// 骨架版——核心 UI 布局 + 事件订阅；XMPP 连接逻辑待 XmppDotNet 集成。</summary>
public sealed partial class XmppLobbyPanel : CanvasLayer
{
    private XmppLobbyClient? _client;
    private LineEdit _usernameInput = null!;
    private LineEdit _passwordInput = null!;
    private LineEdit _chatInput = null!;
    private ItemList _playerList = null!;
    private ItemList _gameList = null!;
    private RichTextLabel _chatLog = null!;
    private Button _connectBtn = null!;
    private Button _disconnectBtn = null!;
    private VBoxContainer _lobbyContent = null!;

    public XmppLobbyPanel()
    {
        Layer = 50;
    }

    public override void _Ready()
    {
        // 全屏背景
        var bg = new ColorRect
        {
            Color = new Color(0.04f, 0.035f, 0.03f, 0.97f),
            AnchorRight = 1.0f, AnchorBottom = 1.0f,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        AddChild(bg);

        var container = new VBoxContainer
        {
            AnchorRight = 1.0f, AnchorBottom = 1.0f,
            OffsetLeft = 20, OffsetTop = 20, OffsetRight = -20, OffsetBottom = -20,
        };
        AddChild(container);

        // ── 登录区 ──
        var loginRow = new HBoxContainer();
        container.AddChild(loginRow);
        loginRow.AddChild(new Label { Text = "Username:" });
        _usernameInput = new LineEdit { CustomMinimumSize = new Vector2(150, 0) };
        loginRow.AddChild(_usernameInput);
        loginRow.AddChild(new Label { Text = "Password:" });
        _passwordInput = new LineEdit { CustomMinimumSize = new Vector2(150, 0), Secret = true };
        loginRow.AddChild(_passwordInput);
        _connectBtn = new Button { Text = "Connect" };
        _connectBtn.Pressed += OnConnect;
        loginRow.AddChild(_connectBtn);
        _disconnectBtn = new Button { Text = "Disconnect", Disabled = true };
        _disconnectBtn.Pressed += OnDisconnect;
        loginRow.AddChild(_disconnectBtn);

        // ── 大厅内容（登录后显示）──
        _lobbyContent = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _lobbyContent.Visible = false;
        container.AddChild(_lobbyContent);

        var split = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _lobbyContent.AddChild(split);

        // 左：玩家列表
        var leftCol = new VBoxContainer { CustomMinimumSize = new Vector2(200, 0) };
        leftCol.AddChild(new Label { Text = "Players" });
        _playerList = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(200, 300) };
        leftCol.AddChild(_playerList);
        split.AddChild(leftCol);

        // 中：聊天
        var midCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        midCol.AddChild(new Label { Text = "Chat" });
        _chatLog = new RichTextLabel { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, BbcodeEnabled = true };
        midCol.AddChild(_chatLog);
        var chatRow = new HBoxContainer();
        _chatInput = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _chatInput.GuiInput += OnChatInputGui;
        chatRow.AddChild(_chatInput);
        var sendBtn = new Button { Text = "Send" };
        sendBtn.Pressed += OnSendChat;
        chatRow.AddChild(sendBtn);
        midCol.AddChild(chatRow);
        split.AddChild(midCol);

        // 右：游戏列表
        var rightCol = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        rightCol.AddChild(new Label { Text = "Games" });
        _gameList = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(280, 300) };
        rightCol.AddChild(_gameList);
        split.AddChild(rightCol);

        // 关闭按钮
        var closeBtn = new Button { Text = "Close", SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        closeBtn.Pressed += () => QueueFree();
        container.AddChild(closeBtn);
    }

    /// <summary>登录信息预填(prelobby login 注册成功/重试回填;原版
    /// page_prelobby_login 的输入框预填语义)。</summary>
    public void SetCredentials(string username, string password)
    {
        if (IsNodeReady())
        {
            _usernameInput.Text = username;
            _passwordInput.Text = password;
        }
        else
        {
            // 延迟到 _Ready(prelobby 注册转登录时面板刚 AddChild)。
            CallDeferred(nameof(SetCredentialsDeferred), username, password);
        }
    }

    private void SetCredentialsDeferred(string username, string password)
    {
        _usernameInput.Text = username;
        _passwordInput.Text = password;
    }

    private async void OnConnect()
    {
        var user = _usernameInput.Text.Trim();
        var pass = _passwordInput.Text;
        if (user.Length == 0 || pass.Length == 0) return;

        _connectBtn.Disabled = true;
        _connectBtn.Text = "Connecting...";

        _client = new XmppLobbyClient();
        _client.OnMessage += OnLobbyMessage;
        _client.OnPlayerListChanged += RefreshPlayerList;
        _client.OnGameListChanged += RefreshGameList;
        _client.OnConnected += () =>
        {
            CallDeferred(nameof(ShowLobby));
        };
        _client.OnDisconnected += (reason) =>
        {
            CallDeferred(nameof(HideLobby), reason);
        };

        try
        {
            await _client.ConnectAsync(user, pass, "arena", user);
        }
        catch (Exception ex)
        {
            // 连接/认证失败(网络不可达、证书、账号错误):复位按钮并把原因摊到聊天日志。
            _connectBtn.Disabled = false;
            _connectBtn.Text = "Connect";
            AppendChatMessage(new LobbyMessage
            {
                Type = LobbyMessage.MsgType.System,
                Level = "error",
                Text = "Connection failed: " + ex.Message,
                Time = DateTime.Now,
            });
            _client.Dispose();
            _client = null;
        }
    }

    private void OnDisconnect()
    {
        _client?.Disconnect();
    }

    private void ShowLobby()
    {
        _lobbyContent.Visible = true;
        _disconnectBtn.Disabled = false;
        _connectBtn.Text = "Connected";
    }

    private void HideLobby(string reason)
    {
        _lobbyContent.Visible = false;
        _disconnectBtn.Disabled = true;
        _connectBtn.Disabled = false;
        _connectBtn.Text = "Connect";
    }

    private void OnLobbyMessage(LobbyMessage msg)
    {
        _pendingMessages.Enqueue(msg);
        CallDeferred(nameof(ProcessPendingMessages));
    }

    private readonly System.Collections.Generic.Queue<LobbyMessage> _pendingMessages = new();

    private void ProcessPendingMessages()
    {
        while (_pendingMessages.Count > 0)
        {
            var msg = _pendingMessages.Dequeue();
            AppendChatMessage(msg);
        }
    }

    private void AppendChatMessage(LobbyMessage msg)
    {
        string line = msg.Type switch
        {
            LobbyMessage.MsgType.System => $"[color=gray][i]{msg.Text}[/i][/color]",
            LobbyMessage.MsgType.Chat => $"[color=yellow]{msg.From}:[/color] {msg.Text}",
            _ => msg.Text,
        };
        _chatLog.AppendText(line + "\n");
    }

    private void OnSendChat()
    {
        var text = _chatInput.Text.Trim();
        if (text.Length == 0 || _client == null) return;
        _client.SendMessage(text);
        _chatInput.Text = "";
    }

    private void OnChatInputGui(InputEvent ev)
    {
        if (ev is InputEventKey k && k.Pressed && k.Keycode == Key.Enter)
            OnSendChat();
    }

    private void RefreshPlayerList()
    {
        CallDeferred(nameof(DoRefreshPlayerList));
    }

    private void DoRefreshPlayerList()
    {
        if (_client == null) return;
        _playerList.Clear();
        foreach (var p in _client.GetPlayerList())
            _playerList.AddItem($"{p.Name} ({p.Presence})");
    }

    private void RefreshGameList()
    {
        CallDeferred(nameof(DoRefreshGameList));
    }

    private void DoRefreshGameList()
    {
        if (_client == null) return;
        _gameList.Clear();
        foreach (var g in _client.GetGameList())
            _gameList.AddItem($"{g.Name} ({g.Nbp}/{g.MaxNbp}) [{g.State}]");
    }

    public override void _ExitTree()
    {
        _client?.Dispose();
        base._ExitTree();
    }
}
