using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ZeroAD.Godot;

// Autostart — 原版 binaries/data/mods/public/autostart/(cmd_line_args.js + autostart.js +
// autostart_host.js/autostart_client.js)的 CLI 端口。CI/自动化测试入口:
//   ZeroAD-Godot -autostart="random/alpine_lakes" -autostart-seed=-1 -autostart-players=2 \
//     -autostart-civ=1:athen -autostart-civ=2:brit -autostart-ai=2:petra
// 语义照抄上游(见 cmd_line_args.js 头注释;补 -autostart-host/-autostart-client=IP 的 MP 分支)。
// 返回值 true = 已写 GameLaunchConfig,调用方直接切 session 场景。
public static class Autostart
{
    /// <summary>解析命令行(OS.GetCmdlineArgs + GetCmdlineUserArgs);无 -autostart/-autostart-client
    /// 返回 false(原版 Autostart() 同款门槛)。</summary>
    public static bool TryApply(GameLaunchConfig cfg, UserConfig userCfg)
    {
        var args = ParseArgs(OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()));
        if (!args.ContainsKey("autostart") && !args.ContainsKey("autostart-client"))
            return false;

        cfg.Reset();
        string playerName = Get(args, "autostart-playername") ?? "anonymous";
        userCfg.SetUserValue("playername", playerName);

        // ── MP 分支(原版 autostart_client.js/autostart_host.js)──
        if (args.ContainsKey("autostart-client"))
        {
            cfg.Mode = GameLaunchConfig.LaunchMode.Multiplayer;
            cfg.MpHost = false;
            cfg.MpAutoTarget = Get(args, "autostart-client") ?? "127.0.0.1";
            cfg.MpAutoPort = GetInt(args, "autostart-port", 0);
            return true;
        }

        // ── 地图(原版:autostart="TYPEDIR/MAPNAME";TYPEDIR ∈ random/scenarios/skirmishes)──
        string mapArg = Get(args, "autostart") ?? "";
        string mapType = mapArg.Contains('/') ? mapArg[..mapArg.IndexOf('/')] : "";
        if (mapType is not ("random" or "scenarios" or "skirmishes"))
        {
            ZeroAD.Sim.Diag.Err("Autostart", $"unknown map type in -autostart=\"{mapArg}\"");
            return false;
        }
        cfg.MapPath = mapType == "random"
            ? "random/" + mapArg[(mapType.Length + 1)..]
            : $"maps/{mapArg}.pmp";

        // ── 玩家表(原版默认 random 2 人;slot 1 = 本地人类,其余 AI 或按 -autostart-ai 指定)──
        int players = GetInt(args, "autostart-players", 2);
        int localPlayer = GetInt(args, "autostart-player", 1);
        if (localPlayer == -1)
            ZeroAD.Sim.Diag.Log("Autostart", "-autostart-player=-1 (observer) 未支持,按玩家 1 处理");
        var slots = new List<ZeroAD.Sim.Net.PlayerSlotSetup>(players);
        for (int i = 1; i <= players; i++)
        {
            bool human = i == localPlayer;
            var civs = GetAll(args, "autostart-civ");
            string civ = civs.FirstOrDefault(v => PlayerOf(v) == i)?.Substring(2) ?? "random";
            slots.Add(new ZeroAD.Sim.Net.PlayerSlotSetup
            {
                PlayerId = i,
                // 原版:未指定 -autostart-ai 的非本地槽在 GameSettings 默认下也是 AI。
                Kind = human ? ZeroAD.Sim.Net.PlayerSlotKind.Human : ZeroAD.Sim.Net.PlayerSlotKind.AI,
                Civ = civ,
                Team = GetAll(args, "autostart-team").FirstOrDefault(v => PlayerOf(v) == i) is { } t
                    && int.TryParse(t.Substring(2), out int team) ? team - 1 : -1,
            });
            if (GetAll(args, "autostart-aidiff").FirstOrDefault(v => PlayerOf(v) == i) is { } d
                && int.TryParse(d.Substring(2), out int diff))
                cfg.AiDifficulties[i] = diff;
        }
        cfg.Slots = slots;

        // ── 种子(默认 0;-1 = 随机,原版同款)──
        cfg.Seed = Get(args, "autostart-seed") is { } s && s != "-1"
            ? (uint)GetInt(args, "autostart-seed", 0)
            : (uint)GD.Randi();

