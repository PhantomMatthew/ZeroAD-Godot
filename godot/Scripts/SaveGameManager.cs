using System.Collections.Generic;
using System.IO;
using Godot;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Net;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Godot;

/// <summary>Save header metadata — everything needed to rebuild the match skeleton on a
/// cold (cross-scene) load, plus display info for the LoadGame browser. Written between the
/// turn field and the payload in the v6 header.</summary>
public sealed record SaveMeta(
    string Slot,
    long TimeUnix,
    string Description,
    string? MapPath,
    string MapType,
    uint Turn,
    bool Tutorial,
    uint LocalPlayerId,
    NetRole Role,
    IReadOnlyList<PlayerSlotSetup> Slots);

/// <summary>
/// Save/load system: serializes the full simulation state to a binary file and
/// restores it on load. Files land in the user data directory under "saves/".
///
/// Save format (little-endian binary):
///   magic   "0ADSAVE" (7 bytes)
///   version uint32    (format version, currently 6)
///   turn    uint32    (sim turn at save time)
///   match-skeleton block (v6): mapPath/mapType/tutorial/localPlayerId/role/slots/timeUnix/description
///   payload            (ComponentManager.SerializeSaveGame output)
///
/// On load the caller (Main) must rebuild visual nodes + spatial indexes + the player
/// registry for every restored entity (cold load) — see Main.AutoLoad.
/// </summary>
public static class SaveGameManager
{
    private const string Magic = "0ADSAVE";
    // v2(2026-07-29):HealthComponent 增 Unhealable 字段 + HealComponent 增计时器字段(MS5)。
    // v3(2026-07-30):单位捕获——DamageBlock.Capture int→Fixed、AttackComponent 增 Capture
    // 攻击类型六字段、UnitOrder 增 AllowCapture。旧档位置流错位,加载方按版本号拒收。
    // v4(2026-07-31):Capturable 增 BaseMaxCapturePoints(bmax)模板基值——Capturable/CapturePoints
    // 科技修正(×1.4 等)按比例缩放 CP 数组;旧 v3 档无 bmax,按版本号拒收。
    // v5(2026-08-01):PlayerComponent 增 Team 字段(队伍号,外交面板显示用)——PlayerComponent
    // 序列化流多一个 int32;旧 v4 档位置流错位,按版本号拒收。
    // v6(2026-08-01):会话外 LoadGame 冷启动——turn 后 payload 前嵌对局骨架(mapPath/mapType/
    // tutorial/localPlayerId/role/slots/timeUnix/description),跨场景冷加载用同一份槽位表+地图
    // 重建世界;payload 同步增 PlayerManager 注册表(pid→entity)序列化,冷加载后玩家映射指向
    // 存活实体而非已销毁者。旧 v5 档无此块(读骨架时位置流错位),按版本号拒收。
    // v7(2026-08-02):UnitAI 增 stance 负载(stance/heldPosition/stanceScan)——站姿系统落地
    // (g_Stances 九 flag + 受击响应 + 驻防锚点),序列化流尾部多 1 字符串+1 bool+3 Fixed;
    // 旧 v6 档位置流错位,按版本号拒收。
    // v8(2026-08-02):ProductionQueue 增 TrainableTokens+NativeCiv(训练列表数据驱动,
    // 原版 Trainer/Entities)——组件流 count/progress 后多 2 个 ASCII 字符串;旧 v7 档
    // 位置流错位,按版本号拒收。
    // v9(2026-08-03):DamageBlock 增 Fire 通道(状态效果燃烧)——capture 后多 1 个 I32。
    // v10(2026-08-07):Foundation/Builder 工人表序列化(多工人递减 n^0.7/n)。
    private const uint Version = 17; // v17: QueuePlan.GoRequirement(houseNeeded 启动门) // v16: waypoints // v15: 转向物理 // v14: UnitMotion 增推挤 Weight // v13: 槽位增 AI 难度/性格 // v12: AIComponent 增 HQ 尾段(AI 计划/队列/攻防军/运输骑缝)

    private static string SavesDir => ProjectSettings.GlobalizePath("user://saves/");

    public static string SavePath(string slot) =>
        Path.Combine(SavesDir, $"{slot}.zsave");

