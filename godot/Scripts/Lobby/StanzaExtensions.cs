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
    /// <summary>可直连地址("ip:port";本端注册扩展——原版经 ConnectionData IQ 交换,
    /// 注册 stanza 本不带;XPartaMuPP 会剥离 ip 属性,故用自定义 "address" 属性承载,
    /// 未知名称随 bot 广播透传)。"" = 列表项无地址(不可 join)。</summary>
    public string Address = "";

    /// <summary>从 XML game 元素解析（原版 GameListQuery::GameList 的 game tag 属性）。</summary>
    public static LobbyGame FromXml(XElement gameElem)
    {
        var g = new LobbyGame();
        string ip = "", port = "";
        foreach (var attr in gameElem.Attributes())
        {
            switch (attr.Name.LocalName)
            {
                case "name": g.Name = attr.Value; break;
                case "hostUsername": g.HostUsername = attr.Value; break;
                case "hostJID": g.HostJID = attr.Value; break;
                case "state": g.State = attr.Value; break;
                case "hasPassword": g.HasPassword = attr.Value == "true"; break;
                case "nbp": g.Nbp = int.TryParse(attr.Value, out var nbp) ? nbp : 0; break;
                case "maxnbp": g.MaxNbp = int.TryParse(attr.Value, out var mnbp) ? mnbp : 0; break;
                case "players": g.Players = attr.Value; break;
                case "mapName": g.MapName = attr.Value; break;
                case "niceMapName": g.NiceMapName = attr.Value; break;
                case "mapSize": g.MapSize = attr.Value; break;
                case "mapType": g.MapType = attr.Value; break;
                case "victoryConditions": g.VictoryConditions = attr.Value; break;
                case "startTime": g.StartTime = long.TryParse(attr.Value, out var st) ? st : 0; break;
                case "mods": g.Mods = attr.Value; break;
                case "address": g.Address = attr.Value; break;
                // ip/port 分置形式的兜底解析(ConnectionData 词汇;真实 bot 广播会剥 ip,
                // 正常到不了这里,仅作健壮性兜底)。
                case "ip": ip = attr.Value; break;
                case "port": port = attr.Value; break;
            }
        }
        if (g.Address.Length == 0 && ip.Length > 0)
            g.Address = port.Length > 0 ? $"{ip}:{port}" : ip;
        return g;
    }

    /// <summary>解析可直连地址("ip:port" → host + port)。false = 无地址/格式坏。</summary>
    public bool TryGetEndpoint(out string host, out int port)
    {
        host = "";
        port = 0;
        int sep = Address.LastIndexOf(':');
        if (sep <= 0 || sep == Address.Length - 1) return false;
        host = Address[..sep];
        return int.TryParse(Address[(sep + 1)..], out port) && port > 0 && port <= 65535;
    }
}

/// <summary>排行榜条目（原版 BoardListQuery）。</summary>
public sealed class LobbyBoardEntry
{
    public string Name = "";
    public int Rank;
    public int Rating;
}

/// <summary>玩家资料（原版 ProfileQuery;Echelon 应答的 profile 元素属性）。
/// rating="-2" = 玩家未知;rating/highestRating/rank 可能为 "-"（未评级,解析为 -1）。</summary>
public sealed class LobbyProfile
{
    public string Player = "";
    public int Rating;
    public int TotalGamesPlayed;
    public int HighestRating;
    public int Wins;
    public int Losses;
    public int Rank;

    /// <summary>Echelon 对未知玩家回 rating=-2(其余字段缺省 0)。</summary>
    public bool Known => Rating != -2;

    /// <summary>从 profile 元素解析(属性缺失/非数值 → -1,UI 显示 "-")。</summary>
    public static LobbyProfile FromXml(XElement profileElem)
    {
        static int ParseInt(XElement e, string name)
            => int.TryParse(e.Attribute(name)?.Value, out var v) ? v : -1;
        return new LobbyProfile
        {
            Player = profileElem.Attribute("player")?.Value ?? "",
            Rating = ParseInt(profileElem, "rating"),
            HighestRating = ParseInt(profileElem, "highestRating"),
            Rank = ParseInt(profileElem, "rank"),
            TotalGamesPlayed = ParseInt(profileElem, "totalGamesPlayed"),
            Wins = ParseInt(profileElem, "wins"),
            Losses = ParseInt(profileElem, "losses"),
        };
    }
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

/// <summary>游戏注册数据（原版 SendRegisterGame 的 data 参数,
/// gui/gamesetup/Controllers/LobbyGameRegistration.js 的 stanza 对象）。</summary>
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
    /// <summary>当前已连接玩家数(原版 nbp;XPartaMuPP add_game 必填,缺了注册被拒)。</summary>
    public int Nbp = 1;
    /// <summary>当前玩家名单(逗号分隔;原版为 team-list JSON,此处简化——见 Players 字段)。</summary>
    public string Players = "";
    /// <summary>可直连地址 "ip:port"(STUN 探测结果;本端扩展属性,原版走 ConnectionData IQ)。</summary>
    public string Address = "";

    /// <summary>生成 XML game 元素（原版 GameListQuery::RegisterGame 的 XML 构建:
    /// game 元素全部属性化;元素名带 gamelist 命名空间,避免序列化出 xmlns="" 把
    /// 子元素踢出默认命名空间导致 bot 解析不到）。</summary>
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
            new("nbp", Nbp.ToString()),
            new("maxnbp", MaxNbp.ToString()),
            new("players", Players),
            // 原版 hasPassword || "":无密码发空串(JS 里 "false" 是 truthy,会误显锁图标)。
            new("hasPassword", HasPassword ? "true" : ""),
        };
        if (Address.Length > 0)
            attrs.Add(new XAttribute("address", Address));
        return new XElement(XName.Get("game", LobbyNamespaces.GameList), attrs);
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
