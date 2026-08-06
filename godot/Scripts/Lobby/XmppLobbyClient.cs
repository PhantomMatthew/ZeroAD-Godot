using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using XmppDotNet;
using XmppDotNet.Xml;
using XmppDotNet.Xmpp;
using XmppDotNet.Xmpp.Client;

namespace ZeroAD.Godot.Lobby;

/// <summary>大厅玩家（原版 MUC roster entry）。</summary>
public sealed class LobbyPlayer
{
    public string Name = "";
    public string Presence = "available";  // available/chat/away/playing/offline
    public int Rating;
    public string Role = "participant";    // participant/moderator/visitor/none
}

/// <summary>大厅消息（原版 GUIMessage）。</summary>
public sealed class LobbyMessage
{
    public enum MsgType { System, Chat, Game }
    public MsgType Type;
    public string Level = "";    // connected/disconnected/room-message/private-message/gamelist/...
    public string From = "";
    public string Text = "";
    public DateTime Time;

    // 系统/事件字段
    public string? Nick;
    public string? OldNick;
    public string? Reason;
    public string? OldRole;
    public string? NewRole;
}

/// <summary>XMPP 大厅客户端接口（原版 XmppClient 的 JS 暴露 API）。
/// 定义全部公开方法——具体 XMPP 库（XmppDotNet）实现后续填充。
/// 当前为异步接口 + 事件队列轮询模型（与原版 pull-based 一致）。</summary>
public sealed class XmppLobbyClient : IDisposable
{
    private string _username = "";
    private string _password = "";
    private string _room = "";
    private string _nick = "";
    private bool _connected;
    private XmppClient? _client;

    // 原版 XmppClient 常量(LOBBY_SERVER / MUC 子域;StanzaExtensions 的 bot 地址)。
    private const string ServerHost = "lobby.wildfiregames.com";
    private const string MucDomain = "muc." + ServerHost;
    private const string GameListBot = "xpartamupp@" + ServerHost;
    private const string BoardListBot = "echelon@" + ServerHost;
    private string RoomJid => $"{_room}@{MucDomain}";
    private string OccupantJid => $"{RoomJid}/{_nick}";

    // 缓存
    private readonly List<LobbyPlayer> _playerList = new();
    private readonly List<LobbyGame> _gameList = new();
    private readonly List<LobbyBoardEntry> _boardList = new();
    private readonly List<LobbyMessage> _messageQueue = new();

    // 事件
    public event Action<LobbyMessage>? OnMessage;
    public event Action? OnPlayerListChanged;
    public event Action? OnGameListChanged;
    public event Action? OnConnected;
    public event Action<string>? OnDisconnected;

    public bool IsConnected => _connected;
    public string Username => _username;
    public string Nick => _nick;

    // ── 连接管理 ──

    /// <summary>启动客户端（原版 StartXmppClient）。
    /// username/password: 大厅账号
    /// room: MUC 房间名（如 "arena"）
    /// nick: 大厅显示名</summary>
    public async Task ConnectAsync(string username, string password, string room, string nick, int historySize = 20)
    {
        _username = username;
        _password = LobbyCrypto.EncryptPassword(password, username);
        _room = room;
        _nick = nick;

        _client = new XmppClient(_ => { })
        {
            // JID resource 带随机后缀(原版 0ad-<rand8>,允许同号多端)。
            Jid = new Jid($"{username}@{ServerHost}/0ad-{Guid.NewGuid().ToString("N")[..8]}"),
            Password = _password,
        };
        _subscription = _client.XmppXElementReceived.Subscribe(new StanzaObserver(OnXmppXElementReceived));

        await _client.ConnectAsync();

        // 加入 MUC 房间(XEP-0045:向 room/nick 发带 muc x 声明的 presence 即入会)。
        var joinPresence = new Presence
        {
            To = new Jid(OccupantJid),
            Muc = new XmppDotNet.Xmpp.Muc.X
            { History = new XmppDotNet.Xmpp.Muc.History { MaxStanzas = historySize } },
        };
        await _client.SendAsync(joinPresence);

        _connected = true;
        OnConnected?.Invoke();
        RequestGameList();
    }

    private IDisposable? _subscription;

    /// <summary>断开连接（原版 StopXmppClient）。</summary>
    public void Disconnect()
    {
        if (_client != null)
        {
            _subscription?.Dispose();
            _subscription = null;
            try
            {
                // 退会(unavailable presence)再断流(原版离开房间的礼貌路径)。
                if (_connected)
                    _ = _client.SendAsync(new Presence
                    { To = new Jid(OccupantJid), Type = PresenceType.Unavailable });
                _ = _client.DisconnectAsync();
            }
            catch { /* 断连异常不掩盖本地清理 */ }
            _client = null;
        }
        _connected = false;
        _playerList.Clear();
        _gameList.Clear();
        _messageQueue.Clear();
        OnDisconnected?.Invoke("disconnected");
    }