    /// <summary>Serializes the current simulation state to a save file. Returns the
    /// file path on success, null on failure.</summary>
    public static string? Save(SimBridge sim, string slot = "quicksave", string? description = null)
    {
        try
        {
            Directory.CreateDirectory(SavesDir);
            string path = SavePath(slot);
            using var fs = new FileStream(path, FileMode.Create);
            using var bw = new BinaryWriter(fs);
            // Header
            foreach (char c in Magic)
                bw.Write((byte)c);
            bw.Write(Version);
            bw.Write(sim.NetTurn.CurrentTurn);
            // v6 match-skeleton block (cold-load contract + browser display info)
            bw.Write(sim.MapPath ?? string.Empty);
            bw.Write(MapTypeOf(sim));
            bw.Write((byte)(sim.IsTutorialMode ? 1 : 0));
            bw.Write(sim.LocalPlayerId);
            bw.Write((byte)sim.NetTurn.Role);
            var slots = sim.Slots;
            bw.Write((byte)slots.Count);
            foreach (var s in slots)
            {
                bw.Write((byte)s.Kind);
                bw.Write(s.Civ ?? string.Empty);
                bw.Write(s.Team);
                // v13:AI 难度/性格随槽骑缝。
                bw.Write(s.AIDifficulty);
                bw.Write(s.AIBehavior ?? string.Empty);
            }
            bw.Write(System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            bw.Write(description ?? $"Turn {sim.NetTurn.CurrentTurn}");
            // Payload: ComponentManager.SerializeSaveGame
            var ser = new BinarySerializer(bw);
            sim.Sim.SerializeSaveGame(ser);
            ZeroAD.Sim.Diag.Log("SaveGame", $"saved to {path} (turn {sim.NetTurn.CurrentTurn})");
            return path;
        }
        catch (System.Exception ex)
        {
            ZeroAD.Sim.Diag.Err("SaveGame", $"save failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Deserializes a save file into the simulation. Clears all existing
    /// sim state first. Returns the turn number on success, null on failure.</summary>
    public static uint? Load(SimBridge sim, string slot = "quicksave",
        System.Action<ZeroAD.Sim.ComponentBase>? prepareComponent = null)
    {
        string path = SavePath(slot);
        if (!File.Exists(path))
        {
            ZeroAD.Sim.Diag.Err("SaveGame", $"save file not found: {path}");
            return null;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open);
            using var br = new BinaryReader(fs);
            var meta = ReadHeaderFromStream(br, slot);
            if (meta == null)
            {
                ZeroAD.Sim.Diag.Err("SaveGame", $"bad magic or version mismatch: {path}");
                return null;
            }
            // Payload
            var deser = new BinaryDeserializer(br);
            sim.Sim.DeserializeSaveGame(deser, prepareComponent);
            ZeroAD.Sim.Diag.Log("SaveGame", $"loaded from {path} (turn {meta.Turn})");
            return meta.Turn;
        }
        catch (System.Exception ex)
        {
            ZeroAD.Sim.Diag.Err("SaveGame", $"load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Read only the header of a save (magic + version + turn + match-skeleton
    /// block), stopping before the payload. Used by the cold-load entry point and the
    /// LoadGame browser. Returns null for a missing/bad/incompatible (≠v6) file.</summary>
    public static SaveMeta? ReadHeader(string slot)
    {
        string path = SavePath(slot);
        if (!File.Exists(path))
            return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open);
            using var br = new BinaryReader(fs);
            return ReadHeaderFromStream(br, slot);
        }
        catch (System.Exception)
        {
            return null; // 坏档/旧版本档跳过,不抛
        }
    }

    /// <summary>List every save in the saves dir, newest first. Skips unreadable or
    /// incompatible-version files. Header-only — payloads are never parsed.</summary>
    public static List<SaveMeta> ListSaves()
    {
        var result = new List<SaveMeta>();
        if (!Directory.Exists(SavesDir))
            return result;
        foreach (var path in Directory.GetFiles(SavesDir, "*.zsave"))
        {
            var meta = ReadHeader(Path.GetFileNameWithoutExtension(path));
            if (meta != null)
                result.Add(meta);
        }
        result.Sort((a, b) => b.TimeUnix.CompareTo(a.TimeUnix)); // newest first
        return result;
    }

    /// <summary>Delete the save file for a slot. Returns true if a file was removed.</summary>
    public static bool Delete(string slot)
    {
        string path = SavePath(slot);
        if (!File.Exists(path))
            return false;
        try
        {
            File.Delete(path);
            ZeroAD.Sim.Diag.Log("SaveGame", $"deleted {path}");
            return true;
        }
        catch (System.Exception ex)
        {
            ZeroAD.Sim.Diag.Err("SaveGame", $"delete failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>True if a save file exists for the given slot.</summary>
    public static bool Exists(string slot = "quicksave") =>
        File.Exists(SavePath(slot));

    /// <summary>Read magic + v6 header from an open stream positioned at the start.
    /// Returns the meta and leaves the reader positioned at the payload, or returns null
    /// when the magic/version is wrong.</summary>
    private static SaveMeta? ReadHeaderFromStream(BinaryReader br, string slot)
    {
        for (int i = 0; i < Magic.Length; i++)
            if (br.ReadByte() != (byte)Magic[i])
                return null;
        uint version = br.ReadUInt32();
        if (version != Version)
            return null;
        uint turn = br.ReadUInt32();
        // v6 match-skeleton block
        string mapPathStr = br.ReadString();
        string mapType = br.ReadString();
        bool tutorial = br.ReadByte() != 0;
        uint localPlayerId = br.ReadUInt32();
        var role = (NetRole)br.ReadByte();
        int slotCount = br.ReadByte();
        var slots = new List<PlayerSlotSetup>(slotCount);
        for (int i = 0; i < slotCount; i++)
        {
            var kind = (PlayerSlotKind)br.ReadByte();
            string civ = br.ReadString();
            int team = br.ReadInt32();
            int aiDiff = br.ReadInt32();
            string aiBehavior = br.ReadString();
            slots.Add(new PlayerSlotSetup { PlayerId = i + 1, Kind = kind, Civ = civ, Team = team,
                AIDifficulty = aiDiff, AIBehavior = aiBehavior });
        }
        long timeUnix = br.ReadInt64();
        string description = br.ReadString();
        return new SaveMeta(slot, timeUnix, description,
            mapPathStr.Length == 0 ? null : mapPathStr, mapType, turn, tutorial,
            localPlayerId, role, slots);
    }

    private static string MapTypeOf(SimBridge sim) =>
        sim.IsTutorialMode ? "tutorial"
        : sim.NetTurn.Role != NetRole.Standalone ? "multiplayer"
        : "singleplayer";
}
