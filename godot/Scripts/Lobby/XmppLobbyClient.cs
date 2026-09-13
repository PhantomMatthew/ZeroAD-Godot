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
    /// <summary>排行榜到达(BoardList IQ 应答解析后)。</summary>
    public event Action? OnBoardListChanged;
    /// <summary>资料应答到达(Profile IQ;Echelon 对在线/离线玩家都回)。</summary>
    public event Action<LobbyProfile>? OnProfileReceived;
    public event Action? OnConnected;
    public event Action<string>? OnDisconnected;

    public bool IsConnected => _connected;
    public string Username => _username;
    public string Nick => _nick;
    /// <summary>自己在 MUC 的角色(self-presence 的 muc#user item role;
    /// participant/moderator/visitor)。原版依此显隐 kick/ban。</summary>
    public string SelfRole { get; private set; } = "participant";
    /// <summary>是否房间 moderator(XEP-0045 role=moderator → kick/ban 菜单可见)。</summary>
    public bool IsModerator => SelfRole == "moderator";

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
        SelfRole = "participant";
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

        // muc#user 扩展:item(role/affiliation) + status(301=banned/307=kicked,XEP-0045)。
        var mucUser = pres.Element(XName.Get("x", "http://jabber.org/protocol/muc#user"));
        var item = mucUser?.Element(XName.Get("item", "http://jabber.org/protocol/muc#user"));
        string? role = item?.Attribute("role")?.Value;

        if (nick == _nick)
        {
            // 自己的 presence:只跟踪自身 role(moderator 判定;原版 m_PlayerMap 同款)。
            if (role != null) SelfRole = role;
            if (pres.Attribute("type")?.Value == "unavailable")
                ReportRemovedFromRoom(mucUser, nick);
            return;
        }

        if (pres.Attribute("type")?.Value == "unavailable")
        {
            ReportRemovedFromRoom(mucUser, nick);
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
        if (role != null)
            player.Role = role;
        OnPlayerListChanged?.Invoke();
    }

    /// <summary>unavailable presence 里的踢出/封禁系统消息(原版
    /// handleMUCParticipantPresence 的 UserKicked/UserBanned 分支:status 307/301 + reason)。</summary>
    private void ReportRemovedFromRoom(XElement? mucUser, string nick)
    {
        if (mucUser == null) return;
        string? code = null;
        foreach (var st in mucUser.Elements(XName.Get("status", "http://jabber.org/protocol/muc#user")))
            if (st.Attribute("code")?.Value is { Length: > 0 } c) { code = c; break; }
        if (code != "307" && code != "301") return;
        string reason = mucUser.Element(XName.Get("item", "http://jabber.org/protocol/muc#user"))
            ?.Element(XName.Get("reason", "http://jabber.org/protocol/muc#user"))?.Value ?? "";
        EnqueueMessage(new LobbyMessage
        {
            Type = LobbyMessage.MsgType.System,
            Level = code == "307" ? "kicked" : "banned",
            Nick = nick,
            Reason = reason,
            Text = reason.Length > 0
                ? $"{nick} was {(code == "307" ? "kicked" : "banned")}. Reason: {reason}"
                : $"{nick} was {(code == "307" ? "kicked" : "banned")}.",
            Time = DateTime.Now,
        });
    }

    private void HandleIq(XElement iq)
    {
        // 游戏/排行/资料列表响应:query 子元素的命名空间区分。
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
            // command 子元素区分应答种类(Echelon):"boardlist" = 全榜应答;
            // "ratinglist" = 在线玩家评分广播(进大厅/评分变动时推送,刷花名册评分)。
            string command = query.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "command")?.Value ?? "";
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
            if (command == "ratinglist")
            {
                // 评分按 nick 并入花名册(原版 ratinglist 语义;榜本身不动)。
                bool changed = false;
                foreach (var e in entries)
                {
                    var p = _playerList.Find(pl => pl.Name == e.Name);
                    if (p != null && p.Rating != e.Rating) { p.Rating = e.Rating; changed = true; }
                }
                if (changed) OnPlayerListChanged?.Invoke();
                return;
            }
            _boardList.Clear();
            _boardList.AddRange(entries);
            OnBoardListChanged?.Invoke();
        }
        else if (query.Name.NamespaceName == LobbyNamespaces.Profile)
        {
            var profileElem = query.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "profile");
            if (profileElem != null)
                OnProfileReceived?.Invoke(LobbyProfile.FromXml(profileElem));
        }
    }

    /// <summary>发自定义 IQ(原版 StanzaExtensions 的 GameListQuery/BoardListQuery/
    /// ProfileQuery::tag():query 内 command 子元素文本承载命令,负载元素
    /// (game/board/profile)全部属性化;子元素一律带命名空间限定名,否则 LINQ-XML
    /// 序列化补 xmlns="" 会把它们踢出默认命名空间,bot 端 find("{ns}game") 落空)。
    /// 裸元素构建——typed Iq 的 Query 强类型不适合自定义命名空间。</summary>
    private Task SendLobbyIq(string to, string type, string ns, XElement? content, string? command = null)
    {
        if (_client == null) return Task.CompletedTask;
        var iq = new XmppXElement(XName.Get("iq", "jabber:client"),
            new XAttribute("type", type),
            new XAttribute("to", to),
            new XAttribute("id", Guid.NewGuid().ToString("N")[..8]));
        var query = new XElement(XName.Get("query", ns));
        if (command != null)
            query.Add(new XElement(XName.Get("command", ns)) { Value = command });
        if (content != null) query.Add(content);
        iq.Add(query);
        return _client.SendAsync(iq);
    }

    /// <summary>负载直挂 iq 的自定义 IQ(原版 GameReport::tag():report 元素是 iq 的
    /// 直接子级,不包 query;Echelon 按 iq@type=set/gamereport 匹配)。</summary>
    private Task SendRawIq(string to, string type, XElement payload)
    {
        if (_client == null) return Task.CompletedTask;
        var iq = new XmppXElement(XName.Get("iq", "jabber:client"),
            new XAttribute("type", type),
            new XAttribute("to", to),
            new XAttribute("id", Guid.NewGuid().ToString("N")[..8]));
        iq.Add(payload);
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

    // ── 游戏列表 IQ(原版 SendIqRegisterGame/UnregisterGame/ChangeStateGame;
    // command 为 query 的 command 子元素文本,bot 读 iq["gamelist"]["command"])──

    public void SendRegisterGame(GameRegisterData data)
    {
        if (!_connected) return;
        var content = data.ToGameXml($"{_username}@{ServerHost}");
        _ = SendLobbyIq(GameListBot, "set", LobbyNamespaces.GameList, content, "register");
    }

    public void SendUnregisterGame()
    {
        if (!_connected) return;
        _ = SendLobbyIq(GameListBot, "set", LobbyNamespaces.GameList,
            new XElement(XName.Get("game", LobbyNamespaces.GameList)), "unregister");
    }

    public void SendChangeStateGame(int nbp, string players)
    {
        if (!_connected) return;
        _ = SendLobbyIq(GameListBot, "set", LobbyNamespaces.GameList,
            new XElement(XName.Get("game", LobbyNamespaces.GameList),
                new XAttribute("nbp", nbp),
                new XAttribute("players", players)),
            "changestate");
    }

    public void RequestGameList()
    {
        if (!_connected) return;
        _ = SendLobbyIq(GameListBot, "get", LobbyNamespaces.GameList, null, "gamelist");
    }

    // ── 排行榜 + 资料 IQ ──

    public void RequestBoardList()
    {
        if (!_connected) return;
        // 原版 SendIqGetBoardList:command = "getleaderboard"(子元素文本,无 board 负载)。
        _ = SendLobbyIq(BoardListBot, "get", LobbyNamespaces.BoardList, null, "getleaderboard");
    }

    public void RequestProfile(string playerName)
    {
        if (!_connected) return;
        // 原版 SendIqGetProfile:command 子元素文本 = 玩家 nick(无 profile 负载)。
        _ = SendLobbyIq(BoardListBot, "get", LobbyNamespaces.Profile, null, playerName);
    }

    /// <summary>对局报告(原版 SendIqGameReport + LobbyRatingReporter.js 字段):
    /// iq/set → echelon,report 直挂 iq,内含单个 game 元素,报告键值全部属性化。</summary>
    public void SendGameReport(Dictionary<string, object> report)
    {
        if (!_connected) return;
        var gameElem = new XElement(XName.Get("game", LobbyNamespaces.GameReport));
        foreach (var kv in report)
            gameElem.SetAttributeValue(kv.Key, kv.Value?.ToString() ?? "");
        _ = SendRawIq(BoardListBot, "set",
            new XElement(XName.Get("report", LobbyNamespaces.GameReport), gameElem));
    }

    // ── MUC 管理(XEP-0045 §8.2 kick / §9.1 ban;原版 XmppClient::kick/ban →
    // gloox MUCRoom::kick/ban 所发的就是这两条 muc#admin IQ)──

    /// <summary>把 nick 踢出房间(role → none)。仅 moderator 发出才有效(服务端强校验)。</summary>
    public void KickOccupant(string nick, string reason)
        => SendMucAdminIq(nick, reason, role: "none");

    /// <summary>把 nick 封禁(affiliation → outcast)。需 moderator/admin 权限。</summary>
    public void BanOccupant(string nick, string reason)
        => SendMucAdminIq(nick, reason, affiliation: "outcast");

    private Task SendMucAdminIq(string nick, string reason, string? role = null, string? affiliation = null)
    {
        if (!_connected) return Task.CompletedTask;
        const string adminNs = "http://jabber.org/protocol/muc#admin";
        var item = new XElement(XName.Get("item", adminNs), new XAttribute("nick", nick));
        if (role != null) item.SetAttributeValue("role", role);
        if (affiliation != null) item.SetAttributeValue("affiliation", affiliation);
        if (reason.Length > 0)
            item.Add(new XElement(XName.Get("reason", adminNs)) { Value = reason });
        return SendLobbyIq(RoomJid, "set", adminNs, item);
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
        // 无面板订阅时消息也会在队列里累积(大厅面板关闭但 LobbySession 保持连接)——
        // 封顶防无限增长;事件仍照常分发。
        if (_messageQueue.Count >= 500)
            _messageQueue.RemoveAt(0);
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