    /// <summary>IObservable 的最小订阅器(XmppDotNet 3.x 以 IObservable 暴露收包流)。</summary>
    private sealed class StanzaObserver : IObserver<XmppXElement>
    {
        private readonly Action<XmppXElement> _onNext;
        public StanzaObserver(Action<XmppXElement> onNext) => _onNext = onNext;
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(XmppXElement value) => _onNext(value);
    }

    // ── 收包分发(按元素名通用解析,不依赖 typed stanza)──

    private void OnXmppXElementReceived(XmppXElement el)
    {
        switch (el.Name.LocalName)
        {
            case "message": HandleMessage(el); break;
            case "presence": HandlePresence(el); break;
            case "iq": HandleIq(el); break;
        }
    }

    private void HandleMessage(XElement msg)
    {
        string from = msg.Attribute("from")?.Value ?? "";
        string body = msg.Element(XName.Get("body", "jabber:client"))?.Value ?? "";
        if (body.Length == 0) return;
        bool groupchat = msg.Attribute("type")?.Value == "groupchat";
        string fromNick = from.Contains('/') ? from[(from.IndexOf('/') + 1)..] : from;
        EnqueueMessage(new LobbyMessage
        {
            Type = groupchat ? LobbyMessage.MsgType.Chat : LobbyMessage.MsgType.System,
            Level = groupchat ? "room-message" : "private-message",
            From = fromNick,
            Text = body,
            Time = DateTime.Now,
        });
    }

    private void HandlePresence(XElement pres)
    {
        string from = pres.Attribute("from")?.Value ?? "";
        if (!from.StartsWith(RoomJid + "/", StringComparison.Ordinal)) return;
        string nick = from[(RoomJid.Length + 1)..];
        if (nick == _nick) return;   // 自己

        if (pres.Attribute("type")?.Value == "unavailable")
        {
            _playerList.RemoveAll(p => p.Name == nick);
            OnPlayerListChanged?.Invoke();
            return;
        }
        var player = _playerList.Find(p => p.Name == nick);
        if (player == null)
        {
            player = new LobbyPlayer { Name = nick };
            _playerList.Add(player);
        }
        player.Presence = pres.Element(XName.Get("show", "jabber:client"))?.Value ?? "available";
        var item = pres.Element(XName.Get("x", "http://jabber.org/protocol/muc#user"))
            ?.Element(XName.Get("item", "http://jabber.org/protocol/muc#user"));
        if (item?.Attribute("role") is { } role)
            player.Role = role.Value;
        OnPlayerListChanged?.Invoke();
    }

    private void HandleIq(XElement iq)
    {
        // 游戏/排行列表响应:query 子元素的命名空间区分。
        var query = iq.Elements().FirstOrDefault();
        if (query == null) return;
        if (query.Name.NamespaceName == LobbyNamespaces.GameList)
        {
            var games = new List<LobbyGame>();
            foreach (var gameElem in query.Elements())
                if (gameElem.Name.LocalName == "game")
                    games.Add(LobbyGame.FromXml(gameElem));
            UpdateGameList(games);
        }
        else if (query.Name.NamespaceName == LobbyNamespaces.BoardList)
        {
            var entries = new List<LobbyBoardEntry>();
            foreach (var item in query.Elements())
            {
                if (item.Name.LocalName != "board") continue;
                entries.Add(new LobbyBoardEntry
                {
                    Name = item.Attribute("name")?.Value ?? "",
                    Rank = int.TryParse(item.Attribute("rank")?.Value, out var r) ? r : 0,
                    Rating = int.TryParse(item.Attribute("rating")?.Value, out var rt) ? rt : 0,
                });
            }
            _boardList.Clear();
            _boardList.AddRange(entries);
        }
    }

    /// <summary>发自定义 IQ(原版 StanzaExtensions 的 Set/Get;XPartaMupp/Echelon bot 协议)。
    /// 裸元素构建——typed Iq 的 Query 强类型不适合自定义命名空间。</summary>
    private Task SendLobbyIq(string to, string type, string ns, XElement? content)
    {
        if (_client == null) return Task.CompletedTask;
        var iq = new XmppXElement(XName.Get("iq", "jabber:client"),
            new XAttribute("type", type),
            new XAttribute("to", to),
            new XAttribute("id", Guid.NewGuid().ToString("N")[..8]));
        var query = new XElement(XName.Get("query", ns));
        if (content != null) query.Add(content);
        iq.Add(query);
        return _client.SendAsync(iq);
    }

