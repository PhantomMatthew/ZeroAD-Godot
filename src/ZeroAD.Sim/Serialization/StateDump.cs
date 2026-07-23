using System.IO;

namespace ZeroAD.Sim.Serialization;

/// <summary>
/// OOS forensics: when the host detects a state-hash mismatch, every peer writes
/// both a binary snapshot (for programmatic inspection/reload tooling later) and a
/// deterministic text dump (for immediate `diff`). File names carry the checkpoint
/// turn and the local player id so dumps from multiple peers land side by side.
/// </summary>
public static class StateDump
{
    public static (string binPath, string txtPath) WriteAll(
        ComponentManager cm, string directory, uint turn, uint playerId)
    {
        Directory.CreateDirectory(directory);
        string baseName = Path.Combine(directory, $"oos_turn{turn}_player{playerId}");

        string binPath = baseName + ".bin";
        using (var fs = new FileStream(binPath, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            cm.SerializeFullState(new BinarySerializer(bw));
        }

        string txtPath = baseName + ".txt";
        var text = new TextDumpSerializer();
        cm.SerializeFullState(text);
        File.WriteAllText(txtPath, text.ToString());

        return (binPath, txtPath);
    }
}