        // ── 可见性(explored/hidden/revealed/allied/allied-explored)──
        switch (Get(args, "autostart-visibility"))
        {
            case "revealed": cfg.AlliedView = true; cfg.ExploredMap = true; cfg.RevealedMap = true; break;
            case "explored": cfg.ExploredMap = true; break;
            case "hidden": break;
            case "allied": cfg.AlliedView = true; break;
            case "allied-explored": cfg.AlliedView = true; cfg.ExploredMap = true; break;
            case null: break;
            default:
                ZeroAD.Sim.Diag.Log("Autostart",
                    $"unknown -autostart-visibility: {Get(args, "autostart-visibility")}");
                break;
        }

        // ── random 图专属 ──
        if (mapType == "random")
        {
            cfg.MapSize = GetInt(args, "autostart-size", 0);           // 0 = 不改(默认 192)
            cfg.BiomeId = Get(args, "autostart-biome") ?? "";
            cfg.PlayerPlacement = Get(args, "autostart-placement") ?? "";
        }

        cfg.GameSpeed = Get(args, "autostart-speed") is { } sp && float.TryParse(sp, out float f) ? f : 0;
        cfg.CeasefireMinutes = GetInt(args, "autostart-ceasefire", 0);

        // ── 胜利条件(可重复;"endless" = 无)──
        var victories = GetAll(args, "autostart-victory");
        if (victories.Count > 0 && !(victories.Count == 1 && victories[0] == "endless"))
            cfg.VictoryConditions = victories;

        // SP autostart 默认可作弊(原版 autostart.js:settings.cheats.setEnabled(true))。
        cfg.Cheats = true;

        // ── MP host 分支(-autostart-host)──
        if (args.ContainsKey("autostart-host"))
        {
            cfg.Mode = GameLaunchConfig.LaunchMode.Multiplayer;
            cfg.MpHost = true;
            cfg.MpAutoTarget = "host";
            cfg.MpAutoPort = GetInt(args, "autostart-port", 0);
            cfg.MpAutoHostPlayers = GetInt(args, "autostart-host-players", 0);
            return true;
        }

        cfg.Mode = GameLaunchConfig.LaunchMode.SinglePlayer;
        ZeroAD.Sim.Diag.Log("Autostart",
            $"map={cfg.MapPath} seed={cfg.Seed} players={players} localP={localPlayer}");
        return true;
    }

    /// <summary>"1:athen" → 玩家号(原版 getPlayer 取 value[0];两位以上玩家号需 substring(2) 语义,
    /// 此处用 ':' 拆分更稳,行为对 1..8 一致)。</summary>
    private static int PlayerOf(string pair)
    {
        int colon = pair.IndexOf(':');
        return colon > 0 && int.TryParse(pair[..colon], out int p) ? p : -1;
    }

    // ── argv 解析:-key=value / -key value / 重复 key 收数组(原版 CmdLineArgs 同款)──
    private static Dictionary<string, List<string>> ParseArgs(IEnumerable<string> argv)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? pending = null;
        foreach (var raw in argv)
        {
            if (!raw.StartsWith('-'))
            {
                if (pending != null)
                {
                    Add(result, pending, raw);
                    pending = null;
                }
                continue;
            }
            string arg = raw.TrimStart('-');
            int eq = arg.IndexOf('=');
            if (eq >= 0)
            {
                Add(result, arg[..eq], arg[(eq + 1)..].Trim('"'));
                pending = null;
            }
            else
                pending = arg;   // 可能无值(开关)或下 token 为值
        }
        if (pending != null)
            Add(result, pending, "true");   // 裸开关(原版 args.Has 语义)
        return result;
    }

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
        list.Add(value);
    }

    private static string? Get(Dictionary<string, List<string>> args, string key) =>
        args.TryGetValue(key, out var v) ? v[0] : null;

    private static List<string> GetAll(Dictionary<string, List<string>> args, string key) =>
        args.TryGetValue(key, out var v) ? v : new List<string>();

    private static int GetInt(Dictionary<string, List<string>> args, string key, int dflt) =>
        args.TryGetValue(key, out var v) && int.TryParse(v[0], out int n) ? n : dflt;
}
