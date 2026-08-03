using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace ZeroAD.Godot.Lobby;

/// <summary>大厅游戏列表项（原版 GameListQuery 的 game 属性）。</summary>
public sealed class LobbyGame
{
    public string Name = "";
    public string HostUsername = "";
    public string HostJID = "";
    public string State = "";       // init/waiting/running
    public bool HasPassword;
    public int Nbp;                 // current players
    public int MaxNbp;              // max players
    public string Players = "";     // comma-separated player names
    public string MapName = "";
    public string NiceMapName = "";
    public string MapSize = "";
    public string MapType = "";
    public string VictoryConditions = "";
    public long StartTime;
    public string Mods = "";

    /// <summary>从 XML game 元素解析（原版 GameListQuery::GameList 的 game tag 属性）。</summary>
    public static LobbyGame FromXml(XElement gameElem)
    {
        var g = new LobbyGame();
        foreach (var attr in gameElem.Attributes())
        {
            switch (attr.Name.LocalName)
            {
                case "name": g.Name = attr.Value; break;
                case "hostUsername": g.HostUsername = attr.Value; break;
                case "hostJID": g.HostJID = attr.Value; break;
                case "state": g.State = attr.Value; break;
                case "hasPassword": g.HasPassword = attr.Value == "true"; break;
                case "nbp": g.Nbp = int.Parse(attr.Value); break;
                case "maxnbp": g.MaxNbp = int.Parse(attr.Value); break;
                case "players": g.Players = attr.Value; break;
                case "mapName": g.MapName = attr.Value; break;
                case "niceMapName": g.NiceMapName = attr.Value; break;
                case "mapSize": g.MapSize = attr.Value; break;
                case "mapType": g.MapType = attr.Value; break;
                case "victoryConditions": g.VictoryConditions = attr.Value; break;
                case "startTime": g.StartTime = long.Parse(attr.Value); break;
                case "mods": g.Mods = attr.Value; break;
            }
        }
        return g;
    }
}

/// <summary>排行榜条目（原版 BoardListQuery）。</summary>
public sealed class LobbyBoardEntry
{
    public string Name = "";
    public int Rank;
    public int Rating;
}

/// <summary>玩家资料（原版 ProfileQuery）。</summary>
public sealed class LobbyProfile
{
    public string Player = "";
    public int Rating;
    public int TotalGamesPlayed;
    public int HighestRating;
    public int Wins;
    public int Losses;
    public int Rank;
}

/// <summary>自定义 IQ 命名空间常量（原版 StanzaExtensions.h）。</summary>
public static class LobbyNamespaces
{
    public const string GameList = "jabber:iq:gamelist";
    public const string BoardList = "jabber:iq:boardlist";
    public const string GameReport = "jabber:iq:gamereport";
    public const string Profile = "jabber:iq:profile";
    public const string LobbyAuth = "jabber:iq:lobbyauth";
    public const string ConnectionData = "jabber:iq:connectiondata";
}

/// <summary>游戏注册数据（原版 SendRegisterGame 的 data 参数）。</summary>
public sealed class GameRegisterData
{
    public string Name = "";
    public string MapName = "";
    public string NiceMapName = "";
    public int MaxNbp;
    public string MapSize = "";
    public string MapType = "";
    public string VictoryConditions = "";
    public string Mods = "";
    public bool HasPassword;
    public string Password = "";

    /// <summary>生成 XML game 元素（原版 GameListQuery::RegisterGame 的 XML 构建）。</summary>
    public XElement ToGameXml(string hostJID)
    {
        var attrs = new List<XAttribute>
        {
            new("name", Name),
            new("hostUsername", hostJID.Split('@')[0]),
            new("hostJID", hostJID),
            new("mapName", MapName),
            new("niceMapName", NiceMapName),
            new("mapSize", MapSize),
            new("mapType", MapType),
            new("victoryConditions", VictoryConditions),
            new("mods", Mods),
            new("maxnbp", MaxNbp.ToString()),
            new("hasPassword", HasPassword ? "true" : "false"),
        };
        return new XElement("game", attrs);
    }
}

/// <summary>连接数据（原版 ConnectionData IQ——NAT 穿透的 IP/port/密码交换）。</summary>
public sealed class ConnectionDataIQ
{
    public string IP = "";
    public int Port;
    public string Password = "";
    public string Salt = "";
    public string Error = "";

    public bool IsError => Error.Length > 0;
}
