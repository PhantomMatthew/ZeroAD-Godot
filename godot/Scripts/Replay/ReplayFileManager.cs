using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using ZeroAD.Sim.Net;

namespace ZeroAD.Godot;

/// <summary>录像文件管理。镜像 <see cref="SaveGameManager"/> 的文件管理（List/Delete/Exists/Open），
/// 但目录是 user://replays/，扩展名 .zreplay，header 解析委托 <see cref="ReplayFile"/>。</summary>
public static class ReplayFileManager
{
    public const string Extension = ".zreplay";

    private static string ReplaysDir => ProjectSettings.GlobalizePath("user://replays/");

    public static string ReplayPath(string slot) =>
        Path.Combine(ReplaysDir, slot.EndsWith(Extension) ? slot : slot + Extension);

    /// <summary>浏览器列表项：slot 名（无扩展名）+ 元数据。镜像 SaveGameManager.SaveMeta 用途。</summary>
    public sealed record ReplayEntry(string Slot, ReplayMeta Meta);

    /// <summary>列出所有录像，最新在前。跳过不可读/版本不兼容的文件。仅读 header。</summary>
    public static List<ReplayEntry> ListReplays()
    {
        var result = new List<ReplayEntry>();
        if (!Directory.Exists(ReplaysDir))
            return result;
        foreach (var path in Directory.GetFiles(ReplaysDir, "*" + Extension))
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read);
                var meta = ReplayFile.ReadHeader(fs);
                if (meta != null)
                    result.Add(new ReplayEntry(Path.GetFileNameWithoutExtension(path), meta));
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[Replay] skip unreadable {path}: {ex.Message}");
            }
        }
        result.Sort((a, b) => b.Meta.TimeUnix.CompareTo(a.Meta.TimeUnix)); // newest first
        return result;
    }

    /// <summary>打开录像（读 header + 初始状态，返回 Reader 供播放驱动器读命令流）。</summary>
    public static ReplayReader? Open(string slot)
    {
        string path = ReplayPath(slot);
        if (!File.Exists(path)) return null;
        try
        {
            var fs = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read);
            return ReplayFile.Open(fs);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Replay] open failed {path}: {ex.Message}");
            return null;
        }
    }

    public static bool Delete(string slot)
    {
        string path = ReplayPath(slot);
        if (!File.Exists(path)) return false;
        try { File.Delete(path); return true; }
        catch (Exception ex) { GD.PrintErr($"[Replay] delete failed: {ex.Message}"); return false; }
    }

    public static bool Exists(string slot) => File.Exists(ReplayPath(slot));
}