    // ── MUC 聊天 ──

    public void SendMessage(string text)
    {
        if (!_connected || _client == null) return;
        _ = _client.SendAsync(new Message
        {
            To = new Jid(RoomJid),
            Type = MessageType.GroupChat,
            Body = text,
        });
    }

    public void SendPrivateMessage(string toNick, string text)
    {
        if (!_connected || _client == null) return;
        _ = _client.SendAsync(new Message
        {
            To = new Jid($"{RoomJid}/{toNick}"),
            Type = MessageType.Chat,
            Body = text,
        });
    }

    public void SetPresence(string presence)
    {
        if (!_connected || _client == null) return;
        var pres = new Presence { To = new Jid(OccupantJid) };
        if (System.Enum.TryParse<Show>(presence, ignoreCase: true, out var show))
            pres.Show = show;
        _ = _client.SendAsync(pres);
    }

    // ── 游戏列表 IQ ──

    public void SendRegisterGame(GameRegisterData data)
    {
        if (!_connected) return;
        var content = data.ToGameXml($"{_username}@{ServerHost}");
        content.SetAttributeValue("command", "register");
        _ = SendLobbyIq(GameListBot, "set", LobbyNamespaces.GameList, content);
    }

    public void SendUnregisterGame()
    {
        if (!_connected) return;
        _ = SendLobbyIq(GameListBot, "set", LobbyNamespaces.GameList,
            new XElement("game", new XAttribute("command", "unregister")));
    }

    public void SendChangeStateGame(int nbp, string players)
    {
        if (!_connected) return;
        _ = SendLobbyIq(GameListBot, "set", LobbyNamespaces.GameList,
            new XElement("game",
                new XAttribute("command", "changestate"),
                new XAttribute("nbp", nbp),
                new XAttribute("players", players)));
    }

    public void RequestGameList()
    {
        if (!_connected) return;
        _ = SendLobbyIq(GameListBot, "get", LobbyNamespaces.GameList,
            new XElement("game", new XAttribute("command", "gamelist")));
    }

    // ── 排行榜 + 资料 IQ ──

    public void RequestBoardList()
    {
        if (!_connected) return;
        _ = SendLobbyIq(BoardListBot, "get", LobbyNamespaces.BoardList,
            new XElement("board", new XAttribute("command", "boardlist")));
    }

    public void RequestProfile(string playerName)
    {
        if (!_connected) return;
        _ = SendLobbyIq(BoardListBot, "get", LobbyNamespaces.Profile,
            new XElement("profile", new XAttribute("command", "profile"),
                new XAttribute("player", playerName)));
    }

    public void SendGameReport(Dictionary<string, object> report)
    {
        if (!_connected) return;
        var reportElem = new XElement("report", new XAttribute("command", "gamereport"));
        foreach (var kv in report)
            reportElem.SetAttributeValue(kv.Key, kv.Value?.ToString() ?? "");
        _ = SendLobbyIq(BoardListBot, "set", LobbyNamespaces.GameReport, reportElem);
    }

    // ── 缓存访问（GUI 轮询用）──

    public IReadOnlyList<LobbyPlayer> GetPlayerList() => _playerList;
    public IReadOnlyList<LobbyGame> GetGameList() => _gameList;
    public IReadOnlyList<LobbyBoardEntry> GetBoardList() => _boardList;

    /// <summary>拉取新消息（原版 LobbyGuiPollNewMessages）。</summary>
    public List<LobbyMessage> PollMessages()
    {
        var msgs = new List<LobbyMessage>(_messageQueue);
        _messageQueue.Clear();
        return msgs;
    }

    // ── 内部事件注入（供 XmppDotNet handler 回调）──

    internal void EnqueueMessage(LobbyMessage msg)
    {
        _messageQueue.Add(msg);
        OnMessage?.Invoke(msg);
    }

    internal void UpdatePlayerList(List<LobbyPlayer> players)
    {
        _playerList.Clear();
        _playerList.AddRange(players);
        OnPlayerListChanged?.Invoke();
    }

    internal void UpdateGameList(List<LobbyGame> games)
    {
        _gameList.Clear();
        _gameList.AddRange(games);
        OnGameListChanged?.Invoke();
    }

    public void Dispose()
    {
        Disconnect();
    }
}
