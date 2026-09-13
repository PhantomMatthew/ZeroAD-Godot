namespace ZeroAD.Godot.Lobby;

/// <summary>进程级大厅会话持有者(原版 g_XmppClient 全局变量的等价物)。
/// XmppLobbyPanel 登录成功后挂入;跨场景(MainMenu ↔ session)存活——host 注册
/// (SendRegisterGame)、对局报告(SendGameReport)、大厅 join 都经它触达同一个
/// XMPP 连接。XmppLobbyPanel 关闭只摘事件订阅,不断连(原版离开大厅页保持在线);
/// 显式 Disconnect 才摘除并释放。本地直连局(未登录大厅)IsConnected 恒 false,
/// 注册/报告路径全部跳过。</summary>
public static class LobbySession
{
    public static XmppLobbyClient? Client { get; private set; }

    public static bool IsConnected => Client?.IsConnected == true;

    /// <summary>登记为当前大厅会话(重复登记同实例为 no-op)。</summary>
    public static void Attach(XmppLobbyClient client) => Client = client;

    /// <summary>摘除(仅当持有者就是该实例;断连/登出时调)。</summary>
    public static void Detach(XmppLobbyClient client)
    {
        if (Client == client) Client = null;
    }
}
