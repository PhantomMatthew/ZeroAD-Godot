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
    private ItemList _boardList = null!;
    private RichTextLabel _chatLog = null!;
    private Button _connectBtn = null!;
    private Button _disconnectBtn = null!;
    private Button _joinBtn = null!;
    private VBoxContainer _lobbyContent = null!;

    // 游戏列表过滤(原版 lobby 的 filter 概念简化版:文本 + 状态)。
    private LineEdit _gameFilterInput = null!;
    private OptionButton _gameStateFilter = null!;
    /// <summary>当前过滤后展示的游戏(与 _gameList 行一一对应;join 解析用)。</summary>
    private readonly List<LobbyGame> _filteredGames = new();

    // 玩家右键菜单(profile + moderator kick/ban)。
    private PopupMenu _playerMenu = null!;
    private int _playerMenuIndex = -1;
    // 资料弹窗(Echelon profile 应答填充)。
    private AcceptDialog _profileDlg = null!;
    private Label _profileLabel = null!;
    // kick/ban 理由输入弹窗。
    private Window _modActionDlg = null!;
    private LineEdit _modReasonInput = null!;
    private string _modActionNick = "";
    private bool _modActionIsBan;

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

        // 左：玩家列表(头部带 Profile 按钮,查自己资料;右键菜单:他人资料 + moderator kick/ban)
        var leftCol = new VBoxContainer { CustomMinimumSize = new Vector2(200, 0) };
        var playerHead = new HBoxContainer();
        playerHead.AddChild(new Label { Text = "Players",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var profileBtn = new Button { Text = "Profile", TooltipText = "View your lobby profile" };
        profileBtn.Pressed += () => OpenProfile(_client?.Nick ?? "");
        playerHead.AddChild(profileBtn);
        leftCol.AddChild(playerHead);
        _playerList = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(200, 300) };
        _playerList.ItemClicked += OnPlayerItemClicked;
        leftCol.AddChild(_playerList);
        split.AddChild(leftCol);

        // 玩家右键菜单( moderator 才见 kick/ban;原版大厅同款条件)。
        _playerMenu = new PopupMenu();
        _playerMenu.AddItem("View Profile", 0);
        _playerMenu.AddItem("Kick…", 1);
        _playerMenu.AddItem("Ban…", 2);
        _playerMenu.IdPressed += OnPlayerMenuIdPressed;
        AddChild(_playerMenu);
        BuildProfileDialog();
        BuildModActionDialog();

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

        // 右：游戏列表(过滤行 + 列表 + Join/Host 按钮行;原版 lobby 的 games 面板简化)
        var rightCol = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        rightCol.AddChild(new Label { Text = "Games" });
        var filterRow = new HBoxContainer();
        _gameFilterInput = new LineEdit
        {
            PlaceholderText = "Filter games…",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _gameFilterInput.TextChanged += _ => DoRefreshGameList();
        filterRow.AddChild(_gameFilterInput);
        _gameStateFilter = new OptionButton();
        foreach (var s in new[] { "All", "Waiting", "Running", "Init" })
            _gameStateFilter.AddItem(s);
        _gameStateFilter.ItemSelected += _ => DoRefreshGameList();
        filterRow.AddChild(_gameStateFilter);
        rightCol.AddChild(filterRow);
        _gameList = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(280, 300) };
        _gameList.ItemActivated += _ => JoinSelectedGame();
        _gameList.ItemSelected += _ => UpdateJoinButton();
        rightCol.AddChild(_gameList);
        var gameBtnRow = new HBoxContainer();
        _joinBtn = new Button
        {
            Text = "Join Game", Disabled = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "Connect to the selected game (needs a listed address)",
        };
        _joinBtn.Pressed += JoinSelectedGame;
        gameBtnRow.AddChild(_joinBtn);
        var hostBtn = new Button
        {
            Text = "Host Game",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "Host a new game (registered to the lobby so others can join)",
        };
        hostBtn.Pressed += HostGameFromLobby;
        gameBtnRow.AddChild(hostBtn);
        rightCol.AddChild(gameBtnRow);
        split.AddChild(rightCol);

        // 右二:排行榜(原版 leaderboard 面板;进大厅拉一次,刷新按钮重拉)。
        var boardCol = new VBoxContainer { CustomMinimumSize = new Vector2(220, 0) };
        var boardHead = new HBoxContainer();
        boardHead.AddChild(new Label { Text = "Leaderboard",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var refreshBtn = new Button { Text = "↻", TooltipText = "Refresh leaderboard" };
        refreshBtn.Pressed += () => _client?.RequestBoardList();
        boardHead.AddChild(refreshBtn);
        boardCol.AddChild(boardHead);
        _boardList = new ItemList
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(220, 300),
        };
        boardCol.AddChild(_boardList);
        split.AddChild(boardCol);

        // 关闭按钮(只关面板——XMPP 连接由 LobbySession 持有保持在线,原版离开大厅页不断连)
        var closeBtn = new Button { Text = "Close", SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        closeBtn.Pressed += () => QueueFree();
        container.AddChild(closeBtn);

        // 已连接会话(回主菜单/打完一局再进大厅):直接接管,免重登。
        if (LobbySession.IsConnected)
            AdoptSessionClient();
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

    /// <summary>订阅客户端事件(具名方法,_ExitTree 可退订防悬垂回调)。</summary>
    private void SubscribeClient(XmppLobbyClient client)
    {
        client.OnMessage += OnLobbyMessage;
        client.OnPlayerListChanged += RefreshPlayerList;
        client.OnGameListChanged += RefreshGameList;
        client.OnBoardListChanged += RefreshBoardList;
        client.OnProfileReceived += OnProfileReceived;
        client.OnConnected += OnClientConnected;
        client.OnDisconnected += OnClientDisconnected;
    }

    private void UnsubscribeClient(XmppLobbyClient client)
    {
        client.OnMessage -= OnLobbyMessage;
        client.OnPlayerListChanged -= RefreshPlayerList;
        client.OnGameListChanged -= RefreshGameList;
        client.OnBoardListChanged -= RefreshBoardList;
        client.OnProfileReceived -= OnProfileReceived;
        client.OnConnected -= OnClientConnected;
        client.OnDisconnected -= OnClientDisconnected;
    }

    private void OnClientConnected() => CallDeferred(nameof(ShowLobby));

    private void OnClientDisconnected(string reason) => CallDeferred(nameof(HideLobby), reason);

    /// <summary>接管 LobbySession 里已连接的客户端(跨场景保活:打完一局/关过面板
    /// 再进大厅不重登;原版 g_XmppClient 全局保活同款)。</summary>
    private void AdoptSessionClient()
    {
        _client = LobbySession.Client!;
        SubscribeClient(_client);
        _connectBtn.Disabled = true;
        ShowLobby();
        DoRefreshPlayerList();
        DoRefreshGameList();
        RefreshBoardList();
    }

    private async void OnConnect()
    {
        var user = _usernameInput.Text.Trim();
        var pass = _passwordInput.Text;
        if (user.Length == 0 || pass.Length == 0) return;

        _connectBtn.Disabled = true;
        _connectBtn.Text = "Connecting...";

        // 重连(上次断连的 client 还在手上):先释放,防泄漏。
        if (_client != null)
        {
            UnsubscribeClient(_client);
            if (LobbySession.Client != _client)
                _client.Dispose();
            _client = null;
        }
        _client = new XmppLobbyClient();
        SubscribeClient(_client);

        try
        {
            await _client.ConnectAsync(user, pass, "arena", user);
            // 登录成功即登记为进程级会话(host 注册/对局报告/再进大厅都经它)。
            LobbySession.Attach(_client);
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

    /// <summary>排行榜刷新(原版 leaderboard 列表:name — rating 行;rating 降序)。</summary>
    private void RefreshBoardList()
    {
        if (_client == null || _boardList == null) return;
        _boardList.Clear();
        foreach (var entry in _client.GetBoardList().OrderByDescending(e => e.Rating))
            _boardList.AddItem($"{entry.Name}  —  {entry.Rating}");
    }

    private void OnDisconnect()
    {
        if (_client == null) return;
        // 显式断连 = 会话结束(摘出 LobbySession;面板可能随后关,client 由这里释放)。
        LobbySession.Detach(_client);
        _client.Disconnect();
    }

    private void ShowLobby()
    {
        _lobbyContent.Visible = true;
        _disconnectBtn.Disabled = false;
        _client?.RequestBoardList();   // 进大厅拉榜(原版同款)
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
        {
            // 原版花名册行:评分(有评分时)+ 状态;moderator 标记便于辨认管理。
            string rating = p.Rating > 0 ? $" {p.Rating}" : "";
            string mod = p.Role == "moderator" ? " [mod]" : "";
            _playerList.AddItem($"{p.Name}{rating} ({p.Presence}){mod}");
        }
    }

    // ── 玩家右键菜单(View Profile / Kick / Ban;原版 lobby 玩家行菜单)──

    private void OnPlayerItemClicked(long index, Vector2 atPosition, long mouseButtonIndex)
    {
        // 左键单击不动作;右键 = 行菜单(profile / moderator kick/ban)。
        if (mouseButtonIndex != (long)MouseButton.Right || _client == null) return;
        _playerMenuIndex = (int)index;
        _playerList.Select((int)index);
        bool isSelf = SelectedPlayerName() == _client.Nick;
        _playerMenu.SetItemDisabled(1, !_client.IsModerator || isSelf);
        _playerMenu.SetItemDisabled(2, !_client.IsModerator || isSelf);
        _playerMenu.SetItemTooltip(1, _client.IsModerator ? "" : "Only room moderators can kick");
        _playerMenu.SetItemTooltip(2, _client.IsModerator ? "" : "Only room moderators can ban");
        _playerMenu.Position = new Vector2I((int)(_playerList.GlobalPosition.X + atPosition.X),
            (int)(_playerList.GlobalPosition.Y + atPosition.Y));
        _playerMenu.Popup();
    }

    private string? SelectedPlayerName()
    {
        if (_client == null || _playerMenuIndex < 0) return null;
        var list = _client.GetPlayerList();
        return _playerMenuIndex < list.Count ? list[_playerMenuIndex].Name : null;
    }

    private void OnPlayerMenuIdPressed(long id)
    {
        var nick = SelectedPlayerName();
        if (nick == null) return;
        switch (id)
        {
            case 0: OpenProfile(nick); break;
            case 1: OpenModAction(nick, ban: false); break;
            case 2: OpenModAction(nick, ban: true); break;
        }
    }

    // ── kick/ban 理由弹窗(XEP-0045 reason 可选;空理由也允许)──

    private void BuildModActionDialog()
    {
        _modActionDlg = new Window
        {
            Title = "Moderate", Transient = true, Exclusive = false,
            MinSize = new Vector2I(340, 120), Visible = false,
        };
        var vbox = new VBoxContainer();
        vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        vbox.OffsetLeft = 10; vbox.OffsetTop = 10; vbox.OffsetRight = -10; vbox.OffsetBottom = -10;
        _modActionDlg.AddChild(vbox);
        _modReasonInput = new LineEdit { PlaceholderText = "Reason (optional)" };
        vbox.AddChild(_modReasonInput);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var okBtn = new Button { Text = "OK" };
        okBtn.Pressed += ConfirmModAction;
        row.AddChild(okBtn);
        var cancelBtn = new Button { Text = "Cancel" };
        cancelBtn.Pressed += () => _modActionDlg.Hide();
        row.AddChild(cancelBtn);
        vbox.AddChild(row);
        _modActionDlg.CloseRequested += () => _modActionDlg.Hide();
        AddChild(_modActionDlg);
    }

    private void OpenModAction(string nick, bool ban)
    {
        _modActionNick = nick;
        _modActionIsBan = ban;
        _modActionDlg.Title = (ban ? "Ban " : "Kick ") + nick;
        _modReasonInput.Text = "";
        _modActionDlg.PopupCentered();
    }

    private void ConfirmModAction()
    {
        _modActionDlg.Hide();
        if (_client == null) return;
        if (_modActionIsBan)
            _client.BanOccupant(_modActionNick, _modReasonInput.Text.Trim());
        else
            _client.KickOccupant(_modActionNick, _modReasonInput.Text.Trim());
    }

    // ── Profile 弹窗(原版 lobby profile 页简化:评分/排名/胜负/总场次)──

    private void BuildProfileDialog()
    {
        _profileDlg = new AcceptDialog { Title = "Profile", Exclusive = false };
        _profileLabel = new Label { CustomMinimumSize = new Vector2(280, 0) };
        _profileDlg.AddChild(_profileLabel);
        AddChild(_profileDlg);
    }

    /// <summary>请求并展示某玩家资料(原版 profile 页打开即请求;应答异步填充)。</summary>
    private void OpenProfile(string playerName)
    {
        if (_client == null || playerName.Length == 0) return;
        _profileLabel.Text = $"Loading profile of {playerName}…";
        _profileDlg.Title = $"Profile — {playerName}";
        _profileDlg.PopupCentered();
        _client.RequestProfile(playerName);
    }

    private LobbyProfile? _pendingProfile;

    private void OnProfileReceived(LobbyProfile profile)
    {
        // CallDeferred 只接 Variant;资料对象经字段摆渡。
        _pendingProfile = profile;
        CallDeferred(nameof(ShowProfile));
    }

    private void ShowProfile()
    {
        var profile = _pendingProfile;
        if (profile == null) return;
        // 迟到应答只填充开着且同玩家的弹窗。
        if (!_profileDlg.Visible || _profileDlg.Title != $"Profile — {profile.Player}") return;
        static string Fmt(int v) => v < 0 ? "-" : v.ToString();
        _profileLabel.Text = !profile.Known
            ? $"{profile.Player}: no rated games on record."
            : $"Player:  {profile.Player}\n" +
              $"Rating:  {Fmt(profile.Rating)}   (highest: {Fmt(profile.HighestRating)})\n" +
              $"Rank:  {Fmt(profile.Rank)}\n" +
              $"Wins:  {Fmt(profile.Wins)}   Losses:  {Fmt(profile.Losses)}\n" +
              $"Total games:  {Fmt(profile.TotalGamesPlayed)}";
    }

    // ── 游戏列表:过滤 + join/host ──

    private void RefreshGameList()
    {
        CallDeferred(nameof(DoRefreshGameList));
    }

    private void DoRefreshGameList()
    {
        if (_client == null) return;
        string text = _gameFilterInput.Text.Trim();
        // 状态过滤:0=All 其余对应 state 字符串(waiting/running/init)。
        string state = _gameStateFilter.Selected switch
        {
            1 => "waiting",
            2 => "running",
            3 => "init",
            _ => "",
        };
        _filteredGames.Clear();
        foreach (var g in _client.GetGameList())
        {
            if (state.Length > 0 && g.State != state) continue;
            if (text.Length > 0
                && !g.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                && !g.NiceMapName.Contains(text, StringComparison.OrdinalIgnoreCase)
                && !g.MapName.Contains(text, StringComparison.OrdinalIgnoreCase)
                && !g.HostUsername.Contains(text, StringComparison.OrdinalIgnoreCase))
                continue;
            _filteredGames.Add(g);
        }
        _gameList.Clear();
        foreach (var g in _filteredGames)
        {
            // 名称(人数)[状态] — 地图;无地址(join 不了)的条目灰显提示。
            string map = g.NiceMapName.Length > 0 ? g.NiceMapName : g.MapName;
            int idx = _gameList.AddItem($"{g.Name} ({g.Nbp}/{g.MaxNbp}) [{g.State}] — {map}");
            if (!g.TryGetEndpoint(out _, out _))
                _gameList.SetItemCustomFgColor(idx, new Color(1f, 1f, 1f, 0.45f));
        }
        UpdateJoinButton();
    }

    /// <summary>当前选中行对应的游戏(过滤后下标 → _filteredGames)。</summary>
    private LobbyGame? SelectedGame()
    {
        var sel = _gameList.GetSelectedItems();
        return sel.Length > 0 && sel[0] < _filteredGames.Count ? _filteredGames[sel[0]] : null;
    }

    private void UpdateJoinButton()
    {
        var g = SelectedGame();
        _joinBtn.Disabled = g == null || !g.TryGetEndpoint(out _, out _)
            || g.State == "running" || g.HasPassword;
        _joinBtn.TooltipText = g is { HasPassword: true }
            ? "Passworded games are not supported yet"
            : "Connect to the selected game (needs a listed address)";
    }

    /// <summary>join 选中的注册游戏:解析 address → 写 GameLaunchConfig 的 MP 自动连接
    /// 字段 → 切 session 场景;Main.AutoMp 的 MpAutoTarget 分支直走 ENet client 连接
    /// (与手动 Connect by IP 表单同路,跳过表单)。running 局不可 join(无局中重连)。</summary>
    private void JoinSelectedGame()
    {
        var g = SelectedGame();
        if (g == null || !g.TryGetEndpoint(out string host, out int port))
        {
            AppendChatMessage(new LobbyMessage
            {
                Type = LobbyMessage.MsgType.System, Level = "error",
                Text = "Selected game has no joinable address.", Time = DateTime.Now,
            });
            return;
        }
        if (g.State == "running")
        {
            AppendChatMessage(new LobbyMessage
            {
                Type = LobbyMessage.MsgType.System, Level = "error",
                Text = "That game is already running.", Time = DateTime.Now,
            });
            return;
        }
        if (g.HasPassword)
        {
            // 我们的 ENet 直连尚无密码握手可走(原版有 server password + lobby auth token)。
            AppendChatMessage(new LobbyMessage
            {
                Type = LobbyMessage.MsgType.System, Level = "error",
                Text = "Passworded games are not supported yet.", Time = DateTime.Now,
            });
            return;
        }
        var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
        cfg.Reset();
        cfg.Mode = GameLaunchConfig.LaunchMode.Multiplayer;
        cfg.MpHost = false;
        cfg.MpAutoTarget = host;
        cfg.MpAutoPort = port;
        GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");
    }

    /// <summary>从大厅 host:走 MainMenu 同款 MP host 入口(连接表单 → StartMpHost;
    /// 大厅已连时 Main 会把房间注册进游戏列表)。</summary>
    private void HostGameFromLobby()
    {
        var cfg = GetNode<GameLaunchConfig>("/root/GameLaunchConfig");
        cfg.Reset();
        cfg.Mode = GameLaunchConfig.LaunchMode.Multiplayer;
        cfg.MpHost = true;
        GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");
    }

    public override void _ExitTree()
    {
        if (_client != null)
        {
            // 面板死 ≠ 连接死:LobbySession 持有的客户端保活(跨场景 host 注册/报告
            // 要用),只摘订阅防悬垂回调;未入会话的(断连后残留)才就地释放。
            UnsubscribeClient(_client);
            if (LobbySession.Client != _client)
                _client.Dispose();
            _client = null;
        }
        base._ExitTree();
    }
}
