using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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
    /// nick: 大厅显示名
    /// TODO: 实际 XMPP 连接逻辑——需安装 XmppDotNet NuGet 后填充。</summary>
    public async Task ConnectAsync(string username, string password, string room, string nick, int historySize = 20)
    {
        _username = username;
        _password = LobbyCrypto.EncryptPassword(password, username);
        _room = room;
        _nick = nick;

        // TODO: XmppDotNet 实际连接
        // var xmppClient = new XmppClient(new Jid(username + "@lobby.wildfiregames.com/0ad-" + Guid.NewGuid().ToString("N").Substring(0, 8)), password);
        // xmppClient.Connect();
        // 加入 MUC 房间
        // 请求玩家列表 + 游戏列表

        _connected = true;
        OnConnected?.Invoke();
    }

    /// <summary>断开连接（原版 StopXmppClient）。</summary>
    public void Disconnect()
    {
        _connected = false;
        _playerList.Clear();
        _gameList.Clear();
        _messageQueue.Clear();
        OnDisconnected?.Invoke("disconnected");
    }

    // ── MUC 聊天 ──

    public void SendMessage(string text)
    {
        if (!_connected) return;
        // TODO: XmppDotNet MUC room.SendMessage(text)
    }

    public void SendPrivateMessage(string toNick, string text)
    {
        if (!_connected) return;
        // TODO: XmppDotNet 私信
    }

    public void SetPresence(string presence)
    {
        if (!_connected) return;
        // TODO: XmppDotNet MUC room 设置 presence
    }

    // ── 游戏列表 IQ ──

    public void SendRegisterGame(GameRegisterData data)
    {
        if (!_connected) return;
        // TODO: 发 IQ 到 XPartaMupp bot (jabber:iq:gamelist, command="register")
    }

    public void SendUnregisterGame()
    {
        if (!_connected) return;
        // TODO: 发 IQ (command="unregister")
    }

    public void SendChangeStateGame(int nbp, string players)
    {
        if (!_connected) return;
        // TODO: 发 IQ (command="changestate")
    }

    public void RequestGameList()
    {
        if (!_connected) return;
        // TODO: 发 IQ (command="gamelist")
    }

    // ── 排行榜 + 资料 IQ ──

    public void RequestBoardList()
    {
        if (!_connected) return;
        // TODO: 发 IQ 到 Echelon bot (jabber:iq:boardlist)
    }

    public void RequestProfile(string playerName)
    {
        if (!_connected) return;
        // TODO: 发 IQ (jabber:iq:profile)
    }

    public void SendGameReport(Dictionary<string, object> report)
    {
        if (!_connected) return;
        // TODO: 发 IQ (jabber:iq:gamereport)
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
