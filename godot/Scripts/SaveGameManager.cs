using System.IO;
using Godot;
using ZeroAD.Sim;
using ZeroAD.Sim.Components;
using ZeroAD.Sim.Serialization;

namespace ZeroAD.Godot;

/// <summary>
/// Save/load system: serializes the full simulation state to a binary file and
/// restores it on load. Files land in the user data directory under "saves/".
///
/// Save format (little-endian binary):
///   magic   "0ADSAVE" (7 bytes)
///   version uint32    (format version, currently 1)
///   turn    uint32    (sim turn at save time)
///   payload            (ComponentManager.SerializeSaveGame output)
///
/// On load the caller (Main) must rebuild visual nodes for every restored entity.
/// </summary>
public static class SaveGameManager
{
    private const string Magic = "0ADSAVE";
    // v2(2026-07-29):HealthComponent 增 Unhealable 字段 + HealComponent 增计时器字段(MS5)。
    // v3(2026-07-30):单位捕获——DamageBlock.Capture int→Fixed、AttackComponent 增 Capture
    // 攻击类型六字段、UnitOrder 增 AllowCapture。旧档位置流错位,加载方按版本号拒收。
    private const uint Version = 3;

    private static string SavesDir => ProjectSettings.GlobalizePath("user://saves/");

    public static string SavePath(string slot) =>
        Path.Combine(SavesDir, $"{slot}.zsave");

    /// <summary>Serializes the current simulation state to a save file. Returns the
    /// file path on success, null on failure.</summary>
    public static string? Save(SimBridge sim, string slot = "quicksave")
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
            // Payload: ComponentManager.SerializeSaveGame
            var ser = new BinarySerializer(bw);
            sim.Sim.SerializeSaveGame(ser);
            GD.Print($"[SaveGame] saved to {path} (turn {sim.NetTurn.CurrentTurn})");
            return path;
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[SaveGame] save failed: {ex.Message}");
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
            GD.PrintErr($"[SaveGame] save file not found: {path}");
            return null;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open);
            using var br = new BinaryReader(fs);
            // Header
            for (int i = 0; i < Magic.Length; i++)
            {
                if (br.ReadByte() != (byte)Magic[i])
                {
                    GD.PrintErr("[SaveGame] bad magic — not a save file");
                    return null;
                }
            }
            uint version = br.ReadUInt32();
            if (version != Version)
            {
                GD.PrintErr($"[SaveGame] version mismatch: file={version} expected={Version}");
                return null;
            }
            uint turn = br.ReadUInt32();
            // Payload
            var deser = new BinaryDeserializer(br);
            sim.Sim.DeserializeSaveGame(deser, prepareComponent);
            GD.Print($"[SaveGame] loaded from {path} (turn {turn})");
            return turn;
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[SaveGame] load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>True if a save file exists for the given slot.</summary>
    public static bool Exists(string slot = "quicksave") =>
        File.Exists(SavePath(slot));
}
